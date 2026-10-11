using System.Windows;

namespace OpenPin.Capture;

/// <summary>What part of the selection a point hits. <see cref="Inside"/> = move.</summary>
public enum HitKind { None, Inside, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

public enum Edge { Left, Top, Right, Bottom }

/// <summary>
/// Pure selection-rectangle math (image pixels). A selection covers whole pixels: X..X+Width-1 inclusive,
/// so the pixel under the magnifier crosshair is always part of the selection.
/// </summary>
internal static class SelectionMath
{
    public static bool IsEmpty(Int32Rect r) => r.Width <= 0 || r.Height <= 0;

    public static bool Contains(Int32Rect r, Point p) =>
        p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    /// <summary>Intersection of two rectangles (empty rect if they do not overlap).</summary>
    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r <= l || btm <= t ? Int32Rect.Empty : new Int32Rect(l, t, r - l, btm - t);
    }

    /// <summary>Clamp a rectangle into <paramref name="bounds"/> (keeps at least 1×1 if it overlaps).</summary>
    public static Int32Rect Clamp(Int32Rect r, Int32Rect bounds) => Intersect(r, bounds);

    /// <summary>
    /// Selection from a drag between pixels a and b (both inclusive), optionally square (Shift) or with a fixed
    /// width/height ratio, clamped to <paramref name="bounds"/>.
    /// </summary>
    public static Int32Rect FromPoints(int ax, int ay, int bx, int by, Int32Rect bounds, double? ratio = null)
    {
        ax = Math.Clamp(ax, bounds.X, bounds.X + bounds.Width - 1);
        ay = Math.Clamp(ay, bounds.Y, bounds.Y + bounds.Height - 1);
        bx = Math.Clamp(bx, bounds.X, bounds.X + bounds.Width - 1);
        by = Math.Clamp(by, bounds.Y, bounds.Y + bounds.Height - 1);

        if (ratio is > 0 and var k)
        {
            int sx = bx >= ax ? 1 : -1, sy = by >= ay ? 1 : -1;
            int w = Math.Abs(bx - ax) + 1, h = Math.Abs(by - ay) + 1;
            // Room from the anchor to the bounds in the drag direction.
            int maxW = sx > 0 ? bounds.X + bounds.Width - ax : ax - bounds.X + 1;
            int maxH = sy > 0 ? bounds.Y + bounds.Height - ay : ay - bounds.Y + 1;
            (w, h) = FitRatio(w, h, k, grow: true);
            (w, h) = Shrink(w, h, maxW, maxH, k);
            bx = ax + sx * (w - 1);
            by = ay + sy * (h - 1);
        }

        int x = Math.Min(ax, bx), y = Math.Min(ay, by);
        return new Int32Rect(x, y, Math.Abs(bx - ax) + 1, Math.Abs(by - ay) + 1);
    }

    /// <summary>Make w/h match ratio: grow=true enlarges the smaller side, otherwise shrinks the larger one.</summary>
    internal static (int W, int H) FitRatio(int w, int h, double ratio, bool grow)
    {
        if ((double)w / h > ratio == grow) h = Math.Max(1, (int)Math.Round(w / ratio));
        else w = Math.Max(1, (int)Math.Round(h * ratio));
        return (w, h);
    }

