using Perch.Core;

namespace Perch.Pages.Focus;

public enum Phase { Idle, Focus, ShortBreak, LongBreak }

/// <summary>
/// Pomodoro state machine. Pure: it never reads the clock, every call takes "now" from the caller.
/// A running phase is kept as an end timestamp, so after sleep or a clock jump the remaining time is right.
/// </summary>
public sealed class PomodoroEngine
{
    private DateTime _endsAt;
    private TimeSpan _remaining;
    private TimeSpan _phaseLength;

    public PomodoroEngine(PomodoroSettings settings) => Settings = settings;

    /// <summary>Read whenever a phase starts. The length of a running phase is fixed when it starts.</summary>
    public PomodoroSettings Settings { get; set; }

    public Phase Current { get; private set; } = Phase.Idle;
    public bool IsRunning { get; private set; }

    /// <summary>Focus sessions completed in the current cycle. Reset after a long break.</summary>
    public int CompletedFocusCount { get; private set; }

    /// <summary>Full length of the current phase; while Idle, the length the next focus will get.</summary>
    public TimeSpan PhaseLength => Current == Phase.Idle ? LengthOf(Phase.Focus) : _phaseLength;

    /// <summary>The phase that follows the current one (what Tick or Skip would move to).</summary>
    public Phase NextPhase => Current == Phase.Focus ? BreakAfter(CompletedFocusCount + 1) : Phase.Focus;

    public TimeSpan Remaining(DateTime now)
    {
        if (Current == Phase.Idle) return PhaseLength;
        if (!IsRunning) return _remaining;
        var left = _endsAt - now;
        if (left < TimeSpan.Zero) return TimeSpan.Zero;
        return left > PhaseLength ? PhaseLength : left;
    }

    /// <summary>How far through the current phase we are, 0 to 1.</summary>
    public double Progress(DateTime now)
    {
        if (Current == Phase.Idle || PhaseLength <= TimeSpan.Zero) return 0;
        return Math.Clamp(1 - Remaining(now).TotalSeconds / PhaseLength.TotalSeconds, 0, 1);
    }

    /// <summary>From Idle starts a focus; when paused or waiting, resumes the current phase.</summary>
    public void Start(DateTime now)
    {
        if (IsRunning) return;
        if (Current == Phase.Idle) Enter(Phase.Focus, now, anchor: null, run: true);
        else Resume(now);
    }

    public void Pause(DateTime now)
    {
        if (!IsRunning) return;
        _remaining = Remaining(now);
        IsRunning = false;
    }

    public void Resume(DateTime now)
    {
        if (IsRunning || Current == Phase.Idle) return;
        _endsAt = now + _remaining;
        IsRunning = true;
    }

    /// <summary>Back to Idle. Nothing runs and the cycle starts over.</summary>
    public void Reset()
    {
        Current = Phase.Idle;
        IsRunning = false;
        _remaining = TimeSpan.Zero;
        _phaseLength = TimeSpan.Zero;
        CompletedFocusCount = 0;
    }

    /// <summary>Ends the current phase now and moves on. A skipped focus is not counted as completed.</summary>
    public void Skip(DateTime now)
    {
        if (Current == Phase.Idle) return;
        var next = Advance(Current, completed: false);
        Enter(next, now, anchor: null, run: AutoStarts(next));
    }

    /// <summary>
    /// Call about once a second. If the current phase has ended, moves to the next one and returns the
    /// phase that completed; otherwise returns null. One completion per call, so a long gap (sleep) never
    /// replays a pile of phases. The next phase is anchored to the old end time, so small tick delays do not
    /// accumulate, unless that would already be in the past, in which case it starts at now.
    /// </summary>
    public Phase? Tick(DateTime now)
    {
        if (!IsRunning || now < _endsAt) return null;
        var finished = Current;
        var anchor = _endsAt;
        var next = Advance(finished, completed: true);
        Enter(next, now, anchor, run: AutoStarts(next));
        return finished;
    }

    /// <summary>
    /// Works out the phase after <paramref name="finished"/> and updates the cycle count. Only completed focuses
    /// count. A finished (or skipped) long break closes the cycle.
    /// </summary>
    private Phase Advance(Phase finished, bool completed)
    {
        switch (finished)
        {
            case Phase.Focus:
                if (completed)
                {
                    CompletedFocusCount++;
                    return BreakAfter(CompletedFocusCount);
                }
                return BreakAfter(CompletedFocusCount + 1);
            case Phase.LongBreak:
                CompletedFocusCount = 0;
                return Phase.Focus;
            default:
                return Phase.Focus;
        }
    }

    private void Enter(Phase phase, DateTime now, DateTime? anchor, bool run)
    {
        Current = phase;
        _phaseLength = LengthOf(phase);
        _remaining = _phaseLength;
        IsRunning = run;
        if (!run) return;
        var end = (anchor ?? now) + _phaseLength;
        _endsAt = end <= now ? now + _phaseLength : end;
    }

    private Phase BreakAfter(int completedFocuses) =>
        completedFocuses % Math.Max(1, Settings.LongEvery) == 0 ? Phase.LongBreak : Phase.ShortBreak;

    private bool AutoStarts(Phase phase) => phase == Phase.Focus ? Settings.AutoStartFocus : Settings.AutoStartBreaks;

    private TimeSpan LengthOf(Phase phase)
    {
        var minutes = phase switch
        {
            Phase.ShortBreak => Settings.ShortBreakMin,
            Phase.LongBreak => Settings.LongBreakMin,
            _ => Settings.FocusMin,
        };
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }
}
