namespace Perch.Pages.Today;

/// <summary>Pure calendar helpers (unit tested).</summary>
public static class CalendarMath
{
    /// <summary>
    /// The 42 days (6 weeks) shown for a month grid, starting on <paramref name="firstDay"/>.
    /// The first cell is the last <paramref name="firstDay"/> on or before the 1st of the month.
    /// </summary>
    public static IReadOnlyList<DateOnly> MonthGrid(int year, int month, DayOfWeek firstDay = DayOfWeek.Monday)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
        var start = first.AddDays(-offset);
        return Enumerable.Range(0, 42).Select(start.AddDays).ToList();
    }

    /// <summary>ISO-8601 week number.</summary>
    public static int IsoWeek(DateOnly date) =>
        System.Globalization.ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));

    /// <summary>Weekday headers in display order, e.g. Mo Tu We ...</summary>
    public static IReadOnlyList<string> WeekdayHeaders(DayOfWeek firstDay = DayOfWeek.Monday) =>
        Enumerable.Range(0, 7).Select(i => ((DayOfWeek)(((int)firstDay + i) % 7)).ToString()[..2]).ToList();

    /// <summary>Fraction of the day elapsed, 0..1.</summary>
    public static double DayProgress(DateTime now) => now.TimeOfDay.TotalSeconds / 86400.0;
}
