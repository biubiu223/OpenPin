namespace OpenPin.Recognition;

/// <summary>
/// Mathematical symbols, Greek letters, and big operators for the LaTeX parser and renderer.
/// </summary>
internal static class LatexSymbols
{
    private static readonly Dictionary<string, string> GreekLetters = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α",
        ["beta"] = "β",
        ["gamma"] = "γ",
        ["delta"] = "δ",
        ["epsilon"] = "ε",
        ["varepsilon"] = "ε",
        ["zeta"] = "ζ",
        ["eta"] = "η",
        ["theta"] = "θ",
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",
        ["kappa"] = "κ",
        ["lambda"] = "λ",
        ["mu"] = "μ",
        ["nu"] = "ν",
        ["xi"] = "ξ",
        ["pi"] = "π",
        ["varpi"] = "ϖ",
        ["rho"] = "ρ",
        ["varrho"] = "ϱ",
        ["sigma"] = "σ",
        ["varsigma"] = "ς",
        ["tau"] = "τ",
        ["upsilon"] = "υ",
        ["phi"] = "φ",
        ["varphi"] = "ϕ",
        ["chi"] = "χ",
        ["psi"] = "ψ",
        ["omega"] = "ω",
        ["Gamma"] = "Γ",
        ["Delta"] = "Δ",
        ["Theta"] = "Θ",
        ["Lambda"] = "Λ",
        ["Xi"] = "Ξ",
        ["Pi"] = "Π",
        ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ",
        ["Phi"] = "Φ",
        ["Psi"] = "Ψ",
        ["Omega"] = "Ω",
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["pm"] = "±",
        ["mp"] = "∓",
        ["times"] = "×",
        ["div"] = "÷",
        ["cdot"] = "·",
        ["ast"] = "∗",
        ["star"] = "⋆",
        ["circ"] = "∘",
        ["bullet"] = "•",
        ["le"] = "≤",
        ["leq"] = "≤",
        ["ge"] = "≥",
        ["geq"] = "≥",
        ["ne"] = "≠",
        ["neq"] = "≠",
        ["approx"] = "≈",
        ["equiv"] = "≡",
        ["sim"] = "∼",
        ["simeq"] = "≃",
        ["cong"] = "≅",
        ["propto"] = "∝",
        ["infty"] = "∞",
        ["partial"] = "∂",
        ["nabla"] = "∇",
        ["in"] = "∈",
        ["notin"] = "∉",
        ["ni"] = "∋",
        ["subset"] = "⊂",
        ["supset"] = "⊃",
        ["subseteq"] = "⊆",
        ["supseteq"] = "⊇",
        ["cap"] = "∩",
        ["cup"] = "∪",
        ["setminus"] = "∖",
        ["emptyset"] = "∅",
        ["varnothing"] = "∅",
        ["forall"] = "∀",
        ["exists"] = "∃",
        ["nexists"] = "∄",
        ["to"] = "→",
        ["rightarrow"] = "→",
        ["leftarrow"] = "←",
        ["gets"] = "←",
        ["leftrightarrow"] = "↔",
        ["Rightarrow"] = "⇒",
        ["Leftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔",
        ["iff"] = "⇔",
        ["implies"] = "⇒",
        ["dots"] = "…",
        ["ldots"] = "…",
        ["cdots"] = "⋯",
        ["vdots"] = "⋮",
        ["ddots"] = "⋱",
        ["hbar"] = "ℏ",
        ["angle"] = "∠",
        ["perp"] = "⊥",
        ["parallel"] = "∥",
        ["prime"] = "′",
    };

    private static readonly Dictionary<string, string> BigOperators = new(StringComparer.Ordinal)
    {
        ["sum"] = "∑",
        ["prod"] = "∏",
        ["coprod"] = "∐",
        ["int"] = "∫",
        ["iint"] = "∬",
        ["iiint"] = "∭",
        ["oint"] = "∮",
        ["bigcup"] = "⋃",
        ["bigcap"] = "⋂",
    };

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc",
        "arcsin", "arccos", "arctan",
        "sinh", "cosh", "tanh",
        "ln", "log", "exp", "lg",
        "lim", "min", "max", "sup", "inf", "det",
        "gcd", "deg", "dim", "hom", "ker", "arg"
    };

    public static bool TryGetGreek(string command, out string glyph) => GreekLetters.TryGetValue(command, out glyph!);

    public static bool TryGetSymbol(string command, out string glyph) => Symbols.TryGetValue(command, out glyph!);

    public static bool TryGetBigOperator(string command, out string glyph) => BigOperators.TryGetValue(command, out glyph!);

    public static bool IsBigOperator(string command) => BigOperators.ContainsKey(command);

    public static bool IsFunction(string command) => Functions.Contains(command);

    public static bool IsOperatorChar(char c) => c is '+' or '-' or '*' or '/' or '=' or '<' or '>' or '!' or ',' or ':' or ';' or '|';
}
