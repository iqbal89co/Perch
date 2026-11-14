using System.Diagnostics;
using System.Net.NetworkInformation;
using WinForms = System.Windows.Forms;

namespace Perch.Pages.SystemMonitor;

public sealed record MemoryReading(double UsedFraction, long UsedBytes, long TotalBytes);

public sealed record DiskReading(string Name, long UsedBytes, long TotalBytes);

/// <summary>Fraction is -1 when the battery does not report a level.</summary>
public sealed record BatteryReading(double Fraction, bool Charging, bool PluggedIn);

/// <summary>One reading of everything on the page. Rates and CPU are null when there is no fresh baseline yet.</summary>
public sealed record SystemReading(
    double? Cpu,
    MemoryReading? Memory,
    double? DownBytesPerSec,
    double? UpBytesPerSec,
    IReadOnlyList<DiskReading> Disks,
    BatteryReading? Battery,
    TimeSpan Uptime);

/// <summary>
/// Reads CPU, memory, network, disks, battery and uptime on the UI thread (all calls are fast).
/// CPU and network are deltas, so each read keeps the previous totals. Reads closer than
/// MinInterval apart do not produce rates (they would be noise), but still advance the baseline.
/// </summary>
internal sealed class SystemSampler
{
    private const double MinInterval = 0.5;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private (ulong Idle, ulong Kernel, ulong User)? _cpuPrev;
    private (long Rx, long Tx)? _netPrev;
    private double _prevAt = double.NaN;

    public SystemReading Read()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _prevAt; // NaN on the first read, so no rates yet
        var rateOk = dt >= MinInterval;

        double? cpu = null;
        if (SystemNative.TryGetCpuTimes(out var idle, out var kernel, out var user))
        {
            if (_cpuPrev is { } p && rateOk) cpu = SystemMath.CpuUsage(p.Idle, p.Kernel, p.User, idle, kernel, user);
            _cpuPrev = (idle, kernel, user);
        }

        var (rx, tx) = ReadNetworkTotals();
        double? down = null, up = null;
        if (_netPrev is { } n && rateOk)
        {
            down = RateCalculator.BytesPerSecond(n.Rx, rx, dt);
            up = RateCalculator.BytesPerSecond(n.Tx, tx, dt);
        }
        _netPrev = (rx, tx);
        _prevAt = now;

        return new SystemReading(
            cpu,
            ReadMemory(),
            down,
            up,
            ReadDisks(),
            ReadBattery(),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static MemoryReading? ReadMemory()
    {
        if (!SystemNative.TryGetMemory(out var total, out var available) || total == 0) return null;
        var used = total > available ? total - available : 0;
        return new MemoryReading((double)used / total, (long)used, (long)total);
    }

    private static (long Rx, long Tx) ReadNetworkTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch (Exception)
                {
                    // Some virtual adapters do not expose counters; skip them.
                }
            }
        }
        catch (Exception)
        {
            // Interface enumeration failed this tick; the rate reads as 0 on the next good one.
        }
        return (rx, tx);
    }

    private static IReadOnlyList<DiskReading> ReadDisks()
    {
        var disks = new List<DiskReading>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (disks.Count >= 3) break;
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    var total = drive.TotalSize;
                    var free = drive.TotalFreeSpace;
                    disks.Add(new DiskReading(drive.Name.TrimEnd('\\'), total > free ? total - free : 0, total));
                }
                catch (Exception)
                {
                    // Drive vanished between listing and querying.
                }
            }
        }
        catch (Exception)
        {
            // Drive listing failed; show no disks this tick.
        }
        return disks;
    }

    private static BatteryReading? ReadBattery()
    {
        var status = WinForms.SystemInformation.PowerStatus;
        // Desktops report NoSystemBattery (and Unknown, which includes that bit): hide the card.
        if (status.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery)) return null;
        return new BatteryReading(
            status.BatteryLifePercent,
            status.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.Charging),
            status.PowerLineStatus == WinForms.PowerLineStatus.Online);
    }
}
