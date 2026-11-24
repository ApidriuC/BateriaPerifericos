using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BateriaPerifericos;

/// <summary>Lo que se pinta de un dispositivo en la barra.</summary>
public sealed record WidgetItem(DeviceKind Kind, string Text, Color Color, bool Charging = false);

/// <summary>
/// Muestra "🎧 44%  🖱 43%" en el extremo izquierdo de TODAS las barras de tareas: la principal
/// (Shell_TrayWnd) y las de otras pantallas (Shell_SecondaryTrayWnd).
/// Revisa cada segundo si aparecieron/desaparecieron barras (pantallas conectadas, Explorador reiniciado).
/// </summary>
public sealed class TaskbarWidget : IDisposable
{
    private readonly ContextMenuStrip _menu;
    private readonly Action _onClick;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };
    private readonly Dictionary<IntPtr, TaskbarPanel> _panels = new();
    private IReadOnlyList<WidgetItem> _items = Array.Empty<WidgetItem>();

    public TaskbarWidget(ContextMenuStrip menu, Action onClick)
    {
        _menu = menu;
        _onClick = onClick;
        _watch.Tick += (_, _) => Sync();
    }

    /// <summary>Se incrusta en las barras. Devuelve false si no hay barra principal.</summary>
    public bool Attach()
    {
        Sync();
        _watch.Start();
        return _panels.Count > 0;
    }

    public void SetItems(IReadOnlyList<WidgetItem> items)
    {
        _items = items;
        foreach (var p in _panels.Values) p.SetItems(items);
    }

    private void Sync()
    {
        var current = Native.FindTaskbars();
        foreach (var gone in _panels.Keys.Where(h => !current.Contains(h)).ToList())
        {
            _panels[gone].Dispose();
            _panels.Remove(gone);
        }
        foreach (var h in current)
        {
            if (!_panels.TryGetValue(h, out var panel) || !panel.IsAlive)
            {
                panel?.Dispose();
                panel = new TaskbarPanel(h, _menu, _onClick);
                panel.SetItems(_items);
                _panels[h] = panel;
            }
            else
            {
                panel.Reposition();
            }
        }
    }

    public void Dispose()
    {
        _watch.Stop();
        _watch.Dispose();
        foreach (var p in _panels.Values) p.Dispose();
        _panels.Clear();
    }
}

/// <summary>El panel transparente dentro de una barra de tareas concreta.</summary>
internal sealed class TaskbarPanel : NativeWindow, IDisposable
{
    private const string GlyphMouse = "", GlyphHeadset = "";
    private const int Gap = 12, Margin = 6;
    /// <summary>Separación desde el borde izquierdo de la barra (a 96 dpi).</summary>
    private const int LeftMargin = 12;

    private readonly IntPtr _taskbar;
    private readonly ContextMenuStrip _menu;
    private readonly Action _onClick;
    private IReadOnlyList<WidgetItem> _items = Array.Empty<WidgetItem>();
    private Rectangle _lastBounds;
    private bool _hover, _tracking;

    public TaskbarPanel(IntPtr taskbar, ContextMenuStrip menu, Action onClick)
    {
        _taskbar = taskbar;
        _menu = menu;
        _onClick = onClick;
        CreateHandle(new CreateParams
        {
            Parent = taskbar,
            Style = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW,
        });
    }

    public bool IsAlive => Handle != IntPtr.Zero && Native.IsWindow(Handle) && Native.IsWindow(_taskbar);

    public void SetItems(IReadOnlyList<WidgetItem> items)
    {
        _items = items;
        Redraw(force: true);
    }

    public void Reposition() => Redraw(force: false);

    private void Redraw(bool force)
    {
        if (Handle == IntPtr.Zero) return;

        Native.GetWindowRect(_taskbar, out var tb);
        float scale = Native.GetDpiForWindow(_taskbar) / 96f;
        if (scale <= 0) scale = 1;

        // En el extremo izquierdo de la barra (con los íconos de apps centrados, ese espacio está libre).
        int height = tb.Bottom - tb.Top;
        using var bmp = Render(height, scale, out int width);
        var bounds = new Rectangle((int)(LeftMargin * scale), 0, width, height);
        if (!force && bounds == _lastBounds) return;
        _lastBounds = bounds;

        Native.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.Present(Handle, bmp);
    }

