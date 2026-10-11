using System.Windows;
using OpenPin.Capture;

namespace OpenPin.Tests.Capture;

public class SelectionMathTests
{
    private static readonly Int32Rect Bounds = new(0, 0, 1000, 800);

    [Fact]
    public void FromPoints_IsInclusiveAndNormalized()
    {
        Assert.Equal(new Int32Rect(10, 20, 91, 81), SelectionMath.FromPoints(10, 20, 100, 100, Bounds));
        Assert.Equal(new Int32Rect(10, 20, 91, 81), SelectionMath.FromPoints(100, 100, 10, 20, Bounds));
        Assert.Equal(new Int32Rect(5, 5, 1, 1), SelectionMath.FromPoints(5, 5, 5, 5, Bounds));
    }

    [Fact]
    public void FromPoints_ClampsToBounds()
    {
        Assert.Equal(new Int32Rect(0, 0, 51, 51), SelectionMath.FromPoints(-20, -30, 50, 50, Bounds));
        Assert.Equal(new Int32Rect(900, 700, 100, 100), SelectionMath.FromPoints(900, 700, 5000, 5000, Bounds));
    }

    [Fact]
    public void FromPoints_SquareUsesLargerSide()
    {
        var r = SelectionMath.FromPoints(100, 100, 200, 130, Bounds, ratio: 1.0);
        Assert.Equal(r.Width, r.Height);
        Assert.Equal(101, r.Width);
        Assert.Equal(100, r.X);

        // Dragging up-left keeps the anchor at the bottom-right.
        var up = SelectionMath.FromPoints(300, 300, 250, 200, Bounds, ratio: 1.0);
        Assert.Equal(new Int32Rect(200, 200, 101, 101), up);
    }

    [Fact]
    public void FromPoints_SquareShrinksToFitBounds()
    {
        var r = SelectionMath.FromPoints(950, 100, 999, 400, Bounds, ratio: 1.0);
        Assert.Equal(50, r.Width);
        Assert.Equal(50, r.Height);
        Assert.True(r.X + r.Width <= Bounds.Width);
    }

    [Fact]
    public void FromPoints_FixedRatio()
    {
        var r = SelectionMath.FromPoints(0, 0, 159, 10, Bounds, ratio: 16.0 / 9);
        Assert.Equal(160, r.Width);
        Assert.Equal(90, r.Height);
    }

    [Theory]
    [InlineData(100, 100, HitKind.TopLeft)]
    [InlineData(300, 100, HitKind.TopRight)]
    [InlineData(100, 300, HitKind.BottomLeft)]
    [InlineData(300, 300, HitKind.BottomRight)]
    [InlineData(200, 100, HitKind.Top)]
    [InlineData(200, 300, HitKind.Bottom)]
    [InlineData(100, 200, HitKind.Left)]
    [InlineData(300, 200, HitKind.Right)]
    [InlineData(150, 101, HitKind.Top)]      // on the edge, not on a handle
    [InlineData(200, 200, HitKind.Inside)]
    [InlineData(50, 50, HitKind.None)]
    [InlineData(320, 200, HitKind.None)]
    public void HitTest(double x, double y, HitKind expected)
    {
        var sel = new Int32Rect(100, 100, 200, 200);
        Assert.Equal(expected, SelectionMath.HitTest(sel, new Point(x, y), handleTolerance: 6, edgeTolerance: 4));
    }

    [Fact]
    public void HitTest_WithoutEdgesOrInside_OnlyHandles()
    {
        var sel = new Int32Rect(100, 100, 200, 200);
        Assert.Equal(HitKind.None, SelectionMath.HitTest(sel, new Point(150, 101), 6, 4, includeEdges: false, includeInside: false));
        Assert.Equal(HitKind.None, SelectionMath.HitTest(sel, new Point(200, 200), 6, 4, includeEdges: false, includeInside: false));
        Assert.Equal(HitKind.BottomRight, SelectionMath.HitTest(sel, new Point(302, 298), 6, 4, includeEdges: false, includeInside: false));
    }

    [Fact]
    public void HitTest_SmallSelection_PrefersCorners()
    {
        var sel = new Int32Rect(100, 100, 10, 10);
        Assert.Equal(HitKind.TopLeft, SelectionMath.HitTest(sel, new Point(101, 101), 6, 4));
        Assert.NotEqual(HitKind.Top, SelectionMath.HitTest(sel, new Point(105, 100), 6, 4));
    }

    [Fact]
    public void Move_StaysInsideBounds()
    {
        var sel = new Int32Rect(10, 10, 100, 50);
        Assert.Equal(new Int32Rect(15, 5, 100, 50), SelectionMath.Move(sel, 5, -5, Bounds));
        Assert.Equal(new Int32Rect(0, 0, 100, 50), SelectionMath.Move(sel, -100, -100, Bounds));
        Assert.Equal(new Int32Rect(900, 750, 100, 50), SelectionMath.Move(sel, 5000, 5000, Bounds));
    }

