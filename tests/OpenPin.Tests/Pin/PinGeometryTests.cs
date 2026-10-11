using System.Windows;
using OpenPin.Pin;
using OpenPin.Pin.Logic;

namespace OpenPin.Tests.Pin;

public class PinGeometryTests
{
    [Fact]
    public void VisibleRect_At100Percent_IsImagePixelsOneToOne()
    {
        var r = PinGeometry.VisibleRect(100, 200, new Size(640, 480), null, 1);
        Assert.Equal(new Int32Rect(100, 200, 640, 480), r);
    }

    [Fact]
    public void WindowRect_IsVisibleRectInflatedByMargin()
    {
        var vis = new Int32Rect(-1910, 40, 300, 200); // negative virtual-screen coordinates (monitor left of primary)
        Assert.Equal(new Int32Rect(-1920, 30, 320, 220), PinGeometry.Inflate(vis, 10));
    }

    [Fact]
    public void VisibleRect_WithViewport_ShowsOnlyThatPart()
    {
        var r = PinGeometry.VisibleRect(0, 0, new Size(1000, 800), new Rect(100, 50, 200, 100), 2);
        Assert.Equal(new Int32Rect(200, 100, 400, 200), r);
    }

    [Fact]
    public void ZoomAround_KeepsAnchorFixed()
    {
        var origin = new Point(100, 100);
        var anchor = new Point(150, 130); // content point (50,30) at scale 1
        var o = PinGeometry.ZoomAround(origin, 1, 2, anchor);
        // after zoom the same content point must still be under the anchor
        Assert.Equal(anchor.X, o.X + 50 * 2, 6);
        Assert.Equal(anchor.Y, o.Y + 30 * 2, 6);
    }

    [Theory]
    [InlineData(WheelZoomAnchor.TopLeft, 10, 20)]
    [InlineData(WheelZoomAnchor.BottomRight, 110, 70)]
    [InlineData(WheelZoomAnchor.Center, 60, 45)]
    [InlineData(WheelZoomAnchor.Mouse, 33, 44)]
    public void AnchorPoint_FollowsSetting(WheelZoomAnchor anchor, double x, double y)
    {
        var p = PinGeometry.AnchorPoint(anchor, new Int32Rect(10, 20, 100, 50), new Point(33, 44));
        Assert.Equal(new Point(x, y), p);
    }

    [Theory]
    [InlineData(1.0, 10, 1, 1.1)]
    [InlineData(1.0, 10, -1, 0.9)]
    [InlineData(0.33, 10, 1, 0.4)]
    [InlineData(0.33, 10, -1, 0.3)]
    [InlineData(1.5, 25, 1, 1.75)]
    public void StepZoom_SnapsToStep(double zoom, int step, int dir, double expected)
    {
        Assert.Equal(expected, PinGeometry.StepZoom(zoom, step, dir), 6);
    }

    [Fact]
    public void StepZoom_BelowOneStep_KeepsShrinkingButStaysPositive()
    {
        double z = 0.1;
        for (int i = 0; i < 10; i++)
        {
            z = PinGeometry.StepZoom(z, 10, -1);
            Assert.True(z > 0);
        }
        Assert.True(z >= PinGeometry.MinZoom - 1e-9);
    }

    [Fact]
    public void ClampZoom_LimitsVisibleSize()
    {
        Assert.Equal(PinGeometry.MaxVisiblePx / 4000.0, PinGeometry.ClampZoom(20, 4000, 100, 1), 6);
        Assert.Equal(PinGeometry.MinVisiblePx / 10.0, PinGeometry.ClampZoom(0.01, 1000, 10, 1), 6);
        Assert.Equal(1.0, PinGeometry.ClampZoom(1, 640, 480, 1));
    }

    [Fact]
    public void ClampInto_MovesInsideWorkArea()
    {
        var work = new Int32Rect(0, 0, 1920, 1040);
        Assert.Equal(new Int32Rect(1820, 940, 100, 100), PinGeometry.ClampInto(new Int32Rect(1900, 1000, 100, 100), work));
        Assert.Equal(new Int32Rect(0, 0, 100, 100), PinGeometry.ClampInto(new Int32Rect(-50, -50, 100, 100), work));
        // larger than the work area: aligned top-left
        Assert.Equal(new Int32Rect(0, 10, 3000, 100), PinGeometry.ClampInto(new Int32Rect(500, 10, 3000, 100), work));
    }

    [Fact]
    public void BringIntoView_LeavesReachablePinsAlone()
    {
        var works = new[] { new Int32Rect(0, 0, 1920, 1040) };
        Assert.Equal((0, 0), PinGeometry.BringIntoView(new Int32Rect(1800, 900, 400, 400), works));
    }

