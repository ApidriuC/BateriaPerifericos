using System.Drawing.Drawing2D;

namespace BateriaPerifericos;

public enum DeviceKind { Mouse, Headset }

/// <summary>
/// Dibuja el ícono de la bandeja a 16×16: silueta del dispositivo en verde/amarillo/rojo según el %
/// y el porcentaje con una fuente de píxeles 3×5 en la parte inferior.
/// </summary>
public static class IconRenderer
{
    public static readonly Color Green = Color.FromArgb(40, 200, 60);
    public static readonly Color Yellow = Color.FromArgb(245, 200, 0);
    public static readonly Color Red = Color.FromArgb(240, 60, 45);
    public static readonly Color Gray = Color.FromArgb(150, 150, 150);
    private static readonly Color Edge = Color.FromArgb(20, 20, 20);

    public static Color LevelColor(int pct) => pct > 50 ? Green : pct > 20 ? Yellow : Red;

    /// <param name="text">Texto inferior (p. ej. "55"); null para no mostrar número.</param>
    /// <param name="dimmed">Atenúa el color (dispositivo en reposo, última lectura).</param>
    public static Bitmap Render(DeviceKind kind, Color color, string? text, bool dimmed = false)
    {
        if (dimmed) color = Color.FromArgb((color.R + 2 * 120) / 3, (color.G + 2 * 120) / 3, (color.B + 2 * 120) / 3);

        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // Con número: la silueta ocupa la parte superior; sin número, todo el ícono.
        var area = text is null ? new RectangleF(0.5f, 0.5f, 15, 15) : new RectangleF(1.5f, 0.5f, 13, 9.5f);
        using (var path = Shape(kind, area))
        {
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
            using var pen = new Pen(Edge, 1f);
            g.DrawPath(pen, path);
        }
        if (kind == DeviceKind.Mouse) MouseButtons(g, area);

        if (text is not null)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            int w = text.Length * 4 - 1;
            int x = (16 - w) / 2;
            // Placa oscura detrás del número para que se lea en barra clara u oscura.
            using (var plate = new SolidBrush(Color.FromArgb(230, 20, 20, 20)))
                g.FillRectangle(plate, x - 1, 10, w + 2, 6);
            PixelText(g, text, x, 11, color);
        }
        return bmp;
    }

    private static GraphicsPath Shape(DeviceKind kind, RectangleF r)
    {
        var p = new GraphicsPath { FillMode = FillMode.Winding };
        if (kind == DeviceKind.Mouse)
        {
            float w = Math.Min(r.Width, r.Height * 0.72f), x = r.X + (r.Width - w) / 2;
            p.AddArc(x, r.Y, w, w, 180, 180);
            p.AddArc(x, r.Bottom - w, w, w, 0, 180);
            p.CloseFigure();
        }
        else
        {
            float cw = r.Width * 0.3f, ch = r.Height * 0.5f, top = r.Bottom - ch;
            p.AddRectangle(new RectangleF(r.X, top, cw, ch));
            p.AddRectangle(new RectangleF(r.Right - cw, top, cw, ch));
            var band = new GraphicsPath();
            float t = Math.Max(1.6f, r.Width * 0.17f);
            band.AddArc(r.X, r.Y, r.Width, r.Height * 1.1f, 180, 180);
            band.AddLine(r.Right, r.Y + r.Height * 0.55f, r.Right - t, r.Y + r.Height * 0.55f);
            band.AddArc(r.X + t, r.Y + t, r.Width - 2 * t, r.Height * 1.1f - 2 * t, 0, -180);
            band.CloseFigure();
            p.AddPath(band, false);
        }
        return p;
    }

    private static void MouseButtons(Graphics g, RectangleF r)
    {
        float w = Math.Min(r.Width, r.Height * 0.72f), x = r.X + (r.Width - w) / 2;
        float split = r.Y + r.Height * 0.42f;
        using var pen = new Pen(Edge, 1f);
        g.DrawLine(pen, x + w / 2, r.Y + 0.5f, x + w / 2, split);
        g.DrawLine(pen, x + 0.5f, split, x + w - 0.5f, split);
    }

    // Dígitos 3×5 (filas de arriba a abajo).
    private static readonly string[] Digits =
    {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111",
    };
    private const string Dash = "000000111000000";

    private static void PixelText(Graphics g, string text, int x, int y, Color c)
    {
        using var b = new SolidBrush(c);
        foreach (char ch in text)
        {
            string bits = char.IsDigit(ch) ? Digits[ch - '0'] : Dash;
            for (int i = 0; i < 15; i++)
                if (bits[i] == '1') g.FillRectangle(b, x + i % 3, y + i / 3, 1, 1);
            x += 4;
        }
    }
}
