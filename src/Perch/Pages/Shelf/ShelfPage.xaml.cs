using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Perch.Core;

namespace Perch.Pages.Shelf;

/// <summary>
/// Tray for files dragged onto the island. Drop files or text to keep them; drag tiles back out to use them
/// elsewhere. Copies happen on a background task so the island never stalls.
/// </summary>
public partial class ShelfPage : IslandPage
{
    private readonly ShelfStore _store = new();
    private int _addingFiles;      // files still being copied (drives the "Adding n files…" caption)
    private bool _savingNote;      // dropped text still being written
    private bool _dragOutActive;   // a tile is being dragged out: ignore our own drop target

    public override string Id => "shelf";
    public override string Title => "Shelf";
    public override string Glyph => ""; // download
    public override int Order => 30;

    public ShelfPage()
    {
        InitializeComponent();
        OpenFolderButton.Click += (_, _) => OpenPath(AppPaths.Shelf);
        ClearButton.Click += (_, _) => ClearShelf();
        Refresh();
    }

    public override void OnShown()
    {
        _store.Prune();
        Refresh();
    }

    // ---------------------------------------------------------------- drop target

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepts = !_dragOutActive
                      && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText));
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
        // Not marked handled: the island uses DragOver to keep itself open while something is dragged over it.
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when really leaving the page.
        var p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_dragOutActive)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Effects = DragDropEffects.Copy;
            AddFilesAsync(paths);
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string text
                 && !string.IsNullOrWhiteSpace(text))
        {
            e.Effects = DragDropEffects.Copy;
            AddNoteAsync(text);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    // ---------------------------------------------------------------- background work

    private async void AddFilesAsync(string[] paths)
    {
        _addingFiles += paths.Length;
        Refresh();
        var added = await Task.Run(() => _store.AddFiles(paths));
        _addingFiles -= paths.Length;
        Refresh();

        if (added > 0)
            Ctx.Notify(Glyph, $"Added {ShelfRules.Plural(added, "file")} to the Shelf");
        else
            Ctx.Notify(Glyph, "Already on the Shelf");
    }

    private async void AddNoteAsync(string text)
    {
        _savingNote = true;
        Refresh();
        var item = await Task.Run(() => _store.AddText(text));
        _savingNote = false;
        Refresh();
        if (item != null) Ctx.Notify(Glyph, "Saved a note to the Shelf");
    }

    private async void RemoveItem(string id)
    {
        await Task.Run(() => _store.Remove(id));
        Refresh();
    }

    private async void ClearShelf()
    {
        var count = _store.Count;
        if (count == 0) return;
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Remove all {ShelfRules.Plural(count, "item")} from the shelf?\n\nCopies are deleted. Files you referenced stay where they are.",
            "Clear the Shelf",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await Task.Run(() => _store.Clear());
        Refresh();
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Rebuilds the tiles from the store. UI thread only.</summary>
    private void Refresh()
    {
        var items = _store.Snapshot();
        Tiles.Children.Clear();
        foreach (var item in items) Tiles.Children.Add(BuildTile(item));

        var hasItems = items.Count > 0;
        TileScroll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = hasItems;

        if (_addingFiles > 0)
        {
            SummaryText.Text = $"Adding {ShelfRules.Plural(_addingFiles, "file")}…";
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        }
        else if (_savingNote)
        {
            SummaryText.Text = "Saving note…";
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        }
        else
        {
            SummaryText.Text = ShelfRules.Summary(items);
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle");
        }
    }

    // ---------------------------------------------------------------- actions

    private void OpenItem(ShelfItem item) => OpenPath(item.StoredPath);

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"shelf: could not open {path}: {ex.Message}");
        }
    }

    private static void ShowInFolder(ShelfItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.StoredPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"shelf: could not show {item.StoredPath}: {ex.Message}");
        }
    }

    private void CopyPath(ShelfItem item)
    {
        try
        {
            Clipboard.SetText(item.StoredPath);
            Ctx.Notify(Glyph, "Path copied");
        }
        catch (Exception ex)
        {
            Log.Warn($"shelf: clipboard unavailable: {ex.Message}");
        }
    }

    /// <summary>Starts a drag that carries the stored file out to another app.</summary>
    private void StartDragOut(UIElement source, ShelfItem item)
    {
        if (!(File.Exists(item.StoredPath) || Directory.Exists(item.StoredPath)))
        {
            Refresh(); // the file vanished; the tile disappears on the next prune
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { item.StoredPath });
        _dragOutActive = true;
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragOutActive = false;
        }
    }
}
