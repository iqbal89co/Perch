using Perch.Core;
using Perch.Pages.Tasks;

// The task tests write through the shared AppPaths root, so run the whole assembly one test at a time.

namespace Perch.Tests;

public sealed class TasksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-tasks-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 14, 9, 0, 0);

    public TasksTests() => AppPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private TaskStore NewStore() => new(() => _now);

    private void Tick() => _now = _now.AddMinutes(1);

    private static IEnumerable<string> Texts(TaskStore store) => store.Items.Select(t => t.Text);

    [Fact]
    public void Add_trims_and_ignores_blank_text()
    {
        var store = NewStore();
        Assert.Null(store.Add(""));
        Assert.Null(store.Add("   \t "));
        Assert.Null(store.Add(null));
        var item = store.Add("  buy milk  ");
        Assert.NotNull(item);
        Assert.Equal("buy milk", item!.Text);
        Assert.False(item.Done);
        Assert.Equal(1, store.OpenCount);
    }

    [Fact]
    public void Text_is_capped_at_200_characters()
    {
        var store = NewStore();
        var item = store.Add(new string('a', 250));
        Assert.Equal(200, item!.Text.Length);
        Assert.Equal(200, TaskStore.Clean(new string('b', 201))!.Length);
        Assert.Equal("x", TaskStore.Clean(" x "));
    }

    [Fact]
    public void Open_tasks_keep_insertion_order_and_done_tasks_go_last_newest_first()
    {
        var store = NewStore();
        var a = store.Add("a")!;
        var b = store.Add("b")!;
        var c = store.Add("c")!;
        Tick();
        store.Toggle(a.Id);
        Tick();
        store.Toggle(c.Id);
        // open first (b), then done newest first (c was finished after a)
        Assert.Equal(new[] { "b", "c", "a" }, Texts(store));
        Assert.Equal(new[] { false, true, true }, store.Items.Select(t => t.Done));

        Tick();
        store.Toggle(b.Id);
        Assert.Equal(new[] { "b", "c", "a" }, Texts(store));
        Assert.Equal(0, store.OpenCount);
    }

    [Fact]
    public void Toggle_reopens_in_the_old_place_and_unsets_DoneAt()
    {
        var store = NewStore();
        var a = store.Add("a")!;
        store.Add("b");
        var done = store.Toggle(a.Id)!;
        Assert.True(done.Done);
        Assert.Equal(_now, done.DoneAt);
        Assert.Equal(new[] { "b", "a" }, Texts(store));

        var reopened = store.Toggle(a.Id)!;
        Assert.False(reopened.Done);
        Assert.Null(reopened.DoneAt);
        Assert.Equal(new[] { "a", "b" }, Texts(store));
        Assert.Null(store.Toggle(999));
    }

    [Fact]
    public void Move_up_and_down_skip_done_tasks_and_stop_at_the_edges()
    {
        var store = NewStore();
        var a = store.Add("a")!;
        var b = store.Add("b")!;
        var c = store.Add("c")!;
        store.Toggle(b.Id); // b sits between a and c, but is done

        Assert.False(store.MoveUp(a.Id));    // already first
        Assert.False(store.MoveDown(c.Id));  // already last among open tasks
        Assert.False(store.MoveUp(b.Id));    // done tasks cannot move

        Assert.True(store.MoveDown(a.Id));   // a jumps over the done b
        Assert.Equal(new[] { "c", "a", "b" }, Texts(store));
        Assert.True(store.MoveUp(a.Id));
        Assert.Equal(new[] { "a", "c", "b" }, Texts(store));
        Assert.False(store.MoveUp(9999));
    }

    [Fact]
    public void Edit_replaces_text_and_ignores_blank()
    {
        var store = NewStore();
        var a = store.Add("draft")!;
        Assert.True(store.Edit(a.Id, "  final  "));
        Assert.Equal("final", store.Items[0].Text);
        Assert.False(store.Edit(a.Id, "   "));
        Assert.Equal("final", store.Items[0].Text);
        Assert.False(store.Edit(404, "nope"));
        Assert.True(store.Edit(a.Id, new string('z', 300)));
        Assert.Equal(200, store.Items[0].Text.Length);
    }

    [Fact]
    public void Clear_done_removes_only_done_tasks()
    {
        var store = NewStore();
        var a = store.Add("a")!;
        store.Add("b");
        var c = store.Add("c")!;
        store.Toggle(a.Id);
        store.Toggle(c.Id);
        Assert.True(store.HasDone);

        Assert.Equal(2, store.ClearDone());
        Assert.Equal(new[] { "b" }, Texts(store));
        Assert.False(store.HasDone);
        Assert.Equal(0, store.ClearDone());
    }

    [Fact]
    public void Open_count_follows_toggles_and_removals()
    {
        var store = NewStore();
        var a = store.Add("a")!;
        var b = store.Add("b")!;
        Assert.Equal(2, store.OpenCount);
        store.Toggle(a.Id);
        Assert.Equal(1, store.OpenCount);
        store.Remove(b.Id);
        Assert.Equal(0, store.OpenCount);
        Assert.False(store.Remove(b.Id));
    }

    [Fact]
    public void Changes_persist_and_a_new_store_loads_them()
    {
        var first = NewStore();
        var a = first.Add("write report")!;
        first.Add("call mum");
        first.Toggle(a.Id);
        first.Edit(first.Items[0].Id, "call mum tonight"); // open tasks are listed first

        var second = NewStore();
        second.Load();
        Assert.Equal(2, second.Items.Count);
        Assert.Equal("call mum tonight", second.Items[0].Text);
        Assert.Equal("write report", second.Items[1].Text);
        Assert.True(second.Items[1].Done);
        Assert.Equal(_now, second.Items[1].DoneAt);
        Assert.Equal(_now, second.Items[1].CreatedAt);
    }

    [Fact]
    public void Document_is_written_as_snake_case_json()
    {
        NewStore().Add("hello");
        var json = File.ReadAllText(JsonStore.PathFor(TaskStore.FileName));
        Assert.Contains("\"tasks\"", json);
        Assert.Contains("\"next_id\": 2", json);
        Assert.Contains("\"created_at\"", json);
    }

    [Fact]
    public void Ids_are_never_reused_even_after_removal_and_reload()
    {
        var store = NewStore();
        var a = store.Add("a")!;
        var b = store.Add("b")!;
        store.Remove(b.Id);
        var c = store.Add("c")!;
        Assert.True(c.Id > b.Id);

        store.ClearDone();
        store.Remove(c.Id);
        var reloaded = NewStore();
        reloaded.Load();
        var d = reloaded.Add("d")!;
        Assert.True(d.Id > c.Id);
        Assert.NotEqual(a.Id, d.Id);
    }

    [Fact]
    public void Next_id_is_kept_above_the_highest_stored_id_even_if_the_counter_is_stale()
    {
        var doc = new TaskDocument
        {
            Tasks = new List<TaskItem> { new() { Id = 7, Text = "kept", CreatedAt = _now } },
            NextId = 3,
        };
        JsonStore.Save(TaskStore.FileName, doc);

        var store = NewStore();
        store.Load();
        Assert.Equal(7, store.Items[0].Id);
        Assert.True(store.Add("next")!.Id > 7);
    }
}
