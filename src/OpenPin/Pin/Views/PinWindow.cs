using System.IO;
using System.Runtime.InteropServices;
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
using OpenPin.Pin.Logic;
using OpenPin.Pin.Model;
using OpenPin.Pin.Native;
using OpenPin.Services;

namespace OpenPin.Pin.Views;

/// <summary>
/// One floating pin. Geometry (all physical px):
/// <code>
///  window = visible rect inflated by the shadow margin
///  visible rect = content origin + viewport × scale        (viewport = whole content unless thumbnail mode)
///  scale = BaseScale × Zoom                                 (images: BaseScale 1 → 100% = 1 image px per physical px)
/// </code>
/// The window content is a "pixel layer" with <c>ScaleTransform(1/dpi)</c>, so one layer unit is one physical pixel on
/// any monitor; the window is positioned with <see cref="WindowHelper.SetBoundsPx"/> and WM_DPICHANGED is rewritten so a
/// pin keeps its physical size when it moves to a monitor with another scale (PIN-15).
/// </summary>
internal sealed class PinWindow : Window
{
    private enum DragMode { None, Move, Resize, ViewportResize, Pan, Select, DragOutPending, CropPending, Crop }

    private readonly PinManager _mgr;
    private PinBody _body;
    private readonly int _margin;
    private double _dpi = 1;

    // visual tree (pixel layer → glow + clip(visible area) → content(scaled) ; badges)
    private readonly Canvas _px = new();
    private readonly ScaleTransform _pxScale = new(1, 1);
    private readonly GlowFrame _glow = new();
    private readonly Canvas _clip = new() { ClipToBounds = true };
    private readonly Grid _content = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScaleTransform _contentScale = new(1, 1);
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _cropRect = new() { Visibility = Visibility.Collapsed, StrokeThickness = 1 };
    private readonly Grid _badges = new() { IsHitTestVisible = false };
    private readonly ScaleTransform _badgeScale = new(1, 1);
    private readonly Border _zoomBadge = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _zoomText = new() { FontSize = 13 };
    private readonly Border _titleBadge = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _titleText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    // interaction
    private DragMode _drag;
    private Point _dragStartCursor;
    private Point _dragStartOrigin;
    private Int32Rect _dragStartVisible;
    private double _dragStartZoom;
    private Rect _dragStartViewport;
    private PinEdges _dragEdges;
    private Point _rightStartContent;
    private bool _moved;
    private (double Zoom, double Opacity, Point Origin)? _resetMemo;
    private DispatcherTimer? _shakeTimer;

    // annotation
    private AnnotationSurface? _surface;
    private PinToolbarWindow? _toolbarWindow;

    // OCR text selection
    private IReadOnlyList<OcrWordRef> _words = Array.Empty<OcrWordRef>();
    private OcrResult? _ocr;
    private int _selA = -1, _selB = -1;
    private int _imageVersion;
    private CancellationTokenSource? _ocrCts;

    // Alt magnifier
    private PinMagnifier? _magnifier;
    private byte[]? _magPixels;
    private bool _magAltFormat;

