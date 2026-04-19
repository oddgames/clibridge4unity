using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace clibridge4unity;

/// <summary>
/// Lightweight per-project "who's active" ledger so multiple CLI agents sharing one
/// Unity instance can see each other. Presence only — no locking, no coordination,
/// no RPC. Each agent drops a file on startup, removes it on exit; others read the
/// directory to learn what's in flight.
///
/// Storage: {projectPath}/.clibridge4unity/sessions/{sessionId}.txt
/// Format:  key=value lines (plain text; no JSON lib dependency).
/// </summary>
internal static class SessionLedger
{
    public class Session
    {
        public string SessionId;
        public int Pid;
        public DateTime StartedAtUtc;
        public string Command;
        public string DataSummary;
        public string Intent;
        public string Cwd;
        public string SourceFile; // file on disk that represents this session

        public TimeSpan Age => DateTime.UtcNow - StartedAtUtc;
    }

    static string SessionDir(string projectPath)
        => Path.Combine(projectPath, ".clibridge4unity", "sessions");

    /// <summary>Register the current process as an active session. Returns the path of the
    /// session file so the caller can delete it in a finally block.</summary>
    public static string Register(string projectPath, string command, string data, string intent)
    {
        try
        {
            string dir = SessionDir(projectPath);
            Directory.CreateDirectory(dir);

            int pid = Process.GetCurrentProcess().Id;
            string sessionId = $"cli-{pid:X}";
            string file = Path.Combine(dir, sessionId + ".txt");

            var sb = new StringBuilder();
            sb.AppendLine($"sessionId={sessionId}");
            sb.AppendLine($"pid={pid}");
            sb.AppendLine($"startedAt={DateTime.UtcNow:O}");
            sb.AppendLine($"command={command ?? ""}");
            sb.AppendLine($"dataSummary={Summarize(data)}");
            if (!string.IsNullOrEmpty(intent))
                sb.AppendLine($"intent={Escape(intent)}");
            sb.AppendLine($"cwd={Escape(Environment.CurrentDirectory)}");

            File.WriteAllText(file, sb.ToString());
            return file;
        }
        catch { return null; }
    }

    /// <summary>List active sessions for this project, excluding one session id (usually self).
    /// Sweeps stale files (pid dead, or on-disk parse failure) as a side effect.</summary>
    public static List<Session> List(string projectPath, string excludeSessionId = null)
    {
        var results = new List<Session>();
        string dir = SessionDir(projectPath);
        if (!Directory.Exists(dir)) return results;

        foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
        {
            try
            {
                var s = Parse(file);
                if (s == null) { TryDelete(file); continue; }
                if (excludeSessionId != null && s.SessionId == excludeSessionId) continue;

                // Stale cleanup: pid gone → the process exited without deleting its file.
                try { Process.GetProcessById(s.Pid); }
                catch { TryDelete(file); continue; }

                results.Add(s);
            }
            catch { TryDelete(file); }
        }

        return results.OrderBy(s => s.StartedAtUtc).ToList();
    }

    static Session Parse(string file)
    {
        var s = new Session { SourceFile = file };
        foreach (var raw in File.ReadAllLines(file))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            string key = raw.Substring(0, eq);
            string val = Unescape(raw.Substring(eq + 1));
            switch (key)
            {
                case "sessionId": s.SessionId = val; break;
                case "pid": if (int.TryParse(val, out int p)) s.Pid = p; break;
                case "startedAt": if (DateTime.TryParse(val, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)) s.StartedAtUtc = t.ToUniversalTime(); break;
                case "command": s.Command = val; break;
                case "dataSummary": s.DataSummary = val; break;
                case "intent": s.Intent = val; break;
                case "cwd": s.Cwd = val; break;
            }
        }
        return string.IsNullOrEmpty(s.SessionId) || s.Pid == 0 ? null : s;
    }

    static string Summarize(string data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        const int cap = 80;
        string trimmed = data.Length > cap ? data.Substring(0, cap) + "..." : data;
        return Escape(trimmed);
    }

    // Escape newlines + embedded CR so a single-line key=value record survives round-trip.
    static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
    static string Unescape(string s) => (s ?? "").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");

    static void TryDelete(string file) { try { File.Delete(file); } catch { } }
}
