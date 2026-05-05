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

            // Step 2: mtime scan — covers cases the daemon missed (started after a change,
            // or external file modifications that bypassed the FSW).
            var roots = new List<string> { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages") };
            // UPM `file:` deps (e.g. `file:../../MyPackage`) resolve outside Packages/ — scan their actual locations too.
            roots.AddRange(GetLocalFilePackageRoots(projectRoot));
            var patterns = new[] { "*.cs", "*.asmdef", "*.asmref" };

            try
            {
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var pattern in patterns)
                    {
                        foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                        {
                            if (seenPaths.Contains(f)) continue;
                            var mtime = File.GetLastWriteTime(f);
                            if (mtime <= result.lastCompile) continue;

                            result.changedCount++;
                            if (result.changed.Count < maxList)
                            {
                                result.changed.Add(new ChangedScript
                                {
                                    path = MakeRelativePath(projectRoot, f),
                                    mtime = mtime.ToString("yyyy-MM-dd HH:mm:ss"),
                                    kind = "S" // scan-detected
                                });
                            }
                        }
                    }
                }
            }
            catch
            {
                result.scanFailed = true;
            }

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

        private static IEnumerable<string> GetLocalFilePackageRoots(string projectRoot)
        {
            var result = new List<string>();
            string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return result;

            string manifestDir = Path.GetDirectoryName(manifestPath);
            try
            {
                string text = File.ReadAllText(manifestPath);
                // Match every `"file:..."` value in the JSON. Sufficient for manifest.json's flat schema.
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    text, "\"file:([^\"]+)\"");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string rel = m.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(rel)) continue;
                    string full = Path.GetFullPath(Path.Combine(manifestDir, rel));
                    if (Directory.Exists(full)) result.Add(full);
                }
            }
            catch { }
            return result;
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
                    $"{changes} changed + {deletions} deleted script file(s) since last compile — run COMPILE");
            }
            catch (System.Exception ex)
            {
                return (false, 0, 0, $"check failed: {ex.GetType().Name}");
            }
        }

        private static bool ScriptsModifiedSinceCompileCached(double maxAgeSeconds = 1.0)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastScriptModifiedCheckTime < maxAgeSeconds)
                return _lastScriptsModified;

            _lastScriptsModified = ScriptsModifiedSinceCompile();
            _lastScriptModifiedCheckTime = now;
            return _lastScriptsModified;
        }

        private static string[] GetOpenEditorWindowsCached(double maxAgeSeconds = 2.0)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastWindowListTime < maxAgeSeconds)
                return _cachedWindowList;

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
            if (staleness < 0)
                return "Pong (WARNING: no heartbeat ticks yet — Unity may still be initializing)";
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

        [BridgeCommand("STATUS", "Get Unity Editor status",
            Category = "Core",
            Usage = "STATUS",
            RequiresMainThread = true,
            RelatedCommands = new[] { "DIAG", "LOG", "PROBE" })]
        public static string GetStatus()
        {
            var windowList = GetOpenEditorWindowsCached();

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

            // Get Unity Console counts + compile errors (always included)
            int consoleErrors = 0, consoleWarnings = 0;
            LogCommands.GetConsoleCounts(out consoleErrors, out consoleWarnings);

            // Always read compile errors from console — they're critical diagnostics
            var compileErrorMessages = LogCommands.GetCompileErrorsFromConsole();
            if (compileErrorMessages.Count > 0)
            {
                hasCompileErrors = true;
                compileErrorCount = compileErrorMessages.Count;
            }

            var uiToolkitDiagnostics = (consoleErrors > 0 || consoleWarnings > 0)
                ? LogCommands.GetUiToolkitDiagnosticsFromConsole(20)
                : new List<LogCommands.UiToolkitDiagnostic>();
            var uiToolkitErrors = uiToolkitDiagnostics
                .Where(d => d.IsError)
                .Select(d => d.ToString())
                .ToArray();

            // Play mode duration
            string playModeDuration = null;
            if (EditorApplication.isPlaying)
            {
                string startStr = SessionState.GetString(SessionKeys.PlayModeStartTime, "0");
                if (long.TryParse(startStr, out var startTicks) && startTicks > 0)
                {
                    var elapsed = System.DateTime.Now - new System.DateTime(startTicks);
                    playModeDuration = elapsed.TotalMinutes >= 1
                        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                        : $"{(int)elapsed.TotalSeconds}s";
                }
            }

            // Current scene
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string currentScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;

            return Response.SuccessWithData(new
            {
                bridgeVersion = BridgeServer.Version,
                diagnosticLog = BridgeDiagnostics.LogPath,
                isCompiling = EditorApplication.isCompiling,
                hasCompileErrors,
                compileErrorCount,
                compileErrors = compileErrorMessages.Count > 0 ? compileErrorMessages.ToArray() : null,
                hasUiToolkitErrors = uiToolkitErrors.Length > 0,
                uiToolkitErrorCount = uiToolkitErrors.Length,
                uiToolkitErrors = uiToolkitErrors.Length > 0 ? uiToolkitErrors : null,
                consoleErrors,
                consoleWarnings,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                playModeDuration,
                currentScene,
                currentScenePath,
                scriptsModified = ScriptsModifiedSinceCompileCached(),
                compileRecommended = ScriptsModifiedSinceCompileCached() && !EditorApplication.isCompiling,
                compileRecommendation = ScriptsModifiedSinceCompileCached() && !EditorApplication.isCompiling
                    ? "Run COMPILE — script changes detected since last compile."
                    : (EditorApplication.isCompiling ? "Compilation in progress." : "Up to date."),
                lastCompileRequest = lastCompileRequestStr,
                lastCompileFinished = lastCompileStr,
                compileTimeAvg,
                compileTimeLast,
                projectPath = Application.dataPath,
                unityVersion = Application.unityVersion,
                openWindows = windowList
            });
        }

        [BridgeCommand("COMPILE", "Force script recompilation. Pass 'force' to bypass the no-change skip check. " +
                                  "TIP: For fast syntax-only check (no Unity, no domain reload), use LINT instead.",
            Category = "Core",
            Usage = "COMPILE [force]\n" +
                    "  Prefer LINT first — offline, instant, catches syntax errors in NEW files Unity hasn't seen.\n" +
                    "  Use COMPILE only when you need full semantic check (type errors, missing usings).",
            Streaming = false,
            RequiresMainThread = true,
            TimeoutSeconds = 300,
            RelatedCommands = new[] { "LINT", "LOG", "STATUS", "REFRESH" })]
        public static string Compile(string data)
        {
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
            RelatedCommands = new[] { "COMPILE", "STATUS", "LOG" })]
        public static string Refresh()
        {
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
