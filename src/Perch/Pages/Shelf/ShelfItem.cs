namespace Perch.Pages.Shelf;

/// <summary>
/// One thing on the shelf. Either a private copy of a file (IsReference = false, StoredPath lives under
/// AppPaths.Shelf) or a pointer to the original file or folder (IsReference = true, StoredPath == OriginalPath).
/// </summary>
public sealed class ShelfItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string? OriginalPath { get; set; }
    public long Size { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public bool IsReference { get; set; }
}

/// <summary>What gets written to data/shelf.json.</summary>
public sealed class ShelfState
{
    public List<ShelfItem> Items { get; set; } = new();
}
