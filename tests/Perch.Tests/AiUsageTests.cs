using System.Text;
using System.Text.Json;
using Perch.Pages.AiUsage;

// Scanner tests write synthetic log files into their own temp folders; nothing touches real user logs.

namespace Perch.Tests;

public sealed class AiUsageParserTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string Claude(string ts, string msgId, string reqId, long input, long output,
        string model = "claude-opus-5-5", long cacheCreate = 0, long cacheRead = 0, string type = "assistant") =>
        JsonSerializer.Serialize(new
        {
            type,
            timestamp = ts,
            requestId = reqId,
            message = new
            {
                id = msgId,
                model,
                usage = new { input_tokens = input, output_tokens = output, cache_creation_input_tokens = cacheCreate, cache_read_input_tokens = cacheRead },
                content = new[] { new { type = "text", text = "private words that must never be kept" } },
            },
        });

    [Fact]
    public void Claude_assistant_usage_line_yields_tokens_model_time_and_dedupe_key()
    {
        var line = Claude("2026-11-14T09:37:12.123Z", "msg_1", "req_1", 120, 340, cacheCreate: 5000, cacheRead: 20000);
        var entry = AiLogParsers.ParseClaude(Bytes(line));
        Assert.NotNull(entry);
        Assert.Equal(new DateTime(2026, 11, 14, 9, 37, 12, 123, DateTimeKind.Utc), entry!.TimeUtc);
        Assert.Equal("claude-opus-5-5", entry.Model);
        Assert.Equal(120, entry.Input);
        Assert.Equal(340, entry.Output);
        Assert.Equal(5000, entry.CacheCreate);
        Assert.Equal(20000, entry.CacheRead);
        Assert.Equal("msg_1|req_1", entry.DedupKey);
    }

    [Fact]
    public void Missing_cache_fields_count_as_zero()
    {
        var line = """{"type":"assistant","timestamp":"2026-11-14T09:00:00Z","message":{"id":"m","model":"x","usage":{"input_tokens":7,"output_tokens":3}}}""";
        var entry = AiLogParsers.ParseClaude(Bytes(line));
        Assert.NotNull(entry);
        Assert.Equal(0, entry!.CacheCreate);
        Assert.Equal(0, entry.CacheRead);
        Assert.Equal("m|", entry.DedupKey);
    }

    [Fact]
    public void Non_assistant_synthetic_and_usage_free_lines_are_ignored()
    {
        Assert.Null(AiLogParsers.ParseClaude(Bytes(Claude("2026-11-14T09:00:00Z", "m", "r", 1, 1, type: "user"))));
        Assert.Null(AiLogParsers.ParseClaude(Bytes(Claude("2026-11-14T09:00:00Z", "m", "r", 1, 1, model: "<synthetic>"))));
        Assert.Null(AiLogParsers.ParseClaude(Bytes("""{"type":"assistant","timestamp":"2026-11-14T09:00:00Z","message":{"content":"no usage here"}}""")));
        Assert.Null(AiLogParsers.ParseClaude(Bytes("""{"type":"summary","summary":"no usage marker"}""")));
    }

    [Fact]
    public void Malformed_or_timestampless_lines_are_skipped_without_throwing()
    {
        Assert.Null(AiLogParsers.ParseClaude(Bytes("""{"type":"assistant","usage":{"input_tokens":""")));
        Assert.Null(AiLogParsers.ParseClaude(Bytes("""{"type":"assistant","message":{"usage":{"input_tokens":1}}}""")));
    }

}

