using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Translate;

/// <summary>
/// User-level translation flows used by the capture overlay and pins (PRO-01). All entry points check that a provider
/// is configured first (otherwise: toast with a button to the settings, and no network use).
/// </summary>
public static class TranslateWorkflow
{
    /// <summary>
    /// Translate the text in an image. <see cref="TranslateMode.ImageToText"/> (or no <paramref name="pinResult"/>):
    /// opens the result window. <see cref="TranslateMode.ImageToImage"/>: renders the translation over the original text
    /// boxes and hands the new image to <paramref name="pinResult"/> (which pins it over the source). UI thread.
    /// </summary>
    public static async Task TranslateImageAsync(BitmapSource image, Action<BitmapSource>? pinResult, string? ocrLanguage = null)
    {
        if (!TranslationService.EnsureConfigured()) return;
        try
        {
            var settings = TranslationService.Settings;
            var ocr = await OcrService.RecognizeAsync(image, ocrLanguage);
            if (!string.IsNullOrEmpty(ocr.Error))
            {
                Notify.Error(OcrService.IsAvailable ? ocr.Error : Loc.Get("Translate.OcrUnavailable"));
                return;
            }
            var segments = TextSegmenter.Paragraphs(ocr.Lines);
            if (segments.Count == 0)
            {
                Notify.Info(Loc.Get("Translate.NoText"));
                return;
            }

            if (settings.Mode == TranslateMode.ImageToText || pinResult == null)
            {
                TranslateResultWindow.Show(segments.Select(s => s.Text).ToList());
                return;
            }

            Notify.Show(new NotifyOptions { Title = Loc.Get("Translate.Title"), Message = Loc.Get("Translate.Working"), DurationMs = 1500 });
            var outcome = await TranslationService.TranslateAsync(segments.Select(s => s.Text).ToList(), settings: settings);
            var boxes = segments.Zip(outcome.Segments, (s, t) => new TranslatedBox(s.Bounds, t, s.LineHeight)).ToList();
            var renderer = new ImageTranslationRenderer { RightToLeft = outcome.TargetLanguage == "ar" };
            var translated = renderer.Render(image, boxes);
            pinResult(translated);
        }
        catch (TranslationException ex)
        {
            Notify.Error(Loc.Format("Translate.Failed", ex.Message));
        }
        catch (Exception ex)
        {
            Log.Error("Image translation failed", ex);
            Notify.Error(Loc.Format("Translate.Failed", ex.Message));
        }
    }

    /// <summary>Translate plain text (text pins): opens the result window, one segment per line.</summary>
    public static void TranslateText(string text)
    {
        if (!TranslationService.EnsureConfigured()) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            Notify.Info(Loc.Get("Translate.NoText"));
            return;
        }
        TranslateResultWindow.Show(TranslationHttp.Lines(text));
    }
}
