using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace LagLens;

internal static class HelpMenu
{
    private static bool _checking;

    public static Forms.ToolStripMenuItem Create()
    {
        var help = new Forms.ToolStripMenuItem("帮助");
        var check = new Forms.ToolStripMenuItem("检测更新");
        help.DropDownOpening += (_, _) =>
        {
            if (help.Owner is Forms.ToolStrip owner)
            {
                help.DropDown.Renderer = owner.Renderer;
                help.DropDown.BackColor = owner.BackColor;
                help.DropDown.ForeColor = owner.ForeColor;
                foreach (Forms.ToolStripItem item in help.DropDownItems) item.ForeColor = owner.ForeColor;
            }
            check.Enabled = !_checking;
            check.Text = _checking ? "正在检测更新…" : "检测更新";
        };
        check.Click += async (_, _) =>
        {
            if (_checking) return;
            _checking = true;
            check.Enabled = false;
            check.Text = "正在检测更新…";
            try
            {
                var result = await new UpdateService().CheckAsync();
                if (result?.IsUpdateAvailable == true)
                {
                    if (Forms.MessageBox.Show(
                        $"发现新版本 {result.TagName}（当前 {result.CurrentVersion.ToString(3)}）。\n\n是否前往 GitHub 发布页下载？",
                        "检测更新", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Information) == Forms.DialogResult.Yes)
                        Open(result.ReleaseUrl);
                }
                else
                {
                    Forms.MessageBox.Show(result == null ? "暂时没有可用的正式发布版。" :
                        $"当前已是最新版本（{result.CurrentVersion.ToString(3)}）。",
                        "检测更新", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException
                or OperationCanceledException or System.Text.Json.JsonException)
            {
                Forms.MessageBox.Show("无法检测更新，请检查网络连接后重试。GitHub 暂时不可用或请求次数受限时也可能出现此提示。",
                    "检测更新", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
            finally
            {
                _checking = false;
                if (!check.IsDisposed)
                {
                    check.Enabled = true;
                    check.Text = "检测更新";
                }
            }
        };
        help.DropDownItems.Add("使用说明", null, (_, _) => Open(UpdateService.ProjectUrl + "#readme"));
        help.DropDownItems.Add(check);
        help.DropDownItems.Add("关于", null, (_, _) => Forms.MessageBox.Show(
            $"LagLens {UpdateService.CurrentVersion.ToString(3)}\n\n{UpdateService.ProjectUrl}",
            "关于 LagLens", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information));
        return help;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Forms.MessageBox.Show($"无法打开浏览器，请手动访问：\n{url}", "LagLens",
                Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }
}
