using System.Text.RegularExpressions;
using Perch.Core;

namespace Perch.SettingsUi;

/// <summary>Keeps user-editable settings inside the ranges the Settings window offers.</summary>
public static class SettingsValidator
{
    public const string DefaultStartPage = "media";

    private static readonly Regex HexColour = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    public static void Clamp(AppSettings s)
    {
        s.HoverDelayMs = Math.Clamp(s.HoverDelayMs, 0, 1000);
        s.CollapseDelayMs = Math.Clamp(s.CollapseDelayMs, 0, 1500);
        s.ExpandedWidth = Math.Clamp(s.ExpandedWidth, 560, 800);
        s.ExpandedHeight = Math.Clamp(s.ExpandedHeight, 280, 420);
        s.LyricsOffsetMs = Math.Clamp(s.LyricsOffsetMs, -2000, 2000);
        s.Monitor = Math.Max(0, s.Monitor);

        s.Pomodoro ??= new PomodoroSettings();
        var p = s.Pomodoro;
        p.FocusMin = Math.Clamp(p.FocusMin, 1, 120);
        p.ShortBreakMin = Math.Clamp(p.ShortBreakMin, 1, 60);
        p.LongBreakMin = Math.Clamp(p.LongBreakMin, 1, 90);
        p.LongEvery = Math.Clamp(p.LongEvery, 2, 8);

        s.Extras ??= new Dictionary<string, string>();
        s.HiddenTabs = CleanTabs(s.HiddenTabs);

        if (!HexColour.IsMatch(s.Accent ?? string.Empty)) s.Accent = ThemeManager.AccentChoices[0];

        if (string.IsNullOrWhiteSpace(s.StartPage) || s.HiddenTabs.Contains(s.StartPage, StringComparer.OrdinalIgnoreCase))
            s.StartPage = DefaultStartPage;
    }

    /// <summary>Trims the ids, drops blanks and case-insensitive duplicates, keeps the first spelling.</summary>
    public static List<string> CleanTabs(IEnumerable<string>? ids) =>
        (ids ?? Array.Empty<string>())
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
