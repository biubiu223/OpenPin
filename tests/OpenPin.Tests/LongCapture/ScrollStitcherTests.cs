using System.Diagnostics;
using OpenPin.LongCapture;
using OpenPin.LongCapture.Stitching;
using Xunit.Abstractions;

namespace OpenPin.Tests.LongCapture;

public class ScrollStitcherTests
{
    private readonly ITestOutputHelper _out;
    public ScrollStitcherTests(ITestOutputHelper output) => _out = output;

    private const int W = 360, Band = 330;

    private static ScrollStitcher Run(Img page, IEnumerable<int> positions, Img? header, Img? footer, out List<StitchResult> results,
        Img? sidebar = null, int noise = 0, StitchOptions? options = null, bool autoCrop = false)
    {
        int frameH = (header?.H ?? 0) + Band + (footer?.H ?? 0);
        var st = new ScrollStitcher(page.W, frameH, LongCaptureDirection.Vertical, options ?? new StitchOptions { AutoCrop = autoCrop });
        results = new List<StitchResult>();
        int seed = 1;
        foreach (int pos in positions)
            results.Add(st.Push(SyntheticPage.Frame(page, pos, Band, header, footer, sidebar, noise, seed++).Px));
        return st;
    }

    private void Dump(List<StitchResult> results)
    {
        foreach (var r in results) _out.WriteLine(r.ToString());
    }