public sealed class AiUsageFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-ai-files-" + Guid.NewGuid().ToString("N"));

    public AiUsageFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private string File(string name) => Path.Combine(_dir, name);

    private static string ClaudeLine(int i) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc).AddSeconds(i).ToString("o"),
            requestId = "r" + i,
            message = new { id = "m" + i, model = "claude-sonnet", usage = new { input_tokens = 1, output_tokens = 2 }, content = new[] { new { text = new string('x', 300) } } },
        });

    [Fact]
    public void Reader_consumes_only_complete_lines_and_picks_up_the_rest_later()
    {
        var path = File("a.jsonl");
        var first = ClaudeLine(0) + "\n" + ClaudeLine(1) + "\n";
        var partial = ClaudeLine(2);
        System.IO.File.WriteAllText(path, first + partial);

        var index = new JsonlFileIndex<UsageEntry>();
        index.Refresh(path, AiLogParsers.ParseClaude);
        Assert.Equal(2, index.Items.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(first), index.Offset);

        System.IO.File.AppendAllText(path, "\n" + ClaudeLine(3) + "\n");
        var read = index.Refresh(path, AiLogParsers.ParseClaude);
        Assert.Equal(4, index.Items.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(first + partial + "\n" + ClaudeLine(3) + "\n") - Encoding.UTF8.GetByteCount(first),
            read);
    }

    [Fact]
    public void Reader_handles_many_chunks_and_reads_only_appended_bytes()
    {
        var path = File("big.jsonl");
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++) sb.Append(ClaudeLine(i)).Append('\n'); // about 700 KB: several 256 KB chunks
        System.IO.File.WriteAllText(path, sb.ToString());

        var index = new JsonlFileIndex<UsageEntry>();
        index.Refresh(path, AiLogParsers.ParseClaude);
        Assert.Equal(2000, index.Items.Count);

        var extra = ClaudeLine(9999) + "\n";
        System.IO.File.AppendAllText(path, extra);
        var read = index.Refresh(path, AiLogParsers.ParseClaude);
        Assert.Equal(2001, index.Items.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(extra), read);
    }

    [Fact]
    public void Reader_starts_over_when_the_file_shrinks()
    {
        var path = File("shrink.jsonl");
        System.IO.File.WriteAllText(path, ClaudeLine(0) + "\n" + ClaudeLine(1) + "\n" + ClaudeLine(2) + "\n");
        var index = new JsonlFileIndex<UsageEntry>();
        index.Refresh(path, AiLogParsers.ParseClaude);
        Assert.Equal(3, index.Items.Count);

        System.IO.File.WriteAllText(path, ClaudeLine(7) + "\n");
        index.Refresh(path, AiLogParsers.ParseClaude);
        Assert.Single(index.Items);
        Assert.Equal("m7|r7", index.Items[0].DedupKey);
    }

    [Fact]
    public void Scanner_finds_nested_subagent_logs_and_drops_files_that_vanish()
    {
        var root = Path.Combine(_dir, "projects");
        var sub = Path.Combine(root, "proj-a", "session-1", "subagents");
        Directory.CreateDirectory(sub);
        System.IO.File.WriteAllText(Path.Combine(root, "proj-a", "main.jsonl"), ClaudeLine(0) + "\n");
        System.IO.File.WriteAllText(Path.Combine(sub, "agent-1.jsonl"), ClaudeLine(1) + "\n");
        System.IO.File.WriteAllText(Path.Combine(sub, "notes.txt"), ClaudeLine(2) + "\n");

        var scanner = new AiLogScanner();
        var output = scanner.Scan(new[] { root });
        Assert.Equal(2, output.Files);
        Assert.Equal(2, output.Claude.Count);

        System.IO.File.Delete(Path.Combine(sub, "agent-1.jsonl"));
        output = scanner.Scan(new[] { root });
        Assert.Equal(1, output.Files);
        Assert.Single(output.Claude);
    }

    [Fact]
    public void Scanner_is_incremental_across_scans()
    {
        var root = Path.Combine(_dir, "projects");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "s.jsonl");
        System.IO.File.WriteAllText(path, ClaudeLine(0) + "\n");

        var scanner = new AiLogScanner();
        Assert.True(scanner.IsEmpty);
        var first = scanner.Scan(new[] { root });
        Assert.False(scanner.IsEmpty);
        Assert.Single(first.Claude);

        var appended = ClaudeLine(1) + "\n";
        System.IO.File.AppendAllText(path, appended);
        var second = scanner.Scan(new[] { root });
        Assert.Equal(2, second.Claude.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(appended), second.BytesRead);

        scanner.Reset();
        Assert.True(scanner.IsEmpty);
    }

    [Fact]
    public void Dedupe_removes_repeated_claude_lines_read_by_the_scanner()
    {
        var claudeRoot = Path.Combine(_dir, "projects");
        Directory.CreateDirectory(claudeRoot);
        // One message logged three times (one line per content block), identical usage.
        var same = ClaudeLine(5);
        System.IO.File.WriteAllText(Path.Combine(claudeRoot, "dup.jsonl"), same + "\n" + same + "\n" + same + "\n");

        var output = new AiLogScanner().Scan(new[] { claudeRoot });
        Assert.Equal(3, output.Claude.Count);
        Assert.Single(AiUsageMath.Dedupe(output.Claude));
    }
}

