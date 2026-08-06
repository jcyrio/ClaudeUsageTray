using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Draws the session percentage straight into the tray icon, so the number is
/// readable without opening anything. Icons created from a GDI bitmap own an
/// unmanaged handle, so callers must dispose what they get back.
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(int pct)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var text = pct >= 100 ? "!" : pct.ToString();
            float emSize = text.Length >= 3 ? 15f : text.Length == 2 ? 21f : 24f;

            using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ColorFor(pct));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
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

    static Color ColorFor(int pct) => pct switch
    {
        >= 90 => Color.FromArgb(255, 107, 107),
        >= 75 => Color.FromArgb(233, 168, 96),
        _ => Color.FromArgb(240, 238, 234),
    };
}
