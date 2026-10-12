using System.Windows.Media;
using OpenPin.Core.Imaging;
using AppThemeMode = OpenPin.Core.Settings.ThemeMode;

namespace OpenPin.Core.Theme;

/// <summary>Pure theme decisions (UI-03): light / dark resolution and accent handling. Unit tested.</summary>
public static class ThemeLogic
{
    /// <summary>The accent used when the setting is empty or unparsable.</summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0x16, 0x77, 0xFF);

    /// <summary>
    /// True when the app should be dark. <paramref name="systemAppsUseLightTheme"/> is Windows' AppsUseLightTheme
    /// registry value (1 light, 0 dark, null unknown → light).
    /// </summary>
    public static bool ResolveIsDark(AppThemeMode mode, int? systemAppsUseLightTheme) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => systemAppsUseLightTheme == 0,
    };

    /// <summary>
    /// Accent from the settings text ("#1677FF", "rgb(…)", …), always opaque. Falls back to <paramref name="fallback"/>
    /// (or <see cref="DefaultAccent"/>) when the text is empty, unparsable or fully transparent.
    /// </summary>
    public static Color ParseAccent(string? text, Color? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(text) && ColorText.TryParse(text, out var c) && c.A > 0)
            return Color.FromRgb(c.R, c.G, c.B);
        return fallback ?? DefaultAccent;
    }

    /// <summary>
    /// The effective accent: the Windows accent when <paramref name="useSystemAccent"/> and it is known,
    /// otherwise the configured one.
    /// </summary>
    public static Color ResolveAccent(string? configured, bool useSystemAccent, Color? systemAccent) =>
        useSystemAccent && systemAccent is { } s ? Color.FromRgb(s.R, s.G, s.B) : ParseAccent(configured);

    /// <summary>DWM stores colors as 0xAABBGGRR (HKCU\Software\Microsoft\Windows\DWM\AccentColor).</summary>
    public static Color FromAbgr(int abgr)
    {
        uint v = unchecked((uint)abgr);
        return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
    }

    /// <summary>WCAG relative luminance (0 black … 1 white).</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Text / glyph color on top of an accent fill: white unless the accent is so light that black reads clearly better
    /// (white is preferred like Windows does, as long as it keeps a 3:1 contrast).
    /// </summary>
    public static Color OnAccent(Color accent) =>
        ContrastRatio(Colors.White, accent) >= 3.0 || ContrastRatio(Colors.White, accent) >= ContrastRatio(Colors.Black, accent)
            ? Colors.White
            : Colors.Black;

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
