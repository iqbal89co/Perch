using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Perch.Shell;

/// <summary>Four small bars that dance while music plays and rest flat when paused.</summary>
public sealed class EqualizerBars : StackPanel
{
    private readonly Rectangle[] _bars = new Rectangle[4];
    private readonly double[] _speed = { 7.1, 9.3, 6.2, 8.4 };
    private readonly double[] _phase = { 0.0, 1.7, 3.1, 4.6 };
    private bool _playing;
    private bool _hooked;

    public EqualizerBars()
    {
        Orientation = Orientation.Horizontal;
        Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            bar.SetResourceReference(Shape.FillProperty, "IslandAccent");
            _bars[i] = bar;
            Children.Add(bar);
        }
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unhook();
    }

    public bool IsPlaying
    {
        get => _playing;
        set
        {
            _playing = value;
            Sync();
        }
    }

    private void Sync()
    {
        if (_playing && IsLoaded) Hook();
        else
        {
            Unhook();
            foreach (var b in _bars) b.Height = 3;
        }
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = Environment.TickCount64 / 1000.0;
        for (var i = 0; i < _bars.Length; i++)
        {
            var wave = Math.Abs(Math.Sin(t * _speed[i] / 2 + _phase[i])) * 0.7 +
                       Math.Abs(Math.Sin(t * _speed[(i + 1) % 4] / 3.3 + _phase[i] * 0.5)) * 0.3;
            _bars[i].Height = 3 + wave * 11;
        }
    }
}