    private static (int W, int H) Shrink(int w, int h, int maxW, int maxH, double ratio)
    {
        if (w > maxW) { w = maxW; h = Math.Max(1, (int)Math.Round(w / ratio)); }
        if (h > maxH) { h = maxH; w = Math.Max(1, (int)Math.Round(h * ratio)); }
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// Hit-test the selection. Handles (corners, edge midpoints) win, then edges (if <paramref name="includeEdges"/>),
    /// then the inside (if <paramref name="includeInside"/>). Tolerances are in the same units as the point.
    /// </summary>
    public static HitKind HitTest(Int32Rect sel, Point p, double handleTolerance, double edgeTolerance,
        bool includeEdges = true, bool includeInside = true)
    {
        if (IsEmpty(sel)) return HitKind.None;
        double l = sel.X, t = sel.Y, r = sel.X + sel.Width, b = sel.Y + sel.Height;
        double cx = (l + r) / 2, cy = (t + b) / 2;

        bool Near(double px, double py) => Math.Abs(p.X - px) <= handleTolerance && Math.Abs(p.Y - py) <= handleTolerance;

        if (Near(l, t)) return HitKind.TopLeft;
        if (Near(r, t)) return HitKind.TopRight;
        if (Near(l, b)) return HitKind.BottomLeft;
        if (Near(r, b)) return HitKind.BottomRight;
        // Mid handles only if the selection is big enough for them not to overlap the corners.
        bool midX = sel.Width > handleTolerance * 4, midY = sel.Height > handleTolerance * 4;
        if (midX && Near(cx, t)) return HitKind.Top;
        if (midX && Near(cx, b)) return HitKind.Bottom;
        if (midY && Near(l, cy)) return HitKind.Left;
        if (midY && Near(r, cy)) return HitKind.Right;

        if (includeEdges)
        {
            bool inX = p.X >= l - edgeTolerance && p.X <= r + edgeTolerance;
            bool inY = p.Y >= t - edgeTolerance && p.Y <= b + edgeTolerance;
            if (inY && Math.Abs(p.X - l) <= edgeTolerance) return HitKind.Left;
            if (inY && Math.Abs(p.X - r) <= edgeTolerance) return HitKind.Right;
            if (inX && Math.Abs(p.Y - t) <= edgeTolerance) return HitKind.Top;
            if (inX && Math.Abs(p.Y - b) <= edgeTolerance) return HitKind.Bottom;
        }

        if (includeInside && p.X >= l && p.X < r && p.Y >= t && p.Y < b) return HitKind.Inside;
        return HitKind.None;
    }

    /// <summary>Move a rectangle by (dx, dy), keeping it fully inside <paramref name="bounds"/>.</summary>
    public static Int32Rect Move(Int32Rect start, int dx, int dy, Int32Rect bounds)
    {
        int w = Math.Min(start.Width, bounds.Width), h = Math.Min(start.Height, bounds.Height);
        int x = Math.Clamp(start.X + dx, bounds.X, bounds.X + bounds.Width - w);
        int y = Math.Clamp(start.Y + dy, bounds.Y, bounds.Y + bounds.Height - h);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>
    /// Resize <paramref name="start"/> by dragging the part <paramref name="kind"/> by (dx, dy). Dragging past the
    /// opposite side flips the rectangle. Optional fixed ratio (Shift = 1.0). Result is clamped to bounds, min 1×1.
    /// </summary>
    public static Int32Rect Resize(Int32Rect start, HitKind kind, int dx, int dy, Int32Rect bounds, double? ratio = null)
    {
        if (kind == HitKind.Inside) return Move(start, dx, dy, bounds);
        if (kind == HitKind.None) return start;

        int l = start.X, t = start.Y, r = start.X + start.Width, b = start.Y + start.Height;
        bool moveL = kind is HitKind.Left or HitKind.TopLeft or HitKind.BottomLeft;
        bool moveR = kind is HitKind.Right or HitKind.TopRight or HitKind.BottomRight;
        bool moveT = kind is HitKind.Top or HitKind.TopLeft or HitKind.TopRight;
        bool moveB = kind is HitKind.Bottom or HitKind.BottomLeft or HitKind.BottomRight;
        if (moveL) l += dx;
        if (moveR) r += dx;
        if (moveT) t += dy;
        if (moveB) b += dy;

        // Flip (normalize) and enforce at least 1 px.
        if (l > r) (l, r) = (r, l);
        if (t > b) (t, b) = (b, t);
        if (r == l) { if (moveL) l = r - 1; else r = l + 1; }
        if (b == t) { if (moveT) t = b - 1; else b = t + 1; }

        l = Math.Max(l, bounds.X); t = Math.Max(t, bounds.Y);
        r = Math.Min(r, bounds.X + bounds.Width); b = Math.Min(b, bounds.Y + bounds.Height);
        if (r <= l) r = Math.Min(l + 1, bounds.X + bounds.Width);
        if (b <= t) b = Math.Min(t + 1, bounds.Y + bounds.Height);

        if (ratio is > 0 and var k)
        {
            // Anchor = the sides that did not move.
            int w = r - l, h = b - t;
            bool corner = (moveL || moveR) && (moveT || moveB);
            if (corner) (w, h) = FitRatio(w, h, k, grow: true);
            else if (moveL || moveR) h = Math.Max(1, (int)Math.Round(w / k));
            else w = Math.Max(1, (int)Math.Round(h * k));

            // The moved sides absorb the change; the opposite (anchor) sides stay fixed.
            bool growLeft = moveL;
            bool growUp = moveT;
            int maxW = growLeft ? r - bounds.X : bounds.X + bounds.Width - l;
            int maxH = growUp ? b - bounds.Y : bounds.Y + bounds.Height - t;
            (w, h) = Shrink(w, h, maxW, maxH, k);
            if (growLeft) l = r - w; else r = l + w;
            if (growUp) t = b - h; else b = t + h;
        }
        return new Int32Rect(l, t, r - l, b - t);
    }

    /// <summary>Apply a ratio to an existing selection, keeping its top-left corner (used when the ratio is chosen).</summary>
    public static Int32Rect ApplyRatio(Int32Rect sel, double ratio, Int32Rect bounds)
    {
        if (IsEmpty(sel) || ratio <= 0) return sel;
        var (w, h) = FitRatio(sel.Width, sel.Height, ratio, grow: false);
        (w, h) = Shrink(w, h, bounds.X + bounds.Width - sel.X, bounds.Y + bounds.Height - sel.Y, ratio);
        return new Int32Rect(sel.X, sel.Y, w, h);
    }

    /// <summary>Keyboard edge adjust: <paramref name="outward"/> grows that edge by 1 (Ctrl+arrow), otherwise shrinks it (Shift+arrow).</summary>
    public static Int32Rect AdjustEdge(Int32Rect sel, Edge edge, bool outward, Int32Rect bounds, int amount = 1)
    {
        int l = sel.X, t = sel.Y, r = sel.X + sel.Width, b = sel.Y + sel.Height;
        int d = outward ? amount : -amount;
        switch (edge)
        {
            case Edge.Left: l = Math.Clamp(l - d, bounds.X, r - 1); break;
            case Edge.Right: r = Math.Clamp(r + d, l + 1, bounds.X + bounds.Width); break;
            case Edge.Top: t = Math.Clamp(t - d, bounds.Y, b - 1); break;
            case Edge.Bottom: b = Math.Clamp(b + d, t + 1, bounds.Y + bounds.Height); break;
        }
        return new Int32Rect(l, t, r - l, b - t);
    }

    /// <summary>Parse "16:9" / "1:1" → 16/9. Empty or invalid → null (free).</summary>
    public static double? ParseRatio(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(':', '/', 'x', '×');
        if (parts.Length != 2) return null;
        if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ||
            !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ||
            a <= 0 || b <= 0) return null;
        return a / b;
    }
}
