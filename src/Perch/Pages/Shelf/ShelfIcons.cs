using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Perch.Pages.Shelf;

/// <summary>Thumbnails for images and the shell's own file icons, cached per extension.</summary>
internal static class ShelfIcons
{
    // Icons that differ per file (executables, shortcuts) are cached per path rather than per extension.
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url",
    };

    private static readonly Dictionary<string, ImageSource?> FileIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A small decoded preview for an image file, or null when WPF cannot decode it.</summary>
    public static ImageSource? Thumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The icon Explorer shows for this file's type.</summary>
    public static ImageSource? FileIcon(string path)
    {
        var ext = Path.GetExtension(path);
        var key = PerFileExtensions.Contains(ext) ? path : ext;
        lock (FileIcons)
        {
            if (FileIcons.TryGetValue(key, out var cached)) return cached;
        }

        ImageSource? source = null;
        try
        {
            using var icon = Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            }
        }
        catch (Exception)
        {
            source = null; // missing file or no association: the tile shows a generic glyph instead
        }

        lock (FileIcons)
        {
            FileIcons[key] = source;
        }
        return source;
    }
}
