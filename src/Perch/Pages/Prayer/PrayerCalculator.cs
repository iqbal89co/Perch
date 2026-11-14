namespace Perch.Pages.Prayer;

/// <summary>The five daily prayers plus sunrise (shown for reference, never a reminder).</summary>
public enum PrayerKind { Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha }

/// <summary>Asr shadow factor: Standard (Shafi'i, Maliki, Hanbali) = 1, Hanafi = 2.</summary>
public enum AsrSchool { Standard, Hanafi }

/// <summary>What to do when the sun never reaches the Fajr or Isha angle (high latitudes).</summary>
public enum HighLatitudeRule { None, AngleBased, MiddleOfNight, OneSeventh }

/// <summary>The times of one civil day in one place. A null time means it does not occur that day.</summary>
public sealed record PrayerDay(
    DateOnly Date,
    DateTimeOffset? Fajr,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Dhuhr,
    DateTimeOffset? Asr,
    DateTimeOffset? Maghrib,
    DateTimeOffset? Isha)
{
    public DateTimeOffset? At(PrayerKind kind) => kind switch
    {
        PrayerKind.Fajr => Fajr,
        PrayerKind.Sunrise => Sunrise,
        PrayerKind.Dhuhr => Dhuhr,
        PrayerKind.Asr => Asr,
        PrayerKind.Maghrib => Maghrib,
        PrayerKind.Isha => Isha,
        _ => null,
    };
}

/// <summary>
/// Prayer times computed on the device with the standard "PrayTimes" solar method (Zarrabi-Zadeh),
/// plus the qibla bearing, the next prayer and the Hijri date text. Pure: no WPF, no network.
/// </summary>
public static class PrayerCalculator
{
    private const double KaabaLatitude = 21.4225;
    private const double KaabaLongitude = 39.8262;
    private const double SunriseBaseAltitude = 0.833;
    private const int Iterations = 2;

    private static readonly DateTime J2000Noon = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PrayerKind[] PrayersInOrder =
        { PrayerKind.Fajr, PrayerKind.Dhuhr, PrayerKind.Asr, PrayerKind.Maghrib, PrayerKind.Isha };

    /// <summary>
    /// Times for one local date. <paramref name="zone"/> supplies the UTC offset at local noon, so daylight saving
    /// is handled per day. <paramref name="extraMinutes"/> adds to the per-prayer adjustments; the method's own
    /// adjustments (e.g. Kemenag's +2 minutes) are added unless <paramref name="applyMethodAdjustments"/> is false.
    /// </summary>
    public static PrayerDay Calculate(
        DateOnly date,
        double latitude,
        double longitude,
        double elevationMeters,
        TimeZoneInfo zone,
        CalculationMethod method,
        AsrSchool asr = AsrSchool.Standard,
        HighLatitudeRule rule = HighLatitudeRule.AngleBased,
        IReadOnlyDictionary<PrayerKind, int>? extraMinutes = null,
        bool applyMethodAdjustments = true)
    {
        var noon = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Unspecified);
        var offset = zone.GetUtcOffset(noon);
        var tzHours = offset.TotalHours;

