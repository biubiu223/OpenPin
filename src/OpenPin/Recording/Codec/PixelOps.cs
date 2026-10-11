using System.Runtime.InteropServices;
using System.Windows;

namespace OpenPin.Recording.Codec;

/// <summary>
/// UI-free pixel helpers for 32-bit BGRA (top-down, stride = width*4) buffers. The alpha byte of captured screen pixels is
/// undefined, so all comparisons ignore it (mask 0x00FFFFFF on the little-endian uint view).
/// </summary>
internal static class PixelOps
{
    public const uint RgbMask = 0x00FFFFFF;

    /// <summary>
    /// Bounding rectangle of the pixels whose RGB differs between <paramref name="a"/> and <paramref name="b"/>
    /// (same size), or an empty rect (Width = 0) when identical.
    /// </summary>
    public static Int32Rect FindChangedRect(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        var pa = MemoryMarshal.Cast<byte, uint>(a);
        var pb = MemoryMarshal.Cast<byte, uint>(b);
        int minX = int.MaxValue, maxX = -1, minY = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            var ra = pa.Slice(y * width, width);
            var rb = pb.Slice(y * width, width);
            if (ra.SequenceEqual(rb)) continue;

            // first differing pixel (RGB only)
            int first = ra.CommonPrefixLength(rb);
            while (first < width && ((ra[first] ^ rb[first]) & RgbMask) == 0)
            {
                first++;
                if (first < width) first += ra[first..].CommonPrefixLength(rb[first..]);
            }
            if (first >= width) continue; // only alpha differed

            int last = width - 1;
            while (last > first && ((ra[last] ^ rb[last]) & RgbMask) == 0) last--;

            if (minY < 0) minY = y;
            maxY = y;
            if (first < minX) minX = first;
            if (last > maxX) maxX = last;
        }
        return minY < 0 ? Int32Rect.Empty : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Copy a rectangle out of a BGRA image into a tightly packed BGRA buffer.</summary>
    public static void CopyRect(ReadOnlySpan<byte> src, int srcWidth, Int32Rect r, Span<byte> dst)
    {
        int rowBytes = r.Width * 4;
        for (int y = 0; y < r.Height; y++)
            src.Slice(((r.Y + y) * srcWidth + r.X) * 4, rowBytes).CopyTo(dst.Slice(y * rowBytes, rowBytes));
    }

    /// <summary>Set the alpha byte of every pixel to 255 (makes captured frames displayable as Bgra32).</summary>
    public static void MakeOpaque(Span<byte> bgra)
    {
        var px = MemoryMarshal.Cast<byte, uint>(bgra);
        for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000;
    }

