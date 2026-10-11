using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Tests.Services;

/// <summary>PRO-08: PDF page layout (paper, orientation, margins, fit / actual size, long-image pagination) and the multi-page writer.</summary>
public class PdfExportTests : IDisposable
{
    private const double Mm = 72 / 25.4;
    private const double A4W = 595.276, A4H = 841.89;
    private readonly string _dir = Directory.CreateTempSubdirectory("openpin-pdf-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void Near(double expected, double actual, double tol = 0.01) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    // ------------------------------------------------------------------ layout

    [Fact]
    public void Original_DefaultIsOnePageTheSizeOfTheImage()
    {
        var p = Assert.Single(PdfLayout.Plan(800, 600, new PdfOptions()));
        Near(600, p.PageWidth); Near(450, p.PageHeight);
        Assert.Equal(new Int32Rect(0, 0, 800, 600), p.Source);
        Assert.Equal(new Rect(0, 0, 600, 450), p.Placement);
    }

    [Fact]
    public void Original_WithMarginsGrowsThePage()
    {
        var p = Assert.Single(PdfLayout.Plan(800, 600, new PdfOptions { MarginMm = 10 }));
        Near(600 + 20 * Mm, p.PageWidth); Near(450 + 20 * Mm, p.PageHeight);
        Near(10 * Mm, p.Placement.X); Near(10 * Mm, p.Placement.Y);
        Near(600, p.Placement.Width); Near(450, p.Placement.Height);
    }

    [Fact]
    public void Original_VeryTallImageIsSplitAtTheMaximumPageSize()
    {
        var pages = PdfLayout.Plan(1000, 40000, new PdfOptions());
        Assert.Equal(3, pages.Count); // 40000 px * 0.75 = 30000 pt > 14400 pt
        Assert.All(pages, p => Assert.True(p.PageHeight <= PdfLayout.MaxPagePt + 0.01));
        Assert.Equal(40000, pages.Sum(p => p.Source.Height));
        Assert.Equal(19200, pages[0].Source.Height); // 14400 pt / 0.75
        // Without pagination it is shrunk onto one page instead.
        var one = Assert.Single(PdfLayout.Plan(1000, 40000, new PdfOptions { AutoPaginate = false }));
        Near(PdfLayout.MaxPagePt, one.PageHeight);
    }

    [Fact]
    public void A4_AutoOrientationFitsAndCentres()
    {
        // Wide image → landscape A4.
        var p = Assert.Single(PdfLayout.Plan(1600, 900, new PdfOptions { Paper = PdfPaperSize.A4, MarginMm = 10 }));
        Near(A4H, p.PageWidth); Near(A4W, p.PageHeight);
        double m = 10 * Mm, cw = A4H - 2 * m, ch = A4W - 2 * m;
        double s = Math.Min(cw / 1200, ch / 675);
        Near(1200 * s, p.Placement.Width); Near(675 * s, p.Placement.Height);
        Near(m + (cw - p.Placement.Width) / 2, p.Placement.X);
        Near(m + (ch - p.Placement.Height) / 2, p.Placement.Y);
        // Explicit portrait for the same image.
        var q = Assert.Single(PdfLayout.Plan(1600, 900, new PdfOptions { Paper = PdfPaperSize.A4, Orientation = PdfOrientation.Portrait, MarginMm = 10 }));
        Near(A4W, q.PageWidth); Near(A4H, q.PageHeight);
        Near(A4W - 2 * m, q.Placement.Width);
    }

    [Fact]
    public void Letter_LandscapeAndPortrait()
    {
        var l = Assert.Single(PdfLayout.Plan(100, 100, new PdfOptions { Paper = PdfPaperSize.Letter, Orientation = PdfOrientation.Landscape }));
        Near(792, l.PageWidth); Near(612, l.PageHeight);
        var p = Assert.Single(PdfLayout.Plan(100, 100, new PdfOptions { Paper = PdfPaperSize.Letter }));
        Near(612, p.PageWidth); Near(792, p.PageHeight); // square → portrait
    }

    [Fact]
    public void ActualSize_KeepsSmallImagesAt96DpiAndShrinksWideOnes()
    {
        var small = Assert.Single(PdfLayout.Plan(200, 100, new PdfOptions { Paper = PdfPaperSize.A4, FitToPage = false, Orientation = PdfOrientation.Portrait }));
        Near(150, small.Placement.Width); Near(75, small.Placement.Height);
        Near((A4W - 150) / 2, small.Placement.X); // centred, no margin
        var wide = Assert.Single(PdfLayout.Plan(3000, 500, new PdfOptions { Paper = PdfPaperSize.A4, FitToPage = false, Orientation = PdfOrientation.Portrait, MarginMm = 5 }));
        Near(A4W - 10 * Mm, wide.Placement.Width);
    }

    [Fact]
    public void LongImage_IsPaginatedTopAligned()
    {
        var o = new PdfOptions { Paper = PdfPaperSize.A4, MarginMm = 10 };
        var pages = PdfLayout.Plan(1000, 6000, o);
        double m = 10 * Mm, cw = A4W - 2 * m, ch = A4H - 2 * m;
        double s = cw / 750; // fit width
        int slice = (int)Math.Floor(ch / (0.75 * s));
        Assert.Equal((int)Math.Ceiling(6000.0 / slice), pages.Count);
        Assert.Equal(6000, pages.Sum(p => p.Source.Height));
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            Near(A4W, p.PageWidth); Near(A4H, p.PageHeight);
            Assert.Equal(i * slice, p.Source.Y);
            Near(cw, p.Placement.Width);
            Near(A4H - m, p.Placement.Y + p.Placement.Height); // top-aligned
            Near(p.Source.Height * 0.75 * s, p.Placement.Height);
            Assert.Equal(new Rect(p.Placement.X, A4H - p.Placement.Y - p.Placement.Height, p.Placement.Width, p.Placement.Height), p.PlacementTopLeft);
        }
        Assert.True(pages[^1].Source.Height < slice);
    }

