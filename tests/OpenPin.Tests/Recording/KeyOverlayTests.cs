using OpenPin.Recording.Codec;
using OpenPin.Recording.UI;

namespace OpenPin.Tests.Recording;

public class KeyOverlayTests
{
    [Fact]
    public void PillSprites_HaveRoundedTransparentCorners_AndOpaqueBody()
    {
        TestFrames.Sta(() =>
        {
            var sprites = KeyPillRenderer.Render(new[] { "Ctrl + C", "A" }, 1280, 720);
            var pill = sprites["Ctrl + C"];
            Assert.True(pill.Width > pill.Height && pill.Height > 20, $"{pill.Width}×{pill.Height}");
            Assert.True(sprites["A"].Width < pill.Width);
            Assert.Equal(0, pill.Bgra[3]);                                    // top-left corner: transparent
            int mid = (pill.Height / 2 * pill.Width + 4) * 4;
            Assert.True(pill.Bgra[mid + 3] > 200);                            // body: (almost) opaque
        });
    }

    [Fact]
    public void Composer_DrawsKeyPill_AtBottomCentre_WhenEnabled()
    {
        var w = new FrameStoreWriter(TestFrames.TempFile(".oprec"), 320, 180, 10);
        w.Append(TestFrames.Solid(320, 180, 255, 255, 255), 0, CursorSample.None);
        var video = w.Complete(2000);
        var keys = new[] { new KeyEvent(100, Keystrokes.VK_LCONTROL, true), new KeyEvent(200, 0x43, true), new KeyEvent(300, 0x43, false), new KeyEvent(310, Keystrokes.VK_LCONTROL, false) };
        var data = new RecordingData(video, new InputLog(Array.Empty<ClickEvent>(), null, keys) { HasKeyboard = true });
        try
        {
            var pill = new CursorShape(40, 10, Enumerable.Range(0, 400).SelectMany(_ => new byte[] { 0, 0, 0, 255 }).ToArray(), null, 0, 0);
            using var c = new FrameComposer(data, new Dictionary<string, CursorShape> { ["Ctrl + C"] = pill });
            var on = new OverlayOptions { ShowKeys = true };
            var frame = c.Compose(0, 250, on);
            Assert.Equal((0, 0, 0), TestFrames.At(frame, 320, 160, 180 - 10 - 15 + 5)); // centre of the pill
            frame = c.Compose(0, 250, on with { ShowKeys = false });
            Assert.Equal((255, 255, 255), TestFrames.At(frame, 320, 160, 160));
            frame = c.Compose(0, 250, on with { KeyFilter = KeystrokeFilter.CombosOnly });
            Assert.Equal((0, 0, 0), TestFrames.At(frame, 320, 160, 160));
            frame = c.Compose(0, 5000, on);                                  // after the hold time
            Assert.Equal((255, 255, 255), TestFrames.At(frame, 320, 160, 160));
        }
        finally { data.Delete(); }
    }
}
