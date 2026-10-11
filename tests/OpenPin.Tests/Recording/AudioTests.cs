using System.IO;
using OpenPin.Recording.Codec;

namespace OpenPin.Tests.Recording;

public class AudioTests
{
    private static float[] Tone(int frames, float value)
    {
        var s = new float[frames * 2];
        Array.Fill(s, value);
        return s;
    }

    [Fact]
    public void Writer_FillsGaps_WithSilence_AndAlignsToClock()
    {
        var path = TestFrames.TempFile(".pcm");
        var w = new AudioTrackWriter(path);
        w.Append(Tone(4800, 0.5f), 0);        // 0–100 ms
        w.Append(Tone(4800, 0.25f), 500);     // loopback silence gap: 100–500 ms must become zeros
        var track = w.Complete(1000);
        try
        {
            Assert.Equal(48000, track.Frames);
            using var r = track.OpenReader();
            var buf = new float[2];
            r.Read(AudioFormat.MsToFrames(50), buf);
            Assert.Equal(0.5f, buf[0]);
            r.Read(AudioFormat.MsToFrames(300), buf);
            Assert.Equal(0f, buf[0]);
            r.Read(AudioFormat.MsToFrames(550), buf);
            Assert.Equal(0.25f, buf[1]);
            r.Read(AudioFormat.MsToFrames(900), buf);
            Assert.Equal(0f, buf[0]); // padded to the duration
        }
        finally { track.Delete(); }
    }

    [Fact]
    public void Writer_DropsOverlap_BeyondTolerance_AndAppliesGain()
    {
        var w = new AudioTrackWriter(new MemoryStream(), 60, ownsStream: true) { Gain = 0.5f };
        w.Append(Tone(48000, 1f), 0);          // 1 s
        w.Append(Tone(14400, 1f), 800);        // 300 ms packet claiming to start 200 ms before the file end → overlap dropped
        Assert.Equal(48000 + 4800, w.FramesWritten);
        Assert.Equal(0.5f, w.LastPeak, 3);
        w.Append(Tone(480, 1f), 1120);         // 20 ms late: within tolerance, appended directly
        Assert.Equal(48000 + 4800 + 480, w.FramesWritten);
    }

    [Fact]
    public void Converter_ResamplesAndUpmixes()
    {
        var conv = new AudioConverter(44100, 1);
        int total = 0;
        for (int i = 0; i < 10; i++)
        {
            var mono = Enumerable.Repeat(0.3f, 4410).ToArray(); // 100 ms
            var o = conv.Process(mono);
            Assert.True(o.Length % 2 == 0);
            if (o.Length > 0) Assert.Equal(0.3f, o[1], 4);
            total += o.Length / 2;
        }
        Assert.InRange(total, 47995, 48005); // ~1 s at 48 kHz
    }

    [Fact]
    public void Mixer_SumsAndClamps()
    {
        var a = new AudioTrackWriter(TestFrames.TempFile(".pcm"));
        a.Append(Tone(10, 0.75f), 0);
        var ta = a.Complete(AudioFormat.FramesToMs(10));
        var b = new AudioTrackWriter(TestFrames.TempFile(".pcm"));
        b.Append(Tone(10, 0.5f), 0);
        var tb = b.Complete(AudioFormat.FramesToMs(10));
        try
        {
            using var ra = ta.OpenReader();
            using var rb = tb.OpenReader();
            var outp = new short[24];
            AudioMixer.MixToPcm16(new[] { ra, rb }, 0, outp);
            Assert.Equal(short.MaxValue, outp[0]);          // 1.25 clamps
            Assert.Equal(0, outp[22]);                      // frame 11: beyond both tracks
            AudioMixer.MixToPcm16(new[] { rb }, 0, outp);
            Assert.InRange(outp[0], (short)16383, (short)16384);
        }
        finally { ta.Delete(); tb.Delete(); }
    }
}
