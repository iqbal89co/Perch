namespace Perch.Pages.Quran;

public enum RepeatMode { Off, Ayah, Surah }

/// <summary>Which translations to show under the Arabic.</summary>
public enum TranslationMode { English, Indonesian, Both, None }

/// <summary>What happens when a surah's audio reaches its end.</summary>
public enum EndAction { Stop, RepeatSurah, NextSurah }

/// <summary>
/// Maps a playback position to the ayah and word being recited. Ayahs are binary-searched by start time, so a
/// position in a gap keeps the previous ayah; before the first ayah it is ayah 1 with no word.
/// </summary>
public static class QuranSync
{
    /// <summary>Index into the timings of the ayah at ms, or -1 when there are none.</summary>
    public static int AyahIndexAt(IReadOnlyList<AyahTiming> ayahs, long ms)
    {
        if (ayahs.Count == 0) return -1;
        var lo = 0;
        var hi = ayahs.Count - 1;
        var answer = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (ayahs[mid].FromMs <= ms)
            {
                answer = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return answer;
    }

    /// <summary>
    /// The word position being recited inside an ayah: the last segment that started at or before ms, so gaps
    /// keep the previous word. Null before the first segment.
    /// </summary>
    public static int? WordAt(AyahTiming ayah, long ms)
    {
        var segments = ayah.Words;
        var lo = 0;
        var hi = segments.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (segments[mid].FromMs <= ms)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found >= 0 ? segments[found].Position : null;
    }
}

/// <summary>Playback decisions that do not touch the player, so they can be unit-tested.</summary>
public static class QuranRules
{
    public static EndAction OnSurahEnded(RepeatMode mode, bool continueNext, bool hasNextSurah)
    {
        if (mode == RepeatMode.Surah) return EndAction.RepeatSurah;
        return continueNext && hasNextSurah ? EndAction.NextSurah : EndAction.Stop;
    }

    /// <summary>With ayah repeat on, the position to seek back to once playback passes the ayah's end.</summary>
    public static long? AyahRepeatTarget(RepeatMode mode, AyahTiming? ayah, long ms)
    {
        if (mode != RepeatMode.Ayah || ayah is null) return null;
        return ms >= ayah.ToMs && ayah.ToMs > ayah.FromMs ? ayah.FromMs : null;
    }

    public static RepeatMode NextRepeat(RepeatMode mode) => (RepeatMode)(((int)mode + 1) % 3);

    public static bool HasSurah(int id) => id >= 1 && id <= 114;
}

/// <summary>Surah search: number (exact or prefix), Latin name, English or Indonesian meaning, Arabic name.</summary>
public static class QuranSearch
{
    public static bool Matches(Surah surah, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return true;

        if (q.All(char.IsDigit))
        {
            var digits = q.TrimStart('0');
            var id = surah.Id.ToString();
            return digits.Length > 0 && id.StartsWith(digits, StringComparison.Ordinal);
        }

        var folded = Fold(q);
        if (folded.Length > 0 &&
            (Fold(surah.NameSimple).Contains(folded, StringComparison.Ordinal)
             || Fold(surah.NameEn).Contains(folded, StringComparison.Ordinal)
             || Fold(surah.NameId).Contains(folded, StringComparison.Ordinal)))
            return true;

        return surah.NameArabic.Contains(q, StringComparison.Ordinal);
    }

    /// <summary>Lower case letters and digits only, so "ya sin", "Ya-Sin" and "yasin" compare equal.</summary>
    private static string Fold(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
