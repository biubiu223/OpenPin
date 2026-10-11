using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Recording;
using OpenPin.Recording.Codec;

namespace OpenPin.Tests.Recording;

public class GifEncoderTests
{
    private static byte[] Encode(int w, int h, IReadOnlyList<(byte[] Px, double Start)> frames, double end,
        GifDitherMode dither = GifDitherMode.None)
    {
        using var ms = new MemoryStream();
        using (var enc = new GifEncoder(ms, w, h, dither))
        {
            foreach (var (px, start) in frames) enc.AddFrame(px, start);
            enc.Finish(end);
        }
        return ms.ToArray();
    }

    /// <summary>Decode with WPF and composite frames (disposal 1 + transparency) into full canvases.</summary>
    private static List<(byte[] Canvas, int DelayCs)> Decode(byte[] gif, int w, int h)
    {
        var result = new List<(byte[], int)>();
        TestFrames.Sta(() =>
        {
            var dec = new GifBitmapDecoder(new MemoryStream(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var canvas = new byte[w * h * 4];
            foreach (var f in dec.Frames)
            {
                var meta = (BitmapMetadata)f.Metadata;
                int left = Convert.ToInt32(meta.GetQuery("/imgdesc/Left"));
                int top = Convert.ToInt32(meta.GetQuery("/imgdesc/Top"));
                int delay = Convert.ToInt32(meta.GetQuery("/grctlext/Delay"));
                var bgra = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
                var px = new byte[fw * fh * 4];
                bgra.CopyPixels(px, fw * 4, 0);
                for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                {
                    int s = (y * fw + x) * 4, d = ((top + y) * w + left + x) * 4;
                    if (px[s + 3] == 0) continue; // transparent: keep previous
                    canvas[d] = px[s]; canvas[d + 1] = px[s + 1]; canvas[d + 2] = px[s + 2]; canvas[d + 3] = 255;
                }
                result.Add(((byte[])canvas.Clone(), delay));
            }
        });
        return result;
    }

    private static int MaxDiff(byte[] a, byte[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++)
                max = Math.Max(max, Math.Abs(a[i + c] - b[i + c]));
        return max;
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++)
                sum += Math.Abs(a[i + c] - b[i + c]);
        return sum / (a.Length / 4.0 * 3);
    }

    [Fact]
    public void FewColors_RoundTripExactly_WithDeltaFrames()
    {
        const int w = 64, h = 48;
        var f0 = TestFrames.Solid(w, h, 255, 255, 255);
        TestFrames.FillRect(f0, w, 5, 5, 20, 10, 30, 120, 220);
        var f1 = (byte[])f0.Clone();
        TestFrames.FillRect(f1, w, 40, 30, 8, 8, 200, 0, 0);
        var f2 = (byte[])f1.Clone();
        TestFrames.FillRect(f2, w, 0, 0, 3, 3, 0, 0, 0);

        var gif = Encode(w, h, new[] { (f0, 0.0), (f1, 100.0), (f2, 250.0) }, 400);
        var frames = Decode(gif, w, h);

        Assert.Equal(3, frames.Count);
        Assert.Equal(0, MaxDiff(f0, frames[0].Canvas));
        Assert.Equal(0, MaxDiff(f1, frames[1].Canvas));
        Assert.Equal(0, MaxDiff(f2, frames[2].Canvas));
        Assert.Equal(new[] { 10, 15, 15 }, frames.Select(f => f.DelayCs).ToArray());
    }

    [Fact]
    public void IdenticalFrames_AreMerged_IntoLongerDelay()
    {
        const int w = 16, h = 16;
        var a = TestFrames.Solid(w, h, 10, 20, 30);
        var b = TestFrames.Solid(w, h, 200, 20, 30);
        var gif = Encode(w, h, new[] { (a, 0.0), ((byte[])a.Clone(), 50.0), ((byte[])a.Clone(), 100.0), (b, 150.0) }, 300);
        var frames = Decode(gif, w, h);
        Assert.Equal(2, frames.Count);
        Assert.Equal(15, frames[0].DelayCs);
        Assert.Equal(15, frames[1].DelayCs);
    }

    [Fact]
    public void DeltaFrame_IsCroppedToChangedRect()
    {
        const int w = 100, h = 80;
        var a = TestFrames.Solid(w, h, 255, 255, 255);
        var b = (byte[])a.Clone();
        TestFrames.FillRect(b, w, 30, 20, 10, 5, 0, 0, 255);
        var gif = Encode(w, h, new[] { (a, 0.0), (b, 100.0) }, 200);
        TestFrames.Sta(() =>
        {
            var dec = new GifBitmapDecoder(new MemoryStream(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(2, dec.Frames.Count);
            Assert.Equal(10, dec.Frames[1].PixelWidth);
            Assert.Equal(5, dec.Frames[1].PixelHeight);
            var meta = (BitmapMetadata)dec.Frames[1].Metadata;
            Assert.Equal(30, Convert.ToInt32(meta.GetQuery("/imgdesc/Left")));
            Assert.Equal(20, Convert.ToInt32(meta.GetQuery("/imgdesc/Top")));
        });
    }

    [Theory]
    [InlineData(GifDitherMode.None)]
    [InlineData(GifDitherMode.Ordered)]
    [InlineData(GifDitherMode.FloydSteinberg)]
    public void ManyColors_AreQuantized_WithinTolerance(GifDitherMode dither)
    {
        const int w = 120, h = 90;
        var f0 = TestFrames.Gradient(w, h);
        var f1 = TestFrames.Gradient(w, h, phase: 40);
        var gif = Encode(w, h, new[] { (f0, 0.0), (f1, 100.0) }, 200, dither);
        var frames = Decode(gif, w, h);
        Assert.Equal(2, frames.Count);
        double limit = dither == GifDitherMode.Ordered ? 8 : 6; // ordered dithering trades per-pixel error for smoothness
        Assert.True(MeanDiff(f0, frames[0].Canvas) < limit, $"mean error {MeanDiff(f0, frames[0].Canvas)}");
        Assert.True(MeanDiff(f1, frames[1].Canvas) < limit, $"mean error {MeanDiff(f1, frames[1].Canvas)}");
        Assert.True(MaxDiff(f0, frames[0].Canvas) < 64);
    }

    [Fact]
    public void Header_HasLoopExtension_AndTrailer()
    {
        var a = TestFrames.Solid(4, 4, 1, 2, 3);
        var gif = Encode(4, 4, new[] { (a, 0.0) }, 100);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));
        Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(gif));
        Assert.Equal(0x3B, gif[^1]);
    }

    [Fact]
    public void LargeNoisyFrame_LzwTableResets_DecodesCorrectly()
    {
        // > 4096 distinct sequences forces LZW clear codes; 256 exact colors keeps the check lossless
        const int w = 256, h = 64;
        var px = new byte[w * h * 4];
        var rnd = new Random(42);
        var colors = Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)(255 - i), (byte)(i * 7))).ToArray();
        for (int i = 0; i < w * h; i++)
        {
            var c = colors[rnd.Next(256)];
            px[i * 4] = c.Item3; px[i * 4 + 1] = c.Item2; px[i * 4 + 2] = c.Item1; px[i * 4 + 3] = 255;
        }
        var gif = Encode(w, h, new[] { (px, 0.0) }, 100);
        var frames = Decode(gif, w, h);
        Assert.Single(frames);
        Assert.Equal(0, MaxDiff(px, frames[0].Canvas));
    }
}
