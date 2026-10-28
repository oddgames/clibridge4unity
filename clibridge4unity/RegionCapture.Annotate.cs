using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace clibridge4unity;

/// <summary>
/// Annotation pass for CAPTURE: once the region is dragged, the selection stays up with a toolbar
/// and you can draw boxes, arrows, freehand strokes, highlights and text before it is copied.
///
/// Shapes are vectors in overlay coordinates, redrawn over the frozen backdrop on every paint, so
/// undo is just dropping the last one. They are burned into the undimmed desktop bitmap once, when
/// the capture is accepted — the PNG, the clipboard and latest.png all come out of that buffer, so
/// what you marked is what everyone downstream sees.
///
/// Paint goes through a back buffer clipped to the dirty rect. The old path blitted the whole
/// virtual desktop to the window on every mouse-move, which was tolerable for one rectangle and
/// flickers badly once shapes are drawn on top of it.
///
/// GDI, like the rest of this file: no WinForms in a trimmed exe. It does not antialias, which
/// shows on diagonal arrows and nowhere that matters.
/// </summary>
static partial class RegionCapture
{
    const uint WM_CHAR = 0x0102;
    const int VK_RETURN = 0x0D, VK_BACK = 0x08, VK_CONTROL = 0x11;
    const int IDC_ARROW = 32512, IDC_IBEAM = 32513;
    const int BK_TRANSPARENT = 1;
    const int NULL_BRUSH = 5, NULL_PEN = 8;
    const int LOGPIXELSY = 90;

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern bool Polyline(IntPtr hdc, POINT[] pts, int count);
    [DllImport("gdi32.dll")] static extern bool Polygon(IntPtr hdc, POINT[] pts, int count);
    // BLENDFUNCTION is four bytes passed by value: op | flags << 8 | constant alpha << 16 | format << 24.
    [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, int sw, int sh, uint blend);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
        uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOut(IntPtr hdc, int x, int y, string s, int len);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextExtentPoint32(IntPtr hdc, string s, int len, out SIZE size);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);
    [DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr hdc, int saved);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hWnd, ref RECT rect, bool erase);

    enum Tool { Box, Arrow, Pen, Highlight, Text }

    sealed class Shape
    {
        public Tool Kind;
        public uint Color;
        public int Width;
        public readonly List<POINT> Pts = new();
        public StringBuilder Text;
    }

    // COLORREF is 0x00BBGGRR.
    static readonly uint[] Palette = { 0x003C4CE7, 0x000FC4F1, 0x0071CC2E, 0x00DB9834, 0x00FFFFFF, 0x00000000 };
    static readonly string[] PaletteNames = { "red", "yellow", "green", "blue", "white", "black" };
    // Highlight ignores the palette: a translucent yellow wash. Not R2_MASKPEN, the classic
    // marker trick — ANDing yellow into the pixels only removes colour, so on Unity's dark theme
    // the mark was all but invisible. A blend shows on dark and light alike and keeps text legible.
    const uint HighlightColor = 0x0000E0FF;
    const byte HighlightAlpha = 0x60;

    static readonly List<Shape> _shapes = new();
    static Shape _live;                  // being dragged, or receiving keystrokes for Text
    static bool _wantAnnotate;           // this overlay ends in the annotate phase (CAPTURE, not RECORD)
    static bool _annotating;             // region chosen, toolbar up
    static bool _annotateEnabled = true; // --no-annotate / tray toggle
    static Tool _tool = Tool.Box;        // survives between captures in the daemon
    static int _colour;
    static float _ui = 1f;
    static IntPtr _hdcBack, _hbmBack, _fontNote, _fontUi, _hdcHighlight, _hbmHighlight;
    static int _hoverBtn = -1;
    static RECT _lastBounds, _barRect;
    static readonly List<(RECT rc, int id, string label)> _buttons = new();
    static string _annotationNotes;      // what was drawn, for the .context.md

    const int BTN_COLOUR = 10, BTN_UNDO = 20, BTN_DONE = 30, BTN_CANCEL = 40;

    static int PenWidth => Math.Max(2, (int)Math.Round(3 * _ui));
    static int Px(float v) => (int)Math.Round(v * _ui);

    static RECT SelRect() => new RECT
    {
        Left = Math.Min(_x0, _x1), Top = Math.Min(_y0, _y1),
        Right = Math.Max(_x0, _x1), Bottom = Math.Max(_y0, _y1),
    };

    static RECT Inflate(RECT r, int d) => new RECT { Left = r.Left - d, Top = r.Top - d, Right = r.Right + d, Bottom = r.Bottom + d };

    static RECT Union(RECT a, RECT b)
    {
        if (a.Right <= a.Left || a.Bottom <= a.Top) return b;
        if (b.Right <= b.Left || b.Bottom <= b.Top) return a;
        return new RECT
        {
            Left = Math.Min(a.Left, b.Left), Top = Math.Min(a.Top, b.Top),
            Right = Math.Max(a.Right, b.Right), Bottom = Math.Max(a.Bottom, b.Bottom),
        };
    }

    static bool Contains(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    static void ResetAnnotation(bool annotate)
    {
        _wantAnnotate = annotate;
        _annotating = false;
        _shapes.Clear();
        _live = null;
        _hoverBtn = -1;
        _lastBounds = default;
        _annotationNotes = null;
    }

    static void InitOverlayScale()
    {
        IntPtr sdc = GetDC(IntPtr.Zero);
        int dpi = GetDeviceCaps(sdc, LOGPIXELSY);
        ReleaseDC(IntPtr.Zero, sdc);
        _ui = dpi > 0 ? dpi / 96f : 1f;
    }

    static IntPtr NoteFont() => _fontNote != IntPtr.Zero ? _fontNote
        // ANTIALIASED, not ClearType: this text is burned into a PNG, and ClearType's subpixel
        // fringes look like colour noise once the image leaves this monitor.
        : _fontNote = CreateFont(-Px(22), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");

    static IntPtr UiFont() => _fontUi != IntPtr.Zero ? _fontUi
        : _fontUi = CreateFont(-Px(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

    static void ReleaseOverlayGdi()
    {
        if (_hbmBack != IntPtr.Zero) { DeleteObject(_hbmBack); _hbmBack = IntPtr.Zero; }
        if (_hdcBack != IntPtr.Zero) { DeleteDC(_hdcBack); _hdcBack = IntPtr.Zero; }
    }

    static void ReleaseAnnotationGdi()
    {
        if (_hbmHighlight != IntPtr.Zero) { DeleteObject(_hbmHighlight); _hbmHighlight = IntPtr.Zero; }
        if (_hdcHighlight != IntPtr.Zero) { DeleteDC(_hdcHighlight); _hdcHighlight = IntPtr.Zero; }
        if (_fontNote != IntPtr.Zero) { DeleteObject(_fontNote); _fontNote = IntPtr.Zero; }
        if (_fontUi != IntPtr.Zero) { DeleteObject(_fontUi); _fontUi = IntPtr.Zero; }
    }

    // ─── Paint ────────────────────────────────────────────────────────

    static void PaintOverlay(IntPtr hdc, RECT clip)
    {
        if (_hdcBack == IntPtr.Zero)
        {
            _hdcBack = CreateCompatibleDC(hdc);
            _hbmBack = CreateCompatibleBitmap(hdc, _vw, _vh);
            SelectObject(_hdcBack, _hbmBack);
        }
        int cw = clip.Right - clip.Left, ch = clip.Bottom - clip.Top;
        if (cw <= 0 || ch <= 0) return;

        int saved = SaveDC(_hdcBack);
        IntersectClipRect(_hdcBack, clip.Left, clip.Top, clip.Right, clip.Bottom);
        BitBlt(_hdcBack, clip.Left, clip.Top, cw, ch, _hdcDim, clip.Left, clip.Top, SRCCOPY);

        var s = SelRect();
        int sw = s.Right - s.Left, sh = s.Bottom - s.Top;
        if ((_dragging || _haveRect || _annotating) && sw > 0 && sh > 0)
        {
            // Undimmed inside the selection, so the crop is judged on real pixels.
            BitBlt(_hdcBack, s.Left, s.Top, sw, sh, _hdcScreen, s.Left, s.Top, SRCCOPY);

            int inner = SaveDC(_hdcBack);
            IntersectClipRect(_hdcBack, s.Left, s.Top, s.Right, s.Bottom);
            foreach (var shape in _shapes) DrawShape(_hdcBack, shape, false);
            if (_live != null) DrawShape(_hdcBack, _live, true);
            RestoreDC(_hdcBack, inner);

            var border = Inflate(s, 1);
            FrameRect(_hdcBack, ref border, _brushBorder);

            if (_annotating) DrawToolbar(_hdcBack);
            else DrawSizeLabel(_hdcBack, s);
        }
        RestoreDC(_hdcBack, saved);
        BitBlt(hdc, clip.Left, clip.Top, cw, ch, _hdcBack, clip.Left, clip.Top, SRCCOPY);
    }

    /// <summary>Invalidate only what changed: last frame's selection furniture plus this frame's.</summary>
    static void InvalidateOverlay(IntPtr hWnd)
    {
        var s = SelRect();
        var now = Union(Inflate(s, Px(4)), SizeLabelRect(s));
        if (_annotating) now = Union(now, Inflate(_barRect, 2));
        var dirty = Union(now, _lastBounds);
        _lastBounds = now;
        InvalidateRect(hWnd, ref dirty, false);
    }

    static RECT SizeLabelRect(RECT s)
    {
        int h = Px(20), w = Px(96);
        int top = s.Top - h - Px(4);
        if (top < 0) top = s.Top + Px(4);
        return new RECT { Left = s.Left, Top = top, Right = s.Left + w, Bottom = top + h };
    }

    static void DrawSizeLabel(IntPtr hdc, RECT s)
    {
        string text = $"{s.Right - s.Left} × {s.Bottom - s.Top}";
        var r = SizeLabelRect(s);
        var of = SelectObject(hdc, UiFont());
        GetTextExtentPoint32(hdc, text, text.Length, out SIZE sz);
        r.Right = r.Left + sz.cx + Px(12);
        FillSolid(hdc, r, 0x00302B28);
        SetBkMode(hdc, BK_TRANSPARENT);
        SetTextColor(hdc, 0x00FFFFFF);
        TextOut(hdc, r.Left + Px(6), r.Top + (r.Bottom - r.Top - sz.cy) / 2, text, text.Length);
        SelectObject(hdc, of);
    }

    static void FillSolid(IntPtr hdc, RECT r, uint color)
    {
        IntPtr b = CreateSolidBrush(color);
        FillRect(hdc, ref r, b);
        DeleteObject(b);
    }

    static void DrawShape(IntPtr hdc, Shape s, bool live)
    {
        if (s.Kind == Tool.Text) { DrawNote(hdc, s, live); return; }
        if (s.Pts.Count < 2) return;

        if (s.Kind == Tool.Highlight)
        {
            if (_hdcHighlight == IntPtr.Zero)
            {
                // One yellow pixel, stretched by AlphaBlend over the marked rectangle.
                _hdcHighlight = CreateCompatibleDC(hdc);
                _hbmHighlight = CreateCompatibleBitmap(hdc, 1, 1);
                SelectObject(_hdcHighlight, _hbmHighlight);
                FillSolid(_hdcHighlight, new RECT { Right = 1, Bottom = 1 }, HighlightColor);
            }
            var r = Norm(s.Pts[0], s.Pts[^1]);
            AlphaBlend(hdc, r.Left, r.Top, r.Right - r.Left + 1, r.Bottom - r.Top + 1,
                       _hdcHighlight, 0, 0, 1, 1, (uint)HighlightAlpha << 16);
            return;
        }

        IntPtr pen = CreatePen(0, s.Width, s.Color);
        var oldPen = SelectObject(hdc, pen);
        switch (s.Kind)
        {
            case Tool.Box:
            {
                var ob = SelectObject(hdc, GetStockObject(NULL_BRUSH));
                var r = Norm(s.Pts[0], s.Pts[^1]);
                Rectangle(hdc, r.Left, r.Top, r.Right, r.Bottom);
                SelectObject(hdc, ob);
                break;
            }
            case Tool.Pen:
                Polyline(hdc, s.Pts.ToArray(), s.Pts.Count);
                break;
            case Tool.Arrow:
                DrawArrow(hdc, s);
                break;
        }
        SelectObject(hdc, oldPen);
        DeleteObject(pen);
    }

    /// <summary>Shaft stops at the base of the head, so a thick pen does not poke through the tip.</summary>
    static void DrawArrow(IntPtr hdc, Shape s)
    {
        POINT a = s.Pts[0], b = s.Pts[^1];
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        double ux = dx / len, uy = dy / len;
        double head = Math.Min(len, s.Width * 4 + 10 * _ui), half = head * 0.55;
        double bx = b.X - ux * head, by = b.Y - uy * head;
        var baseP = new POINT { X = (int)Math.Round(bx), Y = (int)Math.Round(by) };
        Polyline(hdc, new[] { a, baseP }, 2);

        IntPtr brush = CreateSolidBrush(s.Color);
        IntPtr thin = CreatePen(0, 1, s.Color);
        var ob = SelectObject(hdc, brush);
        var op = SelectObject(hdc, thin);
        Polygon(hdc, new[]
        {
            b,
            new POINT { X = (int)Math.Round(bx - uy * half), Y = (int)Math.Round(by + ux * half) },
            new POINT { X = (int)Math.Round(bx + uy * half), Y = (int)Math.Round(by - ux * half) },
        }, 3);
        SelectObject(hdc, op);
        SelectObject(hdc, ob);
        DeleteObject(thin);
        DeleteObject(brush);
    }

    /// <summary>Text with a contrasting halo, so a note stays legible on whatever it lands on.</summary>
    static void DrawNote(IntPtr hdc, Shape s, bool live)
    {
        var p = s.Pts[0];
        string t = s.Text?.ToString() ?? "";
        var of = SelectObject(hdc, NoteFont());
        SetBkMode(hdc, BK_TRANSPARENT);
        if (t.Length > 0)
        {
            uint c = s.Color;
            int luma = ((int)(c & 0xFF) * 299 + (int)((c >> 8) & 0xFF) * 587 + (int)((c >> 16) & 0xFF) * 114) / 1000;
            SetTextColor(hdc, luma > 140 ? 0x00000000u : 0x00FFFFFFu);
            int o = Math.Max(1, Px(1.5f));
            for (int ox = -o; ox <= o; ox += o)
                for (int oy = -o; oy <= o; oy += o)
                    if (ox != 0 || oy != 0) TextOut(hdc, p.X + ox, p.Y + oy, t, t.Length);
            SetTextColor(hdc, c);
            TextOut(hdc, p.X, p.Y, t, t.Length);
        }
        if (live)
        {
            GetTextExtentPoint32(hdc, t + "|", t.Length + 1, out SIZE full);
            GetTextExtentPoint32(hdc, t, t.Length, out SIZE body);
            FillSolid(hdc, new RECT { Left = p.X + body.cx + 1, Top = p.Y, Right = p.X + body.cx + 1 + Math.Max(2, Px(2)), Bottom = p.Y + full.cy }, s.Color);
        }
        SelectObject(hdc, of);
    }

    static RECT Norm(POINT a, POINT b) => new RECT
    {
        Left = Math.Min(a.X, b.X), Top = Math.Min(a.Y, b.Y),
        Right = Math.Max(a.X, b.X), Bottom = Math.Max(a.Y, b.Y),
    };

    // ─── Toolbar ──────────────────────────────────────────────────────

    /// <summary>
    /// Lay the toolbar out once per region: below the selection, right-aligned; above it when the
    /// selection reaches the bottom of the desktop; inside it as a last resort.
    /// </summary>
    static void LayoutToolbar(IntPtr hWnd)
    {
        _buttons.Clear();
        var items = new (int id, string label)[]
        {
            ((int)Tool.Box, "Box"), ((int)Tool.Arrow, "Arrow"), ((int)Tool.Pen, "Pen"),
            ((int)Tool.Highlight, "Highlight"), ((int)Tool.Text, "Text"), (BTN_COLOUR, null),
            (BTN_UNDO, "Undo"), (BTN_DONE, "Copy"), (BTN_CANCEL, "Cancel"),
        };

        IntPtr hdc = GetDC(hWnd);
        var of = SelectObject(hdc, UiFont());
        int h = Px(30), pad = Px(12), gap = Px(2), x = gap;
        foreach (var (id, label) in items)
        {
            int w;
            if (label == null) w = h;
            else { GetTextExtentPoint32(hdc, label, label.Length, out SIZE sz); w = sz.cx + pad * 2; }
            _buttons.Add((new RECT { Left = x, Top = gap, Right = x + w, Bottom = gap + h }, id, label));
            x += w + gap;
        }
        SelectObject(hdc, of);
        ReleaseDC(hWnd, hdc);

        // Fitted to the MONITOR the selection is on, not the virtual desktop: with several monitors
        // the desktop's bounding box has dead zones and neighbours, and a bar that lands on the
        // next screen (or in a gap off every screen) is a bar you cannot click.
        int barW = x, barH = h + gap * 2, margin = Px(6);
        var s = SelRect();
        var screenSel = new RECT { Left = s.Left + _vx, Top = s.Top + _vy, Right = s.Right + _vx, Bottom = s.Bottom + _vy };
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var mon = GetMonitorInfo(MonitorFromRect(ref screenSel, 2 /* NEAREST */), ref mi)
            ? new RECT { Left = mi.rcMonitor.Left - _vx, Top = mi.rcMonitor.Top - _vy, Right = mi.rcMonitor.Right - _vx, Bottom = mi.rcMonitor.Bottom - _vy }
            : new RECT { Left = 0, Top = 0, Right = _vw, Bottom = _vh };
        int bx = Math.Clamp(s.Right - barW, mon.Left, Math.Max(mon.Left, mon.Right - barW));
        int by = s.Bottom + margin;
        if (by + barH > mon.Bottom) by = s.Top - margin - barH;
        if (by < mon.Top) by = Math.Min(s.Bottom, mon.Bottom) - margin - barH;

        _barRect = new RECT { Left = bx, Top = by, Right = bx + barW, Bottom = by + barH };
        for (int i = 0; i < _buttons.Count; i++)
        {
            var (rc, id, label) = _buttons[i];
            rc.Left += bx; rc.Right += bx; rc.Top += by; rc.Bottom += by;
            _buttons[i] = (rc, id, label);
        }
    }

    static int HitButton(int x, int y)
    {
        foreach (var b in _buttons) if (Contains(b.rc, x, y)) return b.id;
        return -1;
    }

    static void DrawToolbar(IntPtr hdc)
    {
        FillSolid(hdc, _barRect, 0x00302B28);
        var of = SelectObject(hdc, UiFont());
        SetBkMode(hdc, BK_TRANSPARENT);
        SetTextColor(hdc, 0x00FFFFFF);
        foreach (var (rc, id, label) in _buttons)
        {
            bool selected = id == (int)_tool || id == BTN_DONE;
            if (selected) FillSolid(hdc, rc, 0x00D7A24E);
            else if (id == _hoverBtn) FillSolid(hdc, rc, 0x00504840);

            if (id == BTN_COLOUR)
            {
                int inset = Px(7);
                var sw = new RECT { Left = rc.Left + inset, Top = rc.Top + inset, Right = rc.Right - inset, Bottom = rc.Bottom - inset };
                FillSolid(hdc, Inflate(sw, 1), 0x00FFFFFF);
                FillSolid(hdc, sw, Palette[_colour]);
                continue;
            }
            GetTextExtentPoint32(hdc, label, label.Length, out SIZE sz);
            TextOut(hdc, rc.Left + (rc.Right - rc.Left - sz.cx) / 2, rc.Top + (rc.Bottom - rc.Top - sz.cy) / 2, label, label.Length);
        }
        SelectObject(hdc, of);
    }

    // ─── Input ────────────────────────────────────────────────────────

    /// <summary>Enter the annotate phase instead of closing when the drag ends.</summary>
    static void BeginAnnotate(IntPtr hWnd)
    {
        _annotating = true;
        _live = null;
        _hoverBtn = -1;
        LayoutToolbar(hWnd);
        InvalidateOverlay(hWnd);
    }

    /// <summary>Messages while annotating. Returns false to fall through to the overlay's own handling.</summary>
    static bool AnnotateMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_SETCURSOR:
            {
                GetCursorPos(out POINT p);
                int x = p.X - _vx, y = p.Y - _vy;
                int cursor = HitButton(x, y) >= 0 ? IDC_ARROW
                           : _tool == Tool.Text && Contains(SelRect(), x, y) ? IDC_IBEAM : IDC_CROSS;
                SetCursor(LoadCursor(IntPtr.Zero, cursor));
                result = (IntPtr)1;
                return true;
            }

            case WM_MOUSEMOVE:
            {
                int x = LoWord(lParam), y = HiWord(lParam);
                if (_live != null && _live.Kind != Tool.Text)
                {
                    var p = ClampToSelection(x, y);
                    if (_live.Kind == Tool.Pen)
                    {
                        var last = _live.Pts[^1];
                        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 2) _live.Pts.Add(p);
                    }
                    else if (_live.Pts.Count == 1) _live.Pts.Add(p);
                    else _live.Pts[1] = p;
                    InvalidateOverlay(hWnd);
                }
                else
                {
                    int hover = HitButton(x, y);
                    if (hover != _hoverBtn) { _hoverBtn = hover; InvalidateOverlay(hWnd); }
                }
                return true;
            }

            case WM_LBUTTONDOWN:
            {
                int x = LoWord(lParam), y = HiWord(lParam);
                int btn = HitButton(x, y);
                CommitNote();
                if (btn >= 0) { OnButton(hWnd, btn); return true; }

                // With nothing drawn yet, a press outside the selection starts a new one — the
                // drag missed. Once something is drawn, it never silently throws the work away.
                if (_shapes.Count == 0 && !Contains(SelRect(), x, y))
                {
                    _annotating = false;
                    _hoverBtn = -1;
                    return false;   // the overlay's own WM_LBUTTONDOWN starts the new drag
                }

                var start = ClampToSelection(x, y);
                _live = new Shape { Kind = _tool, Color = Palette[_colour], Width = PenWidth };
                _live.Pts.Add(start);
                if (_tool == Tool.Text) _live.Text = new StringBuilder();
                else SetCapture(hWnd);
                InvalidateOverlay(hWnd);
                return true;
            }

            case WM_LBUTTONUP:
                if (_live != null && _live.Kind != Tool.Text)
                {
                    ReleaseCapture();
                    // A click that drew nothing copies. Keeps the common case — no marks wanted —
                    // at drag + one click, with no hunting for the Copy button or a key.
                    if (!Meaningful(_live)) { _live = null; Accept(hWnd); return true; }
                    _shapes.Add(_live);
                    _live = null;
                    InvalidateOverlay(hWnd);
                }
                return true;

            case WM_CHAR:
            {
                if (_live == null || _live.Kind != Tool.Text) return true;
                char c = (char)wParam.ToInt32();
                if (c == '\r') CommitNote();
                else if (c == (char)VK_ESCAPE) _live = null;
                else if (c == '\b') { if (_live.Text.Length > 0) _live.Text.Length--; }
                else if (c >= ' ') _live.Text.Append(c);
                InvalidateNote(hWnd);
                return true;
            }

            case WM_KEYDOWN:
            {
                if (_live != null && _live.Kind == Tool.Text) return true;   // WM_CHAR does the typing
                int vk = wParam.ToInt32();
                bool ctrl = GetKeyState(VK_CONTROL) < 0;
                if (vk == VK_ESCAPE) return false;                            // overlay cancels
                if (vk == VK_RETURN || (ctrl && vk == 'C')) Accept(hWnd);
                else if ((ctrl && vk == 'Z') || vk == VK_BACK) Undo(hWnd);
                else if (!ctrl)
                {
                    switch (vk)
                    {
                        case 'B': SetTool(hWnd, Tool.Box); break;
                        case 'A': SetTool(hWnd, Tool.Arrow); break;
                        case 'P': SetTool(hWnd, Tool.Pen); break;
                        case 'H': SetTool(hWnd, Tool.Highlight); break;
                        case 'T': SetTool(hWnd, Tool.Text); break;
                        case 'C': _colour = (_colour + 1) % Palette.Length; InvalidateOverlay(hWnd); break;
                        default:
                            if (vk >= '1' && vk < '1' + Palette.Length) { _colour = vk - '1'; InvalidateOverlay(hWnd); }
                            break;
                    }
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>A note can outgrow the selection-plus-margin rect; repaint the whole selection band.</summary>
    static void InvalidateNote(IntPtr hWnd)
    {
        var r = new RECT { Left = 0, Top = SelRect().Top - Px(4), Right = _vw, Bottom = SelRect().Bottom + Px(4) };
        InvalidateRect(hWnd, ref r, false);
        InvalidateOverlay(hWnd);
    }

    static void OnButton(IntPtr hWnd, int id)
    {
        switch (id)
        {
            case BTN_COLOUR: _colour = (_colour + 1) % Palette.Length; InvalidateOverlay(hWnd); break;
            case BTN_UNDO: Undo(hWnd); break;
            case BTN_DONE: Accept(hWnd); break;
            case BTN_CANCEL: _haveRect = false; DestroyWindow(hWnd); break;
            default: SetTool(hWnd, (Tool)id); break;
        }
    }

    static void SetTool(IntPtr hWnd, Tool t) { _tool = t; InvalidateOverlay(hWnd); }

    static void Undo(IntPtr hWnd)
    {
        if (_shapes.Count > 0) _shapes.RemoveAt(_shapes.Count - 1);
        InvalidateNote(hWnd);
    }

    static void Accept(IntPtr hWnd)
    {
        CommitNote();
        _haveRect = true;
        DestroyWindow(hWnd);
    }

    static void CommitNote()
    {
        if (_live == null || _live.Kind != Tool.Text) return;
        if (_live.Text.Length > 0) _shapes.Add(_live);
        _live = null;
    }

    static bool Meaningful(Shape s)
    {
        if (s.Pts.Count < 2) return false;
        if (s.Kind == Tool.Pen) return true;
        var r = Norm(s.Pts[0], s.Pts[^1]);
        return Math.Max(r.Right - r.Left, r.Bottom - r.Top) >= 3;
    }

    static POINT ClampToSelection(int x, int y)
    {
        var s = SelRect();
        return new POINT { X = Math.Clamp(x, s.Left, s.Right - 1), Y = Math.Clamp(y, s.Top, s.Bottom - 1) };
    }

    // ─── Accept ───────────────────────────────────────────────────────

    /// <summary>
    /// Burn the accepted shapes into the undimmed desktop bitmap and re-read the pixel buffer the
    /// crop comes from, and describe them for the .context.md — a model told "the red box at
    /// 120,40" knows where to look, where a model handed only the picture has to guess.
    /// </summary>
    static void BurnAnnotations(int rx, int ry)
    {
        _annotationNotes = null;
        if (_shapes.Count == 0) return;

        var s = SelRect();
        int saved = SaveDC(_hdcScreen);
        IntersectClipRect(_hdcScreen, s.Left, s.Top, s.Right, s.Bottom);
        foreach (var shape in _shapes) DrawShape(_hdcScreen, shape, false);
        RestoreDC(_hdcScreen, saved);
        var bmi = NewBmi(_vw, _vh);
        GetDIBits(_hdcScreen, _hbmScreen, 0, (uint)_vh, _pixels, ref bmi, 0);

        var sb = new StringBuilder();
        foreach (var shape in _shapes)
        {
            string colour = PaletteNames[Math.Max(0, Array.IndexOf(Palette, shape.Color))];
            POINT a = shape.Pts[0], b = shape.Pts[^1];
            var r = Norm(a, b);
            switch (shape.Kind)
            {
                case Tool.Box:
                    sb.AppendLine($"- {colour} box at {r.Left - rx},{r.Top - ry} size {r.Right - r.Left}x{r.Bottom - r.Top}");
                    break;
                case Tool.Highlight:
                    sb.AppendLine($"- highlighted area at {r.Left - rx},{r.Top - ry} size {r.Right - r.Left}x{r.Bottom - r.Top}");
                    break;
                case Tool.Arrow:
                    sb.AppendLine($"- {colour} arrow from {a.X - rx},{a.Y - ry} pointing at {b.X - rx},{b.Y - ry}");
                    break;
                case Tool.Pen:
                {
                    int l = int.MaxValue, t = int.MaxValue, rr = int.MinValue, bb = int.MinValue;
                    foreach (var p in shape.Pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); rr = Math.Max(rr, p.X); bb = Math.Max(bb, p.Y); }
                    sb.AppendLine($"- {colour} freehand mark around {l - rx},{t - ry} size {rr - l}x{bb - t}");
                    break;
                }
                case Tool.Text:
                    sb.AppendLine($"- {colour} note at {a.X - rx},{a.Y - ry}: \"{shape.Text}\"");
                    break;
            }
        }
        _annotationNotes = sb.ToString();
    }
}
