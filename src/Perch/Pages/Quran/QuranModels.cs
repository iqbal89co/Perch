using System.IO;
using System.Text.Json;
using Perch.Core;

namespace Perch.Pages.Quran;

/// <summary>A Quran.com chapter reciter. Style is empty when the reciter has only one recitation style.</summary>
public sealed record Reciter(int Id, string Name, string Style)
{
    public static readonly IReadOnlyList<Reciter> All = new[]
    {
        new Reciter(97, "Yasser ad-Dussary", "Murattal"),
        new Reciter(7, "Mishari Rashid al-`Afasy", ""),
        new Reciter(3, "Abdur-Rahman as-Sudais", ""),
        new Reciter(10, "Sa`ud ash-Shuraym", ""),
        new Reciter(6, "Mahmoud Khaleel Al-Husary", ""),
        new Reciter(12, "Mahmoud Khalil Al-Husary", "Muallim"),
        new Reciter(9, "Muhammad Siddiq al-Minshawi", ""),
        new Reciter(2, "Abdul Basit Abdul Samad", "Murattal"),
        new Reciter(1, "Abdul Basit Abdul Samad", "Mujawwad"),
        new Reciter(4, "Abu Bakr al-Shatri", ""),
        new Reciter(5, "Hani ar-Rifai", ""),
        new Reciter(161, "Khalifah Al Tunaiji", ""),
        new Reciter(173, "Mishari Rashid al-`Afasy", "Streaming"),
        new Reciter(168, "Muhammad Siddiq al-Minshawi", "Kids repeat"),
    };

    public static Reciter Default => All[0];

    public static Reciter Find(int id) => All.FirstOrDefault(r => r.Id == id) ?? Default;

    /// <summary>"Yasser ad-Dussary (Murattal)" or just the name.</summary>
    public string Display => string.IsNullOrEmpty(Style) ? Name : $"{Name} ({Style})";
}

/// <summary>Translation and transliteration resource ids used by the Quran.com verses API.</summary>
public static class QuranIds
{
    public const int SaheehInternational = 20;
    public const int AbdelHaleem = 85;
    public const int Kemenag = 33;
    public const int KingFahadComplex = 134;
    public const int Transliteration = 57;
}

/// <summary>A chapter of the Quran as bundled in chapters.json.</summary>
public sealed record Surah(int Id, string NameSimple, string NameArabic, string NameEn, string NameId, int VersesCount, string RevelationPlace)
{
    public string Revelation => RevelationPlace == "madinah" ? "Madaniyah" : "Makkiyah";

    public string Meaning(bool indonesian) => indonesian ? NameId : NameEn;
}

/// <summary>The 114 chapters, read from the embedded chapters.json resource (generated once from the Quran.com chapters API).</summary>
public static class QuranChapters
{
    private const string ResourceName = "Perch.Pages.Quran.chapters.json";
    private static readonly Lazy<IReadOnlyList<Surah>> Cache = new(Load);

    public static IReadOnlyList<Surah> All => Cache.Value;

    public static Surah Get(int id) => All[Math.Clamp(id, 1, All.Count) - 1];

    public static IReadOnlyList<Surah> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Surah>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new Surah(
                Id: el.GetProperty("id").GetInt32(),
                NameSimple: el.GetProperty("name_simple").GetString() ?? "",
                NameArabic: el.GetProperty("name_arabic").GetString() ?? "",
                NameEn: el.GetProperty("name_en").GetString() ?? "",
                NameId: el.GetProperty("name_id").GetString() ?? "",
                VersesCount: el.GetProperty("verses_count").GetInt32(),
                RevelationPlace: el.GetProperty("revelation_place").GetString() ?? ""));
        }
        return list;
    }

    private static IReadOnlyList<Surah> Load()
    {
        using var stream = typeof(QuranChapters).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"missing resource {ResourceName}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

/// <summary>One word (or the ayah-end marker) of an ayah in Uthmani script.</summary>
public sealed record WordText(int Position, string Text, bool IsEnd);

/// <summary>An ayah's text: Arabic, its words (empty for ayah-level fallback text) and translations by resource id.</summary>
public sealed record AyahText(int Number, string Arabic, IReadOnlyList<WordText> Words, IReadOnlyDictionary<int, string> Translations);

/// <summary>A word's time span inside an ayah's audio, from the recitation segments.</summary>
public sealed record WordSegment(int Position, long FromMs, long ToMs);

/// <summary>An ayah's time span inside the surah audio, with its word segments sorted by start.</summary>
public sealed record AyahTiming(int Number, long FromMs, long ToMs, IReadOnlyList<WordSegment> Words);

/// <summary>The audio file of one surah by one reciter and its ayah timings sorted by start.</summary>
public sealed record Recitation(string AudioUrl, IReadOnlyList<AyahTiming> Ayahs);

/// <summary>Ayah texts for a surah. WordLevel is false when only the ayah-level fallback could be loaded.</summary>
public sealed record VerseSet(IReadOnlyList<AyahText> Ayahs, bool WordLevel);

/// <summary>Everything needed to show and play one surah.</summary>
public sealed record QuranTrack(Surah Surah, IReadOnlyList<AyahText> Texts, bool WordLevel, Recitation Audio, Reciter Reciter);
