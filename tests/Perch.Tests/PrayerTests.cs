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

public class PrayerDayViewTests
{
    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");
    private static readonly DateOnly Day = new(2026, 11, 16);

    private static PrayerDay JakartaDay(DateOnly? date = null) =>
        PrayerCalculator.Calculate(date ?? Day, -6.2886, 106.7179, 0, Jakarta, PrayerMethods.Kemenag);

    private static DateTimeOffset At(int hour, int minute) =>
        new(Day.Year, Day.Month, Day.Day, hour, minute, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Imsak_is_exactly_ten_minutes_before_fajr()
    {
        var day = JakartaDay();
        Assert.Equal(day.Fajr!.Value.AddMinutes(-10), PrayerCalculator.Imsak(day));
        Assert.Null(PrayerCalculator.Imsak(new PrayerDay(Day, null, null, null, null, null, null)));
    }

    [Fact]
    public void Dhuha_is_about_eighteen_minutes_after_sunrise_in_jakarta()
    {
        var day = JakartaDay();
        Assert.NotNull(day.Dhuha);
        // Sunrise about 05:25 on this day; the sun reaches +4.5 degrees about 05:43 to 05:50.
        Assert.InRange(day.Dhuha!.Value.TimeOfDay.TotalMinutes, 5 * 60 + 40, 5 * 60 + 55);
        Assert.True(day.Dhuha > day.Sunrise);
        Assert.True(day.Dhuha < day.Dhuhr);
    }

    [Fact]
    public void Dhuha_is_not_a_prayer_for_reminders()
    {
        // Next() only walks the five prayers: just before and just after Dhuha, the next one is Dhuhr.
        var day = JakartaDay();
        Assert.Equal("Dhuhr", PrayerCalculator.Next(day, day.Dhuha!.Value.AddMinutes(-1))!.Value.Name);
        Assert.Equal("Dhuhr", PrayerCalculator.Next(day, day.Dhuha!.Value.AddMinutes(1))!.Value.Name);
    }

    [Fact]
    public void Night_splits_maghrib_to_fajr_in_half_and_two_thirds()
    {
        // Maghrib 18:00, tomorrow's Fajr 04:00: ten hours of night.
        var night = PrayerCalculator.Night(At(18, 0), At(18, 0).AddHours(10));
        Assert.NotNull(night);
        Assert.Equal(At(23, 0), night!.Value.Midnight);
        Assert.Equal(At(18, 0).AddMinutes(400), night.Value.LastThird); // 18:00 + 6 h 40 min = 00:40 next day
        Assert.Null(PrayerCalculator.Night(null, At(4, 0)));
        Assert.Null(PrayerCalculator.Night(At(18, 0), null));
    }

    [Fact]
    public void Night_times_for_jakarta_are_plausible()
    {
        var today = JakartaDay();
        var tomorrow = JakartaDay(Day.AddDays(1));
        var night = PrayerCalculator.Night(today.Maghrib, tomorrow.Fajr);
        Assert.NotNull(night);
        Assert.True(night!.Value.Midnight > today.Isha);
        Assert.True(night.Value.Midnight < tomorrow.Fajr);
        Assert.True(night.Value.LastThird > night.Value.Midnight);
        Assert.True(night.Value.LastThird < tomorrow.Fajr);
    }

    [Fact]
    public void Day_length_runs_from_sunrise_to_maghrib()
    {
        Assert.Equal(TimeSpan.FromMinutes(12 * 60 + 24), PrayerCalculator.DayLength(At(5, 25), At(17, 49)));
    }

    [Fact]
    public void Distance_to_the_kaaba_from_jakarta_and_london()
    {
        Assert.InRange(PrayerCalculator.DistanceToKaabaKm(-6.2088, 106.8456), 7850, 7930);
        Assert.InRange(PrayerCalculator.DistanceToKaabaKm(51.5074, -0.1278), 4700, 4800);
        Assert.InRange(PrayerCalculator.DistanceToKaabaKm(21.4225, 39.8262), 0, 0.5);
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(11.2, "N")]
    [InlineData(11.3, "NNE")]
    [InlineData(45, "NE")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(295, "WNW")]
    [InlineData(348.74, "NNW")]
    [InlineData(348.8, "N")]
    [InlineData(359.9, "N")]
    [InlineData(-65, "WNW")]
    public void Compass_has_sixteen_points(double bearing, string expected)
    {
        Assert.Equal(expected, PrayerCalculator.Compass16(bearing));
    }

    [Fact]
    public void Qibla_text_shows_bearing_direction_and_distance()
    {
        var bearing = PrayerCalculator.QiblaBearing(-6.2088, 106.8456);
        var distance = PrayerCalculator.DistanceToKaabaKm(-6.2088, 106.8456);
        var text = PrayerText.Qibla(bearing, distance);
        Assert.Matches(@"^29[45]° WNW · 7,\d{3} km$", text);
    }

    [Fact]
    public void Previous_prayer_is_the_last_one_passed_and_looks_back_to_yesterday()
    {
        var today = JakartaDay();
        var yesterday = JakartaDay(Day.AddDays(-1));

        var afterAsr = PrayerCalculator.PreviousPrayer(today, today.Asr!.Value.AddMinutes(65), yesterday);
        Assert.Equal("Asr", afterAsr!.Value.Name);
        Assert.Equal(today.Asr, afterAsr.Value.Time);

        // Sunrise is not a prayer: after sunrise and before Dhuhr the previous one is Fajr.
        var afterSunrise = PrayerCalculator.PreviousPrayer(today, today.Sunrise!.Value.AddMinutes(5), yesterday);
        Assert.Equal("Fajr", afterSunrise!.Value.Name);

        // Before today's Fajr the previous prayer is yesterday's Isha.
        var beforeFajr = PrayerCalculator.PreviousPrayer(today, today.Fajr!.Value.AddMinutes(-30), yesterday);
        Assert.Equal("Isha", beforeFajr!.Value.Name);
        Assert.Equal(yesterday.Isha, beforeFajr.Value.Time);
        Assert.Null(PrayerCalculator.PreviousPrayer(today, today.Fajr!.Value.AddMinutes(-30)));
    }

    [Fact]
    public void Progress_is_the_fraction_between_two_prayers_and_clamped()
    {
        var previous = At(14, 59);
        var next = At(17, 49);
        Assert.Equal(0.5, PrayerCalculator.PrayerProgress(previous, next, previous + (next - previous) / 2), 6);
        Assert.Equal(0.0, PrayerCalculator.PrayerProgress(previous, next, previous.AddHours(-1)));
        Assert.Equal(1.0, PrayerCalculator.PrayerProgress(previous, next, next.AddHours(1)));
        Assert.Equal(0.0, PrayerCalculator.PrayerProgress(next, next, next));
    }

    [Fact]
    public void Next_islamic_date_is_upcoming_and_one_of_the_listed_dates()
    {
        var names = new[] { "Islamic New Year", "Ashura", "Mawlid", "Isra Mi'raj", "Ramadan", "Eid al-Fitr", "Eid al-Adha" };
        var today = new DateOnly(2026, 11, 16);
        var next = PrayerCalculator.NextIslamicEvent(today);

        Assert.NotNull(next);
        Assert.Contains(next!.Name, names);
        Assert.True(next.Date >= today);
        Assert.Equal(next.Date.DayNumber - today.DayNumber, next.DaysUntil);
        // Every listed date comes at least once a year, so the nearest one is within a year.
        Assert.InRange(next.DaysUntil, 0, 366);
    }

    [Fact]
    public void Next_islamic_date_never_throws_across_a_year_and_wraps_at_year_end()
    {
        var start = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 400; i++)
        {
            var next = PrayerCalculator.NextIslamicEvent(start.AddDays(i));
            Assert.NotNull(next);
            Assert.InRange(next!.DaysUntil, 0, 366);
        }
    }

