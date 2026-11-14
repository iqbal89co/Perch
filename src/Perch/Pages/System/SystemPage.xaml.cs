using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.SystemMonitor;

/// <summary>
/// Live CPU, memory, network, battery, disks and top processes. Sampling only runs while the tab is shown:
/// a 1 s timer reads the cheap counters, and process CPU is measured every 2 s on a background task.
/// The CPU and network history lives for the whole session, so it survives tab switches.
/// </summary>
public partial class SystemPage : IslandPage
{
    private const int HistoryLength = 60;
    private const int ProcessCount = 4;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SystemSampler _sampler = new();
    private readonly ProcessSampler _processes = new();
    private readonly RingBuffer<double> _cpuHistory = new(HistoryLength);
    private readonly RingBuffer<double> _downHistory = new(HistoryLength);
    private readonly RingBuffer<double> _upHistory = new(HistoryLength);
    private int _ticks;
    private bool _processBusy;
    private bool? _batteryShown;

    public override string Id => "system";
    public override string Title => "System";
    public override string Glyph => ""; // Segoe MDL2 "Processing"
    public override int Order => 60;

    public SystemPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTick();
        CoresText.Text = $"{Environment.ProcessorCount} logical cores";
        DownText.Text = "↓ —";
        UpText.Text = "↑ —";
        // Take a baseline now so the first live read after showing the tab already has deltas.
        _sampler.Read();
    }

    public override void OnShown()
    {
        Sample();
        StartProcessSample();
        _timer.Start();
    }

    public override void OnHidden() => _timer.Stop();

    private void OnTick()
    {
        Sample();
        if (++_ticks % 2 == 0) StartProcessSample();
    }

    private void Sample()
    {
        try
        {
            var reading = _sampler.Read();
            if (reading.Cpu is { } cpu) _cpuHistory.Add(cpu);
            if (reading.DownBytesPerSec is { } down) _downHistory.Add(down);
            if (reading.UpBytesPerSec is { } up) _upHistory.Add(up);
            Render(reading);
        }
        catch (Exception ex)
        {
            Log.Error("system sample failed", ex);
        }
    }

    private void Render(SystemReading r)
    {
        if (r.Cpu is { } cpu) ShowRing(CpuRing, CpuText, cpu, warn: cpu > 0.9);
        CpuSpark.SetData(_cpuHistory.ToArray(), 1);

        if (r.Memory is { } mem)
        {
            ShowRing(MemRing, MemText, mem.UsedFraction, warn: mem.UsedFraction > 0.9);
            MemDetail.Text = $"{SystemMath.FormatGiB(mem.UsedBytes)} / {SystemMath.FormatGiB(mem.TotalBytes)} GB";
        }

        if (r.DownBytesPerSec is { } down) DownText.Text = $"↓ {Format.Bytes(down)}/s";
        if (r.UpBytesPerSec is { } up) UpText.Text = $"↑ {Format.Bytes(up)}/s";
        // Both lines share one scale so their relative size reads correctly; 16 KB/s is the floor.
        var netMax = Math.Max(16 * 1024.0, Math.Max(MaxOf(_downHistory), MaxOf(_upHistory)));
        DownSpark.SetData(_downHistory.ToArray(), netMax);
        UpSpark.SetData(_upHistory.ToArray(), netMax);

        RenderBattery(r.Battery);
        RenderDisks(r.Disks);
        UptimeText.Text = SystemMath.FormatUptime(r.Uptime);
    }

    private void RenderBattery(BatteryReading? battery)
    {
        SetBatteryVisible(battery != null);
        if (battery is not { } b) return;

        var level = b.Fraction;
        if (level < 0)
        {
            ShowRing(BatRing, BatText, 0, warn: false, textOverride: "--");
        }
        else
        {
            ShowRing(BatRing, BatText, level, warn: level < 0.15 && !b.Charging);
        }
        BatDetail.Text = SystemMath.BatteryStatusText(b.Charging, b.PluggedIn);
    }

    /// <summary>Desktops have no battery: the card goes and the top row re-flows into three columns.</summary>
    private void SetBatteryVisible(bool visible)
    {
        if (_batteryShown == visible) return;
        _batteryShown = visible;
        BatteryCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BatteryColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NetworkCard.Margin = visible ? new Thickness(0, 0, 8, 0) : new Thickness(0);
    }

    private void RenderDisks(IReadOnlyList<DiskReading> disks)
    {
        DiskList.Children.Clear();
        foreach (var disk in disks) DiskList.Children.Add(CreateDiskRow(disk));
        DiskHint.Visibility = disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement CreateDiskRow(DiskReading disk)
    {
        var fraction = disk.TotalBytes > 0 ? Math.Clamp((double)disk.UsedBytes / disk.TotalBytes, 0, 1) : 0;

        var header = new DockPanel();
        var size = new TextBlock
        {
            Text = $"{SystemMath.FormatGiB(disk.UsedBytes)} / {SystemMath.FormatGiB(disk.TotalBytes)} GB",
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 11,
        };
        DockPanel.SetDock(size, Dock.Right);
        header.Children.Add(size);
        header.Children.Add(new TextBlock
        {
            Text = disk.Name,
            Style = (Style)FindResource("PerchBody"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        });

        // Two star columns split the bar at the used fraction; the track spans both.
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        fill.SetResourceReference(Border.BackgroundProperty, fraction > 0.9 ? "IslandDanger" : "IslandAccent");
        bar.Children.Add(track);
        bar.Children.Add(fill);

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        row.Children.Add(header);
        row.Children.Add(bar);
        return row;
    }

    private void ShowProcesses(IReadOnlyList<ProcessUsage> list)
    {
        ProcessList.Children.Clear();
        foreach (var p in list) ProcessList.Children.Add(CreateProcessRow(p));
        ProcessHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement CreateProcessRow(ProcessUsage process)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock { Text = process.Name, Style = (Style)FindResource("PerchBody"), FontSize = 12.5 };
        var pct = new TextBlock
        {
            Text = SystemMath.Percent(process.Fraction),
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
        };
        pct.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        Grid.SetColumn(pct, 1);
        grid.Children.Add(name);
        grid.Children.Add(pct);
        return grid;
    }

    private void StartProcessSample()
    {
        if (_processBusy) return;
        _processBusy = true;
        Task.Run(() => _processes.Sample(ProcessCount))
            .ContinueWith(t =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _processBusy = false;
                    if (t.IsCompletedSuccessfully) ShowProcesses(t.Result);
                    else if (t.Exception is { } ex) Log.Error("process sample failed", ex.GetBaseException());
                });
            }, TaskScheduler.Default);
    }

    private static void ShowRing(RingGauge ring, TextBlock text, double fraction, bool warn, string? textOverride = null)
    {
        ring.Value = fraction;
        ring.SetResourceReference(RingGauge.ValueBrushProperty, warn ? "IslandDanger" : "IslandAccent");
        text.Text = textOverride ?? SystemMath.Percent(fraction);
    }

    private static double MaxOf(RingBuffer<double> history) => history.Count == 0 ? 0 : history.Max();
}
