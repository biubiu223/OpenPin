using System.IO;
using OpenPin.Recording.Codec;

namespace OpenPin.Tests.Recording;

public class FrameStoreTests
{
    private static (FrameStore Store, List<byte[]> Originals) Record(int w, int h, int count, int keyIntervalMs = 3000)
    {
        var path = TestFrames.TempFile(".oprec");
        var originals = new List<byte[]>();
        using var writer = new FrameStoreWriter(path, w, h, 10) { KeyframeIntervalMs = keyIntervalMs };
        var cur = TestFrames.Gradient(w, h);
        for (int i = 0; i < count; i++)
        {
            var frame = (byte[])cur.Clone();
            if (i % 3 != 2) // every third frame unchanged
                TestFrames.FillRect(frame, w, (i * 7) % (w - 10), (i * 5) % (h - 10), 10, 10, (byte)(i * 20), 0, (byte)(255 - i));
            originals.Add((byte[])frame.Clone());
            cur = frame;
            writer.Append(frame, i * 100.0, new CursorSample(-1, 0, 0));
        }
        return (writer.Complete(count * 100.0), originals);
    }

    private static void AssertRgbEqual(byte[] expected, byte[] actual)
    {
        for (int i = 0; i < expected.Length; i += 4)
        {
            Assert.Equal(expected[i], actual[i]);
            Assert.Equal(expected[i + 1], actual[i + 1]);
            Assert.Equal(expected[i + 2], actual[i + 2]);
            Assert.Equal(255, actual[i + 3]);
        }
    }

    [Fact]
    public void RoundTrip_Sequential()
    {
        var (store, originals) = Record(64, 40, 20);
        try
        {
            Assert.Equal(20, store.Frames.Count);
            Assert.Equal(2000, store.DurationMs);
            using var reader = store.OpenReader();
            for (int i = 0; i < originals.Count; i++)
            {
                reader.Seek(i);
                AssertRgbEqual(originals[i], reader.Canvas);
            }
        }
        finally { store.Delete(); }
    }

    [Fact]
    public void RoundTrip_RandomAccess_UsesKeyframes()
    {
        var (store, originals) = Record(50, 30, 40, keyIntervalMs: 500);
        try
        {
            Assert.True(store.Frames.Count(f => f.IsKey) > 3);
            Assert.True(store.Frames[0].IsKey);
            using var reader = store.OpenReader();
            foreach (var i in new[] { 39, 3, 17, 18, 0, 25, 24, 39 })
            {
                reader.Seek(i);
                AssertRgbEqual(originals[i], reader.Canvas);
            }
        }
        finally { store.Delete(); }
    }

    [Fact]
    public void UnchangedFrames_StoreNothing()
    {
        var (store, _) = Record(32, 32, 6);
        try
        {
            Assert.Equal(0, store.Frames[2].Length);
            Assert.Equal(0, store.Frames[5].Length);
            Assert.True(store.Frames[1].Length > 0);
            Assert.True(store.Frames[1].Rect.Width <= 10 && store.Frames[1].Rect.Height <= 10);
        }
        finally { store.Delete(); }
    }

    [Fact]
    public void Writer_ReturnsPreviousBuffer_ForReuse()
    {
        var path = TestFrames.TempFile(".oprec");
        using var writer = new FrameStoreWriter(path, 8, 8, 10);
        var a = TestFrames.Solid(8, 8, 1, 2, 3);
        var b = TestFrames.Solid(8, 8, 4, 5, 6);
        Assert.Null(writer.Append(a, 0, CursorSample.None));
        Assert.Same(a, writer.Append(b, 100, CursorSample.None));
        writer.Complete(200).Delete();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ScreenLikeContent_CompressesWell()
    {
        const int w = 1280, h = 720;
        var path = TestFrames.TempFile(".oprec");
        using var writer = new FrameStoreWriter(path, w, h, 30);
        var frame = TestFrames.Solid(w, h, 243, 243, 243);
        TestFrames.FillRect(frame, w, 100, 100, 600, 400, 255, 255, 255);
        for (int i = 0; i < 30; i++)
        {
            var f = (byte[])frame.Clone();
            TestFrames.FillRect(f, w, 120 + i * 10, 120, 8, 16, 0, 0, 0); // "typing"
            frame = f;
            writer.Append(f, i * 33.3, CursorSample.None);
        }
        var store = writer.Complete(1000);
        try
        {
            long size = new FileInfo(path).Length;
            Assert.True(size < 200_000, $"store is {size} bytes");
        }
        finally { store.Delete(); }
    }
}
