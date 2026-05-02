using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Writes Unity state to a temp file periodically so the CLI can check
    /// Unity's status without a pipe connection (useful during domain reload).
    /// File: %TEMP%/clibridge4unity_{ProjectHash}.status
    /// </summary>
    [InitializeOnLoad]
    public static class Heartbeat
    {
        private const int HeartbeatIntervalMs = 2000;

        private static string _statusFile;
        private static string _projectName;
        private static string _projectRoot;
        private static string _pipeName;
        private static string _statusJsonStaticFields;
        private static int _pid;
        private static string _currentState;
        private static long _stateEnteredAtUnix;
        private static readonly object DiskWriteLock = new object();
        private static string _pendingStatusJson;
        private static bool _pendingTouch;
        private static bool _diskWriteQueued;
        private static volatile bool _isRunning;

        static Heartbeat()
        {
            BridgeDiagnostics.Log("Heartbeat", "static ctor");
            EditorApplication.update += InitOnFirstTick;
        }

        private static void InitOnFirstTick()
        {
            BridgeDiagnostics.Log("Heartbeat", "InitOnFirstTick");
            EditorApplication.update -= InitOnFirstTick;
            Initialize();
        }

        private static void Initialize()
        {
            BridgeDiagnostics.Log("Heartbeat", "Initialize enter");
            _projectRoot = Application.dataPath.Replace("/Assets", "");
            string normalizedPath = _projectRoot.ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            string hash = BridgeServer.GetDeterministicHashCode(normalizedPath).ToString("X8");
            _pipeName = $"UnityBridge_{Environment.UserName}_{hash}";
            _projectName = Path.GetFileName(_projectRoot.TrimEnd('/', '\\'));
            string safeProjectName = SanitizeName(_projectName);
            _statusFile = Path.Combine(Path.GetTempPath(), $"clibridge4unity_{hash}_{safeProjectName}.status");
            _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _statusJsonStaticFields =
                $"  \"pid\": {_pid},\n" +
                $"  \"version\": \"{EscapeJson(BridgeServer.Version)}\",\n" +
                $"  \"project\": \"{EscapeJson(_projectName)}\",\n" +
                $"  \"projectPath\": \"{EscapeJson(Path.GetFullPath(_projectRoot))}\",\n" +
                $"  \"pipeName\": \"{EscapeJson(_pipeName)}\",\n" +
                "  \"compileErrors\": false,\n" +
                "  \"compileErrorCount\": 0,\n" +
                "  \"compileTimeAvg\": 0,\n";
            BridgeDiagnostics.Log("Heartbeat", $"status file: {_statusFile}");

            AssemblyReloadEvents.beforeAssemblyReload += WriteReloadingNow;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += Cleanup;

            WriteStatusNow(GetState(), forceStateEnteredAt: true, synchronous: true);
            StartHeartbeatThread();
            BridgeDiagnostics.Log("Heartbeat", "Initialize exit");
        }

        static void StartHeartbeatThread()
        {
            _isRunning = true;
            var thread = new Thread(HeartbeatLoop)
            {
                Name = "Bridge Heartbeat",
                IsBackground = true
            };
            thread.Start();
        }

        static void HeartbeatLoop()
        {
            while (_isRunning)
            {
                Thread.Sleep(HeartbeatIntervalMs);
                if (!_isRunning)
                    continue;

                QueueTouchStatusFile();
            }
        }

        static void OnCompilationStarted(object _)
        {
            WriteStatusNow("compiling", forceStateEnteredAt: true);
        }

        static void OnCompilationFinished(object _)
        {
            WriteStatusNow(GetState(), forceStateEnteredAt: true);
        }

        static void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            WriteStatusNow(GetState(), forceStateEnteredAt: true);
        }

        static void QueueTouchStatusFile()
        {
            QueueDiskWrite(null, touchOnly: true);
        }

        static void WriteStatusNow(string state, bool forceStateEnteredAt, bool synchronous = false)
        {
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (forceStateEnteredAt || state != _currentState)
            {
                _currentState = state;
                _stateEnteredAtUnix = nowUnix;
            }

            string json = BuildStatusJson(state, nowUnix);
            if (!synchronous)
            {
                QueueDiskWrite(json, touchOnly: false);
                return;
            }

            WriteStatusToDisk(json);
        }

        static string BuildStatusJson(string state, long nowUnix)
        {
            return "{\n" +
                   $"  \"state\": \"{state}\",\n" +
                   _statusJsonStaticFields +
                   $"  \"stateEnteredAt\": {_stateEnteredAtUnix},\n" +
                   $"  \"timestamp\": {nowUnix}\n" +
                   "}";
        }

        static void QueueDiskWrite(string json, bool touchOnly)
        {
            lock (DiskWriteLock)
            {
                if (json != null)
                {
                    _pendingStatusJson = json;
                    _pendingTouch = false;
                }
                else if (_pendingStatusJson == null)
                {
                    _pendingTouch = touchOnly;
                }

                if (_diskWriteQueued) return;
                _diskWriteQueued = true;
            }

            ThreadPool.QueueUserWorkItem(_ => FlushDiskWrites());
        }

        static void FlushDiskWrites()
        {
            while (true)
            {
                string json;
                bool touchOnly;
                lock (DiskWriteLock)
                {
                    json = _pendingStatusJson;
                    touchOnly = _pendingTouch;
                    _pendingStatusJson = null;
                    _pendingTouch = false;

                    if (json == null && !touchOnly)
                    {
                        _diskWriteQueued = false;
                        return;
                    }
                }

                try
                {
                    if (json != null)
                    {
                        WriteStatusToDisk(json);
                    }
                    else
                    {
                        try
                        {
                            File.SetLastWriteTimeUtc(_statusFile, DateTime.UtcNow);
                        }
                        catch (FileNotFoundException)
                        {
                            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            WriteStatusToDisk(BuildStatusJson(_currentState ?? "ready", nowUnix));
                        }
                    }
                }
                catch (Exception ex)
                {
                    BridgeDiagnostics.LogException("Heartbeat disk write", ex);
                }
            }
        }

        static void WriteStatusToDisk(string json)
        {
            File.WriteAllText(_statusFile, json);
        }

        static void WriteReloadingNow()
        {
            BridgeDiagnostics.Log("Heartbeat", "before assembly reload - writing reloading state");
            try
            {
                _isRunning = false;
                WriteStatusNow("reloading", forceStateEnteredAt: true, synchronous: true);
                BridgeDiagnostics.Log("Heartbeat", $"reloading state written: {_statusFile}");
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("Heartbeat WriteReloadingNow", ex);
            }
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
            _isRunning = false;
            AssemblyReloadEvents.beforeAssemblyReload -= WriteReloadingNow;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            try
            {
                if (File.Exists(_statusFile))
                    File.Delete(_statusFile);
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("Heartbeat cleanup", ex);
            }
            BridgeDiagnostics.Log("Heartbeat", "cleanup exit");
        }

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 32 ? sb.ToString().Substring(0, 32) : sb.ToString();
        }

        static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