    [Fact]
    public void Next_islamic_date_is_today_on_the_day_itself_and_null_when_unrepresentable()
    {
        var calendar = new System.Globalization.UmAlQuraCalendar();
        var ramadan = DateOnly.FromDateTime(calendar.ToDateTime(1448, 9, 1, 0, 0, 0, 0));
        var onRamadan = PrayerCalculator.NextIslamicEvent(ramadan);
        Assert.Equal("Ramadan", onRamadan!.Name);
        Assert.Equal(0, onRamadan.DaysUntil);

        // Before the calendar's supported range the lookup must not throw.
        Assert.Null(PrayerCalculator.NextIslamicEvent(new DateOnly(1800, 1, 1)));
    }

    [Theory]
    [InlineData(0, "Ramadan today")]
    [InlineData(1, "Ramadan tomorrow")]
    [InlineData(103, "Ramadan in 103 days")]
    public void Islamic_caption_words_today_and_tomorrow(int days, string expected)
    {
        Assert.Equal(expected, PrayerText.IslamicEventCaption(new IslamicEvent("Ramadan", Day, days)));
    }

    [Theory]
    [InlineData(65, "1 h 05 m")]
    [InlineData(42, "42 min")]
    [InlineData(0, "0 min")]
    [InlineData(-5, "0 min")]
    public void Elapsed_text(int minutes, string expected)
    {
        Assert.Equal(expected, PrayerText.Elapsed(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Method_short_names_for_the_header()
    {
        Assert.Equal("Kemenag", PrayerText.MethodShortName("kemenag"));
        Assert.Equal("MWL", PrayerText.MethodShortName("mwl"));
        Assert.Equal("Kemenag", PrayerText.MethodShortName(null));
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
