using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Perch.Core;

/// <summary>
/// Gives memory back to Windows while Perch sits idle. A few seconds after the island folds away (and after
/// startup or a background scan), it runs one compacting collection that releases the GC's spare memory and then
/// empties the working set, so only the pages the resting pill actually uses come back. Pages handed back go to
/// Windows' standby list: they cost nothing to keep and are quick to fault in when the island opens again.
/// At most once every 30 seconds; never while the island is open. UI thread only.
/// </summary>
public static class MemoryTrim
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    private static DispatcherTimer? _timer;
    private static Func<bool>? _isIdle;
    private static long _lastTicks = long.MinValue / 2;

    /// <summary>Sets up trimming. <paramref name="isIdle"/> says whether the island is at rest right now.</summary>
    public static void Initialize(Dispatcher dispatcher, Func<bool> isIdle)
    {
        _isIdle = isIdle;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = Delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _timer.Interval = Delay;
            TrimNow();
        };
    }

    /// <summary>Asks for a trim a few seconds from now (restarts the wait if one is already pending).</summary>
    public static void Request()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Interval = Delay;
        _timer.Start();
    }

    /// <summary>Cancels a pending trim (the island opened).</summary>
    public static void Cancel() => _timer?.Stop();

    private static void TrimNow()
    {
        if (_isIdle?.Invoke() != true) return;
        var now = Environment.TickCount64;
        if (now - _lastTicks < MinGap.TotalMilliseconds)
        {
            // Too soon after the last one: try again when the gap has passed.
            _timer!.Interval = TimeSpan.FromMilliseconds(MinGap.TotalMilliseconds - (now - _lastTicks) + 50);
            _timer.Start();
            return;
        }
        _lastTicks = now;

        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            using var me = Process.GetCurrentProcess();
            EmptyWorkingSet(me.Handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"memory trim failed ({ex.Message})");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
