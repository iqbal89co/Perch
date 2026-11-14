using System.Text;
using System.Text.Json;
using Perch.Core;
using Perch.Pages.Prayer;

namespace Perch.Tests;

/// <summary>
/// Reference times were fetched once from the public Aladhan API (api.aladhan.com/v1/timings) at development time:
/// Jakarta 14-11-2026 (method 20, Kemenag), London 15-01-2026 and 15-07-2026 (method 3, MWL), Mecca 15-01-2026
/// (method 4, Umm al-Qura) and New York 15-07-2026 (method 2, ISNA), all at elevation 0.
/// Aladhan applies no precautionary minutes for Kemenag, so the reference comparisons switch the method's own
/// adjustments off. The +2 minute ihtiyat is then tested on its own.
/// </summary>
public class PrayerCalculatorTests
{
    private const double Tolerance = 3.0; // minutes

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    private static void AssertNear(string label, string expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        var want = TimeSpan.Parse(expected + ":00");
        var diff = Math.Abs((actual!.Value.TimeOfDay - want).TotalMinutes);
        Assert.True(diff <= Tolerance, $"{label}: expected about {expected}, got {actual.Value:HH:mm} ({diff:0.0} min off)");
    }

    private static void AssertPrayers(PrayerDay day, string fajr, string sunrise, string dhuhr, string asr, string maghrib, string isha)
    {
        AssertNear("Fajr", fajr, day.Fajr);
        AssertNear("Sunrise", sunrise, day.Sunrise);
        AssertNear("Dhuhr", dhuhr, day.Dhuhr);
        AssertNear("Asr", asr, day.Asr);
        AssertNear("Maghrib", maghrib, day.Maghrib);
        AssertNear("Isha", isha, day.Isha);
    }

    [Fact]
    public void Jakarta_kemenag_matches_reference_without_precaution()
    {
        var day = PrayerCalculator.Calculate(new DateOnly(2026, 11, 14), -6.2088, 106.8456, 0,
            Zone("Asia/Jakarta"), PrayerMethods.Kemenag, applyMethodAdjustments: false);

        AssertPrayers(day, "04:03", "05:25", "11:37", "14:59", "17:49", "19:02");
        Assert.Equal(TimeSpan.FromHours(7), day.Fajr!.Value.Offset);
    }

