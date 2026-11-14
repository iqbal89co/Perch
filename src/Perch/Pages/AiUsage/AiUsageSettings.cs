namespace Perch.Pages.AiUsage;

/// <summary>Persisted as data/ai_usage.json. Owned by the AI page only.</summary>
public sealed class AiUsageSettings
{
    /// <summary>Optional Claude Code budget per 5-hour window (input + output tokens). Null shows no bar.</summary>
    public long? ClaudeBlockBudgetTokens { get; set; }

    /// <summary>Post a notice when a limit is near. Off by default. Also keeps the page refreshing while hidden.</summary>
    public bool WarnNearLimit { get; set; } = false;

    /// <summary>Usage percentage that counts as "near" (50..95).</summary>
    public int NearPercent { get; set; } = 80;

    /// <summary>Which windows were already warned about, so each one warns once.</summary>
    public AiNotifiedState Notified { get; set; } = new();
}

/// <summary>Identifies the windows already warned about: a Claude window by its end, Codex windows by reset time.</summary>
public sealed record AiNotifiedState
{
    public long? ClaudeWarnedEndTicks { get; set; }
    public long? CodexPrimaryResetTicks { get; set; }
    public long? CodexSecondaryResetTicks { get; set; }
}
