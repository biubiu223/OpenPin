using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation.Model.Items;

/// <summary>Numbered marker (ANN-05): a filled circle with 1/A/I…, optionally with a pointer toward <see cref="Tip"/>.</summary>
internal sealed class SerialItem : AnnotationItem
{
    public Point Center { get; set; }
    public int Value { get; set; } = 1;
    public SerialType Type { get; set; }
    public Color Color { get; set; } = Colors.Red;
    /// <summary>Circle diameter (image px).</summary>
    public double Size { get; set; } = 28;
    public Point? Tip { get; set; }

    public override AnnotationTool Tool => AnnotationTool.Serial;

    public string Label => SerialFormat.Format(Value, Type);

    private double Radius => Size / 2;

    private bool HasPointer => Tip is { } t && (t - Center).Length > Radius + 2;

    public override Rect Bounds
    {
        get
        {
            var r = new Rect(Center.X - Radius, Center.Y - Radius, Size, Size);
            if (HasPointer) r.Union(Tip!.Value);
            return Geo.Inflate(r, 2);
        }
    }

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var brush = Brush(Color);
        if (HasPointer)
        {
            var tip = Tip!.Value;
            var dir = tip - Center; dir.Normalize();
            var perp = new Vector(-dir.Y, dir.X);
            double w = Radius * 0.62;
            var g = Geo.Polyline(new[] { Center + perp * w, tip, Center - perp * w }, closed: true);
            dc.DrawGeometry(brush, null, g);
        }
        dc.DrawEllipse(brush, new Pen(Brush(Colors.White), Math.Max(1, Size / 14)), Center, Radius, Radius);
        var text = Label;
        double fontSize = Size * (text.Length <= 1 ? 0.58 : text.Length == 2 ? 0.5 : text.Length == 3 ? 0.4 : 0.32);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            fontSize, Brush(ContrastText(Color)), 1.0);
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }

    /// <summary>White on dark colors, black on light ones.</summary>
    public static Color ContrastText(Color c)
    {
        double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return lum > 170 && c.A > 100 ? Colors.Black : Colors.White;
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if ((p - Center).Length <= Radius + tolerance) return true;
        return HasPointer && Geo.DistanceToSegment(p, Center, Tip!.Value) <= Radius * 0.3 + tolerance;
    }

    public override IReadOnlyList<Handle> GetHandles()
    {
        var list = new List<Handle>();
        if (HasPointer) list.Add(new Handle(0, Tip!.Value, HandleKind.Point));
        return list;
    }

    public override void DragHandle(int id, Point p, ModifierKeys mods, AnnotationItem start)
    {
        if (id == 0) Tip = p;
    }

    public override void Offset(Vector v)
    {
        Center += v;
        // The pointer tip stays where it points (it marks something on the image).
    }

    public override void ApplyOptions(ToolOptions o)
    {
        Color = o.ColorValue; Size = o.Size; Type = o.SerialType;
    }

    public override void ReadOptions(ToolOptions o)
    {
        o.ColorValue = Color; o.Size = Size; o.SerialType = Type;
    }
}
