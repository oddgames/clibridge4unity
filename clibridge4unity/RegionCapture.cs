using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace clibridge4unity;

/// <summary>
/// Drag-select screen region capture, plus an optional resident daemon that arms a global hotkey.
///
/// Why a frozen backdrop rather than a see-through window: a transparent overlay over a LIVE
/// desktop makes the selection race the thing being selected — Unity repaints, a tooltip expires,
/// the console scrolls, and you capture a different frame than the one you aimed at. So the
/// desktop is grabbed ONCE up front, the overlay paints that still, and the crop comes out of the
/// same buffer the user aimed at. It also means the capture is correct even when the source window
/// is mid-repaint or backgrounded.
///
/// The dimmed backdrop is precomputed into a second HBITMAP rather than alpha-blended per paint:
/// one pass over the pixel buffer at startup beats an AlphaBlend on every mouse-move.
/// </summary>
static class RegionCapture
{
    // ─── Win32 ────────────────────────────────────────────────────────

    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;

    const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010;
    const uint WM_KEYDOWN = 0x0100, WM_HOTKEY = 0x0312;
    const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
    const uint WM_RBUTTONDOWN = 0x0204, WM_SETCURSOR = 0x0020, WM_ERASEBKGND = 0x0014;
    const uint WM_RBUTTONUP = 0x0205, WM_TIMER = 0x0113;

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const int VK_ESCAPE = 0x1B;
    const int IDC_CROSS = 32515;
    const uint VK_SNAPSHOT = 0x2C;   // Print Screen

