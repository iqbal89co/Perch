using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Core;
using Shapes = System.Windows.Shapes;

namespace Perch.Pages.Focus;

/// <summary>
/// Pomodoro timer. The countdown lives in <see cref="PomodoroEngine"/> and keeps running on the resting island
/// through the "focus" pill activity, even while another tab is open.
/// </summary>
public partial class FocusPage : IslandPage
{
    private const string PillKey = "focus";
    private const string TimerGlyph = "";
    private const string PauseGlyph = "";
    private const double RingRadius = 70;
    private const double RingCenter = 70;

    private readonly PomodoroEngine _engine = new(new PomodoroSettings());
    private readonly FocusLog _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _pillGlyph, _pillPhase, _pillClock;
    private (int Every, int Count) _dotsShown = (-1, -1);

    public override string Id => "focus";
    public override string Title => "Focus";
    public override string Glyph => TimerGlyph;
    public override int Order => 50;

    public FocusPage()
    {
        InitializeComponent();
        _log = new FocusLog(JsonStore.Load<Dictionary<string, FocusDay>>("focus"));
        _log.Prune(Today);

        _timer.Tick += (_, _) => OnTick();
        PrimaryButton.Click += (_, _) => OnPrimary();
        ResetButton.Click += (_, _) =>
        {
            SyncSettings();
            _engine.Reset();
            SyncTimer();
            Refresh();
        };
        SkipButton.Click += (_, _) =>
        {
            SyncSettings();
            _engine.Skip(DateTime.Now);
            SyncTimer();
            Refresh();
        };
        Refresh();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public override void OnShown()
    {
        Refresh();
        SyncTimer();
    }

    // The timer keeps running while the page is hidden (the pill needs it), so OnHidden does nothing.

    private void SyncSettings()
    {
        var ctx = IslandContext.Current;
        if (ctx is not null) _engine.Settings = ctx.Settings.Pomodoro;
    }

    /// <summary>The 1 s timer only runs while the engine runs; a paused or idle engine needs no ticks.</summary>
    private void SyncTimer()
    {
        if (_engine.IsRunning)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnPrimary()
    {
        SyncSettings();
        var now = DateTime.Now;
        if (_engine.IsRunning) _engine.Pause(now);
        else _engine.Start(now);
        SyncTimer();
        Refresh();
    }

    private void OnTick()
    {
        SyncSettings();
        var length = _engine.PhaseLength; // the phase that might end now; Tick moves on to the next one
        var finished = _engine.Tick(DateTime.Now);
        if (finished is { } phase) PhaseFinished(phase, length);
        SyncTimer();
        Refresh();
    }

    private void PhaseFinished(Phase phase, TimeSpan length)
    {
        if (phase == Phase.Focus)
        {
            _log.RecordSession(Today, (int)Math.Round(length.TotalMinutes));
            JsonStore.Save("focus", _log.Days);
            Ctx.Notify(TimerGlyph, "Focus done — take a break");
        }
        else
        {
            Ctx.Notify(TimerGlyph, "Break over — back to it");
        }

        if (Ctx.Settings.Sounds) SystemSounds.Asterisk.Play();
    }

    private void Refresh()
    {
        SyncSettings();
        var now = DateTime.Now;
        var phase = _engine.Current;
        var remaining = _engine.Remaining(now);
        var clock = FocusMath.Clock(remaining);
        var phaseName = FocusMath.PhaseName(phase);

        ClockText.Text = clock;
        PhaseText.Text = phaseName;
        HeadlineText.Text = FocusMath.Headline(phase);
        NextText.Text = $"Up next · {FocusMath.PhaseName(_engine.NextPhase)}";
        RingArc.Data = BuildArc(_engine.Progress(now));

        PrimaryButton.Content = _engine.IsRunning ? "Pause"
            : phase == Phase.Idle || remaining == _engine.PhaseLength ? "Start"
            : "Resume";
        ResetButton.IsEnabled = phase != Phase.Idle;
        SkipButton.IsEnabled = phase != Phase.Idle;

        RenderDots();

        var day = _log.ForDay(Today);
        TodayText.Text = $"Today: {FocusLog.SessionsText(day.Sessions)} · {FocusLog.FormatDuration(day.Minutes)}";

        PostPill(phaseName, clock);
    }

    /// <summary>One dot per focus in a cycle; filled for the ones done. Shows all filled during the long break.</summary>
    private void RenderDots()
    {
        var every = Math.Max(1, _engine.Settings.LongEvery);
        var count = _engine.CompletedFocusCount;
        var done = count % every;
        if (done == 0 && count > 0) done = every;
        if (_dotsShown == (every, count) && DotsPanel.Children.Count == every) return;
        _dotsShown = (every, count);

        DotsPanel.Children.Clear();
        for (var i = 0; i < every; i++)
        {
            var dot = new Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 7, 0) };
            dot.SetResourceReference(Shapes.Shape.FillProperty, i < done ? "IslandAccent" : "IslandCardHover");
            DotsPanel.Children.Add(dot);
        }
        DotsText.Text = done >= every ? "Long break" : $"{every - done} more to long break";
    }

    /// <summary>Arc from the top, clockwise, covering the elapsed fraction. Null when nothing has elapsed.</summary>
    private static Geometry? BuildArc(double progress)
    {
        if (progress <= 0.001) return null;
        var fraction = Math.Min(progress, 0.9995); // a full circle cannot be one arc
        var start = FocusMath.RingPoint(0, RingRadius, RingCenter);
        var end = FocusMath.RingPoint(fraction, RingRadius, RingCenter);
        var figure = new PathFigure { StartPoint = new Point(start.X, start.Y), IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            new Point(end.X, end.Y), new Size(RingRadius, RingRadius), 0,
            fraction > 0.5, SweepDirection.Clockwise, isStroked: true));
        return new PathGeometry(new[] { figure });
    }

    /// <summary>
    /// Keeps the "focus" pill on the resting island. It is posted once per countdown and then updated in place
    /// each second (no new visuals, no re-post), so the island does not redraw the whole pill every tick.
    /// </summary>
    private void PostPill(string phaseName, string clock)
    {
        if (IslandContext.Current is not { } ctx) return;
        if (_engine.Current == Phase.Idle)
        {
            ctx.Activities.Remove(PillKey);
            return;
        }

        if (_pillClock != null && ctx.Activities.Contains(PillKey))
        {
            _pillGlyph!.Text = _engine.IsRunning ? TimerGlyph : PauseGlyph;
            _pillPhase!.Text = phaseName;
            _pillClock.Text = clock;
            return;
        }

        _pillGlyph = new TextBlock
        {
            Text = _engine.IsRunning ? TimerGlyph : PauseGlyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pillGlyph.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");

        _pillPhase = new TextBlock
        {
            Text = phaseName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pillPhase.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

        _pillClock = new TextBlock
        {
            Text = clock,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(_pillClock, FontNumeralAlignment.Tabular);
        _pillClock.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

        ctx.Activities.Post(new PillActivity
        {
            Key = PillKey,
            Priority = 60,
            Width = 190,
            Leading = _pillGlyph,
            Center = _pillPhase,
            Trailing = _pillClock,
        });
    }
}
