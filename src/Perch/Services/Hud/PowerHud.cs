using System.Windows.Threading;
using Microsoft.Win32;
using Perch.Core;
using WinForms = System.Windows.Forms;

namespace Perch.Services.Hud;

/// <summary>
/// Glances at the battery: plug/unplug, low battery (20% and 10%, once per discharge) and full charge.
/// Polls every 5 s and also reacts to power status changes right away. Machines without a battery
/// (desktops) are skipped entirely.
/// </summary>
public sealed class PowerHud : IIslandService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(3);

    private readonly PowerNoticeLogic _logic = new();
    private IslandContext? _ctx;
    private DispatcherTimer? _timer;
    private PowerModeChangedEventHandler? _modeHandler;
    private volatile bool _disposed;

    public void Start(IslandContext ctx)
    {
        _ctx = ctx;
        if (!HasBattery())
        {
            Log.Info("power hud: no system battery, skipped");
            return;
        }

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        _modeHandler = OnPowerModeChanged;
        SystemEvents.PowerModeChanged += _modeHandler;

        Poll(); // baseline reading, produces no notice
    }

    private static bool HasBattery()
    {
        try
        {
            return !WinForms.SystemInformation.PowerStatus.BatteryChargeStatus
                .HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery);
        }
        catch (Exception ex)
        {
            Log.Warn($"power hud: battery query failed ({ex.Message})");
            return false;
        }
    }

    // SystemEvents may raise on a non-UI thread; hop to the dispatcher before touching the island.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;
            var ctx = _ctx;
            if (ctx == null || _disposed) return;
            ctx.Dispatcher.BeginInvoke(new Action(Poll));
        }
        catch (Exception ex)
        {
            Log.Warn($"power hud: mode change failed ({ex.Message})");
        }
    }

    private void Poll()
    {
        var ctx = _ctx;
        if (ctx == null || _disposed) return;
        try
        {
            var status = WinForms.SystemInformation.PowerStatus;
            if (status.PowerLineStatus == WinForms.PowerLineStatus.Unknown) return;
            var fraction = status.BatteryLifePercent;
            if (!(fraction >= 0)) return; // unknown charge (-1) or NaN

            var percent = (int)Math.Round(Math.Clamp(fraction, 0f, 1f) * 100, MidpointRounding.AwayFromZero);
            var sample = new PowerSample(status.PowerLineStatus == WinForms.PowerLineStatus.Online, percent);

            var notice = _logic.Observe(sample);
            if (notice is null || !ctx.Settings.HudBattery) return;
            ctx.Notify(notice.Glyph, notice.Text, NoticeDuration, key: "power");
        }
        catch (Exception ex)
        {
            Log.Warn($"power hud: poll failed ({ex.Message})");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_modeHandler != null) SystemEvents.PowerModeChanged -= _modeHandler;
        _modeHandler = null;
        _timer?.Stop();
        _timer = null;
    }
}
