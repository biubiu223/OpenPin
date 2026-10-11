using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenPin.Core;

namespace OpenPin.SettingsUI;

/// <summary>DWM backdrop / dark title bar for the settings window.</summary>
internal static class SettingsNative
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Mica needs Windows 11 22H2 (build 22621) for DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public static bool SupportsMica => Environment.OSVersion.Version.Build >= 22621;

    public static void Apply(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int d = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
            if (SupportsMica)
            {
                int b = DWMSBT_MAINWINDOW;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref b, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"DWM attributes failed: {ex.Message}");
        }
    }

    /// <summary>HKCU AppsUseLightTheme (1 light, 0 dark), null if unknown.</summary>
    public static int? AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v : null;
        }
        catch { return null; }
    }
}
