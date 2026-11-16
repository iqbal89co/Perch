using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perch.Pages.Quran;

/// <summary>
/// Pure JSON parsing for the Quran.com v4 responses and the alquran.cloud fallback. Malformed segments are
/// skipped; a response without an audio URL or a verses array throws FormatException.
/// </summary>
public static class QuranParser
{
    private static readonly Regex SupFootnote = new(@"<sup\b[^>]*>.*?</sup>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Parses /chapter_recitations/{reciter}/{chapter}?segments=true.</summary>
    public static Recitation ParseRecitation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("audio_file", out var file) || file.ValueKind != JsonValueKind.Object)
            throw new FormatException("no audio_file");
        var url = StringProp(file, "audio_url");
        if (string.IsNullOrWhiteSpace(url)) throw new FormatException("no audio_url");

        var ayahs = new List<AyahTiming>();
        var seen = new HashSet<int>();
        if (file.TryGetProperty("timestamps", out var stamps) && stamps.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in stamps.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var number = VerseNumber(StringProp(t, "verse_key"));
                if (number <= 0 || !seen.Add(number)) continue;

                var words = Segments(t);
                var from = LongProp(t, "timestamp_from") ?? (words.Count > 0 ? words[0].FromMs : -1);
                var to = LongProp(t, "timestamp_to") ?? (words.Count > 0 ? words[^1].ToMs : -1);
                if (from < 0 || to <= from) continue;
                ayahs.Add(new AyahTiming(number, from, to, words));
            }
        }
        ayahs.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        return new Recitation(url, ayahs);
    }

    /// <summary>Parses a verses response (or the merged {"verses":[...]} the client caches).</summary>
    public static IReadOnlyList<AyahText> ParseVerses(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("verses", out var verses) || verses.ValueKind != JsonValueKind.Array)
            throw new FormatException("no verses");

        var list = new List<AyahText>();
        foreach (var v in verses.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            var number = VerseNumber(StringProp(v, "verse_key"));
            if (number <= 0) continue;

            var words = new List<WordText>();
            if (v.TryGetProperty("words", out var wordArray) && wordArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wordArray.EnumerateArray())
                {
                    var text = StringProp(w, "text_uthmani") ?? StringProp(w, "text") ?? "";
                    if (text.Trim().Length == 0) continue;
                    var position = IntProp(w, "position") ?? words.Count + 1;
                    var isEnd = string.Equals(StringProp(w, "char_type_name"), "end", StringComparison.OrdinalIgnoreCase);
                    words.Add(new WordText(position, text.Trim(), isEnd));
                }
            }

            var translations = new Dictionary<int, string>();
            if (v.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tr.EnumerateArray())
                {
                    var id = IntProp(t, "resource_id");
                    var text = StripHtml(StringProp(t, "text"));
                    if (id is { } rid && text.Length > 0) translations[rid] = text;
                }
            }

            list.Add(new AyahText(number, (StringProp(v, "text_uthmani") ?? "").Trim(), words, translations));
        }
        list.Sort((a, b) => a.Number.CompareTo(b.Number));
        return list;
    }

    /// <summary>Parses alquran.cloud /surah/{n}/editions/quran-uthmani,en.sahih,id.indonesian (ayah level only).</summary>
    public static IReadOnlyList<AyahText> ParseAlQuranCloud(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("no data");

        var arabic = new Dictionary<int, string>();
        var english = new Dictionary<int, string>();
        var indonesian = new Dictionary<int, string>();
        foreach (var edition in data.EnumerateArray())
        {
            var identifier = edition.TryGetProperty("edition", out var ed) ? StringProp(ed, "identifier") : null;
            var map = identifier switch
            {
                "quran-uthmani" => arabic,
                "en.sahih" => english,
                "id.indonesian" => indonesian,
                _ => null,
            };
            if (map is null || !edition.TryGetProperty("ayahs", out var ayahs) || ayahs.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in ayahs.EnumerateArray())
            {
                var n = IntProp(a, "numberInSurah");
                var text = StringProp(a, "text");
                if (n is not { } num || string.IsNullOrWhiteSpace(text)) continue;
                // The Arabic is kept as is; translations come back with the same footnote markup as Quran.com.
                map[num] = map == arabic ? text.Trim() : StripHtml(text);
            }
        }

        var list = new List<AyahText>();
        foreach (var (number, text) in arabic.OrderBy(p => p.Key))
        {
            var translations = new Dictionary<int, string>();
            if (english.TryGetValue(number, out var en)) translations[QuranIds.SaheehInternational] = en;
            if (indonesian.TryGetValue(number, out var idText)) translations[QuranIds.Kemenag] = idText;
            list.Add(new AyahText(number, text, Array.Empty<WordText>(), translations));
        }
        return list;
    }

    /// <summary>Removes footnote &lt;sup&gt; elements, other tags and entities, and collapses white space.</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = SupFootnote.Replace(html, "");
        s = Tag.Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>"36:2" gives 2; anything else gives 0.</summary>
    public static int VerseNumber(string? verseKey)
    {
        if (string.IsNullOrEmpty(verseKey)) return 0;
        var colon = verseKey.LastIndexOf(':');
        var part = colon >= 0 ? verseKey[(colon + 1)..] : verseKey;
        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>Segments are [wordPosition, fromMs, toMs]. Short, non-numeric or inverted entries are skipped.</summary>
    private static List<WordSegment> Segments(JsonElement timestamp)
    {
        var list = new List<WordSegment>();
        if (!timestamp.TryGetProperty("segments", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() < 3) continue;
            if (!TryLong(s[0], out var position) || !TryLong(s[1], out var from) || !TryLong(s[2], out var to)) continue;
            if (position < 1 || position > 10_000 || from < 0 || to <= from) continue;
            list.Add(new WordSegment((int)position, from, to));
        }
        list.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        return list;
    }

    private static bool TryLong(JsonElement e, out long value)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            if (e.TryGetInt64(out value)) return true;
            if (e.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15)
            {
                value = (long)Math.Round(d);
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static string? StringProp(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static long? LongProp(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && TryLong(p, out var v) ? v : null;

    private static int? IntProp(JsonElement el, string name) =>
        LongProp(el, name) is { } v && v >= int.MinValue && v <= int.MaxValue ? (int)v : null;
}
