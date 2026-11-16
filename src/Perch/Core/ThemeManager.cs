using System.Windows;
using System.Windows.Media;

namespace Perch.Core;

/// <summary>
/// Publishes the island's brushes into Application resources. Always reference them with
/// DynamicResource (XAML) or SetResourceReference (code) so a theme switch applies live.
///
/// Keys: IslandBackground, IslandForeground, IslandSubtle, IslandAccent, IslandAccentSoft,
///       IslandCard, IslandCardHover, IslandStroke, IslandDanger, IslandOnAccent,
///       IslandGlassBorder (the light rim of a glass card), IslandThemeAccent (the theme's own accent)
///
/// Tide and Apricot are "glass" themes: a gradient lit by soft colour glows, with translucent cards
/// and a bright rim on top, so the cards read as frosted glass over the backdrop.
/// </summary>
public static class ThemeManager
{
    /// <summary>Accent value meaning "use the theme's own accent".</summary>
    public const string AutoAccent = "auto";

    public static readonly string[] AccentChoices =
    {
        AutoAccent,
        "#22D3EE", // cyan
        "#2DD4BF", // teal
        "#60A5FA", // sky
        "#A78BFA", // violet
        "#F472B6", // pink
        "#FB923C", // amber
        "#4ADE80", // green
    };

    /// <summary>The themes offered in Settings, in order. Smoke and Aurora remain only so old files load.</summary>
    public static readonly IslandTheme[] Offered = { IslandTheme.Tide, IslandTheme.Apricot, IslandTheme.Midnight, IslandTheme.Daylight };

    public static bool IsAuto(string? accent) =>
        string.IsNullOrWhiteSpace(accent) || string.Equals(accent.Trim(), AutoAccent, StringComparison.OrdinalIgnoreCase);

    /// <summary>Retired themes draw as their successor.</summary>
    public static IslandTheme Effective(IslandTheme theme) =>
        theme is IslandTheme.Smoke or IslandTheme.Aurora ? IslandTheme.Tide : theme;

    public static void Apply(AppSettings s)
    {
        var p = Palette(Effective(s.Theme));
        var accent = IsAuto(s.Accent) ? p.Accent : ParseColor(s.Accent, p.Accent);
        var res = Application.Current.Resources;
        res["IslandBackground"] = p.Background;
        res["IslandForeground"] = Brush(p.Foreground);
        res["IslandSubtle"] = Brush(p.Subtle);
        res["IslandCard"] = Brush(p.Card);
        res["IslandCardHover"] = Brush(p.CardHover);
        res["IslandStroke"] = Brush(p.Stroke);
        res["IslandGlassBorder"] = Rim(p.RimTop, p.RimBottom);
        res["IslandAccent"] = Brush(accent);
        res["IslandAccentSoft"] = Brush(Color.FromArgb(p.IsLight ? (byte)0x40 : (byte)0x33, accent.R, accent.G, accent.B));
        res["IslandThemeAccent"] = Brush(p.Accent);
        res["IslandOnAccent"] = Brush(Luminance(accent) > 0.55 ? Color.FromRgb(0x0B, 0x0B, 0x10) : Colors.White);
        res["IslandDanger"] = Brush(p.IsLight ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromRgb(0xF8, 0x71, 0x71));
        res["IslandAccentColor"] = accent;
        res["IslandIsLight"] = p.IsLight;
    }

    /// <summary>The fill of a theme's preview chip in Settings (same look as the island, small).</summary>
    public static Brush PreviewFill(IslandTheme theme) => Palette(Effective(theme)).Background;

    public static Color PreviewText(IslandTheme theme) => Palette(Effective(theme)).Foreground;

    private sealed record ThemePalette(
        Brush Background, Color Foreground, Color Subtle, Color Card, Color CardHover, Color Stroke,
        Color RimTop, Color RimBottom, Color Accent, bool IsLight);

    private static readonly Dictionary<IslandTheme, ThemePalette> Cache = new();

