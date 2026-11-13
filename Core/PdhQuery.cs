using System.Runtime.InteropServices;

namespace LagLens.Core;

/// <summary>
/// 对 PDH 的一层薄封装：一次查询挂多个计数器，每个采样周期只 Collect 一次。
/// 使用 PdhAddEnglishCounter，保证中文系统上计数器路径也能解析。
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr SzName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PdhQuery(IEnumerable<string> counterPaths)
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            throw new InvalidOperationException("PdhOpenQuery 失败，无法读取性能计数器。");
        }

        foreach (string path in counterPaths)
        {
            if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr handle) == 0)
            {
                _counters[path] = handle;
            }
        }
    }

    public bool IsAvailable(string path) => _counters.ContainsKey(path);

    /// <summary>采集一次。第一次调用后大部分计数器还没有有效值，需要连续采样。</summary>
    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public double? ReadDouble(string path)
    {
        if (!_counters.TryGetValue(path, out IntPtr handle))
        {
            return null;
        }

        uint rc = PdhGetFormattedCounterValue(handle, PDH_FMT_DOUBLE, out _, out PDH_FMT_COUNTERVALUE value);
        if (rc != 0 || value.CStatus != 0 || double.IsNaN(value.DoubleValue))
        {
            return null;
        }

        return value.DoubleValue;
    }

    /// <summary>读取通配实例计数器（例如 \PhysicalDisk(*)\% Disk Time）。</summary>
    public List<(string Name, double Value)> ReadArray(string path)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out IntPtr handle))
        {
            return result;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint size = 0;
            uint count = 0;
            uint rc = PdhGetFormattedCounterArrayW(handle, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0 || count == 0)
            {
                return result;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArrayW(handle, PDH_FMT_DOUBLE, ref size, ref count, buffer);
                if (rc == PDH_MORE_DATA)
                {
                    continue;
                }

                if (rc != 0)
                {
                    return result;
                }

                int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + (i * stride));
                    if (item.FmtValue.CStatus != 0 || double.IsNaN(item.FmtValue.DoubleValue))
                    {
                        continue;
                    }

                    string name = Marshal.PtrToStringUni(item.SzName) ?? string.Empty;
                    result.Add((name, item.FmtValue.DoubleValue));
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IntPtr handle in _counters.Values)
        {
            PdhRemoveCounter(handle);
        }

        _counters.Clear();
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
        }
    }
}
