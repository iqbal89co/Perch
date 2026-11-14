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

/// <summary>One Codex rate-limit window (for example the 5-hour or the weekly limit).</summary>
public sealed record RateWindow(double UsedPercent, int WindowMinutes, DateTime? ResetsAtUtc);

/// <summary>The rate limits Codex reported at one moment.</summary>
public sealed record RateLimits(DateTime TimeUtc, RateWindow? Primary, RateWindow? Secondary);

/// <summary>One Codex token_count event: the tokens of that turn and the limits it reported, if any.</summary>
public sealed record CodexEvent(DateTime TimeUtc, long Tokens, RateLimits? Limits);

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

/// <summary>Claude Code figures: the current 5-hour window and the last seven local days (oldest first).</summary>
public sealed record ClaudeSummary(ClaudeBlock? Current, long[] Week, DateTime? LastActivityUtc);

/// <summary>A Codex limit window as shown. Expired windows read 0% until new usage is logged.</summary>
public sealed record CodexWindowView(string Label, double Percent, DateTime? ResetsAtUtc, bool Expired);

/// <summary>Codex figures: the newest limits seen and today's tokens.</summary>
public sealed record CodexSummary(CodexWindowView? Primary, CodexWindowView? Secondary, DateTime? LimitsSeenUtc, long TodayTokens);

/// <summary>What one scan touched, for the log line and the settings view.</summary>
public sealed record ScanStats(int Files, long Entries, long BytesRead, long Millis, bool Full);

/// <summary>An immutable result of one refresh. Built on a background thread, drawn on the UI thread.</summary>
public sealed record AiSnapshot(DateTime NowUtc, ClaudeSummary? Claude, CodexSummary? Codex, ScanStats Stats);
