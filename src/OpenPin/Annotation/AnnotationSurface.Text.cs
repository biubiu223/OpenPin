using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenPin.Annotation.Common;
using OpenPin.Annotation.Model.Items;

namespace OpenPin.Annotation;

public partial class AnnotationSurface
{
    /// <summary>
    /// Offset of the TextBox's text relative to its top-left with the chrome-less template below. The text item is
    /// rendered by the surface (WYSIWYG, same as export); the TextBox only provides caret, selection and IME, so its
    /// glyph origin must coincide with <see cref="TextItem.Position"/>.
    /// </summary>
    internal static readonly Vector EditorTextOffset = new(2, 0);

    private static ControlTemplate? s_editorTemplate;

    private TextBox? _editor;
    private TextItem? _editingItem;
    private List<Model.AnnotationItem>? _editBefore;
    private long _editStartVersion;
    private bool _editingNew;

    private static ControlTemplate EditorTemplate
    {
        get
        {
            if (s_editorTemplate != null) return s_editorTemplate;
            var tpl = new ControlTemplate(typeof(TextBox));
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.PaddingProperty, new Thickness(0));
            host.SetValue(FocusableProperty, false);
            tpl.VisualTree = host;
            tpl.Seal();
            return s_editorTemplate = tpl;
        }
    }

    /// <summary>Text tool click on empty space: new text with the caret at the click point.</summary>
    private void BeginNewText(Point p, Color? color = null)
    {
        var item = new TextItem();
        item.ApplyOptions(Options.Get(AnnotationTool.Text));
        if (color is { } c) item.Color = c;
        double lineH = item.Layout.Height;
        var pos = new Point(Math.Round(p.X), Math.Round(p.Y - lineH / 2));
        var area = EditRect;
        pos = new Point(Math.Clamp(pos.X, area.Left, Math.Max(area.Left, area.Right - 4)),
                        Math.Clamp(pos.Y, area.Top, Math.Max(area.Top, area.Bottom - lineH)));
        item.Position = pos;
        _preview = item;
        SyncVisuals();
        OpenEditor(item, isNew: true);
    }

    /// <summary>Edit an existing text item in place (ANN-09 secondary edit).</summary>
    internal void BeginEditText(TextItem item, bool isNew)
    {
        if (_editor != null || _gesture != null) return;
        if (!_doc.Items.Contains(item)) return;
        _editBefore = _doc.Snapshot();
        // Reflect the item's style in the toolbar without restyling it.
        var o = Options.Get(AnnotationTool.Text);
        item.ReadOptions(o);
        _suppressApply = true;
        try { Options.Commit(AnnotationTool.Text); }
        finally { _suppressApply = false; }
        OpenEditor(item, isNew);
    }

    /// <summary>Double-click on an arrow: add a note at its tail (ANN-04 P1).</summary>
    private void BeginArrowNote(LineItem arrow)
    {
        if (arrow.Points.Count < 2) return;
        var tail = arrow.Points[0];
        var dir = arrow.Points[0] - arrow.Points[^1];
        if (dir.Length > 0.1) dir.Normalize();
        var o = Options.Get(AnnotationTool.Text);
        // Text starts a bit beyond the tail, on the side the arrow comes from.
        var p = tail + dir * (o.Size * 0.6 + 4);
        if (dir.X < -0.3) p.X -= o.Size * 3; // arrow coming from the left: start text further left
        BeginNewText(p, arrow.Color);
    }

    /// <summary>"A" on a selected serial marker: add description text next to it (ANN-05 P1).</summary>
    private void BeginSerialNote(SerialItem s)
    {
        Select(null);
        BeginNewText(new Point(s.Center.X + s.Size / 2 + 6, s.Center.Y), s.Color);
    }

    private void OpenEditor(TextItem item, bool isNew)
    {
        Select(null);
        _editingItem = item;
        _editingNew = isNew;
        _editStartVersion = item.Version;
        var tb = new TextBox
        {
            Template = EditorTemplate,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            // The surface renders the glyphs; the box only shows caret + selection.
            Foreground = Brushes.Transparent,
            SelectionOpacity = 0.35,
            MinWidth = 2,
            Text = item.Text,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam,
        };
        if (TryFindResource("Brush.Accent") is Brush accent) tb.SelectionBrush = accent;
        tb.TextChanged += (_, _) =>
        {
            if (_editingItem != item) return;
            item.Text = tb.Text;
            item.Touch();
            SyncVisuals();
            UpdateEditorStyle();
        };
        _editor = tb;
        UpdateEditorStyle();
        Children.Add(tb);
        tb.CaretIndex = tb.Text.Length;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_editor != tb) return;
            tb.Focus();
            Keyboard.Focus(tb);
        });
        RedrawOverlay();
        UpdateCursor();
    }

    /// <summary>Sync the inline TextBox with the edited item's font, size, color and position.</summary>
    private void UpdateEditorStyle()
    {
        if (_editor is not { } tb || _editingItem is not { } item) return;
        tb.FontFamily = new FontFamily(item.FontFamily);
        tb.FontSize = Math.Max(1, item.FontSize);
        tb.FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal;
        tb.FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal;
        var caret = Color.FromRgb(item.Color.R, item.Color.G, item.Color.B);
        tb.CaretBrush = new SolidColorBrush(caret);
        SetLeft(tb, item.Position.X - EditorTextOffset.X);
        SetTop(tb, item.Position.Y - EditorTextOffset.Y);
        tb.RenderTransformOrigin = new Point(0.5, 0.5);
        tb.RenderTransform = item.Angle != 0 ? new RotateTransform(item.Angle) : Transform.Identity;
    }

    /// <summary>Close the inline editor and record the result (empty text removes the item).</summary>
    private void CommitText()
    {
        if (_editor is not { } tb || _editingItem is not { } item) return;
        bool hadFocus = tb.IsKeyboardFocusWithin;
        _editor = null;
        _editingItem = null;
        Children.Remove(tb);
        var finalText = tb.Text.TrimEnd('\r', '\n');
        if (item.Text != finalText)
        {
            item.Text = finalText;
            item.Touch();
        }
        bool empty = string.IsNullOrWhiteSpace(item.Text);
        if (_editingNew)
        {
            if (empty) DiscardPreview();
            else CommitPreview(select: false);
        }
        else if (_editBefore != null)
        {
            if (empty)
            {
                _doc.Push(_editBefore);
                _doc.Items.Remove(item);
                SyncVisuals();
                RaiseChanged();
            }
            else if (item.Version != _editStartVersion)
            {
                _doc.Push(_editBefore);
                SyncVisuals();
                RaiseChanged();
            }
        }
        _editBefore = null;
        if (hadFocus) Focus();
        RedrawOverlay();
        UpdateCursor();
    }

    /// <summary>Is keyboard focus in some OTHER text input (toolbar watermark text, color hex…)?</summary>
    private bool ForeignTextInputFocused()
    {
        var f = Keyboard.FocusedElement as DependencyObject;
        if (f == null || ReferenceEquals(f, _editor)) return false;
        return f is TextBoxBase or PasswordBox || (f is ComboBox cb && cb.IsEditable);
    }
}
