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
        public bool Streaming { get; set; }
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
        private const uint WM_NULL = 0x0000;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_PAINT = 0x000F;
        private static IntPtr _unityWindowHandle;

        // Win32 timer for reliable main thread processing even when Unity is in background.
        // Unlike EditorApplication.update or SynchronizationContext.Post, this fires via
        // DispatchMessage in the Win32 message pump — which runs even when Unity is idle/background.
        private delegate void TimerProc(IntPtr hWnd, uint uMsg, UIntPtr nIDEvent, uint dwTime);
        [DllImport("user32.dll")]
        private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, TimerProc lpTimerFunc);
        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);
        private static TimerProc _timerCallback; // prevent GC of the delegate
        private static UIntPtr _timerId;
        private static long _timerTickCount; // increments on each timer tick for diagnostics
        private static DateTime _lastTimerTick;

        private class MainThreadWork
        {
            public Func<object> Action;
            public TaskCompletionSource<object> CompletionSource;
            public string Description;
            public DateTime EnqueuedAt;
        }

        // Captured on main thread for SynchronizationContext.Post
        private static SynchronizationContext _mainThreadContext;

        static CommandRegistry()
        {
            // Don't initialize in batch mode (AssetImportWorker processes)
            if (Application.isBatchMode) return;

            // Capture main thread context + window handle
            _mainThreadContext = SynchronizationContext.Current;

            // Restore HWND from SessionState first (survives domain reloads)
            // Note: Unity 6+ runs scripting in a separate process from the editor window,
            // so we validate by checking if the window still exists and has the right class name.
            string rawSessionHwnd = SessionState.GetString(SessionKeys.UnityHwnd, "0");
            long savedHwnd = 0;
            if (long.TryParse(rawSessionHwnd, out savedHwnd) && savedHwnd != 0)
            {
                var candidate = new IntPtr(savedHwnd);
                var classBuf = new StringBuilder(256);
                GetClassName(candidate, classBuf, 256);
                if (classBuf.ToString() == "UnityContainerWndClass")
                    _unityWindowHandle = candidate;
            }

            // If no valid HWND, discover it by matching project name in window title
            if (_unityWindowHandle == IntPtr.Zero)
            {
                _unityWindowHandle = FindUnityMainWindow();
                if (_unityWindowHandle != IntPtr.Zero)
                    SessionState.SetString(SessionKeys.UnityHwnd, _unityWindowHandle.ToInt64().ToString());
                else
                    Debug.LogWarning($"[Bridge] FindUnityMainWindow returned 0! SessionState raw='{rawSessionHwnd}'");
            }

            // Three redundant main-thread processing paths:
            // 1. EditorApplication.update - fires when Unity's editor loop ticks
            EditorApplication.update += ProcessAllPendingWork;

            // 2. Win32 timer - fires via DispatchMessage even when Unity is in background/idle.
            //    This is the most reliable path because it doesn't depend on Unity's editor loop.
            _timerCallback = OnTimerTick;
            _timerId = SetTimer(IntPtr.Zero, UIntPtr.Zero, 50, _timerCallback);

            // 3. Background thread that posts via SynchronizationContext AND sends wake messages
            var wakeThread = new Thread(WakeLoop)
            {
                Name = "Bridge Wake Thread",
                IsBackground = true
            };
            wakeThread.Start();

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                _isRunning = false;
                EditorApplication.update -= ProcessAllPendingWork;
                // Kill the Win32 timer
                if (_timerId != UIntPtr.Zero)
                {
                    KillTimer(IntPtr.Zero, _timerId);
                    _timerId = UIntPtr.Zero;
                }
                // Save HWND for next domain reload
                if (_unityWindowHandle != IntPtr.Zero)
                    SessionState.SetString(SessionKeys.UnityHwnd, _unityWindowHandle.ToInt64().ToString());
            };

            Debug.Log($"[Bridge] CommandRegistry init - HWND: {_unityWindowHandle}, SyncCtx: {_mainThreadContext?.GetType().Name}");
        }

        /// <summary>
        /// Win32 timer callback — fires via DispatchMessage even when Unity is backgrounded.
        /// This is the most reliable way to process main thread work when Unity doesn't have focus.
        /// </summary>
        private static void OnTimerTick(IntPtr hWnd, uint uMsg, UIntPtr nIDEvent, uint dwTime)
        {
            _timerTickCount++;
            _lastTimerTick = DateTime.Now;
            if (!_mainThreadQueue.IsEmpty)
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

                try
                {
                    var result = work.Action();
                    work.CompletionSource.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Bridge] Main thread action failed: {ex}");
                    work.CompletionSource.TrySetException(ex);
                }
            }
        }

        /// <summary>
        /// Background thread that posts work via SynchronizationContext AND
        /// sends Win32 messages to wake Unity's editor loop.
        /// </summary>
        private static void WakeLoop()
        {
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

                        var hwnd = GetUnityHwnd();
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
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private static IntPtr FindUnityMainWindow()
        {
            // Unity 6+ uses separate processes: our scripting runtime may be in a different
            // process than the one that owns the editor window. We find the window by matching
            // the project name in the title of UnityContainerWndClass windows.
            string projectName = System.IO.Path.GetFileName(
                Application.dataPath.Replace("/Assets", ""));

            IntPtr found = IntPtr.Zero;
            var classNameBuf = new StringBuilder(256);
            var titleBuf = new StringBuilder(512);

            EnumWindows((hwnd, _) =>
            {
                classNameBuf.Clear();
                GetClassName(hwnd, classNameBuf, 256);
                if (classNameBuf.ToString() != "UnityContainerWndClass")
                    return true;

                titleBuf.Clear();
                GetWindowText(hwnd, titleBuf, 512);
                if (titleBuf.ToString().StartsWith(projectName + " -"))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Get the HWND, re-discovering if needed (it can be zero at init time when backgrounded).
        /// NOTE: Do NOT call SessionState from here — this is called from background threads.
        /// </summary>
        public static IntPtr GetUnityHwnd()
        {
            if (_unityWindowHandle == IntPtr.Zero)
            {
                _unityWindowHandle = FindUnityMainWindow();
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

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        /// <summary>
        /// Enumerate windows whose title contains a substring (diagnostic helper).
        /// </summary>
        public static void EnumWindowsByTitle(string titleSubstring, StringBuilder sb)
        {
            var classNameBuf = new StringBuilder(256);
            var titleBuf = new StringBuilder(256);
            EnumWindows((hwnd, _) =>
            {
                titleBuf.Clear();
                GetWindowText(hwnd, titleBuf, 256);
                string title = titleBuf.ToString();
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

            _commands = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                string assemblyName = assembly.GetName().Name;
                if (!assemblyName.StartsWith("clibridge4unity")) continue;

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
                                Streaming = attr.Streaming,
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
        }

        public static CommandInfo GetCommand(string name)
        {
            EnsureInitialized();
            return _commands.TryGetValue(name, out var cmd) ? cmd : null;
        }

        public static async Task<string> ExecuteCommand(string name, string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var cmd = GetCommand(name);
            if (cmd == null)
                return Response.Error($"Unknown command: {name}. Use HELP for available commands.");

            // Capture log position before command execution (skip for LOG command itself)
            long logIdBefore = 0;
            bool captureLogs = name != "LOG" && name != "PING" && name != "HELP" && name != "PROBE" && name != "DIAG" && GetLastLogId != null;
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
                Debug.LogError($"[Bridge] Command '{name}' failed: {ex}");
                response = Response.Exception(ex);
            }

            // Append any logs that occurred during command execution
            if (captureLogs && response != null && GetLogsSinceFormatted != null)
            {
                try
                {
                    string recentLogs = GetLogsSinceFormatted(logIdBefore, 20);
                    if (recentLogs != null)
                        response = response + "\n" + recentLogs;
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

            return response;
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

            if (cmd.Streaming)
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
        /// </summary>
        public static async Task<T> RunOnMainThreadAsync<T>(Func<T> action, string description = null)
        {
            return await InvokeOnMainThread(action, description ?? "RunOnMainThreadAsync");
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

            var completedTask = await Task.WhenAny(
                work.CompletionSource.Task,
                Task.Delay(25000)
            );

            if (completedTask != work.CompletionSource.Task)
            {
                work.CompletionSource.TrySetCanceled();
                var sb = new StringBuilder();
                sb.AppendLine($"Main thread timed out (25s).");
                sb.AppendLine($"Timed-out command: {work.Description} (queued {(DateTime.Now - work.EnqueuedAt).TotalSeconds:F1}s ago)");

                var snapshot = _mainThreadQueue.ToArray();
                var pending = snapshot.Where(w => !w.CompletionSource.Task.IsCanceled).ToArray();
                if (pending.Length > 0)
                {
                    sb.AppendLine($"Pending queue ({pending.Length} items):");
                    foreach (var item in pending)
                        sb.AppendLine($"  - {item.Description} (waiting {(DateTime.Now - item.EnqueuedAt).TotalSeconds:F1}s)");
                }
                else
                {
                    sb.AppendLine("Queue is empty (main thread may be stuck executing a previous command).");
                }
                sb.AppendLine($"HWND: {_unityWindowHandle}");
                sb.Append("Try: DISMISS (close dialogs), COMPILE (force assembly reload to clear stuck state), or ask user to foreground Unity.");
                throw new TimeoutException(sb.ToString());
            }

            var result = await work.CompletionSource.Task;
            return (T)result;
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
