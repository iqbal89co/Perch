using System.IO;
using System.Text.Json;
using Perch.Core;
using Perch.Pages.Quran;

namespace Perch.Tests;

/// <summary>
/// Quran tab logic: parsing of saved Quran.com responses, HTML stripping, ayah and word sync, repeat and
/// continue rules, search, the chapter and reciter lists, settings, and the disk cache. No network access.
/// </summary>
public class QuranTests : IDisposable
{
    // Surah 1 by reciter 97, as returned by /chapter_recitations/97/1?segments=true (trimmed to nothing).
    private const string RecitationSurah1 = """
{"audio_file":{"id":18372,"chapter_id":1,"file_size":776665.0,"format":"mp3","audio_url":"https://download.quranicaudio.com/quran/yasser_ad-dussary//001.mp3","timestamps":[{"verse_key":"1:1","timestamp_from":280,"timestamp_to":3080,"duration":2800,"segments":[[1,180,780],[2,800,1430],[3,1430,2330],[4,2330,3080]]},{"verse_key":"1:2","timestamp_from":3300,"timestamp_to":9010,"duration":5710,"segments":[[1,3300,3830],[2,3830,4620],[3,4620,5150],[4,5210,9010]]},{"verse_key":"1:3","timestamp_from":9610,"timestamp_to":15680,"duration":6070,"segments":[[1,9910,10980],[2,10980,15680]]},{"verse_key":"1:4","timestamp_from":16080,"timestamp_to":22050,"duration":5970,"segments":[[1,16280,17000],[2,17000,17520],[3,17520,22050]]},{"verse_key":"1:5","timestamp_from":22230,"timestamp_to":28920,"duration":6690,"segments":[[1,22430,23230],[2,23290,23900],[3,23900,24790],[4,24850,28920]]},{"verse_key":"1:6","timestamp_from":29340,"timestamp_to":32090,"duration":2750,"segments":[[1,29540,30190],[2,30190,31020],[3,31020,32090]]},{"verse_key":"1:7","timestamp_from":32170,"timestamp_to":47970,"duration":15800,"segments":[[1,32170,32910],[2,32910,33650],[3,33650,34490],[4,34490,36430],[5,36430,36940],[6,36940,37820],[7,37820,38870],[8,38870,39290],[9,39290,47970]]}]}}
""";

