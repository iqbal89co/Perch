using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.Quran;

/// <summary>
/// Streams one surah's MP3. The WPF System.Windows.Media.MediaPlayer refuses http(s) sources ("Only site-of-origin
/// pack URIs are supported for media"), so this uses the Windows.Media.Playback player, which streams the URL.
/// The page owns it for the whole session, so audio keeps playing while the island is collapsed or another tab is
/// shown. The position timer only runs while playing: every 50 ms when the Quran tab is visible (SetFast), every
/// second otherwise. PositionMs is the position the UI should show: it is kept while paused, and before the file
/// has opened it is the requested start position.
/// </summary>
public sealed class QuranPlayer
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Windows.Media.Playback.MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _opened;
    private bool _wantPlay;
    private long _positionMs;
    private long? _pendingSeekMs;
    private string _url = "";

    /// <summary>Raised on each timer tick while playing (UI thread).</summary>
    public event Action? Ticked;

    /// <summary>Raised when the file reaches its end (UI thread). The player is already paused at that point.</summary>
    public event Action? Ended;

    /// <summary>Raised when the media cannot be opened or played (UI thread).</summary>
    public event Action<string>? Failed;

    public bool IsPlaying => _wantPlay;

    public long PositionMs => _positionMs;

    /// <summary>The file's length in ms once it is known, else 0.</summary>
    public long DurationMs
    {
        get
        {
            if (!_opened) return 0;
            var duration = _player.NaturalDuration;
            return duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : 0;
        }
    }

    public QuranPlayer()
    {
        _player.AutoPlay = false;
        // Keep the surah out of the system media overlay and the Music tab, which follow the system sessions.
        _player.CommandManager.IsEnabled = false;
        _player.MediaOpened += (_, _) => OnUi(OnOpened);
        _player.MediaEnded += (_, _) => OnUi(OnMediaEnded);
        _player.MediaFailed += (_, e) =>
        {
            var message = e.ErrorMessage ?? "unknown error";
            OnUi(() => OnMediaFailed(message));
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void SetFast(bool fast) =>
        _timer.Interval = fast ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(1);

    /// <summary>Loads a new file. It starts at startMs and plays only when play is true.</summary>
    public void Open(string url, long startMs, bool play)
    {
        _timer.Stop();
        _wantPlay = false;
        _opened = false;
        _url = url;
        _positionMs = Math.Max(0, startMs);
        _pendingSeekMs = _positionMs;
        _player.Pause();
        _player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url, UriKind.Absolute));
        if (play) Play();
    }

    public void Play()
    {
        if (_url.Length == 0) return;
        _wantPlay = true;
        _player.Play();
        _timer.Start();
    }

    public void Pause()
    {
        if (_opened && _wantPlay) _positionMs = (long)_player.Position.TotalMilliseconds;
        _wantPlay = false;
        _player.Pause();
        _timer.Stop();
    }

    public void Seek(long ms)
    {
        _positionMs = Math.Max(0, ms);
        if (_opened) _player.Position = TimeSpan.FromMilliseconds(_positionMs);
        else _pendingSeekMs = _positionMs;
    }

    private void Tick()
    {
        if (_opened && _wantPlay) _positionMs = (long)_player.Position.TotalMilliseconds;
        Ticked?.Invoke();
    }

    /// <summary>Player events may arrive off the UI thread; everything below runs on it.</summary>
    private void OnUi(Action action)
    {
        if (_ui.CheckAccess()) action();
        else _ui.BeginInvoke(action);
    }

    private void OnOpened()
    {
        _opened = true;
        if (_pendingSeekMs is { } start)
        {
            _player.Position = TimeSpan.FromMilliseconds(start);
            _pendingSeekMs = null;
        }
        Log.Info($"quran: opened {_url} duration {_player.NaturalDuration}");
    }

    private void OnMediaEnded()
    {
        _wantPlay = false;
        _timer.Stop();
        _positionMs = DurationMs;
        Ended?.Invoke();
    }

    private void OnMediaFailed(string message)
    {
        _wantPlay = false;
        _timer.Stop();
        Log.Error($"quran: media failed for {_url}: {message}");
        Failed?.Invoke(message);
    }
}
