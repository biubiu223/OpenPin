using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenPin.Core.Imaging;

/// <summary>Small, dependency-free bitmap helpers. All results are frozen (safe to share across threads).</summary>
public static class BitmapUtil
{
    public static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    /// <summary>Crop (rectangle is clamped to the image).</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var r = Clamp(rect, source.PixelWidth, source.PixelHeight);
        if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("Crop rectangle outside image", nameof(rect));
        // Materialize so the crop does not keep the (possibly huge) parent alive.
        return ToBgra32(new CroppedBitmap(source, r));
    }

    public static Int32Rect Clamp(Int32Rect r, int width, int height)
    {
        int x = Math.Clamp(r.X, 0, width), y = Math.Clamp(r.Y, 0, height);
        int right = Math.Clamp(r.X + r.Width, 0, width), bottom = Math.Clamp(r.Y + r.Height, 0, height);
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    /// <summary>Convert to a materialized Bgra32 bitmap at 96 DPI (1 px == 1 DIP).</summary>
    public static BitmapSource ToBgra32(BitmapSource source)
    {
        BitmapSource src = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = src.PixelWidth * 4;
        var buffer = new byte[stride * src.PixelHeight];
        src.CopyPixels(buffer, stride, 0);
        if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgr24)
            for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255; // Bgr32 "alpha" byte is undefined
        return Frozen(BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Bgra32, null, buffer, stride));
    }

    /// <summary>Raw BGRA pixels (top-down, stride = width*4).</summary>
    public static byte[] GetPixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : ToBgra32(source);
        var buffer = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(buffer, bgra.PixelWidth * 4, 0);
        return buffer;
    }

    public static BitmapSource FromPixels(byte[] bgra, int width, int height) =>
        Frozen(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4));

    /// <summary>Same pixels, DPI forced to 96 (so WPF shows 1 px == 1 DIP). Images loaded from files often carry other DPIs.</summary>
    public static BitmapSource Normalize96Dpi(BitmapSource source)
    {
        if (Math.Abs(source.DpiX - 96) < 0.01 && Math.Abs(source.DpiY - 96) < 0.01)
            return source.IsFrozen ? source : Frozen(source.Clone());
        return ToBgra32(source);
    }

    /// <summary>Render any visual to a bitmap of exactly width×height pixels (visual units treated as pixels).</summary>
    public static BitmapSource Render(Visual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return Frozen(rtb);
    }

    /// <summary>Render a drawing callback to a bitmap of width×height pixels.</summary>
    public static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        return Render(dv, width, height);
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource DecodeImage(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return Normalize96Dpi(decoder.Frames[0]);
    }

    public static BitmapSource LoadFile(string path)
    {
        using var fs = File.OpenRead(path);
        return DecodeImage(fs);
    }

    /// <summary>Color of one pixel (returns opaque for Bgr32 sources).</summary>
    public static Color GetPixel(BitmapSource source, int x, int y)
    {
        if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight) return Colors.Transparent;
        var px = new byte[4];
        var src = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        src.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        byte a = source.Format == PixelFormats.Bgr32 ? (byte)255 : px[3];
        return Color.FromArgb(a, px[2], px[1], px[0]);
    }
}
