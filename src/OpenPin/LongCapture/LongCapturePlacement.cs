using System.Windows;

namespace OpenPin.LongCapture;

/// <summary>Pure geometry for the long-capture windows (physical pixels).</summary>
internal static class LongCapturePlacement
{
    public static int Right(Int32Rect r) => r.X + r.Width;
    public static int Bottom(Int32Rect r) => r.Y + r.Height;

    public static Int32Rect Inflate(Int32Rect r, int d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    public static bool Intersects(Int32Rect a, Int32Rect b) =>
        a.X < Right(b) && b.X < Right(a) && a.Y < Bottom(b) && b.Y < Bottom(a);

    public static bool Contains(Int32Rect outer, Int32Rect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && Right(inner) <= Right(outer) && Bottom(inner) <= Bottom(outer);

    /// <summary>Moves <paramref name="r"/> (keeping its size) to lie inside <paramref name="area"/> as far as possible.</summary>
    public static Int32Rect ClampInto(Int32Rect r, Int32Rect area)
    {
        int x = Math.Max(area.X, Math.Min(r.X, Right(area) - r.Width));
        int y = Math.Max(area.Y, Math.Min(r.Y, Bottom(area) - r.Height));
        return new Int32Rect(x, y, r.Width, r.Height);
    }

    /// <summary>
    /// Control bar: below the region (left-aligned), else above, else inside the region's bottom edge; always kept
    /// inside the work area. <paramref name="keepOut"/> = region inflated by the frame's grab ring.
    /// </summary>
    public static Int32Rect PlaceBar(Int32Rect keepOut, int width, int height, Int32Rect work, int gap)
    {
        var below = ClampInto(new Int32Rect(keepOut.X, Bottom(keepOut) + gap, width, height), work);
        if (!Intersects(below, keepOut)) return below;
        var above = ClampInto(new Int32Rect(keepOut.X, keepOut.Y - gap - height, width, height), work);
        if (!Intersects(above, keepOut)) return above;
        return ClampInto(new Int32Rect(keepOut.X + gap, Bottom(keepOut) - gap - height, width, height), work);
    }

    /// <summary>
    /// Preview panel. Vertical capture: right of the region, else left, else inside its right edge.
    /// Horizontal capture: above the region, else below the bar, else inside its top edge. Avoids the bar.
    /// </summary>
    public static Int32Rect PlacePreview(Int32Rect keepOut, Int32Rect bar, int width, int height, Int32Rect work, int gap, bool vertical)
    {
        var candidates = vertical
            ? new[]
            {
                new Int32Rect(Right(keepOut) + gap, keepOut.Y, width, height),
                new Int32Rect(keepOut.X - gap - width, keepOut.Y, width, height),
            }
            : new[]
            {
                new Int32Rect(keepOut.X, keepOut.Y - gap - height, width, height),
                new Int32Rect(keepOut.X, Math.Max(Bottom(keepOut), Bottom(bar)) + gap, width, height),
            };
        foreach (var c in candidates)
        {
            // Only slide along the free axis: the panel must stay beside the region, not over it.
            var placed = ClampInto(c, work);
            if (Contains(work, placed) && !Intersects(placed, keepOut) && !Intersects(placed, bar)) return placed;
        }
        var inside = vertical
            ? new Int32Rect(Right(keepOut) - gap - width, keepOut.Y + gap, width, height)
            : new Int32Rect(keepOut.X + gap, keepOut.Y + gap, width, height);
        return ClampInto(inside, work);
    }
}