    public PinWindow(PinManager mgr, PinManifest manifest, PinBody body)
    {
        _mgr = mgr;
        M = manifest;
        _body = body;
        _margin = Math.Clamp(mgr.Settings.ShadowSizePx, 2, 30);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = false;
        AllowDrop = true;
        Focusable = true;
        Topmost = M.Topmost;
        Title = string.IsNullOrWhiteSpace(M.Title) ? Loc.Get("Pin.WindowTitle") : M.Title;
        Left = -32000; Top = -32000; Width = 1; Height = 1; // real bounds are set in physical px after the handle exists

        BuildVisuals();
        AttachBody(body);

        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); _zoomBadge.Visibility = Visibility.Collapsed; };
        SourceInitialized += OnSourceInitialized;
        Activated += (_, _) => UpdateGlow();
        Deactivated += (_, _) => { UpdateGlow(); HideMagnifier(); };
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>The persisted state (kept up to date).</summary>
    public PinManifest M { get; }

    public PinBody Body => _body;

    public bool IsAnnotating => _surface != null;

    public double Scale => M.BaseScale * M.Zoom;

    private Rect? Viewport
    {
        get => M.Viewport is { Length: 4 } v ? new Rect(v[0], v[1], v[2], v[3]) : null;
        set => M.Viewport = value is { } r ? new[] { r.X, r.Y, r.Width, r.Height } : null;
    }

    public Int32Rect VisibleRectPx => PinGeometry.VisibleRect(M.OriginX, M.OriginY, _body.ContentSize, Viewport, Scale);

    public Int32Rect WindowRectPx => PinGeometry.Inflate(VisibleRectPx, _margin);

    // ================================================================================ setup

    private void BuildVisuals()
    {
        _px.LayoutTransform = _pxScale;
        _px.Children.Add(_glow);

        // a nearly transparent fill keeps the whole content area clickable even over transparent image pixels
        _clip.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Canvas.SetLeft(_clip, _margin);
        Canvas.SetTop(_clip, _margin);
        _px.Children.Add(_clip);

        _content.RenderTransform = _contentScale;
        _clip.Children.Add(_content);

        _cropRect.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _overlay.Children.Add(_cropRect);

        // badges (zoom %, title) live over the visible area and are scaled back to DIP size
        Canvas.SetLeft(_badges, _margin);
        Canvas.SetTop(_badges, _margin);
        _px.Children.Add(_badges);
        _zoomBadge.LayoutTransform = _badgeScale;
        _zoomBadge.Child = _zoomText;
        _zoomBadge.SetResourceReference(StyleProperty, "Style.Pin.Badge");
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Pin.BadgeText");
        _badges.Children.Add(_zoomBadge);
        _titleBadge.LayoutTransform = _badgeScale;
        _titleBadge.Child = _titleText;
        _titleBadge.SetResourceReference(StyleProperty, "Style.Pin.TitleBar");
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Pin.BadgeText");
        _badges.Children.Add(_titleBadge);

        Content = _px;
    }

    private void AttachBody(PinBody body)
    {
        _content.Children.Clear();
        _body = body;
        _content.Children.Add(body.View);
        _content.Children.Add(_overlay);
        body.Changed += OnBodyChanged;
        UpdateContentSize();
    }

    private void UpdateContentSize()
    {
        var s = _body.ContentSize;
        _content.Width = s.Width;
        _content.Height = s.Height;
        _overlay.Width = s.Width;
        _overlay.Height = s.Height;
    }

    private void OnBodyChanged(object? sender, EventArgs e)
    {
        UpdateContentSize();
        if (Viewport is { } v) Viewport = PinGeometry.ClampViewport(v, _body.ContentSize);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = WindowHelper.GetHandle(this);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        WindowHelper.SetToolWindow(this, true);
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (M.ClickThrough) WindowHelper.SetClickThrough(this, true);
    }

    /// <summary>Creates the window at its exact physical bounds and shows it.</summary>
    public void ShowPin(bool activate)
    {
        ShowActivated = activate;
        WindowHelper.GetHandle(this);
        ApplyLayout();
        Show();
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ApplyLayout();
        UpdateTitle();
        if (activate) WindowHelper.ForceForeground(this);
        UpdateGlow();
    }

    // ================================================================================ layout

    /// <summary>Recomputes every size/position from <see cref="M"/> and moves the window.</summary>
    public void ApplyLayout()
    {
        var size = _body.ContentSize;
        M.Zoom = PinGeometry.ClampZoom(M.Zoom, size.Width, size.Height, M.BaseScale);
        var vis = VisibleRectPx;
        var win = PinGeometry.Inflate(vis, _margin);
        double scale = Scale;
        var vp = Viewport ?? new Rect(0, 0, size.Width, size.Height);

        _pxScale.ScaleX = _pxScale.ScaleY = 1 / _dpi;
        _badgeScale.ScaleX = _badgeScale.ScaleY = _dpi;
        _px.Width = win.Width;
        _px.Height = win.Height;
        _glow.Width = win.Width;
        _glow.Height = win.Height;
        _clip.Width = vis.Width;
        _clip.Height = vis.Height;
        _badges.Width = vis.Width;
        _badges.Height = vis.Height;
        _contentScale.ScaleX = _contentScale.ScaleY = scale;
        Canvas.SetLeft(_content, -Math.Round(vp.X * scale));
        Canvas.SetTop(_content, -Math.Round(vp.Y * scale));
        if (_body is ImageBody ib) ib.SetScalingMode(_mgr.Settings.SmoothScaling, scale);
        _px.Opacity = M.Opacity;

        if (IsLoadedHandle) WindowHelper.SetBoundsPx(this, win);
        UpdateGlow();
        PositionToolbar();
    }

    private bool IsLoadedHandle => new WindowInteropHelper(this).Handle != IntPtr.Zero;

    /// <summary>Fast path for moves (size unchanged).</summary>
    private void ApplyPosition()
    {
        var win = WindowRectPx;
        WindowHelper.SetPositionPx(this, win.X, win.Y);
        PositionToolbar();
    }

    public void PositionToolbar() => _toolbarWindow?.PlaceNear(VisibleRectPx);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi.DpiScaleX;
        ApplyLayout();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case PinNative.WM_DPICHANGED:
            {
                // Keep the physical size: replace the suggested (DPI-scaled) rect with our own before WPF applies it.
                PinNative.GetWindowRect(hwnd, out var cur);
                var want = WindowRectPx;
                // adopt the actual position (the window may have been moved by the system)
                int dx = cur.Left - want.X, dy = cur.Top - want.Y;
                if (dx != 0 || dy != 0)
                {
                    M.OriginX += dx;
                    M.OriginY += dy;
                    want = WindowRectPx;
                }
                var r = new PinNative.RECT { Left = want.X, Top = want.Y, Right = want.X + want.Width, Bottom = want.Y + want.Height };
                Marshal.StructureToPtr(r, lParam, false);
                break;
            }
            case PinNative.WM_GETMINMAXINFO:
            {
                var mmi = Marshal.PtrToStructure<PinNative.MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = 1;
                mmi.ptMinTrackSize.Y = 1;
                mmi.ptMaxTrackSize.X = PinGeometry.MaxVisiblePx + 200;
                mmi.ptMaxTrackSize.Y = PinGeometry.MaxVisiblePx + 200;
                Marshal.StructureToPtr(mmi, lParam, false);
                handled = true;
                break;
            }
        }
        return IntPtr.Zero;
    }

    // ================================================================================ state visuals

    public void UpdateGlow()
    {
        bool active = IsActive || (_toolbarWindow?.IsActive ?? false);
        string key = M.ClickThrough ? "Color.PinClickThrough" : M.Locked ? "Color.PinLocked" : active ? "Color.PinActive" : "Color.PinInactive";
        Color color = TryFindResource(key) is Color c ? c : Colors.Gray;
        if (!M.ClickThrough && !M.Locked && !active && _mgr.GroupColor(M.GroupId) is { } gc) color = gc;
        var vis = VisibleRectPx;
        _glow.Set(color, active || M.Locked || M.ClickThrough, M.Shadow, _margin, new Rect(_margin, _margin, vis.Width, vis.Height));
    }

    public void UpdateTitle()
    {
        var t = M.Title;
        Title = string.IsNullOrWhiteSpace(t) ? Loc.Get("Pin.WindowTitle") : t;
        bool show = !string.IsNullOrWhiteSpace(t) && _mgr.Settings.ShowTitle;
        _titleText.Text = t ?? "";
        _titleBadge.VerticalAlignment = _mgr.Settings.TitlePosition == PinTitlePosition.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _titleBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowZoomBadge()
    {
        if (!_mgr.Settings.ShowZoomIndicator) return;
        _zoomText.Text = $"{Math.Round(M.Zoom * 100)}%";
        _zoomBadge.Visibility = Visibility.Visible;
        _zoomTimer.Stop();
        _zoomTimer.Start();
    }

    private void ShowBadgeText(string text)
    {
        _zoomText.Text = text;
        _zoomBadge.Visibility = Visibility.Visible;
        _zoomTimer.Stop();
        _zoomTimer.Start();
    }

    // ================================================================================ basic operations

    public void MoveBy(int dx, int dy)
    {
        M.OriginX += dx;
        M.OriginY += dy;
        ApplyPosition();
        _mgr.MarkDirty(this);
    }

    /// <summary>Changes the zoom keeping <paramref name="anchorPx"/> (screen px) fixed.</summary>
    public void SetZoom(double zoom, Point anchorPx)
    {
        var size = _body.ContentSize;
        zoom = PinGeometry.ClampZoom(zoom, size.Width, size.Height, M.BaseScale);
        if (Math.Abs(zoom - M.Zoom) < 1e-9) { ShowZoomBadge(); return; }
        double oldScale = Scale;
        double newScale = M.BaseScale * zoom;
        var o = PinGeometry.ZoomAround(new Point(M.OriginX, M.OriginY), oldScale, newScale, anchorPx);
        M.Zoom = zoom;
        M.OriginX = (int)Math.Round(o.X);
        M.OriginY = (int)Math.Round(o.Y);
        ApplyLayout();
        ShowZoomBadge();
        _mgr.MarkDirty(this);
    }

    private Point VisibleTopLeft => new(VisibleRectPx.X, VisibleRectPx.Y);

    private Point VisibleCenter
    {
        get
        {
            var v = VisibleRectPx;
            return new Point(v.X + v.Width / 2.0, v.Y + v.Height / 2.0);
        }
    }

    public void SetOpacity(double opacity)
    {
        M.Opacity = Math.Clamp(Math.Round(opacity, 2), 0.1, 1);
        _px.Opacity = M.Opacity;
        ShowBadgeText(Loc.Format("Pin.OpacityBadge", Math.Round(M.Opacity * 100)));
        _mgr.MarkDirty(this);
    }

    public void ToggleLock()
    {
        M.Locked = !M.Locked;
        UpdateGlow();
        ShowBadgeText(Loc.Get(M.Locked ? "Pin.LockedBadge" : "Pin.UnlockedBadge"));
        _mgr.MarkDirty(this);
    }

    public void ToggleTopmost()
    {
        M.Topmost = !M.Topmost;
        Topmost = M.Topmost;
        ShowBadgeText(Loc.Get(M.Topmost ? "Pin.TopmostOnBadge" : "Pin.TopmostOffBadge"));
        _mgr.MarkDirty(this);
    }

    public void ToggleShadow()
    {
        M.Shadow = !M.Shadow;
        UpdateGlow();
        _mgr.MarkDirty(this);
    }

    public void SetClickThrough(bool on)
    {
        if (M.ClickThrough == on) return;
        M.ClickThrough = on;
        var target = _mgr.Settings.ClickThroughOpacity / 100.0;
        if (on)
        {
            if (_mgr.Settings.ClickThroughOpacity > 0 && M.Opacity > target)
            {
                M.OpacityBeforeClickThrough = M.Opacity;
                M.Opacity = Math.Clamp(target, 0.1, 1);
            }
        }
        else if (M.OpacityBeforeClickThrough > 0)
        {
            M.Opacity = M.OpacityBeforeClickThrough;
            M.OpacityBeforeClickThrough = 0;
        }
        _px.Opacity = M.Opacity;
        WindowHelper.SetClickThrough(this, on);
        UpdateGlow();
        _mgr.MarkDirty(this);
    }

    /// <summary>Middle click: reset zoom &amp; opacity to 100%; a second middle click restores the previous values.</summary>
    public void ToggleReset()
    {
        bool isReset = Math.Abs(M.Zoom - 1) < 1e-6 && Math.Abs(M.Opacity - 1) < 1e-6;
        if (isReset && _resetMemo is { } memo)
        {
            _resetMemo = null;
            M.Zoom = memo.Zoom;
            M.Opacity = memo.Opacity;
            M.OriginX = (int)memo.Origin.X;
            M.OriginY = (int)memo.Origin.Y;
            ApplyLayout();
            ShowZoomBadge();
            _mgr.MarkDirty(this);
            return;
        }
        if (isReset) { ShowZoomBadge(); return; }
        _resetMemo = (M.Zoom, M.Opacity, new Point(M.OriginX, M.OriginY));
        M.Opacity = 1;
        _px.Opacity = 1;
        SetZoom(1, VisibleTopLeft);
        _mgr.MarkDirty(this);
    }

    public void ToggleThumbnail()
    {
        if (IsAnnotating) return;
        if (M.IsThumbnail) Viewport = null;
        else
        {
            var cur = PinNative.CursorPx();
            var vis = VisibleRectPx;
            var size = _body.ContentSize;
            Point focus = new(size.Width / 2, size.Height / 2);
            if (cur.X >= vis.X && cur.Y >= vis.Y && cur.X < vis.Right() && cur.Y < vis.Bottom())
                focus = new Point((cur.X - M.OriginX) / Scale, (cur.Y - M.OriginY) / Scale);
            Viewport = PinGeometry.ThumbnailViewport(size, Scale, Math.Max(32, _mgr.Settings.ThumbnailSizePx), focus);
        }
        ClearSelection();
        ApplyLayout();
        _mgr.MarkDirty(this);
    }

    public void SetThumbnail(bool on)
    {
        if (M.IsThumbnail != on) ToggleThumbnail();
    }

    public void BringIntoView(IReadOnlyList<Int32Rect> workAreas)
    {
        var (dx, dy) = PinGeometry.BringIntoView(VisibleRectPx, workAreas);
        if (dx != 0 || dy != 0) MoveBy(dx, dy);
    }

    public void Shake()
    {
        if (!IsVisible) return;
        _shakeTimer?.Stop();
        int[] offsets = { -10, 10, -8, 8, -5, 5, -2, 2, 0 };
        int step = 0;
        var basePos = WindowRectPx;
        _shakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _shakeTimer.Tick += (_, _) =>
        {
            if (step >= offsets.Length) { _shakeTimer!.Stop(); ApplyPosition(); return; }
            WindowHelper.SetPositionPx(this, basePos.X + offsets[step++], basePos.Y);
        };
        _shakeTimer.Start();
        WindowHelper.SetTopmost(this, true);
        if (!M.Topmost) WindowHelper.SetTopmost(this, false);
        Activate();
    }

    public void EditTitle()
    {
        var d = new InputDialog(Loc.Get("Pin.Title.DialogTitle"), Loc.Get("Pin.Title.Prompt"), M.Title ?? "");
        if (d.ShowDialog() != true) return;
        var t = d.Value.Trim();
        M.Title = t.Length == 0 ? null : t;
        UpdateTitle();
        _mgr.MarkDirty(this);
    }

    public void SetTextIgnoreFormat(bool ignore)
    {
        if (_body is not TextBody tb) return;
        var center = VisibleCenter;
        tb.SetIgnoreFormat(ignore);
        Viewport = null;
        KeepCenter(center);
        _mgr.MarkDirty(this);
    }

    private void KeepCenter(Point center)
    {
        var size = _body.ContentSize;
        M.OriginX = (int)Math.Round(center.X - size.Width * Scale / 2);
        M.OriginY = (int)Math.Round(center.Y - size.Height * Scale / 2);
        ApplyLayout();
    }

    // ================================================================================ image processing (PIN-10)

    public void Adjust(Action<ImageAdjustments> change)
    {
        if (_body is not ImageBody ib || IsAnnotating) return;
        var oldSize = ib.ContentSize;
        var center = VisibleCenter;
        change(ib.Adjust);
        ib.Recompute();
        OnImagePixelsChanged(oldSize, center);
    }

    public void Crop(Rect contentRect)
    {
        if (_body is not ImageBody ib || IsAnnotating) return;
        var r = new Int32Rect((int)Math.Floor(contentRect.X), (int)Math.Floor(contentRect.Y),
            (int)Math.Ceiling(contentRect.Width), (int)Math.Ceiling(contentRect.Height));
        r = BitmapUtil.Clamp(r, ib.Display.PixelWidth, ib.Display.PixelHeight);
        if (r.Width < 2 || r.Height < 2) return;
        var cropped = BitmapUtil.Crop(ib.Display, r);
        M.OriginX += (int)Math.Round(r.X * Scale);
        M.OriginY += (int)Math.Round(r.Y * Scale);
        Viewport = null;
        ib.Bake(cropped);
        ImageEdited();
    }

    public void RestoreOriginalImage()
    {
        if (_body is not ImageBody ib || IsAnnotating) return;
        var oldSize = ib.ContentSize;
        var center = VisibleCenter;
        ib.RestoreOriginal();
        OnImagePixelsChanged(oldSize, center);
        _mgr.OnImageEdited(this);
    }

    private void OnImagePixelsChanged(Size oldSize, Point center)
    {
        if (_body.ContentSize != oldSize)
        {
            Viewport = null;
            KeepCenter(center);
        }
        else ApplyLayout();
        InvalidateOcr();
        _mgr.MarkDirty(this);
    }

    private void ImageEdited()
    {
        ApplyLayout();
        InvalidateOcr();
        _mgr.OnImageEdited(this);
    }

    /// <summary>Replaces the content (Ctrl+V / drop, PIN-14): keeps the position, resets zoom and processing.</summary>
    public void ReplaceBody(PinBody body, double baseScale)
    {
        if (IsAnnotating) EndAnnotation(false);
        _body.Changed -= OnBodyChanged;
        M.Kind = body.Kind;
        M.BaseScale = baseScale;
        M.Zoom = 1;
        Viewport = null;
        _resetMemo = null;
        AttachBody(body);
        ApplyLayout();
        InvalidateOcr();
    }

    // ================================================================================ copy / save / drag

    /// <summary>Image of the pin: current (as shown, incl. zoom) or original pixels.</summary>
    public BitmapSource GetImage(bool original) =>
        original ? _body.RenderOriginal(M.BaseScale) : _body.RenderCurrent(Scale, _mgr.Settings.SmoothScaling);

    public void Copy(bool? original = null)
    {
        if (_body is ImageBody)
        {
            bool orig = original ?? _mgr.Settings.CopySource == PinCopySource.Original;
            ClipboardService.SetImage(GetImage(orig));
            ShowBadgeText(Loc.Get("Pin.CopiedBadge"));
        }
        else if (original == null)
        {
            _body.CopyToClipboard();
            ShowBadgeText(Loc.Get("Pin.CopiedBadge"));
        }
        else
        {
            ClipboardService.SetImage(GetImage(original.Value));
            ShowBadgeText(Loc.Get("Pin.CopiedBadge"));
        }
    }

    private SaveContext SaveContext() => new() { WindowTitle = M.Title, FromCapture = false };

    public void SaveAs()
    {
        var img = GetImage(false);
        SaveService.SaveWithDialog(img, SaveContext());
    }

    public void QuickSave() => SaveService.QuickSave(GetImage(false), SaveContext());

    private void StartDragOut()
    {
        try
        {
            var data = new DataObject();
            if (_body is ImageBody)
            {
                var img = GetImage(false);
                var name = string.IsNullOrWhiteSpace(M.Title) ? $"OpenPin_{DateTime.Now:yyyyMMdd_HHmmss_fff}" : SafeFileName(M.Title!);
                var path = System.IO.Path.Combine(AppPaths.TempDir, name + ".png");
                SaveService.SaveToFile(img, path);
                var files = new System.Collections.Specialized.StringCollection { path };
                data.SetFileDropList(files);
                data.SetImage(img);
                data.SetData("PNG", new MemoryStream(BitmapUtil.EncodePng(img)));
            }
            else _body.FillDataObject(data);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Error("Drag out failed", ex);
        }
    }

    public static string SafeFileName(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim();
        return s.Length == 0 ? "OpenPin" : s.Length > 80 ? s[..80] : s;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = _mgr.Settings.PasteBehavior != PinPasteBehavior.Disabled && !IsAnnotating &&
                  (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) ||
                   e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_mgr.Settings.PasteBehavior == PinPasteBehavior.Disabled || IsAnnotating) return;
        try
        {
            var content = PinManager.ContentFromData(e.Data);
            if (content != null) _mgr.PasteOnto(this, content);
        }
        catch (Exception ex)
        {
            Log.Error("Drop on pin failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
        e.Handled = true;
    }

    // ================================================================================ annotation (PIN-05)

    public void ToggleAnnotation()
    {
        if (IsAnnotating) EndAnnotation(true);
        else BeginAnnotation();
    }

    private void BeginAnnotation()
    {
        if (_body is not ImageBody ib || M.ClickThrough) return;
        if (M.IsThumbnail) { Viewport = null; ApplyLayout(); }
        ClearSelection();
        HideMagnifier();
        try
        {
            _surface = new AnnotationSurface(ib.Display) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, FocusVisualStyle = null };
            _content.Children.Add(_surface);
            var toolbar = new AnnotationToolbar { Surface = _surface };
            toolbar.HostActions.Add(new ToolbarAction("pin.annotate.done", "", Loc.Get("Pin.Annotate.Done"), () => EndAnnotation(true)));
            toolbar.HostActions.Add(new ToolbarAction("pin.annotate.copy", "", Loc.Get("Pin.Annotate.Copy"), () =>
            {
                ClipboardService.SetImage(_surface?.Render() ?? ib.Display);
                ShowBadgeText(Loc.Get("Pin.CopiedBadge"));
            }));
            toolbar.HostActions.Add(new ToolbarAction("pin.annotate.save", "", Loc.Get("Pin.Annotate.Save"), () =>
                SaveService.SaveWithDialog(_surface?.Render() ?? ib.Display, SaveContext())));
            toolbar.HostActions.Add(new ToolbarAction("pin.annotate.cancel", "", Loc.Get("Pin.Annotate.Cancel"), () => EndAnnotation(false)));
            _toolbarWindow = new PinToolbarWindow(toolbar, this);
            _toolbarWindow.Activated += (_, _) => UpdateGlow();
            _toolbarWindow.Deactivated += (_, _) => UpdateGlow();
            _toolbarWindow.Show();
            _toolbarWindow.UpdateLayout();
            PositionToolbar();
            Activate();
            _surface.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("Begin annotation failed", ex);
            EndAnnotation(false);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    /// <summary>Leaves annotation mode; with <paramref name="apply"/> the annotations are merged into the pin image.</summary>
    public void EndAnnotation(bool apply)
    {
        var surface = _surface;
        if (surface == null) return;
        try
        {
            if (apply && surface.HasAnnotations && _body is ImageBody ib)
            {
                surface.CommitPendingEdits();
                var merged = surface.Render();
                ib.Bake(merged);
                ImageEdited();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Applying annotations failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
        finally
        {
            _content.Children.Remove(surface);
            _surface = null;
            var tw = _toolbarWindow;
            _toolbarWindow = null;
            tw?.Close();
            UpdateGlow();
            if (IsVisible) Activate();
        }
    }

    // ================================================================================ OCR (PIN-08)

    public void InvalidateOcr()
    {
        _imageVersion++;
        _ocrCts?.Cancel();
        _ocr = null;
        _words = Array.Empty<OcrWordRef>();
        ClearSelection();
        _magPixels = null;
        if (_mgr.Settings.TextSelectableByDefault) ScheduleOcr();
    }

    public async void ScheduleOcr()
    {
        if (_body is not ImageBody ib || !OcrService.IsAvailable) return;
        _ocrCts?.Cancel();
        var cts = _ocrCts = new CancellationTokenSource();
        int version = _imageVersion;
        var image = ib.Display;
        try
        {
            await Task.Delay(400, cts.Token);
            var result = await OcrService.RecognizeAsync(image, null, cts.Token);
            if (cts.IsCancellationRequested || version != _imageVersion) return;
            _ocr = result;
            _words = OcrTextSelection.Flatten(result);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex) { Log.Error("Background OCR failed", ex); }
    }

    public async void OcrCopyAll()
    {
        if (_body is not ImageBody ib)
        {
            _body.CopyToClipboard();
            return;
        }
        if (!OcrService.IsAvailable)
        {
            Notify.Error(Loc.Get("Pin.Ocr.Unavailable"));
            return;
        }
        try
        {
            int version = _imageVersion;
            var result = _ocr ?? await OcrService.RecognizeAsync(ib.Display);
            if (version == _imageVersion && _ocr == null)
            {
                _ocr = result;
                _words = OcrTextSelection.Flatten(result);
            }
            var text = !string.IsNullOrWhiteSpace(result.Text) ? result.Text : string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
            if (string.IsNullOrWhiteSpace(text))
            {
                Notify.Info(Loc.Get("Pin.Ocr.NoText"));
                return;
            }
            ClipboardService.SetText(text);
            Notify.Info(Loc.Get("Pin.Ocr.Copied"));
        }
        catch (Exception ex)
        {
            Log.Error("OCR copy failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    private string SelectedText => _selA >= 0 && _selB >= 0 ? OcrTextSelection.Join(_words, _selA, _selB) : "";

    private void ClearSelection()
    {
        _selA = _selB = -1;
        RenderSelection();
    }

    private void RenderSelection()
    {
        for (int i = _overlay.Children.Count - 1; i >= 0; i--)
            if (_overlay.Children[i] is Rectangle { Tag: "sel" }) _overlay.Children.RemoveAt(i);
        if (_selA < 0 || _selB < 0) return;
        int from = Math.Min(_selA, _selB), to = Math.Min(Math.Max(_selA, _selB), _words.Count - 1);
        for (int i = from; i <= to; i++)
        {
            var b = _words[i].Bounds;
            var r = new Rectangle { Width = b.Width + 2, Height = b.Height + 2, Tag = "sel" };
            r.SetResourceReference(Shape.FillProperty, "Brush.Pin.Selection");
            Canvas.SetLeft(r, b.X - 1);
            Canvas.SetTop(r, b.Y - 1);
            _overlay.Children.Add(r);
        }
    }

    // ================================================================================ magnifier (PIN-16)

    private void UpdateMagnifier()
    {
        if (_body is not ImageBody ib || !PinNative.IsKeyDown(PinNative.VK_MENU) || IsAnnotating)
        {
            HideMagnifier();
            return;
        }
        var cur = PinNative.CursorPx();
        int x = (int)Math.Floor((cur.X - M.OriginX) / Scale), y = (int)Math.Floor((cur.Y - M.OriginY) / Scale);
        var img = ib.Display;
        _magPixels ??= BitmapUtil.GetPixels(img);
        _magnifier ??= new PinMagnifier();
        var fmt = _magAltFormat ? _mgr.Settings.MagnifierAltFormat : ColorFormat.Rgb;
        _magnifier.Update(_magPixels, img.PixelWidth, img.PixelHeight, x, y, fmt, _mgr.Settings.MagnifierZoom, cur);
    }

    private void HideMagnifier()
    {
        if (_magnifier == null) return;
        _magnifier.Close();
        _magnifier = null;
        _magPixels = null;
    }

    // ================================================================================ mouse

    private bool FromActiveSurface(RoutedEventArgs e)
    {
        if (_surface == null || _surface.Tool == AnnotationTool.None) return false;
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, _surface)) return true;
        return false;
    }

    private int Grip => Math.Max(6, (int)Math.Round(5 * _dpi));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || FromActiveSurface(e)) return;
        var cur = PinNative.CursorPx();
        var cp = e.GetPosition(_content);
        var mods = Keyboard.Modifiers;

        if (e.ClickCount == 2)
        {
            EndDrag();
            if (mods.HasFlag(ModifierKeys.Shift)) ToggleThumbnail();
            else if (!_body.HandleDoubleClick(cp) && !IsAnnotating) _mgr.ClosePin(this, PinCloseReason.DoubleClick);
            e.Handled = true;
            return;
        }

        _dragStartCursor = cur;
        _dragStartOrigin = new Point(M.OriginX, M.OriginY);
        _dragStartVisible = VisibleRectPx;
        _dragStartZoom = M.Zoom;
        var size = _body.ContentSize;
        _dragStartViewport = Viewport ?? new Rect(0, 0, size.Width, size.Height);
        _moved = false;

        var edges = _mgr.Settings.ResizeByBorder && !M.Locked && !IsAnnotating ? PinGeometry.HitEdges(_dragStartVisible, cur, Grip) : PinEdges.None;
        int word = _words.Count > 0 && !mods.HasFlag(ModifierKeys.Alt) ? OcrTextSelection.HitTest(_words, cp) : -1;

        if (mods.HasFlag(ModifierKeys.Control)) _drag = DragMode.DragOutPending;
        else if (edges != PinEdges.None)
        {
            _dragEdges = edges;
            _drag = M.IsThumbnail ? DragMode.ViewportResize : DragMode.Resize;
        }
        else if (M.IsThumbnail && mods.HasFlag(ModifierKeys.Shift)) _drag = DragMode.Pan;
        else if (word >= 0 && !IsAnnotating)
        {
            _drag = DragMode.Select;
            _selA = _selB = word;
            RenderSelection();
        }
        else if (!M.Locked) _drag = DragMode.Move;
        else _drag = DragMode.None;

        if (_drag != DragMode.Select && _selA >= 0) ClearSelection();
        if (_drag != DragMode.None) CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_magnifier != null || (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && IsActive)) UpdateMagnifier();
        if (_drag == DragMode.None)
        {
            UpdateCursor(e);
            return;
        }
        var cur = PinNative.CursorPx();
        double dx = cur.X - _dragStartCursor.X, dy = cur.Y - _dragStartCursor.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 0) _moved = true;
        var size = _body.ContentSize;
        switch (_drag)
        {
            case DragMode.Move:
                M.OriginX = (int)(_dragStartOrigin.X + dx);
                M.OriginY = (int)(_dragStartOrigin.Y + dy);
                ApplyPosition();
                break;

            case DragMode.Resize:
            {
                var (ratio, anchor) = PinGeometry.EdgeResize(_dragStartVisible, _dragEdges, dx, dy);
                double zoom = PinGeometry.ClampZoom(_dragStartZoom * ratio, size.Width, size.Height, M.BaseScale);
                var o = PinGeometry.ZoomAround(_dragStartOrigin, M.BaseScale * _dragStartZoom, M.BaseScale * zoom, anchor);
                M.Zoom = zoom;
                M.OriginX = (int)Math.Round(o.X);
                M.OriginY = (int)Math.Round(o.Y);
                ApplyLayout();
                ShowZoomBadge();
                break;
            }

            case DragMode.ViewportResize:
            {
                double s = Scale;
                Viewport = PinGeometry.ResizeViewport(_dragStartViewport, _dragEdges, dx / s, dy / s, size, Math.Min(16 / s, Math.Min(size.Width, size.Height)));
                ApplyLayout();
                break;
            }

            case DragMode.Pan:
            {
                double s = Scale;
                var v = PinGeometry.ClampViewport(new Rect(_dragStartViewport.X - dx / s, _dragStartViewport.Y - dy / s, _dragStartViewport.Width, _dragStartViewport.Height), size);
                Viewport = v;
                // keep the visible rect where it is on screen
                M.OriginX = (int)Math.Round(_dragStartVisible.X - v.X * s);
                M.OriginY = (int)Math.Round(_dragStartVisible.Y - v.Y * s);
                ApplyLayout();
                break;
            }

            case DragMode.Select:
                _selB = OcrTextSelection.Nearest(_words, e.GetPosition(_content));
                RenderSelection();
                break;

            case DragMode.DragOutPending:
                if (Math.Abs(dx) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(dy) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    EndDrag();
                    StartDragOut();
                }
                break;

            case DragMode.CropPending:
                if (Math.Abs(dx) + Math.Abs(dy) > 4 && _body is ImageBody && !IsAnnotating)
                {
                    _drag = DragMode.Crop;
                    _cropRect.Visibility = Visibility.Visible;
                    goto case DragMode.Crop;
                }
                break;

            case DragMode.Crop:
            {
                var p = e.GetPosition(_content);
                var r = new Rect(Clamp(_rightStartContent, size), Clamp(p, size));
                _cropRect.Width = r.Width;
                _cropRect.Height = r.Height;
                _cropRect.StrokeThickness = 1 / Scale * Math.Max(1, _dpi);
                Canvas.SetLeft(_cropRect, r.X);
                Canvas.SetTop(_cropRect, r.Y);
                break;
            }
        }
    }

    private static Point Clamp(Point p, Size s) => new(Math.Clamp(p.X, 0, s.Width), Math.Clamp(p.Y, 0, s.Height));

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is DragMode.CropPending or DragMode.Crop) return;
        var was = _drag;
        EndDrag();
        if (_moved && was is DragMode.Move or DragMode.Resize or DragMode.ViewportResize or DragMode.Pan) _mgr.MarkDirty(this);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (e.Handled || FromActiveSurface(e)) return;
        if (_drag != DragMode.None) EndDrag();
        _dragStartCursor = PinNative.CursorPx();
        _rightStartContent = e.GetPosition(_content);
        _drag = DragMode.CropPending;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (FromActiveSurface(e) && _drag == DragMode.None) return;
        var was = _drag;
        var end = e.GetPosition(_content);
        EndDrag();
        e.Handled = true;
        if (was == DragMode.Crop)
        {
            var size = _body.ContentSize;
            Crop(new Rect(Clamp(_rightStartContent, size), Clamp(end, size)));
        }
        else if (was == DragMode.CropPending || was == DragMode.None) OpenContextMenu(end);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && !FromActiveSurface(e))
        {
            ToggleReset();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is DragMode.Move or DragMode.Resize or DragMode.ViewportResize or DragMode.Pan && _moved) _mgr.MarkDirty(this);
        if (_drag != DragMode.None && _drag != DragMode.Select) ResetDragState();
        else if (_drag == DragMode.Select) _drag = DragMode.None;
    }

    private void EndDrag()
    {
        ResetDragState();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void ResetDragState()
    {
        _drag = DragMode.None;
        _cropRect.Visibility = Visibility.Collapsed;
    }

    private void UpdateCursor(MouseEventArgs e)
    {
        if (_surface != null && _surface.Tool != AnnotationTool.None) { Cursor = null; return; }
        var cur = PinNative.CursorPx();
        var edges = _mgr.Settings.ResizeByBorder && !M.Locked && !IsAnnotating ? PinGeometry.HitEdges(VisibleRectPx, cur, Grip) : PinEdges.None;
        Cursor = edges switch
        {
            PinEdges.Left or PinEdges.Right => Cursors.SizeWE,
            PinEdges.Top or PinEdges.Bottom => Cursors.SizeNS,
            PinEdges.Left | PinEdges.Top or PinEdges.Right | PinEdges.Bottom => Cursors.SizeNWSE,
            PinEdges.Right | PinEdges.Top or PinEdges.Left | PinEdges.Bottom => Cursors.SizeNESW,
            _ => _words.Count > 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && OcrTextSelection.HitTest(_words, e.GetPosition(_content)) >= 0
                ? Cursors.IBeam
                : M.Locked ? Cursors.Arrow : Cursors.SizeAll,
        };
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        e.Handled = true;
        int dir = Math.Sign(e.Delta);
        if (dir == 0) return;
        var s = _mgr.Settings;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetOpacity(M.Opacity + dir * Math.Max(1, s.OpacityStepPercent) / 100.0);
            return;
        }
        if (s.WheelZoom == WheelZoomAnchor.Disabled || M.Locked) return;
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Math.Max(1, s.FineZoomStepPercent) : Math.Max(1, s.ZoomStepPercent);
        var anchor = PinGeometry.AnchorPoint(s.WheelZoom, VisibleRectPx, PinNative.CursorPx());
        SetZoom(PinGeometry.StepZoom(M.Zoom, step, dir), anchor);
    }

    // ================================================================================ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled) HandleKey(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt) { HideMagnifier(); e.Handled = true; }
    }

    /// <summary>Pin keyboard shortcuts (also receives keys forwarded from the annotation toolbar window).</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (_surface != null && _surface.HandleKeyDown(e)) { e.Handled = true; return; }
        bool busy = _surface?.IsBusy ?? false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        e.Handled = true;

        // Alt magnifier
        if (key is Key.LeftAlt or Key.RightAlt) { UpdateMagnifier(); return; }
        if (_magnifier != null && alt)
        {
            if (key == Key.C) { ClipboardService.SetText(ColorText.FormatCss(_magnifier.CurrentColor, _magAltFormat ? _mgr.Settings.MagnifierAltFormat : ColorFormat.Rgb)); ShowBadgeText(Loc.Get("Pin.CopiedBadge")); return; }
            if (key is Key.LeftShift or Key.RightShift) { _magAltFormat = !_magAltFormat; UpdateMagnifier(); return; }
        }

        if (IsAnnotating)
        {
            switch (key)
            {
                case Key.Escape when !busy:
                case Key.Space when !busy && !ctrl:
                case Key.Enter when !busy:
                    EndAnnotation(true);
                    return;
                case Key.C when ctrl && !busy:
                    ClipboardService.SetImage(_surface!.Render());
                    ShowBadgeText(Loc.Get("Pin.CopiedBadge"));
                    return;
                case Key.S when ctrl && !busy:
                    if (shift) SaveService.QuickSave(_surface!.Render(), SaveContext());
                    else SaveService.SaveWithDialog(_surface!.Render(), SaveContext());
                    return;
                case Key.Left or Key.Right or Key.Up or Key.Down when !busy && !M.Locked:
                    ArrowMove(key, shift);
                    return;
            }
            e.Handled = false;
            return;
        }

        switch (key)
        {
            case Key.Escape:
                if (_drag != DragMode.None) { EndDrag(); return; }
                if (_selA >= 0) { ClearSelection(); return; }
                _mgr.ClosePin(this, PinCloseReason.Esc);
                return;
            case Key.W when ctrl: _mgr.ClosePin(this, PinCloseReason.Command); return;
            case Key.D when ctrl: _mgr.DestroyPin(this); return;
            case Key.C when ctrl && shift:
                // the image Ctrl+C does not copy (original ⇄ current); non-image pins: copy as image
                Copy(_body is ImageBody ? _mgr.Settings.CopySource == PinCopySource.Current : true);
                return;
            case Key.C when ctrl:
                if (SelectedText is { Length: > 0 } sel) { ClipboardService.SetText(sel); ShowBadgeText(Loc.Get("Pin.CopiedBadge")); }
                else Copy();
                return;
            case Key.C when shift: OcrCopyAll(); return;
            case Key.S when ctrl && shift: QuickSave(); return;
            case Key.S when ctrl: SaveAs(); return;
            case Key.V when ctrl: _mgr.PasteFromClipboardOnto(this); return;
            case Key.L when !ctrl: ToggleLock(); return;
            case Key.T when !ctrl: ToggleTopmost(); return;
            case Key.R when !ctrl: ToggleThumbnail(); return;
            case Key.F2: EditTitle(); return;
            case Key.Space when !ctrl: ToggleAnnotation(); return;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                if (!M.Locked) ArrowMove(key, shift);
                return;
            case Key.OemPlus or Key.Add when !M.Locked:
                SetZoom(PinGeometry.StepZoom(M.Zoom, _mgr.Settings.ZoomStepPercent, 1), VisibleCenter); return;
            case Key.OemMinus or Key.Subtract when !M.Locked:
                SetZoom(PinGeometry.StepZoom(M.Zoom, _mgr.Settings.ZoomStepPercent, -1), VisibleCenter); return;
            case Key.OemCloseBrackets: SetOpacity(M.Opacity + _mgr.Settings.OpacityStepPercent / 100.0); return;
            case Key.OemOpenBrackets: SetOpacity(M.Opacity - _mgr.Settings.OpacityStepPercent / 100.0); return;
        }

        if (!ctrl && !alt && _body is ImageBody)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1: Adjust(a => a.RotateRight()); return;
                case Key.D2 or Key.NumPad2: Adjust(a => a.RotateLeft()); return;
                case Key.D3 or Key.NumPad3: Adjust(a => a.FlipHorizontal()); return;
                case Key.D4 or Key.NumPad4: Adjust(a => a.FlipVertical()); return;
                case Key.D5 or Key.NumPad5: Adjust(a => a.Grayscale = !a.Grayscale); return;
                case Key.D6 or Key.NumPad6: Adjust(a => a.Invert = !a.Invert); return;
                case Key.D7 or Key.NumPad7: Adjust(a => a.ChangeBrightness(10)); return;
                case Key.D8 or Key.NumPad8: Adjust(a => a.ChangeBrightness(-10)); return;
                case Key.D0 or Key.NumPad0: Adjust(a => a.Reset()); return;
            }
        }
        e.Handled = false;
    }

    private void ArrowMove(Key key, bool big)
    {
        int d = big ? 10 : 1;
        switch (key)
        {
            case Key.Left: MoveBy(-d, 0); break;
            case Key.Right: MoveBy(d, 0); break;
            case Key.Up: MoveBy(0, -d); break;
            case Key.Down: MoveBy(0, d); break;
        }
    }

    // ================================================================================ context menu (PIN-04)

    private void OpenContextMenu(Point contentPos)
    {
        var cm = BuildContextMenu(contentPos);
        cm.PlacementTarget = this;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        cm.IsOpen = true;
    }

    private ContextMenu BuildContextMenu(Point contentPos)
    {
        var f = new PinMenuFactory(this);
        var cm = PinMenuFactory.NewContextMenu();
        var items = cm.Items;
        bool image = _body is ImageBody;
        var s = _mgr.Settings;

        if (SelectedText is { Length: > 0 } sel)
            items.Add(f.Item("Pin.Menu.CopySelectedText", "", "Ctrl+C", () => ClipboardService.SetText(sel)));
        if (image)
        {
            items.Add(f.Item("Pin.Menu.CopyCurrent", "", s.CopySource == PinCopySource.Current ? "Ctrl+C" : "Ctrl+Shift+C", () => Copy(false)));
            items.Add(f.Item("Pin.Menu.CopyOriginal", null, s.CopySource == PinCopySource.Original ? "Ctrl+C" : "Ctrl+Shift+C", () => Copy(true)));
        }
        else
        {
            foreach (var extra in _body.MenuItems(contentPos, f)) items.Add(extra);
            items.Add(f.Item("Pin.Menu.CopyAsImage", null, "Ctrl+Shift+C", () => Copy(true)));
        }
        items.Add(f.Item("Pin.Menu.SaveAs", "", "Ctrl+S", SaveAs));
        items.Add(f.Item("Pin.Menu.QuickSave", "", "Ctrl+Shift+S", QuickSave));
        items.Add(PinMenuFactory.Sep());

        if (image)
        {
            items.Add(f.Item("Pin.Menu.Annotate", "", "Space", ToggleAnnotation, !M.ClickThrough));
            items.Add(f.Item("Pin.Menu.OcrCopy", "", "Shift+C", OcrCopyAll));
            items.Add(PinMenuFactory.Sep());
        }

        items.Add(f.Check("Pin.Menu.Lock", M.Locked, ToggleLock, "L"));
        items.Add(f.Check("Pin.Menu.Topmost", M.Topmost, ToggleTopmost, "T"));
        items.Add(f.Check("Pin.Menu.ClickThrough", M.ClickThrough, () => SetClickThrough(!M.ClickThrough)));
        items.Add(f.Check("Pin.Menu.Shadow", M.Shadow, ToggleShadow));

        var opacity = f.Sub("Pin.Menu.Opacity", "");
        foreach (var p in new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 })
        {
            var mi = PinMenuFactory.Make($"{p}%", null, () => SetOpacity(p / 100.0));
            mi.IsChecked = Math.Abs(M.Opacity * 100 - p) < 0.5;
            opacity.Items.Add(mi);
        }
        opacity.Items.Add(PinMenuFactory.Sep());
        opacity.Items.Add(f.Item("Pin.Menu.OpacityUp", null, "]  /  Ctrl+" + Loc.Get("Pin.Wheel"), () => SetOpacity(M.Opacity + s.OpacityStepPercent / 100.0)));
        opacity.Items.Add(f.Item("Pin.Menu.OpacityDown", null, "[", () => SetOpacity(M.Opacity - s.OpacityStepPercent / 100.0)));
        items.Add(opacity);

        var zoom = f.Sub("Pin.Menu.Zoom", "", !M.Locked);
        foreach (var p in new[] { 25, 50, 75, 100, 125, 150, 200, 300, 400 })
        {
            var mi = PinMenuFactory.Make($"{p}%", null, () => SetZoom(p / 100.0, VisibleTopLeft));
            mi.IsChecked = Math.Abs(M.Zoom * 100 - p) < 0.5;
            zoom.Items.Add(mi);
        }
        zoom.Items.Add(PinMenuFactory.Sep());
        zoom.Items.Add(f.Item("Pin.Menu.ZoomIn", null, "+  /  " + Loc.Get("Pin.Wheel"), () => SetZoom(PinGeometry.StepZoom(M.Zoom, s.ZoomStepPercent, 1), VisibleCenter)));
        zoom.Items.Add(f.Item("Pin.Menu.ZoomOut", null, "-", () => SetZoom(PinGeometry.StepZoom(M.Zoom, s.ZoomStepPercent, -1), VisibleCenter)));
        zoom.Items.Add(f.Item("Pin.Menu.ZoomReset", null, Loc.Get("Pin.MiddleClick"), ToggleReset));
        items.Add(zoom);

        if (image)
        {
            var ip = f.Sub("Pin.Menu.ImageProcessing", "", !IsAnnotating);
            ip.Items.Add(f.Item("Pin.Image.RotateRight", "", "1", () => Adjust(a => a.RotateRight())));
            ip.Items.Add(f.Item("Pin.Image.RotateLeft", "", "2", () => Adjust(a => a.RotateLeft())));
            ip.Items.Add(f.Item("Pin.Image.FlipH", "", "3", () => Adjust(a => a.FlipHorizontal())));
            ip.Items.Add(f.Item("Pin.Image.FlipV", null, "4", () => Adjust(a => a.FlipVertical())));
            var ib = (ImageBody)_body;
            ip.Items.Add(f.Check("Pin.Image.Grayscale", ib.Adjust.Grayscale, () => Adjust(a => a.Grayscale = !a.Grayscale), "5"));
            ip.Items.Add(f.Check("Pin.Image.Invert", ib.Adjust.Invert, () => Adjust(a => a.Invert = !a.Invert), "6"));
            ip.Items.Add(f.Item("Pin.Image.BrightnessUp", "", "7", () => Adjust(a => a.ChangeBrightness(10))));
            ip.Items.Add(f.Item("Pin.Image.BrightnessDown", null, "8", () => Adjust(a => a.ChangeBrightness(-10))));
            ip.Items.Add(f.Item("Pin.Image.OpacityUp", null, "]", () => SetOpacity(M.Opacity + s.OpacityStepPercent / 100.0)));
            ip.Items.Add(f.Item("Pin.Image.OpacityDown", null, "[", () => SetOpacity(M.Opacity - s.OpacityStepPercent / 100.0)));
            ip.Items.Add(f.Item("Pin.Image.Reset", "", "0", () => Adjust(a => a.Reset())));
            ip.Items.Add(PinMenuFactory.Sep());
            ip.Items.Add(f.Item("Pin.Image.RestoreOriginal", null, null, RestoreOriginalImage, ib.HasEdits));
            ip.Items.Add(f.Item("Pin.Image.CropHint", "", null, () => { }, false));
            items.Add(ip);
        }

        items.Add(f.Check("Pin.Menu.Thumbnail", M.IsThumbnail, ToggleThumbnail, "R"));
        items.Add(f.Item("Pin.Menu.SetTitle", "", "F2", EditTitle));

        var groups = f.Sub("Pin.Menu.MoveToGroup", "");
        foreach (var g in _mgr.Groups.Groups)
        {
            var id = g.Id;
            var mi = PinMenuFactory.Make(g.Name, null, () => _mgr.MovePinToGroup(this, id));
            mi.IsChecked = g.Id == M.GroupId;
            groups.Items.Add(mi);
        }
        groups.Items.Add(PinMenuFactory.Sep());
        groups.Items.Add(f.Item("Pin.Group.NewEllipsis", "", null, () => _mgr.CreateGroupAndMove(this)));
        items.Add(groups);

        items.Add(PinMenuFactory.Sep());
        items.Add(f.Item("Pin.Menu.Close", "", "Esc", () => _mgr.ClosePin(this, PinCloseReason.Command)));
        items.Add(f.Item("Pin.Menu.Destroy", "", "Ctrl+D", () => _mgr.DestroyPin(this)));
        return cm;
    }

    // ================================================================================ lifetime

    /// <summary>Set by the manager right before it closes the window; other closes (Alt+F4) are routed to the manager.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (AllowClose || PinManager.AppIsShuttingDown) return;
        e.Cancel = true;
        _mgr.OnWindowClosingExternally(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _ocrCts?.Cancel();
        _zoomTimer.Stop();
        _shakeTimer?.Stop();
        HideMagnifier();
        var tw = _toolbarWindow;
        _toolbarWindow = null;
        _surface = null;
        tw?.Close();
        _body.Changed -= OnBodyChanged;
        base.OnClosed(e);
    }
}

internal enum PinCloseReason { Esc, DoubleClick, Command }
