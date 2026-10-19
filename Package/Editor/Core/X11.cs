using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace clibridge4unity
{
    /// <summary>
    /// Minimal Xlib access for Linux editors: list a process's top-level windows, grab pixels, focus,
    /// close and type Ctrl+R. Each call opens the display the editor itself runs on (read from its
    /// /proc environ), so it works from an SSH session that has no DISPLAY of its own.
    /// </summary>
    public static class X11
    {
        public sealed class Window
        {
            public ulong Id;
            public uint Pid;
            public string Display;
            public string Title;
            public string WmClass;
            public int X, Y, Width, Height;
            /// <summary>Typed _NET_WM_WINDOW_TYPE_DIALOG: a native modal. Editor panels and utility windows are NORMAL.</summary>
            public bool Dialog;
        }

        const string LibX11 = "libX11.so.6";
        const string LibXtst = "libXtst.so.6";

        [DllImport(LibX11)] static extern IntPtr XOpenDisplay(string name);
        [DllImport(LibX11)] static extern int XCloseDisplay(IntPtr display);
        [DllImport(LibX11)] static extern ulong XDefaultRootWindow(IntPtr display);
        [DllImport(LibX11)] static extern int XQueryTree(IntPtr display, ulong w, out ulong root, out ulong parent, out IntPtr children, out uint count);
        [DllImport(LibX11)] static extern ulong XInternAtom(IntPtr display, string name, bool onlyIfExists);
        [DllImport(LibX11)] static extern int XGetWindowProperty(IntPtr display, ulong w, ulong property, long offset, long length, bool delete,
            ulong reqType, out ulong actualType, out int actualFormat, out ulong nitems, out ulong bytesAfter, out IntPtr data);
        [DllImport(LibX11)] static extern int XFree(IntPtr data);
        [DllImport(LibX11)] static extern int XGetWindowAttributes(IntPtr display, ulong w, out XWindowAttributes attributes);
        [DllImport(LibX11)] static extern bool XTranslateCoordinates(IntPtr display, ulong src, ulong dst, int x, int y, out int dx, out int dy, out ulong child);
        [DllImport(LibX11)] static extern IntPtr XGetImage(IntPtr display, ulong drawable, int x, int y, uint width, uint height, ulong planeMask, int format);
        [DllImport(LibX11)] static extern int XDestroyImage(IntPtr image);
        [DllImport(LibX11)] static extern int XRaiseWindow(IntPtr display, ulong w);
        [DllImport(LibX11)] static extern int XMoveWindow(IntPtr display, ulong w, int x, int y);
        [DllImport(LibX11)] static extern int XSetInputFocus(IntPtr display, ulong w, int revertTo, ulong time);
        [DllImport(LibX11)] static extern int XSendEvent(IntPtr display, ulong w, bool propagate, long eventMask, ref XClientMessageEvent ev);
        [DllImport(LibX11)] static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
        [DllImport(LibX11)] static extern int XSync(IntPtr display, bool discard);
        [DllImport(LibXtst)] static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, ulong delay);

#if !UNITY_EDITOR
        [DllImport(LibX11)] static extern IntPtr XSetErrorHandler(XErrorHandler handler);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

        // Xlib's default handler exits the process on BadWindow, which any window closing mid-walk raises.
        // Never inside the editor: the handler is process-wide, and GDK's (installed there) already
        // ignores errors on connections it did not open.
        static readonly XErrorHandler IgnoreErrors = (_, _) => 0;
        static int _handlerInstalled;
#endif

        [StructLayout(LayoutKind.Sequential)]
        struct XWindowAttributes
        {
            public int x, y, width, height, border_width, depth;
            public IntPtr visual;
            public ulong root;
            public int c_class, bit_gravity, win_gravity, backing_store;
            public ulong backing_planes, backing_pixel;
            public int save_under;
            public ulong colormap;
            public int map_installed, map_state;
            public long all_event_masks, your_event_mask, do_not_propagate_mask;
            public int override_redirect;
            public IntPtr screen;
        }

        // XSendEvent reads a whole XEvent union (24 longs), not just the client-message prefix.
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        struct XClientMessageEvent
        {
            [FieldOffset(0)] public int type;
            [FieldOffset(16)] public int send_event;
            [FieldOffset(24)] public IntPtr display;
            [FieldOffset(32)] public ulong window;
            [FieldOffset(40)] public ulong message_type;
            [FieldOffset(48)] public int format;
            [FieldOffset(56)] public long l0;
            [FieldOffset(64)] public long l1;
        }

        const int IsViewable = 2;
        const int ZPixmap = 2;
        const int ClientMessage = 33;
        const int RevertToParent = 2;
        const ulong AnyPropertyType = 0;
        const ulong XA_ATOM = 4;
        const ulong XA_CARDINAL = 6;
        const ulong XA_STRING = 31;
        const ulong XK_Control_L = 0xffe3;
        const ulong XK_r = 0x0072;

        static IntPtr Open(string display)
        {
            if (string.IsNullOrEmpty(display)) return IntPtr.Zero;
#if !UNITY_EDITOR
            if (Interlocked.Exchange(ref _handlerInstalled, 1) == 0) XSetErrorHandler(IgnoreErrors);
#endif
            return XOpenDisplay(display);
        }

        /// <summary>Top-level windows on <paramref name="display"/> owned by any of <paramref name="pids"/> (null = any
        /// process). Unless <paramref name="includeHidden"/>, only viewable windows that carry a title.</summary>
        public static List<Window> ListWindows(string display, ICollection<uint> pids, bool includeHidden = false)
        {
            var result = new List<Window>();
            IntPtr d = Open(display);
            if (d == IntPtr.Zero) return result;
            try
            {
                ulong root = XDefaultRootWindow(d);
                ulong pidAtom = XInternAtom(d, "_NET_WM_PID", false);
                ulong nameAtom = XInternAtom(d, "_NET_WM_NAME", false);
                ulong classAtom = XInternAtom(d, "WM_CLASS", false);
            ulong typeAtom = XInternAtom(d, "_NET_WM_WINDOW_TYPE", false);
            ulong dialogAtom = XInternAtom(d, "_NET_WM_WINDOW_TYPE_DIALOG", false);
                var stack = new Stack<(ulong w, int depth)>();
                stack.Push((root, 0));
                while (stack.Count > 0)
                {
                    var (w, depth) = stack.Pop();
                    if (w != root)
                    {
                        uint pid = (uint)ReadCardinal(d, w, pidAtom);
                        if (pid != 0)
                        {
                            // A window carrying _NET_WM_PID is a client window; its children are the toolkit's own.
                            if ((pids == null || pids.Contains(pid)) && XGetWindowAttributes(d, w, out var a) != 0
                                && (includeHidden || a.map_state == IsViewable))
                            {
                                string title = ReadText(d, w, nameAtom) ?? ReadText(d, w, (ulong)39 /* XA_WM_NAME */);
                                if (includeHidden || !string.IsNullOrEmpty(title))
                                {
                                    XTranslateCoordinates(d, w, root, 0, 0, out int ax, out int ay, out _);
                                    result.Add(new Window
                                    {
                                        Id = w, Pid = pid, Display = display, Title = title,
                                        WmClass = ReadText(d, w, classAtom),
                                        X = ax, Y = ay, Width = a.width, Height = a.height,
                                        Dialog = HasAtom(d, w, typeAtom, dialogAtom),
                                    });
                                }
                            }
                            continue;
                        }
                    }
                    // Client windows sit directly under the root, or one/two frames deep under a window manager.
                    if (depth >= 3) continue;
                    if (XQueryTree(d, w, out _, out _, out IntPtr children, out uint count) == 0 || children == IntPtr.Zero) continue;
                    for (int i = 0; i < count; i++)
                        stack.Push(((ulong)Marshal.ReadInt64(children, i * 8), depth + 1));
                    XFree(children);
                }
            }
            finally { XCloseDisplay(d); }
            return result;
        }

        static long ReadCardinal(IntPtr d, ulong w, ulong atom)
        {
            if (XGetWindowProperty(d, w, atom, 0, 1, false, XA_CARDINAL, out _, out int format, out ulong n, out _, out IntPtr data) != 0
                || data == IntPtr.Zero) return 0;
            try { return format == 32 && n > 0 ? Marshal.ReadInt64(data) : 0; }
            finally { XFree(data); }
        }

        static bool HasAtom(IntPtr d, ulong w, ulong property, ulong value)
        {
            if (XGetWindowProperty(d, w, property, 0, 32, false, XA_ATOM, out _, out int format, out ulong n, out _, out IntPtr data) != 0
                || data == IntPtr.Zero) return false;
            try
            {
                if (format != 32) return false;
                for (int i = 0; i < (int)n; i++)
                    if ((ulong)Marshal.ReadInt64(data, i * 8) == value) return true;
                return false;
            }
            finally { XFree(data); }
        }

        static string ReadText(IntPtr d, ulong w, ulong atom)
        {
            if (XGetWindowProperty(d, w, atom, 0, 1024, false, AnyPropertyType, out _, out int format, out ulong n, out _, out IntPtr data) != 0
                || data == IntPtr.Zero) return null;
            try
            {
                if (format != 8 || n == 0) return null;
                var bytes = new byte[n];
                Marshal.Copy(data, bytes, 0, (int)n);
                // WM_CLASS is "instance\0class\0"; titles have no NULs.
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0').Replace('\0', '/');
            }
            finally { XFree(data); }
        }

        /// <summary>Top-down BGRA pixels of what the screen shows inside the window's rectangle.</summary>
        public static (byte[] bgra, int width, int height) Capture(Window win)
        {
            IntPtr d = Open(win.Display);
            if (d == IntPtr.Zero) return (null, 0, 0);
            try
            {
                ulong root = XDefaultRootWindow(d);
                XGetWindowAttributes(d, root, out var screen);
                int x = Math.Max(0, win.X), y = Math.Max(0, win.Y);
                int w = Math.Min(win.X + win.Width, screen.width) - x;
                int h = Math.Min(win.Y + win.Height, screen.height) - y;
                if (w <= 0 || h <= 0) return (null, 0, 0);

                // Grab from the root, not the window: the result is what a viewer of this display sees,
                // including any dialog on top, and needs no compositor.
                IntPtr img = XGetImage(d, root, x, y, (uint)w, (uint)h, ulong.MaxValue, ZPixmap);
                if (img == IntPtr.Zero) return (null, 0, 0);
                try
                {
                    IntPtr pixels = Marshal.ReadIntPtr(img, 16);
                    int stride = Marshal.ReadInt32(img, 44);
                    int bpp = Marshal.ReadInt32(img, 48);
                    ulong rMask = (ulong)Marshal.ReadInt64(img, 56);
                    ulong gMask = (ulong)Marshal.ReadInt64(img, 64);
                    ulong bMask = (ulong)Marshal.ReadInt64(img, 72);
                    if (bpp != 32) throw new NotSupportedException($"X display depth uses {bpp} bits per pixel; only 32 is supported.");

                    int rShift = Shift(rMask), gShift = Shift(gMask), bShift = Shift(bMask);
                    var row = new byte[stride];
                    var bgra = new byte[w * h * 4];
                    for (int yy = 0; yy < h; yy++)
                    {
                        Marshal.Copy(pixels + yy * stride, row, 0, stride);
                        int o = yy * w * 4;
                        for (int xx = 0; xx < w; xx++)
                        {
                            uint p = BitConverter.ToUInt32(row, xx * 4);
                            bgra[o++] = (byte)((p & bMask) >> bShift);
                            bgra[o++] = (byte)((p & gMask) >> gShift);
                            bgra[o++] = (byte)((p & rMask) >> rShift);
                            bgra[o++] = 255;
                        }
                    }
                    return (bgra, w, h);
                }
                finally { XDestroyImage(img); }
            }
            finally { XCloseDisplay(d); }
        }

        static int Shift(ulong mask)
        {
            int s = 0;
            while (mask != 0 && (mask & 1) == 0) { mask >>= 1; s++; }
            return s;
        }

        public static void Activate(Window win)
        {
            IntPtr d = Open(win.Display);
            if (d == IntPtr.Zero) return;
            try
            {
                XRaiseWindow(d, win.Id);
                XSetInputFocus(d, win.Id, RevertToParent, 0);
                XSync(d, false);
            }
            finally { XCloseDisplay(d); }
        }

        public static void Move(string display, ulong window, int x, int y)
        {
            IntPtr d = Open(display);
            if (d == IntPtr.Zero) return;
            try
            {
                XMoveWindow(d, window, x, y);
                XSync(d, false);
            }
            finally { XCloseDisplay(d); }
        }

        /// <summary>Ask the window to close, the same request a title-bar close button sends.</summary>
        public static void Close(Window win)
        {
            IntPtr d = Open(win.Display);
            if (d == IntPtr.Zero) return;
            try
            {
                var ev = new XClientMessageEvent
                {
                    type = ClientMessage,
                    send_event = 1,
                    display = d,
                    window = win.Id,
                    message_type = XInternAtom(d, "WM_PROTOCOLS", false),
                    format = 32,
                    l0 = (long)XInternAtom(d, "WM_DELETE_WINDOW", false),
                };
                XSendEvent(d, win.Id, false, 0, ref ev);
                XSync(d, false);
            }
            finally { XCloseDisplay(d); }
        }

        /// <summary>Type Ctrl+R into whichever window has focus on the display (call <see cref="Activate"/> first).</summary>
        public static void SendCtrlR(string display)
        {
            IntPtr d = Open(display);
            if (d == IntPtr.Zero) return;
            try
            {
                uint ctrl = XKeysymToKeycode(d, XK_Control_L), r = XKeysymToKeycode(d, XK_r);
                XTestFakeKeyEvent(d, ctrl, true, 0);
                XTestFakeKeyEvent(d, r, true, 0);
                XTestFakeKeyEvent(d, r, false, 0);
                XTestFakeKeyEvent(d, ctrl, false, 0);
                XSync(d, false);
            }
            finally { XCloseDisplay(d); }
        }
    }
}
