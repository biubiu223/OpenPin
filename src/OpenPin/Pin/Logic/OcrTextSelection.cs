using System.Text;
using System.Windows;
using OpenPin.Services;

namespace OpenPin.Pin.Logic;

/// <summary>A recognized word in reading order (<see cref="Line"/> = index of its line).</summary>
public sealed record OcrWordRef(string Text, Rect Bounds, int Line);

/// <summary>
/// Text selection on image pins (PIN-08): words are kept in reading order, a selection is the inclusive range of word
/// indices between the drag start and the drag end.
/// </summary>
public static class OcrTextSelection
{
    public static IReadOnlyList<OcrWordRef> Flatten(OcrResult? result)
    {
        var list = new List<OcrWordRef>();
        if (result == null) return list;
        for (int li = 0; li < result.Lines.Count; li++)
        {
            var line = result.Lines[li];
            if (line.Words.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(line.Text)) list.Add(new OcrWordRef(line.Text, line.Bounds, li));
                continue;
            }
            foreach (var w in line.Words) list.Add(new OcrWordRef(w.Text, w.Bounds, li));
        }
        return list;
    }

    /// <summary>Index of the word containing <paramref name="p"/> (bounds inflated by <paramref name="tolerance"/>), or -1.</summary>
    public static int HitTest(IReadOnlyList<OcrWordRef> words, Point p, double tolerance = 2)
    {
        for (int i = 0; i < words.Count; i++)
        {
            var r = words[i].Bounds;
            r.Inflate(tolerance, tolerance);
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Word to extend a selection to while dragging: the word under the point, else the nearest word on the nearest
    /// line (by vertical distance, then horizontal distance).
    /// </summary>
    public static int Nearest(IReadOnlyList<OcrWordRef> words, Point p)
    {
        if (words.Count == 0) return -1;
        int hit = HitTest(words, p);
        if (hit >= 0) return hit;
        int best = 0;
        double bestDy = double.MaxValue, bestDx = double.MaxValue;
        for (int i = 0; i < words.Count; i++)
        {
            var r = words[i].Bounds;
            double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
            if (dy < bestDy - 0.5 || (Math.Abs(dy - bestDy) <= 0.5 && dx < bestDx))
            {
                best = i; bestDy = dy; bestDx = dx;
            }
        }
        return best;
    }

    /// <summary>Text of words [a..b] (any order). Same-line words are joined with a space unless CJK; lines with '\n'.</summary>
    public static string Join(IReadOnlyList<OcrWordRef> words, int a, int b)
    {
        if (words.Count == 0 || a < 0 || b < 0) return "";
        int from = Math.Min(a, b), to = Math.Min(Math.Max(a, b), words.Count - 1);
        var sb = new StringBuilder();
        for (int i = from; i <= to; i++)
        {
            var w = words[i];
            if (i > from)
            {
                var prev = words[i - 1];
                if (prev.Line != w.Line) sb.Append('\n');
                else if (NeedsSpace(prev.Text, w.Text)) sb.Append(' ');
            }
            sb.Append(w.Text);
        }
        return sb.ToString();
    }

    public static bool NeedsSpace(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return false;
        return !IsCjk(left[^1]) && !IsCjk(right[0]);
    }

    public static bool IsCjk(char c) =>
        (c >= 0x3000 && c <= 0x303F) || // CJK punctuation
        (c >= 0x3040 && c <= 0x30FF) || // kana
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0xAC00 && c <= 0xD7AF) || // hangul
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFF00 && c <= 0xFFEF);   // full-width forms
}
