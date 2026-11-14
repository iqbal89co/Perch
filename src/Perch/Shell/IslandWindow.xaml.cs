using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Perch.Core;
using Perch.Services;
using WinForms = System.Windows.Forms;

namespace Perch.Shell;

/// <summary>
/// The island: a transparent, always-on-top window whose visible part is a shape that springs between
/// a small resting pill and an opened panel with tabs.
/// </summary>
public partial class IslandWindow : Window
{
    // Geometry (DIPs)
    private const double Ear = 9;            // concave shoulders where a docked island meets the edge
    private const double DockedHeight = 32;
    private const double FloatingHeight = 34;
    private const double FloatingTop = 8;
    private const double ExpandedRadius = 26;
    private const double ShadowRoom = 40;

    private readonly IslandContext _ctx;
    private readonly Spring _w;
    private readonly Spring _h;
    private readonly Spring _r;
    private readonly Spring _p; // 0 = resting, 1 = open
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly FullscreenWatcher _fullscreen;
    private readonly List<IslandPage> _pages = new();
    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly TextBlock _clockText = new();
    private readonly EqualizerBars _mediaBars = new();

    private IntPtr _hwnd;
    private bool _animating;
    private TimeSpan _lastFrame;
    private bool _expanded;
    private bool _pinned;
    private bool _hiddenForFullscreen;
    private IslandPage? _current;
    private PillActivity? _shownActivity;
    private string _shownActivityKey = "";

    public IReadOnlyList<IslandPage> Pages => _pages;
    public bool IsExpanded => _expanded;
    public IslandPage? CurrentPage => _current;

