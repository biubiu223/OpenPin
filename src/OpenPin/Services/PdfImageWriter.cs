using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>
/// Writes a minimal, valid single-page PDF 1.4 containing one image that fills the page.
/// Page size = image size at 96 DPI (1 px = 0.75 pt). Transparency is flattened onto white.
/// </summary>
internal static class PdfImageWriter
{
    public static void Write(Stream output, BitmapSource image, bool useJpeg, int jpegQuality = 92)
    {
        var flat = ImageEncoding.FlattenOnWhite(image);
        int w = flat.PixelWidth, h = flat.PixelHeight;

        byte[] imageData;
        string filter;
        if (useJpeg)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
            enc.Frames.Add(BitmapFrame.Create(flat));
            using var ms = new MemoryStream();
            enc.Save(ms);
            imageData = ms.ToArray();
            filter = "/DCTDecode";
        }
        else
        {
            imageData = DeflateRgb(BitmapUtil.GetPixels(flat), w, h);
            filter = "/FlateDecode";
        }

        WritePdf(output, w, h, imageData, filter);
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

    internal static void WritePdf(Stream output, int widthPx, int heightPx, byte[] imageData, string filter)
    {
        var inv = CultureInfo.InvariantCulture;
        string pw = (widthPx * 0.75).ToString("0.###", inv), ph = (heightPx * 0.75).ToString("0.###", inv);
        var content = Encoding.ASCII.GetBytes($"q {pw} 0 0 {ph} 0 0 cm /Im0 Do Q\n");

        var offsets = new long[6];
        long pos = 0;
        void Raw(byte[] bytes) { output.Write(bytes, 0, bytes.Length); pos += bytes.Length; }
        void Text(string s) => Raw(Encoding.ASCII.GetBytes(s));

        Text("%PDF-1.4\n");
        Raw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // binary marker

        offsets[1] = pos;
        Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = pos;
        Text("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = pos;
        Text($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw} {ph}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        offsets[4] = pos;
        Text($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {widthPx} /Height {heightPx} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {filter} /Length {imageData.Length} >>\nstream\n");
        Raw(imageData);
        Text("\nendstream\nendobj\n");
        offsets[5] = pos;
        Text($"5 0 obj\n<< /Length {content.Length} >>\nstream\n");
        Raw(content);
        Text("endstream\nendobj\n");

        long xref = pos;
        var sb = new StringBuilder();
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) sb.Append(offsets[i].ToString("D10", inv)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(inv)).Append("\n%%EOF\n");
        Text(sb.ToString());
    }
}
