using System.Globalization;
using Perch.Core;

namespace Perch.Pages.SystemMonitor;

public sealed record ProcessUsage(string Name, double Fraction, long MemoryBytes);

/// <summary>One network interface with its recent traffic, for picking the busiest one.</summary>
public sealed record InterfaceLoad(string Name, string? Ipv4, double BytesPerSec);

/// <summary>Pure arithmetic and text for the System page. No WPF and no OS calls, so it is unit-tested.</summary>
public static class SystemMath
{
    private const double GiB = 1024.0 * 1024 * 1024;
    private const string Dot = " · ";
    private const double SlowGpuSampleMs = 30;
    private static readonly TimeSpan FastGpuInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowGpuInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Busy fraction 0..1 between two GetSystemTimes snapshots. Kernel time already includes idle time,
    /// so busy = 1 - dIdle / (dKernel + dUser). A zero delta (nothing moved) reads as 0.
    /// </summary>
    public static double CpuUsage(ulong prevIdle, ulong prevKernel, ulong prevUser, ulong idle, ulong kernel, ulong user)
    {
        var dIdle = Delta(prevIdle, idle);
        var dTotal = Delta(prevKernel, kernel) + Delta(prevUser, user);
        if (dTotal == 0) return 0;
        return Math.Clamp(1 - (double)dIdle / dTotal, 0, 1);
    }

    /// <summary>Share of one process's CPU time over a wall-clock interval, as a fraction of all logical cores.</summary>
    public static double ProcessPercent(TimeSpan before, TimeSpan after, TimeSpan elapsed, int cores)
    {
        if (elapsed <= TimeSpan.Zero || cores <= 0 || after < before) return 0;
        return Math.Clamp((after - before).TotalSeconds / (elapsed.TotalSeconds * cores), 0, 1);
    }

    /// <summary>Highest CPU first (ties by name), at most <paramref name="count"/> entries.</summary>
    public static IReadOnlyList<ProcessUsage> TopProcesses(IEnumerable<ProcessUsage> all, int count) =>
        all.OrderByDescending(p => p.Fraction)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();

    /// <summary>Current clock: the base clock scaled by "% Processor Performance" (100 = base). No counter means base.</summary>
    public static double CurrentMhz(double baseMhz, double? performancePercent) =>
        performancePercent is { } p && p > 0 ? baseMhz * Math.Min(p, 400) / 100 : baseMhz;