    const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint;
        public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] bmiColors;
    }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize; public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadCursor(IntPtr inst, int name);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern int FrameRect(IntPtr hDC, ref RECT rc, IntPtr brush);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, UIntPtr id);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start,
        uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern int SetDIBits(IntPtr hdc, IntPtr bmp, uint start,
        uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint colorref);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_HIDE = 0;

    // ─── Overlay state (one overlay at a time, message-loop thread only) ───

    static WndProcDelegate _wndProcRef;   // GC root — the window class holds a raw fn pointer to this
    static IntPtr _hbmScreen, _hbmDim, _hdcScreen, _hdcDim;
    static byte[] _pixels;                // top-down BGRA of the frozen desktop
    static int _vx, _vy, _vw, _vh;
    static bool _dragging, _haveRect;
    static int _x0, _y0, _x1, _y1;
    static IntPtr _brushBorder;

    // ─── Public entry ─────────────────────────────────────────────────

    public static string CapturesDir => Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
        "clibridge4unity", "captures");

    public static int Run(string args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Error: CAPTURE is Windows-only.");
            return 1;
        }

        string outPath = null;
        bool daemon = false, full = false;
        var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].ToLowerInvariant();
            if ((p == "--out" || p == "-o") && i + 1 < parts.Length) outPath = parts[++i].Trim('"');
            else if (p == "--daemon" || p == "daemon") daemon = true;
            else if (p == "--full") full = true;
            else if (p == "--no-context") _collectContext = false;
            else if (p == "--stop" || p == "stop") return StopDaemon();
            else if (p == "--status" || p == "status") return DaemonStatus();
            else if (p == "--settings" || p == "settings") return ShowSettings();
            else if (p == "--autostart" || p == "autostart")
            {
                string mode = i + 1 < parts.Length ? parts[i + 1].ToLowerInvariant() : "on";
                return SetAutostart(mode != "off" && mode != "false" && mode != "0");
            }
        }

        Directory.CreateDirectory(CapturesDir);
        if (daemon) return RunDaemon();

        string saved = CaptureOnce(outPath, full);
        if (saved == null)
        {
            Console.Error.WriteLine("Capture cancelled.");
            return 1;
        }

        Console.WriteLine("Captured region");
        Console.WriteLine($"output: {saved}");
        return 0;
    }

    /// <summary>
    /// Freeze the desktop, let the user drag a rectangle, and hand back the result in SCREEN
    /// coordinates — what ffmpeg's gdigrab -offset_x/-offset_y expect. The overlay works in
    /// virtual-desktop-relative coordinates (0,0 = top-left of the bounding box across all
    /// monitors), which differ from screen coordinates whenever a secondary monitor sits above
    /// or left of the primary, so the conversion is not optional.
    /// </summary>
    public static bool TrySelectRegion(out int sx, out int sy, out int sw, out int sh)
    {
        sx = sy = sw = sh = 0;
        if (!GrabDesktop()) return false;
        try
        {
            if (!SelectInternal(out int rx, out int ry, out int rw, out int rh)) return false;
            sx = _vx + rx; sy = _vy + ry; sw = rw; sh = rh;
            return true;
        }
        finally { ReleaseDesktop(); }
    }

    /// <summary>Run the overlay and resolve the dragged rectangle. Assumes the desktop is grabbed.</summary>
    static bool SelectInternal(out int rx, out int ry, out int rw, out int rh)
    {
        rx = ry = rw = rh = 0;
        _haveRect = false; _dragging = false;
        if (!ShowOverlay() || !_haveRect) return false;

        rx = Math.Clamp(Math.Min(_x0, _x1), 0, Math.Max(0, _vw - 1));
        ry = Math.Clamp(Math.Min(_y0, _y1), 0, Math.Max(0, _vh - 1));
        rw = Math.Min(Math.Abs(_x1 - _x0), _vw - rx);
        rh = Math.Min(Math.Abs(_y1 - _y0), _vh - ry);
        return rw >= 4 && rh >= 4;
    }

    /// <summary>Show the overlay and return the saved PNG path, or null if the user cancelled.</summary>
    public static string CaptureOnce(string outPath, bool full = false)
    {
        // Sample the foreground owner FIRST — the overlay is about to become the foreground
        // window, and after that we can no longer tell which editor you were looking at.
        uint fgPid = ForegroundPid();

        if (!GrabDesktop()) return null;
        try
        {
            int rx, ry, rw, rh;
            if (full)
            {
                rx = 0; ry = 0; rw = _vw; rh = _vh;
            }
            else if (!SelectInternal(out rx, out ry, out rw, out rh))
            {
                return null;
            }

            var crop = Crop(_pixels, _vw, _vh, rx, ry, rw, rh);
            string dest = outPath;
            if (string.IsNullOrWhiteSpace(dest))
                dest = Path.Combine(CapturesDir, $"region_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            dest = Path.GetFullPath(dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            Program.WritePng(dest, rw, rh, crop);

            PublishLatest(dest);
            if (_collectContext) TryWriteContext(dest, fgPid);
            return dest;
        }
        finally { ReleaseDesktop(); }
    }

    /// <summary>
    /// Ask Unity what it is showing right now and write it beside the capture as markdown.
    ///
    /// This has to happen AT capture time, not when someone later opens the panel: the selection
    /// moves, play mode exits, the console scrolls. A screenshot whose explanation was gathered
    /// thirty seconds later describes a different editor.
    ///
    /// Everything here is best-effort and silent on failure. Unity may be closed, compiling, or
    /// mid-import, and none of that should turn a successful screenshot into an error — you still
    /// have the PNG. <paramref name="preferredPid"/> is whichever process owned the foreground
    /// window before the overlay appeared, so that with several editors open we describe the one
    /// you were actually looking at.
    /// </summary>
    /// <summary>Public entry for callers with no foreground hint (a finished recording).</summary>
    public static void WriteContextFor(string capturePath)
    {
        if (_collectContext) TryWriteContext(capturePath, 0);
    }

    static void TryWriteContext(string capturePath, uint preferredPid)
    {
        try
        {
            var workspaces = Program.EnumerateUnityWorkspaces();
            if (workspaces == null || workspaces.Count == 0) return;

            var pick = workspaces.Find(w => w.Pid == preferredPid) ?? workspaces[0];
            if (string.IsNullOrEmpty(pick.ProjectPath)) return;

            string markdown = QueryContext(pick.ProjectPath);
            if (string.IsNullOrWhiteSpace(markdown)) return;

            // The CLI self-updates independently of the UPM package, so a new CLI regularly meets
            // an older bridge that has never heard of CONTEXT. Writing that reply out would put
            // "Unknown command" into a file whose whole purpose is being pasted verbatim.
            if (markdown.IndexOf("Unknown command", StringComparison.OrdinalIgnoreCase) >= 0
                || markdown.TrimStart().StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("context: bridge has no CONTEXT command — update the Unity package to match this CLI.");
                return;
            }

            string dest = Path.ChangeExtension(capturePath, ".context.md");
            var sb = new StringBuilder();
            sb.AppendLine($"# Editor state when `{Path.GetFileName(capturePath)}` was captured");
            sb.AppendLine();
            sb.AppendLine($"Image: `{capturePath}`");
            if (workspaces.Count > 1)
                sb.AppendLine($"Project: `{pick.ProjectPath}` (of {workspaces.Count} open editors)");
            else
                sb.AppendLine($"Project: `{pick.ProjectPath}`");
            sb.AppendLine();
            sb.AppendLine(markdown.TrimEnd());
            File.WriteAllText(dest, sb.ToString());
            Console.WriteLine($"context: {dest}");
        }
        catch { /* the screenshot is the deliverable; context is a bonus */ }
    }

    /// <summary>
    /// One CONTEXT round trip on the bridge pipe. Deliberately not Program.SendCommand: that path
    /// prints to the console, retries, and consults the play-mode gate — none of which belongs in
    /// a keystroke handler. Short budgets throughout, because a busy editor must not stall a
    /// screenshot that has already been written to disk.
    /// </summary>
    static string QueryContext(string projectPath)
    {
        try
        {
            string pipeName = Program.GeneratePipeName(projectPath);
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut);

            pipe.Connect(1500);   // Unity not running / bridge not loaded → give up quietly

            byte[] msg = Encoding.UTF8.GetBytes("CONTEXT\n");
            pipe.Write(msg, 0, msg.Length);
            pipe.Flush();

            var sb = new StringBuilder();
            var buf = new byte[8192];
            // CONTEXT needs the main thread, so it queues behind whatever the editor is doing.
            // 8s is generous for an idle editor and short enough not to be noticed when it is not.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var read = pipe.ReadAsync(buf, 0, buf.Length);
                if (!read.Wait(TimeSpan.FromSeconds(8))) break;
                int n = read.Result;
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            // The server leads with a "__timeout:N" budget-hint line before the payload. Every
            // other caller consumes it as protocol; here it would land verbatim at the top of a
            // file whose entire purpose is being pasted.
            string text = sb.ToString();
            if (text.StartsWith("__timeout:", StringComparison.Ordinal))
            {
                int nl = text.IndexOf('\n');
                text = nl >= 0 ? text.Substring(nl + 1) : "";
            }
            return text;
        }
        catch { return null; }
    }

    /// <summary>Process that owned the foreground window, sampled before the overlay steals it.</summary>
    static uint ForegroundPid()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Refresh the well-known pointer files so the Unity panel can pick up an artefact whose path
    /// it never asked for — a hotkey grab, or a recording started from the tray. latest.png only
    /// tracks stills; latest.txt tracks whatever came last, image or video.
    /// </summary>
    public static void PublishLatest(string dest)
    {
        try
        {
            Directory.CreateDirectory(CapturesDir);
            if (dest.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string latest = Path.Combine(CapturesDir, "latest.png");
                if (!string.Equals(latest, dest, StringComparison.OrdinalIgnoreCase))
                    File.Copy(dest, latest, true);
            }
            File.WriteAllText(Path.Combine(CapturesDir, "latest.txt"), dest);
        }
        catch { /* pointer file is a convenience, not a contract */ }
    }

    // ─── Frozen desktop grab ──────────────────────────────────────────

    static bool GrabDesktop()
    {
        _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (_vw <= 0 || _vh <= 0) return false;

        IntPtr screenDC = GetDC(IntPtr.Zero);
        _hdcScreen = CreateCompatibleDC(screenDC);
        _hbmScreen = CreateCompatibleBitmap(screenDC, _vw, _vh);
        SelectObject(_hdcScreen, _hbmScreen);
        BitBlt(_hdcScreen, 0, 0, _vw, _vh, screenDC, _vx, _vy, SRCCOPY | CAPTUREBLT);

        var bmi = NewBmi(_vw, _vh);
        _pixels = new byte[_vw * _vh * 4];
        GetDIBits(_hdcScreen, _hbmScreen, 0, (uint)_vh, _pixels, ref bmi, 0);

        // Precompute the dimmed backdrop once (45% of original luminance).
        var dim = new byte[_pixels.Length];
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            dim[i] = (byte)(_pixels[i] * 45 / 100);
            dim[i + 1] = (byte)(_pixels[i + 1] * 45 / 100);
            dim[i + 2] = (byte)(_pixels[i + 2] * 45 / 100);
            dim[i + 3] = 255;
        }
        _hdcDim = CreateCompatibleDC(screenDC);
        _hbmDim = CreateCompatibleBitmap(screenDC, _vw, _vh);
        SelectObject(_hdcDim, _hbmDim);
        var bmi2 = NewBmi(_vw, _vh);
        SetDIBits(_hdcDim, _hbmDim, 0, (uint)_vh, dim, ref bmi2, 0);

        ReleaseDC(IntPtr.Zero, screenDC);
        return true;
    }

    static void ReleaseDesktop()
    {
        if (_hbmScreen != IntPtr.Zero) { DeleteObject(_hbmScreen); _hbmScreen = IntPtr.Zero; }
        if (_hbmDim != IntPtr.Zero) { DeleteObject(_hbmDim); _hbmDim = IntPtr.Zero; }
        if (_hdcScreen != IntPtr.Zero) { DeleteDC(_hdcScreen); _hdcScreen = IntPtr.Zero; }
        if (_hdcDim != IntPtr.Zero) { DeleteDC(_hdcDim); _hdcDim = IntPtr.Zero; }
        _pixels = null;
    }

    static BITMAPINFO NewBmi(int w, int h)
    {
        var bmi = new BITMAPINFO { bmiColors = new uint[4] };
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;  // negative = top-down, which is what WritePng expects
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        return bmi;
    }

    static byte[] Crop(byte[] src, int srcW, int srcH, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int sy = Math.Clamp(y + row, 0, srcH - 1);
            Buffer.BlockCopy(src, (sy * srcW + x) * 4, dst, row * w * 4, w * 4);
        }
        return dst;
    }

    // ─── Overlay window ───────────────────────────────────────────────

    static bool ShowOverlay()
    {
        const string cls = "CliBridgeRegionOverlay";
        _wndProcRef = OverlayProc;
        IntPtr inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x0003,                 // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = _wndProcRef,
            hInstance = inst,
            hCursor = LoadCursor(IntPtr.Zero, IDC_CROSS),
            lpszClassName = cls,
        };
        RegisterClassEx(ref wc);            // benign failure if this process already registered it

        _brushBorder = CreateSolidBrush(0x00D7A24E);  // COLORREF is BGR — a Unity-ish blue

        IntPtr hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, cls, "Select region",
            WS_POPUP | WS_VISIBLE, 0, 0, _vw, _vh, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: could not create the selection overlay window.");
            DeleteObject(_brushBorder);
            return false;
        }

        SetForegroundWindow(hwnd);
        SetFocus(hwnd);

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        DeleteObject(_brushBorder);
        return true;
    }

    static IntPtr OverlayProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero, IDC_CROSS));
                return (IntPtr)1;

            case WM_ERASEBKGND:
                return (IntPtr)1; // WM_PAINT covers every pixel — erasing first only flickers

            case WM_PAINT:
            {
                IntPtr hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);
                BitBlt(hdc, 0, 0, _vw, _vh, _hdcDim, 0, 0, SRCCOPY);
                if (_dragging || _haveRect)
                {
                    int rx = Math.Min(_x0, _x1), ry = Math.Min(_y0, _y1);
                    int rw = Math.Abs(_x1 - _x0), rh = Math.Abs(_y1 - _y0);
                    if (rw > 0 && rh > 0)
                    {
                        // Undimmed inside the selection, so the crop is judged on real pixels.
                        BitBlt(hdc, rx, ry, rw, rh, _hdcScreen, rx, ry, SRCCOPY);
                        var border = new RECT { Left = rx - 1, Top = ry - 1, Right = rx + rw + 1, Bottom = ry + rh + 1 };
                        FrameRect(hdc, ref border, _brushBorder);
                    }
                }
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }

            case WM_LBUTTONDOWN:
                _dragging = true; _haveRect = false;
                _x0 = _x1 = LoWord(lParam); _y0 = _y1 = HiWord(lParam);
                SetCapture(hWnd);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (_dragging)
                {
                    _x1 = LoWord(lParam); _y1 = HiWord(lParam);
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_dragging)
                {
                    _dragging = false; _haveRect = true;
                    _x1 = LoWord(lParam); _y1 = HiWord(lParam);
                    ReleaseCapture();
                    DestroyWindow(hWnd);
                }
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                _haveRect = false;
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if (wParam.ToInt32() == VK_ESCAPE) { _haveRect = false; DestroyWindow(hWnd); }
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    static int LoWord(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);
    static int HiWord(IntPtr v) => (short)((v.ToInt64() >> 16) & 0xFFFF);

    // ─── Daemon: global hotkey ────────────────────────────────────────

    // Print Screen grabs a region; Ctrl+Print Screen starts/stops a recording.
    const int HOTKEY_CAPTURE = 0xC1B4;
    const int HOTKEY_RECORD = 0xC1B5;

    static string PidFile => Path.Combine(CapturesDir, "capture-daemon.pid");

    static int LivePid()
    {
        try
        {
            if (!File.Exists(PidFile)) return 0;
            if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid)) return 0;
            var p = Process.GetProcessById(pid);
            // PID reuse defense: Windows recycles pids, so confirm it is still one of ours.
            if (p.HasExited || !p.ProcessName.StartsWith("clibridge", StringComparison.OrdinalIgnoreCase))
                return 0;
            return pid;
        }
        catch { return 0; }
    }

    static int DaemonStatus()
    {
        int pid = LivePid();
        if (pid == 0) { Console.WriteLine("Capture daemon: not running."); return 1; }
        Console.WriteLine($"Capture daemon: running (pid {pid}) — PrtScn grabs a region, Ctrl+PrtScn records.");
        Console.WriteLine($"Captures: {CapturesDir}");
        return 0;
    }

    static int StopDaemon()
    {
        int pid = LivePid();
        if (pid == 0)
        {
            Console.WriteLine("No capture daemon running.");
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
            return 0;
        }
        try
        {
            Process.GetProcessById(pid).Kill();
            Console.WriteLine($"Stopped capture daemon (pid {pid}).");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Error stopping daemon: {ex.Message}"); return 1; }
        try { File.Delete(PidFile); } catch { }
        return 0;
    }

    /// <summary>
    /// Add/remove a logon entry so the hotkey survives a reboot. A .cmd in the Startup folder
    /// rather than the HKCU Run key: the registry APIs would mean either a net8.0-windows retarget
    /// or a Microsoft.Win32.Registry package reference, and this exe deliberately carries almost no
    /// dependencies. The `start /min` keeps the console out of the way — a brief flash on logon is
    /// the price, and it beats a VBScript launcher on a deprecation clock.
    /// </summary>
    static int SetAutostart(bool enable)
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrEmpty(startup))
        {
            Console.Error.WriteLine("Error: could not locate the Startup folder.");
            return 1;
        }
        string link = Path.Combine(startup, "clibridge4unity-capture.cmd");

        if (!enable)
        {
            try { if (File.Exists(link)) File.Delete(link); } catch (Exception ex)
            {
                Console.Error.WriteLine($"Error removing autostart: {ex.Message}");
                return 1;
            }
            Console.WriteLine("Capture daemon autostart removed.");
            Console.WriteLine("(The running daemon is untouched — stop it with: clibridge4unity CAPTURE --stop)");
            return 0;
        }

        string exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Console.Error.WriteLine("Error: could not resolve this executable's path.");
            return 1;
        }

        try
        {
            File.WriteAllText(link,
                "@echo off\r\n" +
                "rem Installed by: clibridge4unity CAPTURE --autostart on\r\n" +
                "rem Remove with:  clibridge4unity CAPTURE --autostart off\r\n" +
                $"start \"clibridge capture\" /min \"{exe}\" CAPTURE --daemon\r\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error writing autostart entry: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Capture daemon will start at logon: {link}");
        Console.WriteLine("Start it now with: clibridge4unity CAPTURE --daemon");
        return 0;
    }

    /// <summary>
    /// Launch the daemon as a detached background process.
    ///
    /// UseShellExecute=true is load-bearing, not a style choice: with it false the child inherits
    /// this process's stdio handles, and on Windows a PowerShell pipeline stays open until every
    /// writer to it closes — including the copy inherited by a daemon that runs for days. The
    /// symptom is `clibridge4unity SETUP` appearing to hang forever after it has already finished.
    /// Same reasoning as the Roslyn daemon's spawn.
    /// </summary>
    public static bool StartDetached()
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "CAPTURE --daemon",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (proc == null) return false;

            // The daemon publishes its pid file once the hotkeys are registered; wait briefly so
            // we report what actually happened rather than what we hoped would.
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                if (LivePid() != 0) return true;
                Thread.Sleep(150);
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Report everything that decides the daemon's behaviour, and how to change each piece.
    /// `CAPTURE --settings` exists because the tray menu is unreachable when the daemon isn't
    /// running — which is exactly when you need to know why.
    /// </summary>
    static int ShowSettings()
    {
        int pid = LivePid();
        bool auto = AutostartInstalled;

        Console.WriteLine("clibridge capture — settings");
        Console.WriteLine();
        Console.WriteLine($"  Daemon          {(pid != 0 ? $"running (pid {pid})" : "not running")}");
        Console.WriteLine($"  Start with PC   {(auto ? "yes" : "no")}");
        Console.WriteLine($"  Captures        {CapturesDir}");
        Console.WriteLine($"  Editor state    {(_collectContext ? "collected with each capture (.context.md)" : "off")}");
        Console.WriteLine($"  ffmpeg          {(ScreenRecorder.FindFfmpeg() != null ? "found (RECORD available)" : "not found — RECORD unavailable")}");
        Console.WriteLine();
        Console.WriteLine("  Hotkeys (only while the daemon runs)");
        Console.WriteLine("    Print Screen         grab a screen region");
        Console.WriteLine("    Ctrl+Print Screen    start / stop a recording");
        Console.WriteLine();
        Console.WriteLine("  Change it");
        Console.WriteLine(pid != 0
            ? "    clibridge4unity CAPTURE --stop           stop the daemon now (frees Print Screen)"
            : "    clibridge4unity CAPTURE --daemon         start the daemon now");
        Console.WriteLine(auto
            ? "    clibridge4unity CAPTURE --autostart off  stop it starting with Windows"
            : "    clibridge4unity CAPTURE --autostart on   start it with Windows");
        if (pid != 0)
            Console.WriteLine("    Right-click the tray icon for the same settings, plus recording.");
        return 0;
    }

    /// <summary>
    /// Offer the daemon during SETUP / UPDATE. Prompts only on a real terminal — SETUP is run by
    /// scripts and coding agents far more often than by hand, and a blocked stdin read there would
    /// hang the whole install rather than ask anybody anything.
    /// </summary>
    public static void OfferInstall()
    {
        if (!OperatingSystem.IsWindows()) return;

        bool auto = AutostartInstalled;
        int pid = LivePid();

        if (auto && pid != 0)
        {
            Console.WriteLine($"Screenshot daemon: running (pid {pid}), starts with Windows. `CAPTURE --settings` to change.");
            return;
        }

        // Already opted in, just not running (a self-update kills it — see KillStaleClibridgeProcesses).
        if (auto && pid == 0)
        {
            Console.WriteLine("Screenshot daemon: enabled but not running — restarting it...");
            Console.WriteLine(StartDetached()
                ? "  Started. Print Screen grabs a region, Ctrl+Print Screen records."
                : "  Could not start it. Run: clibridge4unity CAPTURE --daemon");
            return;
        }

        Console.WriteLine("Screenshot daemon (optional)");
        Console.WriteLine("  A small background app that puts an icon in your notification area and");
        Console.WriteLine("  takes over two keys:");
        Console.WriteLine("    Print Screen         drag a box around anything — a broken bit of UI, a");
        Console.WriteLine("                         console error, a wrong material — and it is saved.");
        Console.WriteLine("    Ctrl+Print Screen    record that region with audio; narrate the problem");
        Console.WriteLine("                         as it happens, then press it again to stop.");
        Console.WriteLine("  In Unity, Tools > CLI Bridge for Unity > Capture Context turns whatever you");
        Console.WriteLine("  captured — plus the GameObjects you tick — into a prompt on your clipboard.");
        Console.WriteLine("  It starts with Windows, uses no CPU while idle, and the tray icon's menu");
        Console.WriteLine("  turns it off again.");
        Console.WriteLine();

        if (Console.IsInputRedirected)
        {
            Console.WriteLine("  To install:  clibridge4unity CAPTURE --autostart on");
            Console.WriteLine("               clibridge4unity CAPTURE --daemon");
            return;
        }

        Console.Write("  Install it? [Y/n] ");
        string answer;
        try { answer = Console.ReadLine(); }
        catch { return; }   // no console (service, redirected late) — treat as declined

        answer = (answer ?? "").Trim().ToLowerInvariant();
        if (answer == "n" || answer == "no")
        {
            Console.WriteLine("  Skipped. Enable later with: clibridge4unity CAPTURE --autostart on");
            return;
        }

        SetAutostart(true);
        Console.WriteLine(StartDetached()
            ? "  Started. Print Screen grabs a region, Ctrl+Print Screen records."
            : "  Installed for next logon, but could not start it now. Run: clibridge4unity CAPTURE --daemon");
        Console.WriteLine("  Settings / turn off: clibridge4unity CAPTURE --settings");
    }

    /// <summary>
    /// Hide the console window, but only when this process is the sole owner of it.
    ///
    /// The exe is a console app — every other command needs stdout — so a daemon launched from the
    /// Startup entry gets a console window it never uses, and the user gets a dead black box in
    /// their taskbar for the rest of the session.
    ///
    /// GetConsoleProcessList is what makes this safe. A count of 1 means the console was created
    /// for us alone (Startup entry, Start-Process), so hiding it costs nothing. Anything higher
    /// means we are sharing a terminal with the shell that launched us — hiding *that* would take
    /// the user's own window away and swallow Ctrl+C. Distinct from the "never ShowWindow" rule for
    /// Unity's hidden console: this is our own window, found via GetConsoleWindow, not Unity's.
    /// </summary>
    static void HideOwnConsole()
    {
        try
        {
            IntPtr console = GetConsoleWindow();
            if (console == IntPtr.Zero) return;          // output redirected — no window to hide

            var pids = new uint[4];
            uint count = GetConsoleProcessList(pids, (uint)pids.Length);
            if (count != 1) return;                      // shared with a parent shell — leave it alone

            ShowWindow(console, SW_HIDE);
        }
        catch { /* cosmetic — never let it stop the daemon starting */ }
    }

    static int RunDaemon()
    {
        // Refuse to double-arm: two daemons fighting over one hotkey means RegisterHotKey fails
        // in the loser and the user is left with a shortcut that silently does nothing.
        int existing = LivePid();
        if (existing != 0)
        {
            Console.Error.WriteLine($"Capture daemon already running (pid {existing}). Use CAPTURE --stop first.");
            return 1;
        }

        HideOwnConsole();

        const string cls = "CliBridgeCaptureDaemon";
        _daemonProcRef = DaemonProc;
        IntPtr inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _daemonProcRef,
            hInstance = inst,
            lpszClassName = cls,
        };
        RegisterClassEx(ref wc);

        // A real (never-shown) popup window rather than HWND_MESSAGE: a message-only window
        // cannot be brought to the foreground, and TrackPopupMenu needs that to dismiss properly.
        // WS_POPUP with no WS_VISIBLE and zero size stays invisible and off the taskbar anyway.
        IntPtr hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, cls, "clibridge capture", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Error: could not create the daemon window.");
            return 1;
        }

        // Print Screen is the natural key, but it is also the most contested one on Windows:
        // "Use the Print screen key to open screen capture" (Settings > Ease of Access > Keyboard)
        // claims it at the OS level, and OneDrive/Dropbox/ShareX all offer to take it. Register
        // each chord independently and report exactly which one lost, rather than failing whole —
        // a working record hotkey is still worth having if capture lost the race.
        bool gotCapture = RegisterHotKey(hwnd, HOTKEY_CAPTURE, MOD_NOREPEAT, VK_SNAPSHOT);
        bool gotRecord = RegisterHotKey(hwnd, HOTKEY_RECORD, MOD_CONTROL | MOD_NOREPEAT, VK_SNAPSHOT);

        if (!gotCapture)
        {
            Console.Error.WriteLine("Warning: Print Screen is already claimed — region capture hotkey unavailable.");
            Console.Error.WriteLine("  Turn off Settings > Ease of Access > Keyboard > 'Use the PrtScn button to open screen snipping',");
            Console.Error.WriteLine("  or quit whichever tool has it (OneDrive, Dropbox, ShareX), then restart this daemon.");
        }
        if (!gotRecord)
            Console.Error.WriteLine("Warning: Ctrl+Print Screen is already claimed — record hotkey unavailable.");

        if (!gotCapture && !gotRecord)
        {
            Console.Error.WriteLine("Error: neither hotkey could be registered. The tray menu would still work,");
            Console.Error.WriteLine("but starting a hotkey daemon with no hotkeys is almost certainly not what you wanted.");
            DestroyWindow(hwnd);
            return 1;
        }

        _taskbarCreated = TrayIcon.RegisterWindowMessage("TaskbarCreated");
        if (!TrayIcon.Add(hwnd, "clibridge — PrtScn: region · Ctrl+PrtScn: record"))
            Console.Error.WriteLine("Warning: could not add the tray icon; the hotkeys still work.");

        // 1s poll so the icon reflects a recording started from elsewhere (the panel, another
        // terminal). Recording runs in a detached child process, so there is nothing to subscribe
        // to — and a 1s timer in a do-nothing daemon costs nothing measurable.
        SetTimer(hwnd, (UIntPtr)1, 1000, IntPtr.Zero);

        File.WriteAllText(PidFile, Process.GetCurrentProcess().Id.ToString());
        Console.WriteLine("Capture daemon armed.");
        if (gotCapture) Console.WriteLine("  Print Screen         grab a region");
        if (gotRecord) Console.WriteLine("  Ctrl+Print Screen    start / stop a recording");
        // Windows 10/11 park every newly-registered tray icon in the hidden overflow, and there is
        // no supported API to promote one (deliberately — it stops apps fighting over tray space).
        // Say so once, or the icon looks broken rather than hidden.
        Console.WriteLine("Tray icon added — Windows hides new icons at first, so click the ^ in the");
        Console.WriteLine("notification area and drag it out to keep it visible. Right-click it for the menu.");
        Console.WriteLine($"Captures land in: {CapturesDir}");
        Console.WriteLine("Stop with: clibridge4unity CAPTURE --stop");

        try
        {
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            KillTimer(hwnd, (UIntPtr)1);
            TrayIcon.Remove();
            if (gotCapture) UnregisterHotKey(hwnd, HOTKEY_CAPTURE);
            if (gotRecord) UnregisterHotKey(hwnd, HOTKEY_RECORD);
            DestroyWindow(hwnd);
            try { File.Delete(PidFile); } catch { }
        }
        return 0;
    }

    // ─── Tray menu ────────────────────────────────────────────────────

    const int MENU_CAPTURE = 1, MENU_REC = 2, MENU_REC_TRANSCRIBE = 3, MENU_REC_STOP = 4;
    const int MENU_FOLDER = 5, MENU_AUTOSTART = 6, MENU_EXIT = 7, MENU_CONTEXT = 8;

    static WndProcDelegate _daemonProcRef;  // separate GC root — ShowOverlay reassigns _wndProcRef
    static uint _taskbarCreated;
    static bool _wasRecording;
    static bool _transcribeByDefault = true;   // what Ctrl+PrtScn does; toggled from the menu
    static bool _collectContext = true;        // snapshot editor state alongside each capture

    static bool AutostartInstalled
    {
        get
        {
            try
            {
                string s = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return !string.IsNullOrEmpty(s) && File.Exists(Path.Combine(s, "clibridge4unity-capture.cmd"));
            }
            catch { return false; }
        }
    }

    static void ShowTrayMenu(IntPtr hWnd)
    {
        bool rec = ScreenRecorder.IsRecording;
        var items = new List<TrayIcon.Item>
        {
            new TrayIcon.Item { Id = MENU_CAPTURE, Text = "Capture region\tPrtScn", Disabled = rec },
            TrayIcon.Item.Sep(),
            rec
                ? new TrayIcon.Item { Id = MENU_REC_STOP, Text = "Stop recording\tCtrl+PrtScn" }
                : new TrayIcon.Item { Id = MENU_REC, Text = "Start recording\tCtrl+PrtScn" },
            new TrayIcon.Item
            {
                Id = MENU_REC_TRANSCRIBE,
                Text = "Transcribe narration",
                Checked = _transcribeByDefault,
                Disabled = rec,
            },
            new TrayIcon.Item
            {
                Id = MENU_CONTEXT,
                Text = "Collect editor state with captures",
                Checked = _collectContext,
            },
            TrayIcon.Item.Sep(),
            new TrayIcon.Item { Id = MENU_FOLDER, Text = "Open captures folder" },
            new TrayIcon.Item { Id = MENU_AUTOSTART, Text = "Start at logon", Checked = AutostartInstalled },
            TrayIcon.Item.Sep(),
            new TrayIcon.Item { Id = MENU_EXIT, Text = "Exit" },
        };

        switch (TrayIcon.ShowMenu(hWnd, items))
        {
            case MENU_CAPTURE:
                DoCapture();
                break;

            case MENU_REC:
                ScreenRecorder.StartDetached(_transcribeByDefault ? "--transcribe" : "");
                break;

            case MENU_REC_TRANSCRIBE:
                _transcribeByDefault = !_transcribeByDefault;
                break;

            case MENU_CONTEXT:
                _collectContext = !_collectContext;
                break;

            case MENU_REC_STOP:
                ScreenRecorder.Stop();
                break;

            case MENU_FOLDER:
                try
                {
                    Directory.CreateDirectory(CapturesDir);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CapturesDir}\"") { UseShellExecute = true });
                }
                catch (Exception ex) { Console.Error.WriteLine($"Could not open folder: {ex.Message}"); }
                break;

            case MENU_AUTOSTART:
                SetAutostart(!AutostartInstalled);
                break;

            case MENU_EXIT:
                DestroyWindow(hWnd);
                break;
        }
    }

    /// <summary>
    /// Run the overlay from inside the daemon's own message loop. ShowOverlay reassigns
    /// _wndProcRef for its own window class, which is why the daemon keeps its delegate in a
    /// separate field — sharing one would leave the daemon's class pointing at a collected
    /// delegate as soon as the first capture finished.
    /// </summary>
    static void DoCapture()
    {
        try
        {
            string saved = CaptureOnce(null);
            Console.WriteLine(saved != null ? $"captured: {saved}" : "cancelled");
        }
        catch (Exception ex) { Console.Error.WriteLine($"capture failed: {ex.Message}"); }
    }

    static IntPtr DaemonProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_CAPTURE)
            {
                DoCapture();
                return IntPtr.Zero;
            }
            if (id == HOTKEY_RECORD)
            {
                // One chord toggles, because reaching for a different key to stop a recording you
                // started with Ctrl+PrtScn is exactly the moment you fumble it.
                if (ScreenRecorder.IsRecording) ScreenRecorder.Stop();
                else ScreenRecorder.StartDetached(_transcribeByDefault ? "--transcribe" : "");
                return IntPtr.Zero;
            }
        }

        if (msg == TrayIcon.WM_TRAYICON)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP) ShowTrayMenu(hWnd);
            return IntPtr.Zero;
        }

        if (msg == WM_TIMER)
        {
            bool rec = ScreenRecorder.IsRecording;
            if (rec != _wasRecording)
            {
                _wasRecording = rec;
                TrayIcon.Update(rec, rec
                    ? "clibridge — RECORDING (right-click to stop)"
                    : "clibridge — PrtScn: region · Ctrl+PrtScn: record");
            }
            return IntPtr.Zero;
        }

        // Explorer restarting silently drops every tray icon; this is the only notification.
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            TrayIcon.Restore();
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
