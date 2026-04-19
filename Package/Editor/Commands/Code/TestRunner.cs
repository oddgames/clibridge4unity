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
        private static readonly string[] TestOptions = { "category", "tests" };

        [BridgeCommand("TEST", "Run Unity tests",
            Category = "Code",
            Usage = "TEST [mode] [group ...] [--category X,Y] [--tests Full.Name,Other.Name]\n" +
                    "  TEST                              - Run EditMode tests\n" +
                    "  TEST playmode                     - Run PlayMode tests\n" +
                    "  TEST all                          - Run all tests\n" +
                    "  TEST list                         - List all available tests\n" +
                    "  TEST list MyClass                 - List tests matching filter\n" +
                    "  TEST MyTestClass                  - Run tests matching one group/class\n" +
                    "  TEST PlayerTests,CameraTests      - Run multiple groups (OR — comma-separated)\n" +
                    "  TEST PlayerTests CameraTests      - Same (multiple positional args also OR)\n" +
                    "  TEST --category Physics,AI        - Run by [Category(\"X\")] attribute (multiple OR)\n" +
                    "  TEST --tests Foo.TestA,Foo.TestB  - Run exact test names (multiple OR)\n" +
                    "  TEST MyTest --category Physics playmode  - Combine filters + mode",
            RequiresMainThread = true,
            Streaming = true,
            TimeoutSeconds = 600,
            RelatedCommands = new[] { "LOG", "CODE_EXEC_RETURN" })]
        public static async Task Run(string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };

            try
            {
                var args = CommandArgs.Parse(data, TestFlags, TestOptions);

                if (api == null)
                    api = await CommandRegistry.RunOnMainThreadAsync(() =>
                        ScriptableObject.CreateInstance<TestRunnerApi>());

                // Determine test mode
                var testMode = args.Has("all") ? TestMode.EditMode | TestMode.PlayMode
                    : args.Has("playmode") ? TestMode.PlayMode : TestMode.EditMode;

                // Build filter arrays. Unity's Filter OR's entries within each array.
                // Group patterns come from positional AND "warning" tokens — CommandArgs.Parse
                // routes unrecognized tokens to Warnings when a schema (flags/options) is defined,
                // so `TEST MyClass` lands in Warnings, not Positional.
                var groupTokens = new List<string>();
                groupTokens.AddRange(args.Positional);
                groupTokens.AddRange(args.Warnings);
                var groupNames = SplitFilterList(groupTokens);
                var categoryNames = SplitCommaList(args.Get("category"));
                var testNames = SplitCommaList(args.Get("tests"));

                // LIST mode: enumerate tests without running (uses first group pattern as substring filter).
                if (args.Has("list"))
                {
                    string listFilter = groupNames.Length > 0 ? groupNames[0] : null;
                    await ListTests(writer, testMode, listFilter);
                    return;
                }

                // Run tests with streaming output
                await RunTests(writer, testMode, groupNames, categoryNames, testNames, ct);
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

        private static async Task RunTests(StreamWriter writer, TestMode testMode,
            string[] groupNames, string[] categoryNames, string[] testNames, CancellationToken ct)
        {
            var state = new RunState { Writer = writer, StartTime = DateTime.Now, Ct = ct };
            var callbacks = new StreamingTestCallbacks(state);

            // Surface the filter so the user can confirm what's being run.
            if (groupNames.Length > 0 || categoryNames.Length > 0 || testNames.Length > 0)
            {
                var parts = new List<string>();
                if (groupNames.Length > 0) parts.Add($"groups=[{string.Join(",", groupNames)}]");
                if (categoryNames.Length > 0) parts.Add($"categories=[{string.Join(",", categoryNames)}]");
                if (testNames.Length > 0) parts.Add($"tests=[{string.Join(",", testNames)}]");
                await writer.WriteLineAsync($"Filter: {string.Join(" ", parts)}  mode={testMode}");
            }

            await CommandRegistry.RunOnMainThreadAsync(() =>
            {
                // Unregister previous callbacks
                if (currentCallbacks != null)
                    api.UnregisterCallbacks(currentCallbacks);

                currentCallbacks = callbacks;
                api.RegisterCallbacks(callbacks);

                // Create filter. Each array is OR'd within, so passing multiple entries
                // runs every test matching any of them.
                var testFilter = new Filter { testMode = testMode };
                if (groupNames.Length > 0) testFilter.groupNames = groupNames;
                if (categoryNames.Length > 0) testFilter.categoryNames = categoryNames;
                if (testNames.Length > 0) testFilter.testNames = testNames;

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

        /// <summary>Split each entry into comma-separated segments and flatten.</summary>
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

        /// <summary>Split a single comma-separated string; empty/null → empty array.</summary>
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
