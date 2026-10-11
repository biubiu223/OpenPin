using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.LongCapture.Views;

/// <summary>
/// Hollow accent frame drawn just OUTSIDE the capture region (so it never appears in grabbed frames), excluded from
/// capture. The inside is fully transparent → mouse input goes to the app below. While <see cref="Editable"/>, a thin
/// ring around the region moves it and 8 handles resize it; afterwards the whole window is click-through.
/// </summary>
internal sealed class RegionFrameWindow : Window
{
    private const double RingDip = 12;   // grab ring width outside the region
    private const double HandleDip = 8;
    private const int MinSizePx = 40;

    private readonly FrameVisual _visual;
    private Int32Rect _regionPx;
    private bool _editable = true;

    // drag state
    private Hit _drag;
    private Int32Rect _dragStartRegion;
    private (int X, int Y) _dragStartCursor;

    [Flags]
    private enum Hit { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    public RegionFrameWindow(Int32Rect regionPx)
    {
        _regionPx = regionPx;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "OpenPin long capture frame";
        _visual = new FrameVisual(this);
        Content = _visual;
        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            ExcludedFromCapture = WindowHelper.SetExcludeFromCapture(this, true);
        };
    }

    /// <summary>Raised continuously while the user moves / resizes the region (physical pixels).</summary>
    public event Action<Int32Rect>? RegionDragged;

    /// <summary>False on Windows older than 10 2004 (the frame is still kept outside the region).</summary>
    public bool ExcludedFromCapture { get; private set; }

    /// <summary>Grab ring width in physical pixels (other windows keep out of it).</summary>
    public int RingPx => (int)Math.Ceiling(RingDip * MonitorScale);

    public Brush FrameBrush { get; set; } = Brushes.DodgerBlue;

    private double MonitorScale => Monitors.FromPointPx(_regionPx.X + _regionPx.Width / 2, _regionPx.Y + _regionPx.Height / 2).Scale;

    public Int32Rect RegionPx => _regionPx;

    public void SetRegion(Int32Rect regionPx)
    {
        _regionPx = regionPx;
        if (IsLoaded || new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero)
            WindowHelper.SetBoundsPx(this, LongCapturePlacement.Inflate(regionPx, RingPx), topmost: true);
        _visual.InvalidateVisual();
    }

    public void ShowFrame()
    {
        Show();
        SetRegion(_regionPx);
    }

