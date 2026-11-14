using System.Windows.Threading;
using Perch.Core;

namespace Perch.Services;

/// <summary>
/// Reports whether a full-screen app (game, video, presentation) is in front on the island's monitor.
/// Polls once a second; cheap and avoids global hooks.
/// </summary>
public sealed class FullscreenWatcher
{
    private readonly DispatcherTimer _timer;
    private readonly Func<IntPtr> _ownWindow;
    private bool _isFullscreen;

    public event Action<bool>? Changed;

    public bool IsFullscreen => _isFullscreen;

    public FullscreenWatcher(Func<IntPtr> ownWindow)
    {
        _ownWindow = ownWindow;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Check();
    }

    public void Start() => _timer.Start();

    private void Check()
    {
        bool now;
        try { now = Detect(); }
        catch (Exception ex)
        {
            Log.Warn($"fullscreen: check failed ({ex.Message})");
            now = false;
        }
        if (now == _isFullscreen) return;
        _isFullscreen = now;
        Log.Info(now ? "fullscreen: app in front, hiding" : "fullscreen: gone, showing");
        Changed?.Invoke(now);
    }

    private bool Detect()
    {
        var own = _ownWindow();
        if (Native.SHQueryUserNotificationState(out var state) == 0 &&
            state is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
            return true;

        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == own || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow())
            return false;
        var cls = Native.ClassNameOf(fg);
        // Desktop, taskbar and shell surfaces (Start, Search, notification centre) are never "an app in front".
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland")
            return false;
        if (!Native.IsWindowVisible(fg) || Native.IsCloaked(fg)) return false;
        // A maximized window (e.g. with an auto-hiding taskbar) can cover the monitor without being full screen.
        if (Native.IsZoomed(fg)) return false;
        var style = (long)Native.GetWindowLongPtr(fg, Native.GWL_STYLE);
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;

        var fgMonitor = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (own != IntPtr.Zero && fgMonitor != Native.MonitorFromWindow(own, Native.MONITOR_DEFAULTTONEAREST))
            return false;
        var m = Native.MonitorInfoFor(fgMonitor).rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
