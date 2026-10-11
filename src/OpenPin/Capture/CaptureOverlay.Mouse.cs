using System.Windows;
using System.Windows.Input;
using OpenPin.Annotation;

namespace OpenPin.Capture;

internal sealed partial class CaptureOverlay
{
    private const double DragThresholdDip = 3;
    private bool _initialToolApplied;

    private double HandleTolerancePx => 6 * _dpi;
    private double EdgeTolerancePx => 4 * _dpi;
    private bool ToolActive => _surface.Tool != AnnotationTool.None;

    /// <summary>Fixed ratio while dragging: Shift = 1:1, else the configured aspect ratio (CAP-11).</summary>
    private double? DragRatio =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1.0 : SelectionMath.ParseRatio(_cfg.AspectRatio);

    // ================================================================================== left button

    private void OnLeftDown(MouseButtonEventArgs e)
    {
        if (IsFromUi(e.OriginalSource)) return;
        var p = PosPx(e);
        _mousePx = p;
        _mouseKnown = true;
        _shiftClean = false;
        if (_sizeLabel.IsEditing) _sizeLabel.EndEdit();
        HideFlyouts();

        if (HasSel)
        {
            bool tool = ToolActive;
            if (e.ClickCount == 2 && !tool && SelectionMath.Contains(_sel, p))
            {
                e.Handled = true;
                ConfirmAction();
                return;
            }
            var hit = SelectionMath.HitTest(_sel, p, HandleTolerancePx, EdgeTolerancePx, includeEdges: !tool,
                includeInside: !tool);
            if (hit != HitKind.None)
            {
                e.Handled = true;
                BeginDrag(DragKind.Adjusting, p, hit);
                return;
            }
            if (tool) return; // the annotation surface draws (clamped to EditBounds)
            e.Handled = true; // outside the selection without a tool: ignored (right-click resets)
            return;
        }

        e.Handled = true;
        BeginDrag(DragKind.Pressing, p, HitKind.None);
    }

    private void BeginDrag(DragKind kind, Point p, HitKind hit)
    {
        _drag = kind;
        _downPx = p;
        _adjustKind = hit;
        _dragStartSel = _sel;
        if (kind == DragKind.Adjusting) Mouse.OverrideCursor = CursorFor(hit);
        CaptureMouse();
        MarkDirty();
    }

    private void OnOverlayMouseMove(MouseEventArgs e)
    {
        var p = PosPx(e);
        bool moved = !_mouseKnown || Floor(p.X) != Floor(_mousePx.X) || Floor(p.Y) != Floor(_mousePx.Y);
        _mousePx = p;
        _mouseKnown = true;

        switch (_drag)
        {
            case DragKind.Pressing:
            case DragKind.Selecting:
            case DragKind.Adjusting:
                if (moved) UpdateDrag();
                break;
            default:
                if (IsFromUi(e.OriginalSource)) { Mouse.OverrideCursor = null; break; }
                if (!HasSel) { if (moved) UpdateHover(p); }
                UpdateCursor(p);
                break;
        }
        if (moved) MarkDirty();
    }

    /// <summary>Apply the current mouse position (and Shift state) to the drag in progress.</summary>
    private void UpdateDrag()
    {
        var p = _mousePx;
        switch (_drag)
        {
            case DragKind.Pressing:
                var d = (p - _downPx) / _dpi;
                if (Math.Abs(d.X) < DragThresholdDip && Math.Abs(d.Y) < DragThresholdDip) return;
                _drag = DragKind.Selecting;
                _hover = null;
                _levels = null;
                goto case DragKind.Selecting;
            case DragKind.Selecting:
                _sel = SelectionMath.FromPoints(Floor(_downPx.X), Floor(_downPx.Y), Floor(p.X), Floor(p.Y), _bounds, DragRatio);
                OnSelectionChanged();
                break;
            case DragKind.Adjusting:
                int dx = Floor(p.X) - Floor(_downPx.X), dy = Floor(p.Y) - Floor(_downPx.Y);
                bool corner = _adjustKind is HitKind.TopLeft or HitKind.TopRight or HitKind.BottomLeft or HitKind.BottomRight;
                double? ratio = _adjustKind == HitKind.Inside ? null
                    : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && corner ? 1.0 : SelectionMath.ParseRatio(_cfg.AspectRatio);
                _sel = SelectionMath.Resize(_dragStartSel, _adjustKind, dx, dy, _bounds, ratio);
                OnSelectionChanged();
                break;
        }
    }

