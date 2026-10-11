using System.Windows;
using System.Windows.Media;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;

namespace OpenPin.Annotation.Rendering;

/// <summary>
/// Hosts one DrawingVisual per annotation (plus the combined spotlight layer). Only items whose Version changed are
/// re-rendered, so a gesture redraws a single visual per mouse move, even on a 4K surface.
/// </summary>
internal sealed class AnnotationLayer : FrameworkElement
{
    private readonly VisualCollection _children;
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly DrawingVisual _spotlight = new();
    private readonly List<Visual> _order = new();
    private long _spotlightKey = long.MinValue;

    private sealed class Entry
    {
        public required DrawingVisual Visual;
        public long Version = -1;
        public long Stamp;
        public int Generation = -1;
        public Rect Content = Rect.Empty;
    }

    public AnnotationLayer()
    {
        _children = new VisualCollection(this);
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>
    /// Synchronize visuals with <paramref name="items"/> (z-order) plus an optional in-progress <paramref name="preview"/>.
    /// <paramref name="hidden"/>: items drawn faded (eraser preview) — returned as opacity; <paramref name="skip"/>: not drawn.
    /// </summary>
    public void Sync(IReadOnlyList<AnnotationItem> items, AnnotationItem? preview, RenderContext ctx,
        ISet<Guid>? faded = null, Guid? skip = null)
    {
        var all = preview == null ? items : items.Append(preview).ToList();
        var ordered = Ordered(all).ToList();
        var alive = new HashSet<Guid>();
        var newOrder = new List<Visual>(ordered.Count + 1);
        foreach (var item in ordered)
        {
            alive.Add(item.Id);
            if (!_entries.TryGetValue(item.Id, out var e))
            {
                e = new Entry { Visual = new DrawingVisual() };
                _entries[item.Id] = e;
            }
            long stamp = item.RenderStamp(ctx);
            bool stale = e.Version != item.Version
                         || e.Stamp != stamp
                         || (item.DependsOnBase && e.Generation != ctx.Generation)
                         || (item.DependsOnContent && e.Content != ctx.ContentBounds);
            if (stale)
            {
                using (var dc = e.Visual.RenderOpen())
                {
                    if (skip != item.Id) SafeDraw(item, dc, ctx);
                }
                e.Version = skip == item.Id ? -1 : item.Version;
                e.Stamp = stamp;
                e.Generation = ctx.Generation;
                e.Content = ctx.ContentBounds;
            }
            else if (skip == item.Id)
            {
                using (e.Visual.RenderOpen()) { }
                e.Version = -1;
            }
            e.Visual.Opacity = faded != null && faded.Contains(item.Id) ? 0.3 : 1;
            newOrder.Add(e.Visual);
        }

        // Combined spotlight dimming on top.
        var spots = all.OfType<SpotlightItem>().ToList();
        long key = spots.Count == 0 ? 0 : spots.Aggregate(17L, (h, s) => unchecked(h * 31 + s.Version + (faded?.Contains(s.Id) == true ? 7 : 0))) ^ ctx.Generation;
        if (key != _spotlightKey)
        {
            using (var dc = _spotlight.RenderOpen())
                SpotlightItem.DrawCombined(dc, spots, ctx, faded);
            _spotlightKey = key;
        }
        newOrder.Add(_spotlight);

        foreach (var id in _entries.Keys.Where(k => !alive.Contains(k)).ToList()) _entries.Remove(id);

        if (!newOrder.SequenceEqual(_order))
        {
            _children.Clear();
            foreach (var v in newOrder) _children.Add(v);
            _order.Clear();
            _order.AddRange(newOrder);
        }
    }

    /// <summary>Force every visual to re-render on the next Sync.</summary>
    public void InvalidateAll()
    {
        foreach (var e in _entries.Values) e.Version = -1;
        _spotlightKey = long.MinValue;
    }

    /// <summary>Paint order shared by screen and export: multiply highlighters, normal items, then spotlight.</summary>
    public static IEnumerable<AnnotationItem> Ordered(IEnumerable<AnnotationItem> items) =>
        items.Where(i => i.Layer == 0).Concat(items.Where(i => i.Layer == 1));

    /// <summary>Draw everything into one DrawingContext (export path), in the same order as on screen.</summary>
    public static void DrawAll(DrawingContext dc, IReadOnlyList<AnnotationItem> items, RenderContext ctx)
    {
        foreach (var item in Ordered(items)) SafeDraw(item, dc, ctx);
        SpotlightItem.DrawCombined(dc, items.OfType<SpotlightItem>().ToList(), ctx);
    }

    private static void SafeDraw(AnnotationItem item, DrawingContext dc, RenderContext ctx)
    {
        try
        {
            // Partial erase (ANN-10 P1): clip away the erased area.
            var mask = item.EraseMask;
            if (mask != null)
            {
                var clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(Common.Geo.Inflate(item.Bounds, 4)), mask);
                clip.Freeze();
                dc.PushClip(clip);
            }
            item.Draw(dc, ctx);
            if (mask != null) dc.Pop();
        }
        catch (Exception ex)
        {
            Core.Log.Error($"Drawing annotation {item.GetType().Name} failed", ex);
        }
    }
}

/// <summary>Single DrawingVisual host for transient UI (handles, brush cursor, rubber bands).</summary>
internal sealed class OverlayLayer : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public OverlayLayer()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    public DrawingContext Open() => _visual.RenderOpen();
}
