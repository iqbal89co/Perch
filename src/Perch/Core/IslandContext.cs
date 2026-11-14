using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Services;

namespace Perch.Core;

/// <summary>Shared state handed to every page and service.</summary>
public sealed class IslandContext
{
    public static IslandContext Current { get; private set; } = null!;

    public AppSettings Settings { get; private set; }
    public ActivityHub Activities { get; } = new();
    public MediaService Media { get; }
    public Dispatcher Dispatcher { get; }

    /// <summary>Raised (UI thread) after UpdateSettings saved a change.</summary>
    public event Action? SettingsChanged;

    /// <summary>Raised when something asks the island to open on a page (page id).</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>True while the island is open.</summary>
    public bool IsExpanded { get; internal set; }

    private IslandContext(Dispatcher dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = JsonStore.Load<AppSettings>("settings");
        Media = new MediaService(dispatcher);
    }

    public static IslandContext Initialize(Dispatcher dispatcher)
    {
        Current = new IslandContext(dispatcher);
        return Current;
    }

    /// <summary>For tests: a context without starting any OS integration.</summary>
    internal static IslandContext CreateForTests(Dispatcher dispatcher)
    {
        Current = new IslandContext(dispatcher);
        return Current;
    }

    public void UpdateSettings(Action<AppSettings> change)
    {
        change(Settings);
        JsonStore.Save("settings", Settings);
        SettingsChanged?.Invoke();
    }

    public void SaveSettings() => JsonStore.Save("settings", Settings);

    /// <summary>Open the island on the given page.</summary>
    public void Navigate(string pageId) => NavigateRequested?.Invoke(pageId);

    /// <summary>Show a short notice on the resting island: a glyph and a line of text.</summary>
    public void Notify(string glyph, string text, TimeSpan? duration = null, string key = "notice", int priority = 100)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "IslandForeground");
        var width = Math.Clamp(80 + text.Length * 7.0, 160, 340);
        Activities.Post(new PillActivity
        {
            Key = key,
            Priority = priority,
            Leading = icon,
            Center = label,
            Width = width,
            ExpiresAt = DateTime.Now + (duration ?? TimeSpan.FromSeconds(3)),
        });
    }
}
