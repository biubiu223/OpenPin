using System.Windows.Media;
using OpenPin.Core.Imaging;

namespace OpenPin.Tests.Core;

public class ColorTextTests
{
    [Theory]
    [InlineData("#29B8DB", 0x29, 0xB8, 0xDB)]
    [InlineData("29B8DB", 0x29, 0xB8, 0xDB)]
    [InlineData("#fff", 255, 255, 255)]
    [InlineData("rgb(41, 184, 219)", 41, 184, 219)]
    [InlineData("41, 184, 219", 41, 184, 219)]
    [InlineData("orange", 255, 165, 0)]
    public void ParsesCommonFormats(string text, byte r, byte g, byte b)
    {
        Assert.True(ColorText.TryParse(text, out var c));
        Assert.Equal(Color.FromRgb(r, g, b), Color.FromRgb(c.R, c.G, c.B));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("123456")]          // plain number, not a color
    [InlineData("300, 1, 1")]       // out of range
    [InlineData("")]
    public void RejectsNonColors(string text) => Assert.False(ColorText.TryParse(text, out _));

    [Fact]
    public void Formats()
    {
        var c = Color.FromRgb(41, 184, 219);
        Assert.Equal("#29B8DB", ColorText.Format(c, ColorFormat.Hex));
        Assert.Equal("41, 184, 219", ColorText.Format(c, ColorFormat.Rgb));
        Assert.Equal("rgb(41, 184, 219)", ColorText.FormatCss(c, ColorFormat.Rgb));
    }
}
