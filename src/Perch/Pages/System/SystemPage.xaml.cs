using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Perch.Core;

namespace Perch.Pages.SystemMonitor;

/// <summary>
/// Live CPU, GPU, memory, power, network, disks and top processes. Sampling only runs while the tab is shown:
/// a 1 s timer reads the cheap counters, processes are measured every 2 s and the GPU every 2 s (5 s when slow),
/// both on background tasks. Histories live for the whole session, so they survive tab switches.
/// </summary>
public partial class SystemPage : IslandPage
{
    private const int HistoryLength = 60;
    private const int ProcessCount = 4;
    private const int ProcessEveryTicks = 2;
    private const long GpuRetryMs = 5000;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SystemSampler _sampler = new();
    private readonly GpuSampler _gpu = new();
    private readonly ProcessSampler _processes = new();
    private readonly RingBuffer<double> _cpuHistory = new(HistoryLength);
    private readonly RingBuffer<double> _gpuHistory = new(HistoryLength);
    private readonly RingBuffer<double> _memHistory = new(HistoryLength);
    private readonly RingBuffer<double> _batHistory = new(HistoryLength);
    private readonly RingBuffer<double> _downHistory = new(HistoryLength);
    private readonly RingBuffer<double> _upHistory = new(HistoryLength);
    private bool _processBusy;
    private bool _processHasData;
    private int _ticksSinceProcess;
    private bool _gpuBusy;
    private bool? _gpuAvailable;
    private long _gpuDueAtMs;

    public override string Id => "system";
    public override string Title => "System";
    public override string Glyph => ""; // Segoe MDL2 "Processing"
    public override int Order => 60;

