using System.Windows;
using System.Windows.Threading;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;
using OpenPin.Core;

namespace OpenPin.Annotation.Rendering;

/// <summary>
/// Smart erase (PRO-06) results: computed on a worker thread, cached per item state, shown when ready.
/// <para>
/// Smart-erase items stack like paint: an item is computed on the base image with the results of the EARLIER smart-erase
/// items that overlap its context area already applied (so erasing next to / over a previous erase sees clean
/// background). The cache key of an item therefore covers its own state (Id+Version, base generation) and the keys of
/// those earlier items. Undo/redo restore clones with the same Version, so they hit the cache.
/// </para>
/// <para>
/// Export (<see cref="SynchronousEffects"/> = true) computes missing results inline with the very same code, so the
/// exported pixels equal the on-screen ones (ANN-16).
/// </para>
/// </summary>
internal sealed partial class RenderContext
{
    private const long SmartEraseCacheBytes = 384L * 1024 * 1024;
    private readonly object _seGate = new();
    private readonly Dictionary<long, SeEntry> _seCache = new();
    private readonly HashSet<Guid> _seRunning = new();
    private readonly Dispatcher? _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
    private long _seBytes;
    private long _seTick;

    private sealed class SeEntry
    {
        public required SmartErasePatch Patch;
        public long LastUse;
    }

    private sealed record SeStep(long Key, SmartEraseShape Shape, Int32Rect Work);

    /// <summary>The document's items in z-order (set by the surface). Smart erase only computes items found here.</summary>
    public Func<IReadOnlyList<AnnotationItem>>? ItemsProvider { get; set; }

    /// <summary>Compute effects inline instead of on a worker (export, tests).</summary>
    public bool SynchronousEffects { get; set; }

    /// <summary>Incremented (UI thread) whenever a background effect finished; part of the items' render stamps.</summary>
    public int AsyncEpoch { get; private set; }

    /// <summary>Raised on the UI thread after a background effect finished (the surface re-syncs its visuals).</summary>
    public event Action? AsyncEffectReady;

    /// <summary>Number of background smart-erase computations in flight (diagnostics / tests).</summary>
    public int PendingSmartErase { get { lock (_seGate) return _seRunning.Count; } }

    private static long Mix(long h, long v) => unchecked((h ^ v) * 0x100000001B3L + (h >>> 29));

    private void ClearSmartErase()
    {
        lock (_seGate)
        {
            _seCache.Clear();
            _seBytes = 0;
        }
    }

    /// <summary>Cache key of a smart-erase item in the current document, or null if it is not in the document (draft).</summary>
    public long? SmartEraseKey(MosaicItem item)
    {
        var items = ItemsProvider?.Invoke();
        if (items == null) return null;
        int index = IndexOf(items, item);
        if (index < 0) return null;
        return KeyAt(items, index, new Dictionary<int, long>());
    }

    /// <summary>Render stamp: changes when the item's result (or its availability) may have changed.</summary>
    public long SmartEraseStamp(MosaicItem item) => Mix(SmartEraseKey(item) ?? 0, AsyncEpoch);

