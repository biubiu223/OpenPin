using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>PDF export options (PRO-08). Defaults = one page exactly the size of the image (96 DPI), lossless.</summary>
public sealed record PdfOptions
{
    public PdfPaperSize Paper { get; init; } = PdfPaperSize.Original;
    public PdfOrientation Orientation { get; init; } = PdfOrientation.Auto;
    /// <summary>Margin on all sides (mm, 0–100).</summary>
    public double MarginMm { get; init; }
    /// <summary>A4 / Letter: scale to the printable area (true) or actual size (96 DPI, shrunk only if wider than the page).</summary>
    public bool FitToPage { get; init; } = true;
    /// <summary>Split long images over several pages (top-aligned slices) instead of shrinking them onto one page.</summary>
    public bool AutoPaginate { get; init; } = true;
    /// <summary>Overlap between consecutive slices (mm).</summary>
    public double OverlapMm { get; init; }
    /// <summary>Embed JPEG (DCTDecode) instead of lossless Flate RGB.</summary>
    public bool UseJpeg { get; init; }
    public int JpegQuality { get; init; } = 92;
}

/// <summary>One PDF page: page size (pt), the image rows it shows (px) and where they go (pt, PDF coordinates: origin bottom-left).</summary>
internal readonly record struct PdfPagePlan(double PageWidth, double PageHeight, Int32Rect Source, Rect Placement)
{
    /// <summary>Placement with a top-left origin (for previews).</summary>
    public Rect PlacementTopLeft => new(Placement.X, PageHeight - Placement.Y - Placement.Height, Placement.Width, Placement.Height);
}

/// <summary>Page layout math for <see cref="PdfImageWriter"/> (pure, unit tested).</summary>
internal static class PdfLayout
{
    /// <summary>1 image px at 96 DPI = 0.75 pt.</summary>
    public const double PtPerPx = 0.75;
    public const double PtPerMm = 72 / 25.4;
    /// <summary>Largest page side most viewers accept (200 in, the PDF 1.4 implementation limit).</summary>
    public const double MaxPagePt = 14400;
    /// <summary>A long image is split (when <see cref="PdfOptions.AutoPaginate"/>) if fitting its width makes it taller than this many pages.</summary>
    public const double LongImagePages = 1.5;

    public static (double W, double H) PaperPt(PdfPaperSize paper) => paper switch
    {
        PdfPaperSize.A4 => (595.276, 841.89),
        PdfPaperSize.Letter => (612, 792),
        _ => (0, 0),
    };

    public static List<PdfPagePlan> Plan(int widthPx, int heightPx, PdfOptions o)
    {
        if (widthPx <= 0 || heightPx <= 0) throw new ArgumentException("Empty image");
        double iw = widthPx * PtPerPx, ih = heightPx * PtPerPx;
        double m = Math.Clamp(double.IsFinite(o.MarginMm) ? o.MarginMm : 0, 0, 100) * PtPerMm;
        var pages = new List<PdfPagePlan>();

        if (o.Paper == PdfPaperSize.Original)
        {
            m = Math.Min(m, MaxPagePt / 4);
            double s = Math.Min(1, (MaxPagePt - 2 * m) / iw);
            double maxContentH = MaxPagePt - 2 * m;
            if (ih * s <= maxContentH)
            {
                pages.Add(new PdfPagePlan(iw * s + 2 * m, ih * s + 2 * m, new Int32Rect(0, 0, widthPx, heightPx), new Rect(m, m, iw * s, ih * s)));
                return pages;
            }
            if (!o.AutoPaginate)
            {
                s = maxContentH / ih;
                pages.Add(new PdfPagePlan(iw * s + 2 * m, ih * s + 2 * m, new Int32Rect(0, 0, widthPx, heightPx), new Rect(m, m, iw * s, ih * s)));
                return pages;
            }
            foreach (var (y, h) in Slices(heightPx, maxContentH / (PtPerPx * s), o.OverlapMm * PtPerMm / (PtPerPx * s)))
            {
                double ph = h * PtPerPx * s;
                pages.Add(new PdfPagePlan(iw * s + 2 * m, ph + 2 * m, new Int32Rect(0, y, widthPx, h), new Rect(m, m, iw * s, ph)));
            }
            return pages;
        }

        var (pw, phh) = PaperPt(o.Paper);
        bool landscape = o.Orientation switch
        {
            PdfOrientation.Landscape => true,
            PdfOrientation.Portrait => false,
            _ => widthPx > heightPx,
        };
        double pageW = landscape ? Math.Max(pw, phh) : Math.Min(pw, phh);
        double pageH = landscape ? Math.Min(pw, phh) : Math.Max(pw, phh);
        m = Math.Min(m, Math.Min(pageW, pageH) / 2 - 18); // keep at least 36 pt of printable area
        double cw = pageW - 2 * m, ch = pageH - 2 * m;

        double sWidth = o.FitToPage ? cw / iw : Math.Min(1, cw / iw);
        double sWhole = o.FitToPage ? Math.Min(cw / iw, ch / ih) : Math.Min(1, Math.Min(cw / iw, ch / ih));
        bool split = o.AutoPaginate && ih * sWidth > ch * (o.FitToPage ? LongImagePages : 1) + 1e-6;
        if (!split)
        {
            double w = iw * sWhole, h = ih * sWhole;
            // Centred in the printable area.
            pages.Add(new PdfPagePlan(pageW, pageH, new Int32Rect(0, 0, widthPx, heightPx), new Rect(m + (cw - w) / 2, m + (ch - h) / 2, w, h)));
            return pages;
        }
        double sp = PtPerPx * sWidth; // pt per image px
        foreach (var (y, h) in Slices(heightPx, ch / sp, o.OverlapMm * PtPerMm / sp))
        {
            double w = iw * sWidth, hh = h * sp;
            // Horizontally centred, top-aligned (PDF y grows upwards).
            pages.Add(new PdfPagePlan(pageW, pageH, new Int32Rect(0, y, widthPx, h), new Rect(m + (cw - w) / 2, pageH - m - hh, w, hh)));
        }
        return pages;
    }

