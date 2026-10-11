using System.Runtime.InteropServices;
using System.Windows;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Recording.Capture;

/// <summary>
/// GDI grabber for a fixed screen rectangle that keeps its memory DC + DIB section alive between frames (the per-call
/// allocations of <c>ScreenCapture.CaptureRegionInto</c> cost more than the blit at 30–60 fps). Single thread only.
/// Output: BGRA top-down, alpha undefined, no cursor (the recorder stores the cursor separately).
/// </summary>
internal sealed class ScreenGrabber : IDisposable
{
    private readonly Int32Rect _rect;
    private readonly IntPtr _screenDc, _memDc, _dib, _old;
    private readonly IntPtr _bits;
    private bool _disposed;

    public ScreenGrabber(Int32Rect rectPx)
    {
        if (rectPx.Width <= 0 || rectPx.Height <= 0) throw new ArgumentException("Empty capture rectangle", nameof(rectPx));
        _rect = rectPx;
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = rectPx.Width,
                biHeight = -rectPx.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            }
        };
        _dib = CreateDIBSection(_screenDc, ref bmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            DeleteDC(_memDc);
            ReleaseDC(IntPtr.Zero, _screenDc);
            throw new InvalidOperationException("CreateDIBSection failed");
        }
        _old = SelectObject(_memDc, _dib);
    }

    public int Width => _rect.Width;
    public int Height => _rect.Height;
    public int ByteCount => _rect.Width * _rect.Height * 4;

    /// <summary>Grab the rectangle into <paramref name="buffer"/>. Returns false if the blit failed (e.g. secure desktop).</summary>
    public bool Grab(byte[] buffer)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenGrabber));
        // CAPTUREBLT: include layered windows (pins, tooltips…); windows with WDA_EXCLUDEFROMCAPTURE are left out.
        bool ok = BitBlt(_memDc, 0, 0, _rect.Width, _rect.Height, _screenDc, _rect.X, _rect.Y, SRCCOPY | CAPTUREBLT);
        RecNative.GdiFlush();
        Marshal.Copy(_bits, buffer, 0, ByteCount);
        return ok;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SelectObject(_memDc, _old);
        DeleteObject(_dib);
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
