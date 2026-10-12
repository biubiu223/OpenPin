using System.Text;
using System.Text.RegularExpressions;

namespace OpenPin.Recognition;

#region AST Nodes

public abstract record LatexNode;

public sealed record LatexGroupNode(IReadOnlyList<LatexNode> Children) : LatexNode;

public sealed record LatexTextNode(string Text, bool IsItalic = true, bool IsBold = false, bool IsOperator = false) : LatexNode;

public sealed record LatexSymbolNode(string Symbol, bool IsOperator = false) : LatexNode;

public sealed record LatexFractionNode(LatexNode Numerator, LatexNode Denominator) : LatexNode;

public sealed record LatexScriptNode(LatexNode? Base, LatexNode? Subscript, LatexNode? Superscript) : LatexNode;

public sealed record LatexRootNode(LatexNode Radicand, LatexNode? Degree = null) : LatexNode;

public sealed record LatexBigOperatorNode(string Operator, string Glyph, LatexNode? Lower = null, LatexNode? Upper = null) : LatexNode;

public sealed record LatexDelimitedNode(string LeftDelim, LatexNode Content, string RightDelim) : LatexNode;

public sealed record LatexSpaceNode(double EmWidth) : LatexNode;

#endregion

/// <summary>
/// Lightweight recursive-descent LaTeX math formula parser.
/// Converts LaTeX string into an AST of <see cref="LatexNode"/>s.
/// </summary>
public sealed class LatexParser
{
    private enum TokenType
    {
        Command,
        LeftBrace,
        RightBrace,
        LeftBracket,
        RightBracket,
        LeftParen,
        RightParen,
        Caret,
        Underscore,
        Number,
        Letter,
        Operator,
        Space,
        EndOfInput,
    }

    private sealed record Token(TokenType Type, string Text, int Position);

    private readonly List<Token> _tokens = new();
    private int _pos;

    public static LatexNode Parse(string latex)
    {
        var parser = new LatexParser();
        return parser.ParseCore(latex);
    }

    private LatexNode ParseCore(string latex)
    {
        var clean = StripEnclosingDelimiters(latex);
        Tokenize(clean);
        _pos = 0;
        var nodes = ParseExpression();
        if (nodes.Count == 0) return new LatexGroupNode(Array.Empty<LatexNode>());
        if (nodes.Count == 1) return nodes[0];
        return new LatexGroupNode(nodes);
    }

