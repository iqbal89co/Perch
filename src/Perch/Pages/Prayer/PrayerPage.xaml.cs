using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Core;
using Geo = Windows.Devices.Geolocation;

namespace Perch.Pages.Prayer;

/// <summary>
/// Prayer times for the chosen city, computed on the device. The day view shows the next prayer, the eight times
/// and a strip of facts (night, day length, qibla, next Islamic date); the settings view picks the city and the
/// reminder options. A 1 s timer drives the countdown on
/// the resting island and the prayer-time notices, and keeps running while the island is closed.
/// </summary>
public partial class PrayerPage : IslandPage
{
    private const string PillKey = "prayer";
    private const string PrayerGlyph = ""; // crescent moon
    private const string LocationOff = "Location is off — pick your city instead";

    private static readonly PrayerKind[] Five =
        { PrayerKind.Fajr, PrayerKind.Dhuhr, PrayerKind.Asr, PrayerKind.Maghrib, PrayerKind.Isha };
    private static readonly Lazy<byte[]> Chime = new(() => PrayerSound.BuildChimeWav());

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PrayerSettings _settings;
    private PrayerDay? _today;
    private PrayerDay? _tomorrow;
    private DateOnly _day;
    private DateTimeOffset _lastTick;
    private PrayerDay? _yesterday;
    private string _tilesKey = string.Empty;
    private bool _locating;
    private bool _shown;
    private double _progress;
    private SoundPlayer? _player;

    // Day-view facts, worked out once per day (or per place) and formatted on each render.
    private (DateTimeOffset Midnight, DateTimeOffset LastThird)? _night;
    private TimeSpan? _dayLength;
    private IslamicEvent? _islamic;
    private (double Bearing, double DistanceKm)? _qibla;

    public override string Id => "prayer";
    public override string Title => "Prayer";
    public override string Glyph => PrayerGlyph;
    public override int Order => 56;

    public PrayerPage()
    {
        InitializeComponent();
        _settings = JsonStore.Load<PrayerSettings>("prayer");
        _lastTick = DateTimeOffset.Now;

        SettingsButton.Click += (_, _) => ShowSettings(true);
        DoneButton.Click += (_, _) => ShowSettings(false);
        SearchBox.TextChanged += (_, _) => RenderPlaces();
        LocateButton.Click += async (_, _) => await LocateAsync();

        MethodPrev.Click += (_, _) => CycleMethod(-1);
        MethodNext.Click += (_, _) => CycleMethod(1);
        AsrPrev.Click += (_, _) => CycleAsr();
        AsrNext.Click += (_, _) => CycleAsr();
        RemindPrev.Click += (_, _) => StepRemind(-5);
        RemindNext.Click += (_, _) => StepRemind(5);
        SoundPrev.Click += (_, _) => CycleSound(-1);
        SoundNext.Click += (_, _) => CycleSound(1);
        PauseToggle.Click += (_, _) => { _settings.PauseMusic = PauseToggle.IsChecked == true; Save(); };
        NotifyToggle.Click += (_, _) => { _settings.Notify = NotifyToggle.IsChecked == true; Save(); };

        _timer.Tick += (_, _) => OnTick();
        ProgressTrack.SizeChanged += (_, _) => ApplyProgressWidth();

        RecomputeDay();
        RenderPlaces();
        ShowSettings(!_settings.HasLocation);
        Refresh(DateTimeOffset.Now);
        _timer.Start();
    }

    public override void OnShown()
    {
        _shown = true;
        if (_settings.HasLocation && DateOnly.FromDateTime(DateTime.Now) != _day) RecomputeDay();
        Refresh(DateTimeOffset.Now);
    }

    // The day view is only drawn while the page is visible; the reminder logic keeps running regardless.
    public override void OnHidden() => _shown = false;

    // ---- Clock -------------------------------------------------------------------------------------------

