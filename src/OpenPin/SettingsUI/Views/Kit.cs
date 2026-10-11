using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Core;

namespace OpenPin.SettingsUI.Views;

/// <summary>
/// Building blocks of the Windows 11 Settings look: settings cards, expander cards, section headers, toggles.
/// Colors come from the Fluent theme dictionary of the settings window (DynamicResource), so light/dark just works.
/// </summary>
internal static class Kit
{
    public const double CardSpacing = 4;

    public static bool HasString(string key) => Application.Current?.TryFindResource(key) is string;

    /// <summary>Text bound to a resource string (live), or literal text when <paramref name="keyOrText"/> is not a key.</summary>
    public static TextBlock Text(string keyOrText, double size = 14, string brush = "TextFillColorPrimaryBrush", FontWeight? weight = null)
    {
        var t = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        SetText(t, keyOrText);
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (weight is { } w) t.FontWeight = w;
        return t;
    }

    public static void SetText(TextBlock t, string keyOrText)
    {
        if (keyOrText.StartsWith("SettingsUI.", StringComparison.Ordinal) || keyOrText.StartsWith("Action.", StringComparison.Ordinal) ||
            keyOrText.StartsWith("Common.", StringComparison.Ordinal))
            t.SetResourceReference(TextBlock.TextProperty, keyOrText);
        else t.Text = keyOrText;
    }

    public static string Label(string keyOrText) =>
        keyOrText.StartsWith("SettingsUI.", StringComparison.Ordinal) ? Loc.Get(keyOrText) : keyOrText;

