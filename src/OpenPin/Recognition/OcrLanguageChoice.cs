using System.Diagnostics;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Recognition;

/// <summary>
/// PRO-09: quick OCR language switch shared by the capture toolbar flyout and the pin menu. The choice is remembered
/// in <c>Ocr.Language</c> (settings.json) and passed explicitly to <see cref="OcrService.RecognizeAsync"/>.
/// </summary>
public static class OcrLanguageChoice
{
    /// <summary>Installed OCR languages (Windows language packs with OCR).</summary>
    public static IReadOnlyList<OcrLanguageInfo> Available => OcrService.GetAvailableLanguages();

    /// <summary>The remembered tag; "" = follow the Windows profile languages.</summary>
    public static string Current
    {
        get
        {
            try { return AppServices.Settings?.Current.Ocr.Language ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>Tag to pass to <see cref="OcrService.RecognizeAsync"/> (null = automatic).</summary>
    public static string? CurrentOrNull => string.IsNullOrWhiteSpace(Current) ? null : Current;

    public static bool IsCurrent(string tag) => string.Equals(Current, tag ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remember <paramref name="tag"/> ("" = automatic) and persist the settings.</summary>
    public static void Set(string tag)
    {
        try
        {
            var settings = AppServices.Settings;
            if (settings == null) return;
            settings.Current.Ocr.Language = tag ?? "";
            settings.Save();
            Log.Info($"OCR language set to '{tag}'");
        }
        catch (Exception ex) { Log.Error("Saving the OCR language failed", ex); }
    }

    /// <summary>Open Windows Settings → Time &amp; language → Language &amp; region (to add a language pack with OCR).</summary>
    public static void OpenLanguageSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error("Opening language settings failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }
}
