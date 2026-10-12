using System.Text;
using System.Text.RegularExpressions;

namespace OpenPin.Recognition;

/// <summary>
/// Post-processes raw OCR text into clean standard LaTeX math syntax.
/// Converts Unicode symbols, exponents, fractions, square roots, and functions.
/// </summary>
public static class OcrToLatexConverter
{
    private static readonly Dictionary<char, string> GreekMap = new()
    {
        ['α'] = @"\alpha ",
        ['β'] = @"\beta ",
        ['γ'] = @"\gamma ",
        ['δ'] = @"\delta ",
        ['ε'] = @"\epsilon ",
        ['ζ'] = @"\zeta ",
        ['η'] = @"\eta ",
        ['θ'] = @"\theta ",
        ['ι'] = @"\iota ",
        ['κ'] = @"\kappa ",
        ['λ'] = @"\lambda ",
        ['μ'] = @"\mu ",
        ['ν'] = @"\nu ",
        ['ξ'] = @"\xi ",
        ['π'] = @"\pi ",
        ['ρ'] = @"\rho ",
        ['σ'] = @"\sigma ",
        ['τ'] = @"\tau ",
        ['υ'] = @"\upsilon ",
        ['φ'] = @"\phi ",
        ['ϕ'] = @"\phi ",
        ['χ'] = @"\chi ",
        ['ψ'] = @"\psi ",
        ['ω'] = @"\omega ",
        ['Γ'] = @"\Gamma ",
        ['Δ'] = @"\Delta ",
        ['Θ'] = @"\Theta ",
        ['Λ'] = @"\Lambda ",
        ['Ξ'] = @"\Xi ",
        ['Π'] = @"\Pi ",
        ['Σ'] = @"\Sigma ",
        ['Υ'] = @"\Upsilon ",
        ['Φ'] = @"\Phi ",
        ['Ψ'] = @"\Psi ",
        ['Ω'] = @"\Omega ",
    };

    private static readonly Dictionary<char, string> MathSymbolMap = new()
    {
        ['±'] = @"\pm ",
        ['∓'] = @"\mp ",
        ['×'] = @"\times ",
        ['÷'] = @"\div ",
        ['·'] = @"\cdot ",
        ['•'] = @"\bullet ",
        ['≤'] = @"\le ",
        ['≥'] = @"\ge ",
        ['≠'] = @"\neq ",
        ['≈'] = @"\approx ",
        ['≡'] = @"\equiv ",
        ['∝'] = @"\propto ",
        ['∞'] = @"\infty ",
        ['∂'] = @"\partial ",
        ['∇'] = @"\nabla ",
        ['∈'] = @"\in ",
        ['∉'] = @"\notin ",
        ['⊂'] = @"\subset ",
        ['⊃'] = @"\supset ",
        ['⊆'] = @"\subseteq ",
        ['⊇'] = @"\supseteq ",
        ['∩'] = @"\cap ",
        ['∪'] = @"\cup ",
        ['→'] = @"\to ",
        ['←'] = @"\leftarrow ",
        ['⇒'] = @"\Rightarrow ",
        ['⇔'] = @"\Leftrightarrow ",
        ['∑'] = @"\sum ",
        ['∏'] = @"\prod ",
        ['∫'] = @"\int ",
        ['∬'] = @"\iint ",
        ['∭'] = @"\iiint ",
        ['∮'] = @"\oint ",
        ['∀'] = @"\forall ",
        ['∃'] = @"\exists ",
        ['ℏ'] = @"\hbar ",
    };

    private static readonly Dictionary<char, char> SuperscriptDigits = new()
    {
        ['⁰'] = '0', ['¹'] = '1', ['²'] = '2', ['³'] = '3', ['⁴'] = '4',
        ['⁵'] = '5', ['⁶'] = '6', ['⁷'] = '7', ['⁸'] = '8', ['⁹'] = '9',
        ['⁺'] = '+', ['⁻'] = '-', ['ⁿ'] = 'n',
    };

    private static readonly Dictionary<char, char> SubscriptDigits = new()
    {
        ['₀'] = '0', ['₁'] = '1', ['₂'] = '2', ['₃'] = '3', ['₄'] = '4',
        ['₅'] = '5', ['₆'] = '6', ['₇'] = '7', ['₈'] = '8', ['₉'] = '9',
        ['ᵢ'] = 'i', ['ⱼ'] = 'j',
    };

    public static string Convert(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return "";

        var sb = new StringBuilder();
        string s = ocrText.Trim();

        // 1. Replace Greek letters and math symbols
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (GreekMap.TryGetValue(c, out var greek))
            {
                sb.Append(greek);
            }
            else if (MathSymbolMap.TryGetValue(c, out var sym))
            {
                sb.Append(sym);
            }
            else if (SuperscriptDigits.TryGetValue(c, out var supDigit))
            {
                // Group consecutive superscripts
                var supGroup = new StringBuilder();
                supGroup.Append(supDigit);
                while (i + 1 < s.Length && SuperscriptDigits.TryGetValue(s[i + 1], out var nextSup))
                {
                    supGroup.Append(nextSup);
                    i++;
                }
                sb.Append("^{").Append(supGroup).Append('}');
            }
            else if (SubscriptDigits.TryGetValue(c, out var subDigit))
            {
                // Group consecutive subscripts
                var subGroup = new StringBuilder();
                subGroup.Append(subDigit);
                while (i + 1 < s.Length && SubscriptDigits.TryGetValue(s[i + 1], out var nextSub))
                {
                    subGroup.Append(nextSub);
                    i++;
                }
                sb.Append("_{").Append(subGroup).Append('}');
            }
            else
            {
                sb.Append(c);
            }
        }

        string res = sb.ToString();

        // 2. Square roots: √(...) or √(x) or \sqrt(...) or sqrt(...)
        res = Regex.Replace(res, @"(?:\\?sqrt|√)\s*\(([^)]+)\)", @"\sqrt{$1}");
        res = Regex.Replace(res, @"(?:\\?sqrt|√)\s*([a-zA-Z0-9]+)", @"\sqrt{$1}");

        // 3. Fractions: (a)/(b) -> \frac{a}{b}
        res = Regex.Replace(res, @"\(([^)]+)\)\s*/\s*\(([^)]+)\)", @"\frac{$1}{$2}");

        // 4. Standard math functions: sin, cos, tan, ln, log, exp, lim
        res = Regex.Replace(res, @"\b(?<!\\)(sin|cos|tan|cot|sec|csc|sinh|cosh|tanh|ln|log|exp|lim)\b", @"\$1 ");

        // 5. Clean extra spaces
        res = Regex.Replace(res, @"\s+", " ").Trim();

        return res;
    }
}
