using System.IO;
using System.Text;
using Perch.Core;
using Perch.Pages.Shelf;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Perch.Tests;

/// <summary>Shelf store behaviour, on a private AppPaths root per test.</summary>
[Collection("AppPaths")]
public sealed class ShelfTests : IDisposable
{
    private readonly string _root;
    private readonly string _src;
    private readonly string _shelf;

    public ShelfTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "perch-shelf-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot(_root);
        _src = Path.Combine(_root, "sources");
        _shelf = Path.Combine(_root, "shelf");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteFile(string name, string content = "x")
    {
        var path = Path.Combine(_src, name);
        File.WriteAllText(path, content);
        return path;
    }

    private ShelfStore NewStore(long copyLimit = ShelfStore.DefaultCopyLimitBytes) =>
        new(_shelf, copyLimit);

    [Fact]
    public void Copies_a_small_file_into_the_shelf()
    {
        var store = NewStore();
        var source = WriteFile("hello.txt", "hello");

        var item = store.AddFile(source);

        Assert.NotNull(item);
        Assert.False(item!.IsReference);
        Assert.Equal("hello.txt", item.Name);
        Assert.Equal(5, item.Size);
        Assert.Equal(source, item.OriginalPath);
        Assert.StartsWith(Path.GetFullPath(_shelf), Path.GetFullPath(item.StoredPath));
        Assert.Equal("hello", File.ReadAllText(item.StoredPath));
        Assert.NotEqual(source, item.StoredPath);
    }

    [Fact]
    public void Large_files_are_referenced_not_copied()
    {
        var store = NewStore(copyLimit: 10);
        var source = WriteFile("big.bin", new string('a', 100));

        var item = store.AddFile(source);

        Assert.NotNull(item);
        Assert.True(item!.IsReference);
        Assert.Equal(source, item.StoredPath);
        Assert.Equal(100, item.Size);
        Assert.False(Directory.Exists(Path.Combine(_shelf, item.Id)));
    }

    [Fact]
    public void Folders_are_referenced_and_sized()
    {
        var store = NewStore();
        var folder = Path.Combine(_src, "project");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "123");

        var item = store.AddFile(folder);

