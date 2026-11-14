using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.AiUsage;

/// <summary>
/// How much of an AI coding plan is used, from Claude Code and Codex CLI's own local logs. Only token counts,
/// times and rate-limit percentages are read. The scan runs on a background task. The page refreshes every
/// 30 s while shown, and every 5 min while hidden only when near-limit warnings are on.
/// </summary>
public partial class AiUsagePage : IslandPage
{
    /// <summary>Segoe Fluent glyph for the tab and the notices. Chosen from rendered candidates.</summary>
    private const string TabGlyph = "";
    private const string GearGlyph = "";
    private const string BackGlyph = "";
    private const double BarHeight = 30;

    private static readonly TimeSpan ShownRefreshEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HiddenRefreshEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShownTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HiddenTick = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer = new();
    private readonly AiUsageService _service = new();
    private AiUsageSettings _settings;
    private AiSnapshot? _snapshot;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private bool _busy;
    private bool _shown;
    private bool _settingsOpen;

    public override string Id => "ai";
    public override string Title => "AI";
    public override string Glyph => TabGlyph;
    public override int Order => 52;

    private int NearPercent => Math.Clamp(_settings.NearPercent, AiWarnings.MinNear, AiWarnings.MaxNear);

    public AiUsagePage()
    {
        InitializeComponent();
        _settings = JsonStore.Load<AiUsageSettings>("ai_usage");
        _timer.Tick += (_, _) => OnTick();

        SettingsButton.Click += (_, _) => ToggleSettings();
        RescanButton.Click += (_, _) => Refresh(rescan: true);
        BudgetBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitBudget();
            e.Handled = true;
        };
        BudgetBox.LostFocus += (_, _) => CommitBudget();
        WarnToggle.Click += (_, _) =>
        {
            _settings.WarnNearLimit = WarnToggle.IsChecked == true;
            Save();
            ApplyTimer();
        };
        NearLowerButton.Click += (_, _) => ChangeNearPercent(-5);
        NearHigherButton.Click += (_, _) => ChangeNearPercent(5);

