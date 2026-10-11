using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

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

    /// <summary>User-facing reason when recognition could not run (e.g. no OCR language installed); null otherwise.</summary>
    public string? Error { get; init; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Re-format the text with another layout / punctuation mode (used by the result window).</summary>
    public string FormatText(OcrTextLayout layout, OcrPunctuation punctuation) => OcrTextProcessor.Format(Lines, layout, punctuation);
}

/// <summary>An installed OCR language.</summary>
public sealed record OcrLanguageInfo(string Tag, string DisplayName);

/// <summary>
/// Offline OCR (Windows.Media.Ocr — local, free, uses the OS language packs).
/// Small images are upscaled for accuracy, images larger than <c>OcrEngine.MaxImageDimension</c> are tiled;
/// all bounds are mapped back to ORIGINAL image pixels.
/// </summary>
public static class OcrService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>True if at least one OCR language is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return WinOcrEngine.AvailableRecognizerLanguages.Count > 0; }
            catch (Exception ex) { Log.Warn($"OCR availability check failed: {ex.Message}"); return false; }
        }
    }

    /// <summary>BCP-47 tags of installed OCR languages (e.g. "zh-Hans-CN", "en-US").</summary>
    public static IReadOnlyList<string> AvailableLanguages => GetAvailableLanguages().Select(l => l.Tag).ToList();

    /// <summary>Installed OCR languages with display names (for the settings UI).</summary>
    public static IReadOnlyList<OcrLanguageInfo> GetAvailableLanguages()
    {
        try
        {
            return WinOcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguageInfo(l.LanguageTag, l.DisplayName)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing OCR languages failed: {ex.Message}");
            return Array.Empty<OcrLanguageInfo>();
        }
    }

    /// <summary>
    /// Recognize text. Safe to call from the UI thread (work runs in the background). Never throws for "no text" or
    /// "no OCR language" — returns <see cref="OcrResult.Empty"/> (with <see cref="OcrResult.Error"/> set when it could not run).
    /// Throws only <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static Task<OcrResult> RecognizeAsync(BitmapSource image, string? language = null, CancellationToken ct = default)
    {
        BitmapSource frozen;
        try { frozen = ImageEncoding.EnsureFrozen(image); }
        catch (Exception ex)
        {
            Log.Error("OCR: cannot read image", ex);
            return Task.FromResult(OcrResult.Empty with { Error = ex.Message });
        }
        var settings = ServicesConfig.Ocr;
        var lang = string.IsNullOrWhiteSpace(language) ? settings.Language : language;
        var options = (settings.Layout, settings.Punctuation, settings.UpscaleSmallImages, settings.RemoveCjkSpaces);
        return Task.Run(() => RecognizeCoreAsync(frozen, lang, options, ct), ct);
    }

    private static async Task<OcrResult> RecognizeCoreAsync(BitmapSource image, string? language,
        (OcrTextLayout Layout, OcrPunctuation Punctuation, bool Upscale, bool RemoveCjkSpaces) opt, CancellationToken ct)
    {
        WinOcrEngine? engine;
        try { engine = CreateEngine(language); }
        catch (Exception ex)
        {
            Log.Warn($"OCR engine could not be created: {ex.Message}");
            engine = null;
        }
        if (engine == null)
        {
            Log.Warn("OCR unavailable: no OCR language installed");
            return OcrResult.Empty with { Error = Loc.Get("Services.OcrNoLanguage") };
        }

        int w = image.PixelWidth, h = image.PixelHeight;
        if (w < 4 || h < 4) return OcrResult.Empty with { Language = engine.RecognizerLanguage.LanguageTag };

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pixels = BitmapUtil.GetPixels(image);
            ImageEncoding.FlattenOnWhite(pixels);
            var lines = new List<OcrLine>();
            int max = (int)WinOcrEngine.MaxImageDimension;

            foreach (var job in PlanJobs(w, h, max, opt.Upscale))
            {
                ct.ThrowIfCancellationRequested();
                var (buffer, bw, bh) = PrepareTile(pixels, w, job);
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer.AsBuffer(), BitmapPixelFormat.Bgra8, bw, bh, BitmapAlphaMode.Premultiplied);
                var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                foreach (var line in result.Lines)
                {
                    var words = line.Words.Select(wd => new OcrWord(wd.Text, job.MapBack(wd.BoundingRect))).ToList();
                    if (words.Count == 0) continue;
                    var bounds = words.Select(x => x.Bounds).Aggregate(Rect.Union);
                    double cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
                    if (!job.Owns(cx, cy)) continue; // duplicated in a neighbouring tile's overlap
                    var text = string.Join(" ", words.Select(x => x.Text));
                    if (opt.RemoveCjkSpaces) text = OcrTextProcessor.RemoveCjkSpaces(text);
                    lines.Add(new OcrLine(text, words, bounds));
                }
            }

            // Reading order: tiles may interleave lines; sort top-to-bottom, then left-to-right for lines on the same row.
            if (lines.Count > 1)
                lines = lines.OrderBy(l => Math.Round(l.Bounds.Top / Math.Max(4, l.Bounds.Height * 0.6))).ThenBy(l => l.Bounds.Left).ToList();

            var tag = engine.RecognizerLanguage.LanguageTag;
            return new OcrResult(lines, tag) { Text = OcrTextProcessor.Format(lines, opt.Layout, opt.Punctuation) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("OCR failed", ex);
            return OcrResult.Empty with { Error = ex.Message };
        }
        finally { Gate.Release(); }
    }

    private static WinOcrEngine? CreateEngine(string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            try
            {
                var lang = new Language(tag);
                if (WinOcrEngine.IsLanguageSupported(lang) && WinOcrEngine.TryCreateFromLanguage(lang) is { } e) return e;
            }
            catch (Exception ex) { Log.Warn($"OCR language '{tag}' invalid: {ex.Message}"); }
            // Loose match: "zh" / "zh-CN" → installed "zh-Hans-CN".
            var prefix = tag.Split('-')[0];
            var match = WinOcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l =>
                l.LanguageTag.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) || l.LanguageTag.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            if (match != null && WinOcrEngine.TryCreateFromLanguage(match) is { } m) return m;
            Log.Warn($"OCR language '{tag}' is not installed; falling back to profile languages");
        }
        if (WinOcrEngine.TryCreateFromUserProfileLanguages() is { } p) return p;
        var first = WinOcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        return first != null ? WinOcrEngine.TryCreateFromLanguage(first) : null;
    }

    // ------------------------------------------------------------------ tiling / scaling (pure, unit tested)

    /// <summary>One recognition pass: a source rectangle, scaled by <see cref="Scale"/> and padded by <see cref="Pad"/> px.</summary>
    internal sealed record OcrJob(Int32Rect Source, int Scale, int Pad, double OwnX0, double OwnX1, double OwnY0, double OwnY1)
    {
        public Rect MapBack(Windows.Foundation.Rect r) => MapBack(r.X, r.Y, r.Width, r.Height);

        public Rect MapBack(double x, double y, double width, double height) =>
            new((x - Pad) / Scale + Source.X, (y - Pad) / Scale + Source.Y, width / Scale, height / Scale);

        public bool Owns(double cx, double cy) => cx >= OwnX0 && cx < OwnX1 && cy >= OwnY0 && cy < OwnY1;
    }

    internal const int PadPx = 12;

    internal static int ChooseScale(int w, int h, bool upscale)
    {
        if (!upscale) return 1;
        if (h < 40) return 4;
        if (h < 80) return 3;
        return (long)w * h <= 1_500_000 ? 2 : 1;
    }

    internal static IReadOnlyList<OcrJob> PlanJobs(int w, int h, int maxDim, bool upscale)
    {
        if (maxDim < 64) maxDim = 64;
        int fit = maxDim - 2 * PadPx;
        if (w <= fit && h <= fit)
        {
            int s = ChooseScale(w, h, upscale);
            while (s > 1 && ((long)w * s > fit || (long)h * s > fit)) s--;
            return new[] { new OcrJob(new Int32Rect(0, 0, w, h), s, PadPx, double.MinValue, double.MaxValue, double.MinValue, double.MaxValue) };
        }

        int overlap = Math.Min(256, fit / 8);
        var xs = Spans(w, fit, overlap);
        var ys = Spans(h, fit, overlap);
        var jobs = new List<OcrJob>();
        foreach (var (y0, yl, oy0, oy1) in ys)
            foreach (var (x0, xl, ox0, ox1) in xs)
                jobs.Add(new OcrJob(new Int32Rect(x0, y0, xl, yl), 1, PadPx, ox0, ox1, oy0, oy1));
        return jobs;
    }

    /// <summary>Split [0,len) into spans ≤ size with overlap; each span "owns" the middle of the overlaps.</summary>
    private static List<(int Start, int Length, double Own0, double Own1)> Spans(int len, int size, int overlap)
    {
        var list = new List<(int, int, double, double)>();
        if (len <= size) { list.Add((0, len, double.MinValue, double.MaxValue)); return list; }
        int step = size - overlap;
        var starts = new List<int>();
        for (int s = 0; ; s += step)
        {
            if (s + size >= len) { starts.Add(len - size); break; }
            starts.Add(s);
        }
        for (int i = 0; i < starts.Count; i++)
        {
            int s = starts[i];
            double own0 = i == 0 ? double.MinValue : (s + Math.Min(starts[i - 1] + size, len)) / 2.0;
            double own1 = i == starts.Count - 1 ? double.MaxValue : (starts[i + 1] + s + size) / 2.0;
            list.Add((s, size, own0, own1));
        }
        return list;
    }

    /// <summary>Crop + scale + pad (with the tile's edge colour) into a BGRA buffer.</summary>
    private static (byte[] Buffer, int Width, int Height) PrepareTile(byte[] pixels, int fullWidth, OcrJob job)
    {
        var src = job.Source;
        var crop = new byte[src.Width * src.Height * 4];
        for (int y = 0; y < src.Height; y++)
            Buffer.BlockCopy(pixels, ((src.Y + y) * fullWidth + src.X) * 4, crop, y * src.Width * 4, src.Width * 4);

        BitmapSource bmp = BitmapUtil.FromPixels(crop, src.Width, src.Height);
        if (job.Scale > 1)
        {
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(job.Scale, job.Scale));
            scaled.Freeze();
            bmp = scaled;
        }
        var scaledPx = BitmapUtil.GetPixels(bmp);
        int sw = bmp.PixelWidth, sh = bmp.PixelHeight;

        // Padding colour = top-left pixel (usually the background) — text touching the edge is often missed otherwise.
        byte b = scaledPx[0], g = scaledPx[1], r = scaledPx[2];
        int pw = sw + 2 * job.Pad, ph = sh + 2 * job.Pad;
        var outBuf = new byte[pw * ph * 4];
        for (int i = 0; i < outBuf.Length; i += 4) { outBuf[i] = b; outBuf[i + 1] = g; outBuf[i + 2] = r; outBuf[i + 3] = 255; }
        for (int y = 0; y < sh; y++)
            Buffer.BlockCopy(scaledPx, y * sw * 4, outBuf, ((y + job.Pad) * pw + job.Pad) * 4, sw * 4);
        return (outBuf, pw, ph);
    }
}
