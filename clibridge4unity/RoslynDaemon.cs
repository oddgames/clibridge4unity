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

    // ─── Client side ─────────────────────────────────────────────────

    /// <summary>Check if daemon is running. Returns pipe name or null.</summary>
    public static string GetRunningPipe(string projectPath)
    {
        string pidFile = GetPidFile(projectPath);
        if (!File.Exists(pidFile)) return null;

        try
        {
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return null;
            try { Process.GetProcessById(pid); }
            catch { CleanupFiles(projectPath); return null; }

            // Process is alive — trust the PID and return pipe name
            // Skip health check to avoid consuming a pipe connection
            return GeneratePipeName(projectPath);
        }
        catch { return null; }
    }

    /// <summary>Query the daemon via named pipe. Retries once on transient failure.</summary>
    public static string Query(string pipeName, string endpoint, string query)
    {
        var result = QueryInternal(pipeName, endpoint, query, 30000, out string error);
        if (result != null) return result;

        // One retry — covers the brief window after a listener finishes and before
        // a sibling listener re-arms under heavy concurrent load.
        Thread.Sleep(100);
        result = QueryInternal(pipeName, endpoint, query, 30000, out error);
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
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "clibridge4unity.exe");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-d \"{projectPath}\" DAEMON",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // Don't redirect — let stderr flow freely to avoid buffer deadlock
        };

        try
        {
            var proc = Process.Start(psi);
            if (proc == null) return null;

            // Read stdout for "pipe:<name>" signal (daemon writes this when ready)
            string pipeName = null;
            var readTask = Task.Run(() =>
            {
                try
                {
                    string line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        if (line.StartsWith("pipe:"))
                        {
                            pipeName = line.Substring(5).Trim();
                            break;
                        }
                    }
                }
                catch { }
            });

            // Wait up to 15 seconds for the daemon to parse + start
            readTask.Wait(TimeSpan.FromSeconds(15));
            return pipeName;
        }
        catch { return null; }
    }

    static void CleanupFiles(string projectPath)
    {
        try { File.Delete(GetPidFile(projectPath)); } catch { }
        try { File.Delete(GetPipeFile(projectPath)); } catch { }
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

        var trees = new ConcurrentDictionary<string, SyntaxTree>();
        var fileTexts = new ConcurrentDictionary<string, string>();
        int totalFilesToIndex = 0;
        var indexReady = new ManualResetEventSlim(false);

        // DLL index — built in background after .cs parse completes. Used as fallback
        // when CODE_ANALYZE can't find a type in source (precompiled plugins, package DLLs).
        var dllIndex = new DllIndex(projectPath);

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
            Parallel.ForEach(allFiles, file =>
            {
                try
                {
                    string text = File.ReadAllText(file);
                    trees[file] = CSharpSyntaxTree.ParseText(text, defaultParseOpts, file);
                    fileTexts[file] = text;
                }
                catch { }
            });
            sw.Stop();
            Console.Error.WriteLine($" {trees.Count} files in {sw.ElapsedMilliseconds}ms");
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

        // Phase 2: File watcher (covers both Assets and Packages)
        int reParseCount = 0;
        void OnFileChanged(string filePath)
        {
            try
            {
                Thread.Sleep(100); // debounce
                string text = File.ReadAllText(filePath);
                var opts = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
                trees[filePath] = CSharpSyntaxTree.ParseText(text, opts, filePath);
                fileTexts[filePath] = text;
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
            w.Changed += (_, e) =>
            {
                if (!IsTrackedPath(e.FullPath)) return;
                RecordChange("M", e.FullPath);
                if (IsCsFile(e.FullPath)) Task.Run(() => OnFileChanged(e.FullPath));
            };
            w.Created += (_, e) =>
            {
                if (!IsTrackedPath(e.FullPath)) return;
                RecordChange("C", e.FullPath);
                if (IsCsFile(e.FullPath)) Task.Run(() => OnFileChanged(e.FullPath));
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

        // Signal parent that we're ready to ACCEPT — even before indexing finishes.
        // Queries that arrive during indexing block briefly waiting for it to complete.
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
                        response = $"files: {trees.Count}/{Volatile.Read(ref totalFilesToIndex)}\nready: {indexReady.IsSet}\nreparses: {reParseCount}\ndlls: {dllIndex.DllCount} ({dllIndex.TypeCount} types, ready={dllIndex.Ready})\nuptime: {(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds:F0}s\nproject: {projectPath}";
                        break;
                    case "analyze":
                        // Don't block the connection on indexing — return a progress sentinel
                        // immediately so the client can poll + render a heartbeat. Each request
                        // is short and stateless; client retries until indexReady.IsSet.
                        if (!indexReady.IsSet)
                        {
                            response = $"__indexing:{trees.Count}/{Volatile.Read(ref totalFilesToIndex)}";
                            break;
                        }
                        response = HandleAnalyze(trees, fileTexts, projectPath, query);
                        response = AugmentWithDllHits(response, dllIndex, query);
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
                        // Query: "" (syntax-only, default), "warnings", "semantic" (+ Unity DLL binding),
                        //        "semantic warnings" (semantic + warnings).
                        if (!indexReady.IsSet)
                        {
                            response = $"__indexing:{trees.Count}/{Volatile.Read(ref totalFilesToIndex)}";
                            break;
                        }
                        string q = (query ?? "").Trim().ToLowerInvariant();
                        bool syntaxMode = q.Contains("syntax");
                        bool semantic = q.Contains("semantic") || q.Contains("full");
                        bool includeWarnings = q.Contains("warning");
                        bool graphProbe = q.Contains("graph");
                        // Default = unity (per-asmdef, Unity-faithful). Other modes are explicit opt-ins.
                        bool unityMode = !syntaxMode && !semantic && !graphProbe;
                        if (graphProbe) { response = RunAsmdefGraphProbe(projectPath); break; }
                        if (unityMode)
                        {
                            // Unity-faithful per-asmdef compile. Reuses daemon's parsed file texts.
                            var fileTextsSnapshot = new Dictionary<string, string>(fileTexts.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var kvp in fileTexts) fileTextsSnapshot[kvp.Key] = kvp.Value;
                            var run = LintUnity.Run(projectPath, fileTextsSnapshot);
                            response = LintUnity.Format(run, projectPath, includeWarnings);
                            break;
                        }
                        response = semantic
                            ? RunSemanticLint(trees, projectPath, includeWarnings)
                            : RunSyntaxLint(trees, projectPath, includeWarnings);
                        break;
                    }
                    case "shutdown":
                        response = "shutting down";
                        shutdownCts.Cancel();
                        break;
                    default:
                        response = "endpoints: health, status, analyze, search, lint, compile-changes, compile-mark, shutdown";
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

    static string HandleAnalyze(ConcurrentDictionary<string, SyntaxTree> trees, ConcurrentDictionary<string, string> fileTexts, string projectPath, string query)
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
        var matchingTrees = new Dictionary<string, SyntaxTree>();
        foreach (var kvp in fileTexts)
        {
            if (!kvp.Value.Contains(filterTerm)) continue;
            if (!trees.TryGetValue(kvp.Key, out var tree)) continue;
            matchingTexts[kvp.Key] = kvp.Value;
            matchingTrees[kvp.Key] = tree;
        }

        sw.Stop();
        var resp = CodeAnalysisCore.Analyze(matchingTrees, matchingTexts, projectPath, query, sw.ElapsedMilliseconds, trees.Count);
        // Full trees needed for "did you mean" — pre-filter eliminates candidates on miss.
        return CodeAnalysisCore.AppendSuggestionsIfMissing(resp, trees, query);
    }

    static string RunSyntaxLint(ConcurrentDictionary<string, SyntaxTree> trees, string projectPath, bool includeWarnings)
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
        return FormatLintResponse(sb, userFiles, errorCount, warnCount, includeWarnings,
            mode: $"syntax-only ({trees.Count - userFiles} package files skipped)",
            okHint: "Catches missing braces, bad keywords, unclosed strings.\nMisses: type errors, missing usings, wrong arg counts. Use `LINT semantic` or COMPILE for those.");
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

    static string RunSemanticLint(ConcurrentDictionary<string, SyntaxTree> trees, string projectPath, bool includeWarnings)
    {
        var sw = Stopwatch.StartNew();
        var (refs, builtin, user, editorRoot, version, error) = LintSemantic.Resolve(projectPath);
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
        string mode = $"semantic ({version}, {refs.Count} refs, {sw.ElapsedMilliseconds}ms, {trees.Count - userFileCount} package files skipped)";
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

    static string RunAsmdefGraphProbe(string projectPath)
    {
        var sw = Stopwatch.StartNew();
        var graph = LintAsmdef.Build(projectPath);
        sw.Stop();
        var sb = new StringBuilder();
        sb.AppendLine($"=== asmdef graph ({sw.ElapsedMilliseconds}ms) ===");
        sb.AppendLine($"Total asmdefs: {graph.All.Count}");
        sb.AppendLine($"  Predefined: 4 ({graph.AssemblyCSharp.SourceFiles.Count} + {graph.AssemblyCSharpEditor.SourceFiles.Count} + {graph.AssemblyCSharpFirstpass.SourceFiles.Count} + {graph.AssemblyCSharpEditorFirstpass.SourceFiles.Count} files)");
        int realCount = graph.All.Count - 4;
        int withFiles = graph.All.Count(a => a.SourceFiles.Count > 0);
        int totalFiles = graph.All.Sum(a => a.SourceFiles.Count);
        sb.AppendLine($"  Real asmdefs: {realCount}");
        sb.AppendLine($"  Asmdefs with files: {withFiles}");
        sb.AppendLine($"  Total .cs routed: {totalFiles}");
        sb.AppendLine();
        sb.AppendLine("Top 10 by file count:");
        foreach (var a in graph.All.OrderByDescending(x => x.SourceFiles.Count).Take(10))
            sb.AppendLine($"  {a.SourceFiles.Count,5}  {a.Name}");
        sb.AppendLine();
        sb.AppendLine("Sample reference resolution (first 5 user asmdefs):");
        foreach (var a in graph.All.Where(x => x.AsmdefPath != null && !x.AsmdefPath.Contains("PackageCache")).Take(5))
        {
            sb.AppendLine($"  {a.Name}:");
            foreach (var refTok in a.References.Take(3))
            {
                var resolved = LintAsmdef.ResolveRef(refTok, graph);
                sb.AppendLine($"    {refTok} → {(resolved != null ? resolved.Name : "(unresolved)")}");
            }
            if (a.References.Count > 3) sb.AppendLine($"    ... +{a.References.Count - 3} more");
        }
        return sb.ToString().TrimEnd();
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
