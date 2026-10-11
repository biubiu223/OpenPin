using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model.Items;

/// <summary>
/// Spotlight (ANN-11): a clear rect/ellipse; everything outside all spotlights is dimmed. Individual items draw
/// nothing — the surface draws the combined dimming layer on top (<see cref="DrawCombined"/>).
/// </summary>
internal sealed class SpotlightItem : AnnotationItem
{
    public Rect Rect { get; set; }
    public ShapeKind Shape { get; set; }
    /// <summary>Darkness of the outside (0..1).</summary>
    public double Opacity { get; set; } = 0.6;

    public override AnnotationTool Tool => AnnotationTool.Spotlight;
    public override int Layer => 2;
    public override bool SupportsPartialErase => false;
    public override Rect Bounds => Geo.Inflate(Rect, 1);

    public override void Draw(DrawingContext dc, RenderContext ctx) { }

    public Geometry Hole => Shape == ShapeKind.Ellipse ? new EllipseGeometry(Rect) : new RectangleGeometry(Rect);

    public static void DrawCombined(DrawingContext dc, IReadOnlyList<SpotlightItem> items, RenderContext ctx, ISet<Guid>? hidden = null)
    {
        var visible = items.Where(i => hidden == null || !hidden.Contains(i.Id)).ToList();
        if (visible.Count == 0) return;
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var s in visible) holes.Children.Add(s.Hole);
        var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(ctx.ImageRect), holes);
        dim.Freeze();
        double opacity = Math.Clamp(visible[^1].Opacity, 0, 1);
        dc.DrawGeometry(Brush(Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0)), null, dim);
    }

    public override bool HitTest(Point p, double tolerance) =>
        Shape == ShapeKind.Ellipse ? Geo.InsideEllipse(p, Geo.Inflate(Rect, tolerance)) : Geo.Inflate(Rect, tolerance).Contains(p);

    public override IReadOnlyList<Handle> GetHandles() => RectHandles(Rect, 0, rotate: false);

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        if (id < 8) Rect = ResizeRect(((SpotlightItem)start).Rect, 0, id, p, (mods & ModifierKeys.Shift) != 0);
    }

    public override void Offset(Vector v) { var r = Rect; r.Offset(v); Rect = r; }

    public override void ApplyOptions(ToolOptions o) { Opacity = o.Opacity; Shape = o.Shape; }
    public override void ReadOptions(ToolOptions o) { o.Opacity = Opacity; o.Shape = Shape; }
}

/// <summary>Watermark (ANN-12): text with time variables, tiled or placed at an anchor of the content bounds.</summary>
internal sealed partial class WatermarkItem : AnnotationItem
{
    public string Text { get; set; } = "OpenPin";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public WatermarkLayout Layout { get; set; }
    public double FontSize { get; set; } = 18;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public Color Color { get; set; } = Colors.Black;
    public double Opacity { get; set; } = 0.18;
    public double Spacing { get; set; } = 80;
    public const double TileAngle = -30;
    public const double Margin = 16;

    public override AnnotationTool Tool => AnnotationTool.Watermark;
    public override bool DependsOnContent => true;
    public override bool ShowSelectionFrame => false;

    // Bounds depend on the content bounds; use a huge rect so it is always re-rendered / hit when needed.
    public override Rect Bounds => new(-1e6, -1e6, 2e6, 2e6);

    [GeneratedRegex(@"\$([^$]+)\$")]
    private static partial Regex VariableRegex();

