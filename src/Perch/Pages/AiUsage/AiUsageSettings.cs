namespace Perch.Pages.AiUsage;

/// <summary>Persisted as data/ai_usage.json. Owned by the AI page only.</summary>
public sealed class AiUsageSettings
{
    /// <summary>Optional budget per 5-hour window (input + output tokens). Null shows no bar.</summary>
    public long? ClaudeBlockBudgetTokens { get; set; }

    /// <summary>Optional budget for the last 7 days, all models. Null shows no bar.</summary>
    public long? WeeklyBudgetTokens { get; set; }

    /// <summary>Optional budget for the last 7 days of Fable models. Null shows no bar.</summary>
    public long? FableBudgetTokens { get; set; }

    /// <summary>Post a notice when a limit is near. Off by default. Also keeps the page refreshing while hidden.</summary>
    public bool WarnNearLimit { get; set; } = false;

    /// <summary>Usage percentage that counts as "near" (50..95).</summary>
    public int NearPercent { get; set; } = 80;

    /// <summary>Which figures were already warned about, so each warns once.</summary>
    public AiNotifiedState Notified { get; set; } = new();
}

/// <summary>
/// What was already warned about: the 5-hour window by its end time; the weekly figures by a flag that clears
/// once they drop back under the threshold (they roll, so they have no single reset time).
/// </summary>
public sealed record AiNotifiedState
{
    public long? ClaudeWarnedEndTicks { get; set; }
    public bool WeeklyWarned { get; set; }
    public bool FableWarned { get; set; }
}
