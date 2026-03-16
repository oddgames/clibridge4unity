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
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // Win32 APIs
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
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
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Version from assembly (set in .csproj <Version>)
    private static readonly string CLI_VERSION =
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    // GitHub repository for package installation and updates
    private const string GITHUB_REPO = "oddgames/clibridge4unity";
    private const string UPM_PACKAGE_NAME = "au.com.oddgames.clibridge4unity";

    // Track if last response indicated main thread timeout (for auto-retry with WAKEUP)
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

            if (hwnd == IntPtr.Zero)
            {
                foreach (var proc in Process.GetProcessesByName("Unity"))
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        hwnd = proc.MainWindowHandle;
                        break;
                    }
                }
            }

            if (hwnd != IntPtr.Zero)
            {
                // Strategy: barrage of messages to force Unity's editor loop to tick
                // even when in the background. None of these steal focus.

                // ShowWindow SW_SHOWNA (8) = show window without activating it
                // This can force a redraw cycle which triggers Unity's update loop
                ShowWindow(hwnd, 8);

                // FlashWindow briefly flashes the taskbar icon, which triggers
                // window activity processing without stealing focus
                FlashWindow(hwnd, true);
                FlashWindow(hwnd, false); // un-flash immediately

                for (int i = 0; i < 5; i++)
                {
                    PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                    PostMessage(hwnd, WM_ACTIVATEAPP, (IntPtr)1, IntPtr.Zero);
                    PostMessage(hwnd, WM_TIMER, IntPtr.Zero, IntPtr.Zero);
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                    PostMessage(hwnd, WM_PAINT, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(30);
                }

                // Synchronous send to block until Unity processes at least one message
                SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 2000, out _);
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
    //   - Skipped for fast commands: PING, PROBE, DIAG, WAKEUP, DISMISS, SCREENSHOT

    static string UpdateCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clibridge4unity");
    static string UpdateCacheFile => Path.Combine(UpdateCacheDir, ".last_update_check");
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
                    return 1;
                }
                projectPath = args[++argIndex];
                // Validate it's a Unity project
                if (!Directory.Exists(Path.Combine(projectPath, "Assets")))
                {
                    Console.Error.WriteLine($"Error: Not a Unity project (no Assets folder): {projectPath}");
                    return 1;
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
                    return 1;
                }
                return SendCommand(GeneratePipeName(projectPath), projectPath, "HELP", "");
            }
            else if (arg == "--version")
            {
                Console.WriteLine($"clibridge4unity version {CLI_VERSION}");
                // --version is allowed to block briefly to fetch and show update info
                FetchAndShowUpdate();
                return 0;
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
                    return 1;
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
            return 1;
        }

        // Background update check — only for commands that take long enough for it to matter
        // Skip for fast commands: PING, PROBE, DIAG, WAKEUP, DISMISS, SCREENSHOT
        string cmdUpper = command.ToUpperInvariant();
        if (cmdUpper != "PING" && cmdUpper != "PROBE" && cmdUpper != "DIAG" &&
            cmdUpper != "WAKEUP" && cmdUpper != "DISMISS" && cmdUpper != "SCREENSHOT")
        {
            CheckForUpdateInBackground();
        }

        // WAKEUP is purely CLI-side, doesn't need a project path - wakes all Unity windows
        if (command.Equals("WAKEUP", StringComparison.OrdinalIgnoreCase))
        {
            return HandleWakeup();
        }

        // Auto-detect project path if not specified
        projectPath = projectPath ?? AutoDetectProjectPath();
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: Could not detect Unity project.");
            Console.Error.WriteLine("       Run from a Unity project directory or use -d <path>.");
            return 1;
        }

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

        // Pre-flight: check if Unity is running for this project (instant, no pipe needed)
        var unityInfo = DetectUnityProcess(projectPath);
        if (unityInfo.State == UnityProcessState.NotRunning)
        {
            Console.Error.WriteLine("Error: Unity is not running.");
            Console.Error.WriteLine($"       No Unity process found for project: {Path.GetFileName(Path.GetFullPath(projectPath))}");
            Console.Error.WriteLine("       Open Unity Editor with this project first.");
            return 1;
        }
        if (unityInfo.State == UnityProcessState.DifferentProject)
        {
            Console.Error.WriteLine($"Error: Unity is running but not with this project.");
            Console.Error.WriteLine($"       Expected project: {Path.GetFileName(Path.GetFullPath(projectPath))}");
            foreach (var title in unityInfo.OpenProjects)
                Console.Error.WriteLine($"       Unity has open: {title}");
            if (!string.IsNullOrEmpty(unityInfo.ImportStatus))
                Console.Error.WriteLine($"       Status: {unityInfo.ImportStatus}");
            Console.Error.WriteLine($"       Open this project in Unity or use -d to specify the correct path.");
            return 1;
        }
        if (unityInfo.State == UnityProcessState.Importing)
        {
            Console.Error.WriteLine($"Error: Unity is busy — {unityInfo.ImportStatus}");
            if (unityInfo.RecentErrors.Count > 0)
            {
                Console.Error.WriteLine("       Recent compile errors (from Editor.log):");
                foreach (var err in unityInfo.RecentErrors)
                    Console.Error.WriteLine($"         {err}");
            }
            Console.Error.WriteLine("       Wait for it to finish, then retry the command.");
            return 1;
        }
        // Show recent compile errors even when running (before pipe connect)
        if (unityInfo.RecentErrors.Count > 0)
        {
            Console.Error.WriteLine($"Warning: Recent compile errors detected (from Editor.log):");
            foreach (var err in unityInfo.RecentErrors)
                Console.Error.WriteLine($"  {err}");
            Console.Error.WriteLine();
        }

        // Wake Unity's message pump before any command (ensures responsiveness in background)
        WakeUnityEditor(projectPath);

        // SETUP (or INSTALL): install UPM package + generate CLAUDE.md
        if (command.Equals("SETUP", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("INSTALL", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSetup(pipeName, projectPath, data);
        }

        // DISMISS: close any floating/modal dialogs (handled before modal check)
        if (command.Equals("DISMISS", StringComparison.OrdinalIgnoreCase))
        {
            var dialogs = FindFloatingUnityWindows(projectPath);
            if (dialogs.Count == 0)
            {
                Console.WriteLine("No modal dialogs detected.");
                return 0;
            }
            foreach (var (hwnd, title, rect) in dialogs)
            {
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                Console.WriteLine($"Closing: \"{title}\" (HWND={hwnd}, {w}x{h})");
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            Console.WriteLine($"Sent WM_CLOSE to {dialogs.Count} dialog(s).");
            return 0;
        }

        // Pre-flight: detect modal/floating dialogs that block Unity's main thread
        var floatingWindows = FindFloatingUnityWindows(projectPath);
        if (floatingWindows.Count > 0)
        {
            Console.Error.WriteLine($"Error: Unity has {floatingWindows.Count} modal dialog(s) open — commands cannot execute until closed.");
            Console.Error.WriteLine();
            foreach (var (hwnd, title, rect) in floatingWindows)
            {
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                Console.Error.WriteLine($"  [{floatingWindows.IndexOf((hwnd, title, rect)) + 1}] \"{title}\"");
                Console.Error.WriteLine($"      Size: {w}x{h}, Position: ({rect.Left}, {rect.Top})");

                // Provide context about common dialog types
                if (title.Contains("Package Manager"))
                    Console.Error.WriteLine("      → Package Manager window. May appear after adding/removing packages or asmdefs.");
                else if (title.Contains("Import"))
                    Console.Error.WriteLine("      → Import dialog. Unity wants confirmation before importing assets.");
                else if (title.Contains("Save"))
                    Console.Error.WriteLine("      → Save dialog. Unity is asking to save changes before proceeding.");
                else if (title.Contains("Compil") || title.Contains("Build"))
                    Console.Error.WriteLine("      → Build/compilation dialog. Wait for it to finish or cancel.");
                else if (title.Contains("Error") || title.Contains("Warning"))
                    Console.Error.WriteLine("      → Error/warning dialog. May need user attention before dismissing.");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("Fix: Run 'clibridge4unity DISMISS' to close all dialogs via WM_CLOSE.");
            Console.Error.WriteLine("     If that doesn't work, the dialog may need manual interaction in Unity.");
            return 1;
        }

        // SCREENSHOT / RENDER @editor → CLI-side PrintWindow capture (no Unity connection needed)
        if (command.Equals("SCREENSHOT", StringComparison.OrdinalIgnoreCase) ||
            (command.Equals("UI_RENDER", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrEmpty(data) && data.TrimStart().StartsWith("@", StringComparison.Ordinal)))
        {
            return HandleScreenshot(projectPath);
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

        int result = SendCommand(pipeName, projectPath, command, data);

        // Auto-retry with WAKEUP if main thread timed out (Unity likely backgrounded)
        if (result == 1 && _lastResponseContainedMainThreadTimeout)
        {
            Console.Error.WriteLine("[CLI] Main thread timeout detected. Waking Unity and retrying...");
            HandleWakeup();
            _lastResponseContainedMainThreadTimeout = false;
            result = SendCommand(pipeName, projectPath, command, data);
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
        Console.Error.WriteLine("Setup:");
        Console.Error.WriteLine("  clibridge4unity SETUP                      # Install UPM package + CLAUDE.md");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  clibridge4unity PING                       # Auto-detect project");
        Console.Error.WriteLine("  clibridge4unity CODE_ANALYZE BridgeServer   # Analyze a class");
        Console.Error.WriteLine("  clibridge4unity -d C:\\\\MyUnityProject STATUS # Explicit project path");
        Console.Error.WriteLine();
        Console.Error.WriteLine("The Unity project is auto-detected from the current directory.");
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
                string fileCandidate = data.StartsWith("@") ? data.Substring(1).Trim() : data.Trim();
                bool looksLikeFile = data.StartsWith("@")
                    || (fileCandidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !fileCandidate.Contains("\n")
                        && fileCandidate.Length < 260);

                if (looksLikeFile && File.Exists(Path.GetFullPath(fileCandidate)))
                {
                    data = $"@{Path.GetFullPath(fileCandidate)}";
                }
                else if (data.StartsWith("@"))
                {
                    Console.Error.WriteLine($"Error: File not found: {Path.GetFullPath(fileCandidate)}");
                    return 1;
                }
                else
                {
                    // Inline code — fix shell mangling and write to temp file
                    if (data.Contains("\\\""))
                    {
                        data = data.Replace("$\\\"", "$\"");   // $\" → $"  (interpolated strings)
                        data = data.Replace("\\\"", "\"");     // \" → "    (remaining escaped quotes)
                    }

                    string tempFile = Path.Combine(Path.GetTempPath(), $"clibridge4unity_code_{Guid.NewGuid():N}.cs");
                    File.WriteAllText(tempFile, data);
                    data = $"@{tempFile}";
                }
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(5000);

            // Send command in plain text format: COMMAND|data
            string message = string.IsNullOrEmpty(data) ? command : $"{command}|{data}";
            byte[] msgBytes = Encoding.UTF8.GetBytes(message + "\n");
            pipe.Write(msgBytes, 0, msgBytes.Length);
            pipe.Flush();

            // Read response with timeout (60s)
            using var cts = new CancellationTokenSource(60000);
            var responseBuilder = new StringBuilder();
            byte[] buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var readTask = pipe.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                    readTask.Wait(cts.Token);
                    int bytesRead = readTask.Result;
                    if (bytesRead == 0) break;
                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Console.Write(chunk);
                    responseBuilder.Append(chunk);
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("\nError: Command timed out after 60 seconds");
                return 1;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                Console.Error.WriteLine("\nError: Command timed out after 60 seconds");
                return 1;
            }

            // Check if response indicates we need to wait for reconnection
            string response = responseBuilder.ToString();
            if (ShouldWaitForReconnection(response, out int timeoutSeconds))
            {
                return WaitForCompilationAndReconnect(pipeName, projectPath, timeoutSeconds);
            }

            // Detect main thread timeout for auto-retry
            if (response.Contains("Main thread timed out"))
            {
                _lastResponseContainedMainThreadTimeout = true;
                return 1;
            }

            // Auto-open rendered PNGs in VS Code
            if (command == "UI_RENDER")
            {
                foreach (var line in response.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("output:", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = trimmed.Substring("output:".Length).Trim();
                        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                            OpenInVsCode(path);
                    }
                }
            }

            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Error: Connection timeout. Is Unity running with the CLI Bridge package installed?");
            Console.Error.WriteLine($"       Pipe: {pipeName}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Make sure:");
            Console.Error.WriteLine("  1. Unity Editor is running with your project open");
            Console.Error.WriteLine("  2. CLI Bridge for Unity package is installed in your project");
            Console.Error.WriteLine("  3. Check Unity Console for '[Bridge] Server started' message");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
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
                    return 1;
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
        pipe.Connect(5000);

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

                // Parse status to check if still compiling
                if (IsCompilationComplete(statusResponse, requestedAt))
                {
                    Console.WriteLine($"[CLI] Compilation completed in {currentSeconds} seconds");
                    Console.WriteLine($"[CLI] Total connection attempts: {attemptCount}");
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

                    return 0;
                }
                else
                {
                    Console.WriteLine($"[CLI] Unity is still compiling... ({currentSeconds}s elapsed)");
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
        return 1;
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
                Console.Error.WriteLine($"[CLI] Warning: Last compile ({lastCompileFinished:HH:mm:ss}) is BEFORE our request ({requestedAt:HH:mm:ss}). Unity may not have compiled yet.");
                return false;
            }
        }

        // If isCompiling is false and lastCompileFinished is "never" or missing,
        // Unity didn't need to recompile (no code changes). That's still success.
        return true;
    }

    enum UnityProcessState { Running, NotRunning, DifferentProject, Importing }

    class UnityProcessInfo
    {
        public UnityProcessState State;
        public string ImportStatus;       // e.g. "Importing - Compress 50% (busy for 02:04)..."
        public List<string> OpenProjects = new List<string>();
        public List<string> RecentErrors = new List<string>();  // from Editor.log
    }

    static UnityProcessInfo DetectUnityProcess(string projectPath)
    {
        var info = new UnityProcessInfo { State = UnityProcessState.NotRunning };
        string projectName = Path.GetFileName(Path.GetFullPath(projectPath));
        string lockfilePath = Path.Combine(Path.GetFullPath(projectPath), "Temp", "UnityLockfile");

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
        }
        catch { }

        // Determine state: lockfile is most reliable, window title as fallback
        if (!lockfileHeld && !foundProject)
        {
            info.State = unityPids.Count > 0 ? UnityProcessState.DifferentProject : UnityProcessState.NotRunning;
            return info;
        }

        // Unity has this project open — scan ALL Unity windows for import/progress
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

                if (!string.IsNullOrEmpty(title) &&
                    (title.StartsWith("Import", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Progress", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Compiling", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Loading", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Building", StringComparison.OrdinalIgnoreCase)))
                {
                    info.State = UnityProcessState.Importing;
                    info.ImportStatus = title;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        // Tail Editor.log for recent compile errors (no pipe needed)
        TailEditorLogErrors(info);

        return info;
    }

    /// <summary>
    /// Read the last ~50 lines of Unity's Editor.log and extract compile errors.
    /// Works without any pipe connection — reads the file directly.
    /// </summary>
    static void TailEditorLogErrors(UnityProcessInfo info)
    {
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unity", "Editor", "Editor.log");
            if (!File.Exists(logPath)) return;

            // Read last ~8KB of the log file (shared read, Unity is writing it)
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long tailSize = Math.Min(8192, fs.Length);
            fs.Seek(-tailSize, SeekOrigin.End);
            var buffer = new byte[tailSize];
            int read = fs.Read(buffer, 0, buffer.Length);
            string tail = Encoding.UTF8.GetString(buffer, 0, read);

            // Find compile errors: lines containing "error CS" or "error:"
            foreach (var line in tail.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("error CS") ||
                    (trimmed.Contains("): error") && trimmed.Contains(".cs(")))
                {
                    if (info.RecentErrors.Count < 5)
                        info.RecentErrors.Add(trimmed);
                }
            }
        }
        catch { }
    }

    static List<(IntPtr hwnd, string title, RECT rect)> FindFloatingUnityWindows(string projectPath)
    {
        var floating = new List<(IntPtr, string, RECT)>();
        try
        {
            string projectName = Path.GetFileName(Path.GetFullPath(projectPath));
            IntPtr mainHwnd = IntPtr.Zero;
            uint unityPid = 0;

            foreach (var proc in Process.GetProcessesByName("Unity"))
            {
                if (proc.MainWindowHandle != IntPtr.Zero &&
                    proc.MainWindowTitle.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                {
                    mainHwnd = proc.MainWindowHandle;
                    unityPid = (uint)proc.Id;
                    break;
                }
            }

            if (mainHwnd == IntPtr.Zero) return floating;

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == mainHwnd) return true;
                if (!IsWindowVisible(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != unityPid) return true;

                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();
                if (string.IsNullOrEmpty(title)) return true;

                GetWindowRect(hwnd, out RECT rect);
                floating.Add((hwnd, title, rect));
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return floating;
    }

    /// <summary>
    /// Briefly brings all Unity editor windows to the foreground, waits 1 second,
    /// then returns focus to whichever window was previously active.
    /// Don't use unless you think Unity is stuck (e.g. main-thread commands timing out).
    /// </summary>
    static int HandleInstall(string pipeName, string projectPath, string data)
    {
        // Get HELP output from Unity
        string helpOutput = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(5000);
            byte[] msg = Encoding.UTF8.GetBytes("HELP\n");
            pipe.Write(msg, 0, msg.Length);
            pipe.Flush();

            var sb = new StringBuilder();
            byte[] buf = new byte[4096];
            int bytesRead;
            while ((bytesRead = pipe.Read(buf, 0, buf.Length)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, bytesRead));
            helpOutput = sb.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not get HELP from Unity ({ex.GetType().Name}). Using static docs.");
        }

        // Build CLAUDE.md content
        var md = new StringBuilder();
        md.AppendLine("# Unity Bridge (clibridge4unity) - Tool Reference");
        md.AppendLine();
        md.AppendLine("CLI tool for Unity Editor automation via named pipes. Run from project directory.");
        md.AppendLine();
        md.AppendLine("## Essential Commands");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine("clibridge4unity PING                    # Test connection");
        md.AppendLine("clibridge4unity STATUS                  # Editor state, compile status");
        md.AppendLine("clibridge4unity HELP                    # List all commands");
        md.AppendLine("clibridge4unity HELP COMMAND             # Detailed help for a command");
        md.AppendLine("clibridge4unity LOG errors              # Unity console errors");
        md.AppendLine("clibridge4unity DIAG                    # Heartbeat + diagnostics (always works)");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Execute C# Code (has its own compiler — does NOT need COMPILE)");
        md.AppendLine();
        md.AppendLine("CODE_EXEC and CODE_EXEC_RETURN use a built-in Roslyn compiler on a background");
        md.AppendLine("thread. They do NOT need COMPILE and work even when Unity's main thread is busy");
        md.AppendLine("(dialogs open, importing assets, etc). Use these freely at any time.");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine("# Inline expression (auto-wrapped with return)");
        md.AppendLine("clibridge4unity CODE_EXEC_RETURN 'GameObject.FindObjectsOfType<Camera>().Length'");
        md.AppendLine();
        md.AppendLine("# Inline statements (wrapped in a class for you)");
        md.AppendLine("clibridge4unity CODE_EXEC 'Debug.Log(\"hello\")'");
        md.AppendLine();
        md.AppendLine("# From file — supports bare statements, full classes, or namespaces");
        md.AppendLine("clibridge4unity CODE_EXEC @script.cs");
        md.AppendLine("clibridge4unity CODE_EXEC script.cs                          # @ prefix optional");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("**Script formats** (all work):");
        md.AppendLine("- Bare statements: `var go = new GameObject(\"Test\"); Debug.Log(go.name);`");
        md.AppendLine("- With usings: `using System.IO;` at top (extracted automatically)");
        md.AppendLine("- Full class: must have `public static void Run()` or `static void Main()` entry point");
        md.AppendLine("- Fully qualified types work: `UnityEditor.AssetDatabase.Refresh()`");
        md.AppendLine();
        md.AppendLine("## COMPILE — Only for .cs file changes");
        md.AppendLine();
        md.AppendLine("COMPILE triggers Unity's script compilation pipeline. Only needed after editing");
        md.AppendLine(".cs files in Assets/. It auto-skips if no scripts changed since last compile.");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine("clibridge4unity COMPILE                 # Skips if no .cs files changed");
        md.AppendLine("clibridge4unity COMPILE force            # Force recompile");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Scene & GameObjects");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine("clibridge4unity SCENE                              # Hierarchy");
        md.AppendLine("clibridge4unity CREATE MyObject                    # Create GameObject");
        md.AppendLine("clibridge4unity FIND Player                        # Find by name");
        md.AppendLine("clibridge4unity DELETE TempObject                  # Delete");
        md.AppendLine("clibridge4unity INSPECTOR Player                   # All components + fields");
        md.AppendLine("clibridge4unity COMPONENT_SET Player Transform position \"(1,2,3)\"");
        md.AppendLine("clibridge4unity COMPONENT_ADD Player BoxCollider");
        md.AppendLine("clibridge4unity SAVE                               # Save scene");
        md.AppendLine("clibridge4unity PLAY                               # Enter play mode");
        md.AppendLine("clibridge4unity STOP                               # Exit play mode");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Code Search & Analysis");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine("clibridge4unity CODE_SEARCH class:PlayerController");
        md.AppendLine("clibridge4unity CODE_SEARCH inherits:MonoBehaviour");
        md.AppendLine("clibridge4unity CODE_SEARCH method:Update");
        md.AppendLine("clibridge4unity CODE_ANALYZE PlayerController              # Class overview");
        md.AppendLine("clibridge4unity CODE_ANALYZE PlayerController.TakeDamage   # Member details");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Screenshots & Rendering");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine("clibridge4unity SCREENSHOT              # Capture editor window");
        md.AppendLine("clibridge4unity SCREENSHOT scene         # Specific view");
        md.AppendLine("clibridge4unity UI_RENDER Assets/Prefabs/MyPrefab.prefab   # Render to PNG");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Handling \"Unity is busy\" Errors");
        md.AppendLine();
        md.AppendLine("Commands that need Unity's main thread return instant diagnostics when Unity");
        md.AppendLine("is blocked. The error includes: heartbeat staleness, open dialog windows,");
        md.AppendLine("queued commands, and recommendations. Read the error carefully:");
        md.AppendLine();
        md.AppendLine("- **Dialog windows listed**: Run `clibridge4unity DISMISS` to close them, then retry");
        md.AppendLine("- **No dialogs, Unity busy**: Wait a few seconds and retry (asset import, shader compile)");
        md.AppendLine("- **Heartbeat >10s**: Unity may be frozen — ask user to check");
        md.AppendLine("- **DIAG always works**: `clibridge4unity DIAG` responds even when main thread is blocked");
        md.AppendLine();
        md.AppendLine("## Tips");
        md.AppendLine();
        md.AppendLine("- Paths support fuzzy matching: `Button` finds `Canvas/Panel/Button`");
        md.AppendLine("- Plain args preferred over JSON: `COMPONENT_SET Player Transform position \"(1,2,3)\"`");
        md.AppendLine("- `STACK_MINIMIZE` strips internal frames from stack traces");
        md.AppendLine("- Run `clibridge4unity SETUP` again to regenerate this file");
        md.AppendLine();

        // Append live HELP output if available
        if (!string.IsNullOrEmpty(helpOutput))
        {
            md.AppendLine("## Available Commands (live from Unity)");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(helpOutput.TrimEnd());
            md.AppendLine("```");
        }

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
        return 0;
    }

    static int HandleWakeup()
    {
        IntPtr previousWindow = GetForegroundWindow();
        var titleBuf = new StringBuilder(256);
        GetWindowText(previousWindow, titleBuf, 256);
        string previousTitle = titleBuf.ToString();

        // Find all Unity editor windows
        var unityWindows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();
            if (title.Contains("Unity") && title.Contains(" - "))
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
            Console.Error.WriteLine("Error: No Unity editor windows found");
            return 1;
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

        Console.Error.WriteLine($"Woke {unityWindows.Count} Unity window(s), waiting 1s...");
        Thread.Sleep(1000);

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
        return 0;
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
            return 1;
        }

        // Get window dimensions
        GetWindowRect(hwnd, out RECT rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            Console.Error.WriteLine($"Error: Invalid window rect: {width}x{height}");
            return 1;
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

        OpenInVsCode(outputPath);
        return 0;
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
            pipe.Connect(3000);
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

        // Step 3: Generate CLAUDE.md (reuse existing INSTALL logic)
        Console.WriteLine();
        return HandleInstall(pipeName, projectPath, data);
    }

    static int EnsureUpmPackage(string projectPath)
    {
        string manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: {manifestPath} not found. Is this a Unity project?");
            return 1;
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
                return 0;
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
            return 0;
        }

        // Package not present - add it
        Console.WriteLine($"[..] Adding UPM package v{CLI_VERSION}...");
        string gitUrlNew = GetPackageGitUrl();

        // Insert after "dependencies": {
        int depsIdx = manifest.IndexOf("\"dependencies\"");
        if (depsIdx < 0)
        {
            Console.Error.WriteLine("Error: Could not find \"dependencies\" in manifest.json");
            return 1;
        }
        int braceIdx = manifest.IndexOf('{', depsIdx);
        if (braceIdx < 0)
        {
            Console.Error.WriteLine("Error: Malformed manifest.json");
            return 1;
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
        return 0;
    }

    static string GetPackageGitUrl()
    {
        return $"https://github.com/{GITHUB_REPO}.git?path=Package#v{CLI_VERSION}";
    }

}
