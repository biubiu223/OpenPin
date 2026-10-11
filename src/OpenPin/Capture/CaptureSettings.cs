using OpenPin.Core.Imaging;

namespace OpenPin.Capture;

public enum UiDetectMode { Elements, WindowsOnly, None }
public enum QrDetectMode { Disabled, Manual, Auto, ShowContent }
public enum GuideLinesMode { Never, WhileAlt, Always }

/// <summary>A saved region for the custom / delayed capture dialog (CAP-14). Screen px.</summary>
public sealed class CustomCapturePreset
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public int DelaySeconds { get; set; }
}

/// <summary>settings.json → "Capture". Owned by the Capture module.</summary>
public sealed class CaptureSettings
{
    public UiDetectMode UiDetect { get; set; } = UiDetectMode.Elements;
    /// <summary>Time budget of one UI Automation element lookup (ms).</summary>
    public int UiDetectTimeoutMs { get; set; } = 400;
    public bool ShowCursor { get; set; }
    public GuideLinesMode GuideLines { get; set; } = GuideLinesMode.WhileAlt;
    public bool MagnifierEnabled { get; set; } = true;
    public int MagnifierZoom { get; set; } = 8;
    public bool MagnifierGrid { get; set; } = true;
    /// <summary>Format that Shift toggles to in the magnifier (RGB is the default display).</summary>
    public ColorFormat MagnifierAltFormat { get; set; } = ColorFormat.Hex;
    public bool ConfirmEscClose { get; set; }
    public bool ShowShortcutHints { get; set; } = true;
    public QrDetectMode QrDetect { get; set; } = QrDetectMode.Manual;
    public int HistoryImageCount { get; set; } = 10;
    public int HistoryRegionCount { get; set; } = 10;
    public bool PinAlsoCopies { get; set; }
    /// <summary>Color of the dimmed area outside the selection (#AARRGGBB).</summary>
    public string MaskColor { get; set; } = "#66000000";
    public int SelectionBorderWidth { get; set; } = 2;

    // ---- CAP-11 output style (toolbar "样式" flyout)
    /// <summary>Rounded corners of the output image (px, 0 = square).</summary>
    public int OutputCornerRadius { get; set; }
    /// <summary>Add a drop shadow around copied / saved images (not pins: pins draw their own shadow).</summary>
    public bool OutputShadow { get; set; }
    public int OutputShadowSize { get; set; } = 16;
    public bool OutputBorder { get; set; }
    public int OutputBorderWidth { get; set; } = 2;
    /// <summary>Border color (#AARRGGBB / #RRGGBB); empty = accent color.</summary>
    public string OutputBorderColor { get; set; } = "";
    /// <summary>Fixed selection aspect ratio like "16:9"; empty = free.</summary>
    public string AspectRatio { get; set; } = "";

    // ---- CAP-14 custom / delayed capture
    public List<CustomCapturePreset> CustomPresets { get; set; } = new();
    /// <summary>Last values of the dialog; Width/Height 0 = not used yet (defaults to the monitor under the cursor).</summary>
    public CustomCapturePreset LastCustom { get; set; } = new() { Width = 0, Height = 0 };
    /// <summary>Custom capture copies directly instead of opening the overlay with the region selected.</summary>
    public bool CustomDirectCopy { get; set; }
}