    [Fact]
    public void Kemenag_precaution_adds_two_minutes_to_each_prayer_but_not_sunrise()
    {
        var zone = Zone("Asia/Jakarta");
        var date = new DateOnly(2026, 11, 14);
        var plain = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag, applyMethodAdjustments: false);
        var cautious = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag);

        foreach (var kind in new[] { PrayerKind.Fajr, PrayerKind.Dhuhr, PrayerKind.Asr, PrayerKind.Maghrib, PrayerKind.Isha })
        {
            var delta = (cautious.At(kind)!.Value - plain.At(kind)!.Value).TotalMinutes;
            Assert.InRange(delta, 1.99, 2.01);
        }
        Assert.Equal(plain.Sunrise, cautious.Sunrise);
    }

    [Fact]
    public void Extra_minutes_stack_on_top_of_method_adjustments()
    {
        var zone = Zone("Asia/Jakarta");
        var date = new DateOnly(2026, 11, 14);
        var extra = new Dictionary<PrayerKind, int> { [PrayerKind.Fajr] = 3 };
        var baseline = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag);
        var tuned = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag, extraMinutes: extra);
        Assert.InRange((tuned.Fajr!.Value - baseline.Fajr!.Value).TotalMinutes, 2.99, 3.01);
        Assert.Equal(baseline.Dhuhr, tuned.Dhuhr);
    }

    [Fact]
    public void London_winter_matches_reference()
    {
        var day = PrayerCalculator.Calculate(new DateOnly(2026, 1, 15), 51.5074, -0.1278, 0,
            Zone("Europe/London"), PrayerMethods.Mwl);

        AssertPrayers(day, "05:59", "08:00", "12:10", "14:00", "16:21", "18:15");
        Assert.Equal(TimeSpan.Zero, day.Fajr!.Value.Offset);
    }

    [Fact]
    public void London_summer_uses_daylight_time_and_matches_reference()
    {
        var day = PrayerCalculator.Calculate(new DateOnly(2026, 7, 15), 51.5074, -0.1278, 0,
            Zone("Europe/London"), PrayerMethods.Mwl);

        AssertPrayers(day, "02:40", "05:01", "13:07", "17:25", "21:12", "23:25");
        Assert.Equal(TimeSpan.FromHours(1), day.Dhuhr!.Value.Offset);
    }

    [Fact]
    public void Mecca_umm_al_qura_matches_reference_with_isha_ninety_minutes_after_maghrib()
    {
        var day = PrayerCalculator.Calculate(new DateOnly(2026, 1, 15), 21.4225, 39.8262, 0,
            Zone("Asia/Riyadh"), PrayerMethods.UmmAlQura);

        AssertPrayers(day, "05:41", "07:01", "12:30", "15:37", "17:59", "19:29");
        Assert.InRange((day.Isha!.Value - day.Maghrib!.Value).TotalMinutes, 89.9, 90.1);
    }

    [Fact]
    public void New_York_summer_isna_matches_reference()
    {
        var day = PrayerCalculator.Calculate(new DateOnly(2026, 7, 15), 40.7128, -74.0060, 0,
            Zone("America/New_York"), PrayerMethods.Isna);

        AssertPrayers(day, "04:03", "05:38", "13:02", "17:01", "20:26", "22:01");
    }

    [Fact]
    public void Hanafi_asr_is_later_than_standard()
    {
        var zone = Zone("Asia/Jakarta");
        var date = new DateOnly(2026, 11, 14);
        var standard = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag, AsrSchool.Standard);
        var hanafi = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag, AsrSchool.Hanafi);
        Assert.True(hanafi.Asr > standard.Asr);
        Assert.Equal(standard.Maghrib, hanafi.Maghrib);
    }

    [Fact]
    public void Oslo_in_june_never_returns_nan_with_the_angle_rule()
    {
        var zone = Zone("Europe/Oslo");
        var start = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 365; i++)
        {
            var date = start.AddDays(i);
            var day = PrayerCalculator.Calculate(date, 59.9139, 10.7522, 0, zone, PrayerMethods.Mwl);
            Assert.NotNull(day.Fajr);
            Assert.NotNull(day.Isha);
            Assert.NotNull(day.Dhuhr);
        }

        var june = PrayerCalculator.Calculate(new DateOnly(2026, 6, 21), 59.9139, 10.7522, 0, zone, PrayerMethods.Mwl);
        Assert.True(june.Fajr < june.Sunrise);
        Assert.True(june.Sunrise < june.Dhuhr);
        Assert.True(june.Dhuhr < june.Asr);
        Assert.True(june.Asr < june.Maghrib);
        Assert.True(june.Maghrib < june.Isha);
    }

    [Fact]
    public void Oslo_in_june_without_a_rule_reports_missing_isha_as_null()
    {
        var day = PrayerCalculator.Calculate(new DateOnly(2026, 6, 21), 59.9139, 10.7522, 0,
            Zone("Europe/Oslo"), PrayerMethods.Mwl, rule: HighLatitudeRule.None);
        Assert.Null(day.Isha);
        Assert.NotNull(day.Dhuhr);
    }

    [Fact]
    public void Qibla_from_jakarta_and_london()
    {
        Assert.InRange(PrayerCalculator.QiblaBearing(-6.2088, 106.8456), 294, 296);
        Assert.InRange(PrayerCalculator.QiblaBearing(51.5074, -0.1278), 118, 120);
    }

    [Fact]
    public void Next_rolls_over_to_tomorrows_fajr_after_isha()
    {
        var zone = Zone("Asia/Jakarta");
        var date = new DateOnly(2026, 11, 14);
        var today = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag);
        var tomorrow = PrayerCalculator.Calculate(date.AddDays(1), -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag);
        var offset = TimeSpan.FromHours(7);

        var lateNight = new DateTimeOffset(2026, 11, 14, 21, 30, 0, offset);
        var next = PrayerCalculator.Next(today, lateNight, tomorrow);
        Assert.NotNull(next);
        Assert.Equal("Fajr", next!.Value.Name);
        Assert.Equal(tomorrow.Fajr, next.Value.Time);

        // Without tomorrow's day there is nothing to offer after Isha.
        Assert.Null(PrayerCalculator.Next(today, lateNight));
    }

    [Fact]
    public void Next_picks_the_upcoming_prayer_and_skips_sunrise()
    {
        var zone = Zone("Asia/Jakarta");
        var date = new DateOnly(2026, 11, 14);
        var today = PrayerCalculator.Calculate(date, -6.2088, 106.8456, 0, zone, PrayerMethods.Kemenag);
        var offset = TimeSpan.FromHours(7);

        var beforeFajr = PrayerCalculator.Next(today, new DateTimeOffset(2026, 11, 14, 3, 0, 0, offset));
        Assert.Equal("Fajr", beforeFajr!.Value.Name);

        // Between sunrise (05:25) and Dhuhr: Dhuhr is next, not sunrise.
        var afterSunrise = PrayerCalculator.Next(today, new DateTimeOffset(2026, 11, 14, 6, 0, 0, offset));
        Assert.Equal("Dhuhr", afterSunrise!.Value.Name);

        var afternoon = PrayerCalculator.Next(today, new DateTimeOffset(2026, 11, 14, 16, 0, 0, offset));
        Assert.Equal("Maghrib", afternoon!.Value.Name);
    }

    [Fact]
    public void Methods_fall_back_to_kemenag_for_unknown_ids()
    {
        Assert.Same(PrayerMethods.Kemenag, PrayerMethods.Find("nonsense"));
        Assert.Same(PrayerMethods.Kemenag, PrayerMethods.Find(null));
        Assert.Same(PrayerMethods.Tehran, PrayerMethods.Find("TEHRAN"));
        Assert.Equal(10, PrayerMethods.All.Count);
    }
}

