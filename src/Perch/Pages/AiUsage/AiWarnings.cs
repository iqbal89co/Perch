namespace Perch.Pages.AiUsage;

/// <summary>The notices to show and the notified state to save.</summary>
public sealed record AiWarningResult(IReadOnlyList<string> Messages, AiNotifiedState State);

/// <summary>
/// Decides which notices to post for a snapshot. Pure: the page posts the messages and saves the state.
/// Each window warns once: a Claude 5-hour window when its budget use passes the threshold, a Codex window when
/// its used percentage does. When a warned window resets, a "fresh" or "back" notice follows.
/// </summary>
public static class AiWarnings
{
    public const int MinNear = 50;
    public const int MaxNear = 95;

    public static AiWarningResult Evaluate(AiSnapshot snapshot, AiUsageSettings settings)
    {
        var messages = new List<string>();
        var state = settings.Notified with { };
        var near = Math.Clamp(settings.NearPercent, MinNear, MaxNear);
        var now = snapshot.NowUtc;

        // Claude Code. The reset notice comes first so a new window that is already past the threshold
        // in the same refresh still gets its own warning.
        if (state.ClaudeWarnedEndTicks is { } warnedEnd && now >= new DateTime(warnedEnd, DateTimeKind.Utc))
        {
            messages.Add("Claude Code: fresh 5-hour window");
            state.ClaudeWarnedEndTicks = null;
        }
        if (snapshot.Claude?.Current is { } block &&
            settings.ClaudeBlockBudgetTokens is { } budget && budget > 0)
        {
            var percent = block.ActiveTokens * 100.0 / budget;
            if (percent >= near && state.ClaudeWarnedEndTicks != block.EndUtc.Ticks)
            {
                messages.Add($"Claude Code: {Math.Round(percent):0}% of this window used");
                state.ClaudeWarnedEndTicks = block.EndUtc.Ticks;
            }
        }

        // Codex CLI: primary and secondary windows.
        state.CodexPrimaryResetTicks = CodexWindow(snapshot.Codex?.Primary, state.CodexPrimaryResetTicks, near, now, messages);
        state.CodexSecondaryResetTicks = CodexWindow(snapshot.Codex?.Secondary, state.CodexSecondaryResetTicks, near, now, messages);

        return new AiWarningResult(messages, state);
    }

    /// <summary>Returns the warned reset ticks for one Codex window after this refresh.</summary>
    private static long? CodexWindow(CodexWindowView? window, long? warnedReset, int near, DateTime now, List<string> messages)
    {
        if (warnedReset is { } warned && now >= new DateTime(warned, DateTimeKind.Utc))
        {
            messages.Add("Codex: limit is back");
            warnedReset = null;
        }

        if (window is { Expired: false, ResetsAtUtc: { } reset } && window.Percent >= near && warnedReset != reset.Ticks)
        {
            messages.Add($"Codex: {LowerFirst(window.Label)} limit at {Math.Round(window.Percent):0}%");
            warnedReset = reset.Ticks;
        }
        return warnedReset;
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
