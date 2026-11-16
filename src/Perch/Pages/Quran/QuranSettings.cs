using System.Text.Json.Serialization;

namespace Perch.Pages.Quran;

/// <summary>Persisted as data/quran.json. The last surah and position reopen paused, never auto-playing.</summary>
public sealed class QuranSettings
{
    private int _arabicSize = 26;

    public int ReciterId { get; set; } = Reciter.Default.Id;

    /// <summary>0 when nothing was opened yet.</summary>
    public int LastSurah { get; set; }

    public long LastPositionMs { get; set; }

    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public TranslationMode Translation { get; set; } = TranslationMode.English;

    public int EnglishTranslator { get; set; } = QuranIds.SaheehInternational;

    public int IndonesianTranslator { get; set; } = QuranIds.Kemenag;

    public bool Transliteration { get; set; }

    public int ArabicSize
    {
        get => _arabicSize;
        set => _arabicSize = Math.Clamp(value, 20, 36);
    }

    public bool ContinueNextSurah { get; set; } = true;

    /// <summary>The English translator id that is actually used (only the two supported ids are honoured).</summary>
    [JsonIgnore]
    public int EnglishId => EnglishTranslator == QuranIds.AbdelHaleem ? QuranIds.AbdelHaleem : QuranIds.SaheehInternational;

    [JsonIgnore]
    public int IndonesianId =>IndonesianTranslator == QuranIds.KingFahadComplex ? QuranIds.KingFahadComplex : QuranIds.Kemenag;
}
