using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>Image file formats supported by <see cref="SaveService"/>. (WebP: planned, WIC has no WebP encoder.)</summary>
public enum ImageFileFormat { Png, Jpg, Bmp, Tiff, Pdf }

/// <summary>Encoding helpers shared by save and clipboard. Thread-safe for frozen bitmaps.</summary>
internal static class ImageEncoding
{
    public static ImageFileFormat FormatFromExtension(string? ext) => (ext ?? "").ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFileFormat.Jpg,
        ".bmp" or ".dib" => ImageFileFormat.Bmp,
        ".tif" or ".tiff" => ImageFileFormat.Tiff,
        ".pdf" => ImageFileFormat.Pdf,
        _ => ImageFileFormat.Png,
    };

    public static string ExtensionOf(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Jpg => ".jpg",
        ImageFileFormat.Bmp => ".bmp",
        ImageFileFormat.Tiff => ".tiff",
        ImageFileFormat.Pdf => ".pdf",
        _ => ".png",
    };

    /// <summary>Encode; PDFs use the page layout stored in the settings (default: one page the size of the image).</summary>
    public static void Encode(BitmapSource image, ImageFileFormat format, Stream output, int quality, bool pdfJpeg = false) =>
        Encode(image, format, output, quality, ServicesConfig.Save.ToPdfOptions() with { UseJpeg = pdfJpeg, JpegQuality = quality });

    /// <summary>Encode with explicit PDF options (<paramref name="pdf"/> is ignored for raster formats).</summary>
    public static void Encode(BitmapSource image, ImageFileFormat format, Stream output, int quality, PdfOptions pdf)
    {
        if (format == ImageFileFormat.Pdf)
        {
            PdfImageWriter.Write(output, image, pdf);
            return;
        }
        BitmapEncoder enc;
        BitmapSource frame = image;
        switch (format)
        {
            case ImageFileFormat.Jpg:
                enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
                frame = FlattenOnWhite(image);
                break;
            case ImageFileFormat.Bmp:
                enc = new BmpBitmapEncoder();
                frame = FlattenOnWhite(image);
                break;
            case ImageFileFormat.Tiff:
                enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
                break;
            default:
                enc = new PngBitmapEncoder();
                break;
        }
        enc.Frames.Add(BitmapFrame.Create(frame));
        enc.Save(output);
    }

    public static byte[] Encode(BitmapSource image, ImageFileFormat format, int quality)
    {
        using var ms = new MemoryStream();
        Encode(image, format, ms, quality);
        return ms.ToArray();
    }

    /// <summary>True if any pixel is not fully opaque.</summary>
    public static bool HasTransparency(BitmapSource image)
    {
        var f = image.Format;
        if (f == PixelFormats.Bgr24 || f == PixelFormats.Bgr32 || f == PixelFormats.Rgb24 || f == PixelFormats.Gray8 ||
            f == PixelFormats.BlackWhite || f == PixelFormats.Bgr565 || f == PixelFormats.Bgr555)
            return false;
        var px = BitmapUtil.GetPixels(image);
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] != 255) return true;
        return false;
    }

    /// <summary>Composite onto white → opaque Bgra32 (for formats without alpha: JPG, BMP, PDF, CF_DIB).</summary>
    public static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        if (!HasTransparency(image)) return image;
        var px = BitmapUtil.GetPixels(image);
        FlattenOnWhite(px);
        return BitmapUtil.FromPixels(px, image.PixelWidth, image.PixelHeight);
    }

    /// <summary>In-place: straight-alpha BGRA → opaque BGRA composited on white.</summary>
    public static void FlattenOnWhite(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            int inv = 255 - a;
            bgra[i] = (byte)((bgra[i] * a + 255 * inv + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 255 * inv + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 255 * inv + 127) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>Frozen copy safe to hand to a background thread.</summary>
    public static BitmapSource EnsureFrozen(BitmapSource image)
    {
        if (image.IsFrozen) return image;
        if (image.CanFreeze)
        {
            var clone = image.Clone();
            clone.Freeze();
            return clone;
        }
        return BitmapUtil.ToBgra32(image);
    }
}
