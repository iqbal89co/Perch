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

    /// <summary>Integer field; missing, null or non-numeric values count as zero.</summary>
    private static long Num(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return 0;
        if (v.TryGetInt64(out var l)) return l;
        return v.TryGetDouble(out var d) && double.IsFinite(d) ? (long)d : 0;
    }
}
