using System.Windows;
using OpenPin.Services;

namespace OpenPin.Tests.Services;

public class OcrTextProcessorTests
{
    [Theory]
    [InlineData("你 好 世 界", "你好世界")]
    [InlineData("你 好 ， 世 界 。", "你好，世界。")]
    [InlineData("Hello World", "Hello World")]
    [InlineData("使用 Windows 系 统", "使用 Windows 系统")]
    [InlineData("共 3 个", "共 3 个")]
    [InlineData("こ ん に ち は", "こんにちは")]
    [InlineData("안녕 하세요", "안녕 하세요")] // Korean keeps its word spaces
    [InlineData("  前 后  ", "  前后  ")]
    [InlineData("", "")]
    public void RemoveCjkSpaces(string input, string expected) => Assert.Equal(expected, OcrTextProcessor.RemoveCjkSpaces(input));

    [Theory]
    [InlineData("ＡＢＣ，１２３。", "ABC,123.")]
    [InlineData("“引号”【括号】", "\"引号\"[括号]")]
    [InlineData("你好！", "你好!")]
    [InlineData("a　b", "a b")]
    public void ToHalfWidth(string input, string expected) => Assert.Equal(expected, OcrTextProcessor.ToHalfWidth(input));

    [Theory]
    [InlineData("你好, 世界.", "你好，世界。")]
    [InlineData("价格 3.14 元, 时间 12:30", "价格 3.14 元，时间 12:30")]
    [InlineData("1,000", "1,000")]
    [InlineData("他说\"好\"", "他说“好”")]
    [InlineData("(注意)", "（注意）")]
    [InlineData("www.example.com", "www.example.com")]
    [InlineData("don't", "don't")]
    public void ToFullWidth(string input, string expected) => Assert.Equal(expected, OcrTextProcessor.ToFullWidth(input));

    [Theory]
    [InlineData("你好", "世界", "你好世界")]
    [InlineData("Hello", "World", "Hello World")]
    [InlineData("multi-", "line", "multiline")]
    [InlineData("中文", "English", "中文English")]
    public void JoinPieces(string a, string b, string expected) => Assert.Equal(expected, OcrTextProcessor.JoinPieces(a, b));

    private static (string, Rect) L(string t, double x, double y, double w, double h = 20) => (t, new Rect(x, y, w, h));

    [Fact]
    public void Layout_MultiLine_KeepsLines()
    {
        var lines = new[] { L("first", 0, 0, 300), L("second", 0, 26, 300) };
        Assert.Equal("first" + Environment.NewLine + "second", OcrTextProcessor.Layout(lines, OcrTextLayout.MultiLine));
    }

    [Fact]
    public void Layout_SingleLine_Joins()
    {
        var lines = new[] { L("这是第一行", 0, 0, 300), L("第二行", 0, 26, 300), L("and English", 0, 52, 300) };
        Assert.Equal("这是第一行第二行and English", OcrTextProcessor.Layout(lines, OcrTextLayout.SingleLine));
    }

    [Fact]
    public void Layout_Auto_MergesWrappedParagraph_SplitsAfterGapAndShortLine()
    {
        var lines = new[]
        {
            L("The quick brown fox jumps", 0, 0, 400),
            L("over the lazy dog. End.", 0, 26, 250),   // short line → paragraph ends
            L("Next paragraph starts", 0, 52, 400),
            L("and continues here", 0, 78, 395),
            L("After a big gap", 0, 160, 390),
        };
        var text = OcrTextProcessor.Layout(lines, OcrTextLayout.Auto);
        var nl = Environment.NewLine;
        Assert.Equal($"The quick brown fox jumps over the lazy dog. End.{nl}Next paragraph starts and continues here{nl}After a big gap", text);
    }

    [Fact]
    public void Layout_Auto_ListItemsStayOnOwnLines()
    {
        var lines = new[] { L("1. apples and pears", 0, 0, 400), L("2. oranges", 0, 26, 400) };
        Assert.Equal("1. apples and pears" + Environment.NewLine + "2. oranges", OcrTextProcessor.Layout(lines, OcrTextLayout.Auto));
    }

    [Fact]
    public void Layout_IgnoresBlankLines() => Assert.Equal("", OcrTextProcessor.Layout(new[] { L("  ", 0, 0, 10) }, OcrTextLayout.Auto));

    [Fact]
    public void Format_AppliesPunctuation()
    {
        var lines = new List<OcrLine> { new("你好, 世界", Array.Empty<OcrWord>(), new Rect(0, 0, 100, 20)) };
        Assert.Equal("你好，世界", OcrTextProcessor.Format(lines, OcrTextLayout.Auto, OcrPunctuation.ToFullWidth));
    }
}

public class OcrPlanningTests
{
    [Fact]
    public void SmallImage_IsUpscaled_AndPadded()
    {
        var jobs = OcrService.PlanJobs(300, 30, 10000, upscale: true);
        var j = Assert.Single(jobs);
        Assert.Equal(4, j.Scale);
        // Mapping back from scaled+padded coordinates to original pixels.
        var r = j.MapBack(OcrService.PadPx + 40, OcrService.PadPx + 8, 80, 40);
        Assert.Equal(new Rect(10, 2, 20, 10), r);
    }

    [Fact]
    public void NoUpscale_WhenDisabled() => Assert.Equal(1, Assert.Single(OcrService.PlanJobs(300, 30, 10000, upscale: false)).Scale);

    [Fact]
    public void Upscale_RespectsMaxDimension()
    {
        var j = Assert.Single(OcrService.PlanJobs(2000, 50, 2600, upscale: true));
        Assert.True(2000 * j.Scale + 2 * OcrService.PadPx <= 2600);
        Assert.Equal(1, j.Scale);
    }

    [Fact]
    public void TallImage_IsTiled_WithOverlapAndUniqueOwnership()
    {
        int max = 2600, w = 1000, h = 9000;
        var jobs = OcrService.PlanJobs(w, h, max, upscale: true);
        Assert.True(jobs.Count >= 4);
        Assert.All(jobs, j =>
        {
            Assert.Equal(1, j.Scale);
            Assert.True(j.Source.Height + 2 * OcrService.PadPx <= max);
            Assert.True(j.Source.Y >= 0 && j.Source.Y + j.Source.Height <= h);
        });
        // Every y is owned by exactly one tile, and the owner contains it.
        for (int y = 0; y < h; y += 7)
        {
            var owners = jobs.Where(j => j.Owns(500, y + 0.5)).ToList();
            var owner = Assert.Single(owners);
            Assert.True(y >= owner.Source.Y && y < owner.Source.Y + owner.Source.Height);
        }
    }

    [Fact]
    public void WideAndTall_TilesBothAxes()
    {
        var jobs = OcrService.PlanJobs(6000, 6000, 2600, upscale: false);
        Assert.Equal(9, jobs.Count);
    }
}
