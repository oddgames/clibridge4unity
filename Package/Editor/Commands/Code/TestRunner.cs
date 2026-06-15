using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Unity Test Framework runner with streaming results, ETA, and cancellation.
    ///
    /// Results stream to the CLI as each test completes — no waiting for the full run. They're
    /// also written to a log file under <c>Temp/clibridge4unity_test.log</c>, with a sibling
    /// <c>.status</c> file (<c>running</c>/<c>done</c>/<c>error: …</c>). PlayMode test runs survive
    /// the entering/exiting domain reloads because <c>[InitializeOnLoad]</c> re-registers a
    /// callback that keeps appending to the same log file when a run is mid-flight. The CLI tails
    /// that file from disk, so the pipe-death from entering PlayMode no longer eats the stream.
    /// </summary>
    [InitializeOnLoad]
    public static class TestRunner
    {
        private static TestRunnerApi api;
        private static ICallbacks currentCallbacks;

        private static readonly string[] TestFlags = { "playmode", "editmode", "all", "list" };
        private static readonly string[] TestOptions = { "category", "tests" };

        static TestRunner()
        {
            // Defer to first editor update — InitializeOnLoad is too early to create
            // ScriptableObject instances safely on every Unity version.
            EditorApplication.update += ResumeAfterFirstTick;
        }

        private static void ResumeAfterFirstTick()
        {
            EditorApplication.update -= ResumeAfterFirstTick;
            TryResumeCallbacks();
        }

        /// <summary>
        /// If a test run was in flight when this domain came up (i.e. we just survived the
        /// PlayMode entry or exit reload), re-register a callback that keeps appending results
        /// to the same log/status files. The original CLI pipe is gone — disk is the only sink.
        /// </summary>
        private static void TryResumeCallbacks()
        {
            try
            {
                string runId = SessionState.GetString(SessionKeys.TestRunId, "");
                string logPath = SessionState.GetString(SessionKeys.TestLogPath, "");
                string statusPath = SessionState.GetString(SessionKeys.TestStatusPath, "");
                if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(logPath) || string.IsNullOrEmpty(statusPath))
                    return;

                // Already finished before we got here — nothing to resume.
                string status = SafeReadStatus(statusPath);
                if (status == "done" || status.StartsWith("error"))
                {
                    ClearRunState();
                    return;
                }

                if (api == null) api = ScriptableObject.CreateInstance<TestRunnerApi>();

                var state = new RunState
                {
                    Writer = null, // pipe is gone — file is the only sink
                    StartTime = DateTime.Now,
                    Ct = CancellationToken.None,
                    RunId = runId,
                    LogFilePath = logPath,
                    StatusFilePath = statusPath,
                    TotalTests = SessionState.GetInt(SessionKeys.TestRunId + "_total", 0),
                    CompletedTests = SessionState.GetInt(SessionKeys.TestRunId + "_completed", 0),
                    PassedTests = SessionState.GetInt(SessionKeys.TestRunId + "_passed", 0),
                    FailedTests = SessionState.GetInt(SessionKeys.TestRunId + "_failed", 0),
                    SkippedTests = SessionState.GetInt(SessionKeys.TestRunId + "_skipped", 0),
                };
                var callbacks = new StreamingTestCallbacks(state);

                if (currentCallbacks != null)
                {
                    try { api.UnregisterCallbacks(currentCallbacks); } catch { }
                }
                currentCallbacks = callbacks;
                api.RegisterCallbacks(callbacks);

                TryAppendToLogFile(logPath, $"[resumed after assembly reload at {DateTime.Now:HH:mm:ss}]");
            }
            catch (Exception ex)
            {
                BridgeDiagnostics.LogException("TestRunner.TryResumeCallbacks", ex);
            }
        }

        private static string SafeReadStatus(string path)
        {
            try { return File.ReadAllText(path).Trim(); } catch { return ""; }
        }

        private static void TryAppendToLogFile(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, text + "\n"); } catch { }
        }

        private static void TrySetStatus(string statusPath, string value)
        {
            if (string.IsNullOrEmpty(statusPath)) return;
            try { File.WriteAllText(statusPath, value); } catch { }
        }

        private static void ClearRunState()
        {
            SessionState.SetString(SessionKeys.TestRunId, "");
            SessionState.SetString(SessionKeys.TestLogPath, "");
            SessionState.SetString(SessionKeys.TestStatusPath, "");
            SessionState.SetString(SessionKeys.TestMode, "");
            SessionState.SetInt(SessionKeys.TestRunId + "_total", 0);
            SessionState.SetInt(SessionKeys.TestRunId + "_completed", 0);
            SessionState.SetInt(SessionKeys.TestRunId + "_passed", 0);
            SessionState.SetInt(SessionKeys.TestRunId + "_failed", 0);
            SessionState.SetInt(SessionKeys.TestRunId + "_skipped", 0);
        }

        [BridgeCommand("TEST", "Run Unity tests (results also persisted to Temp/clibridge4unity_test.log so PlayMode survives the domain reload)",
            Category = "Code",
            Usage = "TEST [mode] [group ...] [--category X,Y] [--tests Full.Name,Other.Name]\n" +
                    "  TEST                              - Run EditMode tests\n" +
                    "  TEST playmode                     - Run PlayMode tests (survives domain reload)\n" +
                    "  TEST all                          - Run all tests\n" +
                    "  TEST list                         - List all available tests\n" +
                    "  TEST list MyClass                 - List tests matching filter\n" +
                    "  TEST MyTestClass                  - Run tests matching one group/class\n" +
                    "  TEST PlayerTests,CameraTests      - Run multiple groups (OR — comma-separated)\n" +
                    "  TEST PlayerTests CameraTests      - Same (multiple positional args also OR)\n" +
                    "  TEST --category Physics,AI        - Run by [Category(\"X\")] attribute (multiple OR)\n" +
                    "  TEST --tests Foo.TestA,Foo.TestB  - Run exact test names (multiple OR)\n" +
                    "  TEST MyTest --category Physics playmode  - Combine filters + mode\n" +
                    "  Result lines stream to the CLI AND to Temp/clibridge4unity_test.log; the\n" +
                    "  status is Temp/clibridge4unity_test.status (running/done/error). The CLI\n" +
                    "  tails those files when the pipe dies, so PlayMode results survive the reload.",
            RequiresMainThread = true,
            Streaming = true,
            TimeoutSeconds = 600,
            RelatedCommands = new[] { "LOG", "CODE_EXEC_RETURN" })]
        public static async Task Run(string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
            string runId = null;
            string logPath = null;
            string statusPath = null;

            try
            {
                var args = CommandArgs.Parse(data, TestFlags, TestOptions);

                if (api == null)
                    api = await CommandRegistry.RunOnMainThreadAsync(() =>
                        ScriptableObject.CreateInstance<TestRunnerApi>());

                var testMode = args.Has("all") ? TestMode.EditMode | TestMode.PlayMode
                    : args.Has("playmode") ? TestMode.PlayMode : TestMode.EditMode;

                var groupTokens = new List<string>();
                groupTokens.AddRange(args.Positional);
                groupTokens.AddRange(args.Warnings);
                var groupNames = SplitFilterList(groupTokens);
                var categoryNames = SplitCommaList(args.Get("category"));
                var testNames = SplitCommaList(args.Get("tests"));

                if (args.Has("list"))
                {
                    string listFilter = groupNames.Length > 0 ? groupNames[0] : null;
                    await ListTests(writer, testMode, listFilter);
                    return;
                }

                // Durable log + status files. CLI tails these so the pipe dying mid-PlayMode-reload
                // doesn't eat the results. Path is relative to Unity's working directory (project
                // root). Application.dataPath is main-thread-only; Environment.CurrentDirectory is not.
                runId = DateTime.UtcNow.Ticks.ToString("X");
                string tempDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Temp"));
                try { Directory.CreateDirectory(tempDir); } catch { }
                logPath = Path.Combine(tempDir, "clibridge4unity_test.log");
                statusPath = Path.Combine(tempDir, "clibridge4unity_test.status");
                try { File.WriteAllText(logPath, ""); } catch { }
                TrySetStatus(statusPath, "running");

                SessionState.SetString(SessionKeys.TestRunId, runId);
                SessionState.SetString(SessionKeys.TestLogPath, logPath);
                SessionState.SetString(SessionKeys.TestStatusPath, statusPath);
                SessionState.SetString(SessionKeys.TestMode, testMode.ToString());

                // Headers so the CLI knows the durable paths (and confirms the run started).
                await SafeWriteLineAsync(writer, $"TEST_LOG_FILE: {logPath}");
                await SafeWriteLineAsync(writer, $"TEST_STATUS_FILE: {statusPath}");
                await SafeWriteLineAsync(writer, $"TEST_RUN_ID: {runId}");

                await RunTests(writer, testMode, groupNames, categoryNames, testNames, ct, runId, logPath, statusPath);
            }
            catch (ObjectDisposedException)
            {
                // Client disconnected mid-run (pipe closed) — tests keep running, log file keeps
                // capturing, post-reload callbacks resume. The CLI is tailing the file.
            }
            catch (IOException)
            {
                // Broken pipe — same as above.
            }
            catch (OperationCanceledException)
            {
                await SafeWriteLineAsync(writer, "\n--- Test run cancelled by client ---");
                TrySetStatus(statusPath, "error: cancelled");
            }
            catch (Exception ex)
            {
                await SafeWriteLineAsync(writer, $"\nError: {ex.Message}");
                TrySetStatus(statusPath, $"error: {ex.Message}");
            }
            finally
            {
                // DON'T blindly unregister callbacks here — for PlayMode runs they need to keep
                // firing across the reload. The InitializeOnLoad resume re-registers anyway. Only
                // unregister if the run already finished (status==done) or we hit a definite error.
                try
                {
                    string status = SafeReadStatus(statusPath);
                    bool runFinished = status == "done" || status.StartsWith("error");
                    if (runFinished && currentCallbacks != null && api != null)
                    {
                        await CommandRegistry.RunOnMainThreadAsync(() =>
                        {
                            try { api.UnregisterCallbacks(currentCallbacks); } catch { }
                            return 0;
                        });
                        currentCallbacks = null;
                        ClearRunState();
                    }
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private static async Task SafeWriteLineAsync(StreamWriter w, string text)
        {
            try { await w.WriteLineAsync(text); }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }

        private static async Task ListTests(StreamWriter writer, TestMode testMode, string nameFilter)
        {
            var tcs = new TaskCompletionSource<List<string>>();

            await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                api.RetrieveTestList(testMode, (testRoot) =>
                {
                    var tests = new List<string>();
                    CollectTestNames(testRoot, tests);
                    tcs.TrySetResult(tests);
                });
                return 0;
            });

            var timeout = Task.Delay(10000);
            var completed = await Task.WhenAny(tcs.Task, timeout);
            if (completed == timeout)
            {
                await SafeWriteLineAsync(writer, "Error: Timeout listing tests");
                return;
            }

            var allTests = await tcs.Task;

            if (!string.IsNullOrEmpty(nameFilter))
            {
                var filterLower = nameFilter.ToLowerInvariant();
                allTests = allTests.FindAll(t => t.ToLowerInvariant().Contains(filterLower));
            }

            await SafeWriteLineAsync(writer, $"Found {allTests.Count} tests ({testMode}):");
            foreach (var t in allTests)
                await SafeWriteLineAsync(writer, "  " + t);
        }

        private static async Task RunTests(StreamWriter writer, TestMode testMode,
            string[] groupNames, string[] categoryNames, string[] testNames, CancellationToken ct,
            string runId, string logPath, string statusPath)
        {
            var state = new RunState
            {
                Writer = writer,
                StartTime = DateTime.Now,
                Ct = ct,
                RunId = runId,
                LogFilePath = logPath,
                StatusFilePath = statusPath
            };
            var callbacks = new StreamingTestCallbacks(state);

            if (groupNames.Length > 0 || categoryNames.Length > 0 || testNames.Length > 0)
            {
                var parts = new List<string>();
                if (groupNames.Length > 0) parts.Add($"groups=[{string.Join(",", groupNames)}]");
                if (categoryNames.Length > 0) parts.Add($"categories=[{string.Join(",", categoryNames)}]");
                if (testNames.Length > 0) parts.Add($"tests=[{string.Join(",", testNames)}]");
                state.WriteLine($"Filter: {string.Join(" ", parts)}  mode={testMode}");
            }

            await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                if (currentCallbacks != null)
                    api.UnregisterCallbacks(currentCallbacks);

                currentCallbacks = callbacks;
                api.RegisterCallbacks(callbacks);

                var testFilter = new Filter { testMode = testMode };
                if (groupNames.Length > 0) testFilter.groupNames = groupNames;
                if (categoryNames.Length > 0) testFilter.categoryNames = categoryNames;
                if (testNames.Length > 0) testFilter.testNames = testNames;

                api.Execute(new ExecutionSettings(testFilter));
                return 0;
            });

            var timeoutTask = Task.Delay(600000, ct);
            var completedTask = await Task.WhenAny(state.CompletionSource.Task, timeoutTask);

            if (ct.IsCancellationRequested)
            {
                state.WriteLine("\n--- Cancelled ---");
            }
            else if (completedTask == timeoutTask)
            {
                state.WriteLine("\n--- Test run timed out (10 min) ---");
            }

            state.WriteLine("");
            state.WriteLine(
                $"=== {state.PassedTests}/{state.CompletedTests} passed" +
                (state.FailedTests > 0 ? $", {state.FailedTests} failed" : "") +
                (state.SkippedTests > 0 ? $", {state.SkippedTests} skipped" : "") +
                $" in {(DateTime.Now - state.StartTime).TotalSeconds:F1}s ===");
        }

        private static string[] SplitFilterList(IEnumerable<string> tokens)
        {
            var result = new List<string>();
            foreach (var p in tokens)
                foreach (var segment in p.Split(','))
                {
                    var trimmed = segment.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) result.Add(trimmed);
                }
            return result.ToArray();
        }

        private static string[] SplitCommaList(string value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
            var result = new List<string>();
            foreach (var segment in value.Split(','))
            {
                var trimmed = segment.Trim();
                if (!string.IsNullOrEmpty(trimmed)) result.Add(trimmed);
            }
            return result.ToArray();
        }

        private static void CollectTestNames(ITestAdaptor test, List<string> names)
        {
            if (!test.HasChildren)
            {
                names.Add(test.FullName);
                return;
            }
            foreach (var child in test.Children)
                CollectTestNames(child, names);
        }

        /// <summary>Mutable run state shared between Run/RunTests and the callbacks.</summary>
        private class RunState
        {
            public StreamWriter Writer; // pipe — best-effort; may be null after a reload-resume
            public DateTime StartTime;
            public CancellationToken Ct;
            public TaskCompletionSource<bool> CompletionSource = new TaskCompletionSource<bool>();

            public int TotalTests;
            public int CompletedTests;
            public int PassedTests;
            public int FailedTests;
            public int SkippedTests;
            public List<double> Durations = new List<double>();
            public DateTime? CurrentTestStart;

            // Durable state — survives domain reloads via SessionState (counts) + disk files (log).
            public string RunId;
            public string LogFilePath;
            public string StatusFilePath;

            /// <summary>Write to pipe (best-effort) AND the durable log file (must succeed).</summary>
            public void WriteLine(string text)
            {
                if (Writer != null)
                {
                    try { Writer.WriteLine(text); } catch { /* pipe closed — fall through to file */ }
                }
                if (!string.IsNullOrEmpty(LogFilePath))
                {
                    try { File.AppendAllText(LogFilePath, text + "\n"); } catch { }
                }
            }
        }

        private class StreamingTestCallbacks : ICallbacks
        {
            private readonly RunState _state;
            private const double SlowTestThresholdSeconds = 5.0;

            public StreamingTestCallbacks(RunState state) => _state = state;

            public void RunStarted(ITestAdaptor testsToRun)
            {
                if (_state.TotalTests == 0)
                    _state.TotalTests = CountLeafTests(testsToRun);
                if (_state.TotalTests > 0)
                    SessionState.SetInt(SessionKeys.TestRunId + "_total", _state.TotalTests);
                _state.WriteLine($"Running {_state.TotalTests} tests...\n");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                // Mark status BEFORE clearing SessionState — the CLI is polling the status file.
                if (!string.IsNullOrEmpty(_state.StatusFilePath))
                {
                    try { File.WriteAllText(_state.StatusFilePath, "done"); } catch { }
                }
                ClearRunState();
                _state.CompletionSource.TrySetResult(true);
            }

            public void TestStarted(ITestAdaptor test)
            {
                if (!test.HasChildren)
                    _state.CurrentTestStart = DateTime.Now;
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return;
                if (_state.Ct.IsCancellationRequested) return;

                _state.CompletedTests++;
                _state.Durations.Add(result.Duration);

                string icon;
                switch (result.TestStatus)
                {
                    case TestStatus.Passed:
                        _state.PassedTests++;
                        icon = "PASS";
                        break;
                    case TestStatus.Failed:
                        _state.FailedTests++;
                        icon = "FAIL";
                        break;
                    case TestStatus.Skipped:
                        _state.SkippedTests++;
                        icon = "SKIP";
                        break;
                    default:
                        icon = "????";
                        break;
                }

                // Persist running totals so a reload-resume can pick up where we left off.
                SessionState.SetInt(SessionKeys.TestRunId + "_completed", _state.CompletedTests);
                SessionState.SetInt(SessionKeys.TestRunId + "_passed", _state.PassedTests);
                SessionState.SetInt(SessionKeys.TestRunId + "_failed", _state.FailedTests);
                SessionState.SetInt(SessionKeys.TestRunId + "_skipped", _state.SkippedTests);

                var remaining = _state.TotalTests - _state.CompletedTests;
                var progress = $"[{_state.CompletedTests}/{_state.TotalTests}]";
                var line = $"{progress} {icon} {result.Test.Name} ({result.Duration:F1}s)";

                if (remaining > 0 && _state.Durations.Count > 0)
                {
                    double avg = 0;
                    foreach (var d in _state.Durations) avg += d;
                    avg /= _state.Durations.Count;
                    var eta = remaining * avg;
                    if (eta > 2.0)
                        line += $"  ~{eta:F0}s left";
                }

                if (result.Duration > SlowTestThresholdSeconds)
                    line += "  [SLOW]";

                _state.WriteLine(line);

                if (result.TestStatus == TestStatus.Failed)
                {
                    if (!string.IsNullOrEmpty(result.Message))
                        _state.WriteLine($"      {result.Message.Trim()}");
                    if (!string.IsNullOrEmpty(result.StackTrace))
                    {
                        var stackLines = result.StackTrace.Split('\n');
                        int shown = 0;
                        foreach (var sl in stackLines)
                        {
                            var trimmed = sl.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;
                            if (trimmed.Contains("NUnit.") || trimmed.Contains("UnityEngine.TestRunner")) continue;
                            _state.WriteLine($"      at {trimmed}");
                            if (++shown >= 3) break;
                        }
                    }
                }
            }

            private int CountLeafTests(ITestAdaptor test)
            {
                if (!test.HasChildren) return 1;
                int count = 0;
                foreach (var child in test.Children)
                    count += CountLeafTests(child);
                return count;
            }
        }
    }
}
