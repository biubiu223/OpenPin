using System.Runtime.InteropServices;
using OpenPin.Recording.Codec;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Recording.Capture;

/// <summary>
/// Samples the mouse cursor for each frame: position + shape id. Shapes are extracted once per cursor handle by drawing the
/// cursor on black and on white: alpha = 255 − (white − black), color = black / alpha; pixels that come out much brighter on
/// black than on white are XOR ("invert screen") pixels, e.g. the I-beam. Single thread only.
/// </summary>
internal sealed class CursorTracker
{
    private readonly Dictionary<IntPtr, (int Id, int HotX, int HotY)> _known = new();
    private readonly Func<CursorShape, int> _register;

    /// <param name="register">Stores a new shape and returns its id (e.g. <see cref="FrameStoreWriter.RegisterCursor"/>).</param>
    public CursorTracker(Func<CursorShape, int> register) => _register = register;

    /// <summary>Cursor state relative to the region origin (physical px).</summary>
    public CursorSample Sample(int originX, int originY, out int screenX, out int screenY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        bool ok = GetCursorInfo(ref ci);
        screenX = ci.ptScreenPos.X;
        screenY = ci.ptScreenPos.Y;
        if (!ok || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return CursorSample.None;

        if (!_known.TryGetValue(ci.hCursor, out var info))
        {
            info = (-1, 0, 0);
            try
            {
                if (Extract(ci.hCursor) is { } shape)
                    info = (_register(shape), shape.HotspotX, shape.HotspotY);
            }
            catch (Exception ex) { Core.Log.Warn($"Cursor shape extraction failed: {ex.Message}"); }
            _known[ci.hCursor] = info;
        }
        if (info.Id < 0) return CursorSample.None;
        return new CursorSample(info.Id, screenX - info.HotX - originX, screenY - info.HotY - originY);
    }

    internal static CursorShape? Extract(IntPtr hCursor)
    {
        if (!GetIconInfo(hCursor, out var ii)) return null;
        int w, h;
        try
        {
            IntPtr probe = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
            if (RecNative.GetObject(probe, Marshal.SizeOf<RecNative.BITMAP>(), out var bm) == 0) return null;
            w = bm.bmWidth;
            h = ii.hbmColor != IntPtr.Zero ? bm.bmHeight : bm.bmHeight / 2; // monochrome: AND mask above XOR mask
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

        var onBlack = Render(hCursor, w, h, 0x00);
        var onWhite = Render(hCursor, w, h, 0xFF);
        var bgra = new byte[w * h * 4];
        byte[]? invert = null;
        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            int b0 = onBlack[i], g0 = onBlack[i + 1], r0 = onBlack[i + 2];
            int b1 = onWhite[i], g1 = onWhite[i + 1], r1 = onWhite[i + 2];
            if (b0 > b1 + 64 && g0 > g1 + 64 && r0 > r1 + 64)
            {
                invert ??= new byte[w * h];
                invert[p] = 1;
                continue;
            }
            int a = 255 - Math.Clamp(((b1 - b0) + (g1 - g0) + (r1 - r0)) / 3, 0, 255);
            if (a <= 0) continue;
            bgra[i] = (byte)Math.Min(255, b0 * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, g0 * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, r0 * 255 / a);
            bgra[i + 3] = (byte)a;
        }
        return new CursorShape(w, h, bgra, invert, ii.xHotspot, ii.yHotspot);
    }

    private static byte[] Render(IntPtr hCursor, int w, int h, byte background)
    {
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
            var buf = new byte[w * h * 4];
            if (background != 0) Array.Fill(buf, background);
            Marshal.Copy(buf, 0, bits, buf.Length);
            DrawIconEx(memDc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, DI_NORMAL);
            RecNative.GdiFlush();
            Marshal.Copy(bits, buf, 0, buf.Length);
            return buf;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
