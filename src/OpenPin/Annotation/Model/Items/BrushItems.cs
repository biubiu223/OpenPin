using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model.Items;

/// <summary>Freehand pencil stroke (ANN-06), rendered as a smooth curve through the points.</summary>
internal sealed class PencilItem : AnnotationItem
{
    public List<Point> Points { get; set; } = new();
    public Color Color { get; set; } = Colors.Red;
    public double Width { get; set; } = 4;

    public override AnnotationTool Tool => AnnotationTool.Pencil;
    protected override void CloneDeep() => Points = new List<Point>(Points);

    public override Rect Bounds => Geo.Inflate(Geo.BoundsOf(Points), Width / 2 + 2);

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count == 0) return;
        dc.DrawGeometry(null, MakePen(Color, Width, LineDash.Solid), Geo.SmoothPath(Points));
    }

    public override bool HitTest(Point p, double tolerance) => Geo.DistanceToPolyline(p, Points) <= Width / 2 + tolerance;

    public override bool IntersectsRect(Rect r) => Points.Any(r.Contains);

    public override void Offset(Vector v)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += v;
    }

    public override void ApplyOptions(ToolOptions o) { Color = o.ColorValue; Width = o.Size; }
    public override void ReadOptions(ToolOptions o) { o.ColorValue = Color; o.Size = Width; }
}

/// <summary>Base for items covering an area painted with a brush (freehand) or a rectangle: highlighter, mosaic.</summary>
internal abstract class RegionItem : AnnotationItem
{
    private Geometry? _region;
    private long _regionVersion = -1;

    public DrawMode Mode { get; set; }
    public List<Point> Points { get; set; } = new();
    public Rect Rect { get; set; }
    public double Width { get; set; } = 20;

    protected override void CloneDeep()
    {
        Points = new List<Point>(Points);
        // Geometry is immutable (frozen) and keyed by version: sharing it is fine.
    }

    /// <summary>Filled area of the item (frozen, cached per version).</summary>
    public Geometry Region
    {
        get
        {
            if (_region != null && _regionVersion == Version) return _region;
            Geometry g;
            if (Mode == DrawMode.Rect) g = new RectangleGeometry(Rect);
            else
            {
                var path = Geo.SmoothPath(Points);
                var pen = new Pen(Brushes.Black, Math.Max(1, Width)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                g = path.GetWidenedPathGeometry(pen, 0.25, ToleranceType.Absolute);
            }
            g.Freeze();
            _region = g;
            _regionVersion = Version;
            return g;
        }
    }

    public override Rect Bounds => Mode == DrawMode.Rect
        ? Geo.Inflate(Rect, 1)
        : Geo.Inflate(Geo.BoundsOf(Points), Width / 2 + 2);

    public override bool HitTest(Point p, double tolerance) => Mode == DrawMode.Rect
        ? Geo.Inflate(Rect, tolerance).Contains(p)
        : Geo.DistanceToPolyline(p, Points) <= Width / 2 + tolerance;

    public override bool IntersectsRect(Rect r) => Mode == DrawMode.Rect ? Rect.IntersectsWith(r) : Points.Any(r.Contains);

    public override IReadOnlyList<Handle> GetHandles() => Mode == DrawMode.Rect ? RectHandles(Rect, 0, rotate: false) : Array.Empty<Handle>();

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        if (Mode == DrawMode.Rect && id < 8) Rect = ResizeRect(((RegionItem)start).Rect, 0, id, p, (mods & ModifierKeys.Shift) != 0);
    }

    public override void Offset(Vector v)
    {
        if (Mode == DrawMode.Rect) { var r = Rect; r.Offset(v); Rect = r; }
        else for (int i = 0; i < Points.Count; i++) Points[i] += v;
    }

    protected bool IsEmpty => Mode == DrawMode.Rect ? Rect.Width < 0.5 || Rect.Height < 0.5 : Points.Count == 0;
}

/// <summary>Highlighter (ANN-07): brush or rectangle; multiply (computed on CPU from the base image) or translucent.</summary>
internal sealed class HighlighterItem : RegionItem
{
    /// <summary>Opacity applied in translucent mode.</summary>
    public const double TranslucentOpacity = 0.45;

