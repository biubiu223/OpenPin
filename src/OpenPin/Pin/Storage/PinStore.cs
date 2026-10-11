using System.IO;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Pin.Model;

namespace OpenPin.Pin.Storage;

/// <summary>
/// Pin persistence under <c>pins/</c>:
/// <code>
/// pins/groups.json
/// pins/&lt;id&gt;/manifest.json   state (PinManifest)
/// pins/&lt;id&gt;/original.png    image pins: untouched pixels
/// pins/&lt;id&gt;/edited.png      image pins: annotations / crop baked in (optional)
/// </code>
/// Writes run on a single background worker in submission order (so a delete never races a pending save);
/// <see cref="Flush"/> waits for them (used at shutdown). Reading never throws for corrupt data.
/// </summary>
internal sealed class PinStore
{
    public const string ManifestFile = "manifest.json";
    public const string OriginalFile = "original.png";
    public const string EditedFile = "edited.png";
    public const string GroupsFile = "groups.json";

    private readonly string _root;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public PinStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string DirOf(string id) => Path.Combine(_root, id);

    // ---------------------------------------------------------------- reading (UI thread, startup)

    /// <summary>All valid manifests. Corrupt folders are logged and renamed to <c>*.broken</c> so they are not retried.</summary>
    public List<PinManifest> LoadAll()
    {
        var list = new List<PinManifest>();
        string[] dirs;
        try { dirs = Directory.GetDirectories(_root); }
        catch (Exception ex) { Log.Error("Cannot list pins folder", ex); return list; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.EndsWith(".broken", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.Combine(dir, ManifestFile);
            try
            {
                if (!File.Exists(file))
                {
                    // half-written / foreign folder: leave it alone unless it is clearly ours and empty
                    if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                    continue;
                }
                var m = PinManifest.FromJson(File.ReadAllText(file));
                if (m == null || m.Id != name)
                {
                    Log.Warn($"Corrupt pin manifest skipped: {dir}");
                    MarkBroken(dir);
                    continue;
                }
                if (m.Kind == PinKind.Image && !File.Exists(Path.Combine(dir, OriginalFile)))
                {
                    Log.Warn($"Pin image missing, skipped: {dir}");
                    MarkBroken(dir);
                    continue;
                }
                list.Add(m);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to read pin {dir}", ex);
            }
        }
        return list;
    }

    private static void MarkBroken(string dir)
    {
        try { Directory.Move(dir, dir + ".broken"); }
        catch (Exception ex) { Log.Warn($"Could not rename broken pin folder {dir}: {ex.Message}"); }
    }

    /// <summary>Loads an image of a pin (frozen), or null if missing/corrupt.</summary>
    public BitmapSource? LoadImage(string id, string file)
    {
        var path = Path.Combine(DirOf(id), file);
        try
        {
            return File.Exists(path) ? BitmapUtil.LoadFile(path) : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load pin image {path}", ex);
            return null;
        }
    }

    public string? LoadGroupsJson()
    {
        var path = Path.Combine(_root, GroupsFile);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) { Log.Error("Failed to read pin groups", ex); return null; }
    }

    // ---------------------------------------------------------------- writing (background)

    /// <summary>Queues a manifest write (JSON is produced now, on the caller's thread).</summary>
    public void SaveManifest(PinManifest manifest)
    {
        var id = manifest.Id;
        var json = manifest.ToJson();
        Enqueue(() => WriteAtomic(Path.Combine(DirOf(id), ManifestFile), json));
    }

    public void SaveManifestNow(PinManifest manifest) =>
        WriteAtomic(Path.Combine(DirOf(manifest.Id), ManifestFile), manifest.ToJson());

    /// <summary>Queues a PNG write. <paramref name="image"/> must be frozen.</summary>
    public void SaveImage(string id, string file, BitmapSource image)
    {
        if (!image.IsFrozen) image = BitmapUtil.Normalize96Dpi(image);
        Enqueue(() =>
        {
            var bytes = BitmapUtil.EncodePng(image);
            WriteAtomic(Path.Combine(DirOf(id), file), bytes);
        });
    }

    public void DeleteFile(string id, string file) => Enqueue(() =>
    {
        var p = Path.Combine(DirOf(id), file);
        if (File.Exists(p)) File.Delete(p);
    });

    /// <summary>Queues deletion of a whole pin folder.</summary>
    public void Delete(string id) => Enqueue(() =>
    {
        var dir = DirOf(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    });

    public void SaveGroups(PinGroupBook book)
    {
        var json = book.ToJson();
        Enqueue(() => WriteAtomic(Path.Combine(_root, GroupsFile), json));
    }

    public void SaveGroupsNow(PinGroupBook book) => WriteAtomic(Path.Combine(_root, GroupsFile), book.ToJson());

    /// <summary>Blocks until every queued write has finished (or the timeout elapses).</summary>
    public bool Flush(int timeoutMs = 15000)
    {
        Task t;
        lock (_gate) t = _tail;
        try { return t.Wait(timeoutMs); }
        catch (Exception ex) { Log.Error("Pin store flush failed", ex); return false; }
    }

    private void Enqueue(Action work)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { Log.Error("Pin store write failed", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private static void WriteAtomic(string path, string text) => WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
