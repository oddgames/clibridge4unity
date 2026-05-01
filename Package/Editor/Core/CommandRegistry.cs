using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace clibridge4unity
{
    /// <summary>
    /// Stores metadata about a registered command.
    /// </summary>
    public class CommandInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Usage { get; set; }
        public string Category { get; set; }
        public bool RequiresMainThread { get; set; }
        public bool IsStreaming { get; set; }
        public int TimeoutSeconds { get; set; }
        public string[] RelatedCommands { get; set; }
        public MethodInfo Method { get; set; }
        public object Instance { get; set; }
    }

    /// <summary>
    /// Registry for bridge commands. Collects all [BridgeCommand] methods at startup.
    /// </summary>
    [InitializeOnLoad]
    public static class CommandRegistry
    {
        private static Dictionary<string, CommandInfo> _commands;
        private static bool _isInitialized;

        // Log capture hooks - set by LogCommands to avoid circular asmdef dependency
        public static Func<long> GetLastLogId;
        public static Func<long, int, string> GetLogsSinceFormatted;
        public static Func<string, string, string, string> GetUiToolkitDiagnosticsForCommand;

        // Compile error hook - set by LogCommands to check for active compiler errors
        public static Func<string> GetCompileErrors;

        // Path shortening hook - set by StackTraceMinimizer to shorten paths in all responses
        public static Func<string, string> ShortenResponsePaths;

        // Per-request flag to skip path shortening (e.g. LOG raw)
        [ThreadStatic] public static bool SkipPathShortening;

        // Lock-free main thread work queue
        private static readonly ConcurrentQueue<MainThreadWork> _mainThreadQueue = new ConcurrentQueue<MainThreadWork>();
        private static volatile bool _isRunning = true;

        // Win32 APIs for waking Unity's message pump when in background
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);
        private const uint WM_NULL = 0x0000;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_PAINT = 0x000F;
        private const uint SMTO_BLOCK = 0x0001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static IntPtr _unityWindowHandle;

        private static long _timerTickCount; // increments on each editor update for diagnostics
        private static DateTime _lastTimerTick;

        private class MainThreadWork
        {
            public Func<object> Action;
            public TaskCompletionSource<object> CompletionSource;
            public string Description;
            public DateTime EnqueuedAt;
            public volatile bool IsExecuting; // true once dequeued and running
        }

        // Captured on main thread for SynchronizationContext.Post
        private static SynchronizationContext _mainThreadContext;
        private static double _lastHwndRefreshTime;

        static CommandRegistry()
        {
            BridgeDiagnostics.Log("CommandRegistry", "static ctor");
            EditorApplication.update += InitOnFirstTick;
        }

        private static void InitOnFirstTick()
        {
            BridgeDiagnostics.Log("CommandRegistry", "InitOnFirstTick");
            EditorApplication.update -= InitOnFirstTick;
            OnAfterAssemblyReload();
        }

        private static void OnAfterAssemblyReload()
        {
            BridgeDiagnostics.Log("CommandRegistry", "OnAfterAssemblyReload enter");
            if (Application.isBatchMode)
            {
                BridgeDiagnostics.Log("CommandRegistry", "batch mode - registry disabled");
                return;
            }

            AssemblyReloadEvents.beforeAssemblyReload += ShutdownForReload;
            BridgeDiagnostics.Log("CommandRegistry", "beforeAssemblyReload registered");

            _mainThreadContext = SynchronizationContext.Current;
            _lastTimerTick = DateTime.Now;
            BridgeDiagnostics.Log("CommandRegistry", $"sync context: {_mainThreadContext?.GetType().Name ?? "null"}");

            EditorApplication.update += OnEditorUpdate;
            BridgeDiagnostics.Log("CommandRegistry", "EditorUpdate subscribed");

            var wakeThread = new Thread(WakeLoop)
            {
                Name = "Bridge Wake Thread",
                IsBackground = true
            };
            wakeThread.Start();
            BridgeDiagnostics.Log("CommandRegistry", "wake thread started");

            Debug.Log($"[Bridge] CommandRegistry init - HWND: {_unityWindowHandle}, SyncCtx: {_mainThreadContext?.GetType().Name}");
            BridgeDiagnostics.Log("CommandRegistry", "OnAfterAssemblyReload exit");
        }

        private static void ShutdownForReload()
        {
            BridgeDiagnostics.Log("CommandRegistry", "ShutdownForReload enter");
            _isRunning = false;
            EditorApplication.update -= OnEditorUpdate;

            if (_unityWindowHandle != IntPtr.Zero)
            {
                SessionState.SetString(SessionKeys.UnityHwnd, _unityWindowHandle.ToInt64().ToString());
                BridgeDiagnostics.Log("CommandRegistry", $"saved hwnd={_unityWindowHandle}");
            }
            BridgeDiagnostics.Log("CommandRegistry", "ShutdownForReload exit");
        }

        private static void OnEditorUpdate()
        {
            _timerTickCount++;
            _lastTimerTick = DateTime.Now;

            if (_unityWindowHandle == IntPtr.Zero &&
                !EditorApplication.isCompiling &&
                !EditorApplication.isUpdating &&
                EditorApplication.timeSinceStartup - _lastHwndRefreshTime > 1.0)
            {
                _lastHwndRefreshTime = EditorApplication.timeSinceStartup;
                TryRefreshUnityHwnd();
            }

            ProcessAllPendingWork();
        }

        /// <summary>
        /// Process ALL pending work items. Called from EditorApplication.update (main thread).
        /// </summary>
        private static void ProcessAllPendingWork()
        {
            // Drain the queue - process everything available
            while (_mainThreadQueue.TryDequeue(out var work))
            {
                // Skip cancelled (timed-out) items
                if (work.CompletionSource.Task.IsCanceled)
                    continue;

                work.IsExecuting = true;
                BridgeDiagnostics.Log("CommandRegistry", $"main-thread work begin: {work.Description}");
                try
                {
                    var result = work.Action();
                    work.CompletionSource.TrySetResult(result);
                    BridgeDiagnostics.Log("CommandRegistry", $"main-thread work end: {work.Description}");
                }
                catch (Exception ex)
                {
                    // Unwrap TargetInvocationException — the outer layer is just a reflection
                    // abstraction leak from MethodInfo.Invoke; callers care about the real cause.
                    var inner = (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                        ? tie.InnerException : ex;
                    BridgeDiagnostics.LogException($"CommandRegistry main-thread work failed: {work.Description}", inner);
                    Debug.LogError($"[Bridge] Main thread action failed: {inner}");
                    work.CompletionSource.TrySetException(inner);
                }
            }
        }

        /// <summary>
        /// Background thread that posts work via SynchronizationContext AND
        /// sends Win32 messages to wake Unity's editor loop.
        /// </summary>
        private static void WakeLoop()
        {
            BridgeDiagnostics.Log("CommandRegistry", "WakeLoop enter");
            int cycle = 0;
            while (_isRunning)
            {
                try
                {
                    if (!_mainThreadQueue.IsEmpty)
                    {
                        cycle++;

                        // Post via SynchronizationContext (processed during Unity's sync context pump)
                        _mainThreadContext?.Post(_ => ProcessAllPendingWork(), null);

                        var hwnd = _unityWindowHandle;
                        if (hwnd != IntPtr.Zero)
                        {
                            // Send message barrage to trigger Unity's message pump
                            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                            PostMessage(hwnd, WM_TIMER, IntPtr.Zero, IntPtr.Zero);
                            InvalidateRect(hwnd, IntPtr.Zero, false);
                            PostMessage(hwnd, WM_PAINT, IntPtr.Zero, IntPtr.Zero);
                        }

                        Thread.Sleep(50);
                    }
                    else
                    {
                        cycle = 0;
                        Thread.Sleep(10);
                    }
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
            BridgeDiagnostics.Log("CommandRegistry", "WakeLoop exit");
        }

        private static string GetWindowTitleSafe(IntPtr hwnd, int maxChars = 256)
        {
            try
            {
                var titleBuf = new StringBuilder(maxChars);
                var ok = SendMessageTimeout(hwnd, WM_GETTEXT, new IntPtr(maxChars), titleBuf,
                    SMTO_BLOCK | SMTO_ABORTIFHUNG, 50, out _);
                return ok == IntPtr.Zero ? "" : titleBuf.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static IntPtr FindUnityMainWindow()
        {
            // Unity 6+ uses separate processes: our scripting runtime may be in a different
            // process than the one that owns the editor window. We find the window by matching
            // the project name in the title of UnityContainerWndClass windows.
            string projectName = System.IO.Path.GetFileName(
                Application.dataPath.Replace("/Assets", ""));

            IntPtr found = IntPtr.Zero;
            var classNameBuf = new StringBuilder(256);

            EnumWindows((hwnd, _) =>
            {
                classNameBuf.Clear();
                GetClassName(hwnd, classNameBuf, 256);
                if (classNameBuf.ToString() != "UnityContainerWndClass")
                    return true;

                var title = GetWindowTitleSafe(hwnd, 512);
                if (title.StartsWith(projectName + " -"))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static void TryRefreshUnityHwnd()
        {
            try
            {
                string rawSessionHwnd = SessionState.GetString(SessionKeys.UnityHwnd, "0");
                if (long.TryParse(rawSessionHwnd, out long savedHwnd) && savedHwnd != 0)
                {
                    var candidate = new IntPtr(savedHwnd);
                    if (IsWindow(candidate))
                    {
                        var classBuf = new StringBuilder(256);
                        GetClassName(candidate, classBuf, 256);
                        if (classBuf.ToString() == "UnityContainerWndClass")
                        {
                            _unityWindowHandle = candidate;
                            return;
                        }
                    }
                }

                var found = FindUnityMainWindow();
                if (found != IntPtr.Zero)
                {
                    _unityWindowHandle = found;
                    SessionState.SetString(SessionKeys.UnityHwnd, found.ToInt64().ToString());
                    BridgeDiagnostics.Log("CommandRegistry", $"refreshed hwnd={found}");
                }
                else
                {
                    BridgeDiagnostics.Log("CommandRegistry", "hwnd refresh found no Unity window");
                }
            }
            catch (Exception ex) { BridgeDiagnostics.LogException("CommandRegistry hwnd refresh", ex); }
        }

        /// <summary>
        /// Get the HWND. Background callers must not enumerate windows; that can block while
        /// Unity is reloading. The editor update loop refreshes the handle once Unity is idle.
        /// </summary>
        public static IntPtr GetUnityHwnd()
        {
            if (_unityWindowHandle == IntPtr.Zero &&
                SynchronizationContext.Current == _mainThreadContext &&
                !EditorApplication.isCompiling &&
                !EditorApplication.isUpdating)
            {
                TryRefreshUnityHwnd();
            }
            return _unityWindowHandle;
        }

        /// <summary>
        /// Enumerate windows for a process (diagnostic helper).
        /// </summary>
        public static void EnumProcessWindows(uint pid, StringBuilder sb)
        {
            var classNameBuf = new StringBuilder(256);
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wndPid);
                if (wndPid != pid) return true;

                classNameBuf.Clear();
                GetClassName(hwnd, classNameBuf, 256);
                string className = classNameBuf.ToString();
                bool visible = IsWindowVisible(hwnd);
                sb.AppendLine($"  wnd: {hwnd} class={className} visible={visible}");
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>
        /// Enumerate windows whose title contains a substring (diagnostic helper).
        /// </summary>
        public static void EnumWindowsByTitle(string titleSubstring, StringBuilder sb)
        {
            var classNameBuf = new StringBuilder(256);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                string title = GetWindowTitleSafe(hwnd);
                if (string.IsNullOrEmpty(title)) return true;

                if (title.IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    classNameBuf.Clear();
                    GetClassName(hwnd, classNameBuf, 256);
                    GetWindowThreadProcessId(hwnd, out uint wndPid);
                    bool visible = IsWindowVisible(hwnd);
                    sb.AppendLine($"  wnd: {hwnd} pid={wndPid} class={classNameBuf} visible={visible} title=\"{title}\"");
                }
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>
        /// Diagnostic info about the main thread queue (safe to call from any thread).
        /// </summary>
        public static string GetQueueDiagnostics()
        {
            var sb = new StringBuilder();
            var snapshot = _mainThreadQueue.ToArray();
            int total = snapshot.Length;
            int canceled = snapshot.Count(w => w.CompletionSource.Task.IsCanceled);
            int completed = snapshot.Count(w => w.CompletionSource.Task.IsCompleted);
            sb.AppendLine($"queue: {total} total, {canceled} canceled, {completed} completed");
            sb.AppendLine($"hwnd: {_unityWindowHandle}");
            sb.AppendLine($"mainSyncCtx: {_mainThreadContext?.GetType().Name ?? "NULL"}");
            sb.AppendLine($"isRunning: {_isRunning}");
            sb.AppendLine($"timerTicks: {_timerTickCount}");
            sb.AppendLine($"lastTimerTick: {_lastTimerTick:HH:mm:ss.fff}");
            foreach (var item in snapshot.Where(w => !w.CompletionSource.Task.IsCanceled).Take(5))
            {
                sb.AppendLine($"  pending: {item.Description} (waiting {(DateTime.Now - item.EnqueuedAt).TotalSeconds:F1}s)");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// All registered commands.
        /// </summary>
        public static IReadOnlyDictionary<string, CommandInfo> Commands
        {
            get
            {
                EnsureInitialized();
                return _commands;
            }
        }

        /// <summary>
        /// Initializes the command registry by scanning for [BridgeCommand] attributes.
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;

            BridgeDiagnostics.Log("CommandRegistry", "command scan begin");
            _commands = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                string assemblyName = assembly.GetName().Name;
                // Scan our own assemblies + any assembly that references clibridge4unity.Core
                // This allows external packages to register [BridgeCommand] methods
                if (!assemblyName.StartsWith("clibridge4unity") &&
                    !ReferencesCore(assembly)) continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                        {
                            var attr = method.GetCustomAttribute<BridgeCommandAttribute>();
                            if (attr == null) continue;

                            if (!ValidateMethodSignature(method, attr, out var error))
                            {
                                Debug.LogWarning($"[Bridge] Invalid command '{attr.Name}' on {type.Name}.{method.Name}: {error}");
                                continue;
                            }

                            if (_commands.ContainsKey(attr.Name))
                            {
                                Debug.LogWarning($"[Bridge] Duplicate command '{attr.Name}' on {type.Name}.{method.Name}, ignoring");
                                continue;
                            }

                            _commands[attr.Name] = new CommandInfo
                            {
                                Name = attr.Name,
                                Description = attr.Description,
                                Usage = attr.Usage ?? attr.Name,
                                Category = attr.Category,
                                RequiresMainThread = attr.RequiresMainThread,
                                IsStreaming = attr.Streaming,
                                TimeoutSeconds = attr.TimeoutSeconds > 0 ? attr.TimeoutSeconds
                                    : attr.RequiresMainThread ? 25 : 10,
                                RelatedCommands = attr.RelatedCommands ?? Array.Empty<string>(),
                                Method = method,
                                Instance = method.IsStatic ? null : GetOrCreateInstance(type)
                            };
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Bridge] Error scanning assembly {assembly.FullName}: {ex.Message}");
                }
            }

            sw.Stop();
            _isInitialized = true;
            Debug.Log($"[Bridge] Command registry initialized: {_commands.Count} commands in {sw.ElapsedMilliseconds}ms");
            BridgeDiagnostics.Log("CommandRegistry", $"command scan end, count={_commands.Count}, ms={sw.ElapsedMilliseconds}");
        }

        public static CommandInfo GetCommand(string name)
        {
            EnsureInitialized();
            return _commands.TryGetValue(name, out var cmd) ? cmd : null;
        }

        // Commands that work even with compile errors (diagnostics, non-Unity-code commands)
        private static readonly HashSet<string> CompileErrorBypassCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PING", "HELP", "DIAG", "LOG", "STATUS", "COMPILE", "REFRESH", "DISMISS",
            "PROBE", "STACK_MINIMIZE", "CODE_EXEC", "CODE_EXEC_RETURN"
        };


        public static async Task<string> ExecuteCommand(string name, string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var cmd = GetCommand(name);
            if (cmd == null)
                return Response.Error($"Unknown command: {name}. Use HELP for available commands.");

            // Block commands when there are compile errors (except diagnostic/fix commands)
            if (GetCompileErrors != null && !CompileErrorBypassCommands.Contains(name))
            {
                try
                {
                    string compileErrors = GetCompileErrors();
                    if (compileErrors != null)
                        return Response.Error(compileErrors + "\nFix the errors above, then run COMPILE.");
                }
                catch { }
            }

            // Capture log position before command execution
            // Skip for: LOG itself, lightweight commands, and internal calls (pipe == null)
            long logIdBefore = 0;
            bool captureLogs = pipe != null && name != "LOG" && name != "PING" && name != "HELP" && name != "PROBE" && name != "DIAG" && GetLastLogId != null;
            if (captureLogs)
            {
                try { logIdBefore = GetLastLogId(); }
                catch { captureLogs = false; }
            }

            string response;
            try
            {
                response = await InvokeCommand(cmd, data, pipe, ct);
            }
            catch (Exception ex)
            {
                // Timeouts during domain reload / asset import are expected and noisy —
                // log at warning level so they don't spam the Unity console as errors.
                if (ex is TimeoutException)
                    Debug.LogWarning($"[Bridge] Command '{name}' timed out: {ex.Message}");
                else
                    Debug.LogError($"[Bridge] Command '{name}' failed: {ex}");
                response = Response.Exception(ex);
            }

            // Append any logs that occurred during command execution
            if (captureLogs && response != null && GetLogsSinceFormatted != null)
            {
                try
                {
                    string recentLogs = GetLogsSinceFormatted(logIdBefore, 5);
                    if (recentLogs != null)
                        response = response + "\n" + recentLogs;
                }
                catch { }
            }

            // If this command named or touched USS/UXML/TSS assets, append matching
            // importer diagnostics from Unity's current Console entries.
            if (response != null && GetUiToolkitDiagnosticsForCommand != null)
            {
                try
                {
                    string uiDiagnostics = GetUiToolkitDiagnosticsForCommand(name, data, response);
                    if (uiDiagnostics != null)
                        response = response + "\n" + uiDiagnostics;
                }
                catch { }
            }

            // Shorten all absolute paths in the response ($WORKSPACE, $UNITY, [pkg])
            if (response != null && ShortenResponsePaths != null && !SkipPathShortening)
            {
                try { response = ShortenResponsePaths(response); }
                catch { }
            }
            SkipPathShortening = false;

            // Append "Related:" hint on successful responses when the command declares related commands.
            response = AppendRelatedHint(response, cmd.RelatedCommands);

            return response;
        }

        private static string AppendRelatedHint(string response, string[] related)
        {
            if (response == null || related == null || related.Length == 0) return response;
            if (response.StartsWith("Error:", StringComparison.Ordinal)) return response;
            return response + "\nRelated: " + string.Join(", ", related);
        }

        public static string GetHelp(string filter = null)
        {
            EnsureInitialized();
            filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();

            // HELP <COMMAND> — show detailed help for a single command
            if (filter != null && !filter.Equals("verbose", System.StringComparison.OrdinalIgnoreCase))
            {
                string key = filter.ToUpperInvariant();
                if (_commands.TryGetValue(key, out var cmd))
                {
                    var sb2 = new StringBuilder();
                    sb2.AppendLine($"{cmd.Name} — {cmd.Description}");
                    sb2.AppendLine($"Category: {cmd.Category}");
                    if (cmd.RequiresMainThread) sb2.AppendLine("Requires main thread: yes");
                    if (!string.IsNullOrEmpty(cmd.Usage))
                    {
                        sb2.AppendLine();
                        sb2.AppendLine("Usage:");
                        foreach (var line in cmd.Usage.Split('\n'))
                            sb2.AppendLine($"  {line.TrimEnd()}");
                    }
                    return sb2.ToString().TrimEnd();
                }
                return $"Unknown command: {filter}";
            }

            bool verbose = filter != null && filter.Equals("verbose", System.StringComparison.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            sb.AppendLine("Unity Bridge Commands");
            sb.AppendLine("=====================");
            sb.AppendLine();

            var grouped = _commands.Values
                .OrderBy(c => c.Category)
                .ThenBy(c => c.Name)
                .GroupBy(c => c.Category);

            foreach (var group in grouped)
            {
                sb.AppendLine($"[{group.Key}]");
                foreach (var cmd in group)
                {
                    sb.AppendLine($"  {cmd.Name,-20} {cmd.Description}");
                    if (verbose && !string.IsNullOrEmpty(cmd.Usage) && cmd.Usage != cmd.Name)
                    {
                        foreach (var line in cmd.Usage.Split('\n'))
                            sb.AppendLine($"  {"",-20} {line.TrimEnd()}");
                    }
                }
                sb.AppendLine();
            }

            if (!verbose)
                sb.AppendLine("Use HELP verbose for detailed usage, or HELP <COMMAND> for a specific command.");

            return sb.ToString().TrimEnd();
        }

        private static void EnsureInitialized()
        {
            if (!_isInitialized)
                Initialize();
        }

        private static bool ReferencesCore(System.Reflection.Assembly assembly)
        {
            foreach (var r in assembly.GetReferencedAssemblies())
                if (r.Name == "clibridge4unity.Core") return true;
            return false;
        }

        private static bool ValidateMethodSignature(MethodInfo method, BridgeCommandAttribute attr, out string error)
        {
            error = null;
            var parameters = method.GetParameters();
            var returnType = method.ReturnType;

            if (attr.Streaming)
            {
                if (returnType != typeof(Task))
                {
                    error = "Streaming commands must return Task";
                    return false;
                }
                if (parameters.Length != 3 ||
                    parameters[0].ParameterType != typeof(string) ||
                    parameters[1].ParameterType != typeof(NamedPipeServerStream) ||
                    parameters[2].ParameterType != typeof(CancellationToken))
                {
                    error = "Streaming commands must have signature: Task Method(string data, NamedPipeServerStream pipe, CancellationToken ct)";
                    return false;
                }
                return true;
            }

            if (returnType != typeof(string) && returnType != typeof(Task<string>))
            {
                error = "Commands must return string or Task<string>";
                return false;
            }

            if (parameters.Length > 1)
            {
                error = "Commands can have at most one string parameter";
                return false;
            }

            if (parameters.Length == 1 && parameters[0].ParameterType != typeof(string))
            {
                error = "Command parameter must be string";
                return false;
            }

            return true;
        }

        private static async Task<string> InvokeCommand(CommandInfo cmd, string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var parameters = cmd.Method.GetParameters();

            if (cmd.IsStreaming)
            {
                var streamingTask = (Task)cmd.Method.Invoke(cmd.Instance, new object[] { data, pipe, ct });
                await streamingTask;
                return null;
            }

            object[] args;
            if (parameters.Length == 0)
                args = Array.Empty<object>();
            else
                args = new object[] { data };

            object result;
            if (cmd.RequiresMainThread)
            {
                string desc = data?.Length > 80 ? $"{cmd.Name}|{data.Substring(0, 80)}..." : $"{cmd.Name}|{data}";
                result = await InvokeOnMainThread(() => cmd.Method.Invoke(cmd.Instance, args), desc);
            }
            else
            {
                result = cmd.Method.Invoke(cmd.Instance, args);
            }

            if (result is Task<string> task)
                return await task;

            return (string)result;
        }

        /// <summary>
        /// Public utility for commands to invoke work on the Unity main thread.
        /// If already on the main thread, runs directly (prevents deadlock).
        /// </summary>
        public static async Task<T> RunOnMainThreadAsync<T>(Func<T> action, string description = null)
        {
            // Deadlock guard: if we're already on the main thread, just run it
            if (_mainThreadContext != null &&
                SynchronizationContext.Current == _mainThreadContext)
            {
                return action();
            }
            return await InvokeOnMainThread(action, description ?? "RunOnMainThreadAsync");
        }

        /// <summary>
        /// Detect visible windows belonging to Unity's process (excluding the main editor window).
        /// These are typically dialogs, popups, or floating panels that may block the main thread.
        /// Safe to call from any thread.
        /// </summary>
        public static List<string> DetectOpenDialogs()
        {
            var dialogs = new List<string>();
            var hwnd = GetUnityHwnd();
            if (hwnd == IntPtr.Zero) return dialogs;

            GetWindowThreadProcessId(hwnd, out uint unityPid);
            if (unityPid == 0) return dialogs;

            EnumWindows((wnd, _) =>
            {
                if (wnd == hwnd) return true;
                if (!IsWindowVisible(wnd)) return true;

                GetWindowThreadProcessId(wnd, out uint pid);
                if (pid != unityPid) return true;

                // Skip undocked editor panels (same window class as main window)
                var classBuf = new StringBuilder(256);
                GetClassName(wnd, classBuf, 256);
                if (classBuf.ToString() == "UnityContainerWndClass") return true;

                string title = GetWindowTitleSafe(wnd);
                if (!string.IsNullOrEmpty(title))
                    dialogs.Add(title);

                return true;
            }, IntPtr.Zero);
            return dialogs;
        }

        /// <summary>
        /// Build a detailed status report about why the main thread is unavailable.
        /// Includes heartbeat, open windows, queue state, and recommendations.
        /// Safe to call from any thread.
        /// </summary>
        private static string BuildBusyReport(double staleness, string commandDescription = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Error: Unity main thread is busy (last heartbeat {staleness:F1}s ago).");

            // Queue state — is another command already running?
            var snapshot = _mainThreadQueue.ToArray();
            var pending = snapshot.Where(w => !w.CompletionSource.Task.IsCanceled && !w.CompletionSource.Task.IsCompleted).ToArray();
            if (pending.Length > 0)
            {
                sb.AppendLine($"Commands waiting on main thread ({pending.Length}):");
                foreach (var item in pending)
                    sb.AppendLine($"  - {item.Description} (waiting {(DateTime.Now - item.EnqueuedAt).TotalSeconds:F1}s)");
            }
            else
            {
                sb.AppendLine("No commands were running — main thread is blocked by Unity itself.");
            }

            // Open windows — detect dialogs
            bool hasDialogs = false;
            try
            {
                var dialogs = DetectOpenDialogs();
                hasDialogs = dialogs.Count > 0;
                if (hasDialogs)
                {
                    sb.AppendLine($"Open windows ({dialogs.Count}):");
                    foreach (var title in dialogs)
                    {
                        sb.Append($"  - \"{title}\"");
                        // Classify common dialog types
                        var lower = title.ToLowerInvariant();
                        if (lower.Contains("import")) sb.Append(" [asset import]");
                        else if (lower.Contains("package manager")) sb.Append(" [package manager]");
                        else if (lower.Contains("save")) sb.Append(" [save dialog]");
                        else if (lower.Contains("build")) sb.Append(" [build dialog]");
                        else if (lower.Contains("error") || lower.Contains("exception")) sb.Append(" [error dialog]");
                        else if (lower.Contains("progress")) sb.Append(" [progress bar]");
                        else if (lower.Contains("compil")) sb.Append(" [compilation]");
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine("No dialog windows detected — Unity is likely busy with a background operation (asset import, shader compile, etc).");
                }
            }
            catch { sb.AppendLine("Could not enumerate windows."); }

            // Recommendations
            sb.AppendLine("Recommendations:");
            if (hasDialogs)
                sb.AppendLine("  - Run DISMISS to close dialog windows, then retry.");
            if (staleness > 10)
                sb.AppendLine("  - Unity may be frozen. Ask user to check Unity Editor.");
            else
                sb.AppendLine("  - Wait a few seconds and retry the command.");
            sb.Append("  - Run DIAG for full diagnostics (works even when main thread is blocked).");
            return sb.ToString();
        }

        /// <summary>
        /// Returns how many seconds since the last main thread tick, or -1 if no ticks yet.
        /// Safe to call from any thread.
        /// </summary>
        public static double GetHeartbeatStaleness()
        {
            if (_timerTickCount == 0) return -1;
            return (DateTime.Now - _lastTimerTick).TotalSeconds;
        }

        /// <summary>
        /// Returns heartbeat diagnostics. Safe to call from any thread.
        /// </summary>
        public static string GetHeartbeatInfo()
        {
            var sb = new StringBuilder();
            if (_timerTickCount == 0)
            {
                sb.AppendLine("heartbeat: no ticks yet (Unity may still be initializing)");
            }
            else
            {
                var staleness = (DateTime.Now - _lastTimerTick).TotalSeconds;
                sb.AppendLine($"heartbeat: {staleness:F1}s since last main thread tick");
                sb.AppendLine($"mainThreadResponsive: {(staleness < 0.5 ? "yes" : staleness < 5 ? "slow" : "no")}");
            }
            sb.AppendLine($"timerTicks: {_timerTickCount}");
            sb.AppendLine($"lastTimerTick: {_lastTimerTick:HH:mm:ss.fff}");
            var snapshot = _mainThreadQueue.ToArray();
            int pendingCount = snapshot.Count(w => !w.CompletionSource.Task.IsCanceled && !w.CompletionSource.Task.IsCompleted);
            if (pendingCount > 0)
                sb.AppendLine($"pendingMainThreadWork: {pendingCount}");

            try
            {
                var dialogs = DetectOpenDialogs();
                if (dialogs.Count > 0)
                {
                    sb.AppendLine($"openDialogs: {dialogs.Count}");
                    foreach (var title in dialogs)
                        sb.AppendLine($"  - {title}");
                }
            }
            catch { }

            return sb.ToString().TrimEnd();
        }

        private static async Task<T> InvokeOnMainThread<T>(Func<T> action, string description = null)
        {
            var work = new MainThreadWork
            {
                Action = () => action(),
                CompletionSource = new TaskCompletionSource<object>(),
                Description = description ?? "unknown",
                EnqueuedAt = DateTime.Now
            };

            _mainThreadQueue.Enqueue(work);
            BridgeDiagnostics.Log("CommandRegistry", $"main-thread work queued: {work.Description}");

            // Poll until the command-level timeout wins. The pipe-level heartbeat keeps
            // clients alive while Unity is legitimately busy during play-mode transitions,
            // reloads, imports, or long editor callbacks.
            for (int elapsed = 0; elapsed < 30000; elapsed += 500)
            {
                var done = await Task.WhenAny(work.CompletionSource.Task, Task.Delay(500));
                if (done == work.CompletionSource.Task)
                    return (T)(await work.CompletionSource.Task);
            }

            // 30s hard limit — something is genuinely stuck
            work.CompletionSource.TrySetCanceled();
            BridgeDiagnostics.Log("CommandRegistry", $"main-thread work hard-timeout: {work.Description}");
            throw new TimeoutException(BuildBusyReport(
                _timerTickCount > 0 ? (DateTime.Now - _lastTimerTick).TotalSeconds : 30, work.Description));
        }

        private static readonly Dictionary<Type, object> _instances = new Dictionary<Type, object>();

        private static object GetOrCreateInstance(Type type)
        {
            if (_instances.TryGetValue(type, out var instance))
                return instance;

            instance = Activator.CreateInstance(type);
            _instances[type] = instance;
            return instance;
        }
    }
}