    [Fact]
    public void Resize_EachHandle()
    {
        var s = new Int32Rect(100, 100, 200, 100);
        Assert.Equal(new Int32Rect(90, 100, 210, 100), SelectionMath.Resize(s, HitKind.Left, -10, 0, Bounds));
        Assert.Equal(new Int32Rect(100, 100, 220, 100), SelectionMath.Resize(s, HitKind.Right, 20, 0, Bounds));
        Assert.Equal(new Int32Rect(100, 95, 200, 105), SelectionMath.Resize(s, HitKind.Top, 0, -5, Bounds));
        Assert.Equal(new Int32Rect(100, 100, 200, 130), SelectionMath.Resize(s, HitKind.Bottom, 7, 30, Bounds));
        Assert.Equal(new Int32Rect(110, 110, 190, 90), SelectionMath.Resize(s, HitKind.TopLeft, 10, 10, Bounds));
        Assert.Equal(new Int32Rect(100, 100, 210, 110), SelectionMath.Resize(s, HitKind.BottomRight, 10, 10, Bounds));
        Assert.Equal(new Int32Rect(105, 100, 200, 100), SelectionMath.Resize(s, HitKind.Inside, 5, 0, Bounds));
    }

    [Fact]
    public void Resize_FlipsPastOppositeEdge()
    {
        var s = new Int32Rect(100, 100, 200, 100);
        // Drag the right edge 250 px to the left: new rect spans 50..100.
        Assert.Equal(new Int32Rect(50, 100, 50, 100), SelectionMath.Resize(s, HitKind.Right, -250, 0, Bounds));
        // Exactly onto the opposite edge keeps 1 px.
        Assert.Equal(1, SelectionMath.Resize(s, HitKind.Right, -200, 0, Bounds).Width);
    }

    [Fact]
    public void Resize_ClampsAndRespectsRatio()
    {
        var s = new Int32Rect(100, 100, 200, 100);
        var big = SelectionMath.Resize(s, HitKind.BottomRight, 5000, 5000, Bounds);
        Assert.Equal(new Int32Rect(100, 100, 900, 700), big);

        var sq = SelectionMath.Resize(s, HitKind.BottomRight, 0, 50, Bounds, ratio: 1.0);
        Assert.Equal(sq.Width, sq.Height);
        Assert.Equal(new Point(100, 100), new Point(sq.X, sq.Y)); // anchored top-left

        var tl = SelectionMath.Resize(s, HitKind.TopLeft, -50, 0, Bounds, ratio: 1.0);
        Assert.Equal(tl.Width, tl.Height);
        Assert.Equal(300, tl.X + tl.Width); // anchored bottom-right
        Assert.Equal(200, tl.Y + tl.Height);
    }

    [Theory]
    [InlineData(Edge.Left, true, 99, 100, 201, 100)]
    [InlineData(Edge.Left, false, 101, 100, 199, 100)]
    [InlineData(Edge.Right, true, 100, 100, 201, 100)]
    [InlineData(Edge.Right, false, 100, 100, 199, 100)]
    [InlineData(Edge.Top, true, 100, 99, 200, 101)]
    [InlineData(Edge.Top, false, 100, 101, 200, 99)]
    [InlineData(Edge.Bottom, true, 100, 100, 200, 101)]
    [InlineData(Edge.Bottom, false, 100, 100, 200, 99)]
    public void AdjustEdge(Edge edge, bool outward, int x, int y, int w, int h)
    {
        var s = new Int32Rect(100, 100, 200, 100);
        Assert.Equal(new Int32Rect(x, y, w, h), SelectionMath.AdjustEdge(s, edge, outward, Bounds));
    }

    [Fact]
    public void AdjustEdge_KeepsMinimumAndBounds()
    {
        var one = new Int32Rect(10, 10, 1, 1);
        Assert.Equal(one, SelectionMath.AdjustEdge(one, Edge.Left, outward: false, Bounds));
        var edge = new Int32Rect(0, 0, 10, 10);
        Assert.Equal(edge, SelectionMath.AdjustEdge(edge, Edge.Left, outward: true, Bounds));
    }

    [Theory]
    [InlineData("16:9", 16.0 / 9)]
    [InlineData("1:1", 1.0)]
    [InlineData("4/3", 4.0 / 3)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("0:5", null)]
    public void ParseRatio(string text, double? expected)
    {
        var r = SelectionMath.ParseRatio(text);
        if (expected == null) Assert.Null(r);
        else Assert.Equal(expected.Value, r!.Value, 6);
    }

    [Fact]
    public void ApplyRatio_KeepsTopLeftAndFits()
    {
        var r = SelectionMath.ApplyRatio(new Int32Rect(100, 100, 400, 400), 16.0 / 9, Bounds);
        Assert.Equal(new Int32Rect(100, 100, 400, 225), r);
        var tall = SelectionMath.ApplyRatio(new Int32Rect(100, 100, 400, 400), 9.0 / 16, Bounds);
        Assert.Equal(400, tall.Height);
        Assert.Equal(225, tall.Width);
    }

    [Fact]
    public void Intersect()
    {
        Assert.Equal(new Int32Rect(50, 50, 50, 50), SelectionMath.Intersect(new Int32Rect(0, 0, 100, 100), new Int32Rect(50, 50, 100, 100)));
        Assert.True(SelectionMath.IsEmpty(SelectionMath.Intersect(new Int32Rect(0, 0, 10, 10), new Int32Rect(20, 20, 5, 5))));
    }
}
