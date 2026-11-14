using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.Tasks;

/// <summary>A small to-do list: type and press Enter, tick off, reorder, edit inline, clear done.</summary>
public partial class TasksPage : IslandPage
{
    private const string CheckGlyph = "";
    private const string UpGlyph = "";
    private const string DownGlyph = "";
    private const string DeleteGlyph = "";

    private readonly TaskStore _store = new();
    private long? _editingId;
    private TextBox? _editor;
    private bool _rendering;

    public override string Id => "tasks";
    public override string Title => "Tasks";
    public override string Glyph => ""; // checklist
    public override int Order => 40;

    public TasksPage()
    {
        InitializeComponent();
        _store.Load();
        InputBox.KeyDown += OnInputKeyDown;
        InputBox.TextChanged += (_, _) =>
            Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Click += (_, _) => AddFromInput();
        ClearDoneButton.Click += (_, _) =>
        {
            CancelEdit();
            _store.ClearDone();
            Render();
        };
        Render();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddFromInput();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            InputBox.Clear();
        }
    }

    private void AddFromInput()
    {
        if (_store.Add(InputBox.Text) != null) InputBox.Clear();
        Render();
        InputBox.Focus();
    }

    private void ToggleTask(long id)
    {
        if (_editingId is { } editing && _editor != null) CommitEdit(editing, _editor.Text);
        var openBefore = _store.OpenCount;
        var item = _store.Toggle(id);
        if (item is { Done: true } && openBefore > 0 && _store.OpenCount == 0)
            Ctx.Notify(CheckGlyph, "All tasks done");
        Render();
    }

    private void Render()
    {
        _rendering = true; // removing a focused editor raises LostFocus; that must not commit mid-render
        try
        {
            ListPanel.Children.Clear();
            var items = _store.Items;
            var hasItems = items.Count > 0;
            ListScroll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            FooterPanel.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            if (!hasItems) return;

            var open = items.Where(t => !t.Done).Select(t => t.Id).ToList();
            CountText.Text = open.Count == 0 ? "All done" : $"{open.Count} left";
            ClearDoneButton.Visibility = _store.HasDone ? Visibility.Visible : Visibility.Collapsed;

            foreach (var item in items)
            {
                var index = open.IndexOf(item.Id);
                ListPanel.Children.Add(BuildRow(item, canUp: index > 0, canDown: index >= 0 && index < open.Count - 1));
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private UIElement BuildRow(TaskItem item, bool canUp, bool canDown)
    {
        var id = item.Id;
        var row = new Border { Style = (Style)FindResource("TaskRow") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Check circle: hollow when open, accent with a tick when done.
        var circle = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = item.Done ? "Mark as not done" : "Mark as done",
        };
        if (item.Done)
        {
            circle.SetResourceReference(Border.BackgroundProperty, "IslandAccent");
            circle.SetResourceReference(Border.BorderBrushProperty, "IslandAccent");
            circle.Child = new TextBlock
            {
                Text = CheckGlyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ((TextBlock)circle.Child).SetResourceReference(TextBlock.ForegroundProperty, "IslandOnAccent");
        }
        else
        {
            circle.Background = Brushes.Transparent;
            circle.SetResourceReference(Border.BorderBrushProperty, "IslandSubtle");
        }
        circle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleTask(id);
        };
        grid.Children.Add(circle);

        // Text, or the inline editor while this row is being edited.
        FrameworkElement content;
        if (_editingId == id && _editor != null)
        {
            content = _editor;
        }
        else
        {
            var text = new TextBlock
            {
                Text = item.Text,
                Style = (Style)FindResource("PerchBody"),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            if (item.Done)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle");
                text.TextDecorations = TextDecorations.Strikethrough;
            }
            text.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                BeginEdit(id);
            };
            content = text;
        }
        Grid.SetColumn(content, 2);
        grid.Children.Add(content);

        // Row actions, shown on hover (space is reserved, so the text does not jump).
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
        if (!item.Done)
        {
            actions.Children.Add(ActionButton(UpGlyph, "Move up", canUp, () => { _store.MoveUp(id); Render(); }));
            actions.Children.Add(ActionButton(DownGlyph, "Move down", canDown, () => { _store.MoveDown(id); Render(); }));
        }
        actions.Children.Add(ActionButton(DeleteGlyph, "Delete", true, () =>
        {
            if (_editingId == id) CancelEdit();
            _store.Remove(id);
            Render();
        }));
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        row.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => actions.Visibility = Visibility.Hidden;
        row.Child = grid;
        return row;
    }

    private Button ActionButton(string glyph, string tip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = tip,
            IsEnabled = enabled,
            Style = (Style)FindResource("TaskActionButton"),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void BeginEdit(long id)
    {
        // Save any other row still being edited before switching.
        if (_editingId is { } previous && _editor != null) CommitEdit(previous, _editor.Text);

        var item = _store.Items.FirstOrDefault(t => t.Id == id);
        if (item == null) return;

        var box = new TextBox
        {
            Text = item.Text,
            Style = (Style)FindResource("PerchTextBox"),
            MaxLength = TaskStore.MaxLength,
            Height = 28,
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitEdit(id, box.Text);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEdit();
            }
        };
        box.LostFocus += (_, _) => CommitEdit(id, box.Text);

        _editingId = id;
        _editor = box;
        Render();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_editor != box) return;
            box.Focus();
            box.SelectAll();
        });
    }

    private void CommitEdit(long id, string text)
    {
        if (_rendering || _editingId != id) return; // LostFocus also fires when the editor is removed
        _editingId = null;
        _editor = null;
        _store.Edit(id, text);
        Render();
    }

    private void CancelEdit()
    {
        if (_editingId == null) return;
        _editingId = null;
        _editor = null;
        Render();
    }
}
