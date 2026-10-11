using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Annotation;

/// <summary>Annotation tools. Shortcut keys (Shift+1..0) follow docs/SPEC.md.</summary>
public enum AnnotationTool
{
    /// <summary>No tool: the surface is NOT hit-testable, the host handles the mouse (move/resize selection, drag pin…).</summary>
    None,
    Rectangle,
    Ellipse,
    /// <summary>Straight line / polyline.</summary>
    Line,
    Arrow,
    /// <summary>Numbered markers 1,2,3… / A,B,C… / I,II,III…</summary>
    Serial,
    Pencil,
    Highlighter,
    /// <summary>Mosaic / blur (brush or rectangle).</summary>
    Mosaic,
    Text,
    Eraser,
    Spotlight,
    Watermark,
    Magnifier,
}

/// <summary>A button the HOST adds to the right end of the annotation toolbar (pin, save, copy, OCR, close…).</summary>
public sealed class ToolbarAction
{
    public ToolbarAction(string id, string glyph, string toolTip, Action execute)
    {
        Id = id; Glyph = glyph; ToolTip = toolTip; Execute = execute;
    }

    public string Id { get; }
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph, e.g. "" (pin).</summary>
    public string Glyph { get; }
    public string ToolTip { get; }
    public Action Execute { get; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// ANNOTATION MODULE CONTRACT — editing surface shared by the capture overlay and image pins.
/// <para>
/// Coordinate space: 1 surface unit == 1 pixel of <see cref="BaseImage"/>. The host sizes nothing: the surface sets
/// its own Width/Height to the image's pixel size. The host applies any zoom/DPI transform on a parent element;
/// mouse input therefore arrives already in image pixels.
/// </para>
/// <para>
/// The surface draws ONLY the annotations (transparent background). The host draws the base image underneath.
/// Effect tools (mosaic, blur, spotlight, magnifier) sample <see cref="BaseImage"/> themselves.
/// </para>
/// Implementation is split over the partial files AnnotationSurface.*.cs.
/// </summary>
public partial class AnnotationSurface : Canvas
{
    private AnnotationTool _tool;
    private Rect? _editBounds;
    private readonly RenderContext _ctx;
    private readonly AnnotationDocument _doc = new();
    private readonly AnnotationLayer _layer = new();
    private readonly OverlayLayer _overlay = new();

    public AnnotationSurface(BitmapSource baseImage)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        _ctx = new RenderContext(baseImage);
        Width = baseImage.PixelWidth;
        Height = baseImage.PixelHeight;
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        IsHitTestVisible = false;
        ClipToBounds = false;
        // Grayscale AA on screen == RenderTargetBitmap output (ANN-16).
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _layer.Width = Width; _layer.Height = Height;
        _overlay.Width = Width; _overlay.Height = Height;
        Children.Add(_layer);
        Children.Add(_overlay);

        Options = new ToolOptionsStore();
        Options.Changed += OnOptionsChanged;
    }

    /// <summary>The picture being annotated (image px == surface units). Host may replace it (e.g. pin rotated).</summary>
    public BitmapSource BaseImage
    {
        get => _ctx.BaseImage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _ctx.BaseImage)) return;
            _ctx.SetBaseImage(value);
            Width = value.PixelWidth;
            Height = value.PixelHeight;
            _layer.Width = Width; _layer.Height = Height;
            _overlay.Width = Width; _overlay.Height = Height;
            UpdateContentBounds();
            SyncVisuals();
        }
    }

    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitPendingEdits();
            CancelGesture();
            _tool = value;
            IsHitTestVisible = value != AnnotationTool.None;
            if (value is AnnotationTool.Rectangle or AnnotationTool.Ellipse) Options.LastGeometryTool = value;
            if (value != AnnotationTool.None) ToolOptionsStore.Settings.ToolState[LastToolKey] = value.ToString();
            if (Selected is { } s && !s.MatchesTool(value)) Select(null);
            if (value == AnnotationTool.Watermark) EnsureWatermark();
            UpdateCursor();
            RedrawOverlay();
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Optional limit (image px) for drawing — the capture overlay sets it to the current selection so nothing can be
    /// drawn outside. null = whole image. Annotations are clipped to it at render time (it may change later).
    /// </summary>
    public Rect? EditBounds
    {
        get => _editBounds;
        set
        {
            if (_editBounds == value) return;
            _editBounds = value;
            _layer.Clip = value is { } r ? new RectangleGeometry(r) : null;
            UpdateContentBounds();
            SyncVisuals();
            RedrawOverlay();
        }
    }

    /// <summary>
    /// Optional: given a point (image px) return a UI-element/window rectangle (image px) to snap to.
    /// Used for "right-click to fit element" with rectangle / mosaic / eraser tools.
    /// </summary>
    public Func<Point, Rect?>? SnapRectProvider { get; set; }

    /// <summary>True if at least one annotation exists.</summary>
    public bool HasAnnotations => _doc.Items.Count > 0;

    /// <summary>True while a gesture or text edit is in progress — the host should not treat Esc/Enter as "close/copy".</summary>
    public bool IsBusy => _gesture != null || _polyline != null || _editor != null;

    /// <summary>
    /// ADDED: true while the inline text editor is open. Hosts must not interpret plain letter keys
    /// (e.g. capture's "C"/"R" shortcuts) while this is true; <see cref="HandleKeyDown"/> already returns false for them.
    /// </summary>
    public bool IsEditingText => _editor != null;

    /// <summary>ADDED: true when an annotation is selected for secondary editing (Delete / arrow keys act on it).</summary>
    public bool HasSelection => Selected != null;

    public bool CanUndo => _doc.CanUndo || _gesture != null || _polyline != null;
    public bool CanRedo => _doc.CanRedo;

    public void Undo()
    {
        if (_gesture != null || _polyline != null) { CancelGesture(); return; }
        CommitPendingEdits();
        if (!_doc.Undo()) return;
        AfterHistoryChange();
    }

    public void Redo()
    {
        CancelGesture();
        CommitPendingEdits();
        if (!_doc.Redo()) return;
        AfterHistoryChange();
    }

    /// <summary>Remove every annotation (undoable).</summary>
    public void ClearAll()
    {
        CancelGesture();
        CommitPendingEdits();
        if (_doc.Items.Count == 0) return;
        _doc.Checkpoint();
        _doc.Items.Clear();
        _serialOverride = null;
        Select(null);
        SyncVisuals();
        RaiseChanged();
    }

    /// <summary>Raised whenever annotations change (add/edit/delete/undo/redo).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="Tool"/> changes (toolbar ↔ host sync).</summary>
    public event EventHandler? ToolChanged;

    /// <summary>
    /// Host forwards KeyDown here first. Handles Ctrl+Z / Ctrl+Y / Delete / Shift+1..0 / Esc (cancel current edit) …
    /// Returns true if the key was consumed.
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        bool handled = HandleKeyCore(e);
        if (handled) e.Handled = true;
        return handled;
    }

    /// <summary>Finish any in-progress edit (e.g. open text box). Call before <see cref="Render"/>.</summary>
    public void CommitPendingEdits()
    {
        CommitText();
        if (_polyline != null) FinishPolyline();
    }

    /// <summary>Base image + annotations flattened (Bgra32, 96 DPI). <paramref name="cropPx"/> in image px.</summary>
    public BitmapSource Render(Int32Rect? cropPx = null)
    {
        CommitPendingEdits();
        return RenderCore(cropPx);
    }

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private const string LastToolKey = "LastTool";

    /// <summary>
    /// ADDED: the tool a new capture / pin annotation session should start with — the last used tool when
    /// <see cref="AnnotationSettings.RememberLastToolInCapture"/> / <see cref="AnnotationSettings.RememberLastToolInPin"/>
    /// is on, otherwise <see cref="AnnotationTool.None"/>.
    /// </summary>
    public static AnnotationTool GetInitialTool(bool forPin)
    {
        var s = ToolOptionsStore.Settings;
        bool remember = forPin ? s.RememberLastToolInPin : s.RememberLastToolInCapture;
        if (!remember) return AnnotationTool.None;
        return s.ToolState.TryGetValue(LastToolKey, out var v) && Enum.TryParse<AnnotationTool>(v, out var t) && t != AnnotationTool.Watermark
            ? t : AnnotationTool.None;
    }

    // ------------------------------------------------------------------ internals shared by the partial files

    /// <summary>Per-tool style options (toolbar reads / writes them).</summary>
    internal ToolOptionsStore Options { get; }

    internal AnnotationDocument Document => _doc;

    internal RenderContext RenderContext => _ctx;

    /// <summary>Raised when the selected item changes (toolbar refreshes its options row).</summary>
    internal event EventHandler? SelectionChanged;

    /// <summary>Raised when "next serial number" changes.</summary>
    internal event EventHandler? SerialChanged;

    private void UpdateContentBounds()
    {
        var img = _ctx.ImageRect;
        if (_editBounds is { } eb)
        {
            var r = Rect.Intersect(eb, img);
            _ctx.ContentBounds = r.IsEmpty ? new Rect(0, 0, 0, 0) : r;
        }
        else _ctx.ContentBounds = img;
    }

    private void AfterHistoryChange()
    {
        // Keep the selection if the item still exists in the restored state.
        if (_selectedId is { } id && _doc.Find(id) == null) Select(null);
        SyncVisuals();
        RedrawOverlay();
        SerialChanged?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    /// <summary>Re-render stale visuals (cheap: only changed items are redrawn).</summary>
    internal void SyncVisuals()
    {
        _layer.Sync(_doc.Items, _preview, _ctx, _faded.Count > 0 ? _faded : null, null);
    }
}
