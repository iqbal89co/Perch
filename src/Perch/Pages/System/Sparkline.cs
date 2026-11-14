using System.Windows;
using System.Windows.Media;

namespace Perch.Pages.SystemMonitor;

/// <summary>
/// A small line chart with a soft fill. Values are scaled to the max given to <see cref="SetData"/>; the first
/// sample sits at the left edge and the newest at the right.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _values = Array.Empty<double>();
    private double _max = 1;

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Replaces the series. <paramref name="max"/> is the value that maps to the top edge.</summary>
    public void SetData(IReadOnlyList<double> values, double max)
    {
        _values = values.ToArray();
        _max = max > 0 ? max : 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4 || _values.Length < 2) return;

        // Spread the samples across the full width so a young series is not squeezed into one corner.
        var step = w / (_values.Length - 1);
        var pad = 1.0; // keep the stroke inside the box
        var points = new Point[_values.Length];
        for (var i = 0; i < _values.Length; i++)
        {
            var y = h - pad - Math.Clamp(_values[i] / _max, 0, 1) * (h - 2 * pad);
            points[i] = new Point(i * step, y);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();

        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, h), isFilled: true, isClosed: true);
            ctx.LineTo(points[0], isStroked: false, isSmoothJoin: false);
            ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: false, isSmoothJoin: true);
            ctx.LineTo(new Point(points[^1].X, h), isStroked: false, isSmoothJoin: false);
        }
        fill.Freeze();

        var brush = Stroke;
        if (brush is SolidColorBrush solid)
        {
            var tint = new SolidColorBrush(Color.FromArgb(0x33, solid.Color.R, solid.Color.G, solid.Color.B));
            tint.Freeze();
            dc.DrawGeometry(tint, null, fill);
        }

        var pen = new Pen(brush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
