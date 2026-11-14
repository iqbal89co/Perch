using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Perch.Core;
using Perch.Pages.Media;
using Perch.Services;

namespace Perch.Shell;

/// <summary>
/// A made-up track with original synced lyrics, for screenshots and UI tests (--demo-media).
/// The lyrics are seeded into the lyrics cache so no network request is made.
/// </summary>
internal static class DemoMedia
{
    public const string Title = "Paper Lanterns";
    public const string Artist = "The Quiet Hours";
    public const string Album = "Night Window";

    private static readonly (double Seconds, string Text)[] Lines =
    {
        (0.0, "Streetlights hum a slower tune"),
        (6.5, "We fold the evening into June"),
        (13.0, "Paper lanterns, rising high"),
        (19.5, "Little moons across the sky"),
        (26.0, "Hold the light and let it go"),
        (32.5, "Watch it drift where rivers flow"),
    };

    public static void Show(IslandContext ctx)
    {
        SeedLyrics();
        ctx.Media.ShowDemo(new MediaSnapshot
        {
            SourceApp = "Spotify",
            Title = Title,
            Artist = Artist,
            Album = Album,
            Artwork = Artwork(),
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(14),
            Duration = TimeSpan.FromSeconds(214),
            PositionUpdatedAt = DateTimeOffset.Now,
            CanSeek = true,
        });
    }

    private static void SeedLyrics()
    {
        var dir = Path.Combine(AppPaths.Cache, "lyrics");
        Directory.CreateDirectory(dir);
        var entry = new
        {
            Status = "synced",
            Lines = Lines.Select(l => new { Ms = (long)(l.Seconds * 1000), l.Text }).ToList(),
            Plain = (string?)null,
            CheckedAt = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(dir, LyricsCacheKey.For(Artist, Title, Album) + ".json"), JsonSerializer.Serialize(entry));
    }

    /// <summary>Generated cover art: a dusk gradient with a few glowing lanterns.</summary>
    private static BitmapSource Artwork()
    {
        const int size = 256;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1E, 0x1B, 0x4B), Color.FromRgb(0xF5, 0x9E, 0x0B), 90), null, new Rect(0, 0, size, size));
            var glow = new RadialGradientBrush(Color.FromArgb(0xFF, 0xFD, 0xE6, 0x8A), Color.FromArgb(0x00, 0xFB, 0x92, 0x3C));
            foreach (var (x, y, r) in new[] { (70.0, 90.0, 26.0), (150.0, 60.0, 18.0), (196.0, 120.0, 22.0), (110.0, 150.0, 14.0) })
            {
                dc.DrawEllipse(glow, null, new Point(x, y), r * 2, r * 2);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C)), null, new Rect(x - r / 2, y - r / 1.4, r, r * 1.4), r / 3, r / 3);
            }
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x0B, 0x14)), null, new Rect(0, 200, size, 56));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
