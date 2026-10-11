using System.Windows;
using OpenPin.Pin.Logic;
using OpenPin.Services;

namespace OpenPin.Tests.Pin;

public class PinTextTests
{
    [Fact]
    public void Html_ExtractsClipboardFragment()
    {
        var cf = "Version:0.9\r\nStartHTML:0000000105\r\n<html><body><!--StartFragment--><b>Hi</b> there<!--EndFragment--></body></html>";
        var runs = HtmlLite.Parse(cf);
        Assert.Equal("Hi there", HtmlLite.ToPlainText(runs));
        Assert.True(runs[0].Bold);
        Assert.False(runs[1].Bold);
    }

    [Fact]
    public void Html_BlocksBecomeLines_AndWhitespaceCollapses()
    {
        var runs = HtmlLite.Parse("<p>One   two</p>\n\n<p>Three<br>Four</p><div>  Five </div>");
        Assert.Equal("One two\nThree\nFour\nFive", HtmlLite.ToPlainText(runs));
    }

    [Fact]
    public void Html_ListsHeadingsAndStyles()
    {
        var runs = HtmlLite.Parse("<h1>T</h1><ul><li>a</li><li>b</li></ul><ol><li>x</li></ol><span style=\"color: #ff0000; font-weight:bold\">r</span><a href=\"https://x\">l</a>");
        Assert.Equal("T\n• a\n• b\n1. x\nrl", HtmlLite.ToPlainText(runs));
        Assert.Contains(runs, r => r.Text == "T" && r.SizeScale > 1 && r.Bold);
        Assert.Contains(runs, r => r.Text == "r" && r.Color == "#ff0000" && r.Bold);
        Assert.Contains(runs, r => r.Text == "l" && r.Href == "https://x" && r.Underline);
    }

    [Fact]
    public void Html_SkipsStyleAndScript_DecodesEntities()
    {
        var runs = HtmlLite.Parse("<style>p{color:red}</style><script>alert(1)</script><p>a &amp; b &lt;c&gt;&nbsp;d</p>");
        Assert.Equal("a & b <c> d", HtmlLite.ToPlainText(runs));
    }

    [Fact]
    public void Html_PreKeepsLines()
    {
        var runs = HtmlLite.Parse("<pre>line 1\n  line 2</pre>");
        Assert.Equal("line 1\n  line 2", HtmlLite.ToPlainText(runs));
        Assert.All(runs.Where(r => !r.IsLineBreak), r => Assert.True(r.Monospace));
    }

    [Fact]
    public void Html_ToleratesGarbage()
    {
        Assert.Empty(HtmlLite.Parse(null));
        Assert.StartsWith("x", HtmlLite.ToPlainText(HtmlLite.Parse("</b></i>x<b")));
    }

    private static OcrResult Ocr() => new(new[]
    {
        new OcrLine("Hello world", new[]
        {
            new OcrWord("Hello", new Rect(0, 0, 50, 20)),
            new OcrWord("world", new Rect(60, 0, 50, 20)),
        }, new Rect(0, 0, 110, 20)),
        new OcrLine("你好世界", new[]
        {
            new OcrWord("你好", new Rect(0, 30, 40, 20)),
            new OcrWord("世界", new Rect(42, 30, 40, 20)),
        }, new Rect(0, 30, 82, 20)),
    }, "en-US");

    [Fact]
    public void Ocr_FlattenHitTestAndJoin()
    {
        var words = OcrTextSelection.Flatten(Ocr());
        Assert.Equal(4, words.Count);
        Assert.Equal(1, OcrTextSelection.HitTest(words, new Point(70, 10)));
        Assert.Equal(-1, OcrTextSelection.HitTest(words, new Point(200, 200)));
        Assert.Equal("Hello world\n你好世界", OcrTextSelection.Join(words, 0, 3));
        Assert.Equal("world\n你好", OcrTextSelection.Join(words, 2, 1));
        Assert.Equal("", OcrTextSelection.Join(words, -1, 2));
    }

    [Fact]
    public void Ocr_NearestPrefersSameLine()
    {
        var words = OcrTextSelection.Flatten(Ocr());
        Assert.Equal(1, OcrTextSelection.Nearest(words, new Point(500, 10)));  // right of line 1
        Assert.Equal(2, OcrTextSelection.Nearest(words, new Point(-20, 40)));  // left of line 2
        Assert.Equal(-1, OcrTextSelection.Nearest(Array.Empty<OcrWordRef>(), new Point(0, 0)));
    }

    [Theory]
    [InlineData("abc", "def", true)]
    [InlineData("中文", "def", false)]
    [InlineData("abc", "，", false)]
    [InlineData("1.", "2", true)]
    public void Ocr_SpacingRules(string l, string r, bool space) => Assert.Equal(space, OcrTextSelection.NeedsSpace(l, r));
}
