using System.Windows;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Model;

namespace OpenPin.Tests.Annotation;

public class GeometryTests
{
    [Theory]
    [InlineData(100, 3, 100, 0)]      // almost horizontal → horizontal
    [InlineData(100, 90, 95, 95)]     // near diagonal → 45°
    [InlineData(-4, 100, 0, 100)]     // almost vertical → vertical
    [InlineData(-100, -98, -99, -99)] // up-left diagonal
    public void Snap45SnapsToNearestDirection(double x, double y, double ex, double ey)
    {
        var p = Geo.Snap45(new Point(0, 0), new Point(x, y));
        Assert.Equal(ex, p.X, 0);
        Assert.Equal(ey, p.Y, 0);
        // Always on a multiple of 45°.
        double angle = Math.Atan2(p.Y, p.X) * 180 / Math.PI;
        Assert.Equal(0, Math.Abs(Math.IEEERemainder(angle, 45)), 6);
    }

    [Fact]
    public void Snap45KeepsAnchor()
    {
        var p = Geo.Snap45(new Point(10, 10), new Point(60, 12));
        Assert.Equal(new Point(60, 10), p);
    }

    [Fact]
    public void RectFromPointsSquare()
    {
        var r = Geo.RectFromPoints(new Point(10, 10), new Point(40, 20), square: true);
        Assert.Equal(new Rect(10, 10, 30, 30), r);
        r = Geo.RectFromPoints(new Point(10, 10), new Point(0, 30), square: true);
        Assert.Equal(new Rect(-10, 10, 20, 20), r);
        r = Geo.RectFromPoints(new Point(10, 10), new Point(0, 30), square: false);
        Assert.Equal(new Rect(0, 10, 10, 20), r);
    }

    [Fact]
    public void DistanceToSegment()
    {
        Assert.Equal(5, Geo.DistanceToSegment(new Point(5, 5), new Point(0, 0), new Point(10, 0)), 6);
        Assert.Equal(5, Geo.DistanceToSegment(new Point(-3, 4), new Point(0, 0), new Point(10, 0)), 6); // beyond the end
        Assert.Equal(0, Geo.DistanceToSegment(new Point(3, 0), new Point(0, 0), new Point(10, 0)), 6);
        Assert.Equal(5, Geo.DistanceToSegment(new Point(3, 4), new Point(0, 0), new Point(0, 0)), 6); // degenerate
    }

    [Fact]
    public void SegmentIntersection()
    {
        Assert.True(Geo.SegmentsIntersect(new Point(0, 0), new Point(10, 10), new Point(0, 10), new Point(10, 0)));
        Assert.False(Geo.SegmentsIntersect(new Point(0, 0), new Point(10, 0), new Point(0, 5), new Point(10, 5)));
        Assert.Equal(0, Geo.SegmentDistance(new Point(0, 0), new Point(10, 10), new Point(0, 10), new Point(10, 0)));
        Assert.Equal(5, Geo.SegmentDistance(new Point(0, 0), new Point(10, 0), new Point(0, 5), new Point(10, 5)), 6);
    }

    [Fact]
    public void SmoothingKeepsEndpointsAndReducesJitter()
    {
        var pts = new List<Point>();
        for (int i = 0; i <= 20; i++) pts.Add(new Point(i * 5, i % 2 == 0 ? 0 : 4)); // zig-zag
        var s = Geo.Smooth(pts);
        Assert.Equal(pts[0], s[0]);
        Assert.Equal(pts[^1], s[^1]);
        // Interior amplitude must shrink.
        double maxDev = s.Skip(2).Take(s.Count - 4).Max(p => Math.Abs(p.Y - 2));
        Assert.True(maxDev < 1.5, $"maxDev {maxDev}");
    }

    [Fact]
    public void ChaikinDoublesSegments()
    {
        var res = Geo.Chaikin(new[] { new Point(0, 0), new Point(4, 0), new Point(4, 4) });
        Assert.Equal(6, res.Count);
        Assert.Equal(new Point(0, 0), res[0]);
        Assert.Equal(new Point(4, 4), res[^1]);
        Assert.Equal(new Point(1, 0), res[1]);
        Assert.Equal(new Point(3, 0), res[2]);
    }

    [Fact]
    public void ConvexHullOfSquareWithInteriorPoint()
    {
        var hull = Geo.ConvexHull(new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10), new Point(5, 5) });
        Assert.Equal(4, hull.Count);
        Assert.DoesNotContain(new Point(5, 5), hull);
    }

    [Fact]
    public void EllipseHitHelpers()
    {
        var r = new Rect(0, 0, 100, 50);
        Assert.True(Geo.InsideEllipse(new Point(50, 25), r));
        Assert.False(Geo.InsideEllipse(new Point(2, 2), r));
        Assert.Equal(0, Geo.DistanceToEllipse(new Point(100, 25), r), 6);
        Assert.Equal(10, Geo.DistanceToEllipse(new Point(110, 25), r), 6);
    }

    [Fact]
    public void RotateAndRotatedBounds()
    {
        var p = Geo.Rotate(new Point(10, 0), new Point(0, 0), 90);
        Assert.Equal(0, p.X, 6);
        Assert.Equal(10, p.Y, 6);
        var b = Geo.RotatedBounds(new Rect(0, 0, 20, 10), 90);
        Assert.Equal(new Rect(5, -5, 10, 20).ToString(), new Rect(Math.Round(b.X, 6), Math.Round(b.Y, 6), Math.Round(b.Width, 6), Math.Round(b.Height, 6)).ToString());
    }

    [Fact]
    public void PixelRectHelpers()
    {
        Assert.Equal(new Int32Rect(1, 2, 4, 4), Geo.ToPixelRect(new Rect(1.5, 2.2, 3, 3.5), 100, 100));
        Assert.Equal(new Int32Rect(0, 0, 10, 10), Geo.ToPixelRect(new Rect(-5, -5, 100, 100), 10, 10));
        Assert.True(Geo.Contains(new Int32Rect(0, 0, 10, 10), new Int32Rect(2, 2, 8, 8)));
        Assert.False(Geo.Contains(new Int32Rect(0, 0, 10, 10), new Int32Rect(2, 2, 9, 8)));
        Assert.Equal(new Int32Rect(0, 0, 20, 15), Geo.Union(new Int32Rect(0, 0, 10, 10), new Int32Rect(5, 5, 15, 10)));
    }
}

public class SerialFormatTests
{
    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(40, "XL")]
    [InlineData(1994, "MCMXCIV")]
    [InlineData(3999, "MMMCMXCIX")]
    [InlineData(0, "0")]
    [InlineData(4000, "4000")]
    public void Roman(int value, string expected) => Assert.Equal(expected, SerialFormat.ToRoman(value));

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(52, "AZ")]
    [InlineData(53, "BA")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    public void Letters(int value, string expected) => Assert.Equal(expected, SerialFormat.ToLetters(value));

    [Fact]
    public void FormatByType()
    {
        Assert.Equal("12", SerialFormat.Format(12, SerialType.Number));
        Assert.Equal("L", SerialFormat.Format(12, SerialType.Letter));
        Assert.Equal("XII", SerialFormat.Format(12, SerialType.Roman));
    }
}
