using System.Reflection;
using Perch.Core;

namespace Perch.SettingsUi;

/// <summary>Restores every setting to its default value.</summary>
public static class SettingsReset
{
    /// <summary>
    /// Copies each public read/write property from a fresh AppSettings into target. Collections and the
    /// Pomodoro block are copied, never shared, so the old instances are not aliased by the new values.
    /// </summary>
    public static void ApplyDefaults(AppSettings target)
    {
        var defaults = new AppSettings();
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            property.SetValue(target, Copy(property.GetValue(defaults)));
        }
    }

    private static object? Copy(object? value) => value switch
    {
        List<string> list => new List<string>(list),
        Dictionary<string, string> map => new Dictionary<string, string>(map),
        PomodoroSettings p => new PomodoroSettings
        {
            FocusMin = p.FocusMin,
            ShortBreakMin = p.ShortBreakMin,
            LongBreakMin = p.LongBreakMin,
            LongEvery = p.LongEvery,
            AutoStartBreaks = p.AutoStartBreaks,
            AutoStartFocus = p.AutoStartFocus,
        },
        _ => value,
    };
}
