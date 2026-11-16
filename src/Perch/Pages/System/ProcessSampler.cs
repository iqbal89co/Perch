using System.Diagnostics;

namespace Perch.Pages.SystemMonitor;

/// <summary>
/// Top processes by CPU, grouped by name. Called from a background task only, one call at a time (the page guards this).
/// The first call only records a baseline and returns nothing. The baseline is kept between page openings.
/// </summary>
internal sealed class ProcessSampler
{
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);
    private Dictionary<int, TimeSpan> _prev = new();
    private DateTime _prevAt = DateTime.MinValue;

    public IReadOnlyList<ProcessUsage> Sample(int count)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _prevAt;
        var hasPrev = _prevAt != DateTime.MinValue && elapsed > TimeSpan.Zero;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return Array.Empty<ProcessUsage>();
        }

        var next = new Dictionary<int, TimeSpan>();
        var byName = new Dictionary<string, (double Fraction, long Memory)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.Id == 0) continue; // System Idle Process
                    var total = p.TotalProcessorTime;
                    next[p.Id] = total;
                    var name = p.ProcessName;
                    var (fraction, memory) = byName.GetValueOrDefault(name);
                    if (hasPrev && _prev.TryGetValue(p.Id, out var before))
                    {
                        fraction += SystemMath.ProcessPercent(before, total, elapsed, _cores);
                    }
                    byName[name] = (fraction, memory + p.WorkingSet64);
                }
                catch (Exception)
                {
                    // Access denied (protected processes) or the process exited mid-sample.
                }
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        _prev = next;
        _prevAt = now;
        if (!hasPrev) return Array.Empty<ProcessUsage>();
        return SystemMath.TopProcesses(
            byName.Select(kv => new ProcessUsage(kv.Key, kv.Value.Fraction, kv.Value.Memory)), count);
    }
}
