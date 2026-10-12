using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Recognition;

/// <summary>
/// High-level workflow for formula recognition (PRO-05).
/// Runs recognition (AI or OCR fallback) and presents the result in <see cref="LatexResultWindow"/>.
/// </summary>
public static class LatexRecognition
{
    public static async Task RecognizeAndShowAsync(BitmapSource image, Int32Rect? targetRect = null, string? ocrLanguage = null)
    {
        try
        {
            var result = await LatexRecognitionService.RecognizeAsync(image, ocrLanguage ?? OcrLanguageChoice.CurrentOrNull);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Latex))
            {
                Notify.Info(string.IsNullOrEmpty(result.ErrorMessage) ? Loc.Get("Recognition.Latex.NoFormula") : result.ErrorMessage);
                return;
            }
            LatexResultWindow.Show(result.Latex, source: result.Source);
        }
        catch (Exception ex)
        {
            Log.Error("LaTeX recognition failed", ex);
            Notify.Error(Loc.Format("Recognition.Latex.Failed", ex.Message));
        }
    }
}
