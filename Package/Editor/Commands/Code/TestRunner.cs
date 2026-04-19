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
    /// Results stream to the CLI as each test completes — no waiting for the full run.
    /// </summary>
    public static class TestRunner
    {
        private static TestRunnerApi api;
        private static ICallbacks currentCallbacks;

        private static readonly string[] TestFlags = { "playmode", "editmode", "all", "list" };

        [BridgeCommand("TEST", "Run Unity tests",
            Category = "Code",
            Usage = "TEST [mode] [filter]\n" +
                    "  TEST                   - Run EditMode tests\n" +
                    "  TEST playmode          - Run PlayMode tests\n" +
                    "  TEST all               - Run all tests\n" +
                    "  TEST list              - List all available tests\n" +
                    "  TEST list MyClass      - List tests matching filter\n" +
                    "  TEST MyTestClass       - Run tests matching name (substring)\n" +
                    "  TEST MyTest playmode   - Run matching PlayMode tests",
            RequiresMainThread = true,
            Streaming = true,
            TimeoutSeconds = 600,
            RelatedCommands = new[] { "LOG", "CODE_EXEC_RETURN" })]
        public static async Task Run(string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };

            try
            {
                var args = CommandArgs.Parse(data, TestFlags);

                if (api == null)
                    api = await CommandRegistry.RunOnMainThreadAsync(() =>
                        ScriptableObject.CreateInstance<TestRunnerApi>());

                // Determine test mode
                var testMode = args.Has("all") ? TestMode.EditMode | TestMode.PlayMode
                    : args.Has("playmode") ? TestMode.PlayMode : TestMode.EditMode;

                // Get filter text from positional args
                string testName = args.Positional.Count > 0
                    ? string.Join(" ", args.Positional)
                    : args.Warnings.Count > 0 ? string.Join(" ", args.Warnings) : null;

                // LIST mode: enumerate tests without running
                if (args.Has("list"))
                {
                    await ListTests(writer, testMode, testName);
                    return;
                }

                // Run tests with streaming output
                await RunTests(writer, testMode, testName, ct);
            }
            catch (OperationCanceledException)
            {
                await writer.WriteLineAsync("\n--- Test run cancelled by client ---");
            }
            catch (Exception ex)
            {
                await writer.WriteLineAsync($"\nError: {ex.Message}");
            }
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
                await writer.WriteLineAsync("Error: Timeout listing tests");
                return;
            }

            var allTests = await tcs.Task;

            // Apply substring filter
            if (!string.IsNullOrEmpty(nameFilter))
            {
                var filterLower = nameFilter.ToLowerInvariant();
                allTests = allTests.FindAll(t => t.ToLowerInvariant().Contains(filterLower));
            }

            await writer.WriteLineAsync($"Found {allTests.Count} tests ({testMode}):");
            foreach (var t in allTests)
                await writer.WriteLineAsync("  " + t);
        }

        private static async Task RunTests(StreamWriter writer, TestMode testMode, string testName, CancellationToken ct)
        {
            var state = new RunState { Writer = writer, StartTime = DateTime.Now, Ct = ct };
            var callbacks = new StreamingTestCallbacks(state);

            await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                // Unregister previous callbacks
                if (currentCallbacks != null)
                    api.UnregisterCallbacks(currentCallbacks);

                currentCallbacks = callbacks;
                api.RegisterCallbacks(callbacks);

                // Create filter
                var testFilter = new Filter { testMode = testMode };
                if (!string.IsNullOrEmpty(testName))
                    testFilter.groupNames = new[] { testName };

                // Start the run
                api.Execute(new ExecutionSettings(testFilter));
                return 0;
            });

            // Wait for completion, cancellation, or 10 minute timeout
            var timeoutTask = Task.Delay(600000, ct);
            var completedTask = await Task.WhenAny(state.CompletionSource.Task, timeoutTask);

            if (ct.IsCancellationRequested)
            {
                // Client disconnected — cancel the test run
                // TestRunnerApi doesn't have a Cancel method, but we stop reporting
                await writer.WriteLineAsync("\n--- Cancelled ---");
            }
            else if (completedTask == timeoutTask)
            {
                await writer.WriteLineAsync("\n--- Test run timed out (10 min) ---");
            }

            // Write final summary
            await writer.WriteLineAsync("");
            await writer.WriteLineAsync($"=== {state.PassedTests}/{state.CompletedTests} passed" +
                (state.FailedTests > 0 ? $", {state.FailedTests} failed" : "") +
                (state.SkippedTests > 0 ? $", {state.SkippedTests} skipped" : "") +
                $" in {(DateTime.Now - state.StartTime).TotalSeconds:F1}s ===");
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

        /// <summary>Shared mutable state for the streaming callbacks.</summary>
        private class RunState
        {
            public StreamWriter Writer;
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
        }

        private class StreamingTestCallbacks : ICallbacks
        {
            private readonly RunState _state;
            private const double SlowTestThresholdSeconds = 5.0;

            public StreamingTestCallbacks(RunState state) => _state = state;

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _state.TotalTests = CountLeafTests(testsToRun);
                WriteLine($"Running {_state.TotalTests} tests...\n");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
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

                // Progress: [3/17] PASS TestName (0.5s)
                var remaining = _state.TotalTests - _state.CompletedTests;
                var progress = $"[{_state.CompletedTests}/{_state.TotalTests}]";
                var line = $"{progress} {icon} {result.Test.Name} ({result.Duration:F1}s)";

                // ETA based on average duration
                if (remaining > 0 && _state.Durations.Count > 0)
                {
                    double avg = 0;
                    foreach (var d in _state.Durations) avg += d;
                    avg /= _state.Durations.Count;
                    var eta = remaining * avg;
                    if (eta > 2.0) // Only show ETA if >2s remaining
                        line += $"  ~{eta:F0}s left";
                }

                // Slow test warning
                if (result.Duration > SlowTestThresholdSeconds)
                    line += "  [SLOW]";

                WriteLine(line);

                // Failure details
                if (result.TestStatus == TestStatus.Failed)
                {
                    if (!string.IsNullOrEmpty(result.Message))
                        WriteLine($"      {result.Message.Trim()}");
                    if (!string.IsNullOrEmpty(result.StackTrace))
                    {
                        var stackLines = result.StackTrace.Split('\n');
                        int shown = 0;
                        foreach (var sl in stackLines)
                        {
                            var trimmed = sl.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;
                            if (trimmed.Contains("NUnit.") || trimmed.Contains("UnityEngine.TestRunner")) continue;
                            WriteLine($"      at {trimmed}");
                            if (++shown >= 3) break;
                        }
                    }
                }
            }

            private void WriteLine(string text)
            {
                try { _state.Writer.WriteLine(text); }
                catch { /* pipe closed — client cancelled */ }
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
