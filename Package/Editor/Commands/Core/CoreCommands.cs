using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
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
        // Profiler markers for the heavier core ops. STATUS reflects on EditorWindow + reads
        // log error counts; LOG (via LogCommand) reflects into Unity's internal LogEntries;
        // MENU executes editor menu items (can trigger arbitrary work); PROFILE samples profiler.
        static readonly ProfilerMarker _markerStatus = new ProfilerMarker("Bridge.Core.Status");
        static readonly ProfilerMarker _markerCompile = new ProfilerMarker("Bridge.Core.Compile");
        static readonly ProfilerMarker _markerRefresh = new ProfilerMarker("Bridge.Core.Refresh");
        static readonly ProfilerMarker _markerMenu = new ProfilerMarker("Bridge.Core.Menu");
        static readonly ProfilerMarker _markerProfile = new ProfilerMarker("Bridge.Core.Profile");

        // PROFILE analysis budgets. These commands read frame views on the main thread, so they are
        // bounded by wall clock rather than by frame count: a deep-profiled capture carries ~250k
        // samples per frame, and a full sweep would stall the editor for minutes. Partial results
        // labelled "N of M scanned" are strictly better than a complete answer that freezes Unity.
        private const int PROFILE_SCAN_BUDGET_MS = 8000;   // cheap raw-view sweep for frame times
        private const int PROFILE_AGG_BUDGET_MS = 12000;   // hierarchy walk, the expensive half
        private const int PROFILE_SAMPLE_FRAMES = 40;      // frames folded into any one ranking
        static readonly ProfilerMarker _markerDiag = new ProfilerMarker("Bridge.Core.Diag");
        // Sub-markers inside STATUS to find which sub-call dominates a slow tick.
        static readonly ProfilerMarker _markerStatusWindowList = new ProfilerMarker("Bridge.Core.Status.OpenEditorWindows");
        static readonly ProfilerMarker _markerStatusScriptsModified = new ProfilerMarker("Bridge.Core.Status.ScriptsModifiedSinceCompile");
        static readonly ProfilerMarker _markerStatusCompileStats = new ProfilerMarker("Bridge.Core.Status.GetCompileTimeStats");
        static readonly ProfilerMarker _markerStatusGetCompileErrors = new ProfilerMarker("Bridge.Core.Status.GetCompileErrors");

        private static double _lastScriptModifiedCheckTime = -10;
        private static bool _lastScriptsModified;
        private static double _lastWindowListTime = -10;
        private static string[] _cachedWindowList = new string[0];

        public struct ChangedScript
        {
            public string path;
            public string mtime; // ISO 8601 local time
            public string kind;  // M=modified, C=created, D=deleted, R=renamed, S=scan-detected

            public override string ToString() => $"{mtime}  {kind}  {path}";
        }

        public struct ScriptScanResult
        {
            public bool hasLastCompile;
            public System.DateTime lastCompile; // local time
            public List<ChangedScript> changed;
            public List<ChangedScript> deleted; // separate for clarity in response
            public int changedCount;
            public int deletedCount;
            public bool scanFailed;
            public bool daemonAvailable;
        }

        /// <summary>
        /// Scan compile-relevant files under Assets/ or Packages/ and return any modified since last compile.
        /// Scans .cs, .asmdef, .asmref. Caps detail list at maxList entries; full count tracked separately.
        /// </summary>
        private static ScriptScanResult ScanModifiedScripts(int maxList = 20)
        {
            var result = new ScriptScanResult
            {
                changed = new List<ChangedScript>(),
                deleted = new List<ChangedScript>()
            };

            if (!long.TryParse(SessionState.GetString(SessionKeys.LastCompileTime, "0"), out var ticks) || ticks <= 0)
            {
                result.hasLastCompile = false;
                return result; // No compile recorded — caller treats as modified
            }

            result.hasLastCompile = true;
            result.lastCompile = new System.DateTime(ticks);
            long lastCompileUtcTicks = result.lastCompile.ToUniversalTime().Ticks;
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;

            // Step 1: Merge daemon's FileSystemWatcher change log (deletion-aware, real-time).
            // Daemon tracks ALL file events under Assets/ and Packages/ — covers deletions,
            // renames, and changes Unity hasn't been told about yet.
            var seenPaths = new HashSet<string>();
            try
            {
                string changeLogPath = Path.Combine(projectRoot, ".clibridge4unity", "changes.log");
                if (File.Exists(changeLogPath))
                {
                    result.daemonAvailable = true;
                    foreach (var line in File.ReadAllLines(changeLogPath))
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        var parts = line.Split('\t');
                        if (parts.Length < 3) continue;
                        if (!long.TryParse(parts[0], out long evtTicks)) continue;
                        if (evtTicks <= lastCompileUtcTicks) continue;
                        string kind = parts[1];
                        string fullPath = parts[2];
                        string oldPath = parts.Length > 3 ? parts[3] : "";
                        var local = new System.DateTime(evtTicks, System.DateTimeKind.Utc).ToLocalTime();

                        // Restrict to compile-relevant extensions for the result list (still covers
                        // .meta and assets via daemon, but COMPILE-skip logic only cares about scripts).
                        bool compileRelevant = IsCompileRelevantExt(fullPath) || IsCompileRelevantExt(oldPath);
                        if (!compileRelevant) continue;

                        var entry = new ChangedScript
                        {
                            path = MakeRelativePath(projectRoot, fullPath),
                            mtime = local.ToString("yyyy-MM-dd HH:mm:ss"),
                            kind = kind
                        };
                        if (kind == "D")
                        {
                            result.deletedCount++;
                            if (result.deleted.Count < maxList) result.deleted.Add(entry);
                        }
                        else
                        {
                            result.changedCount++;
                            seenPaths.Add(fullPath);
                            if (result.changed.Count < maxList) result.changed.Add(entry);
                        }
                    }
                }
            }
            catch
            {
                // Daemon log unreadable — fall through to mtime scan
            }

            // No fallback mtime scan — daemon is authoritative for change detection.
            // If the daemon log is absent (daemon never ran for this project), result.daemonAvailable
            // stays false and callers treat scriptsModified as "unknown / assume modified" so
            // they err on the side of recompiling. CLI auto-starts the daemon on every command,
            // so this only happens for the very first invocation against a fresh project.
            if (!result.daemonAvailable)
                result.scanFailed = true;

            // Sort displayed list by most recent first for readability
            result.changed.Sort((a, b) => string.CompareOrdinal(b.mtime, a.mtime));
            result.deleted.Sort((a, b) => string.CompareOrdinal(b.mtime, a.mtime));
            return result;
        }

        private static bool IsCompileRelevantExt(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asmdef", System.StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asmref", System.StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeRelativePath(string root, string full)
        {
            if (string.IsNullOrEmpty(full)) return full;
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(normalizedRoot, System.StringComparison.OrdinalIgnoreCase))
                return full.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
            return full.Replace('\\', '/');
        }

        private static bool ScriptsModifiedSinceCompile()
        {
            var scan = ScanModifiedScripts(maxList: 0);
            if (!scan.hasLastCompile || scan.scanFailed) return true;
            return scan.changedCount > 0 || scan.deletedCount > 0;
        }

        /// <summary>
        /// Thread-safe (no SessionState/Application access) compile recommendation derived
        /// from the daemon's change log. Used by DIAG which runs off the main thread.
        /// Returns (recommended, pendingChanges, pendingDeletions, reason).
        /// </summary>
        // Newest mtime across Library/ScriptAssemblies — when Unity last finished ANY compile.
        // Cached because DIAG/STATUS call this often and a project can carry a couple hundred
        // assemblies; the stat sweep is only a few ms but there is no reason to repeat it per call.
        // Stopwatch, not EditorApplication.timeSinceStartup — this runs off the main thread.
        private static long _asmTicksCache;
        private static readonly System.Diagnostics.Stopwatch _asmTicksAge = System.Diagnostics.Stopwatch.StartNew();
        private static long _asmTicksStampMs = -1;

        private static long NewestScriptAssemblyTicksCached(string projectRoot, double maxAgeSeconds = 5.0)
        {
            long nowMs = _asmTicksAge.ElapsedMilliseconds;
            if (_asmTicksStampMs >= 0 && (nowMs - _asmTicksStampMs) < maxAgeSeconds * 1000)
                return _asmTicksCache;
            long newest = 0;
            try
            {
                string dir = Path.Combine(projectRoot, "Library", "ScriptAssemblies");
                if (Directory.Exists(dir))
                {
                    foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                    {
                        long t = File.GetLastWriteTimeUtc(dll).Ticks;
                        if (t > newest) newest = t;
                    }
                }
            }
            catch { newest = 0; } // unreadable -> fall back to last-compiled.ticks alone
            _asmTicksCache = newest;
            _asmTicksStampMs = nowMs;
            return newest;
        }

        public static (bool recommended, int changes, int deletions, string reason) GetCompileRecommendationFromLog(string projectRoot)
        {
            try
            {
                string changeLogPath = Path.Combine(projectRoot, ".clibridge4unity", "changes.log");
                string lastCompiledPath = Path.Combine(projectRoot, ".clibridge4unity", "last-compiled.ticks");
                if (!File.Exists(changeLogPath))
                    return (false, 0, 0, "daemon not running — cannot determine pending changes");

                long lastCompiledTicks = 0;
                if (File.Exists(lastCompiledPath))
                    long.TryParse(File.ReadAllText(lastCompiledPath).Trim(), out lastCompiledTicks);

                // last-compiled.ticks is only written by our own COMPILE command, but Unity
                // auto-compiles on focus far more often than anyone runs COMPILE — so on its own
                // that watermark never advances and every edit since the last bridge-initiated
                // compile is reported pending forever, long after Unity built it.
                // Library/ScriptAssemblies is stamped by Unity on EVERY compile regardless of who
                // triggered it, so it is the ground truth. Take whichever watermark is later.
                long assemblyTicks = NewestScriptAssemblyTicksCached(projectRoot);
                if (assemblyTicks > lastCompiledTicks) lastCompiledTicks = assemblyTicks;

                int changes = 0, deletions = 0;
                foreach (var line in File.ReadAllLines(changeLogPath))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    if (!long.TryParse(parts[0], out long ticks)) continue;
                    if (ticks <= lastCompiledTicks) continue;
                    string kind = parts[1];
                    string path = parts[2];
                    if (!IsCompileRelevantExt(path) && (parts.Length < 4 || !IsCompileRelevantExt(parts[3])))
                        continue;
                    if (kind == "D") deletions++;
                    else changes++;
                }

                int total = changes + deletions;
                if (total == 0) return (false, 0, 0, "no script changes since last compile");
                return (true, changes, deletions,
                    $"{changes} changed + {deletions} deleted script file(s) newer than Unity's last compile — focus Unity (it auto-compiles) or run COMPILE");
            }
            catch (System.Exception ex)
            {
                return (false, 0, 0, $"check failed: {ex.GetType().Name}");
            }
        }

        // 10s TTL — daemon catches changes in real time, so a slightly stale STATUS reading
        // is fine. Was 1s, which forced a full mtime fallback scan on every STATUS call when
        // the cache expired.
        private static bool ScriptsModifiedSinceCompileCached(double maxAgeSeconds = 10.0)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastScriptModifiedCheckTime < maxAgeSeconds)
                return _lastScriptsModified;

            using var _profile = _markerStatusScriptsModified.Auto();
            _lastScriptsModified = ScriptsModifiedSinceCompile();
            _lastScriptModifiedCheckTime = now;
            return _lastScriptsModified;
        }

        private static string[] GetOpenEditorWindowsCached(double maxAgeSeconds = 2.0)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastWindowListTime < maxAgeSeconds)
                return _cachedWindowList;

            using var _profile = _markerStatusWindowList.Auto();
            var editorWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var windowList = new List<string>(editorWindows.Length);
            foreach (var win in editorWindows)
            {
                string title = win.titleContent?.text ?? win.GetType().Name;
                string typeName = win.GetType().Name;
                windowList.Add(title != typeName ? $"{title} ({typeName})" : typeName);
            }

            _cachedWindowList = windowList.ToArray();
            _lastWindowListTime = now;
            return _cachedWindowList;
        }

        [BridgeCommand("PING", "Test connection (includes main thread health)",
            Category = "Core",
            Usage = "PING")]
        public static string Ping()
        {
            var staleness = CommandRegistry.GetHeartbeatStaleness();
            // staleness < 0 means the tick counter is still zero — which is the normal resting state,
            // not a fault. The queue is usually drained straight from the SynchronizationContext
            // callback, a path that never touches the counter, so it rarely advances at all. And this
            // method is itself main-thread work: reaching this line proves the main thread is alive,
            // which made the old "Unity may still be initializing" warning self-refuting.
            if (staleness < 0)
                return Response.Success("Pong");
            if (staleness > 5.0)
                return $"Pong (WARNING: main thread unresponsive — last heartbeat {staleness:F1}s ago. Run DIAG for details.)";
            if (staleness > 1.0)
                return $"Pong (main thread slow — {staleness:F1}s since last tick)";
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
            using var _profile = _markerDiag.Auto();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"bridgeVersion: {BridgeServer.Version}");
            sb.AppendLine($"diagnosticLog: {BridgeDiagnostics.LogPath}");
            sb.AppendLine("--- heartbeat ---");
            sb.AppendLine(CommandRegistry.GetHeartbeatInfo());
            sb.AppendLine("--- thread ---");
            sb.AppendLine($"thread: {System.Threading.Thread.CurrentThread.ManagedThreadId} ({System.Threading.Thread.CurrentThread.Name})");
            sb.AppendLine($"syncCtx: {System.Threading.SynchronizationContext.Current?.GetType().Name ?? "null"}");
            sb.AppendLine(CommandRegistry.GetQueueDiagnostics());
            sb.AppendLine($"processMainWndHandle: {CommandRegistry.GetUnityHwnd()}");
            var pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            sb.AppendLine($"pid: {pid}");
            sb.AppendLine($"processName: {System.Diagnostics.Process.GetCurrentProcess().ProcessName}");
            sb.AppendLine($"dataPath: {UnityEngine.Application.dataPath}");
            string projRoot = UnityEngine.Application.dataPath.Replace("/Assets", "");
            string projName = System.IO.Path.GetFileName(projRoot);
            sb.AppendLine($"projectName: {projName}");
            sb.AppendLine("--- compile recommendation ---");
            var rec = GetCompileRecommendationFromLog(projRoot);
            sb.AppendLine($"compileRecommended: {rec.recommended}");
            sb.AppendLine($"pendingChanges: {rec.changes}");
            sb.AppendLine($"pendingDeletions: {rec.deletions}");
            sb.AppendLine($"reason: {rec.reason}");
            sb.AppendLine("--- windows for this PID ---");
            CommandRegistry.EnumProcessWindows(pid, sb);
            sb.AppendLine("--- all Unity-titled windows ---");
            CommandRegistry.EnumWindowsByTitle("Unity", sb);
            return sb.ToString().TrimEnd();
        }

        [BridgeCommand("CANCEL",
            "Cancel in-flight command(s) by name or --all. Frees QUEUED main-thread work; an action " +
            "already mid-execute can't be interrupted — use KILL for that. Bypasses the gate; always answers.",
            Category = "Core",
            Usage = "CANCEL <NAME>\n  CANCEL --all\n" +
                    "  Examples: CANCEL STATUS   CANCEL --all\n" +
                    "  Use DIAG to see the inFlight list before cancelling.")]
        public static string Cancel(string data)
        {
            string arg = (data ?? string.Empty).Trim();
            bool all = arg.Equals("--all", System.StringComparison.OrdinalIgnoreCase) || arg.Length == 0;
            string target = all ? null : arg.ToUpperInvariant();
            var cancelled = CommandRegistry.CancelInFlight(target);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(cancelled.Count == 0
                ? (all ? "No in-flight commands to cancel." : $"No in-flight command matching '{target}'.")
                : $"Cancelled {cancelled.Count} command(s):");
            foreach (var line in cancelled) sb.AppendLine($"  - {line}");
            sb.Append("Note: only QUEUED main-thread work is freed. An action already running on the main thread keeps going — restart Unity (KILL) if it's wedged.");
            return sb.ToString();
        }

        [BridgeCommand("BRIDGEINFO", "Bridge handshake: version + minimum compatible extension version (no Unity state, no main thread)",
            Category = "Core",
            Usage = "BRIDGEINFO")]
        public static string BridgeInfo()
        {
            // STABLE HANDSHAKE CONTRACT — consumed by the VSCode extension to decide compatibility.
            // NEVER rename this command and NEVER remove/repurpose a field; only ADD new fields.
            // It carries bridge-code metadata ONLY (no Unity/main-thread state), so it answers even
            // while the Editor is compiling or its main thread is blocked.
            var sb = new StringBuilder();
            sb.AppendLine($"bridgeVersion: {BridgeServer.Version}");
            sb.AppendLine($"minCompatibleExtensionVersion: {BridgeServer.MinCompatibleExtensionVersion}");
            sb.AppendLine("bridgeProtocol: 1");
            return sb.ToString().TrimEnd();
        }

        [BridgeCommand("STATUS", "Get Unity Editor status (degrades gracefully when main thread is busy)",
            Category = "Core",
            Usage = "STATUS",
            RelatedCommands = new[] { "DIAG", "LOG", "PROBE" })]
        public static async System.Threading.Tasks.Task<string> GetStatus()
        {
            // SessionState reads are safe from background (already used freely elsewhere),
            // so derive these up front — they're useful whether or not main thread responds.
            string lastCompileStr = ReadSessionStateDate(SessionKeys.LastCompileTime);
            string lastCompileRequestStr = ReadSessionStateDate(SessionKeys.LastCompileRequest);
            var compileStats = SafeGetCompileTimeStats();
            string compileTimeAvg = compileStats.HasValue ? $"{compileStats.Value.avg}s" : "unknown";
            string compileTimeLast = compileStats.HasValue ? $"{compileStats.Value.last}s" : "unknown";

            try
            {
                var mt = await CommandRegistry.RunOnMainThreadAsync(
                    () => BuildMainThreadStatus(), "STATUS", 5000);

                return Response.SuccessWithData(new
                {
                    bridgeVersion = BridgeServer.Version,
                    diagnosticLog = BridgeDiagnostics.LogPath,
                    mainThreadBusy = false,
                    isCompiling = mt.isCompiling,
                    hasCompileErrors = mt.hasCompileErrors,
                    compileErrorCount = mt.compileErrorCount,
                    compileErrors = mt.compileErrors,
                    hasUiToolkitErrors = mt.uiToolkitErrors != null && mt.uiToolkitErrors.Length > 0,
                    uiToolkitErrorCount = mt.uiToolkitErrors?.Length ?? 0,
                    uiToolkitErrors = mt.uiToolkitErrors,
                    consoleErrors = mt.consoleErrors,
                    consoleWarnings = mt.consoleWarnings,
                    isPlaying = mt.isPlaying,
                    isPaused = mt.isPaused,
                    playModeDuration = mt.playModeDuration,
                    currentScene = mt.currentScene,
                    currentScenePath = mt.currentScenePath,
                    scriptsModified = mt.scriptsModified,
                    compileRecommended = mt.scriptsModified && !mt.isCompiling,
                    compileRecommendation = mt.scriptsModified && !mt.isCompiling
                        ? "Uncompiled script changes. Unity auto-compiles on focus and the user has usually already compiled — only run COMPILE if results look stale or something isn't working."
                        : (mt.isCompiling ? "Compilation in progress." : "Up to date."),
                    lastCompileRequest = lastCompileRequestStr,
                    lastCompileFinished = lastCompileStr,
                    compileTimeAvg,
                    compileTimeLast,
                    projectPath = mt.projectPath,
                    unityVersion = mt.unityVersion,
                    openWindows = mt.openWindows
                });
            }
            catch (System.TimeoutException tex)
            {
                // Main thread blocked — return what we have from background-safe sources
                // plus the busy report so the caller knows *why* fields are missing.
                return Response.SuccessWithData(new
                {
                    bridgeVersion = BridgeServer.Version,
                    diagnosticLog = BridgeDiagnostics.LogPath,
                    mainThreadBusy = true,
                    busyReport = tex.Message.TrimEnd(),
                    heartbeatStaleness = CommandRegistry.GetHeartbeatStaleness(),
                    lastCompileRequest = lastCompileRequestStr,
                    lastCompileFinished = lastCompileStr,
                    compileTimeAvg,
                    compileTimeLast,
                    recommendation = "Main thread is blocked. Re-run STATUS shortly, or use DIAG for full diagnostics."
                });
            }
        }

        private struct MainThreadStatus
        {
            public bool isCompiling;
            public bool isPlaying;
            public bool isPaused;
            public bool hasCompileErrors;
            public int compileErrorCount;
            public string[] compileErrors;
            public string[] uiToolkitErrors;
            public int consoleErrors;
            public int consoleWarnings;
            public string currentScene;
            public string currentScenePath;
            public bool scriptsModified;
            public string projectPath;
            public string unityVersion;
            public string[] openWindows;
            public string playModeDuration;
        }

        private static MainThreadStatus BuildMainThreadStatus()
        {
            using var _profile = _markerStatus.Auto();
            var result = new MainThreadStatus
            {
                openWindows = GetOpenEditorWindowsCached()
            };

            var getErrors = CommandRegistry.GetCompileErrors;
            if (getErrors != null)
            {
                using var _p1 = _markerStatusGetCompileErrors.Auto();
                string compileErrors = getErrors();
                if (compileErrors != null)
                {
                    result.hasCompileErrors = true;
                    var match = System.Text.RegularExpressions.Regex.Match(compileErrors, @"COMPILE ERRORS \((\d+)\)");
                    if (match.Success) result.compileErrorCount = int.Parse(match.Groups[1].Value);
                }
            }

            int consoleErrors = 0, consoleWarnings = 0;
            LogCommands.GetConsoleCounts(out consoleErrors, out consoleWarnings);
            result.consoleErrors = consoleErrors;
            result.consoleWarnings = consoleWarnings;

            var compileErrorMessages = consoleErrors > 0
                ? LogCommands.GetCompileErrorsFromConsole()
                : new List<string>();
            if (compileErrorMessages.Count > 0)
            {
                result.hasCompileErrors = true;
                result.compileErrorCount = compileErrorMessages.Count;
                result.compileErrors = compileErrorMessages.ToArray();
            }

            var uiToolkitDiagnostics = (consoleErrors > 0 || consoleWarnings > 0)
                ? LogCommands.GetUiToolkitDiagnosticsFromConsole(20)
                : new List<LogCommands.UiToolkitDiagnostic>();
            var uiToolkitErrors = uiToolkitDiagnostics
                .Where(d => d.IsError)
                .Select(d => d.ToString())
                .ToArray();
            result.uiToolkitErrors = uiToolkitErrors.Length > 0 ? uiToolkitErrors : null;

            result.isCompiling = EditorApplication.isCompiling;
            result.isPlaying = EditorApplication.isPlaying;
            result.isPaused = EditorApplication.isPaused;
            result.currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            result.currentScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            result.scriptsModified = ScriptsModifiedSinceCompileCached();
            result.projectPath = Application.dataPath;
            result.unityVersion = Application.unityVersion;
            result.playModeDuration = result.isPlaying ? ReadPlayModeDurationFromSession() : null;
            return result;
        }

        private static string ReadSessionStateDate(string key)
        {
            try
            {
                if (long.TryParse(SessionState.GetString(key, "0"), out var ticks) && ticks > 0)
                    return new System.DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { }
            return "never";
        }

        private static string ReadPlayModeDurationFromSession()
        {
            try
            {
                string startStr = SessionState.GetString(SessionKeys.PlayModeStartTime, "0");
                if (!long.TryParse(startStr, out var startTicks) || startTicks <= 0) return null;
                var elapsed = System.DateTime.Now - new System.DateTime(startTicks);
                if (elapsed.TotalSeconds < 0) return null;
                return elapsed.TotalMinutes >= 1
                    ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                    : $"{(int)elapsed.TotalSeconds}s";
            }
            catch { return null; }
        }

        private static (int avg, int last, int count)? SafeGetCompileTimeStats()
        {
            try
            {
                _markerStatusCompileStats.Begin();
                return BridgeServer.GetCompileTimeStats();
            }
            catch { return null; }
            finally { _markerStatusCompileStats.End(); }
        }

        [BridgeCommand("COMPILE", "Force script recompilation. Pass 'force' to bypass the no-change skip check. " +
                                  "Rarely needed — Unity auto-compiles on focus and the user has usually already compiled.",
            Category = "Core",
            Usage = "COMPILE [force]\n" +
                    "  Rarely needed: Unity auto-compiles when focused, so the user has usually already compiled.\n" +
                    "  Reach for it only when something isn't working as expected (stale results, CODE_EXEC can't\n" +
                    "  see a new type) and STATUS confirms uncompiled changes. LINT is the offline alternative\n" +
                    "  when Unity can't compile — also reactive-only, not a routine check.",
            Streaming = false,
            RequiresMainThread = true,
            TimeoutSeconds = 300,
            RelatedCommands = new[] { "LINT", "LOG", "STATUS", "REFRESH" })]
        public static string Compile(string data)
        {
            using var _profile = _markerCompile.Auto();
            if (EditorApplication.isPlaying)
                return Response.Error("Cannot compile during play mode. Use STOP first.");

            bool force = !string.IsNullOrWhiteSpace(data) &&
                         data.Trim().Equals("force", System.StringComparison.OrdinalIgnoreCase);

            // Already compiling/updating? Don't stack — return immediately, caller waits.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return Response.SuccessWithData(new
                {
                    message = "Compilation already in progress. Connection will be lost during reload.",
                    timeoutSeconds = 300,
                    reconnect = true,
                    alreadyCompiling = true
                });
            }

            var scan = ScanModifiedScripts();
            string lastCompileStr = scan.hasLastCompile ? scan.lastCompile.ToString("yyyy-MM-dd HH:mm:ss") : "never";

            // No script changes since last compile? Skip — avoids needless domain reload (unless force).
            if (!force && scan.hasLastCompile && !scan.scanFailed
                && scan.changedCount == 0 && scan.deletedCount == 0)
            {
                // Scripts are clean — any stale compile errors in state are false positives, clear them.
                LogCommands.ClearCompileErrors();
                string skipNote = scan.daemonAvailable
                    ? "Detection backed by clibridge4unity daemon FileSystemWatcher (deletion-aware)."
                    : "Daemon not running — falling back to mtime scan; deletions/renames preserving mtime are NOT detected. Pass 'COMPILE force' to override.";
                return Response.SuccessWithData(new
                {
                    message = "No script changes detected since last compile. Skipped.",
                    skipped = true,
                    reconnect = false,
                    lastCompileTime = lastCompileStr,
                    daemonAvailable = scan.daemonAvailable,
                    changedFileCount = 0,
                    deletedFileCount = 0,
                    changedFiles = new ChangedScript[0],
                    deletedFiles = new ChangedScript[0],
                    note = skipNote
                });
            }

            // Pre-compile sync: ensure Unity's AssetDatabase sees the same changes our mtime scan saw.
            // Without this, RequestScriptCompilation may use stale assembly state if Unity hasn't
            // refreshed since focus was lost (common when bridge writes files programmatically).
            int reimportedCount = 0;
            int deletedCount = 0;
            string syncStrategy;
            if (force)
            {
                // Force path: full sweep — catches deletions/renames our scan misses, deletion-aware.
                AssetDatabase.Refresh(ImportAssetOptions.Default);
                syncStrategy = "AssetDatabase.Refresh (full sweep, deletion-aware)";
            }
            else if (scan.changedCount > 0 || scan.deletedCount > 0)
            {
                // Surgical: import detected changes + delete detected deletions. Avoids full sweep.
                foreach (var f in scan.changed)
                {
                    string p = f.path;
                    if (string.IsNullOrEmpty(p)) continue;
                    // ImportAsset only accepts asset-relative paths (Assets/... or Packages/...).
                    if (!p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith("Packages/", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Native directory-bundle plugins (.xcframework/.androidlib/…) trip Unity's
                    // PreviewImporter assert on force-reimport and don't need it for compilation —
                    // Unity's own refresh picks up rebuilt plugins. Skip (see AssetSyncHelper).
                    if (AssetSyncHelper.IsBundlePluginAsset(p)) continue;
                    try
                    {
                        AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                        reimportedCount++;
                    }
                    catch { }
                }
                foreach (var f in scan.deleted)
                {
                    string p = f.path;
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith("Packages/", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    // DeleteAsset is no-op if Unity already removed it; safe to call defensively.
                    try
                    {
                        if (AssetDatabase.DeleteAsset(p)) deletedCount++;
                    }
                    catch { }
                }
                syncStrategy = $"surgical (ImportAsset x{reimportedCount}, DeleteAsset x{deletedCount})";
            }
            else
            {
                syncStrategy = "none (no changes detected)";
            }

            // Mark daemon's change log as compiled so it prunes events up to now.
            try
            {
                string daemonDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".clibridge4unity");
                if (Directory.Exists(daemonDir))
                {
                    File.WriteAllText(Path.Combine(daemonDir, "last-compiled.ticks"),
                        System.DateTime.UtcNow.Ticks.ToString());
                }
            }
            catch { }

            CompilationPipeline.RequestScriptCompilation();
            EditorApplication.QueuePlayerLoopUpdate();

            string trigger = force
                ? (scan.changedCount > 0
                    ? $"Forced. {scan.changedCount} changed file(s) also detected since last compile."
                    : "Forced. No mtime changes detected since last compile (deletions/renames suspected).")
                : (scan.hasLastCompile
                    ? $"{scan.changedCount} changed file(s) detected since last compile."
                    : "No prior compile recorded — running fresh.");

            return Response.SuccessWithData(new
            {
                message = "Compilation requested. Unity will reload assemblies - connection will be lost during reload.",
                timeoutSeconds = 300,
                reconnect = true,
                forced = force,
                daemonAvailable = scan.daemonAvailable,
                lastCompileTime = lastCompileStr,
                trigger,
                preCompileSync = syncStrategy,
                reimportedCount,
                deletedAssetCount = deletedCount,
                changedFileCount = scan.changedCount,
                deletedFileCount = scan.deletedCount,
                changedFiles = scan.changed,
                deletedFiles = scan.deleted
            });
        }

        [BridgeCommand("REFRESH", "Force asset database refresh",
            Category = "Core",
            Usage = "REFRESH",
            Streaming = false,
            RequiresMainThread = true,
            TimeoutSeconds = 300,
            // AssetDatabase.Refresh runs synchronously here, so the caller previously got nothing
            // until the whole import sweep finished — minutes on a large project, and a reload will
            // drop the pipe partway regardless. Answer once it is clearly a long sweep.
            DetachAfterSeconds = 10,
            RelatedCommands = new[] { "COMPILE", "STATUS", "LOG" })]
        public static string Refresh()
        {
            using var _profile = _markerRefresh.Auto();
            if (EditorApplication.isPlaying)
                return Response.Error("Cannot refresh during play mode. Use STOP first.");

            // Already compiling/updating? Don't stack — refresh on top of in-progress compile
            // causes cascading domain reloads (Addressables, GooglePlayServicesResolver, etc.).
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return Response.SuccessWithData(new
                {
                    message = "Compile/import already in progress. Connection will be lost during reload.",
                    timeoutSeconds = 300,
                    reconnect = true,
                    alreadyBusy = true
                });
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
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
            RequiresMainThread = true,
            // ExecuteMenuItem is completely unbounded — the item may open a modal, which blocks the
            // main thread until a human clicks it. Answer after 3s rather than holding the caller
            // (and every other window's queued work) hostage to an arbitrary editor action.
            DetachAfterSeconds = 3)]
        public static string Menu(string data)
        {
            using var _profile = _markerMenu.Auto();
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

        [BridgeCommand("PROFILE", "Control the Unity Profiler and analyse captured performance data",
            Category = "Core",
            Usage = "PROFILE                          - Status + capture flags\n" +
                    "  PROFILE enable | disable | clear  - Control recording\n" +
                    "  PROFILE deep on|off              - Deep profiling (per-method rows; forces recompile)\n" +
                    "  PROFILE load <path.data>         - Load a .data capture (replaces current frames)\n" +
                    "  PROFILE save <path.data>         - Save current frames to a capture\n" +
                    "  PROFILE breakdown                - Full report: where time goes, calls, GC, spikes\n" +
                    "  PROFILE frames [top:N]           - Most expensive frames\n" +
                    "  PROFILE top [count:N] [by:self|total|calls|gc] [spikes]\n" +
                    "                                   - Most expensive methods/markers\n" +
                    "  PROFILE threads [frame:N]        - Threads present in a frame\n" +
                    "  PROFILE hierarchy [min:1.0] [depth:2] [frame:N] [thread:N]\n" +
                    "  Options: thread:N from:N to:N    - Apply to frames/top/breakdown",
            RequiresMainThread = true,
            // A .data capture can be gigabytes; LoadProfile is a synchronous main-thread read.
            // The server sends this as a __timeout hint so the CLI widens its own read window.
            TimeoutSeconds = 600)]
        public static string Profile(string data)
        {
            using var _profile = _markerProfile.Auto();
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

                    case "deep":
                        return ProfileDeep(data);

                    case "load":
                        return ProfileLoad(data);

                    case "save":
                        return ProfileSave(data);

                    case "status":
                        return ProfileStatus();

                    case "threads":
                        return ProfileThreads(data);

                    case "frames":
                        return ProfileFrames(data);

                    case "top":
                        return ProfileTop(data);

                    case "breakdown":
                    case "report":
                        return ProfileBreakdown(data);

                    case "group":
                        return ProfileGroup(data);

                    case "tree":
                        return ProfileTree(data);

                    case "callers":
                        return ProfileCallers(data);

                    case "hierarchy":
                        return ProfileHierarchy(data);

                    default:
                        return Response.Error($"Unknown action: {action}. Use: enable, disable, clear, deep, " +
                                              "load, save, status, threads, frames, top, breakdown, group, tree, " +
                                              "callers, hierarchy");
                }
            }
            catch (System.Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        // ---- PROFILE option parsing -------------------------------------------------------
        // Shared "key:value" scanner. Options are positional-free so "PROFILE top by:calls
        // count:30 thread:1" reads in any order.

        private static int ProfileIntOpt(string data, string key, int fallback)
        {
            if (string.IsNullOrEmpty(data)) return fallback;
            foreach (var part in data.Split(' '))
                if (part.StartsWith(key + ":", System.StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(part.Substring(key.Length + 1), out var v)) return v;
            return fallback;
        }

        private static string ProfileStrOpt(string data, string key, string fallback)
        {
            if (string.IsNullOrEmpty(data)) return fallback;
            foreach (var part in data.Split(' '))
                if (part.StartsWith(key + ":", System.StringComparison.OrdinalIgnoreCase))
                    return part.Substring(key.Length + 1);
            return fallback;
        }

        private static bool ProfileHasFlag(string data, string flag)
        {
            if (string.IsNullOrEmpty(data)) return false;
            foreach (var part in data.Split(' '))
                if (string.Equals(part, flag, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Everything after the sub-action verb, untouched — paths may contain spaces.</summary>
        private static string ProfileArgTail(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return "";
            var trimmed = data.Trim();
            int sp = trimmed.IndexOf(' ');
            return sp < 0 ? "" : trimmed.Substring(sp + 1).Trim().Trim('"');
        }

        private static string ProfileStatus()
        {
            var sb = new StringBuilder();
            ProfilerAnalysis.AppendCaptureFlags(sb);
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            sb.AppendLine($"frames: {first}..{last} ({(last >= first ? last - first + 1 : 0)} buffered)");
            if (last >= first)
                sb.AppendLine($"threads in last frame: {ProfilerAnalysis.ListThreads(last).Count}");
            else
                sb.AppendLine("no frames — record with PROFILE enable, or read a capture with PROFILE load <path>");
            return sb.ToString().TrimEnd();
        }

        private static string ProfileDeep(string data)
        {
            string arg = ProfileArgTail(data).ToLower();
            if (arg != "on" && arg != "off")
                return Response.Error($"Usage: PROFILE deep on|off   (currently {ProfilerDriver.deepProfiling})");

            bool want = arg == "on";
            if (ProfilerDriver.deepProfiling == want)
                return Response.Success($"Deep profiling already {(want ? "on" : "off")}");

            // Toggling reinstruments every managed method, so Unity recompiles and reloads the
            // domain — the pipe dies exactly as it does for COMPILE. Say so rather than time out.
            ProfilerDriver.deepProfiling = want;
            return Response.Success(
                $"Deep profiling -> {(want ? "on" : "off")}. Unity will recompile and reload the domain " +
                "(this connection drops; reconnect and re-check with PROFILE). " +
                (want
                    ? "Deep profiling inflates absolute timings — read ratios, not milliseconds."
                    : "Per-method rows are gone; only instrumented markers remain."));
        }

        private static string ProfileLoad(string data)
        {
            string path = ProfileArgTail(data);
            if (string.IsNullOrEmpty(path))
                return Response.Error("Usage: PROFILE load <path to .data capture>");
            if (!File.Exists(path))
                return Response.Error($"Capture not found: {path}");

            var info = new FileInfo(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // keepExistingData:false — a capture is read as its own timeline; merging it into
            // whatever the editor happened to be recording produces a meaningless frame range.
            bool ok = ProfilerDriver.LoadProfile(path, false);
            sw.Stop();

            if (!ok)
                return Response.Error($"LoadProfile failed for {path} ({info.Length / (1024 * 1024)}MB). " +
                                      "Unity rejects captures written by a different Editor version.");

            var sb = new StringBuilder();
            sb.AppendLine($"Loaded {Path.GetFileName(path)} ({info.Length / (1024.0 * 1024.0):F1}MB) in {sw.ElapsedMilliseconds}ms");
            sb.AppendLine();
            sb.Append(ProfileStatus());
            return sb.ToString();
        }

        private static string ProfileSave(string data)
        {
            string path = ProfileArgTail(data);
            if (string.IsNullOrEmpty(path))
                return Response.Error("Usage: PROFILE save <path to .data>");
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!ProfilerDriver.SaveProfile(path)) return Response.Error($"SaveProfile failed: {path}");
            var len = File.Exists(path) ? new FileInfo(path).Length : 0;
            return Response.Success($"Saved {path} ({len / (1024.0 * 1024.0):F1}MB)");
        }

        private static string ProfileThreads(string data)
        {
            int frame = ProfileIntOpt(data, "frame", ProfilerDriver.lastFrameIndex);
            int minSamples = ProfileIntOpt(data, "min", 0);
            var threads = ProfilerAnalysis.ListThreads(frame, 64, minSamples);
            if (threads.Count == 0) return Response.Error($"No thread data for frame {frame}");
            var sb = new StringBuilder();
            sb.AppendLine($"Threads in frame {frame} ({threads.Count}):");
            foreach (var t in threads) sb.AppendLine("  " + t);
            return sb.ToString().TrimEnd();
        }

        private static string ProfileFrames(string data)
        {
            int thread = ProfileIntOpt(data, "thread", ProfilerAnalysis.MainThreadIndex);
            int topN = ProfileIntOpt(data, "top", 15);
            var scan = ProfilerAnalysis.ScanFrames(thread, PROFILE_SCAN_BUDGET_MS,
                ProfileIntOpt(data, "from", -1), ProfileIntOpt(data, "to", -1));

            if (scan.Frames.Count == 0)
                return Response.Error("No profiler frames. Record with PROFILE enable, or PROFILE load <path>.");

            var sb = new StringBuilder();
            ProfilerAnalysis.AppendCaptureFlags(sb);
            ProfilerAnalysis.AppendScanHeader(sb, scan, thread);
            sb.AppendLine();
            sb.AppendLine("=== MOST EXPENSIVE FRAMES ===");
            sb.AppendLine($"{"frame",-10} {"ms",10} {"xMedian",8}  heaviest marker (self)");
            foreach (var fc in scan.Frames.OrderByDescending(f => f.Ms).Take(topN))
                sb.AppendLine($"{fc.Frame,-10} {fc.Ms,9:F2}ms {(scan.Median > 0 ? fc.Ms / scan.Median : 0),7:F1}x  " +
                              ProfilerAnalysis.TopMarkerInFrame(fc.Frame, thread));
            return sb.ToString().TrimEnd();
        }

        private static string ProfileTop(string data)
        {
            int thread = ProfileIntOpt(data, "thread", ProfilerAnalysis.MainThreadIndex);
            int count = ProfileIntOpt(data, "count", 25);
            string by = ProfileStrOpt(data, "by", "self").ToLower();
            bool spikesOnly = ProfileHasFlag(data, "spikes");

            var scan = ProfilerAnalysis.ScanFrames(thread, PROFILE_SCAN_BUDGET_MS,
                ProfileIntOpt(data, "from", -1), ProfileIntOpt(data, "to", -1));
            if (scan.Frames.Count == 0)
                return Response.Error("No profiler frames. Record with PROFILE enable, or PROFILE load <path>.");

            // Steady state by default: spikes have their own causes and would otherwise dominate
            // an average that is supposed to describe the normal frame.
            var pool = spikesOnly ? scan.Spikes : scan.Steady;
            var frames = ProfilerAnalysis.EvenSample(pool, PROFILE_SAMPLE_FRAMES);
            var stats = ProfilerAnalysis.AggregateMarkers(frames, thread, PROFILE_AGG_BUDGET_MS, out int analyzed);
            if (analyzed == 0) return Response.Error("No readable frames in the selected set.");

            // filter: scopes the ranking to one subsystem — "filter:MTD2" strips Unity and editor
            // rows so your own code is ranked against itself rather than buried under the engine.
            string filter = ProfileStrOpt(data, "filter", null);
            if (!string.IsNullOrEmpty(filter))
                stats = stats.Where(s => s.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                             .ToList();

            var ordered = by == "total" ? stats.OrderByDescending(s => s.TotalMs)
                        : by == "calls" ? stats.OrderByDescending(s => s.Calls)
                        : by == "gc" ? stats.OrderByDescending(s => s.GcBytes)
                        : stats.OrderByDescending(s => s.SelfMs);

            var sb = new StringBuilder();
            ProfilerAnalysis.AppendCaptureFlags(sb);
            ProfilerAnalysis.AppendScanHeader(sb, scan, thread);
            sb.AppendLine();
            sb.AppendLine($"=== TOP MARKERS by {by} ({(spikesOnly ? "SPIKE" : "steady-state")} frames: " +
                          $"{analyzed} sampled of {pool.Count}" +
                          $"{(string.IsNullOrEmpty(filter) ? "" : $", filter '{filter}'")}) ===");
            sb.AppendLine($"{"self/f",10} {"total/f",10} {"calls/f",9} {"us/call",9} {"gc/f",10}  name");
            foreach (var s in ordered.Take(count))
                sb.AppendLine($"{s.SelfMs / analyzed,9:F3}ms {s.TotalMs / analyzed,9:F3}ms " +
                              $"{s.Calls / analyzed,8:F0} {s.UsPerCall(analyzed),8:F1}u " +
                              $"{s.GcBytes / analyzed,9:F0}B  {s.Name}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The report you read before forming an opinion. A flat "slowest methods" list is not
        /// enough on its own: it hides how much of the frame is editor/profiler overhead, hides
        /// cost that is spread over thousands of cheap calls, and averages hitches into the steady
        /// state. This assembles those separately so each conclusion rests on the right evidence.
        /// </summary>
        private static string ProfileBreakdown(string data)
        {
            int thread = ProfileIntOpt(data, "thread", ProfilerAnalysis.MainThreadIndex);
            var scan = ProfilerAnalysis.ScanFrames(thread, PROFILE_SCAN_BUDGET_MS,
                ProfileIntOpt(data, "from", -1), ProfileIntOpt(data, "to", -1));
            if (scan.Frames.Count == 0)
                return Response.Error("No profiler frames. Record with PROFILE enable, or PROFILE load <path>.");

            var steady = scan.Steady;
            var spikes = scan.Spikes;
            var sample = ProfilerAnalysis.EvenSample(steady, PROFILE_SAMPLE_FRAMES);

            var sb = new StringBuilder();
            sb.AppendLine("=== CAPTURE FLAGS (read before trusting any number below) ===");
            ProfilerAnalysis.AppendCaptureFlags(sb);
            ProfilerAnalysis.AppendScanHeader(sb, scan, thread);
            sb.AppendLine($"steady-state frames (<=1.5x median): {steady.Count}   spikes (>=2x median): {spikes.Count}");
            sb.AppendLine();

            // 1. Structural split — decides whether the capture describes the game at all.
            var roots = ProfilerAnalysis.RootBreakdown(sample, thread, out double avgFrame, out int rootFrames);
            sb.AppendLine($"=== WHERE THE TIME GOES (top level, {rootFrames} steady frames, avg frame {avgFrame:F2}ms) ===");
            double overhead = 0;
            foreach (var kv in roots.Take(12))
            {
                if (ProfilerAnalysis.IsOverheadMarker(kv.Key)) overhead += kv.Value;
                sb.AppendLine($"  {kv.Value,8:F2}ms {(avgFrame > 0 ? kv.Value / avgFrame * 100 : 0),7:F1}%  {kv.Key}" +
                              (ProfilerAnalysis.IsOverheadMarker(kv.Key) ? "   <- not present in a player build" : ""));
            }
            if (overhead > 0 && avgFrame > 0)
                sb.AppendLine($"  --> editor/profiler overhead is {overhead / avgFrame * 100:F0}% of this frame; " +
                              "game cost is the remainder.");
            sb.AppendLine();

            // 2. Thread balance — main-bound vs render-bound.
            int probe = steady.Count > 0 ? steady[steady.Count / 2] : scan.Frames[0].Frame;
            sb.AppendLine($"=== THREAD BALANCE (frame {probe}) ===");
            foreach (var t in ProfilerAnalysis.ListThreads(probe, 8, 3)) sb.AppendLine("  " + t);
            sb.AppendLine();

            var stats = ProfilerAnalysis.AggregateMarkers(sample, thread, PROFILE_AGG_BUDGET_MS, out int analyzed);
            if (analyzed > 0)
            {
                AppendMarkerTable(sb, $"TOP BY TOTAL TIME (inclusive — structural cost, {analyzed} frames)",
                    stats.OrderByDescending(s => s.TotalMs).Take(15), analyzed);
                AppendMarkerTable(sb, "TOP BY SELF TIME (leaf cost — the actual work)",
                    stats.OrderByDescending(s => s.SelfMs).Take(15), analyzed);
                AppendMarkerTable(sb, "TOP BY CALL COUNT (structural bloat — many cheap calls)",
                    stats.OrderByDescending(s => s.Calls).Take(15), analyzed);
                var gc = stats.Where(s => s.GcBytes > 0).OrderByDescending(s => s.GcBytes).Take(12).ToList();
                if (gc.Count > 0)
                    AppendMarkerTable(sb, "TOP BY GC ALLOC (per frame — drives collection spikes)", gc, analyzed);
            }

            // 3. Spikes last, and never averaged into the above.
            if (spikes.Count > 0)
            {
                sb.AppendLine("=== SPIKE FRAMES (distinct cause from steady state) ===");
                foreach (int f in spikes.Take(10))
                {
                    float ms = scan.MsOf(f);
                    sb.AppendLine($"  frame {f,-9} {ms,9:F2}ms {(scan.Median > 0 ? ms / scan.Median : 0),6:F1}x  " +
                                  ProfilerAnalysis.TopMarkerInFrame(f, thread));
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Re-aggregate the same frames at a coarser grain. The leaf ranking answers "which call is
        /// slow"; this answers "which assembly / class / system owns the frame" — the question you
        /// actually act on, and one a 2000-row leaf list cannot show.
        /// </summary>
        private static string ProfileGroup(string data)
        {
            int thread = ProfileIntOpt(data, "thread", ProfilerAnalysis.MainThreadIndex);
            int count = ProfileIntOpt(data, "count", 25);
            string mode = ProfileStrOpt(data, "by", "assembly").ToLower();
            string filter = ProfileStrOpt(data, "filter", null);
            string sort = ProfileStrOpt(data, "sort", "self").ToLower();
            bool spikesOnly = ProfileHasFlag(data, "spikes");

            if (mode != "assembly" && mode != "namespace" && mode != "class" && mode != "prefix")
                return Response.Error($"Unknown grouping '{mode}'. Use by:assembly|namespace|class|prefix");

            var scan = ProfilerAnalysis.ScanFrames(thread, PROFILE_SCAN_BUDGET_MS,
                ProfileIntOpt(data, "from", -1), ProfileIntOpt(data, "to", -1));
            if (scan.Frames.Count == 0)
                return Response.Error("No profiler frames. Record with PROFILE enable, or PROFILE load <path>.");

            var pool = spikesOnly ? scan.Spikes : scan.Steady;
            var frames = ProfilerAnalysis.EvenSample(pool, PROFILE_SAMPLE_FRAMES);
            var stats = ProfilerAnalysis.AggregateMarkers(frames, thread, PROFILE_AGG_BUDGET_MS, out int analyzed);
            if (analyzed == 0) return Response.Error("No readable frames in the selected set.");

            var grouped = ProfilerAnalysis.Regroup(stats, mode, filter);
            var ordered = sort == "total" ? grouped.OrderByDescending(s => s.TotalMs)
                        : sort == "calls" ? grouped.OrderByDescending(s => s.Calls)
                        : sort == "gc" ? grouped.OrderByDescending(s => s.GcBytes)
                        : grouped.OrderByDescending(s => s.SelfMs);

            var sb = new StringBuilder();
            ProfilerAnalysis.AppendCaptureFlags(sb);
            ProfilerAnalysis.AppendScanHeader(sb, scan, thread);
            sb.AppendLine();
            sb.AppendLine($"=== GROUPED BY {mode.ToUpper()} sorted by {sort} " +
                          $"({(spikesOnly ? "SPIKE" : "steady-state")}, {analyzed} frames" +
                          $"{(string.IsNullOrEmpty(filter) ? "" : $", filter '{filter}'")}) ===");
            sb.AppendLine($"{"self/f",10} {"total/f",10} {"calls/f",9} {"gc/f",10}  group");
            foreach (var s in ordered.Take(count))
                sb.AppendLine($"{s.SelfMs / analyzed,9:F3}ms {s.TotalMs / analyzed,9:F3}ms " +
                              $"{s.Calls / analyzed,8:F0} {s.GcBytes / analyzed,9:F0}B  {s.Name}");

            double totSelf = grouped.Sum(g => g.SelfMs) / analyzed;
            sb.AppendLine($"  -- {grouped.Count} groups, {totSelf:F2}ms total self/frame --");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Drill into the call tree under a marker. Deliberately single-frame: a tree is a shape,
        /// and averaging shapes across frames invents parent/child pairs that never co-occurred.
        /// Defaults to the median steady frame; pin a spike with frame:N.
        /// </summary>
        private static string ProfileTree(string data)
        {
            int thread = ProfileIntOpt(data, "thread", ProfilerAnalysis.MainThreadIndex);
            int depth = ProfileIntOpt(data, "depth", 4);
            int rows = ProfileIntOpt(data, "rows", 200);
            float min = 0f;
            var minStr = ProfileStrOpt(data, "min", null);
            if (minStr != null) float.TryParse(minStr, out min);

            // Everything that is not an option is the marker to expand (may contain spaces).
            var tail = ProfileArgTail(data);
            var markerParts = tail.Split(' ')
                .Where(p => p.Length > 0 && p.IndexOf(':') < 0)
                .ToArray();
            string marker = string.Join(" ", markerParts);

            int frame = ProfileIntOpt(data, "frame", -1);
            var scan = ProfilerAnalysis.ScanFrames(thread, PROFILE_SCAN_BUDGET_MS);
            if (scan.Frames.Count == 0)
                return Response.Error("No profiler frames. Record with PROFILE enable, or PROFILE load <path>.");
            if (frame < 0)
            {
                var steady = scan.Steady;
                frame = steady.Count > 0 ? steady[steady.Count / 2] : scan.Frames[0].Frame;
            }

            using var h = ProfilerAnalysis.OpenHierarchy(frame, thread);
            if (h == null || !h.valid)
                return Response.Error($"No valid data for frame {frame}, thread {thread}");

            var sb = new StringBuilder();
            ProfilerAnalysis.AppendCaptureFlags(sb);
            sb.AppendLine($"=== CALL TREE  frame {frame} ({h.frameTimeMs:F2}ms, thread {thread}), " +
                          $"depth {depth}{(min > 0 ? $", >={min}ms" : "")}" +
                          $"{(string.IsNullOrEmpty(marker) ? "" : $", under '{marker}'")} ===");
            sb.AppendLine($"{"total",10} {"self",9} {"calls",7} {"gc",10}  name");
            ProfilerAnalysis.AppendSubtree(h, marker, depth, min, sb, rows);
            return sb.ToString().TrimEnd();
        }

        /// <summary>Reverse view: which parents issue a marker, and what each costs.</summary>
        private static string ProfileCallers(string data)
        {
            int thread = ProfileIntOpt(data, "thread", ProfilerAnalysis.MainThreadIndex);
            int count = ProfileIntOpt(data, "count", 20);
            bool spikesOnly = ProfileHasFlag(data, "spikes");

            var tail = ProfileArgTail(data);
            string marker = string.Join(" ", tail.Split(' ')
                .Where(p => p.Length > 0 && p.IndexOf(':') < 0 &&
                            !string.Equals(p, "spikes", System.StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(marker))
                return Response.Error("Usage: PROFILE callers <marker substring> [count:N] [thread:N] [spikes]");

            var scan = ProfilerAnalysis.ScanFrames(thread, PROFILE_SCAN_BUDGET_MS);
            if (scan.Frames.Count == 0)
                return Response.Error("No profiler frames. Record with PROFILE enable, or PROFILE load <path>.");

            var pool = spikesOnly ? scan.Spikes : scan.Steady;
            var frames = ProfilerAnalysis.EvenSample(pool, PROFILE_SAMPLE_FRAMES);
            var callers = ProfilerAnalysis.FindCallers(frames, thread, marker, PROFILE_AGG_BUDGET_MS, out int analyzed);
            if (analyzed == 0) return Response.Error("No readable frames in the selected set.");
            if (callers.Count == 0)
                return Response.Error($"No marker matching '{marker}' in {analyzed} sampled frames.");

            var sb = new StringBuilder();
            ProfilerAnalysis.AppendCaptureFlags(sb);
            sb.AppendLine($"=== CALLERS OF '{marker}' ({(spikesOnly ? "SPIKE" : "steady-state")}, {analyzed} frames) ===");
            sb.AppendLine($"{"total/f",10} {"self/f",10} {"calls/f",9} {"gc/f",10}  parent");
            foreach (var c in callers.Take(count))
                sb.AppendLine($"{c.TotalMs / analyzed,9:F3}ms {c.SelfMs / analyzed,9:F3}ms " +
                              $"{c.Calls / analyzed,8:F0} {c.GcBytes / analyzed,9:F0}B  {c.Name}");
            sb.AppendLine($"  -- {callers.Count} distinct parents, " +
                          $"{callers.Sum(c => c.Calls) / analyzed:F0} calls/frame total --");
            return sb.ToString().TrimEnd();
        }

        private static void AppendMarkerTable(StringBuilder sb, string title,
            IEnumerable<ProfilerAnalysis.MarkerStat> rows, int frames)
        {
            sb.AppendLine($"=== {title} ===");
            sb.AppendLine($"{"self/f",10} {"total/f",10} {"calls/f",9} {"us/call",9} {"gc/f",10}  name");
            foreach (var s in rows)
                sb.AppendLine($"{s.SelfMs / frames,9:F3}ms {s.TotalMs / frames,9:F3}ms " +
                              $"{s.Calls / frames,8:F0} {s.UsPerCall(frames),8:F1}u " +
                              $"{s.GcBytes / frames,9:F0}B  {s.Name}");
            sb.AppendLine();
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
