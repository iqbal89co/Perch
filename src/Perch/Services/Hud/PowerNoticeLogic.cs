namespace Perch.Services.Hud;

/// <summary>One reading of the battery: whether mains power is connected and the charge in whole percent.</summary>
public readonly record struct PowerSample(bool PluggedIn, int Percent);

/// <summary>A notice to show: a glyph and a short line of text.</summary>
public sealed record PowerNotice(string Glyph, string Text);

/// <summary>
/// Decides which power notice (if any) a new reading deserves, given the previous reading.
/// Pure: no WPF, no OS calls. The first reading only sets the baseline and never produces a notice.
/// </summary>
public sealed class PowerNoticeLogic
{
    public const int LowPercent = 20;
    public const int CriticalPercent = 10;

    public const string GlyphCharging = "";
    public const string GlyphOnBattery = "";
    public const string GlyphLow = "";
    public const string GlyphFull = "";

    private PowerSample? _last;
    private bool _lowNotified;
    private bool _criticalNotified;

    /// <summary>Returns the notice for this reading, or null when nothing is worth saying.</summary>
    public PowerNotice? Observe(PowerSample now)
    {
        var previous = _last;
        _last = now;

        if (previous is null)
        {
            // Baseline: thresholds already passed at start-up must not fire later in this discharge.
            if (!now.PluggedIn)
            {
                if (now.Percent <= LowPercent) _lowNotified = true;
                if (now.Percent <= CriticalPercent) _criticalNotified = true;
            }
            return null;
        }

        var prev = previous.Value;

        if (now.PluggedIn)
        {
            // Charging starts a new cycle: low-battery notices may fire again after the next unplug.
            _lowNotified = false;
            _criticalNotified = false;

            if (!prev.PluggedIn)
                return new PowerNotice(GlyphCharging, $"Charging · {now.Percent}%");
            if (prev.Percent < 100 && now.Percent >= 100)
                return new PowerNotice(GlyphFull, "Fully charged");
            return null;
        }

        if (now.Percent <= CriticalPercent && !_criticalNotified)
        {
            _criticalNotified = true;
            _lowNotified = true;
            return new PowerNotice(GlyphLow, $"Battery low · {now.Percent}%");
        }
        if (now.Percent <= LowPercent && !_lowNotified)
        {
            _lowNotified = true;
            return new PowerNotice(GlyphLow, $"Battery low · {now.Percent}%");
        }
        if (prev.PluggedIn)
            return new PowerNotice(GlyphOnBattery, $"On battery · {now.Percent}%");
        return null;
    }
}
