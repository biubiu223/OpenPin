using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Model;

namespace OpenPin.Annotation.Rendering;

/// <summary>Immutable copy of a smart-erase item's area (safe to hand to a worker thread).</summary>
internal sealed record SmartEraseShape(DrawMode Mode, Rect Rect, Point[] Points, double Width)
{
    /// <summary>Approximate half thickness of the area (used for the search margin and dependency tests).</summary>
    public double Radius => Mode == DrawMode.Rect ? Math.Min(Rect.Width, Rect.Height) / 2 : Width / 2;

    /// <summary>Generous bounds of the area (image px, unclipped).</summary>
    public Rect Bounds => Mode == DrawMode.Rect ? Geo.Inflate(Rect, 1) : Geo.Inflate(Geo.BoundsOf(Points), Width / 2 + 2);

    /// <summary>Context pixels around the hole the inpainter may read (margin grows with the hole thickness).</summary>
    public static int Margin(double radius) => Math.Clamp((int)Math.Ceiling(2.5 * radius) + 24, 24, 192);

    /// <summary>Pixels the computation reads (hole + margin), clipped to the image.</summary>
    public Int32Rect WorkRect(int imgW, int imgH) => Geo.ToPixelRect(Geo.Inflate(Bounds, Margin(Radius)), imgW, imgH);
}

/// <summary>Rasterised smart-erase mask: 1 = pixel to fill. Bounds are image px (clipped), mask is Bounds-sized.</summary>
internal readonly record struct SmartEraseMask(Int32Rect Bounds, byte[] Mask)
{
    public bool IsEmpty => Bounds.Width <= 0 || Bounds.Height <= 0;

    /// <summary>
    /// Pixels covered by the shape. Rectangle: every pixel the rectangle overlaps. Brush: pixel centres within
    /// width/2 + 0.5 of the (smoothed) stroke centre line, i.e. every pixel the anti-aliased stroke touches.
    /// </summary>
    public static SmartEraseMask Rasterize(SmartEraseShape s, int imgW, int imgH)
    {
        if (s.Mode == DrawMode.Rect)
        {
            const double eps = 1e-3;
            var r = s.Rect;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return default;
            int x0 = Math.Clamp((int)Math.Floor(r.Left + eps), 0, imgW), y0 = Math.Clamp((int)Math.Floor(r.Top + eps), 0, imgH);
            int x1 = Math.Clamp((int)Math.Ceiling(r.Right - eps), 0, imgW), y1 = Math.Clamp((int)Math.Ceiling(r.Bottom - eps), 0, imgH);
            if (x1 <= x0 && x0 < imgW && r.Width > 0) x1 = x0 + 1;
            if (y1 <= y0 && y0 < imgH && r.Height > 0) y1 = y0 + 1;
            if (x1 <= x0 || y1 <= y0) return default;
            var b = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
            var m = new byte[b.Width * b.Height];
            Array.Fill(m, (byte)1);
            return new SmartEraseMask(b, m);
        }

        var line = FlattenStroke(s.Points);
        if (line.Count == 0) return default;
        double rad = Math.Max(0.5, s.Width / 2) + 0.5;
        var bb = Geo.ToPixelRect(Geo.Inflate(Geo.BoundsOf(line), rad + 1), imgW, imgH);
        if (bb.Width <= 0 || bb.Height <= 0) return default;
        var full = new byte[bb.Width * bb.Height];
        double r2 = rad * rad;
        for (int k = 0; k < Math.Max(1, line.Count - 1); k++)
        {
            var a = line[k];
            var c = line.Count > 1 ? line[k + 1] : a;
            int sx0 = Math.Max(bb.X, (int)Math.Floor(Math.Min(a.X, c.X) - rad)), sx1 = Math.Min(bb.X + bb.Width - 1, (int)Math.Ceiling(Math.Max(a.X, c.X) + rad));
            int sy0 = Math.Max(bb.Y, (int)Math.Floor(Math.Min(a.Y, c.Y) - rad)), sy1 = Math.Min(bb.Y + bb.Height - 1, (int)Math.Ceiling(Math.Max(a.Y, c.Y) + rad));
            double vx = c.X - a.X, vy = c.Y - a.Y, len2 = vx * vx + vy * vy;
            for (int y = sy0; y <= sy1; y++)
            {
                int row = (y - bb.Y) * bb.Width - bb.X;
                double py = y + 0.5;
                for (int x = sx0; x <= sx1; x++)
                {
                    if (full[row + x] != 0) continue;
                    double px = x + 0.5;
                    double t = len2 > 0 ? Math.Clamp(((px - a.X) * vx + (py - a.Y) * vy) / len2, 0, 1) : 0;
                    double dx = px - (a.X + t * vx), dy = py - (a.Y + t * vy);
                    if (dx * dx + dy * dy <= r2) full[row + x] = 1;
                }
            }
        }
        // Tighten to the covered pixels.
        int mx0 = int.MaxValue, my0 = int.MaxValue, mx1 = -1, my1 = -1;
        for (int y = 0; y < bb.Height; y++)
        for (int x = 0; x < bb.Width; x++)
        {
            if (full[y * bb.Width + x] == 0) continue;
            if (x < mx0) mx0 = x; if (x > mx1) mx1 = x; if (y < my0) my0 = y; if (y > my1) my1 = y;
        }
        if (mx1 < 0) return default;
        var tb = new Int32Rect(bb.X + mx0, bb.Y + my0, mx1 - mx0 + 1, my1 - my0 + 1);
        var tm = new byte[tb.Width * tb.Height];
        for (int y = 0; y < tb.Height; y++)
            Buffer.BlockCopy(full, (y + my0) * bb.Width + mx0, tm, y * tb.Width, tb.Width);
        return new SmartEraseMask(tb, tm);
    }

    /// <summary>The centre line of <see cref="Geo.SmoothPath"/> as a polyline (quadratic Béziers through segment midpoints).</summary>
    internal static List<Point> FlattenStroke(IReadOnlyList<Point> pts)
    {
        var res = new List<Point>();
        if (pts.Count == 0) return res;
        res.Add(pts[0]);
        if (pts.Count == 1) return res;
        if (pts.Count == 2) { res.Add(pts[1]); return res; }
        var cur = pts[0];
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var ctrl = pts[i];
            var end = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
            double len = (ctrl - cur).Length + (end - ctrl).Length;
            int steps = Math.Clamp((int)Math.Ceiling(len / 2), 1, 64);
            for (int k = 1; k <= steps; k++)
            {
                double t = (double)k / steps, u = 1 - t;
                res.Add(new Point(u * u * cur.X + 2 * u * t * ctrl.X + t * t * end.X, u * u * cur.Y + 2 * u * t * ctrl.Y + t * t * end.Y));
            }
            cur = end;
        }
        res.Add(pts[^1]);
        return res;
    }
}

