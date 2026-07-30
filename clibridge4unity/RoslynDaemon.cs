using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace clibridge4unity;

/// <summary>
/// Background daemon that keeps Roslyn syntax trees in memory for instant CODE_ANALYZE queries.
/// Watches for file changes and incrementally re-parses.
/// Communicates over named pipes (fastest IPC on Windows).
/// Protocol: client sends "endpoint query\n", daemon responds with result text and closes pipe.
/// </summary>
static class RoslynDaemon
{
    const int TTL_MINUTES = 120;

    static string GetDaemonDir(string projectPath)
        => Path.Combine(projectPath, ".clibridge4unity");

    static string GetPipeFile(string projectPath)
        => Path.Combine(GetDaemonDir(projectPath), "daemon.pipe");

    static string GetPidFile(string projectPath)
        => Path.Combine(GetDaemonDir(projectPath), "daemon.pid");

    static string GeneratePipeName(string projectPath)
    {
        string normalized = Path.GetFullPath(projectPath).ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
        int hash = 5381;
        unchecked { for (int i = 0; i < normalized.Length; i++) hash = ((hash << 5) + hash) ^ normalized[i]; }
        return $"RoslynDaemon_{Environment.UserName}_{hash:X8}";
    }

    // ─── Duplicate-daemon guards ─────────────────────────────────────
    // GetRunningPipe() + StartBackground() is a check-then-act with no cross-process lock, so two
    // CLI invocations landing in the same window both see "not running" and both spawn a daemon.
    // Duplicates are expensive — each one indexes the whole project (~1.7 GB RSS, sustained CPU)
    // and they compete for the same pipe name. Two separate locks, deliberately:
    //   _spawn — held by the CLI only around check-then-spawn, so spawns serialise.
    //   _own   — held by the daemon process for its whole life, so a daemon that got spawned
    //            anyway exits before indexing instead of becoming a second resident copy.
    // They must be distinct: the spawner holds _spawn while waiting up to 15s for the child to
    // publish daemon.pipe, and the child acquires _own during that window.
    static string SpawnLockName(string projectPath) => $@"Global\{GeneratePipeName(projectPath)}_spawn";
    static string OwnerLockName(string projectPath) => $@"Global\{GeneratePipeName(projectPath)}_own";

    static Mutex _ownership; // kept alive for the daemon's lifetime — do not let this be collected

    /// <summary>Best-effort cross-process lock. Returns the mutex (may be null if the OS refused to
    /// create it — e.g. restricted token); <paramref name="acquired"/> says whether we hold it.
    /// A null mutex means "proceed unguarded" — the guard is an optimisation, never a hard gate.</summary>
    static Mutex TryAcquireLock(string name, int waitMs, out bool acquired)
    {
        acquired = false;
        try
        {
            var m = new Mutex(false, name);
            try { acquired = m.WaitOne(waitMs, false); }
            catch (AbandonedMutexException) { acquired = true; } // previous owner died — we inherit
            return m;
        }
        catch { return null; }
    }

    static void ReleaseLock(Mutex m, bool acquired)
    {
        if (m == null) return;
        try { if (acquired) m.ReleaseMutex(); } catch { }
        try { m.Dispose(); } catch { }
    }

    // ─── Client side ─────────────────────────────────────────────────

    /// <summary>Check if daemon is running. Returns pipe name or null.</summary>
    static string GetVersionFile(string projectPath)
        => Path.Combine(GetDaemonDir(projectPath), "daemon.version");

