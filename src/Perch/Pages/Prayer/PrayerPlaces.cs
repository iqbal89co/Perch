using System.Globalization;
using System.Text;

namespace Perch.Pages.Prayer;

/// <summary>A city the user can pick. Its time zone and default method are used for the prayer times.</summary>
public sealed record PrayerPlace(
    string Name,
    string Country,
    double Latitude,
    double Longitude,
    string TimeZoneId,
    string MethodId);

/// <summary>The built-in list of places, search and zone lookup. Pure, no WPF.</summary>
public static class PrayerPlaces
{
    private const string Id = "kemenag";

    private static PrayerPlace P(string name, string country, double lat, double lon, string zone, string method = "mwl") =>
        new(name, country, lat, lon, zone, method);

    public static readonly IReadOnlyList<PrayerPlace> All = new[]
    {
        // Indonesia: every provincial capital, then the big cities around Jakarta and the main islands.
        P("Banda Aceh", "Indonesia", 5.5483, 95.3238, "Asia/Jakarta", Id),
        P("Medan", "Indonesia", 3.5952, 98.6722, "Asia/Jakarta", Id),
        P("Padang", "Indonesia", -0.9471, 100.4172, "Asia/Jakarta", Id),
        P("Pekanbaru", "Indonesia", 0.5071, 101.4478, "Asia/Jakarta", Id),
        P("Tanjung Pinang", "Indonesia", 0.9182, 104.4550, "Asia/Jakarta", Id),
        P("Jambi", "Indonesia", -1.6101, 103.6131, "Asia/Jakarta", Id),
        P("Palembang", "Indonesia", -2.9761, 104.7754, "Asia/Jakarta", Id),
        P("Bengkulu", "Indonesia", -3.8004, 102.2655, "Asia/Jakarta", Id),
        P("Pangkal Pinang", "Indonesia", -2.1316, 106.1169, "Asia/Jakarta", Id),
        P("Bandar Lampung", "Indonesia", -5.3971, 105.2660, "Asia/Jakarta", Id),
        P("Jakarta", "Indonesia", -6.2088, 106.8456, "Asia/Jakarta", Id),
        P("Tangerang", "Indonesia", -6.1783, 106.6319, "Asia/Jakarta", Id),
        P("Tangerang Selatan", "Indonesia", -6.2886, 106.7179, "Asia/Jakarta", Id),
        P("Bekasi", "Indonesia", -6.2383, 106.9756, "Asia/Jakarta", Id),
        P("Depok", "Indonesia", -6.4025, 106.7942, "Asia/Jakarta", Id),
        P("Bogor", "Indonesia", -6.5971, 106.8060, "Asia/Jakarta", Id),
        P("Bandung", "Indonesia", -6.9175, 107.6191, "Asia/Jakarta", Id),
        P("Serang", "Indonesia", -6.1200, 106.1503, "Asia/Jakarta", Id),
        P("Semarang", "Indonesia", -6.9667, 110.4167, "Asia/Jakarta", Id),
        P("Solo", "Indonesia", -7.5755, 110.8243, "Asia/Jakarta", Id),
        P("Yogyakarta", "Indonesia", -7.7956, 110.3695, "Asia/Jakarta", Id),
        P("Malang", "Indonesia", -7.9666, 112.6326, "Asia/Jakarta", Id),
        P("Surabaya", "Indonesia", -7.2575, 112.7521, "Asia/Jakarta", Id),
        P("Pontianak", "Indonesia", -0.0263, 109.3425, "Asia/Jakarta", Id),
        P("Palangka Raya", "Indonesia", -2.2100, 113.9200, "Asia/Jakarta", Id),
        P("Banjarmasin", "Indonesia", -3.3186, 114.5944, "Asia/Makassar", Id),
        P("Samarinda", "Indonesia", -0.4948, 117.1436, "Asia/Makassar", Id),
        P("Tanjung Selor", "Indonesia", 2.8367, 117.3639, "Asia/Makassar", Id),
        P("Denpasar", "Indonesia", -8.6705, 115.2126, "Asia/Makassar", Id),
        P("Mataram", "Indonesia", -8.5833, 116.1167, "Asia/Makassar", Id),
        P("Kupang", "Indonesia", -10.1772, 123.6070, "Asia/Makassar", Id),
        P("Manado", "Indonesia", 1.4748, 124.8421, "Asia/Makassar", Id),
        P("Gorontalo", "Indonesia", 0.5435, 123.0568, "Asia/Makassar", Id),
        P("Palu", "Indonesia", -0.8917, 119.8707, "Asia/Makassar", Id),
        P("Mamuju", "Indonesia", -2.6779, 118.8895, "Asia/Makassar", Id),
        P("Makassar", "Indonesia", -5.1477, 119.4327, "Asia/Makassar", Id),
        P("Kendari", "Indonesia", -3.9985, 122.5129, "Asia/Makassar", Id),
        P("Ambon", "Indonesia", -3.6954, 128.1814, "Asia/Jayapura", Id),
        P("Sofifi", "Indonesia", 0.7000, 127.5667, "Asia/Jayapura", Id),
        P("Manokwari", "Indonesia", -0.8615, 134.0620, "Asia/Jayapura", Id),
        P("Sorong", "Indonesia", -0.8762, 131.2558, "Asia/Jayapura", Id),
        P("Nabire", "Indonesia", -3.3667, 135.4833, "Asia/Jayapura", Id),
        P("Jayapura", "Indonesia", -2.5333, 140.7167, "Asia/Jayapura", Id),
        P("Wamena", "Indonesia", -4.0972, 138.9441, "Asia/Jayapura", Id),
        P("Merauke", "Indonesia", -8.4950, 140.4023, "Asia/Jayapura", Id),

        // Malaysia, Singapore, Brunei
        P("Kuala Lumpur", "Malaysia", 3.1390, 101.6869, "Asia/Kuala_Lumpur", "singapore"),
        P("Johor Bahru", "Malaysia", 1.4927, 103.7414, "Asia/Kuala_Lumpur", "singapore"),
        P("George Town", "Malaysia", 5.4141, 100.3288, "Asia/Kuala_Lumpur", "singapore"),
        P("Ipoh", "Malaysia", 4.5975, 101.0901, "Asia/Kuala_Lumpur", "singapore"),
        P("Kuching", "Malaysia", 1.5535, 110.3593, "Asia/Kuching", "singapore"),
        P("Kota Kinabalu", "Malaysia", 5.9804, 116.0735, "Asia/Kuching", "singapore"),
        P("Singapore", "Singapore", 1.3521, 103.8198, "Asia/Singapore", "singapore"),
        P("Bandar Seri Begawan", "Brunei", 4.9031, 114.9398, "Asia/Brunei", "singapore"),

        // Arabian peninsula and Levant
        P("Mecca", "Saudi Arabia", 21.4225, 39.8262, "Asia/Riyadh", "ummalqura"),
        P("Medina", "Saudi Arabia", 24.5247, 39.5692, "Asia/Riyadh", "ummalqura"),
        P("Riyadh", "Saudi Arabia", 24.7136, 46.6753, "Asia/Riyadh", "ummalqura"),
        P("Jeddah", "Saudi Arabia", 21.4858, 39.1925, "Asia/Riyadh", "ummalqura"),
        P("Taif", "Saudi Arabia", 21.2703, 40.4158, "Asia/Riyadh", "ummalqura"),
        P("Dubai", "UAE", 25.2048, 55.2708, "Asia/Dubai", "gulf"),
        P("Abu Dhabi", "UAE", 24.4539, 54.3773, "Asia/Dubai", "gulf"),
        P("Sharjah", "UAE", 25.3463, 55.4209, "Asia/Dubai", "gulf"),
        P("Doha", "Qatar", 25.2854, 51.5310, "Asia/Qatar", "ummalqura"),
        P("Manama", "Bahrain", 26.2285, 50.5860, "Asia/Bahrain", "gulf"),
        P("Kuwait City", "Kuwait", 29.3759, 47.9774, "Asia/Kuwait", "ummalqura"),
        P("Muscat", "Oman", 23.5880, 58.3829, "Asia/Muscat", "gulf"),
        P("Amman", "Jordan", 31.9454, 35.9284, "Asia/Amman", "mwl"),
        P("Baghdad", "Iraq", 33.3152, 44.3661, "Asia/Baghdad", "mwl"),
        P("Sanaa", "Yemen", 15.3694, 44.1910, "Asia/Aden", "ummalqura"),

        // Turkey, Iran, Egypt, South and Central Asia
        P("Istanbul", "Turkey", 41.0082, 28.9784, "Europe/Istanbul", "turkey"),
        P("Ankara", "Turkey", 39.9334, 32.8597, "Europe/Istanbul", "turkey"),
        P("Tehran", "Iran", 35.6892, 51.3890, "Asia/Tehran", "tehran"),
        P("Cairo", "Egypt", 30.0444, 31.2357, "Africa/Cairo", "egypt"),
        P("Karachi", "Pakistan", 24.8607, 67.0011, "Asia/Karachi", "karachi"),
        P("Lahore", "Pakistan", 31.5204, 74.3587, "Asia/Karachi", "karachi"),
        P("Islamabad", "Pakistan", 33.6844, 73.0479, "Asia/Karachi", "karachi"),
        P("Peshawar", "Pakistan", 34.0151, 71.5249, "Asia/Karachi", "karachi"),
        P("Quetta", "Pakistan", 30.1798, 66.9750, "Asia/Karachi", "karachi"),
        P("Faisalabad", "Pakistan", 31.4504, 73.1350, "Asia/Karachi", "karachi"),
        P("Dhaka", "Bangladesh", 23.8103, 90.4125, "Asia/Dhaka", "karachi"),
        P("Delhi", "India", 28.7041, 77.1025, "Asia/Kolkata", "mwl"),
        P("Mumbai", "India", 19.0760, 72.8777, "Asia/Kolkata", "mwl"),
        P("Hyderabad", "India", 17.3850, 78.4867, "Asia/Kolkata", "mwl"),
        P("Kolkata", "India", 22.5726, 88.3639, "Asia/Kolkata", "mwl"),
        P("Chennai", "India", 13.0827, 80.2707, "Asia/Kolkata", "mwl"),
        P("Bengaluru", "India", 12.9716, 77.5946, "Asia/Kolkata", "mwl"),
        P("Kabul", "Afghanistan", 34.5553, 69.2075, "Asia/Kabul", "karachi"),
        P("Tashkent", "Uzbekistan", 41.2995, 69.2401, "Asia/Tashkent", "mwl"),
        P("Almaty", "Kazakhstan", 43.2220, 76.8512, "Asia/Almaty", "mwl"),
        P("Baku", "Azerbaijan", 40.4093, 49.8671, "Asia/Baku", "mwl"),
        P("Kathmandu", "Nepal", 27.7172, 85.3240, "Asia/Kathmandu", "mwl"),
        P("Colombo", "Sri Lanka", 6.9271, 79.8612, "Asia/Colombo", "mwl"),
        P("Yangon", "Myanmar", 16.8409, 96.1735, "Asia/Yangon", "mwl"),

        // Africa
        P("Casablanca", "Morocco", 33.5731, -7.5898, "Africa/Casablanca", "mwl"),
        P("Algiers", "Algeria", 36.7538, 3.0588, "Africa/Algiers", "mwl"),
        P("Tunis", "Tunisia", 36.8065, 10.1815, "Africa/Tunis", "mwl"),
        P("Lagos", "Nigeria", 6.5244, 3.3792, "Africa/Lagos", "mwl"),
        P("Abuja", "Nigeria", 9.0765, 7.3986, "Africa/Lagos", "mwl"),
        P("Accra", "Ghana", 5.6037, -0.1870, "Africa/Accra", "mwl"),
        P("Dakar", "Senegal", 14.7167, -17.4677, "Africa/Dakar", "mwl"),
        P("Nairobi", "Kenya", -1.2921, 36.8219, "Africa/Nairobi", "mwl"),
        P("Dar es Salaam", "Tanzania", -6.7924, 39.2083, "Africa/Dar_es_Salaam", "mwl"),
        P("Kampala", "Uganda", 0.3476, 32.5825, "Africa/Kampala", "mwl"),
        P("Addis Ababa", "Ethiopia", 8.9806, 38.7578, "Africa/Addis_Ababa", "mwl"),
        P("Johannesburg", "South Africa", -26.2041, 28.0473, "Africa/Johannesburg", "mwl"),

        // Europe
        P("London", "United Kingdom", 51.5074, -0.1278, "Europe/London", "mwl"),
        P("Birmingham", "United Kingdom", 52.4862, -1.8904, "Europe/London", "mwl"),
        P("Manchester", "United Kingdom", 53.4808, -2.2426, "Europe/London", "mwl"),
        P("Paris", "France", 48.8566, 2.3522, "Europe/Paris", "mwl"),
        P("Lyon", "France", 45.7640, 4.8357, "Europe/Paris", "mwl"),
        P("Berlin", "Germany", 52.5200, 13.4050, "Europe/Berlin", "mwl"),
        P("Hamburg", "Germany", 53.5511, 9.9937, "Europe/Berlin", "mwl"),
        P("Munich", "Germany", 48.1351, 11.5820, "Europe/Berlin", "mwl"),
        P("Amsterdam", "Netherlands", 52.3676, 4.9041, "Europe/Amsterdam", "mwl"),
        P("Brussels", "Belgium", 50.8503, 4.3517, "Europe/Brussels", "mwl"),
        P("Vienna", "Austria", 48.2082, 16.3738, "Europe/Vienna", "mwl"),
        P("Zurich", "Switzerland", 47.3769, 8.5417, "Europe/Zurich", "mwl"),
        P("Madrid", "Spain", 40.4168, -3.7038, "Europe/Madrid", "mwl"),
        P("Rome", "Italy", 41.9028, 12.4964, "Europe/Rome", "mwl"),
        P("Copenhagen", "Denmark", 55.6761, 12.5683, "Europe/Copenhagen", "mwl"),
        P("Stockholm", "Sweden", 59.3293, 18.0686, "Europe/Stockholm", "mwl"),
        P("Oslo", "Norway", 59.9139, 10.7522, "Europe/Oslo", "mwl"),
        P("Moscow", "Russia", 55.7558, 37.6173, "Europe/Moscow", "mwl"),

        // Americas
        P("New York", "United States", 40.7128, -74.0060, "America/New_York", "isna"),
        P("Washington", "United States", 38.9072, -77.0369, "America/New_York", "isna"),
        P("Atlanta", "United States", 33.7490, -84.3880, "America/New_York", "isna"),
        P("Miami", "United States", 25.7617, -80.1918, "America/New_York", "isna"),
        P("Chicago", "United States", 41.8781, -87.6298, "America/Chicago", "isna"),
        P("Houston", "United States", 29.7604, -95.3698, "America/Chicago", "isna"),
        P("Dallas", "United States", 32.7767, -96.7970, "America/Chicago", "isna"),
        P("Denver", "United States", 39.7392, -104.9903, "America/Denver", "isna"),
        P("Los Angeles", "United States", 34.0522, -118.2437, "America/Los_Angeles", "isna"),
        P("San Francisco", "United States", 37.7749, -122.4194, "America/Los_Angeles", "isna"),
        P("Toronto", "Canada", 43.6532, -79.3832, "America/Toronto", "isna"),
        P("Montreal", "Canada", 45.5017, -73.5673, "America/Toronto", "isna"),
        P("Vancouver", "Canada", 49.2827, -123.1207, "America/Vancouver", "isna"),
        P("Mexico City", "Mexico", 19.4326, -99.1332, "America/Mexico_City", "isna"),
        P("Sao Paulo", "Brazil", -23.5505, -46.6333, "America/Sao_Paulo", "mwl"),

        // Asia-Pacific
        P("Sydney", "Australia", -33.8688, 151.2093, "Australia/Sydney", "mwl"),
        P("Melbourne", "Australia", -37.8136, 144.9631, "Australia/Melbourne", "mwl"),
        P("Auckland", "New Zealand", -36.8485, 174.7633, "Pacific/Auckland", "mwl"),
        P("Tokyo", "Japan", 35.6762, 139.6503, "Asia/Tokyo", "mwl"),
        P("Seoul", "South Korea", 37.5665, 126.9780, "Asia/Seoul", "mwl"),
        P("Beijing", "China", 39.9042, 116.4074, "Asia/Shanghai", "mwl"),
        P("Hong Kong", "China", 22.3193, 114.1694, "Asia/Hong_Kong", "mwl"),
        P("Taipei", "Taiwan", 25.0330, 121.5654, "Asia/Taipei", "mwl"),
        P("Manila", "Philippines", 14.5995, 120.9842, "Asia/Manila", "mwl"),
        P("Bangkok", "Thailand", 13.7563, 100.5018, "Asia/Bangkok", "mwl"),
        P("Hanoi", "Vietnam", 21.0278, 105.8342, "Asia/Bangkok", "mwl"),
        P("Ho Chi Minh City", "Vietnam", 10.8231, 106.6297, "Asia/Ho_Chi_Minh", "mwl"),
        P("Phnom Penh", "Cambodia", 11.5564, 104.9282, "Asia/Phnom_Penh", "mwl"),
    };

    /// <summary>Time zone for an IANA id; the device's zone when the id is missing or unknown.</summary>
    public static TimeZoneInfo ResolveZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>Places whose name or country contains the query, ignoring case and accents. Empty query lists all.</summary>
    public static IReadOnlyList<PrayerPlace> Search(string? query)
    {
        var q = Fold(query ?? string.Empty);
        if (q.Length == 0) return All;
        return All
            .Where(p => Fold(p.Name).Contains(q, StringComparison.Ordinal) || Fold(p.Country).Contains(q, StringComparison.Ordinal))
            .OrderBy(p => Fold(p.Name).StartsWith(q, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
    }

    /// <summary>The place closest to a coordinate (great-circle distance).</summary>
    public static PrayerPlace Nearest(double latitude, double longitude) =>
        All.MinBy(p => DistanceKm(latitude, longitude, p.Latitude, p.Longitude))!;

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * 6371 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Lower case with accents removed, so typing "sao" finds "São" and "istanbul" finds "Istanbul".</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString().ToLowerInvariant();
    }
}
