using OpenPin.Core.Actions;
using OpenPin.Core.Hotkeys;
using OpenPin.Core.Settings;

namespace OpenPin.Core;

/// <summary>
/// Minimal service locator, populated once in <c>App.OnStartup</c>. All members are UI-thread affine unless noted.
/// Module implementations are created in App.xaml.cs; modules talk to each other ONLY through these interfaces
/// (see Core/Contracts) plus the static services in OpenPin.Services.
/// </summary>
public static class AppServices
{
    public static SettingsService Settings { get; internal set; } = null!;
    public static HotkeyService Hotkeys { get; internal set; } = null!;
    public static ActionRegistry Actions { get; internal set; } = null!;

    public static ICaptureController Capture { get; internal set; } = null!;
    public static IPinManager Pins { get; internal set; } = null!;
    public static ILongCaptureController LongCapture { get; internal set; } = null!;
    public static IScreenRecorder Recorder { get; internal set; } = null!;
    public static ISettingsUi SettingsUi { get; internal set; } = null!;

    /// <summary>
    /// The currently open full-screen modal session (capture overlay, long capture, recorder), or null.
    /// Sessions set this when they open and clear it (only if still themselves) when they close.
    /// </summary>
    public static IModalSession? ActiveSession { get; set; }

    /// <summary>Shortcut for <c>Settings.Current</c>.</summary>
    public static AppSettings Config => Settings.Current;
}
