using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>
/// Clipboard access. UI thread only (WPF clipboard is STA).
/// PHASE-0 MINIMAL IMPLEMENTATION — the Services module owns and completes this file
/// (retry on locked clipboard, PNG + DIB with alpha, "copy image as file" mode, HTML, file lists, colors…).
/// </summary>
public static class ClipboardService
{
    public static void SetImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", new MemoryStream(BitmapUtil.EncodePng(image)));
        Clipboard.SetDataObject(data, copy: true);
    }

    public static void SetText(string text) => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);

    public static void SetFiles(IEnumerable<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        Clipboard.SetFileDropList(list);
    }

    /// <summary>Best pinnable representation of the clipboard, or null if nothing usable.</summary>
    public static PinContent? GetPinContent()
    {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            return new ImagePinContent(BitmapUtil.Normalize96Dpi(img));
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            if (files.Count == 1 && IsImageFile(files[0]))
                return new ImagePinContent(BitmapUtil.LoadFile(files[0]), files[0]);
            if (files.Count > 0) return new FilePinContent(files);
        }
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (ColorText.TryParse(text, out var color)) return new ColorPinContent(color, text.Trim());
            var html = Clipboard.ContainsText(TextDataFormat.Html) ? Clipboard.GetText(TextDataFormat.Html) : null;
            return new TextPinContent(text, html);
        }
        return null;
    }

    public static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico" or ".jxr";
}
