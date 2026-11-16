using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Perch.Pages.SystemMonitor;

/// <summary>One GPU reading. Utilisation is null on the first read after a (re)build, since it needs two collections.</summary>
public sealed record GpuReading(bool Available, double? Utilization, long? DedicatedBytes, double SampleMs);

/// <summary>
/// 3D engine utilisation and dedicated adapter memory from PDH. The "GPU Engine" counter set is expensive, so this
/// runs on a background task only (one call at a time, guarded by the page). The instance list is re-expanded every
/// 20 s because engine instances belong to processes that come and go. A missing category makes the GPU unavailable.
/// </summary>
internal sealed class GpuSampler : IDisposable
{
    private const string EnginePath = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    private const string MemoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const long RebuildAfterMs = 20_000;

    private static readonly GpuReading Unavailable = new(false, null, null, 0);

    private PdhQuery? _query;
    private readonly List<(IntPtr Counter, string Instance)> _engines = new();
    private readonly List<(IntPtr Counter, string Instance)> _memory = new();
    private long _builtAtMs;
    private bool _unavailable;

    public GpuReading Sample()
    {
        if (_unavailable) return Unavailable;

        if (_query is null || Environment.TickCount64 - _builtAtMs > RebuildAfterMs)
        {
            var rebuilt = Rebuild();
            if (rebuilt is null) return _unavailable ? Unavailable : new GpuReading(true, null, null, 0);
            // Utilisation needs two collections: this first one only primes it. Memory is valid already.
            rebuilt.Collect();
            return new GpuReading(true, null, ReadMax(_memory), 0);
        }

        var clock = Stopwatch.StartNew();
        _query.Collect();
        var utilization = SystemMath.GpuUtilization(Read(_engines));
        var memory = ReadMax(_memory);
        clock.Stop();
        return new GpuReading(true, utilization, memory, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Builds a fresh query with its instance lists. Null when it cannot be built right now.</summary>
    private PdhQuery? Rebuild()
    {
        Release();

        var engines = Expand(EnginePath, out var engineStatus);
        if (engines is null)
        {
            // The category is missing on this PC. Other errors are treated as transient and retried later.
            _unavailable = engineStatus is PdhNative.NoObject or PdhNative.NoCounter;
            return null;
        }

        var query = new PdhQuery();
        if (!query.IsOpen)
        {
            query.Dispose();
            return null;
        }

        foreach (var path in engines)
        {
            var counter = query.AddCounter(path);
            if (counter != IntPtr.Zero) _engines.Add((counter, InstanceOf(path)));
        }
        foreach (var path in Expand(MemoryPath, out _) ?? new List<string>())
        {
            var counter = query.AddCounter(path);
            if (counter != IntPtr.Zero) _memory.Add((counter, InstanceOf(path)));
        }

        _query = query;
        _builtAtMs = Environment.TickCount64;
        return query;
    }

    /// <summary>Expanded counter paths for a wildcard. Null on failure, with the PDH status in <paramref name="status"/>.</summary>
    private static List<string>? Expand(string wildcardPath, out uint status)
    {
        uint length = 0;
        status = PdhNative.PdhExpandWildCardPathW(IntPtr.Zero, wildcardPath, IntPtr.Zero, ref length, 0);
        if (status == PdhNative.Success || status == PdhNative.NoData) return new List<string>(); // no instances right now
        if (status != PdhNative.MoreData) return null;

        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            status = PdhNative.PdhExpandWildCardPathW(IntPtr.Zero, wildcardPath, buffer, ref length, 0);
            if (status != PdhNative.Success) return null;
            var all = Marshal.PtrToStringUni(buffer, (int)length) ?? "";
            return all.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The instance name inside the parentheses: "\GPU Engine(pid_1_luid_...)\Utilization Percentage".</summary>
    private static string InstanceOf(string path)
    {
        var open = path.IndexOf('(');
        var close = path.LastIndexOf(')');
        return open >= 0 && close > open ? path[(open + 1)..close] : path;
    }

    private static List<(string Instance, double Value)> Read(List<(IntPtr Counter, string Instance)> counters)
    {
        var values = new List<(string Instance, double Value)>(counters.Count);
        foreach (var (counter, instance) in counters)
        {
            if (PdhNative.ReadDouble(counter) is { } value) values.Add((instance, value));
        }
        return values;
    }

    private static long? ReadMax(List<(IntPtr Counter, string Instance)> counters)
    {
        var values = Read(counters);
        return values.Count == 0 ? null : (long)SystemMath.LargestAdapterTotal(values);
    }

    private void Release()
    {
        _query?.Dispose();
        _query = null;
        _engines.Clear();
        _memory.Clear();
    }

    public void Dispose() => Release();
}
