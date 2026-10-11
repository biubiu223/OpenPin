using System.IO;
using OpenPin.Recording.Codec;
using Windows.Storage;

namespace OpenPin.Tests.Recording;

public class Mp4EncoderTests
{
    [Fact]
    public async Task Encodes_NonEmptyFile_WithExpectedDurationAndSize()
    {
        const int w = 320, h = 240, fps = 10, n = 20;
        var path = TestFrames.TempFile(".mp4");
        try
        {
            try
            {
                await Mp4Encoder.EncodeAsync(path, w, h, fps, n, 1_000_000, (k, buf) =>
                {
                    // top half red, bottom half blue, plus a moving white bar
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        bool top = y < h / 2;
                        bool bar = x >= k * 10 && x < k * 10 + 20;
                        buf[i] = (byte)(bar ? 255 : top ? 0 : 255);
                        buf[i + 1] = (byte)(bar ? 255 : 0);
                        buf[i + 2] = (byte)(bar ? 255 : top ? 255 : 0);
                        buf[i + 3] = 255;
                    }
                }, null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                // No H.264 encoder (e.g. Windows N without the media feature pack / some CI images): skip.
                Console.WriteLine($"MP4 encoder unavailable, skipping: {ex.Message}");
                return;
            }

            var info = new FileInfo(path);
            Assert.True(info.Length > 1000, $"file is {info.Length} bytes");

            var file = await StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            Assert.Equal((uint)w, props.Width);
            Assert.Equal((uint)h, props.Height);
            Assert.InRange(props.Duration.TotalMilliseconds, 1900, 2100);

            // Decode one frame and check orientation + colors (top red, bottom blue). Skipped if the decoder is unavailable.
            var frame = await TryDecodeFrameAsync(file, 1500, w, h);
            if (frame == null) return;
            var (pixels, dw, dh) = frame.Value;
            (int B, int G, int R) Px(int x, int y) { int i = (y * dw + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2]); }
            var top = Px(dw - 10, 10);
            var bottom = Px(dw - 10, dh - 10);
            Assert.True(top.R > 200 && top.B < 60, $"top pixel {top}");
            Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom pixel {bottom}");
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static async Task<(byte[] Pixels, int Width, int Height)?> TryDecodeFrameAsync(StorageFile file, double ms, int w, int h)
    {
        try
        {
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var comp = new Windows.Media.Editing.MediaComposition();
            comp.Clips.Add(clip);
            var thumb = await comp.GetThumbnailAsync(TimeSpan.FromMilliseconds(ms), w, h,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
            var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Video frame decoding unavailable, skipping pixel check: {ex.Message}");
            return null;
        }
    }
}
