using System.Windows.Media;
using Perch.Core;
using Perch.Pages.Today;
using Perch.Services;
using Perch.Shell;

namespace Perch.Tests;

public class SpringTests
{
    [Fact]
    public void Settles_on_target_and_stops()
    {
        var s = new Spring(0) { Target = 100 };
        for (var i = 0; i < 600 && !s.IsSettled; i++) s.Step(1 / 120.0);
        Assert.True(s.IsSettled);
        Assert.Equal(100, s.Value);
        Assert.Equal(0, s.Velocity);
    }

    [Fact]
    public void Underdamped_spring_overshoots_a_little()
    {
        var s = new Spring(0) { Target = 100 };
        var max = 0.0;
        for (var i = 0; i < 600; i++) { s.Step(1 / 120.0); max = Math.Max(max, s.Value); }
        Assert.InRange(max, 100.5, 115);
    }

    [Fact]
    public void Motion_does_not_depend_on_frame_rate()
    {
        var a = new Spring(0) { Target = 50 };
        var b = new Spring(0) { Target = 50 };
        for (var i = 0; i < 30; i++) a.Step(1 / 60.0);
        for (var i = 0; i < 120; i++) b.Step(1 / 240.0);
        Assert.InRange(Math.Abs(a.Value - b.Value), 0, 0.5);
    }

    [Fact]
    public void A_stalled_frame_does_not_explode()
    {
        var s = new Spring(0) { Target = 10 };
        s.Step(5); // 5 seconds in one frame
        Assert.InRange(s.Value, -50, 60);
    }
}

public class ActivityHubTests
{
    [Fact]
    public void Highest_priority_wins_and_ties_go_to_the_newest()
    {
        var hub = new ActivityHub();
        hub.Post(new PillActivity { Key = "media", Priority = 40 });
        hub.Post(new PillActivity { Key = "focus", Priority = 60 });
        Assert.Equal("focus", hub.Current(DateTime.Now)!.Key);

        hub.Post(new PillActivity { Key = "other", Priority = 60 });
        Assert.Equal("other", hub.Current(DateTime.Now)!.Key);
    }

    [Fact]
    public void Expired_activities_drop_out()
    {
        var hub = new ActivityHub();
        var now = DateTime.Now;
        hub.Post(new PillActivity { Key = "notice", Priority = 100, ExpiresAt = now.AddSeconds(1) });
        hub.Post(new PillActivity { Key = "media", Priority = 40 });
        Assert.Equal("notice", hub.Current(now)!.Key);
        Assert.Equal("media", hub.Current(now.AddSeconds(2))!.Key);
        Assert.False(hub.Contains("notice"));
    }

    [Fact]
    public void Post_replaces_by_key_and_raises_Changed()
    {
        var hub = new ActivityHub();
        var changes = 0;
        hub.Changed += () => changes++;
        hub.Post(new PillActivity { Key = "a", Priority = 1, Width = 100 });
        hub.Post(new PillActivity { Key = "a", Priority = 1, Width = 200 });
        hub.Remove("a");
        hub.Remove("missing");
        Assert.Equal(3, changes);
        Assert.Null(hub.Current(DateTime.Now));
    }
}

public class JsonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-tests-" + Guid.NewGuid().ToString("N"));

    public JsonStoreTests() => AppPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Settings_round_trip_with_snake_case_and_enum_names()
    {
        var s = new AppSettings { Theme = IslandTheme.Apricot, Shape = IslandShape.Floating, HoverDelayMs = 99 };
        s.HiddenTabs.Add("tasks");
        JsonStore.Save("settings", s);

        var json = File.ReadAllText(JsonStore.PathFor("settings"));
        Assert.Contains("\"hover_delay_ms\": 99", json);
        Assert.Contains("\"theme\": \"apricot\"", json);

        var back = JsonStore.Load<AppSettings>("settings");
        Assert.Equal(IslandTheme.Apricot, back.Theme);
        Assert.Equal(IslandShape.Floating, back.Shape);
        Assert.Equal(99, back.HoverDelayMs);
        Assert.Equal(new[] { "tasks" }, back.HiddenTabs);
    }

    [Fact]
    public void Version_1_settings_move_to_the_glass_themes_and_the_theme_accent()
    {
        File.WriteAllText(JsonStore.PathFor("settings"),
            "{\"settings_version\": 1, \"theme\": \"aurora\", \"accent\": \"#FB923C\", \"shape\": \"floating\"}");
        var s = JsonStore.Load<AppSettings>("settings");
        Assert.True(s.Migrate());
        Assert.Equal(IslandTheme.Tide, s.Theme);
        Assert.Equal("auto", s.Accent);
        Assert.Equal(IslandShape.Floating, s.Shape);
        Assert.Equal(AppSettings.CurrentVersion, s.SettingsVersion);
        Assert.False(s.Migrate());

        var daylight = new AppSettings { SettingsVersion = 1, Theme = IslandTheme.Daylight, Accent = "#60A5FA" };
        daylight.Migrate();
        Assert.Equal(IslandTheme.Daylight, daylight.Theme);
    }

    [Fact]
    public void Retired_themes_draw_as_tide_and_auto_accent_is_recognised()
    {
        Assert.Equal(IslandTheme.Tide, ThemeManager.Effective(IslandTheme.Aurora));
        Assert.Equal(IslandTheme.Tide, ThemeManager.Effective(IslandTheme.Smoke));
        Assert.Equal(IslandTheme.Apricot, ThemeManager.Effective(IslandTheme.Apricot));
        Assert.True(ThemeManager.IsAuto("AUTO"));
        Assert.True(ThemeManager.IsAuto(""));
        Assert.False(ThemeManager.IsAuto("#22D3EE"));
        Assert.DoesNotContain(IslandTheme.Aurora, ThemeManager.Offered);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = JsonStore.Load<AppSettings>("nothing-here");
        Assert.Equal(IslandTheme.Tide, s.Theme);
        Assert.Equal("auto", s.Accent);
        Assert.False(s.Migrate());
        Assert.Equal(25, s.Pomodoro.FocusMin);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_is_set_aside()
    {
        File.WriteAllText(JsonStore.PathFor("settings"), "{ this is not json");
        var s = JsonStore.Load<AppSettings>("settings");
        Assert.Equal(220, s.HoverDelayMs);
        Assert.True(File.Exists(JsonStore.PathFor("settings") + ".corrupt"));
    }

    [Fact]
    public void Second_save_keeps_a_backup()
    {
        JsonStore.Save("settings", new AppSettings { HoverDelayMs = 1 });
        JsonStore.Save("settings", new AppSettings { HoverDelayMs = 2 });
        var bak = File.ReadAllText(Path.Combine(AppPaths.Data, "settings.bak.json"));
        Assert.Contains("\"hover_delay_ms\": 1", bak);
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(3600, "1:00:00")]
    [InlineData(-5, "0:00")]
    public void Duration(int seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Clock_24h_and_12h()
    {
        var t = new DateTime(2026, 1, 2, 15, 4, 5);
        Assert.Equal("15:04", Format.Clock(t, true));
        Assert.Equal("15:04:05", Format.Clock(t, true, seconds: true));
        Assert.StartsWith("3:04", Format.Clock(t, false));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10485760, "10 MB")]
    public void Bytes(double value, string expected) => Assert.Equal(expected, Format.Bytes(value));
}

