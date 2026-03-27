using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private static readonly ConcurrentQueue<LogEntry> _pendingWrites = new ConcurrentQueue<LogEntry>();
        private static string _logFilePath;
        private static long _nextId;
        private static volatile bool _isRunning = true;

        // Compile errors persist across log clears — tracked separately
        private static readonly List<CompilerMessage> _compileErrors = new List<CompilerMessage>();
        private static readonly object _compileErrorLock = new object();

        static LogCommands()
        {
            // Restore log ID counter across domain reloads
            _nextId = UnityEditor.SessionState.GetInt(SessionKeys.LogNextId, 1);

            // Restore compile errors from SessionState (survive domain reload)
            string savedErrors = SessionState.GetString("Bridge_CompileErrors", "");
            if (!string.IsNullOrEmpty(savedErrors))
            {
                lock (_compileErrorLock)
                {
                    foreach (var line in savedErrors.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            _compileErrors.Add(new CompilerMessage { message = line, type = CompilerMessageType.Error });
                    }
                }
            }

            // Log file in temp directory - survives domain reloads, cleared on Unity restart
            // Use the same deterministic hash as the pipe name so the CLI can find this file
            string normalizedPath = Application.dataPath.Replace("/Assets", "").ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            string projectHash = BridgeServer.GetDeterministicHashCode(normalizedPath).ToString("X8");
            _logFilePath = Path.Combine(Path.GetTempPath(), $"clibridge4unity_logs_{projectHash}.log");

            // Register hooks with CommandRegistry (avoids circular asmdef dependency)
            CommandRegistry.GetLastLogId = GetLastLogId;
            CommandRegistry.GetLogsSinceFormatted = GetLogsSinceFormatted;
            CommandRegistry.GetCompileErrors = GetCompileErrorsSummary;
            CommandRegistry.ShortenResponsePaths = StackTraceMinimizer.ShortenPaths;

            // Track compile errors via CompilationPipeline (persists until fixed)
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationStarted += _ =>
            {
                lock (_compileErrorLock) _compileErrors.Clear();
                SessionState.SetString("Bridge_CompileErrors", "");
            };

            Application.logMessageReceived += OnLogMessage;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                _isRunning = false;
                Application.logMessageReceived -= OnLogMessage;
                CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
                UnityEditor.SessionState.SetInt(SessionKeys.LogNextId, (int)_nextId);

                // Persist compile errors to SessionState so they survive domain reload
                lock (_compileErrorLock)
                {
                    if (_compileErrors.Count > 0)
                    {
                        var errors = string.Join("\n", _compileErrors.Select(e => e.message).Take(20));
                        SessionState.SetString("Bridge_CompileErrors", errors);
                    }
                }

                FlushPendingWrites();
            };

            // Flush pending writes periodically via editor update
            UnityEditor.EditorApplication.update += FlushPendingWrites;

            // Trim log file if it's grown too large (>1MB)
            try
            {
                if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 1_000_000)
                    TrimLogFile(500);
            }
            catch { }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            lock (_compileErrorLock)
            {
                foreach (var msg in messages)
                {
                    if (msg.type == CompilerMessageType.Error)
                        _compileErrors.Add(msg);
                }
            }
        }

        /// <summary>
        /// Returns a formatted compile error summary, or null if no errors.
        /// </summary>
        public static string GetCompileErrorsSummary()
        {
            lock (_compileErrorLock)
            {
                if (_compileErrors.Count == 0) return null;

                var sb = new StringBuilder();
                sb.AppendLine($"COMPILE ERRORS ({_compileErrors.Count}) — fix these before running commands:");
                foreach (var err in _compileErrors.Take(10))
                    sb.AppendLine($"  {err.message}");
                if (_compileErrors.Count > 10)
                    sb.AppendLine($"  ... and {_compileErrors.Count - 10} more");
                return sb.ToString().TrimEnd();
            }
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            var entry = new LogEntry
            {
                Id = System.Threading.Interlocked.Increment(ref _nextId),
                Timestamp = DateTime.Now,
                Type = type,
                Message = message,
                StackTrace = stackTrace
            };

            _pendingWrites.Enqueue(entry);
        }

        private static void FlushPendingWrites()
        {
            if (_pendingWrites.IsEmpty) return;

            var sb = new StringBuilder();
            while (_pendingWrites.TryDequeue(out var entry))
            {
                string typeTag = LogTypeToTag(entry.Type);
                // Tab-separated format: ID\tTimestamp\tType\tMessage\tStackTrace
                string msg = entry.Message?.Replace("\n", "\\n").Replace("\t", " ") ?? "";
                string stack = entry.StackTrace?.Replace("\n", "\\n").Replace("\t", " ") ?? "";
                sb.AppendLine($"{entry.Id}\t{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\t{typeTag}\t{msg}\t{stack}");
            }

            if (sb.Length > 0)
            {
                try { File.AppendAllText(_logFilePath, sb.ToString()); }
                catch { }
            }
        }

        /// <summary>
        /// Returns the current last log ID (for capturing logs during a command).
        /// </summary>
        public static long GetLastLogId()
        {
            FlushPendingWrites();
            var entries = ReadLogFile();
            return entries.Count > 0 ? entries[entries.Count - 1].Id : 0;
        }

        /// <summary>
        /// Returns formatted log entries since the given ID (for appending to command responses).
        /// Only includes errors/exceptions — warnings and info are noise for LLM consumers.
        /// </summary>
        public static string GetLogsSinceFormatted(long sinceId, int maxLines = 5)
        {
            FlushPendingWrites();
            var entries = ReadLogFile()
                .Where(e => e.Id > sinceId &&
                       (e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert))
                .ToList();
            if (entries.Count == 0) return null;

            // Limit to last N to avoid bloating responses
            if (entries.Count > maxLines)
                entries = entries.Skip(entries.Count - maxLines).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"--- Errors ({entries.Count}) ---");
            FormatEntriesCompact(entries, sb, includeTimestamp: false);
            return sb.ToString().TrimEnd();
        }

        private const int DefaultLogCount = 20;

        private static readonly string[] LogFlags = { "errors", "warnings", "verbose", "raw", "all", "clear" };
        private static readonly string[] LogOptions = { "last", "since" };

        [BridgeCommand("LOG", "Get Unity console logs",
            Category = "Core",
            Usage = "LOG [type] [format] [count]\n" +
                    "  LOG                    - Last 20 entries (compact)\n" +
                    "  LOG errors             - Errors/exceptions (last 20)\n" +
                    "  LOG errors verbose     - Errors with full stack traces\n" +
                    "  LOG raw                - Full traces, no path shortening\n" +
                    "  LOG last:N             - Exact N entries\n" +
                    "  LOG since:ID           - Since ID\n" +
                    "  LOG all                - All entries (no cap)\n" +
                    "  LOG clear              - Clear buffer\n" +
                    "  Combinable in any order: errors|warnings + verbose|raw + last:N|since:ID|all")]
        public static string GetLogs(string data)
        {
            FlushPendingWrites();

            var args = CommandArgs.Parse(data, LogFlags, LogOptions);

            if (args.Has("clear"))
            {
                try { File.Delete(_logFilePath); }
                catch { }
                return Response.Success("Log buffer cleared");
            }

            var entries = ReadLogFile();

            // Type filter
            if (args.Has("errors"))
                entries = entries.Where(e => e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert).ToList();
            else if (args.Has("warnings"))
                entries = entries.Where(e => e.Type != LogType.Log).ToList();

            // Range filter
            if (args.Options.ContainsKey("since"))
            {
                long sinceId = args.GetLong("since", -1);
                if (sinceId >= 0)
                    entries = entries.Where(e => e.Id > sinceId).ToList();
                else
                    return Response.Error("Invalid ID in since:N");
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

            // Format
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

        private static List<LogEntry> ReadLogFile()
        {
            var entries = new List<LogEntry>();
            if (!File.Exists(_logFilePath)) return entries;

            try
            {
                foreach (var line in File.ReadAllLines(_logFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 4) continue;

                    var entry = new LogEntry();
                    if (!long.TryParse(parts[0], out entry.Id)) continue;
                    if (!DateTime.TryParse(parts[1], out entry.Timestamp)) continue;
                    entry.Type = TagToLogType(parts[2]);
                    entry.Message = parts[3].Replace("\\n", "\n");
                    entry.StackTrace = parts.Length > 4 ? parts[4].Replace("\\n", "\n") : "";
                    entries.Add(entry);
                }
            }
            catch { }

            return entries;
        }

        private static void TrimLogFile(int keepLines)
        {
            try
            {
                var lines = File.ReadAllLines(_logFilePath);
                if (lines.Length > keepLines)
                {
                    var trimmed = lines.Skip(lines.Length - keepLines).ToArray();
                    File.WriteAllLines(_logFilePath, trimmed);
                }
            }
            catch { }
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
