using LagLens.Core;

namespace LagLens.Diagnosis;

public enum Bottleneck
{
    None,
    Cpu,
    Disk,
    Memory,
}

public enum Severity
{
    Ok,
    Warn,
    Bad,
}

public sealed record Verdict(Bottleneck Kind, Severity Severity, string Title, string Detail);

public static class Diagnoser
{
    private const double CpuBad = 80;
    private const double DiskLatencyBad = 25;
    private const double DiskQueueBad = 4;
    private const double CommitBad = 0.95;
    private const double CommitWarn = 0.85;

    public static Verdict Evaluate(Snapshot s)
    {
        bool cpuBad = s.CpuTotal >= CpuBad;
        // 硬盘卡不卡看“等多久”，不看“忙不忙”：NVMe 100% 忙但延迟 1ms 时桌面是顺的。
        bool diskBad = s.DiskLatencyMs >= DiskLatencyBad || s.DiskQueue >= DiskQueueBad;
        long memBadMb = Math.Max(900, (long)(s.TotalMB * 0.04));
        bool memBad = s.AvailableMB < memBadMb || s.CommitRatio >= CommitBad;

        double cpuScore = s.CpuTotal / CpuBad;
        double diskScore = Math.Max(s.DiskLatencyMs / DiskLatencyBad, s.DiskQueue / DiskQueueBad);
        double memScore = Math.Max(
            memBadMb / Math.Max(1d, s.AvailableMB),
            s.CommitRatio / CommitBad);

        if (cpuBad || diskBad || memBad)
        {
            if (diskBad && diskScore >= cpuScore && diskScore >= memScore)
            {
                return new Verdict(Bottleneck.Disk, Severity.Bad, "硬盘扛不住了", DiskDetail(s));
            }

            if (cpuBad && cpuScore >= memScore)
            {
                return new Verdict(Bottleneck.Cpu, Severity.Bad, "CPU 被吃满了", CpuDetail(s));
            }

            if (memBad)
            {
                return new Verdict(Bottleneck.Memory, Severity.Bad, "内存不够用了", MemoryDetail(s));
            }
        }

        if (s.CpuTotal >= 55
            || s.DiskLatencyMs >= 10
            || s.DiskBusy >= 85
            || s.DiskQueue >= 2
            || s.CommitRatio >= CommitWarn)
        {
            if (s.CommitRatio >= CommitWarn && s.CommitRatio / CommitBad >= Math.Max(s.DiskLatencyMs / DiskLatencyBad, s.CpuTotal / CpuBad))
            {
                return new Verdict(Bottleneck.Memory, Severity.Warn, "内存有点紧", MemoryDetail(s));
            }

            Bottleneck kind = s.DiskLatencyMs >= 10 || s.DiskBusy >= 85 ? Bottleneck.Disk : Bottleneck.Cpu;
            string detail = kind == Bottleneck.Disk ? DiskDetail(s) : CpuDetail(s);
            return new Verdict(kind, Severity.Warn, kind == Bottleneck.Disk ? "硬盘有点忙" : "CPU 偏高", detail);
        }

        return new Verdict(
            Bottleneck.None,
            Severity.Ok,
            "当前挺顺畅",
            $"CPU {s.CpuTotal:0}% · 磁盘 {s.DiskBusy:0}% · 可用内存 {s.AvailableMB / 1024d:0.0} GB");
    }

    private static string DiskDetail(Snapshot s)
    {
        ProcessGroup? top = s.TopDisk();
        string culprit = top is null
            ? "没有明显的大户"
            : $"{top.Name} 占 {top.DiskMBps:0.0} MB/s、{top.Iops:0} IOPS";

        string disk = string.IsNullOrEmpty(s.DiskName) ? "磁盘" : shortName(s.DiskName);
        string latency = s.DiskLatencyMs > 0 ? $" · 延迟 {s.DiskLatencyMs:0}ms" : string.Empty;
        return $"{disk} {s.DiskBusy:0}% 忙{latency} · 队列 {s.DiskQueue:0.0} · {culprit}";
    }

    private static string CpuDetail(Snapshot s)
    {
        ProcessGroup? top = s.TopCpu();
        string culprit = top is null
            ? "没有明显的大户"
            : $"{top.Name} 占 {top.CpuPercent:0}%{(top.Instances > 1 ? $"（{top.Instances} 个进程）" : string.Empty)}";

        return $"CPU {s.CpuTotal:0}% · 就绪线程 {s.CpuQueue:0} · {culprit}";
    }

    private static string MemoryDetail(Snapshot s)
    {
        ProcessGroup? top = s.TopMemory();
        string culprit = top is null ? "没有明显的大户" : $"{top.Name} 占 {top.MemoryMB:0} MB";
        return $"可用 {s.AvailableMB} MB / 共 {s.TotalMB / 1024d:0.#} GB · 提交 {s.CommitRatio:P0} · {culprit}";
    }

    /// <summary>PDH 实例名形如 "1 D:"，只保留有意义的盘符部分。</summary>
    private static string shortName(string instance)
    {
        int space = instance.IndexOf(' ');
        string tail = space >= 0 ? instance[(space + 1)..] : instance;
        return tail.ToUpperInvariant();
    }
}
