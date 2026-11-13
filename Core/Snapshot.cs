namespace LagLens.Core;

/// <summary>按进程名聚合后的一组进程（Chrome 开 30 个进程只算一条）。</summary>
public sealed class ProcessGroup
{
    public required string Name { get; init; }
    public int Instances { get; set; }
    public int MainPid { get; set; }
    public double CpuPercent { get; set; }
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
    public double Iops { get; set; }
    public double MemoryMB { get; set; }

    public double DiskMBps => (ReadBytesPerSec + WriteBytesPerSec) / 1024d / 1024d;

    /// <summary>综合磁盘压力：带宽和 IOPS 都会造成卡顿，机械盘上 IOPS 影响更大。</summary>
    public double DiskScore => DiskMBps + (Iops * 0.05);
}

public sealed class DiskSample
{
    public required string Name { get; init; }
    public double BusyPercent { get; set; }
    public double Queue { get; set; }
    public double LatencyMs { get; set; }
}

public sealed class Snapshot
{
    public DateTime At { get; init; }
    public double CpuTotal { get; set; }
    public double CpuQueue { get; set; }
    public double DiskBusy { get; set; }
    public double DiskQueue { get; set; }
    public double DiskLatencyMs { get; set; }
    public string DiskName { get; set; } = string.Empty;
    public long AvailableMB { get; set; }
    public long TotalMB { get; set; }
    public double CommitRatio { get; set; }
    public double PagesPerSec { get; set; }
    public List<DiskSample> Disks { get; } = new();
    public List<ProcessGroup> Groups { get; set; } = new();

    /// <summary>采样线程被饿死/系统冻结时，本次采样前实际停滞的秒数（正常为 0）。</summary>
    public double FreezeSeconds { get; set; }

    public ProcessGroup? TopCpu() => Groups
        .Where(g => g.CpuPercent >= 0.8)
        .OrderByDescending(g => g.CpuPercent)
        .FirstOrDefault();

    public ProcessGroup? TopDisk() => Groups
        .Where(g => g.DiskScore >= 1.0)
        .OrderByDescending(g => g.DiskScore)
        .FirstOrDefault();

    public ProcessGroup? TopMemory() => Groups
        .Where(g => g.MemoryMB >= 50)
        .OrderByDescending(g => g.MemoryMB)
        .FirstOrDefault();
}

public sealed record FreezeEvent(DateTime At, double Seconds, string Culprit, string Resource);
