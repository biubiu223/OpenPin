using System.Windows;
using System.Windows.Media.Imaging;

namespace OpenPin.Core.Screen;

/// <summary>
/// A frozen picture of the whole virtual screen taken at the moment a capture starts.
/// <para>
/// Coordinate systems:
/// <list type="bullet">
/// <item><b>Screen px</b>: physical pixels in virtual-screen coordinates (may be negative on multi-monitor setups).</item>
/// <item><b>Image px</b>: pixel coordinates inside <see cref="Image"/>; (0,0) == <see cref="VirtualBoundsPx"/>.X/Y.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ScreenSnapshot : IDisposable
{
    /// <summary>Whole virtual screen, Bgr32, 96 DPI, frozen, WITHOUT the cursor.</summary>
    public required BitmapSource Image { get; init; }

    /// <summary>Virtual screen bounds in screen px. Image pixel (0,0) is at (X,Y).</summary>
    public required Int32Rect VirtualBoundsPx { get; init; }

    public required IReadOnlyList<MonitorInfo> Monitors { get; init; }

    /// <summary>Visible top-level windows, z-order top → bottom, bounds in screen px.</summary>
    public required IReadOnlyList<WindowInfo> Windows { get; init; }

    public CursorSnapshot? Cursor { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.Now;

    private BitmapSource? _withCursor;

    /// <summary>Same as <see cref="Image"/> but with the mouse cursor drawn in (lazily created).</summary>
    public BitmapSource GetImageWithCursor()
    {
        if (Cursor == null) return Image;
        return _withCursor ??= ScreenCapture.DrawCursorOnCopy(Image, Cursor, VirtualBoundsPx);
    }

    public Int32Rect ScreenToImage(Int32Rect screenPx) =>
        new(screenPx.X - VirtualBoundsPx.X, screenPx.Y - VirtualBoundsPx.Y, screenPx.Width, screenPx.Height);

    public Int32Rect ImageToScreen(Int32Rect imagePx) =>
        new(imagePx.X + VirtualBoundsPx.X, imagePx.Y + VirtualBoundsPx.Y, imagePx.Width, imagePx.Height);

    public Point ScreenToImage(Point screenPx) => new(screenPx.X - VirtualBoundsPx.X, screenPx.Y - VirtualBoundsPx.Y);

    public Point ImageToScreen(Point imagePx) => new(imagePx.X + VirtualBoundsPx.X, imagePx.Y + VirtualBoundsPx.Y);

    public void Dispose() => Cursor?.Dispose();
}
