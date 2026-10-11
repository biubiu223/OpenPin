using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model.Items;

/// <summary>Straight line / polyline with optional end decorations (ANN-03) or an arrow (ANN-04, two points).</summary>
internal sealed class LineItem : AnnotationItem
{
    public bool IsArrow { get; set; }
    public List<Point> Points { get; set; } = new();
    public Color Color { get; set; } = Colors.Red;
    public double Width { get; set; } = 4;
    public LineDash Dash { get; set; }
    public ArrowStyle ArrowStyle { get; set; }
    public LineEnd StartEnd { get; set; }
    public LineEnd EndEnd { get; set; }
    public LineJoinStyle Join { get; set; }
    public LineCapStyle Cap { get; set; }

    public override AnnotationTool Tool => IsArrow ? AnnotationTool.Arrow : AnnotationTool.Line;

    protected override void CloneDeep() => Points = new List<Point>(Points);

    public override Rect Bounds
    {
        get
        {
            var b = Geo.BoundsOf(Points);
            return Geo.Inflate(b, HeadSize().Half + Width + 2);
        }
    }

    /// <summary>Arrow head length / half width for the current width.</summary>
    public (double Length, double Half) HeadSize() => IsArrow
        ? (Width * 3 + 8, Width * 1.6 + 4)
        : (Width * 3 + 6, Width * 1.5 + 3);

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count < 2) return;
        if (IsArrow) DrawArrow(dc);
        else DrawLine(dc);
    }

    private void DrawLine(DrawingContext dc)
    {
        var pts = new List<Point>(Points);
        var (len, half) = HeadSize();
        var brush = Brush(Color);
        // Shorten the line under filled arrow heads so a thick line does not poke through the tip.
        if (StartEnd == LineEnd.Arrow) pts[0] = MoveToward(pts[0], pts[1], Math.Min(len * 0.8, (pts[1] - pts[0]).Length));
        if (EndEnd == LineEnd.Arrow) pts[^1] = MoveToward(pts[^1], pts[^2], Math.Min(len * 0.8, (pts[^1] - pts[^2]).Length));
        dc.DrawGeometry(null, MakePen(Color, Width, Dash, Cap, Join), Geo.Polyline(pts));
        DrawEnd(dc, StartEnd, Points[1], Points[0], len, half, brush);
        DrawEnd(dc, EndEnd, Points[^2], Points[^1], len, half, brush);
    }

    private void DrawEnd(DrawingContext dc, LineEnd end, Point from, Point tip, double len, double half, Brush brush)
    {
        switch (end)
        {
            case LineEnd.Arrow:
                dc.DrawGeometry(brush, null, Triangle(from, tip, len, half));
                break;
            case LineEnd.OpenArrow:
            {
                var dir = Dir(from, tip);
                var perp = new Vector(-dir.Y, dir.X);
                var b = tip - dir * len;
                dc.DrawGeometry(null, MakePen(Color, Width, LineDash.Solid), Geo.Polyline(new[] { b + perp * half, tip, b - perp * half }));
                break;
            }
            case LineEnd.Dot:
                dc.DrawEllipse(brush, null, tip, Width * 1.5 + 2, Width * 1.5 + 2);
                break;
        }
    }

    private void DrawArrow(DrawingContext dc)
    {
        Point a = Points[0], b = Points[^1];
        double length = (b - a).Length;
        if (length < 0.5) return;
        var (len, half) = HeadSize();
        // Short arrows: shrink the head proportionally.
        if (len > length * 0.6) { double k = length * 0.6 / len; len *= k; half *= k; }
        var brush = Brush(Color);
        var dir = Dir(a, b);
        var perp = new Vector(-dir.Y, dir.X);
        switch (ArrowStyle)
        {
            case ArrowStyle.Tapered:
            {
                double tail = Math.Max(0.5, Width * 0.15), neck = Math.Max(1, Width * 0.55);
                var barb = b - dir * len;
                var neckP = b - dir * len * 0.78;
                var g = Geo.Polyline(new[]
                {
                    a + perp * tail, neckP + perp * neck, barb + perp * half, b,
                    barb - perp * half, neckP - perp * neck, a - perp * tail,
                }, closed: true);
                dc.DrawGeometry(brush, new Pen(brush, 1) { LineJoin = PenLineJoin.Round }, g);
                break;
            }
            case ArrowStyle.Solid:
                dc.DrawLine(MakePen(Color, Width, LineDash.Solid, LineCapStyle.Round), a, b - dir * len * 0.8);
                dc.DrawGeometry(brush, null, Triangle(a, b, len, half));
                break;
            case ArrowStyle.Line:
                dc.DrawGeometry(null, MakePen(Color, Width, LineDash.Solid), Geo.Polyline(new[] { a, b }));
                dc.DrawGeometry(null, MakePen(Color, Width, LineDash.Solid),
                    Geo.Polyline(new[] { b - dir * len + perp * half, b, b - dir * len - perp * half }));
                break;
            case ArrowStyle.Double:
                if (length > len * 1.7)
                    dc.DrawLine(MakePen(Color, Width, LineDash.Solid, LineCapStyle.Flat), a + dir * len * 0.8, b - dir * len * 0.8);
                dc.DrawGeometry(brush, null, Triangle(a, b, len, half));
                dc.DrawGeometry(brush, null, Triangle(b, a, len, half));
                break;
        }
    }

    private static Vector Dir(Point from, Point to)
    {
        var v = to - from;
        if (v.Length < 1e-9) return new Vector(1, 0);
        v.Normalize();
        return v;
    }

    private static Point MoveToward(Point p, Point target, double d) => p + Dir(p, target) * d;

    private static StreamGeometry Triangle(Point from, Point tip, double len, double half)
    {
        var dir = Dir(from, tip);
        var perp = new Vector(-dir.Y, dir.X);
        var b = tip - dir * len;
        return Geo.Polyline(new[] { tip, b + perp * half, b - perp * half }, closed: true);
    }

    public override bool HitTest(Point p, double tolerance) =>
        Geo.DistanceToPolyline(p, Points) <= Math.Max(Width, IsArrow ? Width * 1.2 : Width) / 2 + tolerance;

    public override bool IntersectsRect(Rect r)
    {
        if (Points.Any(r.Contains)) return true;
        var corners = Geo.Corners(r).ToArray();
        for (int i = 1; i < Points.Count; i++)
            for (int k = 0; k < 4; k++)
                if (Geo.SegmentsIntersect(Points[i - 1], Points[i], corners[k], corners[(k + 1) % 4])) return true;
        return false;
    }

    public override IReadOnlyList<Handle> GetHandles() =>
        Points.Select((p, i) => new Handle(i, p, HandleKind.Point)).ToList();

    public override bool ShowSelectionFrame => false;

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        if (id < 0 || id >= Points.Count) return;
        if ((mods & ModifierKeys.Shift) != 0 && Points.Count >= 2)
        {
            var anchor = id == 0 ? Points[1] : Points[id - 1];
            p = Geo.Snap45(anchor, p);
        }
        Points[id] = p;
    }

    public override void Offset(Vector v)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += v;
    }

    public override void ApplyOptions(ToolOptions o)
    {
        Color = o.ColorValue; Width = o.Size;
        if (IsArrow) ArrowStyle = o.ArrowStyle;
        else { Dash = o.Dash; StartEnd = o.StartEnd; EndEnd = o.EndEnd; Join = o.Join; Cap = o.Cap; }
    }

    public override void ReadOptions(ToolOptions o)
    {
        o.ColorValue = Color; o.Size = Width;
        if (IsArrow) o.ArrowStyle = ArrowStyle;
        else { o.Dash = Dash; o.StartEnd = StartEnd; o.EndEnd = EndEnd; o.Join = Join; o.Cap = Cap; }
    }
}
