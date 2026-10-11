using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model;

internal enum HandleKind { Move, ResizeNWSE, ResizeNESW, ResizeNS, ResizeWE, Point, Rotate }

/// <summary>An edit handle of a selected item (position in image px).</summary>
internal readonly record struct Handle(int Id, Point Position, HandleKind Kind);

/// <summary>
/// Base of every annotation. Items are plain mutable data + drawing code; the surface owns them.
/// Any mutation must be followed by <see cref="Touch"/> so cached visuals are refreshed.
/// <see cref="Clone"/> keeps <see cref="Id"/> and <see cref="Version"/> (undo snapshots reuse unchanged visuals).
/// </summary>
internal abstract class AnnotationItem
{
    private static long s_version;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Globally unique per state (changes on every <see cref="Touch"/>).</summary>
    public long Version { get; private set; } = Interlocked.Increment(ref s_version);

    public void Touch() => Version = Interlocked.Increment(ref s_version);

    /// <summary>Area removed by the partial eraser (frozen, image px), or null.</summary>
    public Geometry? EraseMask { get; set; }

    /// <summary>Whether the partial eraser can cut this item.</summary>
    public virtual bool SupportsPartialErase => true;

    /// <summary>Add an erased area (keeps previous ones).</summary>
    public void AddEraseMask(Geometry area)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (EraseMask is GeometryGroup old && (old.Transform == null || old.Transform.Value.IsIdentity))
            foreach (var c in old.Children) g.Children.Add(c);
        else if (EraseMask != null) g.Children.Add(EraseMask);
        g.Children.Add(area);
        g.Freeze();
        EraseMask = g;
    }

    /// <summary>Move the erased area together with the item.</summary>
    public void OffsetMask(Vector v)
    {
        if (EraseMask == null) return;
        var inner = EraseMask;
        // Collapse consecutive moves into one translation (no deep nesting while dragging).
        if (inner is GeometryGroup { Transform: TranslateTransform t, Children.Count: 1 } wrapped)
        {
            v += new Vector(t.X, t.Y);
            inner = wrapped.Children[0];
        }
        var g = new GeometryGroup { Transform = new TranslateTransform(v.X, v.Y) };
        g.Children.Add(inner);
        g.Freeze();
        EraseMask = g;
    }

    /// <summary>Move the item (or one part of it) and its erased area.</summary>
    public void MoveBy(int part, Vector v)
    {
        OffsetPart(part, v);
        if (part == 0) OffsetMask(v);
    }

    /// <summary>The tool that creates / edits this kind of item.</summary>
    public abstract AnnotationTool Tool { get; }

    public virtual bool MatchesTool(AnnotationTool tool) => tool == Tool;

    /// <summary>0 = under everything (multiply highlighter), 1 = normal, 2 = combined overlay (spotlight).</summary>
    public virtual int Layer => 1;

    /// <summary>Re-render when the base image changes.</summary>
    public virtual bool DependsOnBase => false;

    /// <summary>Re-render when the content bounds (EditBounds) change.</summary>
    public virtual bool DependsOnContent => false;

    /// <summary>Visual bounds (image px), including stroke width.</summary>
    public abstract Rect Bounds { get; }

    public abstract void Draw(DrawingContext dc, RenderContext ctx);

    /// <summary>True if <paramref name="p"/> hits the item (within <paramref name="tolerance"/> px of its visible parts).</summary>
    public abstract bool HitTest(Point p, double tolerance);

    /// <summary>Eraser brush: does a circle of <paramref name="radius"/> swept from a to b touch the item?</summary>
    public virtual bool HitTestSegment(Point a, Point b, double radius)
    {
        double len = (b - a).Length;
        int steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1, radius / 2)));
        for (int i = 0; i <= steps; i++)
        {
            var p = a + (b - a) * ((double)i / steps);
            if (HitTest(p, radius)) return true;
        }
        return false;
    }

    /// <summary>Eraser rectangle: is the item inside / touched by the rectangle?</summary>
    public virtual bool IntersectsRect(Rect r) => Bounds.IntersectsWith(r);

    public virtual IReadOnlyList<Handle> GetHandles() => Array.Empty<Handle>();

    /// <summary>Handle drag (image px). <paramref name="start"/> is the item state when the drag began.</summary>
    public virtual void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start) { }

    /// <summary>Which part of the item is under p when the body is dragged (default 0 = whole item).</summary>
    public virtual int BodyPart(Point p) => 0;

    public abstract void Offset(Vector v);

    public virtual void OffsetPart(int part, Vector v) => Offset(v);

    /// <summary>Apply toolbar options to this item (secondary edit).</summary>
    public virtual void ApplyOptions(ToolOptions o) { }

    /// <summary>Copy this item's style into toolbar options (so the toolbar reflects the selection).</summary>
    public virtual void ReadOptions(ToolOptions o) { }

    /// <summary>Dashed selection frame instead of (or in addition to) handles.</summary>
    public virtual bool ShowSelectionFrame => true;

    public AnnotationItem Clone()
    {
        var c = (AnnotationItem)MemberwiseClone();
        c.CloneDeep();
        return c;
    }

    /// <summary>Copy reference-type fields (lists) after MemberwiseClone; drop caches if needed.</summary>
    protected virtual void CloneDeep() { }

    /// <summary>A brand-new copy with a fresh Id (duplicate).</summary>
    public AnnotationItem Duplicate()
    {
        var c = Clone();
        c.Id = Guid.NewGuid();
        c.Touch();
        return c;
    }

    // ---- shared drawing helpers ----

    protected static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected static Pen MakePen(Color c, double width, LineDash dash, LineCapStyle cap = LineCapStyle.Round, LineJoinStyle join = LineJoinStyle.Round)
    {
        var pen = new Pen(Brush(c), Math.Max(0.1, width))
        {
            StartLineCap = ToCap(cap),
            EndLineCap = ToCap(cap),
            LineJoin = join switch { LineJoinStyle.Miter => PenLineJoin.Miter, LineJoinStyle.Bevel => PenLineJoin.Bevel, _ => PenLineJoin.Round },
        };
        switch (dash)
        {
            case LineDash.Dash:
                pen.DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0);
                pen.DashCap = PenLineCap.Flat;
                break;
            case LineDash.Dot:
                pen.DashStyle = new DashStyle(new[] { 0.0, 2.0 }, 0);
                pen.DashCap = PenLineCap.Round;
                break;
        }
        pen.Freeze();
        return pen;
    }

    private static PenLineCap ToCap(LineCapStyle c) => c switch
    {
        LineCapStyle.Flat => PenLineCap.Flat,
        LineCapStyle.Square => PenLineCap.Square,
        _ => PenLineCap.Round,
    };

    protected static Color WithAlpha(Color c, double factor) => Color.FromArgb((byte)Math.Round(Math.Clamp(c.A * factor, 0, 255)), c.R, c.G, c.B);

    /// <summary>Standard 8 resize handles (+ optional rotate handle) of a rect rotated by <paramref name="angle"/> around its center.</summary>
    protected static List<Handle> RectHandles(Rect r, double angle, bool rotate, double rotateOffset = 24)
    {
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var pts = new[]
        {
            r.TopLeft, new Point(c.X, r.Top), r.TopRight, new Point(r.Right, c.Y),
            r.BottomRight, new Point(c.X, r.Bottom), r.BottomLeft, new Point(r.Left, c.Y),
        };
        var kinds = new[]
        {
            HandleKind.ResizeNWSE, HandleKind.ResizeNS, HandleKind.ResizeNESW, HandleKind.ResizeWE,
            HandleKind.ResizeNWSE, HandleKind.ResizeNS, HandleKind.ResizeNESW, HandleKind.ResizeWE,
        };
        var list = new List<Handle>(9);
        for (int i = 0; i < 8; i++) list.Add(new Handle(i, Geo.Rotate(pts[i], c, angle), angle == 0 ? kinds[i] : HandleKind.Point));
        if (rotate) list.Add(new Handle(8, Geo.Rotate(new Point(c.X, r.Top - rotateOffset), c, angle), HandleKind.Rotate));
        return list;
    }

    /// <summary>
    /// Resize a rect (rotated by <paramref name="angle"/> around its center) by dragging handle <paramref name="id"/> to
    /// <paramref name="p"/>, keeping the opposite side fixed in world space.
    /// </summary>
    protected static Rect ResizeRect(Rect r, double angle, int id, Point p, bool keepAspect)
    {
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var lp = Geo.Rotate(p, c, -angle);
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        // Anchor = opposite handle, in local coords.
        Point anchor = id switch
        {
            0 => r.BottomRight, 1 => new Point(c.X, b), 2 => r.BottomLeft, 3 => new Point(l, c.Y),
            4 => r.TopLeft, 5 => new Point(c.X, t), 6 => r.TopRight, _ => new Point(rr, c.Y),
        };
        switch (id)
        {
            case 0: l = lp.X; t = lp.Y; break;
            case 1: t = lp.Y; break;
            case 2: rr = lp.X; t = lp.Y; break;
            case 3: rr = lp.X; break;
            case 4: rr = lp.X; b = lp.Y; break;
            case 5: b = lp.Y; break;
            case 6: l = lp.X; b = lp.Y; break;
            case 7: l = lp.X; break;
        }
        if (keepAspect && id % 2 == 0 && r.Width > 0 && r.Height > 0)
        {
            // Corner drag with Shift: square / circle (same as creation).
            double s = Math.Max(Math.Abs(lp.X - anchor.X), Math.Abs(lp.Y - anchor.Y));
            var q = new Point(anchor.X + (lp.X < anchor.X ? -s : s), anchor.Y + (lp.Y < anchor.Y ? -s : s));
            l = Math.Min(anchor.X, q.X); rr = Math.Max(anchor.X, q.X); t = Math.Min(anchor.Y, q.Y); b = Math.Max(anchor.Y, q.Y);
        }
        var nr = new Rect(new Point(l, t), new Point(rr, b));
        if (angle == 0) return nr;
        // Keep the anchor fixed in world space: anchor moved because the rotation center moved.
        var worldBefore = Geo.Rotate(anchor, c, angle);
        var nc = new Point(nr.X + nr.Width / 2, nr.Y + nr.Height / 2);
        var worldAfter = Geo.Rotate(anchor, nc, angle);
        nr.Offset(worldBefore - worldAfter);
        return nr;
    }

    protected static double AngleTo(Point center, Point p, bool snap)
    {
        double a = Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI + 90;
        if (snap) a = Math.Round(a / 15) * 15;
        a %= 360;
        if (a < 0) a += 360;
        return a;
    }
}
