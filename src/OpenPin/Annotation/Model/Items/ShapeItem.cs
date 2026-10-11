using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model.Items;

/// <summary>Rectangle / ellipse (incl. sector & arc variants), optional fill, dash, corner radius and rotation (ANN-02).</summary>
internal sealed class ShapeItem : AnnotationItem
{
    public bool IsEllipse { get; set; }
    /// <summary>Unrotated rect; rotation is around its center.</summary>
    public Rect Rect { get; set; }
    public double Angle { get; set; }
    public Color Color { get; set; } = Colors.Red;
    public double StrokeWidth { get; set; } = 4;
    public bool Fill { get; set; }
    public LineDash Dash { get; set; }
    public double CornerRadius { get; set; }
    public EllipseVariant Variant { get; set; }
    /// <summary>Sector / arc start angle (degrees, 0 = right, clockwise).</summary>
    public double StartAngle { get; set; } = -90;
    public double SweepAngle { get; set; } = 270;

    public override AnnotationTool Tool => IsEllipse ? AnnotationTool.Ellipse : AnnotationTool.Rectangle;
    public override bool MatchesTool(AnnotationTool tool) => tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse;

    private Point Center => new(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);

    public override Rect Bounds => Geo.Inflate(Geo.RotatedBounds(Rect, Angle), StrokeWidth / 2 + 1);

    private Geometry BuildGeometry()
    {
        var r = Rect;
        if (!IsEllipse)
        {
            double cr = Math.Min(CornerRadius, Math.Min(r.Width, r.Height) / 2);
            return new RectangleGeometry(r, cr, cr);
        }
        if (Variant == EllipseVariant.Full) return new EllipseGeometry(r);
        double sweep = Math.Clamp(SweepAngle, 1, 359);
        var p0 = PointAt(StartAngle);
        var p1 = PointAt(StartAngle + sweep);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool closed = Variant == EllipseVariant.Sector;
            if (closed)
            {
                ctx.BeginFigure(Center, true, true);
                ctx.LineTo(p0, true, true);
            }
            else ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r.Width / 2, r.Height / 2), 0, sweep > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private Point PointAt(double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(Center.X + Rect.Width / 2 * Math.Cos(a), Center.Y + Rect.Height / 2 * Math.Sin(a));
    }

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Rect.Width < 0.5 && Rect.Height < 0.5) return;
        if (Angle != 0) dc.PushTransform(new RotateTransform(Angle, Center.X, Center.Y));
        var geom = BuildGeometry();
        bool canFill = Fill && !(IsEllipse && Variant == EllipseVariant.Arc);
        dc.DrawGeometry(canFill ? Brush(Color) : null, MakePen(Color, StrokeWidth, Dash), geom);
        if (Angle != 0) dc.Pop();
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var lp = Geo.Rotate(p, Center, -Angle);
        var geom = BuildGeometry();
        if (Fill && geom.FillContains(lp)) return true;
        var pen = new Pen(Brushes.Black, StrokeWidth + tolerance * 2);
        return geom.StrokeContains(pen, lp);
    }

    public override bool IntersectsRect(Rect r) => Bounds.IntersectsWith(r);

    public override IReadOnlyList<Handle> GetHandles()
    {
        var list = RectHandles(Rect, Angle, rotate: true);
        if (IsEllipse && Variant != EllipseVariant.Full)
        {
            list.Add(new Handle(9, Geo.Rotate(PointAt(StartAngle), Center, Angle), HandleKind.Point));
            list.Add(new Handle(10, Geo.Rotate(PointAt(StartAngle + Math.Clamp(SweepAngle, 1, 359)), Center, Angle), HandleKind.Point));
        }
        return list;
    }

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        var s = (ShapeItem)start;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        if (id < 8) Rect = ResizeRect(s.Rect, s.Angle, id, p, shift);
        else if (id == 8) Angle = AngleTo(s.Center, p, shift);
        else if (id is 9 or 10)
        {
            var lp = Geo.Rotate(p, Center, -Angle);
            double a = Math.Atan2((lp.Y - Center.Y) / Math.Max(1e-6, Rect.Height / 2), (lp.X - Center.X) / Math.Max(1e-6, Rect.Width / 2)) * 180 / Math.PI;
            if (shift) a = Math.Round(a / 15) * 15;
            if (id == 9)
            {
                double end = s.StartAngle + s.SweepAngle;
                StartAngle = a;
                SweepAngle = Norm(end - a);
            }
            else SweepAngle = Norm(a - StartAngle);
        }
    }

    private static double Norm(double sweep)
    {
        sweep %= 360;
        if (sweep <= 0) sweep += 360;
        return Math.Clamp(sweep, 1, 359);
    }

    public override void Offset(Vector v)
    {
        var r = Rect; r.Offset(v); Rect = r;
    }

    public override void ApplyOptions(ToolOptions o)
    {
        Color = o.ColorValue; StrokeWidth = o.Size; Fill = o.Fill; Dash = o.Dash; CornerRadius = o.CornerRadius;
        if (IsEllipse) Variant = o.EllipseVariant;
    }

    public override void ReadOptions(ToolOptions o)
    {
        o.ColorValue = Color; o.Size = StrokeWidth; o.Fill = Fill; o.Dash = Dash; o.CornerRadius = CornerRadius;
        if (IsEllipse) o.EllipseVariant = Variant;
    }
}
