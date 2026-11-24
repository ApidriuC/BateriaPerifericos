using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BateriaPerifericos;

/// <summary>Lo que se pinta de un dispositivo en la barra.</summary>
public sealed record WidgetItem(DeviceKind Kind, string Text, Color Color, bool Charging = false);

/// <summary>
/// Panel transparente incrustado en la barra de tareas (hijo de Shell_TrayWnd), justo a la
/// izquierda del área de notificación, que muestra "🎧 44%  🖱 43%".
/// Usa una ventana en capas con alfa por píxel para que se vea el color de la barra detrás.
/// </summary>
public sealed class TaskbarWidget : NativeWindow, IDisposable
{
    private const string GlyphMouse = "", GlyphHeadset = "", Bolt = "";
    private const int Gap = 12, Margin = 6;

    private readonly ContextMenuStrip _menu;
    private readonly Action _onClick;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };
    private IReadOnlyList<WidgetItem> _items = Array.Empty<WidgetItem>();
    private IntPtr _taskbar;
    private Rectangle _lastBounds;
    private bool _hover, _tracking;

    public TaskbarWidget(ContextMenuStrip menu, Action onClick)
    {
        _menu = menu;
        _onClick = onClick;
        _watch.Tick += (_, _) => EnsureAttached();
    }

    /// <summary>Intenta incrustarse en la barra. Devuelve false si no se encontró la barra.</summary>
    public bool Attach()
    {
        bool ok = EnsureAttached();
        _watch.Start();
        return ok;
    }

    public void SetItems(IReadOnlyList<WidgetItem> items)
    {
        _items = items;
        Redraw(force: true);
    }

    /// <summary>Vuelve a crear el panel si el Explorador se reinició y lo reubica si la bandeja cambió de tamaño.</summary>
    private bool EnsureAttached()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;

        if (Handle == IntPtr.Zero || taskbar != _taskbar || !IsWindow(Handle))
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
            _taskbar = taskbar;
            _lastBounds = Rectangle.Empty;
            CreateHandle(new CreateParams
            {
                Parent = taskbar,
                Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW,
            });
        }
        Redraw(force: false);
        return Handle != IntPtr.Zero;
    }

    private void Redraw(bool force)
    {
        if (Handle == IntPtr.Zero) return;

        GetWindowRect(_taskbar, out var tb);
        var tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int trayLeft = tb.Right;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tr)) trayLeft = tr.Left;

        float scale = GetDpiForWindow(_taskbar) / 96f;
        if (scale <= 0) scale = 1;
        int height = tb.Bottom - tb.Top;

        using var bmp = Render(height, scale, out int width);
        var bounds = new Rectangle(trayLeft - tb.Left - width, 0, width, height);
        if (!force && bounds == _lastBounds) return;
        _lastBounds = bounds;

        SetWindowPos(Handle, HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Present(bmp);
    }

    private Bitmap Render(int height, float scale, out int width)
    {
        bool light = IsLightTaskbar();
        var textColorDefault = light ? Color.Black : Color.White;

        using var glyphFont = new Font("Segoe Fluent Icons", 16 * scale, GraphicsUnit.Pixel);
        using var textFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        using var boltFont = new Font("Segoe Fluent Icons", 12 * scale, GraphicsUnit.Pixel);
        using var measureBmp = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measureBmp);
        var fmt = StringFormat.GenericTypographic;

        var layout = new List<(WidgetItem item, float glyphW, float textW)>();
        float total = Margin * scale;
        foreach (var it in _items)
        {
            float gw = mg.MeasureString(Glyph(it.Kind), glyphFont, 1000, fmt).Width;
            float tw = mg.MeasureString(it.Text, textFont, 1000, fmt).Width;
            if (it.Charging) tw += 2 * scale + mg.MeasureString(Bolt, boltFont, 1000, fmt).Width;
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
        using (var bg = new SolidBrush(_hover ? Color.FromArgb(light ? 25 : 30, textColorDefault) : Color.FromArgb(1, 0, 0, 0)))
        using (var path = Rounded(new RectangleF(0, pad, width, height - 2 * pad), 4 * scale))
            g.FillPath(bg, path);

        float x = Margin * scale;
        foreach (var (it, gw, tw) in layout)
        {
            var color = it.Color.IsEmpty ? textColorDefault : it.Color;
            using var brush = new SolidBrush(color);
            float gh = glyphFont.Height, th = textFont.Height;
            g.DrawString(Glyph(it.Kind), glyphFont, brush, x, (height - gh) / 2f + 1 * scale, fmt);
            x += gw + 5 * scale;
            g.DrawString(it.Text, textFont, brush, x, (height - th) / 2f, fmt);
            if (it.Charging)
            {
                // Rayo de "cargando" justo después del porcentaje.
                float textOnly = g.MeasureString(it.Text, textFont, 1000, fmt).Width;
                g.DrawString(Bolt, boltFont, brush, x + textOnly + 2 * scale, (height - boltFont.Height) / 2f + 1 * scale, fmt);
            }
            x += tw + Gap * scale;
        }
        return bmp;
    }

    private static string Glyph(DeviceKind k) => k == DeviceKind.Mouse ? GlyphMouse : GlyphHeadset;

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

    private void Present(Bitmap bmp)
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
            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _tracking = TrackMouseEvent(ref tme);
                    _hover = true;
                    Redraw(force: true);
                }
                break;
            case WM_MOUSELEAVE:
                _tracking = false;
                _hover = false;
                Redraw(force: true);
                break;
            case WM_LBUTTONUP:
                _onClick();
                break;
            case WM_RBUTTONUP:
                SetForegroundWindow(Handle);
                _menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
                break;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        _watch.Stop();
        _watch.Dispose();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    // --- Win32 ---
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSELEAVE = 0x02A3;
    private const uint TME_LEAVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
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
