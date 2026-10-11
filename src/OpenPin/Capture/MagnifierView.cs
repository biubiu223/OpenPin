using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Capture;

/// <summary>
/// CAP-05 magnifier: zoomed pixels (nearest neighbour, via an ImageBrush viewbox on the frozen snapshot — no pixel
/// copies per frame), grid, crosshair band, then position / selection size / color text.
/// Lives in the overlay's unscaled UI layer (DIPs).
/// </summary>
internal sealed class MagnifierView : Border
{
    private const double TargetBox = 168;

    private readonly ImageBrush _brush;
    private readonly GridOverlay _grid;
    private readonly TextBlock _pos = new();
    private readonly TextBlock _size = new();
    private readonly TextBlock _color = new();
    private readonly Border _swatch;
    private readonly int _zoom;
    private readonly int _cellsX;
    private readonly int _cellsY;

    public MagnifierView(int zoom, bool showGrid)
    {
        _zoom = Math.Clamp(zoom, 2, 32);
        _cellsX = MakeOdd((int)(TargetBox / _zoom));
        _cellsY = MakeOdd((int)(TargetBox * 0.75 / _zoom));
        double w = _cellsX * _zoom, h = _cellsY * _zoom;

        SetResourceReference(StyleProperty, "Style.Capture.InfoPanel");
        Padding = new Thickness(0);
        IsHitTestVisible = false;

        _brush = new ImageBrush
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        var zoomRect = new Rectangle { Width = w, Height = h, Fill = _brush };
        RenderOptions.SetBitmapScalingMode(zoomRect, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(zoomRect, EdgeMode.Aliased);

        _grid = new GridOverlay(_zoom, _cellsX, _cellsY, showGrid) { Width = w, Height = h };
        var pixels = new Grid { Width = w, Height = h, Background = Brushes.Black, ClipToBounds = true };
        pixels.Children.Add(zoomRect);
        pixels.Children.Add(_grid);
        var pixelsBorder = new Border
        {
            Child = pixels,
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            ClipToBounds = true,
        };

        _swatch = new Border
        {
            Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "Brush.Capture.InfoBorder");
        _color.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        colorRow.Children.Add(_swatch);
        colorRow.Children.Add(_color);

        var hint = new TextBlock { Text = Loc.Get("Capture.Magnifier.Hint"), Margin = new Thickness(0, 3, 0, 0), FontSize = 11 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Capture.InfoTextSecondary");

        var info = new StackPanel { Margin = new Thickness(8, 5, 8, 7) };
        info.Children.Add(_pos);
        info.Children.Add(_size);
        info.Children.Add(colorRow);
        info.Children.Add(hint);

        var stack = new StackPanel { Width = w };
        stack.Children.Add(pixelsBorder);
        stack.Children.Add(info);
        Child = stack;
    }

    private static int MakeOdd(int n) => Math.Max(5, n % 2 == 0 ? n - 1 : n);

    public void SetSource(BitmapSource? image) => _brush.ImageSource = image;

    /// <summary>Thin lines should stay ~1 device pixel whatever the effective scale is.</summary>
    public void SetDeviceScale(double scale) => _grid.LineThickness = 1 / Math.Max(0.25, scale);

    public void Update(int imgX, int imgY, int screenX, int screenY, Int32Rect? selection, Color color, ColorFormat format)
    {
        _brush.Viewbox = new Rect(imgX - _cellsX / 2, imgY - _cellsY / 2, _cellsX, _cellsY);
        _pos.Text = Loc.Format("Capture.Magnifier.Position", screenX, screenY);
        if (selection is { Width: > 0 } s)
        {
            _size.Text = Loc.Format("Capture.Magnifier.Size", s.Width, s.Height);
            _size.Visibility = Visibility.Visible;
        }
        else _size.Visibility = Visibility.Collapsed;
        _swatch.Background = new SolidColorBrush(color);
        _color.Text = FormatColor(color, format);
    }

    public static string FormatColor(Color c, ColorFormat format) => format switch
    {
        ColorFormat.Rgb => "RGB  " + ColorText.Format(c, ColorFormat.Rgb),
        ColorFormat.Hex => "HEX  " + ColorText.Format(c, ColorFormat.Hex),
        ColorFormat.Hsl => "HSL  " + ColorText.Format(c, ColorFormat.Hsl),
        _ => "HSV  " + ColorText.Format(c, ColorFormat.Hsv),
    };

    /// <summary>Grid lines + crosshair band + center cell outline.</summary>
    private sealed class GridOverlay : FrameworkElement
    {
        private readonly int _zoom, _cx, _cy;
        private readonly bool _showGrid;
        private double _thickness = 1;

        public GridOverlay(int zoom, int cx, int cy, bool showGrid)
        {
            _zoom = zoom; _cx = cx; _cy = cy; _showGrid = showGrid;
            IsHitTestVisible = false;
        }

        public double LineThickness
        {
            get => _thickness;
            set { if (Math.Abs(_thickness - value) > 0.001) { _thickness = value; InvalidateVisual(); } }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = _cx * _zoom, h = _cy * _zoom;
            int mx = _cx / 2, my = _cy / 2;
            var cross = TryFindResource("Brush.Capture.MagnifierCross") as Brush ?? new SolidColorBrush(Color.FromArgb(0x5C, 0x16, 0x77, 0xFF));
            dc.DrawRectangle(cross, null, new Rect(0, my * _zoom, w, _zoom));
            dc.DrawRectangle(cross, null, new Rect(mx * _zoom, 0, _zoom, h));

            if (_showGrid && _zoom >= 4)
            {
                var gridBrush = TryFindResource("Brush.Capture.MagnifierGrid") as Brush ?? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
                var pen = new Pen(gridBrush, _thickness);
                pen.Freeze();
                for (int i = 1; i < _cx; i++) dc.DrawLine(pen, new Point(i * _zoom, 0), new Point(i * _zoom, h));
                for (int j = 1; j < _cy; j++) dc.DrawLine(pen, new Point(0, j * _zoom), new Point(w, j * _zoom));
            }

            var center = new Rect(mx * _zoom, my * _zoom, _zoom, _zoom);
            var black = new Pen(Brushes.Black, _thickness * 3);
            var white = new Pen(Brushes.White, _thickness);
            dc.DrawRectangle(null, black, center);
            dc.DrawRectangle(null, white, center);
        }
    }
}
