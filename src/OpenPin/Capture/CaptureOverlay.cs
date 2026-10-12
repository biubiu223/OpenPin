using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenPin.Annotation;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Services;
using CaptureMode = OpenPin.Core.CaptureMode;

namespace OpenPin.Capture;

/// <summary>
/// The full-screen selection overlay: ONE borderless, opaque, topmost window over the whole virtual screen showing the
/// frozen snapshot. Layers:
/// <list type="bullet">
/// <item><b>pixel layer</b> (<see cref="_pixel"/>): ScaleTransform(1/dpi) so 1 unit = 1 physical px = 1 snapshot px —
/// snapshot image, annotation surface, mask, selection border/handles, guide lines, QR marks.</item>
/// <item><b>UI layer</b> (<see cref="_ui"/>): normal DIPs — toolbar, magnifier, size label, hints, flyouts. Each element
/// is additionally scaled to the DPI of the monitor it sits on.</item>
/// </list>
/// The overlay is its own <see cref="IModalSession"/> while open.
/// </summary>
internal sealed partial class CaptureOverlay : Window, IModalSession
{
    private enum DragKind { None, Pressing, Selecting, Adjusting }

    private readonly CaptureController _owner;
    private readonly ScreenSnapshot _snap;
    private readonly CaptureMode _mode;
    private readonly CaptureSettings _cfg;
    private readonly Stopwatch _clock;
    private readonly Int32Rect _bounds;
    private readonly List<Int32Rect> _monitorsImg;
    private readonly List<Rect> _monitorRects;
    private readonly List<Rect> _workRects;
    private readonly List<double> _monitorScales;
    private readonly DetectionTargets _targets;
    private readonly IntPtr _previousForeground;

    // ---- visuals
    private readonly Grid _root;
    private readonly Canvas _pixel;
    private readonly ScaleTransform _pixelScale = new(1, 1);
    private readonly Image _image;
    private readonly AnnotationSurface _surface;
    private readonly Path _mask;
    private readonly RectangleGeometry _maskHole = new();
    private readonly Rectangle _border;
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Rectangle _guideH;
    private readonly Rectangle _guideV;
    private readonly Canvas _qrMarks = new() { IsHitTestVisible = false };
    private readonly Canvas _ui;
    private readonly MagnifierView? _magnifier;
    private readonly SizeLabel _sizeLabel = new();
    private readonly ShortcutHints? _hints;
    private readonly ToastLabel _toast = new();
    private readonly QrPanel _qrPanel = new();
    private StylePanel? _stylePanel;
    private RecognizePanel? _recognizePanel;
    private AnnotationToolbar? _toolbar;
    private Rect _toolbarRectPx = Rect.Empty;

    // ---- state
    private double _dpi = 1.0;
    private bool _showCursor;
    private Int32Rect _sel = Int32Rect.Empty;
    private Int32Rect? _hover;
    private DetectTarget? _hoverTarget;
    private ElementLevels? _levels;
    private DragKind _drag;
    private HitKind _adjustKind;
    private Point _downPx;
    private Int32Rect _dragStartSel;
    private Point _mousePx;
    private bool _mouseKnown;
    private bool _visualsDirty;
    private bool _renderHooked;
    private ColorFormat _colorFormat = ColorFormat.Rgb;
    private bool _shiftClean;
    private bool _altDown;
    private bool _rightHandled;
    private bool _closing;
    private bool _shown;
    private bool _settingsDirty;
    private ElementDetector? _detector;
    private int _detectSeq;

    private bool HasSel => !SelectionMath.IsEmpty(_sel);

