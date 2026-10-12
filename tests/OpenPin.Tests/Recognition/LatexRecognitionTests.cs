using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;
using OpenPin.Recognition;
using OpenPin.Services;
using OpenPin.Tests.Translate;
using OpenPin.Translate;

namespace OpenPin.Tests.Recognition;

public class LatexRecognitionTests
{
    private static BitmapSource CreateTestBitmap(int w = 50, int h = 50) =>
        BitmapUtil.FromPixels(new byte[w * h * 4], w, h);

    [Fact]
    public void LatexCleaner_StripsMarkdownFencesAndReasoning()
    {
        string raw1 = "```latex\n\\frac{a}{b}\n```";
        Assert.Equal(@"\frac{a}{b}", LatexCleaner.Clean(raw1));

        string raw2 = "<think>Analyzing formula...</think>\n$$E = mc^2$$";
        Assert.Equal("E = mc^2", LatexCleaner.Clean(raw2));

        string raw3 = "Here is the LaTeX formula: \\[\\alpha + \\beta = 1\\]";
        Assert.Equal(@"\alpha + \beta = 1", LatexCleaner.Clean(raw3));

        string raw4 = "Result: $x^2 + y^2 = r^2$";
        Assert.Equal("x^2 + y^2 = r^2", LatexCleaner.Clean(raw4));
    }

    [Fact]
    public void OcrToLatexConverter_ConvertsUnicodeSymbolsAndScripts()
    {
        // Greek letters & math symbols
        string ocr1 = "α + β = γ ± δ ≤ ∞";
        string latex1 = OcrToLatexConverter.Convert(ocr1);
        Assert.Contains(@"\alpha", latex1);
        Assert.Contains(@"\beta", latex1);
        Assert.Contains(@"\gamma", latex1);
        Assert.Contains(@"\pm", latex1);
        Assert.Contains(@"\le", latex1);
        Assert.Contains(@"\infty", latex1);

        // Superscript and subscript digits
        string ocr2 = "x² + y² = z²";
        string latex2 = OcrToLatexConverter.Convert(ocr2);
        Assert.Equal("x^{2} + y^{2} = z^{2}", latex2);

        string ocr3 = "a₁ + a₂";
        string latex3 = OcrToLatexConverter.Convert(ocr3);
        Assert.Equal("a_{1} + a_{2}", latex3);

        // Square roots
        string ocr4 = "√(x + 1)";
        string latex4 = OcrToLatexConverter.Convert(ocr4);
        Assert.Equal(@"\sqrt{x + 1}", latex4);

        // Fractions
        string ocr5 = "(a + b)/(c - d)";
        string latex5 = OcrToLatexConverter.Convert(ocr5);
        Assert.Equal(@"\frac{a + b}{c - d}", latex5);

        // Function names
        string ocr6 = "sin x + cos y = 1";
        string latex6 = OcrToLatexConverter.Convert(ocr6);
        Assert.Contains(@"\sin", latex6);
        Assert.Contains(@"\cos", latex6);
    }

    [Fact]
    public void LatexHelper_DetectsLatexFormulasAccurately()
    {
        Assert.True(LatexHelper.IsLatexFormula(@"\frac{1}{2}"));
        Assert.True(LatexHelper.IsLatexFormula(@"\sqrt{x^2 + y^2}"));
        Assert.True(LatexHelper.IsLatexFormula(@"\sum_{i=1}^n i"));
        Assert.True(LatexHelper.IsLatexFormula(@"\alpha + \beta = 1"));
        Assert.True(LatexHelper.IsLatexFormula(@"$$E = mc^2$$"));
        Assert.True(LatexHelper.IsLatexFormula(@"\[x + y = z\]"));
        Assert.True(LatexHelper.IsLatexFormula(@"x^{2} + y^{2}"));

        // Regular text and paths should not be detected as formulas
        Assert.False(LatexHelper.IsLatexFormula(""));
        Assert.False(LatexHelper.IsLatexFormula("   "));
        Assert.False(LatexHelper.IsLatexFormula("Hello World!"));
        Assert.False(LatexHelper.IsLatexFormula(@"C:\Users\Document\file.txt"));
        Assert.False(LatexHelper.IsLatexFormula(@"D:\Program Files\App\test.exe"));
    }

    [Fact]
    public async Task RecognizeAsync_WithAiVisionMultimodal_ReturnsVisionResult()
    {
        var image = CreateTestBitmap();
        var replyJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = "```latex\n\\frac{a}{b}\n```" } }
            }
        });

        var handler = new FakeHandler((req, body) =>
        {
            Assert.Contains("image_url", body);
            return FakeHandler.Json(replyJson);
        });

        var testSettings = new TranslateSettings
        {
            Provider = "openai",
            OpenAi = new OpenAiTranslateSettings
            {
                BaseUrl = "https://api.deepseek.com/v1",
                Model = "deepseek-vl",
                ApiKeyProtected = SecretProtector.Protect("test-key")
            }
        };

        var result = await LatexRecognitionService.RecognizeAsync(image, testHttpHandler: handler, settings: testSettings);
        Assert.True(result.Success);
        Assert.Equal("ai_vision", result.Source);
        Assert.Equal(@"\frac{a}{b}", result.Latex);
    }

    [Fact]
    public async Task RecognizeAsync_WhenVisionFails_FallsBackToAiChat()
    {
        var image = CreateTestBitmap();
        var chatReplyJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = @"x^2 + y^2 = r^2" } }
            }
        });

        var handler = new FakeHandler((req, body, count) =>
        {
            if (count == 1) // First call: vision request returns 400 Bad Request
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("Vision not supported")
                };
            }
            // Second call: text chat completion succeeds
            return FakeHandler.Json(chatReplyJson);
        });

        var testSettings = new TranslateSettings
        {
            Provider = "openai",
            OpenAi = new OpenAiTranslateSettings
            {
                BaseUrl = "https://api.deepseek.com/v1",
                Model = "deepseek-chat",
                ApiKeyProtected = SecretProtector.Protect("test-key")
            }
        };

        var result = await LatexRecognitionService.RecognizeAsync(image, testHttpHandler: handler, settings: testSettings);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task RecognizeAsync_WhenAiNotConfigured_FallsBackToOcr()
    {
        var image = CreateTestBitmap();
        var testSettings = new TranslateSettings
        {
            Provider = "", // Unconfigured
        };

        var result = await LatexRecognitionService.RecognizeAsync(image, settings: testSettings);
        Assert.NotNull(result);
        Assert.Equal("ocr", result.Source);
    }
}
