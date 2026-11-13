using System.Text;
using System.Threading;
using LagLens.Core;
using LagLens.Diagnosis;
using LagLens.UI;

namespace LagLens;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            RunProbe();
            return;
        }

        using var mutex = new Mutex(true, @"Local\LagLens.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "卡顿侦探已经在运行了，看一下任务栏右下角的托盘图标。",
                "卡顿侦探",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            bool showOnStart = args.Contains("--show", StringComparer.OrdinalIgnoreCase);
            string? shotPath = ReadOption(args, "--shot");
            using var context = new TrayContext(showOnStart || shotPath is not null, shotPath);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "卡顿侦探启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>自检模式：采 8 秒，把原始数据写到文件，用来确认计数器在本机取值正常。</summary>
    private static void RunProbe()
    {
        var report = new StringBuilder();
        using var sampler = new Sampler();
        sampler.Start();

        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(1000);
            Snapshot? s = sampler.Latest;
            if (s is null)
            {
                continue;
            }

            Verdict v = Diagnoser.Evaluate(s);
            report.AppendLine(
                $"[{i + 1}s] CPU {s.CpuTotal:0.0}% | 磁盘 {s.DiskBusy:0.0}% (队列 {s.DiskQueue:0.00}, 延迟 {s.DiskLatencyMs:0.0}ms) | " +
                $"可用 {s.AvailableMB} MB | 提交 {s.CommitRatio:P0} | 硬缺页 {s.PagesPerSec:0}/s | 冻结 {s.FreezeSeconds:0.0}s | " +
                $"判定 {v.Kind}/{v.Severity} {v.Title}");
        }

        Snapshot? last = sampler.Latest;
        if (last is not null)
        {
            report.AppendLine();
            report.AppendLine("物理磁盘:");
            foreach (DiskSample disk in last.Disks.OrderByDescending(d => d.BusyPercent))
            {
                report.AppendLine($"  {disk.Name,-10} 忙 {disk.BusyPercent:0.0}%  队列 {disk.Queue:0.00}  延迟 {disk.LatencyMs:0.0}ms");
            }

            report.AppendLine();
            report.AppendLine("进程 Top10（按 CPU+磁盘压力）:");
            foreach (ProcessGroup group in last.Groups
                .OrderByDescending(g => g.CpuPercent + (g.DiskScore * 1.5))
                .Take(10))
            {
                report.AppendLine(
                    $"  {group.Name,-28} CPU {group.CpuPercent,6:0.0}%  磁盘 {group.DiskMBps,7:0.00} MB/s  " +
                    $"{group.Iops,7:0} IOPS  内存 {group.MemoryMB,7:0} MB  x{group.Instances}");
            }
        }

        string path = Path.Combine(Path.GetTempPath(), "laglens-probe.txt");
        File.WriteAllText(path, report.ToString(), Encoding.UTF8);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }
}
