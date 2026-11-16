using System.Globalization;

namespace Perch.Pages.Prayer;

/// <summary>Text for the prayer page and the resting pill. Pure.</summary>
public static class PrayerText
{
    private static readonly string[] HijriMonths =
    {
        "Muharram", "Safar", "Rabi I", "Rabi II", "Jumada I", "Jumada II",
        "Rajab", "Sha'ban", "Ramadan", "Shawwal", "Dhu al-Qi'dah", "Dhu al-Hijjah",
    };

    /// <summary>"in 1 h 24 m", "in 42 min" from 10 minutes up, "in 8:12" under 10 minutes, "now" at or after zero.</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "now";
        var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
        if (seconds >= 3600)
        {
            var minutes = (seconds + 59) / 60;
            return $"in {minutes / 60} h {minutes % 60} m";
        }

        if (seconds >= 600) return $"in {(seconds + 59) / 60} min";
        return $"in {seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>A duration as "1 h 05 m" from an hour up, "42 min" below that. Negative values read as zero.</summary>
    public static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var minutes = (long)Math.Round(span.TotalMinutes, MidpointRounding.ToZero);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} m" : $"{minutes} min";
    }

    /// <summary>"Ramadan in 103 days", "Eid al-Fitr tomorrow", "Ashura today".</summary>
    public static string IslamicEventCaption(IslamicEvent e) => e.DaysUntil switch
    {
        0 => $"{e.Name} today",
        1 => $"{e.Name} tomorrow",
        _ => $"{e.Name} in {e.DaysUntil} days",
    };

    /// <summary>Qibla bearing and distance, e.g. "295° WNW · 7,890 km".</summary>
    public static string Qibla(double bearing, double distanceKm) =>
        $"{Math.Round(bearing):0}° {PrayerCalculator.Compass16(bearing)} · {distanceKm.ToString("N0", CultureInfo.InvariantCulture)} km";

    /// <summary>The method's short name for the header caption, e.g. "Kemenag", "MWL".</summary>
    public static string MethodShortName(string? id) => PrayerMethods.Find(id).Id switch
    {
        "kemenag" => "Kemenag",
        "mwl" => "MWL",
        "isna" => "ISNA",
        "egypt" => "Egypt",
        "ummalqura" => "Umm al-Qura",
        "karachi" => "Karachi",
        "singapore" => "MUIS",
        "turkey" => "Diyanet",
        "gulf" => "Gulf",
        "tehran" => "Tehran",
        var other => other,
    };

    /// <summary>Hijri date in English transliteration, e.g. "23 Jumada I 1448".</summary>
    public static string Hijri(DateOnly date)
    {
        var calendar = new UmAlQuraCalendar();
        var day = date.ToDateTime(TimeOnly.MinValue);
        var month = calendar.GetMonth(day);
        return $"{calendar.GetDayOfMonth(day)} {HijriMonths[month - 1]} {calendar.GetYear(day)}";
    }

    /// <summary>"Sat 14 Nov 2026 · 4 Jumada II 1448".</summary>
    public static string DateCaption(DateOnly date) =>
        $"{date.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)} · {Hijri(date)}";

    /// <summary>Time of day as shown on the page, honouring the 12/24-hour setting.</summary>
    public static string Clock(DateTimeOffset time, bool use24h) => Perch.Core.Format.Clock(time.DateTime, use24h);
}
