using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Reload-safe diagnostics written outside the Unity console.
    /// </summary>
    public static class BridgeDiagnostics
    {
        private static readonly object LockObj = new object();
        private static string _logPath;
        private const long MaxLogBytes = 1_000_000;

        public static string LogPath => GetLogPath();

        public static void Log(string area, string message)
        {
            try
            {
                string path = GetLogPath();
                string line = $"{DateTimeOffset.UtcNow:O}\tpid={Process.GetCurrentProcess().Id}\tthread={Thread.CurrentThread.ManagedThreadId}\t{area}\t{message}";

                lock (LockObj)
                {
                    RotateIfNeeded(path);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void LogException(string area, Exception ex)
        {
            if (ex == null) return;
            Log(area, $"{ex.GetType().Name}: {ex.Message}");
        }

        private static string GetLogPath()
        {
            if (_logPath != null) return _logPath;

            string normalizedPath = "unknown";
            try
            {
                normalizedPath = Application.dataPath.Replace("/Assets", "")
                    .ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            }
            catch { }

            int hash = GetDeterministicHashCode(normalizedPath);
            _logPath = Path.Combine(Path.GetTempPath(), $"clibridge4unity_{hash:X8}.diag.log");
            return _logPath;
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= MaxLogBytes) return;

                string previous = path + ".prev";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            catch { }
        }

        private static int GetDeterministicHashCode(string str)
        {
            unchecked
            {
                int hash1 = 5381;
                int hash2 = hash1;

                for (int i = 0; i < str.Length && str[i] != '\0'; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0')
                        break;
                    hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                }

                return hash1 + (hash2 * 1566083941);
            }
        }
    }
}
