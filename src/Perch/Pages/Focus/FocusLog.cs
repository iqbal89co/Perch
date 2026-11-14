using System.Globalization;

namespace Perch.Pages.Focus;

/// <summary>Completed focus sessions and minutes for one local day.</summary>
public sealed class FocusDay
{
    public int Sessions { get; set; }
    public int Minutes { get; set; }
}

/// <summary>
/// Completed focus sessions per local day, keyed "yyyy-MM-dd". Pure: the page loads and saves
/// <see cref="Days"/> through JsonStore under the name "focus".
/// </summary>
public sealed class FocusLog
{
    public const int KeepDays = 30;
    private const string KeyFormat = "yyyy-MM-dd";

    public FocusLog(Dictionary<string, FocusDay>? days = null) => Days = days ?? new();

    public Dictionary<string, FocusDay> Days { get; }

    public static string KeyFor(DateOnly day) => day.ToString(KeyFormat, CultureInfo.InvariantCulture);

    public FocusDay ForDay(DateOnly day) => Days.TryGetValue(KeyFor(day), out var entry) ? entry : new FocusDay();

    public void RecordSession(DateOnly day, int minutes)
    {
        var key = KeyFor(day);
        if (!Days.TryGetValue(key, out var entry))
        {
            entry = new FocusDay();
            Days[key] = entry;
        }
        entry.Sessions++;
        entry.Minutes += Math.Max(0, minutes);
        Prune(day);
    }

    /// <summary>Drops days older than <see cref="KeepDays"/>; today counts as the first of them.</summary>
    public void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-(KeepDays - 1));
        foreach (var key in Days.Keys.ToList())
        {
            var valid = DateOnly.TryParseExact(key, KeyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day);
            if (!valid || day < oldest) Days.Remove(key);
        }
    }

    /// <summary>"1h 15m", "45m", "2h".</summary>
    public static string FormatDuration(int minutes)
    {
        var h = Math.Max(0, minutes) / 60;
        var m = Math.Max(0, minutes) % 60;
        if (h == 0) return $"{m}m";
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    public static string SessionsText(int sessions) => sessions == 1 ? "1 focus session" : $"{sessions} focus sessions";
}
