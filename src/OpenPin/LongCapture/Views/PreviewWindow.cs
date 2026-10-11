using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenPin.Core;
using OpenPin.Core.Native;

namespace OpenPin.LongCapture.Views;

/// <summary>
/// Live preview next to the region: the whole stitched result scaled down, a marker of the current viewport, and a
/// frame colored by the last match (green = OK, red = failed). In crop mode two handles trim the start / end.
/// Excluded from capture, never activates.
/// </summary>
internal sealed class PreviewWindow : Window
{
    public const double Pad = 10;
    private const double HandleDip = 6;

    private readonly Grid _host;
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Border _status;
    private readonly Rectangle _viewport;
    private readonly Rectangle _shadeStart, _shadeEnd, _handleStart, _handleEnd;
    private readonly StackPanel _cropBar;
    private readonly TextBlock _cropInfo;

    private bool _vertical = true;
    private double _scale;       // preview px per result px
    private int _length;
    private int _viewportStart, _frameLines;
    private bool _cropMode;
    private int _dragging;       // 0 none, 1 start handle, 2 end handle

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Title = "OpenPin long capture preview";

        _viewport = new Rectangle { StrokeThickness = 1, IsHitTestVisible = false };
        _viewport.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _viewport.SetResourceReference(Shape.FillProperty, "Brush.LongCapture.ViewportFill");
        _shadeStart = Shade();
        _shadeEnd = Shade();
        _handleStart = Handle(1);
        _handleEnd = Handle(2);
        foreach (var e in new UIElement[] { _viewport, _shadeStart, _shadeEnd, _handleStart, _handleEnd }) _overlay.Children.Add(e);

