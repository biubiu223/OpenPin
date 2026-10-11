using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>
/// High-quality resize for "save at 75 % / 50 % / custom size" (PRO-08): area averaging when shrinking, bilinear when
/// enlarging, in premultiplied alpha (no dark fringes). Separable, thread-safe, returns a frozen Bgra32 bitmap.
/// </summary>
internal static class ImageResize
{
    public static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == source.PixelWidth && height == source.PixelHeight) return source;
        int sw = source.PixelWidth, sh = source.PixelHeight;
        var px = BitmapUtil.GetPixels(source);

        // Premultiplied float planes.
        var src = new float[sw * sh * 4];
        for (int i = 0; i < sw * sh; i++)
        {
            float a = px[i * 4 + 3] / 255f;
            src[i * 4] = px[i * 4] * a; src[i * 4 + 1] = px[i * 4 + 1] * a; src[i * 4 + 2] = px[i * 4 + 2] * a; src[i * 4 + 3] = px[i * 4 + 3];
        }
        var wx = Weights(sw, width);
        var wy = Weights(sh, height);

        // Horizontal pass: sh rows × width.
        var tmp = new float[width * sh * 4];
        Parallel.For(0, sh, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var (start, w) = wx[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = (y * sw + start + k) * 4;
                    float f = w[k];
                    b += src[s] * f; g += src[s + 1] * f; r += src[s + 2] * f; a += src[s + 3] * f;
                }
                int o = (y * width + x) * 4;
                tmp[o] = b; tmp[o + 1] = g; tmp[o + 2] = r; tmp[o + 3] = a;
            }
        });

        // Vertical pass + un-premultiply.
        var dst = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            var (start, w) = wy[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = ((start + k) * width + x) * 4;
                    float f = w[k];
                    b += tmp[s] * f; g += tmp[s + 1] * f; r += tmp[s + 2] * f; a += tmp[s + 3] * f;
                }
                int o = (y * width + x) * 4;
                float inv = a > 0.5f ? 255f / a : 0;
                dst[o] = ToByte(b * inv); dst[o + 1] = ToByte(g * inv); dst[o + 2] = ToByte(r * inv); dst[o + 3] = ToByte(a);
            }
        });
        return BitmapUtil.FromPixels(dst, width, height);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    /// <summary>Per output pixel: first source index and normalised weights.</summary>
    private static (int Start, float[] W)[] Weights(int srcLen, int dstLen)
    {
        var res = new (int, float[])[dstLen];
        double scale = (double)srcLen / dstLen;
        for (int i = 0; i < dstLen; i++)
        {
            if (scale >= 1)
            {
                // Box filter over the exact footprint [i*scale, (i+1)*scale).
                double a = i * scale, b = Math.Min(srcLen, (i + 1) * scale);
                int s0 = (int)Math.Floor(a), s1 = Math.Min(srcLen - 1, (int)Math.Ceiling(b) - 1);
                var w = new float[s1 - s0 + 1];
                double sum = 0;
                for (int k = s0; k <= s1; k++)
                {
                    double cover = Math.Min(b, k + 1) - Math.Max(a, k);
                    w[k - s0] = (float)Math.Max(0, cover);
                    sum += w[k - s0];
                }
                for (int k = 0; k < w.Length; k++) w[k] = (float)(w[k] / sum);
                res[i] = (s0, w);
            }
            else
            {
                double c = (i + 0.5) * scale - 0.5;
                int s0 = (int)Math.Floor(c);
                double t = c - s0;
                int a0 = Math.Clamp(s0, 0, srcLen - 1), a1 = Math.Clamp(s0 + 1, 0, srcLen - 1);
                if (a0 == a1) res[i] = (a0, new[] { 1f });
                else res[i] = (a0, new[] { (float)(1 - t), (float)t });
            }
        }
        return res;
    }
}
