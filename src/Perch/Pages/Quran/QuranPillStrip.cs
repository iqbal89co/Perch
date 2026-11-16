using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Perch.Pages.Quran;

/// <summary>
/// The current ayah on the resting island: one line of Arabic, right to left, that follows the recitation.
/// Recited words are drawn in IslandForeground, the word being recited in IslandAccent, the rest in IslandSubtle.
/// A line wider than the pill slides so the current word stays near the middle; a new ayah cross-fades in.
/// Words are laid out as separate TextBlocks placed right to left by hand, so each word's position is known
/// without hit-testing text. Only a word or ayah change touches the visuals.
/// </summary>
public sealed class QuranPillStrip : Panel
{
    private const double ArabicSize = 17;
    private static readonly Duration Slide = TimeSpan.FromMilliseconds(320);

    private readonly StackPanel _line = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform _shift = new();
    private readonly List<(int Position, TextBlock Block)> _words = new();
    private int _ayahNumber = -1;
    private int _wordPosition = int.MinValue;
    private double _offset = double.NaN;

    public QuranPillStrip()
    {
        ClipToBounds = true;
        _line.HorizontalAlignment = HorizontalAlignment.Left;
        _line.RenderTransform = _shift;
        Children.Add(_line);
        SizeChanged += (_, _) => Place(animate: false);
    }

    /// <summary>As wide as the line, up to the room the pill gives; the line itself always keeps its full width.</summary>
    protected override Size MeasureOverride(Size available)
    {
        _line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = double.IsInfinity(available.Width) ? _line.DesiredSize.Width : Math.Min(_line.DesiredSize.Width, available.Width);
        return new Size(width, _line.DesiredSize.Height);
    }

    /// <summary>The line is arranged at its natural width (no layout clip), then slid and clipped by this panel.</summary>
    protected override Size ArrangeOverride(Size final)
    {
        _line.Arrange(new Rect(0, 0, _line.DesiredSize.Width, final.Height));
        return final;
    }

    /// <summary>
    /// Shows an ayah with the given word current. A position of 0 means "no word timings" (the whole ayah is
    /// drawn plainly); int.MinValue means "between words" (nothing is current yet).
    /// </summary>
    public void Show(AyahText? ayah, int wordPosition)
    {
        if (ayah == null) return;
        if (ayah.Number != _ayahNumber)
        {
            _ayahNumber = ayah.Number;
            _wordPosition = int.MinValue;
            Build(ayah);
            _line.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)));
            _offset = double.NaN;
        }
        if (wordPosition == _wordPosition && !double.IsNaN(_offset)) return;
        _wordPosition = wordPosition;
        Colour();
        Place(animate: !double.IsNaN(_offset));
    }

    public void Clear()
    {
        _ayahNumber = -1;
        _words.Clear();
        _line.Children.Clear();
    }

    private void Build(AyahText ayah)
    {
        _words.Clear();
        _line.Children.Clear();
        var font = (FontFamily)Application.Current.FindResource("QuranFont");
        var words = ayah.Words.Count > 0
            ? ayah.Words.Select(w => (w.Position, w.Text, w.IsEnd)).ToList()
            : new List<(int, string, bool)> { (0, ayah.Arabic, false) };

        // The first word sits at the right: add them in reverse so the StackPanel runs left to right.
        for (var i = words.Count - 1; i >= 0; i--)
        {
            var (position, text, isEnd) = words[i];
            var block = new TextBlock
            {
                Text = text,
                FontFamily = font,
                FontSize = isEnd ? ArabicSize - 3 : ArabicSize,
                FlowDirection = FlowDirection.RightToLeft,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3.5, 0, 3.5, 0),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 28,
            };
            _line.Children.Add(block);
            if (!isEnd) _words.Add((position, block));
            else block.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle");
        }
    }

    private void Colour()
    {
        foreach (var (position, block) in _words)
        {
            var key = _wordPosition == 0 || (_wordPosition != int.MinValue && position < _wordPosition)
                ? "IslandForeground"
                : position == _wordPosition ? "IslandAccent" : "IslandSubtle";
            block.SetResourceReference(TextBlock.ForegroundProperty, key);
        }
    }

    /// <summary>Slides the line so the current word is centred, without leaving a gap at either end.</summary>
    private void Place(bool animate)
    {
        if (_line.Children.Count == 0 || ActualWidth <= 0) return;
        _line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var total = _line.DesiredSize.Width;
        var view = ActualWidth;

        double target;
        if (total <= view) target = (view - total) / 2;
        else
        {
            var current = _words.FirstOrDefault(w => w.Position == _wordPosition).Block;
            if (current == null) target = view - total; // start of the ayah: its right edge
            else
            {
                var x = 0.0;
                foreach (UIElement child in _line.Children)
                {
                    if (ReferenceEquals(child, current)) break;
                    x += child.DesiredSize.Width;
                }
                target = Math.Clamp(view / 2 - (x + current.DesiredSize.Width / 2), view - total, 0);
            }
        }

        if (!double.IsNaN(_offset) && Math.Abs(target - _offset) < 0.5) return;
        _offset = target;
        if (animate)
            _shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, Slide) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = target;
        }
    }
}