        // Julian day at 0h UT shifted by longitude: all sun-based hours below are measured from here.
        var jd = (new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc) - J2000Noon).TotalDays
                 + 2451545.0 - longitude / (15.0 * 24.0);
        var sun = new Solar(jd, latitude, elevationMeters);

        var hours = new[] { 5.0, 6.0, 12.0, 13.0, 18.0, 18.0, 18.0 };
        double[] raw = Pass(sun, hours, method, asr);
        for (var i = 0; i < Iterations; i++)
        {
            for (var k = 0; k < hours.Length; k++)
                if (!double.IsNaN(raw[k])) hours[k] = raw[k];
            raw = Pass(sun, hours, method, asr);
        }

        var fajr = raw[0];
        var sunrise = raw[1];
        var dhuhr = raw[2];
        var asrTime = raw[3];
        var sunset = raw[4];
        var maghrib = raw[5];
        var isha = raw[6];

        // High latitudes: the twilight angle may never be reached, or may take longer than a fraction of the night.
        // Then Fajr and Isha are capped at that fraction of the night (the standard "angle-based" rule).
        if (rule != HighLatitudeRule.None && !double.IsNaN(sunrise) && !double.IsNaN(sunset))
        {
            var night = FixHour(sunrise - sunset);
            var fajrLimit = sunrise - Portion(rule, method.FajrAngle) * night;
            if (double.IsNaN(fajr) || sunrise - fajr > Portion(rule, method.FajrAngle) * night)
                fajr = fajrLimit;
            if (method.IshaAngle is { } ishaAngle)
            {
                var ishaLimit = sunset + Portion(rule, ishaAngle) * night;
                if (double.IsNaN(isha) || isha - sunset > Portion(rule, ishaAngle) * night)
                    isha = ishaLimit;
            }
        }

        double Adjust(double value, PrayerKind kind)
        {
            if (double.IsNaN(value)) return value;
            var minutes = (applyMethodAdjustments ? method.Adjustments.GetValueOrDefault(kind) : 0)
                          + (extraMinutes?.GetValueOrDefault(kind) ?? 0);
            return value + minutes / 60.0;
        }

        DateTimeOffset? ToLocal(double value)
        {
            if (double.IsNaN(value)) return null;
            // Hours after local midnight of the requested date. Isha can pass midnight in summer; then it falls on
            // the following calendar day, which is correct, so the value is not wrapped.
            var local = value + tzHours - longitude / 15.0;
            var moment = date.ToDateTime(TimeOnly.MinValue).AddSeconds(Math.Round(local * 3600.0));
            return new DateTimeOffset(moment, offset);
        }

        return new PrayerDay(
            date,
            ToLocal(Adjust(fajr, PrayerKind.Fajr)),
            ToLocal(Adjust(sunrise, PrayerKind.Sunrise)),
            ToLocal(Adjust(dhuhr, PrayerKind.Dhuhr)),
            ToLocal(Adjust(asrTime, PrayerKind.Asr)),
            ToLocal(Adjust(maghrib, PrayerKind.Maghrib)),
            ToLocal(Adjust(isha, PrayerKind.Isha)));
    }

    /// <summary>
    /// Great-circle initial bearing from a place to the Kaaba, degrees clockwise from true north in [0, 360).
    /// </summary>
    public static double QiblaBearing(double latitude, double longitude)
    {
        var phi1 = Rad(latitude);
        var phi2 = Rad(KaabaLatitude);
        var dLambda = Rad(KaabaLongitude - longitude);
        var y = Math.Sin(dLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
        var bearing = Deg(Math.Atan2(y, x));
        return (bearing % 360 + 360) % 360;
    }

    /// <summary>
    /// The next of the five prayers (sunrise excluded) after <paramref name="now"/>. After Isha it is tomorrow's Fajr,
    /// taken from <paramref name="tomorrow"/> (pass null if unknown; then null is returned). Null when nothing is left.
    /// </summary>
    public static (string Name, DateTimeOffset Time)? Next(PrayerDay today, DateTimeOffset now, PrayerDay? tomorrow = null)
    {
        foreach (var kind in PrayersInOrder)
        {
            if (today.At(kind) is { } time && time > now) return (kind.ToString(), time);
        }

        return tomorrow?.Fajr is { } fajr ? (PrayerKind.Fajr.ToString(), fajr) : null;
    }

    // ---- The solar method -------------------------------------------------------------------------------

    /// <summary>One pass of the iteration. Returns Fajr, Sunrise, Dhuhr, Asr, Sunset, Maghrib, Isha in hours.</summary>
    private static double[] Pass(Solar sun, double[] hours, CalculationMethod method, AsrSchool asr)
    {
        var t = hours.Select(h => h / 24.0).ToArray();
        var factor = asr == AsrSchool.Hanafi ? 2.0 : 1.0;

        var fajr = sun.AltitudeTime(-method.FajrAngle, t[0], morning: true);
        var sunrise = sun.AltitudeTime(sun.SunriseAltitude, t[1], morning: true);
        var dhuhr = sun.MidDay(t[2]);
        var asrTime = sun.AsrTime(factor, t[3]);
        var sunset = sun.AltitudeTime(sun.SunriseAltitude, t[4], morning: false);
        var maghrib = method.MaghribAngle is { } maghribAngle
            ? sun.AltitudeTime(-maghribAngle, t[5], morning: false)
            : sunset;
        var isha = method.IshaMinutes is { } minutes
            ? maghrib + minutes / 60.0
            : sun.AltitudeTime(-(method.IshaAngle ?? 18.0), t[6], morning: false);

        return new[] { fajr, sunrise, dhuhr, asrTime, sunset, maghrib, isha };
    }

    private static double Portion(HighLatitudeRule rule, double angle) => rule switch
    {
        HighLatitudeRule.AngleBased => angle / 60.0,
        HighLatitudeRule.MiddleOfNight => 0.5,
        HighLatitudeRule.OneSeventh => 1.0 / 7.0,
        _ => 0,
    };

    /// <summary>Sun position and the hour-angle arithmetic for one date at one place.</summary>
    private sealed class Solar
    {
        private readonly double _jd;
        private readonly double _lat;

        public Solar(double jd, double latitude, double elevationMeters)
        {
            _jd = jd;
            _lat = latitude;
            // Sunrise and sunset use the apparent altitude of the sun's upper limb, lowered for height above sea level.
            SunriseAltitude = -(SunriseBaseAltitude + 0.0347 * Math.Sqrt(Math.Max(0, elevationMeters)));
        }

        /// <summary>Altitude (degrees) at which the sun counts as rising or setting.</summary>
        public double SunriseAltitude { get; }

        /// <summary>Solar noon in hours, measured from the shifted day start, wrapped to [0, 24).</summary>
        public double MidDay(double tDays)
        {
            var (_, eqt) = Position(_jd + tDays);
            return FixHour(12 - eqt);
        }

        /// <summary>Hours from the shifted day start when the sun is at <paramref name="altitude"/> degrees.</summary>
        public double AltitudeTime(double altitude, double tDays, bool morning)
        {
            var (decl, _) = Position(_jd + tDays);
            var cosH = (Math.Sin(Rad(altitude)) - Math.Sin(Rad(decl)) * Math.Sin(Rad(_lat)))
                       / (Math.Cos(Rad(decl)) * Math.Cos(Rad(_lat)));
            if (double.IsNaN(cosH) || cosH < -1 || cosH > 1) return double.NaN; // the sun never reaches it
            var hourAngle = Deg(Math.Acos(cosH)) / 15.0;
            return MidDay(tDays) + (morning ? -hourAngle : hourAngle);
        }

        /// <summary>Asr: the altitude where the shadow equals the object height times <paramref name="factor"/> plus its own.</summary>
        public double AsrTime(double factor, double tDays)
        {
            var (decl, _) = Position(_jd + tDays);
            var altitude = Deg(Math.Atan(1.0 / (factor + Math.Tan(Rad(Math.Abs(_lat - decl))))));
            return AltitudeTime(altitude, tDays, morning: false);
        }

        /// <summary>Declination (degrees) and equation of time (hours) for a Julian day.</summary>
        private static (double Decl, double EqT) Position(double jd)
        {
            var d = jd - 2451545.0;
            var g = FixAngle(357.529 + 0.98560028 * d);
            var q = FixAngle(280.459 + 0.98564736 * d);
            var l = FixAngle(q + 1.915 * Math.Sin(Rad(g)) + 0.020 * Math.Sin(Rad(2 * g)));
            var e = 23.439 - 0.00000036 * d;
            var ra = FixHour(Deg(Math.Atan2(Math.Cos(Rad(e)) * Math.Sin(Rad(l)), Math.Cos(Rad(l)))) / 15.0);
            var decl = Deg(Math.Asin(Math.Sin(Rad(e)) * Math.Sin(Rad(l))));
            var eqt = q / 15.0 - ra;
            return (decl, eqt);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;
    private static double Deg(double radians) => radians * 180.0 / Math.PI;

    private static double FixAngle(double a) => a - 360.0 * Math.Floor(a / 360.0);
    private static double FixHour(double h) => h - 24.0 * Math.Floor(h / 24.0);
}
