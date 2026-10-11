using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Annotation.Rendering;

/// <summary>CPU pixel effects on BGRA buffers (mosaic, blur, multiply). Pure functions, unit tested.</summary>
internal static class PixelEffects
{
    /// <summary>
    /// Pixelate: every <paramref name="block"/>×<paramref name="block"/> cell (aligned to the IMAGE grid, so results do
    /// not depend on the region) becomes its average color. <paramref name="src"/> is region pixels (stride = w*4) whose
    /// top-left is at image coordinate (<paramref name="ox"/>, <paramref name="oy"/>).
    /// </summary>
    public static byte[] Pixelate(byte[] src, int w, int h, int ox, int oy, int block)
    {
        block = Math.Max(1, block);
        var dst = new byte[src.Length];
        int startY = -Mod(oy, block);
        int startX = -Mod(ox, block);
        for (int by = startY; by < h; by += block)
        {
            int y0 = Math.Max(0, by), y1 = Math.Min(h, by + block);
            for (int bx = startX; bx < w; bx += block)
            {
                int x0 = Math.Max(0, bx), x1 = Math.Min(w, bx + block);
                long b = 0, g = 0, r = 0, a = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * w + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        int pa = src[i + 3];
                        b += src[i] * pa; g += src[i + 1] * pa; r += src[i + 2] * pa; a += pa;
                        n++;
                    }
                }
                if (n == 0) continue;
                byte ob = 0, og = 0, or = 0, oa = (byte)((a + n / 2) / n);
                if (a > 0)
                {
                    ob = (byte)((b + a / 2) / a); og = (byte)((g + a / 2) / a); or = (byte)((r + a / 2) / a);
                }
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * w + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        dst[i] = ob; dst[i + 1] = og; dst[i + 2] = or; dst[i + 3] = oa;
                    }
                }
            }
        }
        return dst;
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;

    /// <summary>Approximate Gaussian blur: 3 box-blur passes per axis (in place on a copy). Edge pixels are clamped.</summary>
    public static byte[] BoxBlur3(byte[] src, int w, int h, int radius)
    {
        var a = (byte[])src.Clone();
        if (radius < 1 || w == 0 || h == 0) return a;
        var tmp = new byte[a.Length];
        // Box sizes for 3 passes approximating a gaussian with sigma ~ radius/2.
        for (int pass = 0; pass < 3; pass++)
        {
            BlurH(a, tmp, w, h, radius);
            BlurV(tmp, a, w, h, radius);
        }
        return a;
    }

    private static void BlurH(byte[] s, byte[] d, int w, int h, int r)
    {
        int div = 2 * r + 1;
        Parallel.For(0, h, y =>
        {
            int row = y * w * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += s[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    d[row + x * 4 + c] = (byte)((sum + div / 2) / div);
                    int xo = Math.Max(x - r, 0), xi = Math.Min(x + r + 1, w - 1);
                    sum += s[row + xi * 4 + c] - s[row + xo * 4 + c];
                }
            }
        });
    }

    private static void BlurV(byte[] s, byte[] d, int w, int h, int r)
    {
        int div = 2 * r + 1, stride = w * 4;
        Parallel.For(0, w, x =>
        {
            int col = x * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += s[Math.Clamp(k, 0, h - 1) * stride + col + c];
                for (int y = 0; y < h; y++)
                {
                    d[y * stride + col + c] = (byte)((sum + div / 2) / div);
                    int yo = Math.Max(y - r, 0), yi = Math.Min(y + r + 1, h - 1);
                    sum += s[yi * stride + col + c] - s[yo * stride + col + c];
                }
            }
        });
    }

    /// <summary>Downscale by an integer factor (box filter). Output size = ceil(w/f) × ceil(h/f).</summary>
    public static byte[] Downscale(byte[] src, int w, int h, int f, out int ow, out int oh)
    {
        ow = (w + f - 1) / f; oh = (h + f - 1) / f;
        var dst = new byte[ow * oh * 4];
        int ow2 = ow;
        Parallel.For(0, oh, oy =>
        {
            for (int ox = 0; ox < ow2; ox++)
            {
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (int y = oy * f; y < Math.Min(h, oy * f + f); y++)
                for (int x = ox * f; x < Math.Min(w, ox * f + f); x++)
                {
                    int i = (y * w + x) * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3]; n++;
                }
                int o = (oy * ow2 + ox) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        });
        return dst;
    }

    /// <summary>
    /// Multiply blend of a (possibly translucent) color onto the pixels: result = base × lerp(1, color, alpha).
    /// Base alpha is preserved. Used by the highlighter's "multiply" mode, which WPF cannot composite natively.
    /// </summary>
    public static byte[] Multiply(byte[] src, Color color)
    {
        var dst = new byte[src.Length];
        double a = color.A / 255.0;
        // Per-channel factor (0..256 fixed point).
        int fb = (int)Math.Round((1 - a + a * color.B / 255.0) * 256);
        int fg = (int)Math.Round((1 - a + a * color.G / 255.0) * 256);
        int fr = (int)Math.Round((1 - a + a * color.R / 255.0) * 256);
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = (byte)((src[i] * fb + 128) >> 8);
            dst[i + 1] = (byte)((src[i + 1] * fg + 128) >> 8);
            dst[i + 2] = (byte)((src[i + 2] * fr + 128) >> 8);
            dst[i + 3] = src[i + 3];
        }
        return dst;
    }

    /// <summary>Read a region of a bitmap as BGRA bytes.</summary>
    public static byte[] ReadRegion(BitmapSource bgraSource, Int32Rect r)
    {
        var buf = new byte[r.Width * r.Height * 4];
        if (r.Width > 0 && r.Height > 0) bgraSource.CopyPixels(r, buf, r.Width * 4, 0);
        if (bgraSource.Format == PixelFormats.Bgr32)
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
        return buf;
    }

    public static BitmapSource ToBitmap(byte[] bgra, int w, int h) => BitmapUtil.FromPixels(bgra, w, h);
}
