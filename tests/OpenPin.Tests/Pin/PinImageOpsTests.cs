using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;
using OpenPin.Pin.Logic;
using OpenPin.Pin.Model;
using OpenPin.Pin.Storage;

namespace OpenPin.Tests.Pin;

public class PinImageOpsTests
{
    /// <summary>3×2 image with distinct pixel values.</summary>
    private static BitmapSource Sample()
    {
        var px = new byte[3 * 2 * 4];
        for (int i = 0; i < 6; i++)
        {
            px[i * 4] = (byte)(i * 40);       // B
            px[i * 4 + 1] = (byte)(i * 10);   // G
            px[i * 4 + 2] = (byte)(200 - i);  // R
            px[i * 4 + 3] = 255;
        }
        return BitmapUtil.FromPixels(px, 3, 2);
    }

    private static BitmapSource Rotate(BitmapSource s, int deg) => BitmapUtil.ToBgra32(new TransformedBitmap(s, new RotateTransform(deg)));
    private static BitmapSource FlipH(BitmapSource s) => BitmapUtil.ToBgra32(new TransformedBitmap(s, new ScaleTransform(-1, 1)));
    private static BitmapSource FlipV(BitmapSource s) => BitmapUtil.ToBgra32(new TransformedBitmap(s, new ScaleTransform(1, -1)));

    private static void AssertSamePixels(BitmapSource a, BitmapSource b)
    {
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        Assert.Equal(BitmapUtil.GetPixels(a), BitmapUtil.GetPixels(b));
    }

    [Fact]
    public void Adjustments_MatchSequentialOperations()
    {
        var img = Sample();
        // user does: flip H, rotate right, flip V, rotate left, rotate left
        var expected = Rotate(Rotate(FlipV(Rotate(FlipH(img), 90)), 270), 270);
        var a = new ImageAdjustments();
        a.FlipHorizontal();
        a.RotateRight();
        a.FlipVertical();
        a.RotateLeft();
        a.RotateLeft();
        AssertSamePixels(expected, PinImageOps.Apply(img, a));
    }

    [Fact]
    public void Rotation_SwapsDimensions()
    {
        var a = new ImageAdjustments();
        a.RotateRight();
        var r = PinImageOps.Apply(Sample(), a);
        Assert.Equal(2, r.PixelWidth);
        Assert.Equal(3, r.PixelHeight);
    }

    [Fact]
    public void Filters_GrayInvertBrightness()
    {
        var px = new byte[] { 10, 20, 30, 128 };
        PinImageOps.ApplyFilters(px, grayscale: false, invert: true, brightnessPercent: 0);
        Assert.Equal(new byte[] { 245, 235, 225, 128 }, px);

        px = new byte[] { 0, 0, 255, 255 }; // pure red
        PinImageOps.ApplyFilters(px, grayscale: true, invert: false, brightnessPercent: 0);
        Assert.Equal(px[0], px[1]);
        Assert.Equal(px[1], px[2]);
        Assert.Equal(76, px[0]); // 0.299 * 255

        px = new byte[] { 250, 100, 0, 255 };
        PinImageOps.ApplyFilters(px, grayscale: false, invert: false, brightnessPercent: 10);
        Assert.Equal(new byte[] { 255, 126, 26, 255 }, px);
    }

    [Fact]
    public void Scale_RoundsSize()
    {
        var s = PinImageOps.Scale(Sample(), 2, smooth: false);
        Assert.Equal(6, s.PixelWidth);
        Assert.Equal(4, s.PixelHeight);
        Assert.Same(Sample().GetType(), PinImageOps.Scale(Sample(), 1, true).GetType());
    }

    [Fact]
    public void Hash_DependsOnPixels()
    {
        var a = Sample();
        var px = BitmapUtil.GetPixels(a);
        px[0] ^= 1;
        var b = BitmapUtil.FromPixels(px, 3, 2);
        Assert.Equal(PinImageOps.Hash(a), PinImageOps.Hash(Sample()));
        Assert.NotEqual(PinImageOps.Hash(a), PinImageOps.Hash(b));
    }

    [Fact]
    public void Store_SavesLoadsAndSkipsCorruptPins()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenPinTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PinStore(root);
            var m = new PinManifest { Id = PinManifest.NewId(), Kind = PinKind.Image, OriginX = 5, OriginY = 6 };
            store.SaveImage(m.Id, PinStore.OriginalFile, Sample());
            store.SaveManifest(m);
            var t = new PinManifest { Id = PinManifest.NewId(), Kind = PinKind.Text, Text = "hello" };
            store.SaveManifest(t);
            Assert.True(store.Flush());

            // corrupt pin + image pin without its PNG + stray empty folder
            Directory.CreateDirectory(Path.Combine(root, "bad"));
            File.WriteAllText(Path.Combine(root, "bad", PinStore.ManifestFile), "{ broken");
            var noImg = new PinManifest { Id = "noimg", Kind = PinKind.Image };
            Directory.CreateDirectory(Path.Combine(root, "noimg"));
            File.WriteAllText(Path.Combine(root, "noimg", PinStore.ManifestFile), noImg.ToJson());
            Directory.CreateDirectory(Path.Combine(root, "empty"));

            var loaded = new PinStore(root).LoadAll();
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, x => x.Id == m.Id && x.OriginX == 5);
            Assert.Contains(loaded, x => x.Id == t.Id && x.Text == "hello");
            var img = store.LoadImage(m.Id, PinStore.OriginalFile);
            Assert.NotNull(img);
            AssertSamePixels(Sample(), img!);
            Assert.True(Directory.Exists(Path.Combine(root, "bad.broken")));

            store.Delete(m.Id);
            Assert.True(store.Flush());
            Assert.False(Directory.Exists(Path.Combine(root, m.Id)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
