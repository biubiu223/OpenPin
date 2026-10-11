using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using ZXing;
using ZXing.Common;

namespace OpenPin.Services;

/// <summary>A decoded QR / barcode; <see cref="Bounds"/> in image pixels.</summary>
public sealed record QrCodeResult(string Text, string Format, Rect Bounds)
{
    public bool IsUrl => Uri.TryCreate(Text, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// QR code / barcode detection (ZXing.Net): QR, Data Matrix, Aztec, PDF417 and common 1D formats; several codes per image.
/// Thread-safe (a new reader per call).
/// </summary>
public static class QrService
{
    private static readonly IList<BarcodeFormat> Formats = new[]
    {
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.ITF, BarcodeFormat.CODABAR,
    };

    /// <summary>Decode all QR codes in the image. Thread-safe, CPU bound — call via Task.Run for big images (or use <see cref="DecodeAsync"/>).</summary>
    public static IReadOnlyList<QrCodeResult> Decode(BitmapSource image)
    {
        try
        {
            var bgra = BitmapUtil.GetPixels(image);
            return Decode(bgra, image.PixelWidth, image.PixelHeight);
        }
        catch (Exception ex)
        {
            Log.Error("QR decode failed", ex);
            return Array.Empty<QrCodeResult>();
        }
    }

    /// <summary>Decode on a background thread.</summary>
    public static Task<IReadOnlyList<QrCodeResult>> DecodeAsync(BitmapSource image, CancellationToken ct = default)
    {
        var frozen = ImageEncoding.EnsureFrozen(image);
        return Task.Run(() => Decode(frozen), ct);
    }

    /// <summary>Decode raw top-down BGRA pixels (stride = width*4).</summary>
    public static IReadOnlyList<QrCodeResult> Decode(byte[] bgra, int width, int height)
    {
        if (width < 8 || height < 8 || bgra.Length < width * height * 4) return Array.Empty<QrCodeResult>();
        var px = (byte[])bgra.Clone();
        ImageEncoding.FlattenOnWhite(px); // transparent backgrounds would otherwise read as black

        var found = DecodeOnce(px, width, height, 1);
        // Small codes (e.g. a QR captured from a zoomed-out page) decode much better after upscaling.
        if (found.Count == 0 && Math.Max(width, height) <= 800)
        {
            var bmp = new TransformedBitmap(BitmapUtil.FromPixels(px, width, height), new ScaleTransform(2, 2));
            bmp.Freeze();
            found = DecodeOnce(BitmapUtil.GetPixels(bmp), bmp.PixelWidth, bmp.PixelHeight, 2);
        }
        return found;
    }

    private static List<QrCodeResult> DecodeOnce(byte[] bgra, int width, int height, double scale)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = Formats,
            },
        };
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source) ?? Array.Empty<Result>();
        var list = new List<QrCodeResult>();
        foreach (var r in results)
        {
            if (r == null || string.IsNullOrEmpty(r.Text)) continue;
            var bounds = BoundsOf(r, width, height);
            bounds = new Rect(bounds.X / scale, bounds.Y / scale, bounds.Width / scale, bounds.Height / scale);
            if (list.Any(x => x.Text == r.Text && x.Bounds.IntersectsWith(bounds))) continue;
            list.Add(new QrCodeResult(r.Text, r.BarcodeFormat.ToString(), bounds));
        }
        // Reading order.
        return list.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left).ToList();
    }

    /// <summary>Bounding box from result points (finder pattern centres for QR → inflated to approximate the full symbol).</summary>
    private static Rect BoundsOf(Result r, int width, int height)
    {
        var pts = r.ResultPoints?.Where(p => p != null).ToList();
        if (pts == null || pts.Count == 0) return new Rect(0, 0, width, height);
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        double w = x1 - x0, h = y1 - y0;
        if (r.BarcodeFormat is BarcodeFormat.QR_CODE)
        {
            // Finder centres sit 3.5 modules inside the symbol; a typical (v1–v4) symbol is 21–33 modules → ~15% margin.
            double mx = Math.Max(w, h) * 0.17;
            x0 -= mx; y0 -= mx; x1 += mx; y1 += mx;
        }
        else if (h < w * 0.1)
        {
            // 1D codes report points on a scan line — give the box a plausible height.
            double mh = Math.Max(8, w * 0.15);
            y0 -= mh; y1 += mh;
        }
        x0 = Math.Clamp(x0, 0, width); x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height); y1 = Math.Clamp(y1, 0, height);
        return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }
}