public sealed class AiUsageRootsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "perch-ai-home-" + Guid.NewGuid().ToString("N"));

    public AiUsageRootsTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Defaults_use_existing_claude_folders_under_the_user_profile_only()
    {
        Assert.Empty(AiRoots.Claude(null, null, _home));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "claude", "projects"));
        var roots = AiRoots.Claude(null, null, _home);
        Assert.Equal(new[] { Path.Combine(_home, ".config", "claude", "projects") }, roots);
    }

    [Fact]
    public void Config_dir_may_list_several_folders_and_overrides_the_defaults()
    {
        var a = Path.Combine(_home, "a");
        var b = Path.Combine(_home, "b");
        Directory.CreateDirectory(Path.Combine(a, "projects"));
        Directory.CreateDirectory(Path.Combine(b, "projects"));
        Directory.CreateDirectory(Path.Combine(_home, ".claude", "projects"));

        var roots = AiRoots.Claude(null, $"{a}, {b}, {Path.Combine(_home, "missing")}", _home);
        Assert.Equal(new[] { Path.Combine(a, "projects"), Path.Combine(b, "projects") }, roots);

        var forced = AiRoots.Claude(a, null, _home);
        Assert.Equal(new[] { Path.Combine(a, "projects") }, forced);
    }

}

public sealed class AiUsageMathTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static UsageEntry E(DateTime t, string model, long input, long output, string? key = null, long cacheRead = 0) =>
        new(t, model, input, output, 0, cacheRead, key);

    private static DateTime At(int h, int m = 0) => new(2026, 11, 14, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void Dedupe_keeps_the_first_entry_per_key_and_always_keeps_keyless_ones()
    {
        var list = new List<UsageEntry>
        {
            E(At(10, 2), "a", 5, 5, "m1|r"),
            E(At(10, 0), "a", 5, 5, "m1|r"),
            E(At(10, 1), "a", 1, 1, null),
            E(At(10, 3), "a", 1, 1, null),
        };
        var deduped = AiUsageMath.Dedupe(list);
        Assert.Equal(3, deduped.Count);
        Assert.Equal(At(10, 0), deduped[0].TimeUtc);
    }

    [Fact]
    public void Windows_start_on_the_floored_hour_and_last_five_hours()
    {
        var sorted = AiUsageMath.Dedupe(new[]
        {
            E(At(9, 37), "a", 10, 10),
            E(At(10, 5), "a", 10, 10),
            E(At(13, 59), "a", 10, 10),
            E(At(14, 0), "b", 30, 0),   // exactly 5 h after the first window's start: a new window
        });
        var blocks = AiUsageMath.Blocks(sorted);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(At(9), blocks[0].StartUtc);
        Assert.Equal(At(14), blocks[0].EndUtc);
        Assert.Equal(3, blocks[0].Messages);
        Assert.Equal(At(14), blocks[1].StartUtc);
        Assert.Equal(At(19), blocks[1].EndUtc);
    }

    [Fact]
    public void Window_totals_split_active_tokens_cache_and_models()
    {
        var sorted = AiUsageMath.Dedupe(new[]
        {
            E(At(9, 30), "claude-opus-5-5", 100, 300, cacheRead: 1000),
            E(At(10, 30), "claude-sonnet-5", 50, 50),
            E(At(11, 0), "claude-opus-5-5", 200, 200, cacheRead: 2000),
        });
        var block = Assert.Single(AiUsageMath.Blocks(sorted));
        Assert.Equal(350, block.Input);
        Assert.Equal(550, block.Output);
        Assert.Equal(900, block.ActiveTokens);
        Assert.Equal(3000, block.CacheTokens);
        Assert.Equal(3, block.Messages);
        Assert.Equal(800, block.ModelTokens["claude-opus-5-5"]);
        Assert.Equal("claude-opus-5-5", block.TopModel);
    }

    [Fact]
    public void Current_window_is_the_last_one_only_while_now_is_before_its_end()
    {
        var blocks = AiUsageMath.Blocks(AiUsageMath.Dedupe(new[] { E(At(10, 0), "a", 1, 1) }));
        Assert.NotNull(AiUsageMath.CurrentBlock(blocks, At(14, 59)));
        Assert.Null(AiUsageMath.CurrentBlock(blocks, At(15, 0)));
        Assert.Null(AiUsageMath.CurrentBlock(new List<ClaudeBlock>(), At(12)));
    }

    [Fact]
    public void Totals_cover_the_span_and_split_by_model()
    {
        var entries = new[]
        {
            E(At(9), "claude-opus-5-5", 10, 5),
            E(At(10), "claude-fable-5-2", 100, 20),
            E(At(11), "claude-fable-5-2", 1, 1),
            E(At(13), "claude-opus-5-5", 1000, 1000), // after the span
        };
        var all = AiUsageMath.Total(entries, At(9), At(12));
        Assert.Equal(122, all.Tokens);     // 09:00 is the open start of (09:00, 12:00]
        Assert.Equal(2, all.Messages);
        Assert.Equal(122, all.ModelTokens["claude-fable-5-2"]);

        var fable = AiUsageMath.Total(entries, At(0), At(12), e => AiUsageMath.IsFable(e.Model));
        Assert.Equal(122, fable.Tokens);
        Assert.Single(fable.ModelTokens);

        Assert.Same(UsageTotal.Empty, AiUsageMath.Total(entries, At(14), At(15)));
    }

    [Theory]
    [InlineData("claude-fable-5-2", true)]
    [InlineData("Fable", true)]
    [InlineData("claude-opus-5-5", false)]
    [InlineData("claude-haiku-5-5", false)]
    public void Fable_models_are_recognised(string model, bool expected) => Assert.Equal(expected, AiUsageMath.IsFable(model));

    [Fact]
    public void Summary_has_the_open_window_the_last_seven_days_and_fable_alone()
    {
        var now = At(12);
        var summary = AiUsageMath.SummarizeClaude(new[]
        {
            E(At(11), "claude-opus-5-5", 10, 10, "k1"),
            E(At(11), "claude-opus-5-5", 10, 10, "k1"),                  // duplicate of the same message
            E(now.AddDays(-3), "claude-fable-5-2", 300, 100, "k2"),       // this week, before the 5-hour window
            E(now.AddDays(-7).AddMinutes(1), "claude-fable-5-2", 1, 1, "k3"), // just inside 7 days
            E(now.AddDays(-7), "claude-opus-5-5", 5000, 5000, "k4"),      // exactly 7 days ago: out
        }, now);

        Assert.NotNull(summary.Current);
        Assert.Equal(At(11), summary.Current!.StartUtc);
        Assert.Equal(20, summary.Current.ActiveTokens);
        Assert.Equal(422, summary.Week.Tokens);
        Assert.Equal(3, summary.Week.Messages);
        Assert.Equal(402, summary.FableWeek.Tokens);
        Assert.Equal(2, summary.FableWeek.Messages);
        Assert.Equal(At(11), summary.LastActivityUtc);
    }

    [Fact]
    public void Summary_without_recent_use_has_no_window_and_empty_weeks()
    {
        var summary = AiUsageMath.SummarizeClaude(new[] { E(At(1).AddDays(-10), "claude-fable-5-2", 10, 10, "k1") }, At(12));
        Assert.Null(summary.Current);
        Assert.Equal(0, summary.Week.Tokens);
        Assert.Equal(0, summary.FableWeek.Tokens);
    }
}

