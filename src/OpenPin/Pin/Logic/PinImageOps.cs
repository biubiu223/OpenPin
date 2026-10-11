using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;
using OpenPin.Pin.Model;

namespace OpenPin.Pin.Logic;

/// <summary>Image processing for image pins (PIN-10). All results are frozen Bgra32 @ 96 DPI.</summary>
public static class PinImageOps
{
    /// <summary>Applies rotation / mirror / pixel filters to <paramref name="source"/>.</summary>
    public static BitmapSource Apply(BitmapSource source, ImageAdjustments a)
    {
        if (a.IsIdentity) return BitmapUtil.Normalize96Dpi(source);
        BitmapSource img = source;
        if (a.Rotation != 0) img = new TransformedBitmap(img, new RotateTransform(a.Rotation));
        if (a.Mirror) img = new TransformedBitmap(img, new ScaleTransform(-1, 1));
        var bgra = BitmapUtil.ToBgra32(img);
        if (!a.HasPixelFilters) return bgra;
        var px = BitmapUtil.GetPixels(bgra);
        ApplyFilters(px, a.Grayscale, a.Invert, a.Brightness);
        return BitmapUtil.FromPixels(px, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>In-place per-pixel filters on straight (non-premultiplied) BGRA bytes. Alpha is untouched.</summary>
    public static void ApplyFilters(byte[] bgra, bool grayscale, bool invert, int brightnessPercent)
    {
        int add = (int)Math.Round(255 * Math.Clamp(brightnessPercent, -100, 100) / 100.0);
        Span<byte> lut = stackalloc byte[256];
        for (int v = 0; v < 256; v++)
        {
            int x = invert ? 255 - v : v;
            lut[v] = (byte)Math.Clamp(x + add, 0, 255);
        }
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (grayscale)
            {
                // ITU-R BT.601 luma
                byte y = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114 + 500) / 1000);
                bgra[i] = bgra[i + 1] = bgra[i + 2] = y;
            }
            bgra[i] = lut[bgra[i]];
            bgra[i + 1] = lut[bgra[i + 1]];
            bgra[i + 2] = lut[bgra[i + 2]];
        }
    }

    /// <summary>Scales an image by <paramref name="scale"/> (rounded pixel size). scale 1 returns the source.</summary>
    public static BitmapSource Scale(BitmapSource source, double scale, bool smooth)
    {
        if (Math.Abs(scale - 1) < 1e-6) return BitmapUtil.Normalize96Dpi(source);
        int w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, smooth ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        using (var dc = dv.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, w, h));
        return BitmapUtil.ToBgra32(BitmapUtil.Render(dv, w, h));
    }

    /// <summary>Short stable hash of the pixels (duplicate detection).</summary>
    public static string Hash(BitmapSource image)
    {
        var px = BitmapUtil.GetPixels(image);
        var head = Encoding.ASCII.GetBytes($"{image.PixelWidth}x{image.PixelHeight}:");
        using var sha = SHA1.Create();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(px, 0, px.Length);
        return "img:" + Convert.ToHexString(sha.Hash!)[..24];
    }

    public static string Hash(string kind, string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return kind + ":" + Convert.ToHexString(bytes)[..24];
    }
}
