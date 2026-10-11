using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Toolbar;
using OpenPin.Core;

namespace OpenPin.Annotation;

/// <summary>
/// ANNOTATION MODULE CONTRACT — toolbar bound to a surface: tool buttons (primary area + "›" secondary area),
/// undo/redo, then the host's <see cref="HostActions"/>; below (or above, see <see cref="OptionsAbove"/>) a per-tool
/// options row (colors, size, styles…) that appears while a tool is active. The host only positions it and should
/// listen to SizeChanged (the options row changes the size).
/// </summary>
public class AnnotationToolbar : UserControl
{
    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical };
    private readonly Border _mainBorder = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _optionsBorder = new() { HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly StackPanel _main = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _options = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _secondary = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly StackPanel _host = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<AnnotationTool, ToggleButton> _toolButtons = new();
    private readonly ToggleButton _more = new();
    private readonly Button _undo = new();
    private readonly Button _redo = new();
    private readonly ContentControl _geometryIcon = new() { Focusable = false };
    private AnnotationSurface? _surface;
    private bool _fromUi;
    private bool _optionsAbove;
    private Popup? _openPopup;

    private static readonly AnnotationTool[] Primary =
    {
        AnnotationTool.Rectangle, AnnotationTool.Line, AnnotationTool.Arrow, AnnotationTool.Serial, AnnotationTool.Pencil,
        AnnotationTool.Highlighter, AnnotationTool.Mosaic, AnnotationTool.Text, AnnotationTool.Eraser, AnnotationTool.Spotlight,
    };

    private static readonly AnnotationTool[] Secondary = { AnnotationTool.Watermark, AnnotationTool.Magnifier };

    public AnnotationToolbar()
    {
        Focusable = false;
        _mainBorder.SetResourceReference(StyleProperty, "Style.ToolbarBorder");
        _optionsBorder.SetResourceReference(StyleProperty, "Style.ToolbarBorder");
        _mainBorder.Child = _main;
        _optionsBorder.Child = _options;
        _root.Children.Add(_mainBorder);
        _root.Children.Add(_optionsBorder);
        Content = _root;
        BuildMain();
        HostActions.CollectionChanged += (_, _) => RebuildHostActions();
        Refresh();
    }

    public AnnotationSurface? Surface
    {
        get => _surface;
        set
        {
            if (ReferenceEquals(_surface, value)) return;
            if (_surface != null)
            {
                _surface.ToolChanged -= OnSurfaceStateChanged;
                _surface.Changed -= OnSurfaceChanged;
                _surface.SelectionChanged -= OnSurfaceStateChanged;
                _surface.SerialChanged -= OnSurfaceStateChanged;
                _surface.Options.Changed -= OnOptionsChanged;
            }
            _surface = value;
            if (_surface != null)
            {
                _surface.ToolChanged += OnSurfaceStateChanged;
                _surface.Changed += OnSurfaceChanged;
                _surface.SelectionChanged += OnSurfaceStateChanged;
                _surface.SerialChanged += OnSurfaceStateChanged;
                _surface.Options.Changed += OnOptionsChanged;
            }
            Refresh();
        }
    }

    /// <summary>Buttons supplied by the host, shown at the right end.</summary>
    public ObservableCollection<ToolbarAction> HostActions { get; } = new();

    /// <summary>
    /// ADDED: put the options row ABOVE the tool row (use when the toolbar is placed above the selection / pin so the
    /// options grow away from it). Default false = below.
    /// </summary>
    public bool OptionsAbove
    {
        get => _optionsAbove;
        set
        {
            if (_optionsAbove == value) return;
            _optionsAbove = value;
            _root.Children.Clear();
            if (value) { _root.Children.Add(_optionsBorder); _root.Children.Add(_mainBorder); }
            else { _root.Children.Add(_mainBorder); _root.Children.Add(_optionsBorder); }
            _optionsBorder.Margin = value ? new Thickness(0, 0, 0, 6) : new Thickness(0, 6, 0, 0);
        }
    }

    /// <summary>ADDED: re-read <see cref="ToolbarAction.IsEnabled"/> of the host actions (it has no change notification).</summary>
    public void RefreshHostActions() => RebuildHostActions();

    // ------------------------------------------------------------------ main row

    private void BuildMain()
    {
        _optionsBorder.Margin = new Thickness(0, 6, 0, 0);
        foreach (var tool in Primary) _main.Children.Add(MakeToolButton(tool));

        _more.SetResourceReference(StyleProperty, "Style.IconButton");
        _more.Content = Icons.More;
        _more.SetResourceReference(ToolTipProperty, "Annotation.More");
        _more.Click += (_, _) => SetSecondaryVisible(_more.IsChecked == true);
        _main.Children.Add(_more);
        foreach (var tool in Secondary) _secondary.Children.Add(MakeToolButton(tool));
        _main.Children.Add(_secondary);

        _main.Children.Add(Separator());
        _undo.SetResourceReference(StyleProperty, "Style.IconButton");
        _undo.Content = Icons.Undo;
        _undo.SetResourceReference(ToolTipProperty, "Annotation.Undo");
        _undo.Click += (_, _) => _surface?.Undo();
        _redo.SetResourceReference(StyleProperty, "Style.IconButton");
        _redo.Content = Icons.Redo;
        _redo.SetResourceReference(ToolTipProperty, "Annotation.Redo");
        _redo.Click += (_, _) => _surface?.Redo();
        _main.Children.Add(_undo);
        _main.Children.Add(_redo);
        _main.Children.Add(_host);
    }

    private void SetSecondaryVisible(bool visible)
    {
        _secondary.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _more.IsChecked = visible;
        _more.Content = visible ? Icons.Less : Icons.More;
        _more.SetResourceReference(ToolTipProperty, visible ? "Annotation.Less" : "Annotation.More");
    }

    private ToggleButton MakeToolButton(AnnotationTool tool)
    {
        var b = new ToggleButton();
        b.SetResourceReference(StyleProperty, "Style.IconButton");
        b.Content = tool switch
        {
            AnnotationTool.Rectangle => _geometryIcon,
            AnnotationTool.Line => Icons.Line(),
            AnnotationTool.Arrow => Icons.Arrow(),
            AnnotationTool.Serial => Icons.Serial(),
            AnnotationTool.Pencil => Icons.Pencil,
            AnnotationTool.Highlighter => Icons.Highlighter,
            AnnotationTool.Mosaic => Icons.Mosaic(),
            AnnotationTool.Text => Icons.Text,
            AnnotationTool.Eraser => Icons.Eraser,
            AnnotationTool.Spotlight => Icons.Spotlight(),
            AnnotationTool.Watermark => Icons.Watermark,
            _ => Icons.Magnifier,
        };
        b.SetResourceReference(ToolTipProperty, "Annotation.Tool." + tool);
        b.Click += (_, _) =>
        {
            var s = _surface;
            if (s == null) { b.IsChecked = false; return; }
            bool active = tool == AnnotationTool.Rectangle
                ? s.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
                : s.Tool == tool;
            s.Tool = active ? AnnotationTool.None
                : tool == AnnotationTool.Rectangle ? s.Options.LastGeometryTool : tool;
            RefreshTools();
        };
        _toolButtons[tool] = b;
        return b;
    }

    private static Rectangle Separator()
    {
        var r = new Rectangle();
        r.SetResourceReference(StyleProperty, "Style.ToolbarSeparator");
        return r;
    }

    private void RebuildHostActions()
    {
        _host.Children.Clear();
        if (HostActions.Count == 0) return;
        _host.Children.Add(Separator());
        foreach (var a in HostActions)
        {
            var b = new Button
            {
                Content = a.Glyph,
                ToolTip = a.ToolTip,
                IsEnabled = a.IsEnabled,
            };
            b.SetResourceReference(StyleProperty, "Style.IconButton");
            var action = a;
            b.Click += (_, _) =>
            {
                try { action.Execute(); }
                catch (Exception ex) { Log.Error($"Toolbar action '{action.Id}' failed", ex); }
            };
            _host.Children.Add(b);
        }
    }

    // ------------------------------------------------------------------ state sync

    private void OnSurfaceStateChanged(object? sender, EventArgs e)
    {
        if (_fromUi) return;
        Refresh();
    }

    private void OnSurfaceChanged(object? sender, EventArgs e)
    {
        RefreshUndo();
        if (_fromUi) return;
        // Only the watermark row depends on document content (add/remove state).
        if (_surface?.Tool == AnnotationTool.Watermark) RebuildOptions();
    }

    private void OnOptionsChanged(AnnotationTool tool)
    {
        if (_fromUi) return;
        RebuildOptions();
    }

    private void Refresh()
    {
        RefreshTools();
        RefreshUndo();
        RebuildOptions();
    }

    private void RefreshUndo()
    {
        _undo.IsEnabled = _surface?.CanUndo == true;
        _redo.IsEnabled = _surface?.CanRedo == true;
    }

    private void RefreshTools()
    {
        var tool = _surface?.Tool ?? AnnotationTool.None;
        foreach (var (t, b) in _toolButtons)
        {
            b.IsChecked = t == AnnotationTool.Rectangle ? tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse : t == tool;
            b.IsEnabled = _surface != null;
        }
        var geo = tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse ? tool : _surface?.Options.LastGeometryTool ?? AnnotationTool.Rectangle;
        _geometryIcon.Content = geo == AnnotationTool.Ellipse ? Icons.Ellipse : Icons.Rectangle;
        if (Secondary.Contains(tool)) SetSecondaryVisible(true);
    }

    /// <summary>Apply an option change made in the toolbar. <paramref name="rebuild"/>=false while dragging / typing.</summary>
    private void Commit(AnnotationTool tool, bool rebuild = true)
    {
        if (_surface == null) return;
        _fromUi = true;
        try { _surface.Options.Commit(tool); }
        catch (Exception ex) { Log.Error("Applying annotation option failed", ex); }
        finally { _fromUi = false; }
        RefreshUndo();
        if (rebuild) RebuildOptions();
    }

    // ------------------------------------------------------------------ options row

    private void RebuildOptions()
    {
        if (_openPopup is { IsOpen: true }) return; // rebuilt when the flyout closes
        _options.Children.Clear();
        var s = _surface;
        if (s == null || s.Tool == AnnotationTool.None)
        {
            _optionsBorder.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            BuildOptions(s, s.Tool);
        }
        catch (Exception ex)
        {
            Log.Error("Building annotation options failed", ex);
        }
        _optionsBorder.Visibility = _options.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildOptions(AnnotationSurface s, AnnotationTool tool)
    {
        var o = s.Options.Get(tool);
        switch (tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                Add(Segmented(
                    (Icons.Rectangle, "Annotation.Shape.Rectangle", tool == AnnotationTool.Rectangle, () => s.Tool = AnnotationTool.Rectangle),
                    (Icons.Ellipse, "Annotation.Shape.Ellipse", tool == AnnotationTool.Ellipse, () => s.Tool = AnnotationTool.Ellipse)));
                if (tool == AnnotationTool.Ellipse)
                {
                    AddSep();
                    Add(Segmented(
                        (Icons.Ellipse, "Annotation.Shape.Ellipse", o.EllipseVariant == EllipseVariant.Full, () => { o.EllipseVariant = EllipseVariant.Full; Commit(tool); }),
                        (Icons.Sector(), "Annotation.Shape.Sector", o.EllipseVariant == EllipseVariant.Sector, () => { o.EllipseVariant = EllipseVariant.Sector; Commit(tool); }),
                        (Icons.Arc(), "Annotation.Shape.Arc", o.EllipseVariant == EllipseVariant.Arc, () => { o.EllipseVariant = EllipseVariant.Arc; Commit(tool); })));
                }
                AddSep();
                Add(Toggle(Icons.Fill(), "Annotation.Fill", o.Fill, v => { o.Fill = v; Commit(tool); }));
                Add(DashChoice(o, tool));
                if (tool == AnnotationTool.Rectangle)
                    Add(Stepper("Annotation.CornerRadius", "\uE7FB", o.CornerRadius.ToString("0"), d => { o.CornerRadius = Math.Clamp(o.CornerRadius + d * 2, 0, 100); Commit(tool); }));
                AddSep();
                AddSize(s, tool, o);
                AddSep();
                AddPalette(o, tool);
                break;

            case AnnotationTool.Line:
                Add(DashChoice(o, tool));
                AddSep();
                Add(Button(Icons.LineEnd(o.StartEnd, start: true), "Annotation.Line.Start", () => { o.StartEnd = NextEnd(o.StartEnd); Commit(tool); }));
                Add(Button(Icons.LineEnd(o.EndEnd, start: false), "Annotation.Line.End", () => { o.EndEnd = NextEnd(o.EndEnd); Commit(tool); }));
                Add(FlyoutButton(Icons.Settings, "Annotation.Line.Style", () => LineStyleFlyout(o, tool)));
                AddSep();
                AddSize(s, tool, o);
                AddSep();
                AddPalette(o, tool);
                break;

            case AnnotationTool.Arrow:
                Add(Segmented(Enum.GetValues<ArrowStyle>().Select(a =>
                    ((object)Icons.ArrowStyle(a), "Annotation.Arrow." + a, o.ArrowStyle == a, (Action)(() => { o.ArrowStyle = a; Commit(tool); }))).ToArray()));
                AddSep();
                AddSize(s, tool, o);
                AddSep();
                AddPalette(o, tool);
                break;

            case AnnotationTool.Serial:
                Add(Segmented(
                    (TextFace("1"), "Annotation.Serial.Number", o.SerialType == SerialType.Number, () => { o.SerialType = SerialType.Number; Commit(tool); }),
                    (TextFace("A"), "Annotation.Serial.Letter", o.SerialType == SerialType.Letter, () => { o.SerialType = SerialType.Letter; Commit(tool); }),
                    (TextFace("Ⅰ"), "Annotation.Serial.Roman", o.SerialType == SerialType.Roman, () => { o.SerialType = SerialType.Roman; Commit(tool); })));
                AddSep();
                Add(Label("Annotation.Serial.Next"));
                Add(Stepper("Annotation.Serial.Next", null, Model.SerialFormat.Format(s.NextSerialNumber, o.SerialType),
                    d => { s.NextSerialNumber = Math.Max(1, s.NextSerialNumber + d); RebuildOptions(); }));
                Add(Toggle(TextFace("↻"), "Annotation.Serial.Renumber", ToolOptionsStore.Settings.SerialRenumberOnDelete,
                    v => { ToolOptionsStore.Settings.SerialRenumberOnDelete = v; RebuildOptions(); }));
                AddSep();
                AddSize(s, tool, o);
                AddSep();
                AddPalette(o, tool);
                break;

            case AnnotationTool.Pencil:
                AddSize(s, tool, o);
                AddSep();
                AddPalette(o, tool);
                break;

            case AnnotationTool.Highlighter:
                AddModeChoice(o, tool);
                Add(Segmented(
                    (TextFace(Loc.Get("Annotation.Blend.Multiply")), "Annotation.Blend.Multiply", o.Blend == HighlightBlend.Multiply, () => { o.Blend = HighlightBlend.Multiply; Commit(tool); }),
                    (TextFace(Loc.Get("Annotation.Blend.Translucent")), "Annotation.Blend.Translucent", o.Blend == HighlightBlend.Translucent, () => { o.Blend = HighlightBlend.Translucent; Commit(tool); })));
                AddSep();
                if (o.Mode == DrawMode.Brush) { AddSize(s, tool, o); AddSep(); }
                AddPalette(o, tool);
                break;

            case AnnotationTool.Mosaic:
                AddModeChoice(o, tool);
                Add(Segmented(
                    (TextFace(Loc.Get("Annotation.Mosaic.Mosaic")), "Annotation.Mosaic.Mosaic", o.Effect == MosaicEffect.Mosaic, () => { o.Effect = MosaicEffect.Mosaic; Commit(tool); }),
                    (TextFace(Loc.Get("Annotation.Mosaic.Blur")), "Annotation.Mosaic.Blur", o.Effect == MosaicEffect.Blur, () => { o.Effect = MosaicEffect.Blur; Commit(tool); })));
                AddSep();
                Add(Label("Annotation.Mosaic.Strength"));
                Add(Slider(1, 10, o.Strength, 1, "Annotation.Mosaic.Strength", v => { o.Strength = (int)Math.Round(v); Commit(tool, rebuild: false); }));
                if (o.Mode == DrawMode.Brush) { AddSep(); AddSize(s, tool, o); }
                break;

            case AnnotationTool.Text:
                Add(FontCombo(o.FontFamily, f => { o.FontFamily = f; Commit(tool); }));
                AddSize(s, tool, o);
                AddSep();
                Add(Toggle(Icons.Bold, "Annotation.Text.Bold", o.Bold, v => { o.Bold = v; Commit(tool); }));
                Add(Toggle(Icons.Italic, "Annotation.Text.Italic", o.Italic, v => { o.Italic = v; Commit(tool); }));
                Add(Toggle(Icons.OutlineA(), "Annotation.Text.Outline", o.Outline, v => { o.Outline = v; Commit(tool); }));
                if (o.Outline)
                    Add(ColorDot(ToolOptions.Parse(o.OutlineColor, Colors.White), "Annotation.Text.OutlineColor", c => { o.OutlineColor = ToolOptions.ToText(c); Commit(tool, rebuild: false); }));
                Add(Toggle(Icons.BackgroundA(), "Annotation.Text.Background", o.Background, v => { o.Background = v; Commit(tool); }));
                if (o.Background)
                {
                    Add(ColorDot(ToolOptions.Parse(o.BackgroundColor, Colors.White), "Annotation.Text.BackgroundColor", c => { o.BackgroundColor = ToolOptions.ToText(c); Commit(tool, rebuild: false); }));
                    Add(FlyoutButton(Icons.Settings, "Annotation.Text.BackgroundSettings", () => TextBackgroundFlyout(o, tool)));
                }
                AddSep();
                AddPalette(o, tool);
                break;

            case AnnotationTool.Eraser:
                AddModeChoice(o, tool, partial: true);
                if (o.Mode != DrawMode.Rect) { AddSep(); AddSize(s, tool, o); }
                AddSep();
                Add(TextButton(Icons.Delete, Loc.Get("Annotation.Eraser.Clear"), "Annotation.Eraser.ClearAll", () => s.ClearAll()));
                break;

            case AnnotationTool.Spotlight:
                Add(Segmented(
                    (Icons.Rectangle, "Annotation.Shape.Rectangle", o.Shape == ShapeKind.Rectangle, () => { o.Shape = ShapeKind.Rectangle; Commit(tool); }),
                    (Icons.Ellipse, "Annotation.Shape.Ellipse", o.Shape == ShapeKind.Ellipse, () => { o.Shape = ShapeKind.Ellipse; Commit(tool); })));
                AddSep();
                Add(Label("Annotation.Watermark.Opacity")); Add(Slider(0.05, 0.95, o.Opacity, 0.05, "Annotation.Spotlight.Opacity", v => { o.Opacity = Math.Round(v, 2); Commit(tool, rebuild: false); }));
                break;

            case AnnotationTool.Watermark:
                Add(WatermarkText(o, tool));
                Add(LayoutCombo(o, tool));
                AddSize(s, tool, o);
                if (o.Layout == WatermarkLayout.Tile)
                    Add(Label("Annotation.Watermark.Spacing")); Add(Stepper("Annotation.Watermark.Spacing", null, o.Spacing.ToString("0"), d => { o.Spacing = Math.Clamp(o.Spacing + d * 10, 10, 400); Commit(tool); }));
                Add(Label("Annotation.Watermark.Opacity")); Add(Slider(0.05, 1, o.Opacity, 0.05, "Annotation.Watermark.Opacity", v => { o.Opacity = Math.Round(v, 2); Commit(tool, rebuild: false); }));
                Add(FontCombo(o.FontFamily, f => { o.FontFamily = f; Commit(tool); }));
                AddSep();
                AddPalette(o, tool);
                AddSep();
                if (s.HasWatermark) Add(Button(Icons.Delete, "Annotation.Watermark.Remove", () => s.RemoveWatermark()));
                else Add(Button(Icons.Plus, "Annotation.Watermark.Add", () => s.AddWatermark()));
                break;

            case AnnotationTool.Magnifier:
                Add(Segmented(
                    (Icons.Rectangle, "Annotation.Shape.Rectangle", o.Shape == ShapeKind.Rectangle, () => { o.Shape = ShapeKind.Rectangle; Commit(tool); }),
                    (Icons.Ellipse, "Annotation.Shape.Ellipse", o.Shape == ShapeKind.Ellipse, () => { o.Shape = ShapeKind.Ellipse; Commit(tool); })));
                AddSep();
                Add(Stepper("Annotation.Magnifier.Zoom", null, "×" + o.Zoom, d => { o.Zoom = Math.Clamp(o.Zoom + d, 1, 8); Commit(tool); }));
                Add(Stepper("Annotation.Magnifier.Border", "\uE739", o.Size.ToString("0"), d => { o.Size = Math.Clamp(o.Size + d, 0, 20); Commit(tool); }));
                Add(Segmented(Enum.GetValues<ConnectorStyle>().Select(c =>
                    ((object)Icons.Connector(c), "Annotation.Magnifier.Connector" + c, o.Connector == c, (Action)(() => { o.Connector = c; Commit(tool); }))).ToArray()));
                AddSep();
                AddPalette(o, tool);
                break;
        }
    }

    private static LineEnd NextEnd(LineEnd e) => (LineEnd)(((int)e + 1) % Enum.GetValues<LineEnd>().Length);

    private void Add(UIElement e) => _options.Children.Add(e);

    private void AddSep()
    {
        var r = new Rectangle();
        r.SetResourceReference(StyleProperty, "Style.Annotation.Separator");
        _options.Children.Add(r);
    }

    private void AddModeChoice(ToolOptions o, AnnotationTool tool, bool partial = false)
    {
        Add(Segmented(
            (Icons.Brush, "Annotation.Mode.Brush", o.Mode == DrawMode.Brush, () => { o.Mode = DrawMode.Brush; Commit(tool); }),
            (Icons.Rectangle, "Annotation.Mode.Rect", o.Mode == DrawMode.Rect, () => { o.Mode = DrawMode.Rect; Commit(tool); })));
        if (partial)
            ((Panel)_options.Children[^1]).Children.Add(MakeSegment(Icons.PartialErase(), "Annotation.Mode.Partial", o.Mode == DrawMode.Partial, () => { o.Mode = DrawMode.Partial; Commit(tool); }));
        AddSep();
    }

    private UIElement DashChoice(ToolOptions o, AnnotationTool tool) => Segmented(Enum.GetValues<LineDash>().Select(d =>
        ((object)Icons.Dash(d), "Annotation.Dash." + d, o.Dash == d, (Action)(() => { o.Dash = d; Commit(tool); }))).ToArray());

    private void AddSize(AnnotationSurface s, AnnotationTool tool, ToolOptions o)
    {
        var (min, max, step) = ToolOptions.SizeRange(tool);
        Add(Stepper("Annotation.Size", null, o.Size.ToString("0"), d =>
        {
            o.Size = Math.Clamp(o.Size + d * step, min, max);
            Commit(tool);
        }));
    }

    private void AddPalette(ToolOptions o, AnnotationTool tool)
    {
        var current = o.ColorValue;
        foreach (var text in ToolOptionsStore.Settings.Palette)
        {
            if (!Core.Imaging.ColorText.TryParse(text, out var c)) continue;
            var b = new ToggleButton { Background = new SolidColorBrush(c), ToolTip = text, IsChecked = SameColor(c, current) };
            b.SetResourceReference(StyleProperty, "Style.Annotation.Swatch");
            b.Click += (_, _) => { o.ColorValue = c; Commit(tool); };
            Add(b);
        }
        var custom = new Button { Content = Icons.CustomColor(current) };
        custom.SetResourceReference(StyleProperty, "Style.Annotation.OptionButton");
        custom.SetResourceReference(ToolTipProperty, "Annotation.Color.Custom");
        custom.Click += (_, _) => OpenColorFlyout(custom, o.ColorValue, c => { o.ColorValue = c; Commit(tool, rebuild: false); });
        Add(custom);
    }

    private static bool SameColor(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    // ------------------------------------------------------------------ control factories

    private UIElement Segmented(params (object Face, string TipKey, bool Checked, Action OnClick)[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (face, tip, isChecked, onClick) in items) p.Children.Add(MakeSegment(face, tip, isChecked, onClick));
        return p;
    }

    private static ToggleButton MakeSegment(object face, string tip, bool isChecked, Action onClick)
    {
        var b = new ToggleButton { Content = face, IsChecked = isChecked };
        b.SetResourceReference(StyleProperty, face is TextBlock || (face is string s && !IsGlyph(s)) ? "Style.Annotation.TextButton" : "Style.Annotation.OptionButton");
        b.SetResourceReference(ToolTipProperty, tip);
        b.Click += (_, _) => { b.IsChecked = true; onClick(); };
        return b;
    }

    private static bool IsGlyph(string s) => s.Length == 1 && s[0] >= '\uE000' && s[0] <= '\uF8FF';

    private static TextBlock TextFace(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        return t;
    }

    private UIElement Toggle(object face, string tipKey, bool isChecked, Action<bool> onChange)
    {
        var b = new ToggleButton { Content = face, IsChecked = isChecked };
        b.SetResourceReference(StyleProperty, face is TextBlock ? "Style.Annotation.TextButton" : "Style.Annotation.OptionButton");
        b.SetResourceReference(ToolTipProperty, tipKey);
        b.Click += (_, _) => onChange(b.IsChecked == true);
        return b;
    }

    private UIElement Button(object face, string tipKey, Action onClick)
    {
        var b = new Button { Content = face };
        b.SetResourceReference(StyleProperty, "Style.Annotation.OptionButton");
        b.SetResourceReference(ToolTipProperty, tipKey);
        b.Click += (_, _) => onClick();
        return b;
    }

    private UIElement TextButton(string glyph, string text, string tipKey, Action onClick)
    {
        var face = new StackPanel { Orientation = Orientation.Horizontal };
        var g = new TextBlock { Text = glyph, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(0, 0, 4, 0) };
        g.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        face.Children.Add(g);
        face.Children.Add(TextFace(text));
        var b = new Button { Content = face };
        b.SetResourceReference(StyleProperty, "Style.Annotation.TextButton");
        b.SetResourceReference(ToolTipProperty, tipKey);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static TextBlock Label(string key)
    {
        var t = new TextBlock();
        t.SetResourceReference(StyleProperty, "Style.Annotation.Label");
        t.SetResourceReference(TextBlock.TextProperty, key);
        return t;
    }

    /// <summary>[–] value [+]; the mouse wheel over it also steps.</summary>
    private UIElement Stepper(string tipKey, string? glyph, string value, Action<int> step)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        p.SetResourceReference(ToolTipProperty, tipKey);
        if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
            g.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            g.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            p.Children.Add(g);
        }
        var minus = new Button { Content = Icons.Minus, Width = 22, FontSize = 10 };
        minus.SetResourceReference(StyleProperty, "Style.Annotation.OptionButton");
        minus.Width = 22;
        minus.Click += (_, _) => step(-1);
        var plus = new Button { Content = Icons.Plus, FontSize = 10 };
        plus.SetResourceReference(StyleProperty, "Style.Annotation.OptionButton");
        plus.Width = 22;
        plus.Click += (_, _) => step(1);
        var text = new TextBlock { Text = value };
        text.SetResourceReference(StyleProperty, "Style.Annotation.Value");
        p.Children.Add(minus);
        p.Children.Add(text);
        p.Children.Add(plus);
        p.MouseWheel += (_, e) => { step(Math.Sign(e.Delta)); e.Handled = true; };
        return p;
    }

    private UIElement Slider(double min, double max, double value, double tick, string tipKey, Action<double> onChange)
    {
        var sl = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = tick, LargeChange = tick, TickFrequency = tick, IsSnapToTickEnabled = true };
        sl.SetResourceReference(StyleProperty, "Style.Annotation.Slider");
        sl.SetResourceReference(ToolTipProperty, tipKey);
        sl.ValueChanged += (_, e) => onChange(e.NewValue);
        sl.MouseWheel += (_, e) => { sl.Value = Math.Clamp(sl.Value + Math.Sign(e.Delta) * tick, min, max); e.Handled = true; };
        return sl;
    }

    private UIElement ColorDot(Color c, string tipKey, Action<Color> onChange)
    {
        var b = new Button { Background = new SolidColorBrush(c) };
        b.SetResourceReference(StyleProperty, "Style.Annotation.Swatch");
        b.SetResourceReference(ToolTipProperty, tipKey);
        b.Click += (_, _) => OpenColorFlyout(b, ((SolidColorBrush)b.Background).Color, nc =>
        {
            b.Background = new SolidColorBrush(nc);
            onChange(nc);
        });
        return b;
    }

    private UIElement FlyoutButton(object face, string tipKey, Func<UIElement> content)
    {
        var b = new Button { Content = face };
        b.SetResourceReference(StyleProperty, "Style.Annotation.OptionButton");
        b.SetResourceReference(ToolTipProperty, tipKey);
        b.Click += (_, _) => OpenFlyout(b, content());
        return b;
    }

    private void OpenColorFlyout(FrameworkElement target, Color initial, Action<Color> onChange)
    {
        var picker = new ColorPicker(initial);
        picker.ColorChanged += onChange;
        OpenFlyout(target, picker);
    }

    /// <summary>Win11-style flyout under <paramref name="target"/>; the options row is rebuilt when it closes.</summary>
    private void OpenFlyout(FrameworkElement target, UIElement content)
    {
        var border = new Border { Child = content };
        border.SetResourceReference(StyleProperty, "Style.Annotation.Flyout");
        var popup = new Popup
        {
            Child = border,
            PlacementTarget = target,
            Placement = OptionsAbove ? PlacementMode.Top : PlacementMode.Bottom,
            HorizontalOffset = -10,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.Closed += (_, _) =>
        {
            if (_openPopup == popup) _openPopup = null;
            RebuildOptions();
            // Give keyboard focus back to the surface so shortcuts keep working.
            if (_surface != null && !_surface.IsEditingText && Keyboard.FocusedElement is TextBox) _surface.Focus();
        };
        _openPopup = popup;
        popup.IsOpen = true;
    }

    private UIElement LineStyleFlyout(ToolOptions o, AnnotationTool tool)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var joinLabel = Label("Annotation.Line.Join");
        var capLabel = Label("Annotation.Line.Cap");
        Grid.SetRow(capLabel, 1);
        var joins = Segmented(Enum.GetValues<LineJoinStyle>().Select(j =>
            ((object)Icons.Join(j), "Annotation.Line.Join" + j, o.Join == j, (Action)(() => { o.Join = j; Commit(tool, rebuild: false); }))).ToArray());
        var caps = Segmented(Enum.GetValues<LineCapStyle>().Select(c =>
            ((object)Icons.Cap(c), "Annotation.Line.Cap" + c, o.Cap == c, (Action)(() => { o.Cap = c; Commit(tool, rebuild: false); }))).ToArray());
        Grid.SetColumn(joins, 1);
        Grid.SetColumn(caps, 1);
        Grid.SetRow(caps, 1);
        ExclusiveCheck((Panel)joins);
        ExclusiveCheck((Panel)caps);
        grid.Children.Add(joinLabel);
        grid.Children.Add(capLabel);
        grid.Children.Add(joins);
        grid.Children.Add(caps);
        return grid;
    }

    private UIElement TextBackgroundFlyout(ToolOptions o, AnnotationTool tool)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        void Row(int r, string key, UIElement control)
        {
            var l = Label(key);
            Grid.SetRow(l, r);
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            grid.Children.Add(l);
            grid.Children.Add(control);
        }
        Row(0, "Annotation.Text.BackgroundOpacity",
            Slider(0.1, 1, o.BackgroundOpacity, 0.05, "Annotation.Text.BackgroundOpacity", v => { o.BackgroundOpacity = Math.Round(v, 2); Commit(tool, rebuild: false); }));
        Row(1, "Annotation.Text.BackgroundRadius",
            Slider(0, 30, o.BackgroundRadius, 1, "Annotation.Text.BackgroundRadius", v => { o.BackgroundRadius = v; Commit(tool, rebuild: false); }));
        Row(2, "Annotation.Text.BackgroundPadding",
            Slider(0, 30, o.BackgroundPadding, 1, "Annotation.Text.BackgroundPadding", v => { o.BackgroundPadding = v; Commit(tool, rebuild: false); }));
        return grid;
    }

    /// <summary>Segmented buttons inside a flyout are not rebuilt on click; keep exactly one checked.</summary>
    private static void ExclusiveCheck(Panel p)
    {
        foreach (var b in p.Children.OfType<ToggleButton>())
            b.Click += (_, _) =>
            {
                foreach (var other in p.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, b);
            };
    }

    // ------------------------------------------------------------------ text-ish controls

    private sealed record FontEntry(string Source, string Display)
    {
        public override string ToString() => Display;
    }

    private static List<FontEntry>? s_fonts;

    private static List<FontEntry> Fonts
    {
        get
        {
            if (s_fonts != null) return s_fonts;
            var zh = XmlLanguage.GetLanguage("zh-cn");
            var en = XmlLanguage.GetLanguage("en-us");
            var list = new List<FontEntry>();
            try
            {
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    string source = f.Source;
                    if (source.Contains('#') || source.StartsWith("Segoe Fluent", StringComparison.Ordinal) || source.StartsWith("Segoe MDL2", StringComparison.Ordinal)) continue;
                    string display = f.FamilyNames.TryGetValue(zh, out var n) ? n : f.FamilyNames.TryGetValue(en, out var e) ? e : source;
                    list.Add(new FontEntry(source, display));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Enumerating fonts failed", ex);
            }
            // Chinese-named fonts first, then alphabetical.
            s_fonts = list.DistinctBy(f => f.Source)
                .OrderBy(f => f.Display.Any(ch => ch > 0x2E80) ? 0 : 1)
                .ThenBy(f => f.Display, StringComparer.CurrentCulture)
                .ToList();
            return s_fonts;
        }
    }

    private UIElement FontCombo(string current, Action<string> onChange)
    {
        var cb = new ComboBox { Width = 130, ItemsSource = Fonts };
        cb.SetResourceReference(StyleProperty, "Style.Annotation.ComboBox");
        cb.SetResourceReference(ToolTipProperty, "Annotation.Text.Font");
        var sel = Fonts.FirstOrDefault(f => string.Equals(f.Source, current, StringComparison.OrdinalIgnoreCase));
        if (sel == null)
        {
            sel = new FontEntry(current, current);
            cb.ItemsSource = new[] { sel }.Concat(Fonts).ToList();
        }
        cb.SelectedItem = sel;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is FontEntry f && f.Source != current) onChange(f.Source);
        };
        return cb;
    }

    private sealed record LayoutEntry(WatermarkLayout Key, string Display)
    {
        public override string ToString() => Display;
    }

    private UIElement LayoutCombo(ToolOptions o, AnnotationTool tool)
    {
        var items = Enum.GetValues<WatermarkLayout>().Select(l => new LayoutEntry(l, Loc.Get("Annotation.Watermark." + l))).ToList();
        var cb = new ComboBox { Width = 76, ItemsSource = items };
        cb.SetResourceReference(StyleProperty, "Style.Annotation.ComboBox");
        cb.SetResourceReference(ToolTipProperty, "Annotation.Watermark.Layout");
        cb.SelectedItem = items.First(i => i.Key == o.Layout);
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is LayoutEntry kv && kv.Key != o.Layout)
            {
                o.Layout = kv.Key;
                Commit(tool);
            }
        };
        return cb;
    }

    private UIElement WatermarkText(ToolOptions o, AnnotationTool tool)
    {
        var tb = new TextBox { Width = 170, Text = o.WatermarkText };
        tb.SetResourceReference(StyleProperty, "Style.Annotation.TextBox");
        tb.SetResourceReference(ToolTipProperty, "Annotation.Watermark.Text");
        tb.TextChanged += (_, _) =>
        {
            o.WatermarkText = tb.Text;
            Commit(tool, rebuild: false);
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                _surface?.Focus();
                e.Handled = true;
            }
        };
        return tb;
    }
}
