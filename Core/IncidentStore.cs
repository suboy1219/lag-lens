using System.Text.Json;
using System.Text.Encodings.Web;

namespace LagLens.Core;

public sealed class IncidentRecord
{
    public DateTime At { get; set; }

    /// <summary>记录写入的时刻，用来判断“用户看过没有”（卡顿本身可能发生在几十秒前）。</summary>
    public DateTime DetectedAt { get; set; }
    public double Seconds { get; set; }
    public string Kind { get; set; } = "冻结";
    public string Resource { get; set; } = "未知";
    public string Culprit { get; set; } = "未知";

    /// <summary>元凶进程的完整 exe 路径，点击记录时用来在资源管理器里定位。</summary>
    public string CulpritPath { get; set; } = string.Empty;
    public double Peak { get; set; }
    public double Cpu { get; set; }
    public double DiskBusy { get; set; }
    public double DiskLatencyMs { get; set; }

    public string Summary()
    {
        string head = Kind == "冻结" ? $"卡了 {Seconds:0.#} 秒" : $"高压 {Seconds:0} 秒";
        if (Culprit == "未知")
        {
            return $"{head} · 没抓到具体进程";
        }

        string metric = Resource switch
        {
            "CPU" => $"{Peak:0}% CPU",
            "磁盘" => $"{Peak:0.#} MB/s 磁盘",
            "内存" => $"{Peak:0} MB 内存",
            _ => string.Empty,
        };

        return metric.Length == 0
            ? $"{head} · {Culprit}"
            : $"{head} · {Culprit} {metric}";
    }
}

/// <summary>卡顿记录：内存里用，同时落盘到 %LOCALAPPDATA%\LagLens\incidents.json。</summary>
public sealed class IncidentStore
{
    private const int MaxRecords = 200;

    private sealed class StoreFile
    {
        public List<IncidentRecord> Items { get; set; } = new();
        public DateTime LastSeenAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly List<IncidentRecord> _items = new();
    private readonly string _path;
    private DateTime _lastSeenAt = DateTime.MinValue;

    public IncidentStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LagLens");
        _path = Path.Combine(dir, "incidents.json");
        Load();
    }

    public string FilePath => _path;

    public bool HasUnread
    {
        get
        {
            lock (_gate)
            {
                return _items.Any(x => x.DetectedAt > _lastSeenAt);
            }
        }
    }

    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(x => x.DetectedAt > _lastSeenAt);
            }
        }
    }

    public IReadOnlyList<IncidentRecord> Recent(int count)
    {
        lock (_gate)
        {
            return _items.OrderByDescending(x => x.At).Take(count).ToArray();
        }
    }

    public IncidentRecord? Latest()
    {
        lock (_gate)
        {
            return _items.Count == 0 ? null : _items.MaxBy(x => x.At);
        }
    }

    public void Add(IncidentRecord record)
    {
        lock (_gate)
        {
            record.DetectedAt = DateTime.Now;
            _items.Add(record);
            if (_items.Count > MaxRecords)
            {
                _items.RemoveRange(0, _items.Count - MaxRecords);
            }

            Save();
        }
    }

    public void MarkSeen()
    {
        lock (_gate)
        {
            _lastSeenAt = DateTime.Now;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), JsonOptions);
            if (data is null)
            {
                return;
            }

            _items.AddRange(data.Items);
            _lastSeenAt = data.LastSeenAt;
        }
        catch
        {
            // 文件坏了就当没有记录，不影响主功能。
        }
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var data = new StoreFile { Items = _items, LastSeenAt = _lastSeenAt };
            File.WriteAllText(_path, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch
        {
            // 落盘失败不影响界面展示。
        }
    }
}
