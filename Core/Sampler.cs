using System.Diagnostics;
using System.Text;
using LagLens.Diagnosis;
using LagLens.Native;

namespace LagLens.Core;

/// <summary>每秒采样一次，环形保留最近 60 秒。</summary>
public sealed class Sampler : IDisposable
{
    public const int IntervalMs = 1000;
    public const int HistorySeconds = 60;

    private const string CpuTotalCounter = @"\Processor(_Total)\% Processor Time";
    private const string CpuQueueCounter = @"\System\Processor Queue Length";
    private const string PagesCounter = @"\Memory\Pages Input/sec";
    private const string CommitBytesCounter = @"\Memory\Committed Bytes";
    private const string CommitLimitCounter = @"\Memory\Commit Limit";
    private const string DiskBusyCounter = @"\PhysicalDisk(*)\% Disk Time";
    private const string DiskQueueCounter = @"\PhysicalDisk(*)\Avg. Disk Queue Length";
    private const string DiskBytesCounter = @"\PhysicalDisk(*)\Disk Bytes/sec";
    private const string DiskLatencyCounter = @"\PhysicalDisk(*)\Avg. Disk sec/Transfer";

    private sealed class ProcPrev
    {
        public TimeSpan Cpu;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong ReadOps;
        public ulong WriteOps;
        public bool HasIo;
    }

    private readonly object _gate = new();
    private readonly Queue<Snapshot> _history = new();
    private readonly Dictionary<int, ProcPrev> _prev = new();
    private readonly PdhQuery? _pdh;
    private readonly int _cpuCount = Environment.ProcessorCount;
    private readonly Thread _thread;
    private readonly List<FreezeEvent> _freezes = new();
    private volatile bool _stopping;
    private Snapshot? _latest;
    private DateTime? _episodeStart;
    private Bottleneck _episodeKind;
    private string _episodeCulprit = "未知";
    private string _episodeCulpritPath = string.Empty;
    private double _episodePeak;
    private double _episodeCpu;
    private double _episodeDiskBusy;
    private double _episodeDiskLatency;

    public IncidentStore Store { get; } = new();

