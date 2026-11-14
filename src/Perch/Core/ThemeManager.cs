using System.Windows;
using System.Windows.Media;

namespace Perch.Core;

/// <summary>
/// Publishes the island's brushes into Application resources. Always reference them with
/// DynamicResource (XAML) or SetResourceReference (code) so a theme switch applies live.
///
/// Keys: IslandBackground, IslandForeground, IslandSubtle, IslandAccent, IslandAccentSoft,
///       IslandCard, IslandCardHover, IslandStroke, IslandDanger, IslandOnAccent
/// </summary>
public static class ThemeManager
{
    public static readonly string[] AccentChoices =
    {
        "#2DD4BF", // teal
        "#60A5FA", // sky
        "#A78BFA", // violet
        "#F472B6", // pink
        "#FB923C", // amber
        "#4ADE80", // green
    };

    public static void Apply(AppSettings s)
    {
        var accent = ParseColor(s.Accent, Color.FromRgb(0x2D, 0xD4, 0xBF));
        var p = Palette(s.Theme);
        var res = Application.Current.Resources;
        res["IslandBackground"] = p.Background;
        res["IslandForeground"] = Brush(p.Foreground);
        res["IslandSubtle"] = Brush(p.Subtle);
        res["IslandCard"] = Brush(p.Card);
        res["IslandCardHover"] = Brush(p.CardHover);
        res["IslandStroke"] = Brush(p.Stroke);
        res["IslandAccent"] = Brush(accent);
        res["IslandAccentSoft"] = Brush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        res["IslandOnAccent"] = Brush(Luminance(accent) > 0.55 ? Color.FromRgb(0x0B, 0x0B, 0x10) : Colors.White);
        res["IslandDanger"] = Brush(Color.FromRgb(0xF8, 0x71, 0x71));
        res["IslandAccentColor"] = accent;
        res["IslandIsLight"] = p.IsLight;
    }

    private sealed record ThemePalette(Brush Background, Color Foreground, Color Subtle, Color Card, Color CardHover, Color Stroke, bool IsLight);

    private static ThemePalette Palette(IslandTheme theme) => theme switch
    {
        IslandTheme.Daylight => new ThemePalette(
            Brush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
            Color.FromRgb(0x14, 0x14, 0x1A), Color.FromRgb(0x6B, 0x6B, 0x78),
            Color.FromRgb(0xEE, 0xEE, 0xF3), Color.FromRgb(0xE3, 0xE3, 0xEA), Color.FromArgb(0x22, 0, 0, 0), true),
        IslandTheme.Smoke => new ThemePalette(
            Brush(Color.FromArgb(0xD9, 0x1F, 0x20, 0x26)),
            Color.FromRgb(0xF4, 0xF4, 0xF6), Color.FromRgb(0xA1, 0xA1, 0xAD),
            Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), false),
        IslandTheme.Aurora => new ThemePalette(
            new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x12, 0x10, 0x2B), 0),
                    new(Color.FromRgb(0x0B, 0x1E, 0x2A), 0.6),
                    new(Color.FromRgb(0x07, 0x24, 0x22), 1),
                }, new Point(0, 0), new Point(1, 1)).FreezeAndReturn(),
            Color.FromRgb(0xF1, 0xF5, 0xF9), Color.FromRgb(0x94, 0xA3, 0xB8),
            Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), false),
        _ => new ThemePalette(
            Brush(Color.FromRgb(0x0A, 0x0A, 0x0E)),
            Color.FromRgb(0xF5, 0xF5, 0xF7), Color.FromRgb(0x8E, 0x8E, 0x99),
            Color.FromRgb(0x1A, 0x1A, 0x21), Color.FromRgb(0x25, 0x25, 0x2E), Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), false),
    };

    public static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color c) return c;
        }
        catch (FormatException) { }
        return fallback;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush FreezeAndReturn(this Brush b)
    {
        b.Freeze();
        return b;
    }
}
