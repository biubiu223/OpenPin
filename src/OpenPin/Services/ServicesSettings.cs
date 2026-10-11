using OpenPin.Core;

namespace OpenPin.Services;

/// <summary>settings.json → "Save". Owned by the Services module.</summary>
public sealed class SaveSettings
{
    /// <summary>JPEG/WebP quality 1-100.</summary>
    public int Quality { get; set; } = 92;
    /// <summary>Default folder of the save dialog. Empty = Pictures\OpenPin. Environment variables are expanded.</summary>
    public string ManualSaveFolder { get; set; } = "";
    /// <summary>File-name template (without extension) for manual save. See <see cref="PathTemplate"/>.</summary>
    public string FileNameTemplate { get; set; } = "OpenPin_$yyyy-MM-dd_HH-mm-ss$";
    public string LastExtension { get; set; } = ".png";
    public bool RememberLastExtension { get; set; } = true;
    /// <summary>After saving with the dialog, also copy the image to the clipboard.</summary>
    public bool CopyAfterSave { get; set; }
    /// <summary>Remember the folder last used in the save dialog (otherwise always start in <see cref="ManualSaveFolder"/>).</summary>
    public bool RememberLastFolder { get; set; } = true;
    /// <summary>Folder last used in the save dialog (written by SaveService).</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>Quick save (Ctrl+Shift+S) full path template incl. extension. Empty folder = Pictures\OpenPin.</summary>
    public string QuickSavePathTemplate { get; set; } = "OpenPin_$yyyy-MM-dd_HH-mm-ss-fff$.png";
    public string QuickSaveFolder { get; set; } = "";
    public bool QuickSaveNotify { get; set; } = true;

    public bool AutoSaveEnabled { get; set; }
    /// <summary>Auto-save root folder. Empty = Pictures\OpenPin\AutoSave.</summary>
    public string AutoSaveFolder { get; set; } = "";
    public string AutoSavePathTemplate { get; set; } = "$yyyy$/$MM$/OpenPin_$yyyy-MM-dd_HH-mm-ss-fff$.png";
    /// <summary>Delete auto-saved image files older than N days (0 = never). Only touches image files inside the auto-save folder.</summary>
    public int AutoSaveCleanupDays { get; set; }

    /// <summary>PDF export: embed the page image as JPEG (smaller) instead of lossless Flate-compressed RGB.</summary>
    public bool PdfUseJpeg { get; set; }

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
    /// <summary>Upscale small images before recognition (better accuracy for small UI fonts).</summary>
    public bool UpscaleSmallImages { get; set; } = true;
    /// <summary>Remove the spaces Windows OCR inserts between Chinese / Japanese characters.</summary>
    public bool RemoveCjkSpaces { get; set; } = true;
}

/// <summary>Null-safe access to the Services settings sections (settings may be absent in unit tests).</summary>
internal static class ServicesConfig
{
    public static SaveSettings Save => TryGet(s => s.Save) ?? new SaveSettings();
    public static OcrSettings Ocr => TryGet(s => s.Ocr) ?? new OcrSettings();

    private static T? TryGet<T>(Func<Core.Settings.AppSettings, T?> get) where T : class
    {
        try
        {
            var settings = AppServices.Settings;
            return settings?.Current is { } current ? get(current) : null;
        }
        catch { return null; }
    }

    /// <summary>Persist settings (best effort, no-op without a settings service).</summary>
    public static void Persist()
    {
        try { AppServices.Settings?.Save(); }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }
}
