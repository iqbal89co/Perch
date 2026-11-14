using System.Globalization;
using System.Text.Json;

namespace Perch.Pages.AiUsage;

/// <summary>
/// Turns one JSON Lines record into the few fields Perch keeps. A cheap byte search rejects most lines
/// before any JSON is parsed. Everything else in a line (prompts, outputs, paths) is ignored.
/// </summary>
public static class AiLogParsers
{
    private static ReadOnlySpan<byte> UsageMarker => "\"usage\""u8;
    private static ReadOnlySpan<byte> TokenCountMarker => "\"token_count\""u8;

    /// <summary>A Claude Code assistant line with usage, or null.</summary>
    public static UsageEntry? ParseClaude(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf(UsageMarker) < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "assistant") return null;
            if (!TryObj(root, "message", out var msg) || !TryObj(msg, "usage", out var usage)) return null;

            var model = Str(msg, "model");
            if (model == "<synthetic>") return null;
            if (ParseTime(Str(root, "timestamp")) is not { } time) return null;

            var messageId = Str(msg, "id");
            var requestId = Str(root, "requestId");
            return new UsageEntry(
                time,
                model ?? "unknown",
                Num(usage, "input_tokens"),
                Num(usage, "output_tokens"),
                Num(usage, "cache_creation_input_tokens"),
                Num(usage, "cache_read_input_tokens"),
                messageId == null ? null : messageId + "|" + (requestId ?? ""));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A Codex CLI token_count event (tokens of the turn and/or rate limits), or null.</summary>
    public static CodexEvent? ParseCodex(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf(TokenCountMarker) < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "event_msg") return null;
            if (!TryObj(root, "payload", out var payload) || Str(payload, "type") != "token_count") return null;
            if (ParseTime(Str(root, "timestamp")) is not { } time) return null;

            long tokens = 0;
            if (TryObj(payload, "info", out var info) && TryObj(info, "last_token_usage", out var last))
                tokens = Num(last, "input_tokens") + Num(last, "output_tokens");

            RateLimits? limits = null;
            if (TryObj(payload, "rate_limits", out var rl))
            {
                var primary = Window(rl, "primary", time);
                var secondary = Window(rl, "secondary", time);
                if (primary != null || secondary != null) limits = new RateLimits(time, primary, secondary);
            }

            if (tokens == 0 && limits == null) return null;
            return new CodexEvent(time, tokens, limits);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one window. A reset time comes from "resets_at" (unix seconds) or from "resets_in_seconds"
    /// relative to the event's own timestamp.
    /// </summary>
    private static RateWindow? Window(JsonElement parent, string name, DateTime eventUtc)
    {
        if (!TryObj(parent, name, out var w)) return null;
        if (Dbl(w, "used_percent") is not { } pct || !double.IsFinite(pct)) return null;

        var minutes = (int)Num(w, "window_minutes");
        DateTime? resets = null;
        if (Dbl(w, "resets_at") is { } unix && unix > 0 && unix < 1e11)
            resets = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
        else if (Dbl(w, "resets_in_seconds") is { } secs && double.IsFinite(secs))
            resets = eventUtc.AddSeconds(secs);
        return new RateWindow(pct, minutes, resets);
    }

    /// <summary>Parses an ISO-8601 timestamp to UTC. Returns null when missing or malformed.</summary>
    public static DateTime? ParseTime(string? text)
    {
        if (text == null) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime
            : (DateTime?)null;
    }

    private static bool TryObj(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Dbl(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out var d))
            return d;
        return null;
    }

    /// <summary>Integer field; missing, null or non-numeric values count as zero.</summary>
    private static long Num(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return 0;
        if (v.TryGetInt64(out var l)) return l;
        return v.TryGetDouble(out var d) && double.IsFinite(d) ? (long)d : 0;
    }
}
