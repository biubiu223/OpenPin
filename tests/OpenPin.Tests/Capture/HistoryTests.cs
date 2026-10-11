using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Capture;

namespace OpenPin.Tests.Capture;

public class HistoryTests
{
    [Fact]
    public void Ring_NewestFirst_DedupedAndCapped()
    {
        var h = new RingHistory<int>(3);
        h.Add(1); h.Add(2); h.Add(3);
        Assert.Equal(new[] { 3, 2, 1 }, h.Items);
        h.Add(2);
        Assert.Equal(new[] { 2, 3, 1 }, h.Items);
        h.Add(4);
        Assert.Equal(new[] { 4, 2, 3 }, h.Items);
        h.SetCapacity(1);
        Assert.Equal(new[] { 4 }, h.Items);
    }

    [Fact]
    public void Cursor_WalksOlderAndBackToLive()
    {
        var c = new HistoryCursor();
        Assert.Equal(-1, c.Index);
        Assert.False(c.Newer());
        Assert.True(c.Older(2));
        Assert.Equal(0, c.Index);
        Assert.True(c.Older(2));
        Assert.Equal(1, c.Index);
        Assert.False(c.Older(2)); // oldest reached
        Assert.True(c.Newer());
        Assert.True(c.Newer());
        Assert.Equal(-1, c.Index); // live again
        Assert.False(new HistoryCursor().Older(0));
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "OpenPinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Store_RegionsPersist()
    {
        var dir = TempDir();
        try
        {
            var s = new CaptureHistoryStore(dir);
            s.AddRegion(new Int32Rect(1, 2, 3, 4), 5);
            s.AddRegion(new Int32Rect(-100, 5, 30, 40), 5);
            s.AddRegion(new Int32Rect(0, 0, 0, 0), 5); // ignored
            var reloaded = new CaptureHistoryStore(dir);
            Assert.Equal(new[] { new Int32Rect(-100, 5, 30, 40), new Int32Rect(1, 2, 3, 4) }, reloaded.GetRegions(5));
            Assert.Equal(new Int32Rect(-100, 5, 30, 40), reloaded.LastRegion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Store_ImagesTrimmedAndReloadable()
    {
        var dir = TempDir();
        try
        {
            var s = new CaptureHistoryStore(dir);
            for (int i = 0; i < 4; i++)
            {
                var px = Enumerable.Repeat((byte)(i * 40), 8 * 6 * 4).ToArray();
                var bmp = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, px, 8 * 4);
                bmp.Freeze();
                s.AddImageAsync(bmp, new Int32Rect(i, i, 8, 6), capacity: 2);
            }
            s.Flush();
            var images = new CaptureHistoryStore(dir).GetImages();
            Assert.Equal(2, images.Count);
            Assert.Equal(new Int32Rect(3, 3, 8, 6), images[0].RectPx);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "images"), "*.png").Length);
            var loaded = s.LoadImage(images[0]);
            Assert.NotNull(loaded);
            Assert.Equal(8, loaded!.PixelWidth);
        }
        finally { Directory.Delete(dir, true); }
    }
}
