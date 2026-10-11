using System.Text;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Tests.Services;

public class ClipboardFormatsTests
{
    internal static byte[] TestPixels(int w, int h, bool transparent)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 20); px[i + 1] = (byte)(y * 30); px[i + 2] = (byte)(x + y); px[i + 3] = transparent && x == 0 ? (byte)0 : (byte)255;
            }
        return px;
    }

    [Fact]
    public void Dib24_RoundTrip_FlattensAlphaOnWhite()
    {
        var src = BitmapUtil.FromPixels(TestPixels(5, 3, transparent: true), 5, 3);
        var dib = ClipboardFormats.EncodeDib24(src);
        Assert.Equal(40 + 16 * 3, dib.Length); // stride 15 → 16
        var back = ClipboardFormats.DecodeDib(dib)!;
        Assert.Equal(5, back.PixelWidth);
        Assert.Equal(3, back.PixelHeight);
        var px = BitmapUtil.GetPixels(back);
        var orig = TestPixels(5, 3, false);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 5; x++)
            {
                int i = (y * 5 + x) * 4;
                if (x == 0) Assert.Equal(new byte[] { 255, 255, 255, 255 }, px[i..(i + 4)]);
                else Assert.Equal(orig[i..(i + 4)], px[i..(i + 4)]);
            }
    }

    private static byte[] Dib32(int w, int h, int headerSize, int compression, bool topDown, Func<int, int, uint> pixel, uint aMask = 0)
    {
        var dib = new byte[headerSize + w * h * 4];
        BitConverter.GetBytes(headerSize).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(topDown ? -h : h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(compression).CopyTo(dib, 16);
        if (headerSize >= 56)
        {
            BitConverter.GetBytes(0x00FF0000u).CopyTo(dib, 40);
            BitConverter.GetBytes(0x0000FF00u).CopyTo(dib, 44);
            BitConverter.GetBytes(0x000000FFu).CopyTo(dib, 48);
            BitConverter.GetBytes(aMask).CopyTo(dib, 52);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int row = topDown ? y : h - 1 - y;
                BitConverter.GetBytes(pixel(x, y)).CopyTo(dib, headerSize + (row * w + x) * 4);
            }
        return dib;
    }

    [Fact]
    public void Dib32_BiRgb_ZeroAlpha_IsOpaque()
    {
        var dib = Dib32(2, 2, 40, 0, false, (x, y) => 0x00102030u);
        var px = BitmapUtil.GetPixels(ClipboardFormats.DecodeDib(dib)!);
        Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 255 }, px[0..4]);
    }

    [Fact]
    public void DibV5_WithAlphaMask_KeepsAlpha_TopDown()
    {
        var dib = Dib32(2, 2, 124, 3, true, (x, y) => y == 0 ? 0x80102030u : 0xFF405060u, aMask: 0xFF000000);
        var px = BitmapUtil.GetPixels(ClipboardFormats.DecodeDib(dib)!);
        Assert.Equal(0x80, px[3]);      // row 0 (top) semi transparent
        Assert.Equal(0xFF, px[2 * 4 * 1 + 3]);
        Assert.Equal(0x30, px[0]);
    }

    [Fact]
    public void Dib_Invalid_ReturnsNull()
    {
        Assert.Null(ClipboardFormats.DecodeDib(new byte[10]));
        var dib = Dib32(4, 4, 40, 0, false, (_, _) => 0);
        Assert.Null(ClipboardFormats.DecodeDib(dib[..60])); // truncated pixels
    }

    [Fact]
    public void CfHtml_RoundTrip_WithUtf8()
    {
        var fragment = "<b>你好</b> <i>world</i>";
        var data = ClipboardFormats.CreateCfHtml(fragment);
        var header = Encoding.ASCII.GetString(data, 0, 120);
        Assert.StartsWith("Version:0.9", header);
        Assert.Equal(fragment, ClipboardFormats.ExtractHtmlFragment(data));
    }

    [Fact]
    public void CfHtml_FallsBackToMarkers()
    {
        var s = "Version:0.9\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:-1\r\nEndFragment:-1\r\n<html><body><!--StartFragment--><p>x</p><!--EndFragment--></body></html>\0";
        Assert.Equal("<p>x</p>", ClipboardFormats.ExtractHtmlFragment(Encoding.UTF8.GetBytes(s)));
    }

    [Fact]
    public void HtmlToPlainText() =>
        Assert.Equal("a & b\nc", ClipboardFormats.HtmlToPlainText("<p>a &amp; b</p><style>x{}</style>c"));

    [Fact]
    public void HDrop_ParsesWidePaths()
    {
        var files = new[] { @"C:\a\one.png", @"D:\测试\two.txt" };
        var payload = Encoding.Unicode.GetBytes(string.Join("\0", files) + "\0\0");
        var data = new byte[20 + payload.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 16);
        payload.CopyTo(data, 20);
        Assert.Equal(files, ClipboardFormats.ParseHDrop(data));
    }

    [Theory]
    [InlineData("#29B8DB", true)]
    [InlineData("  rgb(1, 2, 3) ", true)]
    [InlineData("orange", true)]
    [InlineData("#29B8DB\n#FFFFFF", false)]
    [InlineData("hello world", false)]
    [InlineData("", false)]
    public void ColorText(string text, bool isColor) => Assert.Equal(isColor, ClipboardFormats.TryParseColorText(text, out _));

    [Fact]
    public void TempFile_AutoMode_OnlyForTransparentImages()
    {
        var opaque = BitmapUtil.FromPixels(TestPixels(4, 4, false), 4, 4);
        var transparent = BitmapUtil.FromPixels(TestPixels(4, 4, true), 4, 4);
        Assert.Null(ClipboardService.WriteTempFileIfWanted(opaque, "Auto"));
        Assert.Null(ClipboardService.WriteTempFileIfWanted(transparent, "Disabled"));
        var png = ClipboardService.WriteTempFileIfWanted(transparent, "Auto");
        var jpg = ClipboardService.WriteTempFileIfWanted(opaque, "Jpg");
        try
        {
            Assert.EndsWith(".png", png);
            Assert.EndsWith(".jpg", jpg);
            Assert.True(System.IO.File.Exists(png));
            Assert.True(System.IO.File.Exists(jpg));
        }
        finally
        {
            System.IO.File.Delete(png!);
            System.IO.File.Delete(jpg!);
        }
    }
}
