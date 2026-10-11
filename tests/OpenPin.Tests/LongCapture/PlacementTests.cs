using System.Windows;
using OpenPin.LongCapture;
using static OpenPin.LongCapture.LongCapturePlacement;

namespace OpenPin.Tests.LongCapture;

public class PlacementTests
{
    private static readonly Int32Rect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Bar_GoesBelow_ThenAbove_ThenInside()
    {
        var region = new Int32Rect(300, 200, 600, 400);
        var below = PlaceBar(region, 500, 40, Work, 4);
        Assert.Equal(new Int32Rect(300, 604, 500, 40), below);

        var nearBottom = new Int32Rect(300, 700, 600, 320);
        var above = PlaceBar(nearBottom, 500, 40, Work, 4);
        Assert.Equal(656, above.Y);
        Assert.False(Intersects(above, nearBottom));

        var full = new Int32Rect(0, 0, 1920, 1040);
        var inside = PlaceBar(full, 500, 40, Work, 4);
        Assert.True(Contains(Work, inside));
    }

    [Fact]
    public void Bar_IsClampedIntoWorkArea()
    {
        var region = new Int32Rect(1700, 100, 200, 300);
        var bar = PlaceBar(region, 600, 40, Work, 4);
        Assert.True(Contains(Work, bar));
        Assert.False(Intersects(bar, region));
    }

    [Fact]
    public void Preview_Vertical_RightThenLeftThenInside()
    {
        var region = new Int32Rect(300, 200, 600, 400);
        var bar = PlaceBar(region, 500, 40, Work, 4);
        var right = PlacePreview(region, bar, 180, 400, Work, 4, vertical: true);
        Assert.Equal(904, right.X);

        var atRight = new Int32Rect(1500, 200, 400, 400);
        var left = PlacePreview(atRight, PlaceBar(atRight, 500, 40, Work, 4), 180, 400, Work, 4, vertical: true);
        Assert.Equal(1500 - 4 - 180, left.X);

        var wide = new Int32Rect(10, 10, 1900, 900);
        var inside = PlacePreview(wide, PlaceBar(wide, 500, 40, Work, 4), 180, 400, Work, 4, vertical: true);
        Assert.True(Contains(Work, inside));
    }

    [Fact]
    public void Preview_Horizontal_AboveElseBelowBar()
    {
        var region = new Int32Rect(300, 400, 900, 300);
        var bar = PlaceBar(region, 500, 40, Work, 4);
        var above = PlacePreview(region, bar, 900, 140, Work, 4, vertical: false);
        Assert.Equal(400 - 4 - 140, above.Y);

        var top = new Int32Rect(300, 20, 900, 300);
        var barTop = PlaceBar(top, 500, 40, Work, 4);
        var below = PlacePreview(top, barTop, 900, 140, Work, 4, vertical: false);
        Assert.True(below.Y >= Bottom(barTop));
        Assert.False(Intersects(below, top));
        Assert.False(Intersects(below, barTop));
    }
}
