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
    private static string _intent;       // set via --intent flag; purely descriptive
    private static bool _killIfWedged;    // set via --kill-if-wedged; auto-terminate Unity when stuck
    private static string _sessionFile;  // path of this invocation's session ledger entry (for cleanup)

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
    //   - At Main entry: start a background fetch (Task.Run, fire-and-forget) that refreshes
    //     ~/.clibridge4unity/.last_update_check from the GitHub releases/latest API.
    //   - At Main exit: read the cached JSON and print the update banner (version + release
    //     notes) to stderr. Never blocks on the network — the banner reflects whatever was
    //     in cache at exit time. Next run picks up the fresh cache.
    //   - --version calls FetchAndShowUpdate() synchronously (allowed to block up to 5s).

    static string UpdateCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity");
    static string UpdateCacheFile => Path.Combine(UpdateCacheDir, ".last_update_check");
    static string CompileTimesFile => Path.Combine(UpdateCacheDir, ".compile_times");

    /// <summary>Kick off a non-blocking refresh of the release cache if it's stale.</summary>
    static void StartBackgroundUpdateFetch()
    {
        try
        {
            if (File.Exists(UpdateCacheFile) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(UpdateCacheFile)).TotalMinutes < 30)
                return;
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

    // ───────────────────── Sessions ─────────────────────

    /// <summary>
    /// `SESSIONS` command — list other CLI agents currently active on this project.
    /// Pure presence; no Unity pipe, no locking.
    /// </summary>
    static int HandleSessionsCommand(string projectPath)
    {
        string selfId = $"cli-{Process.GetCurrentProcess().Id:X}";
        var sessions = SessionLedger.List(projectPath, excludeSessionId: selfId);
        if (sessions.Count == 0)
        {
            Console.WriteLine("No other active CLI sessions for this project.");
            return EXIT_SUCCESS;
        }
        Console.WriteLine($"{sessions.Count} active session(s):");
        foreach (var s in sessions)
        {
            string intent = string.IsNullOrEmpty(s.Intent) ? "" : $"\n    intent: {s.Intent}";
            Console.WriteLine($"  {s.SessionId}  (pid {s.Pid}, age {s.Age.TotalSeconds:F0}s)");
            Console.WriteLine($"    command: {s.Command} {s.DataSummary}{intent}");
            if (!string.IsNullOrEmpty(s.Cwd)) Console.WriteLine($"    cwd: {s.Cwd}");
        }
        return EXIT_SUCCESS;
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
                // And still refresh CLAUDE.md — content template may have shifted between
                // CLI installs even when the version number stayed the same.
                RefreshProjectClaudeMd();
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

            // Replace current exe: rename current → .old, rename .update → current.
            // If .old is locked by a stale clibridge4unity process, kill it and retry.
            string oldPath = exePath + ".old";
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); }
                catch (UnauthorizedAccessException)
                {
                    KillStaleClibridgeProcesses();
                    System.Threading.Thread.Sleep(500);
                    File.Delete(oldPath);
                }
            }

            if (File.Exists(exePath))
                File.Move(exePath, oldPath);
            File.Move(tempPath, exePath);

            // Clean up old exe
            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }

            Console.WriteLine($"Updated to v{latestVersion}");
            Console.WriteLine($"  {exePath}");

            UpdateManifestTag(latestVersion);
            RefreshProjectClaudeMd();
            return EXIT_SUCCESS;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("  Stale clibridge4unity process holding the file. Recover with:");
            Console.Error.WriteLine("    Get-Process clibridge4unity -ErrorAction SilentlyContinue | Stop-Process -Force");
            Console.Error.WriteLine("    Remove-Item \"$env:USERPROFILE\\.clibridge4unity\\clibridge4unity.exe.old\" -Force -ErrorAction SilentlyContinue");
            Console.Error.WriteLine("    clibridge4unity UPDATE");
            Console.Error.WriteLine("  Or fall back to a fresh install:");
            Console.Error.WriteLine($"    irm https://raw.githubusercontent.com/{GITHUB_REPO}/main/install.ps1 | iex");
            return EXIT_COMMAND_ERROR;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"  Run: irm https://raw.githubusercontent.com/{GITHUB_REPO}/main/install.ps1 | iex");
            return EXIT_COMMAND_ERROR;
        }
    }

    /// <summary>
    /// Kill any clibridge4unity processes on this machine other than the current one.
    /// Used to free a locked .old binary during self-update.
    /// </summary>
    static void KillStaleClibridgeProcesses()
    {
        int self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("clibridge4unity"))
        {
            if (p.Id == self) continue;
            try { p.Kill(); p.WaitForExit(3000); }
            catch { }
        }
    }

    /// <summary>
    /// Regenerate the per-project CLAUDE.md so its command reference reflects this CLI build.
    /// Called from UPDATE (both "up to date" and "upgraded" paths). No-ops when invoked
    /// outside a Unity project or when HandleInstall fails.
    /// </summary>
    static void RefreshProjectClaudeMd()
    {
        try
        {
            string projectPath = AutoDetectProjectPath();
            if (projectPath == null)
            {
                Console.WriteLine("  (Run 'clibridge4unity SETUP' inside a Unity project to refresh its CLAUDE.md.)");
                return;
            }
            HandleInstall(null, projectPath, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  (Could not refresh CLAUDE.md: {ex.GetType().Name}: {ex.Message})");
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
        // Kick off the background update fetch immediately (non-blocking). Runs
        // concurrently with the command; result is used by PrintUpdateBannerAtExit below.
        StartBackgroundUpdateFetch();
        try { return MainImpl(args); }
        finally
        {
            // Remove the session ledger entry so other agents stop seeing us as active.
            try { if (_sessionFile != null) File.Delete(_sessionFile); } catch { }
            // Print the update banner last so it appears at the bottom of the output,
            // after the command's normal stdout/stderr. Uses cached JSON only — no delay.
            try { ShowCachedUpdateNotice(); } catch { }
        }
    }

    static int MainImpl(string[] args)
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
            else if (arg == "--kill-if-wedged")
            {
                _killIfWedged = true;
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
            else if (arg == "--intent")
            {
                // Presence-only annotation — shown to other active CLI agents on this project
                // via the SessionLedger. Doesn't affect command behaviour.
                if (argIndex + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Error: --intent requires a description string");
                    return EXIT_USAGE_ERROR;
                }
                _intent = args[++argIndex];
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

        string cmdUpper = command.ToUpperInvariant();
        // Background update fetch is kicked off from Main() before we get here; the
        // banner is printed from Main()'s finally block at the bottom of output.

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

        // SESSIONS: list other active CLI agents on this project (no Unity needed).
        if (cmdUpper == "SESSIONS")
            return HandleSessionsCommand(projectPath);

        // Register this invocation in the session ledger + peek at anyone else who's active.
        // Pure presence / advisory — never blocks or rewrites command behaviour.
        _sessionFile = SessionLedger.Register(projectPath, command, data, _intent);
        try
        {
            string selfId = $"cli-{Process.GetCurrentProcess().Id:X}";
            var others = SessionLedger.List(projectPath, excludeSessionId: selfId);
            foreach (var s in others)
            {
                string intentTag = string.IsNullOrEmpty(s.Intent) ? "" : $" — {s.Intent}";
                Console.Error.WriteLine($"[sessions] {s.SessionId} active {s.Age.TotalSeconds:F0}s: {s.Command} {s.DataSummary}{intentTag}");
            }
        }
        catch { }

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
                // If Unity is busy (importing/compiling), provide context
                if (unityInfo.State == UnityProcessState.Importing)
                {
                    Console.WriteLine($"Unity appears busy: {unityInfo.ImportStatus ?? "importing/compiling"}");
                    Console.WriteLine("Main thread may be blocked by Unity internals (asset import, shader compile, GC) with no dismissible dialog.");
                    Console.WriteLine("Suggestions: (1) wait for Unity to finish, (2) run DIAG for heartbeat info, (3) check Unity Editor manually.");
                }
                else
                {
                    // Try a quick DIAG via pipe to check heartbeat
                    try
                    {
                        string diagResult = SendCommandGetResponse(pipeName, "DIAG", "");
                        if (diagResult != null && diagResult.Contains("mainThreadResponsive: no"))
                        {
                            Console.WriteLine("WARNING: Main thread is unresponsive (no dialog to dismiss).");
                            Console.WriteLine("Unity may be stuck in asset pipeline, layout rebuild, or GC.");
                            Console.WriteLine("Suggestions: (1) wait, (2) check Unity Editor manually.");
                        }
                    }
                    catch { } // pipe not available — no extra info
                }
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

        // KILL: force-terminate Unity for this project (no restart) — CLI-side, no pipe needed
        if (command.Equals("KILL", StringComparison.OrdinalIgnoreCase))
        {
            return HandleKill(projectPath, unityInfo);
        }

        // WAKEUP: bring Unity to foreground — CLI-side, no pipe needed
        // WAKEUP refresh — also sends Ctrl+R to force asset refresh/recompile
        if (command.Equals("WAKEUP", StringComparison.OrdinalIgnoreCase))
        {
            bool refresh = data != null && data.Contains("refresh", StringComparison.OrdinalIgnoreCase);
            return HandleWakeup(projectPath, refresh);
        }

        // CODE_ANALYZE: offline — served by the Roslyn daemon or single-pass source parsing.
        // Never touches Unity, so must run BEFORE the pre-flight state gates or it gets
        // spuriously blocked while Unity is loading/importing/compiling.
        if (command.Equals("CODE_ANALYZE", StringComparison.OrdinalIgnoreCase))
        {
            string daemonPipe = RoslynDaemon.GetRunningPipe(projectPath);
            if (daemonPipe == null)
            {
                Console.Error.WriteLine("[roslyn] Starting daemon...");
                daemonPipe = RoslynDaemon.StartBackground(projectPath);
            }

            if (daemonPipe != null)
            {
                string dResult = QueryDaemonWithIndexingHeartbeat(daemonPipe, "analyze", data ?? "");
                if (dResult != null)
                {
                    Console.WriteLine(dResult);
                    return dResult.StartsWith("Error:") ? EXIT_COMMAND_ERROR : EXIT_SUCCESS;
                }

                // Daemon claimed to be running but won't answer — it's stuck.
                // Kill it and start fresh before falling through to single-pass.
                Console.Error.WriteLine("[roslyn] Daemon unresponsive — killing and restarting");
                RoslynDaemon.KillAndCleanup(projectPath);
                daemonPipe = RoslynDaemon.StartBackground(projectPath);
                if (daemonPipe != null)
                {
                    string retry = QueryDaemonWithIndexingHeartbeat(daemonPipe, "analyze", data ?? "");
                    if (retry != null)
                    {
                        Console.WriteLine(retry);
                        return retry.StartsWith("Error:") ? EXIT_COMMAND_ERROR : EXIT_SUCCESS;
                    }
                }
            }

            // Fallback: single-pass Roslyn, and start daemon in background for next time
            Console.Error.WriteLine("[roslyn] Daemon unavailable, using single-pass analysis");
            Task.Run(() => RoslynDaemon.StartBackground(projectPath));

            string fallbackResult = RoslynAnalyzer.Analyze(projectPath, data ?? "");
            Console.WriteLine(fallbackResult);
            return fallbackResult.StartsWith("Error:") ? EXIT_COMMAND_ERROR : EXIT_SUCCESS;
        }

        // Offline YAML fallback: INSPECTOR with a prefab/scene asset path works without Unity
        if ((unityInfo.State == UnityProcessState.NotRunning || unityInfo.State == UnityProcessState.DifferentProject)
            && command.Equals("INSPECTOR", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(data))
        {
            string yamlResult = TryYamlInspector(projectPath, data);
            if (yamlResult != null)
            {
                Console.Error.WriteLine("[offline] Unity not running — reading YAML from disk");
                Console.WriteLine(yamlResult);
                return EXIT_SUCCESS;
            }
        }

        // Pre-flight: check if Unity is running for this project (instant, no pipe needed)
        if (unityInfo.State == UnityProcessState.NotRunning)
        {
            Console.Error.WriteLine("Error: Unity is not running. Use 'clibridge4unity OPEN' to launch Unity.");
            Console.Error.WriteLine($"       No Unity process found for project: {Path.GetFullPath(projectPath)}");
            Console.Error.WriteLine("       Open Unity Editor with this project first.");
            return EXIT_CONNECTION;
        }
        if (unityInfo.State == UnityProcessState.DifferentProject)
        {
            Console.Error.WriteLine($"Error: Unity is running but not with this project.");
            Console.Error.WriteLine($"       Expected workspace: {Path.GetFullPath(projectPath)}");
            PrintUnityWorkspaceList(unityInfo.AllWorkspaces, unityInfo.OpenProjects);
            if (!string.IsNullOrEmpty(unityInfo.ImportStatus))
                Console.Error.WriteLine($"       Status: {unityInfo.ImportStatus}");
            PrintDialogInfo(unityInfo.Dialogs);
            Console.Error.WriteLine($"       Open this workspace in Unity, or re-run with -d <path> pointing at one of the above.");
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

            // Parse --output <path> flag (strip from data before routing)
            string outputOverride = null;
            int outIdx = view.IndexOf("--output ", StringComparison.OrdinalIgnoreCase);
            if (outIdx >= 0)
            {
                outputOverride = view.Substring(outIdx + "--output ".Length).Trim();
                view = view.Substring(0, outIdx).Trim();
            }

            string viewLower = view.ToLowerInvariant();
            // "camera", "game", "gameview" route to server (need Unity to render).
            // Other known views use fast CLI-side Win32 capture of the editor window.
            string[] cliViews = { "", "editor", "scene", "inspector", "hierarchy", "console", "project", "profiler" };
            if (Array.Exists(cliViews, v => v == viewLower))
            {
                int rc = HandleScreenshot(projectPath);
                if (rc == EXIT_SUCCESS && outputOverride != null)
                    CopyScreenshotOutput(outputOverride);
                return rc;
            }

            // Server-side render — try pipe, fallback to CLI capture
            try
            {
                int rc = SendCommand(pipeName, projectPath, "SCREENSHOT", view);
                if (rc == EXIT_SUCCESS && outputOverride != null)
                    CopyScreenshotOutput(outputOverride);
                return rc;
            }
            catch
            {
                Console.Error.WriteLine("Warning: Could not connect to Unity for server-side render. Falling back to window capture.");
                int rc = HandleScreenshot(projectPath);
                if (rc == EXIT_SUCCESS && outputOverride != null)
                    CopyScreenshotOutput(outputOverride);
                return rc;
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

        // ASSET_SEARCH: server-side Unity Search + CLI-side scene/prefab YAML grep
        if (command.Equals("ASSET_SEARCH", StringComparison.OrdinalIgnoreCase))
        {
            return HandleAssetSearch(pipeName, projectPath, data);
        }

        int result = SendCommand(pipeName, projectPath, command, data);

        // Auto-retry once on main thread timeout — Unity may have just been momentarily busy
        // (e.g., finishing asset import, GC pause). Wait 3s and try again.
        if (result == EXIT_TIMEOUT && _lastResponseContainedMainThreadTimeout)
        {
            _lastResponseContainedMainThreadTimeout = false;
            Console.Error.WriteLine("Retrying in 3s (main thread was busy)...");
            WakeUnityEditor(projectPath);
            Thread.Sleep(3000);
            result = SendCommand(pipeName, projectPath, command, data);
        }

        // If still timed out after retry, just nudge — don't steal focus
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
        Console.Error.WriteLine("  --kill-if-wedged        Auto-kill Unity if heartbeat says wedged (loses unsaved work)");
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
        Console.Error.WriteLine("  KILL                       Force-terminate Unity for this project (loses unsaved work)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Key bridge commands (requires Unity):");
        Console.Error.WriteLine("  PING / STATUS / HELP       Connection and status");
        Console.Error.WriteLine("  CODE_EXEC_RETURN <code>    Execute C# and return result");
        Console.Error.WriteLine("    --inspect [depth]        Dump result object tree");
        Console.Error.WriteLine("    --trace [--maxlines N]   Line-by-line execution trace");
        Console.Error.WriteLine("  TEST [mode] [group...] [--category X,Y] [--tests A,B]");
        Console.Error.WriteLine("                             Run tests (filters OR'd; mode=editmode|playmode|all)");
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
    /// Force-terminates every Unity process associated with the given project.
    /// Returns the number of processes killed. Loses unsaved work — caller should warn.
    /// </summary>
    static int KillUnityProcesses(UnityProcessInfo unityInfo)
    {
        int killed = 0;
        foreach (uint pid in unityInfo.Pids)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                proc.Kill();
                proc.WaitForExit(10000);
                killed++;
            }
            catch { }
        }
        // Wait for process to fully exit (file locks, named pipes, etc.)
        if (killed > 0) System.Threading.Thread.Sleep(2000);
        return killed;
    }

    /// <summary>
    /// CLI-side KILL command — terminates Unity for the targeted project without restarting.
    /// </summary>
    static int HandleKill(string projectPath, UnityProcessInfo unityInfo)
    {
        if (unityInfo.State != UnityProcessState.Running &&
            unityInfo.State != UnityProcessState.Importing)
        {
            Console.Error.WriteLine($"Unity is not running for this project (state: {unityInfo.State}).");
            return EXIT_SUCCESS;
        }

        string projectName = Path.GetFileName(Path.GetFullPath(projectPath));
        Console.Error.WriteLine($"Force-terminating Unity for '{projectName}' — unsaved work will be LOST.");
        int killed = KillUnityProcesses(unityInfo);
        Console.WriteLine($"Killed {killed} Unity process(es) for '{projectName}'.");
        Console.WriteLine($"Use 'clibridge4unity OPEN' to restart.");
        return EXIT_SUCCESS;
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
            KillUnityProcesses(unityInfo);
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

            // Wait for bridge pipe to become available. While waiting, also detect Safe Mode
            // (compile errors block bridge startup), so we can fail fast with a clear message.
            string pipeName = GeneratePipeName(projectPath);
            bool safeModeRecoveryAttempted = false;
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

                // Safe Mode check — only inspect THIS project's Unity window
                var info = DetectUnityProcess(projectPath);
                if (info.TargetedWindowTitle != null
                    && info.TargetedWindowTitle.Contains("SAFE MODE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!safeModeRecoveryAttempted)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"Unity for '{projectName}' launched in Safe Mode (compile errors).");
                        Console.Error.WriteLine("  Attempting recompile (Ctrl+R) to clear it...");
                        safeModeRecoveryAttempted = true;
                        HandleWakeup(projectPath, sendRefresh: true);
                        // Give Unity ~10s to recompile and exit safe mode
                        System.Threading.Thread.Sleep(10000);
                        continue;
                    }

                    // Recompile didn't clear it — compile errors are real, give up
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Error: Unity for '{projectName}' is stuck in Safe Mode after recompile attempt.");
                    Console.Error.WriteLine("       Compile errors are blocking the bridge from starting.");
                    Console.Error.WriteLine("       Action: open Unity Editor manually, fix the compile errors,");
                    Console.Error.WriteLine("               then click 'Exit Safe Mode' (or rerun OPEN).");
                    return EXIT_COMMAND_ERROR;
                }

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

                // Strong indicator: input is clearly *shaped* like a file path (drive letter, /, ./, ~/, \\).
                // If so, and the file doesn't exist, we must NOT silently treat the path string as inline C#.
                bool looksLikePathShape = dataForFileCheck.StartsWith("@")
                    || System.Text.RegularExpressions.Regex.IsMatch(fileCandidate, @"^[A-Za-z]:[\\/]")
                    || fileCandidate.StartsWith("/")
                    || fileCandidate.StartsWith("./")
                    || fileCandidate.StartsWith("../")
                    || fileCandidate.StartsWith("~/")
                    || fileCandidate.StartsWith(@"\\");

                if (looksLikeFile && File.Exists(Path.GetFullPath(fileCandidate)))
                {
                    data = $"@{Path.GetFullPath(fileCandidate)}{trailingFlags}";
                }
                else if (looksLikePathShape && fileCandidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    // Path-shaped + .cs suffix but file doesn't exist — this is almost certainly
                    // a bad path rather than inline C# code that happens to look like a path.
                    // Refuse instead of compiling the path string as source (which produces
                    // confusing CS1056 '\' errors inside the wrapped Runner).
                    Console.Error.WriteLine($"Error: File not found: {Path.GetFullPath(fileCandidate)}");
                    Console.Error.WriteLine($"       (If you meant to execute inline C#, remove the .cs suffix or pipe the code via @path.)");
                    return EXIT_USAGE_ERROR;
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

            // Heartbeat-aware pre-wait: if Unity is busy (compiling/reloading/importing), use the
            // heartbeat to decide whether to wait inline (cheap) or bail-fast (let the agent retry).
            int waitedForBusyMs = HeartbeatAwarePreWait(projectPath, command, out string busyMsg);
            if (busyMsg != null)
            {
                Console.Error.WriteLine(busyMsg);
                return EXIT_TIMEOUT;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(1000);

            // Send command
            string message = string.IsNullOrEmpty(data) ? command : $"{command}|{data}";
            byte[] msgBytes = Encoding.UTF8.GetBytes(message + "\n");
            pipe.Write(msgBytes, 0, msgBytes.Length);
            pipe.Flush();

            // Mark command-start time so we can correlate with Unity crash dumps
            DateTime commandStart = DateTime.UtcNow;

            // Two timers:
            //   cts        — total command budget (driven by server's __timeout hint)
            //   stallCts   — short idle threshold (STALL_IDLE_MS). Server emits "__hb:N\n"
            //                every 1.5s while a command runs, so 8s of silence = 4+ missed
            //                heartbeats → assume Unity is hung and bail.
            int readTimeoutMs = 10000; // initial timeout — gets bumped to server's hint
            const int STALL_IDLE_MS = 8000;
            using var cts = new CancellationTokenSource(readTimeoutMs);
            using var stallCts = new CancellationTokenSource(STALL_IDLE_MS);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stallCts.Token);
            var responseBuilder = new StringBuilder();
            var lineBuf = new StringBuilder(); // accumulates bytes until newline so sentinel filtering is clean
            byte[] buffer = new byte[4096];
            bool gotTimeoutHint = false;

            void EmitLine(string line)
            {
                if (line.StartsWith("__hb:")) return; // heartbeat — already reset stall, drop
                if (!gotTimeoutHint && line.StartsWith("__timeout:"))
                {
                    if (int.TryParse(line.Substring("__timeout:".Length), out int hintSec))
                        readTimeoutMs = Math.Max(2000, hintSec * 1000 - waitedForBusyMs);
                    gotTimeoutHint = true;
                    cts.CancelAfter(readTimeoutMs);
                    return;
                }
                Console.WriteLine(line);
                responseBuilder.Append(line).Append('\n');
            }

            try
            {
                while (true)
                {
                    var readTask = pipe.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token);
                    readTask.Wait(linkedCts.Token);
                    int bytesRead = readTask.Result;
                    if (bytesRead == 0) break;

                    // Any byte (including a heartbeat) resets the stall + total timers.
                    stallCts.CancelAfter(STALL_IDLE_MS);
                    cts.CancelAfter(readTimeoutMs);

                    lineBuf.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    while (true)
                    {
                        string s = lineBuf.ToString();
                        int nl = s.IndexOf('\n');
                        if (nl < 0) break;
                        EmitLine(s.Substring(0, nl));
                        lineBuf.Remove(0, nl + 1);
                    }
                }
                if (lineBuf.Length > 0) EmitLine(lineBuf.ToString());
            }
            catch (Exception readEx) when (readEx is OperationCanceledException
                                         || (readEx is AggregateException ae && ae.InnerException is OperationCanceledException)
                                         || readEx is IOException)
            {
                bool stallFired = stallCts.IsCancellationRequested && !cts.IsCancellationRequested;
                string crashReport = DetectUnityCrash(commandStart);
                if (crashReport != null)
                {
                    Console.Error.WriteLine($"\nError: Unity CRASHED during '{command}'.");
                    Console.Error.Write(crashReport);
                    return EXIT_COMMAND_ERROR;
                }
                if (readEx is IOException)
                {
                    Console.Error.WriteLine($"\nError: Pipe disconnected during '{command}' (likely domain reload).");
                    return EXIT_CONNECTION;
                }
                if (stallFired)
                {
                    Console.Error.WriteLine($"\nError: Command '{command}' stalled (no heartbeat for {STALL_IDLE_MS / 1000}s). Unity may be hung.");
                }
                else
                {
                    Console.Error.WriteLine($"\nError: Command '{command}' timed out after {readTimeoutMs / 1000}s.");
                }
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
    /// Detects whether Unity hard-crashed during a command. Returns a formatted report with
    /// the crash signature (top of the stack trace) and dump path, or null if no recent crash.
    /// </summary>
    static string DetectUnityCrash(DateTime commandStartUtc)
    {
        try
        {
            string crashesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "Unity", "Editor", "Crashes");
            if (!Directory.Exists(crashesDir)) return null;

            var newest = new DirectoryInfo(crashesDir)
                .GetDirectories("Crash_*")
                .Where(d => d.LastWriteTimeUtc > commandStartUtc.AddSeconds(-5))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("--- Unity Crash Detected ---");
            sb.AppendLine($"crashDir: {newest.FullName}");
            sb.AppendLine($"crashedAt: {newest.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

            // Pull the top of the stack from the crash's Editor.log so the LLM sees the cause
            string editorLog = Path.Combine(newest.FullName, "Editor.log");
            if (File.Exists(editorLog))
            {
                var lines = File.ReadAllLines(editorLog);
                int stackStart = -1;
                for (int i = lines.Length - 1; i >= 0 && i > lines.Length - 200; i--)
                {
                    if (lines[i].Contains("OUTPUTTING STACK TRACE"))
                    {
                        stackStart = i;
                        break;
                    }
                }
                if (stackStart >= 0)
                {
                    sb.AppendLine("--- Stack signature (top frames) ---");
                    int shown = 0;
                    for (int i = stackStart + 1; i < lines.Length && shown < 15; i++)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.Contains("END OF STACKTRACE")) break;
                        sb.AppendLine($"  {line}");
                        shown++;
                    }
                }
            }

            sb.AppendLine("action: Open the crash dir above for the full dump. If this repeats, it's a Unity/package bug — report it.");
            return sb.ToString();
        }
        catch
        {
            return null;
        }
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
        string targetedProjectName = Path.GetFileName(Path.GetFullPath(projectPath));

        // Safe mode applies ONLY to the Unity instance for the targeted project.
        // Other Unity instances on this machine (different projects) being in safe mode is irrelevant here.
        bool targetedInSafeMode = info.TargetedWindowTitle != null
            && info.TargetedWindowTitle.Contains("SAFE MODE", StringComparison.OrdinalIgnoreCase);

        sb.AppendLine($"targetedProject: {targetedProjectName}");
        sb.AppendLine($"state: {(targetedInSafeMode ? "SafeMode" : info.State.ToString())}");

        if (info.TargetedWindowTitle != null)
        {
            sb.AppendLine($"targetedUnityWindow: {info.TargetedWindowTitle}");
        }

        // Show other Unity instances separately so it's obvious they aren't ours
        var otherWindows = info.OpenProjects
            .Where(t => t != info.TargetedWindowTitle)
            .ToList();
        if (otherWindows.Count > 0)
        {
            sb.AppendLine($"otherUnityInstances ({otherWindows.Count}, NOT this project):");
            foreach (var title in otherWindows)
            {
                bool otherSafe = title.Contains("SAFE MODE", StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"  - {title}{(otherSafe ? "   [in SafeMode — unrelated to this project]" : "")}");
            }
        }

        if (targetedInSafeMode)
        {
            sb.AppendLine("--- SAFE MODE ---");
            sb.AppendLine($"Unity for project '{targetedProjectName}' is in Safe Mode due to compile errors.");
            sb.AppendLine("action: Fix compile errors in Unity Editor, exit Safe Mode, then retry.");
            return sb.ToString();
        }

        // Targeted project's Unity isn't running — make sure the AI doesn't conflate this with safe mode
        if (info.State == UnityProcessState.NotRunning || info.State == UnityProcessState.DifferentProject)
        {
            sb.AppendLine($"note: Unity is NOT currently running for '{targetedProjectName}'.");
            if (otherWindows.Any(t => t.Contains("SAFE MODE", StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine("      (One of the other Unity instances above is in Safe Mode, but that is a different project — not relevant here.)");
            sb.AppendLine($"action: Open '{projectPath}' in Unity, then retry.");
            return sb.ToString();
        }

        // Stuck domain reload detection (reads stale heartbeat — works even mid-reload)
        string stuckMsg = CheckStuckDomainReload(projectPath, thresholdSec: 60);
        if (stuckMsg != null)
        {
            sb.AppendLine(stuckMsg);
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

    static void PrintUnityWorkspaceList(List<UnityWorkspaceInfo> workspaces, List<string> fallbackTitles)
    {
        if (workspaces != null && workspaces.Count > 0)
        {
            Console.Error.WriteLine($"       Running Unity workspaces ({workspaces.Count}):");
            foreach (var w in workspaces.OrderBy(w => w.ProjectPath ?? ""))
            {
                Console.Error.WriteLine($"         {w.ProjectPath}");
                if (!string.IsNullOrEmpty(w.WindowTitle))
                    Console.Error.WriteLine($"           pid {w.Pid} — {w.WindowTitle}");
                else
                    Console.Error.WriteLine($"           pid {w.Pid}");
            }
            return;
        }

        // WMI failed — fall back to whatever window titles we caught.
        if (fallbackTitles != null && fallbackTitles.Count > 0)
        {
            Console.Error.WriteLine($"       Unity windows seen ({fallbackTitles.Count}):");
            foreach (var t in fallbackTitles)
                Console.Error.WriteLine($"         {t}");
        }
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
        public List<string> OpenProjects = new List<string>(); // titles of ALL Unity windows on this machine — not just this project
        public string TargetedWindowTitle;  // the window title that matches THIS project (null if not found)
        public List<DialogInfo> Dialogs = new List<DialogInfo>(); // dialog windows with buttons/text
        public List<string> RecentErrors = new List<string>();  // from Editor.log
        public string HeartbeatState;     // from heartbeat file: ready, compiling, reloading, etc.
        public int HeartbeatCompileTimeAvg; // avg compile time from heartbeat file
        public long HeartbeatStateEnteredAtUnix; // when Unity entered HeartbeatState (unix seconds)
        public bool MatchedByCommandLine; // true if project matched via -projectPath arg, not window title
        public HashSet<uint> Pids = new HashSet<uint>();  // Unity process IDs for this project
        public List<UnityWorkspaceInfo> AllWorkspaces = new List<UnityWorkspaceInfo>(); // every Unity instance on this machine
        public string EditorLogPath;  // per-instance Editor.log (from -logFile arg or header-matched scan)
    }

    /// <summary>
    /// Summary of one running Unity instance. The workspace path (from `-projectPath`)
    /// is the stable identifier we show to the user when they're connected to the wrong Unity.
    /// </summary>
    class UnityWorkspaceInfo
    {
        public uint Pid;
        public string ProjectPath;  // from command-line -projectPath (authoritative)
        public string WindowTitle;  // e.g. "MyProject - Main - Windows, Mac, Linux - Unity 6000.3.1f1"
    }

    /// <summary>
    /// Enumerate every non-batch Unity.exe process on the machine with its project path
    /// (read via WMI from the `-projectPath` command-line arg) and main-window title.
    /// Used to tell the user exactly which Unity instances are open when auto-detect misses.
    /// </summary>
    static List<UnityWorkspaceInfo> EnumerateUnityWorkspaces()
    {
        var results = new List<UnityWorkspaceInfo>();
        var byPid = new Dictionary<uint, UnityWorkspaceInfo>();

        try
        {
            foreach (var proc in Process.GetProcessesByName("Unity"))
            {
                try
                {
                    uint pid = (uint)proc.Id;
                    var info = new UnityWorkspaceInfo { Pid = pid };
                    if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(proc.MainWindowTitle))
                        info.WindowTitle = proc.MainWindowTitle;
                    byPid[pid] = info;
                }
                catch { }
            }
        }
        catch { return results; }

        if (byPid.Count == 0) return results;

        // Get command-lines via `wmic` rather than System.Management — the latter's static
        // initializer throws when this app is published as a single-file trimmed binary.
        var cmdLines = ReadUnityCommandLines();
        foreach (var (pid, cmdLine) in cmdLines)
        {
            // Skip AssetImportWorker / CLI child processes — they're not standalone editors
            if (cmdLine.Contains("-batchMode", StringComparison.OrdinalIgnoreCase)) continue;

            if (!byPid.TryGetValue(pid, out var info))
            {
                info = new UnityWorkspaceInfo { Pid = pid };
                byPid[pid] = info;
            }

            var m = System.Text.RegularExpressions.Regex.Match(
                cmdLine,
                @"-(?:projectpath|createProject)\s+(?:""([^""]+)""|(\S+))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                try { info.ProjectPath = Path.GetFullPath(path); }
                catch { info.ProjectPath = path; }
            }
        }

        // Only include instances we got a project path for — the secondary Unity 6 scripting
        // runtime processes don't have -projectPath and would just add noise.
        foreach (var info in byPid.Values)
            if (!string.IsNullOrEmpty(info.ProjectPath))
                results.Add(info);

        return results;
    }

    /// <summary>
    /// Returns (pid, commandLine) for every Unity.exe process, obtained via `wmic`.
    /// Works around System.Management being broken under trimmed single-file publish.
    /// </summary>
    static List<(uint pid, string cmdLine)> ReadUnityCommandLines()
    {
        var results = new List<(uint, string)>();
        bool diag = Environment.GetEnvironmentVariable("CLIBRIDGE_DEBUG") == "1";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "process where \"name='Unity.exe'\" get ProcessId,CommandLine /format:list",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return results;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            // wmic /format:list emits key=value blocks separated by blank lines.
            string currentCmd = null;
            uint currentPid = 0;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    if (currentCmd != null && currentPid != 0)
                        results.Add((currentPid, currentCmd));
                    currentCmd = null; currentPid = 0;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                if (key.Equals("CommandLine", StringComparison.OrdinalIgnoreCase)) currentCmd = value;
                else if (key.Equals("ProcessId", StringComparison.OrdinalIgnoreCase)) uint.TryParse(value, out currentPid);
            }
            // Flush the last block (file may not end with a blank line).
            if (currentCmd != null && currentPid != 0)
                results.Add((currentPid, currentCmd));

            if (diag) Console.Error.WriteLine($"[CLIBRIDGE_DEBUG] wmic returned {results.Count} Unity.exe rows");
        }
        catch (Exception ex)
        {
            if (diag) Console.Error.WriteLine($"[CLIBRIDGE_DEBUG] wmic failed: {ex.GetType().Name}: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Reads the heartbeat status file written by Unity-side Heartbeat.cs.
    /// Returns null if file doesn't exist or is stale (>10s old).
    /// </summary>
    // Commands that legitimately want to talk to a busy Unity (poll status, dump logs, trigger compile, etc.).
    // Everything else gets the heartbeat-aware bail-fast / inline-wait treatment.
    static readonly HashSet<string> BusyAwareBypassCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "STATUS", "DIAG", "PROBE", "PING", "LOG", "HELP", "COMPILE", "REFRESH", "WAKEUP", "DISMISS"
    };

    /// <summary>
    /// If Unity is busy per heartbeat, decide: wait inline (returns ms waited) or bail (sets busyMessage).
    /// Heuristic: budget = 15s. est_remaining = max(0, compileTimeAvg - elapsed_in_state).
    ///   est_remaining > budget * 0.5  → bail (let the agent retry when ready)
    ///   est_remaining ≤ budget * 0.5  → sleep est_remaining, return that as ms waited
    ///   elapsed > avg * 2             → bail (Unity is wedged, no point waiting)
    /// </summary>
    static int HeartbeatAwarePreWait(string projectPath, string command, out string busyMessage)
    {
        busyMessage = null;
        if (BusyAwareBypassCommands.Contains(command)) return 0;

        var hb = ReadHeartbeatFile(projectPath);
        if (!hb.HasValue)
        {
            // Stale heartbeat = domain reload in progress. Check if it's been frozen too long.
            string stuck = CheckStuckDomainReload(projectPath);
            if (stuck != null) { busyMessage = stuck; return -1; }
            return 0;
        }

        string state = hb.Value.state;
        long enteredAt = hb.Value.stateEnteredAt;
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Settle wait: "ready" that JUST flipped (<2s ago) often chains into another reload
        // (e.g. Google PlayServices Resolver patches AndroidManifest then calls
        //  AssetDatabase.Refresh(ForceSynchronousImport|ForceDomainReload) → second 7-8s reload).
        // One-shot sleep misses cascades. Poll every 500ms until "ready" has been CONTINUOUSLY
        // stable for 2s (or 30s max). If state goes non-ready mid-poll, reset the counter and
        // keep waiting — this handles N-deep reload chains automatically.
        if (state == "ready" || state == "playing" || state == "paused")
        {
            if (state == "ready" && enteredAt > 0)
            {
                int sinceReady = (int)Math.Max(0, nowUnix - enteredAt);
                if (sinceReady < 2)
                {
                    const int pollMs = 500;
                    const int requiredStableMs = 2000;
                    const int maxWaitMs = 30000;
                    int stableMs = sinceReady * 1000; // credit already-stable time
                    int totalWaitMs = 0;
                    while (stableMs < requiredStableMs && totalWaitMs < maxWaitMs)
                    {
                        System.Threading.Thread.Sleep(pollMs);
                        totalWaitMs += pollMs;
                        var newHb = ReadHeartbeatFile(projectPath);
                        if (!newHb.HasValue) break;
                        if (newHb.Value.state == "ready")
                            stableMs += pollMs;
                        else
                            stableMs = 0; // cascade reload started — reset stability counter
                    }
                    return totalWaitMs;
                }
            }
            return 0;
        }

        int avg = hb.Value.compileTimeAvg;
        int elapsed = enteredAt > 0 ? (int)Math.Max(0, nowUnix - enteredAt) : 0;
        int estRemaining = avg > 0 ? Math.Max(0, avg - elapsed) : 0;

        // Half of the typical 15s read budget — rounded down. If Unity needs longer than this
        // we bail rather than burning most of the budget on the wait alone.
        const int waitInlineThresholdSec = 7;

        // Wedged: been busy way longer than average. Either kill (if --kill-if-wedged) or bail.
        if (avg > 0 && elapsed > avg * 2)
        {
            if (_killIfWedged)
            {
                Console.Error.WriteLine($"[CLI] Unity wedged in '{state}' for {elapsed}s (avg {avg}s). --kill-if-wedged → terminating.");
                var info = DetectUnityProcess(projectPath);
                int killed = KillUnityProcesses(info);
                busyMessage = $"Error: Killed {killed} Unity process(es). Run 'clibridge4unity OPEN' to restart.\n" +
                              $"       Unsaved work was lost.";
                return -1;
            }
            busyMessage = $"Error: Unity stuck in '{state}' for {elapsed}s (avg {avg}s). Possibly wedged.\n" +
                          $"       Try: clibridge4unity DIAG, or 'clibridge4unity KILL' to force-terminate (loses unsaved work).";
            return -1;
        }

        // Long enough to bail — agent retries when state becomes ready.
        if (estRemaining > waitInlineThresholdSec)
        {
            busyMessage = $"busy: {state} (~{estRemaining}s remaining, elapsed {elapsed}s of avg {avg}s)\n" +
                          $"retry: clibridge4unity {command} ...";
            return -1;
        }

        // Short enough to wait inline — but consume from the read budget so we still fail fast if Unity hangs.
        if (estRemaining > 0)
        {
            System.Threading.Thread.Sleep(estRemaining * 1000);
            return estRemaining * 1000;
        }

        return 0;
    }

    static (string state, bool compileErrors, int compileErrorCount, int compileTimeAvg, long stateEnteredAt)?
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
            long enteredAt = 0;
            var ecMatch = System.Text.RegularExpressions.Regex.Match(json, @"""compileErrorCount"":\s*(\d+)");
            if (ecMatch.Success) errorCount = int.Parse(ecMatch.Groups[1].Value);
            var atMatch = System.Text.RegularExpressions.Regex.Match(json, @"""compileTimeAvg"":\s*(\d+)");
            if (atMatch.Success) avgTime = int.Parse(atMatch.Groups[1].Value);
            var enMatch = System.Text.RegularExpressions.Regex.Match(json, @"""stateEnteredAt"":\s*(\d+)");
            if (enMatch.Success) enteredAt = long.Parse(enMatch.Groups[1].Value);

            return (state, errors, errorCount, avgTime, enteredAt);
        }
        catch { return null; }
    }

    // Reads heartbeat file ignoring the freshness limit — used to detect stuck domain reloads.
    static (string state, long stateEnteredAt, double fileAgeSec)? ReadStaleHeartbeat(string projectPath)
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

            double ageSec = (DateTime.UtcNow - File.GetLastWriteTimeUtc(statusFile)).TotalSeconds;
            string json = File.ReadAllText(statusFile);
            string state = ExtractJsonString(json, "state") ?? "unknown";
            long enteredAt = 0;
            var m = System.Text.RegularExpressions.Regex.Match(json, @"""stateEnteredAt"":\s*(\d+)");
            if (m.Success) enteredAt = long.Parse(m.Groups[1].Value);
            return (state, enteredAt, ageSec);
        }
        catch { return null; }
    }

    // Returns a KILL+OPEN suggestion string if heartbeat shows Unity stuck in reload/compile >thresholdSec.
    // Returns null if not stuck (or can't tell).
    static string CheckStuckDomainReload(string projectPath, int thresholdSec = 60)
    {
        var stale = ReadStaleHeartbeat(projectPath);
        if (stale == null) return null;
        var (state, enteredAt, fileAgeSec) = stale.Value;
        if (state != "reloading" && state != "compiling" && state != "importing") return null;
        if (fileAgeSec < thresholdSec) return null;

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int elapsedSec = enteredAt > 0 ? (int)(nowUnix - enteredAt) : (int)fileAgeSec;
        if (elapsedSec < thresholdSec) return null;

        return $"Error: Unity stuck in '{state}' for {elapsedSec}s (heartbeat last updated {(int)fileAgeSec}s ago — domain reload frozen).\n" +
               $"action: clibridge4unity KILL && clibridge4unity OPEN";
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
            info.HeartbeatStateEnteredAtUnix = heartbeat.Value.stateEnteredAt;
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

        // Discover Unity processes, but only keep PIDs that belong to THIS project.
        // With multiple Unity instances open, collecting all PIDs would leak the
        // other project's dialogs (e.g. a concurrent IL2CPP build) into our report.
        // (Unity 6 uses separate processes for UI and scripting runtime.)
        var allUnityPids = new HashSet<uint>();
        var unityPids = new HashSet<uint>(); // filtered: only this project's PIDs
        bool foundProject = false;
        string fullProjectPath = Path.GetFullPath(projectPath).TrimEnd('\\', '/');

        try
        {
            foreach (var proc in Process.GetProcessesByName("Unity"))
            {
                try
                {
                    allUnityPids.Add((uint)proc.Id);
                    if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(proc.MainWindowTitle))
                    {
                        info.OpenProjects.Add(proc.MainWindowTitle);
                        if (proc.MainWindowTitle.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundProject = true;
                            unityPids.Add((uint)proc.Id);
                            info.TargetedWindowTitle = proc.MainWindowTitle;
                        }
                    }
                }
                catch { }
            }

            // Command-line pass: match by `-projectPath`. Needed when the window title
            // isn't available yet (early load, import, upgrade dialog) AND to catch Unity 6's
            // secondary scripting-runtime process for this project. Uses `wmic` because
            // System.Management is unreliable under trimmed single-file publish.
            if (allUnityPids.Count > 0)
            {
                foreach (var (pid, cmdLine) in ReadUnityCommandLines())
                {
                    if (cmdLine.Contains("-batchMode", StringComparison.OrdinalIgnoreCase)) continue;
                    if (cmdLine.Contains(fullProjectPath, StringComparison.OrdinalIgnoreCase) ||
                        cmdLine.Contains(fullProjectPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        unityPids.Add(pid);
                        if (!foundProject)
                        {
                            foundProject = true;
                            info.MatchedByCommandLine = true;
                        }

                        // Extract -logFile path for this specific instance (authoritative per-workspace log)
                        if (info.EditorLogPath == null)
                        {
                            var logMatch = System.Text.RegularExpressions.Regex.Match(
                                cmdLine, @"-logFile\s+(""([^""]+)""|(\S+))",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (logMatch.Success)
                            {
                                string logPath = logMatch.Groups[2].Success ? logMatch.Groups[2].Value : logMatch.Groups[3].Value;
                                if (logPath != "-" && !string.IsNullOrWhiteSpace(logPath))
                                    info.EditorLogPath = Path.GetFullPath(logPath);
                            }
                        }
                    }
                }
            }

            // Enumerate visible windows, but ONLY from PIDs matched to this project.
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
            // No Unity matched this project — enumerate every running workspace so the
            // user can see exactly which instances exist (and why we couldn't find theirs).
            info.AllWorkspaces = EnumerateUnityWorkspaces();
            info.State = allUnityPids.Count > 0 ? UnityProcessState.DifferentProject : UnityProcessState.NotRunning;
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
    /// 2. Per-instance Editor.log — scans all Editor*.log files to find the one for this project
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

        // Source 2: Per-instance Editor.log — prefer -logFile path, else find newest file whose header matches
        if (info.RecentErrors.Count == 0)
        {
            try
            {
                string editorLog = info.EditorLogPath;

                if (editorLog == null || !File.Exists(editorLog))
                {
                    editorLog = FindEditorLogForProject(projectPath);
                    if (editorLog != null) info.EditorLogPath = editorLog;
                }

                if (editorLog == null || !File.Exists(editorLog)) return;

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

    /// <summary>
    /// Find the Unity Editor log file for a given project by scanning the standard log dir
    /// and matching each file's header against the project path. Prefers the most recently
    /// modified match (that's the currently-active session). Excludes user-made copies.
    /// </summary>
    static string FindEditorLogForProject(string projectPath)
    {
        try
        {
            string editorLogDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unity", "Editor");
            if (!Directory.Exists(editorLogDir)) return null;

            string absProject = Path.GetFullPath(projectPath).Replace("/", "\\").TrimEnd('\\');

            // Unity's real log names: Editor.log, Editor-prev.log, Editor-1.log, Editor-2.log...
            // User backups (e.g. "Editor - Copy.log") contain spaces — exclude them.
            var candidates = Directory.EnumerateFiles(editorLogDir, "Editor*.log")
                .Where(f => !Path.GetFileName(f).Contains(' '))
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc);

            foreach (var fi in candidates)
            {
                try
                {
                    string header = ReadFileHead(fi.FullName, 4096);
                    if (header.Contains(absProject, StringComparison.OrdinalIgnoreCase))
                        return fi.FullName;
                }
                catch { }
            }
        }
        catch { }
        return null;
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
        const string marker = "# Unity Bridge (clibridge4unity) - Tool Reference";
        const string endMarker = "<!-- END clibridge4unity -->";

        var md = new StringBuilder();
        md.AppendLine(marker);
        md.AppendLine();
        md.AppendLine("CLI for Unity Editor automation via named pipes. Run `clibridge4unity -h` for the full command list.");
        md.AppendLine();
        md.AppendLine("## Prefer existing commands over ad-hoc C#");
        md.AppendLine();
        md.AppendLine("Before reaching for CODE_EXEC_RETURN to inspect the scene, try these — they're already structured:");
        md.AppendLine();
        md.AppendLine("- `INSPECTOR` — full active-scene hierarchy (brief, all roots recursed)");
        md.AppendLine("- `INSPECTOR Canvas/Panel` — one GameObject, all serialized fields");
        md.AppendLine("- `INSPECTOR Canvas/Panel --depth 2` / `--children` — recurse subtree");
        md.AppendLine("- `INSPECTOR Canvas/Panel --filter Button` — subtree filtered by GameObject name OR component name");
        md.AppendLine("- `INSPECTOR Canvas/Panel --children --brief` — subtree, components only (no field dumps — concise)");
        md.AppendLine("- `INSPECTOR Assets/Prefabs/Foo.prefab [--children] [--brief] [--filter X]` — prefab asset (replaces old PREFAB_HIERARCHY)");
        md.AppendLine("- `FIND Name` — scene (default) | `FIND prefab:Assets/UI/Menu.prefab/Button,Panel` — find by name inside a prefab asset (comma = OR)");
        md.AppendLine();
        md.AppendLine("## CODE_EXEC / CODE_EXEC_RETURN");
        md.AppendLine();
        md.AppendLine("Own Roslyn compiler — works even when Unity's main thread is busy.");
        md.AppendLine();
        md.AppendLine("**For multi-line scripts or anything containing `$\"...\"`, write the C# to a file and pass its path** —");
        md.AppendLine("the CLI auto-detects file paths. This avoids bash escaping of `$`, `\"`, backticks and the 32KB cmdline limit:");
        md.AppendLine();
        md.AppendLine("```");
        md.AppendLine("clibridge4unity CODE_EXEC_RETURN /tmp/snippet.cs");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("**Put the temp .cs file OUTSIDE the Unity project** (`$TEMP`, `/tmp`, `~/.cache`, etc.) —");
        md.AppendLine("writing it under `Assets/` or `Packages/` will trigger a Unity asset import + recompile, which kills the pipe.");
        md.AppendLine();
        md.AppendLine("Flags: `--inspect [depth]` dumps the result tree, `--trace` emits line-by-line execution, `--vars x,y` filters.");
        md.AppendLine();
        md.AppendLine("## CODE_ANALYZE — offline code search (works without Unity)");
        md.AppendLine();
        md.AppendLine("- `CODE_ANALYZE Foo` — deep view: definition, usages, derived types, GetComponent sites, own members");
        md.AppendLine("- `CODE_ANALYZE Foo.Bar` — zoom into one member");
        md.AppendLine("- `CODE_ANALYZE method:Name` — every method matching `Name` across the codebase (+ signatures)");
        md.AppendLine("- `CODE_ANALYZE field:Name` / `property:Name` — same for fields/properties");
        md.AppendLine("- `CODE_ANALYZE inherits:Type` — derived types");
        md.AppendLine("- `CODE_ANALYZE attribute:Name` — attribute usage sites");
        md.AppendLine();
        md.AppendLine("## SCREENSHOT — single command, smart routing");
        md.AppendLine();
        md.AppendLine("One command, four routing modes. Output PNGs land in `%TEMP%/clibridge4unity_screenshots/` (overwrites previous).");
        md.AppendLine("All renders are capped at **1280px** on the long edge to keep PNGs small — vision models choke on 4K images.");
        md.AppendLine();
        md.AppendLine("- `SCREENSHOT` (no args) — CLI-side window capture of the whole Unity editor. No pipe needed; works while Unity is busy/compiling.");
        md.AppendLine("- `SCREENSHOT editor|scene|inspector|hierarchy|console|project|profiler` — capture that editor view via Win32 PrintWindow.");
        md.AppendLine("- `SCREENSHOT camera [WxH]` — server-side render of `Camera.main` only (default 960x540). **No overlays** — OnGUI / runtime UI Toolkit / IMGUI debug aren't drawn.");
        md.AppendLine("- `SCREENSHOT gameview` — captures the GameView tab (via `GrabPixels`), so OnGUI, runtime `UIDocument`, and the GameView chrome all show up. Use this to see what the player actually sees.");
        md.AppendLine("- `SCREENSHOT Player` — find a scene GameObject by name, render it. 3D objects → 3-view atlas (front|right|top). UI under a Canvas → render that Canvas.");
        md.AppendLine("- `SCREENSHOT Assets/Foo.prefab` — render a prefab asset. UI prefabs auto-size from RectTransform/Canvas; 3D prefabs render an 8-angle turntable.");
        md.AppendLine("- `SCREENSHOT Assets/UI/Foo.uxml` — render a UXML file at 800x450 via an offscreen EditorWindow. UXML and its `.uss`/`.tss` deps are force-reimported first, so on-disk edits show up immediately.");
        md.AppendLine("- `SCREENSHOT Assets/UI/Foo.uxml --el #card-grid` — render only a sub-element. `--el` accepts `#name`, `.class`, or a bare name (tries name then class).");
        md.AppendLine("- `SCREENSHOT a.prefab b.prefab c.prefab` — multi-asset grid render (one image, labeled cells).");
        md.AppendLine("- `SCREENSHOT --output path/file.png ...` — also copy the result to a chosen path.");
        md.AppendLine();
        md.AppendLine("Tips:");
        md.AppendLine("- Asset-path mode is the right tool when you want to *see* a prefab/UXML — it doesn't require Unity to have the scene set up.");
        md.AppendLine("- Window-view mode is the only mode that works when Unity is mid-compile (no pipe needed).");
        md.AppendLine("- The result lines include `output: <path>` — read it back with the Read tool to view the PNG.");
        md.AppendLine();
        md.AppendLine("## Other workflows");
        md.AppendLine();
        md.AppendLine("- Build / state: `COMPILE` | `REFRESH` | `STATUS` | `LOG errors` | `DIAG` (always works — no main thread needed) | `PROBE` (quick main-thread health)");
        md.AppendLine("- Scene: `PLAY` | `STOP` | `PAUSE` | `STEP` | `CREATE` | `FIND` | `DELETE` | `SAVE` | `LOAD` | `SCENEVIEW frame|2d|3d` | `WINDOWS` | `GAMEVIEW WxH`");
        md.AppendLine("- Components: `COMPONENT_SET obj comp field value` | `COMPONENT_ADD obj comp` | `COMPONENT_REMOVE obj comp`");
        md.AppendLine("- Prefabs: `PREFAB_CREATE name path` | `PREFAB_INSTANTIATE path [parent]`");
        md.AppendLine("- Assets: `ASSET_SEARCH query` | `ASSET_DISCOVER [category]` | `ASSET_MOVE src dst` | `ASSET_COPY src dst` | `ASSET_DELETE path...` | `ASSET_MKDIR path...` | `ASSET_LABEL path [+a -b]` | `ASSET_RESERIALIZE [paths...]`");
        md.AppendLine("- Tests: `TEST` (EditMode) | `TEST playmode` | `TEST all` | `TEST Group1,Group2` | `TEST --category A,B` | `TEST --tests Full.Name,Other.Name` (filters OR'd) | `TEST list [filter]`");
        md.AppendLine("- Menu / profiler: `MENU Window/General/Console` | `PROFILE [enable|disable|clear|hierarchy]`");
        md.AppendLine("- Window mgmt: `WAKEUP` (bring Unity to front) | `WAKEUP refresh` (front + Ctrl+R) | `DISMISS` (close modal dialogs)");
        md.AppendLine();
        md.AppendLine("## Diagnosing a stuck command");
        md.AppendLine();
        md.AppendLine("If a command hangs or returns a main-thread timeout: run `DIAG` first (always responds, no main thread). It tells you whether Unity is compiling, importing, has open dialogs, or is just slow. `LOG errors` shows what's broken. `WAKEUP` brings Unity forward if it's been backgrounded.");

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

        string content = md.ToString().TrimEnd() + "\n" + endMarker + "\n";

        // Self-check: the marker must appear in the content we emit, otherwise replace-in-place
        // breaks and every re-run duplicates the block.
        if (!content.Contains(marker))
        {
            Console.Error.WriteLine($"BUG: CLAUDE.md content missing start marker '{marker}'. Aborting.");
            return EXIT_COMMAND_ERROR;
        }

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

    /// <summary>
    /// Query the Roslyn daemon, polling through the "__indexing:N/M" sentinel.
    /// Renders a single-line stderr heartbeat while the daemon's background indexer
    /// catches up. Returns early if progress stalls (no heartbeat for 5s) — better to
    /// fall back to single-pass than block on a wedged indexer.
    /// </summary>
    static string QueryDaemonWithIndexingHeartbeat(string pipeName, string endpoint, string query)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        bool printedHeartbeat = false;
        string lastProgress = null;
        DateTime lastProgressChange = DateTime.UtcNow;
        const int STALL_SECONDS = 5;

        while (DateTime.UtcNow < deadline)
        {
            string r = RoslynDaemon.Query(pipeName, endpoint, query);
            if (r == null)
            {
                if (printedHeartbeat) Console.Error.WriteLine();
                return null;
            }
            if (r.StartsWith("__indexing:"))
            {
                string progress = r.Substring("__indexing:".Length);
                if (progress != lastProgress)
                {
                    lastProgress = progress;
                    lastProgressChange = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - lastProgressChange).TotalSeconds >= STALL_SECONDS)
                {
                    if (printedHeartbeat) Console.Error.WriteLine();
                    Console.Error.WriteLine($"[roslyn] daemon stalled at {progress} for {STALL_SECONDS}s — falling back");
                    return null; // Caller falls back to single-pass.
                }
                Console.Error.Write($"\r[roslyn] indexing {progress}        ");
                printedHeartbeat = true;
                Thread.Sleep(500);
                continue;
            }
            if (printedHeartbeat) Console.Error.WriteLine();
            return r;
        }
        if (printedHeartbeat) Console.Error.WriteLine();
        return "Error: Daemon indexing exceeded 120s. Retry shortly or run `clibridge4unity DAEMON status`.";
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

    // ---- Offline YAML Inspector ----

    class YamlObj
    {
        public long   FileId;
        public int    TypeId;
        public string TypeName = "";
        public string Name     = "";
        public bool   IsActive = true;
        public bool   Enabled  = true;
        public long   Father;       // Transform: parent Transform fileId (0 = root)
        public long   GameObjectId; // component: owning GameObject fileId
        public string ScriptGuid;   // MonoBehaviour: m_Script guid
        public List<long>              Components = new();
        public List<long>              Children   = new();
        public Dictionary<string,string> Fields   = new();
    }

    static readonly HashSet<string> _yamlSkipFields = new(StringComparer.Ordinal)
    {
        "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
        "m_EditorHideFlags", "m_EditorClassIdentifier", "m_PrefabParentObject", "m_PrefabInternal",
        "serializedVersion", "m_LocalEulerAnglesHint", "m_RootOrder", "m_ConstrainProportionsScale",
    };

    static readonly Dictionary<int, string> _unityTypeNames = new()
    {
        [1]   = "GameObject",      [4]   = "Transform",       [20]  = "Camera",
        [23]  = "MeshRenderer",    [25]  = "Renderer",        [33]  = "MeshFilter",
        [54]  = "Rigidbody",       [64]  = "MeshCollider",    [65]  = "BoxCollider",
        [95]  = "Animator",        [108] = "Light",           [114] = "MonoBehaviour",
        [115] = "MonoScript",      [222] = "CanvasRenderer",  [223] = "Canvas",
        [224] = "RectTransform",   [225] = "CanvasGroup",     [226] = "GraphicRaycaster",
    };

    static readonly Dictionary<string, string> _guidToScriptCache = new();

    /// Returns a formatted INSPECTOR result read directly from .prefab/.unity YAML on disk.
    /// Returns null if the asset path isn't recognisable or the file doesn't exist.
    static string TryYamlInspector(string projectPath, string rawData)
    {
        // Strip flags: --brief, --children, --depth N, --filter X
        bool brief = false, showChildren = false;
        int  depth = 0;
        string filter = null;
        var tokens = rawData.Split(' ');
        var pathTokens = new List<string>();
        for (int ti = 0; ti < tokens.Length; ti++)
        {
            switch (tokens[ti])
            {
                case "--brief":    brief = true; break;
                case "--children": showChildren = true; depth = 100; break;
                case "--depth":    if (ti + 1 < tokens.Length) int.TryParse(tokens[++ti], out depth); break;
                case "--filter":   if (ti + 1 < tokens.Length) filter = tokens[++ti]; break;
                default:           pathTokens.Add(tokens[ti]); break;
            }
        }
        string cleanData = string.Join(" ", pathTokens).Trim('"', '\'');

        // Match "Assets/Foo.prefab" or "Assets/Foo.prefab/Root/Child/..."
        var m = System.Text.RegularExpressions.Regex.Match(cleanData,
            @"^(Assets[/\\].+?\.(?:prefab|unity))(?:[/\\](.+))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        string assetPath = m.Groups[1].Value.Replace('\\', '/');
        string nodePath  = m.Groups[2].Success ? m.Groups[2].Value.Replace('\\', '/') : null;
        string filePath  = Path.Combine(projectPath, assetPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath)) return null;

        Dictionary<long, YamlObj> all;
        try { all = ParseUnityYamlFile(filePath); }
        catch { return null; }
        if (all.Count == 0) return null;

        // Index GameObjects and transforms
        var goById      = new Dictionary<long, YamlObj>();
        var xformByGoId = new Dictionary<long, YamlObj>(); // go fileId → its transform
        foreach (var obj in all.Values)
        {
            if (obj.TypeId == 1) goById[obj.FileId] = obj;
            else if ((obj.TypeId == 4 || obj.TypeId == 224) && obj.GameObjectId != 0)
                xformByGoId[obj.GameObjectId] = obj;
        }

        // Find root transform (Father == 0 or Father not in document)
        YamlObj rootXform = null;
        foreach (var obj in all.Values)
        {
            if ((obj.TypeId == 4 || obj.TypeId == 224) && (obj.Father == 0 || !all.ContainsKey(obj.Father)))
            { rootXform = obj; break; }
        }
        if (rootXform == null || !goById.TryGetValue(rootXform.GameObjectId, out var rootGo))
            return null;

        // Navigate path
        YamlObj targetGo;
        if (string.IsNullOrEmpty(nodePath))
        {
            targetGo = rootGo;
        }
        else
        {
            var segs  = nodePath.Split('/');
            var cur   = rootGo;
            int start = (cur.Name == segs[0]) ? 1 : 0;
            for (int si = start; si < segs.Length && cur != null; si++)
            {
                if (!xformByGoId.TryGetValue(cur.FileId, out var xf)) { cur = null; break; }
                YamlObj next = null;
                foreach (var cid in xf.Children)
                {
                    if (!all.TryGetValue(cid, out var cxf)) continue;
                    if (!goById.TryGetValue(cxf.GameObjectId, out var cgo)) continue;
                    if (cgo.Name == segs[si]) { next = cgo; break; }
                }
                cur = next;
            }
            targetGo = cur;
        }

        if (targetGo == null)
            return $"Error: GameObject not found at '{nodePath}' in {assetPath}";

        // Format output (mirrors live INSPECTOR style)
        var sb = new StringBuilder();
        sb.AppendLine($"--- {targetGo.Name} [{assetPath}] [offline YAML]");
        sb.AppendLine($"Active: {(targetGo.IsActive ? "true" : "false")}");
        string fullPath = BuildYamlGoPath(targetGo, goById, xformByGoId, all);
        if (!string.IsNullOrEmpty(fullPath))
            sb.AppendLine($"Path: {fullPath}");
        if (targetGo.Fields.TryGetValue("m_TagString", out var tag))
            sb.Append($"Tag: {tag}");
        if (targetGo.Fields.TryGetValue("m_Layer", out var layer))
            sb.AppendLine($" | Layer: {layer}");
        else
            sb.AppendLine();

        if (!brief)
        {
            sb.AppendLine("Components:");
            foreach (var cid in targetGo.Components)
            {
                if (!all.TryGetValue(cid, out var comp)) continue;
                bool hasEnabled = comp.TypeId != 222 && comp.TypeId != 4 && comp.TypeId != 224 && comp.TypeId != 225;
                string compName = GetYamlComponentName(comp, projectPath);
                sb.AppendLine($"  {compName}{(hasEnabled && !comp.Enabled ? " (disabled)" : "")}");
                foreach (var kv in comp.Fields)
                {
                    if (string.IsNullOrEmpty(kv.Value) || kv.Value == "{fileID: 0}") continue;
                    sb.AppendLine($"    {kv.Key}: {kv.Value}");
                }
            }
        }

        if (showChildren && depth > 0 && xformByGoId.TryGetValue(targetGo.FileId, out var tXform))
        {
            sb.AppendLine("Children:");
            AppendYamlChildTree(sb, tXform, goById, xformByGoId, all, 1, depth, projectPath, filter, brief);
        }

        return sb.ToString().TrimEnd();
    }

    static string BuildYamlGoPath(YamlObj go, Dictionary<long, YamlObj> goById,
        Dictionary<long, YamlObj> xformByGoId, Dictionary<long, YamlObj> all)
    {
        var parts = new List<string>();
        var cur = go;
        for (int safety = 0; safety < 64 && cur != null; safety++)
        {
            parts.Add(cur.Name);
            if (!xformByGoId.TryGetValue(cur.FileId, out var xf) || xf.Father == 0 || !all.TryGetValue(xf.Father, out var pxf)) break;
            goById.TryGetValue(pxf.GameObjectId, out cur);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    static void AppendYamlChildTree(StringBuilder sb, YamlObj xform, Dictionary<long, YamlObj> goById,
        Dictionary<long, YamlObj> xformByGoId, Dictionary<long, YamlObj> all,
        int curDepth, int maxDepth, string projectPath, string filter, bool brief)
    {
        foreach (var cid in xform.Children)
        {
            if (!all.TryGetValue(cid, out var cxf)) continue;
            if (!goById.TryGetValue(cxf.GameObjectId, out var cgo)) continue;
            if (filter != null && !cgo.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            string pad = new string(' ', curDepth * 2);
            sb.AppendLine($"{pad}{cgo.Name}{(cgo.IsActive ? "" : " [inactive]")}");
            if (!brief)
                foreach (var compId in cgo.Components)
                    if (all.TryGetValue(compId, out var comp))
                        sb.AppendLine($"{pad}  [{GetYamlComponentName(comp, projectPath)}]");
            if (curDepth < maxDepth && xformByGoId.TryGetValue(cgo.FileId, out var nextXf))
                AppendYamlChildTree(sb, nextXf, goById, xformByGoId, all, curDepth + 1, maxDepth, projectPath, filter, brief);
        }
    }

    static string GetYamlComponentName(YamlObj comp, string projectPath)
    {
        if (comp.TypeId != 114)
            return _unityTypeNames.TryGetValue(comp.TypeId, out var n) ? n : $"Type{comp.TypeId}";
        if (string.IsNullOrEmpty(comp.ScriptGuid)) return "MonoBehaviour";
        if (_guidToScriptCache.TryGetValue(comp.ScriptGuid, out var cached)) return cached;
        string assetsDir = Path.Combine(projectPath, "Assets");
        try
        {
            foreach (var mf in Directory.EnumerateFiles(assetsDir, "*.cs.meta", SearchOption.AllDirectories))
            {
                foreach (var line in File.ReadLines(mf))
                {
                    if (!line.TrimStart().StartsWith("guid: ")) continue;
                    if (line.Trim().Substring(6) != comp.ScriptGuid) continue;
                    string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(mf));
                    _guidToScriptCache[comp.ScriptGuid] = name;
                    return name;
                }
            }
        }
        catch { }
        string fallback = $"MonoBehaviour({comp.ScriptGuid[..8]})";
        _guidToScriptCache[comp.ScriptGuid] = fallback;
        return fallback;
    }

    static Dictionary<long, YamlObj> ParseUnityYamlFile(string filePath)
    {
        var result = new Dictionary<long, YamlObj>();
        string[] lines = File.ReadAllLines(filePath);
        int i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("--- ")) { i++; continue; }
            var hm = System.Text.RegularExpressions.Regex.Match(lines[i], @"!u!(\d+)\s+&(\d+)");
            if (!hm.Success) { i++; continue; }
            int  typeId = int.Parse(hm.Groups[1].Value);
            long fileId = long.Parse(hm.Groups[2].Value);
            i++;
            string typeName = (i < lines.Length) ? lines[i].Trim().TrimEnd(':') : "";
            if (i < lines.Length) i++;
            var block = new List<string>();
            while (i < lines.Length && !lines[i].StartsWith("--- "))
                block.Add(lines[i++]);
            var obj = new YamlObj { FileId = fileId, TypeId = typeId, TypeName = typeName };
            ParseUnityYamlBlock(obj, block);
            result[fileId] = obj;
        }
        return result;
    }

    static void ParseUnityYamlBlock(YamlObj obj, List<string> lines)
    {
        bool inComp = false, inChild = false;
        int  listIndent = -1, skipDepth = -1;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string t   = raw.TrimStart();
            int    ind = raw.Length - t.Length;

            // Skip nested blocks we don't need (entered when a key has an empty value)
            if (skipDepth >= 0)
            {
                if (ind <= skipDepth && !t.StartsWith("- ")) skipDepth = -1; // exit skip
                else continue;
            }

            // m_Component list
            if (t == "m_Component:") { inComp = true; inChild = false; listIndent = ind; continue; }
            if (inComp)
            {
                if (t.StartsWith("- "))
                {
                    var fm = System.Text.RegularExpressions.Regex.Match(t, @"fileID:\s*(-?\d+)");
                    if (fm.Success && long.TryParse(fm.Groups[1].Value, out long cid) && cid != 0)
                        obj.Components.Add(cid);
                    continue;
                }
                if (ind <= listIndent) inComp = false; // fall through to process as key
            }

            // m_Children list
            if (t == "m_Children:") { inChild = true; inComp = false; listIndent = ind; continue; }
            if (inChild)
            {
                if (t == "[]")       { inChild = false; continue; }
                if (t.StartsWith("- "))
                {
                    var fm = System.Text.RegularExpressions.Regex.Match(t, @"fileID:\s*(-?\d+)");
                    if (fm.Success && long.TryParse(fm.Groups[1].Value, out long cid) && cid != 0)
                        obj.Children.Add(cid);
                    continue;
                }
                if (ind <= listIndent) inChild = false; // fall through to process as key
            }

            if (t.StartsWith("- ")) continue; // stray list item

            // Key: value
            int col = t.IndexOf(':');
            if (col < 0) continue;
            string key = t.Substring(0, col);
            string val = t.Substring(col + 1).Trim();

            if (string.IsNullOrEmpty(val)) { skipDepth = ind; continue; } // nested block start

            switch (key)
            {
                case "m_Name":     obj.Name    = val; break;
                case "m_IsActive": obj.IsActive = val != "0"; break;
                case "m_Enabled":  obj.Enabled  = val != "0"; break;
                case "m_Father":
                {
                    var fm = System.Text.RegularExpressions.Regex.Match(val, @"fileID:\s*(-?\d+)");
                    if (fm.Success) long.TryParse(fm.Groups[1].Value, out obj.Father);
                    break;
                }
                case "m_GameObject":
                {
                    var fm = System.Text.RegularExpressions.Regex.Match(val, @"fileID:\s*(-?\d+)");
                    if (fm.Success) long.TryParse(fm.Groups[1].Value, out obj.GameObjectId);
                    break;
                }
                case "m_Script":
                {
                    var gm = System.Text.RegularExpressions.Regex.Match(val, @"guid:\s*([a-f0-9]+)");
                    if (gm.Success) obj.ScriptGuid = gm.Groups[1].Value;
                    break;
                }
                default:
                    if (!_yamlSkipFields.Contains(key))
                        obj.Fields[key] = val;
                    break;
            }
        }
    }

    /// <summary>
    /// ASSET_SEARCH: server-side Unity Search + CLI-side scene/prefab YAML grep.
    /// Tries server first, then supplements with fast file-based search for names/components
    /// found inside scene hierarchies and prefab internals.
    /// </summary>
    static int HandleAssetSearch(string pipeName, string projectPath, string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            Console.Error.WriteLine("Usage: ASSET_SEARCH <query>");
            return EXIT_USAGE_ERROR;
        }

        // Extract the text portion of the query (strip t:Type filters for scene search)
        string searchTerm = data.Trim();
        string sceneSearchTerm = System.Text.RegularExpressions.Regex.Replace(searchTerm, @"\bt:\w+\b", "").Trim();

        // Try server-side Unity Search first
        bool serverHadResults = false;
        int serverResult = EXIT_TIMEOUT;
        try
        {
            serverResult = SendCommand(pipeName, projectPath, "ASSET_SEARCH", data);
            serverHadResults = serverResult == EXIT_SUCCESS;
        }
        catch { }

        // CLI-side scene/prefab YAML search (always runs if there's a name to search for)
        if (!string.IsNullOrEmpty(sceneSearchTerm))
        {
            var sceneResults = SearchSceneFiles(projectPath, sceneSearchTerm);
            if (sceneResults.results.Count > 0)
            {
                if (serverHadResults)
                    Console.WriteLine(); // separator from server results
                Console.WriteLine($"--- Scene/Prefab internals ({sceneResults.results.Count} hit(s), {sceneResults.fileCount} files, {sceneResults.ms}ms) ---");
                if (sceneResults.scriptGuid != null)
                    Console.WriteLine($"Script GUID: {sceneResults.scriptGuid}");
                foreach (var group in sceneResults.results.GroupBy(r => r.file))
                {
                    Console.WriteLine($"  {group.Key}:");
                    foreach (var r in group)
                        Console.WriteLine($"    L{r.line}: {r.context}");
                }
                return EXIT_SUCCESS;
            }
        }

        return serverResult;
    }

    /// <summary>
    /// Search .unity and .prefab YAML files for GameObjects by name and MonoBehaviours by script GUID.
    /// No Unity connection needed.
    /// </summary>
    static (List<(string file, string context, int line)> results, string scriptGuid, int fileCount, long ms)
        SearchSceneFiles(string projectPath, string searchTerm)
    {
        var results = new List<(string file, string context, int line)>();
        string assetsDir = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsDir))
            return (results, null, 0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Resolve script GUID from .cs.meta files
        string scriptGuid = null;
        try
        {
            var csFiles = Directory.GetFiles(assetsDir, $"{searchTerm}.cs", SearchOption.AllDirectories);
            if (csFiles.Length == 0)
                csFiles = Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileNameWithoutExtension(f)
                        .Equals(searchTerm, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            foreach (var csFile in csFiles)
            {
                string metaFile = csFile + ".meta";
                if (!File.Exists(metaFile)) continue;
                foreach (var metaLine in File.ReadLines(metaFile))
                {
                    if (metaLine.StartsWith("guid: "))
                    {
                        scriptGuid = metaLine.Substring("guid: ".Length).Trim();
                        break;
                    }
                }
                if (scriptGuid != null) break;
            }
        }
        catch { }

        // Search .unity and .prefab files
        var searchFiles = Directory.GetFiles(assetsDir, "*.unity", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(assetsDir, "*.prefab", SearchOption.AllDirectories))
            .ToArray();

        foreach (var filePath in searchFiles)
        {
            try
            {
                string relPath = filePath.Substring(projectPath.Length + 1).Replace('\\', '/');
                int lineNum = 0;
                bool hasGuidHit = false;

                foreach (var line in File.ReadLines(filePath))
                {
                    lineNum++;
                    string trimmed = line.TrimStart();

                    // Name match
                    if (trimmed.StartsWith("m_Name: "))
                    {
                        string name = trimmed.Substring("m_Name: ".Length);
                        if (name.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                            results.Add((relPath, $"GameObject: {name}", lineNum));
                    }

                    // GUID match (component type — one per file)
                    if (scriptGuid != null && !hasGuidHit && trimmed.Contains(scriptGuid))
                    {
                        results.Add((relPath, $"Component: {searchTerm}", lineNum));
                        hasGuidHit = true;
                    }
                }
            }
            catch { }
        }

        sw.Stop();
        return (results, scriptGuid, searchFiles.Length, sw.ElapsedMilliseconds);
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

        // Downscale if larger than max dimension to keep PNGs small (4K → ~1280px)
        const int MAX_DIM = 1280;
        int outW = width, outH = height;
        byte[] outPixels = pixels;
        if (width > MAX_DIM || height > MAX_DIM)
        {
            float scale = Math.Min((float)MAX_DIM / width, (float)MAX_DIM / height);
            outW = Math.Max(1, (int)(width * scale));
            outH = Math.Max(1, (int)(height * scale));
            outPixels = DownscaleBgra(pixels, width, height, outW, outH);
        }

        // Save PNG
        string outputDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
            "clibridge4unity_screenshots");
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, "render_editor.png");
        WritePng(outputPath, outW, outH, outPixels);

        Console.WriteLine($"Captured editor");
        Console.WriteLine($"size: {outW}x{outH}");
        Console.WriteLine($"output: {outputPath}");

        ShowNotification("Screenshot", $"Captured {outW}x{outH}", outputPath);
        return EXIT_SUCCESS;
    }

    /// <summary>Nearest-neighbor downscale of a top-down BGRA buffer.</summary>
    static byte[] DownscaleBgra(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            int srcY = (int)((long)y * srcH / dstH);
            int srcRow = srcY * srcW * 4;
            int dstRow = y * dstW * 4;
            for (int x = 0; x < dstW; x++)
            {
                int srcX = (int)((long)x * srcW / dstW);
                int s = srcRow + srcX * 4;
                int d = dstRow + x * 4;
                dst[d]     = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
        return dst;
    }

    /// <summary>Copy the most recent screenshot output to a user-specified path (--output flag).</summary>
    static void CopyScreenshotOutput(string destPath)
    {
        // Find the most recently written file in the screenshots dir
        string screenshotDir = Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
            "clibridge4unity_screenshots");
        if (!Directory.Exists(screenshotDir)) return;
        var latest = Directory.GetFiles(screenshotDir, "*.png")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null) return;
        try
        {
            string destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(latest.FullName, destPath, overwrite: true);
            Console.WriteLine($"output: {destPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not copy to --output path: {ex.Message}");
        }
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

    /// <summary>
    /// Single-pass fallback when the persistent Roslyn daemon isn't available.
    /// Scans Assets/ on disk, parses only files containing the query, then delegates to
    /// CodeAnalysisCore — same extraction + formatting used by the daemon.
    /// </summary>
    static class RoslynAnalyzer
    {
        public static string Analyze(string projectPath, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
                return "Error: No query. Usage: CODE_ANALYZE ClassName | method:Name | field:Name | inherits:Type | attribute:Name";

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // For prefix queries, filter on the term (after `kind:`), not the whole string.
            string filterTerm = query;
            int colon = filterTerm.IndexOf(':');
            if (colon > 0 && filterTerm.IndexOf(' ') < 0)
                filterTerm = filterTerm.Substring(colon + 1).Trim();
            if (filterTerm.Contains('.'))
                filterTerm = filterTerm.Substring(filterTerm.LastIndexOf('.') + 1);
            int ltAngle = filterTerm.IndexOf('<');
            if (ltAngle > 0) filterTerm = filterTerm.Substring(0, ltAngle);
            while (filterTerm.EndsWith("[]")) filterTerm = filterTerm.Substring(0, filterTerm.Length - 2);

            string assetsDir = Path.Combine(projectPath, "Assets");
            string[] allFiles = Directory.Exists(assetsDir)
                ? Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories).ToArray()
                : Array.Empty<string>();

            var trees = new System.Collections.Concurrent.ConcurrentDictionary<string, Microsoft.CodeAnalysis.SyntaxTree>();
            var texts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            Parallel.ForEach(allFiles, file =>
            {
                try
                {
                    string text = File.ReadAllText(file);
                    if (!text.Contains(filterTerm)) return;
                    trees[file] = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text, path: file);
                    texts[file] = text;
                }
                catch { }
            });

            sw.Stop();
            return CodeAnalysisCore.Analyze(trees, texts, projectPath, query, sw.ElapsedMilliseconds, allFiles.Length);
        }
    }

}