    // The first three verses of surah 36 from /verses/by_chapter/36 (words and five translations).
    private const string VersesSurah36 = """
{"verses":[{"id":3706,"verse_number":1,"verse_key":"36:1","hizb_number":44,"rub_el_hizb_number":176,"ruku_number":383,"manzil_number":5,"sajdah_number":null,"text_uthmani":" يسٓ","page_number":440,"juz_number":22,"words":[{"id":887,"position":1,"audio_url":"wbw/036_001_001.mp3","char_type_name":"word","text_uthmani":"يسٓ","page_number":440,"line_number":6,"text":"يسٓ","translation":{"text":"Ya Seen","language_name":"english"},"transliteration":{"text":"ya-seen","language_name":"english"}},{"id":888,"position":2,"audio_url":null,"char_type_name":"end","text_uthmani":"١","page_number":440,"line_number":6,"text":"١","translation":{"text":"(1)","language_name":"english"},"transliteration":{"text":null,"language_name":"english"}}],"translations":[{"id":99066,"resource_id":20,"text":"Yā, Seen.<sup foot_note=197154>1</sup>"},{"id":405409,"resource_id":85,"text":"Ya Sin"},{"id":330217,"resource_id":57,"text":"Ya-seen"},{"id":920135,"resource_id":134,"text":"Yā sīn <sup foot_note=211032>1</sup>"},{"id":177339,"resource_id":33,"text":"Yā Sīn"}]},{"id":3707,"verse_number":2,"verse_key":"36:2","hizb_number":44,"rub_el_hizb_number":176,"ruku_number":383,"manzil_number":5,"sajdah_number":null,"text_uthmani":"وَٱلْقُرْءَانِ ٱلْحَكِيمِ","page_number":440,"juz_number":22,"words":[{"id":1974,"position":1,"audio_url":"wbw/036_002_001.mp3","char_type_name":"word","text_uthmani":"وَٱلْقُرْءَانِ","page_number":440,"line_number":6,"text":"وَٱلْقُرْءَانِ","translation":{"text":"By the Quran","language_name":"english"},"transliteration":{"text":"wal-qur'āni","language_name":"english"}},{"id":1975,"position":2,"audio_url":"wbw/036_002_002.mp3","char_type_name":"word","text_uthmani":"ٱلْحَكِيمِ","page_number":440,"line_number":6,"text":"ٱلْحَكِيمِ","translation":{"text":"the Wise","language_name":"english"},"transliteration":{"text":"l-ḥakīmi","language_name":"english"}},{"id":1976,"position":3,"audio_url":null,"char_type_name":"end","text_uthmani":"٢","page_number":440,"line_number":6,"text":"٢","translation":{"text":"(2)","language_name":"english"},"transliteration":{"text":null,"language_name":"english"}}],"translations":[{"id":99078,"resource_id":20,"text":"By the wise<sup foot_note=197155>1</sup> Qur’ān,"},{"id":405410,"resource_id":85,"text":"By the wise Quran,"},{"id":333071,"resource_id":57,"text":"Walqur-ani alhakeem"},{"id":920136,"resource_id":134,"text":"Demi Al-Qur`ān yang penuh hikmah,"},{"id":178096,"resource_id":33,"text":"Demi Alquran yang penuh hikmah,"}]},{"id":3708,"verse_number":3,"verse_key":"36:3","hizb_number":44,"rub_el_hizb_number":176,"ruku_number":383,"manzil_number":5,"sajdah_number":null,"text_uthmani":"إِنَّكَ لَمِنَ ٱلْمُرْسَلِينَ","page_number":440,"juz_number":22,"words":[{"id":3169,"position":1,"audio_url":"wbw/036_003_001.mp3","char_type_name":"word","text_uthmani":"إِنَّكَ","page_number":440,"line_number":6,"text":"إِنَّكَ","translation":{"text":"Indeed, you","language_name":"english"},"transliteration":{"text":"innaka","language_name":"english"}},{"id":3170,"position":2,"audio_url":"wbw/036_003_002.mp3","char_type_name":"word","text_uthmani":"لَمِنَ","page_number":440,"line_number":6,"text":"لَمِنَ","translation":{"text":"(are) among","language_name":"english"},"transliteration":{"text":"lamina","language_name":"english"}},{"id":3171,"position":3,"audio_url":"wbw/036_003_003.mp3","char_type_name":"word","text_uthmani":"ٱلْمُرْسَلِينَ","page_number":440,"line_number":6,"text":"ٱلْمُرْسَلِينَ","translation":{"text":"the Messengers","language_name":"english"},"transliteration":{"text":"l-mur'salīna","language_name":"english"}},{"id":3172,"position":4,"audio_url":null,"char_type_name":"end","text_uthmani":"٣","page_number":440,"line_number":6,"text":"٣","translation":{"text":"(3)","language_name":"english"},"transliteration":{"text":null,"language_name":"english"}}],"translations":[{"id":99090,"resource_id":20,"text":"Indeed you, [O Muḥammad], are from among the messengers,"},{"id":405411,"resource_id":85,"text":"you [Muhammad] are truly one of the mes-sengers sent"},{"id":330194,"resource_id":57,"text":"Innaka lamina almursaleen"},{"id":920137,"resource_id":134,"text":"Sesungguhnya kamu salah seorang dari rasul-rasul,"},{"id":178104,"resource_id":33,"text":"sungguh, engkau (Muhammad) adalah salah seorang dari rasul-rasul,"}]}]}
""";

