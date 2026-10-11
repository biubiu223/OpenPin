using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Services;

/// <summary>Pure (testable) codecs for raw clipboard formats: CF_DIB / CF_DIBV5, CF_HTML, CF_HDROP.</summary>
internal static class ClipboardFormats
{
    private const int BI_RGB = 0, BI_BITFIELDS = 3, BI_ALPHABITFIELDS = 6;

    // ------------------------------------------------------------------ DIB

    /// <summary>
    /// Decode a packed DIB (BITMAPINFOHEADER / V4 / V5 + optional masks/palette + pixels).
    /// 32-bit: alpha is used when the header declares an alpha mask or when the alpha bytes are not all zero.
    /// Other bit depths are delegated to WIC (via a synthesized BMP file). Returns null if the data is invalid.
    /// </summary>
    public static BitmapSource? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        int width = BitConverter.ToInt32(dib, 4);
        int height = BitConverter.ToInt32(dib, 8);
        int bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        if (width <= 0 || height == 0 || width > 100_000 || Math.Abs(height) > 100_000) return null;
        bool topDown = height < 0;
        height = Math.Abs(height);

        // Masks: in V4/V5 headers they are part of the header; with a 40-byte header + BI_BITFIELDS they follow it.
        uint rMask = 0x00FF0000, gMask = 0x0000FF00, bMask = 0x000000FF, aMask = 0;
        int offset = headerSize;
        if (headerSize >= 56)
        {
            rMask = BitConverter.ToUInt32(dib, 40); gMask = BitConverter.ToUInt32(dib, 44);
            bMask = BitConverter.ToUInt32(dib, 48); aMask = BitConverter.ToUInt32(dib, 52);
            if (compression == BI_RGB) { rMask = 0x00FF0000; gMask = 0x0000FF00; bMask = 0x000000FF; }
        }
        else if (headerSize >= 52 && compression is BI_BITFIELDS or BI_ALPHABITFIELDS)
        {
            rMask = BitConverter.ToUInt32(dib, 40); gMask = BitConverter.ToUInt32(dib, 44); bMask = BitConverter.ToUInt32(dib, 48);
            if (headerSize >= 56) aMask = BitConverter.ToUInt32(dib, 52);
        }
        else if (compression is BI_BITFIELDS or BI_ALPHABITFIELDS)
        {
            int n = compression == BI_ALPHABITFIELDS ? 4 : 3;
            if (dib.Length < offset + 4 * n) return null;
            rMask = BitConverter.ToUInt32(dib, offset); gMask = BitConverter.ToUInt32(dib, offset + 4); bMask = BitConverter.ToUInt32(dib, offset + 8);
            if (n == 4) aMask = BitConverter.ToUInt32(dib, offset + 12);
            offset += 4 * n;
        }
        // Some producers append 3 masks even with a V5 header + BI_BITFIELDS; handled by size check below.

        if (bitCount <= 8) offset += 4 * (clrUsed > 0 ? clrUsed : 1 << bitCount);
        else offset += 4 * clrUsed;

        if (bitCount == 32 && compression is BI_RGB or BI_BITFIELDS or BI_ALPHABITFIELDS)
        {
            int stride = width * 4;
            long needed = (long)stride * height;
            // Tolerate the "masks after a V5 header" quirk: if exactly 12 extra bytes are present, skip them.
            if (dib.Length - offset == needed + 12 && compression == BI_BITFIELDS) offset += 12;
            if (dib.Length - offset < needed) return null;

            var px = new byte[needed];
            int rs = Shift(rMask), gs = Shift(gMask), bs = Shift(bMask), as_ = Shift(aMask);
            bool anyAlpha = false;
            for (int y = 0; y < height; y++)
            {
                int src = offset + (topDown ? y : height - 1 - y) * stride;
                int dst = y * stride;
                for (int x = 0; x < width; x++, src += 4, dst += 4)
                {
                    uint v = BitConverter.ToUInt32(dib, src);
                    px[dst] = (byte)((v & bMask) >> bs);
                    px[dst + 1] = (byte)((v & gMask) >> gs);
                    px[dst + 2] = (byte)((v & rMask) >> rs);
                    byte a = aMask != 0 ? (byte)((v & aMask) >> as_) : (byte)(v >> 24);
                    px[dst + 3] = a;
                    if (a != 0) anyAlpha = true;
                }
            }
            // BI_RGB 32-bit: the 4th byte is usually garbage/zero → opaque. Only trust it if something is non-zero.
            if (!anyAlpha || (aMask == 0 && compression != BI_RGB))
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return BitmapUtil.FromPixels(px, width, height);
        }

