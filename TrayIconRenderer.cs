using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Draws the session percentage straight into the tray icon, so the number is
/// readable without opening anything. Icons created from a GDI bitmap own an
/// unmanaged handle, so callers must dispose what they get back.
/// </summary>
public static class TrayIconRenderer
{
    /// Icons are drawn at 32px and let the shell scale down; that survives high-DPI
    /// taskbars better than drawing natively at 16px, where two digits have no room.
    const int Size = 32;

    /// Fraction of the canvas kept clear around the glyphs. Small on purpose -- the
    /// number should fill the icon, since it is competing with app logos in the tray.
    const float Margin = 0.04f;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(int pct)
    {
        var text = pct >= 100 ? "!" : pct.ToString();

        using var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var family = ResolveFamily();
            using var path = FitToCanvas(family, text);
            using var brush = new SolidBrush(ColorFor(pct));
            g.FillPath(brush, path);
        }

        // Icon.FromHandle does not take ownership, so clone into a managed icon and
        // release the GDI handle immediately rather than leaking one per refresh.
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Lays the text out at a nominal size, then scales it so the glyphs themselves --
    /// not the font's line box, which carries ascender and descender padding a digit
    /// never uses -- fill the icon. This is what makes one, two and three digit values
    /// all render as large as they can rather than sharing one conservative size.
    /// </summary>
    static GraphicsPath FitToCanvas(FontFamily family, string text)
    {
        const float nominal = 100f;
        var style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;

        var path = new GraphicsPath();
        path.AddString(text, family, (int)style, nominal, PointF.Empty, StringFormat.GenericTypographic);

        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;

        var inset = Size * Margin;
        var available = Size - inset * 2f;
        var scale = Math.Min(available / bounds.Width, available / bounds.Height);

        using var transform = new Matrix();
        transform.Scale(scale, scale, MatrixOrder.Append);
        transform.Translate(
            -bounds.X * scale + (Size - bounds.Width * scale) / 2f,
            -bounds.Y * scale + (Size - bounds.Height * scale) / 2f,
            MatrixOrder.Append);
        path.Transform(transform);

        return path;
    }

    /// Segoe UI is the Windows shell font, so the number matches the rest of the tray.
    static FontFamily ResolveFamily()
    {
        foreach (var name in new[] { "Segoe UI", "Arial" })
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { /* not installed */ }
        }
        return FontFamily.GenericSansSerif;
    }

    static Color ColorFor(int pct) => pct switch
    {
        >= 90 => Color.FromArgb(255, 107, 107),
        >= 75 => Color.FromArgb(233, 168, 96),
        _ => Color.FromArgb(240, 238, 234),
    };
}
