using System.Windows;

namespace Perch.Core;

/// <summary>
/// Something the resting island can show: a leading view (left), a centre view and a trailing view (right).
/// The island shows the highest-priority live activity; with none, it shows the clock.
/// </summary>
public sealed class PillActivity
{
    public required string Key { get; init; }
    /// <summary>Higher wins. Conventions: transient notices 100, focus timer 60, media 40.</summary>
    public int Priority { get; init; }
    public FrameworkElement? Leading { get; init; }
    public FrameworkElement? Center { get; init; }
    public FrameworkElement? Trailing { get; init; }
    /// <summary>Resting width of the island in DIPs while this activity shows.</summary>
    public double Width { get; init; } = 220;
    /// <summary>When set, the activity disappears on its own at this time.</summary>
    public DateTime? ExpiresAt { get; init; }

    internal long Sequence { get; set; }
}

/// <summary>Collects activities from pages and services. UI thread only.</summary>
public sealed class ActivityHub
{
    private readonly Dictionary<string, PillActivity> _items = new();
    private long _sequence;

    public event Action? Changed;

    /// <summary>Adds or replaces the activity with the same key.</summary>
    public void Post(PillActivity activity)
    {
        activity.Sequence = ++_sequence;
        _items[activity.Key] = activity;
        Changed?.Invoke();
    }

    public void Remove(string key)
    {
        if (_items.Remove(key)) Changed?.Invoke();
    }

    public bool Contains(string key) => _items.ContainsKey(key);

    /// <summary>The activity to show now, after dropping expired ones. Ties go to the newest post.</summary>
    public PillActivity? Current(DateTime now)
    {
        var expired = _items.Values.Where(a => a.ExpiresAt is { } t && t <= now).Select(a => a.Key).ToList();
        foreach (var key in expired) _items.Remove(key);
        return _items.Values
            .OrderByDescending(a => a.Priority)
            .ThenByDescending(a => a.Sequence)
            .FirstOrDefault();
    }

    public IReadOnlyCollection<PillActivity> All => _items.Values;
}
