using System.Windows;

namespace OpenPin.Capture;

internal enum ToolbarSide { Below, Above, Inside }

/// <summary>
/// Pure placement math for floating UI (toolbar, magnifier, size label, hints). All inputs share one coordinate
/// space (the overlay uses image pixels); results are top-left positions in that space, always inside the monitor.
/// </summary>
internal static class Placement
{
    /// <summary>
    /// Toolbar: below the selection (right-aligned to it), else above, else inside the selection's bottom edge.
    /// Always kept inside <paramref name="monitor"/>.
    /// </summary>
    public static (Point Position, ToolbarSide Side) PlaceToolbar(Rect selection, Size toolbar, Rect monitor, double gap)
    {
        double x = ClampAxis(selection.Right - toolbar.Width, toolbar.Width, monitor.Left, monitor.Right);
        if (selection.Bottom + gap + toolbar.Height <= monitor.Bottom)
            return (new Point(x, Math.Max(monitor.Top, selection.Bottom + gap)), ToolbarSide.Below);
        if (selection.Top - gap - toolbar.Height >= monitor.Top)
            return (new Point(x, Math.Min(monitor.Bottom - toolbar.Height, selection.Top - gap - toolbar.Height)), ToolbarSide.Above);
        double y = Math.Min(selection.Bottom, monitor.Bottom) - gap - toolbar.Height;
        return (new Point(x, ClampAxis(y, toolbar.Height, monitor.Top, monitor.Bottom)), ToolbarSide.Inside);
    }

    /// <summary>Something that follows the cursor (magnifier): bottom-right of it, flipped when it would leave the monitor.</summary>
    public static Point PlaceNearCursor(Point cursor, Size size, Rect monitor, double offset)
    {
        double x = cursor.X + offset;
        if (x + size.Width > monitor.Right) x = cursor.X - offset - size.Width;
        double y = cursor.Y + offset;
        if (y + size.Height > monitor.Bottom) y = cursor.Y - offset - size.Height;
        return new Point(ClampAxis(x, size.Width, monitor.Left, monitor.Right), ClampAxis(y, size.Height, monitor.Top, monitor.Bottom));
    }

    /// <summary>Size label: above the selection's top-left corner, or inside it when there is no room above.</summary>
    public static Point PlaceLabel(Rect selection, Size label, Rect monitor, double gap)
    {
        double x = ClampAxis(selection.Left, label.Width, monitor.Left, monitor.Right);
        double y = selection.Top - gap - label.Height;
        if (y < monitor.Top) y = Math.Max(selection.Top, monitor.Top) + gap;
        return new Point(x, ClampAxis(y, label.Height, monitor.Top, monitor.Bottom));
    }

    /// <summary>
    /// Shortcut-hint panel: bottom-left corner of the monitor; bottom-right if the cursor/selection overlaps it;
    /// null (hide) if both corners are obstructed.
    /// </summary>
    public static Point? PlaceHints(Size size, Rect monitor, double margin, Rect? obstacle1, Rect? obstacle2)
    {
        var left = new Rect(monitor.Left + margin, monitor.Bottom - margin - size.Height, size.Width, size.Height);
        var right = new Rect(monitor.Right - margin - size.Width, left.Top, size.Width, size.Height);
        bool Blocked(Rect r) => (obstacle1 is { } a && a.IntersectsWith(r)) || (obstacle2 is { } b && b.IntersectsWith(r));
        if (!Blocked(left)) return left.TopLeft;
        if (!Blocked(right)) return right.TopLeft;
        return null;
    }

    /// <summary>Index of the monitor with the largest overlap with <paramref name="r"/> (or nearest to its center).</summary>
    public static int PickMonitor(IReadOnlyList<Rect> monitors, Rect r)
    {
        if (monitors.Count == 0) return -1;
        int best = 0;
        double bestArea = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            var inter = Rect.Intersect(monitors[i], r);
            double area = inter.IsEmpty ? 0 : inter.Width * inter.Height;
            if (area > bestArea) { bestArea = area; best = i; }
        }
        if (bestArea > 0) return best;
        return MonitorAt(monitors, new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
    }

    /// <summary>Index of the monitor containing <paramref name="p"/>, or the nearest one.</summary>
    public static int MonitorAt(IReadOnlyList<Rect> monitors, Point p)
    {
        if (monitors.Count == 0) return -1;
        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (p.X >= m.Left && p.X < m.Right && p.Y >= m.Top && p.Y < m.Bottom) return i;
            double dx = Math.Max(Math.Max(m.Left - p.X, 0), p.X - m.Right);
            double dy = Math.Max(Math.Max(m.Top - p.Y, 0), p.Y - m.Bottom);
            double d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static double ClampAxis(double v, double size, double min, double max) =>
        size >= max - min ? min : Math.Clamp(v, min, max - size);
}
