using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Core.Imaging;

namespace OpenPin.SettingsUI.Controls;

/// <summary>
/// Color setting: swatch (click → preset flyout) + hex text box. <see cref="Value"/> ("#RRGGBB" / "#AARRGGBB", or ""
/// when <see cref="AllowEmpty"/>) binds two-way and only receives valid values.
/// </summary>
internal sealed class ColorBox : StackPanel
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorBox)d).Sync()));

    private static readonly string[] Presets =
    {
        "#F5222D", "#FA541C", "#FA8C16", "#FADB14", "#A0D911", "#52C41A", "#13C2C2", "#1677FF", "#2F54EB", "#722ED1",
        "#EB2F96", "#000000", "#434343", "#8C8C8C", "#BFBFBF", "#F0F0F0", "#FFFFFF", "#1F1F1F",
    };

    private readonly Border _swatch;
    private readonly Border _swatchFill;
    private readonly TextBox _text;
    private readonly Popup _popup;
    private bool _syncing;

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool AllowEmpty { get; init; }
    public bool AllowAlpha { get; init; }

    /// <summary>Raised when the user removes this color (palette editor), only if <see cref="ShowRemove"/>.</summary>
    public event Action<ColorBox>? RemoveRequested;
    public bool ShowRemove { get; init; }

    public ColorBox(bool compact = false)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        _swatchFill = new Border { CornerRadius = new CornerRadius(3) };
        _swatch = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3),
            Cursor = Cursors.Hand,
            Child = new Grid
            {
                Children = { new Border { CornerRadius = new CornerRadius(3), Background = CheckerBrush() }, _swatchFill },
            },
        };
        _swatch.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        _swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
        _swatch.MouseLeftButtonUp += (_, _) => _popup!.IsOpen = true;
        _swatch.SetResourceReference(ToolTipProperty, "SettingsUI.Color.PickTip");
        Children.Add(_swatch);

        _text = new TextBox
        {
            Width = compact ? 104 : 124,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        InputMethod.SetIsInputMethodEnabled(_text, false);
        _text.TextChanged += (_, _) => OnTextChanged();
        _text.LostKeyboardFocus += (_, _) => Sync();
        Children.Add(_text);

        _popup = new Popup
        {
            PlacementTarget = _swatch,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = BuildFlyout(),
        };
        Children.Add(_popup);
        Loaded += (_, _) =>
        {
            if (ShowRemove)
            {
                var remove = new Button { Content = "", Margin = new Thickness(2, 0, 0, 0) };
                remove.SetResourceReference(StyleProperty, "Style.SettingsUI.IconButton");
                remove.SetResourceReference(ToolTipProperty, "SettingsUI.Palette.Remove");
                remove.Click += (_, _) => RemoveRequested?.Invoke(this);
                if (!Children.OfType<Button>().Any()) Children.Insert(2, remove);
            }
            Sync();
        };
    }

    private FrameworkElement BuildFlyout()
    {
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(8) };
        foreach (var hex in Presets)
        {
            ColorText.TryParse(hex, out var c);
            var b = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            b.MouseLeftButtonUp += (_, _) =>
            {
                byte a = AllowAlpha && TryGetColor(Value, out var cur) ? cur.A : (byte)255;
                if (a == 0) a = 255;
                Value = Format(Color.FromArgb(a, c.R, c.G, c.B));
                _popup.IsOpen = false;
            };
            grid.Children.Add(b);
        }
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 8, 8),
            Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.2, Direction = 270 },
        };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        return border;
    }

    private string Format(Color c) => AllowAlpha && c.A != 255 ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryGetColor(string? text, out Color c)
    {
        c = Colors.Transparent;
        return !string.IsNullOrWhiteSpace(text) && ColorText.TryParse(text, out c);
    }

    private void OnTextChanged()
    {
        if (_syncing) return;
        var t = _text.Text.Trim();
        bool ok = (t.Length == 0 && AllowEmpty) || (TryGetColor(t, out var c) && (AllowAlpha || c.A == 255));
        SetInvalid(!ok);
        if (!ok) return;
        if (t.Length > 0 && !t.StartsWith('#') && TryGetColor(t, out var parsed)) t = Format(parsed);
        _syncing = true;
        Value = t;
        _syncing = false;
        UpdateSwatch();
    }

    private void SetInvalid(bool invalid)
    {
        if (invalid) _text.SetResourceReference(Control.BorderBrushProperty, "SystemFillColorCriticalBrush");
        else _text.ClearValue(Control.BorderBrushProperty);
    }

    private void Sync()
    {
        if (_syncing || _text is null) return;
        _syncing = true;
        _text.Text = Value ?? "";
        _syncing = false;
        SetInvalid(false);
        UpdateSwatch();
    }

    private void UpdateSwatch()
    {
        if (TryGetColor(Value, out var c)) _swatchFill.Background = new SolidColorBrush(c);
        else _swatchFill.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush"); // empty = accent
    }

    private static Brush CheckerBrush()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }
}
