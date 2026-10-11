using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Recognition;

/// <summary>Table recognition flow (PRO-02): OCR → ruling lines → structure → result window. Offline.</summary>
public static class TableRecognition
{
    /// <summary>Recognize the table in <paramref name="image"/> and open the result window. UI thread; never throws.</summary>
    public static async Task RecognizeAndShowAsync(BitmapSource image, string? ocrLanguage = null)
    {
        try
        {
            var table = await RecognizeAsync(image, ocrLanguage ?? OcrLanguageChoice.CurrentOrNull);
            if (table == null) return;
            if (table.IsEmpty)
            {
                Notify.Info(Loc.Get("Recognition.Table.NoText"));
                return;
            }
            TableResultWindow.Show(table);
        }
        catch (Exception ex)
        {
            Log.Error("Table recognition failed", ex);
            Notify.Error(Loc.Format("Recognition.Table.Failed", ex.Message));
        }
    }

    /// <summary>The recognized table, or null after an OCR error was reported to the user.</summary>
    public static async Task<TableData?> RecognizeAsync(BitmapSource image, string? ocrLanguage, CancellationToken ct = default)
    {
        var ocr = await OcrService.RecognizeAsync(image, ocrLanguage, ct);
        if (!string.IsNullOrEmpty(ocr.Error))
        {
            Notify.Error(OcrService.IsAvailable ? ocr.Error : Loc.Get("Recognition.OcrUnavailable"));
            return null;
        }
        var words = TableReconstructor.WordsOf(ocr);
        if (words.Count == 0) return new TableData(0, 0);

        bool detectLines = true;
        try { detectLines = AppServices.Settings?.Current.Recognition.DetectTableLines ?? true; } catch { /* defaults */ }
        var frozen = image.IsFrozen ? image : BitmapUtil.Frozen(image.Clone());
        return await Task.Run(() =>
        {
            IReadOnlyList<RulingLine> lines = Array.Empty<RulingLine>();
            if (detectLines) lines = RulingLineDetector.Detect(BitmapUtil.GetPixels(frozen), frozen.PixelWidth, frozen.PixelHeight);
            var table = TableReconstructor.Build(words, lines);
            Log.Info($"Table recognition: {words.Count} words, {lines.Count} ruling lines → {table.RowCount}×{table.ColumnCount}, {table.Merges.Count} merges");
            return table;
        }, ct);
    }
}
