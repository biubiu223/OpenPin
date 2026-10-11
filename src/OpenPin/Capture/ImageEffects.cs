using System.Windows.Media;

namespace OpenPin.Capture;

/// <summary>CAP-11 output decoration options. Sizes in pixels.</summary>
internal readonly record struct OutputEffects(int CornerRadius, int BorderWidth, Color BorderColor, int ShadowSize)
{
    public bool IsNone => CornerRadius <= 0 && BorderWidth <= 0 && ShadowSize <= 0;
}

/// <summary>
/// Pure pixel operations on straight-alpha BGRA buffers (top-down, stride = width*4). No WPF objects, thread-safe.
/// </summary>
internal static class ImageEffects
{
    private const int Supersample = 4;

    /// <summary>
    /// Apply rounded corners, an inner border and a drop shadow. The result is larger than the input when a shadow
    /// is drawn: the original image starts at (<c>Offset</c>, <c>Offset</c>) in the result.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, int Offset) Apply(byte[] bgra, int w, int h, OutputEffects fx)
    {
        if (fx.IsNone) return (bgra, w, h, 0);
        int radius = Math.Clamp(fx.CornerRadius, 0, Math.Min(w, h) / 2);
        int border = Math.Clamp(fx.BorderWidth, 0, Math.Min(w, h) / 2);

        var img = (byte[])bgra.Clone();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double outer = Coverage(x, y, 0, 0, w, h, radius);
                int i = (y * w + x) * 4;
                if (border > 0)
                {
                    double inner = Coverage(x, y, border, border, w - border, h - border, Math.Max(0, radius - border));
                    if (inner < 1)
                    {
                        double k = 1 - inner, a = fx.BorderColor.A / 255.0 * k;
                        img[i] = Lerp(img[i], fx.BorderColor.B, a);
                        img[i + 1] = Lerp(img[i + 1], fx.BorderColor.G, a);
                        img[i + 2] = Lerp(img[i + 2], fx.BorderColor.R, a);
                    }
                }
                img[i + 3] = (byte)Math.Round(img[i + 3] * outer);
            }
        }

        if (fx.ShadowSize <= 0) return (img, w, h, 0);

        int s = fx.ShadowSize, W = w + 2 * s, H = h + 2 * s;
        int dy = Math.Max(1, s / 4);
        var mask = new float[W * H];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int ty = y + s + dy;
                if (ty < H) mask[ty * W + x + s] = img[(y * w + x) * 4 + 3] / 255f;
            }
        int blur = Math.Max(1, s / 3);
        for (int pass = 0; pass < 3; pass++) BoxBlur(mask, W, H, blur);

        const float shadowOpacity = 0.35f;
        var result = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                float sa = mask[y * W + x] * shadowOpacity; // black shadow
                int ix = x - s, iy = y - s;
                if (ix >= 0 && iy >= 0 && ix < w && iy < h)
                {
                    int i = (iy * w + ix) * 4;
                    float a = img[i + 3] / 255f;
                    float ao = a + sa * (1 - a);
                    if (ao > 0)
                    {
                        // Shadow color is black, so it contributes nothing to the premultiplied color sum.
                        result[o] = (byte)Math.Round(img[i] * a / ao);
                        result[o + 1] = (byte)Math.Round(img[i + 1] * a / ao);
                        result[o + 2] = (byte)Math.Round(img[i + 2] * a / ao);
                    }
                    result[o + 3] = (byte)Math.Round(ao * 255);
                }
                else
                {
                    result[o + 3] = (byte)Math.Round(sa * 255);
                }
            }
        }
        return (result, W, H, s);
    }

    /// <summary>
    /// Fraction (0..1) of pixel (x,y) covered by the rounded rectangle [l,r)×[t,b) with corner radius
    /// <paramref name="radius"/>. Uses 4×4 supersampling near the corners.
    /// </summary>
    internal static double Coverage(int x, int y, int l, int t, int r, int b, int radius)
    {
        if (x < l || y < t || x >= r || y >= b) return 0;
        if (radius <= 0) return 1;
        bool inCornerX = x < l + radius || x >= r - radius;
        bool inCornerY = y < t + radius || y >= b - radius;
        if (!inCornerX || !inCornerY) return 1;

        double cx = x < l + radius ? l + radius : r - radius;
        double cy = y < t + radius ? t + radius : b - radius;
        int inside = 0;
        for (int sy = 0; sy < Supersample; sy++)
            for (int sx = 0; sx < Supersample; sx++)
            {
                double px = x + (sx + 0.5) / Supersample - cx, py = y + (sy + 0.5) / Supersample - cy;
                if (px * px + py * py <= (double)radius * radius) inside++;
            }
        return inside / (double)(Supersample * Supersample);
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    /// <summary>Separable box blur (one pass, radius r) in place.</summary>
    private static void BoxBlur(float[] data, int w, int h, int r)
    {
        var tmp = new float[Math.Max(w, h)];
        float norm = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int k = -r; k <= r; k++) sum += data[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[x] = sum * norm;
                sum += data[row + Math.Min(x + r + 1, w - 1)] - data[row + Math.Max(x - r, 0)];
            }
            Array.Copy(tmp, 0, data, row, w);
        }
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int k = -r; k <= r; k++) sum += data[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                tmp[y] = sum * norm;
                sum += data[Math.Min(y + r + 1, h - 1) * w + x] - data[Math.Max(y - r, 0) * w + x];
            }
            for (int y = 0; y < h; y++) data[y * w + x] = tmp[y];
        }
    }

    /// <summary>
    /// Alpha-blend <paramref name="patch"/> (straight BGRA) onto an opaque BGR(A) <paramref name="dest"/> at (x, y), clipped.
    /// Used to show a history screenshot on top of the frozen screen.
    /// </summary>
    public static void BlendOnto(byte[] dest, int dw, int dh, byte[] patch, int pw, int ph, int x, int y)
    {
        for (int py = 0; py < ph; py++)
        {
            int ty = y + py;
            if (ty < 0 || ty >= dh) continue;
            for (int px = 0; px < pw; px++)
            {
                int tx = x + px;
                if (tx < 0 || tx >= dw) continue;
                int s = (py * pw + px) * 4, d = (ty * dw + tx) * 4;
                int a = patch[s + 3];
                if (a == 255)
                {
                    dest[d] = patch[s]; dest[d + 1] = patch[s + 1]; dest[d + 2] = patch[s + 2];
                }
                else if (a > 0)
                {
                    dest[d] = (byte)((patch[s] * a + dest[d] * (255 - a)) / 255);
                    dest[d + 1] = (byte)((patch[s + 1] * a + dest[d + 1] * (255 - a)) / 255);
                    dest[d + 2] = (byte)((patch[s + 2] * a + dest[d + 2] * (255 - a)) / 255);
                }
                dest[d + 3] = 255;
            }
        }
    }
}
