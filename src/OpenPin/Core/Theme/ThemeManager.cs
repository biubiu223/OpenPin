using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using OpenPin.Core.Settings;

namespace OpenPin.Core.Theme;

/// <summary>
/// App-wide light / dark theme and accent (UI-03). Owns the theme dictionary merged into
/// <c>Application.Resources</c> (Resources/Theme.Light.xaml or Theme.Dark.xaml, plus the accent-derived tokens) and swaps
/// it at runtime when the Theme / AccentColor / UseSystemAccent settings or the Windows app theme change. Everything that
/// references the tokens with DynamicResource / SetResourceReference updates live.
/// Also gives every window with a standard title bar the matching DWM (dark) title bar. UI thread only.
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "/OpenPin;component/Resources/Theme.Light.xaml";
    private const string DarkSource = "/OpenPin;component/Resources/Theme.Dark.xaml";

    private static Application? _app;
    private static SettingsService? _settings;
    private static ResourceDictionary? _current;
    private static int? _appsUseLightTheme;
    private static bool _systemValuesRead;
    private static (bool Dark, Color Accent)? _applied;
    private static readonly ConditionalWeakTable<Window, StrongBox<bool>> TitleBars = new();

    /// <summary>True while the dark theme is applied.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The effective accent color (configured, or the Windows accent when UseSystemAccent).</summary>
    public static Color Accent { get; private set; } = ThemeLogic.DefaultAccent;

    /// <summary>Raised on the UI thread after the theme dictionary was swapped (light/dark or accent changed).</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Applies the configured theme. Call once at startup, after settings are loaded and before any window opens.
    /// </summary>
    public static void Initialize(Application app, SettingsService settings)
    {
        if (_app != null) return;
        _app = app;
        _settings = settings;
        Apply();

        settings.Changed += (_, _) => OnUi(Apply);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Standard title bars follow the theme. SizeChanged runs in the first layout pass, before the window is shown
        // (no light flash); Loaded is the fallback.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => OnWindowEvent(s)));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => OnWindowEvent(s)));
    }

    /// <summary>Re-reads the settings / Windows values and applies them (no-op when nothing changed).</summary>
    public static void Refresh()
    {
        _systemValuesRead = false;
        Apply();
    }

    private static void OnUi(Action a)
    {
        var d = _app?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // "ImmersiveColorSet" (app theme / accent) arrives as General; Color covers classic color changes.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            OnUi(Refresh);
    }

    private static void Apply()
    {
        if (_app == null || _settings == null) return;
        var g = _settings.Current.General;
        if (!_systemValuesRead)
        {
            _appsUseLightTheme = ReadAppsUseLightTheme();
            _systemValuesRead = true;
        }
        bool dark = ThemeLogic.ResolveIsDark(g.Theme, _appsUseLightTheme);
        var accent = ThemeLogic.ResolveAccent(g.AccentColor, g.UseSystemAccent, g.UseSystemAccent ? ReadSystemAccent(dark) : null);
        if (_applied is { } a && a.Dark == dark && a.Accent == accent) return;

        try
        {
            var dict = BuildDictionary(dark, accent);
            var merged = _app.Resources.MergedDictionaries;
            int index = _current != null ? merged.IndexOf(_current) : FindThemeIndex(merged);
            if (index >= 0) merged[index] = dict;
            else merged.Insert(0, dict);
            _current = dict;
        }
        catch (Exception ex)
        {
            Log.Error("Applying theme failed", ex);
            return;
        }

        bool firstApply = _applied == null;
        _applied = (dark, accent);
        IsDark = dark;
        Accent = accent;
        Log.Info($"Theme: {(dark ? "dark" : "light")}, accent #{accent.R:X2}{accent.G:X2}{accent.B:X2}");
        if (firstApply) return;

        foreach (Window w in _app.Windows) ApplyTitleBar(w);
        RefreshCustomRendering();
        try { ThemeChanged?.Invoke(null, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("ThemeChanged handler failed", ex); }
    }

    private static int FindThemeIndex(IList<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) || src.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Loads a fresh copy of the theme dictionary and overrides the accent-derived tokens.</summary>
    public static ResourceDictionary BuildDictionary(bool dark, Color accent)
    {
        var dict = new ResourceDictionary { Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative) };
        SetAccent(dict, accent, dark);
        return dict;
    }

    /// <summary>Accent-derived keys (tokens that embed the accent color).</summary>
    public static void SetAccent(ResourceDictionary dict, Color accent, bool dark)
    {
        static SolidColorBrush B(Color c, double opacity = 1)
        {
            var b = new SolidColorBrush(c) { Opacity = opacity };
            b.Freeze();
            return b;
        }
        dict["Color.Accent"] = accent;
        dict["Color.PinActive"] = accent;
        dict["Brush.Accent"] = B(accent);
        dict["Brush.OnAccent"] = B(ThemeLogic.OnAccent(accent));
        dict["Brush.Pin.Selection"] = B(accent, dark ? 0.4 : 0.32);
        dict["Brush.Recording.Ready"] = B(accent);
        dict["Brush.Recording.TrimRange"] = B(accent, dark ? 0.3 : 0.22);
        dict["Brush.LongCapture.ViewportFill"] = B(ThemeLogic.WithAlpha(accent, (byte)(dark ? 0x33 : 0x26)));
        dict["Brush.Capture.MagnifierCross"] = B(ThemeLogic.WithAlpha(accent, 0x5C));
    }

    /// <summary>
    /// Our own controls that draw in OnRender (magnifiers, frames, timelines…) read brushes at render time:
    /// make them render again with the new tokens.
    /// </summary>
    private static void RefreshCustomRendering()
    {
        if (_app == null) return;
        var asm = typeof(ThemeManager).Assembly;
        foreach (Window w in _app.Windows) Walk(w);

        void Walk(DependencyObject d)
        {
            if (d is UIElement e && d.GetType().Assembly == asm) e.InvalidateVisual();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
    }

    // ------------------------------------------------------------------ title bars

    private static void OnWindowEvent(object sender)
    {
        if (sender is not Window w) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (TitleBars.TryGetValue(w, out var box) && box.Value == IsDark) return;
        ApplyTitleBar(w);
    }

    /// <summary>
    /// Light / dark DWM title bar for a window with a standard frame (no-op for borderless windows or before the
    /// window has an HWND). Called automatically for every window; safe to call again.
    /// </summary>
    public static void ApplyTitleBar(Window w)
    {
        if (w.WindowStyle == WindowStyle.None) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool dark = IsDark;
        TitleBars.AddOrUpdate(w, new StrongBox<bool>(dark));
        try
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int)); // Windows 10 before 20H1
        }
        catch (Exception ex)
        {
            Log.Warn($"Dark title bar failed: {ex.Message}");
        }
    }

    /// <summary>Mica needs Windows 11 22H2 (build 22621) for DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public static bool SupportsMica => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Mica backdrop (Windows 11 22H2+) for a window whose background is transparent (the settings window).</summary>
    public static void ApplyMica(Window w)
    {
        if (!SupportsMica) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int b = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref b, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn($"Mica backdrop failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Windows values

    /// <summary>HKCU AppsUseLightTheme (1 light, 0 dark), null if unknown.</summary>
    public static int? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The Windows accent as Fluent uses it for fills: AccentLight2 on dark, AccentDark1 on light (UISettings);
    /// falls back to the DWM accent from the registry. Null if unknown.
    /// </summary>
    public static Color? ReadSystemAccent(bool dark)
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception ex)
        {
            Log.Warn($"UISettings accent unavailable: {ex.Message}");
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr) return ThemeLogic.FromAbgr(abgr);
        }
        catch { /* ignore */ }
        return null;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
