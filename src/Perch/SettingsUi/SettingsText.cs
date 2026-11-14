namespace Perch.SettingsUi;

/// <summary>Captions shown next to settings controls.</summary>
public static class SettingsText
{
    /// <summary>Lyrics timing: positive means the lyrics come earlier, negative later.</summary>
    public static string OffsetCaption(int ms) =>
        ms == 0 ? "In sync" : ms > 0 ? $"{ms} ms earlier" : $"{-ms} ms later";

    public static string Milliseconds(int ms) => $"{ms} ms";

    /// <summary>"Display 2 · 2560×1440"; index is 1-based.</summary>
    public static string DisplayName(int index, int width, int height) =>
        $"Display {index} · {width}×{height}";
}
