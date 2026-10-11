using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Capture;

/// <summary>A persisted region (screen px).</summary>
internal sealed record HistoryRegion(int X, int Y, int Width, int Height)
{
    public Int32Rect ToRect() => new(X, Y, Width, Height);
    public static HistoryRegion From(Int32Rect r) => new(r.X, r.Y, r.Width, r.Height);
}

/// <summary>A persisted screenshot: PNG file name (relative to the images folder) + where it was on screen.</summary>
internal sealed record HistoryImage(string File, int X, int Y, int Width, int Height, DateTime Time)
{
    public Int32Rect RectPx => new(X, Y, Width, Height);
}

/// <summary>
/// CAP-10 capture history: the last N selected regions (regions.json) and the last N output images
/// (images/*.png + images.json) in <c>AppPaths.CaptureHistoryDir</c>. Thread-safe.
/// </summary>
internal sealed class CaptureHistoryStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private RingHistory<HistoryRegion>? _regions;
    private List<HistoryImage>? _images;
    private Task _pendingWrite = Task.CompletedTask;

    public CaptureHistoryStore(string dir) => _dir = dir;

    private string RegionsFile => Path.Combine(_dir, "regions.json");
    private string ImagesDir => Path.Combine(_dir, "images");
    private string ImagesFile => Path.Combine(_dir, "images.json");

    /// <summary>Regions, most recent first (screen px).</summary>
    public IReadOnlyList<Int32Rect> GetRegions(int capacity)
    {
        lock (_gate)
        {
            var r = LoadRegions(capacity);
            return r.Items.Select(x => x.ToRect()).ToList();
        }
    }

    public Int32Rect? LastRegion
    {
        get
        {
            lock (_gate)
            {
                var r = LoadRegions(int.MaxValue);
                return r.Count > 0 ? r[0].ToRect() : null;
            }
        }
    }

    public void AddRegion(Int32Rect rectPx, int capacity)
    {
        if (rectPx.Width <= 0 || rectPx.Height <= 0) return;
        lock (_gate)
        {
            var r = LoadRegions(capacity);
            r.SetCapacity(capacity);
            r.Add(HistoryRegion.From(rectPx));
            TryWrite(RegionsFile, JsonSerializer.Serialize(r.Items));
        }
    }

    /// <summary>Images, most recent first.</summary>
    public IReadOnlyList<HistoryImage> GetImages()
    {
        lock (_gate) return LoadImages().ToList();
    }

    /// <summary>Persist an output image in the background (bitmap must be frozen). Returns the write task.</summary>
    public Task AddImageAsync(BitmapSource image, Int32Rect rectPx, int capacity)
    {
        if (capacity <= 0) return Task.CompletedTask;
        if (!image.IsFrozen) image = BitmapUtil.Frozen(image.Clone());
        lock (_gate)
        {
            _pendingWrite = _pendingWrite.ContinueWith(_ => AddImageCore(image, rectPx, capacity), TaskScheduler.Default);
            return _pendingWrite;
        }
    }

    /// <summary>Wait for background writes (tests / shutdown).</summary>
    public void Flush()
    {
        Task t;
        lock (_gate) t = _pendingWrite;
        try { t.Wait(5000); } catch (Exception ex) { Log.Error("History flush failed", ex); }
    }

    public BitmapSource? LoadImage(HistoryImage entry)
    {
        try
        {
            var path = Path.Combine(ImagesDir, entry.File);
            return File.Exists(path) ? BitmapUtil.LoadFile(path) : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load history image {entry.File}", ex);
            return null;
        }
    }

    private void AddImageCore(BitmapSource image, Int32Rect rectPx, int capacity)
    {
        try
        {
            var now = DateTime.Now;
            var name = $"{now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.png";
            Directory.CreateDirectory(ImagesDir);
            File.WriteAllBytes(Path.Combine(ImagesDir, name), BitmapUtil.EncodePng(image));
            lock (_gate)
            {
                var list = LoadImages();
                list.Insert(0, new HistoryImage(name, rectPx.X, rectPx.Y, rectPx.Width, rectPx.Height, now));
                while (list.Count > capacity)
                {
                    var old = list[^1];
                    list.RemoveAt(list.Count - 1);
                    try { File.Delete(Path.Combine(ImagesDir, old.File)); }
                    catch (Exception ex) { Log.Warn($"Could not delete history image {old.File}: {ex.Message}"); }
                }
                TryWrite(ImagesFile, JsonSerializer.Serialize(list));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save capture history image", ex);
        }
    }

    private RingHistory<HistoryRegion> LoadRegions(int capacity)
    {
        if (_regions != null) return _regions;
        _regions = new RingHistory<HistoryRegion>(Math.Max(1, Math.Min(capacity, 1000)));
        try
        {
            if (File.Exists(RegionsFile) &&
                JsonSerializer.Deserialize<List<HistoryRegion>>(File.ReadAllText(RegionsFile)) is { } list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i].Width > 0 && list[i].Height > 0) _regions.Add(list[i]);
            }
        }
        catch (Exception ex) { Log.Error("Failed to read capture region history", ex); }
        return _regions;
    }

    private List<HistoryImage> LoadImages()
    {
        if (_images != null) return _images;
        _images = new List<HistoryImage>();
        try
        {
            if (File.Exists(ImagesFile) &&
                JsonSerializer.Deserialize<List<HistoryImage>>(File.ReadAllText(ImagesFile)) is { } list)
                _images = list.Where(e => !string.IsNullOrEmpty(e.File) && File.Exists(Path.Combine(ImagesDir, e.File))).ToList();
        }
        catch (Exception ex) { Log.Error("Failed to read capture image history", ex); }
        return _images;
    }

    private void TryWrite(string file, string json)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) { Log.Error($"Failed to write {file}", ex); }
    }
}
