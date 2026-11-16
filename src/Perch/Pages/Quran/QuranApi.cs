using System.IO;
using System.Net.Http;
using System.Text.Json;
using Perch.Core;

namespace Perch.Pages.Quran;

/// <summary>
/// Quran.com v4 text, timings and audio URLs with a disk cache under AppPaths.Cache\quran. Every JSON response
/// is cached, and the cache is read first, so an opened surah works offline. When Quran.com text fails and
/// nothing is cached, the alquran.cloud ayah-level text is used instead.
/// </summary>
public static class QuranApi
{
    public const string ApiBase = "https://api.quran.com/api/v4";
    public const string FallbackUrlFormat = "https://api.alquran.cloud/v1/surah/{0}/editions/quran-uthmani,en.sahih,id.indonesian";

    private static readonly int[] TranslationIds = { 20, 33, 85, 134, 57 };
    private const int MaxVersePages = 10;
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Perch/1.0");
        return client;
    }

    /// <summary>Ayah texts for a surah: cache, then Quran.com, then the alquran.cloud fallback (cache first too).</summary>
    public static async Task<VerseSet> GetVersesAsync(int chapter, CancellationToken ct)
    {
        var cached = ReadCache($"verses-{chapter}.json");
        if (cached != null)
        {
            var fromCache = TryParse(() => QuranParser.ParseVerses(cached));
            if (fromCache is { Count: > 0 }) return new VerseSet(fromCache, true);
        }

        try
        {
            var merged = await FetchVersesAsync(chapter, ct);
            var parsed = QuranParser.ParseVerses(merged);
            if (parsed.Count == 0) throw new FormatException("no verses");
            WriteCache($"verses-{chapter}.json", merged);
            return new VerseSet(parsed, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"quran: Quran.com text for surah {chapter} failed ({ex.Message}); trying the ayah-level fallback");
        }

        var fallback = ReadCache($"alquran-{chapter}.json");
        if (fallback != null)
        {
            var fromCache = TryParse(() => QuranParser.ParseAlQuranCloud(fallback));
            if (fromCache is { Count: > 0 }) return new VerseSet(fromCache, false);
        }

        var body = await GetStringAsync(string.Format(FallbackUrlFormat, chapter), ct);
        var ayahs = QuranParser.ParseAlQuranCloud(body);
        if (ayahs.Count == 0) throw new FormatException("fallback text is empty");
        WriteCache($"alquran-{chapter}.json", body);
        return new VerseSet(ayahs, false);
    }

    /// <summary>Audio URL and ayah timings for one surah by one reciter, cached after the first success.</summary>
    public static async Task<Recitation> GetTimingsAsync(int reciterId, int chapter, CancellationToken ct)
    {
        var name = $"audio-{reciterId}-{chapter}.json";
        var cached = ReadCache(name);
        if (cached != null)
        {
            var fromCache = TryParse(() => QuranParser.ParseRecitation(cached));
            if (fromCache is { Ayahs.Count: > 0 }) return fromCache;
        }

        var body = await GetStringAsync($"{ApiBase}/chapter_recitations/{reciterId}/{chapter}?segments=true", ct);
        var parsed = QuranParser.ParseRecitation(body);
        if (parsed.Ayahs.Count == 0) throw new FormatException("no ayah timings");
        WriteCache(name, body);
        return parsed;
    }

    /// <summary>Fetches every verses page and merges them into one {"verses":[...]} document.</summary>
    private static async Task<string> FetchVersesAsync(int chapter, CancellationToken ct)
    {
        var pieces = new List<string>();
        var translations = string.Join(",", TranslationIds);
        var page = 1;
        var total = 1;
        do
        {
            var url = $"{ApiBase}/verses/by_chapter/{chapter}?words=true&word_fields=text_uthmani&fields=text_uthmani" +
                      $"&translations={translations}&per_page=50&page={page}";
            var body = await GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("verses", out var verses) && verses.ValueKind == JsonValueKind.Array)
                foreach (var v in verses.EnumerateArray()) pieces.Add(v.GetRawText());

            total = root.TryGetProperty("pagination", out var pg)
                    && pg.TryGetProperty("total_pages", out var tp)
                    && tp.TryGetInt32(out var t)
                ? t
                : page;
            page++;
        } while (page <= total && page <= MaxVersePages);

        return "{\"verses\":[" + string.Join(",", pieces) + "]}";
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static T? TryParse<T>(Func<T> parse) where T : class
    {
        try
        {
            return parse();
        }
        catch (Exception ex)
        {
            Log.Warn($"quran: cached data unreadable ({ex.Message}); fetching again");
            return null;
        }
    }

    private static string CacheDir()
    {
        var dir = Path.Combine(AppPaths.Cache, "quran");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string? ReadCache(string name)
    {
        try
        {
            var file = Path.Combine(CacheDir(), name);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"quran: cache read failed for {name} ({ex.Message})");
            return null;
        }
    }

    internal static void WriteCache(string name, string json)
    {
        try
        {
            File.WriteAllText(Path.Combine(CacheDir(), name), json);
        }
        catch (Exception ex)
        {
            Log.Warn($"quran: cache write failed for {name} ({ex.Message})");
        }
    }
}
