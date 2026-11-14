using System.Diagnostics;
using Perch.Core;

namespace Perch.Pages.AiUsage;

/// <summary>
/// Scans the local logs and aggregates them into an <see cref="AiSnapshot"/>. Runs on a background thread;
/// one call at a time (the page guards this). Logs only counts and timings for full scans.
/// </summary>
public sealed class AiUsageService
{
    private readonly AiLogScanner _scanner = new();

    public AiSnapshot Build(DateTime nowUtc, bool rescan)
    {
        var clock = Stopwatch.StartNew();
        if (rescan) _scanner.Reset();
        var full = _scanner.IsEmpty;

        var claudeRoots = AiRoots.ClaudeFromEnvironment();
        var codexRoots = AiRoots.CodexFromEnvironment();
        var output = _scanner.Scan(claudeRoots, codexRoots);

        var zone = TimeZoneInfo.Local;
        var claude = claudeRoots.Count > 0 ? AiUsageMath.SummarizeClaude(output.Claude, nowUtc, zone) : null;
        var codex = codexRoots.Count > 0 ? AiUsageMath.SummarizeCodex(output.Codex, nowUtc, zone) : null;

        clock.Stop();
        var entries = (long)output.Claude.Count + output.Codex.Count;
        var stats = new ScanStats(output.Files, entries, output.BytesRead, clock.ElapsedMilliseconds, full);
        if (full && output.Files > 0)
            Log.Info($"ai: scanned {output.Files} files, {entries:N0} entries in {stats.Millis} ms");
        return new AiSnapshot(nowUtc, claude, codex, stats);
    }
}
