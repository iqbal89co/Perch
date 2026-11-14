using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.Today;

/// <summary>Reference page: today's date, day progress and a browsable month calendar.</summary>
public partial class TodayPage : IslandPage
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateOnly _shownMonth = DateOnly.FromDateTime(DateTime.Today);

    public override string Id => "today";
    public override string Title => "Today";
    public override string Glyph => ""; // calendar
    public override int Order => 20;

    public TodayPage()
    {
        InitializeComponent();
        PrevButton.Click += (_, _) => { _shownMonth = _shownMonth.AddMonths(-1); RenderMonth(); };
        NextButton.Click += (_, _) => { _shownMonth = _shownMonth.AddMonths(1); RenderMonth(); };
        TodayButton.Click += (_, _) => { _shownMonth = DateOnly.FromDateTime(DateTime.Today); RenderMonth(); };
        _timer.Tick += (_, _) => RenderToday();
        foreach (var h in CalendarMath.WeekdayHeaders())
            Headers.Children.Add(new TextBlock { Text = h, Style = (Style)FindResource("PerchCaption"), HorizontalAlignment = HorizontalAlignment.Center });
        RenderToday();
        RenderMonth();
    }

    public override void OnShown()
    {
        _shownMonth = DateOnly.FromDateTime(DateTime.Today);
        RenderToday();
        RenderMonth();
        _timer.Start();
    }

    public override void OnHidden() => _timer.Stop();

    private void RenderToday()
    {
        var now = DateTime.Now;
        WeekdayText.Text = now.ToString("dddd").ToUpperInvariant();
        DayText.Text = now.Day.ToString();
        MonthText.Text = now.ToString("MMMM yyyy");
        WeekText.Text = $"Week {CalendarMath.IsoWeek(DateOnly.FromDateTime(now))} · day {now.DayOfYear} of {(DateTime.IsLeapYear(now.Year) ? 366 : 365)}";
        var progress = CalendarMath.DayProgress(now);
        DayBar.Width = Math.Max(4, 188 * progress);
        var left = TimeSpan.FromDays(1) - now.TimeOfDay;
        DayProgressText.Text = $"{progress:P0} of today gone · {(int)left.TotalHours}h {left.Minutes}m left";
    }

    private void RenderMonth()
    {
        MonthTitle.Text = _shownMonth.ToString("MMMM yyyy");
        Days.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var day in CalendarMath.MonthGrid(_shownMonth.Year, _shownMonth.Month))
        {
            var isToday = day == today;
            var inMonth = day.Month == _shownMonth.Month;
            var text = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty,
                isToday ? "IslandOnAccent" : inMonth ? "IslandForeground" : "IslandSubtle");
            if (!inMonth) text.Opacity = 0.55;
            var cell = new Border
            {
                Child = text,
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = day.ToString("dddd d MMMM yyyy"),
            };
            if (isToday) cell.SetResourceReference(Border.BackgroundProperty, "IslandAccent");
            Days.Children.Add(cell);
        }
    }
}
