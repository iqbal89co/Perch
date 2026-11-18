using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Perch.Core;

namespace Perch.Pages.AiUsage;

/// <summary>Why the plan figures are (or are not) available.</summary>
public enum PlanStatus
{
    Ok,
    /// <summary>No Claude Code sign-in on this PC (no credentials file or no token in it).</summary>
    SignedOut,
    /// <summary>Claude Code's token has expired; Claude Code renews it the next time it runs.</summary>
    Expired,
    /// <summary>Anthropic could not be reached, or answered with an error.</summary>
    Unavailable,
}

/// <summary>One limit as Claude Code's /usage shows it: a label, the percent used and when it resets.</summary>
public sealed record PlanLimit(string Kind, string Label, double Percent, DateTime? ResetsAtUtc, bool Elevated)
{
    /// <summary>The 5-hour session limit.</summary>
    public bool IsSession => Kind == "session";

    /// <summary>The weekly limit across all models.</summary>
    public bool IsWeeklyAll => Kind == "weekly_all";

    /// <summary>A weekly limit for one model family (for example Fable).</summary>
    public bool IsFable => Label.Contains("Fable", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The plan limits from one request, or why there are none.</summary>
public sealed record PlanUsage(PlanStatus Status, IReadOnlyList<PlanLimit> Limits, DateTime FetchedUtc)
{
    public static PlanUsage Failed(PlanStatus status, DateTime nowUtc) => new(status, Array.Empty<PlanLimit>(), nowUtc);
}

/// <summary>
/// Turns the usage response into the rows /usage shows. Prefers the response's "limits" list (already in display
/// order); falls back to the older five_hour / seven_day / seven_day_* fields. Pure, for tests.
/// </summary>
public static class ClaudePlanParser
{
    private static readonly (string Field, string Kind, string Label)[] LegacyFields =
    {
        ("five_hour", "session", "Usage 5h"),
        ("seven_day", "weekly_all", "Usage weekly"),
        ("seven_day_opus", "weekly_scoped", "Usage Opus"),
        ("seven_day_sonnet", "weekly_scoped", "Usage Sonnet"),
    };

    public static IReadOnlyList<PlanLimit> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = new List<PlanLimit>();

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(item, "kind") ?? "";
                if (Num(item, "percent") is not { } percent) continue;
                var severity = Str(item, "severity");
                rows.Add(new PlanLimit(kind, Label(kind, item), percent, Time(item, "resets_at"),
                    severity != null && !severity.Equals("normal", StringComparison.OrdinalIgnoreCase)));
            }
            if (rows.Count > 0) return rows;
        }

        foreach (var (field, kind, label) in LegacyFields)
        {
            if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            if (Num(value, "utilization") is not { } percent) continue;
            rows.Add(new PlanLimit(kind, label, percent, Time(value, "resets_at"), false));
        }
        return rows;
    }

    /// <summary>The same names /usage uses, in Perch's wording ("Current week (Fable)" reads "Usage Fable").</summary>
    private static string Label(string kind, JsonElement item)
    {
        switch (kind)
        {
            case "session": return "Usage 5h";
            case "weekly_all": return "Usage weekly";
        }
        if (item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object)
        {
            if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
                Str(model, "display_name") is { Length: > 0 } modelName)
                return "Usage " + modelName;
            if (Str(scope, "display_name") is { Length: > 0 } scopeName) return "Usage " + scopeName;
        }
        if (Str(item, "display_name") is { Length: > 0 } name) return "Usage " + name;
        var words = kind.Replace('_', ' ').Trim();
        return words.Length == 0 ? "Usage" : "Usage " + words;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? Math.Clamp(d, 0, 1000)
            : null;

    private static DateTime? Time(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime
            : null;
}