    /// <summary>Rows [y, y+h) of each slice: at most <paramref name="slicePx"/> rows, consecutive slices overlapping by <paramref name="overlapPx"/>.</summary>
    internal static IEnumerable<(int Y, int H)> Slices(int heightPx, double slicePx, double overlapPx)
    {
        int slice = Math.Max(1, (int)Math.Floor(slicePx + 1e-6));
        int overlap = Math.Clamp((int)Math.Round(double.IsFinite(overlapPx) ? overlapPx : 0), 0, slice / 2);
        int step = Math.Max(1, slice - overlap);
        for (int y = 0; ; y += step)
        {
            int h = Math.Min(slice, heightPx - y);
            yield return (y, h);
            if (y + h >= heightPx) yield break;
        }
    }
}

/// <summary>
/// Writes a minimal, valid PDF 1.4 with one image per page (see <see cref="PdfLayout"/>). Transparency is flattened
/// onto white. Default options: a single page exactly the size of the image (1 px = 0.75 pt).
/// </summary>
internal static class PdfImageWriter
{
    /// <summary>An encoded page: layout + the image XObject data (and the bitmap it was encoded from, for previews).</summary>
    internal sealed record Page(PdfPagePlan Plan, BitmapSource Slice, byte[] Data, string Filter);

    public static void Write(Stream output, BitmapSource image, bool useJpeg, int jpegQuality = 92) =>
        Write(output, image, new PdfOptions { UseJpeg = useJpeg, JpegQuality = jpegQuality });

    public static void Write(Stream output, BitmapSource image, PdfOptions options) => WritePages(output, Build(image, options));

    /// <summary>Lay out and encode the pages (thread-safe for frozen bitmaps).</summary>
    public static List<Page> Build(BitmapSource image, PdfOptions options)
    {
        var flat = ImageEncoding.FlattenOnWhite(image);
        var pages = new List<Page>();
        foreach (var plan in PdfLayout.Plan(flat.PixelWidth, flat.PixelHeight, options))
        {
            var slice = plan.Source.Width == flat.PixelWidth && plan.Source.Height == flat.PixelHeight
                ? flat
                : BitmapUtil.Crop(flat, plan.Source);
            if (slice.CanFreeze && !slice.IsFrozen) slice = BitmapUtil.Frozen(slice.Clone());
            byte[] data;
            string filter;
            if (options.UseJpeg)
            {
                var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(options.JpegQuality, 1, 100) };
                enc.Frames.Add(BitmapFrame.Create(slice));
                using var ms = new MemoryStream();
                enc.Save(ms);
                data = ms.ToArray();
                filter = "/DCTDecode";
            }
            else
            {
                data = DeflateRgb(BitmapUtil.GetPixels(slice), slice.PixelWidth, slice.PixelHeight);
                filter = "/FlateDecode";
            }
            pages.Add(new Page(plan, slice, data, filter));
        }
        return pages;
    }

    /// <summary>BGRA → packed RGB, zlib-compressed (what /FlateDecode expects).</summary>
    private static byte[] DeflateRgb(byte[] bgra, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        {
            rgb[j] = bgra[i + 2];
            rgb[j + 1] = bgra[i + 1];
            rgb[j + 2] = bgra[i];
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rgb, 0, rgb.Length);
        return ms.ToArray();
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Objects: 1 catalog, 2 page tree, then per page k: 3+3k page, 4+3k image XObject, 5+3k content stream.
    /// </summary>
    internal static void WritePages(Stream output, IReadOnlyList<Page> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("No pages");
        int objCount = 2 + 3 * pages.Count;
        var offsets = new long[objCount + 1];
        long pos = 0;
        void Raw(byte[] bytes) { output.Write(bytes, 0, bytes.Length); pos += bytes.Length; }
        void Text(string s) => Raw(Encoding.ASCII.GetBytes(s));

        Text("%PDF-1.4\n");
        Raw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // binary marker

        offsets[1] = pos;
        Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = pos;
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(k => $"{3 + 3 * k} 0 R"));
        Text($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (int k = 0; k < pages.Count; k++)
        {
            var p = pages[k];
            int pageObj = 3 + 3 * k, imgObj = pageObj + 1, contentObj = pageObj + 2;
            var pl = p.Plan;
            var content = Encoding.ASCII.GetBytes(
                $"q {Num(pl.Placement.Width)} 0 0 {Num(pl.Placement.Height)} {Num(pl.Placement.X)} {Num(pl.Placement.Y)} cm /Im0 Do Q\n");

            offsets[pageObj] = pos;
            Text($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(pl.PageWidth)} {Num(pl.PageHeight)}] " +
                 $"/Resources << /XObject << /Im0 {imgObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");
            offsets[imgObj] = pos;
            Text($"{imgObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {pl.Source.Width} /Height {pl.Source.Height} " +
                 $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {p.Filter} /Length {p.Data.Length} >>\nstream\n");
            Raw(p.Data);
            Text("\nendstream\nendobj\n");
            offsets[contentObj] = pos;
            Text($"{contentObj} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            Raw(content);
            Text("endstream\nendobj\n");
        }

        long xref = pos;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {objCount + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objCount; i++) sb.Append(offsets[i].ToString("D10", inv)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(inv)).Append("\n%%EOF\n");
        Text(sb.ToString());
    }
}
