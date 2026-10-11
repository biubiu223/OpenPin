using System.Windows;
using OpenPin.Services;

namespace OpenPin.Recognition;

/// <summary>An OCR word with its bounds (image px).</summary>
public sealed record WordBox(string Text, Rect Bounds)
{
    public double CenterX => Bounds.X + Bounds.Width / 2;
    public double CenterY => Bounds.Y + Bounds.Height / 2;
}

/// <summary>
/// Rebuilds a table from OCR word boxes plus optional ruling lines (PRO-02). Pure, unit tested.
/// <list type="bullet">
/// <item><b>Grid</b> (≥ 2 horizontal and ≥ 2 vertical lines): rows / columns are the line positions; two neighbouring
/// cells are merged when the line segment between them is missing.</item>
/// <item><b>Rules only</b> (horizontal lines, no vertical): rows from the lines, columns from word alignment.</item>
/// <item><b>Words only</b>: rows by vertical overlap; words are grouped into chunks by gaps; column separators are the
/// x-ranges no chunk of a "typical" row covers; a chunk crossing separators becomes a merged cell.</item>
/// </list>
/// </summary>
public static class TableReconstructor
{
    public static IReadOnlyList<WordBox> WordsOf(OcrResult result) =>
        result.Lines.SelectMany(l => l.Words.Count > 0 ? l.Words.Select(w => new WordBox(w.Text, w.Bounds)) : new[] { new WordBox(l.Text, l.Bounds) })
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Bounds.Width > 0 && w.Bounds.Height > 0)
            .ToList();

    public static TableData Build(IReadOnlyList<WordBox> words, IReadOnlyList<RulingLine>? lines = null)
    {
        words = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && !w.Bounds.IsEmpty).ToList();
        var all = (lines ?? Array.Empty<RulingLine>()).Where(l => !CrossesText(l, words)).ToList();
        var hSegs = all.Where(l => l.Horizontal).ToList();
        var vSegs = all.Where(l => !l.Horizontal).ToList();
        var hPos = Cluster(hSegs.Select(l => l.Position));
        var vPos = Cluster(vSegs.Select(l => l.Position));
        double unit = MedianHeight(words);

        if (hPos.Count >= 2 && vPos.Count >= 2) return BuildGrid(words, hSegs, vSegs, hPos, vPos, unit);
        if (words.Count == 0) return new TableData(0, 0);
        var rowBounds = hPos.Count >= 2 ? WithWordExtents(hPos, words.Select(w => (w.Bounds.Top, w.Bounds.Bottom)), unit) : null;
        return BuildFromWords(words, rowBounds, unit);
    }

    // ================================================================== grid mode

    private static TableData BuildGrid(IReadOnlyList<WordBox> words, List<RulingLine> hSegs, List<RulingLine> vSegs,
        List<double> hPos, List<double> vPos, double unit)
    {
        var rows = WithWordExtents(hPos, words.Select(w => (w.Bounds.Top, w.Bounds.Bottom)), unit);
        var cols = WithWordExtents(vPos, words.Select(w => (w.Bounds.Left, w.Bounds.Right)), unit);
        int nr = rows.Count - 1, nc = cols.Count - 1;
        if (nr <= 0 || nc <= 0) return new TableData(0, 0);

        // Union-find over cells; join neighbours whose separating segment is missing.
        var parent = Enumerable.Range(0, nr * nc).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
        double tol = Math.Max(3, unit * 0.3);
        for (int r = 0; r < nr; r++)
        for (int c = 0; c < nc; c++)
        {
            if (c + 1 < nc && !Covered(vSegs, cols[c + 1], rows[r], rows[r + 1], tol)) Union(r * nc + c, r * nc + c + 1);
            if (r + 1 < nr && !Covered(hSegs, rows[r + 1], cols[c], cols[c + 1], tol)) Union(r * nc + c, (r + 1) * nc + c);
        }

        // Each component → bounding rectangle (first come, first served if rectangles would overlap).
        var taken = new bool[nr, nc];
        var merges = new List<CellMerge>();
        var anchorOf = new (int R, int C)[nr * nc];
        foreach (var group in Enumerable.Range(0, nr * nc).GroupBy(Find).OrderBy(g => g.Key))
        {
            int r0 = group.Min(i => i / nc), r1 = group.Max(i => i / nc), c0 = group.Min(i => i % nc), c1 = group.Max(i => i % nc);
            bool free = true;
            for (int r = r0; r <= r1 && free; r++)
            for (int c = c0; c <= c1; c++)
                if (taken[r, c]) { free = false; break; }
            if (free)
            {
                for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    taken[r, c] = true;
                    anchorOf[r * nc + c] = (r0, c0);
                }
                if (r1 > r0 || c1 > c0) merges.Add(new CellMerge(r0, c0, r1 - r0 + 1, c1 - c0 + 1));
            }
            else
            {
                // Irregular shape overlapping another merge: keep its free cells as plain cells.
                foreach (var i in group)
                    if (!taken[i / nc, i % nc]) { taken[i / nc, i % nc] = true; anchorOf[i] = (i / nc, i % nc); }
            }
        }

        var table = new TableData(nr, nc, merges);
        var buckets = new Dictionary<(int, int), List<WordBox>>();
        foreach (var w in words)
        {
            int r = Index(rows, w.CenterY), c = Index(cols, w.CenterX);
            if (r < 0 || c < 0) continue;
            var a = anchorOf[r * nc + c];
            if (!buckets.TryGetValue(a, out var list)) buckets[a] = list = new List<WordBox>();
            list.Add(w);
        }
        foreach (var ((r, c), list) in buckets) table[r, c] = JoinCellText(list, unit);
        return table;
    }

    /// <summary>Is the boundary at <paramref name="pos"/> drawn between <paramref name="from"/> and <paramref name="to"/>?</summary>
    private static bool Covered(List<RulingLine> segs, double pos, double from, double to, double tol)
    {
        // Synthetic outer boundaries (no line at all at this position) count as present.
        var at = segs.Where(s => Math.Abs(s.Position - pos) <= 3).ToList();
        if (at.Count == 0) return true;
        double need = to - from - 2 * tol;
        if (need <= 0) return true;
        double covered = 0;
        foreach (var s in at)
            covered += Math.Max(0, Math.Min(s.End, to - tol) - Math.Max(s.Start, from + tol));
        return covered >= need * 0.6;
    }

    // ================================================================== word modes

    private sealed class Chunk
    {
        public readonly List<WordBox> Words = new();
        public Rect Bounds = Rect.Empty;
        public void Add(WordBox w) { Words.Add(w); Bounds = Bounds.IsEmpty ? w.Bounds : Rect.Union(Bounds, w.Bounds); }
    }

    private static TableData BuildFromWords(IReadOnlyList<WordBox> words, List<double>? rowBounds, double unit)
    {
        // ---- rows
        List<List<WordBox>> rows;
        if (rowBounds != null)
        {
            rows = Enumerable.Range(0, rowBounds.Count - 1).Select(_ => new List<WordBox>()).ToList();
            foreach (var w in words)
            {
                int r = Index(rowBounds, w.CenterY);
                if (r >= 0) rows[r].Add(w);
            }
        }
        else rows = ClusterRows(words);

        // ---- chunks (words separated by small gaps belong to the same cell)
        double gapLimit = Math.Max(4, unit * 0.9);
        var rowChunks = rows.Select(r => Chunks(r, gapLimit)).ToList();

        // ---- column separators from rows with a typical number of chunks
        var counts = rowChunks.Where(c => c.Count > 0).Select(c => c.Count).OrderBy(x => x).ToList();
        if (counts.Count == 0) return new TableData(rows.Count, 0);
        int median = counts[counts.Count / 2];
        var typical = rowChunks.Where(c => c.Count >= Math.Max(1, median)).SelectMany(c => c).ToList();
        var cols = ColumnBoundaries(typical, rowChunks.SelectMany(c => c).ToList(), Math.Max(3, unit * 0.5));
        int nc = cols.Count - 1;

        // ---- cells (drop rows that ended up empty, e.g. between two rules without text)
        var keptRows = rowBounds != null ? rowChunks : rowChunks.Where(c => c.Count > 0).ToList();
        int nr = keptRows.Count;
        var merges = new List<CellMerge>();
        var buckets = new Dictionary<(int, int), List<WordBox>>();
        for (int r = 0; r < nr; r++)
        {
            foreach (var ch in keptRows[r])
            {
                int c0 = Index(cols, ch.Bounds.Left + Math.Min(2, ch.Bounds.Width / 4));
                int c1 = Index(cols, ch.Bounds.Right - Math.Min(2, ch.Bounds.Width / 4));
                if (c0 < 0) c0 = 0;
                if (c1 < c0) c1 = c0;
                if (c1 > c0 && !merges.Any(m => m.Row == r && m.Column <= c1 && c0 < m.Column + m.ColumnSpan))
                    merges.Add(new CellMerge(r, c0, 1, c1 - c0 + 1));
                var key = (r, c0);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<WordBox>();
                list.AddRange(ch.Words);
            }
        }
        var table = new TableData(nr, nc, merges);
        foreach (var ((r, c), list) in buckets) table[r, c] = JoinCellText(list, unit);
        return table;
    }

    /// <summary>Rows by vertical overlap: a word joins the current row when it overlaps at least half its height.</summary>
    private static List<List<WordBox>> ClusterRows(IReadOnlyList<WordBox> words)
    {
        var rows = new List<(double Top, double Bottom, List<WordBox> Words)>();
        foreach (var w in words.OrderBy(w => w.CenterY))
        {
            int hit = -1;
            for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 3; i--)
            {
                var (top, bottom, _) = rows[i];
                double overlap = Math.Min(bottom, w.Bounds.Bottom) - Math.Max(top, w.Bounds.Top);
                if (overlap >= 0.5 * Math.Min(w.Bounds.Height, bottom - top)) { hit = i; break; }
            }
            if (hit < 0) rows.Add((w.Bounds.Top, w.Bounds.Bottom, new List<WordBox> { w }));
            else
            {
                var r = rows[hit];
                r.Words.Add(w);
                // Keep the band anchored on the row's median extent so a tall word does not swallow the next row.
                var tops = r.Words.Select(x => x.Bounds.Top).OrderBy(x => x).ToList();
                var bottoms = r.Words.Select(x => x.Bounds.Bottom).OrderBy(x => x).ToList();
                rows[hit] = (tops[tops.Count / 2], bottoms[bottoms.Count / 2], r.Words);
            }
        }
        return rows.OrderBy(r => r.Top).Select(r => r.Words).ToList();
    }

    private static List<Chunk> Chunks(List<WordBox> row, double gapLimit)
    {
        var chunks = new List<Chunk>();
        Chunk? cur = null;
        foreach (var w in row.OrderBy(w => w.Bounds.Left))
        {
            if (cur == null || w.Bounds.Left - cur.Bounds.Right > gapLimit)
            {
                cur = new Chunk();
                chunks.Add(cur);
            }
            cur.Add(w);
        }
        return chunks;
    }

    /// <summary>Boundaries [left, sep…, right]: separators sit in the middle of x-ranges no typical chunk covers.</summary>
    private static List<double> ColumnBoundaries(List<Chunk> typical, List<Chunk> all, double minGap)
    {
        double left = all.Min(c => c.Bounds.Left), right = all.Max(c => c.Bounds.Right);
        var spans = typical.Select(c => (c.Bounds.Left, c.Bounds.Right)).OrderBy(s => s.Left).ToList();
        var bounds = new List<double> { left - 1 };
        double reach = double.NegativeInfinity;
        foreach (var (l, r) in spans)
        {
            if (reach > double.NegativeInfinity && l - reach >= minGap) bounds.Add((reach + l) / 2);
            reach = Math.Max(reach, r);
        }
        bounds.Add(right + 1);
        return bounds;
    }

    // ================================================================== helpers

    /// <summary>Horizontal rules crossing the middle of a word (strokes of large text) are not table lines.</summary>
    private static bool CrossesText(RulingLine l, IReadOnlyList<WordBox> words)
    {
        foreach (var w in words)
        {
            var b = w.Bounds;
            if (l.Horizontal)
            {
                if (l.Position > b.Top + b.Height * 0.2 && l.Position < b.Bottom - b.Height * 0.2 &&
                    l.Start < b.Right && l.End > b.Left && l.Length < b.Width * 3) return true;
            }
            else if (l.Position > b.Left + b.Width * 0.2 && l.Position < b.Right - b.Width * 0.2 &&
                     l.Start < b.Bottom && l.End > b.Top && l.Length < b.Height * 3) return true;
        }
        return false;
    }

    /// <summary>Sorted positions with near-duplicates (≤ 3 px, double lines) merged.</summary>
    private static List<double> Cluster(IEnumerable<double> positions)
    {
        var result = new List<double>();
        foreach (var p in positions.OrderBy(p => p))
        {
            if (result.Count > 0 && p - result[^1] <= 3) result[^1] = (result[^1] + p) / 2;
            else result.Add(p);
        }
        return result;
    }

    /// <summary>Add outer boundaries when text lies beyond the first / last line (tables without an outer border).</summary>
    private static List<double> WithWordExtents(List<double> pos, IEnumerable<(double Lo, double Hi)> extents, double unit)
    {
        var list = new List<double>(pos);
        var e = extents.ToList();
        if (e.Count == 0) return list;
        double lo = e.Min(x => x.Lo), hi = e.Max(x => x.Hi);
        double margin = Math.Max(2, unit * 0.25);
        if (e.Any(x => (x.Lo + x.Hi) / 2 < list[0])) list.Insert(0, lo - margin);
        if (e.Any(x => (x.Lo + x.Hi) / 2 > list[^1])) list.Add(hi + margin);
        return list;
    }

    /// <summary>Interval index of <paramref name="v"/> in sorted <paramref name="bounds"/>, or -1 outside.</summary>
    private static int Index(List<double> bounds, double v)
    {
        if (bounds.Count < 2 || v < bounds[0] || v > bounds[^1]) return -1;
        for (int i = 0; i < bounds.Count - 1; i++)
            if (v <= bounds[i + 1]) return i;
        return bounds.Count - 2;
    }

    private static double MedianHeight(IReadOnlyList<WordBox> words)
    {
        if (words.Count == 0) return 12;
        var hs = words.Select(w => w.Bounds.Height).OrderBy(h => h).ToList();
        return Math.Max(4, hs[hs.Count / 2]);
    }

    /// <summary>Words of one cell in reading order: lines by vertical overlap, words by x; CJK-aware joining.</summary>
    internal static string JoinCellText(List<WordBox> words, double unit)
    {
        var lines = ClusterRows(words);
        var parts = lines.Select(l => l.OrderBy(w => w.Bounds.Left)
            .Aggregate("", (acc, w) => OcrTextProcessor.JoinPieces(acc, w.Text.Trim())));
        return parts.Aggregate("", OcrTextProcessor.JoinPieces).Trim();
    }
}
