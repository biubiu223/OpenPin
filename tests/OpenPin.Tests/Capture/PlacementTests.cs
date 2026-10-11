using System.Windows;
using OpenPin.Capture;

namespace OpenPin.Tests.Capture;

public class PlacementTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);
    private static readonly Size Toolbar = new(400, 40);

    [Fact]
    public void Toolbar_BelowRightAligned()
    {
        var (pos, side) = Placement.PlaceToolbar(new Rect(500, 200, 600, 300), Toolbar, Monitor, 8);
        Assert.Equal(ToolbarSide.Below, side);
        Assert.Equal(new Point(700, 508), pos);
    }

    [Fact]
    public void Toolbar_AboveWhenNoRoomBelow()
    {
        var (pos, side) = Placement.PlaceToolbar(new Rect(500, 600, 600, 460), Toolbar, Monitor, 8);
        Assert.Equal(ToolbarSide.Above, side);
        Assert.Equal(600 - 8 - 40, pos.Y);
    }

    [Fact]
    public void Toolbar_InsideWhenSelectionFillsMonitor()
    {
        var (pos, side) = Placement.PlaceToolbar(Monitor, Toolbar, Monitor, 8);
        Assert.Equal(ToolbarSide.Inside, side);
        Assert.True(pos.Y + Toolbar.Height <= Monitor.Bottom);
        Assert.True(pos.Y >= Monitor.Top);
        Assert.Equal(Monitor.Right - Toolbar.Width, pos.X);
    }

    [Fact]
    public void Toolbar_ClampedHorizontally()
    {
        var (pos, _) = Placement.PlaceToolbar(new Rect(10, 100, 50, 50), Toolbar, Monitor, 8);
        Assert.Equal(0, pos.X); // right-aligned would be negative
        var (pos2, _) = Placement.PlaceToolbar(new Rect(1900, 100, 100, 50), Toolbar, Monitor, 8);
        Assert.Equal(1920 - 400, pos2.X);
    }

    [Fact]
    public void Toolbar_StaysOnSecondMonitor()
    {
        var second = new Rect(1920, -200, 1280, 1024);
        var (pos, _) = Placement.PlaceToolbar(new Rect(2000, 700, 300, 100), Toolbar, second, 8);
        Assert.True(pos.X >= second.Left && pos.X + Toolbar.Width <= second.Right);
        Assert.True(pos.Y >= second.Top && pos.Y + Toolbar.Height <= second.Bottom);
    }

    [Fact]
    public void NearCursor_FlipsAtEdges()
    {
        var size = new Size(150, 200);
        Assert.Equal(new Point(120, 120), Placement.PlaceNearCursor(new Point(100, 100), size, Monitor, 20));
        var p = Placement.PlaceNearCursor(new Point(1900, 1070), size, Monitor, 20);
        Assert.Equal(new Point(1900 - 20 - 150, 1070 - 20 - 200), p);
    }

    [Fact]
    public void Label_AboveOrInside()
    {
        var size = new Size(80, 20);
        Assert.Equal(new Point(100, 100 - 4 - 20), Placement.PlaceLabel(new Rect(100, 100, 300, 300), size, Monitor, 4));
        Assert.Equal(new Point(100, 4), Placement.PlaceLabel(new Rect(100, 0, 300, 300), size, Monitor, 4));
    }

    [Fact]
    public void Hints_LeftThenRightThenHidden()
    {
        var size = new Size(300, 150);
        Assert.Equal(new Point(16, 1080 - 16 - 150), Placement.PlaceHints(size, Monitor, 16, null, null));
        var blockLeft = new Rect(0, 900, 400, 180);
        Assert.Equal(new Point(1920 - 16 - 300, 1080 - 16 - 150), Placement.PlaceHints(size, Monitor, 16, blockLeft, null));
        var blockRight = new Rect(1500, 900, 420, 180);
        Assert.Null(Placement.PlaceHints(size, Monitor, 16, blockLeft, blockRight));
    }

    [Fact]
    public void PickMonitor_LargestOverlap()
    {
        var monitors = new List<Rect> { new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080) };
        Assert.Equal(1, Placement.PickMonitor(monitors, new Rect(1800, 100, 400, 100)));
        Assert.Equal(0, Placement.PickMonitor(monitors, new Rect(1700, 100, 300, 100)));
        Assert.Equal(1, Placement.MonitorAt(monitors, new Point(5000, 500))); // nearest
        Assert.Equal(0, Placement.MonitorAt(monitors, new Point(10, 10)));
    }
}
