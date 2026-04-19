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

            // Read response
            var sb = new StringBuilder();
            byte[] buf = new byte[8192];
            int bytesRead;
            while ((bytesRead = pipe.Read(buf, 0, buf.Length)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, bytesRead));

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
        if (!Directory.Exists(assetsDir))
        {
            Console.Error.WriteLine("Error: Assets/ directory not found.");
            return 1;
        }

        // Write PID file immediately so clients know we're starting
        string daemonDir = GetDaemonDir(projectPath);
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(GetPidFile(projectPath), Process.GetCurrentProcess().Id.ToString());

        var sw = Stopwatch.StartNew();

        // Phase 1: Parse all source files
        Console.Error.Write("Parsing source files...");
        var allFiles = Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories).ToArray();
        var trees = new ConcurrentDictionary<string, SyntaxTree>();
        var fileTexts = new ConcurrentDictionary<string, string>();

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

        // Phase 2: File watcher
        var watcher = new FileSystemWatcher(assetsDir, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

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

        watcher.Changed += (_, e) => Task.Run(() => OnFileChanged(e.FullPath));
        watcher.Created += (_, e) => Task.Run(() => OnFileChanged(e.FullPath));
        watcher.Renamed += (_, e) =>
        {
            trees.TryRemove(e.OldFullPath, out SyntaxTree _);
            fileTexts.TryRemove(e.OldFullPath, out string _);
            Task.Run(() => OnFileChanged(e.FullPath));
        };
        watcher.Deleted += (_, e) =>
        {
            trees.TryRemove(e.FullPath, out SyntaxTree _2);
            fileTexts.TryRemove(e.FullPath, out string _3);
        };
        watcher.EnableRaisingEvents = true;

        // Phase 3: Start pipe server (PID file already written at startup)
        string pipeName = GeneratePipeName(projectPath);
        Console.Error.WriteLine($"Daemon started: {pipeName}");
        Console.Error.WriteLine($"  {trees.Count} files indexed, watching for changes");
        Console.Error.WriteLine($"  Auto-shutdown after {TTL_MINUTES} minutes of inactivity");

        // Signal parent that we're ready
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
                        response = "ok";
                        break;
                    case "status":
                        response = $"files: {trees.Count}\nreparses: {reParseCount}\nuptime: {(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds:F0}s\nproject: {projectPath}";
                        break;
                    case "analyze":
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

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        CleanupFiles(projectPath);
        return 0;
    }

    // ─── Query handlers ──────────────────────────────────────────────

    static string Rel(string file, string projectPath)
        => file.Replace(projectPath + "\\", "").Replace(projectPath + "/", "");

    static string HandleAnalyze(ConcurrentDictionary<string, SyntaxTree> trees, ConcurrentDictionary<string, string> fileTexts, string projectPath, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: No query. Usage: CODE_ANALYZE ClassName | ClassName.Member | method:Name | field:Name | inherits:Type | attribute:Name";

        query = query.Trim();

        // Prefix dispatch: method:/field:/property:/inherits:/attribute: → listing via HandleSearch.
        // class:/type: → strip prefix, do deep analysis on the term.
        int colonIdx = query.IndexOf(':');
        if (colonIdx > 0 && query.IndexOf(' ') < 0)
        {
            string prefix = query.Substring(0, colonIdx).ToLowerInvariant();
            string term = query.Substring(colonIdx + 1).Trim();
            switch (prefix)
            {
                case "class":
                case "type":
                    query = term;
                    break;
                case "method":
                case "field":
                case "property":
                case "inherits":
                case "extends":
                case "attribute":
                    return HandleSearch(trees, fileTexts, projectPath, $"{prefix}:{term}");
            }
        }

        // Support dotted member queries: ClassName.MemberName
        string className = query;
        string memberName = null;
        if (query.Contains('.'))
        {
            int dotIdx = query.IndexOf('.');
            className = query.Substring(0, dotIdx);
            memberName = query.Substring(dotIdx + 1);
        }

        var sw = Stopwatch.StartNew();

        var sourceFiles = new List<string>();
        var baseTypes = new List<string>();
        var derivedTypes = new List<string>();
        var fieldUsages = new List<string>();
        var paramUsages = new List<string>();
        var returnUsages = new List<string>();
        var getComponentUsages = new List<string>();
        var localVarUsages = new List<string>();
        var ownMethods = new List<string>();
        var ownFields = new List<string>();
        var grepLines = new ConcurrentBag<string>();

        // Search for className (not full dotted query) so we find the class + all references
        var matching = fileTexts.Where(kvp => kvp.Value.Contains(className)).Select(kvp => kvp.Key).ToList();

        Parallel.ForEach(matching, file =>
        {
            if (!trees.TryGetValue(file, out var tree)) return;
            var root = tree.GetRoot();
            string rel = Rel(file, projectPath);

            // Grep lines — search for member name if specified, otherwise class name
            string grepTerm = memberName ?? className;
            var lines = fileTexts.TryGetValue(file, out var ft) ? ft.Split('\n') : Array.Empty<string>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(grepTerm))
                {
                    string lt = lines[i].Trim();
                    if (lt.Length > 120) lt = lt.Substring(0, 120) + "...";
                    grepLines.Add($"{rel}:{i + 1}: {lt}");
                }

            foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                string enc = td.Identifier.Text;
                bool isSelf = enc.Equals(className, StringComparison.OrdinalIgnoreCase);

                if (isSelf)
                {
                    int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var bases = td.BaseList?.Types.Select(t => t.Type.ToString()).ToArray() ?? Array.Empty<string>();
                    lock (sourceFiles) { sourceFiles.Add($"{rel}:{line} ({td.Modifiers} {td.Keyword} {enc} : {string.Join(", ", bases)})"); baseTypes.AddRange(bases); }

                    foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                    {
                        int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                        lock (ownMethods) ownMethods.Add($"{m.Modifiers} {m.ReturnType} {m.Identifier.Text}({parms}) — {rel}:{mLine}");
                    }
                    foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                        foreach (var v in f.Declaration.Variables)
                        { int fl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (ownFields) ownFields.Add($"{f.Modifiers} {f.Declaration.Type} {v.Identifier} — {rel}:{fl}"); }
                    foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    { int pl = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (ownFields) ownFields.Add($"{p.Modifiers} {p.Type} {p.Identifier} {{ get; set; }} — {rel}:{pl}"); }
                    continue;
                }

                if (td.BaseList?.Types.Any(t => t.Type.ToString().Contains(className)) == true)
                { int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (derivedTypes) derivedTypes.Add($"{enc} — {rel}:{line}"); }

                foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                    if (f.Declaration.Type.ToString().Contains(className))
                        foreach (var v in f.Declaration.Variables)
                        { int fl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (fieldUsages) fieldUsages.Add($"{enc}.{v.Identifier} — {rel}:{fl}"); }

                foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    if (p.Type.ToString().Contains(className))
                    { int pl = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (fieldUsages) fieldUsages.Add($"{enc}.{p.Identifier} (prop) — {rel}:{pl}"); }

                foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                {
                    bool hasParam = m.ParameterList.Parameters.Any(p => p.Type?.ToString().Contains(className) == true);
                    bool returnsIt = m.ReturnType.ToString().Contains(className);
                    if (hasParam)
                    { int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1; var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}")); lock (paramUsages) paramUsages.Add($"{enc}.{m.Identifier.Text}({parms}) — {rel}:{ml}"); }
                    if (returnsIt)
                    { int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (returnUsages) returnUsages.Add($"{enc}.{m.Identifier.Text}() returns {m.ReturnType} — {rel}:{ml}"); }
                    if (m.Body != null)
                    {
                        string bt = m.Body.ToString();
                        if (bt.Contains($"GetComponent<{className}>") || bt.Contains($"GetComponentInChildren<{className}>"))
                        { int ml = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (getComponentUsages) getComponentUsages.Add($"{enc}.{m.Identifier.Text}() — {rel}:{ml}"); }
                    }
                }

                foreach (var ld in td.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                    if (ld.Declaration.Type.ToString().Contains(className))
                    {
                        var method = ld.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        string mn = method?.Identifier.Text ?? "?";
                        foreach (var v in ld.Declaration.Variables)
                        { int vl = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1; lock (localVarUsages) localVarUsages.Add($"{enc}.{mn}() var {v.Identifier} — {rel}:{vl}"); }
                    }
            }
        });

        sw.Stop();
        var sb = new StringBuilder();

        if (sourceFiles.Count == 0 && grepLines.Count == 0)
            return $"Error: '{query}' not found ({trees.Count} files indexed, {sw.ElapsedMilliseconds}ms)";

        // If member query, filter to just that member
        if (memberName != null && sourceFiles.Count > 0)
        {
            sb.AppendLine($"=== {className}.{memberName} === ({sw.ElapsedMilliseconds}ms, {trees.Count} indexed)");
            sb.AppendLine();
            var matchingMethods = ownMethods.Where(m => m.Contains(memberName, StringComparison.OrdinalIgnoreCase)).ToList();
            var matchingFields = ownFields.Where(f => f.Contains(memberName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingMethods.Count > 0) { sb.AppendLine("Methods:"); foreach (var m in matchingMethods) sb.AppendLine($"  {m}"); }
            if (matchingFields.Count > 0) { sb.AppendLine("Fields:"); foreach (var f in matchingFields) sb.AppendLine($"  {f}"); }
            if (matchingMethods.Count == 0 && matchingFields.Count == 0)
                sb.AppendLine($"Member '{memberName}' not found in {className}");
            sb.AppendLine();
            sb.AppendLine($"Defined in:");
            foreach (var s in sourceFiles) sb.AppendLine($"  {s}");
        }
        else if (sourceFiles.Count > 0)
        {
            sb.AppendLine($"=== {className} === ({matching.Count} files matched in {sw.ElapsedMilliseconds}ms, {trees.Count} indexed)");
            sb.AppendLine();
            sb.AppendLine("Defined in:");
            foreach (var s in sourceFiles) sb.AppendLine($"  {s}");
            if (baseTypes.Count > 0) sb.AppendLine($"Inherits from: {string.Join(", ", baseTypes.Distinct())}");
            if (derivedTypes.Count > 0) { sb.AppendLine($"Inherited by ({derivedTypes.Count}):"); foreach (var d in derivedTypes.Take(15)) sb.AppendLine($"  {d}"); if (derivedTypes.Count > 15) sb.AppendLine($"  ... +{derivedTypes.Count - 15} more"); }
            if (fieldUsages.Count > 0) { sb.AppendLine($"Referenced as field/property ({fieldUsages.Count}):"); foreach (var f in fieldUsages.Take(20)) sb.AppendLine($"  {f}"); if (fieldUsages.Count > 20) sb.AppendLine($"  ... +{fieldUsages.Count - 20} more"); }
            if (paramUsages.Count > 0) { sb.AppendLine($"Passed as parameter ({paramUsages.Count}):"); foreach (var p in paramUsages.Take(15)) sb.AppendLine($"  {p}"); if (paramUsages.Count > 15) sb.AppendLine($"  ... +{paramUsages.Count - 15} more"); }
            if (returnUsages.Count > 0) { sb.AppendLine($"Returned by ({returnUsages.Count}):"); foreach (var r in returnUsages.Take(10)) sb.AppendLine($"  {r}"); }
            if (getComponentUsages.Count > 0) { sb.AppendLine($"GetComponent<{className}>() ({getComponentUsages.Count}):"); foreach (var g in getComponentUsages.Take(10)) sb.AppendLine($"  {g}"); }
            if (localVarUsages.Count > 0) { sb.AppendLine($"Local variables ({localVarUsages.Count}):"); foreach (var l in localVarUsages.Take(10)) sb.AppendLine($"  {l}"); }
            if (ownMethods.Count > 0) { sb.AppendLine($"Methods ({ownMethods.Count}):"); foreach (var m in ownMethods.Take(25)) sb.AppendLine($"  {m}"); if (ownMethods.Count > 25) sb.AppendLine($"  ... +{ownMethods.Count - 25} more"); }
            if (ownFields.Count > 0) { sb.AppendLine($"Fields/Properties ({ownFields.Count}):"); foreach (var f in ownFields.Take(25)) sb.AppendLine($"  {f}"); if (ownFields.Count > 25) sb.AppendLine($"  ... +{ownFields.Count - 25} more"); }
        }
        else
            sb.AppendLine($"Type '{className}' not found as a declaration, but found in source:");

        var sortedGrep = grepLines.OrderBy(g => g).ToList();
        if (sortedGrep.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Raw references ({sortedGrep.Count} lines) ---");
            foreach (var g in sortedGrep.Take(40)) sb.AppendLine(g);
            if (sortedGrep.Count > 40) sb.AppendLine($"... +{sortedGrep.Count - 40} more");
        }
        return sb.ToString().TrimEnd();
    }

    static string HandleSearch(ConcurrentDictionary<string, SyntaxTree> trees, ConcurrentDictionary<string, string> fileTexts, string projectPath, string query)
    {
        var sw = Stopwatch.StartNew();
        string searchType = "content";
        string searchTerm = query;
        if (query.Contains(':')) { int idx = query.IndexOf(':'); searchType = query.Substring(0, idx).ToLower(); searchTerm = query.Substring(idx + 1); }

        var matching = fileTexts.Where(kvp => kvp.Value.Contains(searchTerm)).Select(kvp => kvp.Key).ToList();
        var results = new ConcurrentBag<string>();

        Parallel.ForEach(matching, file =>
        {
            if (!trees.TryGetValue(file, out var tree)) return;
            var root = tree.GetRoot();
            string rel = Rel(file, projectPath);

            switch (searchType)
            {
                case "class": case "type":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                        if (td.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        { int l = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1; var b = td.BaseList?.Types.Select(t => t.Type.ToString()).ToArray() ?? Array.Empty<string>(); results.Add($"{td.Modifiers} {td.Keyword} {td.Identifier.Text}{(b.Length > 0 ? $" : {string.Join(", ", b)}" : "")} — {rel}:{l}"); }
                    break;
                case "method":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                        foreach (var m in td.Members.OfType<MethodDeclarationSyntax>())
                            if (m.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            { int l = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1; results.Add($"{td.Identifier.Text}.{m.Identifier.Text}({string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"))}) : {m.ReturnType} — {rel}:{l}"); }
                    break;
                case "field": case "property":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    {
                        foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                            foreach (var v in f.Declaration.Variables)
                                if (v.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                { int l = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1; results.Add($"{td.Identifier.Text}.{v.Identifier} : {f.Declaration.Type} — {rel}:{l}"); }
                        foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                            if (p.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            { int l = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1; results.Add($"{td.Identifier.Text}.{p.Identifier} : {p.Type} (prop) — {rel}:{l}"); }
                    }
                    break;
                case "inherits": case "extends":
                    foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                        if (td.BaseList?.Types.Any(t => t.Type.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) == true)
                        { int l = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1; results.Add($"{td.Identifier.Text} : {string.Join(", ", td.BaseList.Types.Select(t => t.Type.ToString()))} — {rel}:{l}"); }
                    break;
                case "attribute":
                    foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
                        if (attr.Name.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        { int l = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1; var par = attr.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(); string pn = par switch { MethodDeclarationSyntax m => m.Identifier.Text + "()", TypeDeclarationSyntax t => t.Identifier.Text, PropertyDeclarationSyntax p => p.Identifier.Text, FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.Text, _ => "?" }; results.Add($"[{attr.Name}] on {pn} — {rel}:{l}"); }
                    break;
                default:
                    if (!fileTexts.TryGetValue(file, out var ft)) break;
                    var flines = ft.Split('\n');
                    for (int i = 0; i < flines.Length; i++)
                        if (flines[i].Contains(searchTerm))
                        { string lt = flines[i].Trim(); if (lt.Length > 100) lt = lt.Substring(0, 100) + "..."; results.Add($"{rel}:{i + 1}: {lt}"); }
                    break;
            }
        });

        sw.Stop();
        var sorted = results.OrderBy(r => r).ToList();
        if (sorted.Count == 0) return $"No matches for '{query}' ({trees.Count} files indexed, {sw.ElapsedMilliseconds}ms)";
        var sb = new StringBuilder();
        sb.AppendLine($"Found {sorted.Count} matches ({sw.ElapsedMilliseconds}ms, {trees.Count} files indexed):");
        sb.AppendLine();
        foreach (var r in sorted.Take(50)) sb.AppendLine(r);
        if (sorted.Count > 50) sb.AppendLine($"... +{sorted.Count - 50} more");
        return sb.ToString().TrimEnd();
    }
}
