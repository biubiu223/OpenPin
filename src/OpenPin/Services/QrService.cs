using System.Windows;
using System.Windows.Media.Imaging;

namespace OpenPin.Services;

/// <summary>A decoded QR / barcode; <see cref="Bounds"/> in image pixels.</summary>
public sealed record QrCodeResult(string Text, string Format, Rect Bounds)
{
    public bool IsUrl => Uri.TryCreate(Text, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// QR code detection (ZXing.Net). PHASE-0 STUB — implemented by the Services module.
/// </summary>
public static class QrService
{
    /// <summary>Decode all QR codes in the image. Thread-safe, CPU bound — call via Task.Run for big images.</summary>
    public static IReadOnlyList<QrCodeResult> Decode(BitmapSource image) => Array.Empty<QrCodeResult>();
}
