using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.Recording.UI;

internal enum FrameState { Ready, Recording, Paused }

/// <summary>
/// Hollow frame drawn just OUTSIDE the recorded region (REC-01): blue when ready, red while recording, orange when paused.
/// The window covers the region plus a <see cref="Band"/> band; the inside is fully transparent, so clicks pass through to
/// the windows being recorded. In the ready state the band is grabbable: handles resize, the rest of the band moves the
/// region. While recording the whole window is click-through. Excluded from screen capture.
/// </summary>
internal sealed class RegionFrameWindow : Window
{
    /// <summary>Physical-pixel band around the region (room for the line and the handles).</summary>
    public const int Band = 10;
    private const int Line = 2, Handle = 8, MinSize = 32;

    private readonly FrameVisual _visual;
    private Int32Rect _region;
    private FrameState _state = FrameState.Ready;
    private string? _countdown;

    // drag state
    private int _dragZone = -1; // 0..7 handles (TL,T,TR,R,BR,B,BL,L), 8 = move
    private Int32Rect _dragStartRegion;
    private Point _dragStartCursor;

    public RegionFrameWindow(Int32Rect regionPx)
    {
        _region = regionPx;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "OpenPin Recording Frame";
        _visual = new FrameVisual(this);
        Content = _visual;
        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            ExcludedFromCapture = WindowHelper.SetExcludeFromCapture(this, true);
            ApplyBounds();
        };
        DpiChanged += (_, _) => _visual.InvalidateVisual();
    }

    public bool ExcludedFromCapture { get; private set; }

    public Int32Rect RegionPx => _region;

    /// <summary>Raised while the user moves/resizes the region (ready state).</summary>
    public event Action<Int32Rect>? RegionChanged;

    /// <summary>Keyboard input forwarded to the session (the window itself rarely has focus).</summary>
    public event KeyEventHandler? KeyPressed;

    public void SetRegion(Int32Rect regionPx)
    {
        _region = regionPx;
        ApplyBounds();
    }

    public void SetState(FrameState state)
    {
        _state = state;
        WindowHelper.SetClickThrough(this, state != FrameState.Ready);
        _visual.InvalidateVisual();
    }

    /// <summary>Show a big countdown number in the middle (null = none).</summary>
    public void SetCountdown(string? text)
    {
        _countdown = text;
        _visual.InvalidateVisual();
    }

    private void ApplyBounds()
    {
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle == IntPtr.Zero) return; // SourceInitialized applies it
        WindowHelper.SetBoundsPx(this, new Int32Rect(_region.X - Band, _region.Y - Band, _region.Width + 2 * Band, _region.Height + 2 * Band), topmost: true);
        _visual.InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyPressed?.Invoke(this, e);
        base.OnKeyDown(e);
    }

    // ------------------------------------------------------------------ hit testing / dragging (physical px)

    private double Scale => WindowHelper.GetDpiScale(this);

    private int HitZone(Point px)
    {
        // px relative to the window's top-left
        double l = Band, t = Band, r = Band + _region.Width, b = Band + _region.Height;
        double cx = (l + r) / 2, cy = (t + b) / 2;
        var handles = new[] { (l, t), (cx, t), (r, t), (r, cy), (r, b), (cx, b), (l, b), (l, cy) };
        for (int i = 0; i < handles.Length; i++)
            if (Math.Abs(px.X - handles[i].Item1) <= Handle && Math.Abs(px.Y - handles[i].Item2) <= Handle) return i;
        return 8;
    }

    private static Cursor CursorFor(int zone) => zone switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        3 or 7 => Cursors.SizeWE,
        _ => Cursors.SizeAll,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_state != FrameState.Ready) return;
        var p = e.GetPosition(this);
        var px = new Point(p.X * Scale, p.Y * Scale);
        if (_dragZone < 0)
        {
            Cursor = CursorFor(HitZone(px));
            return;
        }
        var cur = Monitors.CursorPx;
        int dx = (int)(cur.X - _dragStartCursor.X), dy = (int)(cur.Y - _dragStartCursor.Y);
        var s = _dragStartRegion;
        int x0 = s.X, y0 = s.Y, x1 = s.X + s.Width, y1 = s.Y + s.Height;
        switch (_dragZone)
        {
            case 8: x0 += dx; x1 += dx; y0 += dy; y1 += dy; break;
            case 0: x0 += dx; y0 += dy; break;
            case 1: y0 += dy; break;
            case 2: x1 += dx; y0 += dy; break;
            case 3: x1 += dx; break;
            case 4: x1 += dx; y1 += dy; break;
            case 5: y1 += dy; break;
            case 6: x0 += dx; y1 += dy; break;
            case 7: x0 += dx; break;
        }
        var v = Monitors.VirtualScreenPx;
        if (_dragZone == 8)
        {
            // keep the size, clamp inside the virtual screen
            int w = s.Width, h = s.Height;
            x0 = Math.Clamp(x0, v.X, v.X + v.Width - w);
            y0 = Math.Clamp(y0, v.Y, v.Y + v.Height - h);
            x1 = x0 + w; y1 = y0 + h;
        }
        else
        {
            x0 = Math.Max(v.X, Math.Min(x0, x1 - MinSize));
            y0 = Math.Max(v.Y, Math.Min(y0, y1 - MinSize));
            x1 = Math.Min(v.X + v.Width, Math.Max(x1, x0 + MinSize));
            y1 = Math.Min(v.Y + v.Height, Math.Max(y1, y0 + MinSize));
        }
        var next = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        if (next != _region)
        {
            SetRegion(next);
            RegionChanged?.Invoke(next);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_state != FrameState.Ready) return;
        var p = e.GetPosition(this);
        _dragZone = HitZone(new Point(p.X * Scale, p.Y * Scale));
        _dragStartRegion = _region;
        _dragStartCursor = Monitors.CursorPx;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragZone >= 0)
        {
            _dragZone = -1;
            ReleaseMouseCapture();
        }
    }

    // ------------------------------------------------------------------ rendering

    private sealed class FrameVisual : FrameworkElement
    {
        private readonly RegionFrameWindow _w;
        public FrameVisual(RegionFrameWindow w) => _w = w;

        protected override void OnRender(DrawingContext dc)
        {
            double s = _w.Scale;
            var r = _w._region;
            double W = (r.Width + 2 * Band) / s, H = (r.Height + 2 * Band) / s, m = Band / s;
            double inW = r.Width / s, inH = r.Height / s;

            var key = _w._state switch
            {
                FrameState.Recording => "Brush.Recording.Active",
                FrameState.Paused => "Brush.Recording.Paused",
                _ => "Brush.Recording.Ready",
            };
            var brush = (Brush?)_w.TryFindResource(key) ?? Brushes.DodgerBlue;

            // grabbable band (ready only): almost transparent, so it receives the mouse
            if (_w._state == FrameState.Ready && _w.TryFindResource("Brush.Recording.HitBand") is Brush band)
            {
                var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
                g.Children.Add(new RectangleGeometry(new Rect(0, 0, W, H)));
                g.Children.Add(new RectangleGeometry(new Rect(m, m, inW, inH)));
                dc.DrawGeometry(band, null, g);
            }

            // the line: Line px wide, immediately outside the region
            double lw = Line / s;
            var outer = new Rect(m - lw, m - lw, inW + 2 * lw, inH + 2 * lw);
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new RectangleGeometry(outer));
            ring.Children.Add(new RectangleGeometry(new Rect(m, m, inW, inH)));
            dc.DrawGeometry(brush, null, ring);

            if (_w._state == FrameState.Ready)
            {
                var fill = (Brush?)_w.TryFindResource("Brush.Surface") ?? Brushes.White;
                var pen = new Pen(brush, 1.5 / s);
                double hs = 7 / s;
                double l = m - lw / 2, t = m - lw / 2, rr = m + inW + lw / 2, b = m + inH + lw / 2, cx = (l + rr) / 2, cy = (t + b) / 2;
                foreach (var (x, y) in new[] { (l, t), (cx, t), (rr, t), (rr, cy), (rr, b), (cx, b), (l, b), (l, cy) })
                    dc.DrawRoundedRectangle(fill, pen, new Rect(x - hs / 2, y - hs / 2, hs, hs), 1.5 / s, 1.5 / s);
            }

            if (_w._countdown != null)
            {
                var face = new Typeface((FontFamily?)_w.TryFindResource("Font.UI") ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                double size = Math.Clamp(Math.Min(inW, inH) * 0.3, 24, 120);
                var ft = new FormattedText(_w._countdown, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size,
                    (Brush?)_w.TryFindResource("Brush.OnAccent") ?? Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double d = Math.Max(ft.Width, ft.Height) + size * 0.6;
                var center = new Point(m + inW / 2, m + inH / 2);
                dc.DrawEllipse((Brush?)_w.TryFindResource("Brush.Recording.Countdown") ?? Brushes.Black, null, center, d / 2, d / 2);
                dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
            }
        }
    }
}
