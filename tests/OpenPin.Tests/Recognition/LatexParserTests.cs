using OpenPin.Recognition;

namespace OpenPin.Tests.Recognition;

public class LatexParserTests
{
    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyGroup()
    {
        var node1 = LatexParser.Parse("");
        var node2 = LatexParser.Parse("   ");
        Assert.NotNull(node1);
        Assert.NotNull(node2);
        if (node1 is LatexGroupNode g1) Assert.Empty(g1.Children);
        if (node2 is LatexGroupNode g2) Assert.Empty(g2.Children);
    }

    [Fact]
    public void StripEnclosingDelimiters_HandlesVariousDelimiters()
    {
        Assert.Equal("x+y", LatexParser.StripEnclosingDelimiters("$x+y$"));
        Assert.Equal("x+y", LatexParser.StripEnclosingDelimiters("$$x+y$$"));
        Assert.Equal("x+y", LatexParser.StripEnclosingDelimiters("\\[x+y\\]"));
        Assert.Equal("x+y", LatexParser.StripEnclosingDelimiters("\\(x+y\\)"));
        Assert.Equal("x+y", LatexParser.StripEnclosingDelimiters("```latex\nx+y\n```"));
        Assert.Equal("x+y", LatexParser.StripEnclosingDelimiters("\\begin{equation}x+y\\end{equation}"));
    }

    [Fact]
    public void Parse_Fractions_CreatesLatexFractionNode()
    {
        var node = LatexParser.Parse(@"\frac{a}{b}");
        var frac = Assert.IsType<LatexFractionNode>(node);
        var num = Assert.IsType<LatexTextNode>(frac.Numerator);
        var den = Assert.IsType<LatexTextNode>(frac.Denominator);
        Assert.Equal("a", num.Text);
        Assert.Equal("b", den.Text);
    }

    [Fact]
    public void Parse_NestedFractions_WorksCorrectly()
    {
        var node = LatexParser.Parse(@"\frac{\frac{1}{2}}{3}");
        var outerFrac = Assert.IsType<LatexFractionNode>(node);
        var innerFrac = Assert.IsType<LatexFractionNode>(outerFrac.Numerator);
        Assert.Equal("1", Assert.IsType<LatexTextNode>(innerFrac.Numerator).Text);
        Assert.Equal("2", Assert.IsType<LatexTextNode>(innerFrac.Denominator).Text);
        Assert.Equal("3", Assert.IsType<LatexTextNode>(outerFrac.Denominator).Text);
    }

    [Fact]
    public void Parse_SuperscriptAndSubscript_CreatesLatexScriptNode()
    {
        var node = LatexParser.Parse("x^2");
        var script = Assert.IsType<LatexScriptNode>(node);
        Assert.Equal("x", Assert.IsType<LatexTextNode>(script.Base).Text);
        Assert.Equal("2", Assert.IsType<LatexTextNode>(script.Superscript).Text);
        Assert.Null(script.Subscript);

        var node2 = LatexParser.Parse("x_i");
        var script2 = Assert.IsType<LatexScriptNode>(node2);
        Assert.Equal("x", Assert.IsType<LatexTextNode>(script2.Base).Text);
        Assert.Equal("i", Assert.IsType<LatexTextNode>(script2.Subscript).Text);
        Assert.Null(script2.Superscript);

        var node3 = LatexParser.Parse("x_i^2");
        var script3 = Assert.IsType<LatexScriptNode>(node3);
        Assert.Equal("x", Assert.IsType<LatexTextNode>(script3.Base).Text);
        Assert.Equal("i", Assert.IsType<LatexTextNode>(script3.Subscript).Text);
        Assert.Equal("2", Assert.IsType<LatexTextNode>(script3.Superscript).Text);
    }

    [Fact]
    public void Parse_SquareRoots_WithAndWithoutDegree()
    {
        var node1 = LatexParser.Parse(@"\sqrt{x}");
        var root1 = Assert.IsType<LatexRootNode>(node1);
        Assert.Equal("x", Assert.IsType<LatexTextNode>(root1.Radicand).Text);
        Assert.Null(root1.Degree);

        var node2 = LatexParser.Parse(@"\sqrt[3]{x+y}");
        var root2 = Assert.IsType<LatexRootNode>(node2);
        Assert.NotNull(root2.Degree);
        Assert.Equal("3", Assert.IsType<LatexTextNode>(root2.Degree).Text);
        Assert.IsType<LatexGroupNode>(root2.Radicand);
    }

