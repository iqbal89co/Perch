namespace Perch.Core;

public enum IslandTheme { Midnight, Daylight, Smoke, Aurora }

public enum IslandShape { Docked, Floating }

public sealed class PomodoroSettings
{
    public int FocusMin { get; set; } = 25;
    public int ShortBreakMin { get; set; } = 5;
    public int LongBreakMin { get; set; } = 15;
    public int LongEvery { get; set; } = 4;
    public bool AutoStartBreaks { get; set; } = true;
    public bool AutoStartFocus { get; set; } = false;
}

/// <summary>Everything the user can change. Persisted as data/settings.json.</summary>
public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 1;

    // Look
    public IslandTheme Theme { get; set; } = IslandTheme.Midnight;
    public IslandShape Shape { get; set; } = IslandShape.Docked;
    /// <summary>Accent colour as #RRGGBB.</summary>
    public string Accent { get; set; } = "#2DD4BF";
    public bool Use24h { get; set; } = true;
    public bool ShowSecondsInPill { get; set; } = false;

    // Behaviour
    public int HoverDelayMs { get; set; } = 220;
    public int CollapseDelayMs { get; set; } = 350;
    public int ExpandedWidth { get; set; } = 640;
    public int ExpandedHeight { get; set; } = 320;
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>0 = primary monitor, otherwise 1-based index into the screen list.</summary>
    public int Monitor { get; set; } = 0;
    public bool StartWithWindows { get; set; } = true;
    public bool Sounds { get; set; } = true;
    public string StartPage { get; set; } = "media";
    public bool RememberPage { get; set; } = true;
    public string LastPage { get; set; } = "media";
    public List<string> HiddenTabs { get; set; } = new();

    // Glances
    public bool ShowMediaInPill { get; set; } = true;
    public bool HudVolume { get; set; } = true;
    public bool HudBattery { get; set; } = true;

    // Music
    public bool Lyrics { get; set; } = true;
    public int LyricsOffsetMs { get; set; } = 0;

    // Focus
    public PomodoroSettings Pomodoro { get; set; } = new();

    // Per-feature extras pages may need without touching this file: free-form string map.
    public Dictionary<string, string> Extras { get; set; } = new();
}
