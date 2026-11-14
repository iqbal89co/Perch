using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perch.Core;
using Perch.Services;

namespace Perch.Pages.Media;

/// <summary>Now playing: artwork, track info, seek bar, transport controls and time-synced lyrics.</summary>
public partial class MediaPage : IslandPage
{
    private enum LyricsView { Off, Loading, Synced, Plain, NotFound, Error }

    private static readonly TimeSpan SeekHold = TimeSpan.FromMilliseconds(800);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private MediaSnapshot? _snap;
    private BitmapSource? _art;
    private bool _dragging;
    private DateTime _seekHoldUntilUtc;

    private string? _trackKey;
    private CancellationTokenSource? _lyricsCts;
    private LyricsView _view = LyricsView.Off;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private string _plain = "";
    private int _currentIndex = int.MinValue;

    public override string Id => "media";
    public override string Title => "Music";
    public override string Glyph => "";
    public override int Order => 10;

    public MediaPage()
    {
        InitializeComponent();

        PrevButton.Click += (_, _) => Run(() => Ctx.Media.PreviousAsync());
        PlayButton.Click += (_, _) => Run(() => Ctx.Media.PlayPauseAsync());
        NextButton.Click += (_, _) => Run(() => Ctx.Media.NextAsync());

        // Dragging: the thumb reports the drag end; a plain click on the track is committed on mouse up.
        Seek.ApplyTemplate();
        if (Seek.Template?.FindName("PART_Track", Seek) is Track track && track.Thumb is { } thumb)
        {
            thumb.DragStarted += (_, _) => _dragging = true;
            thumb.DragCompleted += (_, _) =>
            {
                _dragging = false;
                CommitSeek();
            };
        }
        Seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) CommitSeek();
        };

        _timer.Tick += (_, _) => Tick();

        ApplyLyricsSetting();
        Ctx.Media.Changed += OnMediaChanged;
        Ctx.SettingsChanged += OnSettingsChanged;
        OnMediaChanged(Ctx.Media.Current);
    }

    public override void OnShown()
    {
        Tick();
        _timer.Start();
    }

    public override void OnHidden() => _timer.Stop();

    private void OnSettingsChanged()
    {
        ApplyLyricsSetting();
        OnMediaChanged(Ctx.Media.Current);
    }

    private void OnMediaChanged(MediaSnapshot? snap)
    {
        if (snap is not { HasTrack: true } s)
        {
            _snap = null;
            EmptyPanel.Visibility = Visibility.Visible;
            PlayerGrid.Visibility = Visibility.Collapsed;
            SyncLyrics(null, null);
            return;
        }

        _snap = s;
        EmptyPanel.Visibility = Visibility.Collapsed;
        PlayerGrid.Visibility = Visibility.Visible;

        SourceText.Text = s.SourceApp;
        TitleText.Text = s.Title;
        ArtistText.Text = s.Artist;
        PlayButton.Content = s.IsPlaying ? "" : "";
        PlayButton.ToolTip = s.IsPlaying ? "Pause" : "Play";

        if (!ReferenceEquals(_art, s.Artwork))
        {
            _art = s.Artwork;
            ArtBrush.ImageSource = _art;
            ArtImage.Visibility = _art == null ? Visibility.Collapsed : Visibility.Visible;
            ArtGlyph.Visibility = _art == null ? Visibility.Visible : Visibility.Collapsed;
        }

        SyncLyrics(s, Ctx.Settings.Lyrics ? $"{s.Artist}\u001f{s.Title}" : null);
        Tick();
    }

    /// <summary>Refreshes the seek bar, the times and the current lyric line.</summary>
    private void Tick()
    {
        if (_snap is not { HasTrack: true } s) return;

        var duration = s.Duration;
        var canSeek = s.CanSeek && duration > TimeSpan.Zero;
        Seek.IsEnabled = canSeek;
        Seek.Opacity = canSeek ? 1 : 0.5;
        if (duration > TimeSpan.Zero) Seek.Maximum = duration.TotalSeconds;

        // Don't fight the user: while dragging (or just after a seek) the slider is the source of truth.
        var holding = _dragging || DateTime.UtcNow < _seekHoldUntilUtc;
        var position = holding ? TimeSpan.FromSeconds(Seek.Value) : s.LivePosition(DateTimeOffset.Now);
        if (!holding)
            Seek.Value = duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds, 0, Seek.Maximum) : 0;

        ElapsedText.Text = Format.Duration(position);
        RemainingText.Text = duration > TimeSpan.Zero ? "-" + Format.Duration(duration - position) : "";

        if (_view == LyricsView.Synced)
            UpdateLyricLines(position + TimeSpan.FromMilliseconds(Ctx.Settings.LyricsOffsetMs));
    }

    private void CommitSeek()
    {
        if (_snap is not { CanSeek: true, HasTrack: true } s || s.Duration <= TimeSpan.Zero) return;
        var target = TimeSpan.FromSeconds(Math.Clamp(Seek.Value, 0, s.Duration.TotalSeconds));
        _seekHoldUntilUtc = DateTime.UtcNow + SeekHold;
        Run(() => Ctx.Media.SeekAsync(target));
    }

    private void ApplyLyricsSetting()
    {
        var on = Ctx.Settings.Lyrics;
        LeftColumn.Width = on ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        SpacerColumn.Width = on ? new GridLength(16) : new GridLength(0);
        LyricsColumn.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        LyricsCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LeftPane.HorizontalAlignment = on ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
    }

    /// <summary>
    /// Starts a lyrics lookup when the track (artist + title) changes, or clears lyrics when there is none.
    /// A result that arrives for a track that is no longer current is dropped.
    /// </summary>
    private void SyncLyrics(MediaSnapshot? s, string? key)
    {
        if (key == _trackKey) return;
        _trackKey = key;
        _lyricsCts?.Cancel();
        _lyricsCts = null;
        _lines = Array.Empty<LyricLine>();
        _plain = "";
        _currentIndex = int.MinValue;
        _view = key == null ? LyricsView.Off : LyricsView.Loading;

        if (key != null && s != null)
        {
            var cts = new CancellationTokenSource();
            _lyricsCts = cts;
            _ = LoadLyricsAsync(key, s.Artist, s.Title, s.Album, s.Duration, cts.Token);
        }
        RenderLyricsView();
    }

    private async Task LoadLyricsAsync(string key, string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        LyricsResult result;
        try
        {
            result = await LyricsClient.FetchAsync(artist, title, album, duration, ct);
        }
        catch (Exception ex)
        {
            Log.Error("lyrics: unexpected failure", ex);
            result = new LyricsResult(LyricsStatus.Error, Array.Empty<LyricLine>(), "");
        }

        if (ct.IsCancellationRequested || key != _trackKey) return;

        _lines = result.Lines;
        _plain = result.Plain;
        _currentIndex = int.MinValue;
        _view = result.Status switch
        {
            LyricsStatus.Synced => LyricsView.Synced,
            LyricsStatus.Plain => LyricsView.Plain,
            LyricsStatus.NotFound => LyricsView.NotFound,
            _ => LyricsView.Error,
        };
        RenderLyricsView();
        Tick();
    }

    private void RenderLyricsView()
    {
        LyricsStatusText.Text = _view switch
        {
            LyricsView.Loading => "Looking for lyrics…",
            LyricsView.NotFound => "No lyrics found for this song",
            LyricsView.Error => "Lyrics are unavailable right now",
            _ => "",
        };
        LyricsStatusText.Visibility = _view is LyricsView.Loading or LyricsView.NotFound or LyricsView.Error
            ? Visibility.Visible : Visibility.Collapsed;
        PlainScroll.Visibility = _view == LyricsView.Plain ? Visibility.Visible : Visibility.Collapsed;
        PlainText.Text = _plain;
        SyncedPanel.Visibility = _view == LyricsView.Synced ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLyricLines(TimeSpan position)
    {
        var index = LrcParser.IndexAt(_lines, position);
        if (index == _currentIndex) return;
        _currentIndex = index;

        var current = LineText(index);
        CurrentLyric.Text = current.Length > 0 ? current : index >= 0 ? "♪" : "";
        PrevLyric.Text = LineText(index - 1);
        NextLyric.Text = LineText(index + 1);

        CurrentLyric.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private string LineText(int index) => index >= 0 && index < _lines.Count ? _lines[index].Text : "";

    /// <summary>Fire-and-forget media command; failures are logged, never thrown into the UI.</summary>
    private static async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error("media: command failed", ex);
        }
    }
}
