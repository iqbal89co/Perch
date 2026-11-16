using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.AiUsage;

/// <summary>
/// How much of a Claude plan is used, from Claude Code's own local logs, as three rows: the current 5-hour
/// window, the last 7 days, and the last 7 days of Fable models. Only token counts, model names and times are
/// read. The scan runs on a background task. The page refreshes every 30 s while shown, and every 5 min while
/// hidden only when near-limit warnings are on.
/// </summary>
public partial class AiUsagePage : IslandPage
{
    private const string TabGlyph = "";
    private const string GearGlyph = "";
    private const string BackGlyph = "";

    private static readonly TimeSpan ShownRefreshEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HiddenRefreshEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShownTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HiddenTick = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer = new();
    private readonly AiUsageService _service = new();
    private readonly AiUsageSettings _settings;
    private readonly (TextBox Box, Func<long?> Get, Action<long?> Set)[] _budgets;
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

        _budgets = new (TextBox, Func<long?>, Action<long?>)[]
        {
            (Budget5hBox, () => _settings.ClaudeBlockBudgetTokens, v => _settings.ClaudeBlockBudgetTokens = v),
            (BudgetWeekBox, () => _settings.WeeklyBudgetTokens, v => _settings.WeeklyBudgetTokens = v),
            (BudgetFableBox, () => _settings.FableBudgetTokens, v => _settings.FableBudgetTokens = v),
        };
        foreach (var budget in _budgets)
        {
            var b = budget;
            b.Box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitBudget(b.Box, b.Get, b.Set);
                e.Handled = true;
            };
            b.Box.LostFocus += (_, _) => CommitBudget(b.Box, b.Get, b.Set);
        }

        SettingsButton.Click += (_, _) => ToggleSettings();
        RescanButton.Click += (_, _) => Refresh(rescan: true);
        WarnToggle.Click += (_, _) =>
        {
            _settings.WarnNearLimit = WarnToggle.IsChecked == true;
            Save();
            ApplyTimer();
        };
        NearLowerButton.Click += (_, _) => ChangeNearPercent(-5);
        NearHigherButton.Click += (_, _) => ChangeNearPercent(5);

        LoadSettingsControls();
        RenderHeader();        // With warnings on, scan once at startup so a near-limit figure can warn even if the tab is never opened.
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
        // Keeps "resets in" current between scans.
        else if (_shown && !_settingsOpen) Render();
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
        // One notice per refresh: a second Notify with the same key would replace the first.
        if (result.Messages.Count > 0)
            Ctx.Notify(TabGlyph, string.Join("  ·  ", result.Messages), TimeSpan.FromSeconds(6), key: "ai");
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
        foreach (var (box, get, _) in _budgets) box.Text = get() is { } budget ? AiFormat.Tokens(budget) : "";
        BudgetHint.Visibility = Visibility.Collapsed;
        WarnToggle.IsChecked = _settings.WarnNearLimit;
        NearText.Text = $"{NearPercent}%";
    }

    private void CommitBudget(TextBox box, Func<long?> get, Action<long?> set)
    {
        if (!AiFormat.TryParseBudget(box.Text, out var budget))
        {
            BudgetHint.Visibility = Visibility.Visible;
            return;
        }
        BudgetHint.Visibility = Visibility.Collapsed;
        box.Text = budget is { } b ? AiFormat.Tokens(b) : "";
        if (budget == get()) return;

        set(budget);
        Save();
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
            : "Claude Code · updated " + AiFormat.Ago(DateTime.UtcNow - _lastScanUtc);
    }

    private void Render()
    {
        UsageList.Children.Clear();
        var snapshot = _snapshot;
        var claude = snapshot?.Claude;

        if (claude != null)
        {
            var block = claude.Current;
            AddRow(0, "Usage 5h", block?.ActiveTokens ?? 0, _settings.ClaudeBlockBudgetTokens,
                block != null
                    ? $"resets in {AiFormat.Remaining(block.EndUtc - DateTime.UtcNow)} · {AiFormat.Plural(block.Messages, "message")}"
                    : "No activity in the last 5 hours",
                block != null ? Breakdown(block.ModelTokens) : null);

            AddRow(1, "Usage Weekly", claude.Week.Tokens, _settings.WeeklyBudgetTokens,
                $"last 7 days · {AiFormat.Plural(claude.Week.Messages, "message")}",
                Breakdown(claude.Week.ModelTokens));

            AddRow(2, "Usage Fable", claude.FableWeek.Tokens, _settings.FableBudgetTokens,
                claude.FableWeek.Messages > 0
                    ? $"Fable models, last 7 days · {AiFormat.Plural(claude.FableWeek.Messages, "message")}"
                    : "No Fable use in the last 7 days",
                Breakdown(claude.FableWeek.ModelTokens));
        }

        UsageList.Visibility = _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = !_settingsOpen && snapshot != null && claude == null ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One row: the name and a short caption on the left; on the right the percentage of the budget (or the
    /// token count when there is no budget); a bar along the bottom when there is a budget.
    /// </summary>
    private void AddRow(int index, string name, long tokens, long? budget, string caption, string? tooltip)
    {
        var percent = AiWarnings.Percent(tokens, budget);
        var danger = percent >= NearPercent;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = name,
            Style = (Style)FindResource("PerchTitle"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        grid.Children.Add(title);

        var value = new TextBlock
        {
            Text = percent is { } p ? $"{Math.Round(p):0}%" : AiFormat.Tokens(tokens),
            Style = (Style)FindResource("PerchTitle"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        if (percent != null) value.SetResourceReference(TextBlock.ForegroundProperty, danger ? "IslandDanger" : "IslandAccent");
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var left = Caption(caption);
        Grid.SetRow(left, 1);
        grid.Children.Add(left);

        var right = Caption(budget is { } b && b > 0 ? $"{AiFormat.Tokens(tokens)} of {AiFormat.Tokens(b)}" : "tokens");
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        if (percent is { } fraction)
        {
            var bar = Bar(fraction / 100, danger, new Thickness(0, 7, 0, 0));
            Grid.SetRow(bar, 2);
            Grid.SetColumnSpan(bar, 2);
            grid.Children.Add(bar);
        }

        var card = new Border
        {
            Style = (Style)FindResource("PerchCard"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, index == 0 ? 0 : 3, 0, index == 2 ? 0 : 3),
            Child = grid,
            ToolTip = tooltip,
        };
        Grid.SetRow(card, index);
        UsageList.Children.Add(card);
    }

    /// <summary>Tooltip: tokens per model, largest first. Null when there is nothing to split.</summary>
    private static string? Breakdown(IReadOnlyDictionary<string, long> models) =>
        models.Count == 0
            ? null
            : string.Join(Environment.NewLine, models
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}: {AiFormat.Tokens(kv.Value)}"));

    /// <summary>A thin rounded bar: the track is IslandCardHover, the fill is accent (or danger when near the limit).</summary>
    private static FrameworkElement Bar(double fraction, bool danger, Thickness margin)
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

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("PerchCaption"),
        FontSize = 11.5,
        Margin = new Thickness(0, 2, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
