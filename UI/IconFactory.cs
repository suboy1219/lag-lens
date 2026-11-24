using System.Drawing.Drawing2D;
using LagLens.Native;

namespace LagLens.UI;

internal static class IconFactory
{
    private static readonly Dictionary<Color, Icon> Cache = new();

    public static Icon Create(Color color)
    {
        if (Cache.TryGetValue(color, out Icon? cached))
        {
            return cached;
        }

        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // A solid bolt stays recognizable at tray size, including in grayscale.
            // Keep the load color while distinguishing it from EyeGuard's ring.
            using var bolt = new SolidBrush(color);
            g.FillPolygon(bolt,
            [
                new PointF(18f, 2f),
                new PointF(5f, 18f),
                new PointF(14f, 18f),
                new PointF(11f, 30f),
                new PointF(27f, 12f),
                new PointF(18f, 12f),
            ]);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            var icon = (Icon)temp.Clone();
            Cache[color] = icon;
            return icon;
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
