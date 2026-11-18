using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.AiUsage;

/// <summary>
/// How much of a Claude plan is used, as progress rows. The percentages and reset times come from Anthropic,
/// the same figures Claude Code's /usage shows (using Claude Code's sign-in); token counts come from Claude
/// Code's local logs. Without a sign-in or a connection the rows fall back to the local logs against the
/// budgets typed in settings. Only token counts, model names and times are read. The scan runs on a background task. The page refreshes every 30 s while shown, and every 5 min while
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
    private readonly ClaudePlanClient _planClient = new();
    private PlanUsage? _plan;
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
            var now = DateTime.UtcNow;
            var local = Task.Run(() => _service.Build(now, rescan));
            var plan = _planClient.GetAsync(now, force: rescan);
            _snapshot = await local;
            _plan = await plan;
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
            if (!Ctx.IsExpanded) MemoryTrim.Request(); // a scan in the background leaves its buffers behind
        }
    }

    private void EvaluateWarnings()
    {
        if (!_settings.WarnNearLimit || _snapshot is not { } snapshot) return;
        var result = AiWarnings.Evaluate(snapshot, _settings, _plan);
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
        if (_lastScanUtc == DateTime.MinValue)
        {
            HeaderText.Text = "";
            return;
        }
        HeaderText.Text = _plan?.Status switch
        {
            PlanStatus.Ok => "Claude plan · updated " + AiFormat.Ago(DateTime.UtcNow - _plan.FetchedUtc),
            PlanStatus.Expired => "Estimate from local logs · open Claude Code to renew its sign-in",
            PlanStatus.SignedOut => "Estimate from local logs · sign in to Claude Code for exact %",
            _ => "Estimate from local logs · couldn't reach Anthropic",
        };
    }

    private void Render()
    {
        UsageList.Children.Clear();
        var snapshot = _snapshot;
        var claude = snapshot?.Claude;

        if (_plan is { Status: PlanStatus.Ok } plan)
            RenderPlan(plan, claude);
        else if (claude != null)
        {
            var block = claude.Current;
            AddRow("Usage 5h", block?.ActiveTokens ?? 0, _settings.ClaudeBlockBudgetTokens,
                block != null ? $"resets in {AiFormat.Remaining(block.EndUtc - DateTime.UtcNow)}" : "idle",
                block != null ? Breakdown(block.ModelTokens) : null);
            AddRow("Usage weekly", claude.Week.Tokens, _settings.WeeklyBudgetTokens, "last 7 days",
                Breakdown(claude.Week.ModelTokens));
            AddRow("Usage Fable", claude.FableWeek.Tokens, _settings.FableBudgetTokens, "last 7 days",
                Breakdown(claude.FableWeek.ModelTokens));
        }

        UsageList.Visibility = _settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = !_settingsOpen && snapshot != null && claude == null && _plan?.Status != PlanStatus.Ok
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPanel.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Rows from Anthropic's figures, in the order /usage lists them. Each shows the exact percent and reset time;
    /// the line under the percent is the matching token count from the local logs. Fable gets a row from the
    /// local logs when the plan has no separate Fable limit.
    /// </summary>
    private void RenderPlan(PlanUsage plan, ClaudeSummary? claude)
    {
        var now = DateTime.UtcNow;
        foreach (var limit in plan.Limits)
        {
            UsageTotal? local = limit.IsSession ? null : limit.IsWeeklyAll ? claude?.Week : limit.IsFable ? claude?.FableWeek : null;
            long? tokens = limit.IsSession ? claude?.Current?.ActiveTokens : local?.Tokens;
            var tooltip = limit.IsSession
                ? (claude?.Current is { } block ? Breakdown(block.ModelTokens) : null)
                : local != null ? Breakdown(local.ModelTokens) : null;
            AddRow(limit.Label, limit.Percent, limit.Elevated,
                limit.ResetsAtUtc is { } reset ? AiFormat.Resets(reset, now, Ctx.Settings.Use24h) : limit.IsSession ? "idle" : "",
                tokens is { } t ? $"{AiFormat.Tokens(t)} tokens" : "used", tooltip);
        }

        if (!plan.Limits.Any(l => l.IsFable) && claude != null)
        {
            var fable = claude.FableWeek;
            var percent = AiWarnings.Percent(fable.Tokens, _settings.FableBudgetTokens);
            AddRow("Usage Fable", percent, false, "part of weekly",
                percent == null ? "tokens this week" : $"{AiFormat.Tokens(fable.Tokens)} of {AiFormat.Tokens(_settings.FableBudgetTokens!.Value)}",
                Breakdown(fable.ModelTokens),
                valueText: percent == null ? AiFormat.Tokens(fable.Tokens) : null);
        }
    }

    /// <summary>A row from the local logs against a typed budget (the fallback when there are no plan figures).</summary>
    private void AddRow(string name, long tokens, long? budget, string note, string? tooltip)
    {
        var percent = AiWarnings.Percent(tokens, budget);
        AddRow(name, percent, false, note,
            budget is { } b && b > 0 ? $"{AiFormat.Tokens(tokens)} of {AiFormat.Tokens(b)}" : "tokens, no budget",
            tooltip, valueText: percent == null ? AiFormat.Tokens(tokens) : null);
    }

    /// <summary>
    /// One row: the name and a short note on the left, a full-width progress bar in the middle, and the
    /// percentage with "used of budget" on the right. Without a budget the bar stays empty and the right side
    /// shows the token count. Hover shows tokens per model.
    /// </summary>
    private void AddRow(string name, double? percent, bool elevated, string note, string detailText, string? tooltip,
        string? valueText = null)
    {
        var danger = elevated || percent >= NearPercent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto), MinWidth = 92 });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = name,
            Style = (Style)FindResource("PerchBody"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        left.Children.Add(label);
        left.Children.Add(Caption(note));
        grid.Children.Add(left);

        var bar = Bar(percent is { } p ? p / 100 : 0, danger, new Thickness(6, 0, 14, 0));
        bar.VerticalAlignment = VerticalAlignment.Center;
        if (percent == null) bar.ToolTip = "Set a budget in AI settings to fill this bar";
        if (string.IsNullOrEmpty(note)) left.Children.RemoveAt(1);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var value = new TextBlock
        {
            Text = valueText ?? (percent is { } q ? $"{Math.Round(q):0}%" : "–"),
            Style = (Style)FindResource("PerchTitle"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (danger) value.SetResourceReference(TextBlock.ForegroundProperty, "IslandDanger");
        right.Children.Add(value);
        var detail = Caption(detailText);
        detail.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(detail);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        UsageList.Children.Add(new Border
        {
            Style = (Style)FindResource("PerchCard"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, UsageList.Children.Count == 0 ? 0 : 8, 0, 0),
            Child = grid,
            ToolTip = tooltip,
        });
    }

    /// <summary>Tooltip: tokens per model, largest first. Null when there is nothing to split.</summary>
    private static string? Breakdown(IReadOnlyDictionary<string, long> models) =>
        models.Count == 0
            ? null
            : string.Join(Environment.NewLine, models
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}: {AiFormat.Tokens(kv.Value)}"));

    /// <summary>
    /// A rounded progress bar: the track is IslandCardHover, the fill is accent (or danger when near the limit).
    /// The fill is sized from the track's width and never drawn thinner than the bar is tall, so 1% still reads.
    /// </summary>
    private static FrameworkElement Bar(double fraction, bool danger, Thickness margin)
    {
        const double height = 10;
        var f = Math.Clamp(fraction, 0, 1);
        var grid = new Grid { Height = height, Margin = margin };

        var track = new Border { CornerRadius = new CornerRadius(height / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");

        var fill = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = f <= 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        fill.SetResourceReference(Border.BackgroundProperty, danger ? "IslandDanger" : "IslandAccent");
        grid.SizeChanged += (_, e) => fill.Width = Math.Max(height, e.NewSize.Width * f);

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
