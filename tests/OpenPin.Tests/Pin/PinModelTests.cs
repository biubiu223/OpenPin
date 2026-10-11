using OpenPin.Pin.Logic;
using OpenPin.Pin.Model;

namespace OpenPin.Tests.Pin;

public class PinModelTests
{
    [Fact]
    public void Manifest_RoundTripsThroughJson()
    {
        var m = new PinManifest
        {
            Id = "20261012-101010-abcdef",
            Kind = PinKind.Files,
            GroupId = "g123",
            OriginX = -1500,
            OriginY = 300,
            Zoom = 1.5,
            BaseScale = 1.25,
            Opacity = 0.6,
            Viewport = new[] { 10.0, 20, 30, 40 },
            Title = "标题",
            Locked = true,
            Topmost = false,
            Shadow = false,
            ClickThrough = true,
            IsOpen = false,
            ClosedAt = new DateTime(2026, 10, 1, 8, 0, 0),
            Files = new List<string> { @"C:\a.txt", @"D:\b" },
            ContentHash = "files:XYZ",
        };
        m.Adjust.RotateRight();
        m.Adjust.Grayscale = true;
        m.Adjust.Brightness = 20;

        var back = PinManifest.FromJson(m.ToJson());
        Assert.NotNull(back);
        Assert.Equal(m.Id, back!.Id);
        Assert.Equal(PinKind.Files, back.Kind);
        Assert.Equal("g123", back.GroupId);
        Assert.Equal(-1500, back.OriginX);
        Assert.Equal(1.5, back.Zoom);
        Assert.Equal(1.25, back.BaseScale);
        Assert.Equal(0.6, back.Opacity);
        Assert.Equal(new[] { 10.0, 20, 30, 40 }, back.Viewport);
        Assert.True(back.IsThumbnail);
        Assert.Equal("标题", back.Title);
        Assert.True(back.Locked);
        Assert.False(back.Topmost);
        Assert.True(back.ClickThrough);
        Assert.False(back.IsOpen);
        Assert.Equal(m.ClosedAt, back.ClosedAt);
        Assert.Equal(m.Files, back.Files);
        Assert.Equal(90, back.Adjust.Rotation);
        Assert.True(back.Adjust.Grayscale);
        Assert.Equal(20, back.Adjust.Brightness);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"Kind\":\"Image\"}")]          // no id
    [InlineData("{\"Id\":\"x\",\"Kind\":\"Banana\"}")] // unknown enum
    [InlineData("[1,2,3]")]
    public void Manifest_CorruptJsonReturnsNull(string json) => Assert.Null(PinManifest.FromJson(json));

    [Fact]
    public void Manifest_SanitizesDangerousValues()
    {
        var m = PinManifest.FromJson("{\"Id\":\"x\",\"Zoom\":0,\"Opacity\":7,\"BaseScale\":-1,\"Viewport\":[1,2,0,4],\"GroupId\":\"\",\"Adjust\":{\"Rotation\":450,\"Brightness\":900}}");
        Assert.NotNull(m);
        Assert.Equal(1, m!.Zoom);
        Assert.Equal(1, m.Opacity);
        Assert.Equal(1, m.BaseScale);
        Assert.Null(m.Viewport);
        Assert.Equal(PinGroupBook.DefaultGroupId, m.GroupId);
        Assert.Equal(90, m.Adjust.Rotation);
        Assert.Equal(100, m.Adjust.Brightness);
    }

    [Fact]
    public void History_EvictsOldestBeyondLimit()
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0);
        var closed = new (string, DateTime?)[]
        {
            ("a", now.AddMinutes(-5)),
            ("b", now.AddMinutes(-1)),
            ("c", null),
            ("d", now.AddMinutes(-3)),
        };
        var evict = PinHistoryPolicy.SelectEvictions(closed, 2);
        Assert.Equal(new[] { "a", "c" }, evict.OrderBy(x => x).ToArray());
        Assert.Empty(PinHistoryPolicy.SelectEvictions(closed, 10));
        Assert.Equal(4, PinHistoryPolicy.SelectEvictions(closed, 0).Count);
    }

    [Fact]
    public void History_MostRecentOfGroup()
    {
        var now = DateTime.Now;
        var closed = new (string, string, DateTime?)[]
        {
            ("a", "default", now.AddMinutes(-5)),
            ("b", "g2", now),
            ("c", "default", now.AddMinutes(-1)),
        };
        Assert.Equal("c", PinHistoryPolicy.MostRecent(closed, "default"));
        Assert.Equal("b", PinHistoryPolicy.MostRecent(closed, "g2"));
        Assert.Null(PinHistoryPolicy.MostRecent(closed, "nope"));
    }

    [Fact]
    public void Adjustments_ComposeLikeTheOperations()
    {
        // flip then rotate right == rotate left then flip
        var a = new ImageAdjustments();
        a.FlipHorizontal();
        a.RotateRight();
        Assert.Equal(270, a.Rotation);
        Assert.True(a.Mirror);

        // two vertical flips cancel out
        var b = new ImageAdjustments();
        b.FlipVertical();
        b.FlipVertical();
        Assert.True(b.IsIdentity);

        // four right rotations cancel out
        var c = new ImageAdjustments();
        for (int i = 0; i < 4; i++) c.RotateRight();
        Assert.True(c.IsIdentity);

        var d = new ImageAdjustments();
        d.ChangeBrightness(80);
        d.ChangeBrightness(80);
        Assert.Equal(100, d.Brightness);
        d.Reset();
        Assert.True(d.IsIdentity);
    }
}
