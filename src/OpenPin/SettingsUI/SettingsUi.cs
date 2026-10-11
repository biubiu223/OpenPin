using System.Diagnostics;
using OpenPin.Core;

namespace OpenPin.SettingsUI;

/// <summary>SETTINGS UI MODULE entry point. PHASE-0 STUB: opens settings.json in the default editor.</summary>
public sealed class SettingsUi : ISettingsUi
{
    public void Show(string? page = null)
    {
        AppServices.Settings.Save();
        Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
    }
}
