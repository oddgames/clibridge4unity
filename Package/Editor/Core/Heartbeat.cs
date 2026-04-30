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
        private static string _currentState;
        private static long _stateEnteredAtUnix;

        static Heartbeat()
        {
            BridgeDiagnostics.Log("Heartbeat", "static ctor - subscribing to afterAssemblyReload");
            AssemblyReloadEvents.afterAssemblyReload += Initialize;
        }

        private static void Initialize()
        {
            BridgeDiagnostics.Log("Heartbeat", "Initialize enter");
            string projectRoot = Application.dataPath.Replace("/Assets", "");
            string normalizedPath = projectRoot.ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            string hash = BridgeServer.GetDeterministicHashCode(normalizedPath).ToString("X8");
            string projectName = SanitizeName(Path.GetFileName(projectRoot.TrimEnd('/', '\\')));
            _statusFile = Path.Combine(Path.GetTempPath(), $"clibridge4unity_{hash}_{projectName}.status");
            BridgeDiagnostics.Log("Heartbeat", $"status file: {_statusFile}");

            AssemblyReloadEvents.beforeAssemblyReload += WriteReloadingNow;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += _ => WriteNow();
            EditorApplication.quitting += Cleanup;

            WriteNow();
            BridgeDiagnostics.Log("Heartbeat", "Initialize exit");
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
                string state = GetState();

                // Track when this state was first entered so the CLI can compute elapsed-in-state
                // and decide whether to wait for Unity or bail-fast.
                if (state != _currentState)
                {
                    _currentState = state;
                    _stateEnteredAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    BridgeDiagnostics.Log("Heartbeat", $"state changed: {state}");
                }

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
                    $"  \"stateEnteredAt\": {_stateEnteredAtUnix},\n" +
                    $"  \"timestamp\": {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n" +
                    "}";

                File.WriteAllText(_statusFile, json);
            }
            catch (Exception ex) { BridgeDiagnostics.LogException("Heartbeat WriteNow", ex); }
        }

        static void WriteReloadingNow()
        {
            BridgeDiagnostics.Log("Heartbeat", "before assembly reload - writing reloading state");
            _lastWrite = EditorApplication.timeSinceStartup;
            try
            {
                _currentState = "reloading";
                _stateEnteredAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                string projectName = Path.GetFileName(
                    Application.dataPath.Replace("/Assets", ""));

                string json = "{\n" +
                    "  \"state\": \"reloading\",\n" +
                    $"  \"pid\": {System.Diagnostics.Process.GetCurrentProcess().Id},\n" +
                    $"  \"version\": \"{BridgeServer.Version}\",\n" +
                    $"  \"project\": \"{EscapeJson(projectName)}\",\n" +
                    "  \"compileErrors\": false,\n" +
                    "  \"compileErrorCount\": 0,\n" +
                    "  \"compileTimeAvg\": 0,\n" +
                    $"  \"stateEnteredAt\": {_stateEnteredAtUnix},\n" +
                    $"  \"timestamp\": {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n" +
                    "}";

                File.WriteAllText(_statusFile, json);
                BridgeDiagnostics.Log("Heartbeat", $"reloading state written: {_statusFile}");
            }
            catch (Exception ex) { BridgeDiagnostics.LogException("Heartbeat WriteReloadingNow", ex); }
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
            BridgeDiagnostics.Log("Heartbeat", "cleanup enter");
            EditorApplication.update -= Tick;
            try { if (File.Exists(_statusFile)) File.Delete(_statusFile); }
            catch (Exception ex) { BridgeDiagnostics.LogException("Heartbeat cleanup", ex); }
            BridgeDiagnostics.Log("Heartbeat", "cleanup exit");
        }

        internal static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 32 ? sb.ToString().Substring(0, 32) : sb.ToString();
        }

        static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
