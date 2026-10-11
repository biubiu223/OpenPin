using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Core.Screen;

/// <summary>
/// GDI based screen grabber. Returns frozen <see cref="PixelFormats.Bgr32"/> bitmaps at 96 DPI,
/// so 1 bitmap pixel == 1 physical screen pixel == 1 WPF unit when displayed without scaling.
/// Thread-safe (no shared state); may be called from background threads.
/// </summary>
public static class ScreenCapture
{
    /// <summary>Freezes the whole virtual screen (all monitors) + window list. Call BEFORE showing any overlay.</summary>
    public static ScreenSnapshot CaptureVirtualScreen()
    {
        var bounds = Monitors.VirtualScreenPx;
        var cursor = CursorSnapshot.Grab();
        var image = CaptureRegion(bounds, includeCursor: false);
        return new ScreenSnapshot
        {
            Image = image,
            VirtualBoundsPx = bounds,
            Monitors = Screen.Monitors.GetAll(),
            // Include our own visible windows so pins can be picked by window detection.
            // (The overlay does not exist yet; hidden/cloaked helper windows are filtered out.)
            Windows = WindowEnumerator.GetTopLevelWindows(includeOwnProcess: true),
            Cursor = cursor,
            Timestamp = DateTime.Now,
        };
    }

    /// <summary>Captures a physical-pixel rectangle of the screen into a frozen Bgr32 bitmap.</summary>
    public static BitmapSource CaptureRegion(Int32Rect rectPx, bool includeCursor = false)
    {
        // Build the BitmapSource straight from the DIB section's memory: one copy instead of DIB → byte[] → bitmap.
        BitmapSource? result = null;
        WithScreenDib(rectPx, includeCursor, (bits, byteCount) =>
        {
            result = BitmapSource.Create(rectPx.Width, rectPx.Height, 96, 96, PixelFormats.Bgr32, null,
                bits, byteCount, rectPx.Width * 4);
        });
        result!.Freeze();
        return result;
    }

    /// <summary>
    /// Captures into a caller-provided buffer (length &gt;= width*height*4, BGRA top-down, alpha undefined).
    /// Use this in hot loops (long screenshot, recording) to avoid allocations.
    /// </summary>
    public static void CaptureRegionInto(Int32Rect rectPx, byte[] buffer, bool includeCursor = false)
    {
        int needed = rectPx.Width * rectPx.Height * 4;
        if (buffer.Length < needed) throw new ArgumentException("Buffer too small", nameof(buffer));
        WithScreenDib(rectPx, includeCursor, (bits, byteCount) => Marshal.Copy(bits, buffer, 0, byteCount));
    }

    /// <summary>BitBlt the screen rectangle into a temporary top-down 32bpp DIB and hand its memory to <paramref name="consume"/>.</summary>
    private static void WithScreenDib(Int32Rect rectPx, bool includeCursor, Action<IntPtr, int> consume)
    {
        if (rectPx.Width <= 0 || rectPx.Height <= 0) throw new ArgumentException("Empty capture rectangle", nameof(rectPx));
        int byteCount = rectPx.Width * rectPx.Height * 4;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = rectPx.Width,
                biHeight = -rectPx.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            }
        };
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);
        try
        {
            // CAPTUREBLT is required to include layered windows (e.g. our own pin windows).
            BitBlt(memDc, 0, 0, rectPx.Width, rectPx.Height, screenDc, rectPx.X, rectPx.Y, SRCCOPY | CAPTUREBLT);
            if (includeCursor)
            {
                using var cursor = CursorSnapshot.Grab();
                cursor?.DrawTo(memDc, rectPx.X, rectPx.Y);
            }
            consume(bits, byteCount);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Draws a cursor onto a copy of <paramref name="source"/>. <paramref name="originPx"/> = screen position of the bitmap's (0,0).</summary>
    internal static BitmapSource DrawCursorOnCopy(BitmapSource source, CursorSnapshot cursor, Int32Rect originPx)
    {
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var buffer = new byte[stride * h];
        var src = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        src.CopyPixels(buffer, stride, 0);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = BI_RGB,
            }
        };
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);
        try
        {
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            cursor.DrawTo(memDc, originPx.X, originPx.Y);
            Marshal.Copy(bits, buffer, 0, buffer.Length);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>A copy of the mouse cursor at capture time (so the overlay can toggle it with the ~ key).</summary>
public sealed class CursorSnapshot : IDisposable
{
    private IntPtr _icon;

    /// <summary>Top-left position where the cursor image is drawn (position minus hotspot), physical px.</summary>
    public int X { get; }
    public int Y { get; }

    private CursorSnapshot(IntPtr icon, int x, int y) { _icon = icon; X = x; Y = y; }

    public static CursorSnapshot? Grab()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return null;
        var icon = CopyIcon(ci.hCursor);
        if (icon == IntPtr.Zero) return null;
        int hx = 0, hy = 0;
        if (GetIconInfo(icon, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        return new CursorSnapshot(icon, ci.ptScreenPos.X - hx, ci.ptScreenPos.Y - hy);
    }

    internal void DrawTo(IntPtr hdc, int originX, int originY)
    {
        if (_icon != IntPtr.Zero)
            DrawIconEx(hdc, X - originX, Y - originY, _icon, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    public void Dispose()
    {
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
