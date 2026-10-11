using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Tests.Services;

public class SaveServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openpin-save-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static System.Windows.Media.Imaging.BitmapSource Img(bool transparent = false) =>
        BitmapUtil.FromPixels(ClipboardFormatsTests.TestPixels(7, 5, transparent), 7, 5);

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".bmp")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    public void SaveAndReload(string ext)
    {
        var path = Path.Combine(_dir, "sub", "img" + ext);
        SaveService.SaveToFile(Img(), path);
        var back = BitmapUtil.LoadFile(path);
        Assert.Equal(7, back.PixelWidth);
        Assert.Equal(5, back.PixelHeight);
        Assert.False(File.Exists(path + ".openpin-tmp"));
    }

    [Fact]
    public void Png_KeepsAlpha_Bmp_FlattensOnWhite()
    {
        var png = Path.Combine(_dir, "a.png");
        var bmp = Path.Combine(_dir, "a.bmp");
        SaveService.SaveToFile(Img(true), png);
        SaveService.SaveToFile(Img(true), bmp);
        Assert.Equal(0, BitmapUtil.GetPixels(BitmapUtil.LoadFile(png))[3]);
        var b = BitmapUtil.GetPixels(BitmapUtil.LoadFile(bmp));
        Assert.Equal(new byte[] { 255, 255, 255 }, b[0..3]);
    }

    [Fact]
    public void Jpeg_QualityAffectsSize()
    {
        var big = BitmapUtil.FromPixels(Noise(200, 200), 200, 200);
        var lo = Path.Combine(_dir, "lo.jpg");
        var hi = Path.Combine(_dir, "hi.jpg");
        SaveService.SaveToFile(big, lo, 20);
        SaveService.SaveToFile(big, hi, 100);
        Assert.True(new FileInfo(lo).Length < new FileInfo(hi).Length);
    }

    private static byte[] Noise(int w, int h)
    {
        var r = new Random(1);
        var px = new byte[w * h * 4];
        r.NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pdf_IsStructurallyValid(bool jpeg)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        SaveService.SaveToFile(Img(true), path, 90, jpeg);
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/MediaBox [0 0 5.25 3.75]", text); // 7x5 px at 96 DPI = 5.25 x 3.75 pt
        Assert.Contains(jpeg ? "/DCTDecode" : "/FlateDecode", text);

        // startxref points to the xref table, and every xref entry points to "N 0 obj".
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.Equal("xref", text.Substring(startxref, 4));
        var entries = Regex.Matches(text[startxref..], @"(\d{10}) 00000 n ");
        Assert.Equal(5, entries.Count);
        for (int i = 0; i < entries.Count; i++)
            Assert.Equal($"{i + 1} 0 obj", text.Substring(int.Parse(entries[i].Groups[1].Value), $"{i + 1} 0 obj".Length));

        // Stream /Length matches the actual image data.
        var m = Regex.Match(text, @"/Length (\d+) >>\nstream\n");
        int len = int.Parse(m.Groups[1].Value);
        int start = m.Index + m.Length;
        Assert.Equal("\nendstream", text.Substring(start + len, 10));
        if (!jpeg)
        {
            using var z = new ZLibStream(new MemoryStream(bytes, start, len), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            Assert.Equal(7 * 5 * 3, ms.Length);
            Assert.Equal(255, ms.ToArray()[0]); // transparent first pixel flattened to white
        }
    }

    [Fact]
    public void Encode_ToBytes()
    {
        var png = SaveService.Encode(Img(), ImageFileFormat.Png);
        Assert.Equal(0x89, png[0]);
        var jpg = SaveService.Encode(Img(), ImageFileFormat.Jpg, 80);
        Assert.Equal(0xFF, jpg[0]);
    }

    [Fact]
    public void Cleanup_DeletesOnlyOldImageFiles()
    {
        var root = Path.Combine(_dir, "auto");
        var oldImg = Path.Combine(root, "2020", "old.png");
        var oldTxt = Path.Combine(root, "2020", "notes.txt");
        var newImg = Path.Combine(root, "new.png");
        var emptyDir = Path.Combine(root, "2019");
        Directory.CreateDirectory(Path.GetDirectoryName(oldImg)!);
        Directory.CreateDirectory(Path.Combine(emptyDir, "01"));
        File.WriteAllText(oldImg, "x"); File.WriteAllText(oldTxt, "x"); File.WriteAllText(newImg, "x");
        File.SetLastWriteTime(oldImg, DateTime.Now.AddDays(-10));
        File.SetLastWriteTime(oldTxt, DateTime.Now.AddDays(-10));

        SaveService.CleanupAutoSave(root, 7, force: true);

        Assert.False(File.Exists(oldImg));
        Assert.True(File.Exists(oldTxt));
        Assert.True(File.Exists(newImg));
        Assert.False(Directory.Exists(emptyDir));
        Assert.True(Directory.Exists(root));
    }

    [Theory]
    [InlineData(".png", true)]
    [InlineData(".JPG", true)]
    [InlineData(".pdf", true)]
    [InlineData(".webp", false)]
    [InlineData("", false)]
    public void SupportedExtensions(string ext, bool ok) => Assert.Equal(ok, SaveService.IsSupportedExtension(ext));
}
