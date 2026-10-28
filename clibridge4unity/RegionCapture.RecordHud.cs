using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace clibridge4unity;

/// <summary>
/// On-screen furniture for a RECORD in progress: a red frame just OUTSIDE the recorded rectangle,
/// and a small bar with the elapsed time, Stop and Discard. Once ffmpeg finishes the frame goes and
/// the bar reports what reached the clipboard, with a button to swap it for the video file itself.
///
/// Without it a recording was invisible — no sign of what was being captured, and stopping meant
/// remembering the chord or finding the tray icon in the overflow.
///
/// Kept out of the video two ways. Placement: gdigrab grabs exactly the region, so a frame drawn
/// around it and a bar beside it are never in frame. Display affinity: WDA_EXCLUDEFROMCAPTURE
/// (Windows 10 2004+) hides both from every screen-capture path, which covers --full and a region
/// with no room left for the bar outside it. On older builds the call fails and placement alone
/// has to do.
///
/// Runs on its own thread with its own message loop: the recorder's main thread is a polling loop
/// over ffmpeg, and a window it owned would never repaint. Every window is WS_EX_NOACTIVATE —
/// clicking Stop must not pull focus off the game being recorded.
/// </summary>
static partial class RegionCapture
{
    const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_ALPHA = 0x2, WDA_EXCLUDEFROMCAPTURE = 0x11;
    const uint WM_MOUSEACTIVATE = 0x0021, WM_HUD_UPDATE = 0x8000 + 2;   // WM_APP + 2
    const int MA_NOACTIVATE = 3, RGN_AND = 1, RGN_DIFF = 4;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rc, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint flags);

    const int HUD_STOP = 1, HUD_DISCARD = 2, HUD_COPY_VIDEO = 3, HUD_FOLDER = 4, HUD_CLOSE = 5;

    static WndProcDelegate _hudFrameProc, _hudBarProc;   // GC roots for the two window classes
    static IntPtr _hudFrame, _hudBar;
    static volatile bool _hudStop, _hudDiscard;
    static readonly object _hudLock = new();
    static string _hudLine;               // null while recording; the status line afterwards
    static string _hudVideo;              // set once there is a file to offer
    static bool _hudVideoCopied;
    static DateTime _hudStarted, _hudCloseAt = DateTime.MaxValue;
    static int _hudLimit;
    static int _hudAnchorRight, _hudAnchorLeft, _hudTop;
    static readonly List<(RECT rc, int id, string label, uint fill)> _hudButtons = new();
    static ManualResetEventSlim _hudClosed;

    public static bool HudStopRequested => _hudStop;
    public static bool HudDiscardRequested => _hudDiscard;

    /// <summary>Show the frame and bar around a region in screen coordinates. No-op off Windows or if the windows cannot be made.</summary>
    public static void HudStart(int sx, int sy, int sw, int sh, bool full, int limitSeconds)
    {
        _hudStarted = DateTime.UtcNow;
        _hudLimit = limitSeconds;
        _hudClosed = new ManualResetEventSlim(false);
        var ready = new ManualResetEventSlim(false);
        var t = new Thread(() => HudThread(sx, sy, sw, sh, full, ready)) { IsBackground = true, Name = "record-hud" };
        t.Start();
        ready.Wait(2000);
    }

    /// <summary>Switch to the after-recording state: frame gone, bar shows <paramref name="line"/>.</summary>
    public static void HudStatus(string line, string video = null)
    {
        if (_hudBar == IntPtr.Zero) return;
        lock (_hudLock)
        {
            if (!_hudVideoCopied) _hudLine = line;
            if (video != null) _hudVideo = video;
        }
        PostMessage(_hudBar, WM_HUD_UPDATE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Keep the bar up for <paramref name="seconds"/> (or until closed) so its buttons stay usable, then close it.</summary>
    public static void HudLinger(int seconds)
    {
        if (_hudBar == IntPtr.Zero) return;
        lock (_hudLock) _hudCloseAt = DateTime.UtcNow.AddSeconds(seconds);
        _hudClosed?.Wait(TimeSpan.FromSeconds(seconds + 2));
        HudClose();
    }

    public static void HudClose()
    {
        IntPtr bar = _hudBar;
        if (bar == IntPtr.Zero) return;
        PostMessage(bar, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _hudClosed?.Wait(1000);
    }

    static void HudThread(int sx, int sy, int sw, int sh, bool full, ManualResetEventSlim ready)
    {
        try
        {
            InitOverlayScale();
            IntPtr inst = GetModuleHandle(null);

            _hudFrameProc = (h, m, w, l) => DefWindowProc(h, m, w, l);
            var fc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _hudFrameProc,
                hInstance = inst,
                hbrBackground = CreateSolidBrush(0x003C4CE7),
                lpszClassName = "CliBridgeRecFrame",
            };
            RegisterClassEx(ref fc);

            _hudBarProc = HudBarProc;
            var bc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _hudBarProc,
                hInstance = inst,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                lpszClassName = "CliBridgeRecBar",
            };
            RegisterClassEx(ref bc);

            // --full never grabbed the desktop, so the virtual-screen fields are unset here.
            var region = full
                ? new RECT
                {
                    Left = GetSystemMetrics(SM_XVIRTUALSCREEN), Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
                    Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
                    Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN),
                }
                : new RECT { Left = sx, Top = sy, Right = sx + sw, Bottom = sy + sh };
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromRect(ref region, 2 /* NEAREST */), ref mi);
            var work = mi.rcWork;

            int t = Math.Max(2, Px(3));
            if (!full)
            {
                // One window with a hole cut out of it: layered + transparent so clicks fall through
                // the frame too, and the hole is exactly the recorded pixels, so the border sits
                // just outside what gdigrab sees.
                _hudFrame = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                    "CliBridgeRecFrame", "Recording region", WS_POPUP,
                    sx - t, sy - t, sw + 2 * t, sh + 2 * t, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (_hudFrame != IntPtr.Zero)
                {
                    SetLayeredWindowAttributes(_hudFrame, 0, 255, LWA_ALPHA);
                    IntPtr outer = CreateRectRgn(0, 0, sw + 2 * t, sh + 2 * t);
                    IntPtr inner = CreateRectRgn(t, t, t + sw, t + sh);
                    CombineRgn(outer, outer, inner, RGN_DIFF);
                    DeleteObject(inner);
                    // A region against the screen edge would put that side of the frame on the
                    // neighbouring monitor, or off every monitor. Clip to the region's own screen:
                    // the edge simply has no frame, and the screen border marks it instead.
                    var m = mi.rcMonitor;
                    IntPtr screen = CreateRectRgn(m.Left - (sx - t), m.Top - (sy - t), m.Right - (sx - t), m.Bottom - (sy - t));
                    CombineRgn(outer, outer, screen, RGN_AND);
                    DeleteObject(screen);
                    SetWindowRgn(_hudFrame, outer, true);   // the window owns the region from here
                    SetWindowDisplayAffinity(_hudFrame, WDA_EXCLUDEFROMCAPTURE);
                    SetWindowPos(_hudFrame, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }

            // Below the region, right-aligned; above it if the region reaches the bottom of the
            // monitor; inside its bottom edge (hidden from the video by display affinity) if neither fits.
            int barH = Px(34), gap = Px(6) + t;
            _hudAnchorRight = Math.Min(region.Right, work.Right);
            _hudAnchorLeft = work.Left;
            _hudTop = region.Bottom + gap;
            if (full || _hudTop + barH > work.Bottom) _hudTop = region.Top - gap - barH;
            if (full || _hudTop < work.Top) _hudTop = Math.Min(region.Bottom, work.Bottom) - barH - Px(12);

            _hudBar = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "CliBridgeRecBar", "Recording", WS_POPUP,
                _hudAnchorRight - Px(200), _hudTop, Px(200), barH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hudBar == IntPtr.Zero) return;
            SetWindowDisplayAffinity(_hudBar, WDA_EXCLUDEFROMCAPTURE);
            HudRelayout();
            SetTimer(_hudBar, (UIntPtr)1, 500, IntPtr.Zero);
        }
        finally { ready.Set(); }

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        _hudBar = IntPtr.Zero;
        _hudFrame = IntPtr.Zero;
        _hudClosed.Set();
    }

    /// <summary>Rebuild the button row for the current state and resize the bar to fit it.</summary>
    static void HudRelayout()
    {
        string line;
        string video;
        lock (_hudLock) { line = _hudLine; video = _hudVideo; }

        var buttons = new List<(int id, string label, uint fill)>();
        if (line == null)
        {
            buttons.Add((HUD_STOP, "Stop", 0x003C4CE7));
            buttons.Add((HUD_DISCARD, "Discard", 0x00504840));
        }
        else
        {
            if (video != null && !_hudVideoCopied) buttons.Add((HUD_COPY_VIDEO, "Copy video file", 0x00D7A24E));
            if (video != null) buttons.Add((HUD_FOLDER, "Show", 0x00504840));
            buttons.Add((HUD_CLOSE, "×", 0x00504840));
        }

        IntPtr hdc = GetDC(_hudBar);
        var of = SelectObject(hdc, UiFont());
        GetTextExtentPoint32(hdc, HudLabel(), HudLabel().Length, out SIZE text);
        int h = Px(34), pad = Px(12), gap = Px(5), bh = h - gap * 2;
        int x = Px(30) + Math.Max(text.cx, line == null ? Px(70) : 0) + pad;
        _hudButtons.Clear();
        foreach (var (id, label, fill) in buttons)
        {
            GetTextExtentPoint32(hdc, label, label.Length, out SIZE sz);
            int w = sz.cx + pad * 2;
            _hudButtons.Add((new RECT { Left = x, Top = gap, Right = x + w, Bottom = gap + bh }, id, label, fill));
            x += w + gap;
        }
        SelectObject(hdc, of);
        ReleaseDC(_hudBar, hdc);

        int width = x + Px(2);
        int left = Math.Max(_hudAnchorLeft, _hudAnchorRight - width);
        SetWindowPos(_hudBar, HWND_TOPMOST, left, _hudTop, width, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(_hudBar, IntPtr.Zero, false);
    }

    static string HudLabel()
    {
        string line;
        lock (_hudLock) line = _hudLine;
        if (line != null) return line;
        var e = DateTime.UtcNow - _hudStarted;
        string elapsed = $"{(int)e.TotalMinutes}:{e.Seconds:00}";
        return _hudLimit > 0 ? $"REC {elapsed} / {_hudLimit / 60}:{_hudLimit % 60:00}" : $"REC {elapsed}";
    }

    static IntPtr HudBarProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;

            case WM_ERASEBKGND:
                return (IntPtr)1;

            case WM_PAINT:
            {
                IntPtr hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);
                GetWindowRect(hWnd, out RECT wr);
                int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
                IntPtr mem = CreateCompatibleDC(hdc);
                IntPtr bmp = CreateCompatibleBitmap(hdc, w, h);
                var old = SelectObject(mem, bmp);
                PaintHudBar(mem, w, h);
                BitBlt(hdc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
                SelectObject(mem, old);
                DeleteObject(bmp);
                DeleteDC(mem);
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }

            case WM_LBUTTONDOWN:
            {
                int x = LoWord(lParam), y = HiWord(lParam);
                foreach (var b in _hudButtons)
                    if (Contains(b.rc, x, y)) { OnHudButton(hWnd, b.id); break; }
                return IntPtr.Zero;
            }

            case WM_TIMER:
            {
                bool close;
                lock (_hudLock) close = DateTime.UtcNow >= _hudCloseAt;
                if (close) { DestroyHud(hWnd); return IntPtr.Zero; }
                InvalidateRect(hWnd, IntPtr.Zero, false);   // elapsed time + blinking dot
                return IntPtr.Zero;
            }

            case WM_HUD_UPDATE:
                if (_hudFrame != IntPtr.Zero) { DestroyWindow(_hudFrame); _hudFrame = IntPtr.Zero; }
                HudRelayout();
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyHud(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                KillTimer(hWnd, (UIntPtr)1);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    static void DestroyHud(IntPtr bar)
    {
        if (_hudFrame != IntPtr.Zero) { DestroyWindow(_hudFrame); _hudFrame = IntPtr.Zero; }
        DestroyWindow(bar);
    }

    static void PaintHudBar(IntPtr hdc, int w, int h)
    {
        FillSolid(hdc, new RECT { Left = 0, Top = 0, Right = w, Bottom = h }, 0x00302B28);
        IntPtr edge = CreateSolidBrush(0x00504840);
        var all = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        FrameRect(hdc, ref all, edge);
        DeleteObject(edge);

        bool recording;
        lock (_hudLock) recording = _hudLine == null;

        // Blinking dot while recording, a steady green one afterwards.
        int d = Px(10), dx = Px(12), dy = (h - d) / 2;
        bool on = !recording || (DateTime.UtcNow - _hudStarted).TotalMilliseconds % 1000 < 600;
        if (on)
        {
            IntPtr dot = CreateSolidBrush(recording ? 0x003C4CE7u : 0x0071CC2Eu);
            var ob = SelectObject(hdc, dot);
            var op = SelectObject(hdc, GetStockObject(NULL_PEN));
            Ellipse(hdc, dx, dy, dx + d + 1, dy + d + 1);
            SelectObject(hdc, op);
            SelectObject(hdc, ob);
            DeleteObject(dot);
        }

        var of = SelectObject(hdc, UiFont());
        SetBkMode(hdc, BK_TRANSPARENT);
        SetTextColor(hdc, 0x00FFFFFF);
        string label = HudLabel();
        GetTextExtentPoint32(hdc, label, label.Length, out SIZE sz);
        TextOut(hdc, Px(30), (h - sz.cy) / 2, label, label.Length);

        foreach (var (rc, _, text, fill) in _hudButtons)
        {
            FillSolid(hdc, rc, fill);
            GetTextExtentPoint32(hdc, text, text.Length, out SIZE ts);
            TextOut(hdc, rc.Left + (rc.Right - rc.Left - ts.cx) / 2, rc.Top + (rc.Bottom - rc.Top - ts.cy) / 2, text, text.Length);
        }
        SelectObject(hdc, of);
    }

    static void OnHudButton(IntPtr hWnd, int id)
    {
        string video;
        lock (_hudLock) video = _hudVideo;
        switch (id)
        {
            case HUD_STOP:
                _hudStop = true;
                HudStatus("Saving…");
                break;

            case HUD_DISCARD:
                _hudDiscard = true;
                HudStatus("Discarding…");
                break;

            case HUD_COPY_VIDEO:
                if (video == null) break;
                bool ok = TrySetClipboardFiles(video);
                lock (_hudLock)
                {
                    _hudLine = ok ? "Video file copied — paste it into chat or Explorer" : "Could not copy: " + _lastClipError;
                    _hudVideoCopied = ok;
                    // Give the confirmation time to be read, but never cut the linger short.
                    var min = DateTime.UtcNow.AddSeconds(4);
                    if (_hudCloseAt < min) _hudCloseAt = min;
                }
                HudRelayout();
                break;

            case HUD_FOLDER:
                if (video == null) break;
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{video}\"") { UseShellExecute = true }); }
                catch { }
                break;

            case HUD_CLOSE:
                DestroyHud(hWnd);
                break;
        }
    }
}
