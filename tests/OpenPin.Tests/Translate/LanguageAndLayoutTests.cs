using System.Windows;
using System.Windows.Media;
using OpenPin.Core.Imaging;
using OpenPin.Services;
using OpenPin.Tests.Annotation;
using OpenPin.Translate;

namespace OpenPin.Tests.Translate;

public class LanguageTests
{
    [Theory]
    [InlineData("这是一个简单的测试，用来检查语言。", "zh-Hans")]
    [InlineData("這是一個簡單的測試，用來檢查語言。", "zh-Hant")]
    [InlineData("これは日本語のテストです。", "ja")]
    [InlineData("이것은 한국어 테스트입니다", "ko")]
    [InlineData("Это простой тест на русском языке", "ru")]
    [InlineData("This is a simple test of the detector and it should work.", "en")]
    [InlineData("Ceci est un test simple pour la détection de la langue et des mots.", "fr")]
    [InlineData("Der Hund und die Katze sind nicht im Haus, aber es ist gut.", "de")]
    [InlineData("Đây là một bài kiểm tra đơn giản", "vi")]
    [InlineData("هذا اختبار بسيط", "ar")]
    [InlineData("นี่คือการทดสอบ", "th")]
    [InlineData("使用 Windows 系统设置打开文字识别功能", "zh-Hans")]
    [InlineData("12345 !!!", null)]
    [InlineData("", null)]
    public void Detect(string text, string? expected) => Assert.Equal(expected, TranslateLanguages.Detect(text));

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-Hant-HK", "zh-Hant")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("xx-YY", "zh-Hans")]
    [InlineData("", "zh-Hans")]
    public void FromCulture(string culture, string expected) => Assert.Equal(expected, TranslateLanguages.FromCulture(culture));

    [Fact]
    public void ResolveTarget_FollowsAppWhenEmpty()
    {
        Assert.Equal("en", TranslateLanguages.ResolveTarget("", "en-GB"));
        Assert.Equal("ko", TranslateLanguages.ResolveTarget("ko", "zh-CN"));
        Assert.Equal("zh-Hans", TranslateLanguages.ResolveTarget("not-a-tag", "zh-CN"));
    }

    [Fact]
    public void EffectiveTarget_UsesFallbackOnlyWhenSourceEqualsTarget()
    {
        Assert.Equal("en", TranslateLanguages.EffectiveTarget("你好，世界", "zh-Hans", "en"));
        Assert.Equal("zh-Hans", TranslateLanguages.EffectiveTarget("Hello, this is the world", "zh-Hans", "en"));
        Assert.Equal("ja", TranslateLanguages.EffectiveTarget("Hello, this is the world", "en", "ja"));
        // fallback equal to target: keep the target
        Assert.Equal("en", TranslateLanguages.EffectiveTarget("Hello, this is the world", "en", "en"));
    }

    [Fact]
    public void AllLanguages_HaveProviderCodes()
    {
        Assert.Equal(17, TranslateLanguages.All.Count);
        Assert.All(TranslateLanguages.All, l =>
        {
            Assert.False(string.IsNullOrEmpty(l.Baidu));
            Assert.False(string.IsNullOrEmpty(l.Youdao));
            Assert.False(string.IsNullOrEmpty(l.EnglishName));
        });
    }

    [Theory]
    [InlineData("你好世界", true)]
    [InlineData("OpenPin 截图工具", true)]
    [InlineData("下载 .NET 10 运行库", true)]
    [InlineData("確定", true)]
    [InlineData("這是一個繁體測試", true)]
    [InlineData("Hello World", false)]
    [InlineData("This is an English sentence", false)]
    [InlineData("これはテストです", false)]
    [InlineData("한국어 테스트", false)]
    [InlineData("12345", false)]
    [InlineData("", false)]
    public void IsChineseText_Tests(string text, bool expected) =>
        Assert.Equal(expected, TranslateLanguages.IsChineseText(text));

    [Theory]
    [InlineData("你好世界", "zh-Hans", false)]
    [InlineData("OpenPin 截图工具", "zh-Hans", false)]
    [InlineData("Hello World", "zh-Hans", true)]
    [InlineData("This is an English sentence", "zh-Hans", true)]
    [InlineData("これはテストです", "zh-Hans", true)]
    [InlineData("12345", "zh-Hans", false)]
    [InlineData("Hello World", "en", false)]
    [InlineData("你好世界", "en", true)]
    public void ShouldTranslateSegment_Tests(string text, string target, bool expected) =>
        Assert.Equal(expected, TranslateLanguages.ShouldTranslateSegment(text, target));
}

