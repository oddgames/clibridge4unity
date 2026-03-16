using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Core commands: PING, STATUS, COMPILE, REFRESH, EXEC, TEST
    /// </summary>
    public static class CoreCommands
    {
        /// <summary>
        /// Check if any .cs files under Assets/ or Packages/ have been modified since last compile.
        /// Uses Directory.EnumerateFiles for lazy evaluation — stops early if possible.
        /// </summary>
        private static bool ScriptsModifiedSinceCompile()
        {
            if (!long.TryParse(SessionState.GetString(SessionKeys.LastCompileTime, "0"), out var ticks) || ticks <= 0)
                return true; // No compile recorded, assume modified

            var lastCompile = new System.DateTime(ticks);
            var assetsPath = Application.dataPath; // .../Assets

            try
            {
                return Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories)
                    .Any(f => File.GetLastWriteTime(f) > lastCompile);
            }
            catch { return true; } // If scan fails, assume modified
        }

        [BridgeCommand("PING", "Test connection",
            Category = "Core",
            Usage = "PING")]
        public static string Ping()
        {
            return Response.Success("Pong");
        }

        [BridgeCommand("HELP", "List all available commands",
            Category = "Core",
            Usage = "HELP [verbose|COMMAND]")]
        public static string Help(string data)
        {
            return CommandRegistry.GetHelp(data);
        }

        [BridgeCommand("PROBE", "Quick main thread health check (2s timeout)",
            Category = "Core",
            Usage = "PROBE",
            RequiresMainThread = true)]
        public static string Probe()
        {
            return Response.Success("OK");
        }

        [BridgeCommand("DIAG", "Diagnostic info including heartbeat (no main thread needed)",
            Category = "Core",
            Usage = "DIAG")]
        public static string Diag()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"bridgeVersion: {BridgeServer.Version}");
            sb.AppendLine("--- heartbeat ---");
            sb.AppendLine(CommandRegistry.GetHeartbeatInfo());
            sb.AppendLine("--- thread ---");
            sb.AppendLine($"thread: {System.Threading.Thread.CurrentThread.ManagedThreadId} ({System.Threading.Thread.CurrentThread.Name})");
            sb.AppendLine($"syncCtx: {System.Threading.SynchronizationContext.Current?.GetType().Name ?? "null"}");
            sb.AppendLine(CommandRegistry.GetQueueDiagnostics());
            sb.AppendLine($"processMainWndHandle: {System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle}");
            var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            sb.AppendLine($"pid: {pid}");
            sb.AppendLine($"processName: {System.Diagnostics.Process.GetCurrentProcess().ProcessName}");
            sb.AppendLine($"dataPath: {UnityEngine.Application.dataPath}");
            string projName = System.IO.Path.GetFileName(UnityEngine.Application.dataPath.Replace("/Assets", ""));
            sb.AppendLine($"projectName: {projName}");
            sb.AppendLine("--- windows for this PID ---");
            CommandRegistry.EnumProcessWindows(pid, sb);
            sb.AppendLine("--- all Unity-titled windows ---");
            CommandRegistry.EnumWindowsByTitle("Unity", sb);
            return sb.ToString().TrimEnd();
        }

        [BridgeCommand("STATUS", "Get Unity Editor status",
            Category = "Core",
            Usage = "STATUS",
            RequiresMainThread = true)]
        public static string GetStatus()
        {
            // Get all open editor windows
            var editorWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var windowList = new List<string>();
            foreach (var win in editorWindows)
            {
                string title = win.titleContent?.text ?? win.GetType().Name;
                string typeName = win.GetType().Name;
                if (title != typeName)
                    windowList.Add($"{title} ({typeName})");
                else
                    windowList.Add(typeName);
            }

            // Get last compile time from SessionState (survives domain reloads)
            string lastCompileStr = "never";
            string lastCompileRequestStr = "never";
            if (long.TryParse(SessionState.GetString(SessionKeys.LastCompileTime, "0"), out var compileTicks) && compileTicks > 0)
                lastCompileStr = new System.DateTime(compileTicks).ToString("yyyy-MM-dd HH:mm:ss");
            if (long.TryParse(SessionState.GetString(SessionKeys.LastCompileRequest, "0"), out var requestTicks) && requestTicks > 0)
                lastCompileRequestStr = new System.DateTime(requestTicks).ToString("yyyy-MM-dd HH:mm:ss");

            return Response.SuccessWithData(new
            {
                bridgeVersion = BridgeServer.Version,
                isCompiling = EditorApplication.isCompiling,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                scriptsModified = ScriptsModifiedSinceCompile(),
                lastCompileRequest = lastCompileRequestStr,
                lastCompileFinished = lastCompileStr,
                projectPath = Application.dataPath,
                unityVersion = Application.unityVersion,
                openWindows = windowList.ToArray()
            });
        }

        [BridgeCommand("COMPILE", "Force script recompilation",
            Category = "Core",
            Usage = "COMPILE",
            Streaming = false,
            RequiresMainThread = true,
            TimeoutSeconds = 90)]
        public static string Compile(string data)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("Cannot compile during play mode. Use STOP first.");

            // Skip if no scripts have changed (unless forced with "force")
            bool force = !string.IsNullOrEmpty(data) && data.Trim().Equals("force", System.StringComparison.OrdinalIgnoreCase);
            if (!force && !ScriptsModifiedSinceCompile())
            {
                string lastCompileStr = "unknown";
                if (long.TryParse(SessionState.GetString(SessionKeys.LastCompileTime, "0"), out var ticks) && ticks > 0)
                    lastCompileStr = new System.DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss");
                return Response.SuccessWithData(new
                {
                    message = "No scripts modified since last compile. Use COMPILE force to override.",
                    skipped = true,
                    lastCompileFinished = lastCompileStr
                });
            }

            // Trigger compilation - this will cause Unity to recompile and reload assemblies
            // The connection will be lost during assembly reload, but that's expected
            CompilationPipeline.RequestScriptCompilation();

            // Force Unity to process the compilation request even when in background
            EditorApplication.QueuePlayerLoopUpdate();

            return Response.SuccessWithData(new
            {
                message = "Compilation requested. Unity will reload assemblies - connection will be lost during reload.",
                timeoutSeconds = 90,
                reconnect = true
            });
        }

        [BridgeCommand("REFRESH", "Force asset database refresh",
            Category = "Core",
            Usage = "REFRESH",
            Streaming = false,
            RequiresMainThread = true,
            TimeoutSeconds = 90)]
        public static string Refresh()
        {
            // Write lock file before refresh in case it triggers compilation
            // Lock file removed - CLI uses pipe reconnection to detect compilation("refresh_requested");

            // Trigger asset database refresh - may trigger compilation if assets change
            // If compilation is triggered, Unity will reload assemblies and connection will be lost
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            // Force Unity to process the refresh even when in background
            EditorApplication.QueuePlayerLoopUpdate();

            return Response.SuccessWithData(new
            {
                message = "Asset refresh requested. If compilation is triggered, connection will be lost during assembly reload.",
                timeoutSeconds = 90,
                reconnect = true
            });
        }
    }
}