    public Sampler()
    {
        try
        {
            _pdh = new PdhQuery(new[]
            {
                CpuTotalCounter,
                CpuQueueCounter,
                PagesCounter,
                CommitBytesCounter,
                CommitLimitCounter,
                DiskBusyCounter,
                DiskQueueCounter,
                DiskBytesCounter,
                DiskLatencyCounter,
            });
            _pdh.Collect();
        }
        catch
        {
            _pdh = null;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LagLens.Sampler",
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start() => _thread.Start();

    public Snapshot? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public IReadOnlyList<Snapshot> History()
    {
        lock (_gate)
        {
            return _history.ToArray();
        }
    }

    public FreezeEvent? LastFreeze()
    {
        lock (_gate)
        {
            return _freezes.Count == 0 ? null : _freezes[^1];
        }
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        long lastMs = 0;
        bool first = true;

        while (!_stopping)
        {
            Thread.Sleep(IntervalMs);

            long nowMs = clock.ElapsedMilliseconds;
            double gap = (nowMs - lastMs) / 1000d;
            lastMs = nowMs;

            double freezeSeconds = 0;
            if (!first && gap > 2.5 && gap < 120)
            {
                freezeSeconds = gap - 1;
            }

            Snapshot? snapshot;
            try
            {
                snapshot = Sample(gap, freezeSeconds, first);
            }
            catch
            {
                continue;
            }

            first = false;
            lock (_gate)
            {
                _latest = snapshot;
                _history.Enqueue(snapshot);
                while (_history.Count > HistorySeconds)
                {
                    _history.Dequeue();
                }
            }
        }
    }

    private Snapshot Sample(double elapsedSeconds, double freezeSeconds, bool firstTick)
    {
        var snapshot = new Snapshot { At = DateTime.Now };
        double elapsed = Math.Clamp(elapsedSeconds, 0.2, 60);

        ReadSystem(snapshot, elapsed);
        ReadProcesses(snapshot, elapsed, firstTick);

        if (freezeSeconds > 0)
        {
            snapshot.FreezeSeconds = freezeSeconds;
            Snapshot? before = null;
            lock (_gate)
            {
                if (_history.Count > 0)
                {
                    before = _history.Last();
                }
            }

            ProcessGroup? culprit = null;
            string resource = "未知";
            if (before is not null)
            {
                bool diskDominant = before.DiskBusy >= before.CpuTotal;
                culprit = diskDominant ? before.TopDisk() : before.TopCpu();
                if (culprit is not null)
                {
                    resource = diskDominant ? "磁盘" : "CPU";
                }
                else
                {
                    culprit = before.TopCpu() ?? before.TopDisk();
                    resource = before.TopCpu() is not null ? "CPU" : "磁盘";
                }
            }

            lock (_gate)
            {
                _freezes.Add(new FreezeEvent(
                    snapshot.At.AddSeconds(-freezeSeconds),
                    freezeSeconds,
                    culprit?.Name ?? "未知",
                    resource));
                while (_freezes.Count > 20)
                {
                    _freezes.RemoveAt(0);
                }
            }

            Store.Add(new IncidentRecord
            {
                At = snapshot.At.AddSeconds(-freezeSeconds),
                Seconds = freezeSeconds,
                Kind = "冻结",
                Resource = resource,
                Culprit = culprit?.Name ?? "未知",
                CulpritPath = ResolveProcessPath(culprit?.MainPid ?? 0),
                Peak = resource switch
                {
                    "CPU" => culprit?.CpuPercent ?? 0,
                    "磁盘" => culprit?.DiskMBps ?? 0,
                    "内存" => culprit?.MemoryMB ?? 0,
                    _ => 0,
                },
                Cpu = snapshot.CpuTotal,
                DiskBusy = before?.DiskBusy ?? snapshot.DiskBusy,
                DiskLatencyMs = before?.DiskLatencyMs ?? snapshot.DiskLatencyMs,
            });

            // 冻结这一段已经单独记过一次，不要和“高压”重复计。
            _episodeStart = null;
            _episodeCulpritPath = string.Empty;
            return snapshot;
        }

        TrackEpisode(snapshot);
        return snapshot;
    }

    /// <summary>持续 5 秒以上的高负载也记一条，用来回答“刚才为什么卡”。</summary>
    private void TrackEpisode(Snapshot s)
    {
        Verdict verdict = Diagnoser.Evaluate(s);

        if (verdict.Severity == Severity.Bad)
        {
            ProcessGroup? top = verdict.Kind switch
            {
                Bottleneck.Cpu => s.TopCpu(),
                Bottleneck.Disk => s.TopDisk(),
                Bottleneck.Memory => s.TopMemory(),
                _ => null,
            };

            string culprit = top?.Name ?? "未知";
            double value = verdict.Kind switch
            {
                Bottleneck.Cpu => top?.CpuPercent ?? 0,
                Bottleneck.Disk => top?.DiskMBps ?? 0,
                Bottleneck.Memory => top?.MemoryMB ?? 0,
                _ => 0,
            };

            if (_episodeStart is null)
            {
                _episodeStart = s.At;
                _episodeKind = verdict.Kind;
                _episodeCulprit = culprit;
                _episodeCulpritPath = ResolveProcessPath(top?.MainPid ?? 0);
                _episodePeak = value;
                _episodeCpu = s.CpuTotal;
                _episodeDiskBusy = s.DiskBusy;
                _episodeDiskLatency = s.DiskLatencyMs;
                return;
            }

            _episodePeak = Math.Max(_episodePeak, value);
            _episodeCpu = Math.Max(_episodeCpu, s.CpuTotal);
            _episodeDiskBusy = Math.Max(_episodeDiskBusy, s.DiskBusy);
            _episodeDiskLatency = Math.Max(_episodeDiskLatency, s.DiskLatencyMs);

            if (_episodeCulprit == "未知" && culprit != "未知")
            {
                _episodeCulprit = culprit;
                _episodeCulpritPath = ResolveProcessPath(top?.MainPid ?? 0);
            }

            return;
        }

        if (_episodeStart is null)
        {
            return;
        }

        double seconds = (s.At - _episodeStart.Value).TotalSeconds;
        if (seconds >= 5)
        {
            Store.Add(new IncidentRecord
            {
                At = _episodeStart.Value,
                Seconds = seconds,
                Kind = "高压",
                Resource = _episodeKind switch
                {
                    Bottleneck.Cpu => "CPU",
                    Bottleneck.Disk => "磁盘",
                    Bottleneck.Memory => "内存",
                    _ => "未知",
                },
                Culprit = _episodeCulprit,
                CulpritPath = _episodeCulpritPath,
                Peak = _episodePeak,
                Cpu = _episodeCpu,
                DiskBusy = _episodeDiskBusy,
                DiskLatencyMs = _episodeDiskLatency,
            });
        }

        _episodeStart = null;
        _episodeCulpritPath = string.Empty;
    }

    private void ReadSystem(Snapshot s, double elapsed)
    {
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem))
        {
            s.AvailableMB = (long)(mem.ullAvailPhys / 1024 / 1024);
            s.TotalMB = (long)(mem.ullTotalPhys / 1024 / 1024);
        }

        if (_pdh is null)
        {
            return;
        }

        _pdh.Collect();

        s.CpuTotal = Math.Clamp(_pdh.ReadDouble(CpuTotalCounter) ?? 0, 0, 100);
        s.CpuQueue = Math.Max(0, _pdh.ReadDouble(CpuQueueCounter) ?? 0);
        s.PagesPerSec = Math.Max(0, _pdh.ReadDouble(PagesCounter) ?? 0);

        double commitLimit = _pdh.ReadDouble(CommitLimitCounter) ?? 0;
        double commitBytes = _pdh.ReadDouble(CommitBytesCounter) ?? 0;
        if (commitLimit > 0)
        {
            s.CommitRatio = Math.Clamp(commitBytes / commitLimit, 0, 1);
        }

        var busy = _pdh.ReadArray(DiskBusyCounter);
        var queues = _pdh.ReadArray(DiskQueueCounter);
        var bytes = _pdh.ReadArray(DiskBytesCounter);
        var latency = _pdh.ReadArray(DiskLatencyCounter);
        var queueMap = queues.ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var bytesMap = bytes.ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var latencyMap = latency.ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);

