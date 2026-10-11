namespace OpenPin.Recognition;

/// <summary>A straight table ruling line (image px): position across, extent along, thickness.</summary>
public sealed record RulingLine(bool Horizontal, double Position, double Start, double End, int Thickness)
{
    public double Length => End - Start;
}

/// <summary>
/// Finds table ruling lines: long horizontal / vertical runs of pixels that contrast with the dominant (background)
/// luminance, at most a few px thick. Small gaps (anti-aliasing, dashed borders) are bridged. Pure, unit tested.
/// </summary>
public static class RulingLineDetector
{
    private const int InkThreshold = 40;
    private const int MaxGap = 2;
    private const int MaxThickness = 5;

    /// <param name="bgra">Top-down BGRA pixels, stride = width × 4.</param>
    public static IReadOnlyList<RulingLine> Detect(byte[] bgra, int width, int height)
    {
        if (width < 8 || height < 8 || bgra.Length < width * height * 4) return Array.Empty<RulingLine>();
        var ink = InkMask(bgra, width, height);
        var list = new List<RulingLine>();
        int minH = Math.Max(24, (int)(width * 0.2));
        int minV = Math.Max(16, (int)(height * 0.2));
        list.AddRange(Scan(ink, width, height, horizontal: true, minH));
        list.AddRange(Scan(ink, width, height, horizontal: false, minV));
        return list;
    }

    /// <summary>Pixels whose luminance differs clearly from the most common (background) luminance.</summary>
    internal static bool[] InkMask(byte[] bgra, int width, int height)
    {
        int n = width * height;
        var lum = new byte[n];
        var hist = new int[256];
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            int a = bgra[p + 3];
            // flatten on white
            int b = (bgra[p] * a + 255 * (255 - a)) / 255, g = (bgra[p + 1] * a + 255 * (255 - a)) / 255, r = (bgra[p + 2] * a + 255 * (255 - a)) / 255;
            int l = (r * 299 + g * 587 + b * 114) / 1000;
            lum[i] = (byte)l;
            hist[l]++;
        }
        // Mode of a lightly smoothed histogram = background.
        int bg = 0, best = -1;
        for (int v = 0; v < 256; v++)
        {
            int s = hist[v] + (v > 0 ? hist[v - 1] : 0) + (v < 255 ? hist[v + 1] : 0);
            if (s > best) { best = s; bg = v; }
        }
        var ink = new bool[n];
        for (int i = 0; i < n; i++) ink[i] = Math.Abs(lum[i] - bg) > InkThreshold;
        return ink;
    }

    private sealed class Candidate
    {
        public int First, Last; // across (rows for horizontal lines)
        public int Start, End;  // along, inclusive
    }

    private static IEnumerable<RulingLine> Scan(bool[] ink, int width, int height, bool horizontal, int minLength)
    {
        int across = horizontal ? height : width, along = horizontal ? width : height;
        var open = new List<Candidate>();
        var done = new List<Candidate>();
        for (int a = 0; a < across; a++)
        {
            var runs = Runs(ink, width, a, along, horizontal, minLength);
            var next = new List<Candidate>();
            foreach (var (s, e) in runs)
            {
                var c = open.FirstOrDefault(o => o.Last == a - 1 && Overlap(o.Start, o.End, s, e) >= 0.8 * Math.Min(o.End - o.Start, e - s));
                if (c != null)
                {
                    open.Remove(c);
                    c.Last = a;
                    c.Start = Math.Min(c.Start, s);
                    c.End = Math.Max(c.End, e);
                }
                else c = new Candidate { First = a, Last = a, Start = s, End = e };
                next.Add(c);
            }
            done.AddRange(open); // not continued on this row → finished
            open = next;
        }
        done.AddRange(open);
        foreach (var c in done)
        {
            int thick = c.Last - c.First + 1;
            if (thick > MaxThickness) continue; // filled area, not a line
            yield return new RulingLine(horizontal, (c.First + c.Last) / 2.0 + 0.5, c.Start, c.End + 1, thick);
        }
    }

    private static int Overlap(int s1, int e1, int s2, int e2) => Math.Min(e1, e2) - Math.Max(s1, s2);

    /// <summary>Runs of ink along one row / column (gaps ≤ <see cref="MaxGap"/> bridged), at least <paramref name="minLength"/> long.</summary>
    private static List<(int Start, int End)> Runs(bool[] ink, int width, int a, int along, bool horizontal, int minLength)
    {
        var runs = new List<(int, int)>();
        int start = -1, lastInk = -1;
        for (int i = 0; i <= along; i++)
        {
            bool on = i < along && (horizontal ? ink[a * width + i] : ink[i * width + a]);
            if (on)
            {
                if (start < 0) start = i;
                lastInk = i;
            }
            else if (start >= 0 && i - lastInk > MaxGap || i == along)
            {
                if (start >= 0 && lastInk - start + 1 >= minLength) runs.Add((start, lastInk));
                start = -1;
            }
        }
        return runs;
    }
}
