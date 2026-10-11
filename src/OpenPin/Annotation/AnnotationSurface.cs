using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

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
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph, e.g. "" (pin).</summary>
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
/// PHASE-0 STUB: public API is final; the Annotation module implements the internals.
/// </summary>
public class AnnotationSurface : Canvas
{
    private AnnotationTool _tool;

    public AnnotationSurface(BitmapSource baseImage)
    {
        BaseImage = baseImage;
        Width = baseImage.PixelWidth;
        Height = baseImage.PixelHeight;
        Background = Brushes.Transparent;
        Focusable = true;
        IsHitTestVisible = false;
    }

    /// <summary>The picture being annotated (image px == surface units). Host may replace it (e.g. pin rotated).</summary>
    public BitmapSource BaseImage { get; set; }

    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            IsHitTestVisible = value != AnnotationTool.None;
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Optional limit (image px) for drawing — the capture overlay sets it to the current selection so nothing can be
    /// drawn outside. null = whole image.
    /// </summary>
    public Rect? EditBounds { get; set; }

    /// <summary>
    /// Optional: given a point (image px) return a UI-element/window rectangle (image px) to snap to.
    /// Used for "right-click to fit element" with rectangle / mosaic / eraser tools.
    /// </summary>
    public Func<Point, Rect?>? SnapRectProvider { get; set; }

    /// <summary>True if at least one annotation exists.</summary>
    public bool HasAnnotations => false;

    /// <summary>True while a gesture or text edit is in progress — the host should not treat Esc/Enter as "close/copy".</summary>
    public bool IsBusy => false;

    public bool CanUndo => false;
    public bool CanRedo => false;
    public void Undo() { }
    public void Redo() { }
    public void ClearAll() { }

    /// <summary>Raised whenever annotations change (add/edit/delete/undo/redo).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="Tool"/> changes (toolbar ↔ host sync).</summary>
    public event EventHandler? ToolChanged;

    /// <summary>
    /// Host forwards KeyDown here first. Handles Ctrl+Z / Ctrl+Y / Delete / Shift+1..0 / Esc (cancel current edit) …
    /// Returns true if the key was consumed.
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e) => false;

    /// <summary>Finish any in-progress edit (e.g. open text box). Call before <see cref="Render"/>.</summary>
    public void CommitPendingEdits() { }

    /// <summary>Base image + annotations flattened (Bgra32, 96 DPI). <paramref name="cropPx"/> in image px.</summary>
    public BitmapSource Render(Int32Rect? cropPx = null)
    {
        CommitPendingEdits();
        var full = BaseImage;
        return cropPx is { } c ? BitmapUtil.Crop(full, c) : BitmapUtil.ToBgra32(full);
    }

    // Keep the compiler quiet about the unused events in the stub.
    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// ANNOTATION MODULE CONTRACT — toolbar bound to a surface: tool buttons, per-tool options (color, size, styles…),
/// undo/redo, then the host's <see cref="HostActions"/>. The host only positions it.
/// PHASE-0 STUB.
/// </summary>
public class AnnotationToolbar : UserControl
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };

    public AnnotationToolbar()
    {
        Content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = _panel,
        };
        HostActions.CollectionChanged += (_, _) => Rebuild();
    }

    public AnnotationSurface? Surface { get; set; }

    /// <summary>Buttons supplied by the host, shown at the right end.</summary>
    public ObservableCollection<ToolbarAction> HostActions { get; } = new();

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var a in HostActions)
        {
            var b = new Button
            {
                Content = a.Glyph,
                ToolTip = a.ToolTip,
                IsEnabled = a.IsEnabled,
                Style = TryFindResource("Style.IconButton") as Style,
            };
            b.Click += (_, _) => a.Execute();
            _panel.Children.Add(b);
        }
    }
}
