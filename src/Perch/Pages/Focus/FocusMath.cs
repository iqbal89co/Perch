namespace Perch.Pages.Focus;

/// <summary>Text and geometry helpers for the Focus page. Pure, no WPF types.</summary>
public static class FocusMath
{
    /// <summary>mm:ss, rounding up so the display reads 25:00 at the start and 00:00 only at the end.</summary>
    public static string Clock(TimeSpan remaining)
    {
        var seconds = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public static string PhaseName(Phase phase) => phase switch
    {
        Phase.Focus => "Focus",
        Phase.ShortBreak => "Short break",
        Phase.LongBreak => "Long break",
        _ => "Ready",
    };

    public static string Headline(Phase phase) => phase switch
    {
        Phase.Focus => "Deep work",
        Phase.ShortBreak => "Stretch and breathe",
        Phase.LongBreak => "Step away for a while",
        _ => "Ready when you are",
    };

    /// <summary>Point on a circle, fraction 0 is the top and the ring runs clockwise.</summary>
    public static (double X, double Y) RingPoint(double fraction, double radius, double center)
    {
        var angle = fraction * 2 * Math.PI;
        return (center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
    }
}
