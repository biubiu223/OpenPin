using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OpenPin.Annotation.Toolbar;

/// <summary>Icon factory: Segoe Fluent Icons glyphs where they exist, small vector paths otherwise (all follow the button Foreground).</summary>
internal static class Icons
{
    // Segoe Fluent Icons / MDL2 code points.
    public const string Rectangle = "\uE739";
    public const string Ellipse = "\uEA3A";
    public const string Pencil = "\uE70F";
    public const string Highlighter = "\uE7E6";
    public const string Text = "\uE8D2";
    public const string Eraser = "\uE75C";
    public const string Watermark = "\uE8AC";
    public const string Magnifier = "\uE71E";
    public const string Undo = "\uE7A7";
    public const string Redo = "\uE7A6";
    public const string More = "\uE76C";
    public const string Less = "\uE76B";
    public const string Delete = "\uE74D";
    public const string Plus = "\uE710";
    public const string Minus = "\uE738";
    public const string Bold = "\uE8DD";
    public const string Italic = "\uE8DB";
    public const string Brush = "\uE771";
    public const string Settings = "\uE713";
    public const string ArrowRight = "\uE72A";

    private static Binding FgBinding() => new(nameof(Control.Foreground))
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ButtonBase), 1),
    };

    private static Path MakePath(string data, bool fill, double stroke = 1.5, double opacity = 1)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data),
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = opacity,
            SnapsToDevicePixels = true,
        };
        p.SetBinding(fill ? Shape.FillProperty : Shape.StrokeProperty, FgBinding());
        return p;
    }

    private static Grid Layer(params UIElement[] children)
    {
        var g = new Grid { Width = 16, Height = 16 };
        foreach (var c in children) g.Children.Add(c);
        return g;
    }

    public static UIElement Line() => MakePath("M3,13 L13,3", false, 1.6);

    public static UIElement Arrow() => new TextBlock
    {
        Text = ArrowRight,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new RotateTransform(-45),
    };

    public static UIElement Serial()
    {
        var e = new Ellipse { Width = 16, Height = 16, StrokeThickness = 1.3 };
        e.SetBinding(Shape.StrokeProperty, FgBinding());
        var t = new TextBlock
        {
            Text = "1", FontSize = 9.5, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1),
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        return Layer(e, t);
    }

    public static UIElement Mosaic() => Layer(
        MakePath("M2,2h4v4h-4z M10,2h4v4h-4z M6,6h4v4h-4z M2,10h4v4h-4z M10,10h4v4h-4z", true),
        MakePath("M2,2 H14 V14 H2 Z", false, 1.2));

    public static UIElement Spotlight() => Layer(
        MakePath("M1.5,2.5 H14.5 V13.5 H1.5 Z M8,4.5 A3.5,3.5 0 1 0 8,11.5 A3.5,3.5 0 1 0 8,4.5 Z", true, opacity: 0.45),
        MakePath("M8,4.5 A3.5,3.5 0 1 0 8,11.5 A3.5,3.5 0 1 0 8,4.5 Z", false, 1.3));

    /// <summary>Partial eraser: a stroke with a gap cut out.</summary>
    public static UIElement PartialErase() => Layer(
        MakePath("M2,12 C4,6 6,6 7,8 M10,7 C11,4 13,3 14.5,3", false, 1.6),
        MakePath("M8.5,9.5 A2.2,2.2 0 1 1 8.5,9.49 Z", false, 1.0, 0.6));

    public static UIElement Sector() => MakePath("M8,8 L8,2.5 A5.5,5.5 0 1 1 2.5,8 Z", false, 1.4);
    public static UIElement Arc() => MakePath("M8,2.5 A5.5,5.5 0 1 1 2.5,8", false, 1.4);

    public static UIElement Fill() => Layer(MakePath("M2.5,3.5 H13.5 V12.5 H2.5 Z", true, opacity: 0.85));

    public static UIElement Dash(Model.LineDash dash)
    {
        var p = MakePath("M2,8 L14,8", false, 2);
        p.StrokeStartLineCap = p.StrokeEndLineCap = dash == Model.LineDash.Dot ? PenLineCap.Round : PenLineCap.Flat;
        if (dash == Model.LineDash.Dash) p.StrokeDashArray = new DoubleCollection { 1.6, 1.1 };
        if (dash == Model.LineDash.Dot) { p.StrokeDashArray = new DoubleCollection { 0, 1.6 }; p.StrokeDashCap = PenLineCap.Round; }
        return p;
    }

    public static UIElement ArrowStyle(Model.ArrowStyle s) => s switch
    {
        Model.ArrowStyle.Tapered => MakePath("M1.5,8.4 L9,9.6 L9,12.5 L15,8 L9,3.5 L9,6.4 L1.5,7.6 Z", true),
        Model.ArrowStyle.Solid => Layer(MakePath("M1.5,8 L9,8", false, 1.6), MakePath("M9,4 L15,8 L9,12 Z", true)),
        Model.ArrowStyle.Line => MakePath("M1.5,8 L14,8 M10,4 L14,8 L10,12", false, 1.5),
        _ => Layer(MakePath("M5,8 L11,8", false, 1.6), MakePath("M1,8 L5.5,4.5 L5.5,11.5 Z M15,8 L10.5,4.5 L10.5,11.5 Z", true)),
    };

    /// <summary>Line end decoration icon (drawn at the right end; mirrored for the start).</summary>
    public static UIElement LineEnd(Model.LineEnd end, bool start)
    {
        UIElement icon = end switch
        {
            Model.LineEnd.Arrow => Layer(MakePath("M2,8 L10,8", false, 1.5), MakePath("M9,4.5 L14.5,8 L9,11.5 Z", true)),
            Model.LineEnd.OpenArrow => MakePath("M2,8 L14,8 M10,4.5 L14,8 L10,11.5", false, 1.5),
            Model.LineEnd.Dot => Layer(MakePath("M2,8 L11,8", false, 1.5), MakePath("M11,5.5 A2.5,2.5 0 1 1 11,10.5 A2.5,2.5 0 1 1 11,5.5 Z", true)),
            _ => MakePath("M2,8 L14,8", false, 1.5),
        };
        if (start && icon is FrameworkElement fe)
        {
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.RenderTransform = new ScaleTransform(-1, 1);
        }
        return icon;
    }

    public static UIElement Join(Model.LineJoinStyle j) => WithJoin(MakePath("M4,13 L4,4 L13,4", false, 3.5), j);

    private static Path WithJoin(Path p, Model.LineJoinStyle j)
    {
        p.StrokeLineJoin = j switch { Model.LineJoinStyle.Miter => PenLineJoin.Miter, Model.LineJoinStyle.Bevel => PenLineJoin.Bevel, _ => PenLineJoin.Round };
        p.StrokeStartLineCap = p.StrokeEndLineCap = PenLineCap.Flat;
        return p;
    }

    public static UIElement Cap(Model.LineCapStyle c)
    {
        var p = MakePath("M4,8 L12,8", false, 5);
        p.StrokeStartLineCap = p.StrokeEndLineCap = c switch
        {
            Model.LineCapStyle.Flat => PenLineCap.Flat,
            Model.LineCapStyle.Square => PenLineCap.Square,
            _ => PenLineCap.Round,
        };
        return p;
    }

    public static UIElement Connector(Model.ConnectorStyle c)
    {
        var boxes = MakePath("M1.5,2.5 h4 v4 h-4 Z M8.5,7.5 h6 v6 h-6 Z", false, 1.2);
        if (c == Model.ConnectorStyle.None) return Layer(boxes);
        var line = MakePath("M5.5,4.5 L8.5,7.5 M5.5,6.5 L8.5,13.5", false, 1.1);
        if (c == Model.ConnectorStyle.Dashed) line.StrokeDashArray = new DoubleCollection { 1.5, 1.5 };
        return Layer(boxes, line);
    }

    /// <summary>Hollow "A" (text outline option).</summary>
    public static UIElement OutlineA()
    {
        var ft = new FormattedText("A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 15, Brushes.Black, 1.0);
        var g = ft.BuildGeometry(new Point(8 - ft.Width / 2, 8 - ft.Height / 2 - 0.5));
        g.Freeze();
        var p = new Path { Data = g, Width = 16, Height = 16, StrokeThickness = 1 };
        p.SetBinding(Shape.StrokeProperty, FgBinding());
        return p;
    }

    /// <summary>"A" on a filled rounded box (text background option).</summary>
    public static UIElement BackgroundA()
    {
        var box = new Border { Width = 15, Height = 14, CornerRadius = new CornerRadius(3) };
        box.SetBinding(Border.BackgroundProperty, FgBinding());
        var t = new TextBlock
        {
            Text = "A", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        var g = new Grid { Width = 16, Height = 16 };
        g.Children.Add(box);
        g.Children.Add(t);
        return g;
    }

    /// <summary>Custom color button face: current color inside a rainbow ring.</summary>
    public static UIElement CustomColor(Color current)
    {
        var ring = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        string[] stops = { "#FFF5222D", "#FFFADB14", "#FF52C41A", "#FF13C2C2", "#FF1677FF", "#FF722ED1" };
        for (int i = 0; i < stops.Length; i++)
            ring.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stops[i]), i / (double)(stops.Length - 1)));
        var g = new Grid { Width = 18, Height = 18 };
        g.Children.Add(new Ellipse { Stroke = ring, StrokeThickness = 2.5 });
        g.Children.Add(new Ellipse { Margin = new Thickness(4), Fill = new SolidColorBrush(current) });
        return g;
    }
}
