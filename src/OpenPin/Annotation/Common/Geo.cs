using System.Windows;
using System.Windows.Media;

namespace OpenPin.Annotation.Common;

/// <summary>Pure geometry helpers used by tools, hit testing and rendering (unit tested).</summary>
internal static class Geo
{
    /// <summary>Snap the direction from <paramref name="from"/> to <paramref name="to"/> to the nearest multiple of 45°, keeping the projected length.</summary>
    public static Point Snap45(Point from, Point to)
    {
        var v = to - from;
        double len = v.Length;
        if (len < 1e-9) return to;
        double angle = Math.Atan2(v.Y, v.X);
        double step = Math.PI / 4;
        double snapped = Math.Round(angle / step) * step;
        var dir = new Vector(Math.Cos(snapped), Math.Sin(snapped));
        // Project onto the snapped direction so the end point follows the mouse naturally.
        double proj = Math.Max(0, v.X * dir.X + v.Y * dir.Y);
        var p = from + dir * proj;
        // Clean up floating noise for axis-aligned results.
        return new Point(Math.Round(p.X, 6), Math.Round(p.Y, 6));
    }

    /// <summary>Rectangle spanned by two points; with <paramref name="square"/> the shorter side is extended to a square (anchored at <paramref name="a"/>).</summary>
    public static Rect RectFromPoints(Point a, Point b, bool square)
    {
        if (square)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double s = Math.Max(Math.Abs(dx), Math.Abs(dy));
            b = new Point(a.X + (dx < 0 ? -s : s), a.Y + (dy < 0 ? -s : s));
        }
        return new Rect(a, b);
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-12) return (p - a).Length;
        double t = Math.Clamp(((p - a) * ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public static double DistanceToPolyline(Point p, IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0) return double.PositiveInfinity;
        if (pts.Count == 1) return (p - pts[0]).Length;
        double best = double.PositiveInfinity;
        for (int i = 1; i < pts.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, pts[i - 1], pts[i]));
        return best;
    }

    /// <summary>Minimum distance between segments ab and cd (0 when they intersect).</summary>
    public static double SegmentDistance(Point a, Point b, Point c, Point d)
    {
        if (SegmentsIntersect(a, b, c, d)) return 0;
        return Math.Min(Math.Min(DistanceToSegment(a, c, d), DistanceToSegment(b, c, d)),
                        Math.Min(DistanceToSegment(c, a, b), DistanceToSegment(d, a, b)));
    }

    public static bool SegmentsIntersect(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point o, Point p, Point q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>Distance from p to the outline of an axis-aligned ellipse inscribed in r (approximate, good for hit testing).</summary>
    public static double DistanceToEllipse(Point p, Rect r)
    {
        double rx = r.Width / 2, ry = r.Height / 2;
        if (rx < 1e-6 || ry < 1e-6) return DistanceToSegment(p, r.TopLeft, r.BottomRight);
        var c = new Point(r.X + rx, r.Y + ry);
        double dx = p.X - c.X, dy = p.Y - c.Y;
        double k = Math.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
        if (k < 1e-9) return Math.Min(rx, ry);
        // Point on the ellipse along the ray from the center (radial approximation).
        var onEllipse = new Point(c.X + dx / k, c.Y + dy / k);
        return (p - onEllipse).Length;
    }

    public static bool InsideEllipse(Point p, Rect r)
    {
        double rx = r.Width / 2, ry = r.Height / 2;
        if (rx < 1e-6 || ry < 1e-6) return false;
        double dx = p.X - (r.X + rx), dy = p.Y - (r.Y + ry);
        return dx * dx / (rx * rx) + dy * dy / (ry * ry) <= 1;
    }

    public static double DistanceToRectOutline(Point p, Rect r)
    {
        return Math.Min(Math.Min(DistanceToSegment(p, r.TopLeft, r.TopRight), DistanceToSegment(p, r.TopRight, r.BottomRight)),
                        Math.Min(DistanceToSegment(p, r.BottomRight, r.BottomLeft), DistanceToSegment(p, r.BottomLeft, r.TopLeft)));
    }

    /// <summary>Light smoothing of a freehand stroke: one moving-average pass followed by a Chaikin pass. Keeps both end points.</summary>
    public static List<Point> Smooth(IReadOnlyList<Point> pts, int iterations = 1)
    {
        if (pts.Count < 3) return pts.ToList();
        var cur = pts.ToList();
        // Moving average (window 3) removes mouse jitter.
        var avg = new List<Point>(cur.Count) { cur[0] };
        for (int i = 1; i < cur.Count - 1; i++)
            avg.Add(new Point((cur[i - 1].X + cur[i].X * 2 + cur[i + 1].X) / 4, (cur[i - 1].Y + cur[i].Y * 2 + cur[i + 1].Y) / 4));
        avg.Add(cur[^1]);
        cur = avg;
        for (int it = 0; it < iterations; it++) cur = Chaikin(cur);
        return cur;
    }

    /// <summary>One Chaikin corner-cutting iteration (end points preserved).</summary>
    public static List<Point> Chaikin(IReadOnlyList<Point> pts)
    {
        if (pts.Count < 3) return pts.ToList();
        var res = new List<Point>(pts.Count * 2) { pts[0] };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Point p = pts[i], q = pts[i + 1];
            res.Add(new Point(0.75 * p.X + 0.25 * q.X, 0.75 * p.Y + 0.25 * q.Y));
            res.Add(new Point(0.25 * p.X + 0.75 * q.X, 0.25 * p.Y + 0.75 * q.Y));
        }
        res.Add(pts[^1]);
        return res;
    }

    /// <summary>
    /// Smooth path through the points using quadratic Béziers between segment midpoints (the classic "brush" curve).
    /// </summary>
    public static StreamGeometry SmoothPath(IReadOnlyList<Point> pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (pts.Count > 0)
            {
                ctx.BeginFigure(pts[0], false, false);
                if (pts.Count == 1)
                    ctx.LineTo(pts[0] + new Vector(0.01, 0), true, true); // a dot (round caps make it visible)
                else if (pts.Count == 2)
                    ctx.LineTo(pts[1], true, true);
                else
                {
                    for (int i = 1; i < pts.Count - 1; i++)
                    {
                        var mid = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                        ctx.QuadraticBezierTo(pts[i], mid, true, true);
                    }
                    ctx.LineTo(pts[^1], true, true);
                }
            }
        }
        g.Freeze();
        return g;
    }

    public static StreamGeometry Polyline(IReadOnlyList<Point> pts, bool closed = false)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (pts.Count > 0)
            {
                ctx.BeginFigure(pts[0], closed, closed);
                for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, true);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>Andrew's monotone chain convex hull (counter-clockwise in screen coords; first point not repeated).</summary>
    public static List<Point> ConvexHull(IEnumerable<Point> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new Point[p.Count * 2];
        int k = 0;
        for (int i = 0; i < p.Count; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0) k--;
            hull[k++] = p[i];
        }
        for (int i = p.Count - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0) k--;
            hull[k++] = p[i];
        }
        return hull.Take(k - 1).ToList();
    }

    public static IEnumerable<Point> EllipsePoints(Rect r, int count = 48)
    {
        double rx = r.Width / 2, ry = r.Height / 2, cx = r.X + rx, cy = r.Y + ry;
        for (int i = 0; i < count; i++)
        {
            double a = 2 * Math.PI * i / count;
            yield return new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
        }
    }

    public static IEnumerable<Point> Corners(Rect r)
    {
        yield return r.TopLeft; yield return r.TopRight; yield return r.BottomRight; yield return r.BottomLeft;
    }

    public static Point Clamp(Point p, Rect r) =>
        r.IsEmpty ? p : new Point(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    public static Point Rotate(Point p, Point center, double degrees)
    {
        if (degrees == 0) return p;
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    public static Rect Inflate(Rect r, double d) => r.IsEmpty ? r : new Rect(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    /// <summary>Bounding rect of rotated rect (rotation around its center).</summary>
    public static Rect RotatedBounds(Rect r, double degrees)
    {
        if (degrees % 360 == 0) return r;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var pts = Corners(r).Select(p => Rotate(p, c, degrees)).ToList();
        double minX = pts.Min(q => q.X), minY = pts.Min(q => q.Y), maxX = pts.Max(q => q.X), maxY = pts.Max(q => q.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public static Rect BoundsOf(IReadOnlyList<Point> pts)
    {
        if (pts.Count == 0) return Rect.Empty;
        double minX = pts[0].X, minY = pts[0].Y, maxX = minX, maxY = minY;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Smallest integer pixel rect containing r, clamped to w×h.</summary>
    public static Int32Rect ToPixelRect(Rect r, int w, int h)
    {
        if (r.IsEmpty) return new Int32Rect();
        int x0 = (int)Math.Floor(r.Left), y0 = (int)Math.Floor(r.Top);
        int x1 = (int)Math.Ceiling(r.Right), y1 = (int)Math.Ceiling(r.Bottom);
        x0 = Math.Clamp(x0, 0, w); y0 = Math.Clamp(y0, 0, h); x1 = Math.Clamp(x1, 0, w); y1 = Math.Clamp(y1, 0, h);
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public static bool Contains(Int32Rect outer, Int32Rect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    public static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
