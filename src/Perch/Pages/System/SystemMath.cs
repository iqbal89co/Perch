using System.Globalization;

namespace Perch.Pages.SystemMonitor;

public sealed record ProcessUsage(string Name, double Fraction);

/// <summary>Pure arithmetic and text for the System page. No WPF and no OS calls, so it is unit-tested.</summary>
public static class SystemMath
{
    private const double GiB = 1024.0 * 1024 * 1024;

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

    /// <summary>Highest fractions first (ties by name), at most <paramref name="count"/> entries.</summary>
    public static IReadOnlyList<ProcessUsage> TopProcesses(IEnumerable<KeyValuePair<string, double>> byName, int count) =>
        byName
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(kv => new ProcessUsage(kv.Key, kv.Value))
            .ToList();

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

    public static string BatteryStatusText(bool charging, bool pluggedIn) =>
        charging ? "Charging" : pluggedIn ? "Plugged in" : "On battery";

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
