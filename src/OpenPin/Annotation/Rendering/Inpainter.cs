using System.Diagnostics;

namespace OpenPin.Annotation.Rendering;

/// <summary>How <see cref="Inpainter.Fill"/> filled the hole.</summary>
internal enum InpaintMethod
{
    /// <summary>Nothing to do (empty hole) or no known pixels to fill from.</summary>
    None,
    /// <summary>The boundary is one colour: the hole is filled with exactly that colour.</summary>
    Solid,
    /// <summary>The boundary follows a smooth (≤ quadratic) gradient: model + harmonic (membrane) correction.</summary>
    Smooth,
    /// <summary>Texture on a smooth base: membrane-interpolated base colour + texture detail copied by exemplar inpainting.</summary>
    Texture,
    /// <summary>Edges cross the boundary: multi-scale exemplar (patch) inpainting continues them.</summary>
    Structure,
}

/// <summary>
/// Offline "smart erase" (PRO-06): removes the content under a mask and fills it from the surrounding background.
/// Pure CPU code on BGRA buffers, deterministic (same input → same output, independent of thread scheduling), so the
/// on-screen result and the exported image are identical.
/// <list type="number">
/// <item><b>Solid</b>: ≥ 90 % of the 2-px ring around the hole is one colour (±2) → that exact colour
/// ("text on a flat UI background", chat bubbles).</item>
/// <item><b>Smooth</b>: ≥ 95 % of the ring fits a quadratic colour model within ±6 (gradients) → model + harmonic
/// (Laplace / membrane) interpolation of the residual. Reproduces linear / quadratic gradients exactly (also when the
/// hole touches the image border) and matches any other smooth boundary seamlessly.</item>
/// <item><b>Texture</b>: the low-pass (7×7 normalised box) of the surroundings has no edge on the ring → the base colour
/// is membrane-interpolated as in 2 and the detail (image − low-pass) is copied from the source patches chosen by the
/// exemplar search below (paper textures, guilloche, photo grain…). Colours cannot "jump" in from elsewhere.</item>
/// <item><b>Structure</b>: otherwise plain exemplar inpainting, which continues edges into the hole.</item>
/// </list>
/// Exemplar inpainting follows Criminisi et al. 2004: the fill front is processed in order of confidence × isophote
/// strength; each front patch (9×9) is completed from the best matching fully-known patch (SSD). Holes thicker than
/// 20 px are handled on a box-filtered pyramid: the coarsest level searches a window around the patch, every finer
/// level runs the same front-ordered fill but only tests candidates around the upsampled coarse sources (±2 px) and
/// the sources of already-filled neighbours, so full-resolution texture is copied coherently at a small cost.
/// </summary>
internal static class Inpainter
{
    /// <summary>Max per-channel difference for the "solid" test.</summary>
    public const int SolidTolerance = 2;
    /// <summary>Max per-channel residual of a ring pixel to count as an inlier of the smooth model.</summary>
    public const int SmoothTolerance = 6;
    /// <summary>Per-pixel low-pass gradient above which a ring pixel counts as an edge.</summary>
    public const float EdgeThreshold = 6;
    private const double SolidFraction = 0.90;
    private const double SmoothFraction = 0.95;
    private const int RingWidth = 2;
    private const int LowPassRadius = 3;
    /// <summary>Search radius around each guided candidate on the finer pyramid levels.</summary>
    private const int GuideRadius = 1;

    /// <summary>Development hook: receives timing lines (null in production).</summary>
    internal static Action<string>? Trace;

    /// <summary>
    /// Fills the pixels of <paramref name="bgra"/> (w×h, stride w*4) where <paramref name="mask"/> != 0, in place.
    /// Pixels outside the mask are never modified. Returns the method used.
    /// </summary>
    public static InpaintMethod Fill(byte[] bgra, int w, int h, byte[] mask)
    {
        if (w <= 0 || h <= 0) return InpaintMethod.None;
        if (bgra.Length < w * h * 4 || mask.Length < w * h) throw new ArgumentException("Buffer too small");
        int holes = 0;
        for (int i = 0; i < w * h; i++) if (mask[i] != 0) holes++;
        if (holes == 0) return InpaintMethod.None;
        var sw = Trace != null ? Stopwatch.StartNew() : null;
        var ring = Ring(mask, w, h, RingWidth);
        if (ring.Count == 0) return InpaintMethod.None;
        if (TrySolid(bgra, w, h, mask, ring)) return InpaintMethod.Solid;

        var v = new float[w * h * 4];
        for (int i = 0; i < v.Length; i++) v[i] = bgra[i];
        if (HarmonicFill(v, mask, ring, w, h, SmoothFraction))
        {
            ToBytes(v, bgra, mask, w * h);
            Trace?.Invoke($"smooth {sw!.ElapsedMilliseconds} ms");
            return InpaintMethod.Smooth;
        }

        var low = LowPass(bgra, mask, w, h, LowPassRadius);
        bool smoothBase = !HasEdges(low, mask, ring, w, h);
        Trace?.Invoke($"analysis {sw!.ElapsedMilliseconds} ms, smooth base: {smoothBase}");
        var src = ExemplarPyramid(bgra, mask, w, h);
        Trace?.Invoke($"exemplar {sw!.ElapsedMilliseconds} ms");

        if (smoothBase)
        {
            // Base colour: membrane; detail: copied (source pixel minus its own low-pass).
            var baseFill = (float[])low.Clone();
            HarmonicFill(baseFill, mask, ring, w, h, 0);
            for (int i = 0; i < w * h; i++)
            {
                if (mask[i] == 0) continue;
                int s = src[i];
                for (int c = 0; c < 4; c++)
                {
                    float val = baseFill[i * 4 + c];
                    if (s >= 0) val += bgra[s * 4 + c] - low[s * 4 + c];
                    bgra[i * 4 + c] = ClampByte(val);
                }
            }
            Trace?.Invoke($"texture total {sw!.ElapsedMilliseconds} ms");
            return InpaintMethod.Texture;
        }

        // Anything that could not be sourced (no known patch anywhere) gets a membrane fill.
        var rest = new byte[w * h];
        bool any = false;
        for (int i = 0; i < w * h; i++)
            if (mask[i] != 0 && src[i] < 0) { rest[i] = 1; any = true; }
        if (any) SmoothFillBytes(bgra, w, h, rest);
        Trace?.Invoke($"structure total {sw!.ElapsedMilliseconds} ms");
        return InpaintMethod.Structure;
    }

