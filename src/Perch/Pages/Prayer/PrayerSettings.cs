namespace Perch.Pages.Prayer;

/// <summary>Persisted as data/prayer.json. Every change is saved at once.</summary>
public sealed class PrayerSettings
{
    /// <summary>Display name of the chosen city (or "Near ..." text for the device location).</summary>
    public string? PlaceName { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>IANA time zone id, e.g. "Asia/Jakarta".</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Calculation method id, see <see cref="PrayerMethods"/>.</summary>
    public string Method { get; set; } = "kemenag";
    /// <summary>"standard" or "hanafi".</summary>
    public string Asr { get; set; } = "standard";
    /// <summary>Minutes before a prayer to show the countdown on the resting island. 0 turns it off.</summary>
    public int RemindBeforeMin { get; set; } = 10;
    /// <summary>"chime", "system" or "silent".</summary>
    public string Sound { get; set; } = "chime";
    public bool PauseMusic { get; set; } = true;
    public bool Notify { get; set; } = true;

    /// <summary>Extra minutes per prayer name (e.g. "Fajr": 1), on top of the method's own adjustments.</summary>
    public Dictionary<string, int> ExtraMinutes { get; set; } = new();

    public bool HasLocation => Latitude is not null && Longitude is not null;
}
