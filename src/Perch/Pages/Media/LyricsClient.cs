using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Perch.Core;

namespace Perch.Pages.Media;

public enum LyricsStatus { Synced, Plain, NotFound, Error }

/// <summary>Outcome of a lyrics lookup. Lines for synced lyrics, Plain for unsynced text.</summary>
public sealed record LyricsResult(LyricsStatus Status, IReadOnlyList<LyricLine> Lines, string Plain);

/// <summary>Stable cache file name for a track: first 16 hex chars of SHA-256 over "artist|title|album".</summary>
public static class LyricsCacheKey
{
    public static string For(string artist, string title, string album)
    {
        var text = $"{Normalise(artist)}|{Normalise(title)}|{Normalise(album)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string Normalise(string? s) => (s ?? "").Trim().ToLowerInvariant();
}

/// <summary>
/// Looks lyrics up on lrclib.net with a disk cache under AppPaths.Cache/lyrics. Never throws: any failure
/// comes back as LyricsStatus.Error (and is not cached).
/// </summary>
public static class LyricsClient
{
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Perch/1.0");
        return client;
    }

    public static async Task<LyricsResult> FetchAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct = default)
    {
        try
        {
            var key = LyricsCacheKey.For(artist, title, album);
            var cached = ReadCache(key);
            if (cached != null)
            {
                var fromCache = FromCache(cached);
                if (fromCache != null) return fromCache;
            }

            var result = await FetchRemoteAsync(artist, title, album, duration, ct);
            if (result.Status != LyricsStatus.Error) WriteCache(key, result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"lyrics: lookup failed ({ex.Message})");
            return new LyricsResult(LyricsStatus.Error, Array.Empty<LyricLine>(), "");
        }
    }

    private static async Task<LyricsResult> FetchRemoteAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        var url = $"https://lrclib.net/api/get?track_name={Esc(title)}&artist_name={Esc(artist)}" +
                  $"&album_name={Esc(album)}&duration={(int)Math.Round(duration.TotalSeconds)}";
        using var get = await Http.GetAsync(url, ct);
        if (get.StatusCode == HttpStatusCode.NotFound) return await SearchAsync(artist, title, ct);
        if (!get.IsSuccessStatusCode) return new LyricsResult(LyricsStatus.Error, Array.Empty<LyricLine>(), "");

        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct));
        return FromRecord(doc.RootElement) ?? NotFound();
    }

    private static async Task<LyricsResult> SearchAsync(string artist, string title, CancellationToken ct)
    {
        var url = $"https://lrclib.net/api/search?track_name={Esc(title)}&artist_name={Esc(artist)}";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return NotFound();
        if (!resp.IsSuccessStatusCode) return new LyricsResult(LyricsStatus.Error, Array.Empty<LyricLine>(), "");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return NotFound();

        foreach (var item in root.EnumerateArray())
        {
            var synced = StringProp(item, "syncedLyrics");
            if (string.IsNullOrWhiteSpace(synced)) continue;
            var lines = LrcParser.Parse(synced);
            if (lines.Count > 0) return new LyricsResult(LyricsStatus.Synced, lines, "");
        }

        foreach (var item in root.EnumerateArray())
        {
            var plain = StringProp(item, "plainLyrics");
            if (!string.IsNullOrWhiteSpace(plain)) return new LyricsResult(LyricsStatus.Plain, Array.Empty<LyricLine>(), plain);
        }

        return NotFound();
    }

    private static LyricsResult? FromRecord(JsonElement record)
    {
        var synced = StringProp(record, "syncedLyrics");
        if (!string.IsNullOrWhiteSpace(synced))
        {
            var lines = LrcParser.Parse(synced);
            if (lines.Count > 0) return new LyricsResult(LyricsStatus.Synced, lines, "");
        }

        var plain = StringProp(record, "plainLyrics");
        if (!string.IsNullOrWhiteSpace(plain)) return new LyricsResult(LyricsStatus.Plain, Array.Empty<LyricLine>(), plain);
        return null;
    }

    private static LyricsResult NotFound() => new(LyricsStatus.NotFound, Array.Empty<LyricLine>(), "");

    private static string Esc(string? s) => Uri.EscapeDataString(s ?? "");

    private static string? StringProp(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    // ---- disk cache -------------------------------------------------------------------------------

    private sealed record CachedLine(long Ms, string Text);

    private sealed record CacheEntry(string Status, List<CachedLine>? Lines, string? Plain, DateTimeOffset CheckedAt);

    private static string CacheFile(string key)
    {
        var dir = Path.Combine(AppPaths.Cache, "lyrics");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, key + ".json");
    }

    private static CacheEntry? ReadCache(string key)
    {
        try
        {
            var file = CacheFile(key);
            return File.Exists(file) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"lyrics: cache unreadable ({ex.Message})");
            return null;
        }
    }

    private static void WriteCache(string key, LyricsResult result)
    {
        try
        {
            var entry = result.Status switch
            {
                LyricsStatus.Synced => new CacheEntry("synced",
                    result.Lines.Select(l => new CachedLine((long)l.Time.TotalMilliseconds, l.Text)).ToList(), null, DateTimeOffset.UtcNow),
                LyricsStatus.Plain => new CacheEntry("plain", null, result.Plain, DateTimeOffset.UtcNow),
                _ => new CacheEntry("notfound", null, null, DateTimeOffset.UtcNow),
            };
            File.WriteAllText(CacheFile(key), JsonSerializer.Serialize(entry));
        }
        catch (Exception ex)
        {
            Log.Warn($"lyrics: cache write failed ({ex.Message})");
        }
    }

    /// <summary>Found lyrics are kept forever; "not found" is retried after 24 hours.</summary>
    private static LyricsResult? FromCache(CacheEntry entry)
    {
        switch (entry.Status)
        {
            case "synced":
                var lines = (entry.Lines ?? new()).Select(l => new LyricLine(TimeSpan.FromMilliseconds(l.Ms), l.Text)).ToList();
                return lines.Count > 0 ? new LyricsResult(LyricsStatus.Synced, lines, "") : null;
            case "plain" when !string.IsNullOrWhiteSpace(entry.Plain):
                return new LyricsResult(LyricsStatus.Plain, Array.Empty<LyricLine>(), entry.Plain);
            case "notfound" when DateTimeOffset.UtcNow - entry.CheckedAt < NotFoundRetry:
                return NotFound();
            default:
                return null;
        }
    }
}
