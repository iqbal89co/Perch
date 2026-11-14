using System.Diagnostics;

namespace Perch.Pages.SystemMonitor;

/// <summary>
/// Top processes by CPU. Called from a background task only, one call at a time (the page guards this).
/// The first call only records a baseline and returns nothing.
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
        var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.Id == 0) continue; // System Idle Process
                    var total = p.TotalProcessorTime;
                    next[p.Id] = total;
                    if (!hasPrev || !_prev.TryGetValue(p.Id, out var before)) continue;
                    var name = p.ProcessName;
                    byName[name] = byName.GetValueOrDefault(name) + SystemMath.ProcessPercent(before, total, elapsed, _cores);
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
        return hasPrev ? SystemMath.TopProcesses(byName, count) : Array.Empty<ProcessUsage>();
    }
}
