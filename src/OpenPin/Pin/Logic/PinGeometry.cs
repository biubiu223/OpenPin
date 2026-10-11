using System.Windows;

namespace OpenPin.Pin.Logic;

/// <summary>Which edges of a pin are being dragged (border resize).</summary>
[Flags]
public enum PinEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>
/// Pure placement / zoom math for pin windows. All rectangles and points are PHYSICAL pixels unless they say
/// "content units" (1 content unit = <c>BaseScale × Zoom</c> physical pixels).
/// </summary>
public static class PinGeometry
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 20;
    /// <summary>Upper bound for a pin's visible size in physical pixels (layered windows get slow / fail beyond this).</summary>
    public const int MaxVisiblePx = 16000;
    /// <summary>A pin never gets smaller than this on screen.</summary>
    public const int MinVisiblePx = 8;

    public static int Right(this Int32Rect r) => r.X + r.Width;
    public static int Bottom(this Int32Rect r) => r.Y + r.Height;

    /// <summary>Zoom clamped so that the visible size stays within [MinVisiblePx, MaxVisiblePx].</summary>
    public static double ClampZoom(double zoom, double contentWidth, double contentHeight, double baseScale)
    {
        double w = Math.Max(1, contentWidth * baseScale), h = Math.Max(1, contentHeight * baseScale);
        double min = Math.Max(MinZoom, MinVisiblePx / Math.Min(w, h));
        double max = Math.Min(MaxZoom, MaxVisiblePx / Math.Max(w, h));
        if (max < min) max = min;
        return Math.Clamp(zoom, min, max);
    }

    /// <summary>
    /// Physical rectangle covered by the visible part of the content. <paramref name="viewport"/> (content units) is
    /// the thumbnail-mode window into the content; null = everything.
    /// </summary>
    public static Int32Rect VisibleRect(int originX, int originY, Size contentSize, Rect? viewport, double scale)
    {
        var v = viewport ?? new Rect(0, 0, contentSize.Width, contentSize.Height);
        int x = originX + (int)Math.Round(v.X * scale);
        int y = originY + (int)Math.Round(v.Y * scale);
        int w = Math.Max(1, (int)Math.Round(v.Width * scale));
        int h = Math.Max(1, (int)Math.Round(v.Height * scale));
        return new Int32Rect(x, y, w, h);
    }

    public static Int32Rect Inflate(Int32Rect r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    /// <summary>
    /// New content origin after changing the scale while keeping <paramref name="anchor"/> (screen px) fixed on screen.
    /// </summary>
    public static Point ZoomAround(Point origin, double oldScale, double newScale, Point anchor)
    {
        double k = newScale / oldScale;
        return new Point(anchor.X - (anchor.X - origin.X) * k, anchor.Y - (anchor.Y - origin.Y) * k);
    }

    /// <summary>Anchor point for wheel zoom according to the setting.</summary>
    public static Point AnchorPoint(WheelZoomAnchor anchor, Int32Rect visible, Point cursor) => anchor switch
    {
        WheelZoomAnchor.Mouse => cursor,
        WheelZoomAnchor.TopLeft => new Point(visible.X, visible.Y),
        WheelZoomAnchor.TopRight => new Point(visible.Right(), visible.Y),
        WheelZoomAnchor.BottomLeft => new Point(visible.X, visible.Bottom()),
        WheelZoomAnchor.BottomRight => new Point(visible.Right(), visible.Bottom()),
        _ => new Point(visible.X + visible.Width / 2.0, visible.Y + visible.Height / 2.0),
    };

    /// <summary>
    /// One wheel step: ±<paramref name="stepPercent"/> percent, snapping to multiples of the step
    /// (so 33% → 40% / 30% with a 10% step). Result in zoom units (1 = 100%), not clamped.
    /// </summary>
    public static double StepZoom(double zoom, int stepPercent, int direction)
    {
        if (stepPercent <= 0) stepPercent = 10;
        double p = Math.Round(zoom * 100, 3);
        double q = p / stepPercent;
        double next = direction > 0
            ? (Math.Floor(q + 1e-9) + 1) * stepPercent
            : (Math.Ceiling(q - 1e-9) - 1) * stepPercent;
        if (direction < 0 && next < stepPercent)
        {
            // below one step: keep shrinking smoothly instead of hitting 0
            next = Math.Max(MinZoom * 100, Math.Min(next, p / 2));
            if (next <= 0) next = MinZoom * 100;
        }
        return next / 100.0;
    }

    /// <summary>Moves <paramref name="rect"/> inside <paramref name="work"/>; if it is larger, its top-left aligns with the work area.</summary>
    public static Int32Rect ClampInto(Int32Rect rect, Int32Rect work)
    {
        int x = rect.Width >= work.Width ? work.X : Math.Clamp(rect.X, work.X, work.Right() - rect.Width);
        int y = rect.Height >= work.Height ? work.Y : Math.Clamp(rect.Y, work.Y, work.Bottom() - rect.Height);
        return new Int32Rect(x, y, rect.Width, rect.Height);
    }

    public static int IntersectionArea(Int32Rect a, Int32Rect b)
    {
        int w = Math.Min(a.Right(), b.Right()) - Math.Max(a.X, b.X);
        int h = Math.Min(a.Bottom(), b.Bottom()) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>
    /// "Bring into view": returns the offset (dx, dy) that moves <paramref name="rect"/> so it is reachable. A rect that
    /// already shows at least <paramref name="minVisible"/>×<paramref name="minVisible"/> px on some work area stays;
    /// otherwise it is clamped into the work area that it overlaps most (or the nearest one by center distance).
    /// </summary>
    public static (int Dx, int Dy) BringIntoView(Int32Rect rect, IReadOnlyList<Int32Rect> workAreas, int minVisible = 40)
    {
        if (workAreas.Count == 0) return (0, 0);
        int need = Math.Min(minVisible, rect.Width) * Math.Min(minVisible, rect.Height);
        if (workAreas.Any(w => IntersectionArea(rect, w) >= need)) return (0, 0);
        var target = Best(rect, workAreas);
        var c = ClampInto(rect, target);
        return (c.X - rect.X, c.Y - rect.Y);
    }

    /// <summary>The work area overlapping the rect the most, or the nearest one (center distance).</summary>
    public static Int32Rect Best(Int32Rect rect, IReadOnlyList<Int32Rect> workAreas)
    {
        var overl = workAreas.OrderByDescending(w => IntersectionArea(rect, w)).First();
        if (IntersectionArea(rect, overl) > 0) return overl;
        double cx = rect.X + rect.Width / 2.0, cy = rect.Y + rect.Height / 2.0;
        return workAreas.OrderBy(w => Sq(w.X + w.Width / 2.0 - cx) + Sq(w.Y + w.Height / 2.0 - cy)).First();
    }

    private static double Sq(double v) => v * v;

    /// <summary>Largest zoom ≤ 1 so that the content fits <paramref name="fraction"/> of the work area.</summary>
    public static double FitZoom(double widthPx, double heightPx, Int32Rect work, double fraction = 0.9)
    {
        if (widthPx <= 0 || heightPx <= 0) return 1;
        double z = Math.Min(work.Width * fraction / widthPx, work.Height * fraction / heightPx);
        return Math.Min(1, z);
    }

    /// <summary>Top-left that centers a w×h rect on a point.</summary>
    public static Point CenterOn(Point center, double w, double h) => new(Math.Round(center.X - w / 2), Math.Round(center.Y - h / 2));

    /// <summary>
    /// Places a floating toolbar of <paramref name="size"/> next to <paramref name="anchor"/> (the pin's visible rect):
    /// below if it fits in the work area, else above, else inside the bottom of the anchor; right-aligned with the
    /// anchor and always inside the work area.
    /// </summary>
    public static Int32Rect PlaceToolbar(Int32Rect anchor, int width, int height, Int32Rect work, int gap)
    {
        int x = anchor.Right() - width;
        int y;
        if (anchor.Bottom() + gap + height <= work.Bottom()) y = anchor.Bottom() + gap;
        else if (anchor.Y - gap - height >= work.Y) y = anchor.Y - gap - height;
        else y = anchor.Bottom() - height - gap;
        return ClampInto(new Int32Rect(x, y, width, height), work);
    }

    /// <summary>Which edges of the visible rect a point (screen px) grabs, within <paramref name="grip"/> px inside the rect.</summary>
    public static PinEdges HitEdges(Int32Rect visible, Point p, int grip)
    {
        if (visible.Width < grip * 3 || visible.Height < grip * 3) return PinEdges.None;
        if (p.X < visible.X || p.Y < visible.Y || p.X >= visible.Right() || p.Y >= visible.Bottom()) return PinEdges.None;
        var e = PinEdges.None;
        if (p.X < visible.X + grip) e |= PinEdges.Left;
        else if (p.X >= visible.Right() - grip) e |= PinEdges.Right;
        if (p.Y < visible.Y + grip) e |= PinEdges.Top;
        else if (p.Y >= visible.Bottom() - grip) e |= PinEdges.Bottom;
        return e;
    }

    /// <summary>
    /// Border-drag resize keeping the aspect ratio. Returns the new zoom factor ratio (new/old visible size) for a drag of
    /// (dx, dy) px on <paramref name="edges"/> of a visible rect of <paramref name="width"/>×<paramref name="height"/>,
    /// and the anchor (the opposite edge / corner) in screen px.
    /// </summary>
    public static (double Ratio, Point Anchor) EdgeResize(Int32Rect start, PinEdges edges, double dx, double dy)
    {
        double? rx = null, ry = null;
        if (edges.HasFlag(PinEdges.Right)) rx = (start.Width + dx) / start.Width;
        else if (edges.HasFlag(PinEdges.Left)) rx = (start.Width - dx) / start.Width;
        if (edges.HasFlag(PinEdges.Bottom)) ry = (start.Height + dy) / start.Height;
        else if (edges.HasFlag(PinEdges.Top)) ry = (start.Height - dy) / start.Height;
        double ratio = rx is { } a && ry is { } b ? Math.Max(a, b) : rx ?? ry ?? 1;
        ratio = Math.Max(0.01, ratio);

        double ax = edges.HasFlag(PinEdges.Left) ? start.Right() : edges.HasFlag(PinEdges.Right) ? start.X : start.X + start.Width / 2.0;
        double ay = edges.HasFlag(PinEdges.Top) ? start.Bottom() : edges.HasFlag(PinEdges.Bottom) ? start.Y : start.Y + start.Height / 2.0;
        return (ratio, new Point(ax, ay));
    }

    /// <summary>
    /// Moves the edges of a thumbnail viewport (content units) by (dx, dy) content units, keeping it inside the content
    /// and at least <paramref name="minSize"/> units large.
    /// </summary>
    public static Rect ResizeViewport(Rect v, PinEdges edges, double dx, double dy, Size content, double minSize)
    {
        double l = v.Left, t = v.Top, r = v.Right, b = v.Bottom;
        if (edges.HasFlag(PinEdges.Left)) l = Math.Clamp(l + dx, 0, r - minSize);
        if (edges.HasFlag(PinEdges.Right)) r = Math.Clamp(r + dx, l + minSize, content.Width);
        if (edges.HasFlag(PinEdges.Top)) t = Math.Clamp(t + dy, 0, b - minSize);
        if (edges.HasFlag(PinEdges.Bottom)) b = Math.Clamp(b + dy, t + minSize, content.Height);
        return new Rect(Math.Max(0, l), Math.Max(0, t), Math.Max(minSize, r - l), Math.Max(minSize, b - t));
    }

    /// <summary>Keeps a viewport inside the content (used for Shift+drag panning).</summary>
    public static Rect ClampViewport(Rect v, Size content)
    {
        double w = Math.Min(v.Width, content.Width), h = Math.Min(v.Height, content.Height);
        double x = Math.Clamp(v.X, 0, content.Width - w), y = Math.Clamp(v.Y, 0, content.Height - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>Initial thumbnail viewport: a square of <paramref name="sidePx"/> physical px around a content point.</summary>
    public static Rect ThumbnailViewport(Size content, double scale, double sidePx, Point focus)
    {
        double side = sidePx / scale;
        double w = Math.Min(content.Width, side), h = Math.Min(content.Height, side);
        return ClampViewport(new Rect(focus.X - w / 2, focus.Y - h / 2, w, h), content);
    }
}