    public CaptureOverlay(CaptureController owner, ScreenSnapshot snap, CaptureMode mode, CaptureSettings cfg, Stopwatch clock)
    {
        _owner = owner;
        _snap = snap;
        _mode = mode;
        _cfg = cfg;
        _clock = clock;
        _showCursor = cfg.ShowCursor;
        _previousForeground = CaptureNative.GetForegroundWindow();
        _bounds = new Int32Rect(0, 0, snap.Image.PixelWidth, snap.Image.PixelHeight);

        _monitorsImg = snap.Monitors.Select(m => snap.ScreenToImage(m.BoundsPx)).ToList();
        if (_monitorsImg.Count == 0) _monitorsImg.Add(_bounds);
        _monitorRects = _monitorsImg.Select(r => new Rect(r.X, r.Y, r.Width, r.Height)).ToList();
        _workRects = snap.Monitors.Select(m => snap.ScreenToImage(m.WorkAreaPx)).Select(r => new Rect(r.X, r.Y, r.Width, r.Height)).ToList();
        if (_workRects.Count == 0) _workRects.AddRange(_monitorRects);
        _monitorScales = snap.Monitors.Select(m => m.Scale).ToList();
        if (_monitorScales.Count == 0) _monitorScales.Add(1.0);
        _targets = BuildTargets(snap, _monitorsImg, _bounds);

        // ---- window
        Title = "OpenPin Capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Left = 0; Top = 0; Width = 100; Height = 100;
        Background = Brushes.Black;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;

        // ---- pixel layer
        _pixel = new Canvas
        {
            Width = _bounds.Width,
            Height = _bounds.Height,
            RenderTransform = _pixelScale,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_pixel, BitmapScalingMode.NearestNeighbor);

        _image = new Image { Width = _bounds.Width, Height = _bounds.Height, Stretch = Stretch.Fill, IsHitTestVisible = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _pixel.Children.Add(_image);

        var maskBrush = ColorText.TryParse(cfg.MaskColor, out var maskColor)
            ? new SolidColorBrush(maskColor)
            : new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        maskBrush.Freeze();
        var maskGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        maskGeometry.Children.Add(new RectangleGeometry(new Rect(0, 0, _bounds.Width, _bounds.Height)));
        maskGeometry.Children.Add(_maskHole);
        _mask = new Path { Data = maskGeometry, Fill = maskBrush, IsHitTestVisible = false };
        _pixel.Children.Add(_mask);

        _surface = new AnnotationSurface(snap.Image);
        _surface.EditBounds = new Rect(0, 0, 0, 0);
        _surface.SnapRectProvider = SnapRect;
        _surface.ToolChanged += (_, _) => Guard(() =>
        {
            _stylePanel?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            _recognizePanel?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            MarkDirty();
        });
        _pixel.Children.Add(_surface);

        _pixel.Children.Add(_qrMarks);

        var accent = AccentBrush();
        _border = new Rectangle { Stroke = accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        _pixel.Children.Add(_border);
        for (int i = 0; i < _handles.Length; i++)
        {
            var h = new Rectangle { Stroke = accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
            h.SetResourceReference(Shape.FillProperty, "Brush.Surface");
            _handles[i] = h;
            _pixel.Children.Add(h);
        }

        _guideH = new Rectangle { Height = 1, Width = _bounds.Width, Fill = accent, Opacity = 0.8, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _guideV = new Rectangle { Width = 1, Height = _bounds.Height, Fill = accent, Opacity = 0.8, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _pixel.Children.Add(_guideH);
        _pixel.Children.Add(_guideV);

        // Canvas host: Canvas never clips its children, so the scaled-down pixel canvas is not layout-clipped.
        var pixelHost = new Canvas();
        pixelHost.Children.Add(_pixel);

        // ---- UI layer
        _ui = new Canvas();
        if (cfg.MagnifierEnabled)
        {
            _magnifier = new MagnifierView(cfg.MagnifierZoom, cfg.MagnifierGrid) { Visibility = Visibility.Collapsed };
            _ui.Children.Add(_magnifier);
        }
        _sizeLabel.Visibility = Visibility.Collapsed;
        _sizeLabel.SizeEntered += (w, h) => Guard(() => SetExactSize(w, h));
        _sizeLabel.EditEnded += () => Guard(() => { _root!.Focus(); MarkDirty(); });
        _ui.Children.Add(_sizeLabel);
        if (cfg.ShowShortcutHints)
        {
            _hints = new ShortcutHints { Visibility = Visibility.Collapsed };
            _ui.Children.Add(_hints);
        }
        _qrPanel.CopyRequested += r => Guard(() => CopyQrText(r));
        _qrPanel.OpenRequested += r => Guard(() => OpenQrLink(r));
        _qrPanel.ExpandRequested += () => Guard(() => ShowQrResults(expanded: true));
        _ui.Children.Add(_qrPanel);
        _ui.Children.Add(_toast);

        _root = new Grid { Focusable = true, FocusVisualStyle = null };
        _root.Children.Add(pixelHost);
        _root.Children.Add(_ui);
        Content = _root;

        RefreshDisplayImage();

        // ---- input
        PreviewMouseLeftButtonDown += (_, e) => Guard(() => OnLeftDown(e));
        PreviewMouseMove += (_, e) => Guard(() => OnOverlayMouseMove(e));
        PreviewMouseLeftButtonUp += (_, e) => Guard(() => OnLeftUp(e));
        PreviewMouseRightButtonDown += (_, e) => Guard(() => OnRightDown(e));
        PreviewMouseRightButtonUp += (_, e) => { if (_rightHandled) { e.Handled = true; _rightHandled = false; } };
        PreviewMouseWheel += (_, e) => Guard(() => OnWheel(e));
        LostMouseCapture += (_, _) => Guard(OnLostCapture);
        KeyDown += (_, e) => Guard(() => OnOverlayKeyDown(e));
        KeyUp += (_, e) => Guard(() => OnOverlayKeyUp(e));
        Deactivated += (_, _) => { _altDown = false; MarkDirty(); };

        // Single-letter shortcuts (C, R, Q, W/A/S/D…) must not be eaten by a Chinese IME: no IME on the overlay,
        // but any text box that gets focus (annotation text, size input…) gets it back.
        // (TSF IMEs such as Microsoft Pinyin ignore IsInputMethodEnabled on non-text elements, so the IME is
        // switched off for this UI thread while a non-text element has focus, and restored afterwards.)
        InputMethod.SetIsInputMethodEnabled(this, false);
        PreviewGotKeyboardFocus += (_, e) => SyncImeState(e.NewFocus);
    }

    // ================================================================================== lifecycle

    /// <summary>Position over the virtual screen (cloaked), show, and uncloak after the first frame.</summary>
    public void ShowOverlay(Int32Rect? initialSelectionPx)
    {
        var hwnd = WindowHelper.GetHandle(this);
        _hwnd = hwnd;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        CaptureNative.DisableTransitions(hwnd);
        CaptureNative.DisableRoundedCorners(hwnd);
        CaptureNative.SetCloaked(hwnd, true);
        WindowHelper.SetToolWindow(this, true);
        WindowHelper.SetBoundsPx(this, _snap.VirtualBoundsPx, topmost: true);
        UpdateDpi();

        var cursor = Monitors.CursorPx;
        _mousePx = _snap.ScreenToImage(cursor);
        _mouseKnown = SelectionMath.Contains(_bounds, _mousePx);
        if (initialSelectionPx is { } init && SelectionMath.Clamp(_snap.ScreenToImage(init), _bounds) is { Width: > 0 } clamped)
        {
            _sel = clamped;
            OnSelectionChanged();
        }
        else if (_mouseKnown) UpdateHover(_mousePx);

        AppServices.ActiveSession = this;
        ContentRendered += OnFirstFrame;
        Show();
        // WPF may re-apply its own size while showing; enforce the exact physical bounds again.
        WindowHelper.SetBoundsPx(this, _snap.VirtualBoundsPx, topmost: true);
        UpdateVisuals();
        Log.Info($"Capture overlay shown in {_clock.ElapsedMilliseconds} ms (dpi {_dpi:0.##}, window {WindowHelper.GetBoundsPx(this)})");

        // Safety net: never stay cloaked (invisible but blocking input) if ContentRendered does not come.
        var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        safety.Tick += (_, _) => { safety.Stop(); if (!_shown && !_closing) Reveal(); };
        safety.Start();
    }

    private bool _warmup;
    private IntPtr _hwnd;
    private long _imeKeyTick = long.MinValue / 2;
    private InputMethodState? _savedImeState;

    /// <summary>
    /// JIT / WPF warm-up at startup: build a tiny overlay with a selection (toolbar, magnifier…), show it CLOAKED
    /// (never visible, never activated) for one frame, then close it. Cuts the first real capture's latency.
    /// </summary>
    public static void Warmup(CaptureController owner)
    {
        var pixels = new byte[64 * 64 * 4];
        var img = BitmapUtil.FromPixels(pixels, 64, 64);
        var snap = new ScreenSnapshot
        {
            Image = img,
            VirtualBoundsPx = Monitors.VirtualScreenPx with { Width = 64, Height = 64 },
            Monitors = Monitors.GetAll(),
            Windows = Array.Empty<WindowInfo>(),
        };
        var o = new CaptureOverlay(owner, snap, CaptureMode.Edit, new CaptureSettings(), Stopwatch.StartNew()) { _warmup = true };
        o.ShowActivated = false;
        var hwnd = WindowHelper.GetHandle(o);
        CaptureNative.DisableTransitions(hwnd);
        CaptureNative.SetCloaked(hwnd, true);
        WindowHelper.SetToolWindow(o, true);
        WindowHelper.SetBoundsPx(o, snap.VirtualBoundsPx);
        o.UpdateDpi();
        o._mouseKnown = true;
        o._mousePx = new Point(5, 5);
        o._sel = new Int32Rect(4, 4, 40, 40);
        o.ContentRendered += (_, _) => o.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => o.CloseOverlay("warm-up"));
        o.Show();
        o.UpdateVisuals();
        // Never keep a warm-up window around, whatever happens.
        CaptureController.AfterDelay(3000, () => o.CloseOverlay(), "warm-up close");
    }

    private void OnFirstFrame(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstFrame;
        // ContentRendered fires once the first frame was submitted; give the compositor one more frame.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!_shown && !_closing) Reveal(); });
    }

    private void Reveal()
    {
        _shown = true;
        var hwnd = WindowHelper.GetHandle(this);
        CaptureNative.SetCloaked(hwnd, false);
        Activate();
        CaptureNative.ForceForeground(hwnd);
        _root.Focus();
        Keyboard.Focus(_root);
        Log.Info($"Capture overlay visible after {_clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A Chinese/Japanese IME turns single-letter shortcuts (C, R, Q, Shift+1…) into a composition inside WPF's
    /// keyboard pre-processing. Take IME-sensitive keys for the overlay window here, before WPF and the text
    /// services see them, and raise a normal KeyDown instead. Text boxes keep the IME.
    /// </summary>
    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled || _closing || msg.hwnd != _hwnd) return;
        if (msg.message is not (CaptureNative.WM_KEYDOWN or CaptureNative.WM_SYSKEYDOWN)) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || _surface.IsEditingText) return;
        // Keys the IME itself injects right after one we took (e.g. the caret "Left" after an auto-paired 《》)
        // carry no scan code: drop them.
        if (CaptureNative.ScanCode(msg.lParam) == 0 && Environment.TickCount64 - _imeKeyTick < 200)
        {
            handled = true;
            return;
        }
        int vk = CaptureNative.RealVirtualKey(msg.wParam, msg.lParam);
        if (!CaptureNative.IsImeSensitive(vk) || PresentationSource.FromVisual(this) is not { } source) return;
        handled = true;
        if (CaptureNative.IsImeProcessKey(msg.wParam)) _imeKeyTick = Environment.TickCount64;
        var target = Keyboard.FocusedElement as UIElement ?? _root;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, KeyInterop.KeyFromVirtualKey(vk))
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
        Guard(() => target.RaiseEvent(args));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CaptureNative.WM_DPICHANGED)
        {
            // The overlay spans several monitors; WPF would resize it to the "suggested" rectangle. Keep WPF's DPI
            // (our pixel layer is computed from WPF's own DPI, so the 1:1 mapping stays exact) and our bounds.
            handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                WindowHelper.SetBoundsPx(this, _snap.VirtualBoundsPx, topmost: true);
                UpdateDpi();
                MarkDirty();
            });
        }
        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateDpi();
        MarkDirty();
    }

    private void UpdateDpi()
    {
        // WPF's own notion of the window DPI defines the DIP→device mapping we must invert.
        var wpf = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var real = WindowHelper.GetDpiScale(this);
        _dpi = wpf > 0 ? wpf : real;
        _pixelScale.ScaleX = _pixelScale.ScaleY = 1.0 / _dpi;
        if (Math.Abs(wpf - real) > 0.001) Log.Info($"Capture overlay: WPF dpi {wpf:0.##}, window dpi {real:0.##}");
    }

    /// <summary>Close the overlay (idempotent).</summary>
    public void CloseOverlay([System.Runtime.CompilerServices.CallerMemberName] string reason = "")
    {
        if (_closing) return;
        _closing = true;
        if (!_warmup) Log.Info($"Capture overlay closing ({reason}) after {_clock.ElapsedMilliseconds} ms");
        try { Close(); }
        catch (Exception ex) { Log.Error("Closing capture overlay failed", ex); }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closing && !_warmup) Log.Info("Capture overlay closed externally");
        _closing = true;
        try
        {
            if (ReferenceEquals(AppServices.ActiveSession, this)) AppServices.ActiveSession = null;
            Mouse.OverrideCursor = null;
            RestoreImeState();
            ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
            if (_renderHooked) { CompositionTarget.Rendering -= OnRendering; _renderHooked = false; }
            _detector?.Dispose();
            _toast.Stop();
            _magnifier?.SetSource(null);
            _image.Source = null;
            _pixel.Children.Clear();
            _ui.Children.Clear();
            Content = null;
            _historyComposite = null;
            _historyPatch = null;
            if (_settingsDirty) AppServices.Settings?.Save();
            if (!_warmup && _previousForeground != IntPtr.Zero && CaptureNative.IsWindow(_previousForeground))
                CaptureNative.SetForegroundWindow(_previousForeground);
        }
        catch (Exception ex) { Log.Error("Capture overlay cleanup failed", ex); }
        finally
        {
            _snap.Dispose();
            base.OnClosed(e);
            if (!_warmup) _owner.OnOverlayClosed(this);
        }
    }

    // ================================================================================== IME

    private void SyncImeState(IInputElement? focus)
    {
        try
        {
            var im = InputMethod.Current;
            if (focus is System.Windows.Controls.Primitives.TextBoxBase tb)
            {
                InputMethod.SetIsInputMethodEnabled(tb, true);
                RestoreImeState();
            }
            else if (_savedImeState == null)
            {
                _savedImeState = im.ImeState;
                im.ImeState = InputMethodState.Off;
            }
        }
        catch (Exception ex) { Log.Warn($"IME state change failed: {ex.Message}"); }
    }

    private void RestoreImeState()
    {
        if (_savedImeState is not { } s) return;
        _savedImeState = null;
        try { InputMethod.Current.ImeState = s; }
        catch (Exception ex) { Log.Warn($"IME state restore failed: {ex.Message}"); }
    }

    // ================================================================================== helpers

    private void Guard(Action action)
    {
        if (_closing) return;
        try { action(); }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        Log.Error("Capture overlay error", ex);
        Notify.Error(Loc.Format("Capture.Failed", ex.Message));
        CloseOverlay();
    }

    private static Brush AccentBrush()
    {
        // Effective accent (configured or Windows accent), maintained by Core/Theme/ThemeManager.
        if (Application.Current?.TryFindResource("Brush.Accent") is Brush themed) return themed;
        var text = AppServices.Settings?.Current.General.AccentColor;
        if (ColorText.TryParse(text, out var c))
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        return Application.Current?.TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue;
    }

    private static DetectionTargets BuildTargets(ScreenSnapshot snap, IReadOnlyList<Int32Rect> monitorsImg, Int32Rect bounds)
    {
        var list = new List<DetectTarget>();
        foreach (var w in snap.Windows)
        {
            try
            {
                if (DetectionTargets.IsIgnoredClass(w.ClassName)) continue;
                if (!DetectionTargets.IsDesktopClass(w.ClassName) && CaptureNative.IsInvisibleOverlay(w.Handle)) continue;
                var r = SelectionMath.Clamp(snap.ScreenToImage(w.BoundsPx), bounds);
                if (SelectionMath.IsEmpty(r)) continue;
                list.Add(new DetectTarget(r, w));
            }
            catch (Exception ex) { Log.Warn($"Skipping window {w}: {ex.Message}"); }
        }
        return new DetectionTargets(list, monitorsImg);
    }

    private Point PosPx(MouseEventArgs e) => e.GetPosition(_pixel);

    private static int Floor(double v) => (int)Math.Floor(v);

    private int MonitorIndexAt(Point px) => Math.Max(0, Placement.MonitorAt(_monitorRects, px));

    private int MonitorIndexFor(Int32Rect r) =>
        Math.Max(0, Placement.PickMonitor(_monitorRects, new Rect(r.X, r.Y, r.Width, r.Height)));

    private double MonitorScale(int index) => index >= 0 && index < _monitorScales.Count ? _monitorScales[index] : _dpi;

    /// <summary>Is the event source inside the unscaled UI layer (toolbar, flyouts…)?</summary>
    private bool IsFromUi(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _ui)) return true;
            if (ReferenceEquals(d, _root)) return false;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ================================================================================== IModalSession

    public bool TryHandleAction(string actionId)
    {
        if (_closing) return false;
        switch (actionId)
        {
            case Core.Actions.ActionIds.CapturePin:
            case Core.Actions.ActionIds.PinClipboard:
                Guard(() =>
                {
                    if (!HasSel && _hover is { } h) _sel = h;
                    if (HasSel) DoPin();
                });
                return true;
            default:
                // Pressing any capture hotkey again while the overlay is open is ignored.
                return actionId.StartsWith("capture", StringComparison.Ordinal);
        }
    }

    // ================================================================================== display image

    private BitmapSource? _historyComposite;
    private (BitmapSource Image, int X, int Y)? _historyPatch;

    private BitmapSource LiveImage => _showCursor ? _snap.GetImageWithCursor() : _snap.Image;

    /// <summary>The image shown and annotated: snapshot (± cursor), plus a history screenshot when browsing.</summary>
    private BitmapSource DisplayImage => _historyComposite ?? LiveImage;

    private void RefreshDisplayImage()
    {
        _historyComposite = null;
        if (_historyPatch is { } p)
        {
            var live = LiveImage;
            var dest = BitmapUtil.GetPixels(live);
            var patch = BitmapUtil.GetPixels(p.Image);
            ImageEffects.BlendOnto(dest, live.PixelWidth, live.PixelHeight, patch, p.Image.PixelWidth, p.Image.PixelHeight, p.X, p.Y);
            _historyComposite = BitmapUtil.FromPixels(dest, live.PixelWidth, live.PixelHeight);
        }
        var img = DisplayImage;
        _image.Source = img;
        _surface.BaseImage = img;
        _magnifier?.SetSource(img);
        MarkDirty();
    }
}
