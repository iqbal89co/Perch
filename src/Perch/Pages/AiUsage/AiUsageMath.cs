namespace Perch.Pages.AiUsage;

/// <summary>
/// Pure aggregation: de-duplication, Claude Code 5-hour windows and rolling 7-day totals. All times are UTC.
/// </summary>
public static class AiUsageMath
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeekLength = TimeSpan.FromDays(7);

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

    /// <summary>True for Fable models ("claude-fable-5-2", "fable", ...).</summary>
    public static bool IsFable(string model) => model.Contains("fable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Totals of the entries in (fromUtc, toUtc] that pass <paramref name="filter"/>.</summary>
    public static UsageTotal Total(IEnumerable<UsageEntry> entries, DateTime fromUtc, DateTime toUtc, Func<UsageEntry, bool>? filter = null)
    {
        long tokens = 0;
        var messages = 0;
        var models = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.TimeUtc <= fromUtc || e.TimeUtc > toUtc) continue;
            if (filter != null && !filter(e)) continue;
            var active = e.Input + e.Output;
            tokens += active;
            messages++;
            models[e.Model] = models.GetValueOrDefault(e.Model) + active;
        }
        return messages == 0 ? UsageTotal.Empty : new UsageTotal(tokens, messages, models);
    }

    public static ClaudeSummary SummarizeClaude(IEnumerable<UsageEntry> raw, DateTime nowUtc)
    {
        var deduped = Dedupe(raw);
        var blocks = Blocks(deduped);
        var weekStart = nowUtc - WeekLength;
        return new ClaudeSummary(
            CurrentBlock(blocks, nowUtc),
            Total(deduped, weekStart, nowUtc),
            Total(deduped, weekStart, nowUtc, e => IsFable(e.Model)),
            deduped.Count > 0 ? deduped[^1].TimeUtc : null);
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