        double maxBusy = 0;
        double maxQueue = 0;
        double maxLatency = 0;
        string maxName = string.Empty;
        double totalBytes = 0;

        foreach ((string name, double value) in busy)
        {
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            {
                totalBytes += bytesMap.TryGetValue(name, out double b) ? b : 0;
                continue;
            }

            double busyPct = Math.Clamp(value, 0, 100);
            double queue = queueMap.TryGetValue(name, out double q) ? Math.Max(0, q) : 0;
            double throughput = bytesMap.TryGetValue(name, out double bb) ? Math.Max(0, bb) : 0;
            // PDH 给的是“秒/次传输”，换算成毫秒。空转的盘样本太少会乱跳，只在有压力时采信。
            double latencyMs = latencyMap.TryGetValue(name, out double lat) ? Math.Max(0, lat) * 1000 : 0;
            totalBytes += throughput;

            s.Disks.Add(new DiskSample
            {
                Name = name,
                BusyPercent = busyPct,
                Queue = queue,
                LatencyMs = latencyMs,
            });

            if (busyPct > maxBusy || (Math.Abs(busyPct - maxBusy) < 0.5 && queue > maxQueue))
            {
                maxBusy = busyPct;
                maxQueue = queue;
                maxName = name;
            }

            if (busyPct >= 30 && latencyMs > maxLatency)
            {
                maxLatency = latencyMs;
            }
        }