    public static string StripEnclosingDelimiters(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();

        // Strip markdown ```latex ... ```
        var matchFence = Regex.Match(s, @"^```(?:latex|tex)?\s*(.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matchFence.Success) s = matchFence.Groups[1].Value.Trim();

        // Strip equation environments
        var envMatch = Regex.Match(s, @"^\\begin\{(?:equation|align|gather|displaymath)\*?\}(.*?)\\end\{(?:equation|align|gather|displaymath)\*?\}$", RegexOptions.Singleline);
        if (envMatch.Success) s = envMatch.Groups[1].Value.Trim();

        // Strip $$...$$, $...$, \[...\], \(...\)
        if (s.StartsWith("$$", StringComparison.Ordinal) && s.EndsWith("$$", StringComparison.Ordinal) && s.Length >= 4)
            s = s[2..^2].Trim();
        else if (s.StartsWith("\\[", StringComparison.Ordinal) && s.EndsWith("\\]", StringComparison.Ordinal) && s.Length >= 4)
            s = s[2..^2].Trim();
        else if (s.StartsWith("\\(", StringComparison.Ordinal) && s.EndsWith("\\)", StringComparison.Ordinal) && s.Length >= 4)
            s = s[2..^2].Trim();
        else if (s.StartsWith('$') && s.EndsWith('$') && s.Length >= 2)
            s = s[1..^1].Trim();

        return s;
    }

    private void Tokenize(string text)
    {
        _tokens.Clear();
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            char c = text[i];

            // Ignore whitespace
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // LaTeX comment %
            if (c == '%')
            {
                while (i < len && text[i] != '\n' && text[i] != '\r') i++;
                continue;
            }

            if (c == '\\')
            {
                int start = i++;
                if (i < len)
                {
                    char next = text[i];
                    if (char.IsLetter(next))
                    {
                        while (i < len && char.IsLetter(text[i])) i++;
                        string cmd = text[(start + 1)..i];
                        _tokens.Add(new Token(TokenType.Command, cmd, start));
                    }
                    else
                    {
                        // Escaped non-letter like \{, \}, \ , \,, \\, \$, \%, \_
                        i++;
                        string cmd = text[start..i];
                        if (cmd is "\\{" or "\\}")
                            _tokens.Add(new Token(cmd == "\\{" ? TokenType.LeftBrace : TokenType.RightBrace, cmd[1].ToString(), start));
                        else if (cmd is "\\," or "\\:" or "\\;" or "\\!" or "\\ ")
                            _tokens.Add(new Token(TokenType.Space, cmd, start));
                        else if (cmd == "\\\\")
                            _tokens.Add(new Token(TokenType.Space, "\n", start));
                        else
                            _tokens.Add(new Token(TokenType.Command, cmd[1].ToString(), start));
                    }
                }
                continue;
            }

            if (c == '{') { _tokens.Add(new Token(TokenType.LeftBrace, "{", i++)); continue; }
            if (c == '}') { _tokens.Add(new Token(TokenType.RightBrace, "}", i++)); continue; }
            if (c == '[') { _tokens.Add(new Token(TokenType.LeftBracket, "[", i++)); continue; }
            if (c == ']') { _tokens.Add(new Token(TokenType.RightBracket, "]", i++)); continue; }
            if (c == '(') { _tokens.Add(new Token(TokenType.LeftParen, "(", i++)); continue; }
            if (c == ')') { _tokens.Add(new Token(TokenType.RightParen, ")", i++)); continue; }
            if (c == '^') { _tokens.Add(new Token(TokenType.Caret, "^", i++)); continue; }
            if (c == '_') { _tokens.Add(new Token(TokenType.Underscore, "_", i++)); continue; }
            if (c == '~') { _tokens.Add(new Token(TokenType.Space, "~", i++)); continue; }

            if (char.IsDigit(c))
            {
                int start = i++;
                while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                _tokens.Add(new Token(TokenType.Number, text[start..i], start));
                continue;
            }

            if (char.IsLetter(c))
            {
                _tokens.Add(new Token(TokenType.Letter, c.ToString(), i++));
                continue;
            }

            if (LatexSymbols.IsOperatorChar(c))
            {
                _tokens.Add(new Token(TokenType.Operator, c.ToString(), i++));
                continue;
            }

            // Any other Unicode symbol
            _tokens.Add(new Token(TokenType.Operator, c.ToString(), i++));
        }

        _tokens.Add(new Token(TokenType.EndOfInput, "", len));
    }

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private Token Advance()
    {
        var t = Current;
        if (_pos < _tokens.Count - 1) _pos++;
        return t;
    }

    private bool Match(TokenType type, out Token token)
    {
        if (Current.Type == type)
        {
            token = Advance();
            return true;
        }
        token = null!;
        return false;
    }

    private bool MatchCommand(string command)
    {
        if (Current.Type == TokenType.Command && string.Equals(Current.Text, command, StringComparison.Ordinal))
        {
            Advance();
            return true;
        }
        return false;
    }

    private List<LatexNode> ParseExpression(Func<bool>? stopCondition = null)
    {
        var nodes = new List<LatexNode>();

        while (Current.Type != TokenType.EndOfInput)
        {
            if (stopCondition != null && stopCondition()) break;

            var atom = ParseAtomOrCommand();
            if (atom == null) break;

            // Check for attached superscripts and subscripts (^ and _)
            atom = ParseScripts(atom);

            nodes.Add(atom);
        }

        return nodes;
    }