        _host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _host.Children.Add(_image);
        _host.Children.Add(_overlay);
        _status = new Border { BorderThickness = new Thickness(2), Child = _host, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        _status.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        _cropInfo = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0), TextWrapping = TextWrapping.Wrap };
        _cropInfo.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        ResetButton = new Button { Content = Loc.Get("LongCapture.CropReset"), Style = (Style)FindResource("Style.LongCapture.TextButton"), Margin = new Thickness(0, 0, 4, 0) };
        DoneButton = new Button { Content = Loc.Get("LongCapture.CropDone"), Style = (Style)FindResource("Style.LongCapture.AccentButton") };
        _cropBar = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _cropBar.Children.Add(_cropInfo);
        _cropBar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { ResetButton, DoneButton } });
        ResetButton.Click += (_, _) => { TrimStart = TrimEnd = 0; RefreshOverlay(); TrimChanged?.Invoke(); };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_cropBar, Dock.Bottom);
        panel.Children.Add(_cropBar);
        panel.Children.Add(_status);
        var border = new Border { Style = (Style)FindResource("Style.ToolbarBorder"), Padding = new Thickness(6), Child = panel };
        Content = new Grid { Margin = new Thickness(Pad), Children = { border } };

        _overlay.MouseLeftButtonDown += OnOverlayDown;
        _overlay.MouseMove += OnOverlayMove;
        _overlay.MouseLeftButtonUp += (_, _) => { _dragging = 0; _overlay.ReleaseMouseCapture(); };
        _overlay.MouseWheel += OnOverlayWheel;

        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            WindowHelper.SetExcludeFromCapture(this, true);
        };
    }

    public Button ResetButton { get; }
    public Button DoneButton { get; }

    /// <summary>Lines trimmed at the start / end of the result (top/bottom or left/right).</summary>
    public int TrimStart { get; private set; }
    public int TrimEnd { get; private set; }

    public event Action? TrimChanged;

    /// <summary>Fixed preview area (DIPs) so the window does not jump while the result grows.</summary>
    public void SetArea(double widthDip, double heightDip, bool vertical)
    {
        _vertical = vertical;
        _host.Width = widthDip;
        _host.Height = heightDip;
        _cropInfo.MaxWidth = Math.Max(widthDip, 120);
        _cropBar.Width = Math.Max(widthDip, 120);
    }

    /// <summary><paramref name="good"/>: null = no match yet, true = last match OK, false = failed.</summary>
    public void Update(BitmapSource? preview, double scale, int length, int viewportStart, int frameLines, bool? good)
    {
        _scale = scale;
        _length = length;
        _viewportStart = viewportStart;
        _frameLines = frameLines;
        if (preview != null)
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _image.Source = preview;
            _image.Width = preview.PixelWidth / dpi;
            _image.Height = preview.PixelHeight / dpi;
            _image.HorizontalAlignment = HorizontalAlignment.Left;
            _image.VerticalAlignment = VerticalAlignment.Top;
            _overlay.Width = _image.Width;
            _overlay.Height = _image.Height;
            _overlay.HorizontalAlignment = HorizontalAlignment.Left;
            _overlay.VerticalAlignment = VerticalAlignment.Top;
        }
        _status.SetResourceReference(Border.BorderBrushProperty,
            good == null ? "Brush.Border" : good.Value ? "Brush.LongCapture.MatchOk" : "Brush.Danger");
        TrimStart = Math.Min(TrimStart, Math.Max(0, length - 1));
        TrimEnd = Math.Min(TrimEnd, Math.Max(0, length - 1 - TrimStart));
        RefreshOverlay();
    }

    public bool CropMode
    {
        get => _cropMode;
        set
        {
            _cropMode = value;
            _cropBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _overlay.Cursor = null;
            RefreshOverlay();
        }
    }

    // ------------------------------------------------------------------ overlay

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Result line → DIP offset along the scroll axis on the preview.</summary>
    private double ToDip(double line) => line * _scale / Dpi;

    private double ToLine(double dip) => _scale <= 0 ? 0 : dip * Dpi / _scale;

    private void Place(FrameworkElement e, double start, double extent, bool visible)
    {
        e.Visibility = visible && extent > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        double across = _vertical ? _overlay.Width : _overlay.Height;
        if (double.IsNaN(across)) across = 0;
        if (_vertical)
        {
            Canvas.SetLeft(e, 0); Canvas.SetTop(e, start);
            e.Width = across; e.Height = Math.Max(0, extent);
        }
        else
        {
            Canvas.SetLeft(e, start); Canvas.SetTop(e, 0);
            e.Width = Math.Max(0, extent); e.Height = across;
        }
    }

    private void RefreshOverlay()
    {
        bool any = _length > 0 && _scale > 0;
        Place(_viewport, ToDip(_viewportStart), ToDip(Math.Min(_frameLines, _length - _viewportStart)), any && !_cropMode);
        Place(_shadeStart, 0, ToDip(TrimStart), any && _cropMode);
        Place(_shadeEnd, ToDip(_length - TrimEnd), ToDip(TrimEnd), any && _cropMode);
        // Handles sit just inside the kept range so they stay visible at the very edges.
        Place(_handleStart, ToDip(TrimStart), HandleDip, any && _cropMode);
        Place(_handleEnd, ToDip(_length - TrimEnd) - HandleDip, HandleDip, any && _cropMode);
        _cropInfo.Text = Loc.Get(_vertical ? "LongCapture.CropHintVertical" : "LongCapture.CropHintHorizontal") + "\n" +
                         Loc.Format("LongCapture.CropInfo", TrimStart, TrimEnd);
    }

    private double AxisPos(MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        return _vertical ? p.Y : p.X;
    }

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        if (!_cropMode || _length <= 0) return;
        double pos = AxisPos(e);
        // Grab the nearer handle.
        _dragging = Math.Abs(pos - ToDip(TrimStart)) <= Math.Abs(pos - ToDip(_length - TrimEnd)) ? 1 : 2;
        _overlay.CaptureMouse();
        DragTo(pos);
        e.Handled = true;
    }

    private void OnOverlayMove(object sender, MouseEventArgs e)
    {
        if (!_cropMode) return;
        _overlay.Cursor = _vertical ? Cursors.SizeNS : Cursors.SizeWE;
        if (_dragging != 0) DragTo(AxisPos(e));
    }

    private void DragTo(double pos)
    {
        int line = (int)Math.Round(ToLine(pos));
        if (_dragging == 1) TrimStart = Math.Clamp(line, 0, Math.Max(0, _length - TrimEnd - 1));
        else if (_dragging == 2) TrimEnd = Math.Clamp(_length - line, 0, Math.Max(0, _length - TrimStart - 1));
        RefreshOverlay();
        TrimChanged?.Invoke();
    }

    private void OnOverlayWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_cropMode || _length <= 0) return;
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        int delta = e.Delta > 0 ? -step : step; // wheel down = move the handle down / right
        double pos = AxisPos(e);
        bool start = Math.Abs(pos - ToDip(TrimStart)) <= Math.Abs(pos - ToDip(_length - TrimEnd));
        if (start) TrimStart = Math.Clamp(TrimStart + delta, 0, Math.Max(0, _length - TrimEnd - 1));
        else TrimEnd = Math.Clamp(TrimEnd - delta, 0, Math.Max(0, _length - TrimStart - 1));
        RefreshOverlay();
        TrimChanged?.Invoke();
        e.Handled = true;
    }

    private static Rectangle Shade()
    {
        var r = new Rectangle { IsHitTestVisible = false };
        r.SetResourceReference(Shape.FillProperty, "Brush.LongCapture.Trimmed");
        return r;
    }

    private static Rectangle Handle(int which)
    {
        var r = new Rectangle { IsHitTestVisible = false, Tag = which, RadiusX = 2, RadiusY = 2 };
        r.SetResourceReference(Shape.FillProperty, "Brush.Accent");
        return r;
    }
}