    [Fact]
    public void BringIntoView_MovesStrandedPinToNearestMonitor()
    {
        var works = new[] { new Int32Rect(0, 0, 1920, 1040), new Int32Rect(1920, 0, 2560, 1400) };
        // pin left behind on a monitor that no longer exists (far right)
        var (dx, dy) = PinGeometry.BringIntoView(new Int32Rect(6000, 200, 300, 200), works);
        var moved = new Int32Rect(6000 + dx, 200 + dy, 300, 200);
        Assert.Equal(new Int32Rect(1920 + 2560 - 300, 200, 300, 200), moved);
    }

    [Fact]
    public void FitZoom_OnlyShrinks()
    {
        var work = new Int32Rect(0, 0, 1000, 1000);
        Assert.Equal(1, PinGeometry.FitZoom(100, 100, work));
        Assert.Equal(0.45, PinGeometry.FitZoom(2000, 1000, work), 6);
    }

    [Fact]
    public void PlaceToolbar_PrefersBelowThenAboveThenInside()
    {
        var work = new Int32Rect(0, 0, 1000, 800);
        var below = PinGeometry.PlaceToolbar(new Int32Rect(100, 100, 400, 300), 300, 40, work, 8);
        Assert.Equal(new Int32Rect(200, 408, 300, 40), below);

        var above = PinGeometry.PlaceToolbar(new Int32Rect(100, 500, 400, 290), 300, 40, work, 8);
        Assert.Equal(new Int32Rect(200, 452, 300, 40), above);

        var inside = PinGeometry.PlaceToolbar(new Int32Rect(0, 0, 1000, 800), 300, 40, work, 8);
        Assert.Equal(752, inside.Y);
        Assert.True(inside.X >= 0 && inside.X + inside.Width <= 1000);
    }

    [Fact]
    public void PlaceToolbar_StaysOnMonitorHorizontally()
    {
        var work = new Int32Rect(0, 0, 1000, 800);
        var r = PinGeometry.PlaceToolbar(new Int32Rect(0, 100, 100, 100), 400, 40, work, 8);
        Assert.Equal(0, r.X);
    }

    [Fact]
    public void HitEdges_DetectsCornersAndSides()
    {
        var vis = new Int32Rect(0, 0, 200, 100);
        Assert.Equal(PinEdges.Left | PinEdges.Top, PinGeometry.HitEdges(vis, new Point(2, 2), 6));
        Assert.Equal(PinEdges.Right, PinGeometry.HitEdges(vis, new Point(197, 50), 6));
        Assert.Equal(PinEdges.Bottom, PinGeometry.HitEdges(vis, new Point(100, 98), 6));
        Assert.Equal(PinEdges.None, PinGeometry.HitEdges(vis, new Point(100, 50), 6));
        Assert.Equal(PinEdges.None, PinGeometry.HitEdges(vis, new Point(300, 50), 6));
    }

    [Fact]
    public void EdgeResize_RightEdgeAnchorsLeft()
    {
        var (ratio, anchor) = PinGeometry.EdgeResize(new Int32Rect(100, 100, 200, 100), PinEdges.Right, 100, 0);
        Assert.Equal(1.5, ratio, 6);
        Assert.Equal(100, anchor.X);
    }

    [Fact]
    public void EdgeResize_TopLeftCornerAnchorsBottomRight()
    {
        var (ratio, anchor) = PinGeometry.EdgeResize(new Int32Rect(100, 100, 200, 100), PinEdges.Left | PinEdges.Top, -20, -50);
        Assert.Equal(1.5, ratio, 6); // max(1.1, 1.5)
        Assert.Equal(new Point(300, 200), anchor);
    }

    [Fact]
    public void ResizeViewport_ClampsToContent()
    {
        var v = PinGeometry.ResizeViewport(new Rect(10, 10, 50, 50), PinEdges.Left | PinEdges.Bottom, -100, 500, new Size(200, 100), 4);
        Assert.Equal(new Rect(0, 10, 60, 90), v);
    }

    [Fact]
    public void ThumbnailViewport_IsSquareAroundFocusInsideContent()
    {
        var v = PinGeometry.ThumbnailViewport(new Size(1000, 600), 1, 160, new Point(990, 10));
        Assert.Equal(new Rect(840, 0, 160, 160), v);
        var small = PinGeometry.ThumbnailViewport(new Size(100, 50), 1, 160, new Point(50, 25));
        Assert.Equal(new Rect(0, 0, 100, 50), small);
    }
}