    // alquran.cloud fallback for surah 1 (first two ayahs of each edition).
    private const string AlQuranSurah1 = """
{"code":200,"status":"OK","data":[{"number":1,"name":"سُورَةُ ٱلْفَاتِحَةِ","englishName":"Al-Faatiha","englishNameTranslation":"The Opening","revelationType":"Meccan","numberOfAyahs":7,"ayahs":[{"number":1,"text":"﻿بِسْمِ ٱللَّهِ ٱلرَّحْمَٰنِ ٱلرَّحِيمِ","numberInSurah":1,"juz":1,"manzil":1,"page":1,"ruku":1,"hizbQuarter":1,"sajda":false},{"number":2,"text":"ٱلْحَمْدُ لِلَّهِ رَبِّ ٱلْعَٰلَمِينَ","numberInSurah":2,"juz":1,"manzil":1,"page":1,"ruku":1,"hizbQuarter":1,"sajda":false}],"edition":{"identifier":"quran-uthmani","language":"ar","name":"القرآن الكريم برسم العثماني (uthmani)","englishName":"Uthmani","format":"text","type":"quran","direction":"rtl"}},{"number":1,"name":"سُورَةُ ٱلْفَاتِحَةِ","englishName":"Al-Faatiha","englishNameTranslation":"The Opening","revelationType":"Meccan","numberOfAyahs":7,"ayahs":[{"number":1,"text":"In the name of Allah, the Entirely Merciful, the Especially Merciful.","numberInSurah":1,"juz":1,"manzil":1,"page":1,"ruku":1,"hizbQuarter":1,"sajda":false},{"number":2,"text":"[All] praise is [due] to Allah, Lord of the worlds -","numberInSurah":2,"juz":1,"manzil":1,"page":1,"ruku":1,"hizbQuarter":1,"sajda":false}],"edition":{"identifier":"en.sahih","language":"en","name":"Saheeh International","englishName":"Saheeh International","format":"text","type":"translation","direction":"ltr"}},{"number":1,"name":"سُورَةُ ٱلْفَاتِحَةِ","englishName":"Al-Faatiha","englishNameTranslation":"The Opening","revelationType":"Meccan","numberOfAyahs":7,"ayahs":[{"number":1,"text":"Dengan menyebut nama Allah Yang Maha Pemurah lagi Maha Penyayang.","numberInSurah":1,"juz":1,"manzil":1,"page":1,"ruku":1,"hizbQuarter":1,"sajda":false},{"number":2,"text":"Segala puji bagi Allah, Tuhan semesta alam.","numberInSurah":2,"juz":1,"manzil":1,"page":1,"ruku":1,"hizbQuarter":1,"sajda":false}],"edition":{"identifier":"id.indonesian","language":"id","name":"Bahasa Indonesia","englishName":"Unknown","format":"text","type":"translation","direction":"ltr"}}]}
""";

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-quran-tests-" + Guid.NewGuid().ToString("N"));

    public QuranTests() => AppPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static IReadOnlyList<AyahTiming> Surah1Timings() => QuranParser.ParseRecitation(RecitationSurah1).Ayahs;

    // ---- recitation parsing -----------------------------------------------------------------------

    [Fact]
    public void Recitation_reads_audio_url_and_ayah_timings()
    {
        var rec = QuranParser.ParseRecitation(RecitationSurah1);

        Assert.Equal("https://download.quranicaudio.com/quran/yasser_ad-dussary//001.mp3", rec.AudioUrl);
        Assert.Equal(7, rec.Ayahs.Count);
        Assert.Equal(new AyahTiming(1, 280, 3080, rec.Ayahs[0].Words), rec.Ayahs[0]);
        Assert.Equal(4, rec.Ayahs[0].Words.Count);
        Assert.Equal(new WordSegment(4, 2330, 3080), rec.Ayahs[0].Words[3]);
        Assert.Equal(7, rec.Ayahs[6].Number);
        Assert.Equal(9, rec.Ayahs[6].Words.Count);
    }

    [Fact]
    public void Recitation_skips_malformed_segments_and_bad_ayahs()
    {
        const string json = """
{"audio_file":{"audio_url":"https://example.test/a.mp3","timestamps":[
 {"verse_key":"2:1","timestamp_from":100,"timestamp_to":900,"segments":[[1,100,300],[2,300],["3",300,400],[3,500,450],[0,1,2],[4,600,900],[5,700.0,800.4]]},
 {"verse_key":"bad","timestamp_from":1,"timestamp_to":2},
 {"verse_key":"2:2","timestamp_from":1000,"timestamp_to":900},
 {"verse_key":"2:3","timestamp_from":1000,"timestamp_to":1200,"segments":"nope"}
]}}
""";
        var rec = QuranParser.ParseRecitation(json);

        Assert.Equal(2, rec.Ayahs.Count);
        var first = rec.Ayahs[0];
        Assert.Equal(1, first.Number);
        Assert.Equal(new[] { 1, 4, 5 }, first.Words.Select(w => w.Position).ToArray());
        Assert.Equal(800, first.Words[2].ToMs);
        Assert.Empty(rec.Ayahs[1].Words);
    }

    [Fact]
    public void Recitation_without_audio_url_is_rejected()
    {
        Assert.Throws<FormatException>(() => QuranParser.ParseRecitation("{\"audio_file\":{\"timestamps\":[]}}"));
        Assert.Throws<FormatException>(() => QuranParser.ParseRecitation("{}"));
    }

    // ---- verse parsing ----------------------------------------------------------------------------