    /// <summary>Token identifying the binary that should own the daemon. Combines the assembly
    /// version (bumps every release) with the running exe's last-write time (changes on every
    /// local rebuild, even when the version string is unchanged — e.g. iterating on daemon code
    /// without a version bump). A daemon whose recorded token differs from the current process's
    /// is stale and gets killed + respawned in <see cref="GetRunningPipe"/>.</summary>
    static string DaemonVersionToken()
    {
        string ver = typeof(RoslynDaemon).Assembly.GetName().Version?.ToString() ?? "?";
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null && File.Exists(exe))
                return $"{ver}:{File.GetLastWriteTimeUtc(exe).Ticks:X}";
        }
        catch { }
        return ver;
    }

    public static string GetRunningPipe(string projectPath)
    {
        string pidFile = GetPidFile(projectPath);
        if (!File.Exists(pidFile)) return null;

        try
        {
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return null;

            // Verify process is alive AND is OUR daemon (PID reuse defense — Windows recycles PIDs
            // and an unrelated process inheriting a stale PID would make us hang waiting on a pipe
            // that doesn't exist).
            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch { CleanupFiles(projectPath); return null; }

            string procName;
            try { procName = proc.ProcessName; }
            catch { CleanupFiles(projectPath); return null; }
            if (!procName.Equals("clibridge4unity", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[roslyn] PID {pid} is '{procName}', not our daemon — cleaning up");
                CleanupFiles(projectPath);
                return null;
            }

            // Version check: if daemon was started by an older CLI, kill + restart.
            string versionFile = GetVersionFile(projectPath);
            string currentVer = DaemonVersionToken();
            if (File.Exists(versionFile))
            {
                try
                {
                    string daemonVer = File.ReadAllText(versionFile).Trim();
                    if (daemonVer != currentVer)
                    {
                        Console.Error.WriteLine($"[roslyn] daemon version mismatch ({daemonVer} vs {currentVer}) — restarting");
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        CleanupFiles(projectPath);
                        return null;
                    }
                }
                catch { }
            }
            else
            {
                // Old daemon without version file — kill it, force fresh start.
                Console.Error.WriteLine("[roslyn] daemon predates version tracking — restarting");
                try { proc.Kill(entireProcessTree: true); } catch { }
                CleanupFiles(projectPath);
                return null;
            }

            // No pipe-existence pre-check — racy with daemon startup (daemon may still be indexing
            // when CLI checks, killing a healthy-but-busy daemon and spawning duplicates).
            // The actual command query carries its own timeout + retry, which surfaces dead pipes.
            return GeneratePipeName(projectPath);
        }
        catch { return null; }
    }

    /// <summary>Query the daemon via named pipe. Retries once on transient failure.
    /// LINT capped at 60s — covers per-asmdef compile on big projects (MTD ~30s).
    /// Other endpoints 30s.</summary>
    public static string Query(string pipeName, string endpoint, string query)
    {
        int timeoutMs = endpoint == "lint" ? 60_000 : 30_000;
        var result = QueryInternal(pipeName, endpoint, query, timeoutMs, out string error);
        if (result != null) return result;

        // One retry — covers the brief window after a listener finishes and before
        // a sibling listener re-arms under heavy concurrent load.
        Thread.Sleep(100);
        result = QueryInternal(pipeName, endpoint, query, timeoutMs, out error);
        if (result == null && error != null)
            Console.Error.WriteLine($"[roslyn] daemon query failed: {error}");
        return result;
    }

    static string QueryInternal(string pipeName, string endpoint, string query, int timeoutMs, out string error)
    {
        error = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);

            // Send: "endpoint query\n"
            byte[] msg = Encoding.UTF8.GetBytes($"{endpoint} {query}\n");
            pipe.Write(msg, 0, msg.Length);
            pipe.Flush();

            // Read response with the same timeout — pipe.Read with no token blocks forever
            // if the daemon hangs (e.g., long parse, stuck handler), so wrap in a CTS.
            using var cts = new CancellationTokenSource(timeoutMs);
            var sb = new StringBuilder();
            byte[] buf = new byte[8192];
            try
            {
                while (true)
                {
                    var readTask = pipe.ReadAsync(buf, 0, buf.Length, cts.Token);
                    readTask.Wait(cts.Token);
                    int bytesRead = readTask.Result;
                    if (bytesRead == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, bytesRead));
                }
            }
            catch (OperationCanceledException)
            {
                error = $"daemon read timed out after {timeoutMs}ms (pipe={pipeName})";
                return null;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                error = $"daemon read timed out after {timeoutMs}ms (pipe={pipeName})";
                return null;
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message} (pipe={pipeName})";
            return null;
        }
    }

    /// <summary>Start the daemon as a background process. Returns pipe name or null.</summary>
    public static string StartBackground(string projectPath)
    {
        // Serialise spawns across processes, then re-check: a peer may have won the race and
        // published its pipe while we were queued here.
        var spawnLock = TryAcquireLock(SpawnLockName(projectPath), 20000, out bool spawnHeld);
        try
        {
            if (spawnHeld)
            {
                string winner = GetRunningPipe(projectPath);
                if (winner != null) return winner;
            }
            return StartBackgroundCore(projectPath);
        }
        finally { ReleaseLock(spawnLock, spawnHeld); }
    }

    static string StartBackgroundCore(string projectPath)
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "clibridge4unity.exe");

        // Spawn the daemon FULLY DETACHED — no stdio redirection. Otherwise the daemon
        // inherits CLI's stdin/stdout/stderr handles. Even if we redirect them to our own
        // pipes, on Windows ALL inheritable handles propagate via CreateProcess(bInheritHandles=TRUE).
        // The PowerShell host pipeline (e.g. `clibridge4unity LINT 2>&1 | Out-Null`) holds
        // that pipeline open until *every* writer to its stdout/stderr pipe closes — including
        // the inherited copy in the long-lived daemon. Result: CLI exits in 5s but `pwsh`
        // hangs for minutes/hours waiting on the daemon to die.
        // Detach via cmd.exe `start "" /b` so the new process gets fresh NUL handles, no
        // inheritance from us, and pick up the pipe name via file (`daemon.pipe`).
        try { File.Delete(GetPipeFile(projectPath)); } catch { }
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-d \"{projectPath}\" DAEMON",
            // UseShellExecute=true → ShellExecuteEx → does NOT inherit our std handles. The
            // child gets fresh console handles (or none with WindowStyle=Hidden + CreateNoWindow).
            // The trade-off: we can't redirect stdout/stderr — but we don't need to, because
            // the daemon also writes its pipe name to `daemon.pipe` (file-based handshake).
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            var proc = Process.Start(psi);
            if (proc == null) return null;
            try { proc.WaitForExit(2000); } catch { } // cmd.exe exits immediately after start

            // Poll the daemon.pipe file for up to 15 seconds.
            string pipeFile = GetPipeFile(projectPath);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(pipeFile))
                {
                    try
                    {
                        string name = File.ReadAllText(pipeFile).Trim();
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                    catch { }
                }
                Thread.Sleep(100);
            }
            return null;
        }
        catch { return null; }
    }

    static void CleanupFiles(string projectPath)
    {
        try { File.Delete(GetPidFile(projectPath)); } catch { }
        try { File.Delete(GetPipeFile(projectPath)); } catch { }
        try { File.Delete(GetVersionFile(projectPath)); } catch { }
    }

    /// <summary>Kill the daemon process (if alive) and remove its state files. Used to recover from a stuck daemon.</summary>
    public static void KillAndCleanup(string projectPath)
    {
        string pidFile = GetPidFile(projectPath);
        if (File.Exists(pidFile))
        {
            try
            {
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                {
                    try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
                    catch { /* already gone */ }
                }
            }
            catch { }
        }
        CleanupFiles(projectPath);
    }

    // ─── Server side ─────────────────────────────────────────────────

    /// <summary>Run the daemon (blocking).</summary>
    public static int Run(string projectPath, string args)
    {
        string subCmd = args?.Trim().ToLowerInvariant() ?? "";

        if (subCmd == "stop")
        {
            string pipe = GetRunningPipe(projectPath);
            if (pipe == null) { Console.WriteLine("No daemon running."); return 0; }
            QueryInternal(pipe, "shutdown", "", 2000, out _);
            CleanupFiles(projectPath);
            Console.WriteLine("Daemon stopped.");
            return 0;
        }

        if (subCmd == "status")
        {
            string pipe = GetRunningPipe(projectPath);
            if (pipe == null) { Console.WriteLine("Not running."); return 0; }
            string status = Query(pipe, "status", "");
            Console.WriteLine(status ?? "No response.");
            return 0;
        }

        // Check if already running
        if (GetRunningPipe(projectPath) != null)
        {
            Console.WriteLine("Daemon already running.");
            return 0;
        }

        return RunServer(projectPath);
    }

    static int RunServer(string projectPath)
    {
        // Last line of defence against duplicates: whoever holds this owns the project. Held for
        // the process lifetime (never released) — a second daemon exits here, before it spends
        // ~1.7 GB and a full index pass becoming a resident copy of a daemon we already have.
        _ownership = TryAcquireLock(OwnerLockName(projectPath), 0, out bool owned);
        if (_ownership != null && !owned)
        {
            Console.Error.WriteLine("[roslyn] another daemon already owns this project — exiting");
            return 0;
        }

        string assetsDir = Path.Combine(projectPath, "Assets");
        string packagesDir = Path.Combine(projectPath, "Packages");
        string packageCacheDir = Path.Combine(projectPath, "Library", "PackageCache");
        if (!Directory.Exists(assetsDir))
        {
            Console.Error.WriteLine("Error: Assets/ directory not found.");
            return 1;
        }

        // Write PID file immediately so clients know we're starting
        string daemonDir = GetDaemonDir(projectPath);
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(GetPidFile(projectPath), Process.GetCurrentProcess().Id.ToString());
        try { File.WriteAllText(GetVersionFile(projectPath), DaemonVersionToken()); } catch { }

        // Syntax trees are RESIDENT FOR USER CODE ONLY (Assets/, non-PackageCache Packages/).
        // Measured on a large project: 12,154 files / 104 MB of source cost 775 MB of live objects,
        // and Library/PackageCache was 459 MB of that — 59% — for read-only third-party source the
        // user can never edit. Both lint modes already skip it outright, and the two query paths
        // (analyze, map) only need a tree for files whose *text* matched a filter first. So package
        // files keep their text (the filter needs it) and are re-parsed on demand via GetTreeFor().
        // Re-parsing measured at ~0.7 ms/file, so a query matching 200 package files pays ~140 ms.
        // Escape hatch: CLIBRIDGE_INDEX_PACKAGES=1 restores full residency. Costs ~400 MB on a large
        // project but keeps broad `kind:` queries at their old latency — see the note above.
        bool residentPackages = string.Equals(Environment.GetEnvironmentVariable("CLIBRIDGE_INDEX_PACKAGES"), "1", StringComparison.Ordinal);
        var trees = new ConcurrentDictionary<string, SyntaxTree>();
        var fileTexts = new ConcurrentDictionary<string, string>();
        // Type names harvested from package files before their tree is dropped — keeps the
        // "did you mean" suggester able to name package types without retaining their trees.
        var pkgTypeNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        int totalFilesToIndex = 0;
        var indexReady = new ManualResetEventSlim(false);

        // Resident tree, or a fresh parse for package source. Deliberately uncached: caching would
        // reintroduce the growth this split exists to remove, and a re-parse is sub-millisecond.
        SyntaxTree GetTreeFor(string path)
        {
            if (trees.TryGetValue(path, out var resident)) return resident;
            if (!fileTexts.TryGetValue(path, out var text)) return null;
            try { return CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest), path); }
            catch { return null; }
        }

        // DLL index — built in background after .cs parse completes. Used as fallback
        // when CODE_ANALYZE can't find a type in source (precompiled plugins, package DLLs).
        var dllIndex = new DllIndex(projectPath);

        // Asset graph — serialized-YAML wiring (script attach sites, prefab/scene refs,
        // UnityEvent calls). Powers `usedby:` queries, MAP, and the "Asset wiring" section
        // appended to CODE_ANALYZE deep-type views.
        var assetGraph = new AssetGraph(projectPath);

        // Asset change tracking (latest event per path; older events superseded).
        // Keyed by path; value = (kind, utcTicks, oldPath). Persisted to changes.log for Unity side.
        var changeLog = new ConcurrentDictionary<string, (string Kind, long UtcTicks, string OldPath)>();
        string changeLogPath = Path.Combine(daemonDir, "changes.log");
        string lastCompiledPath = Path.Combine(daemonDir, "last-compiled.ticks");
        var changeLogLock = new object();
        var changeLogFlushPending = 0;
        // Unity-style filter — only track paths that AssetDatabase considers (mirrors focus-refresh scope).
        bool IsTrackedPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string norm = fullPath.Replace('\\', '/');
            // Skip transient or build dirs
            if (norm.Contains("/Library/") || norm.Contains("/Temp/") ||
                norm.Contains("/obj/") || norm.Contains("/Logs/") ||
                norm.Contains("/.git/") || norm.Contains("/UserSettings/")) return false;
            return true;
        }
        void RecordChange(string kind, string fullPath, string oldFullPath = null)
        {
            if (!IsTrackedPath(fullPath)) return;
            long ticks = DateTime.UtcNow.Ticks;
            changeLog[fullPath] = (kind, ticks, oldFullPath ?? "");
            // Debounce disk flush — coalesce burst events into one write.
            if (Interlocked.CompareExchange(ref changeLogFlushPending, 1, 0) == 0)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(200);
                    Interlocked.Exchange(ref changeLogFlushPending, 0);
                    FlushChangeLog();
                });
            }
        }
        void FlushChangeLog()
        {
            try
            {
                lock (changeLogLock)
                {
                    long pruneBefore = 0;
                    if (File.Exists(lastCompiledPath))
                        long.TryParse(File.ReadAllText(lastCompiledPath).Trim(), out pruneBefore);

                    var sb = new StringBuilder();
                    foreach (var kvp in changeLog)
                    {
                        if (kvp.Value.UtcTicks <= pruneBefore)
                        {
                            changeLog.TryRemove(kvp.Key, out _);
                            continue;
                        }
                        // Format: ticks\tkind\tpath\toldPath\n
                        sb.Append(kvp.Value.UtcTicks).Append('\t')
                          .Append(kvp.Value.Kind).Append('\t')
                          .Append(kvp.Key).Append('\t')
                          .Append(kvp.Value.OldPath).Append('\n');
                    }
                    string tmp = changeLogPath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString());
                    File.Move(tmp, changeLogPath, overwrite: true);
                }
            }
            catch { }
        }

        // Phase 1 (background): Parse all source files. Pipe server starts in parallel
        // so queries can be answered (or queued with "still indexing" status) immediately.
        Task indexTask = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            Console.Error.Write("Parsing source files...");
            var allFiles = new List<string>(Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories));
            if (Directory.Exists(packagesDir))
                allFiles.AddRange(Directory.EnumerateFiles(packagesDir, "*.cs", SearchOption.AllDirectories));
            // Library/PackageCache holds UPM-resolved package source. Unity treats these
            // as part of the compile, so CODE_ANALYZE must too. No watcher — UPM rewrites
            // these on package install/update, daemon restart picks up the change.
            if (Directory.Exists(packageCacheDir))
                allFiles.AddRange(Directory.EnumerateFiles(packageCacheDir, "*.cs", SearchOption.AllDirectories));
            Interlocked.Exchange(ref totalFilesToIndex, allFiles.Count);

            // C# language version: Latest matches Unity 6's LangVersion default. Without this
            // Roslyn defaults to 7.3 and falsely flags C# 8+ features (target-typed new, records,
            // single-element tuples in xliff_core_2.0.cs, file-scoped namespaces, etc.).
            var defaultParseOpts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
            // Throttle to N-1 cores so the named-pipe listener thread always has a core to
            // service heartbeat polls. Otherwise CLI sees same `__indexing:N/M` for seconds and
            // its 30s stall watchdog wrongly trips.
            int parseThreads = Math.Max(1, Environment.ProcessorCount - 1);
            Parallel.ForEach(allFiles,
                new ParallelOptions { MaxDegreeOfParallelism = parseThreads },
                file =>
            {
                try
                {
                    string text = File.ReadAllText(file);
                    fileTexts[file] = text;
                    // Package source is never parsed at index time. Parsing it only to harvest names
                    // would force red-tree realisation on every file — the single most expensive part
                    // of holding a tree — and leave the GC heap grown even after the tree is dropped.
                    if (residentPackages || IsUserCode(file, projectPath))
                        trees[file] = CSharpSyntaxTree.ParseText(text, defaultParseOpts, file);
                    else
                        CollectTypeNames(text, pkgTypeNames);
                }
                catch { }
            });
            sw.Stop();
            Console.Error.WriteLine($" {fileTexts.Count} files ({trees.Count} resident, {pkgTypeNames.Count} package types) in {sw.ElapsedMilliseconds}ms");
            indexReady.Set();

            // Phase 1b: Index plugin DLLs after source. Cheap (Cecil reads metadata only)
            // but parallelisable — runs without blocking source-only queries.
            Task.Run(() =>
            {
                try
                {
                    Console.Error.Write("Indexing DLLs...");
                    dllIndex.Build();
                    Console.Error.WriteLine($" {dllIndex.TypeCount} types in {dllIndex.DllCount} DLLs ({dllIndex.BuildMs}ms)");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"\n[daemon] DLL index build failed: {ex.GetType().Name}: {ex.Message}");
                }
            });

        });

        // Asset graph (scenes/prefabs/SO wiring) — no dependency on the syntax trees, so it
        // builds in parallel with the source parse (I/O-bound vs the parse's CPU-bound work).
        Task.Run(() =>
        {
            try
            {
                assetGraph.Build();
                Console.Error.WriteLine($"Asset graph: {assetGraph.ContainerCount} containers, {assetGraph.GuidCount} guids ({assetGraph.BuildMs}ms)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[daemon] asset graph build failed: {ex.GetType().Name}: {ex.Message}");
            }
        });

        // Phase 2: File watcher (covers both Assets and Packages)
        int reParseCount = 0;
        void OnFileChanged(string filePath)
        {
            try
            {
                Thread.Sleep(100); // debounce
                string text = File.ReadAllText(filePath);
                var opts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
                fileTexts[filePath] = text;
                // Same residency rule as the initial index — watched Packages/ can include
                // PackageCache paths on some layouts, and those must not become resident.
                if (IsUserCode(filePath, projectPath))
                    trees[filePath] = CSharpSyntaxTree.ParseText(text, opts, filePath);
                else
                    CollectTypeNames(text, pkgTypeNames);
                Interlocked.Increment(ref reParseCount);
            }
            catch { }
        }

        bool IsCsFile(string p) => p != null && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        FileSystemWatcher MakeWatcher(string root)
        {
            // Watch all files, all extensions — needed for asset change tracking
            // (.meta, manifest.json, .asmdef, .asmref, .uxml, .uss, etc.).
            // .cs handler still fires for Roslyn reparse; everything else only updates change log.
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                InternalBufferSize = 65536 // 64KB — handles bulk ops (git checkout, package install)
            };
            // Asset-graph refresh mirrors the .cs reparse pattern: fire-and-forget with a short
            // debounce so bursts (save-all, import) coalesce into cheap single-file rescans.
            void GraphUpdate(string path, bool deleted, string oldPath = null)
            {
                if (!AssetGraph.IsGraphFile(path) && (oldPath == null || !AssetGraph.IsGraphFile(oldPath))) return;
                Task.Run(() => { Thread.Sleep(150); assetGraph.OnFileEvent(path, deleted, oldPath); });
            }
            w.Changed += (_, e) =>
            {
                if (!IsTrackedPath(e.FullPath)) return;
                RecordChange("M", e.FullPath);
                if (IsCsFile(e.FullPath)) Task.Run(() => OnFileChanged(e.FullPath));
                GraphUpdate(e.FullPath, deleted: false);
            };
            w.Created += (_, e) =>
            {
                if (!IsTrackedPath(e.FullPath)) return;
                RecordChange("C", e.FullPath);
                if (IsCsFile(e.FullPath)) Task.Run(() => OnFileChanged(e.FullPath));
                GraphUpdate(e.FullPath, deleted: false);
            };
            w.Renamed += (_, e) =>
            {
                if (!IsTrackedPath(e.FullPath) && !IsTrackedPath(e.OldFullPath)) return;
                RecordChange("R", e.FullPath, e.OldFullPath);
                if (IsCsFile(e.OldFullPath))
                {
                    trees.TryRemove(e.OldFullPath, out SyntaxTree _);
                    fileTexts.TryRemove(e.OldFullPath, out string _);
                }
                if (IsCsFile(e.FullPath)) Task.Run(() => OnFileChanged(e.FullPath));
                GraphUpdate(e.FullPath, deleted: false, oldPath: e.OldFullPath);
            };
            w.Deleted += (_, e) =>
            {
                if (!IsTrackedPath(e.FullPath)) return;
                RecordChange("D", e.FullPath);
                if (IsCsFile(e.FullPath))
                {
                    trees.TryRemove(e.FullPath, out SyntaxTree _2);
                    fileTexts.TryRemove(e.FullPath, out string _3);
                }
                GraphUpdate(e.FullPath, deleted: true);
            };
            w.Error += (_, e) =>
            {
                Console.Error.WriteLine($"[daemon] watcher error: {e.GetException()?.Message}");
            };
            w.EnableRaisingEvents = true;
            return w;
        }

        var watchers = new List<FileSystemWatcher> { MakeWatcher(assetsDir) };
        if (Directory.Exists(packagesDir)) watchers.Add(MakeWatcher(packagesDir));

        // Phase 3: Start pipe server (PID file already written at startup)
        string pipeName = GeneratePipeName(projectPath);
        Console.Error.WriteLine($"Daemon started: {pipeName} (indexing in background)");
        Console.Error.WriteLine($"  Auto-shutdown after {TTL_MINUTES} minutes of inactivity");

        // Signal parent we're ready to ACCEPT. Two channels for back-compat:
        //   1) Stdout `pipe:<name>` — works for clients that REDIRECT our stdout.
        //   2) `daemon.pipe` file — used by detached spawn (no stdio inherit, see StartBackground).
        // Queries that arrive during indexing block briefly waiting for it to complete.
        try { File.WriteAllText(GetPipeFile(projectPath), pipeName); } catch { }
        Console.WriteLine($"pipe:{pipeName}");
        Console.Out.Flush();

        long lastActivityTicks = DateTime.UtcNow.Ticks;
        var shutdownCts = new CancellationTokenSource();

        // Handle a single connection end-to-end. The listener task stays "busy" on this connection
        // until it completes; other listeners in the pool continue serving new clients in parallel.
        async Task HandleConnection(NamedPipeServerStream server)
        {
            try
            {
                var requestSb = new StringBuilder();
                byte[] buf = new byte[8192];

                while (true)
                {
                    int bytesRead = await server.ReadAsync(buf, 0, buf.Length);
                    if (bytesRead == 0) break;
                    requestSb.Append(Encoding.UTF8.GetString(buf, 0, bytesRead));
                    if (requestSb.ToString().Contains('\n')) break;
                }

                string request = requestSb.ToString().Trim();
                int spaceIdx = request.IndexOf(' ');
                string endpoint = spaceIdx > 0 ? request.Substring(0, spaceIdx) : request;
                string query = spaceIdx > 0 ? request.Substring(spaceIdx + 1) : "";

                string response;
                switch (endpoint)
                {
                    case "health":
                        response = indexReady.IsSet ? "ok" : "indexing";
                        break;
                    case "status":
                        response = $"files: {fileTexts.Count}/{Volatile.Read(ref totalFilesToIndex)} ({trees.Count} resident trees, {pkgTypeNames.Count} package types)\nready: {indexReady.IsSet}\nreparses: {reParseCount}\ndlls: {dllIndex.DllCount} ({dllIndex.TypeCount} types, ready={dllIndex.Ready})\nassets: {assetGraph.ContainerCount} containers ({assetGraph.GuidCount} guids, ready={assetGraph.Ready})\nuptime: {(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds:F0}s\nproject: {projectPath}";
                        break;
                    case "analyze":
                        // Don't block the connection on indexing — return a progress sentinel
                        // immediately so the client can poll + render a heartbeat. Each request
                        // is short and stateless; client retries until indexReady.IsSet.
                        if (!indexReady.IsSet)
                        {
                            response = $"__indexing:{fileTexts.Count}/{Volatile.Read(ref totalFilesToIndex)}";
                            break;
                        }
                        // `usedby:` is an asset-graph reverse lookup, not a code query.
                        if (query.TrimStart().StartsWith("usedby:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!assetGraph.Ready)
                            {
                                response = $"__indexing:assets:{assetGraph.ContainerCount}";
                                break;
                            }
                            response = assetGraph.FormatUsedBy(query.Trim().Substring("usedby:".Length).Trim());
                            break;
                        }
                        response = HandleAnalyze(trees, fileTexts, GetTreeFor, pkgTypeNames.Keys, projectPath, query);
                        response = AugmentWithDllHits(response, dllIndex, query);
                        response = AugmentWithAssetWiring(response, assetGraph, query);
                        break;
                    case "map":
                        if (!indexReady.IsSet)
                        {
                            response = $"__indexing:{fileTexts.Count}/{Volatile.Read(ref totalFilesToIndex)}";
                            break;
                        }
                        if (!assetGraph.Ready)
                        {
                            // Progress sentinel — client polls with heartbeat, count grows as build proceeds.
                            response = $"__indexing:assets:{assetGraph.ContainerCount}";
                            break;
                        }
                        response = assetGraph.FormatMap(
                            GetTreeFor,
                            new Dictionary<string, string>(fileTexts),
                            projectPath, query, 0);
                        break;
                    case "compile-changes":
                    {
                        // Query: ticks (UTC). Returns events with UtcTicks > sinceTicks, one per line.
                        long.TryParse(query.Trim(), out long sinceTicks);
                        var sb = new StringBuilder();
                        foreach (var kvp in changeLog)
                        {
                            if (kvp.Value.UtcTicks <= sinceTicks) continue;
                            sb.Append(kvp.Value.UtcTicks).Append('\t')
                              .Append(kvp.Value.Kind).Append('\t')
                              .Append(kvp.Key).Append('\t')
                              .Append(kvp.Value.OldPath).Append('\n');
                        }
                        response = sb.Length > 0 ? sb.ToString().TrimEnd('\n') : "(none)";
                        break;
                    }
                    case "compile-mark":
                    {
                        // Mark all events up to ticks as compiled — daemon prunes them.
                        if (long.TryParse(query.Trim(), out long compiledTicks))
                        {
                            try { File.WriteAllText(lastCompiledPath, compiledTicks.ToString()); } catch { }
                            int pruned = 0;
                            foreach (var kvp in changeLog)
                            {
                                if (kvp.Value.UtcTicks <= compiledTicks)
                                {
                                    if (changeLog.TryRemove(kvp.Key, out _)) pruned++;
                                }
                            }
                            FlushChangeLog();
                            response = $"pruned: {pruned}\nremaining: {changeLog.Count}";
                        }
                        else
                        {
                            response = "Error: ticks required";
                        }
                        break;
                    }
                    case "lint":
                    {
                        // Syntax check across every cached SyntaxTree. Daemon FileSystemWatcher
                        // sees new files Unity hasn't seen → catches errors in just-added .cs files.
                        // Query: "" (syntax-only, default), "warnings", "unity" (+ per-asmdef compile),
                        //        "unity warnings" (unity + warnings). 60s budget on unity mode.
                        if (!indexReady.IsSet)
                        {
                            response = $"__indexing:{fileTexts.Count}/{Volatile.Read(ref totalFilesToIndex)}";
                            break;
                        }
                        string q = (query ?? "").Trim().ToLowerInvariant();
                        bool unityMode = q.Contains("unity");
                        bool includeWarnings = q.Contains("warning");
                        if (unityMode)
                        {
                            // Per-asmdef compile (Unity-faithful). Re-uses parsed trees + texts from cache.
                            var fileTextsSnapshot = new Dictionary<string, string>(fileTexts.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var kvp in fileTexts) fileTextsSnapshot[kvp.Key] = kvp.Value;
                            var run = LintUnity.Run(projectPath, fileTextsSnapshot);
                            response = LintUnity.Format(run, projectPath, includeWarnings);
                            break;
                        }
                        response = RunSyntaxLint(trees, projectPath, includeWarnings, fileTexts.Count);
                        break;
                    }
                    case "shutdown":
                        response = "shutting down";
                        shutdownCts.Cancel();
                        break;
                    default:
                        response = "endpoints: health, status, analyze, map, lint, compile-changes, compile-mark, shutdown";
                        break;
                }

                byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                await server.WriteAsync(responseBytes, 0, responseBytes.Length);
                server.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[daemon] connection error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { if (server.IsConnected) server.Disconnect(); } catch { }
                try { server.Dispose(); } catch { }
            }
        }

        // One persistent listener: accept → handle inline → loop. Multiple of these run in parallel
        // so there is always at least one free listener available for new clients.
        async Task RunListener(int id)
        {
            while (!shutdownCts.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(shutdownCts.Token);
                    Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

                    // Handle inline — this listener is "busy" until the client is served.
                    // Siblings in the pool keep accepting.
                    var toHandle = server;
                    server = null; // ownership transferred to HandleConnection
                    await HandleConnection(toHandle);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[daemon] listener {id} error: {ex.GetType().Name}: {ex.Message}");
                    try { server?.Dispose(); } catch { }
                    // Brief backoff so a persistent failure doesn't spin the CPU.
                    try { await Task.Delay(500, shutdownCts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        // Idle-timeout watcher: shuts the daemon down after TTL_MINUTES of no activity.
        async Task IdleWatcher()
        {
            while (!shutdownCts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(1), shutdownCts.Token); }
                catch (OperationCanceledException) { return; }

                long ticks = Interlocked.Read(ref lastActivityTicks);
                var idle = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                if (idle.TotalMinutes > TTL_MINUTES)
                {
                    Console.Error.WriteLine("Idle timeout — shutting down.");
                    shutdownCts.Cancel();
                    return;
                }
            }
        }

        const int LISTENER_POOL_SIZE = 4;
        var listenerTasks = new Task[LISTENER_POOL_SIZE + 1];
        for (int i = 0; i < LISTENER_POOL_SIZE; i++)
            listenerTasks[i] = RunListener(i);
        listenerTasks[LISTENER_POOL_SIZE] = IdleWatcher();

        try { Task.WaitAll(listenerTasks); }
        catch (AggregateException) { /* cancellation */ }

        foreach (var w in watchers)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            try { w.Dispose(); } catch { }
        }
        CleanupFiles(projectPath);
        return 0;
    }

    // ─── Query handlers ────────────────────────────────────────────────────────────
    //
    // Real extraction + formatting lives in CodeAnalysisCore so the single-pass fallback
    // in clibridge4unity.cs can share the same logic. This wrapper filters the long-lived
    // trees/fileTexts cache to files matching the query and hands them to the core.

    /// <summary>Append the serialized-wiring section (attach sites, SO instances, UnityEvent
    /// targets) to a deep-type CODE_ANALYZE response. Plain type queries only — listings and
    /// member zooms stay untouched. Silent no-op when the graph is empty for this type.</summary>
    static string AugmentWithAssetWiring(string response, AssetGraph graph, string query)
    {
        if (graph == null || string.IsNullOrWhiteSpace(query)) return response;

        string trimmed = query.Trim();
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0 && trimmed.IndexOf(' ') < 0)
        {
            string prefix = trimmed.Substring(0, colonIdx).ToLowerInvariant();
            if (prefix is "class" or "type") trimmed = trimmed.Substring(colonIdx + 1).Trim();
            else return response; // kind-prefixed listing — not a type view
        }
        if (trimmed.Contains('.') || trimmed.Contains(' ')) return response; // member zoom / freeform

        // Only augment an actual deep-type view (avoids decorating not-found errors).
        if (!response.Contains($"=== {trimmed} ===", StringComparison.Ordinal)) return response;
        if (!graph.Ready) return response; // don't block analyze on the graph

        string wiring = graph.FormatWiring(trimmed);
        if (wiring == null) return response;
        return response.TrimEnd() + "\n\n" + wiring;
    }

    /// <summary>If <paramref name="response"/> indicates source-only Analyze couldn't pin
    /// the type and the DLL index has a hit, prepend a DLL-defined report.
    /// Skipped for kind-prefixed queries (`method:`, `field:`, etc.) — those are listings, not type lookups.</summary>
    static string AugmentWithDllHits(string response, DllIndex dllIndex, string query)
    {
        if (dllIndex == null) return response;
        if (string.IsNullOrWhiteSpace(query)) return response;

        string trimmed = query.Trim();
        // Skip prefix queries — they're cross-cutting listings, not type lookups.
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0 && trimmed.IndexOf(' ') < 0)
        {
            string prefix = trimmed.Substring(0, colonIdx).ToLowerInvariant();
            if (prefix is "method" or "field" or "property" or "inherits" or "extends" or "attribute")
                return response;
            // class:/type: — strip prefix and continue.
            if (prefix is "class" or "type")
                trimmed = trimmed.Substring(colonIdx + 1).Trim();
        }

        // Member zoom (Type.Member) only augments on source miss — DLL members are noisy
        // when the type is well-defined in source. Plain type queries always augment if DLL
        // has a match: source can shadow engine types (e.g. a nested test `struct Vector3`
        // hides `UnityEngine.Vector3`). Showing both keeps the engine answer visible.
        bool sourceMissed =
            response.StartsWith("Error: '", StringComparison.Ordinal) && response.Contains("' not found")
            || response.Contains("not found as a declaration, but found in source", StringComparison.Ordinal)
            || response.StartsWith("'", StringComparison.Ordinal) && response.Contains("' is not a type", StringComparison.Ordinal);
        bool isMemberZoom = trimmed.Contains('.');
        if (isMemberZoom && !sourceMissed) return response;

        // DLL index is built in background after source — first query after startup may
        // race ahead of it. Wait up to 5s for it to finish (typical: <2s).
        if (!dllIndex.Ready)
        {
            var sw = Stopwatch.StartNew();
            while (!dllIndex.Ready && sw.ElapsedMilliseconds < 5000)
                Thread.Sleep(50);
            if (!dllIndex.Ready) return response;
        }

        // Split dotted form `Type.Member` for DLL lookup.
        string typePart = trimmed;
        string memberPart = null;
        int lastDot = trimmed.LastIndexOf('.');
        if (lastDot > 0)
        {
            typePart = trimmed.Substring(0, lastDot);
            memberPart = trimmed.Substring(lastDot + 1);
        }
        string simple = DllIndex.SimpleName(typePart);
        var hits = dllIndex.Lookup(simple);
        if (hits == null || hits.Count == 0) return response;

        string dllReport = dllIndex.Format(trimmed, hits, memberPart);
        return dllReport + "\n\n" + response;
    }

    static string HandleAnalyze(ConcurrentDictionary<string, SyntaxTree> trees, ConcurrentDictionary<string, string> fileTexts,
                                Func<string, SyntaxTree> getTree, IEnumerable<string> pkgTypeNames, string projectPath, string query)
    {
        var sw = Stopwatch.StartNew();

        // For prefix queries, filter on the term (after `kind:`), not the whole string.
        string filterTerm = query?.Trim() ?? "";
        int colon = filterTerm.IndexOf(':');
        if (colon > 0 && filterTerm.IndexOf(' ') < 0)
            filterTerm = filterTerm.Substring(colon + 1).Trim();
        // Dotted queries `Foo.Bar` — filter by the most specific segment (last one).
        if (filterTerm.Contains('.'))
            filterTerm = filterTerm.Substring(filterTerm.LastIndexOf('.') + 1);
        // Strip generic params + array brackets so `MyPool<Foo>` / `Foo[]` match
        // files containing the bare identifier.
        int lt = filterTerm.IndexOf('<');
        if (lt > 0) filterTerm = filterTerm.Substring(0, lt);
        while (filterTerm.EndsWith("[]")) filterTerm = filterTerm.Substring(0, filterTerm.Length - 2);

        var matchingTexts = new Dictionary<string, string>();
        foreach (var kvp in fileTexts)
            if (kvp.Value.Contains(filterTerm)) matchingTexts[kvp.Key] = kvp.Value;

        // Resolve trees in parallel. Package files are not resident, so getTree re-parses them —
        // a broad query like `method:Update` matches thousands of files and serial re-parsing cost
        // ~2.2s where the fully-resident index answered in 20ms. Parallel resolution puts that back
        // in the low hundreds of ms. Only files that already passed the text filter get here, so
        // the work scales with matches, not corpus size.
        var resolved = new ConcurrentDictionary<string, SyntaxTree>();
        Parallel.ForEach(matchingTexts.Keys,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            path => { var t = getTree(path); if (t != null) resolved[path] = t; });

        var matchingTrees = new Dictionary<string, SyntaxTree>(resolved.Count);
        foreach (var kvp in resolved) matchingTrees[kvp.Key] = kvp.Value;
        // Drop texts whose tree failed to resolve, so the two maps stay in step.
        foreach (var path in matchingTexts.Keys.ToList())
            if (!matchingTrees.ContainsKey(path)) matchingTexts.Remove(path);

        sw.Stop();
        var resp = CodeAnalysisCore.Analyze(matchingTrees, matchingTexts, projectPath, query, sw.ElapsedMilliseconds, fileTexts.Count);
        // "Did you mean" needs every declared type name; package names come from the harvested set
        // rather than their trees, which are not retained.
        return CodeAnalysisCore.AppendSuggestionsIfMissing(resp, trees, query, pkgTypeNames);
    }

    // `totalIndexed` is the whole corpus (user + package); `trees` now holds user code only, so the
    // skipped-file count has to come from the caller rather than trees.Count.
    static string RunSyntaxLint(ConcurrentDictionary<string, SyntaxTree> trees, string projectPath, bool includeWarnings, int totalIndexed)
    {
        var sb = new StringBuilder();
        int errorCount = 0, warnCount = 0, userFiles = 0;
        foreach (var kvp in trees)
        {
            // Only surface diagnostics for user-owned code. PackageCache is third-party,
            // not actionable, and frequently has files that need package-specific csc settings.
            if (!IsUserCode(kvp.Key, projectPath)) continue;
            userFiles++;
            foreach (var d in kvp.Value.GetDiagnostics())
            {
                if (d.Severity == DiagnosticSeverity.Error) errorCount++;
                else if (d.Severity == DiagnosticSeverity.Warning) warnCount++;
                else continue;
                if (d.Severity == DiagnosticSeverity.Warning && !includeWarnings) continue;
                AppendDiag(sb, kvp.Key, projectPath, d);
            }
        }
        // Always also lint UXML/USS — well-formedness check, sub-second on typical projects.
        var uiRun = LintUI.Run(projectPath);
        int uiErrors = uiRun.Issues.Count;
        var csResult = FormatLintResponse(sb, userFiles, errorCount, warnCount, includeWarnings,
            mode: $"syntax-only ({totalIndexed - userFiles} package files skipped)",
            okHint: "Catches missing braces, bad keywords, unclosed strings.\nMisses: type errors, missing usings, wrong arg counts. Use `LINT semantic` or COMPILE for those.");
        if (uiErrors == 0 && uiRun.UxmlScanned + uiRun.UssScanned == 0) return csResult;
        return csResult + "\n\n" + LintUI.Format(uiRun, projectPath);
    }

    // Declared type names, straight off the source text. Deliberately NOT Roslyn: this runs for
    // every package file, and parsing one just to read its type names costs far more than the names
    // are worth. Only feeds "did you mean" suggestions, where a rare miss or a commented-out match
    // is harmless — correctness here is not load-bearing.
    static readonly System.Text.RegularExpressions.Regex TypeDeclRx = new(
        @"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)|\bdelegate\s+[\w\.\<\>\[\],\s]+?\s+([A-Za-z_]\w*)\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Harvest declared type names from source we are not going to parse. ~5 MB across a
    /// full PackageCache, versus ~335 MB to hold its trees.</summary>
    static void CollectTypeNames(string text, ConcurrentDictionary<string, byte> into)
    {
        try
        {
            foreach (System.Text.RegularExpressions.Match m in TypeDeclRx.Matches(text))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(name)) into.TryAdd(name, 0);
            }
        }
        catch { }
    }

    /// <summary>True if `file` is user-editable code: Assets/ or non-PackageCache Packages/.
    /// Excludes Library/PackageCache/ (UPM-managed third-party packages).</summary>
    static bool IsUserCode(string file, string projectPath)
    {
        string norm = file.Replace('\\', '/');
        string proj = projectPath.Replace('\\', '/').TrimEnd('/');
        if (norm.StartsWith(proj + "/Assets/", StringComparison.OrdinalIgnoreCase)) return true;
        if (norm.StartsWith(proj + "/Packages/", StringComparison.OrdinalIgnoreCase)
            && !norm.Contains("/PackageCache/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string RunSemanticLint(ConcurrentDictionary<string, SyntaxTree> trees, string projectPath, bool includeWarnings, int totalIndexed)
    {
        var sw = Stopwatch.StartNew();
        var (refs, builtin, user, _, editorRoot, version, error) = LintSemantic.Resolve(projectPath);
        if (error != null)
            return $"Error: cannot run semantic lint — {error}\nFalling back to syntax-only mode is recommended.";

        // Re-parse each tree with file-scoped preprocessor symbols (UNITY_EDITOR for /Editor/ files).
        // Skip PackageCache — third-party, not actionable for user.
        var parsed = new List<SyntaxTree>(trees.Count);
        int userFileCount = 0;
        foreach (var kvp in trees)
        {
            if (!IsUserCode(kvp.Key, projectPath)) continue;
            userFileCount++;
            var opts = LintSemantic.BuildParseOptions(kvp.Key, builtin, user);
            var origText = kvp.Value.GetText();
            parsed.Add(CSharpSyntaxTree.ParseText(origText, opts, kvp.Key));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "LintSemantic",
            syntaxTrees: parsed,
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                concurrentBuild: true,
                metadataImportOptions: MetadataImportOptions.All));

        var sb = new StringBuilder();
        int errorCount = 0, warnCount = 0;
        // Filter noise: PDB-related, missing-XML-comment, etc. that aren't actionable for AI.
        var ignoredIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "CS1701", "CS1702", "CS1705",   // assembly version mismatches (Unity DLLs are messy)
            "CS8019",                        // unnecessary using directive
            "CS1591",                        // missing XML comment
            "CS0436",                        // type defined in source AND ref'd assembly (Unity asmdef overlap)
        };
        foreach (var d in compilation.GetDiagnostics())
        {
            if (ignoredIds.Contains(d.Id)) continue;
            if (d.Severity == DiagnosticSeverity.Error) errorCount++;
            else if (d.Severity == DiagnosticSeverity.Warning) warnCount++;
            else continue;
            if (d.Severity == DiagnosticSeverity.Warning && !includeWarnings) continue;
            string filePath = d.Location.SourceTree?.FilePath ?? "(no file)";
            AppendDiag(sb, filePath, projectPath, d);
        }
        sw.Stop();
        string mode = $"semantic ({version}, {refs.Count} refs, {sw.ElapsedMilliseconds}ms, {totalIndexed - userFileCount} package files skipped)";
        string okHint = "Full type-binding pass — would compile under Unity.\nNote: per-file UNITY_EDITOR scoping is best-effort by /Editor/ folder, not asmdef-perfect.";
        return FormatLintResponse(sb, userFileCount, errorCount, warnCount, includeWarnings, mode, okHint);
    }

    static void AppendDiag(StringBuilder sb, string filePath, string projectPath, Diagnostic d)
    {
        var pos = d.Location.GetLineSpan().StartLinePosition;
        string sev = d.Severity == DiagnosticSeverity.Error ? "ERROR" : "WARN";
        string rel = CodeAnalysisCore.ToRelativePath(filePath, projectPath);
        sb.Append(rel).Append(':').Append(pos.Line + 1).Append(':').Append(pos.Character + 1)
          .Append(": ").Append(sev).Append(' ').Append(d.Id).Append(": ")
          .Append(d.GetMessage()).Append('\n');
    }


    static string FormatLintResponse(StringBuilder body, int fileCount, int errorCount, int warnCount,
                                     bool includeWarnings, string mode, string okHint)
    {
        var header = new StringBuilder();
        header.Append("Files: ").Append(fileCount).Append("  Errors: ").Append(errorCount);
        if (includeWarnings) header.Append("  Warnings: ").Append(warnCount);
        header.Append("  Mode: ").Append(mode).Append('\n');
        if (errorCount == 0 && (!includeWarnings || warnCount == 0))
        {
            header.Append("OK — no errors.\n").Append(okHint);
            return header.ToString();
        }
        return header.Append('\n').Append(body.ToString().TrimEnd('\n')).ToString();
    }
}