public class PrayerTextTests
{
    [Theory]
    [InlineData(-5, "now")]
    [InlineData(0, "now")]
    [InlineData(8 * 60 + 12, "in 8:12")]
    [InlineData(9 * 60 + 59, "in 9:59")]
    [InlineData(10 * 60, "in 10 min")]
    [InlineData(42 * 60, "in 42 min")]
    [InlineData(84 * 60, "in 1 h 24 m")]
    public void Countdown_text(int seconds, string expected)
    {
        Assert.Equal(expected, PrayerText.Countdown(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Hijri_text_is_english_transliteration_for_1448()
    {
        var text = PrayerText.Hijri(new DateOnly(2026, 11, 14));
        Assert.Matches(@"^\d{1,2} [A-Za-z' ]+ 1448$", text);
        Assert.Contains("Jumada", text);
    }

    [Fact]
    public void Date_caption_has_weekday_and_hijri()
    {
        var caption = PrayerText.DateCaption(new DateOnly(2026, 11, 14));
        Assert.StartsWith("Sat 14 Nov 2026", caption);
        Assert.Contains("1448", caption);
    }
}

public class PrayerPlacesTests
{
    [Fact]
    public void There_are_at_least_120_places_with_valid_methods_and_zones()
    {
        Assert.True(PrayerPlaces.All.Count >= 120, $"only {PrayerPlaces.All.Count} places");
        foreach (var place in PrayerPlaces.All)
        {
            Assert.Contains(PrayerMethods.All, m => m.Id == place.MethodId);
            Assert.InRange(place.Latitude, -90, 90);
            Assert.InRange(place.Longitude, -180, 180);
            // Must resolve to the named zone, not the device fallback.
            var zone = TimeZoneInfo.FindSystemTimeZoneById(place.TimeZoneId);
            Assert.Equal(place.TimeZoneId, zone.Id);
        }
    }

    [Fact]
    public void Every_indonesian_provincial_capital_is_present()
    {
        var names = new[]
        {
            "Banda Aceh", "Medan", "Padang", "Pekanbaru", "Tanjung Pinang", "Jambi", "Palembang", "Bengkulu",
            "Pangkal Pinang", "Bandar Lampung", "Jakarta", "Serang", "Bandung", "Semarang", "Yogyakarta", "Surabaya",
            "Pontianak", "Palangka Raya", "Banjarmasin", "Samarinda", "Tanjung Selor", "Denpasar", "Mataram", "Kupang",
            "Manado", "Gorontalo", "Palu", "Mamuju", "Makassar", "Kendari", "Ambon", "Sofifi", "Manokwari", "Sorong",
            "Nabire", "Jayapura", "Wamena", "Merauke",
        };
        foreach (var name in names)
        {
            Assert.Contains(PrayerPlaces.All, p => p.Name == name && p.Country == "Indonesia");
        }
    }

    [Fact]
    public void Search_ignores_case_and_accents_and_matches_country()
    {
        Assert.Contains(PrayerPlaces.Search("JAKARTA"), p => p.Name == "Jakarta");
        Assert.Contains(PrayerPlaces.Search("sao"), p => p.Name == "Sao Paulo");
        Assert.Contains(PrayerPlaces.Search("malaysia"), p => p.Name == "Kuala Lumpur");
        Assert.Equal(PrayerPlaces.All.Count, PrayerPlaces.Search("   ").Count);
        Assert.Empty(PrayerPlaces.Search("zzzz-not-a-place"));
    }

    [Fact]
    public void Nearest_finds_jakarta_for_a_point_in_the_city()
    {
        Assert.Equal("Jakarta", PrayerPlaces.Nearest(-6.21, 106.85).Name);
        Assert.Equal("Mecca", PrayerPlaces.Nearest(21.40, 39.82).Name);
    }

    [Fact]
    public void Unknown_zone_ids_fall_back_to_the_device_zone()
    {
        Assert.Equal(TimeZoneInfo.Local, PrayerPlaces.ResolveZone("Not/AZone"));
        Assert.Equal(TimeZoneInfo.Local, PrayerPlaces.ResolveZone(null));
        Assert.Equal("Asia/Jakarta", PrayerPlaces.ResolveZone("Asia/Jakarta").Id);
    }
}

public class PrayerSoundTests
{
    [Fact]
    public void Chime_is_a_valid_16_bit_mono_wav_of_about_1_6_seconds()
    {
        var wav = PrayerSound.BuildChimeWav();
        var sampleCount = (wav.Length - 44) / 2;

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));                  // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));                  // mono
        Assert.Equal(PrayerSound.SampleRate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));                 // bits per sample
        Assert.Equal(0, (wav.Length - 44) % 2);
        Assert.InRange(sampleCount / (double)PrayerSound.SampleRate, 1.55, 1.65);

        var peak = 0;
        for (var i = 0; i < sampleCount; i++)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, 44 + i * 2)));
        Assert.InRange(peak, 1000, short.MaxValue);
    }

    [Fact]
    public void Chime_starts_quietly_so_it_does_not_click()
    {
        var wav = PrayerSound.BuildChimeWav();
        Assert.True(Math.Abs(BitConverter.ToInt16(wav, 44)) < 50);
    }
}

public class PrayerSettingsTests
{
    [Fact]
    public void Defaults_and_snake_case_round_trip()
    {
        var defaults = new PrayerSettings();
        Assert.Equal("kemenag", defaults.Method);
        Assert.Equal(10, defaults.RemindBeforeMin);
        Assert.Equal("chime", defaults.Sound);
        Assert.False(defaults.HasLocation);

        var chosen = new PrayerSettings
        {
            PlaceName = "Jakarta", Latitude = -6.2088, Longitude = 106.8456, TimeZoneId = "Asia/Jakarta",
            RemindBeforeMin = 15, Sound = "silent",
        };
        chosen.ExtraMinutes["Fajr"] = 1;

        var json = JsonSerializer.Serialize(chosen, JsonStore.Options);
        Assert.Contains("\"remind_before_min\": 15", json);
        Assert.Contains("\"time_zone_id\": \"Asia/Jakarta\"", json);

        var back = JsonSerializer.Deserialize<PrayerSettings>(json, JsonStore.Options)!;
        Assert.True(back.HasLocation);
        Assert.Equal("Jakarta", back.PlaceName);
        Assert.Equal("silent", back.Sound);
        Assert.Equal(1, back.ExtraMinutes["Fajr"]);
    }
}