    [Fact]
    public void Vertical_HeaderFooter_VariousOffsets_ReconstructsExactly()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 11);
        var header = SyntheticPage.Bar(W, 40, 1);
        var footer = SyntheticPage.Bar(W, 28, 2);
        int[] positions = { 0, 1, 5, 37, 120, 121, 260, 400, 690, 950, 1240, 1243, 1500 };
        var st = Run(page, positions, header, footer, out var results);
        Dump(results);

        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        for (int i = 1; i < positions.Length; i++) Assert.Equal(positions[i] - positions[i - 1], results[i].Offset);

        var (px, w, h) = st.Compose();
        var expected = SyntheticPage.Expected(page, positions[^1] + Band, header, footer);
        Assert.Equal(0, SyntheticPage.MaxDiff(expected, px, w, h));
        Assert.Equal(0, st.GapCount);
    }

    [Fact]
    public void Vertical_NoFixedBars_ReconstructsExactly()
    {
        var page = SyntheticPage.Create(W, 2500, seed: 5);
        int[] positions = { 0, 2, 50, 51, 151, 400, 680, 960, 1240 };
        var st = Run(page, positions, null, null, out var results);
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, positions[^1] + Band), px, w, h));
    }

    [Fact]
    public void Vertical_FastScroll_Up_To_90_Percent()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 23);
        var header = SyntheticPage.Bar(W, 36, 3);
        int step = (int)(Band * 0.88);
        var positions = Enumerable.Range(0, 7).Select(i => i * step).ToArray();
        var st = Run(page, positions, header, null, out var results);
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, positions[^1] + Band, header), px, w, h));
    }

    [Fact]
    public void Vertical_WithNoiseAndRandomAlpha_WithinTolerance()
    {
        var page = SyntheticPage.Create(W, 2400, seed: 99);
        var header = SyntheticPage.Bar(W, 40, 4);
        var footer = SyntheticPage.Bar(W, 24, 5);
        int[] positions = { 0, 3, 90, 91, 250, 500, 760, 1030, 1100 };
        const int noise = 4;
        var st = Run(page, positions, header, footer, out var results, noise: noise);
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        for (int i = 1; i < positions.Length; i++) Assert.Equal(positions[i] - positions[i - 1], results[i].Offset);
        var (px, w, h) = st.Compose();
        int diff = SyntheticPage.MaxDiff(SyntheticPage.Expected(page, positions[^1] + Band, header, footer), px, w, h);
        Assert.InRange(diff, 0, noise);
        for (int i = 3; i < px.Length; i += 4) Assert.Equal(255, px[i]);
    }

    [Fact]
    public void Vertical_StaticSidebar_ContentColumnsReconstructed()
    {
        var page = SyntheticPage.Create(W, 2400, seed: 31);
        var sidebar = SyntheticPage.Create(70, Band, seed: 77); // a fixed, textured left panel
        int[] positions = { 0, 40, 200, 201, 420, 700, 980 };
        var st = Run(page, positions, null, null, out var results, sidebar: sidebar);
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        for (int i = 1; i < positions.Length; i++) Assert.Equal(positions[i] - positions[i - 1], results[i].Offset);
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, positions[^1] + Band), px, w, h, x0: 70));
    }

    [Fact]
    public void Horizontal_ReconstructsExactly()
    {
        // A wide page = a tall page transposed; fixed "header/footer" become fixed left/right columns.
        var tall = SyntheticPage.Create(240, 2600, seed: 42);
        var left = SyntheticPage.Bar(240, 30, 6);
        int[] positions = { 0, 4, 60, 61, 200, 470, 760, 1050 };
        var frames = positions.Select(p => SyntheticPage.Frame(tall, p, Band, left, null).Transposed()).ToList();
        var st = new ScrollStitcher(frames[0].W, frames[0].H, LongCaptureDirection.Horizontal, new StitchOptions { AutoCrop = false });
        var results = frames.Select(f => st.Push(f.Px)).ToList();
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        var (px, w, h) = st.Compose();
        var expected = SyntheticPage.Expected(tall, positions[^1] + Band, left).Transposed();
        Assert.Equal(0, SyntheticPage.MaxDiff(expected, px, w, h));
    }

    [Fact]
    public void BackwardScroll_DoesNotDuplicate()
    {
        var page = SyntheticPage.Create(W, 2400, seed: 8);
        var header = SyntheticPage.Bar(W, 40, 7);
        int[] positions = { 0, 150, 300, 200, 100, 250, 400, 330, 600 };
        var st = Run(page, positions, header, null, out var results);
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        Assert.Equal(-100, results[3].Offset);
        Assert.Equal(StitchStatus.Moved, results[3].Status);
        Assert.Equal(0, results[4].Appended);
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, positions[^1] + Band, header), px, w, h));
    }

    [Fact]
    public void EndingAfterScrollingBack_KeepsEverythingOnce()
    {
        var page = SyntheticPage.Create(W, 2000, seed: 12);
        var footer = SyntheticPage.Bar(W, 30, 9);
        int[] positions = { 0, 200, 450, 300, 120 };
        var st = Run(page, positions, null, footer, out var results);
        Dump(results);
        Assert.All(results, r => Assert.True(r.IsGood, r.ToString()));
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, 450 + Band, null, footer), px, w, h));
    }

    [Fact]
    public void NoMovement_And_LocalChanges_DoNotGrow()
    {
        var page = SyntheticPage.Create(W, 1200, seed: 3);
        var header = SyntheticPage.Bar(W, 40, 1);
        var frame = SyntheticPage.Frame(page, 100, Band, header, null);
        var st = new ScrollStitcher(frame.W, frame.H, LongCaptureDirection.Vertical);
        Assert.Equal(StitchStatus.First, st.Push(frame.Px).Status);
        for (int i = 0; i < 3; i++) Assert.Equal(StitchStatus.Unchanged, st.Push(frame.Px).Status);

        // A blinking caret (2×14 px) inside the content: no movement, nothing appended.
        var caret = frame.Crop(0, 0, frame.W, frame.H);
        caret.Fill(180, 150, 2, 14, 0, 0, 0);
        var r = st.Push(caret.Px);
        Assert.Equal(StitchStatus.NoMovement, r.Status);
        Assert.Equal(frame.H, st.Length);
        Assert.False(st.HasStitched);
    }

    [Fact]
    public void UnrelatedFrame_Fails_AndKeepsLastGoodState()
    {
        var page = SyntheticPage.Create(W, 2400, seed: 15);
        var other = SyntheticPage.Create(W, 800, seed: 1234);
        var header = SyntheticPage.Bar(W, 40, 2);
        var st = new ScrollStitcher(W, 40 + Band, LongCaptureDirection.Vertical);
        Assert.True(st.Push(SyntheticPage.Frame(page, 0, Band, header).Px).IsGood);
        Assert.True(st.Push(SyntheticPage.Frame(page, 120, Band, header).Px).IsGood);
        int length = st.Length;

        var bad = st.Push(SyntheticPage.Frame(other, 200, Band, header).Px);
        Assert.Equal(StitchStatus.Failed, bad.Status);
        Assert.Equal(length, st.Length);

        // Too fast: no overlap at all with the reference → must fail, not guess.
        var tooFar = st.Push(SyntheticPage.Frame(page, 120 + Band + 20, Band, header).Px);
        Assert.Equal(StitchStatus.Failed, tooFar.Status);
        Assert.Equal(length, st.Length);

        // Back to overlapping content: recovers and the result is still exact.
        Assert.True(st.Push(SyntheticPage.Frame(page, 300, Band, header).Px).IsGood);
        Assert.True(st.Push(SyntheticPage.Frame(page, 520, Band, header).Px).IsGood);
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, 520 + Band, header), px, w, h));
    }

    [Fact]
    public void MaxLength_StopsAppending()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 19);
        var footer = SyntheticPage.Bar(W, 20, 3);
        var opt = new StitchOptions { MaxLength = 900, AutoCrop = false };
        int[] positions = { 0, 200, 400, 600, 800, 1000 };
        var st = Run(page, positions, null, footer, out var results, options: opt);
        Dump(results);
        Assert.Contains(results, r => r.Status == StitchStatus.LimitReached);
        Assert.True(st.LimitReached);
        Assert.Equal(900, st.Length);
        var (px, w, h) = st.Compose();
        Assert.Equal(900, h);
        // Content before the footer is still a faithful prefix of the page.
        var expected = SyntheticPage.Expected(page, 900 - footer.H, null, footer);
        Assert.Equal(0, SyntheticPage.MaxDiff(expected, px, w, h));
    }

    [Fact]
    public void Compose_Trims_And_Preview_Fits()
    {
        var page = SyntheticPage.Create(W, 2000, seed: 21);
        int[] positions = { 0, 100, 300, 600 };
        var st = Run(page, positions, null, null, out _);
        int length = st.Length;
        Assert.Equal(600 + Band, length);

        var (px, w, h) = st.Compose(trimStart: 50, trimEnd: 70);
        Assert.Equal(length - 120, h);
        var expected = SyntheticPage.Expected(page, 600 + Band).Crop(0, 50, W, length - 120);
        Assert.Equal(0, SyntheticPage.MaxDiff(expected, px, w, h));

        var (pv, pw, ph) = st.RenderPreview(120, 300, out double scale);
        Assert.True(pw <= 120 && ph <= 300 && pw > 0 && ph > 0);
        Assert.Equal(pw * ph * 4, pv.Length);
        Assert.InRange(scale, 0.01, 1);
        Assert.InRange(st.ViewportStart, 0, length);
    }

    [Theory]
    [InlineData(101, 0, false)]
    [InlineData(202, 0, false)]
    [InlineData(303, 3, false)]
    [InlineData(404, 0, false)]
    [InlineData(505, 3, false)]
    [InlineData(606, 0, false)]
    [InlineData(1017, 3, false)]
    [InlineData(101, 0, true)]
    [InlineData(303, 3, true)]
    [InlineData(707, 0, true)]
    [InlineData(808, 3, true)]
    public void RandomSessions_ReconstructExactly(int seed, int noise, bool autoCrop)
    {
        var rnd = new Random(seed);
        int width = rnd.Next(120, 900);
        var page = SyntheticPage.Create(width, 6000, seed);
        var header = rnd.Next(2) == 0 ? null : SyntheticPage.Bar(width, rnd.Next(20, 70), seed + 1);
        var footer = rnd.Next(2) == 0 ? null : SyntheticPage.Bar(width, rnd.Next(16, 50), seed + 2);
        var positions = new List<int> { 0 };
        int pos = 0, max = 0;
        while (pos < 6000 - Band - 400 && positions.Count < 60)
        {
            int step = rnd.Next(10) switch
            {
                0 => 0,                                      // no movement
                1 => -rnd.Next(1, 120),                      // scroll back a bit
                < 5 => rnd.Next(1, 30),                      // smooth-scroll steps
                _ => rnd.Next(30, (int)(Band * 0.85)),       // wheel notches / fast drags
            };
            pos = Math.Max(0, pos + step);
            positions.Add(pos);
            max = Math.Max(max, pos);
        }
        var st = Run(page, positions, header, footer, out var results, noise: noise, autoCrop: autoCrop);
        _out.WriteLine($"width {width}, header {header?.H}, footer {footer?.H}");
        for (int i = 0; i < results.Count; i++) _out.WriteLine($"{positions[i]}: {results[i]}");
        int failed = results.Count(r => !r.IsGood);
        Assert.True(failed == 0, $"{failed} failed: " + string.Join(", ", results.Where(r => !r.IsGood)));
        var (px, w, h) = st.Compose();
        // Without auto-crop everything ever seen is kept; with auto-crop the result ends at the last visible content.
        int end = (autoCrop ? positions[^1] : max) + Band;
        int diff = SyntheticPage.MaxDiff(SyntheticPage.Expected(page, end, header, footer), px, w, h);
        Assert.InRange(diff, 0, noise);
    }

    // ------------------------------------------------------------------------------------------ auto-crop (PRO-04)

    private static void AssertResult(ScrollStitcher st, Img page, int start, int end, Img? header, Img? footer)
    {
        var (px, w, h) = st.Compose();
        Assert.Equal(0, SyntheticPage.MaxDiff(SyntheticPage.Expected(page, end, header, footer, start), px, w, h));
    }

    private static StitchResult Push(ScrollStitcher st, Img page, int pos, Img? header, Img? footer) =>
        st.Push(SyntheticPage.Frame(page, pos, Band, header, footer).Px);

    [Fact]
    public void AutoCrop_ForwardBackForward()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 51);
        var header = SyntheticPage.Bar(W, 40, 1);
        var footer = SyntheticPage.Bar(W, 26, 2);
        var st = new ScrollStitcher(W, 40 + Band + 26, LongCaptureDirection.Vertical, new StitchOptions { AutoCrop = true });
        foreach (int pos in new[] { 0, 200, 450, 700 }) Assert.True(Push(st, page, pos, header, footer).IsGood);
        AssertResult(st, page, 0, 700 + Band, header, footer);

        // Scroll back: the result now ends at the visible content (header, page[0, 520 + band), footer).
        var back = Push(st, page, 520, header, footer);
        Assert.Equal(StitchStatus.Trimmed, back.Status);
        Assert.Equal(-180, back.Offset);
        AssertResult(st, page, 0, 520 + Band, header, footer);
        Assert.True(Push(st, page, 380, header, footer).IsGood);
        AssertResult(st, page, 0, 380 + Band, header, footer);

        // Forward again: appending resumes from the trimmed end.
        foreach (int pos in new[] { 430, 650, 900 }) Assert.Equal(StitchStatus.Appended, Push(st, page, pos, header, footer).Status);
        AssertResult(st, page, 0, 900 + Band, header, footer);
        Assert.Equal(0, st.GapCount);
    }

    [Fact]
    public void AutoCrop_Off_ScrollingBackKeepsEverything()
    {
        var page = SyntheticPage.Create(W, 2000, seed: 52);
        var st = new ScrollStitcher(W, Band, LongCaptureDirection.Vertical, new StitchOptions { AutoCrop = false });
        foreach (int pos in new[] { 0, 250, 500, 300 }) Assert.True(Push(st, page, pos, null, null).IsGood);
        AssertResult(st, page, 0, 500 + Band, null, null);
    }

    [Fact]
    public void FirstMovementUpward_StitchesUpward_ThenAutoCropTrimsTheTop()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 53);
        var header = SyntheticPage.Bar(W, 36, 3);
        var footer = SyntheticPage.Bar(W, 30, 4);
        var st = new ScrollStitcher(W, 36 + Band + 30, LongCaptureDirection.Vertical, new StitchOptions { AutoCrop = true });
        foreach (int pos in new[] { 1500, 1300, 1050, 800, 799, 560 })
            Assert.True(Push(st, page, pos, header, footer).IsGood);
        Assert.True(st.StitchesBackward);
        AssertResult(st, page, 560, 1500 + Band, header, footer);
        Assert.Equal(0, st.ViewportStart);

        // Moving down is now against the stitch direction: the far end (top) is trimmed.
        Assert.Equal(StitchStatus.Trimmed, Push(st, page, 700, header, footer).Status);
        AssertResult(st, page, 700, 1500 + Band, header, footer);
        // Up again: prepending resumes.
        Assert.True(Push(st, page, 420, header, footer).IsGood);
        AssertResult(st, page, 420, 1500 + Band, header, footer);
    }

    [Fact]
    public void AutoCrop_BackToOneFrame_ReestablishesDirection()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 54);
        var header = SyntheticPage.Bar(W, 40, 5);
        var st = new ScrollStitcher(W, 40 + Band, LongCaptureDirection.Vertical, new StitchOptions { AutoCrop = true });
        foreach (int pos in new[] { 900, 1100, 1300 }) Assert.True(Push(st, page, pos, header, null).IsGood);
        Assert.Equal(StitchStatus.Trimmed, Push(st, page, 1100, header, null).Status);
        Assert.Equal(StitchStatus.Trimmed, Push(st, page, 900, header, null).Status);
        Assert.Equal(40 + Band, st.Length); // exactly one frame again
        Assert.False(st.HasStitched);

        // Direction is free again: going further up now stitches upward.
        foreach (int pos in new[] { 700, 450 }) Assert.True(Push(st, page, pos, header, null).IsGood);
        Assert.True(st.StitchesBackward);
        AssertResult(st, page, 450, 900 + Band, header, null);
    }

    [Fact]
    public void ResetDirection_AfterResume_OppositeMoveBecomesTheNewDirection()
    {
        var page = SyntheticPage.Create(W, 3000, seed: 55);
        var footer = SyntheticPage.Bar(W, 24, 6);
        var st = new ScrollStitcher(W, Band + 24, LongCaptureDirection.Vertical, new StitchOptions { AutoCrop = true });
        foreach (int pos in new[] { 1000, 1200, 1450 }) Assert.True(Push(st, page, pos, null, footer).IsGood);
        AssertResult(st, page, 1000, 1450 + Band, null, footer);

        st.ResetDirection(); // Stop, then Start again
        // First movement after resuming is upward: not a trim, it sets the direction (inside known content: no change).
        Assert.Equal(StitchStatus.Moved, Push(st, page, 1250, null, footer).Status);
        AssertResult(st, page, 1000, 1450 + Band, null, footer);
        // Further up past the old start: prepends.
        Assert.True(Push(st, page, 1000, null, footer).IsGood);
        Assert.Equal(StitchStatus.Appended, Push(st, page, 800, null, footer).Status);
        AssertResult(st, page, 800, 1450 + Band, null, footer);
        // Down is now against the direction: trims the top.
        Assert.Equal(StitchStatus.Trimmed, Push(st, page, 900, null, footer).Status);
        AssertResult(st, page, 900, 1450 + Band, null, footer);
    }

    [Fact]
    public void Transpose_RoundTrips()
    {
        var img = SyntheticPage.Create(37, 53, seed: 2);
        var t = new byte[img.Px.Length];
        ScrollStitcher.Transpose(img.Px, img.W, img.H, t);
        Assert.Equal(img.Transposed().Px, t);
        var back = new byte[t.Length];
        ScrollStitcher.Transpose(t, img.H, img.W, back);
        Assert.Equal(img.Px, back);
    }

    [Fact]
    public void StripStore_AppendsAcrossBlocks()
    {
        var store = new StripStore(1000);
        var lines = new byte[1000 * 4 * 3000];
        new Random(1).NextBytes(lines);
        store.Append(lines, 1500);
        store.Append(lines.AsSpan(1500 * 4000), 1500);
        Assert.Equal(3000, store.Count);
        var l = store.Line(2999).ToArray();
        Assert.Equal(lines[(2999 * 4000)..(2999 * 4000 + 3)], l[..3]);
        Assert.Equal(255, l[3]);
    }

    [Fact]
    public void Performance_4K_Frames()
    {
        // 3840×2160 region (full 4K monitor); measures the per-frame processing cost.
        var page = SyntheticPage.Create(3840, 2160 + 5 * 700, seed: 4);
        var frames = Enumerable.Range(0, 6).Select(i => SyntheticPage.Frame(page, i * 700, 2160).Px).ToList();
        var st = new ScrollStitcher(3840, 2160, LongCaptureDirection.Vertical);
        st.Push(frames[0]);
        var sw = Stopwatch.StartNew();
        for (int i = 1; i < frames.Count; i++)
        {
            var r = st.Push(frames[i]);
            Assert.Equal(StitchStatus.Appended, r.Status);
            Assert.Equal(700, r.Offset);
        }
        double ms = sw.Elapsed.TotalMilliseconds / (frames.Count - 1);
        var unchanged = Stopwatch.StartNew();
        st.Push(frames[^1]);
        _out.WriteLine($"4K: {ms:0.0} ms per moving frame, {unchanged.Elapsed.TotalMilliseconds:0.0} ms per unchanged frame");
        Assert.True(ms < 1000, $"too slow: {ms} ms");
    }
}
