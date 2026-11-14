namespace Perch.Pages.AiUsage;

/// <summary>
/// Pure aggregation: de-duplication, Claude Code 5-hour windows, daily totals and Codex limit windows.
/// All times are UTC; local dates are derived with the zone passed in (tests pass UTC).
/// </summary>
public static class AiUsageMath
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);
    public const int WeekDays = 7;

    /// <summary>
    /// Sorts by time and keeps the first entry per <see cref="UsageEntry.DedupKey"/>. Entries without a key
    /// are always kept. The same message is logged once per content block with identical usage.
    /// </summary>
    public static List<UsageEntry> Dedupe(IEnumerable<UsageEntry> entries)
    {
        var sorted = entries.OrderBy(e => e.TimeUtc).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UsageEntry>(sorted.Count);
        foreach (var e in sorted)
        {
            if (e.DedupKey is { } key && !seen.Add(key)) continue;
            result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// Groups time-sorted entries into 5-hour windows. A window starts at its first entry's time floored to
    /// the hour (UTC); an entry at or after the window's end starts the next one.
    /// </summary>
    public static List<ClaudeBlock> Blocks(IReadOnlyList<UsageEntry> sorted)
    {
        var blocks = new List<ClaudeBlock>();
        BlockAccumulator? current = null;
        foreach (var e in sorted)
        {
            if (current == null || e.TimeUtc >= current.End)
            {
                if (current != null) blocks.Add(current.ToBlock());
                var start = FloorToHour(e.TimeUtc);
                current = new BlockAccumulator(start, start + BlockLength);
            }
            current.Add(e);
        }
        if (current != null) blocks.Add(current.ToBlock());
        return blocks;
    }

    /// <summary>The last window, while <paramref name="nowUtc"/> is still before its end.</summary>
    public static ClaudeBlock? CurrentBlock(IReadOnlyList<ClaudeBlock> blocks, DateTime nowUtc) =>
        blocks.Count > 0 && nowUtc < blocks[^1].EndUtc ? blocks[^1] : null;

    /// <summary>
    /// Token totals per local day for the <paramref name="days"/> days ending today. Index 0 is the oldest day,
    /// the last index is today. Items outside the range (or in the future) are ignored.
    /// </summary>
    public static long[] DailyTokens(IEnumerable<(DateTime TimeUtc, long Tokens)> items, DateTime nowUtc, TimeZoneInfo zone, int days)
    {
        var result = new long[days];
        var today = LocalDate(nowUtc, zone);
        var earliest = nowUtc.AddDays(-days - 1);
        foreach (var (time, tokens) in items)
        {
            if (time < earliest) continue;
            var index = LocalDate(time, zone).DayNumber - today.DayNumber + (days - 1);
            if (index < 0 || index >= days) continue;
            result[index] += tokens;
        }
        return result;
    }

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    public static ClaudeSummary SummarizeClaude(IEnumerable<UsageEntry> raw, DateTime nowUtc, TimeZoneInfo zone)
    {
        var deduped = Dedupe(raw);
        var blocks = Blocks(deduped);
        var week = DailyTokens(deduped.Select(e => (e.TimeUtc, e.Input + e.Output)), nowUtc, zone, WeekDays);
        return new ClaudeSummary(
            CurrentBlock(blocks, nowUtc),
            week,
            deduped.Count > 0 ? deduped[^1].TimeUtc : null);
    }

    public static CodexSummary SummarizeCodex(IReadOnlyList<CodexEvent> events, DateTime nowUtc, TimeZoneInfo zone)
    {
        // The newest limits by timestamp, not by file order.
        RateLimits? latest = null;
        foreach (var e in events)
        {
            if (e.Limits is { } limits && (latest == null || limits.TimeUtc >= latest.TimeUtc)) latest = limits;
        }

        var today = DailyTokens(events.Select(e => (e.TimeUtc, e.Tokens)), nowUtc, zone, 1)[0];
        return new CodexSummary(
            View(latest?.Primary, nowUtc),
            View(latest?.Secondary, nowUtc),
            latest?.TimeUtc,
            today);
    }

    /// <summary>A window whose reset time has passed reads 0% (usage restarted) until new usage is logged.</summary>
    public static CodexWindowView? View(RateWindow? window, DateTime nowUtc)
    {
        if (window == null) return null;
        var expired = window.ResetsAtUtc is { } reset && nowUtc >= reset;
        var percent = expired ? 0 : Math.Clamp(window.UsedPercent, 0, 100);
        return new CodexWindowView(WindowLabel(window.WindowMinutes), percent, window.ResetsAtUtc, expired);
    }

    public static string WindowLabel(int minutes)
    {
        if (minutes == 300) return "5-hour";
        if (minutes == 10080) return "Weekly";
        if (minutes <= 0) return "Limit";
        if (minutes % 1440 == 0) return $"{minutes / 1440}-day";
        if (minutes % 60 == 0) return $"{minutes / 60}-hour";
        return $"{minutes}-min";
    }

    public static DateTime FloorToHour(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private sealed class BlockAccumulator
    {
        private long _input, _output, _cacheCreate, _cacheRead;
        private int _messages;
        private readonly Dictionary<string, long> _models = new(StringComparer.Ordinal);

        public BlockAccumulator(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }

        public DateTime Start { get; }
        public DateTime End { get; }

        public void Add(UsageEntry e)
        {
            _input += e.Input;
            _output += e.Output;
            _cacheCreate += e.CacheCreate;
            _cacheRead += e.CacheRead;
            _messages++;
            _models[e.Model] = _models.GetValueOrDefault(e.Model) + e.Input + e.Output;
        }

        public ClaudeBlock ToBlock() => new(Start, End, _input, _output, _cacheCreate, _cacheRead, _messages, _models);
    }
}
