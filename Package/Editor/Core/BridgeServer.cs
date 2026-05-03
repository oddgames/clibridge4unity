using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
#if UNITY_EDITOR_WIN
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Named pipe server for Unity Console Bridge.
    /// Handles communication between console client and Unity Editor.
    /// Persists pending commands across domain reloads for seamless recovery.
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeServer
    {
        public const string Version = "1.1.11";

        private static CancellationTokenSource serverCts;
        private static readonly object serverLock = new object();
        private static readonly List<NamedPipeServerStream> activePipeServers = new List<NamedPipeServerStream>();
        private static string pipeName;
        private static volatile bool isStopping;

        // Serializes all command execution: one command at a time, brief idle between each.
        private static readonly SemaphoreSlim _commandSlot = new SemaphoreSlim(1, 1);
        private const int CommandIdleMs = 100;
        private const int ListenerCount = 8;

        // Main thread context for Unity API calls
        private static SynchronizationContext mainThreadContext;

        // Compilation tracking
        private static DateTime lastCompileRequestTime;
        private static DateTime lastCompileCompleteTime;

        private static readonly DateTime _staticCtorTime;

        static BridgeServer()
        {
            _staticCtorTime = DateTime.UtcNow;
            BridgeDiagnostics.Log("BridgeServer", "static ctor");
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.update += InitOnFirstTick;
        }

        private static void OnAfterAssemblyReload()
        {
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            var lag = (DateTime.UtcNow - _staticCtorTime).TotalSeconds;
            BridgeDiagnostics.Log("BridgeServer", $"afterAssemblyReload, lag from static ctor: {lag:F2}s");
        }

        private static void InitOnFirstTick()
        {
            var lag = (DateTime.UtcNow - _staticCtorTime).TotalSeconds;
            BridgeDiagnostics.Log("BridgeServer", $"InitOnFirstTick, lag from static ctor: {lag:F2}s");
            EditorApplication.update -= InitOnFirstTick;
            Initialize();
        }

        private static void Initialize()
        {
            BridgeDiagnostics.Log("BridgeServer", "Initialize enter");
            if (Application.isBatchMode)
            {
                BridgeDiagnostics.Log("BridgeServer", "batch mode - server disabled");
                return;
            }

            AssemblyReloadEvents.beforeAssemblyReload += StopServerImmediately;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            BridgeDiagnostics.Log("BridgeServer", "handlers registered");

            mainThreadContext = SynchronizationContext.Current;
            BridgeDiagnostics.Log("BridgeServer", $"sync context: {mainThreadContext?.GetType().Name ?? "null"}");

            Application.runInBackground = true;

            // Enable console timestamps so log entries have time data
            try
            {
                var logEntriesType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                var setFlag = logEntriesType?.GetMethod("SetConsoleFlag",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                setFlag?.Invoke(null, new object[] { 1 << 10, true });
            }
            catch { }

            BridgeDiagnostics.Log("BridgeServer", "registered reload/compilation handlers");

            // Check if we just came back from a compilation (domain reload completed)
            long requestTicks = 0, finishedTicks = 0;
            long.TryParse(SessionState.GetString(SessionKeys.LastCompileRequest, "0"), out requestTicks);
            long.TryParse(SessionState.GetString(SessionKeys.LastCompileTime, "0"), out finishedTicks);

            if (requestTicks > finishedTicks)
            {
                lastCompileCompleteTime = DateTime.Now;
                SessionState.SetString(SessionKeys.LastCompileTime, lastCompileCompleteTime.Ticks.ToString());

                int duration = (int)(lastCompileCompleteTime - new DateTime(requestTicks)).TotalSeconds;
                if (duration > 0 && duration < 600)
                {
                    RecordCompileDuration(duration);
                    BridgeDiagnostics.Log("BridgeServer", $"domain reload completed after compile request, duration={duration}s");
                }
            }
            else if (finishedTicks > 0)
            {
                lastCompileCompleteTime = new DateTime(finishedTicks);
                BridgeDiagnostics.Log("BridgeServer", $"restored last compile complete time: {lastCompileCompleteTime:O}");
            }

            pipeName = GeneratePipeName();
            BridgeDiagnostics.Log("BridgeServer", $"pipe name: {pipeName}");
            BridgeDiagnostics.Log("BridgeServer", "StartServer begin");
            StartServer();
            BridgeDiagnostics.Log("BridgeServer", "Initialize exit");
        }

        /// <summary>
        /// Invokes an action on the Unity main thread and waits for completion.
        /// </summary>
        private static Task<T> InvokeOnMainThread<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>();
            mainThreadContext.Post(_ =>
            {
                try { tcs.SetResult(action()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }

        /// <summary>
        /// Invokes an async action on the Unity main thread and waits for completion.
        /// </summary>
        private static Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> action)
        {
            var tcs = new TaskCompletionSource<T>();
            mainThreadContext.Post(async _ =>
            {
                try { tcs.SetResult(await action()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }


        private static string GeneratePipeName()
        {
            // Normalize path: lowercase, backslashes, no trailing slash (must match console client exactly)
            string projectPath = Application.dataPath.Replace("/Assets", "").ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            int hash = GetDeterministicHashCode(projectPath);
            return $"UnityBridge_{Environment.UserName}_{hash:X8}";
        }

        /// <summary>
        /// Deterministic hash that works identically across .NET runtimes (Unity Mono and .NET 8).
        /// This MUST match the algorithm in ConsoleUnityBridge/UnityBridgeClient.cs exactly.
        /// </summary>
        public static int GetDeterministicHashCode(string str)
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

        private static void StartServer()
        {
            lock (serverLock)
            {
                if (serverCts != null) return;

                isStopping = false;
                serverCts = new CancellationTokenSource();
            }

            // Run multiple concurrent server loops to handle simultaneous connections.
            // Commands still execute serially via _commandSlot; extra listeners prevent
            // short client-side connect timeouts during bursts of queued commands.
            for (int i = 0; i < ListenerCount; i++)
            {
                int listenerId = i + 1;
                Task.Run(() => ServerLoop(listenerId, serverCts.Token));
            }
            Debug.Log($"[Bridge] Server started: {pipeName} ({ListenerCount} listeners)");
            BridgeDiagnostics.Log("BridgeServer", $"server started with {ListenerCount} listeners");
        }

        private static void StopServerImmediately()
        {
            BridgeDiagnostics.Log("BridgeServer", "StopServerImmediately enter");
            CancellationTokenSource cts = null;
            NamedPipeServerStream[] pipes;

            lock (serverLock)
            {
                if (serverCts == null && activePipeServers.Count == 0) return;

                isStopping = true;
                cts = serverCts;
                serverCts = null;
                pipes = activePipeServers.ToArray();
                activePipeServers.Clear();
            }

            try
            {
                cts?.Cancel();
            }
            catch { }

            foreach (var pipe in pipes)
            {
                try { pipe.Dispose(); } catch { }
            }
            BridgeDiagnostics.Log("BridgeServer", $"StopServerImmediately exit, disposedPipes={pipes.Length}");
        }

        private static void OnCompilationStarted(object ctx)
        {
            lastCompileRequestTime = DateTime.Now;
            SessionState.SetString(SessionKeys.LastCompileRequest, lastCompileRequestTime.Ticks.ToString());
            BridgeDiagnostics.Log("BridgeServer", $"compilation started ctx={ctx}");
        }

        private static void RecordCompileDuration(int seconds)
        {
            string existing = SessionState.GetString("Bridge_CompileDurations", "");
            var times = new List<int>();
            if (!string.IsNullOrEmpty(existing))
            {
                foreach (var s in existing.Split(','))
                    if (int.TryParse(s.Trim(), out var v) && v > 0) times.Add(v);
            }
            times.Add(seconds);
            while (times.Count > 10) times.RemoveAt(0);
            SessionState.SetString("Bridge_CompileDurations", string.Join(",", times));
        }

        /// <summary>
        /// Returns compile time stats: average, last, count. Null if no data.
        /// </summary>
        public static (int avg, int last, int count)? GetCompileTimeStats()
        {
            string data = SessionState.GetString("Bridge_CompileDurations", "");
            if (string.IsNullOrEmpty(data)) return null;
            var times = new List<int>();
            foreach (var s in data.Split(','))
                if (int.TryParse(s.Trim(), out var v) && v > 0) times.Add(v);
            if (times.Count == 0) return null;
            int sum = 0;
            foreach (var t in times) sum += t;
            return (sum / times.Count, times[times.Count - 1], times.Count);
        }

        private static void OnCompilationFinished(object ctx)
        {
            // Don't set lastCompileTime here — domain reload hasn't happened yet.
            // The [InitializeOnLoad] constructor sets it after domain reload completes.
            BridgeDiagnostics.Log("BridgeServer", $"compilation finished ctx={ctx}");
        }

        private static async Task ServerLoop(int listenerId, CancellationToken ct)
        {
            BridgeDiagnostics.Log("BridgeServer", $"listener {listenerId} loop enter");
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipeServer(pipeName);

                    lock (serverLock)
                    {
                        if (isStopping || ct.IsCancellationRequested)
                        {
                            pipe.Dispose();
                            break;
                        }
                        activePipeServers.Add(pipe);
                    }

                    BridgeDiagnostics.Log("BridgeServer", $"listener {listenerId} waiting for connection");
                    await pipe.WaitForConnectionAsync(ct);
                    BridgeDiagnostics.Log("BridgeServer", $"listener {listenerId} connected");

                    // Keep listener capacity available while queued commands wait for
                    // the serialized command slot. The connected pipe stays tracked in
                    // activePipeServers and is removed by HandleClientAndDispose.
                    var connectedPipe = pipe;
                    pipe = null;
                    _ = Task.Run(() => HandleClientAndDispose(connectedPipe, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    BridgeDiagnostics.Log("BridgeServer", $"listener {listenerId} canceled");
                    break;
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested || isStopping)
                {
                    BridgeDiagnostics.Log("BridgeServer", $"listener {listenerId} disposed during shutdown");
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        BridgeDiagnostics.LogException($"BridgeServer listener {listenerId}", ex);
                        Debug.LogError($"[Bridge] Error: {ex.Message}");
                        await Task.Delay(1000, ct);
                    }
                }
                finally
                {
                    if (pipe != null)
                    {
                        lock (serverLock) activePipeServers.Remove(pipe);
                        try { pipe.Dispose(); } catch { }
                    }
                }
            }
            BridgeDiagnostics.Log("BridgeServer", $"listener {listenerId} loop exit");
        }

        private static NamedPipeServerStream CreatePipeServer(string name)
        {
#if UNITY_EDITOR_WIN
            var allowedSids = LoadConfiguredPipeSids();
            if (allowedSids.Count > 0)
            {
                var securedPipe = TryCreateSecuredPipeServer(name, allowedSids);
                if (securedPipe != null)
                    return securedPipe;
            }
#endif
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            return pipe;
        }

#if UNITY_EDITOR_WIN
        private const int GenericAll = unchecked((int)0x10000000);
        private const int GenericReadWrite = unchecked((int)0xC0000000);
        private const uint TokenQuery = 0x0008;
        private const uint PipeAccessDuplex = 0x00000003;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint PipeTypeByte = 0x00000000;
        private const uint PipeReadModeByte = 0x00000000;
        private const uint PipeWait = 0x00000000;
        private const uint PipeUnlimitedInstances = 255;

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(
            string lpName,
            uint dwOpenMode,
            uint dwPipeMode,
            uint nMaxInstances,
            uint nOutBufferSize,
            uint nInBufferSize,
            uint nDefaultTimeOut,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor,
            uint stringSdRevision,
            out IntPtr securityDescriptor,
            out uint securityDescriptorSize);

        private static NamedPipeServerStream TryCreateSecuredPipeServer(string name, List<SecurityIdentifier> allowedSids)
        {
            try
            {
                string sddl = BuildPipeSecuritySddl(allowedSids);
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var securityDescriptor, out _))
                {
                    int error = Marshal.GetLastWin32Error();
                    BridgeDiagnostics.Log("BridgeServer", $"ConvertStringSecurityDescriptor failed: {error}");
                    return null;
                }

                try
                {
                    var attributes = new SECURITY_ATTRIBUTES
                    {
                        nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                        lpSecurityDescriptor = securityDescriptor,
                        bInheritHandle = false
                    };

                    var handle = CreateNamedPipe(
                        @"\\.\pipe\" + name,
                        PipeAccessDuplex | FileFlagOverlapped,
                        PipeTypeByte | PipeReadModeByte | PipeWait,
                        PipeUnlimitedInstances,
                        0,
                        0,
                        0,
                        ref attributes);

                    if (handle == null || handle.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        BridgeDiagnostics.Log("BridgeServer", $"CreateNamedPipe secured failed: {error}");
                        handle?.Dispose();
                        return null;
                    }

                    BridgeDiagnostics.Log("BridgeServer", "secured pipe created");
                    return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
                }
                finally
                {
                    LocalFree(securityDescriptor);
                }
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("BridgeServer secured pipe", ex);
                return null;
            }
        }

        private static string BuildPipeSecuritySddl(List<SecurityIdentifier> allowedSids)
        {
            if (!TryGetCurrentProcessSid(out var owner))
                throw new InvalidOperationException("Could not resolve Unity process SID");

            var sb = new StringBuilder();
            sb.Append("D:");
            sb.Append("(A;;GA;;;");
            sb.Append(owner.Value);
            sb.Append(")");

            foreach (var sid in allowedSids)
            {
                sb.Append("(A;;GA;;;");
                sb.Append(sid.Value);
                sb.Append(")");
                BridgeDiagnostics.Log("BridgeServer", $"pipe ACL SID allowed: {sid.Value}");
            }

            return sb.ToString();
        }

        private static List<SecurityIdentifier> LoadConfiguredPipeSids()
        {
            var sids = new List<SecurityIdentifier>();
            try
            {
                string projectRoot = Application.dataPath.Replace("/Assets", "");
                string configPath = Path.Combine(projectRoot, "ProjectSettings", "clibridge4unity.json");
                if (!File.Exists(configPath))
                    return sids;

                string json = File.ReadAllText(configPath);
                foreach (var sidValue in ReadJsonStringArray(json, "pipeUserSids"))
                {
                    try
                    {
                        var sid = new SecurityIdentifier(sidValue);
                        if (!sids.Any(existing => existing.Value == sid.Value))
                            sids.Add(sid);
                    }
                    catch (Exception ex)
                    {
                        BridgeDiagnostics.LogException($"BridgeServer pipe SID {sidValue}", ex);
                    }
                }

                foreach (var user in ReadJsonStringArray(json, "pipeUsers"))
                {
                    if (TryResolveUserSid(user, out var sid) && !sids.Any(existing => existing.Value == sid.Value))
                        sids.Add(sid);
                }
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("BridgeServer load pipe config", ex);
            }
            return sids;
        }

        private static List<string> ReadJsonStringArray(string json, string key)
        {
            var values = new List<string>();
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    json,
                    "\"" + key + "\"\\s*:\\s*\\[(.*?)\\]",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!match.Success)
                    return values;

                foreach (System.Text.RegularExpressions.Match item in
                    System.Text.RegularExpressions.Regex.Matches(match.Groups[1].Value, "\"((?:\\\\.|[^\"])*)\""))
                {
                    string user = item.Groups[1].Value
                        .Replace("\\\"", "\"")
                        .Replace("\\\\", "\\")
                        .Trim();
                    if (!string.IsNullOrEmpty(user) && !values.Contains(user, StringComparer.OrdinalIgnoreCase))
                        values.Add(user);
                }
            }
            catch { }
            return values;
        }

        private static bool TryResolveUserSid(string user, out SecurityIdentifier sid)
        {
            sid = null;
            try
            {
                sid = (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier));
                return true;
            }
            catch { }

            try
            {
                sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, user).Translate(typeof(SecurityIdentifier));
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetCurrentProcessSid(out SecurityIdentifier sid)
        {
            sid = null;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                    return false;

                GetTokenInformation(token, 1, IntPtr.Zero, 0, out int length);
                if (length <= 0)
                    return false;

                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, 1, buffer, length, out length))
                    return false;

                IntPtr sidPtr = Marshal.ReadIntPtr(buffer);
                sid = new SecurityIdentifier(sidPtr);
                return true;
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("BridgeServer process SID", ex);
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
#endif

        private static async Task HandleClientAndDispose(NamedPipeServerStream pipe, CancellationToken ct)
        {
            try
            {
                await HandleClient(pipe, ct);
            }
            finally
            {
                lock (serverLock) activePipeServers.Remove(pipe);
                try { pipe.Dispose(); } catch { }
            }
        }

        private static async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
        {
            string command = null;
            string data = null;
            BridgeDiagnostics.Log("BridgeServer", "HandleClient enter");

            try
            {
                var buffer = new byte[8192];
                var dataBuilder = new StringBuilder();

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    int bytesRead = await pipe.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0) break;

                    dataBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    if (dataBuilder.ToString().Contains("\n"))
                        break;
                }

                string rawData = dataBuilder.ToString().Trim();
                if (string.IsNullOrEmpty(rawData)) return;
                BridgeDiagnostics.Log("BridgeServer", $"raw request received, chars={rawData.Length}");

                // Plain text format: "COMMAND|data", "COMMAND data", or just "COMMAND"
                int pipeIdx = rawData.IndexOf('|');
                if (pipeIdx >= 0)
                {
                    command = rawData.Substring(0, pipeIdx).Trim().ToUpper();
                    data = rawData.Substring(pipeIdx + 1);
                }
                else
                {
                    // Support space-separated: "COMMAND data" (first token is command)
                    int spaceIdx = rawData.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        string firstToken = rawData.Substring(0, spaceIdx).Trim().ToUpper();
                        // Only split on space if the first token looks like a known command
                        if (CommandRegistry.GetCommand(firstToken) != null)
                        {
                            command = firstToken;
                            data = rawData.Substring(spaceIdx + 1).TrimStart();
                        }
                        else
                        {
                            command = rawData.Trim().ToUpper();
                            data = "";
                        }
                    }
                    else
                    {
                        command = rawData.Trim().ToUpper();
                        data = "";
                    }
                }

                BridgeDiagnostics.Log("BridgeServer", $"command parsed: {command}, dataChars={data?.Length ?? 0}");

                // Timeout from attribute — no hardcoded fallbacks
                var cmdInfo = CommandRegistry.GetCommand(command);
                int timeoutSec = cmdInfo?.TimeoutSeconds ?? 10;
                int timeoutMs = timeoutSec * 1000;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);

                // Send timeout hint so CLI can set its read timeout dynamically.
                // Pipe writes from the heartbeat task and main flow share a lock to avoid
                // interleaved bytes corrupting the stream.
                var pipeWriteLock = new SemaphoreSlim(1, 1);
                var hintBytes = Encoding.UTF8.GetBytes($"__timeout:{timeoutSec}\n");
                await pipeWriteLock.WaitAsync(ct);
                try
                {
                    await pipe.WriteAsync(hintBytes, 0, hintBytes.Length, ct);
                    await pipe.FlushAsync();
                }
                finally { pipeWriteLock.Release(); }

                // Heartbeat task: emits "__hb:<staleness>\n" every second while the command runs.
                // CLI uses this to detect stalls — if no bytes (heartbeat or response) arrive
                // within its idle threshold, it bails instead of waiting for the full timeout.
                var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeatTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!heartbeatCts.IsCancellationRequested)
                        {
                            await Task.Delay(1000, heartbeatCts.Token);
                            if (heartbeatCts.IsCancellationRequested) break;
                            double staleness = CommandRegistry.GetHeartbeatStaleness();
                            byte[] hb = Encoding.UTF8.GetBytes($"__hb:{staleness:F1}\n");
                            await pipeWriteLock.WaitAsync(heartbeatCts.Token);
                            try
                            {
                                if (pipe.IsConnected)
                                {
                                    await pipe.WriteAsync(hb, 0, hb.Length, heartbeatCts.Token);
                                    await pipe.FlushAsync(heartbeatCts.Token);
                                }
                            }
                            finally { pipeWriteLock.Release(); }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* pipe closed or write failed — main flow will surface the error */ }
                }, heartbeatCts.Token);

                string response;
                var commandToken = timeoutCts.Token;
                bool slotAcquired = false;
                try
                {
                    BridgeDiagnostics.Log("BridgeServer", $"slot wait: {command}");
                    await _commandSlot.WaitAsync(commandToken);
                    slotAcquired = true;
                    BridgeDiagnostics.Log("BridgeServer", $"slot acquired: {command}");

                    BridgeDiagnostics.Log("BridgeServer", $"execute begin: {command}, timeoutSec={timeoutSec}");
                    response = await CommandRegistry.ExecuteCommand(command, data, pipe, commandToken);
                    BridgeDiagnostics.Log("BridgeServer", $"execute end: {command}, responseChars={response?.Length ?? 0}");
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    BridgeDiagnostics.Log("BridgeServer", $"execute timeout: {command}");
                    response = Response.Error($"Command '{command}' timed out after {timeoutSec}s");
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { }
                    heartbeatCts.Dispose();
                    // Skip idle when shutting down so domain reload isn't delayed.
                    if (!ct.IsCancellationRequested)
                        try { await Task.Delay(CommandIdleMs, ct); } catch { }
                    if (slotAcquired)
                    {
                        _commandSlot.Release();
                        BridgeDiagnostics.Log("BridgeServer", $"slot released: {command}");
                    }
                }

                // Send final response (may be empty for streaming commands)
                // Use a 5s per-write timeout so a stalled client can't freeze this handler
                if (!string.IsNullOrEmpty(response))
                {
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                    using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    writeCts.CancelAfter(5000);
                    await pipeWriteLock.WaitAsync(writeCts.Token);
                    try
                    {
                        await pipe.WriteAsync(responseBytes, 0, responseBytes.Length, writeCts.Token);
                        await pipe.FlushAsync();
                    }
                    finally { pipeWriteLock.Release(); }
                }
                pipe.Disconnect();
                BridgeDiagnostics.Log("BridgeServer", $"client disconnected after command: {command}");
            }
            catch (OperationCanceledException)
            {
                BridgeDiagnostics.Log("BridgeServer", $"HandleClient canceled: {command ?? "pre-parse"}");
            }
            catch (IOException)
            {
                BridgeDiagnostics.Log("BridgeServer", $"pipe broken: {command ?? "pre-parse"}");
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("BridgeServer client", ex);
                Debug.LogError($"[Bridge] Client error: {ex.Message}");
            }
            BridgeDiagnostics.Log("BridgeServer", $"HandleClient exit: {command ?? "pre-parse"}");
        }
    }

    /// <summary>
    /// Quote-aware argument splitter. Handles "paths with spaces" in plain args.
    /// </summary>
    public static class ArgParser
    {
        /// <summary>
        /// Splits a string on spaces, respecting double-quoted segments.
        /// "Canvas/Text Area/Text" Component field value → 4 args with spaces preserved.
        /// </summary>
        public static string[] Split(string input, int maxParts = 0)
        {
            if (string.IsNullOrEmpty(input)) return new string[0];

            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == ' ' && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        // If we've reached maxParts-1, take the rest as the last arg
                        if (maxParts > 0 && parts.Count == maxParts - 1)
                        {
                            current.Append(input.Substring(i));
                            break;
                        }
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
                parts.Add(current.ToString().Trim());

            return parts.ToArray();
        }
    }

    /// <summary>
    /// Plain text response builder.
    /// </summary>
    public static class Response
    {
        public static string Success(string result = null)
        {
            return result ?? "OK";
        }

        public static string SuccessWithData(object data)
        {
            if (data == null) return "OK";
            var sb = new StringBuilder();
            foreach (var prop in data.GetType().GetProperties())
            {
                var value = prop.GetValue(data);
                if (value is System.Collections.IEnumerable enumerable && !(value is string))
                {
                    sb.AppendLine($"{prop.Name}:");
                    foreach (var item in enumerable)
                        sb.AppendLine($"  - {item}");
                }
                else
                {
                    sb.AppendLine($"{prop.Name}: {value}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static string Error(string message)
        {
            return $"Error: {message}";
        }

        /// <summary>Error + "Did you mean:" suggestions for a missing scene GameObject path.</summary>
        public static string ErrorSceneNotFound(string path, string extraContext = null)
        {
            string ctx = string.IsNullOrEmpty(extraContext) ? "" : $" ({extraContext})";
            return $"Error: GameObject not found: {path}{ctx}"
                   + PathResolver.FormatSuggestions(path, PathResolver.SuggestKind.SceneGameObject);
        }

        /// <summary>Error + "Did you mean:" suggestions for a missing asset path (prefab, material, SO, scene asset, etc.).</summary>
        public static string ErrorAssetNotFound(string path, string kind = "Asset")
        {
            return $"Error: {kind} not found: {path}"
                   + PathResolver.FormatSuggestions(path, PathResolver.SuggestKind.Asset);
        }

        public static string Exception(Exception ex)
        {
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
