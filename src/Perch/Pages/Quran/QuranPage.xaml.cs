using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Perch.Core;
using Perch.Shell;

namespace Perch.Pages.Quran;

/// <summary>
/// The Quran tab: browse the 114 surahs, then listen while the Arabic follows the recitation word by word, with
/// translations under it. The page lives for the whole session and owns the player, so audio continues when the
/// island collapses or another tab is shown. Views: Browse, Reading (now playing) and Settings.
/// </summary>
public partial class QuranPage : IslandPage
{
    private const string PillKey = "quran";
    private const int PillPriority = 50;
    private const string BookGlyph = "";
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    /// <summary>Drives the scroll position of ReadScroll so auto-scroll can be animated.</summary>
    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        "ScrollOffset", typeof(double), typeof(QuranPage),
        new PropertyMetadata(0.0, (d, e) => ((QuranPage)d).ReadScroll.ScrollToVerticalOffset((double)e.NewValue)));

    private enum QuranView { Browse, Reading, Settings }

    private enum LoadState { Idle, Loading, Ready, Error }

    private readonly QuranSettings _settings;
    private readonly QuranPlayer _player = new();
    private readonly IReadOnlyList<Surah> _surahs = QuranChapters.All;
    private readonly List<AyahView> _views = new();

    private QuranView _view = QuranView.Browse;
    private QuranView _settingsReturn = QuranView.Browse;
    private LoadState _state = LoadState.Idle;
    private string _error = "";
    private string _search = "";
    private bool _pickingReciter;
    private bool _visible;
    private bool _restorePending;
    private bool _lastAutoPlay;
    private bool _seekDragging;
    private Surah? _surah;
    private QuranTrack? _track;
    private int _ayahIndex = -1;
    private int _hlIndex = int.MinValue;
    private int _hlKey = int.MinValue;
    private Run? _hlRun;
    private CancellationTokenSource? _loadCts;
    private DateTime _lastSaveUtc = DateTime.UtcNow;

    private const double IslandStripWidth = 500;
    private QuranPillStrip? _pillStrip;
    private TextBlock? _pillRef;
    private bool _stripPosted;

    public override string Id => "quran";
    public override string Title => "Quran";
    public override string Glyph => BookGlyph;
    public override int Order => 57;

    public QuranPage()
    {
        InitializeComponent();
        _settings = JsonStore.Load<QuranSettings>("quran");

        _player.Ticked += OnPlayerTick;
        _player.Ended += OnSurahEnded;
        _player.Failed += OnAudioFailed;
        // Prayer time (and anything else) can ask Perch's own audio to stop.
        Ctx.PauseAudioRequested += () =>
        {
            if (_player.IsPlaying) TogglePlay();
        };

        Seek.ApplyTemplate();
        if (Seek.Template?.FindName("PART_Track", Seek) is Track seekTrack && seekTrack.Thumb is { } thumb)
        {
            thumb.DragStarted += (_, _) => _seekDragging = true;
            thumb.DragCompleted += (_, _) =>
            {
                _seekDragging = false;
                CommitSeek();
            };
        }
        Seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_seekDragging) CommitSeek();
        };

        ReciterButton.Click += (_, _) => TogglePicker();
        SearchBox.TextChanged += (_, _) => OnSearchChanged();
        BrowseSettingsButton.Click += (_, _) => ShowSettings(QuranView.Browse);
        NowSettingsButton.Click += (_, _) => ShowSettings(QuranView.Reading);
        SettingsBackButton.Click += (_, _) => LeaveSettings();
        BackButton.Click += (_, _) => ShowBrowse();
        RetryButton.Click += (_, _) => Retry();
        PlayButton.Click += (_, _) => TogglePlay();
        PrevAyahButton.Click += (_, _) => StepAyah(-1);
        NextAyahButton.Click += (_, _) => StepAyah(1);
        PrevSurahButton.Click += (_, _) => StepSurah(-1);
        NextSurahButton.Click += (_, _) => StepSurah(1);
        RepeatButton.Click += (_, _) =>
        {
            _settings.Repeat = QuranRules.NextRepeat(_settings.Repeat);
            SaveSettings();
            UpdateAll();
        };

        TranslationButton.Click += (_, _) =>
        {
            _settings.Translation = (TranslationMode)(((int)_settings.Translation + 1) % 4);
            SettingsChanged(reading: true);
        };
        EnglishButton.Click += (_, _) =>
        {
            _settings.EnglishTranslator = _settings.EnglishId == QuranIds.AbdelHaleem
                ? QuranIds.SaheehInternational
                : QuranIds.AbdelHaleem;
            SettingsChanged(reading: true);
        };
        IndonesianButton.Click += (_, _) =>
        {
            _settings.IndonesianTranslator = _settings.IndonesianId == QuranIds.KingFahadComplex
                ? QuranIds.Kemenag
                : QuranIds.KingFahadComplex;
            SettingsChanged(reading: true);
        };
        ArabicMinusButton.Click += (_, _) =>
        {
            _settings.ArabicSize -= 2;
            SettingsChanged(reading: true);
        };
        ArabicPlusButton.Click += (_, _) =>
        {
            _settings.ArabicSize += 2;
            SettingsChanged(reading: true);
        };
        TransliterationToggle.Click += (_, _) =>
        {
            _settings.Transliteration = TransliterationToggle.IsChecked == true;
            SettingsChanged(reading: true);
        };
        ContinueToggle.Click += (_, _) =>
        {
            _settings.ContinueNextSurah = ContinueToggle.IsChecked == true;
            SettingsChanged(reading: false);
        };
        IslandAyahToggle.Click += (_, _) =>
        {
            _settings.ShowAyahOnIsland = IslandAyahToggle.IsChecked == true;
            SettingsChanged(reading: false);
            ApplyInterval();
            RemovePill();
            PostPill();
        };

        // Reopen on the last surah, paused. The audio is only opened once the tab is first shown.
        if (QuranRules.HasSurah(_settings.LastSurah))
        {
            _restorePending = true;
            _view = QuranView.Reading;
            _surah = QuranChapters.Get(_settings.LastSurah);
        }

        RebuildList();
        UpdateAll();
    }

    public override void OnShown()
    {
        _visible = true;
        ApplyInterval();
        if (_restorePending)
        {
            _restorePending = false;
            _ = LoadAsync(_settings.LastSurah, autoPlay: false, startMs: _settings.LastPositionMs);
        }
        UpdateAll();
        if (_track != null)
        {
            RenderTransport(_player.PositionMs);
            ScrollToCurrent(instant: true);
        }
    }

    public override void OnHidden()
    {
        // Audio keeps playing; only the UI work stops and the timer drops to once a second.
        _visible = false;
        ApplyInterval();
        SaveNow();
    }

    // ---- loading ----------------------------------------------------------------------------------

    /// <summary>Loads the text and timings for a surah, then opens its audio. Stale loads are cancelled.</summary>
    private async Task LoadAsync(int surahId, bool autoPlay, long startMs)
    {
        surahId = Math.Clamp(surahId, 1, _surahs.Count);
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        _player.Pause();
        RemovePill();
        var surah = QuranChapters.Get(surahId);
        _surah = surah;
        _track = null;
        _ayahIndex = -1;
        _lastAutoPlay = autoPlay;
        _state = LoadState.Loading;
        _view = QuranView.Reading;
        _settings.LastSurah = surahId;
        _settings.LastPositionMs = Math.Max(0, startMs);
        SaveSettings();
        ClearReading();
        UpdateAll();

        try
        {
            var reciter = Reciter.Find(_settings.ReciterId);
            var verses = await QuranApi.GetVersesAsync(surahId, ct);
            var audio = await QuranApi.GetTimingsAsync(reciter.Id, surahId, ct);
            if (ct.IsCancellationRequested) return;

            _track = new QuranTrack(surah, verses.Ayahs, verses.WordLevel, audio, reciter);
            _state = LoadState.Ready;
            _ayahIndex = QuranSync.AyahIndexAt(audio.Ayahs, startMs);
            _player.Open(audio.AudioUrl, startMs, autoPlay);
            BuildReading();
            if (autoPlay) PostPill();
            UpdateAll();
            RenderTransport(startMs);
            ScrollToCurrent(instant: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer load replaced this one.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Log.Error("quran: could not load surah", ex);
            _state = LoadState.Error;
            _error = $"Could not load {surah.NameSimple}. Check your connection and try again.";
            UpdateAll();
        }
    }

    private void Retry()
    {
        if (_surah != null) _ = LoadAsync(_surah.Id, _lastAutoPlay, _player.PositionMs);
    }

    private void OnAudioFailed(string message)
    {
        RemovePill();
        _state = LoadState.Error;
        _error = "The audio could not be played. Check your connection and try again.";
        UpdateAll();
    }

    // ---- playback ---------------------------------------------------------------------------------

    private void TogglePlay()
    {
        if (_track == null) return;
        if (_state == LoadState.Error)
        {
            // The audio failed earlier: reopen it instead of playing a dead source.
            Retry();
            return;
        }
        if (_player.IsPlaying)
        {
            _player.Pause();
            RemovePill();
            SaveNow();
        }
        else
        {
            _player.Play();
            PostPill();
        }
        UpdatePlayButton();
    }

    /// <summary>Runs on each timer tick while playing: applies ayah repeat, then refreshes the UI.</summary>
    private void OnPlayerTick()
    {
        try
        {
            var track = _track;
            if (track == null) return;

            var ms = _player.PositionMs;
            var timings = track.Audio.Ayahs;
            var current = _ayahIndex >= 0 && _ayahIndex < timings.Count ? timings[_ayahIndex] : null;
            if (QuranRules.AyahRepeatTarget(_settings.Repeat, current, ms) is { } back)
            {
                _player.Seek(back);
                Refresh(back);
                return;
            }

            Refresh(ms);
            if (DateTime.UtcNow - _lastSaveUtc > TimeSpan.FromSeconds(10)) SaveNow();
        }
        catch (Exception ex)
        {
            Log.Error("quran: playback tick failed", ex);
        }
    }

    private void OnSurahEnded()
    {
        try
        {
            var track = _track;
            if (track == null) return;

            var next = track.Surah.Id + 1;
            switch (QuranRules.OnSurahEnded(_settings.Repeat, _settings.ContinueNextSurah, QuranRules.HasSurah(next)))
            {
                case EndAction.RepeatSurah:
                    _player.Seek(0);
                    _player.Play();
                    PostPill();
                    break;
                case EndAction.NextSurah:
                    _ = LoadAsync(next, autoPlay: true, startMs: 0);
                    break;
                default:
                    _player.Seek(0);
                    RemovePill();
                    SaveNow();
                    Refresh(0);
                    break;
            }
            UpdatePlayButton();
        }
        catch (Exception ex)
        {
            Log.Error("quran: end of surah failed", ex);
        }
    }

    private void StepAyah(int delta)
    {
        var track = _track;
        if (track == null) return;
        var target = (_ayahIndex < 0 ? 0 : _ayahIndex) + delta;
        if (target < 0 || target >= track.Audio.Ayahs.Count) return;
        SeekToAyah(target);
    }

    private void SeekToAyah(int index)
    {
        var track = _track;
        if (track == null) return;
        index = Math.Clamp(index, 0, track.Audio.Ayahs.Count - 1);
        var from = track.Audio.Ayahs[index].FromMs;
        _player.Seek(from);
        Refresh(from);
    }

    private void StepSurah(int delta)
    {
        var id = (_surah?.Id ?? 1) + delta;
        if (!QuranRules.HasSurah(id)) return;
        var wasPlaying = _player.IsPlaying;
        _ = LoadAsync(id, wasPlaying, 0);
    }

    private void CommitSeek()
    {
        if (_track == null || _state != LoadState.Ready) return;
        var ms = (long)Seek.Value;
        _player.Seek(ms);
        Refresh(ms);
    }

    /// <summary>Updates the current ayah, word, timeline and pill for a position.</summary>
    private void Refresh(long ms)
    {
        var track = _track;
        if (track == null) return;

        var index = QuranSync.AyahIndexAt(track.Audio.Ayahs, ms);
        var changed = index != _ayahIndex;
        _ayahIndex = index;
        if (changed) PostPill();
        UpdateIslandStrip(track, index, ms);
        if (!_visible) return;

        if (changed) ScrollToCurrent(instant: false);
        Highlight(index, WordKey(track, index, ms));
        RenderTransport(ms);
    }

    private static int WordKey(QuranTrack track, int index, long ms)
    {
        if (!track.WordLevel) return 0; // ayah-level fallback: the whole ayah is one run
        if (index < 0) return int.MinValue;
        return QuranSync.WordAt(track.Audio.Ayahs[index], ms) ?? int.MinValue;
    }

    // ---- resting pill -----------------------------------------------------------------------------

    /// <summary>
    /// 50 ms while the tab is visible (word highlight in the reading view); about 8 times a second while the
    /// island shows the ayah strip; once a second otherwise. The timer only runs while audio plays.
    /// </summary>
    private void ApplyInterval() =>
        _player.SetInterval(_visible ? TimeSpan.FromMilliseconds(50)
            : _settings.ShowAyahOnIsland ? TimeSpan.FromMilliseconds(120)
            : TimeSpan.FromSeconds(1));

    /// <summary>
    /// The resting island while playing. With "Show the ayah on the island" it widens into a strip that follows
    /// the recitation word by word; otherwise it is a compact "Ya-Sin · 12/83" pill. The strip is posted once and
    /// then updated in place, so word and ayah changes do not re-post the activity.
    /// </summary>
    private void PostPill()
    {
        var track = _track;
        if (track == null || _ayahIndex < 0 || !_player.IsPlaying) return;
        var number = track.Audio.Ayahs[_ayahIndex].Number;

        if (_settings.ShowAyahOnIsland)
        {
            if (_pillRef != null) _pillRef.Text = $"{track.Surah.Id}:{number}";
            if (_stripPosted && Ctx.Activities.Contains(PillKey)) return;

            _pillStrip ??= new QuranPillStrip();
            _pillRef = new TextBlock
            {
                Text = $"{track.Surah.Id}:{number}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 1, 0, 0),
            };
            _pillRef.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle");
            var leading = new StackPanel { Orientation = Orientation.Horizontal };
            leading.Children.Add(PillGlyph());
            leading.Children.Add(_pillRef);
            Ctx.Activities.Post(new PillActivity
            {
                Key = PillKey,
                Priority = PillPriority,
                Width = IslandStripWidth,
                Leading = leading,
                Center = _pillStrip,
                Trailing = new EqualizerBars { IsPlaying = true },
            });
            _stripPosted = true;
            UpdateIslandStrip(track, _ayahIndex, _player.PositionMs);
            return;
        }

        var center = new TextBlock
        {
            Text = $"{track.Surah.NameSimple} · {number}/{track.Surah.VersesCount}",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        center.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

        Ctx.Activities.Post(new PillActivity
        {
            Key = PillKey,
            Priority = PillPriority,
            Width = 210,
            Leading = PillGlyph(),
            Center = center,
            Trailing = new EqualizerBars { IsPlaying = true },
        });
        _stripPosted = false;
    }

    private static TextBlock PillGlyph()
    {
        var glyph = new TextBlock
        {
            Text = BookGlyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        return glyph;
    }

    /// <summary>Moves the island strip to the ayah and word at this position (only while it is showing).</summary>
    private void UpdateIslandStrip(QuranTrack track, int index, long ms)
    {
        if (!_stripPosted || _pillStrip == null || index < 0 || index >= track.Audio.Ayahs.Count) return;
        var number = track.Audio.Ayahs[index].Number;
        var text = track.Texts.FirstOrDefault(t => t.Number == number);
        _pillStrip.Show(text, WordKey(track, index, ms));
    }

    private void RemovePill()
    {
        Ctx.Activities.Remove(PillKey);
        _stripPosted = false;
    }

    // ---- persistence ------------------------------------------------------------------------------

    private void SaveSettings() => JsonStore.Save("quran", _settings);

    private void SaveNow()
    {
        if (_surah != null && _track != null)
        {
            _settings.LastSurah = _surah.Id;
            _settings.LastPositionMs = _player.PositionMs;
        }
        _lastSaveUtc = DateTime.UtcNow;
        SaveSettings();
    }

    // ---- browse -----------------------------------------------------------------------------------

    private void OnSearchChanged()
    {
        _search = SearchBox.Text;
        SearchHint.Visibility = string.IsNullOrEmpty(_search) ? Visibility.Visible : Visibility.Collapsed;
        _pickingReciter = false;
        RebuildList();
        UpdateAll();
    }

    private void TogglePicker()
    {
        _pickingReciter = !_pickingReciter;
        RebuildList();
        UpdateAll();
    }

    private void RebuildList()
    {
        SurahList.Children.Clear();
        if (_pickingReciter)
        {
            foreach (var reciter in Reciter.All) SurahList.Children.Add(ReciterRow(reciter));
            return;
        }

        var indonesian = _settings.Translation == TranslationMode.Indonesian;
        var count = 0;
        foreach (var surah in _surahs)
        {
            if (!QuranSearch.Matches(surah, _search)) continue;
            SurahList.Children.Add(SurahRow(surah, indonesian));
            count++;
        }

        if (count == 0)
        {
            SurahList.Children.Add(new TextBlock
            {
                Text = $"No surah matches “{_search.Trim()}”",
                Style = (Style)FindResource("PerchCaption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 12, 0, 0),
            });
        }
    }

    private Button SurahRow(Surah surah, bool indonesian)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = surah.Id.ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
        grid.Children.Add(badge);

        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = surah.NameSimple,
            Style = (Style)FindResource("PerchBody"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        };
        var caption = new TextBlock
        {
            Text = $"{surah.Meaning(indonesian)} · {surah.VersesCount} {(surah.VersesCount == 1 ? "ayah" : "ayahs")} · {surah.Revelation}",
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 11,
        };
        middle.Children.Add(name);
        middle.Children.Add(caption);
        Grid.SetColumn(middle, 2);
        grid.Children.Add(middle);

        var arabic = new TextBlock
        {
            Text = surah.NameArabic,
            FontFamily = (FontFamily)FindResource("QuranFont"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
        };
        arabic.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
        Grid.SetColumn(arabic, 3);
        grid.Children.Add(arabic);

        var row = new Button
        {
            Style = (Style)FindResource("QuranRow"),
            Content = grid,
            Margin = new Thickness(0, 0, 0, 2),
        };
        if (surah.Id == _surah?.Id) row.SetResourceReference(Button.BackgroundProperty, "IslandAccentSoft");
        row.Click += (_, _) => PlaySurah(surah.Id);
        return row;
    }

    private Button ReciterRow(Reciter reciter)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = reciter.Name,
            Style = (Style)FindResource("PerchBody"),
            FontWeight = FontWeights.SemiBold,
        });
        if (!string.IsNullOrEmpty(reciter.Style))
        {
            stack.Children.Add(new TextBlock { Text = reciter.Style, Style = (Style)FindResource("PerchCaption") });
        }

        var row = new Button
        {
            Style = (Style)FindResource("QuranRow"),
            Content = stack,
            Margin = new Thickness(0, 0, 0, 2),
        };
        if (reciter.Id == _settings.ReciterId) row.SetResourceReference(Button.BackgroundProperty, "IslandAccentSoft");
        row.Click += (_, _) => SelectReciter(reciter);
        return row;
    }

    private void PlaySurah(int id)
    {
        // Choosing the surah that is already loaded resumes it instead of restarting.
        if (_track != null && _state == LoadState.Ready && id == _surah?.Id)
        {
            _view = QuranView.Reading;
            _player.Play();
            PostPill();
            UpdateAll();
            return;
        }
        _ = LoadAsync(id, autoPlay: true, startMs: 0);
    }

    private void SelectReciter(Reciter reciter)
    {
        _pickingReciter = false;
        if (reciter.Id == _settings.ReciterId)
        {
            RebuildList();
            UpdateAll();
            return;
        }

        _settings.ReciterId = reciter.Id;
        SaveSettings();
        RebuildList();
        UpdateAll();

        // The audio changes with the reciter: keep the place and the play state.
        if (_surah != null && _track != null)
        {
            var wasPlaying = _player.IsPlaying;
            var position = _player.PositionMs;
            _ = LoadAsync(_surah.Id, wasPlaying, position);
        }
    }

    // ---- views ------------------------------------------------------------------------------------

    private void ShowBrowse()
    {
        _view = QuranView.Browse;
        RebuildList();
        UpdateAll();
    }

    private void ShowSettings(QuranView from)
    {
        _settingsReturn = from;
        _view = QuranView.Settings;
        UpdateAll();
    }

    private void LeaveSettings()
    {
        _view = _settingsReturn;
        if (_view == QuranView.Browse) RebuildList();
        UpdateAll();
    }

    private void SettingsChanged(bool reading)
    {
        SaveSettings();
        if (reading && _track != null)
        {
            BuildReading();
            Refresh(_player.PositionMs);
        }
        UpdateAll();
    }

    /// <summary>Applies the current state to every control. Cheap; the browse list is rebuilt separately.</summary>
    private void UpdateAll()
    {
        BrowseView.Visibility = _view == QuranView.Browse ? Visibility.Visible : Visibility.Collapsed;
        NowView.Visibility = _view == QuranView.Reading ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = _view == QuranView.Settings ? Visibility.Visible : Visibility.Collapsed;

        ReciterText.Text = $"Reciter: {Reciter.Find(_settings.ReciterId).Display} ▾";
        UpdateNowHeader();
        UpdateSettingsCards();
        UpdatePlayButton();
        UpdateRepeat();
        UpdateStatus();
    }

    private void UpdateNowHeader()
    {
        var surah = _surah;
        if (surah == null) return;

        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run(surah.NameSimple));
        TitleText.Inlines.Add(new Run(" · "));
        TitleText.Inlines.Add(new Run(surah.NameArabic) { FontFamily = (FontFamily)FindResource("QuranFont") });
        var reciter = Reciter.Find(_settings.ReciterId);
        ReciterSubText.Text = $"{reciter.Display} · {surah.Revelation}";
    }

    private void UpdateSettingsCards()
    {
        TranslationButton.Content = _settings.Translation switch
        {
            TranslationMode.English => "English",
            TranslationMode.Indonesian => "Bahasa Indonesia",
            TranslationMode.Both => "Both",
            _ => "None",
        };
        EnglishButton.Content = _settings.EnglishId == QuranIds.AbdelHaleem ? "Abdel Haleem" : "Saheeh International";
        IndonesianButton.Content = _settings.IndonesianId == QuranIds.KingFahadComplex ? "King Fahad Complex" : "Kemenag";
        ArabicSizeText.Text = _settings.ArabicSize.ToString(CultureInfo.InvariantCulture);
        TransliterationToggle.IsChecked = _settings.Transliteration;
        ContinueToggle.IsChecked = _settings.ContinueNextSurah;
        IslandAyahToggle.IsChecked = _settings.ShowAyahOnIsland;
    }

    private void UpdatePlayButton()
    {
        var playing = _player.IsPlaying;
        PlayButton.Content = playing ? PauseGlyph : PlayGlyph;
        PlayButton.ToolTip = playing ? "Pause" : "Play";
    }

    private void UpdateRepeat()
    {
        var (glyph, tip, active) = _settings.Repeat switch
        {
            RepeatMode.Ayah => ("", "Repeat: this ayah", true),
            RepeatMode.Surah => ("", "Repeat: this surah", true),
            _ => ("", "Repeat: off", false),
        };
        RepeatButton.Content = glyph;
        RepeatButton.ToolTip = tip;
        RepeatButton.SetResourceReference(Button.ForegroundProperty, active ? "IslandAccent" : "IslandSubtle");
    }

    /// <summary>
    /// Text and player: while the text is loaded it stays visible, and a failed audio load shows its message as a
    /// card at the bottom of the reading area. Without text, the card is centred and shows loading or the error.
    /// </summary>
    private void UpdateStatus()
    {
        var hasText = _track != null;
        ReadScroll.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        var showCard = !hasText || _state == LoadState.Error;
        StatusCard.Visibility = showCard ? Visibility.Visible : Visibility.Collapsed;
        StatusCard.VerticalAlignment = hasText ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        StatusCard.Margin = hasText ? new Thickness(0, 0, 0, 4) : new Thickness(0);
        RetryButton.Visibility = _state == LoadState.Error ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _state == LoadState.Error
            ? _error
            : $"Loading {_surah?.NameSimple ?? "surah"}…";
        PlayerBar.IsEnabled = hasText;
    }

    // ---- reading view -----------------------------------------------------------------------------

    private void ClearReading()
    {
        AyahStack.Children.Clear();
        _views.Clear();
        _hlRun = null;
        _hlIndex = int.MinValue;
        _hlKey = int.MinValue;
    }

    /// <summary>Builds one card per ayah of the timings: Arabic word runs, then the translations.</summary>
    private void BuildReading()
    {
        ClearReading();
        var track = _track;
        if (track == null) return;

        var texts = new Dictionary<int, AyahText>();
        foreach (var text in track.Texts) texts.TryAdd(text.Number, text);
        var arabicFont = (FontFamily)FindResource("QuranFont");

        for (var i = 0; i < track.Audio.Ayahs.Count; i++)
        {
            var timing = track.Audio.Ayahs[i];
            texts.TryGetValue(timing.Number, out var text);
            var index = i;

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            card.MouseLeftButtonUp += (_, _) => SeekToAyah(index);

            var stack = new StackPanel();
            var arabic = new TextBlock
            {
                FlowDirection = FlowDirection.RightToLeft,
                FontFamily = arabicFont,
                FontSize = _settings.ArabicSize,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            arabic.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
            var runs = new Dictionary<int, Run>();
            AppendArabic(arabic, runs, track.WordLevel, text, timing.Number);
            stack.Children.Add(arabic);

            if (text != null) AddTranslations(stack, text);
            card.Child = stack;
            AyahStack.Children.Add(card);
            _views.Add(new AyahView(card, runs));
        }

        Highlight(_ayahIndex, _ayahIndex >= 0 ? WordKey(track, _ayahIndex, _player.PositionMs) : int.MinValue);
    }

    private static void AppendArabic(TextBlock arabic, Dictionary<int, Run> runs, bool wordLevel, AyahText? text, int number)
    {
        if (text == null)
        {
            arabic.Inlines.Add(new Run(ArabicDigits(number)));
            return;
        }

        if (wordLevel && text.Words.Count > 0)
        {
            foreach (var word in text.Words)
            {
                var run = new Run(word.IsEnd ? Ornament(word.Text) : word.Text);
                if (!word.IsEnd) runs[word.Position] = run;
                arabic.Inlines.Add(run);
                arabic.Inlines.Add(new Run(" "));
            }
            return;
        }

        var whole = new Run(text.Arabic);
        runs[0] = whole;
        arabic.Inlines.Add(whole);
        arabic.Inlines.Add(new Run(" " + Ornament(ArabicDigits(number))));
    }

    private void AddTranslations(Panel stack, AyahText text)
    {
        var mode = _settings.Translation;
        var english = mode is TranslationMode.English or TranslationMode.Both;
        var indonesian = mode is TranslationMode.Indonesian or TranslationMode.Both;
        if (english) AddLine(stack, text, _settings.EnglishId, primary: true, italic: false);
        if (indonesian) AddLine(stack, text, _settings.IndonesianId, primary: !english, italic: false);
        if (_settings.Transliteration) AddLine(stack, text, QuranIds.Transliteration, primary: false, italic: true);
    }

    private static void AddLine(Panel stack, AyahText text, int resourceId, bool primary, bool italic)
    {
        if (!text.Translations.TryGetValue(resourceId, out var line) || line.Length == 0) return;
        var block = new TextBlock
        {
            Text = line,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = primary ? 12.5 : 11.5,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            Margin = new Thickness(0, 3, 0, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, primary ? "IslandForeground" : "IslandSubtle");
        stack.Children.Add(block);
    }

    /// <summary>Moves the accent to the given word of the given ayah and dims the other ayahs.</summary>
    private void Highlight(int index, int key)
    {
        if (index == _hlIndex && key == _hlKey) return;

        if (index != _hlIndex)
        {
            for (var i = 0; i < _views.Count; i++) _views[i].Card.Opacity = i == index ? 1.0 : 0.55;
        }

        if (_hlRun != null)
        {
            _hlRun.SetResourceReference(TextElement.ForegroundProperty, "IslandForeground");
            _hlRun = null;
        }
        if (index >= 0 && index < _views.Count && _views[index].Runs.TryGetValue(key, out var run))
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "IslandAccent");
            _hlRun = run;
        }
        _hlIndex = index;
        _hlKey = key;
    }

    /// <summary>Keeps the current ayah in view. Smooth when animated; the user may scroll until the next ayah.</summary>
    private void ScrollToCurrent(bool instant)
    {
        if (!_visible || _state != LoadState.Ready || _view != QuranView.Reading) return;
        if (_ayahIndex < 0 || _ayahIndex >= _views.Count) return;

        try
        {
            ReadScroll.UpdateLayout();
            var card = _views[_ayahIndex].Card;
            var top = card.TransformToAncestor(ReadScroll).Transform(new Point(0, 0)).Y + ReadScroll.VerticalOffset;
            var target = Math.Max(0, top - 6);
            if (instant)
            {
                ReadScroll.ScrollToVerticalOffset(target);
                return;
            }
            BeginAnimation(ScrollOffsetProperty, new DoubleAnimation(ReadScroll.VerticalOffset, target, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet; the next ayah change scrolls.
        }
    }

    private void RenderTransport(long ms)
    {
        var track = _track;
        var duration = _player.DurationMs;
        if (duration <= 0 && track != null && track.Audio.Ayahs.Count > 0) duration = track.Audio.Ayahs[^1].ToMs;

        Seek.Maximum = Math.Max(1, duration);
        if (!_seekDragging) Seek.Value = Math.Clamp(ms, 0, Seek.Maximum);
        ElapsedText.Text = Format.Duration(TimeSpan.FromMilliseconds(ms));
        RemainingText.Text = duration > 0 ? "-" + Format.Duration(TimeSpan.FromMilliseconds(Math.Max(0, duration - ms))) : "";

        AyahLabel.Text = track != null && _ayahIndex >= 0 && _ayahIndex < track.Audio.Ayahs.Count
            ? $"Ayah {track.Audio.Ayahs[_ayahIndex].Number} / {track.Surah.VersesCount}"
            : "";
    }

    /// <summary>Arabic-Indic digits, as used for ayah numbers in the Quran.</summary>
    private static string ArabicDigits(int number) =>
        new(number.ToString(CultureInfo.InvariantCulture).Select(c => (char)('٠' + (c - '0'))).ToArray());

    /// <summary>Ayah-end ornament: the number inside the ornate parentheses U+FD3F and U+FD3E.</summary>
    private static string Ornament(string digits) => "﴿" + digits + "﴾";

    private sealed class AyahView
    {
        public AyahView(Border card, Dictionary<int, Run> runs)
        {
            Card = card;
            Runs = runs;
        }

        public Border Card { get; }

        public Dictionary<int, Run> Runs { get; }
    }
}
