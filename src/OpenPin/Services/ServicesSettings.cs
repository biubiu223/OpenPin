namespace OpenPin.Services;

/// <summary>settings.json → "Save". Owned by the Services module.</summary>
public sealed class SaveSettings
{
    /// <summary>JPEG/WebP quality 1-100.</summary>
    public int Quality { get; set; } = 92;
    /// <summary>Default folder of the save dialog. Empty = Pictures\OpenPin.</summary>
    public string ManualSaveFolder { get; set; } = "";
    /// <summary>File-name template (without extension) for manual save.</summary>
    public string FileNameTemplate { get; set; } = "OpenPin_$yyyy-MM-dd_HH-mm-ss$";
    public string LastExtension { get; set; } = ".png";
    public bool RememberLastExtension { get; set; } = true;
    public bool CopyAfterSave { get; set; }

    /// <summary>Quick save (Ctrl+Shift+S) full path template incl. extension. Empty folder = Pictures\OpenPin.</summary>
    public string QuickSavePathTemplate { get; set; } = "OpenPin_$yyyy-MM-dd_HH-mm-ss-fff$.png";
    public string QuickSaveFolder { get; set; } = "";
    public bool QuickSaveNotify { get; set; } = true;

    public bool AutoSaveEnabled { get; set; }
    public string AutoSaveFolder { get; set; } = "";
    public string AutoSavePathTemplate { get; set; } = "$yyyy$/$MM$/OpenPin_$yyyy-MM-dd_HH-mm-ss-fff$.png";
    /// <summary>Delete auto-saved files older than N days (0 = never).</summary>
    public int AutoSaveCleanupDays { get; set; }

    /// <summary>Clipboard: put images on the clipboard also as a file (for terminals / AI tools). Disabled | Auto | Png | Jpg | Bmp.</summary>
    public string CopyImageAsFile { get; set; } = "Auto";
}

public enum OcrTextLayout { Auto, SingleLine, MultiLine }
public enum OcrPunctuation { None, ToHalfWidth, ToFullWidth }

/// <summary>settings.json → "Ocr". Owned by the Services module.</summary>
public sealed class OcrSettings
{
    /// <summary>BCP-47 tag or empty = follow user profile languages.</summary>
    public string Language { get; set; } = "";
    public OcrTextLayout Layout { get; set; } = OcrTextLayout.Auto;
    public OcrPunctuation Punctuation { get; set; } = OcrPunctuation.None;
}
