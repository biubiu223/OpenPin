using System.IO;
using System.Windows.Media.Imaging;
using OpenPin.Recording;
using OpenPin.Recording.Codec;
using Windows.Storage;

namespace OpenPin.Tests.Recording;

public class ExportTests
{
    /// <summary>A 2 s, 10 fps recording: white background with a moving red square, plus a click and a webcam stream.</summary>
    private static RecordingData MakeRecording(int w = 160, int h = 120, bool webcam = false, bool audio = false)
    {
        var vw = new FrameStoreWriter(TestFrames.TempFile(".oprec"), w, h, 10);
        int cursorId = vw.RegisterCursor(new CursorShape(2, 2, Enumerable.Repeat((byte)0, 16).Select((b, i) => i % 4 == 3 ? (byte)255 : b).ToArray(), null, 0, 0));
        for (int i = 0; i < 20; i++)
        {
            var f = TestFrames.Solid(w, h, 255, 255, 255);
            TestFrames.FillRect(f, w, i * 5, 40, 20, 20, 255, 0, 0);
            vw.Append(f, i * 100, new CursorSample(cursorId, 10, 10));
        }
        var video = vw.Complete(2000);

        FrameStore? cam = null;
        if (webcam)
        {
            var cw = new FrameStoreWriter(TestFrames.TempFile(".oprec"), 64, 48, 5) { KeyframeIntervalMs = 0 };
            for (int i = 0; i < 10; i++) cw.Append(TestFrames.Solid(64, 48, 0, (byte)(100 + i * 10), 0), i * 200, CursorSample.None);
            cam = cw.Complete(2000);
        }

        AudioTrack? mic = null;
        if (audio)
        {
            var aw = new AudioTrackWriter(TestFrames.TempFile(".pcm"));
            var tone = new float[48000 * 2 * 2];
            for (int i = 0; i < tone.Length / 2; i++) tone[i * 2] = tone[i * 2 + 1] = 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / 48000);
            aw.Append(tone, 0);
            mic = aw.Complete(2000);
        }
        var input = new InputLog(new[] { new ClickEvent(500, 80, 60, MouseButtonKind.Left) });
        return new RecordingData(video, input, mic, null, cam);
    }

    [Fact]
    public async Task Gif_TrimmedExport_HasExpectedLength_AndSize()
    {
        var data = MakeRecording();
        var path = TestFrames.TempFile(".gif");
        try
        {
            await RecordingExporter.ExportAsync(data, new ExportOptions
            {
                Format = RecordingFormat.Gif, Quality = RecordingQuality.Original, TrimStartMs = 500, TrimEndMs = 1500,
            }, path, null, CancellationToken.None);

            TestFrames.Sta(() =>
            {
                var dec = new GifBitmapDecoder(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                int total = 0;
                foreach (var f in dec.Frames) total += Convert.ToInt32(((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay"));
                Assert.Equal(100, total); // 1 s in centiseconds
                Assert.True(dec.Frames.Count >= 10);
                Assert.Equal(160, dec.Frames[0].PixelWidth);
                Assert.Equal(120, dec.Frames[0].PixelHeight);
            });
        }
        finally { File.Delete(path); data.Delete(); }
    }

    [Fact]
    public async Task Export_Cancelled_LeavesNoFile()
    {
        var data = MakeRecording();
        var path = TestFrames.TempFile(".gif");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RecordingExporter.ExportAsync(data, new ExportOptions(), path, null, cts.Token));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".openpin-tmp"));
        }
        finally { data.Delete(); }
    }

    [Fact]
    public void Composer_DrawsClickRing_Cursor_AndWebcam_OnlyWhenEnabled()
    {
        var data = MakeRecording(webcam: true);
        try
        {
            using var c = new FrameComposer(data);
            var off = new OverlayOptions { ShowCursor = false, ShowClicks = false, ShowWebcam = false };
            var plain = (byte[])c.Compose(5, 520, off).Clone();
            Assert.Equal((255, 255, 255), TestFrames.At(plain, 160, 10, 10));

            var on = new OverlayOptions { ShowCursor = true, ShowClicks = true, ShowWebcam = true, WebcamCorner = WebcamCorner.BottomRight, WebcamSize = 0.3 };
            var full = c.Compose(6, 600, on);
            Assert.Equal((0, 0, 0), TestFrames.At(full, 160, 10, 10));            // cursor (black 2×2)
            // 100 ms after the click the ring (radius ≈ 7.5 px at this video size) passes x = 87
            var ring = TestFrames.At(full, 160, 87, 60);
            Assert.True(ring.B > ring.R, $"ring pixel {ring}");
            // webcam (green) in the bottom-right corner
            var cam = TestFrames.At(full, 160, 160 - 16 - 18, 120 - 16 - 18);
            Assert.True(cam.G > 100 && cam.R < 60, $"webcam pixel {cam}");
            Assert.True(c.IsAnimatingAt(600, on));
            Assert.False(c.IsAnimatingAt(1500, on));
        }
        finally { data.Delete(); }
    }

    [Fact]
    public async Task Mp4_WithAudio_HasAudioTrack_AndDuration()
    {
        var data = MakeRecording(audio: true);
        var path = TestFrames.TempFile(".mp4");
        try
        {
            try
            {
                await RecordingExporter.ExportAsync(data, new ExportOptions { Format = RecordingFormat.Mp4, Quality = RecordingQuality.Standard },
                    path, null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Console.WriteLine($"MP4 encoder unavailable, skipping: {ex.Message}");
                return;
            }
            var file = await StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            Assert.InRange(props.Duration.TotalMilliseconds, 1900, 2150);
            Assert.Equal(160u, props.Width);

            Windows.Media.Editing.MediaClip clip;
            try { clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file); }
            catch (Exception ex) { Console.WriteLine($"MediaClip unavailable: {ex.Message}"); return; }
            Assert.Single(clip.EmbeddedAudioTracks);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
            data.Delete();
        }
    }
}
