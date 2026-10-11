namespace OpenPin.Recording.Codec;

/// <summary>
/// Palette builder + pixel mapper for GIF frames. Colors are 0x00RRGGBB uints.
/// <list type="bullet">
/// <item>If the pixels contain at most <c>maxColors</c> distinct colors (typical for screen UI) the palette is exact.</item>
/// <item>Otherwise median cut over a 5-bit-per-channel histogram; palette entries are the weighted means of the real colors
/// that fall into each box, so flat colors stay exact.</item>
/// </list>
/// Mapping uses a 32768-entry nearest-color cache, optionally with ordered (Bayer 4×4) or Floyd–Steinberg dithering.
/// </summary>
internal sealed class ColorQuantizer
{
    private const int Bins = 32768;

    private readonly int[] _cache = new int[Bins];
    private readonly Dictionary<uint, byte>? _exact;

    private ColorQuantizer(uint[] palette, Dictionary<uint, byte>? exact)
    {
        Palette = palette;
        _exact = exact;
        Array.Fill(_cache, -1);
    }

    /// <summary>Palette colors (0x00RRGGBB), at most the requested count.</summary>
    public uint[] Palette { get; }

    /// <summary>True when every input color is in the palette exactly (no dithering needed).</summary>
    public bool IsExact => _exact != null;

    /// <summary>Build a palette for the colors in <paramref name="pixels"/> where <paramref name="include"/> is true (null = all).</summary>
    public static ColorQuantizer Build(ReadOnlySpan<uint> pixels, ReadOnlySpan<bool> include, int maxColors)
    {
        maxColors = Math.Clamp(maxColors, 1, 256);
        bool all = include.IsEmpty;

        // 1) exact palette if few distinct colors
        var exact = new Dictionary<uint, byte>(Math.Min(maxColors * 2, 512));
        bool fits = true;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (!all && !include[i]) continue;
            uint c = pixels[i] & PixelOps.RgbMask;
            if (exact.ContainsKey(c)) continue;
            if (exact.Count >= maxColors) { fits = false; break; }
            exact[c] = (byte)exact.Count;
        }
        if (fits)
        {
            var pal = new uint[Math.Max(1, exact.Count)];
            foreach (var (c, idx) in exact) pal[idx] = c;
            return new ColorQuantizer(pal, exact);
        }

        // 2) median cut on a 5-bit histogram
        var count = new int[Bins];
        var sumR = new long[Bins];
        var sumG = new long[Bins];
        var sumB = new long[Bins];
        for (int i = 0; i < pixels.Length; i++)
        {
            if (!all && !include[i]) continue;
            uint c = pixels[i];
            int r = (int)(c >> 16) & 255, g = (int)(c >> 8) & 255, b = (int)c & 255;
            int bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            count[bin]++;
            sumR[bin] += r; sumG[bin] += g; sumB[bin] += b;
        }
        var bins = new List<int>();
        for (int i = 0; i < Bins; i++) if (count[i] > 0) bins.Add(i);
        var arr = bins.ToArray();

        var boxes = new List<(int Start, int End)> { (0, arr.Length) };
        while (boxes.Count < maxColors)
        {
            // pick the box with the largest (pixel count × color range) that can still be split
            int best = -1;
            double bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var (s, e) = boxes[i];
                if (e - s < 2) continue;
                var (range, _) = LongestAxis(arr, s, e);
                long n = 0;
                for (int k = s; k < e; k++) n += count[arr[k]];
                double score = (double)n * (range + 1);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;

            var (bs, be) = boxes[best];
            var (_, axis) = LongestAxis(arr, bs, be);
            int shift = axis == 0 ? 10 : axis == 1 ? 5 : 0;
            Array.Sort(arr, bs, be - bs, Comparer<int>.Create((x, y) => ((x >> shift) & 31).CompareTo((y >> shift) & 31)));
            long total = 0;
            for (int k = bs; k < be; k++) total += count[arr[k]];
            long acc = 0;
            int split = bs + 1;
            for (int k = bs; k < be - 1; k++)
            {
                acc += count[arr[k]];
                split = k + 1;
                if (acc * 2 >= total) break;
            }
            boxes[best] = (bs, split);
            boxes.Add((split, be));
        }