        Assert.NotNull(item);
        Assert.True(item!.IsReference);
        Assert.Equal("project", item.Name);
        Assert.Equal(8, item.Size);
    }

    [Fact]
    public void Duplicate_of_the_same_original_is_skipped()
    {
        var store = NewStore();
        var source = WriteFile("dup.txt");

        Assert.NotNull(store.AddFile(source));
        Assert.Null(store.AddFile(source));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Dragging_a_shelf_copy_back_in_is_skipped()
    {
        var store = NewStore();
        var item = store.AddFile(WriteFile("again.txt"));

        Assert.Null(store.AddFile(item!.StoredPath));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Remove_deletes_the_copy_but_never_the_referenced_original()
    {
        var store = NewStore(copyLimit: 10);
        var copied = store.AddFile(WriteFile("small.txt", "hi"));
        var original = WriteFile("large.txt", new string('z', 50));
        var referenced = store.AddFile(original);
        var copyFolder = Path.Combine(_shelf, copied!.Id);
        Assert.True(Directory.Exists(copyFolder));

        Assert.True(store.Remove(copied.Id));
        Assert.True(store.Remove(referenced!.Id));

        Assert.False(Directory.Exists(copyFolder));
        Assert.True(File.Exists(original));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AddText_saves_a_dated_txt_note()
    {
        var store = NewStore();
        var when = new DateTime(2026, 11, 14, 17, 5, 0);

        var item = store.AddText("Buy milk\nand bread", when);

        Assert.NotNull(item);
        Assert.Equal("Note 2026-11-14 17.05.txt", item!.Name);
        Assert.False(item.IsReference);
        Assert.Equal("Buy milk\nand bread", File.ReadAllText(item.StoredPath, Encoding.UTF8));
        Assert.Equal(Path.Combine(_shelf, item.Id, item.Name), item.StoredPath);
    }

    [Fact]
    public void AddText_ignores_blank_text()
    {
        var store = NewStore();

        Assert.Null(store.AddText("   \r\n"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Prune_drops_entries_whose_files_are_gone()
    {
        var store = NewStore(copyLimit: 10);
        var copied = store.AddFile(WriteFile("gone-copy.txt", "a"));
        var original = WriteFile("gone-ref.bin", new string('b', 50));
        store.AddFile(original);
        var kept = store.AddFile(WriteFile("kept.txt", "c"));

        File.Delete(copied!.StoredPath);
        File.Delete(original);

        Assert.Equal(2, store.Prune());
        var names = store.Snapshot().Select(i => i.Name).ToList();
        Assert.Equal(new[] { kept!.Name }, names);
    }

    [Fact]
    public void Contents_survive_a_restart()
    {
        var first = NewStore();
        first.AddFile(WriteFile("one.txt", "1"));
        first.AddText("two", new DateTime(2026, 1, 2, 3, 4, 0));

        var second = NewStore();

        Assert.Equal(2, second.Count);
        var names = second.Snapshot().Select(i => i.Name).ToList();
        Assert.Equal(new[] { "Note 2026-01-02 03.04.txt", "one.txt" }, names);
    }

    [Fact]
    public void Newest_items_come_first()
    {
        var store = NewStore();
        store.AddFile(WriteFile("first.txt"));
        store.AddFile(WriteFile("second.txt"));

        Assert.Equal("second.txt", store.Snapshot()[0].Name);
    }

    [Fact]
    public void Clear_removes_everything_and_keeps_originals()
    {
        var store = NewStore(copyLimit: 10);
        store.AddFile(WriteFile("c.txt", "c"));
        var original = WriteFile("r.bin", new string('r', 50));
        store.AddFile(original);

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.True(File.Exists(original));
        Assert.Empty(Directory.GetDirectories(_shelf));
    }

    [Fact]
    public void Shelf_is_capped_at_60_and_the_oldest_copy_goes()
    {
        var store = NewStore();
        for (var i = 0; i <= ShelfStore.DefaultMaxItems; i++)
            store.AddFile(WriteFile($"f{i}.txt", i.ToString()));

        var items = store.Snapshot();
        Assert.Equal(ShelfStore.DefaultMaxItems, items.Count);
        Assert.Equal("f60.txt", items[0].Name);
        Assert.DoesNotContain(items, i => i.Name == "f0.txt");
    }

    // ------------------------------------------------------------ pure helpers

    [Theory]
    [InlineData("photo.PNG", true)]
    [InlineData("scan.jpeg", true)]
    [InlineData("doc.pdf", false)]
    [InlineData("noext", false)]
    public void Recognises_image_files(string name, bool expected) =>
        Assert.Equal(expected, ShelfRules.IsImage(name));

    [Fact]
    public void Short_names_are_untouched()
    {
        Assert.Equal("report.pdf", ShelfRules.DisplayName("report.pdf"));
    }

    [Fact]
    public void Long_names_keep_their_extension()
    {
        var shown = ShelfRules.DisplayName("a-very-long-quarterly-report-final-v2.pdf");

        Assert.True(shown.Length <= ShelfRules.MaxDisplayNameLength);
        Assert.EndsWith("….pdf", shown);
    }

    [Fact]
    public void Summary_counts_items_and_size()
    {
        var items = new List<ShelfItem>
        {
            new() { Size = 1024 },
            new() { Size = 1024 * 1024 },
        };

        Assert.Equal("2 items · 1 MB", ShelfRules.Summary(items));
        Assert.Equal("Nothing here yet", ShelfRules.Summary(new List<ShelfItem>()));
        Assert.Equal("1 item · 1 KB", ShelfRules.Summary(new List<ShelfItem> { new() { Size = 1024 } }));
    }
}