    /// <summary>"3.4 GHz"</summary>
    public static string FormatGHz(double mhz) =>
        (Math.Max(0, mhz) / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " GHz";

    /// <summary>"3.4 GHz · 12 threads", or just the thread count when the clock is unknown.</summary>
    public static string CpuDetail(double mhz, int threads)
    {
        var threadText = $"{threads} threads";
        return mhz > 0 ? FormatGHz(mhz) + Dot + threadText : threadText;
    }

    /// <summary>"2 h 10 m left", "45 m left", or "&lt;1 m left" under a minute.</summary>
    public static string FormatBatteryLeft(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1)) return "<1 m left";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours} h {left.Minutes} m left";
        return $"{(int)left.TotalMinutes} m left";
    }

    /// <summary>"Charging", "Plugged in", "On battery · 2 h 10 m left" or "On battery".</summary>
    public static string PowerStatusText(bool charging, bool pluggedIn, TimeSpan? timeLeft)
    {
        if (charging) return "Charging";
        if (pluggedIn) return "Plugged in";
        return timeLeft is { } left ? "On battery" + Dot + FormatBatteryLeft(left) : "On battery";
    }

    /// <summary>"16.1 / 20 GB · 3.9 GB free"</summary>
    public static string MemoryDetail(long usedBytes, long totalBytes, long availableBytes) =>
        $"{OneDecimal(usedBytes)} / {FormatGiB(totalBytes)} GB{Dot}{OneDecimal(availableBytes)} GB free";

    /// <summary>"1.2 GB VRAM", or empty when the adapter memory counter is missing.</summary>
    public static string VramText(long? dedicatedBytes) =>
        dedicatedBytes is { } bytes ? $"{OneDecimal(bytes)} GB VRAM" : "";

    /// <summary>"R 12 MB/s · W 3 MB/s"; a dash for a missing counter.</summary>
    public static string DiskActivity(double? readBytesPerSec, double? writeBytesPerSec) =>
        $"R {Rate(readBytesPerSec)}{Dot}W {Rate(writeBytesPerSec)}";

    /// <summary>"Since start ↓ 1.2 GB · ↑ 300 MB"</summary>
    public static string NetworkTotals(long received, long sent) =>
        $"Since start ↓ {Format.Bytes(Math.Max(0, received))} · ↑ {Format.Bytes(Math.Max(0, sent))}";

    /// <summary>"Windows 11 26200" from the build number (Windows 11 starts at 22000).</summary>
    public static string WindowsLabel(int build)
    {
        if (build <= 0) return "Windows";
        return (build >= 22000 ? "Windows 11 " : "Windows 10 ") + build.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The registry's processor name with its padding spaces collapsed.</summary>
    public static string CleanCpuName(string? raw) =>
        string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Footer caption: "CPU · Windows 11 26200 · Up 2 d 18 h".</summary>
    public static string Footer(string cpuName, string windows, TimeSpan uptime) =>
        string.Join(Dot, new[] { cpuName, windows, FormatUptime(uptime) }.Where(s => s.Length > 0));

    /// <summary>
    /// Groups PDH GPU Engine or Adapter Memory instances by adapter (the "luid_..._phys_N" part of the
    /// instance name, without the per-engine suffix) and returns the largest adapter total. 0 when empty.
    /// </summary>
    public static double LargestAdapterTotal(IEnumerable<(string Instance, double Value)> items)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (instance, value) in items)
        {
            if (double.IsNaN(value) || value < 0) continue;
            var key = AdapterKey(instance);
            totals[key] = totals.GetValueOrDefault(key) + value;
        }
        return totals.Count == 0 ? 0 : totals.Values.Max();
    }

    /// <summary>3D engine utilisation 0..100: the busiest adapter's engines summed, clamped.</summary>
    public static double GpuUtilization(IEnumerable<(string Instance, double Value)> engines) =>
        Math.Clamp(LargestAdapterTotal(engines), 0, 100);

    /// <summary>"pid_4312_luid_0x0_0x1_phys_0_eng_3_engtype_3D" becomes "luid_0x0_0x1_phys_0".</summary>
    public static string AdapterKey(string instance)
    {
        var luid = instance.IndexOf("luid_", StringComparison.Ordinal);
        var key = luid >= 0 ? instance[luid..] : instance;
        var eng = key.IndexOf("_eng_", StringComparison.Ordinal);
        return eng >= 0 ? key[..eng] : key;
    }

    /// <summary>The interface with the most recent traffic among those that have an IPv4 address (ties by name).</summary>
    public static InterfaceLoad? BusiestInterface(IEnumerable<InterfaceLoad> interfaces)
    {
        InterfaceLoad? best = null;
        foreach (var nic in interfaces)
        {
            if (string.IsNullOrEmpty(nic.Ipv4)) continue;
            if (best is null
                || nic.BytesPerSec > best.BytesPerSec
                || (nic.BytesPerSec == best.BytesPerSec && string.CompareOrdinal(nic.Name, best.Name) < 0))
            {
                best = nic;
            }
        }
        return best;
    }

    /// <summary>
    /// How long to wait before the next GPU sample: 2 s, or 5 s when the last sample took longer than 30 ms.
    /// </summary>
    public static TimeSpan GpuSampleInterval(double sampleMs) =>
        sampleMs > SlowGpuSampleMs ? SlowGpuInterval : FastGpuInterval;

    /// <summary>"Up 3 d 4 h", "Up 5 h 12 m" or "Up 7 m".</summary>
    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        if (uptime.TotalDays >= 1) return $"Up {(int)uptime.TotalDays} d {uptime.Hours} h";
        if (uptime.TotalHours >= 1) return $"Up {(int)uptime.TotalHours} h {uptime.Minutes} m";
        return $"Up {(int)uptime.TotalMinutes} m";
    }

    /// <summary>Bytes as gibibytes: "9.4" below ten, "16" from ten up.</summary>
    public static string FormatGiB(long bytes)
    {
        var g = Math.Max(0, bytes) / GiB;
        return g >= 10 ? g.ToString("0", CultureInfo.InvariantCulture) : g.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public static string Percent(double fraction) =>
        $"{(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero)}%";

    /// <summary>Bytes as gibibytes with one decimal always: "16.1".</summary>
    private static string OneDecimal(long bytes) =>
        (Math.Max(0, bytes) / GiB).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Rate(double? bytesPerSec) =>
        bytesPerSec is { } v ? Format.Bytes(Math.Max(0, v)) + "/s" : "—";

    private static ulong Delta(ulong prev, ulong cur) => cur >= prev ? cur - prev : 0;
}

/// <summary>Turns two cumulative byte counters into a rate. Counter resets and bad intervals read as 0.</summary>
public static class RateCalculator
{
    public static double BytesPerSecond(long prevTotal, long total, double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
        if (total < prevTotal) return 0; // counter reset (adapter reconnected or wrapped)
        return (total - prevTotal) / seconds;
    }
}
