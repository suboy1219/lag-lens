using System.Diagnostics;
using LagLens.Core;
using LagLens.Diagnosis;

namespace LagLens.UI;

public sealed class TrayContext : ApplicationContext
{
    private readonly Sampler _sampler;
    private readonly StatusPopup _popup;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _startupItem;

    public TrayContext(bool showOnStart = false, string? shotPath = null)
    {
        _sampler = new Sampler();
        _sampler.Start();

        _popup = new StatusPopup(_sampler);

        _startupItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        _startupItem.Click += OnStartupToggle;

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create(Theme.Ok),
            Text = "卡顿侦探 · 正在采集",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += OnTrayClick;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTick;
        _timer.Start();

        if (showOnStart)
        {
            var once = new System.Windows.Forms.Timer { Interval = 1200 };
            once.Tick += (_, _) =>
            {
                once.Stop();
                once.Dispose();
                ShowPopup();
            };
            once.Start();
        }

        if (shotPath is not null)
        {
            var shot = new System.Windows.Forms.Timer { Interval = 15000 };
            shot.Tick += (_, _) =>
            {
                shot.Stop();
                shot.Dispose();
                _popup.ShowNearTray();
                _popup.Refresh();
                _popup.SaveImage(shotPath);
                ExitApp();
            };
            shot.Start();
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("打开面板", null, (_, _) => ShowPopup()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("打开资源监视器", null, (_, _) => OpenResmon()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        return menu;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            TogglePopup();
        }
    }

    private void TogglePopup()
    {
        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }

        if ((DateTime.UtcNow - _popup.LastHiddenAt).TotalMilliseconds < 250)
        {
            return;
        }

        ShowPopup();
    }

    private void ShowPopup() => _popup.ShowNearTray();

    private void OnStartupToggle(object? sender, EventArgs e)
    {
        try
        {
            AutoStart.Set(_startupItem.Checked);
        }
        catch (Exception ex)
        {
            _startupItem.Checked = AutoStart.IsEnabled();
            MessageBox.Show($"设置开机启动失败：{ex.Message}", "卡顿侦探", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Snapshot? snapshot = _sampler.Latest;
        if (snapshot is null)
        {
            return;
        }

        Verdict verdict = Diagnoser.Evaluate(snapshot);
        int unread = _sampler.Store.UnreadCount;

        // 颜色始终反映当前负载；历史未读记录只在提示中显示，避免恢复后一直红色。
        _tray.Icon = IconFactory.Create(Theme.forSeverity(verdict.Severity));

        string tip = unread > 0
            ? $"卡顿侦探 · {unread} 条卡顿记录未查看 · CPU {snapshot.CpuTotal:0}% · 硬盘 {snapshot.DiskBusy:0}%"
            : $"卡顿侦探 · CPU {snapshot.CpuTotal:0}% · 硬盘 {snapshot.DiskBusy:0}% · 内存 {snapshot.AvailableMB / 1024d:0.0}G 可用";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private static void OpenResmon()
    {
        try
        {
            Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        _sampler.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _popup.Dispose();
            _sampler.Dispose();
        }

        base.Dispose(disposing);
    }
}
