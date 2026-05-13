using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace clibridge4unity
{
    public static class BuildCommand
    {
        private static readonly string[] BuildFlags = { "run", "dev", "development" };
        private static readonly string[] BuildOptKeys = { "output" };

        [BridgeCommand("BUILD", "Build the Unity Player using the active build target",
            Category = "Core",
            Usage = "BUILD                       (output: Builds/<Target>/<ProductName>)\n" +
                    "  BUILD --run               (launch the player after building)\n" +
                    "  BUILD --output <path>     (custom output path)\n" +
                    "  BUILD --dev               (development build, debugging enabled)\n" +
                    "  BUILD --run --dev         (combine)\n" +
                    "Notes:\n" +
                    "  - Uses scenes enabled in File > Build Settings\n" +
                    "  - Active build target (Win/Mac/Linux/Android/WebGL) selected in Unity\n" +
                    "  - --run only works for Standalone (Win/Mac/Linux); Android/WebGL print install hints\n" +
                    "  - First build to a new target may trigger script compilation + assembly reload (pipe will drop; reconnect)",
            RequiresMainThread = true,
            Streaming = true,
            TimeoutSeconds = 1800,
            RelatedCommands = new[] { "STATUS", "LOG", "COMPILE" })]
        public static async Task Build(string data, NamedPipeServerStream pipe, CancellationToken ct)
        {
            var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
            try
            {
                var args = CommandArgs.Parse(data, BuildFlags, BuildOptKeys);
                bool run = args.Has("run");
                bool dev = args.Has("dev") || args.Has("development");
                string customOutput = args.Get("output");

                BuildTarget target = EditorUserBuildSettings.activeBuildTarget;

                var allScenes = EditorBuildSettings.scenes;
                var enabledScenes = Array.FindAll(allScenes, s => s.enabled);
                if (enabledScenes.Length == 0)
                {
                    await writer.WriteLineAsync("Error: No scenes enabled in Build Settings. Add scenes via File > Build Settings.");
                    return;
                }

                var scenePaths = new string[enabledScenes.Length];
                for (int i = 0; i < enabledScenes.Length; i++) scenePaths[i] = enabledScenes[i].path;

                string productName = PlayerSettings.productName;
                string outputPath = string.IsNullOrWhiteSpace(customOutput)
                    ? DefaultOutputPath(target, productName)
                    : Path.GetFullPath(customOutput);

                string parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                UnityEditor.BuildOptions opts = UnityEditor.BuildOptions.None;
                if (dev) opts |= UnityEditor.BuildOptions.Development | UnityEditor.BuildOptions.AllowDebugging;

                await writer.WriteLineAsync($"Target: {target} ({(dev ? "development" : "release")})");
                await writer.WriteLineAsync($"Scenes: {scenePaths.Length}");
                await writer.WriteLineAsync($"Output: {outputPath}");
                await writer.WriteLineAsync("Build started...");

                Application.LogCallback logHandler = (msg, stack, type) =>
                {
                    if (type == LogType.Error || type == LogType.Exception)
                    {
                        try { writer.WriteLine($"[err] {msg}"); } catch { }
                    }
                    else if (type == LogType.Warning)
                    {
                        try { writer.WriteLine($"[warn] {msg}"); } catch { }
                    }
                };

                BuildReport report = null;
                Exception buildErr = null;
                try
                {
                    report = await CommandRegistry.RunOnMainThreadAsync(() =>
                    {
                        Application.logMessageReceived += logHandler;
                        try
                        {
                            return BuildPipeline.BuildPlayer(scenePaths, outputPath, target, opts);
                        }
                        finally
                        {
                            Application.logMessageReceived -= logHandler;
                        }
                    }, "BUILD", timeoutMs: 1800_000);
                }
                catch (Exception ex)
                {
                    buildErr = ex;
                }

                if (buildErr != null)
                {
                    await writer.WriteLineAsync($"Build failed: {buildErr.Message}");
                    return;
                }

                if (report == null)
                {
                    await writer.WriteLineAsync("Build failed: no report returned");
                    return;
                }

                var summary = report.summary;
                await writer.WriteLineAsync();
                await writer.WriteLineAsync($"Result: {summary.result}");
                await writer.WriteLineAsync($"Duration: {summary.totalTime.TotalSeconds:F1}s");
                await writer.WriteLineAsync($"Size: {FormatBytes(summary.totalSize)}");
                await writer.WriteLineAsync($"Warnings: {summary.totalWarnings}, Errors: {summary.totalErrors}");
                await writer.WriteLineAsync($"Output: {summary.outputPath}");

                SessionState.SetString(SessionKeys.LastBuildPath, summary.outputPath);
                SessionState.SetString(SessionKeys.LastBuildTarget, target.ToString());

                if (summary.result != BuildResult.Succeeded)
                {
                    await writer.WriteLineAsync("Build did not succeed.");
                    return;
                }

                if (run)
                {
                    await writer.WriteLineAsync();
                    if (!TryLaunch(target, summary.outputPath, out string launchErr))
                        await writer.WriteLineAsync($"Launch failed: {launchErr}");
                    else
                        await writer.WriteLineAsync($"Launched: {summary.outputPath}");
                }
            }
            catch (Exception ex)
            {
                try { await writer.WriteLineAsync($"Error: {ex.Message}"); } catch { }
            }
        }

        private static string DefaultOutputPath(BuildTarget target, string productName)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dir = Path.Combine(projectRoot, "Builds", target.ToString());
            string safeName = SanitizeFileName(productName);
            string ext = target switch
            {
                BuildTarget.StandaloneWindows => ".exe",
                BuildTarget.StandaloneWindows64 => ".exe",
                BuildTarget.StandaloneOSX => ".app",
                BuildTarget.Android => ".apk",
                _ => ""
            };
            if (target == BuildTarget.WebGL)
                return Path.Combine(dir, safeName);
            return Path.Combine(dir, safeName + ext);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Player" : name;
        }

        private static bool TryLaunch(BuildTarget target, string path, out string err)
        {
            err = null;
            try
            {
                switch (target)
                {
                    case BuildTarget.StandaloneWindows:
                    case BuildTarget.StandaloneWindows64:
                    case BuildTarget.StandaloneLinux64:
                        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                        return true;
                    case BuildTarget.StandaloneOSX:
                        Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"\"{path}\"", UseShellExecute = false });
                        return true;
                    case BuildTarget.Android:
                        err = "Android: use `adb install -r \"" + path + "\"` then `adb shell am start <package>`";
                        return false;
                    case BuildTarget.WebGL:
                        err = "WebGL: build is a directory — serve with `python -m http.server` or Unity's WebGL test player";
                        return false;
                    default:
                        err = $"--run not supported on target {target}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024UL * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }
}
