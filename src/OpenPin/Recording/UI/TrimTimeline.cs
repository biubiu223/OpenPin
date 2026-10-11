using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.UI;

/// <summary>
/// Timeline with a playhead and two trim handles (REC-03 / PRO-07). Drag a handle to trim, click or drag elsewhere to seek.
/// Times in ms. Colors from Theme / Recording.xaml tokens.
/// </summary>
internal sealed class TrimTimeline : FrameworkElement
{
    private const double HandleW = 10, Side = 8;
    private int _drag; // 0 none, 1 start handle, 2 end handle, 3 playhead

    public TrimTimeline()
    {
        Height = 40;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Duration { get; set; } = 1;
    public double TrimStart { get; private set; }
    public double TrimEnd { get; private set; } = 1;
    public double Position { get; private set; }

    /// <summary>User moved the playhead (ms).</summary>
    public event Action<double>? Seeked;
    /// <summary>User changed the trim range.</summary>
    public event Action<double, double>? TrimChanged;

    public void SetTrim(double start, double end)
    {
        (TrimStart, TrimEnd) = Timeline.ClampTrim(start, end, Duration);
        InvalidateVisual();
    }

    public void SetPosition(double ms)
    {
        Position = Math.Clamp(ms, 0, Duration);
        InvalidateVisual();
    }

    private double TrackLeft => Side + HandleW;
    private double TrackWidth => Math.Max(1, ActualWidth - 2 * (Side + HandleW));
    private double X(double ms) => TrackLeft + ms / Math.Max(1, Duration) * TrackWidth;
    private double Ms(double x) => Math.Clamp((x - TrackLeft) / TrackWidth * Duration, 0, Duration);

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable everywhere
        double top = 8, height = h - 16;
        var track = new Rect(TrackLeft, top, TrackWidth, height);
        dc.DrawRoundedRectangle(Res("Brush.SurfaceAlt", Brushes.WhiteSmoke), new Pen(Res("Brush.Border", Brushes.LightGray), 1), track, 4, 4);

        double xs = X(TrimStart), xe = X(TrimEnd);
        var accent = Res("Brush.Accent", Brushes.DodgerBlue);
        // selection + shaded trimmed-away parts
        dc.DrawRectangle(Res("Brush.Recording.TrimRange", Brushes.LightBlue), null, new Rect(xs, top, Math.Max(0, xe - xs), height));
        var shade = Res("Brush.Recording.TrimShade", Brushes.Gray);
        if (xs > track.Left) dc.DrawRectangle(shade, null, new Rect(track.Left, top, xs - track.Left, height));
        if (xe < track.Right) dc.DrawRectangle(shade, null, new Rect(xe, top, track.Right - xe, height));
        var pen = new Pen(accent, 2);
        dc.DrawLine(pen, new Point(xs, top), new Point(xe, top));
        dc.DrawLine(pen, new Point(xs, top + height), new Point(xe, top + height));

        // handles: accent tabs outside the selection with a white grip line
        var onAccent = new Pen(Res("Brush.OnAccent", Brushes.White), 1.5);
        dc.DrawRoundedRectangle(accent, null, new Rect(xs - HandleW, top - 2, HandleW, height + 4), 3, 3);
        dc.DrawLine(onAccent, new Point(xs - HandleW / 2, top + height / 2 - 5), new Point(xs - HandleW / 2, top + height / 2 + 5));
        dc.DrawRoundedRectangle(accent, null, new Rect(xe, top - 2, HandleW, height + 4), 3, 3);
        dc.DrawLine(onAccent, new Point(xe + HandleW / 2, top + height / 2 - 5), new Point(xe + HandleW / 2, top + height / 2 + 5));

        // playhead
        double xp = X(Position);
        var text = Res("Brush.Text", Brushes.Black);
        dc.DrawRectangle(text, null, new Rect(xp - 1, 2, 2, h - 4));
        dc.DrawEllipse(text, null, new Point(xp, 3), 4, 4);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        double x = e.GetPosition(this).X;
        double xs = X(TrimStart), xe = X(TrimEnd);
        if (x >= xs - HandleW - 2 && x <= xs + 2) _drag = 1;
        else if (x >= xe - 2 && x <= xe + HandleW + 2) _drag = 2;
        else
        {
            _drag = 3;
            Seek(x);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (_drag == 0)
        {
            double xs = X(TrimStart), xe = X(TrimEnd);
            Cursor = (x >= xs - HandleW - 2 && x <= xs + 2) || (x >= xe - 2 && x <= xe + HandleW + 2) ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        if (_drag == 3) { Seek(x); return; }
        double ms = Ms(_drag == 1 ? x + HandleW / 2 : x - HandleW / 2);
        double min = Math.Min(Timeline.MinTrimLengthMs, Duration);
        if (_drag == 1) TrimStart = Math.Clamp(ms, 0, TrimEnd - min);
        else TrimEnd = Math.Clamp(ms, TrimStart + min, Duration);
        InvalidateVisual();
        TrimChanged?.Invoke(TrimStart, TrimEnd);
        // show the frame at the handle being dragged
        Seeked?.Invoke(_drag == 1 ? TrimStart : Math.Max(TrimStart, TrimEnd - 1));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = 0;
        ReleaseMouseCapture();
    }

    private void Seek(double x)
    {
        Position = Ms(x);
        InvalidateVisual();
        Seeked?.Invoke(Position);
    }
}