public class SegmenterTests
{
    private static OcrLine Line(string text, double x, double y, double w, double h = 20) =>
        new(text, new[] { new OcrWord(text, new Rect(x, y, w, h)) }, new Rect(x, y, w, h));

    [Fact]
    public void WrappedLines_MergeIntoParagraph_ShortLastLineEndsIt()
    {
        var lines = new[]
        {
            Line("The quick brown fox jumps over", 10, 10, 400),
            Line("the lazy dog.", 10, 34, 150),
            Line("Another paragraph after a gap", 10, 90, 380),
        };
        var p = TextSegmenter.Paragraphs(lines);
        Assert.Equal(2, p.Count);
        Assert.Equal("The quick brown fox jumps over the lazy dog.", p[0].Text);
        Assert.Equal(new Rect(10, 10, 400, 44), p[0].Bounds);
        Assert.Equal(2, p[0].LineBounds.Count);
        Assert.Equal("Another paragraph after a gap", p[1].Text);
    }

    [Fact]
    public void TwoColumns_StaySeparate()
    {
        var lines = new[]
        {
            Line("Left column first line of text", 0, 0, 300),
            Line("Right column first line text", 400, 0, 300),
            Line("left column second line", 0, 24, 200),
            Line("right column second line", 400, 24, 210),
        };
        var p = TextSegmenter.Paragraphs(lines);
        Assert.Equal(2, p.Count);
        Assert.Equal("Left column first line of text left column second line", p[0].Text);
        Assert.Equal("Right column first line text right column second line", p[1].Text);
    }

    [Fact]
    public void MenuItems_AreNotMerged()
    {
        var lines = new[] { Line("File", 0, 0, 40), Line("Edit", 0, 24, 38), Line("View", 0, 48, 42) };
        Assert.Equal(3, TextSegmenter.Paragraphs(lines).Count);
    }

    [Fact]
    public void ChineseLines_JoinWithoutSpaces()
    {
        var lines = new[] { Line("这是一个很长的段落，第一行文字到这里换行了，后面", 0, 0, 500), Line("还有第二行。", 0, 24, 120) };
        var p = Assert.Single(TextSegmenter.Paragraphs(lines));
        Assert.Equal("这是一个很长的段落，第一行文字到这里换行了，后面还有第二行。", p.Text);
    }
}

public class RendererTests
{
    [Fact]
    public void FitFontSize_FindsLargestFittingSize()
    {
        // Fake measurer: a 100-char text wraps into lines of width 200 → height = lines × size × 1.2
        Size Measure(double s)
        {
            double charW = s * 0.5, perLine = Math.Max(1, Math.Floor(200 / charW));
            double lines = Math.Ceiling(100 / perLine);
            return new Size(Math.Min(200, 100 * charW), lines * s * 1.2);
        }
        double size = ImageTranslationRenderer.FitFontSize(new Size(200, 60), 6, 40, Measure);
        Assert.True(Measure(size).Height <= 60);
        Assert.True(Measure(size + 0.5).Height > 60);
        Assert.Equal(40, ImageTranslationRenderer.FitFontSize(new Size(1000, 1000), 6, 40, Measure));
        Assert.Equal(6, ImageTranslationRenderer.FitFontSize(new Size(10, 5), 6, 40, Measure));
    }

