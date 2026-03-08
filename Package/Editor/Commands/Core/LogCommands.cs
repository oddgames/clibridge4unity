using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        static LogCommands()
        {
            // Restore log ID counter across domain reloads
            _nextId = UnityEditor.SessionState.GetInt(SessionKeys.LogNextId, 1);

            // Log file in temp directory - survives domain reloads, cleared on Unity restart
            string projectHash = Application.dataPath.GetHashCode().ToString("X8");
            _logFilePath = Path.Combine(Path.GetTempPath(), $"clibridge4unity_logs_{projectHash}.log");

            // Register log capture hooks with CommandRegistry (avoids circular asmdef dependency)
            CommandRegistry.GetLastLogId = GetLastLogId;
            CommandRegistry.GetLogsSinceFormatted = GetLogsSinceFormatted;

            Application.logMessageReceived += OnLogMessage;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                _isRunning = false;
                Application.logMessageReceived -= OnLogMessage;
                UnityEditor.SessionState.SetInt(SessionKeys.LogNextId, (int)_nextId);
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
        /// </summary>
        public static string GetLogsSinceFormatted(long sinceId, int maxLines = 20)
        {
            FlushPendingWrites();
            var entries = ReadLogFile().Where(e => e.Id > sinceId).ToList();
            if (entries.Count == 0) return null;

            // Limit to last N to avoid bloating responses
            if (entries.Count > maxLines)
                entries = entries.Skip(entries.Count - maxLines).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"--- Logs ({entries.Count} entries) ---");
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
            return sb.ToString().TrimEnd();
        }

        [BridgeCommand("LOG", "Get Unity console logs",
            Category = "Core",
            Usage = "LOG [filter]\n" +
                    "  LOG                - All logs since last query\n" +
                    "  LOG all            - All buffered logs\n" +
                    "  LOG errors         - Only errors and exceptions\n" +
                    "  LOG warnings       - Warnings, errors, and exceptions\n" +
                    "  LOG last:N         - Last N log entries\n" +
                    "  LOG since:ID       - Logs since a specific log ID\n" +
                    "  LOG clear          - Clear the log buffer")]
        public static string GetLogs(string data)
        {
            // Flush any pending writes first
            FlushPendingWrites();

            string filter = string.IsNullOrWhiteSpace(data) ? "" : data.Trim().ToLowerInvariant();

            if (filter == "clear")
            {
                try { File.Delete(_logFilePath); }
                catch { }
                return Response.Success("Log buffer cleared");
            }

            // Read all entries from file
            var entries = ReadLogFile();

            // Apply filters
            if (filter.StartsWith("since:"))
            {
                if (long.TryParse(filter.Substring(6), out long sinceId))
                    entries = entries.Where(e => e.Id > sinceId).ToList();
                else
                    return Response.Error("Invalid ID in since:N filter");
            }
            else if (filter.StartsWith("last:"))
            {
                if (int.TryParse(filter.Substring(5), out int lastN) && lastN > 0)
                    entries = entries.Skip(Math.Max(0, entries.Count - lastN)).ToList();
                else
                    return Response.Error("Invalid count in last:N filter");
            }
            else if (filter == "errors")
            {
                entries = entries.Where(e => e.Type == LogType.Error || e.Type == LogType.Exception || e.Type == LogType.Assert).ToList();
            }
            else if (filter == "warnings")
            {
                entries = entries.Where(e => e.Type != LogType.Log).ToList();
            }
            else if (filter == "all" || filter == "")
            {
                // No filtering
            }
            else
            {
                return Response.Error($"Unknown filter: {filter}. Use: all, errors, warnings, last:N, since:ID, clear");
            }

            return FormatLogResponse(entries);
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
            else
            {
                sb.AppendLine("lastId: 0");
                sb.AppendLine("errors: 0");
                sb.AppendLine("warnings: 0");
                sb.AppendLine("info: 0");
            }

            return sb.ToString().TrimEnd();
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
