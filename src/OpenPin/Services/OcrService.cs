using System.Windows;
using System.Windows.Media.Imaging;

namespace OpenPin.Services;

/// <summary>A recognized word; <see cref="Bounds"/> in image pixels.</summary>
public sealed record OcrWord(string Text, Rect Bounds);

/// <summary>A recognized line; <see cref="Bounds"/> in image pixels.</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words, Rect Bounds);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, string Language)
{
    public static readonly OcrResult Empty = new(Array.Empty<OcrLine>(), "");

    /// <summary>All text, formatted according to OcrSettings (line breaks / punctuation handling).</summary>
    public string Text { get; init; } = "";
}

/// <summary>
/// Offline OCR (Windows.Media.Ocr — local, free, uses the OS language packs).
/// PHASE-0 STUB — implemented by the Services module.
/// </summary>
public static class OcrService
{
    /// <summary>True if at least one OCR language is installed.</summary>
    public static bool IsAvailable => false;

    /// <summary>BCP-47 tags of installed OCR languages (e.g. "zh-Hans-CN", "en-US").</summary>
    public static IReadOnlyList<string> AvailableLanguages => Array.Empty<string>();

    /// <summary>Recognize text. Safe to call from the UI thread (work runs in the background). Never throws for "no text".</summary>
    public static Task<OcrResult> RecognizeAsync(BitmapSource image, string? language = null, CancellationToken ct = default) =>
        Task.FromResult(OcrResult.Empty);
}
