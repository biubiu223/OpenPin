using OpenPin.Core.Imaging;

namespace OpenPin.Pin;

public enum PinDefaultPosition { CapturePosition, Mouse, PrimaryCenter, MouseScreenCenter }
public enum WheelZoomAnchor { Disabled, Mouse, Center, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// What happens when the same content is pinned again (Ctrl+2 with unchanged clipboard).
/// None = always pin a new copy; Shake = shake the existing on-screen pin instead;
/// RestoreClosed = re-open the identical closed pin from history (or shake an on-screen one);
/// PinAgain = close the existing identical pin and pin the content again.
/// </summary>
public enum DuplicatePinBehavior { None, Shake, RestoreClosed, PinAgain }
public enum PinTitlePosition { Top, Bottom }

/// <summary>Which image Ctrl+C copies: what is shown (processing + zoom) or the untouched original pixels.</summary>
public enum PinCopySource { Current, Original }

/// <summary>Ctrl+V / drag-drop onto a pin.</summary>
public enum PinPasteBehavior { Disabled, Replace, NewPin }

/// <summary>settings.json → "Pin". Owned by the Pin module.</summary>
public sealed class PinSettings
{
    public bool RestoreOnStartup { get; set; } = true;
    public PinDefaultPosition DefaultPosition { get; set; } = PinDefaultPosition.CapturePosition;
    public bool Topmost { get; set; } = true;
    public bool ConfirmEscClose { get; set; }
    public bool ConfirmDoubleClickClose { get; set; }
    public bool ShowShadow { get; set; } = true;
    public int HistoryCount { get; set; } = 10;
    /// <summary>Initial opacity, 10-100 (%).</summary>
    public int DefaultOpacity { get; set; } = 100;
    public int ZoomStepPercent { get; set; } = 10;
    public int FineZoomStepPercent { get; set; } = 1;
    public int OpacityStepPercent { get; set; } = 10;
    /// <summary>When click-through is enabled and opacity is higher, lower it to this (%; 0 = keep).</summary>
    public int ClickThroughOpacity { get; set; } = 60;
    public WheelZoomAnchor WheelZoom { get; set; } = WheelZoomAnchor.Mouse;
    public bool ResizeByBorder { get; set; } = true;
    public DuplicatePinBehavior OnDuplicate { get; set; } = DuplicatePinBehavior.Shake;
    public PinTitlePosition TitlePosition { get; set; } = PinTitlePosition.Bottom;
    public bool SmoothScaling { get; set; } = true;
    /// <summary>Run OCR on new image pins so text can be selected directly.</summary>
    public bool TextSelectableByDefault { get; set; } = true;

    // Text pins
    public int TextMaxWidth { get; set; } = 600;
    public bool TextShowHtml { get; set; } = true;
    public string TextFontFamily { get; set; } = "Microsoft YaHei UI";
    public double TextFontSize { get; set; } = 14;
    public string TextForeground { get; set; } = "#FF1F1F1F";
    public string TextBackground { get; set; } = "#FFFFFFFF";

    // ---- added by the Pin module ----

    /// <summary>What Ctrl+C copies on an image pin.</summary>
    public PinCopySource CopySource { get; set; } = PinCopySource.Current;
    /// <summary>Show the pin title (F2) on the pin.</summary>
    public bool ShowTitle { get; set; } = true;
    /// <summary>Briefly show the zoom percentage while zooming.</summary>
    public bool ShowZoomIndicator { get; set; } = true;
    /// <summary>Pins without a capture rectangle that are larger than the work area start zoomed out to fit.</summary>
    public bool FitLargeImagesToScreen { get; set; } = true;
    /// <summary>Width of the shadow / state glow around a pin, physical pixels (2-30).</summary>
    public int ShadowSizePx { get; set; } = 10;
    /// <summary>Thumbnail mode (R) side length, physical pixels.</summary>
    public int ThumbnailSizePx { get; set; } = 160;
    public PinPasteBehavior PasteBehavior { get; set; } = PinPasteBehavior.Replace;
    /// <summary>Alt magnifier: zoom factor and the format Shift toggles to (RGB is the default display).</summary>
    public int MagnifierZoom { get; set; } = 8;
    public ColorFormat MagnifierAltFormat { get; set; } = ColorFormat.Hex;
    /// <summary>Ask before "destroy all" (cannot be undone).</summary>
    public bool ConfirmDestroyAll { get; set; } = true;
    /// <summary>Show the group-switch overlay when switching pin groups.</summary>
    public bool ShowGroupSwitchOverlay { get; set; } = true;
}
