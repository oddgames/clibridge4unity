// clibridge4unity - CLI bridge for Unity Editor
// Usage: clibridge4unity <command> [data]       (auto-detects Unity project from current directory)
// Usage: clibridge4unity -d <unity_project_path> <command> [data]  (explicit project path)
// Usage: clibridge4unity -h | --help           (queries bridge for available commands)
// Example: clibridge4unity PING
// Example: clibridge4unity CODE_ANALYZE BridgeServer
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using clibridge4unity;

class Program
{
    // Win32 APIs
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern long GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xD, int yD, int w, int h, IntPtr hdcSrc, int xS, int yS, uint rop);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int maxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")]
    private static extern bool SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool bInvert);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Version from assembly (set in .csproj <Version>)
    private static readonly string CLI_VERSION =
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    // GitHub repository for package installation and updates
    private const string GITHUB_REPO = "oddgames/clibridge4unity";
    private const string UPM_PACKAGE_NAME = "au.com.oddgames.clibridge4unity";

    // Exit codes — distinct codes let AI agents branch on failure type without parsing stderr
    const int EXIT_SUCCESS = 0;           // Command succeeded
    const int EXIT_COMMAND_ERROR = 1;     // Command ran but failed (e.g., "not found")
    const int EXIT_USAGE_ERROR = 2;       // Invalid arguments or usage
    const int EXIT_CONNECTION = 10;       // Pipe connection timeout (Unity not running/responsive)
    const int EXIT_COMPILE_ERROR = 11;    // Unity has compile errors (commands blocked)
    const int EXIT_PLAY_MODE = 12;        // Play mode conflict (need STOP first)
    const int EXIT_SAFE_MODE = 13;        // Unity in Safe Mode (scripts can't run)
    const int EXIT_TIMEOUT = 14;          // Command response timed out

    // Track if last response indicated main thread timeout
    private static bool _lastResponseContainedMainThreadTimeout;
    private static long _preCompileLogId;

    private const uint WM_NULL = 0x0000;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_ACTIVATEAPP = 0x001C;
    private const uint WM_TIMER = 0x0113;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    /// <summary>
    /// Wakes Unity Editor's message pump by sending WM_NULL to its main window.
    /// </summary>
    /// <summary>
    /// Nudge Unity's message pump without stealing focus.
    /// Server-side Win32 timer handles the main thread processing.
    /// This just ensures the message pump is awake.
    /// </summary>
    static void WakeUnityEditor(string projectPath)
    {
        try
        {
            string projectName = Path.GetFileName(Path.GetFullPath(projectPath));

            IntPtr hwnd = IntPtr.Zero;
            foreach (var proc in Process.GetProcessesByName("Unity"))
            {
                if (proc.MainWindowHandle != IntPtr.Zero &&
                    proc.MainWindowTitle.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                {
                    hwnd = proc.MainWindowHandle;
                    break;
                }
            }

            if (hwnd != IntPtr.Zero)
            {
                // Multiple wake strategies — WM_NULL alone doesn't trigger the import pipeline
                PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                // WM_TIMER forces Unity's internal timer tick which processes pending reloads
                PostMessage(hwnd, 0x0113 /*WM_TIMER*/, IntPtr.Zero, IntPtr.Zero);
                // InvalidateRect forces a repaint cycle which triggers EditorApplication.update
                InvalidateRect(hwnd, IntPtr.Zero, false);
            }
        }
        catch { }
    }

    // ───────────────────── Update Check ─────────────────────
    // Architecture:
    //   - Background HttpClient fetch via Task.Run, fires-and-forgets
    //   - Caches response to ~/.clibridge4unity/.last_update_check
    //   - Shows notification from cache (throttled to once per 30 min via .update_notified)
    //   - If CLI exits before fetch completes, the task just aborts — no big deal,
    //     it'll succeed on a longer-running command eventually
    //   - --version calls FetchAndShowUpdate() synchronously (allowed to block)
    //   - Skipped for fast commands: PING, PROBE, DIAG, DISMISS, SCREENSHOT

    static string UpdateCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity");
    static string UpdateCacheFile => Path.Combine(UpdateCacheDir, ".last_update_check");
    static string CompileTimesFile => Path.Combine(UpdateCacheDir, ".compile_times");
    static string UpdateNotifiedFile => Path.Combine(UpdateCacheDir, ".update_notified");

    static void CheckForUpdateInBackground()
    {
        try
        {
            // Show cached notification first (instant, no network)
            ShowCachedUpdateNotice();

            // If cache is fresh enough, skip network fetch
            if (File.Exists(UpdateCacheFile) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(UpdateCacheFile)).TotalMinutes < 30)
                return;

            // Fire-and-forget fetch — aborts if CLI exits first, that's fine
            Task.Run(() => FetchLatestRelease());
        }
        catch { }
    }

    /// <summary>
    /// Synchronous fetch + display for --version (allowed to block up to 5s)
    /// </summary>
    static void FetchAndShowUpdate()
    {
        try
        {
            FetchLatestRelease();
            ShowCachedUpdateNotice(force: true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[update] {ex.GetType().Name}: {ex.Message}"); }
    }

    static void FetchLatestRelease()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "clibridge4unity");
        http.Timeout = TimeSpan.FromSeconds(5);
        string json = http.GetStringAsync(
            $"https://api.github.com/repos/{GITHUB_REPO}/releases/latest").Result;
        Directory.CreateDirectory(UpdateCacheDir);
        File.WriteAllText(UpdateCacheFile, json);
    }

    static void ShowCachedUpdateNotice(bool force = false)
    {
        if (!File.Exists(UpdateCacheFile)) return;

        string json = File.ReadAllText(UpdateCacheFile).Trim();
        string latestTag = ExtractJsonString(json, "tag_name");
        if (string.IsNullOrEmpty(latestTag)) return;

        string latestVersion = latestTag.TrimStart('v');
        if (!Version.TryParse(latestVersion, out var remote) ||
            !Version.TryParse(CLI_VERSION, out var local) ||
            remote <= local)
        {
            if (force) Console.Error.WriteLine("  Up to date");
            return;
        }

        // Throttle display to once per 30 min (unless forced by --version)
        if (!force && File.Exists(UpdateNotifiedFile) &&
            (DateTime.UtcNow - File.GetLastWriteTimeUtc(UpdateNotifiedFile)).TotalMinutes < 30)
            return;

        try { File.WriteAllText(UpdateNotifiedFile, latestVersion); } catch { }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Update available: v{CLI_VERSION} → v{latestVersion}");

        string body = ExtractJsonString(json, "body");
        if (!string.IsNullOrEmpty(body))
        {
            body = body.Replace("\\r\\n", "\n").Replace("\\n", "\n");
            Console.Error.WriteLine();
            int shown = 0;
            foreach (var line in body.Split('\n'))
            {
                if (shown >= 8) { Console.Error.WriteLine("  ..."); break; }
                string l = line.Trim();
                if (l.Length > 0) { Console.Error.WriteLine($"  {l}"); shown++; }
            }
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Run: irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex");
        Console.Error.WriteLine();
    }

    static string ExtractJsonString(string json, string key)
    {
        string search = $"\"{key}\":\"";
        int idx = json.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
        {
            search = $"\"{key}\": \"";
            idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
        }
        int start = idx + search.Length;
        int end = json.IndexOf('"', start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }

    // ───────────────────── Compile Time Tracking ─────────────────────

    static void RecordCompileTime(string projectPath, int seconds)
    {
        try
        {
            string key = Path.GetFileName(Path.GetFullPath(projectPath));
            Directory.CreateDirectory(UpdateCacheDir);

            // Read existing times for this project (keep last 10)
            var times = ReadCompileTimes(key);
            times.Add(seconds);
            if (times.Count > 10) times.RemoveAt(0);

            // Write back all project entries
            var allLines = new List<string>();
            if (File.Exists(CompileTimesFile))
            {
                foreach (var line in File.ReadAllLines(CompileTimesFile))
                {
                    if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                        allLines.Add(line);
                }
            }
            allLines.Add($"{key}:{string.Join(",", times)}");
            File.WriteAllLines(CompileTimesFile, allLines);
        }
        catch { }
    }

    static List<int> ReadCompileTimes(string projectName)
    {
        try
        {
            if (!File.Exists(CompileTimesFile)) return new List<int>();
            foreach (var line in File.ReadAllLines(CompileTimesFile))
            {
                if (line.StartsWith(projectName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    var values = line.Substring(projectName.Length + 1).Split(',');
                    var result = new List<int>();
                    foreach (var v in values)
                        if (int.TryParse(v.Trim(), out var n) && n > 0)
                            result.Add(n);
                    return result;
                }
            }
        }
        catch { }
        return new List<int>();
    }

    static string GetCompileTimeEstimate(string projectPath)
    {
        string key = Path.GetFileName(Path.GetFullPath(projectPath));
        var times = ReadCompileTimes(key);
        if (times.Count == 0) return null;

        int sum = 0;
        foreach (var t in times) sum += t;
        int avg = sum / times.Count;
        int last = times[times.Count - 1];
        return $"~{avg}s (last: {last}s, based on {times.Count} compiles)";
    }

    // ───────────────────── Self-Update ─────────────────────

    static int HandleSelfUpdate()
    {
        Console.WriteLine($"clibridge4unity v{CLI_VERSION} — checking for updates...");

        try
        {
            // Fetch latest release info
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "clibridge4unity");
            string json = http.GetStringAsync(
                $"https://api.github.com/repos/{GITHUB_REPO}/releases/latest").Result;

            string latestVersion = ExtractJsonString(json, "tag_name")?.TrimStart('v');
            if (string.IsNullOrEmpty(latestVersion))
            {
                Console.Error.WriteLine("Error: Could not determine latest version.");
                return EXIT_COMMAND_ERROR;
            }

            if (!Version.TryParse(latestVersion, out var latest) || !Version.TryParse(CLI_VERSION, out var current))
            {
                Console.Error.WriteLine($"Error: Could not parse versions (current={CLI_VERSION}, latest={latestVersion})");
                return EXIT_COMMAND_ERROR;
            }

            if (latest <= current)
            {
                Console.WriteLine($"CLI already up to date (v{CLI_VERSION}).");
                // Still check manifest — CLI may be updated but manifest lagging
                UpdateManifestTag(CLI_VERSION);
                return EXIT_SUCCESS;
            }

            Console.WriteLine($"Updating v{CLI_VERSION} → v{latestVersion}...");

            // Find the exe download URL from release assets
            string exeUrl = null;
            int searchStart = 0;
            while (true)
            {
                int urlIdx = json.IndexOf("\"browser_download_url\"", searchStart, StringComparison.Ordinal);
                if (urlIdx < 0) break;
                string url = ExtractJsonString(json.Substring(urlIdx), "browser_download_url");
                if (url != null && url.EndsWith("clibridge4unity.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = url;
                    break;
                }
                searchStart = urlIdx + 1;
            }

            if (exeUrl == null)
            {
                Console.Error.WriteLine("Error: Could not find exe in release assets.");
                Console.Error.WriteLine($"  Run: irm https://raw.githubusercontent.com/{GITHUB_REPO}/main/install.ps1 | iex");
                return EXIT_COMMAND_ERROR;
            }

            // Download to temp file
            string installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clibridge4unity");
            string exePath = Path.Combine(installDir, "clibridge4unity.exe");
            string tempPath = exePath + ".update";

            Console.WriteLine($"Downloading from GitHub...");
            byte[] exeBytes = http.GetByteArrayAsync(exeUrl).Result;
            Directory.CreateDirectory(installDir);
            File.WriteAllBytes(tempPath, exeBytes);

            // Replace current exe: rename current → .old, rename .update → current
            string oldPath = exePath + ".old";
            if (File.Exists(oldPath)) File.Delete(oldPath);

            if (File.Exists(exePath))
                File.Move(exePath, oldPath);
            File.Move(tempPath, exePath);

            // Clean up old exe
            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }

            Console.WriteLine($"Updated to v{latestVersion}");
            Console.WriteLine($"  {exePath}");

            UpdateManifestTag(latestVersion);
            return EXIT_SUCCESS;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"  Run: irm https://raw.githubusercontent.com/{GITHUB_REPO}/main/install.ps1 | iex");
            return EXIT_COMMAND_ERROR;
        }
    }

    /// <summary>
    /// Updates the UPM package git tag in the current project's manifest.json.
    /// Finds any existing version tag (#vX.Y.Z) and replaces it with the target version.
    /// </summary>
    static void UpdateManifestTag(string targetVersion)
    {
        try
        {
            string projectPath = AutoDetectProjectPath();
            if (projectPath == null) return;

            string manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return;

            string manifest = File.ReadAllText(manifestPath);
            if (!manifest.Contains($"\"{UPM_PACKAGE_NAME}\"")) return;

            string targetTag = $"#v{targetVersion}";
            if (manifest.Contains(targetTag))
            {
                Console.WriteLine($"  UPM package already at v{targetVersion}");
                return;
            }

            // Find and replace any existing version tag (#vX.Y.Z)
            var tagPattern = new System.Text.RegularExpressions.Regex(@"#v\d+\.\d+\.\d+");
            if (tagPattern.IsMatch(manifest))
            {
                manifest = tagPattern.Replace(manifest, targetTag);
                File.WriteAllText(manifestPath, manifest);
                Console.WriteLine($"  Updated UPM package tag in manifest.json → v{targetVersion}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Warning: Could not update manifest.json ({ex.Message})");
        }
    }

    // ───────────────────── Entry Point ─────────────────────

    static int Main(string[] args)
    {
        string projectPath = null;
        string command = null;
        string data = "";
        bool waitForLogs = false;
        string logFilter = "errors";
        int argIndex = 0;

        // Parse arguments
        while (argIndex < args.Length)
        {
            string arg = args[argIndex];

            if (arg == "-d" || arg == "--directory")
            {
                // Explicit Unity project path
                if (argIndex + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Error: -d requires a Unity project path");
                    return EXIT_USAGE_ERROR;
                }
                projectPath = args[++argIndex];
                // Validate it's a Unity project
                if (!Directory.Exists(Path.Combine(projectPath, "Assets")))
                {
                    Console.Error.WriteLine($"Error: Not a Unity project (no Assets folder): {projectPath}");
                    return EXIT_USAGE_ERROR;
                }
                argIndex++;
            }
            else if (arg == "-h" || arg == "--help")
            {
                // Help - need to resolve project path first
                projectPath = projectPath ?? AutoDetectProjectPath();
                if (projectPath == null)
                {
                    PrintUsage();
                    return EXIT_USAGE_ERROR;
                }
                return SendCommand(GeneratePipeName(projectPath), projectPath, "HELP", "");
            }
            else if (arg == "--version")
            {
                Console.WriteLine($"clibridge4unity version {CLI_VERSION}");
                // --version is allowed to block briefly to fetch and show update info
                FetchAndShowUpdate();
                return EXIT_SUCCESS;
            }
            else if (arg == "--wait" || arg == "-w")
            {
                waitForLogs = true;
                argIndex++;
            }
            else if (arg == "--log-filter")
            {
                if (argIndex + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Error: --log-filter requires a filter value (errors, warnings, all)");
                    return EXIT_USAGE_ERROR;
                }
                logFilter = args[++argIndex];
                argIndex++;
            }
            else if (command == null)
            {
                command = arg;
                argIndex++;
            }
            else
            {
                // Rest is data
                data = string.Join(" ", args, argIndex, args.Length - argIndex);
                break;
            }
        }

        if (command == null)
        {
            PrintUsage();
            return EXIT_USAGE_ERROR;
        }

        // Background update check — only for commands that take long enough for it to matter
        // Skip for fast commands: PING, PROBE, DIAG, DISMISS, SCREENSHOT
        string cmdUpper = command.ToUpperInvariant();
        if (cmdUpper != "PING" && cmdUpper != "PROBE" && cmdUpper != "DIAG" &&
            cmdUpper != "DISMISS" && cmdUpper != "SCREENSHOT" && cmdUpper != "UPDATE" &&
            cmdUpper != "SERVE" && cmdUpper != "HOOK" && cmdUpper != "DAEMON")
        {
            CheckForUpdateInBackground();
        }

        // UPDATE: self-update CLI (no Unity needed)
        if (cmdUpper == "UPDATE")
        {
            return HandleSelfUpdate();
        }

        // SERVE: local HTTP file server (no Unity needed)
        if (cmdUpper == "SERVE")
        {
            return ReportServer.Run(data);
        }

        // DAEMON: Roslyn analysis daemon (no Unity needed)
        if (cmdUpper == "DAEMON")
        {
            projectPath = projectPath ?? AutoDetectProjectPath();
            if (projectPath == null)
            {
                Console.Error.WriteLine("Error: Could not detect Unity project.");
                return EXIT_USAGE_ERROR;
            }
            return RoslynDaemon.Run(projectPath, data);
        }

        // HOOK: Claude Code PreToolUse hook handler — reads JSON from stdin, no pipe/project needed
        if (cmdUpper == "HOOK")
        {
            return HandleHook();
        }

        // Auto-detect project path if not specified
        projectPath = projectPath ?? AutoDetectProjectPath();
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: Could not detect Unity project.");
            Console.Error.WriteLine("       Run from a Unity project directory or use -d <path>.");
            return EXIT_USAGE_ERROR;
        }

        // Initialize path shortening for Editor.log parsing and error output
        StackTraceMinimizer.SetPaths(Path.GetFullPath(projectPath));

        string pipeName = GeneratePipeName(projectPath);

        // Quick manifest check: warn if UPM package not installed (skip for SETUP/PREFLIGHT)
        if (!command.Equals("SETUP", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("PREFLIGHT", StringComparison.OrdinalIgnoreCase))
        {
            string manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (File.Exists(manifestPath))
            {
                string manifest = File.ReadAllText(manifestPath);
                if (!manifest.Contains($"\"{UPM_PACKAGE_NAME}\""))
                {
                    Console.Error.WriteLine($"Warning: UPM package not found in manifest.json.");
                    Console.Error.WriteLine($"         Run 'clibridge4unity SETUP' to install it.");
                    Console.Error.WriteLine();
                }
            }
        }

        // DISMISS: close dialogs or click specific buttons — works on ANY Unity process, no pipe needed
        // Usage: DISMISS          — list dialogs, send WM_CLOSE to all
        //        DISMISS Yes      — click button labeled "Yes"
        //        DISMISS "Don't Save" — click button with exact label
        var unityInfo = DetectUnityProcess(projectPath);
        if (command.Equals("DISMISS", StringComparison.OrdinalIgnoreCase))
        {
            var dialogs = unityInfo.Dialogs;
            if (dialogs.Count == 0)
            {
                Console.WriteLine("No dialogs detected.");
                return EXIT_SUCCESS;
            }

            string targetButton = string.IsNullOrWhiteSpace(data) ? null : data.Trim().Trim('"');

            foreach (var dlg in dialogs)
            {
                Console.WriteLine($"Dialog: \"{dlg.Title}\" ({dlg.Width}x{dlg.Height})");

                // Show all controls
                var buttons = new List<(IntPtr hwnd, string text)>();
                foreach (var ctrl in dlg.Controls)
                {
                    if (!ctrl.IsVisible || string.IsNullOrEmpty(ctrl.Text)) continue;
                    string cls = ctrl.ClassName.ToLowerInvariant();
                    Console.WriteLine($"  [{ctrl.ClassName}] \"{ctrl.Text}\"");
                    if (cls.Contains("button"))
                        buttons.Add((ctrl.Hwnd, ctrl.Text));
                }

                if (targetButton != null)
                {
                    // Find and click the specified button
                    var match = buttons.Find(b =>
                        b.text.Equals(targetButton, StringComparison.OrdinalIgnoreCase));
                    if (match.hwnd != IntPtr.Zero)
                    {
                        Console.WriteLine($"Clicking button: \"{match.text}\"");
                        const uint BM_CLICK = 0x00F5;
                        PostMessage(match.hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Button \"{targetButton}\" not found. Available: {string.Join(", ", buttons.Select(b => $"\"{b.text}\""))}");
                        PostMessage(dlg.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                else
                {
                    // No button specified — send WM_CLOSE
                    PostMessage(dlg.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }

            if (targetButton != null)
                Console.WriteLine($"Clicked \"{targetButton}\" on {dialogs.Count} dialog(s).");
            else
                Console.WriteLine($"Sent WM_CLOSE to {dialogs.Count} dialog(s).");
            return EXIT_SUCCESS;
        }

        // LAST: retrieve previous command result — CLI-side, no pipe needed
        if (command.Equals("LAST", StringComparison.OrdinalIgnoreCase))
        {
            string histDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clibridge4unity", "history");
            if (!Directory.Exists(histDir)) { Console.WriteLine("No command history."); return EXIT_SUCCESS; }

            // If data is a number, get that specific entry; otherwise get latest
            string filter = string.IsNullOrWhiteSpace(data) ? null : data.Trim();
            var files = Directory.GetFiles(histDir, "*.txt")
                .OrderByDescending(f => File.GetLastWriteTime(f)).ToArray();

            if (filter != null && filter.All(char.IsDigit))
            {
                int idx = int.Parse(filter);
                if (idx >= 0 && idx < files.Length)
                    Console.Write(File.ReadAllText(files[idx]));
                else
                    Console.Error.WriteLine($"History index {idx} out of range (0-{files.Length - 1})");
                return EXIT_SUCCESS;
            }

            // Filter by command name
            if (filter != null)
                files = files.Where(f => Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (files.Length == 0) { Console.WriteLine("No matching history."); return EXIT_SUCCESS; }
            Console.Write(File.ReadAllText(files[0]));
            return EXIT_SUCCESS;
        }

        // OPEN: launch or restart Unity with this project — CLI-side, no pipe needed
        if (command.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            return HandleOpen(projectPath, unityInfo);
        }

        // WAKEUP: bring Unity to foreground — CLI-side, no pipe needed
        // WAKEUP refresh — also sends Ctrl+R to force asset refresh/recompile
        if (command.Equals("WAKEUP", StringComparison.OrdinalIgnoreCase))
        {
            bool refresh = data != null && data.Contains("refresh", StringComparison.OrdinalIgnoreCase);
            return HandleWakeup(projectPath, refresh);
        }

        // Pre-flight: check if Unity is running for this project (instant, no pipe needed)
        if (unityInfo.State == UnityProcessState.NotRunning)
        {
            Console.Error.WriteLine("Error: Unity is not running. Use 'clibridge4unity OPEN' to launch Unity.");
            Console.Error.WriteLine($"       No Unity process found for project: {Path.GetFileName(Path.GetFullPath(projectPath))}");
            Console.Error.WriteLine("       Open Unity Editor with this project first.");
            return EXIT_CONNECTION;
        }
        if (unityInfo.State == UnityProcessState.DifferentProject)
        {
            Console.Error.WriteLine($"Error: Unity is running but not with this project.");
            Console.Error.WriteLine($"       Expected project: {Path.GetFileName(Path.GetFullPath(projectPath))}");
            foreach (var title in unityInfo.OpenProjects)
                Console.Error.WriteLine($"       Unity has open: {title}");
            if (!string.IsNullOrEmpty(unityInfo.ImportStatus))
                Console.Error.WriteLine($"       Status: {unityInfo.ImportStatus}");
            PrintDialogInfo(unityInfo.Dialogs);
            Console.Error.WriteLine($"       Open this project in Unity or use -d to specify the correct path.");
            return EXIT_CONNECTION;
        }
        if (unityInfo.State == UnityProcessState.Importing)
        {
            // If COMPILE was requested and Unity is already compiling, just wait for it
            bool isAlreadyCompiling = unityInfo.ImportStatus != null &&
                (unityInfo.ImportStatus.Contains("Compil") || unityInfo.ImportStatus.Contains("Reload"));
            if ((cmdUpper == "COMPILE" || cmdUpper == "REFRESH") && isAlreadyCompiling)
            {
                Console.Error.WriteLine($"[CLI] Unity is already compiling ({unityInfo.ImportStatus}). Waiting for it to finish...");
                return WaitForCompilationAndReconnect(pipeName, projectPath, 300, DateTime.Now);
            }

            Console.Error.WriteLine($"Error: Unity is busy — {unityInfo.ImportStatus}");
            if (unityInfo.RecentErrors.Count > 0)
            {
                Console.Error.WriteLine("       Recent compile errors (from Editor.log):");
                foreach (var err in unityInfo.RecentErrors)
                    Console.Error.WriteLine($"         {err}");
            }
            string estimate = GetCompileTimeEstimate(projectPath);
            if (estimate == null && unityInfo.HeartbeatCompileTimeAvg > 0)
                estimate = $"~{unityInfo.HeartbeatCompileTimeAvg}s (from Unity)";
            Console.Error.WriteLine("       Wait for it to finish, then retry the command.");
            Console.Error.WriteLine($"       status: busy");
            Console.Error.WriteLine($"       retry: true");
            if (estimate != null)
                Console.Error.WriteLine($"       estimatedWait: {estimate}");
            else
                Console.Error.WriteLine($"       estimatedWait: unknown (no compile history yet)");
            return EXIT_TIMEOUT;
        }
        // When importing, show any compile errors found in logs
        if (unityInfo.RecentErrors.Count > 0)
        {
            Console.Error.WriteLine($"       Compile errors found in logs:");
            foreach (var err in unityInfo.RecentErrors)
                Console.Error.WriteLine($"         {err}");
        }

        // Wake Unity's message pump before any command (ensures responsiveness in background)
        WakeUnityEditor(projectPath);

        // SETUP (or INSTALL): install UPM package + generate CLAUDE.md
        if (command.Equals("SETUP", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("INSTALL", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSetup(pipeName, projectPath, data);
        }

        // Pre-flight: report dialogs as warning (don't block — let the command try)
        if (unityInfo.Dialogs.Count > 0)
        {
            Console.Error.WriteLine($"Warning: Unity has {unityInfo.Dialogs.Count} dialog(s) open:");
            PrintDialogInfo(unityInfo.Dialogs);
        }

        // SCREENSHOT: smart routing
        // Known view keywords → CLI-side PrintWindow (fast, no pipe)
        // Everything else → server-side render, with CLI fallback if pipe fails
        if (command.Equals("SCREENSHOT", StringComparison.OrdinalIgnoreCase))
        {
            string view = data?.Trim() ?? "";
            string viewLower = view.ToLowerInvariant();
            // "game" routes to server (can create the tab + render camera)
            // Other known views use fast CLI-side Win32 capture
            string[] cliViews = { "", "editor", "scene", "inspector", "hierarchy", "console", "project", "profiler" };
            if (Array.Exists(cliViews, v => v == viewLower))
            {
                return HandleScreenshot(projectPath);
            }

            // Server-side render — try pipe, fallback to CLI capture
            try
            {
                return SendCommand(pipeName, projectPath, "SCREENSHOT", data);
            }
            catch
            {
                Console.Error.WriteLine("Warning: Could not connect to Unity for server-side render. Falling back to window capture.");
                return HandleScreenshot(projectPath);
            }
        }

        // --wait flag: send command, wait for reconnection, then fetch logs
        if (waitForLogs)
        {
            return SendCommandAndWaitForLogs(pipeName, projectPath, command, data, logFilter);
        }

        // Snapshot log position before commands that trigger recompilation
        if (command == "COMPILE" || command == "REFRESH")
        {
            _preCompileLogId = 0;
            try
            {
                string logSnapshot = SendCommandGetResponse(pipeName, "LOG", "last:1");
                if (logSnapshot != null)
                {
                    foreach (var line in logSnapshot.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("lastId:", StringComparison.OrdinalIgnoreCase))
                            long.TryParse(trimmed.Substring("lastId:".Length).Trim(), out _preCompileLogId);
                    }
                }
            }
            catch { }
        }

        // CODE_SEARCH / CODE_ANALYZE: daemon → single-pass Roslyn fallback
        if (command.Equals("CODE_SEARCH", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("CODE_ANALYZE", StringComparison.OrdinalIgnoreCase))
        {
            string endpoint = command.Equals("CODE_ANALYZE", StringComparison.OrdinalIgnoreCase) ? "analyze" : "search";

            // Try daemon first
            string daemonPipe = RoslynDaemon.GetRunningPipe(projectPath);
            if (daemonPipe == null)
            {
                // Auto-start daemon in background
                Console.Error.WriteLine("[roslyn] Starting daemon...");
                daemonPipe = RoslynDaemon.StartBackground(projectPath);
            }

            if (daemonPipe != null)
            {
                string dResult = RoslynDaemon.Query(daemonPipe, endpoint, data ?? "");
                if (dResult != null)
                {
                    Console.WriteLine(dResult);
                    return dResult.StartsWith("Error:") ? EXIT_COMMAND_ERROR : EXIT_SUCCESS;
                }
            }

            // Fallback: single-pass Roslyn (no daemon available)
            Console.Error.WriteLine("[roslyn] Daemon unavailable, using single-pass analysis");
            string fallbackResult = command.Equals("CODE_ANALYZE", StringComparison.OrdinalIgnoreCase)
                ? RoslynAnalyzer.Analyze(projectPath, data ?? "")
                : RoslynAnalyzer.Search(projectPath, data ?? "");
            Console.WriteLine(fallbackResult);
            return fallbackResult.StartsWith("Error:") ? EXIT_COMMAND_ERROR : EXIT_SUCCESS;
        }

        int result = SendCommand(pipeName, projectPath, command, data);

        // If main thread timed out, just nudge — don't steal focus
        if (result != EXIT_SUCCESS && _lastResponseContainedMainThreadTimeout)
        {
            _lastResponseContainedMainThreadTimeout = false;
            WakeUnityEditor(projectPath);
        }

        return result;
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine($"clibridge4unity v{CLI_VERSION} - CLI bridge for Unity Editor");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: clibridge4unity <command> [data]");
        Console.Error.WriteLine("       clibridge4unity -d <unity_project_path> <command> [data]");
        Console.Error.WriteLine("       clibridge4unity -h | --help");
        Console.Error.WriteLine("       clibridge4unity --version");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  -d, --directory <path>  Specify Unity project path");
        Console.Error.WriteLine("  -h, --help              Show available commands from Unity");
        Console.Error.WriteLine("  -w, --wait              Wait for completion and show logs (for COMPILE/REFRESH)");
        Console.Error.WriteLine("  --log-filter <filter>   Log filter for --wait: errors (default), warnings, all");
        Console.Error.WriteLine("  --version               Show version information");
        Console.Error.WriteLine();
        Console.Error.WriteLine("CLI-side commands (no Unity pipe needed):");
        Console.Error.WriteLine("  SETUP                      Install UPM package + generate CLAUDE.md");
        Console.Error.WriteLine("  UPDATE                     Self-update CLI + UPM package");
        Console.Error.WriteLine("  SERVE [--port N] [--ttl M] Start local file server (port 8420)");
        Console.Error.WriteLine("  WAKEUP                     Bring Unity to foreground (targets -d project)");
        Console.Error.WriteLine("  WAKEUP refresh             Bring to foreground + force recompile (Ctrl+R)");
        Console.Error.WriteLine("  DISMISS [button]           Close modal dialogs or click specific button");
        Console.Error.WriteLine("  SCREENSHOT [view]          Capture Unity window screenshot");
        Console.Error.WriteLine("  LAST [command|index]       Retrieve previous command result from history");
        Console.Error.WriteLine("  OPEN                       Launch Unity (or restart if in Safe Mode)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Key bridge commands (requires Unity):");
        Console.Error.WriteLine("  PING / STATUS / HELP       Connection and status");
        Console.Error.WriteLine("  CODE_EXEC_RETURN <code>    Execute C# and return result");
        Console.Error.WriteLine("    --inspect [depth]        Dump result object tree");
        Console.Error.WriteLine("    --trace [--maxlines N]   Line-by-line execution trace");
        Console.Error.WriteLine("  TEST [filter]              Run tests (streaming with progress/ETA)");
        Console.Error.WriteLine("  TEST list [filter]         List available tests");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Exit codes:");
        Console.Error.WriteLine("  0    Success");
        Console.Error.WriteLine("  1    Command error (ran but failed)");
        Console.Error.WriteLine("  2    Invalid usage / bad arguments");
        Console.Error.WriteLine("  10   Connection failed (Unity not running)");
        Console.Error.WriteLine("  11   Compile errors (commands blocked)");
        Console.Error.WriteLine("  12   Play mode conflict (STOP first)");
        Console.Error.WriteLine("  13   Safe Mode (fix errors in editor)");
        Console.Error.WriteLine("  14   Command timed out");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Run with -h inside a Unity project for the full command list.");
    }

    static string AutoDetectProjectPath()
    {
        // Walk up directory tree looking for Unity project indicators
        string dir = Directory.GetCurrentDirectory();

        while (!string.IsNullOrEmpty(dir))
        {
            // Check for Assets folder (Unity project root indicator)
            string assetsPath = Path.Combine(dir, "Assets");
            if (Directory.Exists(assetsPath))
            {
                return dir;
            }

            // Move up one directory
            string parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir) break;
            dir = parent;
        }

        return null;
    }

    /// <summary>
    /// Launch or restart Unity with the specified project.
    /// Reads ProjectVersion.txt to find the required Unity version, locates it via Unity Hub.
    /// If Unity is already running with this project, kills it first (handles Safe Mode).
    /// </summary>
    static int HandleOpen(string projectPath, UnityProcessInfo unityInfo)
    {
        string projectName = Path.GetFileName(Path.GetFullPath(projectPath));

        // Read required Unity version from ProjectVersion.txt
        string versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile))
        {
            Console.Error.WriteLine($"Error: Not a Unity project (no ProjectSettings/ProjectVersion.txt)");
            return EXIT_USAGE_ERROR;
        }

        string requiredVersion = null;
        foreach (var line in File.ReadLines(versionFile))
        {
            if (line.StartsWith("m_EditorVersion:"))
            {
                requiredVersion = line.Substring("m_EditorVersion:".Length).Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(requiredVersion))
        {
            Console.Error.WriteLine("Error: Cannot read Unity version from ProjectVersion.txt");
            return EXIT_COMMAND_ERROR;
        }

        // Find Unity editor executable
        string unityExe = FindUnityEditor(requiredVersion);
        if (unityExe == null)
        {
            Console.Error.WriteLine($"Error: Unity {requiredVersion} not found.");
            Console.Error.WriteLine("  Searched: C:\\Program Files\\Unity\\Hub\\Editor\\");
            return EXIT_COMMAND_ERROR;
        }

        // If Unity is already running with this project, kill it first
        if (unityInfo.State == UnityProcessState.Running ||
            unityInfo.State == UnityProcessState.Importing)
        {
            Console.Error.WriteLine($"Closing Unity ({projectName})...");
            foreach (uint pid in unityInfo.Pids)
            {
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    proc.Kill();
                    proc.WaitForExit(10000);
                }
                catch { }
            }
            // Wait for process to fully exit
            System.Threading.Thread.Sleep(2000);
        }

        // Launch Unity
        string fullProjectPath = Path.GetFullPath(projectPath);
        Console.Error.WriteLine($"Launching Unity {requiredVersion} with {projectName}...");

        var startInfo = new ProcessStartInfo
        {
            FileName = unityExe,
            Arguments = $"-projectPath \"{fullProjectPath}\"",
            UseShellExecute = false,
            CreateNoWindow = false
        };

        try
        {
            Process.Start(startInfo);
            Console.WriteLine($"Unity {requiredVersion} launched for {projectName}");
            Console.WriteLine("Waiting for bridge to start...");

            // Wait for bridge pipe to become available
            string pipeName = GeneratePipeName(projectPath);
            for (int i = 0; i < 60; i++) // up to 60 seconds
            {
                System.Threading.Thread.Sleep(2000);
                try
                {
                    using var testPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                    testPipe.Connect(1000);
                    Console.WriteLine($"Bridge connected after {(i + 1) * 2}s");
                    return EXIT_SUCCESS;
                }
                catch { }

                if (i % 5 == 4)
                    Console.Error.Write(".");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Unity launched but bridge not yet available. Run PING to check.");
            return EXIT_SUCCESS;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error launching Unity: {ex.Message}");
            return EXIT_COMMAND_ERROR;
        }
    }

    static string FindUnityEditor(string version)
    {
        // Search Unity Hub installation paths
        string[] searchPaths = {
            Path.Combine("C:", "Program Files", "Unity", "Hub", "Editor"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor"),
        };

        foreach (var basePath in searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            // Exact version match
            string versionPath = Path.Combine(basePath, version, "Editor", "Unity.exe");
            if (File.Exists(versionPath)) return versionPath;

            // Try without the revision suffix (e.g., 6000.3.10f1 -> 6000.3.10)
            string baseVersion = version.Split('f', 'a', 'b', 'p')[0];
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.StartsWith(baseVersion))
                {
                    string exe = Path.Combine(dir, "Editor", "Unity.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }

        return null;
    }

    static void SaveCommandHistory(string command, string data, string response)
    {
        try
        {
            string histDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clibridge4unity", "history");
            Directory.CreateDirectory(histDir);

            string timestamp = DateTime.Now.ToString("HHmmss");
            string filename = $"{timestamp}_{command}.txt";
            File.WriteAllText(Path.Combine(histDir, filename), response);

            // Keep only last 20 entries
            var files = Directory.GetFiles(histDir, "*.txt")
                .OrderByDescending(f => File.GetLastWriteTime(f)).ToArray();
            for (int i = 20; i < files.Length; i++)
                try { File.Delete(files[i]); } catch { }
        }
        catch { }
    }

    static string GeneratePipeName(string projectPath)
    {
        // Must match Unity server's pipe name generation exactly
        // Normalize path: lowercase, backslashes, no trailing slash
        string normalizedPath = Path.GetFullPath(projectPath).ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
        int hash = GetDeterministicHashCode(normalizedPath);
        return $"UnityBridge_{Environment.UserName}_{hash:X8}";
    }

    /// <summary>
    /// Deterministic hash that works identically across .NET runtimes.
    /// This MUST match the algorithm in Unity's BridgeServer.cs exactly.
    /// </summary>
    static int GetDeterministicHashCode(string str)
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

    static int SendCommand(string pipeName, string projectPath, string command, string data)
    {
        try
        {
            // For CODE_EXEC commands, resolve file paths and fix shell mangling
            if (!string.IsNullOrEmpty(data) &&
                (command == "CODE_EXEC" || command == "CODE_EXEC_RETURN"))
            {
                // Detect file path input:
                //   @script.cs           — explicit @ prefix
                //   script.cs            — bare filename
                //   ./script.cs          — relative path
                //   C:\path\script.cs    — absolute path
                // Strip --flags from file path detection (flags are for the server)
                string dataForFileCheck = data;
                string trailingFlags = "";
                int flagIdx = dataForFileCheck.IndexOf(" --");
                if (flagIdx > 0)
                {
                    trailingFlags = dataForFileCheck.Substring(flagIdx);
                    dataForFileCheck = dataForFileCheck.Substring(0, flagIdx).Trim();
                }

                string fileCandidate = dataForFileCheck.StartsWith("@") ? dataForFileCheck.Substring(1).Trim() : dataForFileCheck.Trim();
                bool looksLikeFile = dataForFileCheck.StartsWith("@")
                    || (fileCandidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !fileCandidate.Contains("\n")
                        && fileCandidate.Length < 260);

                if (looksLikeFile && File.Exists(Path.GetFullPath(fileCandidate)))
                {
                    data = $"@{Path.GetFullPath(fileCandidate)}{trailingFlags}";
                }
                else if (dataForFileCheck.StartsWith("@"))
                {
                    Console.Error.WriteLine($"Error: File not found: {Path.GetFullPath(fileCandidate)}");
                    return EXIT_USAGE_ERROR;
                }
                else
                {
                    // Inline code — fix shell mangling and write to temp file
                    // Strip --flags before writing (they go after @path, not in the file)
                    string codeForFile = dataForFileCheck;
                    if (codeForFile.Contains("\\\""))
                    {
                        codeForFile = codeForFile.Replace("$\\\"", "$\"");
                        codeForFile = codeForFile.Replace("\\\"", "\"");
                    }

                    string tempFile = Path.Combine(Path.GetTempPath(), $"clibridge4unity_code_{Guid.NewGuid():N}.cs");
                    File.WriteAllText(tempFile, codeForFile);
                    data = $"@{tempFile}{trailingFlags}";
                }
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(1000);

            // Send command
            string message = string.IsNullOrEmpty(data) ? command : $"{command}|{data}";
            byte[] msgBytes = Encoding.UTF8.GetBytes(message + "\n");
            pipe.Write(msgBytes, 0, msgBytes.Length);
            pipe.Flush();

            // Read the server's timeout hint, then read the actual response
            int readTimeoutMs = 10000; // initial timeout for the hint itself
            using var cts = new CancellationTokenSource(readTimeoutMs);
            var responseBuilder = new StringBuilder();
            byte[] buffer = new byte[4096];
            bool gotTimeoutHint = false;
            try
            {
                while (true)
                {
                    var readTask = pipe.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                    readTask.Wait(cts.Token);
                    int bytesRead = readTask.Result;
                    if (bytesRead == 0) break;
                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Parse timeout hint from server (first message: "__timeout:30\n")
                    if (!gotTimeoutHint && chunk.StartsWith("__timeout:"))
                    {
                        int nlIdx = chunk.IndexOf('\n');
                        if (nlIdx > 0)
                        {
                            string hintStr = chunk.Substring(10, nlIdx - 10);
                            if (int.TryParse(hintStr, out int hintSec))
                                readTimeoutMs = hintSec * 1000;
                            gotTimeoutHint = true;
                            cts.CancelAfter(readTimeoutMs);
                            // Process any data after the hint line
                            chunk = chunk.Substring(nlIdx + 1);
                            if (chunk.Length == 0) continue;
                        }
                    }

                    Console.Write(chunk);
                    responseBuilder.Append(chunk);

                    // Reset idle timeout on each chunk (supports streaming commands)
                    cts.CancelAfter(readTimeoutMs);
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"\nError: Command '{command}' timed out after {readTimeoutMs / 1000}s.");
                Console.Error.Write(BuildDiagnosticReport(projectPath));
                return EXIT_TIMEOUT;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                Console.Error.WriteLine($"\nError: Command '{command}' timed out after {readTimeoutMs / 1000}s.");
                Console.Error.Write(BuildDiagnosticReport(projectPath));
                return EXIT_TIMEOUT;
            }

            // Check if response indicates we need to wait for reconnection
            string response = responseBuilder.ToString();
            if (ShouldWaitForReconnection(response, out int timeoutSeconds))
            {
                return WaitForCompilationAndReconnect(pipeName, projectPath, timeoutSeconds);
            }

            // Show notification for screenshots with image paths
            if (response.Contains(".png"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(response,
                    @"([A-Za-z]:\\[^\s\n]+\.png|/[^\s\n]+\.png)");
                if (match.Success && File.Exists(match.Value))
                    ShowNotification("Screenshot", Path.GetFileName(match.Value), match.Value);
            }

            // Detect main thread timeout for auto-retry
            if (response.Contains("Main thread timed out"))
            {
                _lastResponseContainedMainThreadTimeout = true;
                return EXIT_TIMEOUT;
            }

            // Save result to history for retrieval via LAST command
            SaveCommandHistory(command, data, response);

            return EXIT_SUCCESS;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Error: Pipe connection timed out for command '{command}'.");
            Console.Error.Write(BuildDiagnosticReport(projectPath));
            Console.Error.WriteLine("action: Try 'clibridge4unity WAKEUP refresh' to wake Unity and force recompile.");
            return EXIT_CONNECTION;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Error: Command '{command}' timed out.");
            Console.Error.Write(BuildDiagnosticReport(projectPath));
            return EXIT_TIMEOUT;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: Pipe broken during '{command}': {ex.Message}");
            Console.Error.Write(BuildDiagnosticReport(projectPath));
            return EXIT_CONNECTION;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.Write(BuildDiagnosticReport(projectPath));
            return EXIT_COMMAND_ERROR;
        }
    }

    static int SendCommandAndWaitForLogs(string pipeName, string projectPath, string command, string data, string logFilter)
    {
        // Step 1: Snapshot current log position before triggering the command
        long logSinceId = 0;
        try
        {
            string logSnapshot = SendCommandGetResponse(pipeName, "LOG", "last:0");
            if (logSnapshot != null)
            {
                foreach (var line in logSnapshot.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("lastId:", StringComparison.OrdinalIgnoreCase))
                    {
                        long.TryParse(trimmed.Substring("lastId:".Length).Trim(), out logSinceId);
                    }
                }
            }
        }
        catch
        {
            // If we can't get the snapshot, we'll just get all logs after reconnection
        }

        // Step 2: Send the actual command (e.g., COMPILE)
        int result = SendCommand(pipeName, projectPath, command, data);

        // Step 3: If the command triggered reconnection, WaitForCompilationAndReconnect already handled it.
        // Now fetch logs since our snapshot.
        Console.Error.WriteLine($"\n[CLI] Fetching {logFilter} logs...");
        try
        {
            // Give the server a moment to be fully ready after reconnection
            Thread.Sleep(500);

            string logQuery = logSinceId > 0 ? $"since:{logSinceId}" : "all";
            string logResponse = SendCommandGetResponse(pipeName, "LOG", logQuery);

            if (logResponse != null)
            {
                // If user wants filtered view, parse and filter
                if (logFilter == "errors")
                {
                    PrintFilteredLogs(logResponse, onlyErrors: true);
                }
                else if (logFilter == "warnings")
                {
                    PrintFilteredLogs(logResponse, onlyErrors: false);
                }
                else
                {
                    Console.WriteLine(logResponse);
                }

                // Return non-zero if there were errors
                if (HasErrors(logResponse))
                    return EXIT_COMPILE_ERROR;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CLI] Could not fetch logs: {ex.Message}");
            return result;
        }

        return result;
    }

    static string SendCommandGetResponse(string pipeName, string command, string data, int timeoutMs = 15000)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);

        string message = string.IsNullOrEmpty(data) ? command : $"{command}|{data}";
        byte[] msgBytes = Encoding.UTF8.GetBytes(message + "\n");
        pipe.Write(msgBytes, 0, msgBytes.Length);
        pipe.Flush();

        var responseBuilder = new StringBuilder();
        byte[] buffer = new byte[4096];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var readTask = pipe.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                readTask.Wait(cts.Token);
                int bytesRead = readTask.Result;
                if (bytesRead == 0) break;
                responseBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }
        }
        catch (OperationCanceledException) { }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { }

        return responseBuilder.ToString();
    }

    static void PrintFilteredLogs(string logResponse, bool onlyErrors)
    {
        foreach (var line in logResponse.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Print summary lines
            if (trimmed.StartsWith("logCount:") || trimmed.StartsWith("lastId:") ||
                trimmed.StartsWith("errors:") || trimmed.StartsWith("warnings:") ||
                trimmed.StartsWith("info:") || trimmed == "---")
            {
                Console.WriteLine(trimmed);
                continue;
            }

            // Print stack trace lines (indented)
            if (trimmed.StartsWith("    "))
            {
                Console.WriteLine(trimmed);
                continue;
            }

            // Filter log entries by type tag
            if (onlyErrors)
            {
                if (trimmed.Contains("[ERROR]") || trimmed.Contains("[EXCEPTION]") || trimmed.Contains("[ASSERT]"))
                    Console.WriteLine(trimmed);
            }
            else
            {
                // warnings = warnings + errors
                if (trimmed.Contains("[ERROR]") || trimmed.Contains("[EXCEPTION]") || trimmed.Contains("[ASSERT]") ||
                    trimmed.Contains("[WARNING]"))
                    Console.WriteLine(trimmed);
            }
        }
    }

    static bool HasErrors(string logResponse)
    {
        foreach (var line in logResponse.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("errors:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed.Substring("errors:".Length).Trim(), out int errorCount))
                    return errorCount > 0;
            }
        }
        return false;
    }

    static bool ShouldWaitForReconnection(string response, out int timeoutSeconds)
    {
        timeoutSeconds = 0;

        // Parse Unity's plain text response format: "propertyName: value"
        bool hasReconnect = false;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("reconnect:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed.Substring("reconnect:".Length).Trim();
                hasReconnect = value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (trimmed.StartsWith("timeoutSeconds:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed.Substring("timeoutSeconds:".Length).Trim();
                if (int.TryParse(value, out int timeout))
                {
                    timeoutSeconds = timeout;
                }
            }
        }

        return hasReconnect && timeoutSeconds > 0;
    }

    static int WaitForCompilationAndReconnect(string pipeName, string projectPath, int timeoutSeconds)
    {
        return WaitForCompilationAndReconnect(pipeName, projectPath, timeoutSeconds, DateTime.Now);
    }

    static int WaitForCompilationAndReconnect(string pipeName, string projectPath, int timeoutSeconds, DateTime requestedAt)
    {
        Console.WriteLine($"\n[CLI] Waiting for Unity to complete compilation (up to {timeoutSeconds} seconds)...");
        Console.WriteLine($"[CLI] Pipe name: {pipeName}");

        int elapsed = 0;
        int pollInterval = 1000; // Check every second
        int lastUpdateSeconds = 0;
        int attemptCount = 0;
        bool hasConnectedOnce = false;
        int idleStaleSeconds = 0; // how long Unity has been idle with stale timestamp
        bool hasRetriggered = false;

        while (elapsed < timeoutSeconds * 1000)
        {
            Thread.Sleep(pollInterval);
            elapsed += pollInterval;
            attemptCount++;
            int currentSeconds = elapsed / 1000;

            // Wake Unity so it processes compilation even in background
            WakeUnityEditor(projectPath);

            // Try to reconnect and check status
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                pipe.Connect(500); // Short timeout for reconnect attempts

                if (!hasConnectedOnce)
                {
                    Console.WriteLine($"[CLI] Reconnected to Unity after {currentSeconds} seconds (attempt #{attemptCount})");
                    hasConnectedOnce = true;
                }

                // Send STATUS command
                byte[] msgBytes = Encoding.UTF8.GetBytes("STATUS\n");
                pipe.Write(msgBytes, 0, msgBytes.Length);
                pipe.Flush();

                // Read response
                var responseBuilder = new StringBuilder();
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = pipe.Read(buffer, 0, buffer.Length)) > 0)
                {
                    responseBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                }

                string statusResponse = responseBuilder.ToString();

                // Check for compile errors (terminal state — don't keep waiting)
                if (HasCompileErrors(statusResponse, out int errorCount))
                {
                    Console.Error.WriteLine($"\n[CLI] Compilation finished with {errorCount} error(s) in {currentSeconds} seconds");
                    Console.Error.WriteLine($"[CLI] Total connection attempts: {attemptCount}");
                    Console.Error.WriteLine();

                    // Fetch and show the actual errors
                    string errors = SendCommandGetResponse(pipeName, "LOG", "errors");
                    if (errors != null)
                        Console.Error.WriteLine(errors);
                    else
                        Console.Error.WriteLine(statusResponse);

                    Console.Error.WriteLine("\nFix the errors above, then run COMPILE.");
                    return EXIT_COMPILE_ERROR;
                }

                // Check if Unity entered play mode (shouldn't compile during play)
                if (statusResponse.Contains("isPlaying: True"))
                {
                    Console.Error.WriteLine($"\n[CLI] Unity entered play mode — cannot compile. Use STOP first.");
                    return EXIT_PLAY_MODE;
                }

                // Parse status to check if still compiling
                if (IsCompilationComplete(statusResponse, requestedAt))
                {
                    Console.WriteLine($"[CLI] Compilation completed in {currentSeconds} seconds");
                    Console.WriteLine($"[CLI] Total connection attempts: {attemptCount}");
                    RecordCompileTime(projectPath, currentSeconds);
                    Console.WriteLine();
                    Console.WriteLine(statusResponse);

                    // Wait a moment for post-compilation logs to appear, then fetch errors/warnings
                    Thread.Sleep(500);
                    string logQuery = _preCompileLogId > 0 ? $"since:{_preCompileLogId}" : "errors";
                    string logs = SendCommandGetResponse(pipeName, "LOG", logQuery);
                    if (logs != null)
                    {
                        // Filter to only error/warning/exception entries
                        var logLines = logs.Split('\n');
                        var filteredLines = new List<string>();
                        foreach (var line in logLines)
                        {
                            if (line.Contains("[ERROR]") || line.Contains("[WARNING]") || line.Contains("[EXCEPTION]"))
                                filteredLines.Add(line);
                            else if (filteredLines.Count > 0 && line.StartsWith("    "))
                                filteredLines.Add(line); // stack trace continuation
                        }
                        if (filteredLines.Count > 0)
                        {
                            Console.WriteLine();
                            Console.WriteLine("--- Compilation errors/warnings ---");
                            foreach (var line in filteredLines)
                                Console.WriteLine(line);
                        }
                    }

                    return EXIT_SUCCESS;
                }
                else
                {
                    // Check if Unity is idle with a stale compile timestamp
                    bool isIdle = statusResponse.Contains("isCompiling: False");
                    if (isIdle)
                    {
                        idleStaleSeconds++;
                        if (idleStaleSeconds >= 15 && !hasRetriggered)
                        {
                            // Unity has been idle for 15s without compiling our request.
                            // Re-send COMPILE — original request was likely lost during domain reload.
                            Console.WriteLine($"[CLI] Unity idle for {idleStaleSeconds}s without compiling. Re-triggering...");
                            hasRetriggered = true;
                            requestedAt = DateTime.Now;
                            SendCommandGetResponse(pipeName, "COMPILE", "");
                        }
                        else if (hasRetriggered && idleStaleSeconds >= 30)
                        {
                            // Already re-triggered and waited another 15s. Accept as done.
                            Console.WriteLine($"[CLI] No compilation needed (Unity reports no changes).");
                            Console.WriteLine(statusResponse);
                            return EXIT_SUCCESS;
                        }
                    }
                    else
                    {
                        idleStaleSeconds = 0; // reset when actually compiling
                    }
                    // Parse compileTimeAvg from status for ETA
                    string eta = "";
                    foreach (var line in statusResponse.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("compileTimeAvg:", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = t.Substring("compileTimeAvg:".Length).Trim().TrimEnd('s');
                            if (int.TryParse(val, out int avgSec) && avgSec > currentSeconds)
                                eta = $", ~{avgSec - currentSeconds}s remaining";
                            else if (avgSec > 0)
                                eta = $", avg {avgSec}s";
                        }
                    }
                    Console.WriteLine($"[CLI] Unity is still compiling... ({currentSeconds}s elapsed{eta})");
                }
            }
            catch (TimeoutException)
            {
                // Print status update every 5 seconds when not connected
                if (currentSeconds - lastUpdateSeconds >= 5)
                {
                    Console.WriteLine($"[CLI] Waiting for Unity to restart... ({currentSeconds}s / {timeoutSeconds}s, attempt #{attemptCount})");
                    lastUpdateSeconds = currentSeconds;
                }
            }
            catch (Exception ex)
            {
                // Print detailed error for unexpected exceptions
                if (currentSeconds - lastUpdateSeconds >= 5)
                {
                    Console.WriteLine($"[CLI] Connection error: {ex.GetType().Name} - {ex.Message} ({currentSeconds}s / {timeoutSeconds}s)");
                    lastUpdateSeconds = currentSeconds;
                }
            }
        }

        Console.Error.WriteLine($"\n[CLI] Error: Timeout after {timeoutSeconds} seconds waiting for compilation");
        Console.Error.WriteLine($"[CLI] Total connection attempts: {attemptCount}");
        Console.Error.WriteLine($"[CLI] Reconnected at least once: {hasConnectedOnce}");
        return EXIT_TIMEOUT;
    }

    static bool HasCompileErrors(string statusResponse, out int errorCount)
    {
        errorCount = 0;
        bool isCompiling = false;
        bool hasErrors = false;

        foreach (var line in statusResponse.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("isCompiling:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["isCompiling:".Length..].Trim();
                isCompiling = value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (trimmed.StartsWith("hasCompileErrors:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["hasCompileErrors:".Length..].Trim();
                hasErrors = value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (trimmed.StartsWith("compileErrorCount:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["compileErrorCount:".Length..].Trim();
                int.TryParse(value, out errorCount);
            }
        }

        // Only trust error count when compilation is fully done
        // During compilation, early assemblies may have errors while later ones haven't compiled yet
        if (isCompiling) return false;

        return hasErrors && errorCount > 0;
    }

    static bool IsCompilationComplete(string statusResponse, DateTime? requestedAt = null)
    {
        bool isCompiling = true;
        DateTime? lastCompileFinished = null;

        foreach (var line in statusResponse.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("isCompiling:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["isCompiling:".Length..].Trim();
                isCompiling = !value.Equals("False", StringComparison.OrdinalIgnoreCase);
            }
            else if (trimmed.StartsWith("lastCompileFinished:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["lastCompileFinished:".Length..].Trim();
                if (DateTime.TryParse(value, out var dt))
                    lastCompileFinished = dt;
            }
        }

        if (isCompiling) return false;

        // isCompiling is false — Unity is not currently compiling.
        // If we have a requestedAt time, do a sanity check that compilation finished
        // at or after our request. Use 2-second tolerance for clock/timestamp precision.
        if (requestedAt.HasValue && lastCompileFinished.HasValue)
        {
            if (lastCompileFinished.Value < requestedAt.Value.AddSeconds(-2))
            {
                // Unity is idle but last compile was before our request.
                // Could be: (a) compile hasn't started yet, or (b) request was lost.
                // Return false to keep waiting — caller handles re-trigger after timeout.
                return false;
            }
        }

        // If isCompiling is false and lastCompileFinished is "never" or missing,
        // Unity didn't need to recompile (no code changes). That's still success.
        return true;
    }

    /// <summary>
    /// Build a diagnostic report when a command fails. Returns everything the LLM needs
    /// to understand Unity's state without a pipe connection: process info, open windows,
    /// import status, compile errors, lockfile state.
    /// </summary>
    static string BuildDiagnosticReport(string projectPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("--- Unity Diagnostics ---");

        var info = DetectUnityProcess(projectPath);

        // Process state
        bool isSafeMode = info.OpenProjects.Any(t => t.Contains("SAFE MODE", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"state: {(isSafeMode ? "SafeMode" : info.State.ToString())}");
        if (info.OpenProjects.Count > 0)
        {
            sb.AppendLine($"unityWindows ({info.OpenProjects.Count}):");
            foreach (var title in info.OpenProjects)
                sb.AppendLine($"  - {title}");
        }
        if (isSafeMode)
        {
            sb.AppendLine("--- SAFE MODE ---");
            sb.AppendLine("Unity is in Safe Mode due to compile errors.");
            sb.AppendLine("action: Fix compile errors in Unity Editor, exit Safe Mode, then retry.");
            return sb.ToString();
        }

        // Import/busy status
        if (!string.IsNullOrEmpty(info.ImportStatus))
            sb.AppendLine($"busy: {info.ImportStatus}");

        // Floating dialogs
        try
        {
            if (info.Dialogs.Count > 0)
            {
                sb.AppendLine($"dialogs ({info.Dialogs.Count}):");
                foreach (var dlg in info.Dialogs)
                {
                    sb.AppendLine($"  - \"{dlg.Title}\" ({dlg.Width}x{dlg.Height})");
                    var buttons = new List<string>();
                    var texts = new List<string>();
                    foreach (var ctrl in dlg.Controls)
                    {
                        if (!ctrl.IsVisible || string.IsNullOrEmpty(ctrl.Text)) continue;
                        string cls = ctrl.ClassName.ToLowerInvariant();
                        if (cls.Contains("button")) buttons.Add(ctrl.Text);
                        else texts.Add(ctrl.Text);
                    }
                    if (texts.Count > 0) sb.AppendLine($"    message: {string.Join(" | ", texts)}");
                    if (buttons.Count > 0) sb.AppendLine($"    buttons: [{string.Join("] [", buttons)}]");
                }
                sb.AppendLine("action: Run DISMISS to close dialogs, then retry.");
            }
        }
        catch { }

        // Compile errors
        if (info.RecentErrors.Count > 0)
        {
            sb.AppendLine($"compileErrors ({info.RecentErrors.Count}):");
            foreach (var err in info.RecentErrors)
                sb.AppendLine($"  - {err}");
            sb.AppendLine("action: Fix compile errors first, then COMPILE.");
        }

        // Assembly build time
        var lastBuild = GetLastAssemblyBuildTime(projectPath);
        if (lastBuild > DateTime.MinValue)
            sb.AppendLine($"lastAssemblyBuild: {lastBuild:yyyy-MM-dd HH:mm:ss}");

        // Lockfile
        string lockfile = Path.Combine(Path.GetFullPath(projectPath), "Temp", "UnityLockfile");
        sb.AppendLine($"lockfile: {(File.Exists(lockfile) ? "exists" : "missing")}");

        // Recommendations
        sb.AppendLine("---");
        if (info.State == UnityProcessState.NotRunning)
            sb.AppendLine("action: Open Unity Editor with this project.");
        else if (info.State == UnityProcessState.DifferentProject)
            sb.AppendLine("action: Open the correct project in Unity or use -d <path>.");
        else if (info.State == UnityProcessState.Importing)
            sb.AppendLine("action: Wait for import to finish, then retry.");
        else
            sb.AppendLine("action: Try DISMISS, or ask user to check Unity.");

        return sb.ToString();
    }

    class DialogInfo
    {
        public IntPtr Hwnd;
        public string Title;
        public RECT Rect;
        public List<DialogControl> Controls = new List<DialogControl>();
        public int Width => Rect.Right - Rect.Left;
        public int Height => Rect.Bottom - Rect.Top;
    }

    class DialogControl
    {
        public IntPtr Hwnd;
        public string ClassName;  // Button, Static, Edit, etc.
        public string Text;
        public bool IsVisible;
        public RECT Rect;
    }

    static void PrintDialogInfo(List<DialogInfo> dialogs)
    {
        if (dialogs == null || dialogs.Count == 0) return;
        Console.Error.WriteLine($"       Dialogs ({dialogs.Count}):");
        foreach (var dlg in dialogs)
        {
            Console.Error.WriteLine($"         \"{dlg.Title}\" ({dlg.Width}x{dlg.Height})");
            var buttons = new List<string>();
            var texts = new List<string>();
            foreach (var ctrl in dlg.Controls)
            {
                if (!ctrl.IsVisible) continue;
                string cls = ctrl.ClassName.ToLowerInvariant();
                if (string.IsNullOrEmpty(ctrl.Text))
                {
                    // Still report the control class for visibility
                    continue;
                }
                if (cls.Contains("button"))
                    buttons.Add(ctrl.Text);
                else
                    texts.Add(ctrl.Text); // Any control with text is potentially a message
            }
            if (texts.Count > 0) Console.Error.WriteLine($"           Message: {string.Join(" | ", texts)}");
            if (buttons.Count > 0) Console.Error.WriteLine($"           Buttons: [{string.Join("] [", buttons)}]");
            // If no recognized controls found, dump all visible controls for debugging
            if (texts.Count == 0 && buttons.Count == 0)
            {
                if (dlg.Controls.Count > 0)
                {
                    Console.Error.WriteLine($"           Controls ({dlg.Controls.Count}):");
                    foreach (var ctrl in dlg.Controls)
                    {
                        if (!ctrl.IsVisible) continue;
                        int cw = ctrl.Rect.Right - ctrl.Rect.Left;
                        int ch = ctrl.Rect.Bottom - ctrl.Rect.Top;
                        string t = string.IsNullOrEmpty(ctrl.Text) ? "" : $" \"{ctrl.Text}\"";
                        Console.Error.WriteLine($"             [{ctrl.ClassName}]{t} ({cw}x{ch})");
                    }
                }
                else
                {
                    Console.Error.WriteLine($"           (no Win32 child controls — likely IMGUI dialog)");
                }
            }
        }
    }

    enum UnityProcessState { Running, NotRunning, DifferentProject, Importing }

    class UnityProcessInfo
    {
        public UnityProcessState State;
        public string ImportStatus;       // e.g. "Importing - Compress 50% (busy for 02:04)..."
        public List<string> OpenProjects = new List<string>();
        public List<DialogInfo> Dialogs = new List<DialogInfo>(); // dialog windows with buttons/text
        public List<string> RecentErrors = new List<string>();  // from Editor.log
        public string HeartbeatState;     // from heartbeat file: ready, compiling, reloading, etc.
        public int HeartbeatCompileTimeAvg; // avg compile time from heartbeat file
        public bool MatchedByCommandLine; // true if project matched via -projectPath arg, not window title
        public HashSet<uint> Pids = new HashSet<uint>();  // Unity process IDs for this project
    }

    /// <summary>
    /// Reads the heartbeat status file written by Unity-side Heartbeat.cs.
    /// Returns null if file doesn't exist or is stale (>10s old).
    /// </summary>
    static (string state, bool compileErrors, int compileErrorCount, int compileTimeAvg)?
        ReadHeartbeatFile(string projectPath)
    {
        try
        {
            string normalizedPath = Path.GetFullPath(projectPath).ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            int hash1 = 5381, hash2 = hash1;
            for (int i = 0; i < normalizedPath.Length && normalizedPath[i] != '\0'; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ normalizedPath[i];
                if (i == normalizedPath.Length - 1 || normalizedPath[i + 1] == '\0') break;
                hash2 = ((hash2 << 5) + hash2) ^ normalizedPath[i + 1];
            }
            int hash = hash1 + (hash2 * 1566083941);
            string statusFile = Path.Combine(Path.GetTempPath(), $"clibridge4unity_{hash:X8}.status");

            if (!File.Exists(statusFile)) return null;

            // Check freshness — stale files are unreliable
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(statusFile);
            if (age.TotalSeconds > 10) return null;

            string json = File.ReadAllText(statusFile);

            // Simple JSON parsing (no dependency on Newtonsoft in CLI)
            string state = ExtractJsonString(json, "state");
            bool errors = json.Contains("\"compileErrors\": true");
            int errorCount = 0, avgTime = 0;
            var ecMatch = System.Text.RegularExpressions.Regex.Match(json, @"""compileErrorCount"":\s*(\d+)");
            if (ecMatch.Success) errorCount = int.Parse(ecMatch.Groups[1].Value);
            var atMatch = System.Text.RegularExpressions.Regex.Match(json, @"""compileTimeAvg"":\s*(\d+)");
            if (atMatch.Success) avgTime = int.Parse(atMatch.Groups[1].Value);

            return (state, errors, errorCount, avgTime);
        }
        catch { return null; }
    }

    static UnityProcessInfo DetectUnityProcess(string projectPath)
    {
        var info = new UnityProcessInfo { State = UnityProcessState.NotRunning };
        string projectName = Path.GetFileName(Path.GetFullPath(projectPath));
        string lockfilePath = Path.Combine(Path.GetFullPath(projectPath), "Temp", "UnityLockfile");

        // Read heartbeat file for advisory info (compile time estimates, state hints)
        // Don't use it to block commands — it can be slightly stale
        var heartbeat = ReadHeartbeatFile(projectPath);
        if (heartbeat.HasValue)
        {
            info.HeartbeatState = heartbeat.Value.state;
            info.HeartbeatCompileTimeAvg = heartbeat.Value.compileTimeAvg;
        }

        // Check lockfile — Unity creates this while project is open
        bool lockfileExists = File.Exists(lockfilePath);
        bool lockfileHeld = false;
        if (lockfileExists)
        {
            try
            {
                using var fs = new FileStream(lockfilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                // If we can open exclusively, Unity doesn't hold it
            }
            catch (IOException) { lockfileHeld = true; }
            catch (UnauthorizedAccessException) { lockfileHeld = true; }
        }

        // Collect ALL Unity PIDs and enumerate windows across all of them
        // (Unity 6 uses separate processes for UI and scripting runtime)
        var unityPids = new HashSet<uint>();
        bool foundProject = false;
        string fullProjectPath = Path.GetFullPath(projectPath).TrimEnd('\\', '/');

        try
        {
            foreach (var proc in Process.GetProcessesByName("Unity"))
            {
                try
                {
                    unityPids.Add((uint)proc.Id);
                    if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(proc.MainWindowTitle))
                    {
                        info.OpenProjects.Add(proc.MainWindowTitle);
                        if (proc.MainWindowTitle.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                            foundProject = true;
                    }
                }
                catch { }
            }

            // Fallback: match by command line -projectPath when window title isn't available yet
            // (Unity has no window title during early loading, import, upgrade dialogs)
            if (!foundProject && unityPids.Count > 0)
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'Unity.exe'");
                    foreach (var obj in searcher.Get())
                    {
                        string cmdLine = obj["CommandLine"]?.ToString() ?? "";
                        // Match -projectPath (or -projectpath) followed by the path
                        // Skip AssetImportWorker processes (-batchMode -noUpm)
                        if (cmdLine.Contains("-batchMode", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (cmdLine.Contains(fullProjectPath, StringComparison.OrdinalIgnoreCase) ||
                            cmdLine.Contains(fullProjectPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            foundProject = true;
                            uint pid = (uint)(int)obj["ProcessId"];
                            unityPids.Add(pid);
                            info.MatchedByCommandLine = true;
                            break;
                        }
                    }
                }
                catch { } // WMI may fail in restricted environments
            }

            // Enumerate all visible windows from Unity PIDs to find dialogs
            if (unityPids.Count > 0)
            {
                var seenHwnds = new HashSet<IntPtr>();
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (!unityPids.Contains(pid)) return true;

                    var sb = new StringBuilder(256);
                    GetWindowText(hwnd, sb, 256);
                    string title = sb.ToString();
                    if (string.IsNullOrEmpty(title)) return true;

                    // Skip the main Unity editor window (contains " - Unity" or "SAFE MODE")
                    if (title.Contains(" - Unity ") || title.Contains(" - SAFE MODE -")) return true;

                    // Skip undocked editor panels (same class as main window)
                    var classBuf = new StringBuilder(256);
                    GetClassName(hwnd, classBuf, 256);
                    if (classBuf.ToString() == "UnityContainerWndClass") return true;

                    // Any other visible Unity window is potentially a dialog
                    GetWindowRect(hwnd, out RECT rect);
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w > 50 && h > 30 && seenHwnds.Add(hwnd))
                    {
                        var dlg = new DialogInfo { Hwnd = hwnd, Title = title, Rect = rect };
                        EnumerateChildControls(hwnd, dlg.Controls);
                        info.Dialogs.Add(dlg);
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch { }

        // Store discovered PIDs
        info.Pids = unityPids;

        // Determine state: lockfile is most reliable, then command line match, then window title
        if (!lockfileHeld && !foundProject)
        {
            info.State = unityPids.Count > 0 ? UnityProcessState.DifferentProject : UnityProcessState.NotRunning;
            return info;
        }

        // Unity has this project open
        // If matched only by command line (no window title yet), Unity is still loading
        if (info.MatchedByCommandLine && info.OpenProjects.All(t => !t.Contains(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            info.State = UnityProcessState.Importing;
            if (string.IsNullOrEmpty(info.ImportStatus))
                info.ImportStatus = "Loading project (no editor window yet — matched by process command line)";
            return info;
        }

        info.State = UnityProcessState.Running;

        if (unityPids.Count > 0)
        {
            var titleBuf = new StringBuilder(512);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!unityPids.Contains(pid)) return true;

                titleBuf.Clear();
                GetWindowText(hwnd, titleBuf, 512);
                string title = titleBuf.ToString();

                // Detect long-running operations by window title
                // "Importing" = asset import, "Compiling" = shader/script compile
                // Skip transient states: "Reloading Domain", "Hold on...", "Loading"
                if (!string.IsNullOrEmpty(title) &&
                    (title.StartsWith("Importing", StringComparison.OrdinalIgnoreCase) ||
                     title.StartsWith("Building", StringComparison.OrdinalIgnoreCase) ||
                     title.StartsWith("Compiling", StringComparison.OrdinalIgnoreCase)))
                {
                    info.State = UnityProcessState.Importing;
                    info.ImportStatus = title;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        // Only check log files for errors when Unity is busy (can't use pipe)
        // When Unity is responsive, the server handles compile error reporting
        if (info.State == UnityProcessState.Importing)
            TailProjectErrors(info, projectPath);

        return info;
    }

    /// <summary>
    /// Read recent errors from two sources (no pipe needed):
    /// 1. Bridge's own log file (per-project, in %TEMP%)
    /// 2. Global Unity Editor.log (fallback, filtered by project path)
    /// </summary>
    static void TailProjectErrors(UnityProcessInfo info, string projectPath)
    {
        // Source 1: Bridge log (per-project, tab-separated)
        // Only show errors newer than the last successful Assembly-CSharp build
        var lastBuild = GetLastAssemblyBuildTime(projectPath);
        try
        {
            string normalizedPath = Path.GetFullPath(projectPath).ToLowerInvariant().Replace("/", "\\").TrimEnd('\\');
            int hash = GetDeterministicHashCode(normalizedPath);
            string bridgeLog = Path.Combine(Path.GetTempPath(), $"clibridge4unity_logs_{hash:X8}.log");
            if (File.Exists(bridgeLog))
            {
                string tail = TailFile(bridgeLog, 16384);
                foreach (var line in tail.Split('\n'))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    if (parts[2] != "ERROR" && parts[2] != "EXCEPTION") continue;
                    // Skip errors older than the last successful build
                    if (DateTime.TryParse(parts[1], out var ts) && ts < lastBuild) continue;
                    string message = parts[3].Replace("\\n", "\n").Split('\n')[0];
                    if (IsCompileError(message) && info.RecentErrors.Count < 10)
                        info.RecentErrors.Add(message);
                }
            }
        }
        catch { }

        // Source 2: Global Editor.log — only if bridge log had nothing and project matches
        if (info.RecentErrors.Count == 0)
        {
            try
            {
                string editorLog = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Unity", "Editor", "Editor.log");
                if (!File.Exists(editorLog)) return;

                // Verify this log is for our project (check header for -projectPath)
                string header = ReadFileHead(editorLog, 4096);
                string absProject = Path.GetFullPath(projectPath).Replace("/", "\\").TrimEnd('\\');
                if (!header.Contains(absProject, StringComparison.OrdinalIgnoreCase))
                    return; // Log is for a different project

                // Tail last ~256KB and scan for compile errors
                string tail = TailFile(editorLog, 256 * 1024);
                foreach (var line in tail.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (IsCompileError(trimmed) && info.RecentErrors.Count < 10)
                        info.RecentErrors.Add(trimmed);
                }
            }
            catch { }
        }
    }

    static bool IsCompileError(string line)
    {
        // Real Unity compile errors have a file path: "Assets/Foo.cs(10,5): error CS0103: ..."
        // CODE_EXEC errors are just "(13,20): error CS1002:" with no path — skip those
        return (line.Contains("error CS") && line.Contains("Assets"))
            || (line.Contains("): error") && line.Contains(".cs(") && line.Contains("Assets"));
    }

    /// <summary>
    /// Returns the last write time of the newest Assembly-CSharp DLL in Library/ScriptAssemblies.
    /// If assemblies are newer than log errors, the errors are stale (fixed and recompiled).
    /// </summary>
    static DateTime GetLastAssemblyBuildTime(string projectPath)
    {
        try
        {
            string asmDir = Path.Combine(Path.GetFullPath(projectPath), "Library", "ScriptAssemblies");
            if (!Directory.Exists(asmDir)) return DateTime.MinValue;
            var newest = DateTime.MinValue;
            foreach (var dll in Directory.EnumerateFiles(asmDir, "Assembly-CSharp*.dll"))
            {
                var t = File.GetLastWriteTime(dll);
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch { return DateTime.MinValue; }
    }

    static string TailFile(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long tailSize = Math.Min(bytes, fs.Length);
        fs.Seek(-tailSize, SeekOrigin.End);
        var buffer = new byte[tailSize];
        int read = fs.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    static string ReadFileHead(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int readSize = (int)Math.Min(bytes, fs.Length);
        var buffer = new byte[readSize];
        int read = fs.Read(buffer, 0, readSize);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// Enumerates all child controls of a window (buttons, text labels, edit fields, etc.)
    /// </summary>
    static void EnumerateChildControls(IntPtr parentHwnd, List<DialogControl> controls)
    {
        const uint WM_GETTEXT = 0x000D;
        const uint WM_GETTEXTLENGTH = 0x000E;

        EnumChildWindows(parentHwnd, (hwnd, _) =>
        {
            var classNameBuf = new StringBuilder(256);
            GetClassName(hwnd, classNameBuf, 256);
            string className = classNameBuf.ToString();

            // Get control text via WM_GETTEXT (works for all control types)
            var lenResult = SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, null);
            int textLen = lenResult.ToInt32();
            string text = "";
            if (textLen > 0)
            {
                var textBuf = new StringBuilder(textLen + 1);
                SendMessage(hwnd, WM_GETTEXT, (IntPtr)(textLen + 1), textBuf);
                text = textBuf.ToString();
            }

            bool visible = IsWindowVisible(hwnd);
            GetWindowRect(hwnd, out RECT rect);

            controls.Add(new DialogControl
            {
                Hwnd = hwnd,
                ClassName = className,
                Text = text,
                IsVisible = visible,
                Rect = rect
            });

            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Briefly brings all Unity editor windows to the foreground, waits 1 second,
    /// then returns focus to whichever window was previously active.
    /// Don't use unless you think Unity is stuck (e.g. main-thread commands timing out).
    /// </summary>
    static int HandleInstall(string pipeName, string projectPath, string data)
    {
        // Build concise CLAUDE.md content — capabilities overview + pointer to full help
        var md = new StringBuilder();
        md.AppendLine("# Unity Bridge (clibridge4unity) - Tool Reference");
        md.AppendLine();
        md.AppendLine("`clibridge4unity` is a CLI tool that controls the Unity Editor via named pipes. Run commands from the project directory. Run `clibridge4unity -h` for full usage, or `clibridge4unity HELP` for live command list from Unity.");
        md.AppendLine();
        md.AppendLine("**Capabilities:** scene hierarchy & GameObject CRUD, component inspection & modification, compile/play/stop workflow, execute arbitrary C# in Unity (CODE_EXEC/CODE_EXEC_RETURN — has its own Roslyn compiler, works even when Unity is busy), code search & analysis, asset management, run tests with streaming results, screenshots, prefab operations, and diagnostics. Most commands auto-detect the Unity project from the current directory.");

        // Determine target path
        string targetPath;
        if (!string.IsNullOrEmpty(data) && data.Trim().Length > 0)
        {
            targetPath = Path.GetFullPath(data.Trim());
        }
        else
        {
            targetPath = Path.Combine(projectPath, "CLAUDE.md");
        }

        // Check if file exists and has non-bridge content
        string marker = "# Unity Bridge (clibridge4unity) - Tool Reference";
        string endMarker = "<!-- END clibridge4unity -->";
        string content = md.ToString().TrimEnd() + "\n" + endMarker + "\n";

        if (File.Exists(targetPath))
        {
            string existing = File.ReadAllText(targetPath);
            // Replace existing bridge section if present
            int startIdx = existing.IndexOf(marker);
            int endIdx = existing.IndexOf(endMarker);

            if (startIdx >= 0 && endIdx >= 0)
            {
                // Replace the section
                string before = existing[..startIdx];
                string after = existing[(endIdx + endMarker.Length)..].TrimStart('\r', '\n');
                File.WriteAllText(targetPath, before + content + (after.Length > 0 ? "\n" + after : ""));
                Console.WriteLine($"Updated bridge section in: {targetPath}");
            }
            else if (startIdx >= 0)
            {
                // Old format without end marker — replace from marker to end
                string before = existing[..startIdx];
                File.WriteAllText(targetPath, before + content);
                Console.WriteLine($"Updated bridge section in: {targetPath}");
            }
            else
            {
                // Append to existing file
                File.AppendAllText(targetPath, "\n" + content);
                Console.WriteLine($"Appended bridge docs to: {targetPath}");
            }
        }
        else
        {
            File.WriteAllText(targetPath, content);
            Console.WriteLine($"Created: {targetPath}");
        }

        Console.WriteLine($"Claude Code will now know about clibridge4unity in this project.");
        return EXIT_SUCCESS;
    }

    static int HandleWakeup(string projectPath, bool sendRefresh = false)
    {
        string projectName = Path.GetFileName(Path.GetFullPath(projectPath));

        IntPtr previousWindow = GetForegroundWindow();
        var titleBuf = new StringBuilder(256);
        GetWindowText(previousWindow, titleBuf, 256);
        string previousTitle = titleBuf.ToString();

        // Find Unity editor windows matching this project
        var unityWindows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();
            if (title.Contains(" - Unity") && title.Contains(projectName, StringComparison.OrdinalIgnoreCase))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    if (proc.ProcessName == "Unity")
                        unityWindows.Add(hwnd);
                }
                catch { }
            }
            return true;
        }, IntPtr.Zero);

        if (unityWindows.Count == 0)
        {
            Console.Error.WriteLine($"Error: No Unity window found for project '{projectName}'");
            return EXIT_CONNECTION;
        }

        // Attach to foreground thread to bypass SetForegroundWindow restrictions
        uint foregroundThreadId = GetWindowThreadProcessId(previousWindow, out _);
        uint ourThreadId = GetCurrentThreadId();
        bool attached = foregroundThreadId != ourThreadId &&
                        AttachThreadInput(ourThreadId, foregroundThreadId, true);

        try
        {
            foreach (var hwnd in unityWindows)
            {
                ShowWindow(hwnd, 9 /* SW_RESTORE */);
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
        }
        finally
        {
            if (attached)
                AttachThreadInput(ourThreadId, foregroundThreadId, false);
        }

        Console.Error.WriteLine($"Woke {unityWindows.Count} Unity window(s)");

        // Send Ctrl+R to trigger asset refresh/recompile
        if (sendRefresh)
        {
            Thread.Sleep(500); // let Unity process the focus change
            const byte VK_CONTROL = 0x11;
            const byte VK_R = 0x52;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_R, 0, 0, UIntPtr.Zero);
            keybd_event(VK_R, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Console.Error.WriteLine("Sent Ctrl+R (asset refresh)");
        }

        Thread.Sleep(500);

        // Don't return focus when refreshing — Unity needs to stay focused to process Ctrl+R
        if (sendRefresh)
        {
            Console.WriteLine($"Woke {unityWindows.Count} Unity window(s) (keeping focus for refresh)");
            return EXIT_SUCCESS;
        }

        // Return focus to previous window
        if (previousWindow != IntPtr.Zero)
        {
            foregroundThreadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            ourThreadId = GetCurrentThreadId();
            attached = foregroundThreadId != ourThreadId &&
                       AttachThreadInput(ourThreadId, foregroundThreadId, true);
            try
            {
                SetForegroundWindow(previousWindow);
                BringWindowToTop(previousWindow);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(ourThreadId, foregroundThreadId, false);
            }
        }

        Console.WriteLine($"Woke {unityWindows.Count} Unity window(s), focus returned to '{previousTitle}'");
        return EXIT_SUCCESS;
    }

    static int HandleScreenshot(string projectPath)
    {
        // Find Unity's main window
        string projectName = projectPath != null ? Path.GetFileName(Path.GetFullPath(projectPath)) : null;
        IntPtr hwnd = IntPtr.Zero;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            string title = sb.ToString();
            if (!title.Contains("Unity") || !title.Contains(" - ")) return true;
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                var proc = Process.GetProcessById((int)pid);
                if (proc.ProcessName != "Unity") return true;
                if (projectName != null && title.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                {
                    hwnd = h;
                    return false; // exact match, stop
                }
                if (hwnd == IntPtr.Zero) hwnd = h; // fallback to first Unity window
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: Could not find Unity editor window");
            return EXIT_CONNECTION;
        }

        // Get window dimensions
        GetWindowRect(hwnd, out RECT rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            Console.Error.WriteLine($"Error: Invalid window rect: {width}x{height}");
            return EXIT_COMMAND_ERROR;
        }

        // PrintWindow capture (works even when Unity is in background)
        IntPtr winDC = GetDC(hwnd);
        IntPtr memDC = CreateCompatibleDC(winDC);
        IntPtr bitmap = CreateCompatibleBitmap(winDC, width, height);
        IntPtr oldBmp = SelectObject(memDC, bitmap);
        PrintWindow(hwnd, memDC, 2 /* PW_RENDERFULLCONTENT */);

        // Read pixels as top-down (negative biHeight) for PNG output
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // negative = top-down (correct for PNG)
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        byte[] pixels = new byte[width * height * 4];
        GetDIBits(memDC, bitmap, 0, (uint)height, pixels, ref bmi, 0);

        // Cleanup GDI
        SelectObject(memDC, oldBmp);
        DeleteObject(bitmap);
        DeleteDC(memDC);
        ReleaseDC(hwnd, winDC);

        // Save PNG
        string outputDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
            "clibridge4unity_screenshots");
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, "render_editor.png");
        WritePng(outputPath, width, height, pixels);

        Console.WriteLine($"Captured editor");
        Console.WriteLine($"size: {width}x{height}");
        Console.WriteLine($"output: {outputPath}");

        ShowNotification("Screenshot", $"Captured {width}x{height}", outputPath);
        return EXIT_SUCCESS;
    }

    static void ShowNotification(string title, string message, string imagePath = null)
    {
        try
        {
            // Windows toast notification with optional image
            string imageXml = imagePath != null
                ? $"<image placement=\"hero\" src=\"file:///{imagePath.Replace('\\', '/')}\"/>"
                : "";
            string toastXml = $@"
<toast duration=""short"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{System.Security.SecurityElement.Escape(title)}</text>
      <text>{System.Security.SecurityElement.Escape(message)}</text>
      {imageXml}
    </binding>
  </visual>
</toast>";

            string psScript = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('{toastXml.Replace("'", "''").Replace("\r", "").Replace("\n", "")}')
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('clibridge4unity').Show($toast)
";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            Process.Start(psi);
        }
        catch { }
    }

    static void OpenInVsCode(string filePath)
    {
        try
        {
            // Find code.cmd in PATH
            string codePath = null;
            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in envPath.Split(';'))
            {
                string candidate = Path.Combine(dir.Trim(), "code.cmd");
                if (File.Exists(candidate)) { codePath = candidate; break; }
            }
            if (codePath == null) return;

            // Find git root to target the correct VS Code workspace window
            string workspace = null;
            string search = Directory.GetCurrentDirectory();
            while (search != null)
            {
                if (Directory.Exists(Path.Combine(search, ".git")))
                {
                    workspace = search;
                    break;
                }
                search = Path.GetDirectoryName(search);
            }

            // -r = reuse window matching the workspace folder
            string args = workspace != null
                ? $"-r \"{workspace}\" \"{filePath}\""
                : $"\"{filePath}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = codePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { }
    }

    /// <summary>
    /// Writes BGRA pixel data as PNG using .NET's built-in DeflateStream.
    /// </summary>
    static void WritePng(string path, int width, int height, byte[] bgraPixels)
    {
        using var fs = new FileStream(path, FileMode.Create);

        // PNG signature
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        // IHDR chunk
        WritePngChunk(fs, "IHDR", writer =>
        {
            writer.Write(ToBE(width));
            writer.Write(ToBE(height));
            writer.Write((byte)8);  // bit depth
            writer.Write((byte)2);  // color type: RGB
            writer.Write((byte)0);  // compression
            writer.Write((byte)0);  // filter
            writer.Write((byte)0);  // interlace
        });

        // IDAT chunk - deflated image data
        // Build raw image data: filter byte (0) + RGB pixels per row
        byte[] rawData = new byte[height * (1 + width * 3)];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * (1 + width * 3);
            rawData[rowOffset] = 0; // No filter
            for (int x = 0; x < width; x++)
            {
                int srcIdx = (y * width + x) * 4;
                int dstIdx = rowOffset + 1 + x * 3;
                rawData[dstIdx]     = bgraPixels[srcIdx + 2]; // R
                rawData[dstIdx + 1] = bgraPixels[srcIdx + 1]; // G
                rawData[dstIdx + 2] = bgraPixels[srcIdx];     // B
            }
        }

        // Compress with zlib (DeflateStream + zlib header/checksum)
        using var compressedMs = new MemoryStream();
        // zlib header: CMF=0x78 (deflate, window=32K), FLG=0x01 (no dict, check bits)
        compressedMs.Write(new byte[] { 0x78, 0x01 });
        using (var deflate = new DeflateStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(rawData);
        }
        // Adler-32 checksum
        uint adler = Adler32(rawData);
        compressedMs.Write(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler });

        byte[] compressedData = compressedMs.ToArray();
        WritePngChunk(fs, "IDAT", writer => writer.Write(compressedData));

        // IEND chunk
        WritePngChunk(fs, "IEND", _ => { });
    }

    static void WritePngChunk(Stream stream, string type, Action<BinaryWriter> writeData)
    {
        using var dataMs = new MemoryStream();
        using (var writer = new BinaryWriter(dataMs, Encoding.ASCII, leaveOpen: true))
        {
            writeData(writer);
        }
        byte[] data = dataMs.ToArray();
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);

        // Length (big-endian)
        stream.Write(ToBE(data.Length));
        // Type
        stream.Write(typeBytes);
        // Data
        if (data.Length > 0) stream.Write(data);
        // CRC32 over type + data
        uint crc = Crc32(typeBytes, data);
        stream.Write(ToBE((int)crc));
    }

    static byte[] ToBE(int value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    };

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    static readonly uint[] Crc32Table = InitCrc32Table();
    static uint[] InitCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    // ─── SETUP: install UPM package + CLAUDE.md ─────────────────────────

    static int HandleSetup(string pipeName, string projectPath, string data)
    {
        Console.WriteLine($"clibridge4unity v{CLI_VERSION} — Setting up Unity project");
        Console.WriteLine($"Project: {projectPath}");
        Console.WriteLine();

        // Step 1: Ensure UPM package is in manifest.json
        int packageResult = EnsureUpmPackage(projectPath);
        if (packageResult != 0) return packageResult;

        // Step 2: Check Unity connectivity
        Console.WriteLine();
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(1000);
            byte[] msg = Encoding.UTF8.GetBytes("PING\n");
            pipe.Write(msg, 0, msg.Length);
            pipe.Flush();

            var sb = new StringBuilder();
            byte[] buf = new byte[4096];
            int bytesRead;
            while ((bytesRead = pipe.Read(buf, 0, buf.Length)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, bytesRead));

            if (sb.ToString().Contains("PONG"))
                Console.WriteLine("[OK] Unity Editor is running and responsive");
            else
                Console.WriteLine("[!!] Unity responded but PING failed — check Unity console for errors");
        }
        catch
        {
            Console.WriteLine("[!!] Unity Editor not responding — start Unity and re-run SETUP to generate full docs");
        }

        // Step 3: Install Claude Code hooks
        Console.WriteLine();
        InstallHooks(projectPath);

        // Step 4: Generate CLAUDE.md (reuse existing INSTALL logic)
        Console.WriteLine();
        return HandleInstall(pipeName, projectPath, data);
    }

    static int HandleHook()
    {
        // Read hook JSON from stdin
        string input;
        try { input = Console.In.ReadToEnd(); }
        catch { return EXIT_SUCCESS; }

        if (string.IsNullOrWhiteSpace(input))
            return EXIT_SUCCESS;

        // Scope to tool_input section for field extraction
        int toolInputIdx = input.IndexOf("\"tool_input\"");
        string toolInput = toolInputIdx >= 0 ? input.Substring(toolInputIdx) : input;

        string pattern = HookExtractJsonString(toolInput, "pattern");
        string glob = HookExtractJsonString(toolInput, "glob");
        string type = HookExtractJsonString(toolInput, "type");

        if (string.IsNullOrEmpty(pattern))
            return EXIT_SUCCESS;

        // Check if targeting C# files
        bool isCs = (glob != null && (glob.Contains(".cs") || glob.Contains("*.cs"))) ||
                    (type != null && type == "cs");

        if (!isCs)
            return EXIT_SUCCESS;

        // Always deny — Roslyn offline analysis works without Unity
        string reason = $"For C# code searches, use clibridge4unity CODE_ANALYZE instead of grep:\n" +
                        $"  clibridge4unity CODE_ANALYZE {pattern}\n" +
                        $"CODE_ANALYZE returns the full connection graph: inheritance, who references it, " +
                        $"who passes it as a parameter, who returns it, GetComponent calls, methods, fields, " +
                        $"and raw grep matches — all in ~200ms. Always prefer CODE_ANALYZE over grep for C# code.";

        Console.Error.Write(reason);
        return 2; // exit 2 = deny/block
    }

    /// <summary>Walk up from a path to find a Unity project (directory containing Assets/).</summary>
    static string DetectProjectFromPath(string startPath)
    {
        if (string.IsNullOrEmpty(startPath)) return null;
        try
        {
            string dir = Path.GetFullPath(startPath);
            for (int i = 0; i < 15; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Assets")))
                    return dir;
                string parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) break;
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Quick JSON string value extractor for hook input — no dependency on JSON library.</summary>
    static string HookExtractJsonString(string json, string key)
    {
        string needle = $"\"{key}\"";
        int idx = json.IndexOf(needle);
        if (idx < 0) return null;
        int colonIdx = json.IndexOf(':', idx + needle.Length);
        if (colonIdx < 0) return null;
        int quoteStart = json.IndexOf('"', colonIdx + 1);
        if (quoteStart < 0) return null;
        int quoteEnd = json.IndexOf('"', quoteStart + 1);
        if (quoteEnd < 0) return null;
        return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
    }

    static void InstallHooks(string projectPath)
    {
        string claudeDir = Path.Combine(projectPath, ".claude");
        string settingsPath = Path.Combine(claudeDir, "settings.json");

        Directory.CreateDirectory(claudeDir);

        // The hook command — just calls the CLI itself, no Python needed
        string hookCommand = "clibridge4unity HOOK";

        string hookEntry = "{\n" +
            "  \"hooks\": {\n" +
            "    \"PreToolUse\": [\n" +
            "      {\n" +
            "        \"matcher\": \"Grep\",\n" +
            "        \"hooks\": [\n" +
            "          {\n" +
            "            \"type\": \"command\",\n" +
            $"            \"command\": \"{hookCommand}\"\n" +
            "          }\n" +
            "        ]\n" +
            "      }\n" +
            "    ]\n" +
            "  }\n" +
            "}";

        try
        {
            if (File.Exists(settingsPath))
            {
                string existing = File.ReadAllText(settingsPath);
                if (existing.Contains("clibridge4unity HOOK"))
                {
                    Console.WriteLine("[OK] Claude Code hooks already configured");
                    return;
                }

                // Replace old Python hook with CLI hook
                if (existing.Contains("suggest-code-search"))
                {
                    existing = existing.Replace("suggest-code-search.py", "INVALID");
                    // Simpler: just rewrite with clean config
                    File.WriteAllText(settingsPath, hookEntry);
                    Console.WriteLine("[OK] Upgraded hooks from Python to CLI (clibridge4unity HOOK)");
                    return;
                }

                // Merge into existing settings
                if (existing.Contains("\"hooks\""))
                {
                    if (existing.Contains("\"PreToolUse\""))
                    {
                        int preToolIdx = existing.IndexOf("\"PreToolUse\"");
                        int arrStart = existing.IndexOf('[', preToolIdx);
                        if (arrStart >= 0)
                        {
                            string entry =
                                "\n      {\n" +
                                "        \"matcher\": \"Grep\",\n" +
                                "        \"hooks\": [\n" +
                                "          {\n" +
                                "            \"type\": \"command\",\n" +
                                $"            \"command\": \"{hookCommand}\"\n" +
                                "          }\n" +
                                "        ]\n" +
                                "      },";
                            existing = existing.Insert(arrStart + 1, entry);
                            File.WriteAllText(settingsPath, existing);
                            Console.WriteLine("[OK] Added Grep hook to existing PreToolUse hooks");
                        }
                    }
                    else
                    {
                        int hooksIdx = existing.IndexOf("\"hooks\"");
                        int braceStart = existing.IndexOf('{', hooksIdx + 7);
                        if (braceStart >= 0)
                        {
                            string entry =
                                "\n    \"PreToolUse\": [\n" +
                                "      {\n" +
                                "        \"matcher\": \"Grep\",\n" +
                                "        \"hooks\": [\n" +
                                "          {\n" +
                                "            \"type\": \"command\",\n" +
                                $"            \"command\": \"{hookCommand}\"\n" +
                                "          }\n" +
                                "        ]\n" +
                                "      }\n" +
                                "    ],";
                            existing = existing.Insert(braceStart + 1, entry);
                            File.WriteAllText(settingsPath, existing);
                            Console.WriteLine("[OK] Added PreToolUse hooks to existing settings");
                        }
                    }
                }
                else
                {
                    int firstBrace = existing.IndexOf('{');
                    if (firstBrace >= 0)
                    {
                        string entry =
                            "\n  \"hooks\": {\n" +
                            "    \"PreToolUse\": [\n" +
                            "      {\n" +
                            "        \"matcher\": \"Grep\",\n" +
                            "        \"hooks\": [\n" +
                            "          {\n" +
                            "            \"type\": \"command\",\n" +
                            $"            \"command\": \"{hookCommand}\"\n" +
                            "          }\n" +
                            "        ]\n" +
                            "      }\n" +
                            "    ]\n" +
                            "  },";
                        existing = existing.Insert(firstBrace + 1, entry);
                        File.WriteAllText(settingsPath, existing);
                        Console.WriteLine("[OK] Added hooks to existing .claude/settings.json");
                    }
                }
            }
            else
            {
                File.WriteAllText(settingsPath, hookEntry);
                Console.WriteLine("[OK] Created .claude/settings.json with hooks");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not configure hooks ({ex.Message})");
        }

        // Clean up old Python hook script if present
        string oldScript = Path.Combine(claudeDir, "hooks", "suggest-code-search.py");
        if (File.Exists(oldScript))
        {
            try { File.Delete(oldScript); } catch { }
        }
    }

    static int EnsureUpmPackage(string projectPath)
    {
        string manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: {manifestPath} not found. Is this a Unity project?");
            return EXIT_COMMAND_ERROR;
        }

        string manifest = File.ReadAllText(manifestPath);

        // Check if package is already present
        if (manifest.Contains($"\"{UPM_PACKAGE_NAME}\""))
        {
            // Check if the version/URL matches current CLI version
            string gitUrl = GetPackageGitUrl();
            if (manifest.Contains(gitUrl))
            {
                Console.WriteLine($"[OK] UPM package already installed (v{CLI_VERSION})");
                return EXIT_SUCCESS;
            }

            // Package exists but at different version - update it
            Console.WriteLine($"[..] Updating UPM package to v{CLI_VERSION}...");
            // Find and replace the existing entry
            int pkgStart = manifest.IndexOf($"\"{UPM_PACKAGE_NAME}\"");
            int valueStart = manifest.IndexOf(':', pkgStart) + 1;
            // Find the end of the value (next unescaped quote pair)
            int valueQuoteStart = manifest.IndexOf('"', valueStart);
            int valueQuoteEnd = manifest.IndexOf('"', valueQuoteStart + 1);
            string oldValue = manifest.Substring(valueQuoteStart, valueQuoteEnd - valueQuoteStart + 1);
            manifest = manifest.Replace(
                $"\"{UPM_PACKAGE_NAME}\": {oldValue}",
                $"\"{UPM_PACKAGE_NAME}\": \"{gitUrl}\"");
            File.WriteAllText(manifestPath, manifest);
            Console.WriteLine($"[OK] Updated UPM package to v{CLI_VERSION}");
            return EXIT_SUCCESS;
        }

        // Package not present - add it
        Console.WriteLine($"[..] Adding UPM package v{CLI_VERSION}...");
        string gitUrlNew = GetPackageGitUrl();

        // Insert after "dependencies": {
        int depsIdx = manifest.IndexOf("\"dependencies\"");
        if (depsIdx < 0)
        {
            Console.Error.WriteLine("Error: Could not find \"dependencies\" in manifest.json");
            return EXIT_COMMAND_ERROR;
        }
        int braceIdx = manifest.IndexOf('{', depsIdx);
        if (braceIdx < 0)
        {
            Console.Error.WriteLine("Error: Malformed manifest.json");
            return EXIT_COMMAND_ERROR;
        }

        // Detect indentation from the next line
        int nextLineStart = manifest.IndexOf('\n', braceIdx) + 1;
        int indentEnd = nextLineStart;
        while (indentEnd < manifest.Length && (manifest[indentEnd] == ' ' || manifest[indentEnd] == '\t'))
            indentEnd++;
        string indent = manifest[nextLineStart..indentEnd];

        string newEntry = $"\n{indent}\"{UPM_PACKAGE_NAME}\": \"{gitUrlNew}\",";
        manifest = manifest.Insert(braceIdx + 1, newEntry);
        File.WriteAllText(manifestPath, manifest);
        Console.WriteLine($"[OK] Added UPM package v{CLI_VERSION}");
        Console.WriteLine($"     Unity will import the package on next focus/refresh.");
        return EXIT_SUCCESS;
    }

    static string GetPackageGitUrl()
    {
        return $"https://github.com/{GITHUB_REPO}.git?path=Package#v{CLI_VERSION}";
    }

    // ─── Roslyn-based offline code analysis ──────────────────────────────

    static class RoslynAnalyzer
    {
        /// <summary>
        /// Enumerate all .cs files under Assets/ and Packages/ (skip Library/, Temp/, obj/).
        /// </summary>
        static string[] GetSourceFiles(string projectPath)
        {
            var files = new List<string>();
            string assetsDir = Path.Combine(projectPath, "Assets");
            string packagesDir = Path.Combine(projectPath, "Packages");

            if (Directory.Exists(assetsDir))
                files.AddRange(Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories));
            if (Directory.Exists(packagesDir))
            {
                // Only scan local packages (folders), not cached packages in Library/
                foreach (var dir in Directory.EnumerateDirectories(packagesDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith("com.") || dirName.StartsWith("au.")) // UPM package folders
                        files.AddRange(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories));
                }
            }
            return files.ToArray();
        }

        static string ToRelativePath(string file, string projectPath)
        {
            return file.Replace(projectPath + "\\", "").Replace(projectPath + "/", "");
        }


        /// <summary>
        /// CODE_ANALYZE: connection-graph analysis using Roslyn syntax parsing.
        /// Returns structured output showing how a type connects to the rest of the codebase.
        /// </summary>
        public static string Analyze(string projectPath, string query)
        {
            query = query.Trim();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Only scan Assets/ — Packages don't reference project code
            string assetsDir = Path.Combine(projectPath, "Assets");
            string[] allFiles = Directory.Exists(assetsDir)
                ? Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories).ToArray()
                : Array.Empty<string>();

            // Single pass: read + check contains + parse matching files
            var parsedFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, Microsoft.CodeAnalysis.SyntaxTree>();
            Parallel.ForEach(allFiles, file =>
            {
                try
                {
                    string text = File.ReadAllText(file);
                    if (!text.Contains(query)) return;
                    parsedFiles[file] = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text, path: file);
                }
                catch { }
            });

            var scanMs = sw.ElapsedMilliseconds;

            // Extract connections
            var sourceFiles = new List<string>();
            var baseTypes = new List<string>();
            var derivedTypes = new List<string>();
            var fieldUsages = new List<string>();
            var paramUsages = new List<string>();
            var returnUsages = new List<string>();
            var getComponentUsages = new List<string>();
            var localVarUsages = new List<string>();
            var ownMethods = new List<string>();
            var ownFields = new List<string>();
            var grepLines = new System.Collections.Concurrent.ConcurrentBag<string>(); // raw grep for tail

            foreach (var kvp in parsedFiles)
            {
                var root = kvp.Value.GetRoot();
                string rel = ToRelativePath(kvp.Key, projectPath);

                // Collect grep lines from this file
                var lines = root.ToFullString().Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query))
                    {
                        string lineText = lines[i].Trim();
                        if (lineText.Length > 120) lineText = lineText.Substring(0, 120) + "...";
                        grepLines.Add($"{rel}:{i + 1}: {lineText}");
                    }
                }

                foreach (var td in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                {
                    string enclosing = td.Identifier.Text;
                    bool isSelf = enclosing.Equals(query, StringComparison.OrdinalIgnoreCase);

                    if (isSelf)
                    {
                        // Definition info
                        int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var bases = td.BaseList?.Types.Select(t => t.Type.ToString()).ToArray() ?? Array.Empty<string>();
                        sourceFiles.Add($"{rel}:{line} ({td.Modifiers} {td.Keyword} {enclosing} : {string.Join(", ", bases)})");
                        baseTypes.AddRange(bases);

                        // Own methods
                        foreach (var m in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
                        {
                            int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                            ownMethods.Add($"{m.Modifiers} {m.ReturnType} {m.Identifier.Text}({parms}) — {rel}:{mLine}");
                        }

                        // Own fields
                        foreach (var f in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
                        {
                            foreach (var v in f.Declaration.Variables)
                            {
                                int fLine = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                ownFields.Add($"{f.Modifiers} {f.Declaration.Type} {v.Identifier} — {rel}:{fLine}");
                            }
                        }
                        foreach (var p in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>())
                        {
                            int pLine = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            ownFields.Add($"{p.Modifiers} {p.Type} {p.Identifier} {{ get; set; }} — {rel}:{pLine}");
                        }

                        continue;
                    }

                    // Derived types
                    if (td.BaseList?.Types.Any(t => t.Type.ToString().Contains(query)) == true)
                    {
                        int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        derivedTypes.Add($"{enclosing} — {rel}:{line}");
                    }

                    // Fields/properties typed as the target
                    foreach (var f in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
                    {
                        if (f.Declaration.Type.ToString().Contains(query))
                        {
                            foreach (var v in f.Declaration.Variables)
                            {
                                int fLine = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                fieldUsages.Add($"{enclosing}.{v.Identifier} — {rel}:{fLine}");
                            }
                        }
                    }
                    foreach (var p in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>())
                    {
                        if (p.Type.ToString().Contains(query))
                        {
                            int pLine = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            fieldUsages.Add($"{enclosing}.{p.Identifier} (prop) — {rel}:{pLine}");
                        }
                    }

                    // Methods with target in params or return type
                    foreach (var m in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
                    {
                        bool hasParam = m.ParameterList.Parameters.Any(p => p.Type?.ToString().Contains(query) == true);
                        bool returnsIt = m.ReturnType.ToString().Contains(query);

                        if (hasParam)
                        {
                            int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                            paramUsages.Add($"{enclosing}.{m.Identifier.Text}({parms}) — {rel}:{mLine}");
                        }
                        if (returnsIt)
                        {
                            int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            returnUsages.Add($"{enclosing}.{m.Identifier.Text}() returns {m.ReturnType} — {rel}:{mLine}");
                        }

                        // GetComponent<Target>()
                        if (m.Body != null)
                        {
                            string bodyText = m.Body.ToString();
                            if (bodyText.Contains($"GetComponent<{query}>") || bodyText.Contains($"GetComponentInChildren<{query}>"))
                            {
                                int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                getComponentUsages.Add($"{enclosing}.{m.Identifier.Text}() — {rel}:{mLine}");
                            }
                        }
                    }

                    // Local variables typed as target
                    foreach (var localDecl in td.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalDeclarationStatementSyntax>())
                    {
                        if (localDecl.Declaration.Type.ToString().Contains(query))
                        {
                            var method = localDecl.Ancestors().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().FirstOrDefault();
                            string methodName = method?.Identifier.Text ?? "?";
                            foreach (var v in localDecl.Declaration.Variables)
                            {
                                int vLine = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                localVarUsages.Add($"{enclosing}.{methodName}() var {v.Identifier} — {rel}:{vLine}");
                            }
                        }
                    }
                }
            }

            sw.Stop();

            // Build output
            var sb = new StringBuilder();

            if (sourceFiles.Count == 0 && grepLines.Count == 0)
            {
                return $"Error: '{query}' not found in source ({allFiles.Length} files scanned in {sw.ElapsedMilliseconds}ms)";
            }

            if (sourceFiles.Count > 0)
            {
                sb.AppendLine($"=== {query} === ({parsedFiles.Count} files parsed in {sw.ElapsedMilliseconds}ms)");
                sb.AppendLine();

                sb.AppendLine("Defined in:");
                foreach (var s in sourceFiles) sb.AppendLine($"  {s}");

                if (baseTypes.Count > 0)
                    sb.AppendLine($"Inherits from: {string.Join(", ", baseTypes.Distinct())}");

                if (derivedTypes.Count > 0)
                {
                    sb.AppendLine($"Inherited by ({derivedTypes.Count}):");
                    foreach (var d in derivedTypes.Take(15)) sb.AppendLine($"  {d}");
                    if (derivedTypes.Count > 15) sb.AppendLine($"  ... +{derivedTypes.Count - 15} more");
                }

                if (fieldUsages.Count > 0)
                {
                    sb.AppendLine($"Referenced as field/property ({fieldUsages.Count}):");
                    foreach (var f in fieldUsages.Take(20)) sb.AppendLine($"  {f}");
                    if (fieldUsages.Count > 20) sb.AppendLine($"  ... +{fieldUsages.Count - 20} more");
                }

                if (paramUsages.Count > 0)
                {
                    sb.AppendLine($"Passed as parameter ({paramUsages.Count}):");
                    foreach (var p in paramUsages.Take(15)) sb.AppendLine($"  {p}");
                    if (paramUsages.Count > 15) sb.AppendLine($"  ... +{paramUsages.Count - 15} more");
                }

                if (returnUsages.Count > 0)
                {
                    sb.AppendLine($"Returned by ({returnUsages.Count}):");
                    foreach (var r in returnUsages.Take(10)) sb.AppendLine($"  {r}");
                    if (returnUsages.Count > 10) sb.AppendLine($"  ... +{returnUsages.Count - 10} more");
                }

                if (getComponentUsages.Count > 0)
                {
                    sb.AppendLine($"GetComponent<{query}>() ({getComponentUsages.Count}):");
                    foreach (var g in getComponentUsages.Take(10)) sb.AppendLine($"  {g}");
                    if (getComponentUsages.Count > 10) sb.AppendLine($"  ... +{getComponentUsages.Count - 10} more");
                }

                if (localVarUsages.Count > 0)
                {
                    sb.AppendLine($"Local variables ({localVarUsages.Count}):");
                    foreach (var l in localVarUsages.Take(10)) sb.AppendLine($"  {l}");
                    if (localVarUsages.Count > 10) sb.AppendLine($"  ... +{localVarUsages.Count - 10} more");
                }

                if (ownMethods.Count > 0)
                {
                    sb.AppendLine($"Methods ({ownMethods.Count}):");
                    foreach (var m in ownMethods.Take(25)) sb.AppendLine($"  {m}");
                    if (ownMethods.Count > 25) sb.AppendLine($"  ... +{ownMethods.Count - 25} more");
                }

                if (ownFields.Count > 0)
                {
                    sb.AppendLine($"Fields/Properties ({ownFields.Count}):");
                    foreach (var f in ownFields.Take(25)) sb.AppendLine($"  {f}");
                    if (ownFields.Count > 25) sb.AppendLine($"  ... +{ownFields.Count - 25} more");
                }
            }
            else
            {
                sb.AppendLine($"Type '{query}' not found as a declaration, but found in source:");
            }

            // Grep tail — raw references not captured by structured analysis
            var sortedGrep = grepLines.OrderBy(g => g).ToList();
            if (sortedGrep.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Raw references ({sortedGrep.Count} lines) ---");
                foreach (var g in sortedGrep.Take(40)) sb.AppendLine(g);
                if (sortedGrep.Count > 40) sb.AppendLine($"... +{sortedGrep.Count - 40} more");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// CODE_SEARCH: find types, methods, fields by query syntax.
        /// Supports: class:Name, method:Name, field:Name, inherits:Type, attribute:Name, or free text.
        /// </summary>
        public static string Search(string projectPath, string query)
        {
            query = query.Trim();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string searchType = "content";
            string searchTerm = query;

            if (query.Contains(':'))
            {
                int idx = query.IndexOf(':');
                searchType = query.Substring(0, idx).ToLower();
                searchTerm = query.Substring(idx + 1);
            }

            string[] allFiles = GetSourceFiles(projectPath);

            // Parallel: read + check contains + parse matching files
            var results = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.ForEach(allFiles, file =>
            {
                try
                {
                    string text = File.ReadAllText(file);
                    if (!text.Contains(searchTerm)) return;

                    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text, path: file);
                    var root = tree.GetRoot();
                    string rel = ToRelativePath(file, projectPath);

                    switch (searchType)
                    {
                        case "class":
                        case "type":
                            foreach (var td in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                            {
                                if (td.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                {
                                    int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                    var bases = td.BaseList?.Types.Select(t => t.Type.ToString()).ToArray() ?? Array.Empty<string>();
                                    string basesStr = bases.Length > 0 ? $" : {string.Join(", ", bases)}" : "";
                                    results.Add($"{td.Modifiers} {td.Keyword} {td.Identifier.Text}{basesStr} — {rel}:{line}");
                                }
                            }
                            break;

                        case "method":
                            foreach (var td in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                            {
                                foreach (var m in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
                                {
                                    if (m.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                    {
                                        int mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                        var parms = string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                                        results.Add($"{td.Identifier.Text}.{m.Identifier.Text}({parms}) : {m.ReturnType} — {rel}:{mLine}");
                                    }
                                }
                            }
                            break;

                        case "field":
                        case "property":
                            foreach (var td in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                            {
                                foreach (var f in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
                                {
                                    foreach (var v in f.Declaration.Variables)
                                    {
                                        if (v.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                        {
                                            int fLine = v.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                            results.Add($"{td.Identifier.Text}.{v.Identifier} : {f.Declaration.Type} — {rel}:{fLine}");
                                        }
                                    }
                                }
                                foreach (var p in td.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>())
                                {
                                    if (p.Identifier.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                    {
                                        int pLine = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                        results.Add($"{td.Identifier.Text}.{p.Identifier} : {p.Type} (prop) — {rel}:{pLine}");
                                    }
                                }
                            }
                            break;

                        case "inherits":
                        case "extends":
                            foreach (var td in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                            {
                                if (td.BaseList?.Types.Any(t => t.Type.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) == true)
                                {
                                    int line = td.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                    var bases = td.BaseList.Types.Select(t => t.Type.ToString()).ToArray();
                                    results.Add($"{td.Identifier.Text} : {string.Join(", ", bases)} — {rel}:{line}");
                                }
                            }
                            break;

                        case "attribute":
                            foreach (var attr in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>())
                            {
                                if (attr.Name.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                {
                                    int aLine = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                    var parent = attr.Ancestors().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>().FirstOrDefault();
                                    string parentName = parent switch
                                    {
                                        Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax m => m.Identifier.Text + "()",
                                        Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax t => t.Identifier.Text,
                                        Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax p => p.Identifier.Text,
                                        Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.Text,
                                        _ => "?"
                                    };
                                    results.Add($"[{attr.Name}] on {parentName} — {rel}:{aLine}");
                                }
                            }
                            break;

                        default: // free text / refs
                            var fileLines = text.Split('\n');
                            for (int i = 0; i < fileLines.Length; i++)
                            {
                                if (fileLines[i].Contains(searchTerm))
                                {
                                    string lineText = fileLines[i].Trim();
                                    if (lineText.Length > 100) lineText = lineText.Substring(0, 100) + "...";
                                    results.Add($"{rel}:{i + 1}: {lineText}");
                                }
                            }
                            break;
                    }
                }
                catch { }
            });

            sw.Stop();
            var sorted = results.OrderBy(r => r).ToList();

            if (sorted.Count == 0)
                return $"No matches for '{query}' ({allFiles.Length} files scanned in {sw.ElapsedMilliseconds}ms)";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {sorted.Count} matches ({sw.ElapsedMilliseconds}ms):");
            sb.AppendLine();
            foreach (var r in sorted.Take(50))
                sb.AppendLine(r);
            if (sorted.Count > 50)
                sb.AppendLine($"... +{sorted.Count - 50} more");

            return sb.ToString().TrimEnd();
        }
    }

}