    [Fact]
    public void LongImage_OverlapRepeatsRowsOnTheNextPage()
    {
        var o = new PdfOptions { Paper = PdfPaperSize.A4, MarginMm = 10, OverlapMm = 5 };
        var pages = PdfLayout.Plan(1000, 6000, o);
        double s = (A4W - 20 * Mm) / 750;
        int overlap = (int)Math.Round(5 * Mm / (0.75 * s));
        Assert.True(overlap > 0);
        for (int i = 1; i < pages.Count; i++)
            Assert.Equal(pages[i - 1].Source.Y + pages[i - 1].Source.Height - overlap, pages[i].Source.Y);
        Assert.Equal(6000, pages[^1].Source.Y + pages[^1].Source.Height);
    }

    [Fact]
    public void SlightlyTallImage_FitsOnOnePageAndPaginationCanBeTurnedOff()
    {
        // 1.2 pages tall at full width: below the long-image threshold → one page, shrunk.
        var one = Assert.Single(PdfLayout.Plan(1000, 1700, new PdfOptions { Paper = PdfPaperSize.A4 }));
        Near(A4H, one.Placement.Height);
        Assert.Single(PdfLayout.Plan(1000, 9000, new PdfOptions { Paper = PdfPaperSize.A4, AutoPaginate = false }));
        // Actual size paginates as soon as the page is full.
        Assert.Equal(2, PdfLayout.Plan(600, 1200, new PdfOptions { Paper = PdfPaperSize.A4, FitToPage = false }).Count);
    }

    [Fact]
    public void Slices_CoverEveryRowOnce()
    {
        var s = PdfLayout.Slices(1000, 300, 0).ToList();
        Assert.Equal(new[] { (0, 300), (300, 300), (600, 300), (900, 100) }, s);
        var o = PdfLayout.Slices(1000, 300, 50).ToList();
        Assert.Equal(new[] { (0, 300), (250, 300), (500, 300), (750, 250) }, o);
        Assert.Equal(new[] { (0, 10) }, PdfLayout.Slices(10, 300, 0).ToList());
    }

