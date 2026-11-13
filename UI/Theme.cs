using System.Drawing.Drawing2D;

namespace LagLens.UI;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(23, 24, 28);
    public static readonly Color Card = Color.FromArgb(32, 35, 41);
    public static readonly Color CardAlt = Color.FromArgb(28, 30, 35);
    public static readonly Color Border = Color.FromArgb(44, 47, 54);
    public static readonly Color TextPrimary = Color.FromArgb(232, 234, 237);
    public static readonly Color TextSecondary = Color.FromArgb(150, 156, 165);
    public static readonly Color TextDim = Color.FromArgb(112, 118, 128);

    public static readonly Color Accent = Color.FromArgb(91, 157, 255);
    public static readonly Color Warn = Color.FromArgb(255, 159, 69);
    public static readonly Color Danger = Color.FromArgb(255, 92, 92);
    public static readonly Color Ok = Color.FromArgb(76, 208, 125);

    public static Color forSeverity(Diagnosis.Severity severity) => severity switch
    {
        Diagnosis.Severity.Bad => Danger,
        Diagnosis.Severity.Warn => Warn,
        _ => Ok,
    };

    public static GraphicsPath RoundRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0 || rect.Width <= d || rect.Height <= d)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using GraphicsPath path = RoundRect(rect, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRound(Graphics g, Pen pen, RectangleF rect, float radius)
    {
        using GraphicsPath path = RoundRect(rect, radius);
        g.DrawPath(pen, path);
    }
}
