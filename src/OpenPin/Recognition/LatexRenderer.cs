using System.Globalization;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenPin.Recognition;

/// <summary>
/// Rendering options for LaTeX math formulas.
/// </summary>
public sealed class LatexRenderOptions
{
    public double FontSize { get; set; } = 24.0;
    public Brush Foreground { get; set; } = Brushes.Black;
    public Brush? Background { get; set; } = null;
    public FontFamily FontFamily { get; set; } = new FontFamily("Cambria Math, Times New Roman, Segoe UI");
    public Thickness Padding { get; set; } = new Thickness(12, 8, 12, 8);
    public double DpiScale { get; set; } = 1.0;
    public double Scale { get; set; } = 1.0;

    public LatexRenderOptions Clone() => (LatexRenderOptions)MemberwiseClone();
}

/// <summary>
/// Pure C# / WPF mathematical formula renderer.
/// Computes a TeX box layout model and renders to <see cref="DrawingGroup"/>,
/// <see cref="BitmapSource"/> (RenderTargetBitmap), or vector SVG string.
/// </summary>
public static class LatexRenderer
{
    public static DrawingGroup RenderToDrawing(string latex, LatexRenderOptions? options = null)
    {
        var ast = LatexParser.Parse(latex);
        return RenderToDrawing(ast, options);
    }

    public static DrawingGroup RenderToDrawing(LatexNode root, LatexRenderOptions? options = null)
    {
        var opt = (options ?? new LatexRenderOptions()).Clone();
        return RunInSta(() => RenderToDrawingCore(root, opt));
    }

    public static BitmapSource RenderToBitmap(string latex, LatexRenderOptions? options = null)
    {
        var ast = LatexParser.Parse(latex);
        return RenderToBitmap(ast, options);
    }

    public static BitmapSource RenderToBitmap(LatexNode root, LatexRenderOptions? options = null)
    {
        var opt = (options ?? new LatexRenderOptions()).Clone();
        return RunInSta(() => RenderToBitmapCore(root, opt));
    }

    public static string RenderToSvg(string latex, LatexRenderOptions? options = null)
    {
        var ast = LatexParser.Parse(latex);
        return RenderToSvg(ast, options);
    }

    public static string RenderToSvg(LatexNode root, LatexRenderOptions? options = null)
    {
        var opt = (options ?? new LatexRenderOptions()).Clone();
        return RunInSta(() => RenderToSvgCore(root, opt));
    }

