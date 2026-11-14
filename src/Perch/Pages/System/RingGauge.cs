using System.Windows;
using System.Windows.Media;

namespace Perch.Pages.SystemMonitor;

/// <summary>A circular gauge: a track ring with an arc for <see cref="Value"/> (0..1) drawn clockwise from the top.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceValue));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineWidthProperty = DependencyProperty.Register(
        nameof(LineWidth), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public double LineWidth
    {
        get => (double)GetValue(LineWidthProperty);
        set => SetValue(LineWidthProperty, value);
    }

    private static object CoerceValue(DependencyObject d, object value) =>
        double.IsNaN((double)value) ? 0.0 : Math.Clamp((double)value, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var lw = LineWidth;
        var radius = Math.Min(w, h) / 2 - lw / 2;
        if (radius <= 0) return;

        var centre = new Point(w / 2, h / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, lw), centre, radius, radius);

        var v = Value;
        if (v <= 0.001) return;

        var pen = new Pen(ValueBrush, lw) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.9995)
        {
            dc.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        var angle = v * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, v > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
