using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenPin.Pin.Logic;

/// <summary>A run of formatted text produced by <see cref="HtmlLite"/>. <see cref="IsLineBreak"/> runs have no text.</summary>
public sealed record HtmlRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
    double SizeScale = 1, string? Color = null, string? Background = null, bool Monospace = false, string? Href = null,
    bool IsLineBreak = false);

/// <summary>
/// Tiny HTML → formatted runs converter for "simple rich text" text pins (PIN-07, P1). It understands the clipboard
/// CF_HTML fragment markers, basic inline formatting (b/strong, i/em, u, s/del, code, a, font/span colors, h1-h6) and
/// block structure (p, div, br, li, tr, headings…). Everything else is reduced to its text. Never throws.
/// </summary>
public static partial class HtmlLite
{
    [GeneratedRegex(@"<!--StartFragment-->(.*)<!--EndFragment-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FragmentRegex();

    [GeneratedRegex(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?:^|;)\s*color\s*:\s*([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleColorRegex();

    [GeneratedRegex(@"(?:^|;)\s*background(?:-color)?\s*:\s*([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBackgroundRegex();

    [GeneratedRegex(@"\b(style|color|href)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex AttrRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "tr", "table", "blockquote", "pre",
        "section", "article", "header", "footer", "dl", "dt", "dd", "hr", "figure", "figcaption", "address",
    };

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase) { "style", "script", "head", "title", "noscript" };

    /// <summary>Extracts the fragment of a CF_HTML clipboard payload (or returns the input if it has no markers).</summary>
    public static string ExtractFragment(string cfHtml)
    {
        var m = FragmentRegex().Match(cfHtml);
        if (m.Success) return m.Groups[1].Value;
        int i = cfHtml.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? cfHtml[i..] : cfHtml;
    }

    private sealed class Style
    {
        public string Tag = "";
        public bool Bold, Italic, Underline, Strike, Mono, Pre;
        public double Size = 1;
        public string? Color, Background, Href;
        public Style Child(string tag) => new()
        {
            Tag = tag, Bold = Bold, Italic = Italic, Underline = Underline, Strike = Strike, Mono = Mono, Pre = Pre,
            Size = Size, Color = Color, Background = Background, Href = Href,
        };
    }

    public static IReadOnlyList<HtmlRun> Parse(string? html)
    {
        var runs = new List<HtmlRun>();
        if (string.IsNullOrEmpty(html)) return runs;
        try
        {
            ParseCore(ExtractFragment(html), runs);
        }
        catch (Exception)
        {
            runs.Clear();
            runs.Add(new HtmlRun(WebUtility.HtmlDecode(TagRegex().Replace(html, " "))));
        }
        return Normalize(runs);
    }

    private static void ParseCore(string html, List<HtmlRun> runs)
    {
        var stack = new Stack<Style>();
        stack.Push(new Style());
        int skipDepth = 0;
        int pos = 0;
        var listCounters = new Stack<int>(); // -1 = bullet list, n = next ordered number

        foreach (Match m in TagRegex().Matches(html))
        {
            if (m.Index > pos && skipDepth == 0) AddText(runs, html[pos..m.Index], stack.Peek());
            pos = m.Index + m.Length;
            if (!m.Groups[2].Success) continue; // comment

            bool closing = m.Groups[1].Value == "/";
            string tag = m.Groups[2].Value.ToLowerInvariant();
            string attrs = m.Groups[3].Value;
            bool selfClosing = attrs.TrimEnd().EndsWith('/');

            if (SkipTags.Contains(tag))
            {
                if (closing) skipDepth = Math.Max(0, skipDepth - 1);
                else if (!selfClosing) skipDepth++;
                continue;
            }
            if (skipDepth > 0) continue;

            if (tag == "br") { runs.Add(LineBreak); continue; }
            if (tag is "img" or "meta" or "link" or "input" or "col" or "wbr") continue;
            if (tag == "hr") { BlockBreak(runs); runs.Add(new HtmlRun("────────")); BlockBreak(runs); continue; }

            if (closing)
            {
                // pop to the matching tag (tolerate bad nesting)
                if (stack.Any(s => s.Tag == tag))
                    while (stack.Count > 1 && stack.Pop().Tag != tag) { }
                if (tag is "ul" or "ol" && listCounters.Count > 0) listCounters.Pop();
                if (BlockTags.Contains(tag)) BlockBreak(runs);
                else if (tag is "td" or "th") runs.Add(new HtmlRun("\t"));
                continue;
            }

            if (BlockTags.Contains(tag)) BlockBreak(runs);
            var st = stack.Peek().Child(tag);
            switch (tag)
            {
                case "b": case "strong": st.Bold = true; break;
                case "i": case "em": case "cite": case "var": st.Italic = true; break;
                case "u": case "ins": st.Underline = true; break;
                case "s": case "strike": case "del": st.Strike = true; break;
                case "code": case "kbd": case "samp": case "tt": st.Mono = true; break;
                case "pre": st.Mono = true; st.Pre = true; break;
                case "h1": st.Bold = true; st.Size = 1.8; break;
                case "h2": st.Bold = true; st.Size = 1.5; break;
                case "h3": st.Bold = true; st.Size = 1.25; break;
                case "h4": case "h5": case "h6": st.Bold = true; break;
                case "th": st.Bold = true; break;
                case "ul": listCounters.Push(-1); break;
                case "ol": listCounters.Push(1); break;
                case "li":
                    if (listCounters.Count > 0 && listCounters.Peek() > 0)
                    {
                        int n = listCounters.Pop();
                        listCounters.Push(n + 1);
                        runs.Add(new HtmlRun($"{n}. "));
                    }
                    else runs.Add(new HtmlRun("• "));
                    break;
            }
            ApplyAttributes(st, tag, attrs);
            if (!selfClosing) stack.Push(st);
        }
        if (pos < html.Length && skipDepth == 0) AddText(runs, html[pos..], stack.Peek());
    }

    private static void ApplyAttributes(Style st, string tag, string attrs)
    {
        foreach (Match a in AttrRegex().Matches(attrs))
        {
            string name = a.Groups[1].Value.ToLowerInvariant();
            string value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
            switch (name)
            {
                case "href" when tag == "a":
                    st.Href = value; st.Underline = true;
                    break;
                case "color" when tag == "font":
                    st.Color = value.Trim();
                    break;
                case "style":
                    var c = StyleColorRegex().Match(value);
                    if (c.Success) st.Color = c.Groups[1].Value.Trim();
                    var bg = StyleBackgroundRegex().Match(value);
                    if (bg.Success && !bg.Groups[1].Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
                        st.Background = bg.Groups[1].Value.Trim();
                    var v = value.ToLowerInvariant().Replace(" ", "");
                    if (v.Contains("font-weight:bold") || v.Contains("font-weight:700") || v.Contains("font-weight:600") || v.Contains("font-weight:800") || v.Contains("font-weight:900")) st.Bold = true;
                    if (v.Contains("font-style:italic")) st.Italic = true;
                    if (v.Contains("text-decoration:underline")) st.Underline = true;
                    if (v.Contains("line-through")) st.Strike = true;
                    break;
            }
        }
    }

    private static readonly HtmlRun LineBreak = new("", IsLineBreak: true);

    private static void BlockBreak(List<HtmlRun> runs)
    {
        if (runs.Count > 0 && !runs[^1].IsLineBreak) runs.Add(LineBreak);
    }

    private static void AddText(List<HtmlRun> runs, string raw, Style st)
    {
        string text = WebUtility.HtmlDecode(raw);
        if (st.Pre)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) runs.Add(LineBreak);
                if (lines[i].Length > 0) runs.Add(Make(lines[i], st));
            }
            return;
        }
        text = WhitespaceRegex().Replace(text, " ");
        if (text.Length == 0) return;
        // drop leading whitespace at the start of a line
        if ((runs.Count == 0 || runs[^1].IsLineBreak || runs[^1].Text.EndsWith(' ')) && text[0] == ' ') text = text[1..];
        if (text.Length == 0) return;
        text = text.Replace(' ', ' ');
        runs.Add(Make(text, st));
    }

    private static HtmlRun Make(string text, Style st) =>
        new(text, st.Bold, st.Italic, st.Underline, st.Strike, st.Size, st.Color, st.Background, st.Mono, st.Href);

    /// <summary>Trims trailing spaces before line breaks, collapses repeated breaks (max one empty line) and trims the ends.</summary>
    private static List<HtmlRun> Normalize(List<HtmlRun> runs)
    {
        var result = new List<HtmlRun>();
        int breaks = 0;
        foreach (var r in runs)
        {
            if (r.IsLineBreak)
            {
                if (result.Count > 0 && !result[^1].IsLineBreak && result[^1].Text.EndsWith(' '))
                    result[^1] = result[^1] with { Text = result[^1].Text.TrimEnd(' ') };
                breaks++;
                if (breaks <= 2 && result.Count > 0) result.Add(r);
                continue;
            }
            if (r.Text.Length == 0) continue;
            breaks = 0;
            result.Add(r);
        }
        while (result.Count > 0 && result[^1].IsLineBreak) result.RemoveAt(result.Count - 1);
        if (result.Count > 0 && !result[^1].IsLineBreak) result[^1] = result[^1] with { Text = result[^1].Text.TrimEnd() };
        result.RemoveAll(r => !r.IsLineBreak && r.Text.Length == 0);
        return result;
    }

    /// <summary>Plain text of runs (for tests / fallback).</summary>
    public static string ToPlainText(IEnumerable<HtmlRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var r in runs) sb.Append(r.IsLineBreak ? "\n" : r.Text);
        return sb.ToString();
    }
}