    private LatexNode ParseScripts(LatexNode baseNode)
    {
        LatexNode? sub = null;
        LatexNode? sup = null;

        while (Current.Type is TokenType.Caret or TokenType.Underscore)
        {
            if (Current.Type == TokenType.Caret)
            {
                Advance();
                sup = ParseArgument();
            }
            else if (Current.Type == TokenType.Underscore)
            {
                Advance();
                sub = ParseArgument();
            }
        }

        if (sub == null && sup == null) return baseNode;

        if (baseNode is LatexBigOperatorNode big)
        {
            return big with
            {
                Lower = sub ?? big.Lower,
                Upper = sup ?? big.Upper
            };
        }

        if (baseNode is LatexScriptNode existingScript)
        {
            return new LatexScriptNode(
                existingScript.Base,
                sub ?? existingScript.Subscript,
                sup ?? existingScript.Superscript
            );
        }

        return new LatexScriptNode(baseNode, sub, sup);
    }

    private LatexNode? ParseAtomOrCommand()
    {
        var token = Current;

        switch (token.Type)
        {
            case TokenType.LeftBrace:
            {
                Advance(); // eat '{'
                var inner = ParseExpression(() => Current.Type == TokenType.RightBrace);
                if (Current.Type == TokenType.RightBrace) Advance(); // eat '}'
                return inner.Count == 1 ? inner[0] : new LatexGroupNode(inner);
            }

            case TokenType.RightBrace:
                return null; // Closing brace exits current group/expression

            case TokenType.Number:
                Advance();
                return new LatexTextNode(token.Text, IsItalic: false);

            case TokenType.Letter:
                Advance();
                return new LatexTextNode(token.Text, IsItalic: true);

            case TokenType.LeftParen:
            case TokenType.RightParen:
            case TokenType.LeftBracket:
            case TokenType.RightBracket:
                Advance();
                return new LatexTextNode(token.Text, IsItalic: false, IsOperator: true);

            case TokenType.Operator:
                Advance();
                return new LatexSymbolNode(token.Text, IsOperator: true);

            case TokenType.Space:
                Advance();
                double em = token.Text switch
                {
                    "\\," => 0.166,
                    "\\:" => 0.222,
                    "\\;" => 0.278,
                    "\\!" => -0.166,
                    "\\quad" => 1.0,
                    "\\qquad" => 2.0,
                    "~" => 0.333,
                    "\n" => 0.5,
                    _ => 0.25
                };
                return new LatexSpaceNode(em);

            case TokenType.Command:
                return ParseCommand();

            default:
                Advance();
                return null;
        }
    }

