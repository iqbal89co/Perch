using System.Diagnostics.CodeAnalysis;

namespace Perch.Pages.AiUsage;

/// <summary>Everything the scanner read in one pass, flattened for aggregation.</summary>
public sealed record ScanOutput(List<UsageEntry> Claude, List<CodexEvent> Codex, int Files, long BytesRead);

/// <summary>
/// Keeps one incremental index per log file and refreshes all of them. Files that disappeared are dropped.
/// Not thread-safe: the page runs one scan at a time on a background task.
/// </summary>
public sealed class AiLogScanner
{
    private readonly Dictionary<string, JsonlFileIndex<UsageEntry>> _claude = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonlFileIndex<CodexEvent>> _codex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True before the first scan and after <see cref="Reset"/>: the next scan reads everything.</summary>
    public bool IsEmpty => _claude.Count == 0 && _codex.Count == 0;

    public void Reset()
    {
        _claude.Clear();
        _codex.Clear();
    }

    public ScanOutput Scan(IReadOnlyList<string> claudeRoots, IReadOnlyList<string> codexRoots)
    {
        var counters = new Counters();
        ScanRoots(claudeRoots, _claude, AiLogParsers.ParseClaude, ref counters);
        ScanRoots(codexRoots, _codex, AiLogParsers.ParseCodex, ref counters);

        var claude = new List<UsageEntry>();
        foreach (var index in _claude.Values) claude.AddRange(index.Items);
        var codex = new List<CodexEvent>();
        foreach (var index in _codex.Values) codex.AddRange(index.Items);
        return new ScanOutput(claude, codex, counters.Files, counters.Bytes);
    }

    private struct Counters
    {
        public int Files;
        public long Bytes;
    }

    private static void ScanRoots<T>(IReadOnlyList<string> roots, Dictionary<string, JsonlFileIndex<T>> indexes,
        LineParser<T> parse, ref Counters counters) where T : class
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var file in ListLogs(root))
            {
                if (!seen.Add(file)) continue;
                if (!indexes.TryGetValue(file, out var index))
                {
                    index = new JsonlFileIndex<T>();
                    indexes[file] = index;
                }
                try
                {
                    counters.Bytes += index.Refresh(file, parse);
                    counters.Files++;
                }
                catch (IOException)
                {
                    // Busy or vanished between listing and reading: try again on the next refresh.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        foreach (var gone in indexes.Keys.Where(k => !seen.Contains(k)).ToList()) indexes.Remove(gone);
    }

    private static List<string> ListLogs(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}

/// <summary>
/// Where the tools keep their logs. Each source is optional: a source whose folder does not exist is not shown.
/// Environment variables follow the tools' own conventions. PERCH_AI_CLAUDE_DIR and PERCH_AI_CODEX_DIR take
/// precedence and point at a folder laid out like CLAUDE_CONFIG_DIR / CODEX_HOME (used for testing).
/// </summary>
public static class AiRoots
{
    public static List<string> ClaudeFromEnvironment() => Claude(
        Environment.GetEnvironmentVariable("PERCH_AI_CLAUDE_DIR"),
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static List<string> CodexFromEnvironment() => Codex(
        Environment.GetEnvironmentVariable("PERCH_AI_CODEX_DIR"),
        Environment.GetEnvironmentVariable("CODEX_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Claude Code project folders that exist. CLAUDE_CONFIG_DIR may hold several comma-separated folders.</summary>
    public static List<string> Claude(string? overrideDir, string? configDirs, string userProfile)
    {
        if (!IsBlank(overrideDir)) return Existing(Split(overrideDir).Select(d => Path.Combine(d, "projects")));
        if (!IsBlank(configDirs)) return Existing(Split(configDirs).Select(d => Path.Combine(d, "projects")));
        if (IsBlank(userProfile)) return new List<string>();
        return Existing(new[]
        {
            Path.Combine(userProfile, ".claude", "projects"),
            Path.Combine(userProfile, ".config", "claude", "projects"),
        });
    }

    /// <summary>Codex CLI session folders that exist.</summary>
    public static List<string> Codex(string? overrideDir, string? codexHome, string userProfile)
    {
        if (!IsBlank(overrideDir)) return Existing(new[] { Path.Combine(overrideDir.Trim(), "sessions") });
        if (!IsBlank(codexHome)) return Existing(new[] { Path.Combine(codexHome.Trim(), "sessions") });
        if (IsBlank(userProfile)) return new List<string>();
        return Existing(new[] { Path.Combine(userProfile, ".codex", "sessions") });
    }

    private static bool IsBlank([NotNullWhen(false)] string? s) => string.IsNullOrWhiteSpace(s);

    private static IEnumerable<string> Split(string s) =>
        s.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);

    private static List<string> Existing(IEnumerable<string> dirs) =>
        dirs.Where(d => Directory.Exists(d)).ToList();
}
