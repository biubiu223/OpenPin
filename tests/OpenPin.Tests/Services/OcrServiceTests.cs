using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Tests.Services;

/// <summary>Integration tests against the real Windows OCR engine; they pass trivially when no OCR language is installed.</summary>
public class OcrServiceTests
{
    private static BitmapSource RenderText(string text, double fontSize, int width, int height, string font = "Segoe UI")
    {
        BitmapSource? result = null;
        var t = new Thread(() =>
        {
            result = BitmapUtil.Render(width, height, dc =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(font), fontSize, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(6, (height - ft.Height) / 2));
            });
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result!;
    }

    private static string? EnglishTag() =>
        OcrService.AvailableLanguages.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task RecognizesEnglishText_BoundsInOriginalPixels()
    {
        var tag = EnglishTag();
        if (tag == null) return; // no English OCR pack on this machine
        var img = RenderText("Hello OpenPin 2024", 13, 220, 26); // small → upscaled internally
        var result = await OcrService.RecognizeAsync(img, tag);
        Assert.Null(result.Error);
        Assert.Contains("OpenPin", result.Text);
        var line = Assert.Single(result.Lines);
        Assert.InRange(line.Bounds.Left, 0, 30);
        Assert.InRange(line.Bounds.Right, 100, 220);
        Assert.InRange(line.Bounds.Top, -2, 13);
        Assert.InRange(line.Bounds.Bottom, 13, 28);
    }

    [Fact]
    public async Task RecognizesChinese_WithoutInsertedSpaces()
    {
        var tag = OcrService.AvailableLanguages.FirstOrDefault(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (tag == null) return;
        var img = RenderText("截图工具测试文字", 16, 200, 30, "Microsoft YaHei");
        var result = await OcrService.RecognizeAsync(img, tag);
        Assert.Null(result.Error);
        Assert.DoesNotContain(" ", result.Text);
        Assert.Contains("截图", result.Text);
    }

    [Fact]
    public async Task BlankImage_ReturnsEmpty_NoThrow()
    {
        var px = new byte[200 * 100 * 4];
        Array.Fill(px, (byte)255);
        var result = await OcrService.RecognizeAsync(BitmapUtil.FromPixels(px, 200, 100));
        Assert.True(result.IsEmpty);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task TinyImage_ReturnsEmpty()
    {
        var result = await OcrService.RecognizeAsync(BitmapUtil.FromPixels(new byte[4 * 2 * 2], 2, 2));
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void AvailableLanguages_ConsistentWithIsAvailable() =>
        Assert.Equal(OcrService.IsAvailable, OcrService.AvailableLanguages.Count > 0);
}
