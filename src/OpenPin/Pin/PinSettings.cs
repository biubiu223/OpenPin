namespace OpenPin.Pin;

public enum PinDefaultPosition { CapturePosition, Mouse, PrimaryCenter, MouseScreenCenter }
public enum WheelZoomAnchor { Disabled, Mouse, Center, TopLeft, TopRight, BottomLeft, BottomRight }
public enum DuplicatePinBehavior { None, Shake, RestoreClosed, PinAgain }
public enum PinTitlePosition { Top, Bottom }

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
}