    private static byte[] Solid(int w, int h, Color c)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        return px;
    }

    private static void Fill(byte[] px, int w, Int32Rect r, Color c)
    {
        for (int y = r.Y; y < r.Y + r.Height; y++)
        for (int x = r.X; x < r.X + r.Width; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
        }
    }

    [Fact]
    public void BorderMedian_IgnoresTextTouchingTheBorder()
    {
        int w = 60, h = 40;
        var bg = Color.FromRgb(240, 230, 200);
        var px = Solid(w, h, bg);
        Fill(px, w, new Int32Rect(10, 10, 5, 20), Colors.Black); // a stroke crossing the top border
        Assert.Equal(bg, ImageTranslationRenderer.MedianBorderColor(px, w, h, new Int32Rect(5, 12, 50, 20)));
    }

    [Fact]
    public void TextColor_ComesFromGlyphs_OrFallsBackToContrast()
    {
        int w = 40, h = 20;
        var bg = Colors.White;
        var px = Solid(w, h, bg);
        var red = Color.FromRgb(200, 0, 0);
        Fill(px, w, new Int32Rect(5, 5, 10, 10), red);
        Assert.Equal(red, ImageTranslationRenderer.TextColor(px, w, h, new Int32Rect(0, 0, w, h), bg));

        // Pale text on white does not contrast enough → black.
        var pale = Solid(w, h, bg);
        Fill(pale, w, new Int32Rect(5, 5, 10, 10), Color.FromRgb(200, 200, 200));
        Assert.Equal(Colors.Black, ImageTranslationRenderer.TextColor(pale, w, h, new Int32Rect(0, 0, w, h), bg));

        // Dark background without glyph pixels → white.
        var dark = Solid(w, h, Color.FromRgb(20, 20, 30));
        Assert.Equal(Colors.White, ImageTranslationRenderer.TextColor(dark, w, h, new Int32Rect(0, 0, w, h), Color.FromRgb(20, 20, 30)));
    }

    [Fact]
    public void ContrastRatio_Bounds()
    {
        Assert.Equal(21, ImageTranslationRenderer.ContrastRatio(Colors.Black, Colors.White), 1);
        Assert.Equal(1, ImageTranslationRenderer.ContrastRatio(Colors.Red, Colors.Red), 3);
    }

    [Fact]
    public void Render_PaintsBackgroundAndTextInsideTheBox() => Sta.Run(() =>
    {
        int w = 200, h = 80;
        var bg = Color.FromRgb(250, 250, 250);
        var px = Solid(w, h, bg);
        // "original text": black blocks inside the box
        for (int k = 0; k < 6; k++) Fill(px, w, new Int32Rect(22 + k * 25, 22, 14, 18), Colors.Black);
        var image = BitmapUtil.FromPixels(px, w, h);
        var box = new TranslatedBox(new Rect(20, 20, 160, 22), "你好世界", 22); // fake provider result
        var renderer = new ImageTranslationRenderer();

        var plan = renderer.Plan(px, w, h, box)!;
        Assert.Equal(bg, plan.Background);
        Assert.Equal(Colors.Black, plan.Foreground);
        Assert.InRange(plan.FontSize, 6, 22);

        var result = renderer.Render(image, new[] { box });
        Assert.Equal(w, result.PixelWidth);
        Assert.Equal(h, result.PixelHeight);
        var outPx = BitmapUtil.GetPixels(result);
        // Outside the box: untouched.
        Assert.Equal(bg, BitmapUtil.GetPixel(result, 5, 5));
        // The right part of the box (old black blocks, no new text there) is now background.
        Assert.Equal(bg, BitmapUtil.GetPixel(result, 154, 30));
        Assert.Equal(bg, BitmapUtil.GetPixel(result, 129, 30));
        // Some dark text pixels were drawn inside the box.
        int dark = 0;
        for (int y = 18; y < 44; y++)
        for (int x = 18; x < 182; x++)
        {
            int i = (y * w + x) * 4;
            if (outPx[i] < 100 && outPx[i + 1] < 100 && outPx[i + 2] < 100) dark++;
        }
        Assert.InRange(dark, 10, 160 * 22);
    });

    [Fact]
    public void Plan_SkipsEmptyTextAndOutsideBoxes() => Sta.Run(() =>
    {
        var px = Solid(10, 10, Colors.White);
        var r = new ImageTranslationRenderer();
        Assert.Null(r.Plan(px, 10, 10, new TranslatedBox(new Rect(1, 1, 5, 5), "  ", 5)));
        Assert.Null(r.Plan(px, 10, 10, new TranslatedBox(new Rect(50, 50, 5, 5), "x", 5)));
    });

    [Fact]
    public void Plan_SkipsPreservedBoxes() => Sta.Run(() =>
    {
        var px = Solid(100, 100, Colors.White);
        var r = new ImageTranslationRenderer();
        var box = new TranslatedBox(new Rect(10, 10, 50, 20), "你好世界", 16, OriginalText: "你好世界");
        Assert.Null(r.Plan(px, 100, 100, box));
    });
}