    public IslandWindow(IslandContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        var restW = ClockWidth();
        _w = new Spring(restW);
        _h = new Spring(RestHeight);
        _r = new Spring(RestRadius);
        _p = new Spring(0) { Stiffness = 260, DampingRatio = 0.95 };

        _clockText.FontSize = 13;
        _clockText.FontWeight = FontWeights.SemiBold;
        _clockText.FontFamily = (FontFamily)FindResource("DisplayFont");
        _clockText.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

        _fullscreen = new FullscreenWatcher(() => _hwnd);
        _fullscreen.Changed += OnFullscreenChanged;

        CreatePages();
        BuildTabs();

        Island.MouseEnter += (_, _) => OnPointerEnter();
        Island.MouseLeave += (_, _) => OnPointerLeave();
        Island.MouseLeftButtonUp += (_, e) =>
        {
            if (!_expanded) { Expand(); e.Handled = true; }
        };
        Island.DragEnter += (_, _) => OnDragEnter();
        Island.DragOver += (_, _) => _collapseTimer.Stop();
        Island.DragLeave += (_, _) => OnPointerLeave();
        TabStrip.MouseWheel += OnTabWheel;
        SettingsButton.Click += (_, _) => SettingsUi.SettingsWindow.ShowSingle();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _expanded) { _pinned = false; Collapse(); e.Handled = true; }
        };
        Deactivated += (_, _) =>
        {
            if (_expanded && !Island.IsMouseOver && !_pinned) StartCollapseTimer();
        };

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Expand(); };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (Island.IsMouseOver || _pinned || !CanCollapse()) return;
            Collapse();
        };
        _clockTimer.Tick += (_, _) => Tick();
        _topmostTimer.Tick += (_, _) => KeepOnTop();

        _ctx.Activities.Changed += RefreshPill;
        _ctx.Media.Changed += OnMediaChanged;
        _ctx.SettingsChanged += ApplySettings;
        _ctx.NavigateRequested += id => Expand(id, pin: true);

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionOnMonitor, DispatcherPriority.Background);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.BeginInvoke(PositionOnMonitor, DispatcherPriority.Background);

        ApplySettings();
        RefreshPill();
        Tick();
        _clockTimer.Start();
        _topmostTimer.Start();
        _fullscreen.Start();
        StartAnimation();
    }

    private bool Docked => _ctx.Settings.Shape == IslandShape.Docked;
    private double RestHeight => Docked ? DockedHeight : FloatingHeight;
    private double RestRadius => Docked ? 14 : FloatingHeight / 2;
    private double EarSize => Docked ? Ear : 0;

    // ---------------------------------------------------------------- pages

    private void CreatePages()
    {
        var types = typeof(IslandPage).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IslandPage).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null);
        foreach (var type in types)
        {
            try
            {
                var page = (IslandPage)Activator.CreateInstance(type)!;
                _pages.Add(page);
            }
            catch (Exception ex)
            {
                Log.Error($"shell: page {type.Name} failed to start", ex.InnerException ?? ex);
            }
        }
        _pages.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
        Log.Info($"shell: pages {string.Join(", ", _pages.Select(p => p.Id))}");
    }

    private IEnumerable<IslandPage> VisiblePages =>
        _pages.Where(p => !_ctx.Settings.HiddenTabs.Contains(p.Id, StringComparer.OrdinalIgnoreCase));

    private void BuildTabs()
    {
        Tabs.Children.Clear();
        _tabButtons.Clear();
        foreach (var page in VisiblePages)
        {
            var glyph = new TextBlock
            {
                Text = page.Glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = page.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(glyph);
            panel.Children.Add(label);
            var tab = new Border
            {
                Child = panel,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = page.Title,
                Tag = page,
            };
            tab.SetResourceReference(TextElement.ForegroundProperty, "IslandSubtle");
            tab.MouseLeftButtonUp += (_, e) => { Select(page); e.Handled = true; };
            tab.MouseEnter += (_, _) => { if (_current != page) tab.SetResourceReference(Border.BackgroundProperty, "IslandCard"); };
            tab.MouseLeave += (_, _) => { if (_current != page) tab.Background = Brushes.Transparent; };
            _tabButtons[page.Id] = tab;
            Tabs.Children.Add(tab);
        }
        if (_current != null) StyleTabs();
    }

    private void StyleTabs()
    {
        foreach (var (id, tab) in _tabButtons)
        {
            var selected = _current?.Id == id;
            var label = (TextBlock)((StackPanel)tab.Child).Children[1];
            label.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected)
            {
                tab.SetResourceReference(Border.BackgroundProperty, "IslandAccentSoft");
                tab.SetResourceReference(TextElement.ForegroundProperty, "IslandForeground");
            }
            else
            {
                tab.Background = Brushes.Transparent;
                tab.SetResourceReference(TextElement.ForegroundProperty, "IslandSubtle");
            }
        }
    }

    public void Select(IslandPage page)
    {
        if (_current == page) return;
        var previous = _current;
        _current = page;
        previous?.OnHidden();
        PageHost.Content = page;
        StyleTabs();
        if (_expanded) page.OnShown();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase() };
        var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        PageHost.BeginAnimation(OpacityProperty, fade);
        PageShift.BeginAnimation(TranslateTransform.YProperty, slide);

        if (_ctx.Settings.RememberPage && _ctx.Settings.LastPage != page.Id)
        {
            _ctx.Settings.LastPage = page.Id;
            _ctx.SaveSettings();
        }
    }

    private void OnTabWheel(object sender, MouseWheelEventArgs e)
    {
        var visible = VisiblePages.ToList();
        if (visible.Count == 0 || _current == null) return;
        var i = visible.IndexOf(_current);
        i = (i + (e.Delta < 0 ? 1 : -1) + visible.Count) % visible.Count;
        Select(visible[i]);
        e.Handled = true;
    }

    private IslandPage? PageById(string? id) =>
        VisiblePages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- open / close

    public void Expand(string? pageId = null, bool pin = false)
    {
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        if (pin) _pinned = true;
        var page = PageById(pageId)
                   ?? (_expanded ? _current : null)
                   ?? PageById(_ctx.Settings.RememberPage ? _ctx.Settings.LastPage : _ctx.Settings.StartPage)
                   ?? VisiblePages.FirstOrDefault();
        if (page != null && page != _current) Select(page);
        if (_expanded) return;

        _expanded = true;
        _ctx.IsExpanded = true;
        ExpandedLayer.Visibility = Visibility.Visible;
        ExpandedLayer.IsHitTestVisible = true;
        _current?.OnShown();
        UpdateTargets();
        StartAnimation();
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _pinned = false;
        _ctx.IsExpanded = false;
        ExpandedLayer.IsHitTestVisible = false;
        _current?.OnHidden();
        UpdateTargets();
        StartAnimation();
    }

    private bool CanCollapse()
    {
        // Don't fold away while the user is typing into the island.
        if (IsActive && Keyboard.FocusedElement is TextBox tb && tb.IsDescendantOf(this)) return false;
        return true;
    }

    private void OnPointerEnter()
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _ctx.Settings.HoverDelayMs));
        _hoverTimer.Start();
    }

    private void OnPointerLeave()
    {
        _hoverTimer.Stop();
        if (!_expanded) return;
        _pinned = false; // a pinned island folds once the pointer has visited and left
        StartCollapseTimer();
    }

    private void StartCollapseTimer()
    {
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(60, _ctx.Settings.CollapseDelayMs));
        _collapseTimer.Start();
    }

    private void OnDragEnter()
    {
        _collapseTimer.Stop();
        if (!_expanded) Expand(PageById("shelf") != null ? "shelf" : null);
    }

    // ---------------------------------------------------------------- resting pill

    private double ClockWidth() => _ctx.Settings.Use24h
        ? (_ctx.Settings.ShowSecondsInPill ? 120 : 104)
        : (_ctx.Settings.ShowSecondsInPill ? 140 : 124);

    private void Tick()
    {
        var now = DateTime.Now;
        _clockText.Text = Format.Clock(now, _ctx.Settings.Use24h, _ctx.Settings.ShowSecondsInPill);
        HeaderClock.Text = now.ToString("ddd d MMM") + "  " + Format.Clock(now, _ctx.Settings.Use24h);
        // Expire transient activities.
        if (_shownActivity?.ExpiresAt is { } t && t <= now) RefreshPill();
    }

    private void RefreshPill()
    {
        var activity = _ctx.Activities.Current(DateTime.Now);
        _shownActivity = activity;
        var key = activity == null ? "clock" : activity.Key + ":" + activity.GetHashCode();
        if (key == _shownActivityKey) return;
        _shownActivityKey = key;

        PillLeading.Content = activity?.Leading;
        PillCenter.Content = activity == null ? _clockText : activity.Center;
        PillTrailing.Content = activity?.Trailing;
        PillLeading.Visibility = activity?.Leading == null ? Visibility.Collapsed : Visibility.Visible;
        PillTrailing.Visibility = activity?.Trailing == null ? Visibility.Collapsed : Visibility.Visible;

        foreach (var part in new UIElement[] { PillLeading, PillCenter, PillTrailing })
            part.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new QuadraticEase() });
        UpdateTargets();
        StartAnimation();
    }

    private void OnMediaChanged(MediaSnapshot? media)
    {
        if (media == null || !_ctx.Settings.ShowMediaInPill)
        {
            _ctx.Activities.Remove("media");
            return;
        }
        _mediaBars.IsPlaying = media.IsPlaying;

        FrameworkElement leading;
        if (media.Artwork != null)
        {
            leading = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = new ImageBrush(media.Artwork) { Stretch = Stretch.UniformToFill },
            };
        }
        else
        {
            var g = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 };
            g.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
            leading = g;
        }
        var title = new TextBlock
        {
            Text = media.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, media.IsPlaying ? "IslandForeground" : "IslandSubtle");

        if (_mediaBars.Parent is ContentControl oldHost) oldHost.Content = null;
        _ctx.Activities.Post(new PillActivity
        {
            Key = "media",
            Priority = 40,
            Leading = leading,
            Center = title,
            Trailing = _mediaBars,
            Width = 270,
            // A paused track lingers briefly, then the clock returns.
            ExpiresAt = media.IsPlaying ? null : DateTime.Now.AddSeconds(25),
        });
    }

    // ---------------------------------------------------------------- settings & placement

    public void ApplySettings()
    {
        ThemeManager.Apply(_ctx.Settings);
        var s = _ctx.Settings;
        ExpandedLayer.Width = s.ExpandedWidth;
        ExpandedLayer.Height = s.ExpandedHeight;
        Width = s.ExpandedWidth + 2 * Ear + 2 * ShadowRoom;
        Height = s.ExpandedHeight + FloatingTop + ShadowRoom;
        Island.Margin = new Thickness(0, Docked ? 0 : FloatingTop, 0, 0);
        BuildTabs();
        if (_current != null && !VisiblePages.Contains(_current))
        {
            var fallback = VisiblePages.FirstOrDefault();
            if (fallback != null) Select(fallback);
        }
        _shownActivityKey = "";
        RefreshPill();
        UpdateTargets();
        StartAnimation();
        PositionOnMonitor();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no taskbar button, not in Alt+Tab.
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW;
        ex &= ~(long)Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        PositionOnMonitor();
    }

    private WinForms.Screen TargetScreen()
    {
        var screens = WinForms.Screen.AllScreens;
        var index = _ctx.Settings.Monitor;
        if (index >= 1 && index <= screens.Length) return screens[index - 1];
        return WinForms.Screen.PrimaryScreen ?? screens[0];
    }

    public void PositionOnMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var screen = TargetScreen();
            var b = screen.Bounds;
            var monitor = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + 1 }, Native.MONITOR_DEFAULTTONEAREST);
            double scale = 1;
            if (Native.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0) scale = dpiX / 96.0;
            var pw = (int)Math.Round(Width * scale);
            var ph = (int)Math.Round(Height * scale);
            var x = b.Left + (b.Width - pw) / 2;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, b.Top, pw, ph, Native.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Warn($"shell: placement failed ({ex.Message})");
        }
    }

    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || _hiddenForFullscreen || !IsVisible) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (!_ctx.Settings.HideInFullscreen)
        {
            if (_hiddenForFullscreen) { _hiddenForFullscreen = false; Show(); }
            return;
        }
        _hiddenForFullscreen = fullscreen;
        if (fullscreen)
        {
            Collapse();
            Hide();
        }
        else
        {
            Show();
            PositionOnMonitor();
        }
    }

    // ---------------------------------------------------------------- motion

    private void UpdateTargets()
    {
        var s = _ctx.Settings;
        if (_expanded)
        {
            _w.Target = s.ExpandedWidth;
            _h.Target = s.ExpandedHeight;
            _r.Target = ExpandedRadius;
            _p.Target = 1;
        }
        else
        {
            _w.Target = _shownActivity?.Width ?? ClockWidth();
            _h.Target = RestHeight;
            _r.Target = RestRadius;
            _p.Target = 0;
        }
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
        Render();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (now == _lastFrame) return; // WPF can raise Rendering twice per frame
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        _w.Step(dt);
        _h.Step(dt);
        _r.Step(dt);
        _p.Step(dt);
        Render();

        if (_w.IsSettled && _h.IsSettled && _r.IsSettled && _p.IsSettled)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            if (!_expanded) ExpandedLayer.Visibility = Visibility.Hidden;
        }
    }

    private void Render()
    {
        var w = Math.Max(40, _w.Value);
        var h = Math.Max(20, _h.Value);
        var r = Math.Max(4, _r.Value);
        var ear = EarSize;
        var geometry = BuildGeometry(w, h, r, ear, Docked);

        Island.Width = w + 2 * ear;
        Island.Height = h;
        Shape.Data = geometry;
        ContentHost.Clip = geometry;

        var p = Math.Clamp(_p.Value, 0, 1);
        PillLayer.Opacity = Math.Clamp(1 - p * 2.2, 0, 1);
        PillLayer.Height = RestHeight;
        PillLayer.Margin = new Thickness(ear + 14, 0, ear + 14, 0);
        ExpandedLayer.Opacity = Math.Clamp((p - 0.35) / 0.65, 0, 1);
    }

    /// <summary>
    /// Docked: flat top across the full width, concave shoulders of size <paramref name="ear"/> on both sides,
    /// rounded bottom corners. Floating: a plain rounded rectangle.
    /// </summary>
    internal static Geometry BuildGeometry(double w, double h, double r, double ear, bool docked)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (docked)
            {
                r = Math.Min(r, Math.Min(w / 2, h - ear));
                if (r < 1) r = 1;
                var W = w + 2 * ear;
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(W, 0), true, true);
                c.QuadraticBezierTo(new Point(W - ear, 0), new Point(W - ear, ear), true, true);
                c.LineTo(new Point(W - ear, h - r), true, true);
                c.ArcTo(new Point(W - ear - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(ear + r, h), true, true);
                c.ArcTo(new Point(ear, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(ear, ear), true, true);
                c.QuadraticBezierTo(new Point(ear, 0), new Point(0, 0), true, true);
            }
            else
            {
                r = Math.Min(r, Math.Min(w / 2, h / 2));
                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(w - r, 0), true, true);
                c.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(w, h - r), true, true);
                c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(r, h), true, true);
                c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(0, r), true, true);
                c.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>Jump straight to the resting/open size (used by screenshot tests).</summary>
    internal void SettleNow()
    {
        _w.SnapToTarget();
        _h.SnapToTarget();
        _r.SnapToTarget();
        _p.SnapToTarget();
        Render();
    }
}
