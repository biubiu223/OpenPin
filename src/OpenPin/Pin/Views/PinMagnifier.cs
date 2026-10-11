using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.Pin.Views;

/// <summary>
/// Alt-magnifier for image pins (PIN-16): a small click-through, non-activating window following the cursor that shows
/// enlarged pixels with a grid, the pixel position and its color.
/// </summary>
internal sealed class PinMagnifier : Window
{
    private const int Cells = 15;
    private readonly MagnifierView _view = new();
    private readonly TextBlock _pos = new() { FontSize = 11 };
    private readonly TextBlock _color = new() { FontSize = 12 };
    private readonly Border _swatch = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };

    public PinMagnifier()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        _swatch.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _pos.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _color.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _color.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        colorRow.Children.Add(_swatch);
        colorRow.Children.Add(_color);
        var stack = new StackPanel();
        stack.Children.Add(_view);
        stack.Children.Add(_pos);
        stack.Children.Add(colorRow);

        var border = new Border { Child = stack, Margin = new Thickness(10), Padding = new Thickness(6) };
        if (Application.Current?.TryFindResource("Style.Pin.Flyout") is Style s) border.Style = s;
        Content = border;
        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            WindowHelper.SetClickThrough(this, true);
        };
    }

    public Color CurrentColor { get; private set; }

    /// <summary>Updates the view for the pixel (<paramref name="px"/>, <paramref name="py"/>) of <paramref name="pixels"/> (BGRA).</summary>
    public void Update(byte[] pixels, int width, int height, int px, int py, ColorFormat format, int zoom, Point cursorPx)
    {
        _view.Set(pixels, width, height, px, py, Cells, Math.Clamp(zoom, 4, 16));
        CurrentColor = Sample(pixels, width, height, px, py);
        _pos.Text = $"({px}, {py})";
        _color.Text = ColorText.FormatCss(CurrentColor, format);
        _swatch.Background = BitmapUtil.Frozen(new SolidColorBrush(CurrentColor));
        if (!IsVisible) Show();
        UpdateLayout();

        // Place bottom-right of the cursor, flipped to stay on the monitor.
        var b = WindowHelper.GetBoundsPx(this);
        var mon = Monitors.FromPointPx((int)cursorPx.X, (int)cursorPx.Y).BoundsPx;
        int x = (int)cursorPx.X + 16, y = (int)cursorPx.Y + 16;
        if (x + b.Width > mon.X + mon.Width) x = (int)cursorPx.X - 16 - b.Width;
        if (y + b.Height > mon.Y + mon.Height) y = (int)cursorPx.Y - 16 - b.Height;
        WindowHelper.SetPositionPx(this, x, y);
    }

    public static Color Sample(byte[] bgra, int w, int h, int x, int y)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return Colors.Transparent;
        int i = (y * w + x) * 4;
        return Color.FromArgb(bgra[i + 3], bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    private sealed class MagnifierView : FrameworkElement
    {
        private byte[] _px = Array.Empty<byte>();
        private int _w, _h, _cx, _cy, _cells = Cells, _zoom = 8;

        public void Set(byte[] px, int w, int h, int cx, int cy, int cells, int zoom)
        {
            _px = px; _w = w; _h = h; _cx = cx; _cy = cy; _cells = cells; _zoom = zoom;
            Width = Height = cells * zoom;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            int half = _cells / 2;
            var outside = TryFindResource("Brush.SurfaceAlt") as Brush ?? Brushes.LightGray;
            for (int j = 0; j < _cells; j++)
            for (int i = 0; i < _cells; i++)
            {
                int x = _cx - half + i, y = _cy - half + j;
                var rect = new Rect(i * _zoom, j * _zoom, _zoom, _zoom);
                if (x < 0 || y < 0 || x >= _w || y >= _h) { dc.DrawRectangle(outside, null, rect); continue; }
                var c = Sample(_px, _w, _h, x, y);
                var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                b.Freeze();
                dc.DrawRectangle(b, null, rect);
            }
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
            gridPen.Freeze();
            for (int k = 1; k < _cells; k++)
            {
                dc.DrawLine(gridPen, new Point(k * _zoom + 0.5, 0), new Point(k * _zoom + 0.5, _cells * _zoom));
                dc.DrawLine(gridPen, new Point(0, k * _zoom + 0.5), new Point(_cells * _zoom, k * _zoom + 0.5));
            }
            // center pixel: dark + light double outline so it is visible on any color
            var dark = TryFindResource("Brush.Text") as Brush ?? Brushes.Black;
            var light = TryFindResource("Brush.Surface") as Brush ?? Brushes.White;
            var mark = new Rect(half * _zoom - 0.5, half * _zoom - 0.5, _zoom + 1, _zoom + 1);
            dc.DrawRectangle(null, new Pen(dark, 1), mark);
            mark.Inflate(-1, -1);
            dc.DrawRectangle(null, new Pen(light, 1), mark);
        }
    }
}
