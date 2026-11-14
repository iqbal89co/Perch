using System.Windows.Controls;

namespace Perch.Core;

/// <summary>
/// A tab in the opened island. Every non-abstract subclass in the assembly is discovered and created
/// once at startup (on the UI thread), so a page can keep working while hidden (timers, watchers).
/// Pages get a fixed content area of roughly (ExpandedWidth - 32) x (ExpandedHeight - 64) DIPs.
/// Use DynamicResource brushes from ThemeManager so theme changes apply live.
/// </summary>
public abstract class IslandPage : UserControl
{
    /// <summary>Stable lowercase id, e.g. "tasks". Used in settings (StartPage, HiddenTabs).</summary>
    public abstract string Id { get; }

    /// <summary>Short label shown on the tab, e.g. "Tasks".</summary>
    public abstract string Title { get; }

    /// <summary>One glyph from the Segoe Fluent Icons / Segoe MDL2 Assets font, e.g. "".</summary>
    public abstract string Glyph { get; }

    /// <summary>Tab order; lower comes first.</summary>
    public virtual int Order => 100;

    protected IslandContext Ctx => IslandContext.Current;

    /// <summary>Called when the page becomes the visible tab while the island is open.</summary>
    public virtual void OnShown() { }

    /// <summary>Called when the page stops being visible (tab switch or island closes).</summary>
    public virtual void OnHidden() { }
}

/// <summary>
/// A background feature without a tab (e.g. volume HUD). Every non-abstract implementation in the
/// assembly with a public parameterless constructor is created and started once at startup.
/// </summary>
public interface IIslandService
{
    void Start(IslandContext ctx);
}
