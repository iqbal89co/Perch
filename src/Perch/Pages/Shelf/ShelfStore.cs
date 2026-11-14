using System.IO;
using System.Text;
using Perch.Core;

namespace Perch.Pages.Shelf;

/// <summary>
/// The shelf's contents and their files. Thread-safe: file copies happen outside the lock so the UI can
/// read Snapshot() while a background task is copying. Items are kept newest first.
/// </summary>
public sealed class ShelfStore
{
    public const long DefaultCopyLimitBytes = 256L * 1024 * 1024;
    public const int DefaultMaxItems = 60;
    private const string StoreName = "shelf";

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly long _copyLimit;
    private readonly int _maxItems;
    private readonly List<ShelfItem> _items;

    public ShelfStore() : this(AppPaths.Shelf) { }

    /// <param name="shelfDir">Folder that holds one sub-folder per copied item.</param>
    /// <param name="copyLimitBytes">Files larger than this are referenced instead of copied.</param>
    /// <param name="maxItems">Beyond this, the oldest copied item is dropped.</param>
    public ShelfStore(string shelfDir, long copyLimitBytes = DefaultCopyLimitBytes, int maxItems = DefaultMaxItems)
    {
        _dir = Path.GetFullPath(shelfDir);
        Directory.CreateDirectory(_dir);
        _copyLimit = copyLimitBytes;
        _maxItems = maxItems;
        _items = JsonStore.Load<ShelfState>(StoreName).Items ?? new List<ShelfItem>();
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Newest first. A copy, safe to enumerate on any thread.</summary>
    public IReadOnlyList<ShelfItem> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    /// <summary>
    /// Adds a file or folder. Returns the new item, or null when skipped (missing, already on the shelf,
    /// or it is the shelf's own copy being dragged back in).
    /// </summary>
    public ShelfItem? AddFile(string source)
    {
        var full = Path.GetFullPath(source);
        var isFolder = Directory.Exists(full);
        if (!isFolder && !File.Exists(full)) return null;
        if (IsInsideShelf(full) || IsOnShelf(full)) return null;

        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var id = NewId();
        ShelfItem item;

        if (isFolder)
        {
            item = Reference(id, name, full, DirectorySize(full));
        }
        else
        {
            var length = new FileInfo(full).Length;
            if (length > _copyLimit)
            {
                item = Reference(id, name, full, length);
            }
            else
            {
                var folder = Path.Combine(_dir, id);
                Directory.CreateDirectory(folder);
                var dest = Path.Combine(folder, name);
                try
                {
                    File.Copy(full, dest);
                }
                catch
                {
                    DeleteFolder(folder);
                    throw;
                }
                item = new ShelfItem
                {
                    Id = id,
                    Name = name,
                    StoredPath = dest,
                    OriginalPath = full,
                    Size = length,
                    AddedAt = DateTime.Now,
                    IsReference = false,
                };
            }
        }

        Commit(item);
        return item;
    }

    /// <summary>Adds several paths; returns how many were added. Failures are logged and skipped.</summary>
    public int AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            try
            {
                if (AddFile(path) != null) added++;
            }
            catch (Exception ex)
            {
                Log.Warn($"shelf: could not add {path}: {ex.Message}");
            }
        }
        return added;
    }

    /// <summary>Saves dropped text as a .txt note. Returns null for blank text.</summary>
    public ShelfItem? AddText(string text, DateTime? at = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var when = at ?? DateTime.Now;
        var id = NewId();
        var folder = Path.Combine(_dir, id);
        Directory.CreateDirectory(folder);
        var name = ShelfRules.NoteFileName(when);
        var dest = Path.Combine(folder, name);
        File.WriteAllText(dest, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var item = new ShelfItem
        {
            Id = id,
            Name = name,
            StoredPath = dest,
            OriginalPath = null,
            Size = new FileInfo(dest).Length,
            AddedAt = when,
            IsReference = false,
        };
        Commit(item);
        return item;
    }

    /// <summary>Removes an entry. Deletes its copy; a referenced original is never touched.</summary>
    public bool Remove(string id)
    {
        ShelfItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return false;
            _items.Remove(item);
            Save();
        }
        DeleteCopy(item);
        return true;
    }

    /// <summary>Removes every entry and deletes all copies. Originals are not touched.</summary>
    public void Clear()
    {
        List<ShelfItem> all;
        lock (_gate)
        {
            all = _items.ToList();
            _items.Clear();
            Save();
        }
        foreach (var item in all) DeleteCopy(item);
    }

    /// <summary>Drops entries whose stored file or folder no longer exists. Returns how many.</summary>
    public int Prune()
    {
        List<ShelfItem> gone;
        lock (_gate)
        {
            gone = _items.Where(i => !Exists(i)).ToList();
            if (gone.Count == 0) return 0;
            foreach (var item in gone) _items.Remove(item);
            Save();
        }
        foreach (var item in gone) DeleteCopy(item);
        return gone.Count;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Inserts at the top, enforces the cap, persists. Old copies are deleted outside the lock.</summary>
    private void Commit(ShelfItem item)
    {
        var dropped = new List<ShelfItem>();
        lock (_gate)
        {
            _items.Insert(0, item);
            while (_items.Count > _maxItems)
            {
                // Oldest copy first; only when every entry is a reference, the oldest reference goes.
                var victim = _items.LastOrDefault(i => !i.IsReference) ?? _items[^1];
                _items.Remove(victim);
                dropped.Add(victim);
            }
            Save();
        }
        foreach (var victim in dropped) DeleteCopy(victim);
    }

    /// <summary>Caller holds the lock.</summary>
    private void Save() => JsonStore.Save(StoreName, new ShelfState { Items = _items });

    private bool IsOnShelf(string full)
    {
        lock (_gate)
        {
            return _items.Any(i =>
                string.Equals(i.OriginalPath, full, StringComparison.OrdinalIgnoreCase) && Exists(i));
        }
    }

    private bool IsInsideShelf(string full)
    {
        var root = Path.TrimEndingDirectorySeparator(_dir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Exists(ShelfItem item) =>
        !string.IsNullOrEmpty(item.StoredPath) && (File.Exists(item.StoredPath) || Directory.Exists(item.StoredPath));

    private static ShelfItem Reference(string id, string name, string full, long size) => new()
    {
        Id = id,
        Name = name,
        StoredPath = full,
        OriginalPath = full,
        Size = size,
        AddedAt = DateTime.Now,
        IsReference = true,
    };

    /// <summary>Deletes the item's private folder. Only ever deletes <c>shelfDir\id</c>.</summary>
    private void DeleteCopy(ShelfItem item)
    {
        if (item.IsReference || string.IsNullOrEmpty(item.Id)) return;
        var folder = Path.Combine(_dir, item.Id);
        if (!Path.GetFullPath(folder).StartsWith(_dir, StringComparison.OrdinalIgnoreCase)) return;
        DeleteFolder(folder);
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"shelf: could not delete {folder}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"shelf: could not delete {folder}: {ex.Message}");
        }
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
                total += file.Length;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
