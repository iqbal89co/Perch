using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Perch.Pages.Shelf;

/// <summary>Tile construction and the mouse handling for tiles (open, drag out, menu).</summary>
public partial class ShelfPage
{
    private const double TileSize = 92;
    private const double TileMargin = 3;

    private ShelfItem? _pressItem;      // tile pressed with the left button, waiting to see if it becomes a drag
    private Point _pressPoint;

    private Border BuildTile(ShelfItem item)
    {
        var path = item.StoredPath;
        var isFolder = Directory.Exists(path);

        var tile = new Border
        {
            Width = TileSize,
            Height = TileSize,
            Margin = new Thickness(TileMargin),
            CornerRadius = new CornerRadius(12),
            Cursor = Cursors.Hand,
            ToolTip = item.OriginalPath ?? item.StoredPath,
            ContextMenu = BuildMenu(item),
        };
        tile.SetResourceReference(Border.BackgroundProperty, "IslandCard");

        var body = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = TileSize - 12,
        };
        body.Children.Add(BuildIcon(item, path, isFolder));
        body.Children.Add(new TextBlock
        {
            Text = ShelfRules.DisplayName(item.Name),
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var grid = new Grid();
        grid.Children.Add(body);

        if (item.IsReference)
        {
            var badge = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 6, 0, 0),
                ToolTip = "Kept where it is. Not copied to the shelf.",
                Child = IconText("", 9, "IslandOnAccent"), // link
            };
            badge.SetResourceReference(Border.BackgroundProperty, "IslandAccent");
            grid.Children.Add(badge);
        }

        // Remove button: only visible while the pointer is over the tile.
        var remove = new Button
        {
            Style = (Style)FindResource("PerchIconButton"),
            Content = "", // cancel
            Width = 22,
            Height = 22,
            FontSize = 9,
            Margin = new Thickness(0, 4, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            ToolTip = "Remove from the shelf",
        };
        // Keep presses on the button from starting a tile drag or double-click open.
        remove.MouseLeftButtonDown += (_, e) => e.Handled = true;
        remove.Click += (_, _) => RemoveItem(item.Id);
        grid.Children.Add(remove);

        tile.Child = grid;

        tile.MouseEnter += (_, _) =>
        {
            tile.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");
            remove.Visibility = Visibility.Visible;
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.SetResourceReference(Border.BackgroundProperty, "IslandCard");
            remove.Visibility = Visibility.Collapsed;
        };

        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                _pressItem = null;
                OpenItem(item);
                e.Handled = true;
                return;
            }
            _pressItem = item;
            _pressPoint = e.GetPosition(this);
        };
        tile.MouseLeftButtonUp += (_, _) => _pressItem = null;
        tile.MouseMove += (_, e) =>
        {
            if (_pressItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            var dx = Math.Abs(p.X - _pressPoint.X);
            var dy = Math.Abs(p.Y - _pressPoint.Y);
            if (Math.Max(dx, dy) <= SystemParameters.MinimumHorizontalDragDistance) return;

            var dragged = _pressItem;
            _pressItem = null;
            StartDragOut(tile, dragged);
        };

        return tile;
    }

    /// <summary>A 40 x 40 slot: folder glyph, image thumbnail (rounded), or the file type's shell icon.</summary>
    private UIElement BuildIcon(ShelfItem item, string path, bool isFolder)
    {
        const double slot = 40;
        if (isFolder)
            return Slot(IconText("", 30, "IslandAccent")); // folder

        // Images get a rounded thumbnail; everything else (or an image WPF cannot decode) uses the shell icon.
        var thumb = ShelfRules.IsImage(item.Name) ? ShelfIcons.Thumbnail(path) : null;
        if (thumb != null)
        {
            var image = new Image { Source = thumb, Stretch = Stretch.UniformToFill, Width = slot, Height = slot };
            return new Border
            {
                Width = slot,
                Height = slot,
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = image,
            };
        }

        var icon = ShelfIcons.FileIcon(path);
        if (icon == null) return Slot(IconText("", 26, "IslandSubtle")); // generic document
        return Slot(new Image { Source = icon, Stretch = Stretch.Uniform, Width = slot, Height = slot });
    }

    private static UIElement Slot(UIElement content) => new Grid
    {
        Width = 40,
        Height = 40,
        HorizontalAlignment = HorizontalAlignment.Center,
        Children = { content },
    };

    private static TextBlock IconText(string text, double size, string brushKey)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = size,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private ContextMenu BuildMenu(ShelfItem item)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "IslandCard");
        menu.SetResourceReference(Control.ForegroundProperty, "IslandForeground");
        menu.FontFamily = (FontFamily)FindResource("UiFont");

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenItem(item);
        var reveal = new MenuItem { Header = "Show in folder" };
        reveal.Click += (_, _) => ShowInFolder(item);
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) => CopyPath(item);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) => RemoveItem(item.Id);

        menu.Items.Add(open);
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }
}
