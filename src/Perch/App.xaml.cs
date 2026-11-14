using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perch.Core;
using Perch.Services;
using Perch.Shell;

namespace Perch;

/// <summary>
/// Startup. Command-line switches (used by the automated UI tests):
///   --data-dir DIR       keep settings/data in DIR (also skips touching the Run key)
///   --theme NAME         Midnight|Daylight|Smoke|Aurora for this run only
///   --shape NAME         Docked|Floating for this run only
///   --expand PAGEID      open the island on that page and keep it open
///   --notify TEXT        show a notice on the resting island
///   --demo-media         show a made-up playing track with lyrics instead of the system's media
///   --screenshot FILE    render the island to a PNG after --delay ms (default 1500), then exit
///   --list-pages FILE    write the discovered page ids to FILE, then exit
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private IslandWindow? _island;
    private TrayIcon? _tray;
    private IslandContext? _ctx;
    private readonly List<IIslandService> _services = new();

    internal static IslandWindow? Island { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = new Args(e.Args);
        if (args.Get("--data-dir") is { } dataDir) AppPaths.OverrideRoot(dataDir);
        var isTestRun = args.Has("--data-dir");

        _mutex = new Mutex(true, isTestRun ? $@"Local\Perch.Test.{Environment.ProcessId}" : @"Local\Perch.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("unhandled UI exception", a.Exception);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error("unobserved task exception", a.Exception);
            a.SetObserved();
        };

        Log.Info($"Perch {typeof(App).Assembly.GetName().Version} starting");
        var firstRun = !File.Exists(JsonStore.PathFor("settings"));
        _ctx = IslandContext.Initialize(Dispatcher);
        if (firstRun && !isTestRun)
        {
            // The installer's "Start when I sign in" choice wins on the very first run.
            _ctx.Settings.StartWithWindows = Autostart.IsEnabled();
            _ctx.SaveSettings();
        }
        if (args.Get("--theme") is { } theme && Enum.TryParse<IslandTheme>(theme, true, out var t)) _ctx.Settings.Theme = t;
        if (args.Get("--shape") is { } shape && Enum.TryParse<IslandShape>(shape, true, out var sh)) _ctx.Settings.Shape = sh;
        ThemeManager.Apply(_ctx.Settings);

        if (!isTestRun && Autostart.IsEnabled() != _ctx.Settings.StartWithWindows)
            Autostart.Set(_ctx.Settings.StartWithWindows);

        _island = new IslandWindow(_ctx);
        Island = _island;
        _island.Show();

        StartServices();
        _ = _ctx.Media.StartAsync();

        if (args.Get("--list-pages") is { } listFile)
        {
            File.WriteAllLines(listFile, _island.Pages.Select(p => $"{p.Id}\t{p.Title}\t{p.Order}"));
            Shutdown();
            return;
        }

        if (!isTestRun) _tray = new TrayIcon(_ctx, _island);

        if (args.Has("--demo-media")) DemoMedia.Show(_ctx);
        if (args.Get("--notify") is { } text) _ctx.Notify("", text, TimeSpan.FromSeconds(30));
        if (args.Get("--expand") is { } page) _island.Expand(page, pin: true);

        if (args.Get("--screenshot") is { } shot)
        {
            var delay = int.TryParse(args.Get("--delay"), out var d) ? d : 1500;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    _island.SettleNow();
                    SaveScreenshot(_island, shot);
                }
                catch (Exception ex)
                {
                    Log.Error("screenshot failed", ex);
                }
                Shutdown();
            };
            timer.Start();
        }
    }

    private void StartServices()
    {
        var types = typeof(App).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IIslandService).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null);
        foreach (var type in types)
        {
            try
            {
                var service = (IIslandService)Activator.CreateInstance(type)!;
                service.Start(_ctx!);
                _services.Add(service);
                Log.Info($"service: started {type.Name}");
            }
            catch (Exception ex)
            {
                Log.Error($"service: {type.Name} failed to start", ex.InnerException ?? ex);
            }
        }
    }

    internal static void SaveScreenshot(Window window, string file)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Paint a neutral desktop-like backdrop so transparent areas are visible in the PNG.
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x4B, 0x5D, 0x7A), Color.FromRgb(0x8A, 0x9B, 0xB0), 90), null,
                new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(backdrop);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var fs = File.Create(file);
        encoder.Save(fs);
        Log.Info($"screenshot saved to {file}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            foreach (var s in _services.OfType<IDisposable>()) s.Dispose();
            _ctx?.SaveSettings();
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("exit cleanup failed", ex);
        }
        Log.Info("Perch stopped");
        base.OnExit(e);
    }

    private sealed class Args
    {
        private readonly string[] _a;
        public Args(string[] a) => _a = a;
        public bool Has(string name) => _a.Contains(name, StringComparer.OrdinalIgnoreCase);
        public string? Get(string name)
        {
            var i = Array.FindIndex(_a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < _a.Length ? _a[i + 1] : null;
        }
    }
}