public sealed class AiUsageFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(512, "512")]
    [InlineData(999, "999")]
    [InlineData(1234, "1.2K")]
    [InlineData(38_000, "38K")]
    [InlineData(1_200_000, "1.2M")]
    [InlineData(4_100_000, "4.1M")]
    [InlineData(2_300_000_000, "2.3B")]
    public void Token_counts_are_short(long n, string expected) => Assert.Equal(expected, AiFormat.Tokens(n));

    [Fact]
    public void Remaining_time_reads_naturally()
    {
        Assert.Equal("2 h 14 m", AiFormat.Remaining(TimeSpan.FromMinutes(134)));
        Assert.Equal("38 m", AiFormat.Remaining(TimeSpan.FromMinutes(38)));
        Assert.Equal("under a minute", AiFormat.Remaining(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Ago_and_percent_text()
    {
        Assert.Equal("12 s ago", AiFormat.Ago(TimeSpan.FromSeconds(12)));
        Assert.Equal("3 min ago", AiFormat.Ago(TimeSpan.FromMinutes(3)));
        Assert.Equal("82%", AiFormat.Percent(0.82));
        Assert.Equal("1 message", AiFormat.Plural(1, "message"));
    }

    [Theory]
    [InlineData("2.5M", 2_500_000)]
    [InlineData("800k", 800_000)]
    [InlineData("800K", 800_000)]
    [InlineData("1200000", 1_200_000)]
    [InlineData("1,200,000", 1_200_000)]
    [InlineData(" 3 m ", 3_000_000)]
    public void Budgets_accept_short_forms(string text, long expected)
    {
        Assert.True(AiFormat.TryParseBudget(text, out var budget));
        Assert.Equal(expected, budget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_budget_means_no_budget(string? text)
    {
        Assert.True(AiFormat.TryParseBudget(text, out var budget));
        Assert.Null(budget);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5k")]
    [InlineData("1e99")]
    [InlineData("k")]
    public void Bad_budgets_are_rejected(string text)
    {
        Assert.False(AiFormat.TryParseBudget(text, out _));
    }
}

public sealed class AiUsageWarningTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ScanStats Stats = new(0, 0, 0, 0, false);

    private static ClaudeBlock Block(long active) =>
        new(new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc),
            active / 2, active - active / 2, 0, 0, 4, new Dictionary<string, long> { ["claude-opus-5-5"] = active });

    private static UsageTotal Week(long tokens) =>
        tokens == 0 ? UsageTotal.Empty : new UsageTotal(tokens, 1, new Dictionary<string, long> { ["m"] = tokens });

    private static AiSnapshot Snap(DateTime now, ClaudeBlock? block = null, long week = 0, long fable = 0) =>
        new(now, new ClaudeSummary(block, Week(week), Week(fable), now), Stats);

    [Fact]
    public void Five_hour_window_warns_once_when_the_budget_passes_the_threshold()
    {
        var settings = new AiUsageSettings { ClaudeBlockBudgetTokens = 1000, NearPercent = 80 };

        var below = AiWarnings.Evaluate(Snap(Now, Block(790)), settings);
        Assert.Empty(below.Messages);

        var crossed = AiWarnings.Evaluate(Snap(Now, Block(820)), settings);
        Assert.Equal(new[] { "Usage 5h at 82%" }, crossed.Messages);
        settings.Notified = crossed.State;

        var again = AiWarnings.Evaluate(Snap(Now.AddMinutes(5), Block(900)), settings);
        Assert.Empty(again.Messages);
    }

    [Fact]
    public void Five_hour_window_says_fresh_once_the_warned_window_ends()
    {
        var settings = new AiUsageSettings { ClaudeBlockBudgetTokens = 1000, NearPercent = 80 };
        var warned = AiWarnings.Evaluate(Snap(Now, Block(900)), settings);
        settings.Notified = warned.State;

        var later = AiWarnings.Evaluate(Snap(Now.AddHours(3.5)), settings);
        Assert.Equal(new[] { "Fresh 5-hour window" }, later.Messages);
        Assert.Null(later.State.ClaudeWarnedEndTicks);
    }

    [Fact]
    public void A_budget_is_required_for_warnings()
    {
        var result = AiWarnings.Evaluate(Snap(Now, Block(999_999), week: 999_999, fable: 999_999), new AiUsageSettings());
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void Weekly_and_fable_warn_once_and_again_only_after_dropping_back()
    {
        var settings = new AiUsageSettings { WeeklyBudgetTokens = 10_000, FableBudgetTokens = 1000, NearPercent = 80 };

        var first = AiWarnings.Evaluate(Snap(Now, week: 8500, fable: 950), settings);
        Assert.Equal(new[] { "Usage weekly at 85%", "Usage Fable at 95%" }, first.Messages);
        settings.Notified = first.State;

        var repeat = AiWarnings.Evaluate(Snap(Now.AddHours(1), week: 9000, fable: 990), settings);
        Assert.Empty(repeat.Messages);
        settings.Notified = repeat.State;

        // Old usage rolls out of the 7 days: the flags clear quietly, and a later climb warns again.
        var cooled = AiWarnings.Evaluate(Snap(Now.AddDays(2), week: 4000, fable: 950), settings);
        Assert.Empty(cooled.Messages);
        Assert.False(cooled.State.WeeklyWarned);
        Assert.True(cooled.State.FableWarned);
        settings.Notified = cooled.State;

        var climbed = AiWarnings.Evaluate(Snap(Now.AddDays(3), week: 8100, fable: 960), settings);
        Assert.Equal(new[] { "Usage weekly at 81%" }, climbed.Messages);
    }
}
