using System.Text;
using System.Windows;

namespace OpenPin.Services;

/// <summary>Pure text post-processing for OCR results (no WinRT). Unit tested.</summary>
public static class OcrTextProcessor
{
    /// <summary>Han, kana, CJK symbols/punctuation, full-width forms. Hangul is excluded on purpose (Korean uses spaces).</summary>
    public static bool IsCjk(char c) =>
        c is >= '一' and <= '鿿'     // CJK unified ideographs
          or >= '㐀' and <= '䶿'     // extension A
          or >= '豈' and <= '﫿'     // compatibility ideographs
          or >= '　' and <= '〿'     // CJK symbols & punctuation
          or >= '぀' and <= 'ヿ'     // hiragana / katakana
          or >= 'ㇰ' and <= 'ㇿ'     // katakana extensions
          or >= '＀' and <= '￯'     // half/full-width forms
          or >= '︰' and <= '﹏'     // CJK compatibility forms
          || char.IsSurrogate(c);            // ext. B+ ideographs (approximation)

    /// <summary>
    /// Remove the spaces Windows OCR inserts between CJK characters ("你 好 ， 世 界" → "你好，世界").
    /// A run of spaces is removed when both neighbours are CJK; spaces between Latin words (and between CJK and Latin) are kept.
    /// </summary>
    public static string RemoveCjkSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == ' ')
            {
                int j = i;
                while (j < text.Length && text[j] == ' ') j++;
                char prev = sb.Length > 0 ? sb[^1] : '\0';
                char next = j < text.Length ? text[j] : '\0';
                bool drop = prev != '\0' && next != '\0' && IsCjk(prev) && IsCjk(next);
                if (!drop) sb.Append(text, i, j - i);
                i = j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // Full-width → half-width punctuation that has no 1:1 FFxx mapping.
    private static readonly Dictionary<char, char> ToHalfMap = new()
    {
        ['。'] = '.', ['、'] = ',', ['“'] = '"', ['”'] = '"', ['‘'] = '\'', ['’'] = '\'',
        ['【'] = '[', ['】'] = ']', ['《'] = '<', ['》'] = '>', ['「'] = '"', ['」'] = '"', ['『'] = '"', ['』'] = '"',
        ['〔'] = '(', ['〕'] = ')', ['…'] = '.', ['—'] = '-', ['～'] = '~', ['　'] = ' ',
    };

    /// <summary>Full-width letters/digits/punctuation → ASCII ("ＡＢＣ，１２３。" → "ABC,123.").</summary>
    public static string ToHalfWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= '！' and <= '～') sb.Append((char)(c - 0xFEE0));
            else if (c == '…') sb.Append("...");
            else if (ToHalfMap.TryGetValue(c, out var h)) sb.Append(h);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// ASCII punctuation → full-width (Chinese) punctuation (", . : ; ! ? ( )" → "，。：；！？（）").
    /// Letters and digits are kept; '.' / ':' / ',' between digits (3.14, 12:30, 1,000) are kept; quotes alternate “ ”.
    /// </summary>
    public static string ToFullWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool dquoteOpen = false, squoteOpen = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char prev = i > 0 ? text[i - 1] : '\0', next = i + 1 < text.Length ? text[i + 1] : '\0';
            bool betweenDigits = char.IsDigit(prev) && char.IsDigit(next);
            bool inWord = char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(next) && !IsCjk(prev) && !IsCjk(next);
            switch (c)
            {
                case ',': sb.Append(betweenDigits ? c : '，'); break;
                case '.': sb.Append(betweenDigits || inWord ? c : '。'); break;
                case ':': sb.Append(betweenDigits ? c : '：'); break;
                case ';': sb.Append('；'); break;
                case '!': sb.Append('！'); break;
                case '?': sb.Append('？'); break;
                case '(': sb.Append('（'); break;
                case ')': sb.Append('）'); break;
                case '"': sb.Append(dquoteOpen ? '”' : '“'); dquoteOpen = !dquoteOpen; break;
                case '\'' when inWord: sb.Append(c); break; // don't
                case '\'': sb.Append(squoteOpen ? '’' : '‘'); squoteOpen = !squoteOpen; break;
                default: sb.Append(c); break;
            }
        }
        // Spaces after converted full-width punctuation are redundant.
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "([，。：；！？）”’]) +", "$1");
    }

    public static string ApplyPunctuation(string text, OcrPunctuation mode) => mode switch
    {
        OcrPunctuation.ToHalfWidth => ToHalfWidth(text),
        OcrPunctuation.ToFullWidth => ToFullWidth(text),
        _ => text,
    };

    /// <summary>Join two text pieces: no separator at a CJK boundary or after a hyphenated line break, a space otherwise.</summary>
    public static string JoinPieces(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        char last = a[^1], first = b[0];
        if (IsCjk(last) || IsCjk(first)) return a + b;
        if (last == '-' && a.Length > 1 && char.IsLetter(a[^2]) && char.IsLower(first)) return a[..^1] + b;
        return a + " " + b;
    }

    /// <summary>
    /// Build the final text from recognized lines (each line's text already cleaned).
    /// <see cref="OcrTextLayout.MultiLine"/>: one line per OCR line. <see cref="OcrTextLayout.SingleLine"/>: everything joined.
    /// <see cref="OcrTextLayout.Auto"/>: lines are merged into paragraphs; a new paragraph starts after a large vertical gap,
    /// an indent change, a line that ends clearly before the right edge, or a line ending with sentence-final punctuation followed by a short line.
    /// </summary>
    public static string Layout(IReadOnlyList<(string Text, Rect Bounds)> lines, OcrTextLayout layout)
    {
        var items = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (items.Count == 0) return "";
        switch (layout)
        {
            case OcrTextLayout.MultiLine:
                return string.Join(Environment.NewLine, items.Select(l => l.Text.Trim()));
            case OcrTextLayout.SingleLine:
                return items.Skip(1).Aggregate(items[0].Text.Trim(), (acc, l) => JoinPieces(acc, l.Text.Trim()));
        }

        // Auto
        double right = items.Max(l => l.Bounds.Right);
        double left = items.Min(l => l.Bounds.Left);
        double width = Math.Max(1, right - left);
        var sb = new StringBuilder();
        string current = items[0].Text.Trim();
        for (int i = 1; i < items.Count; i++)
        {
            var prev = items[i - 1];
            var cur = items[i];
            double h = Math.Max(1, Math.Min(prev.Bounds.Height, cur.Bounds.Height));
            double gap = cur.Bounds.Top - prev.Bounds.Bottom;
            bool bigGap = gap > h * 0.9;
            bool notBelow = cur.Bounds.Top < prev.Bounds.Top + h * 0.5; // side-by-side columns / out of order
            bool prevShort = right - prev.Bounds.Right > Math.Max(width * 0.2, h * 2);
            bool indentChange = Math.Abs(cur.Bounds.Left - prev.Bounds.Left) > Math.Max(h * 1.5, width * 0.08);
            bool heightChange = Math.Abs(cur.Bounds.Height - prev.Bounds.Height) > h * 0.6;
            bool listItem = System.Text.RegularExpressions.Regex.IsMatch(cur.Text.TrimStart(), @"^([-•·*]|\d+[.、)）]|[（(]?\d+[)）])\s*");
            if (bigGap || notBelow || prevShort || indentChange || heightChange || listItem)
            {
                sb.Append(current).Append(Environment.NewLine);
                current = cur.Text.Trim();
            }
            else current = JoinPieces(current, cur.Text.Trim());
        }
        sb.Append(current);
        return sb.ToString();
    }

    /// <summary>Full pipeline used by <see cref="OcrService"/> and the result window.</summary>
    public static string Format(IReadOnlyList<OcrLine> lines, OcrTextLayout layout, OcrPunctuation punctuation) =>
        ApplyPunctuation(Layout(lines.Select(l => (l.Text, l.Bounds)).ToList(), layout), punctuation);
}
