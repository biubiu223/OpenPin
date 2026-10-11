using System.Windows;
using System.Windows.Media;
using OpenPin.Annotation;
using OpenPin.Core;
using OpenPin.Core.Imaging;

namespace OpenPin.Capture;

internal sealed partial class CaptureOverlay
{
    // Handle order: TL, T, TR, R, BR, B, BL, L
    private static readonly (double X, double Y)[] HandleAnchors =
    {
        (0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1), (0.5, 1), (0, 1), (0, 0.5),
    };

    /// <summary>Request a visual refresh on the next rendering frame (coalesces bursts of mouse moves).</summary>
    private void MarkDirty()
    {
        _visualsDirty = true;
        if (_renderHooked || _closing) return;
        _renderHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _renderHooked = false;
        if (_closing || !_visualsDirty) return;
        _visualsDirty = false;
        Guard(UpdateVisuals);
    }

    private void UpdateVisuals()
    {
        Int32Rect? shown = HasSel ? _sel : _drag == DragKind.None ? _hover : null;
        double borderPx = Math.Max(1, Math.Round(_cfg.SelectionBorderWidth * _dpi));

        // ---- mask + border
        if (shown is { } r)
        {
            double radius = HasSel ? Math.Min(_cfg.OutputCornerRadius, Math.Min(r.Width, r.Height) / 2.0) : 0;
            _maskHole.Rect = new Rect(r.X, r.Y, r.Width, r.Height);
            _maskHole.RadiusX = _maskHole.RadiusY = radius;
            _border.StrokeThickness = borderPx;
            _border.Width = r.Width + 2 * borderPx;
            _border.Height = r.Height + 2 * borderPx;
            _border.RadiusX = _border.RadiusY = radius > 0 ? radius + borderPx : 0;
            System.Windows.Controls.Canvas.SetLeft(_border, r.X - borderPx);
            System.Windows.Controls.Canvas.SetTop(_border, r.Y - borderPx);
            _border.Visibility = Visibility.Visible;
        }
        else
        {
            _maskHole.Rect = Rect.Empty;
            _border.Visibility = Visibility.Collapsed;
        }

        // ---- handles
        bool handles = HasSel;
        double hs = Math.Max(5, Math.Round(7 * _dpi));
        if (hs % 2 == 0) hs++;
        for (int i = 0; i < _handles.Length; i++)
        {
            var h = _handles[i];
            bool mid = i % 2 == 1;
            bool visible = handles && (!mid || (i is 1 or 5 ? _sel.Width > hs * 3 : _sel.Height > hs * 3));
            h.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            var (ax, ay) = HandleAnchors[i];
            double cx = _sel.X - borderPx / 2 + ax * (_sel.Width + borderPx);
            double cy = _sel.Y - borderPx / 2 + ay * (_sel.Height + borderPx);
            h.Width = h.Height = hs;
            h.StrokeThickness = Math.Max(1, Math.Round(_dpi));
            System.Windows.Controls.Canvas.SetLeft(h, Math.Round(cx - hs / 2));
            System.Windows.Controls.Canvas.SetTop(h, Math.Round(cy - hs / 2));
        }

        // ---- guide lines (CAP-16)
        bool guides = _mouseKnown && _surface.Tool == AnnotationTool.None &&
                      (_cfg.GuideLines == GuideLinesMode.Always || (_cfg.GuideLines == GuideLinesMode.WhileAlt && _altDown));
        _guideH.Visibility = _guideV.Visibility = guides ? Visibility.Visible : Visibility.Collapsed;
        if (guides)
        {
            System.Windows.Controls.Canvas.SetTop(_guideH, Floor(_mousePx.Y));
            System.Windows.Controls.Canvas.SetLeft(_guideV, Floor(_mousePx.X));
        }

        UpdateSizeLabel(shown);
        UpdateToolbar();
        UpdateMagnifier();
        UpdateHints();
        UpdateQrPlacement();
    }

    // ================================================================================== UI layer placement

