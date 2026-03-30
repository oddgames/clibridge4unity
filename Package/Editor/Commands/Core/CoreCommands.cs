using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

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

            // Check for compile errors
            bool hasCompileErrors = false;
            int compileErrorCount = 0;
            var getErrors = CommandRegistry.GetCompileErrors;
            if (getErrors != null)
            {
                string compileErrors = getErrors();
                if (compileErrors != null)
                {
                    hasCompileErrors = true;
                    var match = System.Text.RegularExpressions.Regex.Match(compileErrors, @"COMPILE ERRORS \((\d+)\)");
                    if (match.Success) compileErrorCount = int.Parse(match.Groups[1].Value);
                }
            }

            // Compile time stats
            var compileStats = BridgeServer.GetCompileTimeStats();
            string compileTimeAvg = compileStats.HasValue ? $"{compileStats.Value.avg}s" : "unknown";
            string compileTimeLast = compileStats.HasValue ? $"{compileStats.Value.last}s" : "unknown";

            return Response.SuccessWithData(new
            {
                bridgeVersion = BridgeServer.Version,
                isCompiling = EditorApplication.isCompiling,
                hasCompileErrors,
                compileErrorCount,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                scriptsModified = ScriptsModifiedSinceCompile(),
                lastCompileRequest = lastCompileRequestStr,
                lastCompileFinished = lastCompileStr,
                compileTimeAvg,
                compileTimeLast,
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
            TimeoutSeconds = 300)]
        public static string Compile(string data)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("Cannot compile during play mode. Use STOP first.");

            // Refresh first so Unity's file tracker is up to date — this prevents
            // a second compile when Unity regains focus (auto-refresh on focus).
            // Refresh detects changed .cs files and triggers compilation automatically.
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            // If refresh didn't trigger compilation (e.g. no file changes detected),
            // force it explicitly
            if (!EditorApplication.isCompiling)
                CompilationPipeline.RequestScriptCompilation();

            EditorApplication.QueuePlayerLoopUpdate();

            return Response.SuccessWithData(new
            {
                message = "Compilation requested. Unity will reload assemblies - connection will be lost during reload.",
                timeoutSeconds = 300,
                reconnect = true
            });
        }

        [BridgeCommand("REFRESH", "Force asset database refresh",
            Category = "Core",
            Usage = "REFRESH",
            Streaming = false,
            RequiresMainThread = true,
            TimeoutSeconds = 300)]
        public static string Refresh()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("Cannot refresh during play mode. Use STOP first.");

            // Trigger asset database refresh - may trigger compilation if assets change
            // If compilation is triggered, Unity will reload assemblies and connection will be lost
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            // Force Unity to process the refresh even when in background
            EditorApplication.QueuePlayerLoopUpdate();

            return Response.SuccessWithData(new
            {
                message = "Asset refresh requested. If compilation is triggered, connection will be lost during assembly reload.",
                timeoutSeconds = 300,
                reconnect = true
            });
        }

        private static readonly System.Collections.Generic.HashSet<string> _menuBlacklist =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "File/Quit", "File/Exit" };

        [BridgeCommand("MENU", "Execute a Unity menu item",
            Category = "Core",
            Usage = "MENU Window/General/Console\n" +
                    "  MENU Edit/Preferences\n" +
                    "  MENU GameObject/3D Object/Cube",
            RequiresMainThread = true)]
        public static string Menu(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Response.Error("Usage: MENU <menu/path>");

            string menuPath = data.Trim();

            if (_menuBlacklist.Contains(menuPath))
                return Response.Error($"Blocked for safety: {menuPath}");

            bool ok = EditorApplication.ExecuteMenuItem(menuPath);
            if (!ok)
                return Response.Error($"Menu item not found or failed: {menuPath}");

            return Response.Success($"Executed: {menuPath}");
        }

        [BridgeCommand("PROFILE", "Control the Unity Profiler and read performance data",
            Category = "Core",
            Usage = "PROFILE                          - Status\n" +
                    "  PROFILE enable                   - Start profiling\n" +
                    "  PROFILE disable                  - Stop profiling\n" +
                    "  PROFILE clear                    - Clear all frames\n" +
                    "  PROFILE hierarchy                - Last frame hierarchy\n" +
                    "  PROFILE hierarchy min:1.0         - Filter items >1ms\n" +
                    "  PROFILE hierarchy depth:2         - Limit tree depth",
            RequiresMainThread = true)]
        public static string Profile(string data)
        {
            try
            {
                string action = string.IsNullOrWhiteSpace(data) ? "status" : data.Trim().Split(' ')[0].ToLower();

                switch (action)
                {
                    case "enable":
                        ProfilerDriver.enabled = true;
                        Profiler.enabled = true;
                        return Response.Success("Profiler enabled");

                    case "disable":
                        ProfilerDriver.enabled = false;
                        Profiler.enabled = false;
                        return Response.Success("Profiler disabled");

                    case "clear":
                        ProfilerDriver.ClearAllFrames();
                        return Response.Success("Profiler frames cleared");

                    case "status":
                        return Response.SuccessWithData(new
                        {
                            enabled = ProfilerDriver.enabled,
                            firstFrame = ProfilerDriver.firstFrameIndex,
                            lastFrame = ProfilerDriver.lastFrameIndex
                        });

                    case "hierarchy":
                        return ProfileHierarchy(data);

                    default:
                        return Response.Error($"Unknown action: {action}. Use: enable, disable, clear, status, hierarchy");
                }
            }
            catch (System.Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private static string ProfileHierarchy(string data)
        {
            int frame = ProfilerDriver.lastFrameIndex;
            int thread = 0;
            float minMs = 0f;
            int maxDepth = 3;

            // Parse options: "hierarchy min:1.0 depth:2 frame:5"
            if (data != null)
            {
                foreach (var part in data.Split(' '))
                {
                    if (part.StartsWith("min:") && float.TryParse(part.Substring(4), out var m)) minMs = m;
                    else if (part.StartsWith("depth:") && int.TryParse(part.Substring(6), out var d)) maxDepth = d;
                    else if (part.StartsWith("frame:") && int.TryParse(part.Substring(6), out var f)) frame = f;
                    else if (part.StartsWith("thread:") && int.TryParse(part.Substring(7), out var t)) thread = t;
                }
            }

            if (frame < ProfilerDriver.firstFrameIndex || frame > ProfilerDriver.lastFrameIndex)
                return Response.Error($"No profiler data. Frame range: {ProfilerDriver.firstFrameIndex}-{ProfilerDriver.lastFrameIndex}");

            using var frameData = ProfilerDriver.GetHierarchyFrameDataView(
                frame, thread,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime, false);

            if (!frameData.valid)
                return Response.Error("No valid profiler data for this frame/thread");

            var sb = new StringBuilder();
            sb.AppendLine($"Frame {frame} (thread {thread}):");
            sb.AppendLine($"{"Name",-50} {"Total",8} {"Self",8} {"Calls",6}");
            sb.AppendLine(new string('-', 74));

            int rootId = frameData.GetRootItemID();
            var children = new List<int>();
            frameData.GetItemChildren(rootId, children);

            foreach (int childId in children)
                AppendProfileItem(frameData, childId, sb, 0, minMs, maxDepth);

            return sb.ToString().TrimEnd();
        }

        private static void AppendProfileItem(HierarchyFrameDataView frameData, int itemId,
            StringBuilder sb, int depth, float minMs, int maxDepth)
        {
            float totalMs = frameData.GetItemColumnDataAsFloat(itemId, HierarchyFrameDataView.columnTotalTime);
            if (totalMs < minMs) return;

            float selfMs = frameData.GetItemColumnDataAsFloat(itemId, HierarchyFrameDataView.columnSelfTime);
            int calls = (int)frameData.GetItemColumnDataAsFloat(itemId, HierarchyFrameDataView.columnCalls);
            string name = frameData.GetItemName(itemId);

            string indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}{name,-50} {totalMs,7:F2}ms {selfMs,7:F2}ms {calls,5}");

            if (depth >= maxDepth) return;

            var children = new List<int>();
            frameData.GetItemChildren(itemId, children);
            foreach (int childId in children)
                AppendProfileItem(frameData, childId, sb, depth + 1, minMs, maxDepth);
        }
    }
}