    public SystemPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTick();
        Unloaded += (_, _) =>
        {
            _sampler.Dispose();
            _gpu.Dispose();
        };
        DownText.Text = "↓ —";
        UpText.Text = "↑ —";
        // Take a baseline now so the first live read after showing the tab already has deltas.
        _sampler.Read();
    }

    public override void OnShown()
    {
        Sample();
        // The first process sample only sets the baseline; the first tick after this gives the list.
        StartProcessSample();
        _gpuDueAtMs = 0;
        StartGpuSample();
        _timer.Start();
    }

    public override void OnHidden() => _timer.Stop();

    private void OnTick()
    {
        Sample();
        if (!_processHasData || ++_ticksSinceProcess >= ProcessEveryTicks)
        {
            _ticksSinceProcess = 0;
            StartProcessSample();
        }
        if (Environment.TickCount64 >= _gpuDueAtMs) StartGpuSample();
    }

    private void Sample()
    {
        try
        {
            var reading = _sampler.Read();
            if (reading.Cpu is { } cpu) _cpuHistory.Add(cpu);
            if (reading.Memory is { } mem) _memHistory.Add(mem.UsedFraction);
            if (reading.Battery is { Fraction: >= 0 } bat) _batHistory.Add(bat.Fraction);
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
        if (r.Cpu is { } cpu) ShowValue(CpuValue, SystemMath.Percent(cpu), warn: cpu > 0.9);
        CpuDetail.Text = SystemMath.CpuDetail(r.CpuMhz, Environment.ProcessorCount);
        CpuSpark.SetData(_cpuHistory.ToArray(), 1);

        if (r.Memory is { } mem)
        {
            ShowValue(MemValue, SystemMath.Percent(mem.UsedFraction), warn: mem.UsedFraction > 0.9);
            MemDetail.Text = SystemMath.MemoryDetail(mem.UsedBytes, mem.TotalBytes, mem.AvailableBytes);
        }
        MemSpark.SetData(_memHistory.ToArray(), 1);

        RenderPower(r.Battery);
        RenderNetwork(r);

        RenderDisks(r.Disks);
        DiskIoText.Text = SystemMath.DiskActivity(r.DiskIo.ReadBytesPerSec, r.DiskIo.WriteBytesPerSec);

        FooterText.Text = SystemMath.Footer(
            SystemMath.CleanCpuName(SystemInfo.CpuName),
            SystemMath.WindowsLabel(SystemInfo.WindowsBuild),
            r.Uptime);
    }

    private void RenderPower(BatteryReading? battery)
    {
        if (battery is not { } b)
        {
            ShowValue(PowerValue, "AC power", warn: false);
            PowerDetail.Text = "No battery";
            PowerSpark.Visibility = Visibility.Collapsed;
            return;
        }

        PowerSpark.Visibility = Visibility.Visible;
        if (b.Fraction < 0)
        {
            ShowValue(PowerValue, "--", warn: false);
        }
        else
        {
            ShowValue(PowerValue, SystemMath.Percent(b.Fraction), warn: b.Fraction < 0.15 && !b.Charging);
        }
        PowerDetail.Text = SystemMath.PowerStatusText(b.Charging, b.PluggedIn, b.TimeLeft);
        PowerSpark.SetData(_batHistory.ToArray(), 1);
    }

    private void RenderNetwork(SystemReading r)
    {
        if (r.DownBytesPerSec is { } down) DownText.Text = $"↓ {Format.Bytes(down)}/s";
        if (r.UpBytesPerSec is { } up) UpText.Text = $"↑ {Format.Bytes(up)}/s";

        var n = r.Network;
        NetDetail.Text = n.Adapter is null
            ? "No network"
            : n.Ipv4 is null ? n.Adapter : $"{n.Adapter} · {n.Ipv4}";
        NetTotals.Text = SystemMath.NetworkTotals(n.Received, n.Sent);

        // Both lines share one scale so their relative size reads correctly; 16 KB/s is the floor.
        var netMax = Math.Max(16 * 1024.0, Math.Max(MaxOf(_downHistory), MaxOf(_upHistory)));
        DownSpark.SetData(_downHistory.ToArray(), netMax);
        UpSpark.SetData(_upHistory.ToArray(), netMax);
    }

    private void ShowGpu(GpuReading g)
    {
        _gpuDueAtMs = Environment.TickCount64 + (long)SystemMath.GpuSampleInterval(g.SampleMs).TotalMilliseconds;

        if (!g.Available)
        {
            // No GPU Engine counters on this PC: the tile goes and the others share the row.
            _gpuAvailable = false;
            GpuCard.Visibility = Visibility.Collapsed;
            GpuColumn.Width = new GridLength(0);
            return;
        }

        if (_gpuAvailable != true)
        {
            _gpuAvailable = true;
            GpuCard.Visibility = Visibility.Visible;
            GpuColumn.Width = new GridLength(1, GridUnitType.Star);
        }

        if (g.Utilization is { } u)
        {
            _gpuHistory.Add(u / 100);
            ShowValue(GpuValue, SystemMath.Percent(u / 100), warn: u > 90);
        }
        GpuDetail.Text = SystemMath.VramText(g.DedicatedBytes);
        GpuSpark.SetData(_gpuHistory.ToArray(), 1);
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
            FontSize = 10.5,
            Margin = new Thickness(6, 0, 0, 0),
        };
        DockPanel.SetDock(size, Dock.Right);
        header.Children.Add(size);
        header.Children.Add(new TextBlock
        {
            Text = disk.Name,
            Style = (Style)FindResource("PerchBody"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
        });

        // Two star columns split the bar at the used fraction; the track spans both.
        var bar = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(1.5) };
        track.SetResourceReference(Border.BackgroundProperty, "IslandCardHover");
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(1.5) };
        fill.SetResourceReference(Border.BackgroundProperty, fraction > 0.9 ? "IslandDanger" : "IslandAccent");
        bar.Children.Add(track);
        bar.Children.Add(fill);

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(header);
        row.Children.Add(bar);
        return row;
    }

    private void ShowProcesses(IReadOnlyList<ProcessUsage> list)
    {
        _processHasData = list.Count > 0;
        ProcessList.Children.Clear();
        foreach (var p in list) ProcessList.Children.Add(CreateProcessRow(p));
        ProcessHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One line per process: name, CPU share of all cores, and working set.</summary>
    private UIElement CreateProcessRow(ProcessUsage process)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = process.Name,
            Style = (Style)FindResource("PerchBody"),
            FontSize = 11,
        };
        var cpu = new TextBlock
        {
            Text = SystemMath.Percent(process.Fraction),
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            MinWidth = 36,
            Margin = new Thickness(6, 0, 0, 0),
        };
        cpu.SetResourceReference(TextBlock.ForegroundProperty, "IslandAccent");
        var memory = new TextBlock
        {
            Text = Format.Bytes(process.MemoryBytes),
            Style = (Style)FindResource("PerchCaption"),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            MinWidth = 56,
            Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(cpu, 1);
        Grid.SetColumn(memory, 2);
        grid.Children.Add(name);
        grid.Children.Add(cpu);
        grid.Children.Add(memory);
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

    private void StartGpuSample()
    {
        if (_gpuBusy || _gpuAvailable == false) return;
        _gpuBusy = true;
        Task.Run(() => _gpu.Sample())
            .ContinueWith(t =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _gpuBusy = false;
                    if (t.IsCompletedSuccessfully)
                    {
                        ShowGpu(t.Result);
                    }
                    else
                    {
                        _gpuDueAtMs = Environment.TickCount64 + GpuRetryMs;
                        if (t.Exception is { } ex) Log.Error("gpu sample failed", ex.GetBaseException());
                    }
                });
            }, TaskScheduler.Default);
    }

    private static void ShowValue(TextBlock text, string value, bool warn)
    {
        text.Text = value;
        text.SetResourceReference(TextBlock.ForegroundProperty, warn ? "IslandDanger" : "IslandForeground");
    }

    private static double MaxOf(RingBuffer<double> history) => history.Count == 0 ? 0 : history.Max();
}