    private void OnTick()
    {
        try
        {
            var now = DateTimeOffset.Now;
            if (_settings.HasLocation && DateOnly.FromDateTime(now.DateTime) != _day) RecomputeDay(); // midnight

            // After sleep or a clock change the gap is large: do not announce the prayers that passed meanwhile.
            var gap = now - _lastTick;
            if (gap <= TimeSpan.FromMinutes(2) && gap >= TimeSpan.Zero) FireDueNotices(_lastTick, now);
            _lastTick = now;

            Refresh(now);
        }
        catch (Exception ex)
        {
            Log.Error("prayer: tick failed", ex);
        }
    }

    private void FireDueNotices(DateTimeOffset previous, DateTimeOffset now)
    {
        if (_today is null) return;
        foreach (var kind in Five)
        {
            if (_today.At(kind) is { } time && previous < time && time <= now) FirePrayer(kind);
        }
    }

    private void FirePrayer(PrayerKind kind)
    {
        var ctx = IslandContext.Current;
        if (ctx is null) return;
        if (_settings.Notify)
            ctx.Notify(PrayerGlyph, $"It's time for {kind}", TimeSpan.FromSeconds(20), key: "prayer-now", priority: 100);

        switch (_settings.Sound)
        {
            case "silent":
                break;
            case "system":
                SystemSounds.Exclamation.Play();
                break;
            default:
                PlayChime();
                break;
        }

        if (_settings.PauseMusic && ctx.Media.Current?.IsPlaying == true)
            _ = ctx.Media.PlayPauseAsync();
    }