    [Fact]
    public void Verses_read_words_end_markers_and_translations()
    {
        var ayahs = QuranParser.ParseVerses(VersesSurah36);

        Assert.Equal(new[] { 1, 2, 3 }, ayahs.Select(a => a.Number).ToArray());
        var first = ayahs[0];
        Assert.Equal("يسٓ", first.Arabic);
        Assert.Equal(2, first.Words.Count);
        Assert.Equal(new WordText(1, "يسٓ", false), first.Words[0]);
        Assert.True(first.Words[1].IsEnd);
        Assert.Equal("Yā, Seen.", first.Translations[QuranIds.SaheehInternational]);
        Assert.Equal("Ya Sin", first.Translations[QuranIds.AbdelHaleem]);
        Assert.Equal("Ya-seen", first.Translations[QuranIds.Transliteration]);
        Assert.Equal("Yā sīn", first.Translations[QuranIds.KingFahadComplex]);
    }

    [Fact]
    public void AlQuranCloud_fallback_gives_ayah_level_text_and_two_translations()
    {
        var ayahs = QuranParser.ParseAlQuranCloud(AlQuranSurah1);

        Assert.Equal(new[] { 1, 2 }, ayahs.Select(a => a.Number).ToArray());
        Assert.All(ayahs, a => Assert.Empty(a.Words));
        Assert.False(string.IsNullOrWhiteSpace(ayahs[0].Arabic));
        Assert.True(ayahs[0].Translations.ContainsKey(QuranIds.SaheehInternational));
        Assert.True(ayahs[0].Translations.ContainsKey(QuranIds.Kemenag));
    }

    [Theory]
    [InlineData("Yā, Seen.<sup foot_note=197154>1</sup>", "Yā, Seen.")]
    [InlineData("<p>Hello&nbsp;<b>world</b>&amp;co</p>", "Hello world&co")]
    [InlineData("Say &quot;hi&quot;\n\n  now", "Say \"hi\" now")]
    [InlineData("a<sup>x</sup>b<sup foot_note=1>y</sup>", "ab")]
    public void StripHtml_drops_footnotes_tags_and_entities(string html, string expected)
    {
        Assert.Equal(expected, QuranParser.StripHtml(html));
    }

    [Fact]
    public void StripHtml_handles_null_and_empty()
    {
        Assert.Equal("", QuranParser.StripHtml(null));
        Assert.Equal("", QuranParser.StripHtml(""));
    }

    [Theory]
    [InlineData("36:2", 2)]
    [InlineData("1:7", 7)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("nope", 0)]
    public void VerseNumber_reads_the_ayah_after_the_colon(string? key, int expected)
    {
        Assert.Equal(expected, QuranParser.VerseNumber(key));
    }

    // ---- sync -------------------------------------------------------------------------------------

    [Fact]
    public void Ayah_sync_before_first_ayah_is_ayah_one_and_gaps_keep_the_previous_ayah()
    {
        var t = Surah1Timings();

        Assert.Equal(0, QuranSync.AyahIndexAt(t, 0));       // before the first ayah
        Assert.Equal(0, QuranSync.AyahIndexAt(t, 280));
        Assert.Equal(0, QuranSync.AyahIndexAt(t, 3100));    // gap between 1:1 and 1:2
        Assert.Equal(1, QuranSync.AyahIndexAt(t, 3300));
        Assert.Equal(4, QuranSync.AyahIndexAt(t, 22231));   // 1:5 starts at 22230
        Assert.Equal(6, QuranSync.AyahIndexAt(t, 999_999)); // after the last ayah
    }

    [Fact]
    public void Ayah_sync_with_no_timings_has_no_index()
    {
        Assert.Equal(-1, QuranSync.AyahIndexAt(Array.Empty<AyahTiming>(), 5));
    }

    [Fact]
    public void Word_sync_has_no_word_before_the_first_segment_and_keeps_the_previous_word_in_gaps()
    {
        var ayah = Surah1Timings()[0]; // segments 1:180-780, 2:800-1430, 3:1430-2330, 4:2330-3080

        Assert.Null(QuranSync.WordAt(ayah, 100));
        Assert.Equal(1, QuranSync.WordAt(ayah, 500));
        Assert.Equal(1, QuranSync.WordAt(ayah, 785));       // gap between word 1 and word 2
        Assert.Equal(2, QuranSync.WordAt(ayah, 1000));
        Assert.Equal(4, QuranSync.WordAt(ayah, 2500));
        Assert.Equal(4, QuranSync.WordAt(ayah, 3500));      // past the last segment
    }

