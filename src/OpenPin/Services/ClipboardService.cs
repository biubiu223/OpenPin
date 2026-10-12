using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>
/// Clipboard access. Writing: UI thread only (WPF clipboard is STA). Reading uses Win32 directly and works on any thread.
/// All operations retry while another process holds the clipboard (CLIPBRD_E_CANT_OPEN).
/// <para>Images are written as CF_DIB (24-bit, flattened on white — safe for apps that ignore alpha) + "PNG" (keeps alpha)
/// + optionally a temp file as CF_HDROP according to <see cref="SaveSettings.CopyImageAsFile"/>.</para>
/// </summary>
public static class ClipboardService
{
    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);
    // WPF itself retries OleSetClipboard ~10x100 ms; keep the outer loop short so the UI never blocks for long.
    private const int Attempts = 3;

    // ------------------------------------------------------------------ write

    public static void SetImage(BitmapSource image) => SetData(BuildImageData(image, ServicesConfig.Save.CopyImageAsFile));

    /// <summary>Image plus a text representation (e.g. OCR result or a file path) in one clipboard operation.</summary>
    public static void SetImageAndText(BitmapSource image, string text)
    {
        var data = BuildImageData(image, ServicesConfig.Save.CopyImageAsFile);
        data.SetData(DataFormats.UnicodeText, text);
        SetData(data);
    }

    public static void SetText(string text) => SetData(new DataObject(DataFormats.UnicodeText, text ?? ""));

    /// <summary>HTML fragment (CF_HTML) with a plain-text alternative.</summary>
    public static void SetHtml(string htmlFragment, string plainText)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, plainText ?? "");
        data.SetData("HTML Format", new MemoryStream(ClipboardFormats.CreateCfHtml(htmlFragment)));
        SetData(data);
    }

    public static void SetFiles(IEnumerable<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY
        SetData(data);
    }

    /// <summary>Put an arbitrary data object on the clipboard (flushed, so it survives OpenPin exiting), with retries.</summary>
    public static void SetData(IDataObject data) => Retry(() => Clipboard.SetDataObject(data, copy: true));

    internal static DataObject BuildImageData(BitmapSource image, string copyAsFileMode)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Dib, new MemoryStream(ClipboardFormats.EncodeDib24(image)), false);
        data.SetData("PNG", new MemoryStream(BitmapUtil.EncodePng(image)), false);

        var file = WriteTempFileIfWanted(image, copyAsFileMode);
        if (file != null)
        {
            data.SetFileDropList(new StringCollection { file });
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        }
        return data;
    }

    /// <summary>"Copy image as file": Disabled | Auto (only when the image has transparency) | Png | Jpg | Bmp.</summary>
    internal static string? WriteTempFileIfWanted(BitmapSource image, string? mode)
    {
        ImageFileFormat? format = (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "png" => ImageFileFormat.Png,
            "jpg" or "jpeg" => ImageFileFormat.Jpg,
            "bmp" => ImageFileFormat.Bmp,
            "auto" => ImageEncoding.HasTransparency(image) ? ImageFileFormat.Png : null,
            _ => null,
        };
        if (format == null) return null;
        try
        {
            CleanTempFiles();
            var dir = AppPaths.Ensure(Path.Combine(AppPaths.TempDir, "clipboard"));
            var path = PathTemplate.MakeUnique(Path.Combine(dir, $"OpenPin_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ImageEncoding.ExtensionOf(format.Value)}"));
            SaveService.SaveToFile(image, path, ServicesConfig.Save.Quality);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Writing clipboard temp file failed", ex);
            return null;
        }
    }

    private static DateTime _lastTempClean = DateTime.MinValue;

    /// <summary>Delete clipboard temp files older than a day (throttled to once per 10 minutes).</summary>
    internal static void CleanTempFiles(bool force = false)
    {
        if (!force && DateTime.Now - _lastTempClean < TimeSpan.FromMinutes(10)) return;
        _lastTempClean = DateTime.Now;
        try
        {
            var dir = Path.Combine(AppPaths.TempDir, "clipboard");
            if (!Directory.Exists(dir)) return;
            var limit = DateTime.Now.AddDays(-1);
            foreach (var f in Directory.EnumerateFiles(dir, "OpenPin_*"))
                try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch { /* in use (pasted somewhere) */ }
        }
        catch (Exception ex) { Log.Warn($"Temp cleanup failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ read

    /// <summary>Best pinnable representation of the clipboard, or null if nothing usable.</summary>
    public static PinContent? GetPinContent()
    {
        var raw = ReadRaw();
        if (raw == null) return FallbackWpf();

        if (DecodeImage(raw) is { } image) return new ImagePinContent(image);

        string? html = null;
        if (raw.TryGetValue(ClipboardNative.CF_HTML, out var htmlBytes))
            html = ClipboardFormats.ExtractHtmlFragment(htmlBytes);

        if (raw.TryGetValue(ClipboardNative.CF_HDROP, out var drop))
        {
            var files = ClipboardFormats.ParseHDrop(drop).Where(p => p.Length > 0).ToList();
            if (files.Count == 1 && IsImageFile(files[0]) && File.Exists(files[0]))
            {
                try { return new ImagePinContent(BitmapUtil.LoadFile(files[0]), files[0]); }
                catch (Exception ex) { Log.Warn($"Could not load image file {files[0]}: {ex.Message}"); }
            }
            if (files.Count > 0) return new FilePinContent(files);
        }

        var text = raw.TryGetValue(ClipboardNative.CF_UNICODETEXT, out var tb) ? DecodeUnicode(tb) : null;
        if (!string.IsNullOrEmpty(text))
        {
            if (ClipboardFormats.TryParseColorText(text, out var color)) return new ColorPinContent(color, text.Trim());
            if (Recognition.LatexHelper.IsLatexFormula(text)) return new LatexPinContent(text.Trim());
            return new TextPinContent(text, html);
        }
        if (!string.IsNullOrWhiteSpace(html)) return new TextPinContent(ClipboardFormats.HtmlToPlainText(html), html);

        // Formats we do not read natively (e.g. CF_BITMAP-only / private formats): let WPF try.
        return FallbackWpf();
    }

    /// <summary>The clipboard image (PNG, DIBV5/DIB, bitmap, or a single copied image file), or null. 96 DPI, frozen.</summary>
    public static BitmapSource? GetImage()
    {
        switch (GetPinContent())
        {
            case ImagePinContent img: return img.Image;
            default: return null;
        }
    }

    /// <summary>Plain text on the clipboard, or null.</summary>
    public static string? GetText()
    {
        var raw = ReadRaw();
        return raw != null && raw.TryGetValue(ClipboardNative.CF_UNICODETEXT, out var tb) ? DecodeUnicode(tb) : null;
    }

    /// <summary>True if the clipboard currently contains something <see cref="GetPinContent"/> may turn into a pin.</summary>
    public static bool HasPinnableContent()
    {
        try
        {
            return ClipboardNative.IsAvailable(ClipboardNative.CF_PNG) || ClipboardNative.IsAvailable(ClipboardNative.CF_DIB) ||
                   ClipboardNative.IsAvailable(ClipboardNative.CF_BITMAP) || ClipboardNative.IsAvailable(ClipboardNative.CF_HDROP) ||
                   ClipboardNative.IsAvailable(ClipboardNative.CF_UNICODETEXT) || ClipboardNative.IsAvailable(ClipboardNative.CF_HTML);
        }
        catch { return false; }
    }

    public static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico" or ".jxr";

    // ------------------------------------------------------------------ internals

    private static Dictionary<uint, byte[]>? ReadRaw()
    {
        try
        {
            return ClipboardNative.Read(new[]
            {
                ClipboardNative.CF_PNG, ClipboardNative.CF_MIME_PNG, ClipboardNative.CF_DIBV5, ClipboardNative.CF_DIB,
                ClipboardNative.CF_HTML, ClipboardNative.CF_HDROP, ClipboardNative.CF_UNICODETEXT,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Reading clipboard failed", ex);
            return null;
        }
    }

    private static BitmapSource? DecodeImage(Dictionary<uint, byte[]> raw)
    {
        foreach (var f in new[] { ClipboardNative.CF_PNG, ClipboardNative.CF_MIME_PNG })
        {
            if (!raw.TryGetValue(f, out var png)) continue;
            try
            {
                using var ms = new MemoryStream(png);
                return BitmapUtil.ToBgra32(BitmapUtil.DecodeImage(ms));
            }
            catch (Exception ex) { Log.Warn($"Clipboard PNG could not be decoded: {ex.Message}"); }
        }
        foreach (var f in new[] { ClipboardNative.CF_DIBV5, ClipboardNative.CF_DIB })
        {
            if (!raw.TryGetValue(f, out var dib)) continue;
            try
            {
                if (ClipboardFormats.DecodeDib(dib) is { } bmp) return bmp;
            }
            catch (Exception ex) { Log.Warn($"Clipboard DIB could not be decoded: {ex.Message}"); }
        }
        return null;
    }

    private static string DecodeUnicode(byte[] bytes)
    {
        int len = bytes.Length & ~1;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0) { len = i; break; }
        return Encoding.Unicode.GetString(bytes, 0, len);
    }

    private static PinContent? FallbackWpf()
    {
        try
        {
            if (Application.Current?.Dispatcher.CheckAccess() != true && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return null;
            BitmapSource? img = null;
            Retry(() => img = Clipboard.ContainsImage() ? Clipboard.GetImage() : null);
            if (img != null) return new ImagePinContent(BitmapUtil.ToBgra32(img));
            string? text = null;
            Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
            if (!string.IsNullOrEmpty(text))
            {
                if (ClipboardFormats.TryParseColorText(text, out var c)) return new ColorPinContent(c, text.Trim());
                if (Recognition.LatexHelper.IsLatexFormula(text)) return new LatexPinContent(text.Trim());
                return new TextPinContent(text);
            }
        }
        catch (Exception ex) { Log.Warn($"WPF clipboard fallback failed: {ex.Message}"); }
        return null;
    }

    /// <summary>Run a clipboard operation, retrying while the clipboard is locked by another process.</summary>
    internal static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < Attempts && IsLocked(ex))
            {
                Thread.Sleep(15 * attempt);
            }
        }
    }

    private static bool IsLocked(Exception ex) =>
        ex is COMException { HResult: CLIPBRD_E_CANT_OPEN } || ex is ExternalException { ErrorCode: CLIPBRD_E_CANT_OPEN } ||
        ex.HResult == CLIPBRD_E_CANT_OPEN || (ex.InnerException != null && IsLocked(ex.InnerException));
}
