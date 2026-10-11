using System.Diagnostics;
using System.IO;
using System.Windows;
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
/// Saving images: PNG / JPG (quality) / BMP / TIFF / PDF, save dialog, quick save, auto save.
/// File names come from <see cref="PathTemplate"/>. Dialog methods: UI thread. <see cref="SaveToFile(BitmapSource,string)"/>
/// is thread-safe for frozen bitmaps.
/// </summary>
public static class SaveService
{
    /// <summary>Extensions <see cref="SaveToFile(BitmapSource,string)"/> can write.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".pdf" };

    public static bool IsSupportedExtension(string? ext) =>
        !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext.ToLowerInvariant());

    /// <summary>Show a save dialog. Returns the saved path or null if cancelled / failed.</summary>
    public static string? SaveWithDialog(BitmapSource image, SaveContext? context = null) => SaveWithDialog(image, context, null);

    /// <summary>Show a save dialog owned by <paramref name="owner"/> (pass the topmost overlay/pin so the dialog is not hidden behind it).</summary>
    public static string? SaveWithDialog(BitmapSource image, SaveContext? context, Window? owner)
    {
        context ??= new SaveContext();
        var s = ServicesConfig.Save;

        string folder = ResolveFolder(s.RememberLastFolder && Directory.Exists(s.LastFolder) ? s.LastFolder : s.ManualSaveFolder,
            AppPaths.DefaultPicturesDir);
        var name = Path.GetFileName(PathTemplate.Expand(
            string.IsNullOrWhiteSpace(s.FileNameTemplate) ? "OpenPin_$yyyy-MM-dd_HH-mm-ss$" : s.FileNameTemplate,
            context, image.PixelWidth, image.PixelHeight).TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(name)) name = "OpenPin";

        var ext = s.RememberLastExtension && IsSupportedExtension(s.LastExtension) ? s.LastExtension.ToLowerInvariant() : ".png";
        var format = ImageEncoding.FormatFromExtension(ext);
        // Filter order must match ImageFileFormat order (FilterIndex is 1-based).
        var dlg = new SaveFileDialog
        {
            FileName = name,
            DefaultExt = ImageEncoding.ExtensionOf(format),
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg;*.jpeg|BMP (*.bmp)|*.bmp|TIFF (*.tiff)|*.tiff;*.tif|PDF (*.pdf)|*.pdf",
            FilterIndex = (int)format + 1,
            InitialDirectory = folder,
            Title = Loc.Get("Services.SaveDialogTitle"),
        };
        bool? ok;
        try { ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog(); }
        catch (Exception ex)
        {
            Log.Error("Save dialog failed", ex);
            Notify.Error(Loc.Format("Services.SaveFailed", ex.Message));
            return null;
        }
        if (ok != true) return null;

        var path = dlg.FileName;
        if (!IsSupportedExtension(Path.GetExtension(path)))
        {
            var chosen = dlg.FilterIndex is >= 1 and <= 5 ? (ImageFileFormat)(dlg.FilterIndex - 1) : ImageFileFormat.Png;
            path += ImageEncoding.ExtensionOf(chosen);
        }

        if (!TrySave(image, path, out var error))
        {
            Notify.Error(Loc.Format("Services.SaveFailed", error));
            return null;
        }

        if (s.RememberLastExtension) s.LastExtension = Path.GetExtension(path).ToLowerInvariant();
        s.LastFolder = Path.GetDirectoryName(path) ?? "";
        ServicesConfig.Persist();

        if (s.CopyAfterSave)
        {
            try { ClipboardService.SetImage(image); }
            catch (Exception ex) { Log.Error("Copy after save failed", ex); }
        }
        return path;
    }

    /// <summary>
    /// Save without UI to the configured quick-save folder + template (unique name). Shows a notification with
    /// "open folder" / "copy path" buttons if enabled. Returns the path, or an empty string if saving failed
    /// (the user has already been notified).
    /// </summary>
    public static string QuickSave(BitmapSource image, SaveContext? context = null)
    {
        context ??= new SaveContext();
        var s = ServicesConfig.Save;
        string path;
        try
        {
            path = PathTemplate.MakeUnique(PathTemplate.ExpandToFullPath(
                ResolveFolder(s.QuickSaveFolder, AppPaths.DefaultPicturesDir), s.QuickSavePathTemplate, context,
                image.PixelWidth, image.PixelHeight));
        }
        catch (Exception ex)
        {
            Log.Error("Quick save path failed", ex);
            Notify.Error(Loc.Format("Services.SaveFailed", ex.Message));
            return "";
        }

        if (!TrySave(image, path, out var error))
        {
            Notify.Error(Loc.Format("Services.SaveFailed", error));
            return "";
        }

        if (s.QuickSaveNotify) NotifySaved(image, path);
        return path;
    }

    /// <summary>
    /// If auto-save is enabled, silently save a copy in the background. Never throws.
    /// Skipped when <paramref name="context"/> is given with <see cref="SaveContext.FromCapture"/> = false
    /// (null context = treated as a capture).
    /// </summary>
    public static void AutoSave(BitmapSource image, SaveContext? context = null)
    {
        try
        {
            var s = ServicesConfig.Save;
            if (!s.AutoSaveEnabled) return;
            if (context != null && !context.FromCapture) return;
            context ??= new SaveContext { FromCapture = true };

            var frozen = ImageEncoding.EnsureFrozen(image);
            var root = AutoSaveRoot(s);
            var template = s.AutoSavePathTemplate;
            int quality = s.Quality, days = s.AutoSaveCleanupDays;
            bool pdfJpeg = s.PdfUseJpeg;
            Task.Run(() =>
            {
                try
                {
                    var path = PathTemplate.MakeUnique(PathTemplate.ExpandToFullPath(root, template, context, frozen.PixelWidth, frozen.PixelHeight));
                    SaveToFile(frozen, path, quality, pdfJpeg);
                    Log.Info($"Auto-saved {path}");
                }
                catch (Exception ex) { Log.Error("Auto save failed", ex); }
                if (days > 0) CleanupAutoSave(root, days);
            });
        }
        catch (Exception ex) { Log.Error("Auto save failed", ex); }
    }

    /// <summary>Encode by extension (.png/.jpg/.jpeg/.bmp/.tif/.tiff/.pdf) with the configured quality. Thread-safe for frozen bitmaps. Throws on I/O errors.</summary>
    public static void SaveToFile(BitmapSource image, string path)
    {
        var s = ServicesConfig.Save;
        SaveToFile(image, path, s.Quality, s.PdfUseJpeg);
    }

    /// <summary>Encode by extension with an explicit JPEG quality (1-100). Writes to a temp file first, then replaces the target.</summary>
    public static void SaveToFile(BitmapSource image, string path, int quality, bool pdfUseJpeg = false)
    {
        var format = ImageEncoding.FormatFromExtension(Path.GetExtension(path));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".openpin-tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                ImageEncoding.Encode(image, format, fs, quality, pdfUseJpeg);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Encode to bytes in memory (e.g. for drag &amp; drop or uploads by other modules).</summary>
    public static byte[] Encode(BitmapSource image, ImageFileFormat format, int? quality = null)
    {
        var s = ServicesConfig.Save;
        using var ms = new MemoryStream();
        ImageEncoding.Encode(image, format, ms, quality ?? s.Quality, s.PdfUseJpeg);
        return ms.ToArray();
    }

    /// <summary>Open Explorer with the file selected.</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Reveal in Explorer failed", ex);
            Notify.Error(Loc.Format("Services.OpenFolderFailed", ex.Message));
        }
    }

    // ------------------------------------------------------------------ internals

    private static bool TrySave(BitmapSource image, string path, out string error)
    {
        try
        {
            SaveToFile(image, path);
            Log.Info($"Saved {path}");
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Saving {path} failed", ex);
            error = ex.Message;
            return false;
        }
    }

    private static void NotifySaved(BitmapSource image, string path)
    {
        Notify.Show(new NotifyOptions
        {
            Title = Loc.Get("Services.Saved"),
            Message = path,
            Thumbnail = ImageEncoding.EnsureFrozen(image),
            DurationMs = 4000,
            Actions = new[]
            {
                new NotifyAction(Loc.Get("Services.OpenFolder"), () => RevealInExplorer(path)),
                new NotifyAction(Loc.Get("Services.CopyPath"), () =>
                {
                    try { ClipboardService.SetText(path); }
                    catch (Exception ex) { Log.Error("Copy path failed", ex); }
                }),
            },
        });
    }

    internal static string ResolveFolder(string? configured, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configured)) return fallback;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : fallback;
        }
        catch { return fallback; }
    }

    internal static string AutoSaveRoot(SaveSettings s) =>
        ResolveFolder(s.AutoSaveFolder, Path.Combine(AppPaths.DefaultPicturesDir, "AutoSave"));

    private static DateTime _lastCleanup = DateTime.MinValue;
    private static readonly object CleanupGate = new();

    /// <summary>Delete image files older than <paramref name="days"/> under <paramref name="root"/> (at most once per hour).</summary>
    internal static void CleanupAutoSave(string root, int days, bool force = false)
    {
        lock (CleanupGate)
        {
            if (!force && DateTime.Now - _lastCleanup < TimeSpan.FromHours(1)) return;
            _lastCleanup = DateTime.Now;
        }
        try
        {
            var full = Path.GetFullPath(root);
            // Safety: never sweep a drive root or a very shallow folder.
            if (!Directory.Exists(full) || Path.GetPathRoot(full)?.TrimEnd('\\') == full.TrimEnd('\\')) return;
            var limit = DateTime.Now.AddDays(-days);
            int deleted = 0;
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (!IsSupportedExtension(Path.GetExtension(file))) continue;
                try
                {
                    if (File.GetLastWriteTime(file) < limit) { File.Delete(file); deleted++; }
                }
                catch (Exception ex) { Log.Warn($"Cleanup could not delete {file}: {ex.Message}"); }
            }
            // Remove now-empty sub folders (deepest first), never the root itself.
            foreach (var dir in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch { /* in use or not empty */ }
            }
            if (deleted > 0) Log.Info($"Auto-save cleanup removed {deleted} file(s) older than {days} day(s)");
        }
        catch (Exception ex) { Log.Error("Auto-save cleanup failed", ex); }
    }
}
