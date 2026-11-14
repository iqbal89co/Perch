using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perch.Core;
using Windows.Media.Control;
using WinRtStreams = Windows.Storage.Streams;

namespace Perch.Services;

/// <summary>One snapshot of what is playing. Immutable; a new one is published on every change.</summary>
public sealed record MediaSnapshot
{
    public string SourceApp { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public BitmapSource? Artwork { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>When Position was reported; used to extrapolate the live position.</summary>
    public DateTimeOffset PositionUpdatedAt { get; init; }
    public bool CanSeek { get; init; }

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    /// <summary>Best estimate of the current position, moving forward while playing.</summary>
    public TimeSpan LivePosition(DateTimeOffset now)
    {
        var pos = Position;
        if (IsPlaying) pos += now - PositionUpdatedAt;
        if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }
}

/// <summary>
/// Follows Windows' system media controls (GSMTC): Spotify, browsers, Media Player and anything
/// else that reports to the Windows media flyout. All events are raised on the UI thread.
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artworkKey = "";
    private BitmapSource? _artwork;
    private int _refreshGeneration;

    public MediaSnapshot? Current { get; private set; }

    /// <summary>Raised on the UI thread whenever Current changes.</summary>
    public event Action<MediaSnapshot?>? Changed;

    public MediaService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    private bool _demo;

    /// <summary>Shows a made-up track instead of the system's (used by --demo-media for screenshots and tests).</summary>
    internal void ShowDemo(MediaSnapshot snapshot)
    {
        _demo = true;
        Current = snapshot;
        Changed?.Invoke(Current);
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Follow(_manager.GetCurrentSession());
            _manager.SessionsChanged += (_, _) => Follow(_manager.GetCurrentSession());
            Follow(_manager.GetCurrentSession());
            Log.Info("media: watching system media sessions");
        }
        catch (Exception ex)
        {
            Log.Error("media: system media controls unavailable", ex);
        }
    }

    private void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session) && session != null) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.PlaybackInfoChanged -= OnPlaybackInfo;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaProperties;
            _session.PlaybackInfoChanged += OnPlaybackInfo;
            _session.TimelinePropertiesChanged += OnTimeline;
            Log.Info($"media: following {_session.SourceAppUserModelId}");
        }
        _ = RefreshAsync();
    }

    private void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var session = _session;
        MediaSnapshot? snapshot = null;
        try
        {
            if (session != null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                var key = $"{session.SourceAppUserModelId}|{props?.Title}|{props?.Artist}|{props?.AlbumTitle}";
                if (key != _artworkKey)
                {
                    _artworkKey = key;
                    _artwork = props?.Thumbnail != null ? await LoadArtworkAsync(props.Thumbnail) : null;
                }
                var duration = timeline.EndTime - timeline.StartTime;
                snapshot = new MediaSnapshot
                {
                    SourceApp = FriendlyAppName(session.SourceAppUserModelId),
                    Title = props?.Title ?? "",
                    Artist = props?.Artist ?? "",
                    Album = props?.AlbumTitle ?? "",
                    Artwork = _artwork,
                    IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    Position = timeline.Position - timeline.StartTime,
                    Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                    PositionUpdatedAt = timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime : DateTimeOffset.Now,
                    CanSeek = playback.Controls.IsPlaybackPositionEnabled,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"media: refresh failed ({ex.Message})");
        }

        if (generation != _refreshGeneration || _demo) return; // a newer refresh is on its way, or a demo track is showing
        await _dispatcher.InvokeAsync(() =>
        {
            Current = snapshot is { HasTrack: true } ? snapshot : null;
            Changed?.Invoke(Current);
        });
    }

    private static async Task<BitmapSource?> LoadArtworkAsync(WinRtStreams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"media: artwork unreadable ({ex.Message})");
            return null;
        }
    }

    internal static string FriendlyAppName(string aumid)
    {
        var id = aumid.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("zunemusic") || id.Contains("media player")) return "Media Player";
        if (id.Contains("applemusic") || id.Contains("apple")) return "Apple Music";
        var name = aumid.Split('!', '\\', '/').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "Media" : name;
    }

    public async Task PlayPauseAsync()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Log.Warn($"media: play/pause failed ({ex.Message})"); }
    }

    public async Task NextAsync()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); }
        catch (Exception ex) { Log.Warn($"media: next failed ({ex.Message})"); }
    }

    public async Task PreviousAsync()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); }
        catch (Exception ex) { Log.Warn($"media: previous failed ({ex.Message})"); }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            if (_session == null) return;
            var start = _session.GetTimelineProperties().StartTime;
            await _session.TryChangePlaybackPositionAsync((start + position).Ticks);
        }
        catch (Exception ex) { Log.Warn($"media: seek failed ({ex.Message})"); }
    }
}
