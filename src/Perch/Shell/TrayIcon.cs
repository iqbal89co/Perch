using System.Windows;
using Perch.Core;
using Perch.Services;
using WinForms = System.Windows.Forms;

namespace Perch.Shell;

/// <summary>Notification-area icon with Open / Settings / Start with Windows / Quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _autostart;

    public TrayIcon(IslandContext ctx, IslandWindow island)
    {
        _icon = new WinForms.NotifyIcon
        {
            Text = "Perch",
            Visible = true,
            Icon = LoadIcon(),
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Perch", null, (_, _) => island.Expand(pin: true));
        menu.Items.Add("Settings…", null, (_, _) => SettingsUi.SettingsWindow.ShowSingle());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _autostart = new WinForms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = ctx.Settings.StartWithWindows };
        _autostart.CheckedChanged += (_, _) =>
        {
            ctx.UpdateSettings(s => s.StartWithWindows = _autostart.Checked);
            Autostart.Set(_autostart.Checked);
        };
        menu.Items.Add(_autostart);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit Perch", null, (_, _) => Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) island.Expand(pin: true);
        };
        ctx.SettingsChanged += () => _autostart.Checked = ctx.Settings.StartWithWindows;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/perch.ico"));
            if (info != null) return new System.Drawing.Icon(info.Stream, new System.Drawing.Size(16, 16));
        }
        catch (Exception ex)
        {
            Log.Warn($"tray: icon unavailable ({ex.Message})");
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
