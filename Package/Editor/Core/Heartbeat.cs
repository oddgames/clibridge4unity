using System;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Writes Unity state to a temp file every second so the CLI can check
    /// Unity's status without a pipe connection (useful during domain reload).
    /// File: %TEMP%/clibridge4unity_{ProjectHash}.status
    /// </summary>
    [InitializeOnLoad]
    public static class Heartbeat
    {
        private static string _statusFile;
        private static double _lastWrite;
        private static string _overrideState;

        static Heartbeat()
        {
            string normalizedPath = Application.dataPath.Replace("/Assets", "")
                .ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            string hash = BridgeServer.GetDeterministicHashCode(normalizedPath).ToString("X8");
            _statusFile = Path.Combine(Path.GetTempPath(), $"clibridge4unity_{hash}.status");

            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                _overrideState = "reloading";
                WriteNow();
            };
            EditorApplication.playModeStateChanged += _ => WriteNow();
            EditorApplication.quitting += Cleanup;

            // Write immediately on init
            WriteNow();
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastWrite < 1.0) return;
            WriteNow();
        }

        static void WriteNow()
        {
            _lastWrite = EditorApplication.timeSinceStartup;
            try
            {
                string state = _overrideState ?? GetState();
                _overrideState = null;

                bool hasErrors = false;
                int errorCount = 0;
                var getErrors = CommandRegistry.GetCompileErrors;
                if (getErrors != null)
                {
                    string errors = getErrors();
                    if (errors != null)
                    {
                        hasErrors = true;
                        var match = System.Text.RegularExpressions.Regex.Match(errors, @"\((\d+)\)");
                        if (match.Success) errorCount = int.Parse(match.Groups[1].Value);
                    }
                }

                var stats = BridgeServer.GetCompileTimeStats();
                int compileTimeAvg = stats.HasValue ? stats.Value.avg : 0;

                string projectName = Path.GetFileName(
                    Application.dataPath.Replace("/Assets", ""));

                // Manual JSON to avoid Newtonsoft dependency in Core asmdef
                string json = "{\n" +
                    $"  \"state\": \"{state}\",\n" +
                    $"  \"pid\": {System.Diagnostics.Process.GetCurrentProcess().Id},\n" +
                    $"  \"version\": \"{BridgeServer.Version}\",\n" +
                    $"  \"project\": \"{EscapeJson(projectName)}\",\n" +
                    $"  \"compileErrors\": {(hasErrors ? "true" : "false")},\n" +
                    $"  \"compileErrorCount\": {errorCount},\n" +
                    $"  \"compileTimeAvg\": {compileTimeAvg},\n" +
                    $"  \"timestamp\": {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n" +
                    "}";

                File.WriteAllText(_statusFile, json);
            }
            catch { }
        }

        static string GetState()
        {
            if (EditorApplication.isCompiling) return "compiling";
            if (EditorApplication.isUpdating) return "importing";
            if (EditorApplication.isPlaying && EditorApplication.isPaused) return "paused";
            if (EditorApplication.isPlaying) return "playing";
            return "ready";
        }

        static void Cleanup()
        {
            EditorApplication.update -= Tick;
            try { if (File.Exists(_statusFile)) File.Delete(_statusFile); }
            catch { }
        }

        static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
