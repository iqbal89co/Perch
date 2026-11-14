using System.Windows;

namespace Perch.SettingsUi;

/// <summary>
/// STUB — replaced by the full settings window. Keep the public surface: a Window subclass with
/// a static ShowSingle() that shows one instance (or activates the open one).
/// </summary>
public class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void ShowSingle()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow { Title = "Perch Settings", Width = 400, Height = 300 };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }
}
