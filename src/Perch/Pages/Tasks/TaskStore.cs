using Perch.Core;

namespace Perch.Pages.Tasks;

/// <summary>One to-do item as persisted in data/tasks.json.</summary>
public sealed class TaskItem
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DoneAt { get; set; }
}

/// <summary>On-disk document. NextId only ever grows, so ids are never reused.</summary>
public sealed class TaskDocument
{
    public List<TaskItem> Tasks { get; set; } = new();
    public long NextId { get; set; } = 1;
}

/// <summary>
/// The to-do list. Open tasks keep their user-chosen order; done tasks follow, newest done first.
/// Every change is saved straight away. No WPF here, so it is unit-testable.
/// </summary>
public sealed class TaskStore
{
    public const int MaxLength = 200;
    public const string FileName = "tasks";

    private readonly Func<DateTime> _clock;
    private readonly List<TaskItem> _tasks = new(); // stored order: open tasks in their order, done ones interleaved
    private long _nextId = 1;

    public TaskStore(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.Now);

    /// <summary>Display order: open tasks first, then done tasks, newest done first.</summary>
    public IReadOnlyList<TaskItem> Items =>
        _tasks.Where(t => !t.Done)
            .Concat(_tasks.Where(t => t.Done)
                .OrderByDescending(t => t.DoneAt ?? DateTime.MinValue)
                .ThenByDescending(t => t.Id))
            .ToList();

    public int OpenCount => _tasks.Count(t => !t.Done);

    public bool HasDone => _tasks.Any(t => t.Done);

    /// <summary>Trims and caps the text and appends it as an open task. Returns null when nothing is left.</summary>
    public TaskItem? Add(string? text)
    {
        var clean = Clean(text);
        if (clean == null) return null;
        var item = new TaskItem { Id = _nextId++, Text = clean, CreatedAt = _clock() };
        _tasks.Add(item);
        Save();
        return item;
    }

    /// <summary>Flips done/open. A reopened task returns to its old place among the open tasks.</summary>
    public TaskItem? Toggle(long id)
    {
        var item = Find(id);
        if (item == null) return null;
        item.Done = !item.Done;
        item.DoneAt = item.Done ? _clock() : null;
        Save();
        return item;
    }

    public bool Remove(long id)
    {
        var removed = _tasks.RemoveAll(t => t.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Replaces the text. Blank text is ignored and returns false.</summary>
    public bool Edit(long id, string? text)
    {
        var item = Find(id);
        var clean = Clean(text);
        if (item == null || clean == null) return false;
        item.Text = clean;
        Save();
        return true;
    }

    /// <summary>Moves an open task one step up among the open tasks. False at the top or for done tasks.</summary>
    public bool MoveUp(long id) => MoveOpen(id, -1);

    /// <summary>Moves an open task one step down among the open tasks. False at the bottom or for done tasks.</summary>
    public bool MoveDown(long id) => MoveOpen(id, +1);

    /// <summary>Removes every done task and returns how many went.</summary>
    public int ClearDone()
    {
        var count = _tasks.RemoveAll(t => t.Done);
        if (count > 0) Save();
        return count;
    }

    public void Load()
    {
        var doc = JsonStore.Load<TaskDocument>(FileName);
        _tasks.Clear();
        foreach (var t in doc.Tasks ?? new List<TaskItem>())
        {
            var text = Clean(t.Text);
            if (text == null) continue;
            t.Text = text;
            _tasks.Add(t);
        }
        var maxId = _tasks.Count == 0 ? 0 : _tasks.Max(t => t.Id);
        _nextId = Math.Max(Math.Max(doc.NextId, maxId + 1), 1);
    }

    public void Save() =>
        JsonStore.Save(FileName, new TaskDocument { Tasks = _tasks.ToList(), NextId = _nextId });

    /// <summary>Trimmed and capped at MaxLength; null when blank.</summary>
    public static string? Clean(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > MaxLength ? t[..MaxLength] : t;
    }

    private TaskItem? Find(long id) => _tasks.FirstOrDefault(t => t.Id == id);

    private bool MoveOpen(long id, int direction)
    {
        var item = Find(id);
        if (item == null || item.Done) return false;
        var index = _tasks.IndexOf(item);
        for (var j = index + direction; j >= 0 && j < _tasks.Count; j += direction)
        {
            if (_tasks[j].Done) continue; // skip over done tasks
            (_tasks[index], _tasks[j]) = (_tasks[j], _tasks[index]);
            Save();
            return true;
        }
        return false;
    }
}
