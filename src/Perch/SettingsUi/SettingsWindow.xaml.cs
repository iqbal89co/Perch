using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perch.Core;
using Perch.Services;
using WinForms = System.Windows.Forms;

namespace Perch.SettingsUi;

internal enum SettingsSection { Appearance, Behaviour, Tabs, Music, Focus, Glances, About }

/// <summary>A ComboBox entry: the text shown and the value it stands for.</summary>
internal sealed record ComboOption(string Text, object Value)
{
    public override string ToString() => Text;
}

internal sealed record SwatchView(string Hex, Border Ring, TextBlock Check);

/// <summary>
/// The Settings window. Each control writes straight to IslandContext.UpdateSettings, so the island
/// follows along; there is no Save button. Slider drags are coalesced to one write per 150 ms.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private static readonly (SettingsSection Section, string Title, string Glyph)[] Sections =
    {
        (SettingsSection.Appearance, "Appearance", ""),
        (SettingsSection.Behaviour, "Behaviour", ""),
        (SettingsSection.Tabs, "Tabs", ""),
        (SettingsSection.Music, "Music", ""),
        (SettingsSection.Focus, "Focus", ""),
        (SettingsSection.Glances, "Glances", ""),
        (SettingsSection.About, "About", ""),
    };

    private static readonly Brush DarkBacking = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x12)));
    private static readonly Brush LightBacking = Frozen(new SolidColorBrush(Colors.White));

    private readonly List<(SettingsSection Section, Border Item)> _nav = new();
    private readonly List<(IslandTheme Theme, Border Card)> _themeCards = new();
    private readonly List<(IslandShape Shape, Border Card)> _shapeCards = new();
    private readonly List<SwatchView> _swatches = new();
    private readonly Dictionary<string, Action<AppSettings>> _pending = new();
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private SettingsSection _selected = SettingsSection.Appearance;
    private bool _loading;
    private TextBlock? _status;

    private static IslandContext Ctx => IslandContext.Current;

    public SettingsWindow()
    {
        InitializeComponent();
        _throttle.Tick += (_, _) => Flush();
        foreach (var (section, title, glyph) in Sections) BuildNavItem(section, title, glyph);
        ApplyBacking();
        Ctx.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            Flush();
            Ctx.SettingsChanged -= OnSettingsChanged;
        };
        Select(SettingsSection.Appearance);
    }

    /// <summary>Opens the Settings window, or brings the one already open to the front.</summary>
    public static void ShowSingle()
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>
    /// Development aid: renders every section of an offscreen window to folder/settings-&lt;section&gt;.png.
    /// Needs the Application, Styles and IslandContext set up by the caller (STA thread).
    /// </summary>
    internal static void RenderSectionsForReview(string folder)
    {
        Directory.CreateDirectory(folder);
        var win = new SettingsWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = 0,
            ShowActivated = false,
        };
        win.Show();
        try
        {
            foreach (var (section, _, _) in Sections)
            {
                win.Select(section);
                win.UpdateLayout();
                var root = (FrameworkElement)win.Content;
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(System.IO.Path.Combine(folder, $"settings-{section.ToString().ToLowerInvariant()}.png"));
                encoder.Save(stream);
            }
        }
        finally
        {
            win.Close();
        }
    }

    // ---------------------------------------------------------------- navigation

    private void BuildNavItem(SettingsSection section, string title, string glyph)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 14,
            Width = 22,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
        var label = new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("PerchBody"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);

        var item = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 2, 0, 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        item.MouseLeftButtonUp += (_, e) => { Select(section); e.Handled = true; };
        item.MouseEnter += (_, _) => { if (_selected != section) item.SetResourceReference(Border.BackgroundProperty, "IslandCard"); };
        item.MouseLeave += (_, _) => { if (_selected != section) item.Background = Brushes.Transparent; };
        _nav.Add((section, item));
        NavPanel.Children.Add(item);
    }

    private void Select(SettingsSection section)
    {
        _selected = section;
        foreach (var (s, item) in _nav)
        {
            if (s == section) item.SetResourceReference(Border.BackgroundProperty, "IslandAccentSoft");
            else item.Background = Brushes.Transparent;
        }
        ShowSection(section);
    }

    private void ShowSection(SettingsSection section)
    {
        _loading = true;
        try
        {
            ContentHost.Children.Clear();
            _status = null;
            var title = Sections.First(x => x.Section == section).Title;
            Add(new TextBlock { Text = title, Style = (Style)FindResource("PerchTitle"), FontSize = 20 }, 18);
            switch (section)
            {
                case SettingsSection.Appearance: BuildAppearance(); break;
                case SettingsSection.Behaviour: BuildBehaviour(); break;
                case SettingsSection.Tabs: BuildTabs(); break;
                case SettingsSection.Music: BuildMusic(); break;
                case SettingsSection.Focus: BuildFocus(); break;
                case SettingsSection.Glances: BuildGlances(); break;
                case SettingsSection.About: BuildAbout(); break;
            }
        }
        finally
        {
            _loading = false;
        }
        Scroll.ScrollToTop();
    }

    private void Add(UIElement element, double bottom = 14)
    {
        if (element is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, bottom);
        ContentHost.Children.Add(element);
    }

    // ---------------------------------------------------------------- Appearance

    private void BuildAppearance()
    {
        var s = Ctx.Settings;

        Add(Heading("Theme"), 8);
        _themeCards.Clear();
        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var theme in new[] { IslandTheme.Midnight, IslandTheme.Daylight, IslandTheme.Smoke, IslandTheme.Aurora })
            themes.Children.Add(ThemeCard(theme));
        Add(themes, 22);
        RefreshThemeCards();

        Add(Heading("Shape"), 8);
        _shapeCards.Clear();
        var shapes = new StackPanel { Orientation = Orientation.Horizontal };
        shapes.Children.Add(ShapeCard(IslandShape.Docked, "Docked", "Hangs from the top edge"));
        shapes.Children.Add(ShapeCard(IslandShape.Floating, "Floating", "A capsule just below the edge"));
        Add(shapes, 22);
        RefreshShapeCards();

        Add(Heading("Accent"), 8);
        _swatches.Clear();
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in ThemeManager.AccentChoices) swatches.Children.Add(Swatch(hex));
        Add(swatches, 22);
        RefreshSwatches();

        Add(Card(
            Row("24-hour clock", null, Toggle(s.Use24h, (x, v) => x.Use24h = v)),
            Row("Seconds on the resting island", "Show seconds next to the clock", Toggle(s.ShowSecondsInPill, (x, v) => x.ShowSecondsInPill = v))));
    }

    private Border ThemeCard(IslandTheme theme)
    {
        var (fill, text, edge) = Preview(theme);
        var pill = new Border
        {
            Width = 92,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = fill,
            Child = new TextBlock
            {
                Text = "09:41",
                Foreground = text,
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (edge != null)
        {
            pill.BorderBrush = edge;
            pill.BorderThickness = new Thickness(1);
        }

        var stack = new StackPanel();
        stack.Children.Add(pill);
        stack.Children.Add(new TextBlock
        {
            Text = theme.ToString(),
            Style = (Style)FindResource("PerchBody"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });

        var card = new Border
        {
            Width = 124,
            Padding = new Thickness(0, 14, 0, 12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "IslandCard");
        card.MouseLeftButtonUp += (_, e) =>
        {
            Change(s => s.Theme = theme);
            RefreshThemeCards();
            e.Handled = true;
        };
        _themeCards.Add((theme, card));
        return card;
    }

    private void RefreshThemeCards()
    {
        foreach (var (theme, card) in _themeCards) Outline(card, theme == Ctx.Settings.Theme);
    }

    private Border ShapeCard(IslandShape shape, string title, string caption)
    {
        var docked = shape == IslandShape.Docked;
        var edge = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.6 };
        edge.SetResourceReference(Border.BackgroundProperty, "IslandSubtle");
        var pill = new Border
        {
            Width = 96,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, docked ? 2 : 10, 0, 0),
            CornerRadius = docked ? new CornerRadius(0, 0, 11, 11) : new CornerRadius(11),
        };
        pill.SetResourceReference(Border.BackgroundProperty, "IslandForeground");
        var art = new Grid { Width = 150, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        art.Children.Add(edge);
        art.Children.Add(pill);

        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("PerchBody"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 0),
        });
        var captionText = Caption(caption);
        captionText.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(captionText);

        var card = new Border
        {
            Width = 236,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "IslandCard");
        card.MouseLeftButtonUp += (_, e) =>
        {
            Change(s => s.Shape = shape);
            RefreshShapeCards();
            e.Handled = true;
        };
        _shapeCards.Add((shape, card));
        return card;
    }

    private void RefreshShapeCards()
    {
        foreach (var (shape, card) in _shapeCards) Outline(card, shape == Ctx.Settings.Shape);
    }

    private Border Swatch(string hex)
    {
        var color = ThemeManager.ParseColor(hex, Colors.Gray);
        var light = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0 > 0.55;
        var check = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            Foreground = light ? Solid("#0B0B10") : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var swatch = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = Frozen(new SolidColorBrush(color)),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.Hand,
            Child = check,
        };
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            Change(s => s.Accent = hex);
            RefreshSwatches();
            e.Handled = true;
        };
        _swatches.Add(new SwatchView(hex, swatch, check));
        return swatch;
    }

    private void RefreshSwatches()
    {
        foreach (var view in _swatches)
        {
            var selected = string.Equals(view.Hex, Ctx.Settings.Accent, StringComparison.OrdinalIgnoreCase);
            view.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            Outline(view.Ring, selected, "IslandForeground");
        }
    }

    private static void Outline(Border card, bool selected, string key = "IslandAccent")
    {
        if (selected) card.SetResourceReference(Border.BorderBrushProperty, key);
        else card.BorderBrush = Brushes.Transparent;
    }

    /// <summary>Fill, text and optional edge colours for a theme's preview pill (fixed on purpose).</summary>
    private static (Brush Fill, Brush Text, Brush? Edge) Preview(IslandTheme theme) => theme switch
    {
        IslandTheme.Daylight => (Solid("#FAFAFC"), Solid("#14141A"), Solid("#D0D0D8")),
        IslandTheme.Smoke => (Solid("#D91F2026"), Solid("#F4F4F6"), null),
        IslandTheme.Aurora => (AuroraBrush(), Solid("#F1F5F9"), null),
        _ => (Solid("#0A0A0E"), Solid("#F5F5F7"), null),
    };

    private static Brush AuroraBrush() => Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(ThemeManager.ParseColor("#12102B", Colors.Black), 0),
            new GradientStop(ThemeManager.ParseColor("#072422", Colors.Black), 1),
        },
        new Point(0, 0),
        new Point(1, 1)));

    // ---------------------------------------------------------------- Behaviour

    private void BuildBehaviour()
    {
        var s = Ctx.Settings;
        Add(Card(
            Row("Open delay", "How long the pointer rests on the island before it opens",
                MakeSlider("hover", 0, 1000, 20, s.HoverDelayMs, SettingsText.Milliseconds, (x, v) => x.HoverDelayMs = v)),
            Row("Close delay", "How long the pointer can be away before the panel closes",
                MakeSlider("collapse", 0, 1500, 50, s.CollapseDelayMs, SettingsText.Milliseconds, (x, v) => x.CollapseDelayMs = v)),
            Row("Panel width", null,
                MakeSlider("width", 560, 800, 10, s.ExpandedWidth, v => $"{v} px", (x, v) => x.ExpandedWidth = v)),
            Row("Panel height", null,
                MakeSlider("height", 280, 420, 10, s.ExpandedHeight, v => $"{v} px", (x, v) => x.ExpandedHeight = v))));

        Add(Card(
            Row("Hide when a full-screen app is in front", null, Toggle(s.HideInFullscreen, (x, v) => x.HideInFullscreen = v)),
            Row("Start with Windows", null, Toggle(s.StartWithWindows, (x, v) => x.StartWithWindows = v, v => Autostart.Set(v))),
            Row("Sounds", null, Toggle(s.Sounds, (x, v) => x.Sounds = v))));

        var display = Combo();
        display.Items.Add(new ComboOption("Primary display", 0));
        var screens = WinForms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;
            display.Items.Add(new ComboOption(SettingsText.DisplayName(i + 1, bounds.Width, bounds.Height), i + 1));
        }
        SelectValue(display, s.Monitor);
        display.SelectionChanged += (_, _) =>
        {
            if (!_loading && display.SelectedItem is ComboOption option)
            {
                var monitor = (int)option.Value;
                Change(x => x.Monitor = monitor);
            }
        };
        Add(Card(Row("Display", "Where the island appears", display)));
    }

    // ---------------------------------------------------------------- Tabs

    private void BuildTabs()
    {
        var s = Ctx.Settings;
        var pages = Perch.App.Island?.Pages;
        if (pages == null)
        {
            Add(Caption("Open the island once to list its tabs."));
            return;
        }

        var visible = pages.Count(p => !IsHidden(s, p.Id));
        var rows = new List<FrameworkElement>();
        foreach (var page in pages)
        {
            var id = page.Id;
            var shown = !IsHidden(s, id);

            var glyph = new TextBlock
            {
                Text = page.Glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 15,
                Width = 28,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");

            var title = Body(page.Title);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(6, 0, 12, 0);

            var toggle = Toggle(shown,
                (x, on) => SetHidden(x, id, !on),
                _ => RebuildTabsSoon());
            // Keep at least one tab: the last visible one cannot be hidden.
            if (shown && visible == 1) toggle.IsEnabled = false;

            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(glyph);
            grid.Children.Add(title);
            grid.Children.Add(toggle);
            rows.Add(grid);
        }
        Add(Card(rows.ToArray()));

        var openOn = Combo();
        foreach (var page in pages.Where(p => !IsHidden(s, p.Id)))
            openOn.Items.Add(new ComboOption(page.Title, page.Id));
        SelectValue(openOn, s.StartPage);
        openOn.SelectionChanged += (_, _) =>
        {
            if (!_loading && openOn.SelectedItem is ComboOption option)
            {
                var id = (string)option.Value;
                Change(x => x.StartPage = id);
            }
        };
        Add(Card(
            Row("Open on", "The tab shown when the island opens", openOn),
            Row("Remember the last page", "Reopen the tab you used last", Toggle(s.RememberPage, (x, v) => x.RememberPage = v))));
    }

    private void RebuildTabsSoon() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_selected == SettingsSection.Tabs) ShowSection(SettingsSection.Tabs);
    }), DispatcherPriority.Background);

    private static bool IsHidden(AppSettings s, string id) =>
        s.HiddenTabs?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;

    private static void SetHidden(AppSettings s, string id, bool hidden)
    {
        s.HiddenTabs.RemoveAll(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase));
        if (hidden) s.HiddenTabs.Add(id);
    }

    // ---------------------------------------------------------------- Music

    private void BuildMusic()
    {
        var s = Ctx.Settings;
        Add(Card(
            Row("Show what's playing on the resting island", null, Toggle(s.ShowMediaInPill, (x, v) => x.ShowMediaInPill = v)),
            Row("Lyrics", "Show synced lyrics when they are available", Toggle(s.Lyrics, (x, v) => x.Lyrics = v))));

        Add(Card(
            Row("Lyrics timing", "Shift the lyrics earlier or later",
                MakeSlider("lyrics", -2000, 2000, 100, s.LyricsOffsetMs, SettingsText.OffsetCaption, (x, v) => x.LyricsOffsetMs = v))));
    }

    // ---------------------------------------------------------------- Focus

    private void BuildFocus()
    {
        var p = Ctx.Settings.Pomodoro;
        Add(Card(
            Row("Focus length", "How long a focus session runs",
                Stepper(p.FocusMin, 1, 120, (x, v) => x.Pomodoro.FocusMin = v, v => $"{v} min")),
            Row("Short break", null,
                Stepper(p.ShortBreakMin, 1, 60, (x, v) => x.Pomodoro.ShortBreakMin = v, v => $"{v} min")),
            Row("Long break", null,
                Stepper(p.LongBreakMin, 1, 90, (x, v) => x.Pomodoro.LongBreakMin = v, v => $"{v} min")),
            Row("Long break every", "Focus sessions before a long break",
                Stepper(p.LongEvery, 2, 8, (x, v) => x.Pomodoro.LongEvery = v, v => v.ToString()))));

        Add(Card(
            Row("Auto-start breaks", null, Toggle(p.AutoStartBreaks, (x, v) => x.Pomodoro.AutoStartBreaks = v)),
            Row("Auto-start focus", null, Toggle(p.AutoStartFocus, (x, v) => x.Pomodoro.AutoStartFocus = v))));

        Add(Caption("Changes apply to the next phase."), 0);
    }

    private FrameworkElement Stepper(int value, int min, int max, Action<AppSettings, int> set, Func<int, string> format)
    {
        var current = Math.Clamp(value, min, max);
        var text = new TextBlock
        {
            Text = format(current),
            Style = (Style)FindResource("PerchBody"),
            Width = 80,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var minus = StepButton("−");
        var plus = StepButton("+");

        void Refresh()
        {
            minus.IsEnabled = current > min;
            plus.IsEnabled = current < max;
        }

        void Step(int delta)
        {
            current = Math.Clamp(current + delta, min, max);
            text.Text = format(current);
            var v = current;
            Change(s => set(s, v));
            Refresh();
        }

        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        Refresh();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(minus);
        panel.Children.Add(text);
        panel.Children.Add(plus);
        return panel;
    }

    private Button StepButton(string text) => new()
    {
        Content = text,
        Style = (Style)FindResource("PerchGhostButton"),
        Width = 34,
        Height = 34,
        FontSize = 16,
        Padding = new Thickness(0),
    };

    // ---------------------------------------------------------------- Glances

    private void BuildGlances()
    {
        var s = Ctx.Settings;
        Add(Card(
            Row("Volume", "Show a small glance when the volume changes", Toggle(s.HudVolume, (x, v) => x.HudVolume = v)),
            Row("Battery and charging", "Show a small glance when the battery or charger changes", Toggle(s.HudBattery, (x, v) => x.HudBattery = v))));
    }

    // ---------------------------------------------------------------- About

    private void BuildAbout()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Logo(72));
        var text = new StackPanel { Margin = new Thickness(20, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Perch", Style = (Style)FindResource("PerchTitle"), FontSize = 24 });
        text.Children.Add(Margined(Caption($"Version {VersionText()}"), 4));
        text.Children.Add(Margined(Body("A living island for the top of your screen."), 10));
        header.Children.Add(text);
        Add(header, 22);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(GhostButton("Open data folder", (_, _) => OpenDataFolder()));
        buttons.Children.Add(GhostButton("Open log", (_, _) => OpenLog()));
        var reset = GhostButton("Reset all settings", (_, _) => ResetAll());
        reset.SetResourceReference(Control.ForegroundProperty, "IslandDanger");
        buttons.Children.Add(reset);
        Add(buttons, 8);

        var status = Caption("");
        _status = status;
        Add(status, 0);
    }

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("settings: could not open the data folder", ex);
        }
    }

    private void OpenLog()
    {
        if (!File.Exists(AppPaths.LogFile))
        {
            SetStatus("No log yet");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("settings: could not open the log", ex);
            SetStatus("Could not open the log");
        }
    }

    private void ResetAll()
    {
        var answer = MessageBox.Show(this,
            "Reset every setting to its default? This cannot be undone.",
            "Perch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        Flush();
        Change(s => SettingsReset.ApplyDefaults(s));
        Autostart.Set(Ctx.Settings.StartWithWindows);
        ShowSection(_selected);
    }

    private void SetStatus(string text)
    {
        if (_status != null) _status.Text = text;
    }

    // ---------------------------------------------------------------- settings plumbing

    /// <summary>Applies one change to the settings, then clamps them to the allowed ranges.</summary>
    private void Change(Action<AppSettings> change) =>
        Ctx.UpdateSettings(s =>
        {
            change(s);
            SettingsValidator.Clamp(s);
        });

    /// <summary>Queues a slider change; the newest value per slider is written at most every 150 ms.</summary>
    private void Queue(string key, Action<AppSettings> change)
    {
        _pending[key] = change;
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    private void Flush()
    {
        _throttle.Stop();
        if (_pending.Count == 0) return;
        var batch = _pending.Values.ToList();
        _pending.Clear();
        Change(s =>
        {
            foreach (var apply in batch) apply(s);
        });
    }

    private void OnSettingsChanged() => ApplyBacking();

    private void ApplyBacking()
    {
        var light = Application.Current?.Resources["IslandIsLight"] is bool isLight && isLight;
        Backing.Background = light ? LightBacking : DarkBacking;
    }

    private static string VersionText()
    {
        var v = typeof(SettingsWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
        return $"{Math.Max(v.Major, 0)}.{Math.Max(v.Minor, 0)}.{Math.Max(v.Build, 0)}";
    }

    // ---------------------------------------------------------------- control factories

    private FrameworkElement Row(string label, string? caption, FrameworkElement control)
    {
        var grid = new Grid { MinHeight = 36, Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Body(label));
        if (!string.IsNullOrEmpty(caption)) text.Children.Add(Margined(Caption(caption), 2));
        grid.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private Border Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(Divider());
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Style = (Style)FindResource("PerchCard"),
            Padding = new Thickness(16, 4, 16, 4),
            Child = stack,
        };
    }

    private static Border Divider()
    {
        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "IslandStroke");
        return divider;
    }

    private CheckBox Toggle(bool value, Action<AppSettings, bool> set, Action<bool>? after = null)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("PerchToggle"), IsChecked = value };
        void Set(bool on)
        {
            if (_loading) return;
            Change(s => set(s, on));
            after?.Invoke(on);
        }
        toggle.Checked += (_, _) => Set(true);
        toggle.Unchecked += (_, _) => Set(false);
        return toggle;
    }

    private FrameworkElement MakeSlider(string key, int min, int max, int step, int value,
        Func<int, string> format, Action<AppSettings, int> set)
    {
        var slider = new Slider
        {
            Style = (Style)FindResource("PerchSlider"),
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Value = value,
            Width = 180,
        };
        var caption = new TextBlock
        {
            Text = format(value),
            Style = (Style)FindResource("PerchCaption"),
            Width = 96,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = (int)Math.Round(e.NewValue);
            caption.Text = format(v);
            if (!_loading) Queue(key, s => set(s, v));
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(slider);
        panel.Children.Add(caption);
        return panel;
    }

    private ComboBox Combo() => new() { Style = (Style)FindResource("SettingsCombo") };

    private static void SelectValue(ComboBox combo, object value)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboOption option && Equals(option.Value, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private Button GhostButton(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("PerchGhostButton"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        button.Click += click;
        return button;
    }

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("PerchCaption"),
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
    };

    private TextBlock Body(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("PerchBody"),
        TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("PerchCaption"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static T Margined<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, top, 0, 0);
        return element;
    }

    private static Image Logo(double size) => new()
    {
        Source = new BitmapImage(new Uri("pack://application:,,,/Perch;component/Assets/perch.png")),
        Width = size,
        Height = size,
    };

    private static Brush Solid(string hex) => Frozen(new SolidColorBrush(ThemeManager.ParseColor(hex, Colors.Black)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
