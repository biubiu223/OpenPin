using System.Windows;
using OpenPin.Recording;
using OpenPin.Recording.Codec;

namespace OpenPin.Tests.Recording;

public class PixelOpsTests
{
    [Fact]
    public void FindChangedRect_IgnoresAlpha()
    {
        var a = TestFrames.Solid(20, 10, 1, 2, 3);
        var b = (byte[])a.Clone();
        for (int i = 3; i < b.Length; i += 4) b[i] = 0; // alpha differs everywhere
        Assert.True(PixelOps.FindChangedRect(a, b, 20, 10).IsEmpty);
        TestFrames.FillRect(b, 20, 4, 3, 5, 2, 9, 9, 9);
        Assert.Equal(new Int32Rect(4, 3, 5, 2), PixelOps.FindChangedRect(a, b, 20, 10));
    }

    [Fact]
    public void Resize_AveragesBoxes()
    {
        var src = TestFrames.Solid(4, 2, 0, 0, 0);
        TestFrames.FillRect(src, 4, 0, 0, 1, 1, 200, 100, 40); // one of four pixels in the first 2×2 box
        var dst = new byte[2 * 1 * 4];
        PixelOps.Resize(src, 4, 2, dst, 2, 1);
        Assert.Equal((50, 25, 10), TestFrames.At(dst, 2, 0, 0));
        Assert.Equal((0, 0, 0), TestFrames.At(dst, 2, 1, 0));
    }

    [Fact]
    public void PadReplicate_CopiesEdges()
    {
        var src = TestFrames.Solid(3, 3, 10, 10, 10);
        TestFrames.FillRect(src, 3, 2, 0, 1, 3, 99, 0, 0);
        var dst = new byte[4 * 4 * 4];
        PixelOps.PadReplicate(src, 3, 3, dst, 4, 4);
        Assert.Equal((99, 0, 0), TestFrames.At(dst, 4, 3, 0));
        Assert.Equal((10, 10, 10), TestFrames.At(dst, 4, 0, 3));
    }

    [Fact]
    public void DrawCursor_BlendsAndInverts()
    {
        var canvas = TestFrames.Solid(4, 4, 100, 100, 100);
        var img = new byte[2 * 1 * 4];
        img[0] = 0; img[1] = 0; img[2] = 255; img[3] = 255;       // opaque red
        var invert = new byte[] { 0, 1 };                        // second pixel inverts
        PixelOps.DrawCursor(canvas, 4, 4, new CursorShape(2, 1, img, invert, 0, 0), 1, 1);
        Assert.Equal((255, 0, 0), TestFrames.At(canvas, 4, 1, 1));
        Assert.Equal((155, 155, 155), TestFrames.At(canvas, 4, 2, 1));
        Assert.Equal((100, 100, 100), TestFrames.At(canvas, 4, 0, 0));
        // clipped at the border without throwing
        PixelOps.DrawCursor(canvas, 4, 4, new CursorShape(2, 1, img, invert, 0, 0), 3, 3);
        PixelOps.DrawCursor(canvas, 4, 4, new CursorShape(2, 1, img, invert, 0, 0), -5, -5);
    }

    [Fact]
    public void DrawRing_TouchesOnlyNearby()
    {
        var canvas = TestFrames.Solid(40, 40, 0, 0, 0);
        PixelOps.DrawRing(canvas, 40, 40, 20, 20, 8, 3, 0xFF0000, 1, 0);
        Assert.Equal((0, 0, 0), TestFrames.At(canvas, 40, 20, 20)); // centre (no fill)
        Assert.Equal(255, TestFrames.At(canvas, 40, 28, 20).R);     // on the ring
        Assert.Equal((0, 0, 0), TestFrames.At(canvas, 40, 0, 0));
    }

    [Fact]
    public void Quantizer_ExactPalette_ForFewColors()
    {
        var px = new uint[] { 0x112233, 0x445566, 0x112233, 0xFFFFFF };
        var q = ColorQuantizer.Build(px, default, 255);
        Assert.True(q.IsExact);
        Assert.Equal(3, q.Palette.Length);
        var idx = new byte[4];
        q.MapPixels(px, default, 4, 1, idx, GifDitherMode.FloydSteinberg, 255);
        Assert.Equal(idx[0], idx[2]);
        Assert.Equal(0x445566u, q.Palette[idx[1]]);
    }

    [Fact]
    public void Quantizer_MedianCut_LimitsPaletteSize()
    {
        var px = new uint[4096];
        for (int i = 0; i < px.Length; i++) px[i] = (uint)((i * 37 % 256) << 16 | (i * 11 % 256) << 8 | (i % 256));
        var q = ColorQuantizer.Build(px, default, 64);
        Assert.False(q.IsExact);
        Assert.True(q.Palette.Length <= 64);
    }
}
