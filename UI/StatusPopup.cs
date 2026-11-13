using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using LagLens.Core;
using LagLens.Diagnosis;

namespace LagLens.UI;

/// <summary>无边框浮层：一句话结论 + 三个指标 + 60 秒曲线 + Top5 进程。</summary>
public sealed class StatusPopup : Form
{
    private const int BaseWidth = 420;
    private const int BaseHeight = 586;

    private const int IncidentTop = 432;
    private const int IncidentRowHeight = 38;

    private static readonly RectangleF CloseRect = new(BaseWidth - 42, 10, 30, 30);
    private static readonly RectangleF BannerRect = new(14, 50, BaseWidth - 28, 76);
    private static readonly RectangleF ChartRect = new(14, 208, BaseWidth - 28, 64);
    private static readonly RectangleF ResmonRect = new(BaseWidth - 122, 552, 108, 22);

    private static readonly Font TitleFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    private static readonly Font BigFont = new("Microsoft YaHei UI", 14.5f, FontStyle.Bold);
    private static readonly Font ValueFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 8.8f);
    private static readonly Font SmallFont = new("Microsoft YaHei UI", 8f);
    private static readonly Font TinyFont = new("Microsoft YaHei UI", 7.5f);
    private static readonly Font NameFont = new("Microsoft YaHei UI", 9.2f, FontStyle.Bold);
    private static readonly Font MetricFont = new("Microsoft YaHei UI", 8f, FontStyle.Bold);

    private static readonly StringFormat LeftFmt = new(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
    private static readonly StringFormat RightFmt = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        Alignment = StringAlignment.Far,
    };
    private static readonly StringFormat Wrap = new(StringFormatFlags.LineLimit) { Trimming = StringTrimming.EllipsisWord };

    private readonly Sampler _sampler;
    private readonly System.Windows.Forms.Timer _timer;
    private float _scale = 1f;
    private DateTime _lastPaintAt = DateTime.UtcNow;
    private bool _hoverClose;
    private bool _hoverResmon;
    private int _hoverIncident = -1;
    private readonly List<(IncidentRecord Record, RectangleF Rect)> _incidentRects = new();
    private string? _notice;
    private DateTime _noticeUntil;

    /// <summary>上次隐藏的时刻，用来区分“点击托盘自己关掉”和“点了别处失焦关掉”。</summary>
    public DateTime LastHiddenAt { get; private set; } = DateTime.MinValue;

    public StatusPopup(Sampler sampler)
    {
        _sampler = sampler;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        KeyPreview = true;
        Text = "卡顿侦探";
        AutoScaleMode = AutoScaleMode.None;

        ApplyScale();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) =>
        {
            if (Visible)
            {
                Invalidate();
            }
        };
    }

    private int Scale(int value) => (int)Math.Round(value * _scale);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyScale();
    }

    private void ApplyScale()
    {
        float scale = DeviceDpi / 96f;
        _scale = scale > 0.1f ? scale : 1f;
        ClientSize = new Size(Scale(BaseWidth), Scale(BaseHeight));

        using GraphicsPath path = Theme.RoundRect(
            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
            14 * _scale);
        Region = new Region(path);
    }

    public void ShowNearTray()
    {
        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle area = screen.WorkingArea;
        Location = new Point(
            area.Right - Width - Scale(12),
            area.Bottom - Height - Scale(12));

        // 打开面板 = 已读，托盘图标随之恢复常态。
        _sampler.Store.MarkSeen();

        Show();
        BringToFront();
        Activate();
        _timer.Start();
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        // 如果刚才我们自己被冻住了（系统卡死），这次失焦不是用户点走了，保留面板给用户看。
        if ((DateTime.UtcNow - _lastPaintAt).TotalSeconds > 1.5)
        {
            return;
        }

        Hide();
        _timer.Stop();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            LastHiddenAt = DateTime.UtcNow;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            _timer.Stop();
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        PointF p = ToBase(e.Location);
        bool overClose = CloseRect.Contains(p);
        bool overResmon = ResmonRect.Contains(p);
        int overIncident = HitIncident(p);

        if (overClose != _hoverClose || overResmon != _hoverResmon || overIncident != _hoverIncident)
        {
            _hoverClose = overClose;
            _hoverResmon = overResmon;
            _hoverIncident = overIncident;
            Cursor = overClose || overResmon || overIncident >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverClose = _hoverResmon = false;
        _hoverIncident = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        PointF p = ToBase(e.Location);
        if (CloseRect.Contains(p))
        {
            Hide();
            _timer.Stop();
            return;
        }

        if (ResmonRect.Contains(p))
        {
            OpenResourceMonitor();
            return;
        }

        int incident = HitIncident(p);
        if (incident >= 0)
        {
            OpenIncidentLocation(_incidentRects[incident].Record);
            return;
        }

        if (e.Button == MouseButtons.Left && p.Y < 44)
        {
            Native.User32.ReleaseCapture();
            Native.User32.SendMessage(Handle, 0xA1, 0x2, IntPtr.Zero);
        }
    }

    private PointF ToBase(Point p) => new(p.X / _scale, p.Y / _scale);

    private int HitIncident(PointF p)
    {
        for (int i = 0; i < _incidentRects.Count; i++)
        {
            if (_incidentRects[i].Rect.Contains(p))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>把面板自身渲染成 PNG（用于文档截图，不依赖屏幕焦点）。</summary>
    public void SaveImage(string path)
    {
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            using GraphicsPath clip = Theme.RoundRect(
                new RectangleF(0, 0, bmp.Width, bmp.Height),
                14 * _scale);
            g.SetClip(clip, CombineMode.Exclude);
            g.Clear(Color.Transparent);
        }

        bmp.Save(path, ImageFormat.Png);
    }

    private static void OpenResourceMonitor()
    {
        try
        {
            Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不影响主流程。
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = _scale == 1f ? TextRenderingHint.ClearTypeGridFit : TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (_scale != 1f)
        {
            g.ScaleTransform(_scale, _scale);
        }

        Snapshot? s = _sampler.Latest;
        _lastPaintAt = DateTime.UtcNow;
        _incidentRects.Clear();
        IReadOnlyList<Snapshot> history = _sampler.History();
        FreezeEvent? freeze = _sampler.LastFreeze();

        using (var bg = new SolidBrush(Theme.Bg))
        {
            Theme.FillRound(g, bg, new RectangleF(0, 0, BaseWidth, BaseHeight), 14);
        }

        using (var border = new Pen(Theme.Border))
        {
            Theme.DrawRound(g, border, new RectangleF(0.5f, 0.5f, BaseWidth - 1, BaseHeight - 1), 14);
        }

        DrawHeader(g, s);

        if (s is null)
        {
            DrawText(g, "正在采集第一秒数据…", BodyFont, Theme.TextSecondary, new RectangleF(20, 60, 380, 24), LeftFmt);
            return;
        }

        Verdict verdict = Diagnoser.Evaluate(s);
        bool freezeAsHeadline = false;

        if (freeze is not null
            && (DateTime.Now - freeze.At).TotalMinutes < 10
            && verdict.Severity == Severity.Ok)
        {
            string culpritText = freeze.Culprit == "未知"
                ? "系统当时整体无响应，没抓到具体进程"
                : $"系统无响应 · 之前是 {freeze.Culprit} 在占{freeze.Resource}";

            verdict = new Verdict(
                Bottleneck.None,
                Severity.Warn,
                $"刚才卡了 {freeze.Seconds:0.#} 秒",
                $"{freeze.At:HH:mm:ss} {culpritText}");
            freezeAsHeadline = true;
        }
        DrawBanner(g, verdict);
        DrawChips(g, s);
        DrawChart(g, history);
        DrawRows(g, s, verdict);
        DrawIncidents(g);
        DrawFooter(g, freezeAsHeadline, s);
    }

    private void DrawHeader(Graphics g, Snapshot? s)
    {
        Color dotColor = s is null
            ? Theme.TextDim
            : Theme.forSeverity(Diagnoser.Evaluate(s).Severity);

        using (var dot = new SolidBrush(dotColor))
        {
            g.FillEllipse(dot, 18, 21, 8, 8);
        }

        DrawText(g, "卡顿侦探", TitleFont, Theme.TextPrimary, new RectangleF(32, 14, 120, 24), LeftFmt);

        DrawText(g, "×", BigFont, _hoverClose ? Theme.TextPrimary : Theme.TextSecondary,
            new RectangleF(CloseRect.X, CloseRect.Y - 2, CloseRect.Width, CloseRect.Height),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    private void DrawBanner(Graphics g, Verdict verdict)
    {
        Color color = Theme.forSeverity(verdict.Severity);

        using (var fill = new SolidBrush(Color.FromArgb(30, color)))
        {
            Theme.FillRound(g, fill, BannerRect, 9);
        }

        using (var stripe = new SolidBrush(color))
        {
            using GraphicsPath clip = Theme.RoundRect(BannerRect, 9);
            GraphicsState state = g.Save();
            g.SetClip(clip);
            g.FillRectangle(stripe, BannerRect.X, BannerRect.Y, 3.5f, BannerRect.Height);
            g.Restore(state);
        }

        DrawText(g, verdict.Title, BigFont, color,
            new RectangleF(BannerRect.X + 16, BannerRect.Y + 8, BannerRect.Width - 28, 26), LeftFmt);

        DrawText(g, verdict.Detail, BodyFont, Theme.TextSecondary,
            new RectangleF(BannerRect.X + 16, BannerRect.Y + 38, BannerRect.Width - 30, 34), Wrap);
    }

    private void DrawChips(Graphics g, Snapshot s)
    {
        const float gap = 8;
        float width = (BaseWidth - 28 - (gap * 2)) / 3f;

        DrawChip(g, 14, 134, width, "CPU", $"{s.CpuTotal:0}%", s.CpuTotal / 100d, Theme.Accent);
        DrawChip(g, 14 + width + gap, 134, width, "硬盘", $"{s.DiskBusy:0}%", s.DiskBusy / 100d, Theme.Warn);

        double usedRatio = 1 - (s.AvailableMB / 1024d / Math.Max(1d, TotalMemoryGB()));
        DrawChip(g, 14 + ((width + gap) * 2), 134, width, "内存", $"{s.AvailableMB / 1024d:0.0}G 可用",
            Math.Clamp(usedRatio, 0, 1), Theme.Ok);
    }

    private double TotalMemoryGB()
    {
        var status = new Native.MemoryStatus();
        return status.TotalPhysicalGB;
    }

    private void DrawChip(Graphics g, float x, float y, float w, string label, string value, double ratio, Color color)
    {
        var rect = new RectangleF(x, y, w, 48);
        using (var fill = new SolidBrush(Theme.Card))
        {
            Theme.FillRound(g, fill, rect, 8);
        }

        DrawText(g, label, TinyFont, Theme.TextDim, new RectangleF(x + 10, y + 5, w - 20, 14), LeftFmt);
        DrawText(g, value, ValueFont, Theme.TextPrimary, new RectangleF(x + 10, y + 17, w - 20, 22), LeftFmt);

        var track = new RectangleF(x + 10, y + 40, w - 20, 3);
        using (var back = new SolidBrush(Color.FromArgb(255, 52, 56, 64)))
        {
            Theme.FillRound(g, back, track, 1.5f);
        }

        float filled = (float)(track.Width * Math.Clamp(ratio, 0, 1));
        if (filled > 1)
        {
            using var bar = new SolidBrush(color);
            Theme.FillRound(g, bar, new RectangleF(track.X, track.Y, filled, track.Height), 1.5f);
        }
    }

    private void DrawChart(Graphics g, IReadOnlyList<Snapshot> history)
    {
        DrawText(g, "最近 60 秒", SmallFont, Theme.TextSecondary, new RectangleF(14, 190, 100, 16), LeftFmt);

        using (var legend = new SolidBrush(Theme.Accent))
        {
            g.FillEllipse(legend, BaseWidth - 92, 195, 6, 6);
        }

        DrawText(g, "CPU", TinyFont, Theme.TextDim, new RectangleF(BaseWidth - 82, 190, 30, 14), LeftFmt);

        using (var legend = new SolidBrush(Theme.Warn))
        {
            g.FillEllipse(legend, BaseWidth - 48, 195, 6, 6);
        }

        DrawText(g, "硬盘", TinyFont, Theme.TextDim, new RectangleF(BaseWidth - 38, 190, 30, 14), LeftFmt);

        using (var fill = new SolidBrush(Theme.CardAlt))
        {
            Theme.FillRound(g, fill, ChartRect, 7);
        }

        for (int i = 1; i <= 3; i++)
        {
            float y = ChartRect.Bottom - (ChartRect.Height * i / 4f);
            using var grid = new Pen(Color.FromArgb(38, 255, 255, 255));
            g.DrawLine(grid, ChartRect.X + 1, y, ChartRect.Right - 1, y);
        }

        if (history.Count < 2)
        {
            DrawText(g, "再等几秒就有曲线了", TinyFont, Theme.TextDim,
                new RectangleF(ChartRect.X, ChartRect.Y + 24, ChartRect.Width, 16),
                new StringFormat { Alignment = StringAlignment.Center });
            return;
        }

        DrawSeries(g, history.Select(x => x.CpuTotal).ToArray(), Theme.Accent, fillArea: true);
        DrawSeries(g, history.Select(x => x.DiskBusy).ToArray(), Theme.Warn, fillArea: false);
    }

    private void DrawSeries(Graphics g, double[] values, Color color, bool fillArea)
    {
        if (values.Length < 2)
        {
            return;
        }

        float stepX = ChartRect.Width / (values.Length - 1);
        var points = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double v = Math.Clamp(values[i], 0, 100) / 100d;
            points[i] = new PointF(
                ChartRect.X + (i * stepX),
                ChartRect.Bottom - (float)(ChartRect.Height * v) - 1);
        }

        if (fillArea)
        {
            var area = new PointF[points.Length + 2];
            Array.Copy(points, area, points.Length);
            area[^2] = new PointF(ChartRect.Right, ChartRect.Bottom);
            area[^1] = new PointF(ChartRect.X, ChartRect.Bottom);

            using var gradient = new LinearGradientBrush(
                new PointF(0, ChartRect.Top),
                new PointF(0, ChartRect.Bottom),
                Color.FromArgb(70, color),
                Color.FromArgb(0, color));
            using GraphicsPath clip = Theme.RoundRect(ChartRect, 7);
            GraphicsState state = g.Save();
            g.SetClip(clip);
            g.FillPolygon(gradient, area);
            g.Restore(state);
        }

        using var pen = new Pen(color, 1.8f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }

    private void DrawRows(Graphics g, Snapshot s, Verdict verdict)
    {
        DrawText(g, "占用最高的应用", SmallFont, Theme.TextSecondary, new RectangleF(14, 278, 140, 16), LeftFmt);
        DrawText(g, "CPU", TinyFont, Theme.TextDim, new RectangleF(176, 280, 60, 14), RightFmt);
        DrawText(g, "磁盘", TinyFont, Theme.TextDim, new RectangleF(246, 280, 84, 14), RightFmt);
        DrawText(g, "内存", TinyFont, Theme.TextDim, new RectangleF(340, 280, 64, 14), RightFmt);

        IEnumerable<ProcessGroup> ordered = verdict.Kind switch
        {
            Bottleneck.Disk => s.Groups.OrderByDescending(x => x.DiskScore),
            Bottleneck.Cpu => s.Groups.OrderByDescending(x => x.CpuPercent),
            Bottleneck.Memory => s.Groups.OrderByDescending(x => x.MemoryMB),
            _ => s.Groups.OrderByDescending(x => x.CpuPercent + (x.DiskScore * 1.5)),
        };

        ProcessGroup[] top = ordered.Take(5).ToArray();
        if (top.Length == 0)
        {
            return;
        }

        for (int i = 0; i < top.Length; i++)
        {
            ProcessGroup item = top[i];
            float y = 298 + (i * 22);
            var row = new RectangleF(14, y, BaseWidth - 28, 21);

            if (i % 2 == 0)
            {
                using var alt = new SolidBrush(Color.FromArgb(255, 27, 29, 34));
                Theme.FillRound(g, alt, row, 5);
            }

            Color marker = item.CpuPercent >= item.DiskScore * 2 ? Theme.Accent : Theme.Warn;
            using (var bar = new SolidBrush(Color.FromArgb(200, marker)))
            {
                g.FillRectangle(bar, row.X + 3, row.Y + 5, 2.5f, 11);
            }

            DrawText(g, item.Name, BodyFont, Theme.TextPrimary,
                new RectangleF(14, y + 2, 120, 17), LeftFmt);

            if (item.Instances > 1)
            {
                DrawText(g, $"×{item.Instances}", TinyFont, Theme.TextDim,
                    new RectangleF(138, y + 4, 30, 15), LeftFmt);
            }

            string cpuText = item.CpuPercent >= 0.5 ? $"{item.CpuPercent:0}%" : "—";
            DrawText(g, cpuText, BodyFont, item.CpuPercent >= 30 ? Theme.Warn : Theme.TextSecondary,
                new RectangleF(176, y + 2, 60, 17), RightFmt);

            string diskText = item.DiskScore >= 0.05
                ? (item.DiskMBps >= 0.1 ? $"{item.DiskMBps:0.0} M/s" : $"{item.Iops:0} IOPS")
                : "—";
            DrawText(g, diskText, BodyFont, Theme.TextSecondary,
                new RectangleF(246, y + 2, 84, 17), RightFmt);

            string memText = item.MemoryMB >= 1 ? $"{item.MemoryMB:0} M" : "—";
            DrawText(g, memText, BodyFont, Theme.TextSecondary,
                new RectangleF(340, y + 2, 64, 17), RightFmt);
        }

    }

    private void DrawIncidents(Graphics g)
    {
        DrawText(g, "最近卡顿", SmallFont, Theme.TextSecondary, new RectangleF(14, 410, 140, 16), LeftFmt);
        DrawText(g, "点击定位程序", TinyFont, Theme.TextDim,
            new RectangleF(BaseWidth - 128, 411, 114, 15), RightFmt);

        IReadOnlyList<IncidentRecord> items = _sampler.Store.Recent(3);
        if (items.Count == 0)
        {
            DrawText(g, "还没有记录到卡顿", TinyFont, Theme.TextDim,
                new RectangleF(16, IncidentTop + 8, 300, 16), LeftFmt);
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            IncidentRecord item = items[i];
            float y = IncidentTop + (i * IncidentRowHeight);
            var row = new RectangleF(14, y, BaseWidth - 28, IncidentRowHeight - 4);
            _incidentRects.Add((item, row));

            bool hover = _hoverIncident == i;
            using (var fill = new SolidBrush(hover ? Theme.Card : Theme.CardAlt))
            {
                Theme.FillRound(g, fill, row, 6);
            }

            Color accent = item.Resource switch
            {
                "CPU" => Theme.Accent,
                "磁盘" => Theme.Warn,
                "内存" => Theme.Ok,
                _ => Theme.TextDim,
            };

            using (var bar = new SolidBrush(Color.FromArgb(hover ? 255 : 190, accent)))
            {
                g.FillRectangle(bar, row.X + 4, row.Y + 8, 3, row.Height - 16);
            }

            string name = string.IsNullOrWhiteSpace(item.Culprit) || item.Culprit == "未知"
                ? "未知进程"
                : item.Culprit;
            DrawText(g, name, NameFont, Theme.TextPrimary, new RectangleF(16, y + 2, 236, 18), LeftFmt);

            string duration = item.Kind == "冻结" ? $"卡了 {item.Seconds:0.#}s" : $"高压 {item.Seconds:0}s";
            string metric = item.Resource switch
            {
                "CPU" => $"{item.Peak:0}% CPU",
                "磁盘" => $"{item.Peak:0.#} MB/s",
                "内存" => $"{item.Peak:0} MB",
                _ => string.Empty,
            };
            string right = metric.Length > 0 ? $"{duration} · {metric}" : duration;
            DrawText(g, right, MetricFont, accent, new RectangleF(252, y + 3, 152, 17), RightFmt);

            string sub = string.IsNullOrWhiteSpace(item.CulpritPath)
                ? $"{item.At:MM-dd HH:mm} · 路径未记录，可按进程名查找"
                : $"{item.At:MM-dd HH:mm} · {FitPath(item.CulpritPath)}";
            DrawText(g, sub, TinyFont, Theme.TextDim, new RectangleF(16, y + 20, BaseWidth - 30, 15), LeftFmt);
        }
    }

    private void DrawFooter(Graphics g, bool freezeAsHeadline, Snapshot s)
    {
        bool showNotice = _notice is not null && DateTime.UtcNow < _noticeUntil;
        string text = showNotice
            ? _notice!
            : freezeAsHeadline
                ? $"现在已恢复正常 · CPU {s.CpuTotal:0}% · 磁盘 {s.DiskBusy:0}%"
                : "托盘图标单击开关 · Esc 关闭";

        DrawText(g, text, TinyFont, showNotice ? Theme.Warn : Theme.TextDim,
            new RectangleF(16, 558, 282, 18), LeftFmt);

        DrawText(g, "资源监视器 →", SmallFont, _hoverResmon ? Theme.TextPrimary : Theme.Accent,
            ResmonRect, RightFmt);
    }

    private static string FitPath(string path)
    {
        const int max = 56;
        return path.Length <= max ? path : "…" + path[^(max - 1)..];
    }

    private void OpenIncidentLocation(IncidentRecord item)
    {
        string? path = ResolveExecutablePath(item);
        if (path is null)
        {
            ShowNotice($"没找到 {item.Culprit} 的程序位置，可能已经退出");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            ShowNotice($"已定位到 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowNotice($"打开目录失败：{ex.Message}");
        }
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        _noticeUntil = DateTime.UtcNow.AddSeconds(4);
        Invalidate();
    }

    private static string? ResolveExecutablePath(IncidentRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.CulpritPath) && File.Exists(item.CulpritPath))
        {
            return item.CulpritPath;
        }

        if (string.IsNullOrWhiteSpace(item.Culprit) || item.Culprit == "未知")
        {
            return null;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(item.Culprit);
        }
        catch
        {
            return null;
        }

        try
        {
            foreach (Process p in processes)
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // 受保护进程读不到路径，换下一个同名进程。
                }
            }
        }
        finally
        {
            foreach (Process p in processes)
            {
                p.Dispose();
            }
        }

        return null;
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringFormat format)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