    /// <summary>Replaces <c>$format$</c> with <see cref="Timestamp"/> formatted (e.g. <c>$yyyy-MM-dd HH:mm:ss$</c>).</summary>
    public static string Resolve(string text, DateTime time) => VariableRegex().Replace(text, m =>
    {
        try { return time.ToString(m.Groups[1].Value, CultureInfo.CurrentCulture); }
        catch (FormatException) { return m.Value; }
    });

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var text = Resolve(Text, Timestamp);
        if (string.IsNullOrWhiteSpace(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Math.Max(1, FontSize), Brush(Color), 1.0);
        var area = ctx.ContentBounds;
        if (area.IsEmpty || area.Width < 1 || area.Height < 1) return;
        dc.PushClip(new RectangleGeometry(area));
        dc.PushOpacity(Math.Clamp(Opacity, 0, 1));
        double w = ft.WidthIncludingTrailingWhitespace, h = ft.Height;
        if (Layout == WatermarkLayout.Tile)
        {
            var c = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            dc.PushTransform(new RotateTransform(TileAngle, c.X, c.Y));
            double diag = Math.Sqrt(area.Width * area.Width + area.Height * area.Height);
            double stepX = w + Math.Max(4, Spacing), stepY = h + Math.Max(4, Spacing);
            int nx = (int)Math.Ceiling(diag / stepX / 2) + 1, ny = (int)Math.Ceiling(diag / stepY / 2) + 1;
            // Safety cap for tiny text on huge images.
            while ((2 * nx + 1) * (2 * ny + 1) > 4000) { stepX *= 1.4; stepY *= 1.4; nx = (int)Math.Ceiling(diag / stepX / 2) + 1; ny = (int)Math.Ceiling(diag / stepY / 2) + 1; }
            for (int iy = -ny; iy <= ny; iy++)
            {
                double shift = (iy & 1) == 0 ? 0 : stepX / 2;
                for (int ix = -nx; ix <= nx; ix++)
                    dc.DrawText(ft, new Point(c.X + ix * stepX + shift - w / 2, c.Y + iy * stepY - h / 2));
            }
            dc.Pop();
        }
        else
        {
            double m = Margin;
            double x = Layout switch
            {
                WatermarkLayout.TopLeft or WatermarkLayout.BottomLeft => area.Left + m,
                WatermarkLayout.TopRight or WatermarkLayout.BottomRight => area.Right - m - w,
                _ => area.X + (area.Width - w) / 2,
            };
            double y = Layout switch
            {
                WatermarkLayout.TopLeft or WatermarkLayout.TopRight or WatermarkLayout.TopCenter => area.Top + m,
                WatermarkLayout.Center => area.Y + (area.Height - h) / 2,
                _ => area.Bottom - m - h,
            };
            dc.DrawText(ft, new Point(Math.Round(x), Math.Round(y)));
        }
        dc.Pop();
        dc.Pop();
    }

    // Watermarks are edited only through the toolbar; they never capture clicks.
    public override bool HitTest(Point p, double tolerance) => false;
    public override bool HitTestSegment(Point a, Point b, double radius) => false;
    public override bool IntersectsRect(Rect r) => false;
    public override void Offset(Vector v) { }

    public override void ApplyOptions(ToolOptions o)
    {
        if (Text != o.WatermarkText) Timestamp = DateTime.Now;
        Text = o.WatermarkText; Layout = o.Layout; FontSize = o.Size; FontFamily = o.FontFamily;
        Color = o.ColorValue; Opacity = o.Opacity; Spacing = o.Spacing;
    }

    public override void ReadOptions(ToolOptions o)
    {
        o.WatermarkText = Text; o.Layout = Layout; o.Size = FontSize; o.FontFamily = FontFamily;
        o.ColorValue = Color; o.Opacity = Opacity; o.Spacing = Spacing;
    }
}

/// <summary>Magnifier callout (ANN-13): a source region shown enlarged in a target frame, with connector lines.</summary>
internal sealed class MagnifierItem : AnnotationItem
{
    public Rect Source { get; set; }
    public Point TargetTopLeft { get; set; }
    public ShapeKind Shape { get; set; }
    public int Zoom { get; set; } = 2;
    public Color BorderColor { get; set; } = Colors.Red;
    public double BorderWidth { get; set; } = 3;
    public ConnectorStyle Connector { get; set; } = ConnectorStyle.Solid;

    public override AnnotationTool Tool => AnnotationTool.Magnifier;
    public override bool DependsOnBase => true;

    public Rect Target => new(TargetTopLeft, new Size(Source.Width * Zoom, Source.Height * Zoom));

    public override Rect Bounds
    {
        get { var r = Source; r.Union(Target); return Geo.Inflate(r, BorderWidth + 2); }
    }

    private Geometry ShapeGeometry(Rect r) => Shape == ShapeKind.Ellipse ? new EllipseGeometry(r) : new RectangleGeometry(r);

    private IEnumerable<Point> Outline(Rect r) => Shape == ShapeKind.Ellipse ? Geo.EllipsePoints(r, 64) : Geo.Corners(r);

