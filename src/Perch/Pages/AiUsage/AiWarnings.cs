namespace Perch.Pages.AiUsage;

/// <summary>The notices to show and the notified state to save.</summary>
public sealed record AiWarningResult(IReadOnlyList<string> Messages, AiNotifiedState State);

/// <summary>
/// Decides which notices to post for a snapshot. Pure: the page posts the messages and saves the state.
/// With Anthropic's plan figures (<paramref name="plan"/> OK) the percentages come from there, exactly as /usage
/// shows them; otherwise each figure needs a budget. Each figure warns once when its use passes the threshold. The 5-hour window warns again
/// in the next window (with a "fresh window" notice when the warned one ends); the rolling weekly figures warn
/// again only after dropping back under the threshold.
/// </summary>
public static class AiWarnings
{
    public const int MinNear = 50;
    public const int MaxNear = 95;

    public static AiWarningResult Evaluate(AiSnapshot snapshot, AiUsageSettings settings, PlanUsage? plan = null)
    {
        var limits = plan?.Status == PlanStatus.Ok ? plan.Limits : Array.Empty<PlanLimit>();
        var session = limits.FirstOrDefault(l => l.IsSession);
        var weekly = limits.FirstOrDefault(l => l.IsWeeklyAll);
        var fable = limits.FirstOrDefault(l => l.IsFable);
        var messages = new List<string>();
        var state = settings.Notified with { };
        var near = Math.Clamp(settings.NearPercent, MinNear, MaxNear);
        var now = snapshot.NowUtc;
        var claude = snapshot.Claude;

        // 5-hour window. The reset notice comes first so a new window that is already past the threshold
        // in the same refresh still gets its own warning.
        if (state.ClaudeWarnedEndTicks is { } warnedEnd && now >= new DateTime(warnedEnd, DateTimeKind.Utc))
        {
            messages.Add("Fresh 5-hour window");
            state.ClaudeWarnedEndTicks = null;
        }
        var (p5, windowEnd) = session != null
            ? (session.Percent, session.ResetsAtUtc ?? now.AddHours(5))
            : claude?.Current is { } block
                ? (Percent(block.ActiveTokens, settings.ClaudeBlockBudgetTokens), block.EndUtc)
                : ((double?)null, DateTime.MinValue);
        if (p5 is { } p && p >= near && state.ClaudeWarnedEndTicks != windowEnd.Ticks)
        {
            messages.Add($"Usage 5h at {Math.Round(p):0}%");
            state.ClaudeWarnedEndTicks = windowEnd.Ticks;
        }

        state.WeeklyWarned = Rolling("Usage weekly",
            weekly?.Percent ?? Percent(claude?.Week.Tokens ?? 0, settings.WeeklyBudgetTokens), state.WeeklyWarned, near, messages);
        state.FableWarned = Rolling("Usage Fable",
            fable?.Percent ?? Percent(claude?.FableWeek.Tokens ?? 0, settings.FableBudgetTokens), state.FableWarned, near, messages);

        return new AiWarningResult(messages, state);
    }

    /// <summary>Percent of a budget used, or null without a budget.</summary>
    public static double? Percent(long tokens, long? budget) =>
        budget is { } b && b > 0 ? tokens * 100.0 / b : null;

    /// <summary>Returns the warned flag for one rolling figure after this refresh.</summary>
    private static bool Rolling(string name, double? used, bool warned, int near, List<string> messages)
    {
        if (used is not { } percent || percent < near) return false;
        if (!warned) messages.Add($"{name} at {Math.Round(percent):0}%");
        return true;
    }
}