    private static int IndexOf(IReadOnlyList<AnnotationItem> items, AnnotationItem item)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item) || items[i].Id == item.Id) return i;
        return -1;
    }

    private static bool IsSmartErase(AnnotationItem i) => i is MosaicItem { Effect: MosaicEffect.SmartErase };

    /// <summary>Earlier smart-erase items whose area overlaps the context area of item <paramref name="index"/>.</summary>
    private IEnumerable<int> Deps(IReadOnlyList<AnnotationItem> items, int index)
    {
        var work = ((MosaicItem)items[index]).ToShape().WorkRect(Width, Height);
        for (int j = 0; j < index; j++)
        {
            if (!IsSmartErase(items[j])) continue;
            if (Intersects(Common.Geo.ToPixelRect(items[j].Bounds, Width, Height), work)) yield return j;
        }
    }

    private long KeyAt(IReadOnlyList<AnnotationItem> items, int index, Dictionary<int, long> memo)
    {
        if (memo.TryGetValue(index, out var k)) return k;
        var item = items[index];
        long key = Mix(Mix(Mix(0x5E5E, Generation), item.Version), Width * 65537L + Height);
        foreach (int j in Deps(items, index)) key = Mix(key, KeyAt(items, j, memo));
        memo[index] = key;
        return key;
    }

    private void Collect(IReadOnlyList<AnnotationItem> items, int index, SortedSet<int> closure)
    {
        if (!closure.Add(index)) return;
        foreach (int j in Deps(items, index)) Collect(items, j, closure);
    }

    private static bool Intersects(Int32Rect a, Int32Rect b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0 &&
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>
    /// The result for <paramref name="item"/>: cached, computed now (<see cref="SynchronousEffects"/>), or null while it
    /// is being computed in the background / when the item is not in the document.
    /// </summary>
    public SmartErasePatch? GetSmartErase(MosaicItem item)
    {
        var items = ItemsProvider?.Invoke();
        if (items == null) return null;
        int index = IndexOf(items, item);
        if (index < 0) return null;

        var memo = new Dictionary<int, long>();
        long key = KeyAt(items, index, memo);
        lock (_seGate)
        {
            if (_seCache.TryGetValue(key, out var hit))
            {
                hit.LastUse = ++_seTick;
                return hit.Patch;
            }
        }

        // Steps in z-order: every earlier overlapping smart erase (transitively), then the item itself.
        var closure = new SortedSet<int>();
        Collect(items, index, closure);
        var steps = new List<SeStep>();
        var union = new Int32Rect();
        foreach (int i in closure)
        {
            var shape = ((MosaicItem)items[i]).ToShape();
            var work = shape.WorkRect(Width, Height);
            steps.Add(new SeStep(memo[i], shape, work));
            union = Common.Geo.Union(union, work);
        }
        if (union.Width <= 0 || union.Height <= 0) return null;
        int w = Width, h = Height;

        if (SynchronousEffects || _dispatcher == null)
            return RunSteps(steps, PixelEffects.ReadRegion(Bgra, union), union, w, h);

        lock (_seGate)
        {
            if (!_seRunning.Add(item.Id)) return null; // one job per item; the latest state is requested when it ends
        }
        var pixels = PixelEffects.ReadRegion(Bgra, union);
        var dispatcher = _dispatcher;
        Task.Run(() =>
        {
            try { RunSteps(steps, pixels, union, w, h); }
            catch (Exception ex) { Log.Error("Smart erase failed", ex); }
            finally
            {
                lock (_seGate) _seRunning.Remove(item.Id);
                dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    AsyncEpoch++;
                    AsyncEffectReady?.Invoke();
                });
            }
        });
        return null;
    }

    private SmartErasePatch RunSteps(List<SeStep> steps, byte[] buffer, Int32Rect area, int w, int h)
    {
        SmartErasePatch? last = null;
        foreach (var step in steps)
        {
            SmartErasePatch? patch;
            lock (_seGate)
            {
                patch = _seCache.TryGetValue(step.Key, out var e) ? e.Patch : null;
                if (e != null) e.LastUse = ++_seTick;
            }
            if (patch == null)
            {
                try
                {
                    patch = SmartErasePatch.Compute(step.Shape, buffer, area, w, h);
                    Log.Info($"Smart erase {patch.Bounds.Width}x{patch.Bounds.Height}: {patch.Method}, {patch.Milliseconds:0.0} ms");
                }
                catch (Exception ex)
                {
                    Log.Error("Smart erase computation failed", ex);
                    patch = new SmartErasePatch { Bounds = new Int32Rect(), Mask = Array.Empty<byte>(), Pixels = Array.Empty<byte>(), Failed = true };
                }
                Store(step.Key, patch);
            }
            patch.ApplyTo(buffer, area);
            last = patch;
        }
        return last!;
    }

    private void Store(long key, SmartErasePatch patch)
    {
        lock (_seGate)
        {
            if (_seCache.ContainsKey(key)) return;
            _seCache[key] = new SeEntry { Patch = patch, LastUse = ++_seTick };
            _seBytes += patch.Bytes;
            while (_seBytes > SmartEraseCacheBytes && _seCache.Count > 1)
            {
                var oldest = _seCache.MinBy(kv => kv.Value.LastUse);
                _seCache.Remove(oldest.Key);
                _seBytes -= oldest.Value.Patch.Bytes;
            }
        }
    }
}
