using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace clibridge4unity;

/// <summary>
/// A notification-area icon with a context menu, built on Shell_NotifyIcon directly.
///
/// No WinForms: this exe is PublishTrimmed + PublishSingleFile, and pulling in System.Windows.Forms
/// for one 16px icon costs more than the whole rest of the CLI. The icon itself is drawn into a DIB
/// at run time rather than embedded as a .ico resource — two states (idle / recording) fall out of
/// the same few lines, and a tray icon that changes colour while recording is the difference
/// between a daemon you trust and one you forget is running.
/// </summary>
static class TrayIcon
{
    public const uint WM_TRAYICON = 0x0400 + 1;   // WM_APP + 1

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04;

    const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MF_CHECKED = 0x0008;
    const uint MF_GRAYED = 0x0001;
    const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;

    const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string item);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    // ─── State ────────────────────────────────────────────────────────

    static NOTIFYICONDATA _data;
    static IntPtr _iconIdle, _iconRec;
    static bool _added;

    public sealed class Item
    {
        public int Id;
        public string Text;
        public bool Checked;
        public bool Disabled;
        public bool Separator;
        public static Item Sep() => new Item { Separator = true };
    }

    // ─── Icon ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a 32x32 icon: a dark disc with a coloured aperture. Alpha is premultiplied, which
    /// CreateIconIndirect requires for a 32bpp colour bitmap — skip that and the icon renders with
    /// a black fringe on light taskbars.
    /// </summary>
    static IntPtr MakeIcon(bool recording)
    {
        const int S = 32;
        var px = new byte[S * S * 4];

        // BGRA. Outer disc is Unity's charcoal; the aperture is blue at rest, red while recording.
        byte[] ring = { 0x38, 0x30, 0x2C, 0xFF };
        byte[] core = recording
            ? new byte[] { 0x3C, 0x4C, 0xE7, 0xFF }   // #E74C3C red
            : new byte[] { 0xD7, 0xA2, 0x4E, 0xFF };  // #4EA2D7 blue

        const float cx = 15.5f, cy = 15.5f;
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                int i = (y * S + x) * 4;

                byte[] c = null;
                float alpha = 0f;
                if (d <= 8.5f) { c = core; alpha = Clamp01(9.0f - d); }
                else if (d <= 15.0f) { c = ring; alpha = Clamp01(15.5f - d); }

                if (c == null) continue;
                // Premultiply so the taskbar composites it correctly over any background.
                px[i] = (byte)(c[0] * alpha);
                px[i + 1] = (byte)(c[1] * alpha);
                px[i + 2] = (byte)(c[2] * alpha);
                px[i + 3] = (byte)(255 * alpha);
            }
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = S,
            biHeight = -S,   // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(px, 0, bits, px.Length);

        // A 1bpp mask is still required even for an alpha icon; all-zero means "use the alpha".
        IntPtr mask = CreateBitmap(S, S, 1, 1, IntPtr.Zero);

        var ii = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        IntPtr icon = CreateIconIndirect(ref ii);

        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    // ─── Lifecycle ────────────────────────────────────────────────────

    public static bool Add(IntPtr hwnd, string tip)
    {
        _iconIdle = MakeIcon(false);
        _iconRec = MakeIcon(true);

        // A null HICON is accepted by Shell_NotifyIcon and produces an invisible entry — which is
        // indistinguishable from "Windows filed it under hidden icons". Fail loudly instead, or
        // that is an afternoon of looking in the wrong place.
        if (_iconIdle == IntPtr.Zero || _iconRec == IntPtr.Zero)
        {
            Console.Error.WriteLine("Warning: could not build the tray icon bitmaps "
                + $"(idle={_iconIdle != IntPtr.Zero}, recording={_iconRec != IntPtr.Zero}).");
            Remove();
            return false;
        }

        _data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _iconIdle,
            szTip = Trim(tip, 127),
            szInfo = "",
            szInfoTitle = "",
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
        return _added;
    }

    /// <summary>Re-add after an Explorer restart, which silently drops every tray icon.</summary>
    public static void Restore()
    {
        if (!_added) return;
        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public static void Update(bool recording, string tip)
    {
        if (!_added) return;
        _data.hIcon = recording ? _iconRec : _iconIdle;
        _data.szTip = Trim(tip, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    public static void Remove()
    {
        if (_added) { Shell_NotifyIcon(NIM_DELETE, ref _data); _added = false; }
        if (_iconIdle != IntPtr.Zero) { DestroyIcon(_iconIdle); _iconIdle = IntPtr.Zero; }
        if (_iconRec != IntPtr.Zero) { DestroyIcon(_iconRec); _iconRec = IntPtr.Zero; }
    }

    static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

    // ─── Menu ─────────────────────────────────────────────────────────

    /// <summary>Show the context menu at the cursor and return the chosen item id (0 = dismissed).</summary>
    public static int ShowMenu(IntPtr hwnd, IEnumerable<Item> items)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return 0;
        try
        {
            foreach (var it in items)
            {
                if (it.Separator) { AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null); continue; }
                uint flags = MF_STRING;
                if (it.Checked) flags |= MF_CHECKED;
                if (it.Disabled) flags |= MF_GRAYED;
                AppendMenu(menu, flags, (UIntPtr)(uint)it.Id, it.Text);
            }

            GetCursorPos(out POINT p);
            // The SetForegroundWindow / trailing PostMessage pair is the documented workaround for
            // a tray menu that otherwise refuses to dismiss when you click elsewhere.
            SetForegroundWindow(hwnd);
            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally { DestroyMenu(menu); }
    }
}
