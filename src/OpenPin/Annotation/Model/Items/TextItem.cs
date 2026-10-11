using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model.Items;

/// <summary>Text (ANN-09): font, size, color, bold/italic, outline, background box, rotation.</summary>
internal sealed class TextItem : AnnotationItem
{
    private FormattedText? _ft;
    private long _ftVersion = -1;

    /// <summary>Top-left of the text layout (the background box extends outward by <see cref="Padding"/>).</summary>
    public Point Position { get; set; }
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 20;
    public Color Color { get; set; } = Colors.Red;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Outline { get; set; }
    public Color OutlineColor { get; set; } = Colors.White;
    public bool Background { get; set; }
    public Color BackgroundColor { get; set; } = Colors.White;
    public double BackgroundOpacity { get; set; } = 0.85;
    public double BackgroundRadius { get; set; } = 4;
    public double Padding { get; set; } = 6;
    public double Angle { get; set; }

    public override AnnotationTool Tool => AnnotationTool.Text;

    public Typeface Typeface => new(new FontFamily(FontFamily), Italic ? FontStyles.Italic : FontStyles.Normal,
        Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    public double OutlineWidth => Math.Max(2, FontSize / 7);

    public FormattedText Layout
    {
        get
        {
            if (_ft != null && _ftVersion == Version) return _ft;
            // A trailing newline has no height in FormattedText; add a space so the empty last line counts (like the TextBox).
            var text = Text.EndsWith('\n') || Text.Length == 0 ? Text + " " : Text;
            _ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface,
                Math.Max(1, FontSize), Brush(Color), 1.0);
            _ftVersion = Version;
            return _ft;
        }
    }

    /// <summary>Unrotated text layout rect.</summary>
    public Rect TextRect
    {
        get
        {
            var ft = Layout;
            return new Rect(Position, new Size(Math.Max(ft.WidthIncludingTrailingWhitespace, FontSize * 0.3), ft.Height));
        }
    }

    /// <summary>Unrotated frame: text rect + background padding.</summary>
    public Rect Frame => Geo.Inflate(TextRect, Background ? Padding : Outline ? OutlineWidth / 2 : 0);

    private Point Center { get { var r = TextRect; return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); } }

    public override Rect Bounds => Geo.Inflate(Geo.RotatedBounds(Frame, Angle), 2);

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Angle != 0) dc.PushTransform(new RotateTransform(Angle, Center.X, Center.Y));
        if (Background)
        {
            double r = Math.Min(BackgroundRadius, Math.Min(Frame.Width, Frame.Height) / 2);
            dc.DrawRoundedRectangle(Brush(WithAlpha(BackgroundColor, BackgroundOpacity)), null, Frame, r, r);
        }
        if (Text.Length > 0)
        {
            var ft = Layout;
            if (Outline)
            {
                var geom = ft.BuildGeometry(Position);
                var pen = new Pen(Brush(OutlineColor), OutlineWidth) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geom);
                dc.DrawGeometry(Brush(Color), null, geom);
            }
            else dc.DrawText(ft, Position);
        }
        if (Angle != 0) dc.Pop();
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var lp = Geo.Rotate(p, Center, -Angle);
        return Geo.Inflate(Frame, tolerance).Contains(lp);
    }

    public override IReadOnlyList<Handle> GetHandles()
    {
        var f = Frame;
        var c = Center;
        return new[] { new Handle(8, Geo.Rotate(new Point(c.X, f.Top - 24), c, Angle), HandleKind.Rotate) };
    }

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        if (id == 8) Angle = AngleTo(((TextItem)start).Center, p, (mods & ModifierKeys.Shift) != 0);
    }

    public override void Offset(Vector v) => Position += v;

    public override void ApplyOptions(ToolOptions o)
    {
        Color = o.ColorValue; FontSize = o.Size; FontFamily = o.FontFamily; Bold = o.Bold; Italic = o.Italic;
        Outline = o.Outline; OutlineColor = ToolOptions.Parse(o.OutlineColor, Colors.White);
        Background = o.Background; BackgroundColor = ToolOptions.Parse(o.BackgroundColor, Colors.White);
        BackgroundOpacity = o.BackgroundOpacity; BackgroundRadius = o.BackgroundRadius; Padding = o.BackgroundPadding;
    }

    public override void ReadOptions(ToolOptions o)
    {
        o.ColorValue = Color; o.Size = FontSize; o.FontFamily = FontFamily; o.Bold = Bold; o.Italic = Italic;
        o.Outline = Outline; o.OutlineColor = ToolOptions.ToText(OutlineColor);
        o.Background = Background; o.BackgroundColor = ToolOptions.ToText(BackgroundColor);
        o.BackgroundOpacity = BackgroundOpacity; o.BackgroundRadius = BackgroundRadius; o.BackgroundPadding = Padding;
    }
}
