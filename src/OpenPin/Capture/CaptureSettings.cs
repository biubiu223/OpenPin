using OpenPin.Core.Imaging;

namespace OpenPin.Capture;

public enum UiDetectMode { Elements, WindowsOnly, None }
public enum QrDetectMode { Disabled, Manual, Auto, ShowContent }
public enum GuideLinesMode { Never, WhileAlt, Always }

/// <summary>settings.json → "Capture". Owned by the Capture module.</summary>
public sealed class CaptureSettings
{
    public UiDetectMode UiDetect { get; set; } = UiDetectMode.Elements;
    public bool ShowCursor { get; set; }
    public GuideLinesMode GuideLines { get; set; } = GuideLinesMode.WhileAlt;
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
}