    private void OnLeftUp(MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None) return;
        e.Handled = true;
        EndDrag(commit: true);
    }

    private void OnLostCapture()
    {
        if (_drag != DragKind.None) EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        var kind = _drag;
        _drag = DragKind.None;
        Mouse.OverrideCursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!commit) { MarkDirty(); return; }

        switch (kind)
        {
            case DragKind.Pressing:
                // Click without drag: take the detected window / element.
                if (_hover is { } h)
                {
                    _sel = h;
                    ConfirmSelection();
                }
                break;
            case DragKind.Selecting:
                if (HasSel) ConfirmSelection();
                break;
            case DragKind.Adjusting:
                OnSelectionChanged();
                break;
        }
        UpdateCursor(_mousePx);
        MarkDirty();
    }

    // ================================================================================== right button / wheel

    private void OnRightDown(MouseButtonEventArgs e)
    {
        if (IsFromUi(e.OriginalSource)) return;
        var p = PosPx(e);
        if (ToolActive && HasSel && SelectionMath.Contains(_sel, p) && _drag == DragKind.None) return; // surface (end polyline, snap…)
        e.Handled = true;
        _rightHandled = true;
        if (_drag != DragKind.None)
        {
            CancelDrag();
            return;
        }
        if (HasSel) ResetSelection();
        else CloseOverlay();
    }

    /// <summary>Abort the drag in progress: a new selection is dropped, a move/resize is undone.</summary>
    private void CancelDrag()
    {
        bool wasNew = _drag is DragKind.Pressing or DragKind.Selecting;
        if (_drag == DragKind.Adjusting) _sel = _dragStartSel;
        EndDrag(commit: false);
        if (wasNew) ResetSelection();
        else OnSelectionChanged();
    }

    private void OnWheel(MouseWheelEventArgs e)
    {
        if (IsFromUi(e.OriginalSource) || ToolActive || HasSel || _drag != DragKind.None) return;
        if (_levels == null) return;
        e.Handled = true;
        bool changed = e.Delta > 0 ? _levels.Parent() : _levels.Child();
        if (changed)
        {
            _hover = _levels.Current;
            MarkDirty();
        }
    }

    // ================================================================================== hover detection (CAP-02 / CAP-03)

    private void UpdateHover(Point p)
    {
        if (_cfg.UiDetect == UiDetectMode.None)
        {
            _hover = null;
            _levels = null;
            return;
        }
        var t = _targets.HitTest(p);
        if (t == null)
        {
            _hover = null;
            _hoverTarget = null;
            _levels = null;
            return;
        }

        bool sameTarget = _hoverTarget != null && _hoverTarget.RectImg == t.RectImg && _hoverTarget.Window?.Handle == t.Window?.Handle;
        if (!sameTarget || _levels == null)
        {
            _hoverTarget = t;
            _levels = new ElementLevels(new[] { t.RectImg });
            _hover = t.RectImg;
            RequestElements(p);
            return;
        }

        // Same window: if the cursor left the current deepest element, fall back to the deepest one still containing it.
        var rects = _levels.Rects;
        if (rects.Count > 0 && !SelectionMath.Contains(rects[^1], p))
        {
            var keep = rects.TakeWhile(r => SelectionMath.Contains(r, p)).ToList();
            if (keep.Count == 0) keep.Add(t.RectImg);
            _levels = new ElementLevels(keep);
            _hover = _levels.Current;
            RequestElements(p);
        }
    }

    private void RequestElements(Point p)
    {
        if (_cfg.UiDetect != UiDetectMode.Elements || _hoverTarget?.Window == null) return;
        if (DetectionTargets.IsDesktopClass(_hoverTarget.Window.ClassName)) return;
        _detector ??= CreateDetector();
        var screen = _snap.ImageToScreen(new Point(Floor(p.X), Floor(p.Y)));
        _detector.Request(_hoverTarget.Window.Handle, (int)screen.X, (int)screen.Y, ++_detectSeq);
    }

    private ElementDetector CreateDetector()
    {
        var d = new ElementDetector(Dispatcher, _cfg.UiDetectTimeoutMs);
        d.Completed += (seq, hwnd, chain) => Guard(() => OnElementsDetected(seq, hwnd, chain));
        return d;
    }

    private void OnElementsDetected(int seq, IntPtr hwnd, IReadOnlyList<Int32Rect> chainScreen)
    {
        if (seq != _detectSeq || HasSel || _drag != DragKind.None || _hoverTarget?.Window?.Handle != hwnd) return;
        var rects = new List<Int32Rect> { _hoverTarget.RectImg };
        rects.AddRange(chainScreen.Select(r => _snap.ScreenToImage(r)));
        var levels = new ElementLevels(rects);
        if (levels.Rects.Count == 0 || !SelectionMath.Contains(levels.Rects[^1], _mousePx)) return;
        if (levels.SameAs(_levels)) return;
        // Keep the level the user picked with the wheel; otherwise show the deepest element.
        bool wasDeepest = _levels == null || _levels.Index == _levels.Rects.Count - 1;
        if (!wasDeepest && _levels!.Current is { } current) levels.TrySelect(current);
        _levels = levels;
        _hover = levels.Current;
        MarkDirty();
    }

    /// <summary>ANN-17 support: the smallest known window/element rectangle containing an image-px point.</summary>
    private Rect? SnapRect(Point p)
    {
        if (_levels != null)
            for (int i = _levels.Rects.Count - 1; i >= 0; i--)
                if (SelectionMath.Contains(_levels.Rects[i], p))
                {
                    var r = _levels.Rects[i];
                    return new Rect(r.X, r.Y, r.Width, r.Height);
                }
        if (_targets.HitTest(p) is { } t) return new Rect(t.RectImg.X, t.RectImg.Y, t.RectImg.Width, t.RectImg.Height);
        return null;
    }

    // ================================================================================== cursor

    private void UpdateCursor(Point p)
    {
        if (_drag != DragKind.None) return;
        if (!HasSel)
        {
            Mouse.OverrideCursor = null;
            Cursor = Cursors.Cross;
            return;
        }
        bool tool = ToolActive;
        var hit = SelectionMath.HitTest(_sel, p, HandleTolerancePx, EdgeTolerancePx, includeEdges: !tool,
            includeInside: !tool);
        if (tool)
        {
            // Only the handles stay usable while a tool is active; elsewhere the surface decides.
            Mouse.OverrideCursor = hit == HitKind.None ? null : CursorFor(hit);
        }
        else
        {
            Mouse.OverrideCursor = null;
            Cursor = hit == HitKind.None ? Cursors.Arrow : CursorFor(hit);
        }
    }

    private static Cursor CursorFor(HitKind hit) => hit switch
    {
        HitKind.Left or HitKind.Right => Cursors.SizeWE,
        HitKind.Top or HitKind.Bottom => Cursors.SizeNS,
        HitKind.TopLeft or HitKind.BottomRight => Cursors.SizeNWSE,
        HitKind.TopRight or HitKind.BottomLeft => Cursors.SizeNESW,
        HitKind.Inside => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    // ================================================================================== selection state

    /// <summary>Selection finished by the user (mouse up / window click / Ctrl+A). Non-Edit modes act immediately.</summary>
    private void ConfirmSelection()
    {
        _hover = null;
        _levels = null;
        OnSelectionChanged();
        if (_mode != Core.CaptureMode.Edit)
        {
            ExecuteModeAction();
            return;
        }
        if (!_initialToolApplied)
        {
            // Honour Annotation's "remember last tool in capture" on the first selection of this session.
            _initialToolApplied = true;
            var tool = AnnotationSurface.GetInitialTool(forPin: false);
            if (tool != AnnotationTool.None) _surface.Tool = tool;
        }
        if (_cfg.QrDetect is QrDetectMode.Auto or QrDetectMode.ShowContent) RunQrDetection(manual: false);
    }

    private void OnSelectionChanged()
    {
        _surface.EditBounds = HasSel ? new Rect(_sel.X, _sel.Y, _sel.Width, _sel.Height) : new Rect(0, 0, 0, 0);
        if (_qrRegion is { } q && q != _sel) ClearQr();
        MarkDirty();
    }

    /// <summary>Right-click / cancel: drop the selection and annotations, back to window detection.</summary>
    private void ResetSelection()
    {
        _sel = Int32Rect.Empty;
        _surface.Tool = AnnotationTool.None;
        _surface.ClearAll();
        HideFlyouts();
        ClearQr();
        if (_sizeLabel.IsEditing) _sizeLabel.EndEdit();
        _hoverTarget = null;
        _levels = null;
        OnSelectionChanged();
        if (_mouseKnown) UpdateHover(_mousePx);
        UpdateCursor(_mousePx);
        MarkDirty();
    }

    /// <summary>Select without triggering the mode action (history, exact size, custom region).</summary>
    private void SetSelection(Int32Rect rect)
    {
        var r = SelectionMath.Clamp(rect, _bounds);
        if (SelectionMath.IsEmpty(r)) return;
        _sel = r;
        _hover = null;
        _levels = null;
        OnSelectionChanged();
        UpdateCursor(_mousePx);
    }

    private void SetExactSize(int w, int h)
    {
        if (!HasSel) return;
        SetSelection(new Int32Rect(_sel.X, _sel.Y, w, h));
    }

    private void HideFlyouts()
    {
        if (_stylePanel != null) _stylePanel.Visibility = Visibility.Collapsed;
        if (_recognizePanel != null) _recognizePanel.Visibility = Visibility.Collapsed;
        if (_qrPanel.IsExpanded) _qrPanel.Hide();
    }
}
