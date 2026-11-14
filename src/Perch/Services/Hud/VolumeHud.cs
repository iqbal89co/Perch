using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Perch.Core;

namespace Perch.Services.Hud;

/// <summary>
/// Shows a short volume glance on the resting island whenever the system volume or mute state changes.
/// Follows the default output device, so switching headphones/speakers re-subscribes automatically.
/// NAudio calls back on a COM thread; every callback marshals to the UI dispatcher.
/// </summary>
public sealed class VolumeHud : IIslandService, IMMNotificationClient, IDisposable
{
    public const string Key = "hud";
    public const double HudWidth = 236;
    public const double BarWidth = 120;
    public const string GlyphMute = "";
    public const string GlyphLow = "";
    public const string GlyphMedium = "";
    public const string GlyphHigh = "";

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    private IslandContext? _ctx;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private AudioEndpointVolumeNotificationDelegate? _handler;
    private DateTime _ignoreUntil = DateTime.MinValue;
    private volatile bool _disposed;

    /// <summary>Glyph for a volume level in 0..1. Muted or silent shows the mute glyph.</summary>
    public static string VolumeGlyph(double level, bool muted)
    {
        if (muted || !(level > 0)) return GlyphMute;
        if (level < 0.34) return GlyphLow;
        if (level < 0.67) return GlyphMedium;
        return GlyphHigh;
    }

    /// <summary>Whole-number percent for a 0..1 level, clamped.</summary>
    public static int Percent(double level)
    {
        if (!double.IsFinite(level)) return 0;
        return (int)Math.Round(Math.Clamp(level, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }

    public void Start(IslandContext ctx)
    {
        _ctx = ctx;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Log.Error("volume hud: audio enumerator unavailable", ex);
            return;
        }
        _ignoreUntil = DateTime.UtcNow + Settle;
        Attach();
    }

    private void Attach()
    {
        Detach();
        if (_enumerator == null) return;
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _endpoint = _device.AudioEndpointVolume;
            _handler = OnVolumeNotification;
            _endpoint.OnVolumeNotification += _handler;
        }
        catch (Exception ex)
        {
            Log.Warn($"volume hud: no default output device ({ex.Message})");
            Detach();
        }
    }

    private void Detach()
    {
        try
        {
            if (_endpoint != null && _handler != null) _endpoint.OnVolumeNotification -= _handler;
        }
        catch (Exception ex)
        {
            Log.Warn($"volume hud: unsubscribe failed ({ex.Message})");
        }
        _handler = null;
        _endpoint?.Dispose();
        _endpoint = null;
        _device?.Dispose();
        _device = null;
    }

    // Runs on a COM thread.
    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        try
        {
            var level = data.MasterVolume;
            var muted = data.Muted;
            Post(() => Show(level, muted));
        }
        catch (Exception ex)
        {
            Log.Warn($"volume hud: notification failed ({ex.Message})");
        }
    }

    private void Show(float level, bool muted)
    {
        var ctx = _ctx;
        if (ctx == null || _disposed || !ctx.Settings.HudVolume) return;
        if (DateTime.UtcNow < _ignoreUntil) return;

        var clamped = double.IsFinite(level) ? Math.Clamp((double)level, 0, 1) : 0;
        ctx.Activities.Post(new PillActivity
        {
            Key = Key,
            Priority = 100,
            Width = HudWidth,
            ExpiresAt = DateTime.Now + Lifetime,
            Leading = BuildGlyph(VolumeGlyph(clamped, muted)),
            Center = BuildBar(clamped, muted),
            Trailing = BuildPercent(Percent(clamped)),
        });
    }

    /// <summary>Runs an action on the UI thread; never throws back into the caller (COM thread).</summary>
    private void Post(Action action)
    {
        var ctx = _ctx;
        if (ctx == null || _disposed) return;
        try
        {
            ctx.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                try { action(); }
                catch (Exception ex) { Log.Error("volume hud failed", ex); }
            }));
        }
        catch (Exception ex)
        {
            Log.Warn($"volume hud: dispatch failed ({ex.Message})");
        }
    }

    private static TextBlock BuildGlyph(string glyph)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
        return icon;
    }

    private static Grid BuildBar(double level, bool muted)
    {
        var grid = new Grid { Width = BarWidth, Height = 5, VerticalAlignment = VerticalAlignment.Center };

        var track = new Border { Height = 5, CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");

        var fill = new Border
        {
            Height = 5,
            Width = BarWidth * level,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2.5),
        };
        fill.SetResourceReference(Border.BackgroundProperty, muted ? "IslandSubtle" : "IslandAccent");

        grid.Children.Add(track);
        grid.Children.Add(fill);
        return grid;
    }

    private static TextBlock BuildPercent(int percent)
    {
        var text = new TextBlock
        {
            Text = percent.ToString(CultureInfo.InvariantCulture),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Width = 24,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
        return text;
    }

    // IMMNotificationClient: device topology changes, also on COM threads.

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        Post(() =>
        {
            Attach();
            _ignoreUntil = DateTime.UtcNow + Settle;
        });
    }

    public void OnDeviceAdded(string pwstrDeviceId) => Post(() => { if (_endpoint == null) Attach(); });

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Post(() => { if (_endpoint == null) Attach(); });

    public void OnDeviceRemoved(string deviceId) { }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator?.UnregisterEndpointNotificationCallback(this); }
        catch (Exception ex) { Log.Warn($"volume hud: unregister failed ({ex.Message})"); }
        Detach();
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