    /// <summary>Connector segments: hull edges that join the source outline to the target outline.</summary>
    public List<(Point A, Point B)> ConnectorLines()
    {
        var src = Outline(Source).ToList();
        var dst = Outline(Target).ToList();
        var srcSet = new HashSet<Point>(src);
        var hull = Geo.ConvexHull(src.Concat(dst));
        var res = new List<(Point, Point)>();
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            if (srcSet.Contains(a) != srcSet.Contains(b)) res.Add((a, b));
        }
        return res;
    }

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Source.Width < 1 || Source.Height < 1) return;
        var pen = MakePen(BorderColor, BorderWidth, LineDash.Solid);
        if (Connector != ConnectorStyle.None && BorderWidth > 0 && !Source.IntersectsWith(Target))
        {
            var cpen = MakePen(BorderColor, Math.Max(1, BorderWidth * 0.6), Connector == ConnectorStyle.Dashed ? LineDash.Dash : LineDash.Solid);
            foreach (var (a, b) in ConnectorLines()) dc.DrawLine(cpen, a, b);
        }
        var target = Target;
        var src = Geo.ToPixelRect(Source, ctx.Width, ctx.Height);
        if (src.Width > 0 && src.Height > 0)
        {
            var crop = new CroppedBitmap(ctx.BaseImage, src);
            crop.Freeze();
            var dest = new Rect(target.X + (src.X - Source.X) * Zoom, target.Y + (src.Y - Source.Y) * Zoom, src.Width * Zoom, src.Height * Zoom);
            dc.PushClip(ShapeGeometry(target));
            var dg = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.NearestNeighbor);
            using (var g = dg.Open())
            {
                g.DrawRectangle(Brushes.White, null, target);
                g.DrawImage(crop, dest);
            }
            dc.DrawDrawing(dg);
            dc.Pop();
        }
        if (BorderWidth > 0)
        {
            dc.DrawGeometry(null, pen, ShapeGeometry(Source));
            dc.DrawGeometry(null, pen, ShapeGeometry(target));
        }
    }

    public override bool HitTest(Point p, double tolerance) =>
        Geo.Inflate(Source, tolerance).Contains(p) || Geo.Inflate(Target, tolerance).Contains(p);

    public override int BodyPart(Point p) => Geo.Inflate(Target, 4).Contains(p) ? 1 : 0;

    public override void OffsetPart(int part, Vector v)
    {
        if (part == 1) TargetTopLeft += v;
        else Offset(v);
    }

    public override IReadOnlyList<Handle> GetHandles() => RectHandles(Source, 0, rotate: false);

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        if (id >= 8) return;
        var s = (MagnifierItem)start;
        var r = ResizeRect(s.Source, 0, id, p, (mods & ModifierKeys.Shift) != 0);
        Source = new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Max(1, Math.Round(r.Width)), Math.Max(1, Math.Round(r.Height)));
        // Keep the target's center where it was.
        var oc = new Point(s.Target.X + s.Target.Width / 2, s.Target.Y + s.Target.Height / 2);
        TargetTopLeft = new Point(Math.Round(oc.X - Source.Width * Zoom / 2), Math.Round(oc.Y - Source.Height * Zoom / 2));
    }

    public override void Offset(Vector v)
    {
        var r = Source; r.Offset(v); Source = r;
        TargetTopLeft += v;
    }

    public void SetZoom(int zoom)
    {
        var t = Target;
        var c = new Point(t.X + t.Width / 2, t.Y + t.Height / 2);
        Zoom = Math.Clamp(zoom, 1, 8);
        TargetTopLeft = new Point(Math.Round(c.X - Source.Width * Zoom / 2), Math.Round(c.Y - Source.Height * Zoom / 2));
    }

    public override void ApplyOptions(ToolOptions o)
    {
        Shape = o.Shape; BorderColor = o.ColorValue; BorderWidth = o.Size; Connector = o.Connector;
        if (o.Zoom != Zoom) SetZoom(o.Zoom);
    }

    public override void ReadOptions(ToolOptions o)
    {
        o.Shape = Shape; o.ColorValue = BorderColor; o.Size = BorderWidth; o.Connector = Connector; o.Zoom = Zoom;
    }
}