    private static T RunInSta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return action();

        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("LaTeX rendering failed on STA thread", error);
        return result;
    }

    private static DrawingGroup RenderToDrawingCore(LatexNode root, LatexRenderOptions options)
    {
        var box = BuildBox(root, options.FontSize, options.FontFamily);
        double w = Math.Ceiling(box.Width + options.Padding.Left + options.Padding.Right);
        double h = Math.Ceiling(box.Height + options.Padding.Top + options.Padding.Bottom);
        var origin = new Point(options.Padding.Left, options.Padding.Top);

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            if (options.Background != null)
            {
                dc.DrawRectangle(options.Background, null, new Rect(0, 0, w, h));
            }
            box.Render(dc, origin, options.Foreground);
        }
        group.Freeze();
        return group;
    }

    private static BitmapSource RenderToBitmapCore(LatexNode root, LatexRenderOptions options)
    {
        var box = BuildBox(root, options.FontSize, options.FontFamily);
        double effScale = options.Scale * options.DpiScale;
        int pixelW = Math.Max(1, (int)Math.Ceiling((box.Width + options.Padding.Left + options.Padding.Right) * effScale));
        int pixelH = Math.Max(1, (int)Math.Ceiling((box.Height + options.Padding.Top + options.Padding.Bottom) * effScale));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (Math.Abs(effScale - 1.0) > 1e-4)
                dc.PushTransform(new ScaleTransform(effScale, effScale));

            double w = box.Width + options.Padding.Left + options.Padding.Right;
            double h = box.Height + options.Padding.Top + options.Padding.Bottom;
            if (options.Background != null)
            {
                dc.DrawRectangle(options.Background, null, new Rect(0, 0, w, h));
            }
            var origin = new Point(options.Padding.Left, options.Padding.Top);
            box.Render(dc, origin, options.Foreground);

            if (Math.Abs(effScale - 1.0) > 1e-4)
                dc.Pop();
        }

        var rtb = new RenderTargetBitmap(pixelW, pixelH, 96 * options.DpiScale, 96 * options.DpiScale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static string RenderToSvgCore(LatexNode root, LatexRenderOptions options)
    {
        var box = BuildBox(root, options.FontSize, options.FontFamily);
        double w = Math.Ceiling(box.Width + options.Padding.Left + options.Padding.Right);
        double h = Math.Ceiling(box.Height + options.Padding.Top + options.Padding.Bottom);
        var origin = new Point(options.Padding.Left, options.Padding.Top);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w:0.##}\" height=\"{h:0.##}\" viewBox=\"0 0 {w:0.##} {h:0.##}\">");
        if (options.Background is SolidColorBrush scb)
        {
            sb.AppendLine($"  <rect width=\"100%\" height=\"100%\" fill=\"#{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}\" />");
        }
        string fgHex = options.Foreground is SolidColorBrush sfg
            ? $"#{sfg.Color.R:X2}{sfg.Color.G:X2}{sfg.Color.B:X2}"
            : "#000000";

        box.RenderSvg(sb, origin, fgHex);
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static LatexBox BuildBox(LatexNode node, double fontSize, FontFamily family)
    {
        switch (node)
        {
            case LatexGroupNode gn:
            {
                var children = gn.Children.Select(c => BuildBox(c, fontSize, family)).ToList();
                return new HorizontalBox(children);
            }

            case LatexTextNode tn:
                return new TextBox(tn.Text, fontSize, family, tn.IsItalic ? FontStyles.Italic : FontStyles.Normal, tn.IsBold ? FontWeights.Bold : FontWeights.Normal, tn.IsOperator);

            case LatexSymbolNode sn:
                return new TextBox(sn.Symbol, fontSize, family, FontStyles.Normal, FontWeights.Normal, sn.IsOperator);

            case LatexFractionNode fn:
            {
                var num = BuildBox(fn.Numerator, fontSize * 0.85, family);
                var den = BuildBox(fn.Denominator, fontSize * 0.85, family);
                return new FractionBox(num, den, fontSize);
            }

            case LatexScriptNode sc:
            {
                var b = sc.Base != null ? BuildBox(sc.Base, fontSize, family) : null;
                var sub = sc.Subscript != null ? BuildBox(sc.Subscript, fontSize * 0.72, family) : null;
                var sup = sc.Superscript != null ? BuildBox(sc.Superscript, fontSize * 0.72, family) : null;
                return new ScriptBox(b, sub, sup, fontSize);
            }

            case LatexRootNode rn:
            {
                var rad = BuildBox(rn.Radicand, fontSize, family);
                var deg = rn.Degree != null ? BuildBox(rn.Degree, fontSize * 0.6, family) : null;
                return new RootBox(rad, deg, fontSize);
            }

            case LatexBigOperatorNode bn:
            {
                var low = bn.Lower != null ? BuildBox(bn.Lower, fontSize * 0.72, family) : null;
                var up = bn.Upper != null ? BuildBox(bn.Upper, fontSize * 0.72, family) : null;
                return new BigOperatorBox(bn.Operator, bn.Glyph, low, up, fontSize, family);
            }

            case LatexDelimitedNode dn:
            {
                var content = BuildBox(dn.Content, fontSize, family);
                return new DelimitedBox(dn.LeftDelim, content, dn.RightDelim, fontSize, family);
            }

            case LatexSpaceNode sn:
                return new SpaceBox(sn.EmWidth * fontSize);

            default:
                return new SpaceBox(0);
        }
    }

    #region Layout Box Classes

    internal abstract class LatexBox
    {
        public double Width { get; set; }
        public double Ascent { get; set; }
        public double Descent { get; set; }
        public double Height => Ascent + Descent;
        public Point Offset { get; set; }

        public abstract void Render(DrawingContext dc, Point origin, Brush foreground);
        public abstract void RenderSvg(StringBuilder svg, Point origin, string fgHex);
    }

    internal sealed class SpaceBox : LatexBox
    {
        public SpaceBox(double width)
        {
            Width = Math.Max(0, width);
            Ascent = 0;
            Descent = 0;
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground) { }
        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex) { }
    }

    internal sealed class TextBox : LatexBox
    {
        public string Text { get; }
        public double FontSize { get; }
        public FontFamily Family { get; }
        public FontStyle Style { get; }
        public FontWeight Weight { get; }
        public bool IsOperator { get; }
        public double MarginLeft { get; }
        public double MarginRight { get; }

        public TextBox(string text, double fontSize, FontFamily family, FontStyle style, FontWeight weight, bool isOperator)
        {
            Text = text;
            FontSize = fontSize;
            Family = family;
            Style = style;
            Weight = weight;
            IsOperator = isOperator;

            if (isOperator && text is "+" or "-" or "=" or "<" or ">" or "≤" or "≥" or "≠" or "≈" or "→" or "⇒")
            {
                MarginLeft = fontSize * 0.18;
                MarginRight = fontSize * 0.18;
            }

            var ft = CreateFormattedText(Brushes.Black);
            Width = ft.WidthIncludingTrailingWhitespace + MarginLeft + MarginRight;
            Ascent = ft.Baseline > 0 ? ft.Baseline : fontSize * 0.8;
            Descent = ft.Height > ft.Baseline ? ft.Height - ft.Baseline : fontSize * 0.2;
        }

        private FormattedText CreateFormattedText(Brush brush)
        {
            var face = new Typeface(Family, Style, Weight, FontStretches.Normal);
            return new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, brush, 1.0);
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            if (string.IsNullOrEmpty(Text)) return;
            var ft = CreateFormattedText(foreground);
            dc.DrawText(ft, new Point(origin.X + Offset.X + MarginLeft, origin.Y + Offset.Y));
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            if (string.IsNullOrEmpty(Text)) return;
            double x = origin.X + Offset.X + MarginLeft;
            double y = origin.Y + Offset.Y + Ascent;
            string escaped = SecurityElement.Escape(Text) ?? "";
            string fontStyle = Style == FontStyles.Italic ? "italic" : "normal";
            string fontWeight = Weight == FontWeights.Bold ? "bold" : "normal";
            svg.AppendLine($"  <text x=\"{x:0.##}\" y=\"{y:0.##}\" font-size=\"{FontSize:0.##}\" font-family=\"{Family.Source}\" font-style=\"{fontStyle}\" font-weight=\"{fontWeight}\" fill=\"{fgHex}\">{escaped}</text>");
        }
    }

    internal sealed class HorizontalBox : LatexBox
    {
        public List<LatexBox> Children { get; }

        public HorizontalBox(List<LatexBox> children)
        {
            Children = children;
            if (children.Count == 0)
            {
                Width = 0; Ascent = 0; Descent = 0;
                return;
            }

            double maxAscent = children.Max(c => c.Ascent);
            double maxDescent = children.Max(c => c.Descent);
            Ascent = maxAscent;
            Descent = maxDescent;

            double curX = 0;
            foreach (var child in children)
            {
                child.Offset = new Point(curX, maxAscent - child.Ascent);
                curX += child.Width;
            }
            Width = curX;
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            foreach (var child in Children)
            {
                child.Render(dc, pos, foreground);
            }
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            foreach (var child in Children)
            {
                child.RenderSvg(svg, pos, fgHex);
            }
        }
    }

    internal sealed class FractionBox : LatexBox
    {
        public LatexBox Numerator { get; }
        public LatexBox Denominator { get; }
        public double BarThickness { get; }
        public double BarY { get; }
        public double BarWidth { get; }

        public FractionBox(LatexBox numerator, LatexBox denominator, double fontSize)
        {
            Numerator = numerator;
            Denominator = denominator;
            BarThickness = Math.Max(1.5, fontSize * 0.06);
            double gap = fontSize * 0.16;
            double axis = fontSize * 0.28;

            BarWidth = Math.Max(numerator.Width, denominator.Width) + fontSize * 0.35;
            Width = BarWidth;

            double numX = (BarWidth - numerator.Width) / 2;
            double denX = (BarWidth - denominator.Width) / 2;

            numerator.Offset = new Point(numX, 0);
            BarY = numerator.Height + gap;
            denominator.Offset = new Point(denX, BarY + BarThickness + gap);

            double totalHeight = denominator.Offset.Y + denominator.Height;
            Ascent = BarY + BarThickness / 2 + axis;
            Descent = totalHeight - Ascent;
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Numerator.Render(dc, pos, foreground);
            var pen = new Pen(foreground, BarThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(pos.X, pos.Y + BarY), new Point(pos.X + BarWidth, pos.Y + BarY));
            Denominator.Render(dc, pos, foreground);
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Numerator.RenderSvg(svg, pos, fgHex);
            double y = pos.Y + BarY;
            svg.AppendLine($"  <line x1=\"{pos.X:0.##}\" y1=\"{y:0.##}\" x2=\"{(pos.X + BarWidth):0.##}\" y2=\"{y:0.##}\" stroke=\"{fgHex}\" stroke-width=\"{BarThickness:0.##}\" stroke-linecap=\"round\" />");
            Denominator.RenderSvg(svg, pos, fgHex);
        }
    }

    internal sealed class ScriptBox : LatexBox
    {
        public LatexBox? Base { get; }
        public LatexBox? Subscript { get; }
        public LatexBox? Superscript { get; }

        public ScriptBox(LatexBox? baseBox, LatexBox? subscript, LatexBox? superscript, double fontSize)
        {
            Base = baseBox;
            Subscript = subscript;
            Superscript = superscript;

            double baseW = baseBox?.Width ?? 0;
            double baseAscent = baseBox?.Ascent ?? fontSize * 0.7;
            double baseDescent = baseBox?.Descent ?? fontSize * 0.2;

            double scriptX = baseW + fontSize * 0.04;
            double scriptW = Math.Max(subscript?.Width ?? 0, superscript?.Width ?? 0);
            Width = scriptX + scriptW;

            double supShift = baseAscent * 0.45;
            double subShift = baseDescent * 0.35 + (subscript?.Ascent ?? 0) * 0.3;

            if (superscript != null)
            {
                superscript.Offset = new Point(scriptX, 0);
            }

            double baseTop = superscript != null ? Math.Max(0, superscript.Height - supShift) : 0;
            if (baseBox != null)
            {
                baseBox.Offset = new Point(0, baseTop);
            }

            if (subscript != null)
            {
                double subY = baseTop + baseAscent + subShift;
                subscript.Offset = new Point(scriptX, subY);
            }

            double totalH = Math.Max(baseTop + (baseBox?.Height ?? 0),
                Math.Max(superscript != null ? superscript.Offset.Y + superscript.Height : 0,
                         subscript != null ? subscript.Offset.Y + subscript.Height : 0));

            Ascent = baseTop + baseAscent;
            Descent = totalH - Ascent;
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Base?.Render(dc, pos, foreground);
            Superscript?.Render(dc, pos, foreground);
            Subscript?.Render(dc, pos, foreground);
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Base?.RenderSvg(svg, pos, fgHex);
            Superscript?.RenderSvg(svg, pos, fgHex);
            Subscript?.RenderSvg(svg, pos, fgHex);
        }
    }

    internal sealed class RootBox : LatexBox
    {
        public LatexBox Radicand { get; }
        public LatexBox? Degree { get; }
        public double FontSize { get; }
        public double TickWidth { get; }
        public double VinculumY { get; }
        public double LineThickness { get; }

        public RootBox(LatexBox radicand, LatexBox? degree, double fontSize)
        {
            Radicand = radicand;
            Degree = degree;
            FontSize = fontSize;

            TickWidth = fontSize * 0.65;
            LineThickness = Math.Max(1.2, fontSize * 0.05);
            double topGap = fontSize * 0.18;
            VinculumY = topGap * 0.5;

            Width = TickWidth + radicand.Width + fontSize * 0.15;
            radicand.Offset = new Point(TickWidth + fontSize * 0.05, topGap);

            if (degree != null)
            {
                degree.Offset = new Point(TickWidth * 0.1, 0);
            }

            double totalHeight = radicand.Offset.Y + radicand.Height;
            Ascent = radicand.Offset.Y + radicand.Ascent;
            Descent = totalHeight - Ascent;
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Degree?.Render(dc, pos, foreground);
            Radicand.Render(dc, pos, foreground);

            double baseY = pos.Y + Radicand.Offset.Y + Radicand.Ascent;
            double botY = pos.Y + Radicand.Offset.Y + Radicand.Height;
            double topY = pos.Y + VinculumY;

            var p1 = new Point(pos.X, baseY - FontSize * 0.2);
            var p2 = new Point(pos.X + TickWidth * 0.3, baseY);
            var p3 = new Point(pos.X + TickWidth * 0.6, botY);
            var p4 = new Point(pos.X + TickWidth, topY);
            var p5 = new Point(pos.X + Width, topY);

            var pen = new Pen(foreground, LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, p1, p2);
            dc.DrawLine(pen, p2, p3);
            dc.DrawLine(pen, p3, p4);
            dc.DrawLine(pen, p4, p5);
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Degree?.RenderSvg(svg, pos, fgHex);
            Radicand.RenderSvg(svg, pos, fgHex);

            double baseY = pos.Y + Radicand.Offset.Y + Radicand.Ascent;
            double botY = pos.Y + Radicand.Offset.Y + Radicand.Height;
            double topY = pos.Y + VinculumY;

            double p1x = pos.X, p1y = baseY - FontSize * 0.2;
            double p2x = pos.X + TickWidth * 0.3, p2y = baseY;
            double p3x = pos.X + TickWidth * 0.6, p3y = botY;
            double p4x = pos.X + TickWidth, p4y = topY;
            double p5x = pos.X + Width, p5y = topY;

            svg.AppendLine($"  <path d=\"M {p1x:0.##} {p1y:0.##} L {p2x:0.##} {p2y:0.##} L {p3x:0.##} {p3y:0.##} L {p4x:0.##} {p4y:0.##} L {p5x:0.##} {p5y:0.##}\" stroke=\"{fgHex}\" stroke-width=\"{LineThickness:0.##}\" fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\" />");
        }
    }

    internal sealed class BigOperatorBox : LatexBox
    {
        public TextBox OperatorBox { get; }
        public LatexBox? Lower { get; }
        public LatexBox? Upper { get; }
        public bool IsIntegral { get; }

        public BigOperatorBox(string op, string glyph, LatexBox? lower, LatexBox? upper, double fontSize, FontFamily family)
        {
            IsIntegral = op.StartsWith("int", StringComparison.Ordinal) || op == "oint";
            OperatorBox = new TextBox(glyph, fontSize * 1.45, family, FontStyles.Normal, FontWeights.Normal, isOperator: true);
            Lower = lower;
            Upper = upper;

            if (IsIntegral)
            {
                // Integral: limits stacked on right side like a script
                double opW = OperatorBox.Width;
                double scriptW = Math.Max(lower?.Width ?? 0, upper?.Width ?? 0);
                Width = opW + scriptW + fontSize * 0.05;

                OperatorBox.Offset = new Point(0, upper != null ? upper.Height * 0.4 : 0);
                if (upper != null) upper.Offset = new Point(opW, 0);
                if (lower != null) lower.Offset = new Point(opW, OperatorBox.Offset.Y + OperatorBox.Height * 0.7);

                Ascent = OperatorBox.Offset.Y + OperatorBox.Ascent;
                double totalH = Math.Max(OperatorBox.Offset.Y + OperatorBox.Height, lower != null ? lower.Offset.Y + lower.Height : 0);
                Descent = totalH - Ascent;
            }
            else
            {
                // Sum, Prod: limits centered above and below
                double maxW = Math.Max(OperatorBox.Width, Math.Max(lower?.Width ?? 0, upper?.Width ?? 0));
                Width = maxW;

                double curY = 0;
                if (upper != null)
                {
                    upper.Offset = new Point((maxW - upper.Width) / 2, 0);
                    curY += upper.Height + fontSize * 0.08;
                }

                OperatorBox.Offset = new Point((maxW - OperatorBox.Width) / 2, curY);
                curY += OperatorBox.Height + fontSize * 0.08;

                if (lower != null)
                {
                    lower.Offset = new Point((maxW - lower.Width) / 2, curY);
                    curY += lower.Height;
                }

                Ascent = OperatorBox.Offset.Y + OperatorBox.Ascent;
                Descent = curY - Ascent;
            }
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            OperatorBox.Render(dc, pos, foreground);
            Upper?.Render(dc, pos, foreground);
            Lower?.Render(dc, pos, foreground);
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            OperatorBox.RenderSvg(svg, pos, fgHex);
            Upper?.RenderSvg(svg, pos, fgHex);
            Lower?.RenderSvg(svg, pos, fgHex);
        }
    }

    internal sealed class DelimitedBox : LatexBox
    {
        public string Left { get; }
        public LatexBox Content { get; }
        public string Right { get; }
        public double DelimWidth { get; }
        public double DelimHeight { get; }

        public DelimitedBox(string left, LatexBox content, string right, double fontSize, FontFamily family)
        {
            Left = left;
            Content = content;
            Right = right;

            DelimHeight = Math.Max(fontSize, content.Height);
            DelimWidth = fontSize * 0.35;
            double leftW = string.IsNullOrEmpty(left) ? 0 : DelimWidth;
            double rightW = string.IsNullOrEmpty(right) ? 0 : DelimWidth;

            Width = leftW + content.Width + rightW;
            content.Offset = new Point(leftW, 0);

            Ascent = content.Ascent;
            Descent = content.Descent;
        }

        public override void Render(DrawingContext dc, Point origin, Brush foreground)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Content.Render(dc, pos, foreground);

            var pen = new Pen(foreground, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (!string.IsNullOrEmpty(Left))
            {
                DrawDelimiter(dc, pen, Left, pos.X, pos.Y, DelimWidth, DelimHeight, isLeft: true);
            }
            if (!string.IsNullOrEmpty(Right))
            {
                DrawDelimiter(dc, pen, Right, pos.X + Content.Offset.X + Content.Width, pos.Y, DelimWidth, DelimHeight, isLeft: false);
            }
        }

        public override void RenderSvg(StringBuilder svg, Point origin, string fgHex)
        {
            var pos = new Point(origin.X + Offset.X, origin.Y + Offset.Y);
            Content.RenderSvg(svg, pos, fgHex);

            if (!string.IsNullOrEmpty(Left))
            {
                DrawDelimiterSvg(svg, fgHex, Left, pos.X, pos.Y, DelimWidth, DelimHeight, isLeft: true);
            }
            if (!string.IsNullOrEmpty(Right))
            {
                DrawDelimiterSvg(svg, fgHex, Right, pos.X + Content.Offset.X + Content.Width, pos.Y, DelimWidth, DelimHeight, isLeft: false);
            }
        }

        private static void DrawDelimiter(DrawingContext dc, Pen pen, string delim, double x, double y, double w, double h, bool isLeft)
        {
            if (delim is "(" or ")")
            {
                var geom = new PathGeometry();
                var fig = new PathFigure { StartPoint = isLeft ? new Point(x + w, y) : new Point(x, y), IsClosed = false };
                fig.Segments.Add(new BezierSegment(
                    isLeft ? new Point(x, y + h * 0.3) : new Point(x + w, y + h * 0.3),
                    isLeft ? new Point(x, y + h * 0.7) : new Point(x + w, y + h * 0.7),
                    isLeft ? new Point(x + w, y + h) : new Point(x, y + h),
                    true
                ));
                geom.Figures.Add(fig);
                dc.DrawGeometry(null, pen, geom);
            }
            else if (delim is "[" or "]")
            {
                double edgeX = isLeft ? x : x + w;
                double armX = isLeft ? x + w : x;
                dc.DrawLine(pen, new Point(armX, y), new Point(edgeX, y));
                dc.DrawLine(pen, new Point(edgeX, y), new Point(edgeX, y + h));
                dc.DrawLine(pen, new Point(edgeX, y + h), new Point(armX, y + h));
            }
            else if (delim is "|" or "\\|")
            {
                dc.DrawLine(pen, new Point(x + w / 2, y), new Point(x + w / 2, y + h));
            }
            else
            {
                // Fallback vertical bar
                dc.DrawLine(pen, new Point(x + w / 2, y), new Point(x + w / 2, y + h));
            }
        }

        private static void DrawDelimiterSvg(StringBuilder svg, string fgHex, string delim, double x, double y, double w, double h, bool isLeft)
        {
            if (delim is "(" or ")")
            {
                double startX = isLeft ? x + w : x;
                double endX = startX;
                double c1x = isLeft ? x : x + w;
                double c1y = y + h * 0.3;
                double c2x = isLeft ? x : x + w;
                double c2y = y + h * 0.7;
                svg.AppendLine($"  <path d=\"M {startX:0.##} {y:0.##} C {c1x:0.##} {c1y:0.##}, {c2x:0.##} {c2y:0.##}, {endX:0.##} {(y + h):0.##}\" stroke=\"{fgHex}\" stroke-width=\"1.4\" fill=\"none\" stroke-linecap=\"round\" />");
            }
            else if (delim is "[" or "]")
            {
                double edgeX = isLeft ? x : x + w;
                double armX = isLeft ? x + w : x;
                svg.AppendLine($"  <path d=\"M {armX:0.##} {y:0.##} L {edgeX:0.##} {y:0.##} L {edgeX:0.##} {(y + h):0.##} L {armX:0.##} {(y + h):0.##}\" stroke=\"{fgHex}\" stroke-width=\"1.4\" fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"miter\" />");
            }
            else
            {
                double midX = x + w / 2;
                svg.AppendLine($"  <line x1=\"{midX:0.##}\" y1=\"{y:0.##}\" x2=\"{midX:0.##}\" y2=\"{(y + h):0.##}\" stroke=\"{fgHex}\" stroke-width=\"1.4\" stroke-linecap=\"round\" />");
            }
        }
    }

    #endregion
}