    private void PlayChime()
    {
        try
        {
            // The player keeps a reference to the stream, so the stream lives as long as the player.
            _player ??= new SoundPlayer(new System.IO.MemoryStream(Chime.Value));
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"prayer: chime failed ({ex.Message})");
        }
    }

    // ---- Computing ---------------------------------------------------------------------------------------

    /// <summary>Works out today's and tomorrow's times for the saved place. Runs on setting changes and at midnight.</summary>
    private void RecomputeDay()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _day = today;
        _today = null;
        _tomorrow = null;
        _yesterday = null;
        _night = null;
        _dayLength = null;
        _qibla = null;
        _tilesKey = string.Empty;
        _islamic = PrayerCalculator.NextIslamicEvent(today);

        var city = string.IsNullOrWhiteSpace(_settings.PlaceName) ? "Prayer" : _settings.PlaceName;
        PlaceText.Text = $"{city} · {PrayerText.DateCaption(today)}";
        var hanafi = string.Equals(_settings.Asr, "hanafi", StringComparison.OrdinalIgnoreCase);
        DateText.Text = $"{PrayerText.MethodShortName(_settings.Method)} · {(hanafi ? "Hanafi" : "Standard")} Asr";

        if (!_settings.HasLocation)
        {
            QiblaPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var lat = _settings.Latitude!.Value;
        var lon = _settings.Longitude!.Value;
        var zone = PrayerPlaces.ResolveZone(_settings.TimeZoneId);
        var method = PrayerMethods.Find(_settings.Method);
        var asr = hanafi ? AsrSchool.Hanafi : AsrSchool.Standard;
        var extra = ExtraMinutes();

        _today = PrayerCalculator.Calculate(today, lat, lon, 0, zone, method, asr, HighLatitudeRule.AngleBased, extra);
        _tomorrow = PrayerCalculator.Calculate(today.AddDays(1), lat, lon, 0, zone, method, asr, HighLatitudeRule.AngleBased, extra);
        _yesterday = PrayerCalculator.Calculate(today.AddDays(-1), lat, lon, 0, zone, method, asr, HighLatitudeRule.AngleBased, extra);

        _night = PrayerCalculator.Night(_today.Maghrib, _tomorrow.Fajr);
        if (_today.Sunrise is { } sunrise && _today.Maghrib is { } maghrib)
            _dayLength = PrayerCalculator.DayLength(sunrise, maghrib);

        var bearing = PrayerCalculator.QiblaBearing(lat, lon);
        _qibla = (bearing, PrayerCalculator.DistanceToKaabaKm(lat, lon));
        QiblaRotate.Angle = bearing;
        QiblaPanel.Visibility = Visibility.Visible;
    }

    private IReadOnlyDictionary<PrayerKind, int> ExtraMinutes()
    {
        var map = new Dictionary<PrayerKind, int>();
        foreach (var (name, minutes) in _settings.ExtraMinutes)
        {
            if (Enum.TryParse<PrayerKind>(name, true, out var kind)) map[kind] = minutes;
        }
        return map;
    }

    // ---- Rendering ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reminders and the resting pill run on every tick. The day view is drawn only while the page is shown,
    /// so a hidden page costs nothing beyond the reminder check.
    /// </summary>
    private void Refresh(DateTimeOffset now)
    {
        var next = _settings.HasLocation && _today is not null ? PrayerCalculator.Next(_today, now, _tomorrow) : null;
        if (_shown) RenderDay(now, next);
        PostPill(next, now);
    }

    private void RenderDay(DateTimeOffset now, (string Name, DateTimeOffset Time)? next)
    {
        var use24h = IslandContext.Current?.Settings.Use24h ?? true;
        if (!_settings.HasLocation || _today is null)
        {
            NextNameText.Text = "Prayer";
            NextTimeText.Text = "--:--";
            NextCountdownText.Text = "Choose your city";
            ProgressText.Text = string.Empty;
            _progress = 0;
            ApplyProgressWidth();
            TilesGrid.Children.Clear();
            _tilesKey = string.Empty;
            return;
        }

        if (next is { } n)
        {
            NextNameText.Text = n.Name;
            NextTimeText.Text = PrayerText.Clock(n.Time, use24h);
            NextCountdownText.Text = PrayerText.Countdown(n.Time - now);
        }
        else
        {
            NextNameText.Text = "—";
            NextTimeText.Text = "--:--";
            NextCountdownText.Text = string.Empty;
        }

        RenderProgress(now, next);
        RenderTiles(_today, now, use24h);
        RenderFacts(use24h);
    }

    /// <summary>The bar fills from the previous prayer to the next one; the caption says how long since the previous.</summary>
    private void RenderProgress(DateTimeOffset now, (string Name, DateTimeOffset Time)? next)
    {
        var previous = PrayerCalculator.PreviousPrayer(_today!, now, _yesterday);
        if (next is { } n && previous is { } p)
        {
            _progress = PrayerCalculator.PrayerProgress(p.Time, n.Time, now);
            ProgressText.Text = $"since {p.Name} · {PrayerText.Elapsed(now - p.Time)}";
        }
        else
        {
            _progress = 0;
            ProgressText.Text = string.Empty;
        }

        ApplyProgressWidth();
    }

    private void ApplyProgressWidth() => ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * _progress);

    /// <summary>
    /// Eight times: the current period (the last time that has passed) gets the accent soft background, the times
    /// before it are dimmed. Rebuilt only when the day, the clock format or the current period changes.
    /// </summary>
    private void RenderTiles(PrayerDay day, DateTimeOffset now, bool use24h)
    {
        var entries = new (string Label, DateTimeOffset? Time)[]
        {
            ("Imsak", PrayerCalculator.Imsak(day)),
            ("Fajr", day.Fajr),
            ("Sunrise", day.Sunrise),
            ("Dhuha", day.Dhuha),
            ("Dhuhr", day.Dhuhr),
            ("Asr", day.Asr),
            ("Maghrib", day.Maghrib),
            ("Isha", day.Isha),
        };

        var current = -1;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Time is { } t && t <= now) current = i;
        }

        var key = $"{day.Date:yyyy-MM-dd}|{use24h}|{current}";
        if (key == _tilesKey) return;
        _tilesKey = key;

        TilesGrid.Children.Clear();
        for (var i = 0; i < entries.Length; i++)
        {
            var (label, time) = entries[i];
            var isCurrent = i == current;
            var isPassed = i < current;

            var name = new TextBlock { Text = label, FontSize = 11, Style = (Style)FindResource("PerchCaption") };
            var value = new TextBlock
            {
                Text = time is { } v ? PrayerText.Clock(v, use24h) : "—",
                Style = (Style)FindResource("PerchTitle"),
                FontSize = 16,
                FontWeight = isCurrent ? FontWeights.Bold : FontWeights.SemiBold,
            };
            if (isPassed)
            {
                name.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle");
                value.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle");
            }

            var tile = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { name, value },
                },
            };
            tile.SetResourceReference(Border.BackgroundProperty, isCurrent ? "IslandAccentSoft" : "IslandCard");
            TilesGrid.Children.Add(tile);
        }
    }

    /// <summary>The bottom strip: midnight and last third, day length, qibla and the next Islamic date.</summary>
    private void RenderFacts(bool use24h)
    {
        NightText.Text = _night is { } night
            ? $"Midnight {PrayerText.Clock(night.Midnight, use24h)} · Last third {PrayerText.Clock(night.LastThird, use24h)}"
            : "Night —";
        DayLengthText.Text = _dayLength is { } length ? $"Day {PrayerText.Elapsed(length)}" : "Day —";
        IslamicText.Text = _islamic is { } islamic ? PrayerText.IslamicEventCaption(islamic) : string.Empty;
        QiblaText.Text = _qibla is { } qibla ? PrayerText.Qibla(qibla.Bearing, qibla.DistanceKm) : string.Empty;
    }

    /// <summary>The resting-island countdown during the reminder window before the next prayer.</summary>
    private void PostPill(( string Name, DateTimeOffset Time)? next, DateTimeOffset now)
    {
        var ctx = IslandContext.Current;
        if (ctx is null) return;

        var remaining = next is { } n ? n.Time - now : TimeSpan.Zero;
        var inWindow = next is not null && _settings.HasLocation && _settings.RemindBeforeMin > 0
                       && remaining > TimeSpan.Zero && remaining <= TimeSpan.FromMinutes(_settings.RemindBeforeMin);
        if (!inWindow)
        {
            ctx.Activities.Remove(PillKey);
            return;
        }

        var leading = new TextBlock
        {
            Text = PrayerGlyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        leading.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");

        var center = new TextBlock
        {
            Text = next!.Value.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        center.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

        var trailing = new TextBlock
        {
            Text = Format.Duration(remaining),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(trailing, FontNumeralAlignment.Tabular);
        trailing.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

        ctx.Activities.Post(new PillActivity
        {
            Key = PillKey,
            Priority = 70,
            Width = 200,
            Leading = leading,
            Center = center,
            Trailing = trailing,
        });
    }

    // ---- Settings view -----------------------------------------------------------------------------------

    private void ShowSettings(bool open)
    {
        DayView.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            RenderSettingsChrome();
            RenderSettingsValues();
            RenderPlaces();
        }
    }

    private void RenderSettingsChrome()
    {
        var hasLocation = _settings.HasLocation;
        CityHeading.Text = hasLocation ? "Your city" : "Choose your city";
        SettingsHeading.Text = hasLocation ? "Prayer settings" : "Settings";
        DoneButton.Visibility = hasLocation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSettingsValues()
    {
        MethodValue.Text = PrayerMethods.Find(_settings.Method).Name;
        AsrValue.Text = string.Equals(_settings.Asr, "hanafi", StringComparison.OrdinalIgnoreCase) ? "Hanafi" : "Standard";
        RemindValue.Text = _settings.RemindBeforeMin <= 0 ? "Off" : $"{_settings.RemindBeforeMin} min before";
        SoundValue.Text = _settings.Sound switch
        {
            "system" => "System sound",
            "silent" => "Silent",
            _ => "Chime",
        };
        PauseToggle.IsChecked = _settings.PauseMusic;
        NotifyToggle.IsChecked = _settings.Notify;
    }

    /// <summary>The city list, filtered by the search box. The chosen city is highlighted.</summary>
    private void RenderPlaces()
    {
        PlacesPanel.Children.Clear();
        foreach (var place in PrayerPlaces.Search(SearchBox.Text))
        {
            var chosen = _settings.PlaceName == place.Name && _settings.TimeZoneId == place.TimeZoneId;
            var button = new Button
            {
                Style = (Style)FindResource("PlaceButton"),
                Content = $"{place.Name}  ·  {place.Country}",
                Height = 28,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 0, 2),
                FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal,
            };
            button.SetResourceReference(Button.BackgroundProperty, chosen ? "IslandAccentSoft" : "IslandCard");
            button.Click += (_, _) => ChoosePlace(place);
            PlacesPanel.Children.Add(button);
        }
    }

    private void ChoosePlace(PrayerPlace place)
    {
        _settings.PlaceName = place.Name;
        _settings.Latitude = place.Latitude;
        _settings.Longitude = place.Longitude;
        _settings.TimeZoneId = place.TimeZoneId;
        _settings.Method = place.MethodId;
        LocationText.Text = string.Empty;
        Save();
        ApplyChanged();
    }

    private void CycleMethod(int step)
    {
        var all = PrayerMethods.All;
        var index = all.ToList().FindIndex(m => m.Id == PrayerMethods.Find(_settings.Method).Id);
        _settings.Method = all[(index + step + all.Count) % all.Count].Id;
        Save();
        ApplyChanged();
    }

    private void CycleAsr()
    {
        _settings.Asr = string.Equals(_settings.Asr, "hanafi", StringComparison.OrdinalIgnoreCase) ? "standard" : "hanafi";
        Save();
        ApplyChanged();
    }

    private void StepRemind(int delta)
    {
        _settings.RemindBeforeMin = Math.Clamp(_settings.RemindBeforeMin + delta, 0, 30);
        Save();
        ApplyChanged();
    }

    private void CycleSound(int step)
    {
        string[] options = { "chime", "system", "silent" };
        var index = Array.IndexOf(options, _settings.Sound);
        if (index < 0) index = 0;
        _settings.Sound = options[(index + step + options.Length) % options.Length];
        Save();
        ApplyChanged();
    }

    /// <summary>Every change lands here: refresh the labels and recompute the times that depend on it.</summary>
    private void ApplyChanged()
    {
        RecomputeDay();
        RenderSettingsChrome();
        RenderSettingsValues();
        RenderPlaces();
        Refresh(DateTimeOffset.Now);
    }

    private void Save() => JsonStore.Save("prayer", _settings);

    // ---- Location ----------------------------------------------------------------------------------------

    /// <summary>Asks Windows for the device position (10 s timeout) and picks the nearest place in the list.</summary>
    private async Task LocateAsync()
    {
        if (_locating) return;
        _locating = true;
        LocateButton.IsEnabled = false;
        LocationText.Text = "Finding your location…";
        try
        {
            var access = await Geo.Geolocator.RequestAccessAsync();
            if (access != Geo.GeolocationAccessStatus.Allowed)
            {
                LocationText.Text = LocationOff;
                return;
            }

            var locator = new Geo.Geolocator { DesiredAccuracy = Geo.PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
            var lat = position.Coordinate.Point.Position.Latitude;
            var lon = position.Coordinate.Point.Position.Longitude;
            var nearest = PrayerPlaces.Nearest(lat, lon);

            _settings.PlaceName = nearest.Name;
            _settings.Latitude = lat;
            _settings.Longitude = lon;
            _settings.TimeZoneId = nearest.TimeZoneId;
            _settings.Method = nearest.MethodId;
            Save();
            ApplyChanged();
            LocationText.Text = $"Found you near {nearest.Name}.";
        }
        catch (Exception ex)
        {
            Log.Warn($"prayer: location failed ({ex.Message})");
            LocationText.Text = LocationOff;
        }
        finally
        {
            _locating = false;
            LocateButton.IsEnabled = true;
        }
    }
}
