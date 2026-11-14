using System.Text.Json;
using System.Threading;
using Perch.Core;
using Perch.SettingsUi;

namespace Perch.Tests;

public class SettingsValidatorTests
{
    [Fact]
    public void Clamp_pulls_every_numeric_setting_into_its_range()
    {
        var s = new AppSettings
        {
            HoverDelayMs = 5000,
            CollapseDelayMs = -4,
            ExpandedWidth = 100,
            ExpandedHeight = 9000,
            LyricsOffsetMs = -9999,
            Monitor = -3,
        };
        s.Pomodoro.FocusMin = 0;
        s.Pomodoro.ShortBreakMin = 99;
        s.Pomodoro.LongBreakMin = 0;
        s.Pomodoro.LongEvery = 1;

        SettingsValidator.Clamp(s);

        Assert.Equal(1000, s.HoverDelayMs);
        Assert.Equal(0, s.CollapseDelayMs);
        Assert.Equal(560, s.ExpandedWidth);
        Assert.Equal(420, s.ExpandedHeight);
        Assert.Equal(-2000, s.LyricsOffsetMs);
        Assert.Equal(0, s.Monitor);
        Assert.Equal(1, s.Pomodoro.FocusMin);
        Assert.Equal(60, s.Pomodoro.ShortBreakMin);
        Assert.Equal(1, s.Pomodoro.LongBreakMin);
        Assert.Equal(2, s.Pomodoro.LongEvery);
    }

    [Fact]
    public void Clamp_leaves_defaults_alone()
    {
        var s = new AppSettings();
        var before = JsonSerializer.Serialize(s);

        SettingsValidator.Clamp(s);

        Assert.Equal(before, JsonSerializer.Serialize(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("teal")]
    [InlineData("#GGGGGG")]
    [InlineData("#2DD4")]
    [InlineData(null)]
    public void Invalid_accent_falls_back_to_the_first_choice(string? accent)
    {
        var s = new AppSettings { Accent = accent! };

        SettingsValidator.Clamp(s);

        Assert.Equal(ThemeManager.AccentChoices[0], s.Accent);
    }

    [Fact]
    public void Valid_accent_is_kept()
    {
        var s = new AppSettings { Accent = "#A78BFA" };

        SettingsValidator.Clamp(s);

        Assert.Equal("#A78BFA", s.Accent);
    }

    [Fact]
    public void Hidden_tabs_lose_blanks_and_duplicates()
    {
        var s = new AppSettings { HiddenTabs = new List<string> { "today", " ", "Today", "shelf", "", "shelf" } };

        SettingsValidator.Clamp(s);

        Assert.Equal(new[] { "today", "shelf" }, s.HiddenTabs);
    }

    [Fact]
    public void Null_collections_become_empty()
    {
        var s = new AppSettings { HiddenTabs = null!, Extras = null! };

        SettingsValidator.Clamp(s);

        Assert.Empty(s.HiddenTabs);
        Assert.Empty(s.Extras);
    }

    [Fact]
    public void A_hidden_start_page_resets_to_media()
    {
        var s = new AppSettings { StartPage = "tasks", HiddenTabs = new List<string> { "TASKS" } };

        SettingsValidator.Clamp(s);

        Assert.Equal("media", s.StartPage);
    }

    [Fact]
    public void A_visible_start_page_is_kept()
    {
        var s = new AppSettings { StartPage = "today", HiddenTabs = new List<string> { "tasks" } };

        SettingsValidator.Clamp(s);

        Assert.Equal("today", s.StartPage);
    }
}

public class SettingsResetTests
{
    [Fact]
    public void Apply_defaults_makes_the_target_equal_to_a_fresh_settings_object()
    {
        var target = new AppSettings
        {
            Theme = IslandTheme.Aurora,
            Shape = IslandShape.Floating,
            Accent = "#FB923C",
            HoverDelayMs = 900,
            Use24h = false,
            Monitor = 2,
            HiddenTabs = new List<string> { "tasks" },
            Extras = new Dictionary<string, string> { ["k"] = "v" },
            Pomodoro = new PomodoroSettings { FocusMin = 50, AutoStartFocus = true },
        };

        SettingsReset.ApplyDefaults(target);

        Assert.Equal(JsonSerializer.Serialize(new AppSettings()), JsonSerializer.Serialize(target));
    }

    [Fact]
    public void Apply_defaults_does_not_alias_collections_with_a_previous_instance()
    {
        var previous = new List<string> { "tasks" };
        var target = new AppSettings { HiddenTabs = previous };

        SettingsReset.ApplyDefaults(target);
        target.HiddenTabs.Add("shelf");

        Assert.Equal(new[] { "tasks" }, previous);
        Assert.Equal(new[] { "shelf" }, target.HiddenTabs);
    }
}

public class SettingsTextTests
{
    [Fact]
    public void Offset_caption_reads_early_and_late()
    {
        Assert.Equal("In sync", SettingsText.OffsetCaption(0));
        Assert.Equal("300 ms earlier", SettingsText.OffsetCaption(300));
        Assert.Equal("200 ms later", SettingsText.OffsetCaption(-200));
    }

    [Fact]
    public void Milliseconds_caption()
    {
        Assert.Equal("220 ms", SettingsText.Milliseconds(220));
    }

    [Fact]
    public void Display_name_shows_index_and_resolution()
    {
        Assert.Equal("Display 2 · 2560×1440", SettingsText.DisplayName(2, 2560, 1440));
    }
}

/// <summary>
/// Renders each Settings section to docs/screens/settings-*.png for visual review. It needs a real WPF
/// Application on an STA thread, so it is skipped in normal runs (headless CI).
/// </summary>
public class SettingsWindowReviewTests
{
    [Fact(Skip = "manual visual review")]
    public void Render_settings_sections_for_review()
    {
        var screens = Path.Combine(FindPerchFolder(), "docs", "screens");
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                AppPaths.OverrideRoot(Path.Combine(Path.GetTempPath(), "perch-settings-review-" + Guid.NewGuid().ToString("N")));
                if (System.Windows.Application.Current == null) new System.Windows.Application();
                System.Windows.Application.Current!.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Perch;component/Shell/Styles.xaml"),
                });
                IslandContext.CreateForTests(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                ThemeManager.Apply(IslandContext.Current.Settings);
                SettingsUi.SettingsWindow.RenderSectionsForReview(screens);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw failure;
    }

    /// <summary>The folder that holds Perch.sln (the Perch/ folder of the repository).</summary>
    private static string FindPerchFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Perch.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Perch.sln was not found above the test output.");
    }
}
