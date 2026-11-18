using System.Globalization;

namespace Perch.Pages.AiUsage;

/// <summary>Number, time and budget text for the AI page. Pure, so it is unit-tested.</summary>
public static class AiFormat
{
    /// <summary>512, 1.2K, 38K, 4.1M, 2.3B.</summary>
    public static string Tokens(long n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        double value;
        string unit;
        if (n >= 1_000_000_000) { value = n / 1e9; unit = "B"; }
        else if (n >= 1_000_000) { value = n / 1e6; unit = "M"; }
        else { value = n / 1e3; unit = "K"; }
        var digits = value >= 100 ? "0" : "0.#";
        return value.ToString(digits, CultureInfo.InvariantCulture) + unit;
    }

    /// <summary>"2 h 14 m", "38 m", "under a minute".</summary>
    public static string Remaining(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "under a minute";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} m";
        return $"{(int)t.TotalHours} h {t.Minutes} m";
    }

    /// <summary>"12 s ago", "3 min ago", "2 h ago".</summary>
    public static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return $"{Math.Max(0, (int)t.TotalSeconds)} s ago";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} min ago";
        return $"{(int)t.TotalHours} h ago";
    }

    /// <summary>A reset time in local time: "18:40" today, "Mon 18:40" on another day.</summary>
    public static string ResetClock(DateTime utc, DateTime nowUtc, TimeZoneInfo zone, bool use24h)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var clock = local.ToString(use24h ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        return local.Date == nowLocal.Date ? clock : local.ToString("ddd ", CultureInfo.CurrentCulture) + clock;
    }

    /// <summary>
    /// When a plan limit resets, as /usage puts it: "resets in 2 h 14 m" within a day, otherwise the local day
    /// and time ("resets Sun 04:00").
    /// </summary>
    public static string Resets(DateTime resetUtc, DateTime nowUtc, bool use24h = true, TimeZoneInfo? zone = null)
    {
        var left = resetUtc - nowUtc;
        if (left <= TimeSpan.Zero) return "resetting now";
        if (left < TimeSpan.FromDays(1)) return "resets in " + Remaining(left);
        return "resets " + ResetClock(resetUtc, nowUtc, zone ?? TimeZoneInfo.Local, use24h);
    }

    /// <summary>"82%" from a 0..1 fraction.</summary>
    public static string Percent(double fraction) => $"{Math.Round(fraction * 100):0}%";

    /// <summary>"1 message", "38 messages".</summary>
    public static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";

    /// <summary>
    /// Parses a token budget: "2.5M", "800k", "1200000", "1,200,000". Empty means no budget (valid, null).
    /// Commas and spaces are dropped as thousands separators, so use a dot for decimals.
    /// </summary>
    public static bool TryParseBudget(string? text, out long? budget)
    {
        budget = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var s = text.Replace(",", "").Replace("_", "").Replace(" ", "").Trim().ToLowerInvariant();
        double multiplier = 1;
        if (s.EndsWith('k')) { multiplier = 1e3; s = s[..^1]; }
        else if (s.EndsWith('m')) { multiplier = 1e6; s = s[..^1]; }
        else if (s.EndsWith('b')) { multiplier = 1e9; s = s[..^1]; }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            return false;
        var value = Math.Round(number * multiplier);
        if (value < 1 || value > 1e12) return false;
        budget = (long)value;
        return true;
    }
}
