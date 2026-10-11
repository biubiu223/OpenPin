using System.Windows.Media;
using OpenPin.Capture;

namespace OpenPin.Tests.Capture;

public class ImageEffectsTests
{
    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    private static byte Alpha(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    [Fact]
    public void None_ReturnsInput()
    {
        var px = Solid(4, 4, 1, 2, 3);
        var (o, w, h, off) = ImageEffects.Apply(px, 4, 4, new OutputEffects(0, 0, Colors.Red, 0));
        Assert.Same(px, o);
        Assert.Equal((4, 4, 0), (w, h, off));
    }

    [Fact]
    public void RoundedCorners_TransparentCornersOpaqueCenter()
    {
        var px = Solid(40, 30, 10, 20, 30);
        var (o, w, h, off) = ImageEffects.Apply(px, 40, 30, new OutputEffects(10, 0, Colors.Red, 0));
        Assert.Equal((40, 30, 0), (w, h, off));
        Assert.Equal(0, Alpha(o, w, 0, 0));
        Assert.Equal(0, Alpha(o, w, 39, 29));
        Assert.Equal(255, Alpha(o, w, 20, 15));
        Assert.Equal(255, Alpha(o, w, 20, 0));   // top edge middle is not a corner
        Assert.Equal(255, Alpha(o, w, 10, 10));  // inside the corner arc
        // Color untouched in the middle
        Assert.Equal(new byte[] { 10, 20, 30 }, o.Skip((15 * 40 + 20) * 4).Take(3).ToArray());
    }

    [Fact]
    public void Border_PaintsEdgesOnly()
    {
        var px = Solid(20, 20, 0, 0, 0);
        var (o, w, _, _) = ImageEffects.Apply(px, 20, 20, new OutputEffects(0, 2, Color.FromRgb(255, 0, 0), 0));
        int edge = (0 * w + 10) * 4, inner = (10 * w + 10) * 4;
        Assert.Equal(255, o[edge + 2]);   // red on the edge
        Assert.Equal(0, o[inner + 2]);    // untouched inside
        Assert.Equal(255, o[(1 * w + 10) * 4 + 2]); // second row is still border (width 2)
        Assert.Equal(0, o[(2 * w + 10) * 4 + 2]);
    }

    [Fact]
    public void Shadow_ExpandsCanvasAndKeepsImage()
    {
        var px = Solid(30, 20, 50, 60, 70);
        var (o, w, h, off) = ImageEffects.Apply(px, 30, 20, new OutputEffects(0, 0, Colors.Red, 12));
        Assert.Equal(12, off);
        Assert.Equal(30 + 24, w);
        Assert.Equal(20 + 24, h);
        // Original pixels are opaque and unchanged at their offset.
        int i = ((off + 5) * w + off + 5) * 4;
        Assert.Equal(new byte[] { 50, 60, 70, 255 }, o.Skip(i).Take(4).ToArray());
        // Just below the image there is a semi-transparent shadow; the far corner is (almost) clear.
        Assert.InRange(Alpha(o, w, off + 15, off + 20 + 2), 10, 254);
        Assert.InRange(Alpha(o, w, 0, 0), 0, 10);
    }

    [Fact]
    public void Coverage_Basics()
    {
        Assert.Equal(1, ImageEffects.Coverage(5, 5, 0, 0, 10, 10, 0));
        Assert.Equal(0, ImageEffects.Coverage(10, 5, 0, 0, 10, 10, 0));
        Assert.InRange(ImageEffects.Coverage(0, 0, 0, 0, 10, 10, 4), 0, 0.2);
    }

    [Fact]
    public void BlendOnto_ClipsAndBlends()
    {
        var dest = Solid(4, 4, 0, 0, 0);
        var patch = new byte[2 * 2 * 4];
        for (int i = 0; i < patch.Length; i += 4) { patch[i] = 200; patch[i + 3] = 255; }
        patch[3 + 4] = 0;            // second pixel fully transparent
        ImageEffects.BlendOnto(dest, 4, 4, patch, 2, 2, 3, 3); // only the top-left patch pixel lands
        Assert.Equal(200, dest[(3 * 4 + 3) * 4]);
        Assert.Equal(0, dest[(2 * 4 + 2) * 4]);
    }
}
