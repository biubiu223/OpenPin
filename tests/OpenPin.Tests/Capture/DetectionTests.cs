using System.Windows;
using OpenPin.Capture;
using OpenPin.Core.Screen;

namespace OpenPin.Tests.Capture;

public class DetectionTests
{
    private static readonly List<Int32Rect> Monitors = new() { new(0, 0, 1000, 800), new(1000, 0, 800, 600) };

    private static DetectTarget Win(int x, int y, int w, int h, string cls = "App") =>
        new(new Int32Rect(x, y, w, h), new WindowInfo { Handle = new IntPtr(x * 10000 + y + 1), BoundsPx = new Int32Rect(x, y, w, h), ClassName = cls });

    [Fact]
    public void HitTest_TopmostFirst()
    {
        var top = Win(100, 100, 200, 200);
        var below = Win(50, 50, 500, 500);
        var t = new DetectionTargets(new[] { top, below }, Monitors);
        Assert.Same(top, t.HitTest(new Point(150, 150)));
        Assert.Same(below, t.HitTest(new Point(400, 400)));
    }

    [Fact]
    public void HitTest_DesktopAndEmptyAreaResolveToMonitor()
    {
        var desktop = Win(0, 0, 1800, 800, "Progman");
        var t = new DetectionTargets(new[] { Win(10, 10, 20, 20), desktop }, Monitors);
        var hit = t.HitTest(new Point(1200, 100));
        Assert.NotNull(hit);
        Assert.Null(hit!.Window);
        Assert.Equal(Monitors[1], hit.RectImg);
        Assert.Null(t.HitTest(new Point(1500, 700))); // outside every monitor (gap of the virtual screen)
    }

    [Fact]
    public void Levels_NormalizeAndNavigate()
    {
        var l = new ElementLevels(new[]
        {
            new Int32Rect(0, 0, 100, 100),
            new Int32Rect(0, 0, 100, 100),      // duplicate → dropped
            new Int32Rect(10, 10, 200, 50),     // clipped to parent → (10,10,90,50)
            new Int32Rect(20, 20, 1, 1),        // too small → dropped
            new Int32Rect(20, 20, 10, 10),
        });
        Assert.Equal(new[] { new Int32Rect(0, 0, 100, 100), new Int32Rect(10, 10, 90, 50), new Int32Rect(20, 20, 10, 10) }, l.Rects);
        Assert.Equal(new Int32Rect(20, 20, 10, 10), l.Current);
        Assert.False(l.Child());
        Assert.True(l.Parent());
        Assert.True(l.Parent());
        Assert.False(l.Parent());
        Assert.Equal(new Int32Rect(0, 0, 100, 100), l.Current);
        l.TrySelect(new Int32Rect(10, 10, 90, 50));
        Assert.Equal(1, l.Index);
        Assert.True(l.SameAs(new ElementLevels(l.Rects)));
    }
}
