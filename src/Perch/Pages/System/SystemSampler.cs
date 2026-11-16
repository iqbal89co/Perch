using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinForms = System.Windows.Forms;

namespace Perch.Pages.SystemMonitor;

public sealed record MemoryReading(double UsedFraction, long UsedBytes, long TotalBytes, long AvailableBytes);

public sealed record DiskReading(string Name, long UsedBytes, long TotalBytes);

/// <summary>Disk read and write rates from PDH (null until PDH has two collections).</summary>
public sealed record DiskIoReading(double? ReadBytesPerSec, double? WriteBytesPerSec);

/// <summary>Fraction is -1 when the battery does not report a level. TimeLeft is null when Windows does not estimate it.</summary>
public sealed record BatteryReading(double Fraction, bool Charging, bool PluggedIn, TimeSpan? TimeLeft);

/// <summary>The busiest interface with an IPv4 address, plus traffic totals since Perch started (Adapter is null when none is up).</summary>
public sealed record NetworkReading(string? Adapter, string? Ipv4, long Received, long Sent);

/// <summary>
/// One reading of everything on the page. Rates and CPU are null when there is no fresh baseline yet.
/// CpuMhz is the current clock, or the base clock when PDH has no performance counter (0 if unknown).
/// </summary>
public sealed record SystemReading(
    double? Cpu,
    double CpuMhz,
    MemoryReading? Memory,
    double? DownBytesPerSec,
    double? UpBytesPerSec,
    NetworkReading Network,
    IReadOnlyList<DiskReading> Disks,
    DiskIoReading DiskIo,
    BatteryReading? Battery,
    TimeSpan Uptime);

/// <summary>
/// Reads CPU, memory, network, disks, battery, uptime and two PDH counter groups on the UI thread (all calls are fast).
/// CPU and network are deltas, so each read keeps the previous totals. Reads closer than MinInterval apart do not
/// produce rates (they would be noise), but still advance the baseline.
/// </summary>
internal sealed class SystemSampler : IDisposable
{
    private const double MinInterval = 0.5;
    private const int MaxDisks = 3;
    private const double IpRefreshSeconds = 10;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly PdhQuery _perf = new();
    private readonly IntPtr _cpuPerformance;
    private readonly IntPtr _diskRead;
    private readonly IntPtr _diskWrite;

    private (ulong Idle, ulong Kernel, ulong User)? _cpuPrev;
    private (long Rx, long Tx)? _netPrev;
    private (long Rx, long Tx)? _netStart;
    private readonly Dictionary<string, (long Rx, long Tx)> _nicPrev = new();
    private readonly Dictionary<string, string> _ipv4 = new();
    private double _ipAt = double.NegativeInfinity;
    private double _prevAt = double.NaN;

    public SystemSampler()
    {
        _cpuPerformance = _perf.AddEnglish(@"\Processor Information(_Total)\% Processor Performance");
        _diskRead = _perf.AddEnglish(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _perf.AddEnglish(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _perf.Collect(); // rate counters need a second collection before they have values
    }

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

        // Collect returns false once the query is disposed, and the counters are then not read at all.
        var perfOpen = _perf.Collect();
        var cpuMhz = SystemMath.CurrentMhz(SystemInfo.BaseMhz, perfOpen ? PdhNative.ReadDouble(_cpuPerformance) : null);
        var diskIo = perfOpen
            ? new DiskIoReading(PdhNative.ReadDouble(_diskRead), PdhNative.ReadDouble(_diskWrite))
            : new DiskIoReading(null, null);

        var network = ReadNetwork(now, dt, rateOk, out var down, out var up);

        return new SystemReading(
            cpu,
            cpuMhz,
            ReadMemory(),
            down,
            up,
            network,
            ReadDisks(),
            diskIo,
            ReadBattery(),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public void Dispose() => _perf.Dispose();

    private static MemoryReading? ReadMemory()
    {
        if (!SystemNative.TryGetMemory(out var total, out var available) || total == 0) return null;
        var used = total > available ? total - available : 0;
        return new MemoryReading((double)used / total, (long)used, (long)total, (long)available);
    }

    /// <summary>Totals and rates over all up interfaces, plus the busiest interface with an IPv4 address.</summary>
    private NetworkReading ReadNetwork(double now, double dt, bool rateOk, out double? down, out double? up)
    {
        var refreshIp = now - _ipAt >= IpRefreshSeconds;
        if (refreshIp) _ipAt = now;

        long rx = 0, tx = 0;
        var loads = new List<InterfaceLoad>();
        foreach (var nic in ReadInterfaces())
        {
            rx += nic.Rx;
            tx += nic.Tx;

            if (refreshIp) _ipv4[nic.Id] = FirstIpv4(nic.Source) ?? "";
            var rate = 0.0;
            if (_nicPrev.TryGetValue(nic.Id, out var prev) && rateOk)
            {
                rate = RateCalculator.BytesPerSecond(prev.Rx, nic.Rx, dt) + RateCalculator.BytesPerSecond(prev.Tx, nic.Tx, dt);
            }
            _nicPrev[nic.Id] = (nic.Rx, nic.Tx);
            var ip = _ipv4.TryGetValue(nic.Id, out var addr) && addr.Length > 0 ? addr : null;
            loads.Add(new InterfaceLoad(nic.Name, ip, rate));
        }

        down = up = null;
        if (_netPrev is { } n && rateOk)
        {
            down = RateCalculator.BytesPerSecond(n.Rx, rx, dt);
            up = RateCalculator.BytesPerSecond(n.Tx, tx, dt);
        }
        _netPrev = (rx, tx);
        _netStart ??= (rx, tx);

        var busiest = SystemMath.BusiestInterface(loads);
        return new NetworkReading(
            busiest?.Name,
            busiest?.Ipv4,
            Math.Max(0, rx - _netStart.Value.Rx),
            Math.Max(0, tx - _netStart.Value.Tx));
    }

    private readonly record struct NicCounters(string Id, string Name, long Rx, long Tx, NetworkInterface Source);

    private static List<NicCounters> ReadInterfaces()
    {
        var list = new List<NicCounters>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    list.Add(new NicCounters(nic.Id, nic.Name, stats.BytesReceived, stats.BytesSent, nic));
                }
                catch (Exception)
                {
                    // Some virtual adapters do not expose counters; skip them.
                }
            }
        }
        catch (Exception)
        {
            // Interface enumeration failed this tick; the rates read as 0 on the next good one.
        }
        return list;
    }

    private static string? FirstIpv4(NetworkInterface nic)
    {
        try
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork) return address.Address.ToString();
            }
        }
        catch (Exception)
        {
            // Address lookup can fail on adapters that are going away.
        }
        return null;
    }

    private static IReadOnlyList<DiskReading> ReadDisks()
    {
        var disks = new List<DiskReading>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (disks.Count >= MaxDisks) break;
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

    /// <summary>Reads GetSystemPowerStatus through WinForms. BatteryLifeRemaining is the BatteryLifeTime estimate (-1 when unknown).</summary>
    private static BatteryReading? ReadBattery()
    {
        var status = WinForms.SystemInformation.PowerStatus;
        // Desktops report NoSystemBattery (and Unknown, which includes that bit): no battery tile content.
        if (status.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery)) return null;
        var life = status.BatteryLifeRemaining;
        return new BatteryReading(
            status.BatteryLifePercent,
            status.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.Charging),
            status.PowerLineStatus == WinForms.PowerLineStatus.Online,
            life >= 0 ? TimeSpan.FromSeconds(life) : null);
    }
}