    [Fact]
    public void Word_sync_uses_the_segments_of_the_malformed_recitation_that_were_kept()
    {
        var ayah = QuranParser.ParseRecitation("""
{"audio_file":{"audio_url":"u","timestamps":[{"verse_key":"2:1","timestamp_from":100,"timestamp_to":900,"segments":[[1,100,300],[2,300],[4,600,900]]}]}}
""").Ayahs[0];

        Assert.Equal(new[] { 1, 4 }, ayah.Words.Select(w => w.Position).ToArray());
        Assert.Equal(1, QuranSync.WordAt(ayah, 350));
        Assert.Equal(4, QuranSync.WordAt(ayah, 650));
    }

    // ---- rules ------------------------------------------------------------------------------------

    [Fact]
    public void Ayah_repeat_seeks_back_to_the_start_once_the_end_is_passed()
    {
        var ayah = Surah1Timings()[0]; // 280-3080

        Assert.Null(QuranRules.AyahRepeatTarget(RepeatMode.Ayah, ayah, 3079));
        Assert.Equal(280, QuranRules.AyahRepeatTarget(RepeatMode.Ayah, ayah, 3080));
        Assert.Equal(280, QuranRules.AyahRepeatTarget(RepeatMode.Ayah, ayah, 3500));
        Assert.Null(QuranRules.AyahRepeatTarget(RepeatMode.Off, ayah, 3500));
        Assert.Null(QuranRules.AyahRepeatTarget(RepeatMode.Surah, ayah, 3500));
        Assert.Null(QuranRules.AyahRepeatTarget(RepeatMode.Ayah, null, 3500));
    }

    [Theory]
    [InlineData(RepeatMode.Off, true, true, EndAction.NextSurah)]
    [InlineData(RepeatMode.Off, false, true, EndAction.Stop)]
    [InlineData(RepeatMode.Off, true, false, EndAction.Stop)]
    [InlineData(RepeatMode.Ayah, true, true, EndAction.NextSurah)]
    [InlineData(RepeatMode.Surah, false, true, EndAction.RepeatSurah)]
    [InlineData(RepeatMode.Surah, true, false, EndAction.RepeatSurah)]
    public void Surah_end_repeats_continues_or_stops(RepeatMode mode, bool continueNext, bool hasNext, EndAction expected)
    {
        Assert.Equal(expected, QuranRules.OnSurahEnded(mode, continueNext, hasNext));
    }

    [Fact]
    public void Repeat_cycles_off_ayah_surah_off()
    {
        Assert.Equal(RepeatMode.Ayah, QuranRules.NextRepeat(RepeatMode.Off));
        Assert.Equal(RepeatMode.Surah, QuranRules.NextRepeat(RepeatMode.Ayah));
        Assert.Equal(RepeatMode.Off, QuranRules.NextRepeat(RepeatMode.Surah));
    }

    [Fact]
    public void Surah_ids_are_limited_to_one_to_114()
    {
        Assert.False(QuranRules.HasSurah(0));
        Assert.True(QuranRules.HasSurah(1));
        Assert.True(QuranRules.HasSurah(114));
        Assert.False(QuranRules.HasSurah(115));
    }

    // ---- search -----------------------------------------------------------------------------------

    [Fact]
    public void Search_matches_number_latin_name_meanings_and_arabic()
    {
        var all = QuranChapters.All;
        var yasin = QuranChapters.Get(36);

        Assert.Equal(114, all.Count(s => QuranSearch.Matches(s, "")));
        Assert.Equal(new[] { 36 }, all.Where(s => QuranSearch.Matches(s, "36")).Select(s => s.Id).ToArray());
        Assert.Contains(yasin, all.Where(s => QuranSearch.Matches(s, "ya sin")));
        Assert.Contains(yasin, all.Where(s => QuranSearch.Matches(s, "YASIN")));
        Assert.Contains(QuranChapters.Get(1), all.Where(s => QuranSearch.Matches(s, "the opener")));
        Assert.Contains(QuranChapters.Get(1), all.Where(s => QuranSearch.Matches(s, "Pembukaan")));
        Assert.Contains(yasin, all.Where(s => QuranSearch.Matches(s, "يس")));
        Assert.DoesNotContain(all, s => QuranSearch.Matches(s, "zzqx"));
    }