        LoadSettingsControls();
        RenderHeader();
        // With warnings on, scan once at startup so a near-limit window can warn even if the tab is never opened.
        if (_settings.WarnNearLimit)
        {
            ApplyTimer();
            Refresh(rescan: false);
        }
    }

    public override void OnShown()
    {
        _shown = true;
        ApplyTimer();
        RenderHeader();
        if (DateTime.UtcNow - _lastScanUtc >= ShownRefreshEvery) Refresh(rescan: false);
    }

    public override void OnHidden()
    {
        _shown = false;
        ApplyTimer();
    }

    /// <summary>The timer runs while shown, or while hidden with warnings on; otherwise it stops.</summary>
    private void ApplyTimer()
    {
        var wanted = _shown ? ShownTick : _settings.WarnNearLimit ? HiddenTick : (TimeSpan?)null;
        if (wanted is not { } interval)
        {
            _timer.Stop();
            return;
        }
        _timer.Interval = interval;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick()
    {
        RenderHeader();
        var every = _shown ? ShownRefreshEvery : HiddenRefreshEvery;
        if (DateTime.UtcNow - _lastScanUtc >= every) Refresh(rescan: false);
    }

    /// <summary>Scans on a background task. Ignored while a scan is running.</summary>
    private async void Refresh(bool rescan)
    {
        if (_busy) return;
        _busy = true;
        RenderHeader();
        try
        {
            var snapshot = await Task.Run(() => _service.Build(DateTime.UtcNow, rescan));
            _snapshot = snapshot;
            Render();
            EvaluateWarnings();
        }
        catch (Exception ex)
        {
            Log.Error("ai: scan failed", ex);
        }
        finally
        {
            _busy = false;
            _lastScanUtc = DateTime.UtcNow;
            RenderHeader();
        }
    }

    private void EvaluateWarnings()
    {
        if (!_settings.WarnNearLimit || _snapshot is not { } snapshot) return;
        var result = AiWarnings.Evaluate(snapshot, _settings);
        if (!result.State.Equals(_settings.Notified))
        {
            _settings.Notified = result.State;
            Save();
        }
        foreach (var message in result.Messages)
            Ctx.Notify(TabGlyph, message, TimeSpan.FromSeconds(6), key: "ai");
    }

    private void ToggleSettings()
    {
        _settingsOpen = !_settingsOpen;
        SettingsButton.Content = _settingsOpen ? BackGlyph : GearGlyph;
        SettingsButton.ToolTip = _settingsOpen ? "Back" : "AI settings";
        if (_settingsOpen) LoadSettingsControls();
        RenderHeader();
        Render();
    }

    private void LoadSettingsControls()
    {
        BudgetBox.Text = _settings.ClaudeBlockBudgetTokens is { } budget ? AiFormat.Tokens(budget) : "";
        BudgetHint.Visibility = Visibility.Collapsed;
        WarnToggle.IsChecked = _settings.WarnNearLimit;
        NearText.Text = $"{NearPercent}%";
    }

    private void CommitBudget()
    {
        if (!AiFormat.TryParseBudget(BudgetBox.Text, out var budget))
        {
            BudgetHint.Visibility = Visibility.Visible;
            return;
        }
        BudgetHint.Visibility = Visibility.Collapsed;
        if (budget == _settings.ClaudeBlockBudgetTokens) return;

        _settings.ClaudeBlockBudgetTokens = budget;
        Save();
        BudgetBox.Text = budget is { } b ? AiFormat.Tokens(b) : "";
        Render();
        EvaluateWarnings();
    }

    private void ChangeNearPercent(int delta)
    {
        _settings.NearPercent = Math.Clamp(NearPercent + delta, AiWarnings.MinNear, AiWarnings.MaxNear);
        Save();
        NearText.Text = $"{NearPercent}%";
        Render();
        EvaluateWarnings();
    }

    private void Save() => JsonStore.Save("ai_usage", _settings);

    private void RenderHeader()
    {
        if (_settingsOpen)
        {
            HeaderText.Text = "Settings";
            return;
        }
        if (_busy)
        {
            HeaderText.Text = _snapshot == null ? "Reading logs…" : "Updating…";
            return;
        }
        HeaderText.Text = _lastScanUtc == DateTime.MinValue
            ? ""
            : "Updated " + AiFormat.Ago(DateTime.UtcNow - _lastScanUtc);
    }

    private void Render()
    {
        CardHost.Children.Clear();
        var snapshot = _snapshot;

        var cards = new List<Border>();
        if (snapshot?.Claude is { } claude) cards.Add(BuildClaudeCard(claude, snapshot.NowUtc));
        if (snapshot?.Codex is { } codex) cards.Add(BuildCodexCard(codex, snapshot.NowUtc));
        CardHost.Columns = Math.Max(1, cards.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            cards[i].Margin = cards.Count > 1 && i == 0 ? new Thickness(0, 0, 8, 0) : new Thickness(0);
            CardHost.Children.Add(cards[i]);
        }

        CardHost.Visibility = _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = !_settingsOpen && snapshot != null && cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildClaudeCard(ClaudeSummary summary, DateTime nowUtc)
    {
        var block = summary.Current;
        var stack = new StackPanel();
        stack.Children.Add(Label("Claude Code", "PerchTitle", 14));
        stack.Children.Add(Caption("This 5-hour window", margin: new Thickness(0, 1, 0, 0)));

        var big = new TextBlock { Style = (Style)FindResource("PerchBody"), Margin = new Thickness(0, 6, 0, 0) };
        big.Inlines.Add(new Run(AiFormat.Tokens(block?.ActiveTokens ?? 0)) { FontSize = 26, FontWeight = FontWeights.Bold });
        big.Inlines.Add(RunWithResource(" tokens", 12, "IslandSubtle"));
        stack.Children.Add(big);

        stack.Children.Add(Caption(block != null
            ? $"resets in {AiFormat.Remaining(block.EndUtc - nowUtc)} · {AiFormat.Plural(block.Messages, "message")} · {AiFormat.Tokens(block.CacheTokens)} cached"
            : "No activity in the last 5 hours"));

        if (_settings.ClaudeBlockBudgetTokens is { } budget && budget > 0)
        {
            var fraction = (double)(block?.ActiveTokens ?? 0) / budget;
            stack.Children.Add(Bar(Math.Min(1, fraction), fraction * 100 >= NearPercent, new Thickness(0, 6, 0, 0)));
            stack.Children.Add(Caption($"{AiFormat.Percent(fraction)} of {AiFormat.Tokens(budget)} budget"));
        }

        if (block?.TopModel is { } model)
            stack.Children.Add(Caption($"Mostly {model}", margin: new Thickness(0, 4, 0, 0)));

        stack.Children.Add(Caption("LAST 7 DAYS", size: 10, semibold: true, margin: new Thickness(0, 8, 0, 2)));
        stack.Children.Add(WeekChart(summary.Week));

        return Card(stack);
    }

    private Border BuildCodexCard(CodexSummary summary, DateTime nowUtc)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label("Codex CLI", "PerchTitle", 14));
        stack.Children.Add(Caption(summary.LimitsSeenUtc is { } seen
            ? "Limits as of " + AiFormat.Ago(nowUtc - seen)
            : "No limit data yet", margin: new Thickness(0, 1, 0, 0)));

        foreach (var window in new[] { summary.Primary, summary.Secondary })
        {
            if (window == null) continue;
            var percent = window.Percent / 100.0;
            var danger = window.Percent >= NearPercent;

            var header = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var value = new TextBlock
            {
                Text = $"{Math.Round(window.Percent):0}%",
                Style = (Style)FindResource("PerchBody"),
                FontWeight = FontWeights.SemiBold,
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, danger ? "IslandDanger" : "IslandAccent");
            DockPanel.SetDock(value, Dock.Right);
            header.Children.Add(value);
            header.Children.Add(new TextBlock { Text = window.Label, Style = (Style)FindResource("PerchBody") });
            stack.Children.Add(header);

            stack.Children.Add(Bar(percent, danger, new Thickness(0, 5, 0, 0)));
            stack.Children.Add(Caption(ResetText(window, nowUtc)));
        }

        stack.Children.Add(Caption($"Today {AiFormat.Tokens(summary.TodayTokens)} tokens", margin: new Thickness(0, 10, 0, 0)));
        return Card(stack);
    }

    private string ResetText(CodexWindowView window, DateTime nowUtc)
    {
        if (window.Expired) return "window reset";
        if (window.ResetsAtUtc is not { } reset) return "";
        return "resets " + AiFormat.ResetClock(reset, nowUtc, TimeZoneInfo.Local, Ctx.Settings.Use24h);
    }

    /// <summary>Seven thin bars, oldest first, with the weekday initial under each. Today is accented.</summary>
    private UIElement WeekChart(long[] days)
    {
        var grid = new UniformGrid { Columns = days.Length, Height = BarHeight + 14 };
        var max = days.Length == 0 ? 0 : days.Max();
        var today = DateTime.Today;
        for (var i = 0; i < days.Length; i++)
        {
            var date = today.AddDays(i - (days.Length - 1));
            var height = max <= 0 ? 2 : Math.Max(2, days[i] * BarHeight / max);

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var bar = new Border
            {
                Width = 12,
                Height = height,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = $"{date:ddd d MMM}: {AiFormat.Tokens(days[i])} tokens",
            };
            bar.SetResourceReference(Border.BackgroundProperty, i == days.Length - 1 ? "IslandAccent" : "IslandAccentSoft");
            column.Children.Add(bar);

            var initial = new TextBlock
            {
                Text = CultureInfo.CurrentCulture.DateTimeFormat.GetShortestDayName(date.DayOfWeek)[..1],
                Style = (Style)FindResource("PerchCaption"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetRow(initial, 1);
            column.Children.Add(initial);
            grid.Children.Add(column);
        }
        return grid;
    }

    /// <summary>A thin rounded bar: the track is IslandCardHover, the fill is accent (or danger when near the limit).</summary>
    private UIElement Bar(double fraction, bool danger, Thickness margin)
    {
        var f = Math.Clamp(fraction, 0, 1);
        var grid = new Grid { Height = 5, Margin = margin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(f, 0.0001), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - f, 0.0001), GridUnitType.Star) });

        var track = new Border { CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");
        Grid.SetColumnSpan(track, 2);

        var fill = new Border { CornerRadius = new CornerRadius(2.5) };
        fill.SetResourceReference(Border.BackgroundProperty, danger ? "IslandDanger" : "IslandAccent");

        grid.Children.Add(track);
        grid.Children.Add(fill);
        return grid;
    }

    private Border Card(UIElement content) => new()
    {
        Style = (Style)FindResource("PerchCard"),
        Padding = new Thickness(12, 10, 12, 8),
        Child = content,
    };

    private TextBlock Label(string text, string style, double size) => new()
    {
        Text = text,
        Style = (Style)FindResource(style),
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
    };

    private TextBlock Caption(string text, double size = 11.5, bool semibold = false, Thickness? margin = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("PerchCaption"),
            FontSize = size,
            Margin = margin ?? new Thickness(0),
        };
        if (semibold) block.FontWeight = FontWeights.SemiBold;
        return block;
    }

    private static Run RunWithResource(string text, double size, string resourceKey)
    {
        var run = new Run(text) { FontSize = size };
        run.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
        return run;
    }
}