    public static TextBlock Icon(string glyph, double size = 20)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return t;
    }

    /// <summary>Title + optional description stacked.</summary>
    public static StackPanel TitleBlock(string titleKeyOrText, string? descKeyOrText)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Text(titleKeyOrText));
        if (descKeyOrText != null && (!descKeyOrText.StartsWith("SettingsUI.", StringComparison.Ordinal) || HasString(descKeyOrText)))
        {
            var d = Text(descKeyOrText, 12, "TextFillColorSecondaryBrush");
            d.Margin = new Thickness(0, 1, 0, 0);
            sp.Children.Add(d);
        }
        return sp;
    }

    private static Border CardBorder()
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 16, 10),
            MinHeight = 66,
            Margin = new Thickness(0, 0, 0, CardSpacing),
        };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return b;
    }

    /// <summary>
    /// A settings card: icon, title, description, control on the right. <paramref name="below"/> (optional) spans the
    /// full width under the text (path boxes, previews, lists).
    /// </summary>
    public static Border Card(string? icon, FrameworkElement titleBlock, UIElement? control, UIElement? below = null)
    {
        var border = CardBorder();
        border.Child = Row(icon, titleBlock, control, below, iconColumnWidth: 36);
        return border;
    }

    public static Border Card(string? icon, string titleKey, string? descKey, UIElement? control, UIElement? below = null) =>
        Card(icon, TitleBlock(titleKey, descKey), control, below);

    /// <summary>Card layout grid (also used for rows inside expander cards).</summary>
    public static Grid Row(string? icon, FrameworkElement titleBlock, UIElement? control, UIElement? below, double iconColumnWidth)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconColumnWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!string.IsNullOrEmpty(icon))
        {
            var ic = Icon(icon);
            ic.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(ic);
        }
        titleBlock.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(titleBlock, 1);
        g.Children.Add(titleBlock);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(fe, 2);
            g.Children.Add(fe);
        }
        if (below is FrameworkElement b)
        {
            b.Margin = new Thickness(0, 8, 0, 2);
            Grid.SetRow(b, 1);
            Grid.SetColumn(b, 1);
            Grid.SetColumnSpan(b, 2);
            g.Children.Add(b);
        }
        return g;
    }

    /// <summary>Section caption above a group of cards.</summary>
    public static TextBlock Header(string key)
    {
        var t = Text(key, 14, "TextFillColorPrimaryBrush", FontWeights.SemiBold);
        t.Margin = new Thickness(1, 22, 0, 8);
        return t;
    }

    public static CheckBox Toggle()
    {
        var c = new CheckBox();
        c.SetResourceReference(FrameworkElement.StyleProperty, "Style.SettingsUI.ToggleSwitch");
        return c;
    }

    public static Button Button(string keyOrText, RoutedEventHandler click, bool accent = false, string? icon = null)
    {
        var b = new Button { MinWidth = 96, Padding = new Thickness(12, 5, 12, 6) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
        {
            var ic = Icon(icon, 14);
            ic.Margin = new Thickness(0, 0, 8, 0);
            ic.ClearValue(TextBlock.ForegroundProperty);
            content.Children.Add(ic);
        }
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        SetText(t, keyOrText);
        content.Children.Add(t);
        b.Content = content;
        SetAutomationName(b, keyOrText);
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        b.Click += click;
        return b;
    }

    /// <summary>Accessible name (screen readers, UI Automation) from a resource key or literal.</summary>
    public static void SetAutomationName(FrameworkElement e, string keyOrText)
    {
        if (keyOrText.StartsWith("SettingsUI.", StringComparison.Ordinal) || keyOrText.StartsWith("Common.", StringComparison.Ordinal) ||
            keyOrText.StartsWith("Action.", StringComparison.Ordinal))
            e.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, keyOrText);
        else System.Windows.Automation.AutomationProperties.SetName(e, keyOrText);
    }

    public static Button IconButton(string glyph, string tooltipKey, RoutedEventHandler click)
    {
        var b = new Button { Content = glyph };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.SettingsUI.IconButton");
        b.SetResourceReference(FrameworkElement.ToolTipProperty, tooltipKey);
        SetAutomationName(b, tooltipKey);
        b.Click += click;
        return b;
    }

    /// <summary>Combo box with (value, label key or text) items; bind <see cref="Selector.SelectedValueProperty"/>.</summary>
    public static ComboBox Combo(IEnumerable<(object Value, string Label)> items, double minWidth = 200)
    {
        var cb = new ComboBox { MinWidth = minWidth, SelectedValuePath = nameof(ComboBoxItem.Tag) };
        foreach (var (value, label) in items)
        {
            var item = new ComboBoxItem { Tag = value };
            if (label.StartsWith("SettingsUI.", StringComparison.Ordinal) && HasString(label)) item.SetResourceReference(ContentControl.ContentProperty, label);
            else item.Content = label.StartsWith("SettingsUI.Enum.", StringComparison.Ordinal) ? label[(label.LastIndexOf('.') + 1)..] : label;
            cb.Items.Add(item);
        }
        return cb;
    }

    /// <summary>Two-way binding to <c>Draft.{path}</c> of the session.</summary>
    public static BindingExpressionBase Bind(FrameworkElement target, DependencyProperty dp, SettingsSession session, string path,
        IValueConverter? converter = null, BindingMode mode = BindingMode.TwoWay)
    {
        var b = new Binding("Draft." + path)
        {
            Source = session,
            Mode = mode,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            NotifyOnSourceUpdated = true,
            Converter = converter,
        };
        return target.SetBinding(dp, b);
    }

    /// <summary>Two-way binding to an arbitrary object (preset rows).</summary>
    public static void BindTo(FrameworkElement target, DependencyProperty dp, object source, string path, IValueConverter? converter = null)
    {
        target.SetBinding(dp, new Binding(path)
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            NotifyOnSourceUpdated = true,
            Converter = converter,
        });
    }

    /// <summary>A text box with a grey placeholder shown while empty.</summary>
    public static Grid WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = new TextBlock
        {
            Text = placeholder,
            IsHitTestVisible = false,
            Margin = new Thickness(11, 0, 40, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        void Update() => hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update();
        box.Loaded += (_, _) => Update();
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(hint);
        return g;
    }

    /// <summary>Small secondary text line (previews, hints).</summary>
    public static TextBlock Hint(string keyOrText = "", string brush = "TextFillColorSecondaryBrush")
    {
        var t = Text(keyOrText, 12, brush);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    public static void OpenShell(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error($"Opening '{target}' failed", ex);
            Services.Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    public static Cursor Hand => Cursors.Hand;
}

/// <summary>Win11 expander card: header card (icon, title, description, optional control, chevron) + nested rows.</summary>
internal sealed class ExpanderCard : StackPanel
{
    private readonly Border _header;
    private readonly Border _body;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _chevron;
    private bool _expanded;

    public ExpanderCard(string icon, string titleKey, string? descKey, UIElement? headerControl, bool expanded = false)
    {
        Margin = new Thickness(0, 0, 0, Kit.CardSpacing);

        _chevron = Kit.Icon("", 12);
        _chevron.Margin = new Thickness(16, 0, 0, 0);
        _chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        _chevron.RenderTransform = new RotateTransform();

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (headerControl != null) right.Children.Add(headerControl);
        var chevronHost = new Border { Background = Brushes.Transparent, Child = _chevron, Padding = new Thickness(0, 8, 0, 8) };
        right.Children.Add(chevronHost);

        _header = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 16, 10),
            MinHeight = 66,
            Cursor = Cursors.Hand,
            Child = Kit.Row(icon, Kit.TitleBlock(titleKey, descKey), right, null, 36),
        };
        _header.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        _header.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _header.MouseLeftButtonUp += (_, e) =>
        {
            // Clicks on the header control (toggle) must not expand/collapse.
            if (headerControl is DependencyObject hc && e.OriginalSource is DependencyObject src && IsDescendant(src, hc)) return;
            Expanded = !Expanded;
        };
        _header.MouseEnter += (_, _) => _header.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        _header.MouseLeave += (_, _) => _header.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        Children.Add(_header);

        _body = new Border { BorderThickness = new Thickness(1, 0, 1, 1), CornerRadius = new CornerRadius(0, 0, 4, 4), Child = _rows };
        _body.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        _body.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Children.Add(_body);
        Expanded = expanded;
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _body.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _header.CornerRadius = value ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(4);
            ((RotateTransform)_chevron.RenderTransform).Angle = value ? 180 : 0;
        }
    }

    /// <summary>Adds a nested row (title/description left, control right; optional full-width content below).</summary>
    public FrameworkElement AddRow(FrameworkElement titleBlock, UIElement? control, UIElement? below = null)
    {
        var row = new Border
        {
            Padding = new Thickness(16, 10, 16, 10),
            MinHeight = 52,
            BorderThickness = new Thickness(0, _rows.Children.Count > 0 ? 1 : 0, 0, 0),
            Child = Kit.Row(null, titleBlock, control, below, 36),
        };
        row.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        _rows.Children.Add(row);
        return row;
    }

    /// <summary>Adds arbitrary content as a row.</summary>
    public FrameworkElement AddContent(FrameworkElement content)
    {
        var row = new Border
        {
            Padding = new Thickness(52, 12, 16, 12),
            BorderThickness = new Thickness(0, _rows.Children.Count > 0 ? 1 : 0, 0, 0),
            Child = content,
        };
        row.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        _rows.Children.Add(row);
        return row;
    }

    private static bool IsDescendant(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n != null; n = n is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }
}
