using OpenPin.Core;

namespace OpenPin.SettingsUI;

/// <summary>SETTINGS UI MODULE entry point: a single settings window, focused and navigated on repeated calls.</summary>
public sealed class SettingsUi : ISettingsUi
{
    private SettingsWindow? _window;

    public void Show(string? page = null)
    {
        if (_window is null)
        {
            _window = new SettingsWindow();
            _window.Closed += (_, _) => _window = null;
            if (page != null) _window.Navigate(page);
            _window.Show();
        }
        else
        {
            if (page != null) _window.Navigate(page);
            if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
        }
        _window.Activate();
        try { Core.Native.WindowHelper.ForceForeground(_window); }
        catch (Exception ex) { Log.Warn($"Settings foreground: {ex.Message}"); }
    }
}
