using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace OpenPin.SettingsUI.Controls;

/// <summary>
/// Win11-style NumberBox: text box + inline up/down buttons, clamps to [Minimum, Maximum], arrow keys / wheel step.
/// <see cref="Value"/> binds two-way by default.
/// </summary>
internal sealed class NumberBox : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).SyncText()));

    private readonly TextBox _text;
    private bool _syncing;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double Step { get; set; } = 1;
    public bool IsInteger { get; set; } = true;

    public NumberBox()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Width = 132;

        _text = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 5, 52, 6) };
        InputMethod.SetIsInputMethodEnabled(_text, false);
        Grid.SetColumnSpan(_text, 2);
        _text.LostKeyboardFocus += (_, _) => Commit();
        _text.PreviewKeyDown += OnKey;
        _text.PreviewMouseWheel += (_, e) =>
        {
            if (!_text.IsKeyboardFocusWithin) return;
            StepBy(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        };
        Children.Add(_text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(SpinButton("", 1));
        buttons.Children.Add(SpinButton("", -1));
        Grid.SetColumn(buttons, 1);
        Children.Add(buttons);
        SyncText();
    }

    private RepeatButton SpinButton(string glyph, int direction)
    {
        var b = new RepeatButton
        {
            Content = glyph,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 10,
            Focusable = false,
            Delay = 400,
            Interval = 60,
        };
        b.SetResourceReference(StyleProperty, "Style.SettingsUI.SpinButton");
        b.Click += (_, _) => { Commit(); StepBy(direction); };
        return b;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: StepBy(1); e.Handled = true; break;
            case Key.Down: StepBy(-1); e.Handled = true; break;
            case Key.PageUp: StepBy(10); e.Handled = true; break;
            case Key.PageDown: StepBy(-10); e.Handled = true; break;
            case Key.Enter: Commit(); _text.SelectAll(); e.Handled = true; break;
            case Key.Escape: SyncText(); e.Handled = true; break;
        }
    }

    private void StepBy(int steps) => SetClamped(Value + steps * Step);

    private void Commit()
    {
        if (TryParse(_text.Text, out var v)) SetClamped(v);
        else SyncText();
    }

    private void SetClamped(double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        if (IsInteger) v = Math.Round(v);
        if (v != Value) Value = v;
        SyncText();
    }

    private static bool TryParse(string text, out double v) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void SyncText()
    {
        if (_syncing || _text is null) return;
        _syncing = true;
        _text.Text = IsInteger ? ((long)Math.Round(Value)).ToString(CultureInfo.CurrentCulture) : Value.ToString("0.##", CultureInfo.CurrentCulture);
        _syncing = false;
    }
}

/// <summary>Converts between numeric source properties (int, long, float, double) and double targets.</summary>
internal sealed class NumberConverter : IValueConverter
{
    public static readonly NumberConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0.0;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t == typeof(int)) return (int)Math.Round(d);
        if (t == typeof(long)) return (long)Math.Round(d);
        if (t == typeof(float)) return (float)d;
        return d;
    }
}
