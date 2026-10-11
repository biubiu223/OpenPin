using OpenPin.Recording;
using OpenPin.Recording.Codec;

namespace OpenPin.Tests.Recording;

public class TimelineTests
{
    [Fact]
    public void FrameAt_FindsLastFrameNotAfterTime()
    {
        var t = new double[] { 0, 100, 200, 300 };
        Assert.Equal(0, Timeline.FrameAt(t, -5));
        Assert.Equal(0, Timeline.FrameAt(t, 99.9));
        Assert.Equal(1, Timeline.FrameAt(t, 100));
        Assert.Equal(3, Timeline.FrameAt(t, 10_000));
    }

    [Fact]
    public void GifDelays_AccumulateRounding()
    {
        // 16 fps: 62.5 ms per frame → 6.25 cs; naive rounding would give 6 cs each (4% too fast)
        var starts = Enumerable.Range(0, 16).Select(i => i * 62.5).ToArray();
        var delays = Timeline.GifDelays(starts, 1000);
        Assert.Equal(100, delays.Sum());
        Assert.All(delays, d => Assert.InRange(d, 6, 7));
    }

    [Fact]
    public void GifDelays_HaveMinimumOfTwoCentiseconds()
    {
        var delays = Timeline.GifDelays(new[] { 0.0, 5, 10 }, 12);
        Assert.All(delays, d => Assert.True(d >= 2));
    }

    [Fact]
    public void PlanConstant_Resamples_AndTrims()
    {
        // source: 60 fps-ish frames for 1 s
        var times = Enumerable.Range(0, 60).Select(i => i * 1000.0 / 60).ToArray();
        var plan = Timeline.PlanConstant(times, 250, 750, 10);
        Assert.Equal(5, plan.Length);
        Assert.Equal(15, plan[0]); // frame at 250 ms
        Assert.Equal(new[] { 15, 21, 27, 33, 39 }, plan);
    }

    [Fact]
    public void PlanConstant_RepeatsFrames_InStaticPeriods()
    {
        var times = new double[] { 0, 1000 };
        var plan = Timeline.PlanConstant(times, 0, 2000, 5);
        Assert.Equal(10, plan.Length);
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 }, plan);
    }

    [Fact]
    public void ClampTrim_KeepsOrder_AndMinimumLength()
    {
        Assert.Equal((0.0, 5000.0), Timeline.ClampTrim(-10, 9000, 5000));
        Assert.Equal((1000.0, 2000.0), Timeline.ClampTrim(2000, 1000, 5000));
        var (s, e) = Timeline.ClampTrim(4990, 4995, 5000);
        Assert.Equal(Timeline.MinTrimLengthMs, e - s, 6);
        Assert.Equal(5000, e);
        var (s2, e2) = Timeline.ClampTrim(0, 10, 50); // shorter than the minimum: whole recording
        Assert.Equal((0.0, 50.0), (s2, e2));
    }

    [Fact]
    public void Plan_Presets()
    {
        var gifStd = Timeline.Plan(RecordingFormat.Gif, RecordingQuality.Standard, 30, 1920, 1080);
        Assert.Equal(15, gifStd.Fps);
        Assert.Equal((1920, 1080), (gifStd.Width, gifStd.Height));

        var gifOrig = Timeline.Plan(RecordingFormat.Gif, RecordingQuality.Original, 60, 800, 600);
        Assert.Equal(Timeline.GifMaxFps, gifOrig.Fps);

        var light = Timeline.Plan(RecordingFormat.Gif, RecordingQuality.Light, 30, 1000, 500);
        Assert.Equal(10, light.Fps);
        Assert.Equal((500, 250), (light.Width, light.Height));

        var mp4 = Timeline.Plan(RecordingFormat.Mp4, RecordingQuality.Standard, 60, 1001, 667);
        Assert.Equal(30, mp4.Fps);
        Assert.Equal((1002, 668), (mp4.Width, mp4.Height)); // even
        Assert.InRange(mp4.Bitrate, 1_000_000u, 3_000_000u);
    }

    [Fact]
    public void FormatTime()
    {
        Assert.Equal("00:00", Timeline.FormatTime(0));
        Assert.Equal("01:05", Timeline.FormatTime(65_400));
        Assert.Equal("01:05.4", Timeline.FormatTime(65_400, withTenths: true));
        Assert.Equal("1:00:01", Timeline.FormatTime(3_601_000));
    }
}