        var palette = new uint[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            var (s, e) = boxes[i];
            long n = 0, r = 0, g = 0, b = 0;
            for (int k = s; k < e; k++)
            {
                int bin = arr[k];
                n += count[bin]; r += sumR[bin]; g += sumG[bin]; b += sumB[bin];
            }
            if (n == 0) n = 1;
            palette[i] = (uint)(((r + n / 2) / n) << 16 | ((g + n / 2) / n) << 8 | ((b + n / 2) / n));
        }
        return new ColorQuantizer(palette, null);
    }

    private static (int Range, int Axis) LongestAxis(int[] arr, int s, int e)
    {
        int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
        for (int k = s; k < e; k++)
        {
            int bin = arr[k];
            int r = bin >> 10, g = (bin >> 5) & 31, b = bin & 31;
            if (r < rMin) rMin = r; if (r > rMax) rMax = r;
            if (g < gMin) gMin = g; if (g > gMax) gMax = g;
            if (b < bMin) bMin = b; if (b > bMax) bMax = b;
        }
        // perceptual weights: green matters most, blue least
        int rr = (rMax - rMin) * 3, gr = (gMax - gMin) * 4, br = (bMax - bMin) * 2;
        if (gr >= rr && gr >= br) return (gMax - gMin, 1);
        if (rr >= br) return (rMax - rMin, 0);
        return (bMax - bMin, 2);
    }

    /// <summary>Nearest palette index of an (r, g, b) color (cached per 5-bit bin unless the palette is exact).</summary>
    public int Map(int r, int g, int b)
    {
        if (_exact != null && _exact.TryGetValue((uint)(r << 16 | g << 8 | b), out var e)) return e;
        int bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
        int idx = _cache[bin];
        if (idx >= 0) return idx;
        // search with the bin centre so the cached answer is fair for the whole bin
        int cr = (r & ~7) | 4, cg = (g & ~7) | 4, cb = (b & ~7) | 4;
        idx = Nearest(cr, cg, cb);
        _cache[bin] = idx;
        return idx;
    }

    private int Nearest(int r, int g, int b)
    {
        int best = 0, bestD = int.MaxValue;
        var p = Palette;
        for (int i = 0; i < p.Length; i++)
        {
            int dr = (int)(p[i] >> 16 & 255) - r, dg = (int)(p[i] >> 8 & 255) - g, db = (int)(p[i] & 255) - b;
            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    private static readonly int[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    /// <summary>
    /// Map a w×h block of pixels to palette indices (<paramref name="output"/>), writing <paramref name="transparentIndex"/>
    /// where <paramref name="include"/> is false (if given).
    /// </summary>
    public void MapPixels(ReadOnlySpan<uint> pixels, ReadOnlySpan<bool> include, int w, int h, Span<byte> output,
        GifDitherMode dither, byte transparentIndex)
    {
        bool all = include.IsEmpty;
        if (IsExact) dither = GifDitherMode.None;

        if (dither == GifDitherMode.FloydSteinberg)
        {
            MapFloydSteinberg(pixels, include, w, h, output, transparentIndex);
            return;
        }
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (!all && !include[i]) { output[i] = transparentIndex; continue; }
            uint c = pixels[i];
            int r = (int)(c >> 16) & 255, g = (int)(c >> 8) & 255, b = (int)c & 255;
            if (dither == GifDitherMode.Ordered)
            {
                int t = Bayer4[(y & 3) * 4 + (x & 3)] - 8; // -8..7 ≈ one 5-bit histogram bin
                r = Math.Clamp(r + t, 0, 255); g = Math.Clamp(g + t, 0, 255); b = Math.Clamp(b + t, 0, 255);
            }
            output[i] = (byte)Map(r, g, b);
        }
    }

    private void MapFloydSteinberg(ReadOnlySpan<uint> pixels, ReadOnlySpan<bool> include, int w, int h, Span<byte> output, byte transparentIndex)
    {
        bool all = include.IsEmpty;
        // error rows (current, next), 3 channels, with one guard pixel each side; fixed point ×16
        var cur = new int[(w + 2) * 3];
        var next = new int[(w + 2) * 3];
        var p = Palette;
        for (int y = 0; y < h; y++)
        {
            Array.Clear(next);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int e = (x + 1) * 3;
                if (!all && !include[i])
                {
                    output[i] = transparentIndex;
                    continue; // do not diffuse error across transparent (unchanged) pixels
                }
                uint c = pixels[i];
                int r = Math.Clamp(((int)(c >> 16) & 255) + (cur[e] >> 4), 0, 255);
                int g = Math.Clamp(((int)(c >> 8) & 255) + (cur[e + 1] >> 4), 0, 255);
                int b = Math.Clamp(((int)c & 255) + (cur[e + 2] >> 4), 0, 255);
                int idx = Map(r, g, b);
                output[i] = (byte)idx;
                int er = r - (int)(p[idx] >> 16 & 255), eg = g - (int)(p[idx] >> 8 & 255), eb = b - (int)(p[idx] & 255);
                // 7/16 right, 3/16 down-left, 5/16 down, 1/16 down-right (values kept ×16)
                cur[e + 3] += er * 7; cur[e + 4] += eg * 7; cur[e + 5] += eb * 7;
                next[e - 3] += er * 3; next[e - 2] += eg * 3; next[e - 1] += eb * 3;
                next[e] += er * 5; next[e + 1] += eg * 5; next[e + 2] += eb * 5;
                next[e + 3] += er; next[e + 4] += eg; next[e + 5] += eb;
            }
            (cur, next) = (next, cur);
        }
    }
}
