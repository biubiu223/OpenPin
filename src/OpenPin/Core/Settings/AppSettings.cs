using OpenPin.Annotation;
using OpenPin.Capture;
using OpenPin.LongCapture;
using OpenPin.Pin;
using OpenPin.Recording;
using OpenPin.Services;

namespace OpenPin.Core.Settings;

/// <summary>
/// Root of settings.json. Each module owns ONE section class that lives in the module's folder
/// (e.g. <c>Capture/CaptureSettings.cs</c>); add new properties there, not here.
/// Rules: properties must have sensible defaults, be JSON-serializable (System.Text.Json),
/// and enums are stored as strings.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public AnnotationSettings Annotation { get; set; } = new();
    public PinSettings Pin { get; set; } = new();
    public SaveSettings Save { get; set; } = new();
    public OcrSettings Ocr { get; set; } = new();
    public LongCaptureSettings LongCapture { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
}

public enum ThemeMode { System, Light, Dark }

public enum TrayClickAction { Capture, PinClipboard, None }

public sealed class GeneralSettings
{
    /// <summary>UI language (culture name of a folder in Resources/Strings).</summary>
    public string Language { get; set; } = "zh-CN";
    public bool AutoStart { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.Light;
    /// <summary>Accent color (#RRGGBB): selection borders, active pin shadow, highlights.</summary>
    public string AccentColor { get; set; } = "#1677FF";
    public TrayClickAction TrayLeftClick { get; set; } = TrayClickAction.Capture;
    public bool ShowStartupNotification { get; set; } = true;
}

/// <summary>Global hotkeys: action id (see <see cref="Actions.ActionIds"/>) → hotkey text like "Ctrl+Shift+A". Empty = unbound.</summary>
public sealed class HotkeySettings
{
    public bool Disabled { get; set; }

    public Dictionary<string, string> Bindings { get; set; } = DefaultBindings();

    public static Dictionary<string, string> DefaultBindings() => new()
    {
        [Actions.ActionIds.Capture] = "Ctrl+1",
        [Actions.ActionIds.PinClipboard] = "Ctrl+2",
        [Actions.ActionIds.PinRestore] = "Ctrl+3",
        [Actions.ActionIds.CaptureCustom] = "Alt+1",
        [Actions.ActionIds.PinNextGroup] = "Ctrl+`",
    };
}
