using OpenPin.Core.Imaging;
using OpenPin.Services;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace OpenPin.Tests.Services;

public class QrServiceTests
{
    /// <summary>Render a ZXing BitMatrix into a white BGRA canvas at (ox, oy), each module = scale px.</summary>
    private static void Draw(byte[] canvas, int canvasW, BitMatrix m, int ox, int oy, int scale)
    {
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
            {
                if (!m[x, y]) continue;
                for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int i = ((oy + y * scale + dy) * canvasW + ox + x * scale + dx) * 4;
                        canvas[i] = canvas[i + 1] = canvas[i + 2] = 0;
                    }
            }
    }

    private static byte[] White(int w, int h)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        return px;
    }

    private static BitMatrix Qr(string text) =>
        new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 });

    [Fact]
    public void DecodesSingleQr_WithBounds()
    {
        const string text = "https://example.com/openpin?x=1";
        var m = Qr(text);
        int w = 300, h = 260, scale = 5, ox = 60, oy = 40;
        var px = White(w, h);
        Draw(px, w, m, ox, oy, scale);
        var results = QrService.Decode(BitmapUtil.FromPixels(px, w, h));

        var r = Assert.Single(results);
        Assert.Equal(text, r.Text);
        Assert.Equal("QR_CODE", r.Format);
        Assert.True(r.IsUrl);
        int size = m.Width * scale;
        // Bounds approximately cover the symbol.
        Assert.InRange(r.Bounds.X, ox - 15, ox + 25);
        Assert.InRange(r.Bounds.Y, oy - 15, oy + 25);
        Assert.InRange(r.Bounds.Right, ox + size - 25, ox + size + 15);
        Assert.InRange(r.Bounds.Bottom, oy + size - 25, oy + size + 15);
    }

    [Fact]
    public void DecodesMultipleCodes()
    {
        var a = Qr("first code");
        var b = Qr("second code");
        int w = 600, h = 260;
        var px = White(w, h);
        Draw(px, w, a, 40, 40, 6);
        Draw(px, w, b, 340, 60, 5);
        var results = QrService.Decode(BitmapUtil.FromPixels(px, w, h));
        Assert.Equal(new[] { "first code", "second code" }.OrderBy(x => x), results.Select(r => r.Text).OrderBy(x => x));
        Assert.All(results, r => Assert.False(r.IsUrl));
    }

    [Fact]
    public void DecodesCode128()
    {
        var m = new MultiFormatWriter().encode("OPENPIN-12345", BarcodeFormat.CODE_128, 300, 80);
        int w = 400, h = 140;
        var px = White(w, h);
        Draw(px, w, m, 50, 30, 1);
        var r = Assert.Single(QrService.Decode(BitmapUtil.FromPixels(px, w, h)));
        Assert.Equal("OPENPIN-12345", r.Text);
        Assert.Equal("CODE_128", r.Format);
    }

    [Fact]
    public void TransparentBackground_Decodes()
    {
        var m = Qr("transparent");
        int w = 200, h = 200;
        var px = new byte[w * h * 4]; // fully transparent black
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
                if (m[x, y])
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                            px[((30 + y * 4 + dy) * w + 30 + x * 4 + dx) * 4 + 3] = 255; // opaque black modules
        Assert.Equal("transparent", Assert.Single(QrService.Decode(BitmapUtil.FromPixels(px, w, h))).Text);
    }

    [Fact]
    public void NoCode_ReturnsEmpty() => Assert.Empty(QrService.Decode(BitmapUtil.FromPixels(White(100, 100), 100, 100)));

    [Fact]
    public async Task DecodeAsync_Works()
    {
        var m = Qr("async");
        var px = White(200, 200);
        Draw(px, 200, m, 20, 20, 5);
        var results = await QrService.DecodeAsync(BitmapUtil.FromPixels(px, 200, 200));
        Assert.Equal("async", Assert.Single(results).Text);
    }

    [Theory]
    [InlineData("https://a.b/c", true)]
    [InlineData("http://a.b", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/x", false)]
    [InlineData("hello", false)]
    public void IsUrl(string text, bool expected) => Assert.Equal(expected, new QrCodeResult(text, "QR_CODE", default).IsUrl);
}