    private static ThemePalette Palette(IslandTheme theme)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(theme, out var p)) Cache[theme] = p = Build(theme);
            return p;
        }
    }

    private static ThemePalette Build(IslandTheme theme) => theme switch
    {
        // Signature dark: deep navy flowing into cyan, lit by a cyan glow low right and a blue one top left.
        IslandTheme.Tide => new ThemePalette(
            Glow(
                new[] { (Hex("#081226"), 0.0), (Hex("#0B1F3D"), 0.45), (Hex("#0A3550"), 0.8), (Hex("#0B4C5F"), 1.0) },
                (Hex("#5922D3EE"), 0.90, 1.00, 0.60),
                (Hex("#403B82F6"), 0.08, 0.00, 0.55),
                (Hex("#262DD4BF"), 0.45, 1.15, 0.45)),
            Hex("#EAF4FF"), Hex("#8DAFC8"),
            Hex("#17FFFFFF"), Hex("#2AFFFFFF"), Hex("#4067E8F9"),
            Hex("#47FFFFFF"), Hex("#0AFFFFFF"), Hex("#22D3EE"), false),

        // Warm light glass: cream into apricot, with an orange glow top right, coral low left, sunlight low middle.
        IslandTheme.Apricot => new ThemePalette(
            Glow(
                new[] { (Hex("#FFF7EE"), 0.0), (Hex("#FFE9D3"), 0.5), (Hex("#FFD9B8"), 1.0) },
                (Hex("#80FFA45C"), 0.95, -0.05, 0.60),
                (Hex("#4DFF8F8F"), 0.02, 1.05, 0.55),
                (Hex("#66FFD27A"), 0.45, 1.00, 0.50)),
            Hex("#2A1709"), Hex("#85604A"),
            Hex("#94FFFFFF"), Hex("#80E9C3A3"), Hex("#D9FFFFFF"),
            Hex("#F2FFFFFF"), Hex("#59FFFFFF"), Hex("#EE6A12"), true),

        IslandTheme.Daylight => new ThemePalette(
            Brush(Hex("#FAFAFC")),
            Hex("#14141A"), Hex("#6B6B78"),
            Hex("#EEEEF3"), Hex("#E3E3EA"), Hex("#22000000"),
            Hex("#0F000000"), Hex("#08000000"), Hex("#0EA5A4"), true),

        _ => new ThemePalette(
            Brush(Hex("#0A0A0E")),
            Hex("#F5F5F7"), Hex("#8E8E99"),
            Hex("#1A1A21"), Hex("#25252E"), Hex("#1EFFFFFF"),
            Hex("#14FFFFFF"), Hex("#05FFFFFF"), Hex("#2DD4BF"), false),
    };

    /// <summary>
    /// A diagonal gradient with soft radial glows on top, drawn in a unit square and stretched over the island.
    /// Each glow is (colour, centre x, centre y, radius) in that square. Frozen and cached, so it is cheap to draw.
    /// </summary>
    private static Brush Glow((Color Color, double Offset)[] stops, params (Color Color, double X, double Y, double R)[] glows)
    {
        var unit = new RectangleGeometry(new Rect(0, 0, 1, 1));
        var group = new DrawingGroup { ClipGeometry = unit };

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (color, offset) in stops) gradient.GradientStops.Add(new GradientStop(color, offset));
        group.Children.Add(new GeometryDrawing(gradient, null, unit));

        foreach (var (color, x, y, r) in glows)
        {
            var radial = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(color, 0),
                    new GradientStop(Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B), 0.55),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
            group.Children.Add(new GeometryDrawing(radial, null, new EllipseGeometry(new Point(x, y), r, r)));
        }

        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return brush;
    }

    /// <summary>A card rim: bright at the top left, fading towards the bottom right, like light on glass.</summary>
    private static Brush Rim(Color top, Color bottom)
    {
        var b = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0.6, 1));
        b.Freeze();
        return b;
    }

    public static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && !IsAuto(text) && ColorConverter.ConvertFromString(text) is Color c) return c;
        }
        catch (FormatException) { }
        return fallback;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