    private LatexNode ParseCommand()
    {
        var cmdToken = Advance();
        string cmd = cmdToken.Text;

        // Fraction: \frac{num}{den}
        if (cmd == "frac" || cmd == "dfrac" || cmd == "tfrac")
        {
            var num = ParseArgument();
            var den = ParseArgument();
            return new LatexFractionNode(num, den);
        }

        // Square root: \sqrt{arg} or \sqrt[n]{arg}
        if (cmd == "sqrt")
        {
            LatexNode? degree = null;
            if (Current.Type == TokenType.LeftBracket)
            {
                Advance(); // eat '['
                var degList = ParseExpression(() => Current.Type == TokenType.RightBracket);
                if (Current.Type == TokenType.RightBracket) Advance(); // eat ']'
                degree = degList.Count == 1 ? degList[0] : new LatexGroupNode(degList);
            }
            var radicand = ParseArgument();
            return new LatexRootNode(radicand, degree);
        }

        // Big operators: \sum, \int, \prod, etc.
        if (LatexSymbols.TryGetBigOperator(cmd, out string bigGlyph))
        {
            LatexNode? lower = null;
            LatexNode? upper = null;
            while (Current.Type is TokenType.Underscore or TokenType.Caret)
            {
                if (Current.Type == TokenType.Underscore)
                {
                    Advance();
                    lower = ParseArgument();
                }
                else if (Current.Type == TokenType.Caret)
                {
                    Advance();
                    upper = ParseArgument();
                }
            }
            return new LatexBigOperatorNode(cmd, bigGlyph, lower, upper);
        }

        // Delimiters: \left( ... \right)
        if (cmd == "left")
        {
            string leftDelim = ParseDelimiterString();
            var inner = ParseExpression(() => Current.Type == TokenType.Command && Current.Text == "right");
            string rightDelim = ")";
            if (Current.Type == TokenType.Command && Current.Text == "right")
            {
                Advance(); // eat \right
                rightDelim = ParseDelimiterString();
            }
            var content = inner.Count == 1 ? inner[0] : new LatexGroupNode(inner);
            return new LatexDelimitedNode(leftDelim, content, rightDelim);
        }

        // Font / Text styles: \text{...}, \mathrm{...}, \mathbf{...}, \mathit{...}
        if (cmd is "text" or "mathrm" or "mathbf" or "mathit" or "textbf" or "textit")
        {
            bool bold = cmd is "mathbf" or "textbf";
            bool italic = cmd is "mathit" or "textit";
            var arg = ParseArgument();
            if (arg is LatexTextNode tn) return tn with { IsBold = bold, IsItalic = italic };
            if (arg is LatexGroupNode gn)
            {
                var formatted = gn.Children.Select(c => c is LatexTextNode t ? t with { IsBold = bold, IsItalic = italic } : c).ToList();
                return new LatexGroupNode(formatted);
            }
            return arg;
        }

        // Spacing commands: \quad, \qquad
        if (cmd == "quad") return new LatexSpaceNode(1.0);
        if (cmd == "qquad") return new LatexSpaceNode(2.0);

        // Greek letters
        if (LatexSymbols.TryGetGreek(cmd, out string greek))
        {
            return new LatexSymbolNode(greek, IsOperator: false);
        }

        // Math symbols
        if (LatexSymbols.TryGetSymbol(cmd, out string sym))
        {
            return new LatexSymbolNode(sym, IsOperator: true);
        }

        // Math functions (\sin, \cos, etc.)
        if (LatexSymbols.IsFunction(cmd))
        {
            return new LatexTextNode(cmd, IsItalic: false, IsOperator: false);
        }

        // Overline / Underline
        if (cmd == "overline" || cmd == "bar")
        {
            var inner = ParseArgument();
            return inner; // simplified
        }

        // Fallback: render command name as plain text
        return new LatexTextNode(cmd, IsItalic: false);
    }

    private string ParseDelimiterString()
    {
        if (Current.Type is TokenType.LeftParen or TokenType.RightParen or TokenType.LeftBracket or TokenType.RightBracket or TokenType.LeftBrace or TokenType.RightBrace or TokenType.Operator)
        {
            return Advance().Text;
        }
        if (Current.Type == TokenType.Command)
        {
            string t = Advance().Text;
            if (t == "{") return "{";
            if (t == "}") return "}";
            if (t == "|") return "|";
            if (t == ".") return ""; // empty delimiter
            return t;
        }
        if (Current.Text == ".")
        {
            Advance();
            return "";
        }
        return Advance().Text;
    }

    private LatexNode ParseArgument()
    {
        if (Current.Type == TokenType.LeftBrace)
        {
            Advance(); // eat '{'
            var inner = ParseExpression(() => Current.Type == TokenType.RightBrace);
            if (Current.Type == TokenType.RightBrace) Advance(); // eat '}'
            return inner.Count == 1 ? inner[0] : new LatexGroupNode(inner);
        }

        // A single atom as argument (e.g. \sqrt 2 or x^2)
        var atom = ParseAtomOrCommand();
        return atom ?? new LatexTextNode("", IsItalic: false);
    }
}
