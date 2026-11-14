using System.Runtime.InteropServices;

namespace Perch.Pages.SystemMonitor;

/// <summary>kernel32 calls for CPU and memory figures. Both are cheap: no WMI, no performance counters.</summary>
internal static class SystemNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    /// <summary>Cumulative idle, kernel (includes idle) and user times in 100 ns units.</summary>
    public static bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        if (!GetSystemTimes(out var i, out var k, out var u)) return false;
        idle = i.ToUInt64();
        kernel = k.ToUInt64();
        user = u.ToUInt64();
        return true;
    }

    public static bool TryGetMemory(out ulong total, out ulong available)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        total = available = 0;
        if (!GlobalMemoryStatusEx(ref status)) return false;
        total = status.ullTotalPhys;
        available = status.ullAvailPhys;
        return true;
    }
}
