using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;
using OpenPin.Core;

namespace OpenPin.Annotation;

public partial class AnnotationSurface
{
    /// <summary>A mouse-drag interaction in progress (create / move / resize / erase).</summary>
    private sealed class Gesture
    {
        public Action<Point, ModifierKeys>? Move;
        public Action<Point, ModifierKeys>? Up;
        public Action? Cancel;
    }

    private Gesture? _gesture;
    /// <summary>Item being created (not yet in the document).</summary>
    private AnnotationItem? _preview;
    /// <summary>Items about to be erased (drawn faded).</summary>
    private readonly HashSet<Guid> _faded = new();
    /// <summary>Polyline in progress (click-by-click mode of the line tool).</summary>
    private LineItem? _polyline;
    private Rect? _rubber;
    private Point _mouse;
    private bool _mouseOver;

    // ------------------------------------------------------------------ helpers

    private Rect EditRect => _ctx.ContentBounds;

    private bool InEdit(Point p) => EditRect.Contains(p);

    private Point ClampEdit(Point p) => Geo.Clamp(p, EditRect);

    /// <summary>Device pixels per surface unit (host zoom × DPI). Used to keep handles a constant screen size.</summary>
    internal double ScreenScale()
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.RootVisual == null || src.CompositionTarget == null) return 1;
            var t = TransformToAncestor(src.RootVisual);
            var a = t.Transform(new Point(0, 0));
            var b = t.Transform(new Point(100, 0));
            double s = (b - a).Length / 100 * src.CompositionTarget.TransformToDevice.M11;
            return s > 0.001 ? s : 1;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    private double HandleRadius() => 5 / ScreenScale();
    private double HitTolerance() => 4 / ScreenScale();

    private void CommitPreview(bool select)
    {
        var item = _preview;
        _preview = null;
        if (item == null) return;
        _doc.Checkpoint();
        _doc.Items.Add(item);
        SyncVisuals();
        if (select && ToolOptionsStore.Settings.AllowSecondaryEdit) Select(item);
        if (item is SerialItem) SerialChanged?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    private void DiscardPreview()
    {
        _preview = null;
        SyncVisuals();
    }

    private void CancelGesture()
    {
        if (_polyline != null)
        {
            _polyline = null;
            DiscardPreview();
        }
        if (_gesture == null) return;
        var g = _gesture;
        _gesture = null;
        try { g.Cancel?.Invoke(); }
        catch (Exception ex) { Log.Error("Annotation gesture cancel failed", ex); }
        _preview = null;
        _faded.Clear();
        _rubber = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SyncVisuals();
        RedrawOverlay();
    }

    private void EndGesture(Point p, ModifierKeys mods)
    {
        var g = _gesture;
        _gesture = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (g == null) return;
        try { g.Up?.Invoke(p, mods); }
        catch (Exception ex) { Log.Error("Annotation gesture failed", ex); _preview = null; }
        _rubber = null;
        SyncVisuals();
        RedrawOverlay();
        UpdateCursor(p);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled) return;
        if (PointerDown(e.GetPosition(this), Keyboard.Modifiers, e.ClickCount))
        {
            e.Handled = true;
            if (_gesture != null) CaptureMouse();
        }
    }

    /// <summary>Left button down (image px). Returns true if the surface handled it. Also used by tests.</summary>
    internal bool PointerDown(Point p, ModifierKeys mods, int clickCount = 1)
    {
        if (_tool == AnnotationTool.None) return false;
        _mouse = p;

        if (_polyline != null)
        {
            PolylineClick(p, mods, clickCount);
            return true;
        }

        if (_editor != null)
        {
            // Clicking outside the editor finishes the text (the click does not start something new).
            CommitText();
            Focus();
            return true;
        }
        if (_gesture != null) return true;
        Focus();

        // 1. Handles of the selected item.
        if (Selected is { } sel && HitHandle(sel, p) is { } h)
        {
            StartHandleDrag(sel, h, p);
        }
        // 2. Existing item of the current tool (secondary edit, ANN-15).
        else if (_tool != AnnotationTool.Eraser && HitItem(p, HitTolerance()) is { } hit)
        {
            if (clickCount == 2 && hit is LineItem { IsArrow: true } arrow)
            {
                Select(null);
                BeginArrowNote(arrow);
                return true;
            }
            Select(hit);
            StartMove(hit, p);
        }
        // 3. New annotation.
        else if (InEdit(p))
        {
            Select(null);
            StartCreate(p, mods);
        }
        else
        {
            Select(null);
            return false; // outside the editable area: let the host handle it
        }
        RedrawOverlay();
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_tool == AnnotationTool.None) return;
        _mouseOver = true;
        PointerMove(e.GetPosition(this), Keyboard.Modifiers, e.LeftButton == MouseButtonState.Pressed || IsMouseCaptured);
    }

    /// <summary>Mouse move (image px). Also used by tests.</summary>
    internal void PointerMove(Point p, ModifierKeys mods, bool leftPressed = true)
    {
        _mouse = p;
        if (_gesture != null)
        {
            if (!leftPressed)
            {
                EndGesture(p, mods);
                return;
            }
            try { _gesture.Move?.Invoke(p, mods); }
            catch (Exception ex) { Log.Error("Annotation gesture move failed", ex); }
            RedrawOverlay();
            return;
        }
        if (_polyline != null)
        {
            UpdatePolylineRubber(p, mods);
            return;
        }
        UpdateCursor(p);
        if (ShowsBrushCursor) RedrawOverlay();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_gesture == null) return;
        PointerUp(e.GetPosition(this), Keyboard.Modifiers);
        e.Handled = true;
    }

    /// <summary>Left button up (image px). Also used by tests.</summary>
    internal void PointerUp(Point p, ModifierKeys mods)
    {
        if (_gesture != null) EndGesture(p, mods);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // E.g. the window was deactivated mid-drag: finish what we have rather than leaving a dangling gesture.
        if (_gesture != null) EndGesture(_mouse, Keyboard.Modifiers);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseOver = false;
        if (ShowsBrushCursor) RedrawOverlay();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_tool == AnnotationTool.None || e.Handled) return;
        if (_polyline != null)
        {
            FinishPolyline();
            e.Handled = true;
            return;
        }
        if (_gesture != null || _editor != null) { e.Handled = true; return; }
        var p = e.GetPosition(this);
        if (SnapRectProvider != null && SupportsSnap(_tool) && InEdit(p))
        {
            Rect? snapped = null;
            try { snapped = SnapRectProvider(p); }
            catch (Exception ex) { Log.Error("SnapRectProvider failed", ex); }
            if (snapped is { } r)
            {
                r = Rect.Intersect(r, EditRect);
                if (!r.IsEmpty && r.Width >= 1 && r.Height >= 1)
                {
                    CreateFromRect(r);
                    e.Handled = true;
                }
            }
        }
    }

    /// <summary>Tools that support right-click "fit to element" (ANN-17).</summary>
    private bool SupportsSnap(AnnotationTool t) => t switch
    {
        AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Spotlight => true,
        AnnotationTool.Mosaic or AnnotationTool.Eraser or AnnotationTool.Highlighter => true,
        _ => false,
    };

    /// <summary>Create the current tool's rect-shaped annotation from a snapped rectangle.</summary>
    private void CreateFromRect(Rect r)
    {
        Select(null);
        var o = Options.Get(_tool);
        switch (_tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
            {
                var s = new ShapeItem { IsEllipse = _tool == AnnotationTool.Ellipse };
                s.ApplyOptions(o);
                // Keep the stroke inside the element bounds.
                s.Rect = Geo.Inflate(r, -s.StrokeWidth / 2);
                _preview = s;
                CommitPreview(select: true);
                break;
            }
            case AnnotationTool.Spotlight:
                _preview = new SpotlightItem { Rect = r, Shape = o.Shape, Opacity = o.Opacity };
                CommitPreview(select: true);
                break;
            case AnnotationTool.Mosaic:
            {
                var m = new MosaicItem { Mode = DrawMode.Rect, Rect = r };
                m.ApplyOptions(o);
                _preview = m;
                CommitPreview(select: true);
                break;
            }
            case AnnotationTool.Highlighter:
            {
                var hl = new HighlighterItem { Mode = DrawMode.Rect, Rect = r };
                hl.ApplyOptions(o);
                _preview = hl;
                CommitPreview(select: true);
                break;
            }
            case AnnotationTool.Eraser:
                DeleteItems(_doc.Items.Where(i => i.IntersectsRect(r)).Select(i => i.Id).ToList());
                break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_tool == AnnotationTool.None || e.Handled || _gesture != null) return;
        AdjustByWheel(_tool, Math.Sign(e.Delta), (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        if (ShowsBrushCursor) RedrawOverlay();
        e.Handled = true;
    }

    /// <summary>Wheel: size (or the tool's main value); Ctrl+wheel: color alpha (ANN-14).</summary>
    internal void AdjustByWheel(AnnotationTool tool, int dir, bool ctrl)
    {
        if (dir == 0) return;
        var o = Options.Get(tool);
        if (ctrl)
        {
            if (tool is AnnotationTool.Mosaic or AnnotationTool.Eraser) return;
            if (tool is AnnotationTool.Spotlight or AnnotationTool.Watermark)
                o.Opacity = Math.Round(Math.Clamp(o.Opacity + dir * 0.05, 0.05, 1), 2);
            else
            {
                var c = o.ColorValue;
                int a = Math.Clamp(c.A + dir * 26, 26, 255);
                o.ColorValue = Color.FromArgb((byte)a, c.R, c.G, c.B);
            }
        }
        else
        {
            switch (tool)
            {
                case AnnotationTool.Spotlight:
                    o.Opacity = Math.Round(Math.Clamp(o.Opacity + dir * 0.05, 0.05, 0.95), 2);
                    break;
                case AnnotationTool.Magnifier:
                    o.Zoom = Math.Clamp(o.Zoom + dir, 1, 8);
                    break;
                case AnnotationTool.Mosaic when o.Mode == DrawMode.Rect:
                    // Smart erase has no strength (PRO-06).
                    if (o.Effect != MosaicEffect.SmartErase) o.Strength = Math.Clamp(o.Strength + dir, 1, 10);
                    break;
                default:
                {
                    var (min, max, step) = ToolOptions.SizeRange(tool);
                    o.Size = Math.Clamp(o.Size + dir * step, min, max);
                    break;
                }
            }
        }
        Options.Commit(tool);
    }

    // ------------------------------------------------------------------ gestures

    private void StartHandleDrag(AnnotationItem item, Handle h, Point p)
    {
        var before = _doc.Snapshot();
        var start = item.Clone();
        bool moved = false;
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                moved = true;
                item.DragHandle(h.Id, q, m, start);
                item.Touch();
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                if (!moved) return;
                _doc.Push(before);
                RaiseChanged();
            },
            Cancel = () => { _doc.Restore(before); },
        };
    }

    private void StartMove(AnnotationItem item, Point p)
    {
        var before = _doc.Snapshot();
        int part = item.BodyPart(p);
        var last = p;
        bool moved = false;
        double threshold = 2 / ScreenScale();
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                if (!moved && (q - p).Length < threshold) return;
                moved = true;
                item.MoveBy(part, q - last);
                last = q;
                item.Touch();
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                if (moved)
                {
                    _doc.Push(before);
                    RaiseChanged();
                }
                else if (item is TextItem t && _tool == AnnotationTool.Text)
                {
                    // Click (no drag) on a text with the text tool → edit it in place (ANN-09).
                    Dispatcher.BeginInvoke(() => BeginEditText(t, isNew: false));
                }
            },
            Cancel = () => { _doc.Restore(before); },
        };
    }

    private void StartCreate(Point p, ModifierKeys mods)
    {
        var o = Options.Get(_tool);
        var start = p;
        switch (_tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
            {
                var item = new ShapeItem { IsEllipse = _tool == AnnotationTool.Ellipse, Rect = new Rect(p, p) };
                item.ApplyOptions(o);
                StartRectCreate(item, start, (it, r) => ((ShapeItem)it).Rect = r);
                break;
            }
            case AnnotationTool.Spotlight:
            {
                var item = new SpotlightItem { Rect = new Rect(p, p), Shape = o.Shape, Opacity = o.Opacity };
                StartRectCreate(item, start, (it, r) => ((SpotlightItem)it).Rect = r);
                break;
            }
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
                StartLineCreate(p, o);
                break;
            case AnnotationTool.Serial:
                StartSerialCreate(p, o);
                break;
            case AnnotationTool.Pencil:
            {
                var item = new PencilItem();
                item.ApplyOptions(o);
                StartFreehand(item, item.Points, p, smooth: true);
                break;
            }
            case AnnotationTool.Highlighter:
            {
                var item = new HighlighterItem { Mode = o.Mode };
                item.ApplyOptions(o);
                if (o.Mode == DrawMode.Rect) StartRectCreate(item, start, (it, r) => ((RegionItem)it).Rect = r);
                else StartFreehand(item, item.Points, p, smooth: true);
                break;
            }
            case AnnotationTool.Mosaic:
            {
                var item = new MosaicItem { Mode = o.Mode };
                item.ApplyOptions(o);
                if (o.Mode == DrawMode.Rect) StartRectCreate(item, start, (it, r) => ((RegionItem)it).Rect = r);
                else StartFreehand(item, item.Points, p, smooth: false);
                break;
            }
            case AnnotationTool.Magnifier:
                StartMagnifierCreate(p, o);
                break;
            case AnnotationTool.Eraser:
                if (o.Mode == DrawMode.Rect) StartEraserRect(p);
                else if (o.Mode == DrawMode.Partial) StartEraserPartial(p, o.Size / 2);
                else StartEraserBrush(p, o.Size / 2);
                break;
            case AnnotationTool.Text:
                BeginNewText(p);
                break;
            case AnnotationTool.Watermark:
                break;
        }
    }

    private void StartRectCreate(AnnotationItem item, Point start, Action<AnnotationItem, Rect> setRect)
    {
        _preview = item;
        bool hasHandles = item is not RegionItem;
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                q = ClampEdit(q);
                setRect(item, Geo.RectFromPoints(start, q, (m & ModifierKeys.Shift) != 0));
                item.Touch();
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                q = ClampEdit(q);
                var r = Geo.RectFromPoints(start, q, (m & ModifierKeys.Shift) != 0);
                setRect(item, r);
                item.Touch();
                if (r.Width >= 2 && r.Height >= 2) CommitPreview(select: hasHandles || item is RegionItem { Mode: DrawMode.Rect });
                else DiscardPreview();
            },
            Cancel = DiscardPreview,
        };
        SyncVisuals();
    }

    private void StartLineCreate(Point p, ToolOptions o)
    {
        var item = new LineItem { IsArrow = _tool == AnnotationTool.Arrow };
        item.ApplyOptions(o);
        item.Points.Add(p);
        item.Points.Add(p);
        _preview = item;
        var start = p;
        double clickThreshold = 3 / ScreenScale();
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                q = ClampEdit(q);
                item.Points[1] = (m & ModifierKeys.Shift) != 0 ? ClampEdit(Geo.Snap45(start, q)) : q;
                item.Touch();
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                q = ClampEdit(q);
                if ((q - start).Length >= clickThreshold)
                {
                    item.Points[1] = (m & ModifierKeys.Shift) != 0 ? ClampEdit(Geo.Snap45(start, q)) : q;
                    item.Touch();
                    CommitPreview(select: true);
                }
                else if (!item.IsArrow)
                {
                    // Click without drag: polyline mode — click to add points, double-click / right-click / Enter to finish.
                    _polyline = item;
                    item.Points[1] = start;
                    item.Touch();
                }
                else DiscardPreview();
            },
            Cancel = DiscardPreview,
        };
        SyncVisuals();
    }

    private void PolylineClick(Point p, ModifierKeys mods, int clickCount)
    {
        var item = _polyline!;
        if (clickCount >= 2)
        {
            FinishPolyline();
            return;
        }
        if (!InEdit(p)) p = ClampEdit(p);
        if ((mods & ModifierKeys.Shift) != 0) p = ClampEdit(Geo.Snap45(item.Points[^2], p));
        item.Points[^1] = p;
        item.Points.Add(p);
        item.Touch();
        SyncVisuals();
    }

    private void UpdatePolylineRubber(Point p, ModifierKeys mods)
    {
        var item = _polyline!;
        p = ClampEdit(p);
        if ((mods & ModifierKeys.Shift) != 0) p = ClampEdit(Geo.Snap45(item.Points[^2], p));
        item.Points[^1] = p;
        item.Touch();
        SyncVisuals();
    }

    private void FinishPolyline()
    {
        var item = _polyline;
        _polyline = null;
        if (item == null) return;
        // Drop the rubber-band point and duplicates (double-click adds the same point twice).
        if (item.Points.Count > 2) item.Points.RemoveAt(item.Points.Count - 1);
        var pts = new List<Point>();
        foreach (var q in item.Points)
            if (pts.Count == 0 || (q - pts[^1]).Length > 0.5) pts.Add(q);
        item.Points = pts;
        item.Touch();
        if (pts.Count >= 2) CommitPreview(select: true);
        else DiscardPreview();
    }

    private void StartSerialCreate(Point p, ToolOptions o)
    {
        var item = new SerialItem { Center = p, Value = NextSerialNumber };
        item.ApplyOptions(o);
        _preview = item;
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                q = ClampEdit(q);
                item.Tip = (q - item.Center).Length > item.Size / 2 + 4 ? q : null;
                item.Touch();
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                item.Value = TakeSerialNumber();
                item.Touch();
                CommitPreview(select: false);
            },
            Cancel = DiscardPreview,
        };
        SyncVisuals();
    }

    /// <summary>Freehand stroke: points with a minimum spacing; Shift draws a straight segment from where it was pressed.</summary>
    private void StartFreehand(AnnotationItem item, List<Point> points, Point p, bool smooth)
    {
        points.Add(p);
        _preview = item;
        int? anchor = null;
        bool straightUsed = false;
        double minStep = 1 / Math.Max(0.5, ScreenScale());
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                q = ClampEdit(q);
                bool shift = (m & ModifierKeys.Shift) != 0;
                if (shift)
                {
                    anchor ??= points.Count - 1;
                    straightUsed = true;
                    if (points.Count > anchor.Value + 1) points.RemoveRange(anchor.Value + 1, points.Count - anchor.Value - 1);
                    points.Add(q);
                }
                else
                {
                    anchor = null;
                    if ((q - points[^1]).Length < minStep) return;
                    points.Add(q);
                }
                item.Touch();
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                q = ClampEdit(q);
                if ((m & ModifierKeys.Shift) == 0 && (q - points[^1]).Length >= minStep) points.Add(q);
                if (smooth && !straightUsed && points.Count > 3)
                {
                    var sm = Geo.Smooth(points);
                    points.Clear();
                    points.AddRange(sm);
                }
                item.Touch();
                CommitPreview(select: false);
            },
            Cancel = DiscardPreview,
        };
        item.Touch();
        SyncVisuals();
    }

    private void StartMagnifierCreate(Point p, ToolOptions o)
    {
        var item = new MagnifierItem();
        item.ApplyOptions(o);
        item.Zoom = Math.Clamp(o.Zoom, 1, 8);
        var start = p;
        _preview = item;
        void Update(Point q, ModifierKeys m)
        {
            q = ClampEdit(q);
            var r = Geo.RectFromPoints(start, q, (m & ModifierKeys.Shift) != 0);
            item.Source = new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
            item.TargetTopLeft = PlaceMagnifierTarget(item.Source, item.Zoom);
            item.Touch();
            SyncVisuals();
        }
        _gesture = new Gesture
        {
            Move = Update,
            Up = (q, m) =>
            {
                Update(q, m);
                if (item.Source.Width >= 4 && item.Source.Height >= 4) CommitPreview(select: true);
                else DiscardPreview();
            },
            Cancel = DiscardPreview,
        };
    }

    /// <summary>Put the enlarged view next to the source: right, left, below, above (first that fits), else right.</summary>
    internal Point PlaceMagnifierTarget(Rect src, int zoom)
    {
        const double gap = 24;
        double tw = src.Width * zoom, th = src.Height * zoom;
        var c = EditRect;
        double cy = Math.Clamp(src.Y + src.Height / 2 - th / 2, c.Top, Math.Max(c.Top, c.Bottom - th));
        double cx = Math.Clamp(src.X + src.Width / 2 - tw / 2, c.Left, Math.Max(c.Left, c.Right - tw));
        if (src.Right + gap + tw <= c.Right) return new Point(src.Right + gap, cy);
        if (src.Left - gap - tw >= c.Left) return new Point(src.Left - gap - tw, cy);
        if (src.Bottom + gap + th <= c.Bottom) return new Point(cx, src.Bottom + gap);
        if (src.Top - gap - th >= c.Top) return new Point(cx, src.Top - gap - th);
        return new Point(src.Right + gap, cy);
    }

    private void StartEraserBrush(Point p, double radius)
    {
        var last = p;
        void Hit(Point a, Point b)
        {
            foreach (var it in _doc.Items)
                if (!_faded.Contains(it.Id) && it.HitTestSegment(a, b, radius)) _faded.Add(it.Id);
        }
        Hit(p, p);
        SyncVisuals();
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                int before = _faded.Count;
                Hit(last, q);
                last = q;
                if (_faded.Count != before) SyncVisuals();
            },
            Up = (q, m) =>
            {
                var ids = _faded.ToList();
                _faded.Clear();
                DeleteItems(ids);
            },
            Cancel = () => _faded.Clear(),
        };
    }

    /// <summary>Partial eraser (ANN-10 P1): cuts the brush area out of every annotation it touches.</summary>
    private void StartEraserPartial(Point p, double radius)
    {
        var before = _doc.Snapshot();
        bool changed = false;
        var last = p;
        var pen = new Pen(Brushes.Black, Math.Max(1, radius * 2)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        void Cut(Point a, Point b)
        {
            var hits = _doc.Items.Where(it => it.SupportsPartialErase && it.HitTestSegment(a, b, radius)).ToList();
            if (hits.Count == 0) return;
            var seg = Geo.Polyline(new[] { a, (b - a).Length < 0.01 ? b + new Vector(0.01, 0) : b });
            var area = seg.GetWidenedPathGeometry(pen, 0.25, ToleranceType.Absolute);
            area.Freeze();
            foreach (var it in hits)
            {
                it.AddEraseMask(area);
                it.Touch();
            }
            changed = true;
            SyncVisuals();
        }
        Cut(p, p);
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                if ((q - last).Length < 1) return;
                Cut(last, q);
                last = q;
            },
            Up = (q, m) =>
            {
                if (!changed) return;
                _doc.Push(before);
                RaiseChanged();
            },
            Cancel = () => _doc.Restore(before),
        };
    }

    private void StartEraserRect(Point p)
    {
        var start = p;
        _rubber = new Rect(p, p);
        _gesture = new Gesture
        {
            Move = (q, m) =>
            {
                var r = new Rect(start, ClampEdit(q));
                _rubber = r;
                _faded.Clear();
                foreach (var it in _doc.Items)
                    if (it.IntersectsRect(r)) _faded.Add(it.Id);
                SyncVisuals();
            },
            Up = (q, m) =>
            {
                var ids = _faded.ToList();
                _faded.Clear();
                DeleteItems(ids);
            },
            Cancel = () => _faded.Clear(),
        };
    }

    // ------------------------------------------------------------------ cursor & overlay

    private bool ShowsBrushCursor
    {
        get
        {
            if (_tool is AnnotationTool.Pencil) return true;
            if (_tool is AnnotationTool.Highlighter or AnnotationTool.Mosaic or AnnotationTool.Eraser)
                return Options.Get(_tool).Mode != DrawMode.Rect;
            return false;
        }
    }

    private void UpdateCursor(Point? at = null)
    {
        if (_tool == AnnotationTool.None) { Cursor = null; return; }
        if (_gesture != null) return;
        var p = at ?? _mouse;
        if (Selected is { } sel && HitHandle(sel, p) is { } h)
        {
            Cursor = h.Kind switch
            {
                HandleKind.ResizeNWSE => Cursors.SizeNWSE,
                HandleKind.ResizeNESW => Cursors.SizeNESW,
                HandleKind.ResizeNS => Cursors.SizeNS,
                HandleKind.ResizeWE => Cursors.SizeWE,
                _ => Cursors.Hand,
            };
            return;
        }
        if (_tool != AnnotationTool.Eraser && HitItem(p, HitTolerance()) is { } hit)
        {
            Cursor = hit is TextItem && _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.SizeAll;
            return;
        }
        Cursor = _tool switch
        {
            AnnotationTool.Text => Cursors.IBeam,
            AnnotationTool.Watermark => Cursors.Arrow,
            _ when !InEdit(p) => Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    private Color AccentColor() => TryFindResource("Color.Accent") is Color c ? c : Color.FromRgb(0x16, 0x77, 0xFF);

    /// <summary>Selection frame, handles, rubber band and brush cursor (constant screen size).</summary>
    internal void RedrawOverlay()
    {
        double s = ScreenScale();
        double px = 1 / s;
        var accent = new SolidColorBrush(AccentColor());
        accent.Freeze();
        using var dc = _overlay.Open();
        var sel = Selected;
        if (sel != null && !ReferenceEquals(sel, _editingItem))
        {
            if (sel.ShowSelectionFrame)
            {
                var frame = new Pen(accent, px) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
                frame.Freeze();
                var b = sel.Bounds;
                dc.DrawRectangle(null, new Pen(Brushes.White, px), b);
                dc.DrawRectangle(null, frame, b);
            }
            double r = HandleRadius();
            var hpen = new Pen(accent, 1.5 * px);
            hpen.Freeze();
            foreach (var h in sel.GetHandles())
            {
                if (h.Kind == HandleKind.Rotate)
                {
                    dc.DrawEllipse(accent, new Pen(Brushes.White, 1.5 * px), h.Position, r, r);
                }
                else dc.DrawEllipse(Brushes.White, hpen, h.Position, r, r);
            }
        }
        if (_rubber is { } rr)
        {
            var fill = new SolidColorBrush(Color.FromArgb(0x22, AccentColor().R, AccentColor().G, AccentColor().B));
            var pen = new Pen(accent, px) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
            dc.DrawRectangle(fill, pen, rr);
        }
        if (_mouseOver && _gesture == null && _editor == null && ShowsBrushCursor && InEdit(_mouse) && _polyline == null)
        {
            double radius = Math.Max(1, Options.Get(_tool).Size / 2);
            dc.DrawEllipse(null, new Pen(Brushes.White, px * 1.5), _mouse, radius + px, radius + px);
            dc.DrawEllipse(null, new Pen(Brushes.Black, px), _mouse, radius, radius);
        }
        else if (_gesture != null && ShowsBrushCursor && _tool == AnnotationTool.Eraser)
        {
            double radius = Math.Max(1, Options.Get(_tool).Size / 2);
            dc.DrawEllipse(null, new Pen(Brushes.Black, px), _mouse, radius, radius);
        }
    }
}