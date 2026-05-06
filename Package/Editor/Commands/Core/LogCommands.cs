using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Captures Unity console logs and provides them via bridge commands.
    /// Logs persist to a temp file so they survive assembly reloads.
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    public static class LogCommands
    {
        // Lazy log capture: `Application.logMessageReceived` is subscribed only WHILE a command
        // is executing — ExecuteCommand wraps each command in BeginCommandCapture/EndCommandCapture.
        // Logs from before/between commands aren't buffered (use Unity's Console via LOG instead).
        // Eliminates the per-frame allocation churn the old streaming-to-file design caused.
        private static readonly List<LogEntry> _currentCapture = new List<LogEntry>();
        private static readonly object _currentCaptureLock = new object();
        private static long _nextId;
        private static bool _captureSubscribed;

        // Compile errors persist across log clears — tracked separately
        private static readonly List<CompilerMessage> _compileErrors = new List<CompilerMessage>();
        private static readonly object _compileErrorLock = new object();

        static LogCommands()
        {
            BridgeDiagnostics.Log("LogCommands", "static ctor");
            EditorApplication.update += InitOnFirstTick;
        }

        private static void InitOnFirstTick()
        {
            BridgeDiagnostics.Log("LogCommands", "InitOnFirstTick");
            EditorApplication.update -= InitOnFirstTick;
            Initialize();
        }

        private static void Initialize()
        {
            BridgeDiagnostics.Log("LogCommands", "Initialize enter");

            AssemblyReloadEvents.beforeAssemblyReload += ShutdownForReload;

            // Restore log ID counter across domain reloads
            _nextId = UnityEditor.SessionState.GetInt(SessionKeys.LogNextId, 1);

            // Restore compile errors from SessionState (survive domain reload)
            // Only restore genuine Unity compiler errors (path.cs(line,col): error CS...)
            // to prevent CODE_EXEC Roslyn errors or other Bridge messages from persisting.
            string savedErrors = SessionState.GetString("Bridge_CompileErrors", "");
            if (!string.IsNullOrEmpty(savedErrors))
            {
                lock (_compileErrorLock)
                {
                    foreach (var line in savedErrors.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line)
                            && !line.StartsWith("[Bridge]")
                            && System.Text.RegularExpressions.Regex.IsMatch(line, @"error CS\d+"))
                            _compileErrors.Add(new CompilerMessage { message = line, type = CompilerMessageType.Error });
                    }
                }
            }

            BridgeDiagnostics.Log("LogCommands", "registering hooks");
            CommandRegistry.BeginCommandLogCapture = BeginCommandCapture;
            CommandRegistry.EndCommandLogCapture = EndCommandCapture;
            CommandRegistry.GetCompileErrors = GetCompileErrorsSummary;
            CommandRegistry.GetUiToolkitDiagnosticsForCommand = GetUiToolkitDiagnosticsForCommand;
            CommandRegistry.ShortenResponsePaths = StackTraceMinimizer.ShortenPaths;

            BridgeDiagnostics.Log("LogCommands", "subscribing compile events (rare, cheap)");
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationStarted += _ =>
            {
                lock (_compileErrorLock) _compileErrors.Clear();
                SessionState.SetString("Bridge_CompileErrors", "");
            };
            BridgeDiagnostics.Log("LogCommands", "Initialize exit");
        }

        // ─── Lazy command-scoped log capture ─────────────────────────────────────
        /// <summary>Subscribe to `Application.logMessageReceived` for the duration of one command.
        /// Reset capture buffer. Called by CommandRegistry.ExecuteCommand at command start.</summary>
        public static void BeginCommandCapture()
        {
            lock (_currentCaptureLock) _currentCapture.Clear();
            if (!_captureSubscribed)
            {
                Application.logMessageReceived += OnLogMessage;
                _captureSubscribed = true;
            }
        }

        /// <summary>Unsubscribe + return formatted captured logs (errors/exceptions only by default).
        /// Returns null if nothing to surface.</summary>
        public static string EndCommandCapture(int maxLines = 5, bool errorsOnly = true)
        {
            if (_captureSubscribed)
            {
                Application.logMessageReceived -= OnLogMessage;
                _captureSubscribed = false;
            }
            return GetCurrentCaptureFormatted(maxLines, errorsOnly);
        }

        /// <summary>Peek current capture without unsubscribing — used by CODE_EXEC for log-settling polls.</summary>
        public static int GetCurrentCaptureCount()
        {
            lock (_currentCaptureLock) return _currentCapture.Count;
        }

        /// <summary>Format captured logs for response. errorsOnly=false includes Debug.Log
        /// (used by CODE_EXEC where info logs are the primary user feedback).</summary>
        public static string GetCurrentCaptureFormatted(int maxLines = 50, bool errorsOnly = false)
        {
            List<LogEntry> snapshot;
            lock (_currentCaptureLock)
            {
                if (_currentCapture.Count == 0) return null;
                snapshot = errorsOnly
                    ? _currentCapture.Where(e => e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert).ToList()
                    : new List<LogEntry>(_currentCapture);
            }
            if (snapshot.Count == 0) return null;
            if (snapshot.Count > maxLines)
                snapshot = snapshot.Skip(snapshot.Count - maxLines).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"--- {(errorsOnly ? "Errors" : "Logs")} ({snapshot.Count}) ---");
            FormatEntriesCompact(snapshot, sb, includeTimestamp: false);
            return sb.ToString().TrimEnd();
        }

        private static void ShutdownForReload()
        {
            BridgeDiagnostics.Log("LogCommands", "ShutdownForReload enter");
            if (_captureSubscribed)
            {
                Application.logMessageReceived -= OnLogMessage;
                _captureSubscribed = false;
            }
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            UnityEditor.SessionState.SetInt(SessionKeys.LogNextId, (int)_nextId);

            lock (_compileErrorLock)
            {
                if (_compileErrors.Count > 0)
                {
                    var errors = string.Join("\n", _compileErrors.Select(e => e.message).Take(20));
                    SessionState.SetString("Bridge_CompileErrors", errors);
                }
            }
            BridgeDiagnostics.Log("LogCommands", "ShutdownForReload exit");
        }

        public static void ClearCompileErrors()
        {
            lock (_compileErrorLock) _compileErrors.Clear();
            SessionState.SetString("Bridge_CompileErrors", "");
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            lock (_compileErrorLock)
            {
                foreach (var msg in messages)
                {
                    // Capture both errors and warnings-as-errors (e.g. CS1998 with TreatWarningsAsErrors)
                    if (msg.type == CompilerMessageType.Error)
                        _compileErrors.Add(msg);
                }
                if (_compileErrors.Count > 0)
                    BridgeDiagnostics.Log("LogCommands", $"compile errors tracked: {_compileErrors.Count}, assembly={assemblyPath}");
            }
        }

        /// <summary>
        /// Fast console error/warning counts via LogEntries reflection. No entry scanning.
        /// </summary>
        public static void GetConsoleCounts(out int errors, out int warnings)
        {
            errors = 0;
            warnings = 0;
            try
            {
                var logEntries = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntries == null) return;
                var getCount = logEntries.GetMethod("GetCountsByType",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (getCount != null)
                {
                    var args = new object[] { 0, 0, 0 };
                    getCount.Invoke(null, args);
                    errors = (int)args[0];
                    warnings = (int)args[1];
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads compile errors (error CS*) directly from Unity's Console via LogEntries.
        /// Called on demand by LOG compile, not on every STATUS poll.
        /// </summary>
        public static List<string> GetCompileErrorsFromConsole()
        {
            // Primary: use CompilationPipeline events (cleared on each compile start)
            lock (_compileErrorLock)
            {
                if (_compileErrors.Count > 0)
                {
                    var result = new List<string>();
                    foreach (var err in _compileErrors)
                        result.Add(err.message.Split('\n')[0]);
                    return result;
                }
            }

            // Fallback: scan Unity console for "error CS" entries
            // Needed when CompilationPipeline callbacks aren't registered
            // (e.g., after failed compilation that prevented domain reload)
            var fallback = new List<string>();
            try
            {
                var logEntries = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntries == null) return fallback;

                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
                var startGetting = logEntries.GetMethod("StartGettingEntries", flags);
                var endGetting = logEntries.GetMethod("EndGettingEntries", flags);
                var getEntry = logEntries.GetMethod("GetEntryInternal", flags);
                var getCount = logEntries.GetMethod("GetCount", flags);
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");

                if (startGetting == null || getCount == null || getEntry == null || logEntryType == null)
                    return fallback;

                startGetting.Invoke(null, null);
                try
                {
                    int count = (int)getCount.Invoke(null, null);
                    // Scan the entire console — compile errors can be buried by warnings/info
                    // and we'd rather block execution one false alarm than miss a real one.
                    for (int i = count - 1; i >= 0; i--)
                    {
                        var entry = System.Activator.CreateInstance(logEntryType);
                        if ((bool)getEntry.Invoke(null, new object[] { i, entry }))
                        {
                            var msg = logEntryType.GetField("message")?.GetValue(entry)?.ToString();
                            // Require Unity compiler format: path/to/file.cs(line,col): error CS
                            // This avoids matching CODE_EXEC Roslyn errors logged to console.
                            if (msg != null && System.Text.RegularExpressions.Regex.IsMatch(msg, @"\.cs\(\d+,\d+\).*error CS"))
                            {
                                string firstLine = msg.Split('\n')[0];
                                if (!fallback.Contains(firstLine))
                                    fallback.Add(firstLine);
                                if (fallback.Count >= 20) break;
                            }
                        }
                    }
                }
                finally
                {
                    endGetting?.Invoke(null, null);
                }
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// Returns a formatted compile error summary, or null if no errors.
        /// </summary>
        public static string GetCompileErrorsSummary()
        {
            // Check our tracked errors first
            lock (_compileErrorLock)
            {
                if (_compileErrors.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"COMPILE ERRORS ({_compileErrors.Count}) — fix these before running commands:");
                    foreach (var err in _compileErrors.Take(10))
                        sb.AppendLine($"  {err.message}");
                    if (_compileErrors.Count > 10)
                        sb.AppendLine($"  ... and {_compileErrors.Count - 10} more");
                    return sb.ToString().TrimEnd();
                }
            }

            // Fall back to reading Unity's Console directly (survives domain reload)
            var consoleErrors = GetCompileErrorsFromConsole();
            if (consoleErrors.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"COMPILE ERRORS ({consoleErrors.Count}) — fix these before running commands:");
                foreach (var err in consoleErrors)
                    sb.AppendLine($"  {err}");
                return sb.ToString().TrimEnd();
            }

            return null;
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            // Cap buffer size to prevent runaway growth if a command logs in a tight loop.
            const int maxBufferSize = 500;
            var entry = new LogEntry
            {
                Id = System.Threading.Interlocked.Increment(ref _nextId),
                Timestamp = DateTime.Now,
                Type = type,
                Message = message,
                StackTrace = stackTrace
            };
            lock (_currentCaptureLock)
            {
                if (_currentCapture.Count >= maxBufferSize) _currentCapture.RemoveAt(0);
                _currentCapture.Add(entry);
            }
        }

        // GetLastLogId / GetLogsSinceFormatted / GetLogsSinceAllFormatted removed — replaced by
        // BeginCommandCapture / EndCommandCapture / GetCurrentCaptureFormatted (lazy in-memory).
        // LOG command queries Unity's Console directly via LogEntries reflection (see GetLogs).

        public sealed class UiToolkitDiagnostic
        {
            public string Path;
            public int Line;
            public string Severity;
            public string Message;

            public bool IsError => string.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase);

            public override string ToString()
            {
                string line = Line > 0 ? $":{Line}" : "";
                return $"{Path}{line}: {Severity}: {Message}";
            }
        }

        private static readonly Regex UiToolkitDiagnosticRegex = new Regex(
            @"(?<path>(?:Assets|Packages)/[^\r\n:]*?\.(?:uss|uxml|tss))\s*(?:\((?:line|Line)\s*(?<line>\d+)\))?\s*:\s*(?<severity>error|warning)\s*:\s*(?<message>[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UiToolkitPseudoClassRegex = new Regex(
            @"(?<path>(?:Assets|Packages)/[^\s\r\n]+?\.(?:uss|uxml|tss)).*?(?<message>Unknown pseudo class[^\r\n]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads current UI Toolkit USS/UXML/TSS diagnostics from Unity's Console.
        /// These asset importer errors are not C# compile errors, so they need their
        /// own classification instead of relying on CompilationPipeline callbacks.
        /// </summary>
        public static List<UiToolkitDiagnostic> GetUiToolkitDiagnosticsFromConsole(int maxCount = 50)
        {
            var diagnostics = new List<UiToolkitDiagnostic>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                var logEntries = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                var logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntries == null || logEntryType == null) return diagnostics;

                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
                var startGetting = logEntries.GetMethod("StartGettingEntries", flags);
                var endGetting = logEntries.GetMethod("EndGettingEntries", flags);
                var getEntry = logEntries.GetMethod("GetEntryInternal", flags);
                var getCount = logEntries.GetMethod("GetCount", flags);
                var messageField = logEntryType.GetField("message");
                var modeField = logEntryType.GetField("mode");

                if (startGetting == null || getCount == null || getEntry == null || messageField == null)
                    return diagnostics;

                startGetting.Invoke(null, null);
                try
                {
                    int count = (int)getCount.Invoke(null, null);
                    for (int i = count - 1; i >= 0 && diagnostics.Count < maxCount; i--)
                    {
                        var entry = Activator.CreateInstance(logEntryType);
                        if (!(bool)getEntry.Invoke(null, new object[] { i, entry })) continue;

                        string message = messageField.GetValue(entry)?.ToString();
                        if (string.IsNullOrWhiteSpace(message)) continue;

                        int mode = 0;
                        if (modeField != null)
                        {
                            try { mode = Convert.ToInt32(modeField.GetValue(entry)); }
                            catch { mode = 0; }
                        }

                        foreach (var diagnostic in ParseUiToolkitDiagnostics(message, mode))
                        {
                            string key = diagnostic.ToString();
                            if (seen.Add(key))
                                diagnostics.Add(diagnostic);
                            if (diagnostics.Count >= maxCount) break;
                        }
                    }
                }
                finally
                {
                    endGetting?.Invoke(null, null);
                }
            }
            catch { }

            return diagnostics;
        }

        private static IEnumerable<UiToolkitDiagnostic> ParseUiToolkitDiagnostics(string message, int mode)
        {
            bool matchedExplicitDiagnostic = false;
            foreach (Match match in UiToolkitDiagnosticRegex.Matches(message))
            {
                matchedExplicitDiagnostic = true;
                string severity = match.Groups["severity"].Value.ToLowerInvariant();
                yield return new UiToolkitDiagnostic
                {
                    Path = match.Groups["path"].Value,
                    Line = int.TryParse(match.Groups["line"].Value, out var line) ? line : 0,
                    Severity = severity,
                    Message = match.Groups["message"].Value.Trim()
                };
            }

            if (matchedExplicitDiagnostic) yield break;

            // Some UI Toolkit importer messages, especially unsupported pseudo-classes,
            // include the asset path and message but no explicit "warning:" token.
            foreach (Match match in UiToolkitPseudoClassRegex.Matches(message))
            {
                yield return new UiToolkitDiagnostic
                {
                    Path = match.Groups["path"].Value,
                    Line = 0,
                    Severity = IsErrorMode(mode) ? "error" : "warning",
                    Message = match.Groups["message"].Value.Trim()
                };
            }
        }

        private static bool IsErrorMode(int mode)
        {
            // Unity's LogEntry.mode is internal and may change between versions.
            // Bit 0x400 is Error in current editors; keep this best-effort only.
            return (mode & 0x400) != 0;
        }

        private static int CountUiToolkitErrors(IReadOnlyList<UiToolkitDiagnostic> diagnostics)
        {
            int count = 0;
            foreach (var diagnostic in diagnostics)
                if (diagnostic.IsError) count++;
            return count;
        }

        private static string FormatUiToolkitDiagnostics(List<UiToolkitDiagnostic> diagnostics, bool errorsOnly)
        {
            var filtered = errorsOnly
                ? diagnostics.Where(d => d.IsError).ToList()
                : diagnostics;

            var sb = new StringBuilder();
            sb.AppendLine($"uiToolkitDiagnostics: {filtered.Count}");
            sb.AppendLine($"uiToolkitErrors: {CountUiToolkitErrors(filtered)}");
            if (filtered.Count > 0)
            {
                sb.AppendLine("---");
                foreach (var diagnostic in filtered)
                    sb.AppendLine(StackTraceMinimizer.ShortenPaths(diagnostic.ToString()));
            }
            return sb.ToString().TrimEnd();
        }

        public static string GetUiToolkitDiagnosticsForCommand(string commandName, string data, string response)
        {
            if (string.IsNullOrEmpty(commandName)) return null;
            if (commandName.Equals("LOG", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("PING", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("HELP", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("PROBE", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("DIAG", StringComparison.OrdinalIgnoreCase))
                return null;

            GetConsoleCounts(out var consoleErrors, out var consoleWarnings);
            if (consoleErrors == 0 && consoleWarnings == 0) return null;

            string command = commandName.ToUpperInvariant();
            bool broad = command == "REFRESH"
                || (command == "ASSET_RESERIALIZE" && string.IsNullOrWhiteSpace(data));

            var mentionedPaths = ExtractUiToolkitPaths((data ?? "") + "\n" + (response ?? ""));
            bool mentionsUxml = mentionedPaths.Any(p => p.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase));
            if (!broad && mentionedPaths.Count == 0) return null;

            var diagnostics = GetUiToolkitDiagnosticsFromConsole(50)
                .Where(d => d.IsError)
                .ToList();

            if (!broad && !mentionsUxml)
            {
                diagnostics = diagnostics
                    .Where(d => mentionedPaths.Contains(NormalizeAssetPath(d.Path)))
                    .ToList();
            }

            if (diagnostics.Count == 0) return null;

            if (diagnostics.Count > 10)
                diagnostics = diagnostics.Take(10).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"--- UI Toolkit Errors ({diagnostics.Count}) ---");
            foreach (var diagnostic in diagnostics)
                sb.AppendLine(diagnostic.ToString());
            return sb.ToString().TrimEnd();
        }

        private static HashSet<string> ExtractUiToolkitPaths(string text)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return paths;

            foreach (Match match in Regex.Matches(text,
                @"(?:Assets|Packages)/[^\s""'<>|]+?\.(?:uss|uxml|tss)",
                RegexOptions.IgnoreCase))
            {
                paths.Add(NormalizeAssetPath(match.Value));
            }

            return paths;
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? "")
                .Replace('\\', '/')
                .Trim()
                .TrimEnd('.', ',', ';', ':', ')', ']', '}');
        }

        private const int DefaultLogCount = 20;

        private static readonly string[] LogFlags = { "errors", "warnings", "verbose", "raw", "all", "clear", "ui", "assets" };
        private static readonly string[] LogOptions = { "last", "since", "filter", "grep" };

        [BridgeCommand("LOG", "Get Unity console logs",
            Category = "Core",
            Usage = "LOG [type] [format] [count] [filter]\n" +
                    "  LOG                    - Last 20 entries (compact)\n" +
                    "  LOG errors             - Errors/exceptions (last 20)\n" +
                    "  LOG errors verbose     - Errors with full stack traces\n" +
                    "  LOG ui                 - Current USS/UXML/TSS Console diagnostics\n" +
                    "  LOG ui errors          - Current USS/UXML/TSS Console errors only\n" +
                    "  LOG raw                - Full traces, no path shortening\n" +
                    "  LOG last:N             - Exact N entries\n" +
                    "  LOG since:ID           - Since ID\n" +
                    "  LOG --filter text      - Substring search in messages + stacks\n" +
                    "  LOG all                - All entries (no cap)\n" +
                    "  LOG clear              - Clear buffer\n" +
                    "  Combinable in any order: errors|warnings + verbose|raw + last:N|since:ID|all + --filter text",
            RelatedCommands = new[] { "STACK_MINIMIZE", "STATUS" })]
        public static string GetLogs(string data)
        {
            var args = CommandArgs.Parse(data, LogFlags, LogOptions);

            if (args.Has("clear"))
            {
                lock (_currentCaptureLock) _currentCapture.Clear();
                try
                {
                    var logEntries = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                    var clearMethod = logEntries?.GetMethod("Clear",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    clearMethod?.Invoke(null, null);
                }
                catch { }
                return Response.Success("Log buffer cleared");
            }

            if (args.Has("ui") || args.Has("assets"))
            {
                int maxCount = args.Has("all") ? 500 : DefaultLogCount;
                var diagnostics = GetUiToolkitDiagnosticsFromConsole(maxCount);
                return args.WarningPrefix() + FormatUiToolkitDiagnostics(diagnostics, args.Has("errors"));
            }

            // Read entries directly from Unity's Console via reflection (no in-process file).
            var entries = ReadConsoleEntries();

            // Type filter
            if (args.Has("errors"))
                entries = entries.Where(e => e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert).ToList();
            else if (args.Has("warnings"))
                entries = entries.Where(e => e.Type != LogType.Log).ToList();

            // Text filter
            string filterText = args.Get("filter") ?? args.Get("grep");
            if (!string.IsNullOrEmpty(filterText))
            {
                entries = entries.Where(e =>
                    (e.Message != null && e.Message.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (e.StackTrace != null && e.StackTrace.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                ).ToList();
            }

            // Count: last:N or all bypass default cap
            if (args.Options.ContainsKey("last"))
            {
                int lastN = args.GetInt("last", -1);
                if (lastN > 0)
                    entries = entries.Skip(Math.Max(0, entries.Count - lastN)).ToList();
                else
                    return Response.Error("Invalid count in last:N");
            }
            else if (!args.Has("all"))
            {
                if (entries.Count > DefaultLogCount)
                    entries = entries.Skip(entries.Count - DefaultLogCount).ToList();
            }

            string prefix = args.WarningPrefix();

            if (args.Has("raw"))
            {
                CommandRegistry.SkipPathShortening = true;
                return prefix + FormatLogResponseVerbose(entries);
            }

            if (args.Has("verbose"))
                return prefix + FormatLogResponseVerbose(entries);

            return prefix + FormatLogResponse(entries);
        }

        /// <summary>Read entries from Unity's Console via LogEntries reflection. Replaces the
        /// old streamed-to-file capture — Console is the source of truth and survives reloads.</summary>
        private static List<LogEntry> ReadConsoleEntries()
        {
            var entries = new List<LogEntry>();
            try
            {
                var asm = typeof(UnityEditor.Editor).Assembly;
                var logEntriesType = asm.GetType("UnityEditor.LogEntries");
                var logEntryType = asm.GetType("UnityEditor.LogEntry");
                if (logEntriesType == null || logEntryType == null) return entries;

                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
                var startGetting = logEntriesType.GetMethod("StartGettingEntries", flags);
                var endGetting = logEntriesType.GetMethod("EndGettingEntries", flags);
                var getEntry = logEntriesType.GetMethod("GetEntryInternal", flags);
                var getCount = logEntriesType.GetMethod("GetCount", flags);
                var messageField = logEntryType.GetField("message");
                var modeField = logEntryType.GetField("mode");
                if (startGetting == null || getCount == null || getEntry == null || messageField == null) return entries;

                startGetting.Invoke(null, null);
                try
                {
                    int count = (int)getCount.Invoke(null, null);
                    for (int i = 0; i < count; i++)
                    {
                        var entryObj = Activator.CreateInstance(logEntryType);
                        if (!(bool)getEntry.Invoke(null, new object[] { i, entryObj })) continue;
                        string msg = messageField.GetValue(entryObj)?.ToString() ?? "";
                        int mode = 0;
                        if (modeField != null)
                            try { mode = Convert.ToInt32(modeField.GetValue(entryObj)); } catch { }
                        var split = msg.Split(new[] { '\n' }, 2);
                        entries.Add(new LogEntry
                        {
                            Id = i + 1,
                            Timestamp = DateTime.Now,
                            Type = ModeToLogType(mode),
                            Message = split[0],
                            StackTrace = split.Length > 1 ? split[1] : ""
                        });
                    }
                }
                finally { endGetting?.Invoke(null, null); }
            }
            catch { }
            return entries;
        }

        static LogType ModeToLogType(int mode)
        {
            // Unity LogEntry.mode bits (best-effort, internal API):
            // 0x100 = Error, 0x200 = Assert, 0x400 = Warning, otherwise Log
            if ((mode & 0x100) != 0 || (mode & 0x10000) != 0) return LogType.Error;
            if ((mode & 0x200) != 0) return LogType.Assert;
            if ((mode & 0x400) != 0) return LogType.Warning;
            return LogType.Log;
        }

        private static string FormatLogResponse(List<LogEntry> entries)
        {
            var sb = new StringBuilder();

            // Path legend so $WORKSPACE references are resolvable
            string legend = StackTraceMinimizer.GetPathLegend();
            if (legend != null)
                sb.AppendLine(legend);

            sb.AppendLine($"logCount: {entries.Count}");

            if (entries.Count > 0)
            {
                sb.AppendLine($"lastId: {entries[entries.Count - 1].Id}");

                int errors = 0, warnings = 0, logs = 0;
                foreach (var e in entries)
                {
                    if (e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert)
                        errors++;
                    else if (e.Type == LogType.Warning)
                        warnings++;
                    else
                        logs++;
                }
                sb.AppendLine($"errors: {errors}");
                sb.AppendLine($"warnings: {warnings}");
                sb.AppendLine($"info: {logs}");
                sb.AppendLine("---");

                FormatEntriesCompact(entries, sb, includeTimestamp: true);
            }
            else
            {
                sb.AppendLine("lastId: 0");
                sb.AppendLine("errors: 0");
                sb.AppendLine("warnings: 0");
                sb.AppendLine("info: 0");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Full verbose format (LOG verbose). Shows IDs, timestamps, full stack traces.
        /// </summary>
        private static string FormatLogResponseVerbose(List<LogEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"logCount: {entries.Count}");

            if (entries.Count > 0)
            {
                sb.AppendLine($"lastId: {entries[entries.Count - 1].Id}");
                sb.AppendLine("---");

                foreach (var entry in entries)
                {
                    string typeTag = LogTypeToTag(entry.Type);
                    sb.AppendLine($"[{entry.Id}] [{entry.Timestamp:HH:mm:ss.fff}] [{typeTag}] {entry.Message}");

                    if ((entry.Type == LogType.Error || entry.Type == LogType.Exception || entry.Type == LogType.Assert)
                        && !string.IsNullOrWhiteSpace(entry.StackTrace))
                    {
                        foreach (var line in entry.StackTrace.Split('\n'))
                        {
                            var trimmed = line.TrimEnd();
                            if (!string.IsNullOrEmpty(trimmed))
                                sb.AppendLine($"    {trimmed}");
                        }
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Compact entry formatting shared by LOG command and appended logs.
        /// Collapses consecutive duplicates, minimizes stack traces.
        /// </summary>
        private static void FormatEntriesCompact(List<LogEntry> entries, StringBuilder sb, bool includeTimestamp)
        {
            // Group consecutive duplicate messages
            var collapsed = new List<(LogEntry entry, int count)>();
            foreach (var entry in entries)
            {
                if (collapsed.Count > 0 && entry.Message == collapsed[collapsed.Count - 1].entry.Message
                    && entry.Type == collapsed[collapsed.Count - 1].entry.Type)
                {
                    var last = collapsed[collapsed.Count - 1];
                    collapsed[collapsed.Count - 1] = (last.entry, last.count + 1);
                }
                else
                {
                    collapsed.Add((entry, 1));
                }
            }

            foreach (var (entry, count) in collapsed)
            {
                string tag = ShortTag(entry.Type);
                string countSuffix = count > 1 ? $" (x{count})" : "";
                string timestamp = includeTimestamp ? $" [{entry.Timestamp:HH:mm:ss}]" : "";

                string msg = StackTraceMinimizer.ShortenPaths(entry.Message);
                sb.Append($"[{tag}]{timestamp} {msg}{countSuffix}");

                // For errors, append minimized stack trace
                if ((entry.Type == LogType.Error || entry.Type == LogType.Exception || entry.Type == LogType.Assert)
                    && !string.IsNullOrWhiteSpace(entry.StackTrace))
                {
                    string minimized = StackTraceMinimizer.Minimize(entry.StackTrace);
                    if (!string.IsNullOrEmpty(minimized))
                        sb.Append($"\n{minimized}");
                }

                sb.AppendLine();
            }
        }

        private static string ShortTag(LogType type)
        {
            return type switch
            {
                LogType.Error => "E",
                LogType.Exception => "X",
                LogType.Assert => "A",
                LogType.Warning => "W",
                _ => "I"
            };
        }

        private static string LogTypeToTag(LogType type)
        {
            return type switch
            {
                LogType.Error => "ERROR",
                LogType.Exception => "EXCEPTION",
                LogType.Assert => "ASSERT",
                LogType.Warning => "WARNING",
                _ => "INFO"
            };
        }

        private static LogType TagToLogType(string tag)
        {
            return tag switch
            {
                "ERROR" => LogType.Error,
                "EXCEPTION" => LogType.Exception,
                "ASSERT" => LogType.Assert,
                "WARNING" => LogType.Warning,
                _ => LogType.Log
            };
        }

        private class LogEntry
        {
            public long Id;
            public DateTime Timestamp;
            public LogType Type;
            public string Message;
            public string StackTrace;
        }
    }
}
