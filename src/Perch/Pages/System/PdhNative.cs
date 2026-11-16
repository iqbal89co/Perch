using System.Runtime.InteropServices;

namespace Perch.Pages.SystemMonitor;

/// <summary>
/// pdh.dll calls for the performance counters the System page reads. Fixed counters are added by English
/// name so they work in any display language; expanded wildcard paths are added as the OS returned them.
/// </summary>
internal static class PdhNative
{
    public const uint Success = 0;
    public const uint NoObject = 0xC0000BB8;
    public const uint NoCounter = 0xC0000BB9;
    public const uint InvalidData = 0xC0000BBA;
    public const uint MoreData = 0x800007D2;
    public const uint NoData = 0x800007D5;

    private const uint FormatDouble = 0x00000200;
    private const uint CStatusValid = 0;
    private const uint CStatusNewData = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhExpandWildCardPathW(IntPtr dataSource, string wildCardPath, IntPtr expandedPathList, ref uint pathListLength, uint flags);

    /// <summary>The counter's current double value, or null when PDH has no valid sample yet (rate counters need two collections).</summary>
    public static double? ReadDouble(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        var status = PdhGetFormattedCounterValue(counter, FormatDouble, out _, out var value);
        if (status != Success) return null;
        if (value.CStatus != CStatusValid && value.CStatus != CStatusNewData) return null;
        return double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue) ? null : value.DoubleValue;
    }
}

/// <summary>An owned PDH query. Counters belong to the query and are released with it. Not thread-safe: use from one thread at a time.</summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;

    public PdhQuery()
    {
        if (PdhNative.PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out var query) == PdhNative.Success) _query = query;
    }

    public bool IsOpen => _query != IntPtr.Zero;

    /// <summary>Adds a counter by its English path. Returns IntPtr.Zero if the counter does not exist.</summary>
    public IntPtr AddEnglish(string path) =>
        IsOpen && PdhNative.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == PdhNative.Success
            ? counter
            : IntPtr.Zero;

    /// <summary>Adds a counter by a path returned from wildcard expansion (localised names).</summary>
    public IntPtr AddCounter(string path) =>
        IsOpen && PdhNative.PdhAddCounterW(_query, path, IntPtr.Zero, out var counter) == PdhNative.Success
            ? counter
            : IntPtr.Zero;

    public bool Collect() => IsOpen && PdhNative.PdhCollectQueryData(_query) == PdhNative.Success;

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhNative.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