/// <summary>A computed smart-erase result: the filled pixels of the hole, ready to draw.</summary>
internal sealed class SmartErasePatch
{
    public required Int32Rect Bounds { get; init; }
    /// <summary>1 = filled pixel (Bounds-sized).</summary>
    public required byte[] Mask { get; init; }
    /// <summary>BGRA of Bounds; only pixels with Mask = 1 are meaningful.</summary>
    public required byte[] Pixels { get; init; }
    /// <summary>Frozen Bgra32 image of Bounds, alpha 0 outside the mask. Null when there was nothing to fill or it failed.</summary>
    public BitmapSource? Image { get; init; }
    public InpaintMethod Method { get; init; }
    public bool Failed { get; init; }
    public double Milliseconds { get; init; }

    public long Bytes => Mask.Length + Pixels.Length * 2L;

    /// <summary>Write the filled pixels into a BGRA buffer that covers <paramref name="area"/> (image px).</summary>
    public void ApplyTo(byte[] buffer, Int32Rect area)
    {
        if (Failed) return;
        for (int y = 0; y < Bounds.Height; y++)
        {
            int ay = Bounds.Y + y - area.Y;
            if (ay < 0 || ay >= area.Height) continue;
            for (int x = 0; x < Bounds.Width; x++)
            {
                if (Mask[y * Bounds.Width + x] == 0) continue;
                int ax = Bounds.X + x - area.X;
                if (ax < 0 || ax >= area.Width) continue;
                Buffer.BlockCopy(Pixels, (y * Bounds.Width + x) * 4, buffer, (ay * area.Width + ax) * 4, 4);
            }
        }
    }

    /// <summary>Compute the patch for <paramref name="shape"/> from <paramref name="buffer"/> (BGRA covering <paramref name="area"/>).</summary>
    public static SmartErasePatch Compute(SmartEraseShape shape, byte[] buffer, Int32Rect area, int imgW, int imgH)
    {
        var sw = Stopwatch.StartNew();
        var mask = SmartEraseMask.Rasterize(shape, imgW, imgH);
        if (mask.IsEmpty)
            return new SmartErasePatch { Bounds = new Int32Rect(), Mask = Array.Empty<byte>(), Pixels = Array.Empty<byte>() };
        var work = Intersect(shape.WorkRect(imgW, imgH), area);
        // The hole must lie inside the work area (it always does: work = bounds + margin, clipped like the mask).
        var hole = Intersect(mask.Bounds, work);
        int ww = work.Width, wh = work.Height;
        var px = new byte[ww * wh * 4];
        for (int y = 0; y < wh; y++)
            Buffer.BlockCopy(buffer, ((work.Y - area.Y + y) * area.Width + (work.X - area.X)) * 4, px, y * ww * 4, ww * 4);
        var wm = new byte[ww * wh];
        for (int y = hole.Y; y < hole.Y + hole.Height; y++)
        for (int x = hole.X; x < hole.X + hole.Width; x++)
            wm[(y - work.Y) * ww + (x - work.X)] = mask.Mask[(y - mask.Bounds.Y) * mask.Bounds.Width + (x - mask.Bounds.X)];

        var method = Inpainter.Fill(px, ww, wh, wm);

        var b = mask.Bounds;
        var outPx = new byte[b.Width * b.Height * 4];
        var img = new byte[b.Width * b.Height * 4];
        for (int y = 0; y < b.Height; y++)
        for (int x = 0; x < b.Width; x++)
        {
            int ix = b.X + x - work.X, iy = b.Y + y - work.Y;
            if (ix < 0 || iy < 0 || ix >= ww || iy >= wh) continue;
            int o = (y * b.Width + x) * 4, s = (iy * ww + ix) * 4;
            Buffer.BlockCopy(px, s, outPx, o, 4);
            if (mask.Mask[y * b.Width + x] != 0) Buffer.BlockCopy(px, s, img, o, 4);
        }
        BitmapSource? bmp = method == InpaintMethod.None ? null : PixelEffects.ToBitmap(img, b.Width, b.Height);
        return new SmartErasePatch
        {
            Bounds = b,
            Mask = method == InpaintMethod.None ? new byte[mask.Mask.Length] : mask.Mask,
            Pixels = outPx,
            Image = bmp,
            Method = method,
            Milliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
        int x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x1 <= x0 || y1 <= y0 ? new Int32Rect() : new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
