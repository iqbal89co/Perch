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
            AddTile(0, "5 hours", block?.ActiveTokens ?? 0, _settings.ClaudeBlockBudgetTokens,
                block != null ? $"resets in {AiFormat.Remaining(block.EndUtc - DateTime.UtcNow)}" : "idle",
                block != null ? Breakdown(block.ModelTokens) : null);
            AddTile(1, "Weekly", claude.Week.Tokens, _settings.WeeklyBudgetTokens, "last 7 days",
                Breakdown(claude.Week.ModelTokens));
            AddTile(2, "Fable", claude.FableWeek.Tokens, _settings.FableBudgetTokens, "last 7 days",
                Breakdown(claude.FableWeek.ModelTokens));
        }

        UsageList.Visibility = _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = !_settingsOpen && snapshot != null && claude == null ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One tile: a small label, one big number (the percentage of the budget, or the token count when there is
    /// no budget), a bar when there is a budget, and a short note. Hover shows tokens per model.
    /// </summary>
    private void AddTile(int index, string name, long tokens, long? budget, string note, string? tooltip)
    {
        var percent = AiWarnings.Percent(tokens, budget);
        var danger = percent >= NearPercent;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var label = new TextBlock
        {
            Text = name.ToUpperInvariant(),
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        stack.Children.Add(label);

        var value = new TextBlock
        {
            Text = percent is { } p ? $"{Math.Round(p):0}%" : AiFormat.Tokens(tokens),
            Style = (Style)FindResource("PerchTitle"),
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 0),
        };
        if (danger) value.SetResourceReference(TextBlock.ForegroundProperty, "IslandDanger");
        stack.Children.Add(value);

        stack.Children.Add(Caption(budget is { } b && b > 0 ? $"{AiFormat.Tokens(tokens)} of {AiFormat.Tokens(b)} tokens" : "tokens"));
        if (percent is { } fraction) stack.Children.Add(Bar(fraction / 100, danger, new Thickness(0, 8, 0, 0)));

        var foot = Caption(note);
        foot.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(foot);

        var card = new Border
        {
            Style = (Style)FindResource("PerchCard"),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(index == 0 ? 0 : 4, 0, index == 2 ? 0 : 4, 0),
            Child = stack,
            ToolTip = tooltip,
        };
        Grid.SetColumn(card, index);
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
