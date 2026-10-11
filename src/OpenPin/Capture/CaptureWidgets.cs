using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Capture;

internal static class Ui
{
    public static TextBlock Text(string text, double size = 12, bool secondary = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (secondary) t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return t;
    }

    public static Button Button(string text, Action onClick, bool accent = false)
    {
        var b = new Button { Content = text };
        b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "Style.Capture.AccentButton" : "Style.Capture.TextButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static ToggleButton Chip(string text, bool isChecked, Action<bool> onChanged)
    {
        var b = new ToggleButton { Content = text, IsChecked = isChecked };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.Capture.Chip");
        b.Click += (_, _) => onChanged(b.IsChecked == true);
        return b;
    }
}

/// <summary>CAP-07: "W × H" above the selection; click to type an exact size.</summary>
internal sealed class SizeLabel : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _editor = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly TextBox _w = new() { Width = 64 };
    private readonly TextBox _h = new() { Width = 64 };
    private bool _editable;

    /// <summary>Raised with the typed width/height (Enter).</summary>
    public event Action<int, int>? SizeEntered;
    public event Action? EditEnded;

    public SizeLabel()
    {
        SetResourceReference(StyleProperty, "Style.Capture.InfoPanel");
        Padding = new Thickness(8, 3, 8, 3);
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        foreach (var tb in new[] { _w, _h })
        {
            tb.SetResourceReference(StyleProperty, "Style.Capture.TextBox");
            tb.Height = 24;
            tb.KeyDown += OnEditorKeyDown;
            tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        }
        _editor.Children.Add(_w);
        _editor.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center });
        _editor.Children.Add(_h);
        _editor.LostKeyboardFocus += (_, _) =>
        {
            if (IsEditing && !_editor.IsKeyboardFocusWithin) EndEdit();
        };
        var grid = new Grid();
        grid.Children.Add(_text);
        grid.Children.Add(_editor);
        Child = grid;
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_editable || IsEditing) return;
            e.Handled = true;
            BeginEdit();
        };
    }

    public bool IsEditing => _editor.Visibility == Visibility.Visible;

    public bool Editable
    {
        get => _editable;
        set
        {
            _editable = value;
            Cursor = value ? Cursors.Hand : null;
            ToolTip = value ? Loc.Get("Capture.Size.EditTip") : null;
        }
    }

    public void SetSize(int w, int h)
    {
        _text.Text = $"{w} × {h}";
        if (!IsEditing) { _w.Text = w.ToString(CultureInfo.InvariantCulture); _h.Text = h.ToString(CultureInfo.InvariantCulture); }
    }

    public void BeginEdit()
    {
        _text.Visibility = Visibility.Hidden;
        _editor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _w.Focus(); _w.SelectAll(); });
    }

    public void EndEdit()
    {
        if (!IsEditing) return;
        _editor.Visibility = Visibility.Collapsed;
        _text.Visibility = Visibility.Visible;
        EditEnded?.Invoke();
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (int.TryParse(_w.Text, out var w) && int.TryParse(_h.Text, out var h) && w > 0 && h > 0)
                SizeEntered?.Invoke(w, h);
            EndEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndEdit();
        }
    }
}