        s.DiskBusy = maxBusy;
        s.DiskQueue = maxQueue;
        s.DiskLatencyMs = maxLatency;
        s.DiskName = maxName;
        _ = totalBytes;
        _ = elapsed;
    }

    private void ReadProcesses(Snapshot s, double elapsed, bool firstTick)
    {
        var groups = new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();

        foreach (Process p in Process.GetProcesses())
        {
            int pid = 0;
            string name = string.Empty;
            try
            {
                pid = p.Id;
                name = p.ProcessName;
            }
            catch
            {
                p.Dispose();
                continue;
            }

            seen.Add(pid);

            double cpuPercent = 0;
            bool hasCpu = false;
            TimeSpan cpu = TimeSpan.Zero;
            try
            {
                cpu = p.TotalProcessorTime;
                hasCpu = true;
            }
            catch
            {
                // 受保护进程（系统 / 反作弊）读不到 CPU，跳过。
            }

            long memoryBytes = 0;
            try
            {
                memoryBytes = p.WorkingSet64;
            }
            catch
            {
                // ignore
            }

            p.Dispose();

            bool hasIo = TryReadIo(pid, out ulong readBytes, out ulong writeBytes, out ulong readOps, out ulong writeOps);

            if (hasCpu && _prev.TryGetValue(pid, out ProcPrev? prev))
            {
                cpuPercent = Math.Clamp((cpu - prev.Cpu).TotalSeconds / elapsed / _cpuCount * 100, 0, 100);
            }

            if (!_prev.TryGetValue(pid, out ProcPrev? prev2))
            {
                prev2 = new ProcPrev();
                _prev[pid] = prev2;
            }

            if (hasCpu)
            {
                prev2.Cpu = cpu;
            }

            double readBps = 0;
            double writeBps = 0;
            double iops = 0;
            if (hasIo)
            {
                if (prev2.HasIo)
                {
                    readBps = Delta(readBytes, prev2.ReadBytes) / elapsed;
                    writeBps = Delta(writeBytes, prev2.WriteBytes) / elapsed;
                    iops = (Delta(readOps, prev2.ReadOps) + Delta(writeOps, prev2.WriteOps)) / elapsed;
                }

                prev2.ReadBytes = readBytes;
                prev2.WriteBytes = writeBytes;
                prev2.ReadOps = readOps;
                prev2.WriteOps = writeOps;
                prev2.HasIo = true;
            }

            if (!groups.TryGetValue(name, out ProcessGroup? group))
            {
                group = new ProcessGroup { Name = name };
                groups[name] = group;
            }

            group.Instances++;
            if (cpuPercent > group.CpuPercent)
            {
                group.MainPid = pid;
            }

            group.CpuPercent += cpuPercent;
            group.ReadBytesPerSec += readBps;
            group.WriteBytesPerSec += writeBps;
            group.Iops += iops;
            group.MemoryMB += memoryBytes / 1024d / 1024d;
        }

        foreach (int pid in _prev.Keys.ToArray())
        {
            if (!seen.Contains(pid))
            {
                _prev.Remove(pid);
            }
        }

        if (firstTick)
        {
            foreach (ProcessGroup g in groups.Values)
            {
                g.CpuPercent = 0;
            }
        }

        s.Groups = groups.Values.ToList();
    }

    private static double Delta(ulong current, ulong previous) =>
        current >= previous ? current - previous : 0;

    private static bool TryReadIo(int pid, out ulong readBytes, out ulong writeBytes, out ulong readOps, out ulong writeOps)
    {
        readBytes = writeBytes = readOps = writeOps = 0;
        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_QUERY_INFORMATION,
            false,
            pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.GetProcessIoCounters(handle, out NativeMethods.IO_COUNTERS io))
            {
                return false;
            }

            readBytes = io.ReadTransferCount;
            writeBytes = io.WriteTransferCount;
            readOps = io.ReadOperationCount;
            writeOps = io.WriteOperationCount;
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>取进程可执行文件的完整路径；受保护进程取不到时返回空串。</summary>
    private static string ResolveProcessPath(int pid)
    {
        if (pid <= 0)
        {
            return string.Empty;
        }

        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString(0, (int)size)
                : string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _thread.Join(1500);
        }
        catch
        {
            // ignore
        }

        _pdh?.Dispose();
    }
}
