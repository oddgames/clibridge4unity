using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace clibridge4unity
{
    /// <summary>
    /// Unity Test Framework runner with streaming results.
    /// </summary>
    public static class TestRunner
    {
        private static TestRunnerApi api;
        private static TaskCompletionSource<string> testTcs;
        private static StringBuilder resultsBuilder;
        private static int totalTests;
        private static int completedTests;
        private static int passedTests;
        private static int failedTests;
        private static DateTime startTime;

        private static readonly string[] TestFlags = { "playmode", "editmode", "all" };

        [BridgeCommand("TEST", "Run Unity tests",
            Category = "Code",
            Usage = "TEST [mode] [filter]\n" +
                    "  TEST                   - Run EditMode tests\n" +
                    "  TEST playmode          - Run PlayMode tests\n" +
                    "  TEST all               - Run all tests\n" +
                    "  TEST MyTestClass       - Run specific test by name",
            RequiresMainThread = true)]
        public static async Task<string> Run(string filter = null)
        {
            try
            {
                var args = CommandArgs.Parse(filter, TestFlags);

                if (api == null)
                    api = ScriptableObject.CreateInstance<TestRunnerApi>();

                // Reset state
                testTcs = new TaskCompletionSource<string>();
                resultsBuilder = new StringBuilder();
                totalTests = 0;
                completedTests = 0;
                passedTests = 0;
                failedTests = 0;
                startTime = DateTime.Now;

                // Register callbacks
                api.RegisterCallbacks(new TestCallbacks());

                // Create filter
                var testFilter = new Filter
                {
                    testMode = args.Has("playmode") ? TestMode.PlayMode : TestMode.EditMode
                };

                // Positional args or unknown tokens = test name filter
                string testName = args.Positional.Count > 0
                    ? string.Join(" ", args.Positional)
                    : args.Warnings.Count > 0 ? string.Join(" ", args.Warnings) : null;

                if (!string.IsNullOrEmpty(testName))
                    testFilter.testNames = new[] { testName };

                // Run tests
                api.Execute(new ExecutionSettings(testFilter));

                // Wait for completion with timeout
                var timeout = Task.Delay(300000); // 5 minute timeout
                var completed = await Task.WhenAny(testTcs.Task, timeout);

                if (completed == timeout)
                    return Response.Error("Test run timeout");

                return await testTcs.Task;
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        private class TestCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                totalTests = CountTests(testsToRun);
                resultsBuilder.AppendLine($"Running {totalTests} tests...\n");
                Debug.Log($"[Bridge] Test run started: {totalTests} tests");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var duration = (DateTime.Now - startTime).TotalSeconds;

                resultsBuilder.AppendLine();
                resultsBuilder.AppendLine($"=== Test Run Complete ===");
                resultsBuilder.AppendLine($"Duration: {duration:F2}s");
                resultsBuilder.AppendLine($"Passed: {passedTests}/{completedTests}");

                if (failedTests > 0)
                    resultsBuilder.AppendLine($"Failed: {failedTests}");

                var summary = new
                {
                    passed = passedTests,
                    failed = failedTests,
                    total = completedTests,
                    duration = duration,
                    status = failedTests == 0 ? "PASSED" : "FAILED",
                    output = resultsBuilder.ToString()
                };

                testTcs?.TrySetResult(Response.SuccessWithData(summary));
            }

            public void TestStarted(ITestAdaptor test)
            {
                // Only log leaf tests
                if (!test.HasChildren)
                    Debug.Log($"[Bridge] Starting: {test.Name}");
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                // Only process leaf tests
                if (result.HasChildren) return;

                completedTests++;

                string status;
                switch (result.TestStatus)
                {
                    case TestStatus.Passed:
                        passedTests++;
                        status = "✓";
                        break;
                    case TestStatus.Failed:
                        failedTests++;
                        status = "✗";
                        break;
                    case TestStatus.Skipped:
                        status = "○";
                        break;
                    default:
                        status = "?";
                        break;
                }

                resultsBuilder.AppendLine($"{status} {result.Test.Name} ({result.Duration:F3}s)");

                if (result.TestStatus == TestStatus.Failed)
                {
                    if (!string.IsNullOrEmpty(result.Message))
                        resultsBuilder.AppendLine($"    {result.Message}");
                }

                Debug.Log($"[Bridge] {status} {result.Test.Name}");
            }

            private int CountTests(ITestAdaptor test)
            {
                if (!test.HasChildren) return 1;
                int count = 0;
                foreach (var child in test.Children)
                    count += CountTests(child);
                return count;
            }
        }
    }
}