    private static void ToBytes(float[] v, byte[] bgra, byte[] mask, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (mask[i] == 0) continue;
            for (int c = 0; c < 4; c++) bgra[i * 4 + c] = ClampByte(v[i * 4 + c]);
        }
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    // ------------------------------------------------------------------ ring analysis

    /// <summary>Known pixels within Chebyshev distance ≤ <paramref name="radius"/> of the hole (ascending indices).</summary>
    internal static List<int> Ring(byte[] mask, int w, int h, int radius)
    {
        var mark = new bool[w * h];
        var ring = new List<int>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] == 0) continue;
                bool edge = (x > 0 && mask[i - 1] == 0) || (x < w - 1 && mask[i + 1] == 0)
                         || (y > 0 && mask[i - w] == 0) || (y < h - 1 && mask[i + w] == 0)
                         || (x > 0 && y > 0 && mask[i - w - 1] == 0) || (x < w - 1 && y > 0 && mask[i - w + 1] == 0)
                         || (x > 0 && y < h - 1 && mask[i + w - 1] == 0) || (x < w - 1 && y < h - 1 && mask[i + w + 1] == 0);
                if (!edge) continue;
                int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                for (int yy = y0; yy <= y1; yy++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int j = yy * w + xx;
                    if (mask[j] != 0 || mark[j]) continue;
                    mark[j] = true;
                    ring.Add(j);
                }
            }
        }
        ring.Sort();
        return ring;
    }

    private static bool TrySolid(byte[] img, int w, int h, byte[] mask, List<int> ring)
    {
        var counts = new Dictionary<uint, int>();
        foreach (int i in ring)
        {
            uint key = BitConverter.ToUInt32(img, i * 4);
            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        uint mode = 0;
        int best = -1;
        foreach (var (k, c) in counts)
            if (c > best || (c == best && k < mode)) { best = c; mode = k; }
        byte mb = (byte)mode, mg = (byte)(mode >> 8), mr = (byte)(mode >> 16), ma = (byte)(mode >> 24);
        int near = 0;
        foreach (int i in ring)
        {
            int o = i * 4;
            if (Math.Abs(img[o] - mb) <= SolidTolerance && Math.Abs(img[o + 1] - mg) <= SolidTolerance &&
                Math.Abs(img[o + 2] - mr) <= SolidTolerance && Math.Abs(img[o + 3] - ma) <= SolidTolerance)
                near++;
        }
        if (near < SolidFraction * ring.Count) return false;
        for (int i = 0; i < w * h; i++)
        {
            if (mask[i] == 0) continue;
            int o = i * 4;
            img[o] = mb; img[o + 1] = mg; img[o + 2] = mr; img[o + 3] = ma;
        }
        return true;
    }

    /// <summary>Least-squares colour model c(x,y) = Σ coef·basis over a point set (1, u, v, u², uv, v²; fewer if few points).</summary>
    private sealed class ColorModel
    {
        private readonly int _terms;
        private readonly double _cx, _cy, _s;
        private readonly double[,] _coef; // [channel, term]

        private ColorModel(int terms, double cx, double cy, double s, double[,] coef)
        {
            _terms = terms; _cx = cx; _cy = cy; _s = s; _coef = coef;
        }

        private static void Basis(double u, double v, int terms, Span<double> b)
        {
            b[0] = 1;
            if (terms > 1) { b[1] = u; b[2] = v; }
            if (terms > 3) { b[3] = u * u; b[4] = u * v; b[5] = v * v; }
        }

        public double Predict(int x, int y, int c)
        {
            Span<double> b = stackalloc double[6];
            Basis((x - _cx) / _s, (y - _cy) / _s, _terms, b);
            double v = 0;
            for (int t = 0; t < _terms; t++) v += _coef[c, t] * b[t];
            return v;
        }

        public static ColorModel? Fit(float[] img, int w, IReadOnlyList<int> pts, int maxTerms)
        {
            int n = pts.Count;
            if (n == 0) return null;
            int terms = Math.Min(maxTerms, n >= 24 ? 6 : n >= 6 ? 3 : 1);
            double cx = 0, cy = 0;
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (int i in pts)
            {
                int x = i % w, y = i / w;
                cx += x; cy += y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            cx /= n; cy /= n;
            double s = Math.Max(1, Math.Max(maxX - minX, maxY - minY) / 2.0);
            var ata = new double[terms, terms];
            var atb = new double[4, terms];
            Span<double> b = stackalloc double[6];
            foreach (int i in pts)
            {
                int x = i % w, y = i / w;
                Basis((x - cx) / s, (y - cy) / s, terms, b);
                for (int r = 0; r < terms; r++)
                {
                    for (int c = 0; c < terms; c++) ata[r, c] += b[r] * b[c];
                    for (int ch = 0; ch < 4; ch++) atb[ch, r] += b[r] * img[i * 4 + ch];
                }
            }
            for (int r = 0; r < terms; r++) ata[r, r] += 1e-9 * n; // tiny ridge: thin rings make u²/v² nearly collinear
            var coef = new double[4, terms];
            for (int ch = 0; ch < 4; ch++)
            {
                var rhs = new double[terms];
                for (int r = 0; r < terms; r++) rhs[r] = atb[ch, r];
                var sol = SolveLinear((double[,])ata.Clone(), rhs);
                if (sol == null) return null;
                for (int r = 0; r < terms; r++) coef[ch, r] = sol[r];
            }
            return new ColorModel(terms, cx, cy, s, coef);
        }

        public bool IsInlier(float[] img, int w, int i)
        {
            int x = i % w, y = i / w;
            for (int c = 0; c < 4; c++)
                if (Math.Abs(img[i * 4 + c] - Predict(x, y, c)) > SmoothTolerance) return false;
            return true;
        }
    }

    /// <summary>Gaussian elimination with partial pivoting. Null if singular.</summary>
    private static double[]? SolveLinear(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12) return null;
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                if (f == 0) continue;
                for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int c = r + 1; c < n; c++) s -= a[r, c] * x[c];
            x[r] = s / a[r, r];
        }
        return x;
    }

    /// <summary>
    /// Fills the hole of <paramref name="v"/> (4 interleaved float channels) with a linear / quadratic model fitted on the ring
    /// plus the harmonic interpolation of the ring residual. With <paramref name="minInliers"/> &gt; 0 it first checks
    /// that this fraction of the ring fits the model within <see cref="SmoothTolerance"/> (returns false otherwise;
    /// outliers are then replaced by the model).
    /// </summary>
    private static bool HarmonicFill(float[] v, byte[] hole, List<int> ring, int w, int h, double minInliers)
    {
        // Simplest model that explains the ring: linear first (stable when extrapolating into a hole that touches the
        // image border), quadratic only if needed.
        ColorModel? model = null;
        HashSet<int>? inlierSet = null;
        if (minInliers <= 0) model = ColorModel.Fit(v, w, ring, 3);
        else
        {
            foreach (int terms in new[] { 3, 6 })
            {
                var m = ColorModel.Fit(v, w, ring, terms);
                if (m == null) continue;
                var inliers = ring.Where(i => m.IsInlier(v, w, i)).ToList();
                if (inliers.Count < ring.Count && inliers.Count >= minInliers * ring.Count)
                {
                    // Refit without the outliers (e.g. a few anti-aliased glyph pixels the mask did not cover).
                    var refit = ColorModel.Fit(v, w, inliers, terms);
                    if (refit != null)
                    {
                        m = refit;
                        inliers = ring.Where(i => m.IsInlier(v, w, i)).ToList();
                    }
                }
                if (inliers.Count < minInliers * ring.Count) continue;
                model = m;
                inlierSet = new HashSet<int>(inliers);
                break;
            }
        }
        if (model == null) return false;

        // Residual r = v - model on the ring (outliers → 0, i.e. trust the model there); only ring pixels touch the hole.
        var res = new float[w * h * 4];
        bool nonZero = false;
        foreach (int i in ring)
        {
            if (inlierSet != null && !inlierSet.Contains(i)) continue;
            int x = i % w, y = i / w;
            for (int c = 0; c < 4; c++)
            {
                float r = (float)(v[i * 4 + c] - model.Predict(x, y, c));
                res[i * 4 + c] = r;
                if (Math.Abs(r) > 1e-3f) nonZero = true;
            }
        }
        if (nonZero) Laplace(res, hole, w, h);
        for (int i = 0; i < w * h; i++)
        {
            if (hole[i] == 0) continue;
            int x = i % w, y = i / w;
            for (int c = 0; c < 4; c++) v[i * 4 + c] = (float)(model.Predict(x, y, c) + res[i * 4 + c]);
        }
        return true;
    }

    /// <summary>Normalised box low-pass over the known pixels (hole pixels are ignored; their output is undefined).</summary>
    internal static float[] LowPass(byte[] img, byte[] hole, int w, int h, int r)
    {
        int iw = w + 1;
        var sum = new long[iw * (h + 1) * 5];
        Span<long> row = stackalloc long[5];
        for (int y = 0; y < h; y++)
        {
            row.Clear();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (hole[i] == 0)
                {
                    row[0] += img[i * 4]; row[1] += img[i * 4 + 1]; row[2] += img[i * 4 + 2]; row[3] += img[i * 4 + 3]; row[4]++;
                }
                int o = ((y + 1) * iw + x + 1) * 5, u = (y * iw + x + 1) * 5;
                for (int c = 0; c < 5; c++) sum[o + c] = sum[u + c] + row[c];
            }
        }
        var low = new float[w * h * 4];
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                int a = (y1 * iw + x1) * 5, b = (y0 * iw + x1) * 5, c0 = (y1 * iw + x0) * 5, d = (y0 * iw + x0) * 5;
                long n = sum[a + 4] - sum[b + 4] - sum[c0 + 4] + sum[d + 4];
                if (n == 0) continue;
                for (int c = 0; c < 4; c++)
                    low[(y * w + x) * 4 + c] = (float)(sum[a + c] - sum[b + c] - sum[c0 + c] + sum[d + c]) / n;
            }
        });
        return low;
    }

    /// <summary>True if the low-pass has an edge on the ring (e.g. a bubble border crossing the hole).</summary>
    private static bool HasEdges(float[] low, byte[] hole, List<int> ring, int w, int h)
    {
        int edges = 0;
        int allowed = Math.Max(2, ring.Count / 500);
        foreach (int i in ring)
        {
            int x = i % w, y = i / w;
            bool l = x > 0 && hole[i - 1] == 0, rr = x < w - 1 && hole[i + 1] == 0;
            bool u = y > 0 && hole[i - w] == 0, d = y < h - 1 && hole[i + w] == 0;
            float g = 0;
            for (int c = 0; c < 4; c++)
            {
                float v = low[i * 4 + c];
                if (l || rr)
                {
                    float a = rr ? low[(i + 1) * 4 + c] : v, b = l ? low[(i - 1) * 4 + c] : v;
                    g = Math.Max(g, Math.Abs(a - b) / (l && rr ? 2 : 1));
                }
                if (u || d)
                {
                    float a = d ? low[(i + w) * 4 + c] : v, b = u ? low[(i - w) * 4 + c] : v;
                    g = Math.Max(g, Math.Abs(a - b) / (u && d ? 2 : 1));
                }
            }
            if (g > EdgeThreshold && ++edges > allowed) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ membrane (Laplace) solver

    /// <summary>
    /// Harmonic interpolation of 4 interleaved float channels into the pixels where <paramref name="hole"/> != 0
    /// (Dirichlet = known neighbours, Neumann at the buffer border). Coarse-to-fine initialisation + SOR.
    /// </summary>
    internal static void Laplace(float[] v, byte[] hole, int w, int h) => Laplace(v, hole, w, h, 0);

    private static void Laplace(float[] v, byte[] hole, int w, int h, int depth)
    {
        int n = 0, minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (hole[y * w + x] == 0) continue;
            n++;
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        if (n == 0) return;
        var idx = new int[n];
        var nb = new int[n * 4];
        int k = 0;
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            int i = y * w + x;
            if (hole[i] == 0) continue;
            idx[k] = i;
            nb[k * 4] = x > 0 ? i - 1 : -1;
            nb[k * 4 + 1] = x < w - 1 ? i + 1 : -1;
            nb[k * 4 + 2] = y > 0 ? i - w : -1;
            nb[k * 4 + 3] = y < h - 1 ? i + w : -1;
            k++;
        }

        bool coarse = n > 256 && w >= 8 && h >= 8 && depth < 12;
        if (coarse)
        {
            int w2 = (w + 1) / 2, h2 = (h + 1) / 2;
            var v2 = new float[w2 * h2 * 4];
            var hole2 = new byte[w2 * h2];
            for (int y2 = 0; y2 < h2; y2++)
            for (int x2 = 0; x2 < w2; x2++)
            {
                float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                int cnt = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int x = x2 * 2 + dx, y = y2 * 2 + dy;
                    if (x >= w || y >= h) continue;
                    int i = y * w + x;
                    if (hole[i] != 0) continue;
                    s0 += v[i * 4]; s1 += v[i * 4 + 1]; s2 += v[i * 4 + 2]; s3 += v[i * 4 + 3];
                    cnt++;
                }
                int j = y2 * w2 + x2;
                if (cnt == 0) { hole2[j] = 1; continue; }
                v2[j * 4] = s0 / cnt; v2[j * 4 + 1] = s1 / cnt; v2[j * 4 + 2] = s2 / cnt; v2[j * 4 + 3] = s3 / cnt;
            }
            Laplace(v2, hole2, w2, h2, depth + 1);
            // Bilinear upsample as the initial guess.
            for (int t = 0; t < n; t++)
            {
                int i = idx[t], x = i % w, y = i / w;
                double fx = Math.Clamp((x - 0.5) / 2, 0, w2 - 1), fy = Math.Clamp((y - 0.5) / 2, 0, h2 - 1);
                int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(w2 - 1, x0 + 1), y1 = Math.Min(h2 - 1, y0 + 1);
                double ax = fx - x0, ay = fy - y0;
                for (int c = 0; c < 4; c++)
                {
                    double a = v2[(y0 * w2 + x0) * 4 + c] * (1 - ax) + v2[(y0 * w2 + x1) * 4 + c] * ax;
                    double b = v2[(y1 * w2 + x0) * 4 + c] * (1 - ax) + v2[(y1 * w2 + x1) * 4 + c] * ax;
                    v[i * 4 + c] = (float)(a * (1 - ay) + b * ay);
                }
            }
        }
        else
        {
            // Mean of the known neighbours of the hole.
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            int cnt = 0;
            for (int t = 0; t < n * 4; t++)
            {
                int j = nb[t];
                if (j < 0 || hole[j] != 0) continue;
                s0 += v[j * 4]; s1 += v[j * 4 + 1]; s2 += v[j * 4 + 2]; s3 += v[j * 4 + 3];
                cnt++;
            }
            if (cnt > 0)
                for (int t = 0; t < n; t++)
                {
                    int i = idx[t];
                    v[i * 4] = (float)(s0 / cnt); v[i * 4 + 1] = (float)(s1 / cnt); v[i * 4 + 2] = (float)(s2 / cnt); v[i * 4 + 3] = (float)(s3 / cnt);
                }
        }

        // Coarsest level: SOR with the optimal factor for a strip as thick as the hole's smaller extent, to convergence.
        // Finer levels start from the upsampled solution: a short smoothing run removes the remaining fine error.
        int thick = Math.Min(maxX - minX, maxY - minY) + 2;
        double omega = coarse ? 1.5 : Math.Clamp(2 / (1 + Math.Sin(Math.PI / (thick + 1))), 1.0, 1.95);
        int maxIter = coarse ? 40 : 12 * thick + 60;
        float tol = coarse ? 0.01f : 0.002f;
        for (int iter = 0; iter < maxIter; iter++)
        {
            float maxd = 0;
            for (int t = 0; t < n; t++)
            {
                int i = idx[t] * 4;
                float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                int cnt = 0;
                for (int q = 0; q < 4; q++)
                {
                    int j = nb[t * 4 + q];
                    if (j < 0) continue;
                    j *= 4;
                    a0 += v[j]; a1 += v[j + 1]; a2 += v[j + 2]; a3 += v[j + 3];
                    cnt++;
                }
                if (cnt == 0) continue;
                float inv = 1f / cnt;
                float d0 = a0 * inv - v[i], d1 = a1 * inv - v[i + 1], d2 = a2 * inv - v[i + 2], d3 = a3 * inv - v[i + 3];
                v[i] += (float)(omega * d0); v[i + 1] += (float)(omega * d1); v[i + 2] += (float)(omega * d2); v[i + 3] += (float)(omega * d3);
                float m = Math.Max(Math.Max(Math.Abs(d0), Math.Abs(d1)), Math.Max(Math.Abs(d2), Math.Abs(d3)));
                if (m > maxd) maxd = m;
            }
            if (maxd < tol) break;
        }
    }

    /// <summary>Membrane fill of the hole of a BGRA buffer from its current known pixels (fallback).</summary>
    private static void SmoothFillBytes(byte[] img, int w, int h, byte[] hole)
    {
        var v = new float[w * h * 4];
        for (int i = 0; i < v.Length; i++) v[i] = img[i];
        Laplace(v, hole, w, h);
        ToBytes(v, img, hole, w * h);
    }

    // ------------------------------------------------------------------ exemplar inpainting

    /// <summary>Approximate Euclidean distance (chamfer 3-4, divided by 3) of the deepest hole pixel to the nearest known pixel.</summary>
    internal static float MaxHoleRadius(byte[] mask, int w, int h)
    {
        const int Inf = int.MaxValue / 4;
        var d = new int[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = mask[i] != 0 ? Inf : 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (d[i] == 0) continue;
            int m = d[i];
            if (x > 0) m = Math.Min(m, d[i - 1] + 3);
            if (y > 0)
            {
                m = Math.Min(m, d[i - w] + 3);
                if (x > 0) m = Math.Min(m, d[i - w - 1] + 4);
                if (x < w - 1) m = Math.Min(m, d[i - w + 1] + 4);
            }
            d[i] = m;
        }
        int max = 0;
        for (int y = h - 1; y >= 0; y--)
        for (int x = w - 1; x >= 0; x--)
        {
            int i = y * w + x;
            if (d[i] == 0) continue;
            int m = d[i];
            if (x < w - 1) m = Math.Min(m, d[i + 1] + 3);
            if (y < h - 1)
            {
                m = Math.Min(m, d[i + w] + 3);
                if (x < w - 1) m = Math.Min(m, d[i + w + 1] + 4);
                if (x > 0) m = Math.Min(m, d[i + w - 1] + 4);
            }
            d[i] = m;
            if (m < Inf && m > max) max = m;
        }
        return max / 3f;
    }

    private sealed class Level
    {
        public required byte[] Img;
        public required byte[] Hole; // original hole of this level
        public required int W;
        public required int H;
    }

    private static Level Downsample(Level f)
    {
        int w2 = (f.W + 1) / 2, h2 = (f.H + 1) / 2;
        var img = new byte[w2 * h2 * 4];
        var hole = new byte[w2 * h2];
        for (int y2 = 0; y2 < h2; y2++)
        for (int x2 = 0; x2 < w2; x2++)
        {
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0, cnt = 0;
            bool anyHole = false;
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int x = x2 * 2 + dx, y = y2 * 2 + dy;
                if (x >= f.W || y >= f.H) continue;
                int i = y * f.W + x;
                if (f.Hole[i] != 0) { anyHole = true; continue; }
                s0 += f.Img[i * 4]; s1 += f.Img[i * 4 + 1]; s2 += f.Img[i * 4 + 2]; s3 += f.Img[i * 4 + 3];
                cnt++;
            }
            int j = y2 * w2 + x2;
            // Conservative: a coarse pixel is known only if all its children are known.
            if (anyHole) hole[j] = 1;
            if (cnt > 0)
            {
                img[j * 4] = (byte)((s0 + cnt / 2) / cnt); img[j * 4 + 1] = (byte)((s1 + cnt / 2) / cnt);
                img[j * 4 + 2] = (byte)((s2 + cnt / 2) / cnt); img[j * 4 + 3] = (byte)((s3 + cnt / 2) / cnt);
            }
        }
        return new Level { Img = img, Hole = hole, W = w2, H = h2 };
    }

    /// <summary>
    /// Exemplar inpainting of <paramref name="img"/> in place (coarse-to-fine). Returns the full-resolution source map:
    /// for each filled pixel the index of the (originally known) pixel it was copied from, -1 elsewhere / if unfilled.
    /// </summary>
    private static int[] ExemplarPyramid(byte[] img, byte[] mask, int w, int h)
    {
        float rmax = MaxHoleRadius(mask, w, h);
        var levels = new List<Level> { new() { Img = img, Hole = mask, W = w, H = h } };
        while (levels.Count < 6 && rmax / (1 << (levels.Count - 1)) > 10 && levels[^1].W >= 48 && levels[^1].H >= 48)
            levels.Add(Downsample(levels[^1]));
        int[]? src = null;
        Level? coarse = null;
        for (int l = levels.Count - 1; l >= 0; l--)
        {
            var sw = Trace != null ? Stopwatch.StartNew() : null;
            src = Exemplar(levels[l], rmax / (1 << l), coarse, src);
            Trace?.Invoke($"  level {l} ({levels[l].W}x{levels[l].H}): {sw!.ElapsedMilliseconds} ms");
            coarse = levels[l];
        }
        return src!;
    }

    private readonly record struct Match(long Cost, int Dist2, int Index)
    {
        public static readonly Match None = new(long.MaxValue, int.MaxValue, int.MaxValue);
        public bool Better(Match o) => Cost != o.Cost ? Cost < o.Cost : Dist2 != o.Dist2 ? Dist2 < o.Dist2 : Index < o.Index;
    }

    /// <summary>
    /// Criminisi exemplar-based inpainting of one level (in place). Without <paramref name="coarse"/> the best source
    /// patch is searched in a window; with it, only around the upsampled coarse sources and the sources of filled
    /// neighbours. Returns the source map of this level.
    /// </summary>
    private static int[] Exemplar(Level lv, float rmax, Level? coarse, int[]? coarseSrc)
    {
        var img = lv.Img;
        int w = lv.W, h = lv.H, n = w * h;
        int half = Math.Min(w, h) >= 24 ? 4 : Math.Min(w, h) >= 12 ? 2 : 1;
        int side = 2 * half + 1;
        int area = side * side;
        var hole = (byte[])lv.Hole.Clone();
        var src = new int[n];
        Array.Fill(src, -1);
        var conf = new float[n];
        var lum = new float[n];
        int remaining = 0;
        for (int i = 0; i < n; i++)
        {
            if (hole[i] != 0) { remaining++; continue; }
            conf[i] = 1;
            lum[i] = Luma(img, i);
        }

        // Valid source centres: whole patch inside the level and free of (original) hole pixels.
        var integral = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int row = 0;
            for (int x = 0; x < w; x++)
            {
                row += lv.Hole[y * w + x] != 0 ? 1 : 0;
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }
        var validSrc = new bool[n];
        bool anyValid = false;
        for (int y = half; y < h - half; y++)
        for (int x = half; x < w - half; x++)
        {
            int x0 = x - half, y0 = y - half, x1 = x + half + 1, y1 = y + half + 1;
            int s = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
            if (s == 0) { validSrc[y * w + x] = true; anyValid = true; }
        }
        if (!anyValid) return src;

        // Fill front bookkeeping.
        var front = new List<int>();
        var frontPos = new int[n];
        Array.Fill(frontPos, -1);
        var prio = new float[n];

        bool IsFront(int i)
        {
            if (hole[i] == 0) return false;
            int x = i % w, y = i / w;
            return (x > 0 && hole[i - 1] == 0) || (x < w - 1 && hole[i + 1] == 0) || (y > 0 && hole[i - w] == 0) || (y < h - 1 && hole[i + w] == 0);
        }
        void SetFront(int i, bool on)
        {
            if (on && frontPos[i] < 0) { frontPos[i] = front.Count; front.Add(i); }
            else if (!on && frontPos[i] >= 0)
            {
                int pos = frontPos[i], last = front[^1];
                front[pos] = last; frontPos[last] = pos;
                front.RemoveAt(front.Count - 1);
                frontPos[i] = -1;
            }
        }
        int H(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && hole[y * w + x] != 0 ? 1 : 0;
        float Priority(int i)
        {
            int px = i % w, py = i / w;
            float c = 0;
            for (int y = Math.Max(0, py - half); y <= Math.Min(h - 1, py + half); y++)
            for (int x = Math.Max(0, px - half); x <= Math.Min(w - 1, px + half); x++)
                c += conf[y * w + x];
            c /= area;
            // Front normal from the hole indicator (Sobel); isophote = strongest known gradient next to the pixel, rotated 90°.
            float nx = H(px + 1, py - 1) + 2 * H(px + 1, py) + H(px + 1, py + 1) - H(px - 1, py - 1) - 2 * H(px - 1, py) - H(px - 1, py + 1);
            float ny = H(px - 1, py + 1) + 2 * H(px, py + 1) + H(px + 1, py + 1) - H(px - 1, py - 1) - 2 * H(px, py - 1) - H(px + 1, py - 1);
            float nl = MathF.Sqrt(nx * nx + ny * ny);
            float d = 0;
            if (nl > 0)
            {
                nx /= nl; ny /= nl;
                float bgx = 0, bgy = 0, bm = -1;
                for (int y = Math.Max(0, py - 1); y <= Math.Min(h - 1, py + 1); y++)
                for (int x = Math.Max(0, px - 1); x <= Math.Min(w - 1, px + 1); x++)
                {
                    int q = y * w + x;
                    if (hole[q] != 0) continue;
                    float l = lum[q];
                    float lx1 = x < w - 1 && hole[q + 1] == 0 ? lum[q + 1] : l, lx0 = x > 0 && hole[q - 1] == 0 ? lum[q - 1] : l;
                    float ly1 = y < h - 1 && hole[q + w] == 0 ? lum[q + w] : l, ly0 = y > 0 && hole[q - w] == 0 ? lum[q - w] : l;
                    float gx = lx1 - lx0, gy = ly1 - ly0;
                    float m = gx * gx + gy * gy;
                    if (m > bm) { bm = m; bgx = gx; bgy = gy; }
                }
                d = MathF.Abs(-bgy * nx + bgx * ny) / 510f; // |isophote · normal|
            }
            return c * (d + 0.02f);
        }

        for (int i = 0; i < n; i++) if (IsFront(i)) SetFront(i, true);
        foreach (int i in front) prio[i] = Priority(i);

        int searchR = Math.Max(16, (int)MathF.Ceiling(2 * rmax) + 2 * half + 6);
        var tOff = new int[area];
        var tVal = new int[area * 3];
        var guesses = new List<int>(16);

        while (remaining > 0 && front.Count > 0)
        {
            // Highest priority (ties → lowest index, deterministic).
            int p = -1;
            float bp = float.NegativeInfinity;
            foreach (int i in front)
                if (prio[i] > bp || (prio[i] == bp && i < p)) { bp = prio[i]; p = i; }
            int px = p % w, py = p / w;

            // Known pixels of the target patch.
            int cnt = 0;
            for (int dy = -half; dy <= half; dy++)
            {
                int y = py + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -half; dx <= half; dx++)
                {
                    int x = px + dx;
                    if (x < 0 || x >= w) continue;
                    int j = y * w + x;
                    if (hole[j] != 0) continue;
                    tOff[cnt] = dy * w + dx;
                    tVal[cnt * 3] = img[j * 4]; tVal[cnt * 3 + 1] = img[j * 4 + 1]; tVal[cnt * 3 + 2] = img[j * 4 + 2];
                    cnt++;
                }
            }

            int q = -1;
            if (coarse != null && coarseSrc != null)
            {
                guesses.Clear();
                // Upsampled coarse sources of the patch centre and its edge midpoints…
                for (int k = 0; k < 5; k++)
                {
                    int tx = px + (k == 1 ? -half : k == 2 ? half : 0), ty = py + (k == 3 ? -half : k == 4 ? half : 0);
                    if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                    int cx = Math.Min(coarse.W - 1, tx >> 1), cy = Math.Min(coarse.H - 1, ty >> 1);
                    int s = coarseSrc[cy * coarse.W + cx];
                    if (s < 0) continue;
                    int sx = (s % coarse.W) * 2 + (tx & 1) - (tx - px), sy = (s / coarse.W) * 2 + (ty & 1) - (ty - py);
                    if (sx >= 0 && sy >= 0 && sx < w && sy < h) guesses.Add(sy * w + sx);
                }
                // …and the sources of pixels already filled at this level (coherence with the neighbours).
                for (int k = 0; k < 8; k++)
                {
                    int dx = k is 0 or 3 or 5 ? -half : k is 2 or 4 or 7 ? half : 0;
                    int dy = k is 0 or 1 or 2 ? -half : k is 5 or 6 or 7 ? half : 0;
                    int tx = px + dx, ty = py + dy;
                    if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                    int s = src[ty * w + tx];
                    if (s < 0) continue;
                    int sx = s % w - dx, sy = s / w - dy;
                    if (sx >= 0 && sy >= 0 && sx < w && sy < h) guesses.Add(sy * w + sx);
                }
                q = BestGuided(img, validSrc, w, h, px, py, guesses, GuideRadius, tOff, tVal, cnt);
            }
            if (q < 0) q = BestSource(img, validSrc, w, h, px, py, coarse != null ? 2 * half + 8 : searchR, tOff, tVal, cnt);
            if (q < 0) q = BestSource(img, validSrc, w, h, px, py, Math.Max(w, h), tOff, tVal, cnt);
            if (q < 0) break;

            // Copy the missing pixels.
            float cp = 0;
            for (int y = Math.Max(0, py - half); y <= Math.Min(h - 1, py + half); y++)
            for (int x = Math.Max(0, px - half); x <= Math.Min(w - 1, px + half); x++)
                cp += conf[y * w + x];
            cp /= area;
            int qx = q % w, qy = q / w;
            for (int dy = -half; dy <= half; dy++)
            {
                int y = py + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -half; dx <= half; dx++)
                {
                    int x = px + dx;
                    if (x < 0 || x >= w) continue;
                    int j = y * w + x;
                    if (hole[j] == 0) continue;
                    int s = (qy + dy) * w + qx + dx;
                    Buffer.BlockCopy(img, s * 4, img, j * 4, 4);
                    src[j] = s;
                    hole[j] = 0;
                    conf[j] = cp;
                    lum[j] = Luma(img, j);
                    remaining--;
                }
            }

            // Update the front around the patch and the priorities of front pixels whose patch overlaps it.
            for (int y = Math.Max(0, py - half - 1); y <= Math.Min(h - 1, py + half + 1); y++)
            for (int x = Math.Max(0, px - half - 1); x <= Math.Min(w - 1, px + half + 1); x++)
            {
                int j = y * w + x;
                SetFront(j, IsFront(j));
            }
            int r2 = 2 * half + 1;
            for (int y = Math.Max(0, py - r2); y <= Math.Min(h - 1, py + r2); y++)
            for (int x = Math.Max(0, px - r2); x <= Math.Min(w - 1, px + r2); x++)
            {
                int j = y * w + x;
                if (frontPos[j] >= 0) prio[j] = Priority(j);
            }
        }
        return src;
    }

    private static float Luma(byte[] img, int i) => 0.114f * img[i * 4] + 0.587f * img[i * 4 + 1] + 0.299f * img[i * 4 + 2];

    private static long Ssd(byte[] img, int q, int[] tOff, int[] tVal, int cnt, long limit)
    {
        long ssd = 0;
        for (int t = 0; t < cnt; t++)
        {
            int j = (q + tOff[t]) * 4;
            int d0 = img[j] - tVal[t * 3], d1 = img[j + 1] - tVal[t * 3 + 1], d2 = img[j + 2] - tVal[t * 3 + 2];
            ssd += d0 * d0 + d1 * d1 + d2 * d2;
            if ((t & 7) == 7 && ssd > limit) return long.MaxValue;
        }
        return ssd;
    }

    /// <summary>Best valid source centre within ±<paramref name="r"/> of each guess.</summary>
    private static int BestGuided(byte[] img, bool[] validSrc, int w, int h, int px, int py, List<int> guesses, int r,
        int[] tOff, int[] tVal, int cnt)
    {
        var best = Match.None;
        for (int g = 0; g < guesses.Count; g++)
        {
            int gx = guesses[g] % w, gy = guesses[g] / w;
            bool seen = false;
            for (int k = 0; k < g && !seen; k++) seen = guesses[k] == guesses[g];
            if (seen) continue;
            for (int y = Math.Max(0, gy - r); y <= Math.Min(h - 1, gy + r); y++)
            for (int x = Math.Max(0, gx - r); x <= Math.Min(w - 1, gx + r); x++)
            {
                int q = y * w + x;
                if (!validSrc[q]) continue;
                long ssd = Ssd(img, q, tOff, tVal, cnt, best.Cost);
                if (ssd == long.MaxValue) continue;
                var m = new Match(ssd, (x - px) * (x - px) + (y - py) * (y - py), q);
                if (m.Better(best)) best = m;
            }
        }
        return best.Index == int.MaxValue ? -1 : best.Index;
    }

    /// <summary>Best fully-known source patch centre within ±<paramref name="radius"/> of (px,py) by SSD over the target's known pixels.</summary>
    private static int BestSource(byte[] img, bool[] validSrc, int w, int h, int px, int py, int radius,
        int[] tOff, int[] tVal, int cnt)
    {
        int y0 = Math.Max(0, py - radius), y1 = Math.Min(h - 1, py + radius);
        int x0 = Math.Max(0, px - radius), x1 = Math.Min(w - 1, px + radius);
        var best = Match.None;
        var gate = new object();

        Match Row(int y, Match local)
        {
            int dy = y - py;
            for (int x = x0; x <= x1; x++)
            {
                int q = y * w + x;
                if (!validSrc[q]) continue;
                long ssd = Ssd(img, q, tOff, tVal, cnt, local.Cost);
                if (ssd == long.MaxValue) continue;
                int dx = x - px;
                var m = new Match(ssd, dx * dx + dy * dy, q);
                if (m.Better(local)) local = m;
            }
            return local;
        }

        long work = (long)(y1 - y0 + 1) * (x1 - x0 + 1) * Math.Max(1, cnt);
        if (work < 60_000)
        {
            for (int y = y0; y <= y1; y++) best = Row(y, best);
        }
        else
        {
            Parallel.For(y0, y1 + 1, () => Match.None, (y, _, local) => Row(y, local), local =>
            {
                lock (gate) { if (local.Better(best)) best = local; }
            });
        }
        return best.Index == int.MaxValue ? -1 : best.Index;
    }
}
