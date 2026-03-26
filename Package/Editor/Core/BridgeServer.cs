using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
        public const string Version = "1.0.11";

        private static CancellationTokenSource serverCts;
        private static NamedPipeServerStream currentPipeServer;
        private static string pipeName;

        // Main thread context for Unity API calls
        private static SynchronizationContext mainThreadContext;

        // Compilation tracking
        private static DateTime lastCompileRequestTime;
        private static DateTime lastCompileCompleteTime;
        private static TaskCompletionSource<bool> compilationTcs;

        static BridgeServer()
        {
            // Don't start the bridge in batch mode (AssetImportWorker processes)
            if (Application.isBatchMode) return;

            // Capture main thread context for invoking Unity APIs from background threads
            mainThreadContext = SynchronizationContext.Current;

            // Force Unity to keep processing in background so commands work without focus
            Application.runInBackground = true;

            // Enable console timestamps so log entries have time data
            // LogEntries.SetConsoleFlag(ShowTimestamp = 1 << 10, true)
            try
            {
                var logEntriesType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                var setFlag = logEntriesType?.GetMethod("SetConsoleFlag",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                setFlag?.Invoke(null, new object[] { 1 << 10, true });
            }
            catch { }

            AssemblyReloadEvents.beforeAssemblyReload += StopServerImmediately;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // Restore from SessionState
            if (long.TryParse(SessionState.GetString(SessionKeys.LastCompileTime, "0"), out var ticks) && ticks > 0)
                lastCompileCompleteTime = new DateTime(ticks);

            pipeName = GeneratePipeName();
            StartServer();
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
            if (serverCts != null) return;

            serverCts = new CancellationTokenSource();
            // Run multiple concurrent server loops to handle simultaneous connections
            for (int i = 0; i < 3; i++)
                Task.Run(async () => await ServerLoop(serverCts.Token));
            Debug.Log($"[Bridge] Server started: {pipeName} (3 listeners)");
        }

        private static void StopServerImmediately()
        {
            try
            {
                serverCts?.Cancel();
                currentPipeServer?.Dispose();
            }
            catch { }
            finally
            {
                serverCts = null;
                currentPipeServer = null;
            }
        }

        private static void OnCompilationStarted(object ctx)
        {
            lastCompileRequestTime = DateTime.Now;
            SessionState.SetString(SessionKeys.LastCompileRequest, lastCompileRequestTime.Ticks.ToString());
        }

        private static void OnCompilationFinished(object ctx)
        {
            lastCompileCompleteTime = DateTime.Now;
            SessionState.SetString(SessionKeys.LastCompileTime, lastCompileCompleteTime.Ticks.ToString());
            compilationTcs?.TrySetResult(true);
        }

        private static async Task ServerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    currentPipeServer = pipe;

                    await pipe.WaitForConnectionAsync(ct);
                    await HandleClient(pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Debug.LogError($"[Bridge] Error: {ex.Message}");
                        await Task.Delay(1000, ct);
                    }
                }
            }
        }

        private static async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
        {
            string command = null;
            string data = null;

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

                // Process command with timeout — fast fail, don't block
                var cmdInfo = CommandRegistry.GetCommand(command);
                int timeoutMs = cmdInfo?.RequiresMainThread == true ? 10000 : 25000;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);

                string response;
                try
                {
                    response = await CommandRegistry.ExecuteCommand(command, data, pipe, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    response = Response.Error($"Command '{command}' timed out after {timeoutMs / 1000}s");
                }

                // Send final response (may be empty for streaming commands)
                // Use a 5s per-write timeout so a stalled client can't freeze this handler
                if (!string.IsNullOrEmpty(response))
                {
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                    using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    writeCts.CancelAfter(5000);
                    await pipe.WriteAsync(responseBytes, 0, responseBytes.Length, writeCts.Token);
                    await pipe.FlushAsync();
                }
                pipe.Disconnect();
            }
            catch (IOException)
            {
                // Pipe broken - client disconnected during operation
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Bridge] Client error: {ex.Message}");
            }
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

        public static string Exception(Exception ex)
        {
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
