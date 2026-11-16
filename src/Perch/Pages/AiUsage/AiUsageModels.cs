namespace Perch.Pages.AiUsage;

/// <summary>
/// One Claude Code assistant message: its token usage plus the id used to drop repeated log lines.
/// Only these fields are kept; message content is never read.
/// </summary>
public sealed record UsageEntry(
    DateTime TimeUtc,
    string Model,
    long Input,
    long Output,
    long CacheCreate,
    long CacheRead,
    string? DedupKey);

/// <summary>A Claude Code 5-hour window ("block") with its totals.</summary>
public sealed record ClaudeBlock(
    DateTime StartUtc,
    DateTime EndUtc,
    long Input,
    long Output,
    long CacheCreate,
    long CacheRead,
    int Messages,
    IReadOnlyDictionary<string, long> ModelTokens)
{
    /// <summary>Input plus output tokens: the part that counts against a budget.</summary>
    public long ActiveTokens => Input + Output;

    /// <summary>Cache creation plus cache reads, shown separately.</summary>
    public long CacheTokens => CacheCreate + CacheRead;

    /// <summary>The model with the most active tokens in this window (null when unknown).</summary>
    public string? TopModel => ModelTokens
        .Where(kv => kv.Key != "unknown")
        .OrderByDescending(kv => kv.Value)
        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => (string?)kv.Key)
        .FirstOrDefault();
}

/// <summary>Totals for a span of time: active tokens (input + output), messages, and active tokens per model.</summary>
public sealed record UsageTotal(long Tokens, int Messages, IReadOnlyDictionary<string, long> ModelTokens)
{
    public static readonly UsageTotal Empty = new(0, 0, new Dictionary<string, long>());
}

/// <summary>
/// The three figures the page shows: the current 5-hour window, the last 7 days across all models,
/// and the last 7 days of Fable models only.
/// </summary>
public sealed record ClaudeSummary(ClaudeBlock? Current, UsageTotal Week, UsageTotal FableWeek, DateTime? LastActivityUtc);

/// <summary>What one scan touched, for the log line and the settings view.</summary>
public sealed record ScanStats(int Files, long Entries, long BytesRead, long Millis, bool Full);

/// <summary>An immutable result of one refresh. Built on a background thread, drawn on the UI thread.</summary>
public sealed record AiSnapshot(DateTime NowUtc, ClaudeSummary? Claude, ScanStats Stats);
