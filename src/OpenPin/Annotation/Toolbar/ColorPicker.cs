using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Annotation.Toolbar;

/// <summary>Compact HSV color picker (saturation/value square, hue bar, alpha bar, hex input) for the custom color flyout.</summary>
internal sealed class ColorPicker : UserControl
{
    private const double AreaW = 200, AreaH = 130, BarH = 14;
    private double _h, _s, _v, _a = 1;
    private bool _updating;

    private readonly Border _area;
    private readonly Ellipse _areaThumb = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Canvas _areaCanvas = new() { Width = AreaW, Height = AreaH };
    private readonly Canvas _hueCanvas = new() { Width = AreaW, Height = BarH };
    private readonly Canvas _alphaCanvas = new() { Width = AreaW, Height = BarH };
    private readonly Border _hueThumb = Thumb();
    private readonly Border _alphaThumb = Thumb();
    private readonly Rectangle _alphaGradient = new() { Width = AreaW, Height = BarH, RadiusX = 4, RadiusY = 4 };
    private readonly Border _preview = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBox _hex = new() { Width = 100 };

    public event Action<Color>? ColorChanged;

    public ColorPicker(Color initial)
    {
        _area = new Border { Width = AreaW, Height = AreaH, CornerRadius = new CornerRadius(4), ClipToBounds = true };
        var white = new Rectangle
        {
            Width = AreaW, Height = AreaH, RadiusX = 4, RadiusY = 4,
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var black = new Rectangle
        {
            Width = AreaW, Height = AreaH, RadiusX = 4, RadiusY = 4,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        };
        _areaCanvas.Children.Add(_area);
        _areaCanvas.Children.Add(white);
        _areaCanvas.Children.Add(black);
        _areaCanvas.Children.Add(_areaThumb);
        _areaCanvas.Background = Brushes.Transparent;
        Hook(_areaCanvas, p =>
        {
            _s = Math.Clamp(p.X / AreaW, 0, 1);
            _v = 1 - Math.Clamp(p.Y / AreaH, 0, 1);
        });

        var hue = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) hue.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        _hueCanvas.Children.Add(new Rectangle { Width = AreaW, Height = BarH, RadiusX = 4, RadiusY = 4, Fill = hue });
        _hueCanvas.Children.Add(_hueThumb);
        _hueCanvas.Background = Brushes.Transparent;
        Hook(_hueCanvas, p => _h = Math.Clamp(p.X / AreaW, 0, 0.9999) * 360);

        _alphaCanvas.Children.Add(new Rectangle { Width = AreaW, Height = BarH, RadiusX = 4, RadiusY = 4, Fill = Checker() });
        _alphaCanvas.Children.Add(_alphaGradient);
        _alphaCanvas.Children.Add(_alphaThumb);
        _alphaCanvas.Background = Brushes.Transparent;
        Hook(_alphaCanvas, p => _a = Math.Clamp(p.X / AreaW, 0, 1));

        _preview.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _hex.SetResourceReference(StyleProperty, "Style.Annotation.TextBox");
        _hex.SetResourceReference(ToolTipProperty, "Annotation.Color.Hex");
        _hex.LostKeyboardFocus += (_, _) => ApplyHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ApplyHex(); e.Handled = true; }
        };

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var previewHost = new Grid { Width = 26, Height = 26 };
        previewHost.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Checker() });
        previewHost.Children.Add(_preview);
        bottom.Children.Add(previewHost);
        bottom.Children.Add(new FrameworkElement { Width = 8 });
        bottom.Children.Add(_hex);

        var root = new StackPanel();
        root.Children.Add(_areaCanvas);
        root.Children.Add(new FrameworkElement { Height = 10 });
        root.Children.Add(_hueCanvas);
        root.Children.Add(new FrameworkElement { Height = 10 });
        root.Children.Add(_alphaCanvas);
        root.Children.Add(bottom);
        Content = root;

        SetColor(initial);
    }

    private static Border Thumb() => new()
    {
        Width = 8, Height = BarH + 4, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(2),
        BorderBrush = Brushes.White, Background = Brushes.Transparent, IsHitTestVisible = false,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.6 },
    };

    private static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    private void Hook(FrameworkElement e, Action<Point> set)
    {
        void Apply(MouseEventArgs a)
        {
            set(a.GetPosition(e));
            UpdateVisuals();
            Raise();
        }
        e.MouseLeftButtonDown += (_, a) => { e.CaptureMouse(); Apply(a); a.Handled = true; };
        e.MouseMove += (_, a) => { if (e.IsMouseCaptured) Apply(a); };
        e.MouseLeftButtonUp += (_, a) => { if (e.IsMouseCaptured) e.ReleaseMouseCapture(); };
    }

    public Color Color => WithAlpha(FromHsv(_h, _s, _v), _a);

    public void SetColor(Color c)
    {
        var (h, s, v) = ColorText.ToHsv(c);
        // Keep the hue when the color is gray (hue undefined).
        if (s > 0.001 && v > 0.001) _h = h;
        _s = s; _v = v; _a = c.A / 255.0;
        UpdateVisuals();
    }

    private void ApplyHex()
    {
        if (_updating) return;
        var text = _hex.Text.Trim();
        if (!ColorText.TryParse(text, out var c)) { UpdateVisuals(); return; }
        // "#RRGGBB" keeps the current alpha.
        if (text.TrimStart('#').Length == 6) c = WithAlpha(c, _a);
        SetColor(c);
        Raise();
    }

    private void Raise()
    {
        try { ColorChanged?.Invoke(Color); }
        catch (Exception ex) { Log.Error("Color change handler failed", ex); }
    }

    private void UpdateVisuals()
    {
        _updating = true;
        var pure = FromHsv(_h, 1, 1);
        _area.Background = new SolidColorBrush(pure);
        Canvas.SetLeft(_areaThumb, _s * AreaW - 6);
        Canvas.SetTop(_areaThumb, (1 - _v) * AreaH - 6);
        _areaThumb.Stroke = _v > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_hueThumb, _h / 360 * AreaW - 4);
        Canvas.SetTop(_hueThumb, -2);
        var opaque = FromHsv(_h, _s, _v);
        _alphaGradient.Fill = new LinearGradientBrush(WithAlpha(opaque, 0), opaque, 0);
        Canvas.SetLeft(_alphaThumb, _a * AreaW - 4);
        Canvas.SetTop(_alphaThumb, -2);
        var c = Color;
        _preview.Background = new SolidColorBrush(c);
        if (!_hex.IsKeyboardFocused) _hex.Text = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        _updating = false;
    }

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
