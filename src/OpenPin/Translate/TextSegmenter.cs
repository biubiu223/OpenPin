using System.Text.RegularExpressions;
using System.Windows;
using OpenPin.Services;

namespace OpenPin.Translate;

/// <summary>A paragraph of OCR text: joined text, union bounds and the bounds of its lines (image px).</summary>
public sealed record TextSegment(string Text, Rect Bounds, IReadOnlyList<Rect> LineBounds)
{
    /// <summary>Median line height (px), ≈ the original font size × 1.2.</summary>
    public double LineHeight => LineBounds.Count == 0 ? Bounds.Height : LineBounds.Select(r => r.Height).OrderBy(x => x).ElementAt(LineBounds.Count / 2);
}

/// <summary>
/// Groups OCR lines into paragraphs by geometry (pure, unit tested). Unlike plain-text layout this works per column:
/// a line continues the paragraph whose last line sits directly above it (small gap, overlapping horizontally, same
/// height, same left edge) when that last line was long and reached the paragraph's right edge (i.e. it wrapped).
/// </summary>
public static class TextSegmenter
{
    private static readonly Regex ListItem = new(@"^([-•·*▪●]|\d+[.、)）]|[（(]?\d+[)）])\s*", RegexOptions.Compiled);

    public static IReadOnlyList<TextSegment> Paragraphs(IReadOnlyList<OcrLine> lines)
    {
        var paras = new List<List<OcrLine>>();
        var bounds = new List<Rect>();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Bounds.Top))
        {
            int best = -1;
            double bestGap = double.MaxValue;
            for (int i = 0; i < paras.Count; i++)
            {
                var last = paras[i][^1].Bounds;
                if (!Continues(last, bounds[i], line)) continue;
                double gap = line.Bounds.Top - last.Bottom;
                if (gap < bestGap) { best = i; bestGap = gap; }
            }
            if (best >= 0)
            {
                paras[best].Add(line);
                bounds[best] = Rect.Union(bounds[best], line.Bounds);
            }
            else
            {
                paras.Add(new List<OcrLine> { line });
                bounds.Add(line.Bounds);
            }
        }

        // Reading order: by the first line (top, then left for lines on the same row).
        return paras
            .Select((p, i) => (P: p, B: bounds[i]))
            .OrderBy(x => Math.Round(x.P[0].Bounds.Top / Math.Max(4, x.P[0].Bounds.Height * 0.6)))
            .ThenBy(x => x.P[0].Bounds.Left)
            .Select(x => new TextSegment(
                x.P.Skip(1).Aggregate(x.P[0].Text.Trim(), (acc, l) => OcrTextProcessor.JoinPieces(acc, l.Text.Trim())),
                x.B,
                x.P.Select(l => l.Bounds).ToList()))
            .ToList();
    }

    private static bool Continues(Rect last, Rect para, OcrLine line)
    {
        var cur = line.Bounds;
        double h = Math.Max(1, Math.Min(last.Height, cur.Height));
        double gap = cur.Top - last.Bottom;
        if (gap < -0.3 * h || gap > 0.9 * h) return false;                                   // not directly below
        if (Math.Max(last.Height, cur.Height) / h > 1.5) return false;                         // different font size
        double overlap = Math.Min(last.Right, cur.Right) - Math.Max(last.Left, cur.Left);
        if (overlap < 0.3 * Math.Min(last.Width, cur.Width)) return false;                     // other column
        if (Math.Abs(cur.Left - last.Left) > Math.Max(2 * h, 0.1 * para.Width)) return false;  // indent change
        if (last.Width < 6 * h) return false;                                                  // too short to have wrapped
        double right = Math.Max(para.Right, cur.Right);
        if (right - last.Right > Math.Max(2.5 * h, 0.2 * para.Width)) return false;            // previous line ended early
        if (ListItem.IsMatch(line.Text.TrimStart())) return false;
        return true;
    }
}
