using Perch.Core;
using Perch.Pages.Focus;

namespace Perch.Tests;

public class PomodoroEngineTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 9, 0, 0);

    private static PomodoroSettings Settings(bool autoBreaks = true, bool autoFocus = false) => new()
    {
        FocusMin = 25,
        ShortBreakMin = 5,
        LongBreakMin = 15,
        LongEvery = 4,
        AutoStartBreaks = autoBreaks,
        AutoStartFocus = autoFocus,
    };

    [Fact]
    public void Start_from_idle_runs_a_full_focus()
    {
        var e = new PomodoroEngine(Settings());
        e.Start(T0);
        Assert.Equal(Phase.Focus, e.Current);
        Assert.True(e.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(10), e.Remaining(T0.AddSeconds(10)));
    }

    [Fact]
    public void Pause_and_resume_keep_the_remaining_time()
    {
        var e = new PomodoroEngine(Settings());
        e.Start(T0);
        e.Pause(T0.AddMinutes(1));
        Assert.False(e.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(24), e.Remaining(T0.AddMinutes(10))); // time spent paused does not count
        e.Resume(T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(23), e.Remaining(T0.AddMinutes(11)));
    }

    [Fact]
    public void Completing_a_focus_starts_a_short_break()
    {
        var e = new PomodoroEngine(Settings());
        e.Start(T0);
        Assert.Null(e.Tick(T0.AddMinutes(24)));
        Assert.Equal((Phase?)Phase.Focus, e.Tick(T0.AddMinutes(25)));
        Assert.Equal(Phase.ShortBreak, e.Current);
        Assert.True(e.IsRunning);
        Assert.Equal(1, e.CompletedFocusCount);
        Assert.Equal(TimeSpan.FromMinutes(5), e.Remaining(T0.AddMinutes(25)));
    }

    [Fact]
    public void Every_fourth_focus_leads_to_a_long_break_and_the_cycle_restarts()
    {
        var e = new PomodoroEngine(Settings(autoBreaks: true, autoFocus: true));
        var now = T0;
        e.Start(now);
        for (var i = 0; i < 3; i++)
        {
            now = now.AddMinutes(25);
            Assert.Equal((Phase?)Phase.Focus, e.Tick(now));
            Assert.Equal(Phase.ShortBreak, e.Current);
            now = now.AddMinutes(5);
            Assert.Equal((Phase?)Phase.ShortBreak, e.Tick(now));
            Assert.Equal(Phase.Focus, e.Current);
        }

        now = now.AddMinutes(25);
        Assert.Equal((Phase?)Phase.Focus, e.Tick(now));
        Assert.Equal(Phase.LongBreak, e.Current);
        Assert.Equal(4, e.CompletedFocusCount);
        Assert.Equal(TimeSpan.FromMinutes(15), e.Remaining(now));

        now = now.AddMinutes(15);
        Assert.Equal((Phase?)Phase.LongBreak, e.Tick(now));
        Assert.Equal(Phase.Focus, e.Current);
        Assert.Equal(0, e.CompletedFocusCount);
    }

    [Fact]
    public void Next_phase_is_the_long_break_once_three_focuses_are_done()
    {
        var e = new PomodoroEngine(Settings(autoFocus: true));
        e.Start(T0);
        Assert.Equal(Phase.ShortBreak, e.NextPhase);
        var now = T0;
        for (var i = 0; i < 3; i++)
        {
            now = now.AddMinutes(25);
            e.Tick(now);
            now = now.AddMinutes(5);
            e.Tick(now);
        }
        Assert.Equal(Phase.Focus, e.Current);
        Assert.Equal(Phase.LongBreak, e.NextPhase);
    }

    [Fact]
    public void With_auto_start_off_the_next_phase_waits_paused_at_full_length()
    {
        var e = new PomodoroEngine(Settings(autoBreaks: false));
        e.Start(T0);
        e.Tick(T0.AddMinutes(25));
        Assert.Equal(Phase.ShortBreak, e.Current);
        Assert.False(e.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), e.Remaining(T0.AddHours(1)));
        Assert.Null(e.Tick(T0.AddHours(2)));

        e.Resume(T0.AddHours(1));
        Assert.True(e.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), e.Remaining(T0.AddHours(1)));
    }

    [Fact]
    public void Skipping_a_focus_moves_on_without_counting_it()
    {
        var e = new PomodoroEngine(Settings());
        e.Start(T0);
        e.Skip(T0.AddMinutes(3));
        Assert.Equal(Phase.ShortBreak, e.Current);
        Assert.Equal(0, e.CompletedFocusCount);
        Assert.True(e.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), e.Remaining(T0.AddMinutes(3)));
    }

    [Fact]
    public void Skipping_a_break_with_auto_start_off_waits_for_the_focus()
    {
        var e = new PomodoroEngine(Settings(autoBreaks: true, autoFocus: false));
        e.Start(T0);
        e.Skip(T0.AddMinutes(1));
        e.Skip(T0.AddMinutes(2));
        Assert.Equal(Phase.Focus, e.Current);
        Assert.False(e.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25), e.Remaining(T0.AddMinutes(2)));
    }

    [Fact]
    public void Reset_returns_to_idle_and_clears_the_cycle()
    {
        var e = new PomodoroEngine(Settings());
        e.Start(T0);
        e.Tick(T0.AddMinutes(25));
        e.Reset();
        Assert.Equal(Phase.Idle, e.Current);
        Assert.False(e.IsRunning);
        Assert.Equal(0, e.CompletedFocusCount);
        Assert.Equal(TimeSpan.FromMinutes(25), e.Remaining(T0.AddHours(1)));
        Assert.Null(e.Tick(T0.AddHours(2)));
    }

    [Fact]
    public void A_two_hour_jump_completes_one_phase_and_never_goes_negative()
    {
        var e = new PomodoroEngine(Settings(autoBreaks: true, autoFocus: true));
        e.Start(T0);
        var wake = T0.AddHours(2);

        Assert.Equal((Phase?)Phase.Focus, e.Tick(wake));
        Assert.Equal(Phase.ShortBreak, e.Current);
        Assert.True(e.IsRunning);
        Assert.Equal(1, e.CompletedFocusCount);
        Assert.Equal(TimeSpan.FromMinutes(5), e.Remaining(wake));

        Assert.Equal(TimeSpan.Zero, e.Remaining(wake.AddHours(1))); // overdue, clamped, not negative
        Assert.Equal((Phase?)Phase.ShortBreak, e.Tick(wake.AddHours(1)));
        Assert.Equal(Phase.Focus, e.Current);
        Assert.Equal(TimeSpan.FromMinutes(25), e.Remaining(wake.AddHours(1)));
    }

    [Fact]
    public void A_small_late_tick_is_carried_into_the_next_phase()
    {
        var e = new PomodoroEngine(Settings());
        e.Start(T0);
        var late = T0.AddMinutes(25).AddSeconds(2);
        e.Tick(late);
        Assert.Equal(TimeSpan.FromSeconds(298), e.Remaining(late));
    }

    [Fact]
    public void Progress_runs_from_zero_to_one_across_the_phase()
    {
        var e = new PomodoroEngine(Settings());
        Assert.Equal(0, e.Progress(T0));
        e.Start(T0);
        Assert.InRange(e.Progress(T0.AddMinutes(12.5)), 0.499, 0.501);
        Assert.Equal(1, e.Progress(T0.AddHours(1)));
    }
}

