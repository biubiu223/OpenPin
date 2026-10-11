namespace OpenPin.Tests.LongCapture;

/// <summary>Deterministic synthetic "documents" and viewport frames for stitching tests (BGRA, top-down).</summary>
internal sealed class Img
{
    public Img(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }

    public int W { get; }
    public int H { get; }
    public byte[] Px { get; }

    public void Set(int x, int y, byte b, byte g, byte r)
    {
        int i = (y * W + x) * 4;
        Px[i] = b; Px[i + 1] = g; Px[i + 2] = r; Px[i + 3] = 255;
    }

    public void Fill(int x0, int y0, int w, int h, byte b, byte g, byte r)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + w); x++)
                Set(x, y, b, g, r);
    }

    /// <summary>Copy a w×h block from <paramref name="src"/> (sx, sy) to (dx, dy).</summary>
    public void Blit(Img src, int sx, int sy, int w, int h, int dx, int dy)
    {
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src.Px, ((sy + y) * src.W + sx) * 4, Px, ((dy + y) * W + dx) * 4, w * 4);
    }

    public Img Transposed()
    {
        var t = new Img(H, W);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                Buffer.BlockCopy(Px, (y * W + x) * 4, t.Px, (x * H + y) * 4, 4);
        return t;
    }

    public Img Crop(int x0, int y0, int w, int h)
    {
        var c = new Img(w, h);
        c.Blit(this, x0, y0, w, h, 0, 0);
        return c;
    }
}

internal static class SyntheticPage
{
    /// <summary>
    /// A tall page: paragraphs of "text" (words with glyph-like noise texture), gradient images, rules and short
    /// blank gaps (never longer than <paramref name="maxGap"/> so two different viewports never look identical).
    /// </summary>
    public static Img Create(int width, int height, int seed, int maxGap = 36)
    {
        var rnd = new Random(seed);
        var img = new Img(width, height);
        img.Fill(0, 0, width, height, 255, 255, 255);
        int y = 8;
        while (y < height)
        {
            switch (rnd.Next(10))
            {
                case < 6: // paragraph
                {
                    int lines = rnd.Next(2, 8);
                    for (int l = 0; l < lines && y < height; l++)
                    {
                        int x = 12 + rnd.Next(0, 24);
                        int lineEnd = width - 12 - rnd.Next(0, width / 3);
                        int glyphH = rnd.Next(9, 13);
                        byte ink = (byte)rnd.Next(0, 90);
                        while (x < lineEnd)
                        {
                            int ww = rnd.Next(8, 70);
                            for (int yy = 0; yy < glyphH; yy++)
                                for (int xx = 0; xx < ww && x + xx < width; xx++)
                                    if (rnd.NextDouble() < 0.45 && y + yy < height) img.Set(x + xx, y + yy, ink, ink, (byte)(ink + 20));
                            x += ww + rnd.Next(5, 12);
                        }
                        y += glyphH + rnd.Next(5, 9);
                    }
                    break;
                }
                case < 8: // image with gradients
                {
                    int ih = rnd.Next(40, 150), x0 = rnd.Next(0, width / 4), iw = rnd.Next(width / 3, width - x0);
                    byte b0 = (byte)rnd.Next(256), g0 = (byte)rnd.Next(256), r0 = (byte)rnd.Next(256);
                    for (int yy = 0; yy < ih && y + yy < height; yy++)
                        for (int xx = 0; xx < iw; xx++)
                        {
                            int v = (xx * 255 / Math.Max(1, iw) + yy * 180 / ih) / 2;
                            img.Set(x0 + xx, y + yy, (byte)((b0 + v) & 255), (byte)((g0 + yy * 2) & 255), (byte)((r0 + xx) & 255));
                        }
                    y += ih;
                    break;
                }
                case 8: // horizontal rule
                    img.Fill(8, y, width - 16, rnd.Next(1, 3), 200, 200, 200);
                    y += 3;
                    break;
                default: // colored banner with a few "words"
                {
                    int bh = rnd.Next(20, 44);
                    byte c = (byte)rnd.Next(150, 240);
                    img.Fill(0, y, width, bh, c, (byte)(c - 30), (byte)(255 - c));
                    for (int i = 0; i < 4; i++) img.Fill(rnd.Next(0, width - 40), y + bh / 3, rnd.Next(10, 40), bh / 3, 30, 30, 30);
                    y += bh;
                    break;
                }
            }
            y += rnd.Next(4, maxGap);
        }
        return img;
    }

    /// <summary>A fixed bar (title bar / sticky header / status bar) with some structure.</summary>
    public static Img Bar(int width, int height, int seed)
    {
        var rnd = new Random(seed);
        var bar = new Img(width, height);
        bar.Fill(0, 0, width, height, 243, 236, 230);
        bar.Fill(0, height - 1, width, 1, 180, 180, 180);
        for (int i = 0; i < 6; i++) bar.Fill(rnd.Next(0, Math.Max(1, width - 50)), height / 4, rnd.Next(12, 50), height / 2, 60, 60, 60);
        return bar;
    }

    /// <summary>Viewport frame: header + page[pos .. pos + band) + footer, optional static left sidebar, optional noise.</summary>
    public static Img Frame(Img page, int pos, int band, Img? header = null, Img? footer = null, Img? sidebar = null,
        int noise = 0, int noiseSeed = 0)
    {
        int hh = header?.H ?? 0, fh = footer?.H ?? 0, sw = sidebar?.W ?? 0;
        var f = new Img(page.W, hh + band + fh);
        if (header != null) f.Blit(header, 0, 0, page.W, hh, 0, 0);
        f.Blit(page, 0, pos, page.W, band, 0, hh);
        if (footer != null) f.Blit(footer, 0, 0, page.W, fh, 0, hh + band);
        if (sidebar != null) f.Blit(sidebar, 0, 0, sw, band, 0, hh);
        if (noise > 0)
        {
            var rnd = new Random(noiseSeed);
            for (int i = 0; i < f.Px.Length; i++)
            {
                if ((i & 3) == 3) { f.Px[i] = (byte)rnd.Next(256); continue; } // undefined alpha
                f.Px[i] = (byte)Math.Clamp(f.Px[i] + rnd.Next(-noise, noise + 1), 0, 255);
            }
        }
        return f;
    }

    /// <summary>Expected stitched result: header + page[start .. end) + footer.</summary>
    public static Img Expected(Img page, int end, Img? header = null, Img? footer = null, int start = 0)
    {
        int hh = header?.H ?? 0, fh = footer?.H ?? 0, len = end - start;
        var e = new Img(page.W, hh + len + fh);
        if (header != null) e.Blit(header, 0, 0, page.W, hh, 0, 0);
        e.Blit(page, 0, start, page.W, len, 0, hh);
        if (footer != null) e.Blit(footer, 0, 0, page.W, fh, 0, hh + len);
        return e;
    }

    /// <summary>Largest per-channel difference (alpha ignored) over the column range [x0, x1); -1 if sizes differ.</summary>
    public static int MaxDiff(Img expected, byte[] actual, int width, int height, int x0 = 0, int x1 = -1)
    {
        if (expected.W != width || expected.H != height) return -1;
        if (x1 < 0) x1 = width;
        int max = 0;
        for (int y = 0; y < height; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * width + x) * 4;
                for (int c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(expected.Px[i + c] - actual[i + c]));
            }
        return max;
    }
}