/// <summary>
/// Claude Code's own sign-in, read from its credentials file. Only the access token and its expiry are read;
/// Perch never renews the token (renewing would replace the one Claude Code holds).
/// </summary>
public static class ClaudeCredentials
{
    /// <summary>Where Claude Code keeps the file: PERCH_AI_CLAUDE_DIR, then CLAUDE_CONFIG_DIR, then ~/.claude.</summary>
    public static IEnumerable<string> Candidates()
    {
        foreach (var variable in new[] { "PERCH_AI_CLAUDE_DIR", "CLAUDE_CONFIG_DIR" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (var dir in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Path.Combine(dir, ".credentials.json");
            yield break;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    }

    /// <summary>The token and its expiry from file text, or nulls when there is none.</summary>
    public static (string? Token, DateTime? ExpiresUtc) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                return (null, null);
            var token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            DateTime? expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : null;
            return (string.IsNullOrWhiteSpace(token) ? null : token, expires);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public static (string? Token, DateTime? ExpiresUtc) Read()
    {
        foreach (var path in Candidates())
        {
            try
            {
                if (File.Exists(path)) return Parse(File.ReadAllText(path));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return (null, null);
    }
}

/// <summary>
/// Asks Anthropic for the plan's usage, the same request Claude Code's /usage makes, using Claude Code's sign-in.
/// The token goes only to api.anthropic.com and is never logged or stored. Calls are spaced at least a minute
/// apart (five minutes after a "slow down" answer). PERCH_AI_PLAN_FILE points at a saved response instead (tests).
/// </summary>
public sealed class ClaudePlanClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SlowDownGap = TimeSpan.FromMinutes(5);
    /// <summary>A failed request keeps showing the last good figures for this long.</summary>
    private static readonly TimeSpan KeepLastGood = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = CreateHttp();

    private PlanUsage? _last;
    private PlanUsage? _lastGood;
    private DateTime _nextAllowedUtc = DateTime.MinValue;
    private bool _warned;

    /// <summary>Returns fresh figures, or the last ones when asked again too soon. Never throws.</summary>
    public async Task<PlanUsage> GetAsync(DateTime nowUtc, bool force)
    {
        if (!force && _last != null && nowUtc < _nextAllowedUtc) return _last;
        _nextAllowedUtc = nowUtc + MinGap;

        var result = await FetchAsync(nowUtc).ConfigureAwait(false);
        if (result.Status == PlanStatus.Ok)
        {
            _lastGood = result;
            _warned = false;
        }
        else if (result.Status == PlanStatus.Unavailable && _lastGood != null && nowUtc - _lastGood.FetchedUtc < KeepLastGood)
            result = _lastGood;
        _last = result;
        return result;
    }

    private async Task<PlanUsage> FetchAsync(DateTime nowUtc)
    {
        var file = Environment.GetEnvironmentVariable("PERCH_AI_PLAN_FILE");
        if (!string.IsNullOrWhiteSpace(file))
        {
            try
            {
                return new PlanUsage(PlanStatus.Ok, ClaudePlanParser.Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false)), nowUtc);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return PlanUsage.Failed(PlanStatus.Unavailable, nowUtc);
            }
        }

        var (token, expires) = ClaudeCredentials.Read();
        if (token == null) return PlanUsage.Failed(PlanStatus.SignedOut, nowUtc);
        if (expires is { } e && e <= nowUtc) return PlanUsage.Failed(PlanStatus.Expired, nowUtc);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return PlanUsage.Failed(PlanStatus.Expired, nowUtc);
            if (response.StatusCode == (HttpStatusCode)429) _nextAllowedUtc = nowUtc + SlowDownGap;
            if (!response.IsSuccessStatusCode)
            {
                WarnOnce($"ai: plan usage answered {(int)response.StatusCode}");
                return PlanUsage.Failed(PlanStatus.Unavailable, nowUtc);
            }
            var limits = ClaudePlanParser.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return limits.Count > 0
                ? new PlanUsage(PlanStatus.Ok, limits, nowUtc)
                : PlanUsage.Failed(PlanStatus.Unavailable, nowUtc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The message never contains the token (it is only in a request header).
            WarnOnce($"ai: plan usage unavailable ({ex.GetType().Name})");
            return PlanUsage.Failed(PlanStatus.Unavailable, nowUtc);
        }
    }

    /// <summary>One log line per outage, not one a minute.</summary>
    private void WarnOnce(string message)
    {
        if (_warned) return;
        _warned = true;
        Log.Warn(message);
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Perch",
            typeof(ClaudePlanClient).Assembly.GetName().Version?.ToString(3) ?? "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}
