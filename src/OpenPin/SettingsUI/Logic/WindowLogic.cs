using System.Windows;
using AppThemeMode = OpenPin.Core.Settings.ThemeMode;

namespace OpenPin.SettingsUI.Logic;

/// <summary>Light / dark resolution for the settings window (UI-03).</summary>
internal static class ThemeResolver
{
    /// <summary>
    /// True when the window should be dark. <paramref name="systemAppsUseLightTheme"/> is Windows' AppsUseLightTheme
    /// value (null = unknown → light).
    /// </summary>
    public static bool IsDark(AppThemeMode mode, int? systemAppsUseLightTheme) =>
        OpenPin.Core.Theme.ThemeLogic.ResolveIsDark(mode, systemAppsUseLightTheme);
}

/// <summary>Persisted size/position of the settings window, physical pixels.</summary>
internal sealed class SettingsWindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public string LastPage { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasBounds => Width > 0 && Height > 0;
}

/// <summary>Validation of restored window bounds against the current monitor layout.</summary>
internal static class WindowBoundsLogic
{
    public const int MinVisibleWidthPx = 160;
    public const int MinVisibleHeightPx = 60;

    /// <summary>
    /// Returns the bounds to use, or null when the saved rectangle is no longer sufficiently visible on any work area
    /// (monitor unplugged, resolution changed). The size is capped to the work area that contains most of the window.
    /// </summary>
    public static Int32Rect? Validate(Int32Rect saved, IReadOnlyList<Int32Rect> workAreasPx)
    {
        if (saved.Width <= 0 || saved.Height <= 0 || workAreasPx.Count == 0) return null;

        Int32Rect? best = null;
        long bestArea = 0;
        foreach (var wa in workAreasPx)
        {
            var (w, h) = IntersectSize(saved, wa);
            if ((long)w * h > bestArea) { bestArea = (long)w * h; best = wa; }
        }
        if (best is not { } area) return null;

        // The title bar must be reachable: require a visible strip at the top of the window.
        var top = new Int32Rect(saved.X, saved.Y, saved.Width, Math.Min(saved.Height, 40));
        bool titleVisible = workAreasPx.Any(wa =>
        {
            var (w, h) = IntersectSize(top, wa);
            return w >= MinVisibleWidthPx && h >= Math.Min(MinVisibleHeightPx, top.Height) / 2;
        });
        if (!titleVisible) return null;

        int width = Math.Min(saved.Width, area.Width);
        int height = Math.Min(saved.Height, area.Height);
        // Keep the whole window inside the work area it mostly belongs to.
        int x = Math.Clamp(saved.X, area.X, area.X + area.Width - width);
        int y = Math.Clamp(saved.Y, area.Y, area.Y + area.Height - height);
        return new Int32Rect(x, y, width, height);
    }

    private static (int W, int H) IntersectSize(Int32Rect a, Int32Rect b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return (Math.Max(0, r - l), Math.Max(0, btm - t));
    }
}
