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

            using var ringPen = new Pen(color, 4.5f);
            g.DrawEllipse(ringPen, 6.5f, 6.5f, 19f, 19f);

            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, 12f, 12f, 8f, 8f);
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