/// <summary>CAP-16: shortcut cheat sheet in the bottom-left corner of the monitor.</summary>
internal sealed class ShortcutHints : Border
{
    public ShortcutHints()
    {
        SetResourceReference(StyleProperty, "Style.Capture.InfoPanel");
        Padding = new Thickness(12, 8, 12, 10);
        IsHitTestVisible = false;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Loc.Get("Capture.Hints.Title"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var key in new[] { "Select", "Wheel", "Copy", "Save", "Arrows", "Misc", "History", "Exit" })
        {
            var t = new TextBlock { Text = Loc.Get("Capture.Hints." + key), Margin = new Thickness(0, 1, 0, 1) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Capture.InfoTextSecondary");
            stack.Children.Add(t);
        }
        Child = stack;
    }
}

/// <summary>Short transient message at the top of the monitor (history position, cursor toggle…).</summary>
internal sealed class ToastLabel : Border
{
    private readonly TextBlock _text = new() { FontSize = 13 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    public ToastLabel()
    {
        SetResourceReference(StyleProperty, "Style.Capture.InfoPanel");
        Padding = new Thickness(14, 7, 14, 7);
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Child = _text;
        _timer.Tick += (_, _) => { _timer.Stop(); Visibility = Visibility.Collapsed; };
    }

    public void Show(string message)
    {
        _text.Text = message;
        Visibility = Visibility.Visible;
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}

/// <summary>CAP-12: QR / barcode results — compact chip or full list with copy / open-link buttons.</summary>
internal sealed class QrPanel : Border
{
    private readonly StackPanel _stack = new();

    public event Action<QrCodeResult>? CopyRequested;
    public event Action<QrCodeResult>? OpenRequested;
    public event Action? ExpandRequested;

    public QrPanel()
    {
        Visibility = Visibility.Collapsed;
        Child = _stack;
    }

    public bool IsExpanded { get; private set; }

    public void ShowCompact(int count)
    {
        IsExpanded = false;
        SetResourceReference(StyleProperty, "Style.Capture.InfoPanel");
        Padding = new Thickness(10, 5, 10, 5);
        _stack.Children.Clear();
        var t = new TextBlock { Text = Loc.Format("Capture.Qr.Found", count), Cursor = Cursors.Hand };
        t.MouseLeftButtonDown += (_, e) => { e.Handled = true; ExpandRequested?.Invoke(); };
        _stack.Children.Add(t);
        Visibility = Visibility.Visible;
    }

    public void ShowFull(IReadOnlyList<QrCodeResult> results)
    {
        IsExpanded = true;
        SetResourceReference(StyleProperty, "Style.Capture.Flyout");
        _stack.Children.Clear();
        _stack.MaxWidth = 360;
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (i > 0) _stack.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
            var caption = Ui.Text(r.Format, 11, secondary: true);
            _stack.Children.Add(caption);
            var text = new TextBox
            {
                Text = r.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 96, Height = double.NaN,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(6, 4, 6, 4),
            };
            text.SetResourceReference(StyleProperty, "Style.Capture.TextBox");
            text.Height = double.NaN;
            _stack.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Ui.Button(Loc.Get("Capture.Qr.Copy"), () => CopyRequested?.Invoke(r), accent: !r.IsUrl));
            if (r.IsUrl) buttons.Children.Add(Ui.Button(Loc.Get("Capture.Qr.Open"), () => OpenRequested?.Invoke(r), accent: true));
            _stack.Children.Add(buttons);
        }
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        IsExpanded = false;
    }
}

/// <summary>CAP-11 output style flyout: corner radius, shadow, border, fixed aspect ratio.</summary>
internal sealed class StylePanel : Border
{
    public static readonly string[] Ratios = { "", "1:1", "4:3", "3:4", "16:9", "9:16", "3:2", "2:3", "21:9" };

    private readonly CaptureSettings _cfg;
    private readonly List<ToggleButton> _ratioButtons = new();

    /// <summary>Raised after any change (argument: true if the aspect ratio changed).</summary>
    public event Action<bool>? Changed;

    public StylePanel(CaptureSettings cfg)
    {
        _cfg = cfg;
        SetResourceReference(StyleProperty, "Style.Capture.Flyout");
        Visibility = Visibility.Collapsed;
        var stack = new StackPanel { Width = 300 };

        // Corner radius
        var cornerValue = Ui.Text(cfg.OutputCornerRadius + " px", 12, secondary: true);
        cornerValue.Width = 44;
        cornerValue.TextAlignment = TextAlignment.Right;
        var slider = new Slider { Minimum = 0, Maximum = 64, Value = cfg.OutputCornerRadius, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 180, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        slider.ValueChanged += (_, e) =>
        {
            cfg.OutputCornerRadius = (int)e.NewValue;
            cornerValue.Text = cfg.OutputCornerRadius + " px";
            Changed?.Invoke(false);
        };
        stack.Children.Add(Row(Loc.Get("Capture.Style.Corner"), slider, cornerValue));

        // Shadow / border
        var effects = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        effects.Children.Add(Ui.Chip(Loc.Get("Capture.Style.Shadow"), cfg.OutputShadow, v => { cfg.OutputShadow = v; Changed?.Invoke(false); }));
        effects.Children.Add(Ui.Chip(Loc.Get("Capture.Style.Border"), cfg.OutputBorder, v => { cfg.OutputBorder = v; Changed?.Invoke(false); }));
        stack.Children.Add(effects);

        // Aspect ratio
        var caption = new TextBlock { Text = Loc.Get("Capture.Style.Ratio"), Margin = new Thickness(0, 8, 0, 4) };
        caption.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        stack.Children.Add(caption);
        var ratios = new WrapPanel();
        foreach (var r in Ratios)
        {
            var value = r;
            var chip = Ui.Chip(r.Length == 0 ? Loc.Get("Capture.Style.Free") : r, string.Equals(cfg.AspectRatio ?? "", r, StringComparison.Ordinal), _ =>
            {
                cfg.AspectRatio = value;
                foreach (var b in _ratioButtons) b.IsChecked = string.Equals(b.Tag as string, value, StringComparison.Ordinal);
                Changed?.Invoke(true);
            });
            chip.Tag = value;
            _ratioButtons.Add(chip);
            ratios.Children.Add(chip);
        }
        stack.Children.Add(ratios);

        var note = new TextBlock { Text = Loc.Get("Capture.Style.Note"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        stack.Children.Add(note);
        Child = stack;
    }

    private static FrameworkElement Row(string label, FrameworkElement control, FrameworkElement? trailing = null)
    {
        var p = new DockPanel { LastChildFill = false };
        var t = new TextBlock { Text = label, Width = 64 };
        t.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        p.Children.Add(t);
        p.Children.Add(control);
        if (trailing != null) p.Children.Add(trailing);
        return p;
    }
}