    [Fact]
    public void Parse_BigOperators_WithLimits()
    {
        var node = LatexParser.Parse(@"\sum_{i=1}^n i");
        var group = Assert.IsType<LatexGroupNode>(node);
        var big = Assert.IsType<LatexBigOperatorNode>(group.Children[0]);
        Assert.Equal("sum", big.Operator);
        Assert.Equal("∑", big.Glyph);
        Assert.NotNull(big.Lower);
        Assert.NotNull(big.Upper);
        Assert.Equal("i", Assert.IsType<LatexTextNode>(group.Children[1]).Text);

        var intNode = LatexParser.Parse(@"\int_0^\infty");
        var bigInt = Assert.IsType<LatexBigOperatorNode>(intNode);
        Assert.Equal("int", bigInt.Operator);
        Assert.Equal("∫", bigInt.Glyph);
        Assert.NotNull(bigInt.Lower);
        Assert.NotNull(bigInt.Upper);
    }

    [Fact]
    public void Parse_GreekLetters_MapsToGlyphs()
    {
        var node = LatexParser.Parse(@"\alpha + \beta = \gamma");
        var group = Assert.IsType<LatexGroupNode>(node);
        Assert.Equal("α", Assert.IsType<LatexSymbolNode>(group.Children[0]).Symbol);
        Assert.Equal("+", Assert.IsType<LatexSymbolNode>(group.Children[1]).Symbol);
        Assert.Equal("β", Assert.IsType<LatexSymbolNode>(group.Children[2]).Symbol);
        Assert.Equal("=", Assert.IsType<LatexSymbolNode>(group.Children[3]).Symbol);
        Assert.Equal("γ", Assert.IsType<LatexSymbolNode>(group.Children[4]).Symbol);

        var omegaNode = LatexParser.Parse(@"\Omega");
        Assert.Equal("Ω", Assert.IsType<LatexSymbolNode>(omegaNode).Symbol);
    }

    [Fact]
    public void Parse_MathSymbolsAndOperators_Recognized()
    {
        var node = LatexParser.Parse(@"a \pm b \le c \times d");
        var group = Assert.IsType<LatexGroupNode>(node);
        Assert.Equal("±", Assert.IsType<LatexSymbolNode>(group.Children[1]).Symbol);
        Assert.Equal("≤", Assert.IsType<LatexSymbolNode>(group.Children[3]).Symbol);
        Assert.Equal("×", Assert.IsType<LatexSymbolNode>(group.Children[5]).Symbol);
    }

    [Fact]
    public void Parse_Functions_RenderUpright()
    {
        var node = LatexParser.Parse(@"\sin x + \ln y");
        var group = Assert.IsType<LatexGroupNode>(node);
        var sin = Assert.IsType<LatexTextNode>(group.Children[0]);
        Assert.Equal("sin", sin.Text);
        Assert.False(sin.IsItalic);
    }

    [Fact]
    public void Parse_Delimiters_CreatesDelimitedNode()
    {
        var node = LatexParser.Parse(@"\left( \frac{a}{b} \right)");
        var delim = Assert.IsType<LatexDelimitedNode>(node);
        Assert.Equal("(", delim.LeftDelim);
        Assert.Equal(")", delim.RightDelim);
        Assert.IsType<LatexFractionNode>(delim.Content);
    }

    [Fact]
    public void Parse_MalformedFormulas_DoesNotThrow()
    {
        // Unclosed brace
        var n1 = LatexParser.Parse(@"\frac{1}{2");
        Assert.NotNull(n1);

        // Trailing command or caret
        var n2 = LatexParser.Parse(@"x^");
        Assert.NotNull(n2);

        // Standalone backslash
        var n3 = LatexParser.Parse(@"\");
        Assert.NotNull(n3);

        // Extra braces
        var n4 = LatexParser.Parse(@"{x}}}");
        Assert.NotNull(n4);
    }
}