    /// <summary>Put a UI-layer element at an image-px position, scaled for the DPI of monitor <paramref name="monitor"/>.</summary>
    private void PlaceUi(FrameworkElement el, Point px, int monitor)
    {
        double k = MonitorScale(monitor) / _dpi;
        if (Math.Abs(k - 1) < 0.001) el.RenderTransform = Transform.Identity;
        else if (el.RenderTransform is not ScaleTransform st || Math.Abs(st.ScaleX - k) > 0.001) el.RenderTransform = new ScaleTransform(k, k);
        System.Windows.Controls.Canvas.SetLeft(el, Math.Round(px.X) / _dpi);
        System.Windows.Controls.Canvas.SetTop(el, Math.Round(px.Y) / _dpi);
    }

    /// <summary>Element size in image px when shown on monitor <paramref name="monitor"/>.</summary>
    private Size PxSize(FrameworkElement el, int monitor)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double s = MonitorScale(monitor);
        return new Size(el.DesiredSize.Width * s, el.DesiredSize.Height * s);
    }

    private void UpdateSizeLabel(Int32Rect? shown)
    {
        if (shown is not { } r)
        {
            _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _sizeLabel.SetSize(r.Width, r.Height);
        _sizeLabel.Editable = HasSel && _drag == DragKind.None && _mode == Core.CaptureMode.Edit;
        _sizeLabel.Visibility = Visibility.Visible;
        int mon = MonitorIndexAt(new Point(r.X, r.Y));
        var size = PxSize(_sizeLabel, mon);
        var pos = Placement.PlaceLabel(new Rect(r.X, r.Y, r.Width, r.Height), size, _monitorRects[mon], 4 * MonitorScale(mon));
        PlaceUi(_sizeLabel, pos, mon);
    }

    private void UpdateMagnifier()
    {
        if (_magnifier == null) return;
        bool visible = _mouseKnown && _surface.Tool == AnnotationTool.None && (!HasSel || _drag != DragKind.None) &&
                       SelectionMath.Contains(_bounds, _mousePx);
        _magnifier.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        int x = Floor(_mousePx.X), y = Floor(_mousePx.Y);
        var color = BitmapUtil.GetPixel(DisplayImage, x, y);
        var screen = _snap.ImageToScreen(new Point(x, y));
        Int32Rect? size = HasSel ? _sel : _hover;
        _magnifier.Update(x, y, (int)screen.X, (int)screen.Y, size, color, _colorFormat);

        int mon = MonitorIndexAt(_mousePx);
        _magnifier.SetDeviceScale(MonitorScale(mon));
        var px = PxSize(_magnifier, mon);
        var pos = Placement.PlaceNearCursor(new Point(x, y), px, _monitorRects[mon], 20 * MonitorScale(mon));
        PlaceUi(_magnifier, pos, mon);
    }

    private void UpdateHints()
    {
        if (_hints == null) return;
        if (!_mouseKnown || _surface.Tool != AnnotationTool.None)
        {
            _hints.Visibility = Visibility.Collapsed;
            return;
        }
        int mon = MonitorIndexAt(_mousePx);
        double s = MonitorScale(mon);
        _hints.Visibility = Visibility.Visible;
        var size = PxSize(_hints, mon);
        var cursorBox = new Rect(_mousePx.X - 40 * s, _mousePx.Y - 40 * s, 80 * s + 200 * s, 80 * s + 180 * s);
        Rect? selBox = HasSel ? new Rect(_sel.X, _sel.Y, _sel.Width, _sel.Height) : null;
        if (!_toolbarRectPx.IsEmpty && _toolbar?.Visibility == Visibility.Visible)
            selBox = selBox is { } sb ? Rect.Union(sb, _toolbarRectPx) : _toolbarRectPx;
        var pos = Placement.PlaceHints(size, _workRects[mon], 16 * s, cursorBox, selBox);
        if (pos is { } p) PlaceUi(_hints, p, mon);
        else _hints.Visibility = Visibility.Collapsed;
    }

    public void ShowToast(string message)
    {
        _toast.Show(message);
        int mon = MonitorIndexAt(_mouseKnown ? _mousePx : new Point(0, 0));
        var size = PxSize(_toast, mon);
        var m = _monitorRects[mon];
        PlaceUi(_toast, new Point(m.Left + (m.Width - size.Width) / 2, m.Top + 48 * MonitorScale(mon)), mon);
    }

    // ================================================================================== toolbar (CAP-08)

    private void UpdateToolbar()
    {
        bool visible = HasSel && _mode == Core.CaptureMode.Edit && _drag == DragKind.None;
        if (!visible)
        {
            if (_toolbar != null) _toolbar.Visibility = Visibility.Collapsed;
            if (_stylePanel != null) _stylePanel.Visibility = Visibility.Collapsed;
            _toolbarRectPx = Rect.Empty;
            return;
        }
        _toolbar ??= CreateToolbar();
        _toolbar.Visibility = Visibility.Visible;
        PlaceToolbar();
    }

    private void PlaceToolbar()
    {
        if (_toolbar == null || _toolbar.Visibility != Visibility.Visible || !HasSel) return;
        int mon = MonitorIndexFor(_sel);
        double s = MonitorScale(mon);
        var size = PxSize(_toolbar, mon);
        var selRect = new Rect(_sel.X, _sel.Y, _sel.Width, _sel.Height);
        selRect.Inflate(_cfg.SelectionBorderWidth * _dpi, _cfg.SelectionBorderWidth * _dpi);
        // The toolbar's drop shadow reaches ~12 px outside its bounds: keep that gap from the selection border.
        var (pos, side) = Placement.PlaceToolbar(selRect, size, _monitorRects[mon], 12 * s);
        // Tool options open towards the selection when the toolbar sits above it (or inside it).
        _toolbar.OptionsAbove = side != ToolbarSide.Below;
        PlaceUi(_toolbar, pos, mon);
        _toolbarRectPx = new Rect(pos, size);

        if (_stylePanel is { Visibility: Visibility.Visible } panel)
        {
            var psize = PxSize(panel, mon);
            var (ppos, _) = Placement.PlaceToolbar(_toolbarRectPx, psize, _monitorRects[mon], 6 * s);
            PlaceUi(panel, ppos, mon);
        }
    }

    private AnnotationToolbar CreateToolbar()
    {
        var tb = new AnnotationToolbar { Surface = _surface, Focusable = false };
        void Add(string id, string glyph, string tipKey, Action a) =>
            tb.HostActions.Add(new ToolbarAction(id, glyph, Loc.Get(tipKey), () => Guard(a)));
        // Segoe Fluent Icons / MDL2 glyphs
        Add("capture.style", "", "Capture.Action.Style", ToggleStylePanel);
        Add("capture.long", "", "Capture.Action.LongCapture", DoLongCapture);
        Add("capture.record", "", "Capture.Action.Record", DoRecord);
        Add("capture.ocr", "", "Capture.Action.Ocr", DoOcr);
        Add("capture.pin", "", "Capture.Action.Pin", DoPin);
        Add("capture.save", "", "Capture.Action.Save", DoSave);
        Add("capture.copy", "", "Capture.Action.Copy", DoCopy);
        Add("capture.close", "", "Capture.Action.Close", () => CloseOverlay("toolbar"));
        // The toolbar may grow (tool options); keep it inside the monitor.
        tb.SizeChanged += (_, _) => Guard(PlaceToolbar);
        _ui.Children.Add(tb);
        return tb;
    }

    private void ToggleStylePanel()
    {
        if (_stylePanel == null)
        {
            _stylePanel = new StylePanel(_cfg);
            _stylePanel.Changed += ratioChanged => Guard(() =>
            {
                _settingsDirty = true;
                if (ratioChanged && HasSel && SelectionMath.ParseRatio(_cfg.AspectRatio) is { } k)
                {
                    _sel = SelectionMath.ApplyRatio(_sel, k, _bounds);
                    OnSelectionChanged();
                }
                MarkDirty();
            });
            _stylePanel.SizeChanged += (_, _) => Guard(PlaceToolbar);
            _ui.Children.Add(_stylePanel);
        }
        bool show = _stylePanel.Visibility != Visibility.Visible;
        if (show && _qrPanel.IsExpanded) _qrPanel.Hide();
        _stylePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlaceToolbar();
    }
}