    private Bitmap Render(int height, float scale, out int width)
    {
        bool light = IsLightTaskbar();
        var textColorDefault = light ? Color.Black : Color.White;

        using var glyphFont = new Font("Segoe Fluent Icons", 16 * scale, GraphicsUnit.Pixel);
        using var textFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        using var measureBmp = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measureBmp);
        var fmt = StringFormat.GenericTypographic;

        var layout = new List<(WidgetItem item, float glyphW, float textW)>();
        float total = Margin * scale;
        foreach (var it in _items)
        {
            float gw = mg.MeasureString(Glyph(it.Kind), glyphFont, 1000, fmt).Width;
            float tw = mg.MeasureString(it.Text, textFont, 1000, fmt).Width;
            layout.Add((it, gw, tw));
            total += gw + 5 * scale + tw + Gap * scale;
        }
        total += (Margin - Gap) * scale;
        width = Math.Max(1, (int)Math.Ceiling(total));

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Alfa mínimo en todo el panel para que reciba los clics; resaltado al pasar el mouse.
        int pad = (int)(4 * scale);
        var baseFill = _hover ? Color.FromArgb(light ? 25 : 30, textColorDefault) : Color.FromArgb(1, 0, 0, 0);
        using (var bg = new SolidBrush(baseFill))
        using (var path = Rounded(new RectangleF(0, pad, width, height - 2 * pad), 4 * scale))
            g.FillPath(bg, path);

        float x = Margin * scale;
        foreach (var (it, gw, tw) in layout)
        {
            var color = it.Color.IsEmpty ? textColorDefault : it.Color;
            using var brush = new SolidBrush(color);
            float gh = glyphFont.Height, th = textFont.Height;
            float gy = (height - gh) / 2f + 1 * scale;
            g.DrawString(Glyph(it.Kind), glyphFont, brush, x, gy, fmt);

            if (it.Charging) DrawChargingBadge(g, brush, baseFill, new PointF(x + gw - 2.5f * scale, gy + gh * 0.70f), scale);

            x += gw + 5 * scale;
            g.DrawString(it.Text, textFont, brush, x, (height - th) / 2f, fmt);
            x += tw + Gap * scale;
        }
        return bmp;
    }

    private static string Glyph(DeviceKind k) => k == DeviceKind.Mouse ? GlyphMouse : GlyphHeadset;

    /// <summary>
    /// Rayito de "cargando" en la esquina inferior derecha del dibujo: primero recorta un círculo
    /// (como los distintivos de Windows) para que no se mezcle con las líneas del ícono, luego
    /// dibuja un rayo vectorial nítido.
    /// </summary>
    private static void DrawChargingBadge(Graphics g, Brush brush, Color hole, PointF c, float scale)
    {
        float w = 7f * scale, h = 10f * scale, r = 6.2f * scale;
        var old = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var cut = new SolidBrush(hole))
            g.FillEllipse(cut, c.X - r, c.Y - r, 2 * r, 2 * r);
        g.CompositingMode = old;

        float x0 = c.X - w / 2, y0 = c.Y - h / 2;
        PointF P(float u, float v) => new(x0 + u * w, y0 + v * h);
        using var bolt = new GraphicsPath();
        bolt.AddPolygon(new[]
        {
            P(0.62f, 0.00f), P(0.05f, 0.58f), P(0.45f, 0.58f), P(0.30f, 1.00f),
            P(0.95f, 0.38f), P(0.55f, 0.38f), P(0.78f, 0.00f),
        });
        g.FillPath(brush, bolt);
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEMOVE:
                if (!_tracking)
                {
                    _tracking = Native.TrackLeave(Handle);
                    _hover = true;
                    Redraw(force: true);
                }
                break;
            case Native.WM_MOUSELEAVE:
                _tracking = false;
                _hover = false;
                Redraw(force: true);
                break;
            case Native.WM_LBUTTONUP:
                _onClick();
                break;
            case Native.WM_RBUTTONUP:
                Native.SetForegroundWindow(Handle);
                _menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveRight);
                break;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

internal static class Native
{
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080;
    public const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSELEAVE = 0x02A3;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint TME_LEAVE = 0x2;

    /// <summary>Barra principal primero, luego las de las demás pantallas.</summary>
    public static List<IntPtr> FindTaskbars()
    {
        var list = new List<IntPtr>();
        var main = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            list.Add(h);
        return list;
    }

    public static bool TrackLeave(IntPtr hwnd)
    {
        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hwnd };
        return TrackMouseEvent(ref tme);
    }

    public static void Present(IntPtr hwnd, Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