    public Color Color { get; set; } = Color.FromRgb(0xFA, 0xDB, 0x14);
    public HighlightBlend Blend { get; set; }

    public override AnnotationTool Tool => AnnotationTool.Highlighter;
    public override int Layer => Blend == HighlightBlend.Multiply ? 0 : 1;
    public override bool DependsOnBase => Blend == HighlightBlend.Multiply;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (IsEmpty) return;
        var region = Region;
        if (Blend == HighlightBlend.Translucent)
        {
            dc.DrawGeometry(Brush(WithAlpha(Color, TranslucentOpacity)), null, region);
            return;
        }
        var fx = ctx.GetMultiply(Color, region.Bounds);
        dc.PushClip(region);
        dc.DrawImage(fx.Image, fx.Placement);
        dc.Pop();
    }

    public override void ApplyOptions(ToolOptions o) { Color = o.ColorValue; Width = o.Size; Blend = o.Blend; }
    public override void ReadOptions(ToolOptions o) { o.ColorValue = Color; o.Size = Width; o.Blend = Blend; o.Mode = Mode; }
}

/// <summary>
/// Mosaic / blur (ANN-08) / smart erase (PRO-06): brush or rectangle, pixels computed from the base image.
/// Smart erase edits the picture itself, so it is painted under the other annotations (layer 0); its result is
/// computed in the background (<see cref="RenderContext.GetSmartErase"/>) and drawn pixel-exact when ready.
/// </summary>
internal sealed class MosaicItem : RegionItem
{
    /// <summary>Translucent fill shown while a smart erase is being drawn / computed.</summary>
    private static readonly SolidColorBrush PendingBrush = MakePending();

    public MosaicEffect Effect { get; set; }
    public int Strength { get; set; } = 5;

    public override AnnotationTool Tool => AnnotationTool.Mosaic;
    public override bool DependsOnBase => true;
    public override int Layer => Effect == MosaicEffect.SmartErase ? 0 : 1;

    private static SolidColorBrush MakePending()
    {
        var b = new SolidColorBrush(Color.FromArgb(0x73, 0x8F, 0x6B, 0xFF));
        b.Freeze();
        return b;
    }

    /// <summary>Thread-safe copy of the area for the smart-erase worker.</summary>
    public SmartEraseShape ToShape() => new(Mode, Rect, Points.ToArray(), Width);

    public override long RenderStamp(RenderContext ctx) => Effect == MosaicEffect.SmartErase ? ctx.SmartEraseStamp(this) : 0;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (IsEmpty) return;
        if (Effect == MosaicEffect.SmartErase)
        {
            DrawSmartErase(dc, ctx);
            return;
        }
        var region = Region;
        var fx = Effect == MosaicEffect.Blur ? ctx.GetBlur(Strength, region.Bounds) : ctx.GetMosaic(Strength, region.Bounds);
        dc.PushClip(region);
        if (Effect == MosaicEffect.Mosaic)
        {
            // Crisp blocks at any zoom.
            var dg = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.NearestNeighbor);
            using (var g = dg.Open()) g.DrawImage(fx.Image, fx.Placement);
            dc.DrawDrawing(dg);
        }
        else dc.DrawImage(fx.Image, fx.Placement);
        dc.Pop();
    }

    private void DrawSmartErase(DrawingContext dc, RenderContext ctx)
    {
        var patch = ctx.GetSmartErase(this);
        if (patch == null)
        {
            // Being drawn (not in the document yet) or computing: show the area.
            dc.DrawGeometry(PendingBrush, null, Region);
            return;
        }
        if (patch.Image == null) return; // nothing to fill / failed: the original stays visible
        var b = patch.Bounds;
        // Pixel-exact: the patch carries its own mask (alpha), nearest-neighbour so no smoothing at any zoom.
        var dg = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.NearestNeighbor);
        using (var g = dg.Open()) g.DrawImage(patch.Image, new Rect(b.X, b.Y, b.Width, b.Height));
        dc.DrawDrawing(dg);
    }

    public override void ApplyOptions(ToolOptions o) { Width = o.Size; Effect = o.Effect; Strength = o.Strength; }
    public override void ReadOptions(ToolOptions o) { o.Size = Width; o.Effect = Effect; o.Strength = Strength; o.Mode = Mode; }
}
