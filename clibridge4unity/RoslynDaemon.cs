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

        // Phase 1 (background): Parse all source files. Pipe server starts in parallel
        // so queries can be answered (or queued with "still indexing" status) immediately.
        Task indexTask = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            Console.Error.Write("Parsing source files...");
            var allFiles = new List<string>(Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories));
            if (Directory.Exists(packagesDir))
                allFiles.AddRange(Directory.EnumerateFiles(packagesDir, "*.cs", SearchOption.AllDirectories));
            Interlocked.Exchange(ref totalFilesToIndex, allFiles.Count);

            Parallel.ForEach(allFiles, file =>
            {
                try
                {
                    string text = File.ReadAllText(file);
                    trees[file] = CSharpSyntaxTree.ParseText(text, path: file);
                    fileTexts[file] = text;
                }
                catch { }
            });
            sw.Stop();
            Console.Error.WriteLine($" {trees.Count} files in {sw.ElapsedMilliseconds}ms");
            indexReady.Set();
        });

        // Phase 2: File watcher (covers both Assets and Packages)
        int reParseCount = 0;
        void OnFileChanged(string filePath)
        {
            try
            {
                Thread.Sleep(100); // debounce
                string text = File.ReadAllText(filePath);
                trees[filePath] = CSharpSyntaxTree.ParseText(text, path: filePath);
                fileTexts[filePath] = text;
                Interlocked.Increment(ref reParseCount);
            }
            catch { }
        }

        FileSystemWatcher MakeWatcher(string root)
        {
            var w = new FileSystemWatcher(root, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            w.Changed += (_, e) => Task.Run(() => OnFileChanged(e.FullPath));
            w.Created += (_, e) => Task.Run(() => OnFileChanged(e.FullPath));
            w.Renamed += (_, e) =>
            {
                trees.TryRemove(e.OldFullPath, out SyntaxTree _);
                fileTexts.TryRemove(e.OldFullPath, out string _);
                Task.Run(() => OnFileChanged(e.FullPath));
            };
            w.Deleted += (_, e) =>
            {
                trees.TryRemove(e.FullPath, out SyntaxTree _2);
                fileTexts.TryRemove(e.FullPath, out string _3);
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
                        response = $"files: {trees.Count}/{Volatile.Read(ref totalFilesToIndex)}\nready: {indexReady.IsSet}\nreparses: {reParseCount}\nuptime: {(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds:F0}s\nproject: {projectPath}";
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
                        break;
                    case "shutdown":
                        response = "shutting down";
                        shutdownCts.Cancel();
                        break;
                    default:
                        response = "endpoints: health, status, analyze, search, shutdown";
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
        return CodeAnalysisCore.Analyze(matchingTrees, matchingTexts, projectPath, query, sw.ElapsedMilliseconds, trees.Count);
    }
}
