using System.Windows;
using OpenPin.Services;

namespace OpenPin.Recognition;

/// <summary>
/// PRO-12: rectangle of the OCR text under a point (image px) — used as the annotation surface's snap provider on pins
/// so that right-click fits a rectangle / highlighter / mosaic to a text line (or a single word). Pure, unit tested.
/// </summary>
public static class TextSnap
{
    /// <param name="wholeLine">true: the whole OCR line; false: only the word under the point.</param>
    /// <param name="pad">Padding added around the OCR box (OCR boxes are tight around the glyphs).</param>
    public static Rect? Find(OcrResult? ocr, Point p, bool wholeLine, double pad = 2)
    {
        if (ocr == null || ocr.IsEmpty) return null;
        // 1) a word under the point
        foreach (var line in ocr.Lines)
        {
            foreach (var w in line.Words)
            {
                var r = w.Bounds;
                r.Inflate(2, 2);
                if (r.Contains(p)) return Pad(wholeLine ? line.Bounds : w.Bounds, pad);
            }
        }
        // 2) between the words of a line (spaces, punctuation gaps)
        foreach (var line in ocr.Lines)
        {
            var r = line.Bounds;
            r.Inflate(2, Math.Max(2, r.Height * 0.25));
            if (!r.Contains(p)) continue;
            if (wholeLine || line.Words.Count == 0) return Pad(line.Bounds, pad);
            var nearest = line.Words.OrderBy(w => Distance(w.Bounds, p)).First();
            return Pad(nearest.Bounds, pad);
        }
        return null;
    }

    private static double Distance(Rect r, Point p)
    {
        double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
        double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
        return dx * dx + dy * dy;
    }

    private static Rect Pad(Rect r, double pad)
    {
        r.Inflate(pad, pad);
        return r;
    }
}