public class CalendarMathTests
{
    [Fact]
    public void Month_grid_starts_on_monday_and_has_six_weeks()
    {
        var grid = CalendarMath.MonthGrid(2026, 11); // 1 Nov 2026 is a Sunday
        Assert.Equal(42, grid.Count);
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Equal(new DateOnly(2026, 10, 26), grid[0]);
        Assert.Equal(new DateOnly(2026, 11, 1), grid[6]);
    }

    [Fact]
    public void Month_starting_on_monday_starts_the_grid()
    {
        var grid = CalendarMath.MonthGrid(2026, 6); // 1 Jun 2026 is a Monday
        Assert.Equal(new DateOnly(2026, 6, 1), grid[0]);
    }

    [Fact]
    public void Iso_week_and_headers()
    {
        Assert.Equal(53, CalendarMath.IsoWeek(new DateOnly(2026, 12, 31)));
        Assert.Equal(1, CalendarMath.IsoWeek(new DateOnly(2027, 1, 4)));
        Assert.Equal(new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" }, CalendarMath.WeekdayHeaders());
    }

    [Fact]
    public void Day_progress_is_a_fraction()
    {
        Assert.Equal(0.5, CalendarMath.DayProgress(new DateTime(2026, 1, 1, 12, 0, 0)), 6);
    }
}

public class ShellGeometryTests
{
    [Fact]
    public void Docked_shape_spans_the_ears_and_full_height()
    {
        var g = IslandWindow.BuildGeometry(200, 32, 14, 9, docked: true);
        Assert.Equal(0, g.Bounds.Left, 3);
        Assert.Equal(218, g.Bounds.Width, 3);
        Assert.Equal(32, g.Bounds.Height, 3);
    }

    [Fact]
    public void Floating_shape_is_a_rounded_rect_of_the_given_size()
    {
        var g = IslandWindow.BuildGeometry(120, 34, 17, 0, docked: false);
        Assert.Equal(120, g.Bounds.Width, 3);
        Assert.Equal(34, g.Bounds.Height, 3);
        Assert.True(g.FillContains(new System.Windows.Point(60, 17)));
        Assert.False(g.FillContains(new System.Windows.Point(1, 1))); // rounded-off corner
    }

    [Fact]
    public void Radius_larger_than_the_shape_is_clamped()
    {
        var g = IslandWindow.BuildGeometry(40, 20, 500, 9, docked: true);
        Assert.False(g.IsEmpty());
        Assert.Equal(20, g.Bounds.Height, 3);
    }
}

public class MediaTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Brave.SSPANI7VZFDOSAEFPRHUJFMORA", "Brave")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData(@"C:\Apps\foobar2000.exe", "foobar2000")]
    public void Friendly_app_names(string aumid, string expected) =>
        Assert.Equal(expected, MediaService.FriendlyAppName(aumid));

    [Fact]
    public void Live_position_moves_while_playing_and_clamps_to_duration()
    {
        var at = DateTimeOffset.Now;
        var playing = new MediaSnapshot { Title = "x", IsPlaying = true, Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromSeconds(12), PositionUpdatedAt = at };
        Assert.Equal(TimeSpan.FromSeconds(11), playing.LivePosition(at.AddSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(12), playing.LivePosition(at.AddSeconds(30)));

        var paused = playing with { IsPlaying = false };
        Assert.Equal(TimeSpan.FromSeconds(10), paused.LivePosition(at.AddSeconds(30)));
    }
}

public class ThemeTests
{
    [Fact]
    public void Parse_color_falls_back_on_garbage()
    {
        var fallback = Colors.Red;
        Assert.Equal(Color.FromRgb(0x2D, 0xD4, 0xBF), ThemeManager.ParseColor("#2DD4BF", fallback));
        Assert.Equal(fallback, ThemeManager.ParseColor("not-a-color", fallback));
        Assert.Equal(fallback, ThemeManager.ParseColor(null, fallback));
    }
}
