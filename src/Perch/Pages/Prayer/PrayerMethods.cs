namespace Perch.Pages.Prayer;

/// <summary>
/// A set of twilight angles (or minute offsets) used to compute the prayer times, plus the minutes each
/// authority adds on top as a precaution.
/// </summary>
/// <param name="Id">Stable id stored in settings, e.g. "kemenag".</param>
/// <param name="Name">Display name.</param>
/// <param name="FajrAngle">Sun altitude below the horizon at Fajr, degrees.</param>
/// <param name="IshaAngle">Sun altitude below the horizon at Isha, degrees. Null when Isha is a fixed delay.</param>
/// <param name="IshaMinutes">Isha as minutes after Maghrib. Used when set.</param>
/// <param name="MaghribAngle">Sun altitude below the horizon at Maghrib. Null means sunset.</param>
/// <param name="Adjustments">Default minutes added to each prayer (sunrise is never adjusted).</param>
public sealed record CalculationMethod(
    string Id,
    string Name,
    double FajrAngle,
    double? IshaAngle,
    int? IshaMinutes,
    double? MaghribAngle,
    IReadOnlyDictionary<PrayerKind, int> Adjustments);

/// <summary>The calculation methods Perch ships with.</summary>
public static class PrayerMethods
{
    public static readonly CalculationMethod Kemenag = new(
        "kemenag", "Kemenag (Indonesia)", 20, 18, null, null,
        Adjust(fajr: 2, dhuhr: 2, asr: 2, maghrib: 2, isha: 2)); // ihtiyat: a precautionary 2 minutes

    public static readonly CalculationMethod Mwl = new(
        "mwl", "Muslim World League", 18, 17, null, null, Adjust());

    public static readonly CalculationMethod Isna = new(
        "isna", "ISNA (North America)", 15, 15, null, null, Adjust());

    public static readonly CalculationMethod Egypt = new(
        "egypt", "Egyptian General Authority", 19.5, 17.5, null, null, Adjust());

    public static readonly CalculationMethod UmmAlQura = new(
        "ummalqura", "Umm al-Qura (Makkah)", 18.5, null, 90, null, Adjust());

    public static readonly CalculationMethod Karachi = new(
        "karachi", "Karachi (Univ. of Islamic Sciences)", 18, 18, null, null, Adjust());

    public static readonly CalculationMethod Singapore = new(
        "singapore", "MUIS (Singapore)", 20, 18, null, null, Adjust());

    public static readonly CalculationMethod Turkey = new(
        "turkey", "Diyanet (Turkey)", 18, 17, null, null, Adjust());

    public static readonly CalculationMethod Gulf = new(
        "gulf", "Gulf Region", 19.5, null, 90, null, Adjust());

    public static readonly CalculationMethod Tehran = new(
        "tehran", "Tehran (Institute of Geophysics)", 17.7, 14, null, 4.5, Adjust());

    /// <summary>Every method, in the order the settings picker cycles through them.</summary>
    public static readonly IReadOnlyList<CalculationMethod> All =
        new[] { Kemenag, Mwl, Isna, Egypt, UmmAlQura, Karachi, Singapore, Turkey, Gulf, Tehran };

    /// <summary>The method with this id; Kemenag when the id is unknown.</summary>
    public static CalculationMethod Find(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Kemenag;

    private static IReadOnlyDictionary<PrayerKind, int> Adjust(int fajr = 0, int dhuhr = 0, int asr = 0, int maghrib = 0, int isha = 0) =>
        new Dictionary<PrayerKind, int>
        {
            [PrayerKind.Fajr] = fajr,
            [PrayerKind.Dhuhr] = dhuhr,
            [PrayerKind.Asr] = asr,
            [PrayerKind.Maghrib] = maghrib,
            [PrayerKind.Isha] = isha,
        };
}