    /// <summary>True before capturing starts: the region can be moved / resized.</summary>
    public bool Editable
    {
        get => _editable;
        set
        {
            _editable = value;
            WindowHelper.SetClickThrough(this, !value);
            Cursor = null;
            _visual.InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ hit testing / dragging

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Region rectangle in window DIPs.</summary>
    private Rect RegionDip
    {
        get
        {
            double dpi = Dpi, ring = RingPx / dpi;
            return new Rect(ring, ring, _regionPx.Width / dpi, _regionPx.Height / dpi);
        }
    }

    private IEnumerable<(Hit Hit, Point Center)> Handles()
    {
        var r = RegionDip;
        double o = HandleDip / 2 + 2; // just outside the region edge
        double l = r.Left - o, t = r.Top - o, rr = r.Right + o, b = r.Bottom + o, cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        yield return (Hit.Left | Hit.Top, new Point(l, t));
        yield return (Hit.Top, new Point(cx, t));
        yield return (Hit.Right | Hit.Top, new Point(rr, t));
        yield return (Hit.Right, new Point(rr, cy));
        yield return (Hit.Right | Hit.Bottom, new Point(rr, b));
        yield return (Hit.Bottom, new Point(cx, b));
        yield return (Hit.Left | Hit.Bottom, new Point(l, b));
        yield return (Hit.Left, new Point(l, cy));
    }

    private Hit HitTestAt(Point p)
    {
        if (!_editable) return Hit.None;
        foreach (var (hit, c) in Handles())
            if (Math.Abs(p.X - c.X) <= HandleDip && Math.Abs(p.Y - c.Y) <= HandleDip) return hit;
        return RegionDip.Contains(p) ? Hit.None : Hit.Move;
    }

    private static Cursor CursorFor(Hit hit) => hit switch
    {
        Hit.Move => Cursors.SizeAll,
        Hit.Left or Hit.Right => Cursors.SizeWE,
        Hit.Top or Hit.Bottom => Cursors.SizeNS,
        Hit.Left | Hit.Top or Hit.Right | Hit.Bottom => Cursors.SizeNWSE,
        Hit.Right | Hit.Top or Hit.Left | Hit.Bottom => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTestAt(e.GetPosition(this));
        if (hit == Hit.None) return;
        _drag = hit;
        _dragStartRegion = _regionPx;
        _dragStartCursor = LongCaptureNative.CursorPx;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == Hit.None)
        {
            Cursor = CursorFor(HitTestAt(e.GetPosition(this)));
            return;
        }
        try
        {
            var (cx, cy) = LongCaptureNative.CursorPx;
            var r = Apply(_drag, _dragStartRegion, cx - _dragStartCursor.X, cy - _dragStartCursor.Y, Monitors.VirtualScreenPx);
            if (!r.Equals(_regionPx)) RegionDragged?.Invoke(r);
        }
        catch (Exception ex) { Log.Error("Long capture: region drag failed", ex); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Hit.None) return;
        _drag = Hit.None;
        ReleaseMouseCapture();
    }

    /// <summary>New region after dragging <paramref name="hit"/> by (dx, dy), kept inside <paramref name="bounds"/>.</summary>
    private static Int32Rect Apply(Hit hit, Int32Rect r, int dx, int dy, Int32Rect bounds)
    {
        int left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height;
        int bRight = bounds.X + bounds.Width, bBottom = bounds.Y + bounds.Height;
        if (hit == Hit.Move)
        {
            int x = Math.Clamp(left + dx, bounds.X, bRight - r.Width), y = Math.Clamp(top + dy, bounds.Y, bBottom - r.Height);
            return new Int32Rect(x, y, r.Width, r.Height);
        }
        if (hit.HasFlag(Hit.Left)) left = Math.Clamp(left + dx, bounds.X, right - MinSizePx);
        if (hit.HasFlag(Hit.Right)) right = Math.Clamp(right + dx, left + MinSizePx, bRight);
        if (hit.HasFlag(Hit.Top)) top = Math.Clamp(top + dy, bounds.Y, bottom - MinSizePx);
        if (hit.HasFlag(Hit.Bottom)) bottom = Math.Clamp(bottom + dy, top + MinSizePx, bBottom);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    // ------------------------------------------------------------------ rendering

    private sealed class FrameVisual : FrameworkElement
    {
        private static readonly Brush GrabBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        private readonly RegionFrameWindow _owner;

        public FrameVisual(RegionFrameWindow owner) => _owner = owner;

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        protected override void OnRender(DrawingContext dc)
        {
            var r = _owner.RegionDip;
            double dpi = _owner.Dpi;
            var outer = new Rect(0, 0, ActualWidth, ActualHeight);
            if (_owner._editable)
            {
                // Nearly invisible ring = hit-testable "move" area (alpha 0 pixels would let clicks through).
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer), new RectangleGeometry(r));
                dc.DrawGeometry(GrabBrush, null, ring);
            }

            // Border: entirely outside the region (inner edge == region edge).
            double t = Math.Max(2, Math.Round(2 * dpi)) / dpi;
            var pen = new Pen(_owner.FrameBrush, t);
            if (_owner._editable) pen.DashStyle = new DashStyle(new double[] { 4, 2 }, 0);
            var br = r;
            br.Inflate(t / 2, t / 2);
            dc.DrawRectangle(null, pen, br);

            if (!_owner._editable) return;
            var handlePen = new Pen(_owner.FrameBrush, 1 / dpi * Math.Max(1, Math.Round(dpi)));
            var handleFill = (Brush?)_owner.TryFindResource("Brush.Surface") ?? Brushes.White;
            foreach (var (_, c) in _owner.Handles())
                dc.DrawRectangle(handleFill, handlePen, new Rect(c.X - HandleDip / 2, c.Y - HandleDip / 2, HandleDip, HandleDip));
        }
    }
}