    // ---- chapters and reciters -------------------------------------------------------------------

    [Fact]
    public void Chapters_are_loaded_from_the_embedded_resource()
    {
        var all = QuranChapters.All;

        Assert.Equal(114, all.Count);
        var yasin = QuranChapters.Get(36);
        Assert.Equal("Ya-Sin", yasin.NameSimple);
        Assert.Equal("يس", yasin.NameArabic);
        Assert.Equal(83, yasin.VersesCount);
        Assert.Equal("Makkiyah", yasin.Revelation);
        Assert.Equal("Madaniyah", QuranChapters.Get(2).Revelation);
        Assert.Equal("The Opener", QuranChapters.Get(1).NameEn);
        Assert.Equal("Pembukaan", QuranChapters.Get(1).Meaning(indonesian: true));
    }

    [Fact]
    public void Reciters_have_the_fourteen_chapter_reciters_with_yasser_as_default()
    {
        Assert.Equal(14, Reciter.All.Count);
        Assert.Equal(97, Reciter.Default.Id);
        Assert.Equal(97, Reciter.Find(999).Id);
        Assert.Equal("Mahmoud Khalil Al-Husary (Muallim)", Reciter.Find(12).Display);
        Assert.Equal("Yasser ad-Dussary (Murattal)", Reciter.Find(97).Display);
    }

    // ---- settings ---------------------------------------------------------------------------------

    [Fact]
    public void Arabic_size_is_clamped_and_the_translator_ids_are_restricted()
    {
        var s = new QuranSettings { ArabicSize = 10 };
        Assert.Equal(20, s.ArabicSize);
        s.ArabicSize = 99;
        Assert.Equal(36, s.ArabicSize);

        s.EnglishTranslator = 12345;
        Assert.Equal(QuranIds.SaheehInternational, s.EnglishId);
        s.EnglishTranslator = QuranIds.AbdelHaleem;
        Assert.Equal(QuranIds.AbdelHaleem, s.EnglishId);
        s.IndonesianTranslator = QuranIds.KingFahadComplex;
        Assert.Equal(QuranIds.KingFahadComplex, s.IndonesianId);
    }

    [Fact]
    public void Settings_round_trip_through_the_quran_store_with_snake_case_names()
    {
        var saved = new QuranSettings
        {
            ReciterId = 7,
            LastSurah = 36,
            LastPositionMs = 20_000,
            Repeat = RepeatMode.Surah,
            Translation = TranslationMode.Both,
            Transliteration = true,
            ContinueNextSurah = false,
        };
        JsonStore.Save("quran", saved);

        var text = File.ReadAllText(JsonStore.PathFor("quran"));
        Assert.Contains("\"last_position_ms\": 20000", text);
        Assert.Contains("\"surah\"", text);
        Assert.DoesNotContain("english_id", text);

        var loaded = JsonStore.Load<QuranSettings>("quran");
        Assert.Equal(7, loaded.ReciterId);
        Assert.Equal(36, loaded.LastSurah);
        Assert.Equal(20_000, loaded.LastPositionMs);
        Assert.Equal(RepeatMode.Surah, loaded.Repeat);
        Assert.Equal(TranslationMode.Both, loaded.Translation);
        Assert.True(loaded.Transliteration);
        Assert.False(loaded.ContinueNextSurah);
        Assert.Equal(26, loaded.ArabicSize);
    }

    // ---- cache ------------------------------------------------------------------------------------

    [Fact]
    public void Cache_writes_and_reads_back_in_the_quran_folder()
    {
        QuranApi.WriteCache("verses-36.json", VersesSurah36);

        Assert.True(File.Exists(Path.Combine(AppPaths.Cache, "quran", "verses-36.json")));
        Assert.Equal(VersesSurah36, QuranApi.ReadCache("verses-36.json"));
        Assert.Null(QuranApi.ReadCache("missing.json"));
    }

    [Fact]
    public async Task Verses_and_timings_are_served_from_the_cache_without_network()
    {
        QuranApi.WriteCache("verses-36.json", VersesSurah36);
        QuranApi.WriteCache("audio-97-1.json", RecitationSurah1);

        var verses = await QuranApi.GetVersesAsync(36, CancellationToken.None);
        var audio = await QuranApi.GetTimingsAsync(97, 1, CancellationToken.None);

        Assert.True(verses.WordLevel);
        Assert.Equal(3, verses.Ayahs.Count);
        Assert.Equal(7, audio.Ayahs.Count);
    }
}
