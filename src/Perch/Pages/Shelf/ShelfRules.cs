using System.IO;
using Perch.Core;

namespace Perch.Pages.Shelf;

/// <summary>Pure helpers for the Shelf: file kinds, names and captions. No IO, no WPF.</summary>
public static class ShelfRules
{
    /// <summary>Files with these extensions get a thumbnail instead of a shell icon.</summary>
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public const int MaxDisplayNameLength = 24;

    public static bool IsImage(string name) =>
        ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>File name for a note saved from dropped text, e.g. "Note 2026-11-14 17.05.txt".</summary>
    public static string NoteFileName(DateTime when) => $"Note {when:yyyy-MM-dd HH.mm}.txt";

    /// <summary>
    /// Shortens a name to about two tile lines, keeping the extension so the file type stays visible:
    /// "a-very-long-report-title-final.pdf" -> "a-very-long-report-t….pdf".
    /// </summary>
    public static string DisplayName(string name, int max = MaxDisplayNameLength)
    {
        if (name.Length <= max) return name;
        var ext = Path.GetExtension(name);
        var keep = ext.Length is > 0 and <= 6 ? max - ext.Length - 1 : max - 1;
        if (keep < 4) return name[..(max - 1)] + "…";
        return ext.Length is > 0 and <= 6
            ? name[..keep] + "…" + ext
            : name[..keep] + "…";
    }

    public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"5 items · 12 MB", or "Nothing here yet" for an empty shelf.</summary>
    public static string Summary(IReadOnlyCollection<ShelfItem> items)
    {
        if (items.Count == 0) return "Nothing here yet";
        return $"{Plural(items.Count, "item")} · {Format.Bytes(items.Sum(i => (double)i.Size))}";
    }
}
