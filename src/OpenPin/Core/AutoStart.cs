using Microsoft.Win32;

namespace OpenPin.Core;

/// <summary>Start with Windows via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenPin";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
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

    /// <summary>Keep the registry entry pointing at the current executable (the app may have been moved/updated).</summary>
    public static void Sync(bool enabled)
    {
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
}
