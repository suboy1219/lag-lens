using System.Runtime.InteropServices;

namespace LagLens.Native;

internal static class User32
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
}

internal readonly struct MemoryStatus
{
    public MemoryStatus()
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };

        TotalPhysicalGB = NativeMethods.GlobalMemoryStatusEx(ref status)
            ? status.ullTotalPhys / 1024d / 1024d / 1024d
            : 16;
    }

    public double TotalPhysicalGB { get; }
}