    // ------------------------------------------------------------------ writer

    private sealed record ParsedPdf(string Text, byte[] Bytes, int PageCount, List<double[]> MediaBoxes, List<(int W, int H, string Filter, byte[] Data)> Images);

    /// <summary>Parses our own output: xref ↔ object offsets, page tree, page MediaBoxes and image XObjects (by stream /Length).</summary>
    private static ParsedPdf Parse(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value);
        Assert.Equal("xref", text.Substring(startxref, 4));
        int size = int.Parse(Regex.Match(text, @"trailer\n<< /Size (\d+) /Root 1 0 R >>").Groups[1].Value);
        var entries = Regex.Matches(text[startxref..], @"(\d{10}) 00000 n ");
        Assert.Equal(size - 1, entries.Count);
        for (int i = 0; i < entries.Count; i++)
            Assert.Equal($"{i + 1} 0 obj", text.Substring(int.Parse(entries[i].Groups[1].Value), $"{i + 1} 0 obj".Length));

        var count = int.Parse(Regex.Match(text, @"/Type /Pages /Kids \[([^\]]*)\] /Count (\d+)").Groups[2].Value);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value.Split(" 0 R", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(count, kids.Length);
        var boxes = Regex.Matches(text, @"/Type /Page /Parent 2 0 R /MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")
            .Select(m => new[] { double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) })
            .ToList();
        var images = new List<(int, int, string, byte[])>();
        foreach (Match m in Regex.Matches(text, @"/Subtype /Image /Width (\d+) /Height (\d+) /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter (/\w+) /Length (\d+) >>\nstream\n"))
        {
            int len = int.Parse(m.Groups[4].Value), start = m.Index + m.Length;
            Assert.Equal("\nendstream", text.Substring(start + len, 10));
            images.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value, bytes[start..(start + len)]));
        }
        // Every page draws its own image.
        Assert.Equal(count, Regex.Matches(text, @"/Im0 Do Q").Count);
        return new ParsedPdf(text, bytes, count, boxes, images);
    }

    private static System.Windows.Media.Imaging.BitmapSource Striped(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = (byte)(y % 256); px[o + 1] = (byte)(x % 256); px[o + 2] = (byte)((y / 100) * 20); px[o + 3] = 255;
        }
        return BitmapUtil.FromPixels(px, w, h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiPagePdf_Structure(bool jpeg)
    {
        var img = Striped(400, 2600);
        var o = new PdfOptions { Paper = PdfPaperSize.A4, MarginMm = 10, OverlapMm = 3, UseJpeg = jpeg, JpegQuality = 80 };
        var plan = PdfLayout.Plan(400, 2600, o);
        Assert.True(plan.Count >= 3, $"{plan.Count} pages");

        var path = Path.Combine(_dir, "long.pdf");
        SaveService.SavePdf(img, path, o);
        var pdf = Parse(File.ReadAllBytes(path));

        Assert.Equal(plan.Count, pdf.PageCount);
        Assert.Equal(plan.Count, pdf.MediaBoxes.Count);
        Assert.Equal(plan.Count, pdf.Images.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            Near(Math.Round(plan[i].PageWidth, 3), pdf.MediaBoxes[i][0], 0.001);
            Near(Math.Round(plan[i].PageHeight, 3), pdf.MediaBoxes[i][1], 0.001);
            Assert.Equal(plan[i].Source.Width, pdf.Images[i].W);
            Assert.Equal(plan[i].Source.Height, pdf.Images[i].H);
            Assert.Equal(jpeg ? "/DCTDecode" : "/FlateDecode", pdf.Images[i].Filter);
            if (jpeg)
            {
                Assert.Equal(0xFF, pdf.Images[i].Data[0]);
                Assert.Equal(0xD8, pdf.Images[i].Data[1]);
            }
            else
            {
                using var z = new ZLibStream(new MemoryStream(pdf.Images[i].Data), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                var rgb = ms.ToArray();
                Assert.Equal(pdf.Images[i].W * pdf.Images[i].H * 3, rgb.Length);
                // First row of the slice is image row Source.Y (blue channel = y % 256 → RGB byte 2).
                Assert.Equal((byte)(plan[i].Source.Y % 256), rgb[2]);
            }
        }
        // Content stream of page 1 places the image where the plan says.
        var first = plan[0].Placement;
        string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"q {Num(first.Width)} 0 0 {Num(first.Height)} {Num(first.X)} {Num(first.Y)} cm /Im0 Do Q", pdf.Text);
    }

    [Fact]
    public void Encode_WithPdfOptions_And_DefaultEncodeUnchanged()
    {
        var img = Striped(120, 80);
        var single = Parse(SaveService.Encode(img, ImageFileFormat.Pdf));
        Assert.Equal(1, single.PageCount);
        Near(90, single.MediaBoxes[0][0]); Near(60, single.MediaBoxes[0][1]);
        var a4 = Parse(SaveService.Encode(img, ImageFileFormat.Pdf, null, new PdfOptions { Paper = PdfPaperSize.Letter, Orientation = PdfOrientation.Landscape }));
        Near(792, a4.MediaBoxes[0][0]); Near(612, a4.MediaBoxes[0][1]);
        // Raster formats ignore the PDF options.
        Assert.Equal(0x89, SaveService.Encode(img, ImageFileFormat.Png, null, new PdfOptions())[0]);
    }

    // ------------------------------------------------------------------ resize / misc

    [Fact]
    public void Resize_AveragesWhenShrinkingAndKeepsSizeWhenEnlarging()
    {
        var px = new byte[4 * 2 * 4];
        void Set(int x, int y, byte v) { int o = (y * 4 + x) * 4; px[o] = px[o + 1] = px[o + 2] = v; px[o + 3] = 255; }
        Set(0, 0, 0); Set(1, 0, 100); Set(0, 1, 200); Set(1, 1, 100);
        Set(2, 0, 10); Set(3, 0, 10); Set(2, 1, 10); Set(3, 1, 10);
        var half = ImageResize.Resize(BitmapUtil.FromPixels(px, 4, 2), 2, 1);
        Assert.Equal(2, half.PixelWidth);
        Assert.Equal(1, half.PixelHeight);
        var h = BitmapUtil.GetPixels(half);
        Assert.Equal(100, h[0]);
        Assert.Equal(10, h[4]);
        var big = ImageResize.Resize(BitmapUtil.FromPixels(px, 4, 2), 10, 5);
        Assert.Equal(10, big.PixelWidth);
        Assert.Equal(5, big.PixelHeight);
    }

    [Fact]
    public void Resize_TransparentPixelsDoNotDarkenEdges()
    {
        // White opaque pixel next to a transparent BLACK one: premultiplied averaging keeps the colour white.
        var px = new byte[] { 255, 255, 255, 255, 0, 0, 0, 0 };
        var r = BitmapUtil.GetPixels(ImageResize.Resize(BitmapUtil.FromPixels(px, 2, 1), 1, 1));
        Assert.Equal(new byte[] { 255, 255, 255, 128 }, r);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(300 * 1024, "300 KB")]
    [InlineData(5L * 1024 * 1024 + 300 * 1024, "5.29 MB")]
    public void FormatBytes(long bytes, string expected) => Assert.Equal(expected, SaveWindow.FormatBytes(bytes));

    [Fact]
    public void SettingsDefaults()
    {
        var s = new SaveSettings();
        Assert.Equal(SaveDialogStyle.Modern, s.SaveDialogStyle);
        var o = s.ToPdfOptions();
        Assert.Equal(PdfPaperSize.Original, o.Paper);
        Assert.Equal(0, o.MarginMm);
        Assert.True(o.AutoPaginate);
        Assert.Equal(new PdfOptions { JpegQuality = s.Quality }, o);
    }
}
