using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenPin.Core;

namespace OpenPin.Services;

/// <summary>Metadata about what is being saved; feeds file-name templates (%title%, %process%, $yyyy-MM-dd$…).</summary>
public sealed class SaveContext
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    /// <summary>Title of the window under the selection, if known.</summary>
    public string? WindowTitle { get; init; }
    /// <summary>Process name of the window under the selection, if known.</summary>
    public string? ProcessName { get; init; }
    /// <summary>True when the save comes from the capture overlay (auto-save applies), false for pins etc.</summary>
    public bool FromCapture { get; init; }
}

/// <summary>
/// Saving images. UI thread.
/// PHASE-0 MINIMAL IMPLEMENTATION — the Services module owns and completes this file
/// (formats PNG/JPG/BMP/WebP/TIFF/PDF, quality, file-name templates, quick save, auto save, notifications).
/// </summary>
public static class SaveService
{
    /// <summary>Show a save dialog. Returns the saved path or null if cancelled.</summary>
    public static string? SaveWithDialog(BitmapSource image, SaveContext? context = null)
    {
        context ??= new SaveContext();
        var dlg = new SaveFileDialog
        {
            FileName = $"OpenPin_{context.Timestamp:yyyy-MM-dd_HH-mm-ss}",
            DefaultExt = ".png",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
            InitialDirectory = AppPaths.DefaultPicturesDir,
        };
        if (dlg.ShowDialog() != true) return null;
        SaveToFile(image, dlg.FileName);
        return dlg.FileName;
    }

    /// <summary>Save without UI to the configured quick-save folder. Returns the path.</summary>
    public static string QuickSave(BitmapSource image, SaveContext? context = null)
    {
        context ??= new SaveContext();
        var path = Path.Combine(AppPaths.DefaultPicturesDir, $"OpenPin_{context.Timestamp:yyyy-MM-dd_HH-mm-ss-fff}.png");
        SaveToFile(image, path);
        Notify.Info(path, Loc.Get("Services.Saved"));
        return path;
    }

    /// <summary>If auto-save is enabled, silently save a copy. Never throws.</summary>
    public static void AutoSave(BitmapSource image, SaveContext? context = null) { /* implemented by Services module */ }

    /// <summary>Encode by extension (.png/.jpg/.jpeg/.bmp…).</summary>
    public static void SaveToFile(BitmapSource image, string path)
    {
        BitmapEncoder enc = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        enc.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
