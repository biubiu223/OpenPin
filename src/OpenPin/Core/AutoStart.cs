using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenPin.Core;

/// <summary>
/// Start with Windows.
/// <list type="bullet">
/// <item>Unpackaged (zip / portable): HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin rights needed).</item>
/// <item>MSIX: the <c>windows.startupTask</c> extension "OpenPinStartup" declared in packaging/msix/AppxManifest.xml
/// (registry writes of packaged apps are virtualized, so the Run key would not work).</item>
/// </list>
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenPin";
    private const string StartupTaskId = "OpenPinStartup";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    /// <summary>True when running from an MSIX package (has package identity).</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool? _packagedEnabled;

    public static bool IsEnabled
    {
        get
        {
            if (IsPackaged) return _packagedEnabled ?? false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        if (IsPackaged)
        {
            _ = SetPackagedAsync(enabled);
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update auto-start registration", ex);
        }
    }

    /// <summary>Keep the registration in line with the setting (the app may have been moved / updated).</summary>
    public static void Sync(bool enabled)
    {
        if (IsPackaged)
        {
            _ = SyncPackagedAsync(enabled);
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            if (enabled && current != Command) Set(true);
            else if (!enabled && current != null) Set(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Auto-start sync failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ MSIX

    private static async Task SetPackagedAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (enabled)
            {
                // May show a one-time system prompt; the user (or policy) can refuse.
                var state = await task.RequestEnableAsync();
                _packagedEnabled = state is Windows.ApplicationModel.StartupTaskState.Enabled
                    or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                if (_packagedEnabled != true) Log.Warn($"Startup task not enabled: {state}");
            }
            else
            {
                task.Disable();
                _packagedEnabled = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the MSIX startup task", ex);
        }
    }

    private static async Task SyncPackagedAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            _packagedEnabled = task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            // Only turn it off automatically; enabling needs explicit user action (it can prompt).
            if (!enabled && _packagedEnabled == true) { task.Disable(); _packagedEnabled = false; }
        }
        catch (Exception ex)
        {
            Log.Warn($"MSIX startup task sync failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    private static bool DetectPackaged()
    {
        try
        {
            const int APPMODEL_ERROR_NO_PACKAGE = 15700;
            int length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch
        {
            return false;
        }
    }
}