public class FocusLogTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    [Fact]
    public void Sessions_and_minutes_accumulate_per_day()
    {
        var log = new FocusLog();
        log.RecordSession(Today, 25);
        log.RecordSession(Today, 25);
        var day = log.ForDay(Today);
        Assert.Equal(2, day.Sessions);
        Assert.Equal(50, day.Minutes);
        Assert.Equal(0, log.ForDay(Today.AddDays(1)).Sessions);
        Assert.True(log.Days.ContainsKey("2026-11-14"));
    }

    [Fact]
    public void Days_older_than_thirty_are_pruned()
    {
        var log = new FocusLog();
        log.Days["2026-10-15"] = new FocusDay { Sessions = 1, Minutes = 25 }; // 30 days back: gone
        log.Days["2026-10-16"] = new FocusDay { Sessions = 1, Minutes = 25 }; // 29 days back: kept
        log.Days["not-a-date"] = new FocusDay { Sessions = 9, Minutes = 9 };
        log.RecordSession(Today, 25);
        Assert.False(log.Days.ContainsKey("2026-10-15"));
        Assert.False(log.Days.ContainsKey("not-a-date"));
        Assert.True(log.Days.ContainsKey("2026-10-16"));
    }

    [Theory]
    [InlineData(75, "1h 15m")]
    [InlineData(45, "45m")]
    [InlineData(120, "2h")]
    [InlineData(0, "0m")]
    public void Durations_read_naturally(int minutes, string expected)
    {
        Assert.Equal(expected, FocusLog.FormatDuration(minutes));
    }

    [Fact]
    public void Session_count_text_is_pluralised()
    {
        Assert.Equal("1 focus session", FocusLog.SessionsText(1));
        Assert.Equal("3 focus sessions", FocusLog.SessionsText(3));
    }
}

public class FocusMathTests
{
    [Fact]
    public void Clock_reads_mm_ss_and_rounds_up_partial_seconds()
    {
        Assert.Equal("25:00", FocusMath.Clock(TimeSpan.FromMinutes(25)));
        Assert.Equal("00:01", FocusMath.Clock(TimeSpan.FromSeconds(0.4)));
        Assert.Equal("00:00", FocusMath.Clock(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Ring_starts_at_the_top_and_runs_clockwise()
    {
        var quarter = FocusMath.RingPoint(0.25, 70, 70);
        Assert.InRange(quarter.X, 139.99, 140.01);
        Assert.InRange(quarter.Y, 69.99, 70.01);
        var top = FocusMath.RingPoint(0, 70, 70);
        Assert.InRange(top.Y, -0.01, 0.01);
    }
}