        if (bitCount == 24 && compression == BI_RGB)
        {
            int srcStride = (width * 3 + 3) & ~3;
            if (dib.Length - offset < (long)srcStride * height) return null;
            var px = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int src = offset + (topDown ? y : height - 1 - y) * srcStride;
                int dst = y * width * 4;
                for (int x = 0; x < width; x++, src += 3, dst += 4)
                {
                    px[dst] = dib[src]; px[dst + 1] = dib[src + 1]; px[dst + 2] = dib[src + 2]; px[dst + 3] = 255;
                }
            }
            return BitmapUtil.FromPixels(px, width, height);
        }

        // Other depths / compressions: let WIC decode a synthesized .bmp file.
        try
        {
            var file = new byte[14 + dib.Length];
            file[0] = (byte)'B'; file[1] = (byte)'M';
            BitConverter.GetBytes(file.Length).CopyTo(file, 2);
            BitConverter.GetBytes(14 + offset).CopyTo(file, 10);
            dib.CopyTo(file, 14);
            using var ms = new System.IO.MemoryStream(file);
            return BitmapUtil.ToBgra32(BitmapUtil.DecodeImage(ms));
        }
        catch { return null; }
    }

    private static int Shift(uint mask)
    {
        if (mask == 0) return 0;
        int s = 0;
        while ((mask & 1) == 0) { mask >>= 1; s++; }
        return s;
    }

    /// <summary>Encode as an opaque 24-bit bottom-up CF_DIB (transparency flattened onto white).</summary>
    public static byte[] EncodeDib24(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var px = BitmapUtil.GetPixels(image);
        ImageEncoding.FlattenOnWhite(px);
        int stride = (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        BitConverter.GetBytes(stride * h).CopyTo(dib, 20);
        BitConverter.GetBytes(3780).CopyTo(dib, 24); // 96 DPI
        BitConverter.GetBytes(3780).CopyTo(dib, 28);
        for (int y = 0; y < h; y++)
        {
            int src = (h - 1 - y) * w * 4, dst = 40 + y * stride;
            for (int x = 0; x < w; x++, src += 4, dst += 3)
            {
                dib[dst] = px[src]; dib[dst + 1] = px[src + 1]; dib[dst + 2] = px[src + 2];
            }
        }
        return dib;
    }

    // ------------------------------------------------------------------ CF_HTML

    /// <summary>Extract the HTML fragment between StartFragment/EndFragment (byte offsets into UTF-8 data).</summary>
    public static string? ExtractHtmlFragment(byte[] cfHtml)
    {
        int len = Array.IndexOf(cfHtml, (byte)0);
        if (len < 0) len = cfHtml.Length;
        var header = Encoding.ASCII.GetString(cfHtml, 0, Math.Min(len, 1024));
        int start = ReadOffset(header, "StartFragment:"), end = ReadOffset(header, "EndFragment:");
        if (start >= 0 && end > start && end <= len)
            return Encoding.UTF8.GetString(cfHtml, start, end - start).Trim();

        // No (valid) offsets: fall back to the comment markers, then to the HTML part.
        var all = Encoding.UTF8.GetString(cfHtml, 0, len);
        int s = all.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int e = all.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (s >= 0 && e > s) return all.Substring(s + 20, e - s - 20).Trim();
        int sh = ReadOffset(header, "StartHTML:"), eh = ReadOffset(header, "EndHTML:");
        if (sh >= 0 && eh > sh && eh <= len) return Encoding.UTF8.GetString(cfHtml, sh, eh - sh).Trim();
        int lt = all.IndexOf('<');
        return lt >= 0 ? all[lt..].Trim() : null;
    }

    private static int ReadOffset(string header, string key)
    {
        int i = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        i += key.Length;
        int j = i;
        while (j < header.Length && (char.IsDigit(header[j]) || header[j] == '-')) j++;
        return int.TryParse(header.AsSpan(i, j - i), out var v) ? v : -1;
    }

    /// <summary>Build a CF_HTML payload (UTF-8, with correct byte offsets) for an HTML fragment.</summary>
    public static byte[] CreateCfHtml(string fragment)
    {
        const string headerFmt = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body>\r\n<!--StartFragment-->";
        const string post = "<!--EndFragment-->\r\n</body></html>";
        int headerLen = Encoding.ASCII.GetByteCount(string.Format(headerFmt, 0, 0, 0, 0));
        int startHtml = headerLen;
        int startFrag = startHtml + Encoding.UTF8.GetByteCount(pre);
        int endFrag = startFrag + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFrag + Encoding.UTF8.GetByteCount(post);
        var text = string.Format(headerFmt, startHtml, endHtml, startFrag, endFrag) + pre + fragment + post;
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Very small HTML → plain text conversion (for HTML-only clipboard content).</summary>
    public static string HtmlToPlainText(string html)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</li>|</tr>|</h\d>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(t).Trim();
    }

    // ------------------------------------------------------------------ CF_HDROP

    /// <summary>Parse a DROPFILES structure (CF_HDROP global memory) into file paths.</summary>
    public static IReadOnlyList<string> ParseHDrop(byte[] data)
    {
        var list = new List<string>();
        if (data.Length < 20) return list;
        int pFiles = BitConverter.ToInt32(data, 0);
        bool wide = BitConverter.ToInt32(data, 16) != 0;
        if (pFiles < 20 || pFiles >= data.Length) return list;
        var enc = wide ? Encoding.Unicode : Encoding.Default;
        int step = wide ? 2 : 1;
        int i = pFiles;
        while (i + step <= data.Length)
        {
            int start = i;
            while (i + step <= data.Length && (wide ? BitConverter.ToUInt16(data, i) != 0 : data[i] != 0)) i += step;
            if (i == start) break; // double terminator
            list.Add(enc.GetString(data, start, i - start));
            i += step;
        }
        return list;
    }

    /// <summary>Single short line → color, else null.</summary>
    public static bool TryParseColorText(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length > 40 || t.Contains('\n') || t.Contains('\r')) return false;
        return ColorText.TryParse(t, out color);
    }
}
