using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Annotation.Common;
using OpenPin.Core;

namespace OpenPin.Annotation.Rendering;

/// <summary>A cached effect bitmap and where it goes (image px).</summary>
internal readonly record struct EffectImage(BitmapSource Image, Rect Placement);

/// <summary>
/// Everything items need to draw besides their own data: the base image (for effects), the content bounds
/// (EditBounds or whole image) and a cache of base-derived effect bitmaps (mosaic, blur, multiply).
/// The same context is used for on-screen rendering and <c>Render()</c>, so the output is identical.
/// </summary>
internal sealed partial class RenderContext
{
    private const int MaxEntries = 6;
    private BitmapSource _base;
    private BitmapSource? _bgra;
    private readonly List<Entry> _cache = new();
    private long _tick;

    private sealed class Entry
    {
        public required string Key;
        public required Int32Rect Region;
        public required EffectImage Effect;
        public long LastUse;
    }

    public RenderContext(BitmapSource baseImage)
    {
        _base = baseImage;
        ContentBounds = new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight);
    }

    public BitmapSource BaseImage => _base;
    public int Width => _base.PixelWidth;
    public int Height => _base.PixelHeight;
    public Rect ImageRect => new(0, 0, Width, Height);

    /// <summary>EditBounds ∩ image, or the whole image. Watermarks lay out inside it; effects precompute it.</summary>
    public Rect ContentBounds { get; set; }

    /// <summary>Incremented whenever the base image changes (items depending on it must re-render).</summary>
    public int Generation { get; private set; }

    public void SetBaseImage(BitmapSource image)
    {
        _base = image;
        _bgra = null;
        _cache.Clear();
        ClearSmartErase();
        Generation++;
    }

    private BitmapSource Bgra
    {
        get
        {
            if (_bgra != null) return _bgra;
            BitmapSource b = _base.Format == PixelFormats.Bgra32 || _base.Format == PixelFormats.Bgr32
                ? _base
                : new FormatConvertedBitmap(_base, PixelFormats.Bgra32, null, 0);
            if (b.CanFreeze && !b.IsFrozen) b.Freeze();
            return _bgra = b;
        }
    }

    public static int MosaicBlock(int strength) => 3 + Math.Clamp(strength, 1, 10) * 3;
    public static int BlurRadius(int strength) => Math.Clamp(strength, 1, 10) * 3;

    public EffectImage GetMosaic(int strength, Rect needed)
    {
        int block = MosaicBlock(strength);
        return Get("mosaic:" + block, needed, block, r =>
        {
            var px = PixelEffects.ReadRegion(Bgra, r);
            var outPx = PixelEffects.Pixelate(px, r.Width, r.Height, r.X, r.Y, block);
            return new EffectImage(PixelEffects.ToBitmap(outPx, r.Width, r.Height), new Rect(r.X, r.Y, r.Width, r.Height));
        });
    }

    public EffectImage GetBlur(int strength, Rect needed)
    {
        int radius = BlurRadius(strength);
        return Get("blur:" + radius, needed, 1, r =>
        {
            // Blur a margin around the region so edges sample real neighbours, on a downscaled copy for speed.
            var outer = Geo.ToPixelRect(new Rect(r.X - radius * 2, r.Y - radius * 2, r.Width + radius * 4, r.Height + radius * 4), Width, Height);
            int f = Math.Max(1, radius / 4);
            var px = PixelEffects.ReadRegion(Bgra, outer);
            var small = PixelEffects.Downscale(px, outer.Width, outer.Height, f, out int sw, out int sh);
            var blurred = PixelEffects.BoxBlur3(small, sw, sh, Math.Max(1, radius / f / 2));
            var bmp = PixelEffects.ToBitmap(blurred, sw, sh);
            // The small bitmap is stretched back over the outer rect when drawn (bilinear) — cheap and smooth.
            return new EffectImage(bmp, new Rect(outer.X, outer.Y, sw * f, sh * f));
        });
    }

    public EffectImage GetMultiply(Color color, Rect needed)
    {
        return Get("mul:" + color, needed, 1, r =>
        {
            var px = PixelEffects.ReadRegion(Bgra, r);
            var outPx = PixelEffects.Multiply(px, color);
            return new EffectImage(PixelEffects.ToBitmap(outPx, r.Width, r.Height), new Rect(r.X, r.Y, r.Width, r.Height));
        });
    }

    /// <summary>
    /// Returns a cached effect that covers <paramref name="needed"/>. A new one is computed for
    /// needed ∪ ContentBounds (aligned to <paramref name="align"/>) so that later strokes in the same area hit the cache.
    /// </summary>
    private EffectImage Get(string key, Rect needed, int align, Func<Int32Rect, EffectImage> compute)
    {
        var need = Geo.ToPixelRect(needed, Width, Height);
        if (need.Width <= 0 || need.Height <= 0) need = new Int32Rect(0, 0, Math.Min(1, Width), Math.Min(1, Height));
        _tick++;
        foreach (var e in _cache)
        {
            if (e.Key == key && Geo.Contains(e.Region, need))
            {
                e.LastUse = _tick;
                return e.Effect;
            }
        }
        var region = Geo.Union(need, Geo.ToPixelRect(ContentBounds, Width, Height));
        if (align > 1)
        {
            int x0 = region.X / align * align, y0 = region.Y / align * align;
            int x1 = Math.Min(Width, (region.X + region.Width + align - 1) / align * align);
            int y1 = Math.Min(Height, (region.Y + region.Height + align - 1) / align * align);
            region = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }
        EffectImage effect;
        try
        {
            effect = compute(region);
        }
        catch (Exception ex)
        {
            Log.Error($"Annotation effect '{key}' failed", ex);
            effect = new EffectImage(_base, ImageRect);
        }
        _cache.RemoveAll(e => e.Key == key);
        if (_cache.Count >= MaxEntries) _cache.Remove(_cache.MinBy(e => e.LastUse)!);
        _cache.Add(new Entry { Key = key, Region = region, Effect = effect, LastUse = _tick });
        return effect;
    }
}
