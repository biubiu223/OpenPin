using System.Windows;
using System.Windows.Media;

namespace OpenPin.Pin.Views;

/// <summary>
/// Draws the pin's state shadow / glow in the transparent margin around the content (1 unit = 1 physical px).
/// Implemented as a few concentric 1px strokes instead of a DropShadowEffect so that huge pins stay cheap to render.
/// </summary>
internal sealed class GlowFrame : FrameworkElement
{
    private Color _color = Colors.Gray;
    private bool _strong;
    private bool _shadow = true;
    private int _size;
    private Rect _inner;

    public GlowFrame()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>Updates the look; <paramref name="inner"/> is the visible content rect inside this element.</summary>
    public void Set(Color color, bool strong, bool shadow, int size, Rect inner)
    {
        if (_color == color && _strong == strong && _shadow == shadow && _size == size && _inner == inner) return;
        _color = color; _strong = strong; _shadow = shadow; _size = size; _inner = inner;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_inner.Width <= 0 || _inner.Height <= 0) return;
        if (_shadow && _size > 0)
        {
            double peak = _strong ? 0.42 : 0.26;
            for (int i = 1; i <= _size; i++)
            {
                double t = 1 - (i - 1) / (double)_size;
                byte a = (byte)Math.Round(255 * peak * t * t);
                if (a == 0) continue;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(a, _color.R, _color.G, _color.B)), 1);
                pen.Freeze();
                var r = _inner;
                r.Inflate(i - 0.5, i - 0.5);
                dc.DrawRectangle(null, pen, r);
            }
        }
        if (_shadow || _strong)
        {
            // crisp 1px state line just outside the content
            byte a = (byte)(_strong ? 255 : 170);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(a, _color.R, _color.G, _color.B)), 1);
            pen.Freeze();
            var r = _inner;
            r.Inflate(0.5, 0.5);
            dc.DrawRectangle(null, pen, r);
        }
    }
}