    /// <summary>
    /// Box-filter (area average) resize. Intended for downscaling; upscaling degenerates to nearest neighbour.
    /// Output alpha is 255.
    /// </summary>
    public static void Resize(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        if (sw == dw && sh == dh)
        {
            src[..(sw * sh * 4)].CopyTo(dst);
            MakeOpaque(dst[..(dw * dh * 4)]);
            return;
        }
        Span<int> x0 = dw <= 4096 ? stackalloc int[dw] : new int[dw];
        Span<int> x1 = dw <= 4096 ? stackalloc int[dw] : new int[dw];
        for (int dx = 0; dx < dw; dx++)
        {
            x0[dx] = (int)((long)dx * sw / dw);
            x1[dx] = Math.Max(x0[dx] + 1, (int)((long)(dx + 1) * sw / dw));
        }
        for (int dy = 0; dy < dh; dy++)
        {
            int y0 = (int)((long)dy * sh / dh);
            int y1 = Math.Max(y0 + 1, (int)((long)(dy + 1) * sh / dh));
            int dRow = dy * dw * 4;
            for (int dx = 0; dx < dw; dx++)
            {
                int sb = 0, sg = 0, sr = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int sRow = y * sw * 4;
                    for (int x = x0[dx]; x < x1[dx]; x++)
                    {
                        int i = sRow + x * 4;
                        sb += src[i]; sg += src[i + 1]; sr += src[i + 2];
                        n++;
                    }
                }
                int o = dRow + dx * 4;
                dst[o] = (byte)((sb + n / 2) / n);
                dst[o + 1] = (byte)((sg + n / 2) / n);
                dst[o + 2] = (byte)((sr + n / 2) / n);
                dst[o + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Copy <paramref name="src"/> (sw×sh) into the top-left of <paramref name="dst"/> (dw×dh, dw ≥ sw, dh ≥ sh) and fill the
    /// extra columns/rows by replicating the last column/row (used to pad to even sizes for H.264).
    /// </summary>
    public static void PadReplicate(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        var s = MemoryMarshal.Cast<byte, uint>(src);
        var d = MemoryMarshal.Cast<byte, uint>(dst);
        for (int y = 0; y < dh; y++)
        {
            int sy = Math.Min(y, sh - 1);
            var srow = s.Slice(sy * sw, sw);
            var drow = d.Slice(y * dw, dw);
            srow.CopyTo(drow);
            if (dw > sw) drow[sw..].Fill(srow[sw - 1]);
        }
    }

    /// <summary>Alpha-blend a cursor image onto a BGRA canvas at (x, y) (top-left of the cursor image).</summary>
    public static void DrawCursor(Span<byte> canvas, int cw, int ch, CursorShape shape, int x, int y)
    {
        int w = shape.Width, h = shape.Height;
        var img = shape.Bgra;
        var inv = shape.InvertMask;
        for (int j = 0; j < h; j++)
        {
            int cy = y + j;
            if ((uint)cy >= (uint)ch) continue;
            for (int i = 0; i < w; i++)
            {
                int cx = x + i;
                if ((uint)cx >= (uint)cw) continue;
                int si = (j * w + i) * 4, di = (cy * cw + cx) * 4;
                if (inv != null && inv[j * w + i] != 0)
                {
                    canvas[di] = (byte)(255 - canvas[di]);
                    canvas[di + 1] = (byte)(255 - canvas[di + 1]);
                    canvas[di + 2] = (byte)(255 - canvas[di + 2]);
                    continue;
                }
                int a = img[si + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    canvas[di] = img[si]; canvas[di + 1] = img[si + 1]; canvas[di + 2] = img[si + 2];
                    continue;
                }
                int ia = 255 - a;
                canvas[di] = (byte)((img[si] * a + canvas[di] * ia + 127) / 255);
                canvas[di + 1] = (byte)((img[si + 1] * a + canvas[di + 1] * ia + 127) / 255);
                canvas[di + 2] = (byte)((img[si + 2] * a + canvas[di + 2] * ia + 127) / 255);
            }
        }
    }

    /// <summary>Fill a convex/concave polygon (pixel coordinates) with 4×4 supersampled anti-aliasing. Meant for small shapes.</summary>
    public static void FillPolygon(Span<byte> canvas, int cw, int ch, ReadOnlySpan<(double X, double Y)> pts, uint rgb, double opacity)
    {
        if (pts.Length < 3 || opacity <= 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        int x0 = Math.Max(0, (int)Math.Floor(minX)), x1 = Math.Min(cw - 1, (int)Math.Ceiling(maxX));
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(ch - 1, (int)Math.Ceiling(maxY));
        byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int inside = 0;
            for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
                if (Inside(pts, x + (sx + 0.5) / 4, y + (sy + 0.5) / 4)) inside++;
            if (inside == 0) continue;
            double a = opacity * inside / 16.0;
            int i = (y * cw + x) * 4;
            canvas[i] = (byte)(canvas[i] + (b - canvas[i]) * a);
            canvas[i + 1] = (byte)(canvas[i + 1] + (g - canvas[i + 1]) * a);
            canvas[i + 2] = (byte)(canvas[i + 2] + (r - canvas[i + 2]) * a);
        }
    }

    private static bool Inside(ReadOnlySpan<(double X, double Y)> pts, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
        {
            if ((pts[i].Y > y) != (pts[j].Y > y) &&
                x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                c = !c;
        }
        return c;
    }

    /// <summary>
    /// Coverage (0..1) of a rounded rectangle [0,w)×[0,h) with corner radius <paramref name="radius"/> at pixel (x, y)
    /// (1 px soft edge). radius = min(w,h)/2 gives a circle/stadium.
    /// </summary>
    public static double RoundRectCoverage(int x, int y, int w, int h, double radius)
    {
        double px = x + 0.5, py = y + 0.5;
        double cx = Math.Clamp(px, radius, w - radius), cy = Math.Clamp(py, radius, h - radius);
        double dx = px - cx, dy = py - cy;
        double d = Math.Sqrt(dx * dx + dy * dy); // distance into the corner arc (0 when on the straight parts)
        double edge = radius - d;                // > 0 inside the arc
        double straight = Math.Min(Math.Min(px, w - px), Math.Min(py, h - py));
        double inside = d > 0 ? edge : straight;
        return Math.Clamp(inside + 0.5, 0, 1);
    }

    /// <summary>
    /// Blend a BGRA image (iw×ih) onto the canvas at (x, y) through a rounded-rect mask (radius as above) and draw a
    /// <paramref name="border"/>-px white outline (webcam picture-in-picture).
    /// </summary>
    public static void BlendRounded(Span<byte> canvas, int cw, int ch, ReadOnlySpan<byte> img, int iw, int ih, int x, int y,
        double radius, double border)
    {
        for (int j = 0; j < ih; j++)
        {
            int cy = y + j;
            if ((uint)cy >= (uint)ch) continue;
            for (int i = 0; i < iw; i++)
            {
                int cx = x + i;
                if ((uint)cx >= (uint)cw) continue;
                double cov = RoundRectCoverage(i, j, iw, ih, radius);
                if (cov <= 0) continue;
                // distance from the outer edge, for the border ring
                double inner = border > 0 ? RoundRectCoverageInset(i, j, iw, ih, radius, border) : 1;
                int si = (j * iw + i) * 4, di = (cy * cw + cx) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double v = img[si + c] * inner + 255 * (1 - inner);
                    canvas[di + c] = (byte)(canvas[di + c] + (v - canvas[di + c]) * cov);
                }
            }
        }
    }

    private static double RoundRectCoverageInset(int x, int y, int w, int h, double radius, double inset)
    {
        double px = x + 0.5 - inset, py = y + 0.5 - inset;
        double iw = w - 2 * inset, ih = h - 2 * inset, r = Math.Max(0, radius - inset);
        if (iw <= 0 || ih <= 0) return 0;
        double cx = Math.Clamp(px, r, iw - r), cy = Math.Clamp(py, r, ih - r);
        double dx = px - cx, dy = py - cy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double straight = Math.Min(Math.Min(px, iw - px), Math.Min(py, ih - py));
        double inside = d > 0 ? r - d : straight;
        return Math.Clamp(inside + 0.5, 0, 1);
    }

    /// <summary>
    /// Draw an anti-aliased ring (with an optional translucent fill) centred at (cx, cy). Colors are 0xRRGGBB; opacities 0..1.
    /// </summary>
    public static void DrawRing(Span<byte> canvas, int cw, int ch, double cx, double cy, double radius, double thickness,
        uint rgb, double ringOpacity, double fillOpacity)
    {
        if (radius <= 0 || (ringOpacity <= 0 && fillOpacity <= 0)) return;
        byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
        double outer = radius + thickness / 2, inner = Math.Max(0, radius - thickness / 2);
        int x0 = Math.Max(0, (int)Math.Floor(cx - outer - 1)), x1 = Math.Min(cw - 1, (int)Math.Ceiling(cx + outer + 1));
        int y0 = Math.Max(0, (int)Math.Floor(cy - outer - 1)), y1 = Math.Min(ch - 1, (int)Math.Ceiling(cy + outer + 1));
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            // coverage of the ring band and of the inner disc (1px soft edges)
            double ring = Math.Clamp(Math.Min(outer - d + 0.5, d - inner + 0.5), 0, 1);
            double disc = Math.Clamp(inner - d + 0.5, 0, 1);
            double a = ring * ringOpacity + disc * fillOpacity;
            if (a <= 0) continue;
            if (a > 1) a = 1;
            int i = (y * cw + x) * 4;
            canvas[i] = (byte)(canvas[i] + (b - canvas[i]) * a);
            canvas[i + 1] = (byte)(canvas[i + 1] + (g - canvas[i + 1]) * a);
            canvas[i + 2] = (byte)(canvas[i + 2] + (r - canvas[i + 2]) * a);
        }
    }
}
