using System.Text.RegularExpressions;

namespace OpenPin.Recognition;

/// <summary>
/// Helpers for LaTeX formula detection and verification.
/// </summary>
public static class LatexHelper
{
    private static readonly string[] MathCommands = new[]
    {
        @"\frac", @"\dfrac", @"\tfrac", @"\sqrt",
        @"\sum", @"\int", @"\iint", @"\iiint", @"\oint", @"\prod",
        @"\alpha", @"\beta", @"\gamma", @"\delta", @"\epsilon", @"\theta",
        @"\lambda", @"\mu", @"\pi", @"\sigma", @"\phi", @"\omega",
        @"\Gamma", @"\Delta", @"\Theta", @"\Lambda", @"\Sigma", @"\Omega",
        @"\pm", @"\mp", @"\times", @"\div", @"\cdot",
        @"\le", @"\leq", @"\ge", @"\geq", @"\ne", @"\neq", @"\approx", @"\equiv",
        @"\infty", @"\partial", @"\nabla", @"\forall", @"\exists",
        @"\to", @"\rightarrow", @"\leftarrow", @"\Rightarrow", @"\Leftarrow", @"\Leftrightarrow", @"\iff",
        @"\left", @"\right",
        @"\lim", @"\sin", @"\cos", @"\tan", @"\ln", @"\log",
        @"\begin{matrix}", @"\begin{pmatrix}", @"\begin{bmatrix}",
    };

    public static bool IsLatexFormula(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        // 1. Explicit math delimiters: $$...$$, \[...\], \(...\)
        if (s.StartsWith("$$", StringComparison.Ordinal) && s.EndsWith("$$", StringComparison.Ordinal) && s.Length > 4)
            return true;
        if (s.StartsWith("\\[", StringComparison.Ordinal) && s.EndsWith("\\]", StringComparison.Ordinal) && s.Length > 4)
            return true;
        if (s.StartsWith("\\(", StringComparison.Ordinal) && s.EndsWith("\\)", StringComparison.Ordinal) && s.Length > 4)
            return true;
        if (s.StartsWith('$') && s.EndsWith('$') && s.Length > 2)
            return true;

        // Ignore Windows paths like "C:\foo\bar"
        if (Regex.IsMatch(s, @"^[a-zA-Z]:\\")) return false;

        // 2. Math commands
        foreach (var cmd in MathCommands)
        {
            if (s.Contains(cmd, StringComparison.Ordinal)) return true;
        }

        // 3. Mathematical super/subscripts with braces like "x^{2} + y^{2}" or "a_{i} = b_{i}"
        if (Regex.IsMatch(s, @"[a-zA-Z0-9](\^|_)\{[^}]+\}"))
            return true;

        return false;
    }
}
