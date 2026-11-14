using System.Globalization;
using System.Text.RegularExpressions;

namespace Perch.Pages.Media;

/// <summary>One timed lyric line. Empty text is a deliberate break (instrumental part).</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Pure LRC parser and lookup. No WPF, no I/O.</summary>
public static class LrcParser
{
    private static readonly Regex TimeTag = new(@"^([0-9]{1,3}):([0-9]{1,2})(?:[.:]([0-9]{1,3}))?$", RegexOptions.Compiled);
    private static readonly Regex OffsetTag = new(@"^offset\s*:\s*([+-]?[0-9]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MetadataTag = new(@"^[A-Za-z]+\s*:", RegexOptions.Compiled);

    /// <summary>
    /// Parses LRC text into lines sorted by time. Supports several timestamps per line, [mm:ss.x],
    /// [mm:ss.xx] and [mm:ss.xxx], an [offset:+/-ms] tag (positive = lyrics earlier) and ignores metadata
    /// tags. Lines without a timestamp are skipped; timestamped lines with empty text are kept as breaks.
    /// </summary>
    public static List<LyricLine> Parse(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        var timed = new List<(List<TimeSpan> Times, string Text)>();
        long offsetMs = 0;

        foreach (var raw in lrc.Split('\n'))
        {
            var rest = raw.Trim();
            var times = new List<TimeSpan>();
            while (rest.StartsWith('['))
            {
                var close = rest.IndexOf(']');
                if (close < 0) break;
                var tag = rest[1..close].Trim();
                if (TryParseTime(tag, out var time))
                {
                    times.Add(time);
                }
                else if (OffsetTag.Match(tag) is { Success: true } om
                         && long.TryParse(om.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var off))
                {
                    offsetMs = off;
                }
                else if (!MetadataTag.IsMatch(tag))
                {
                    break; // bracketed lyric text such as "[Chorus]" is not a tag
                }
                rest = rest[(close + 1)..].TrimStart();
            }

            if (times.Count > 0) timed.Add((times, rest.Trim()));
        }

        foreach (var (times, text) in timed)
        {
            foreach (var time in times)
            {
                var adjusted = time - TimeSpan.FromMilliseconds(offsetMs);
                if (adjusted < TimeSpan.Zero) adjusted = TimeSpan.Zero;
                result.Add(new LyricLine(adjusted, text));
            }
        }

        return result.OrderBy(l => l.Time).ToList();
    }

    /// <summary>
    /// Index of the last line whose time is at or before <paramref name="position"/>, or -1 when the
    /// position is before the first line (or there are no lines). Binary search; lines must be sorted.
    /// </summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var lo = 0;
        var hi = lines.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    private static bool TryParseTime(string tag, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var m = TimeTag.Match(tag);
        if (!m.Success) return false;
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var millis = 0;
        if (m.Groups[3].Success)
            millis = int.Parse(m.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        time = TimeSpan.FromMilliseconds(minutes * 60_000L + seconds * 1_000L + millis);
        return true;
    }
}
