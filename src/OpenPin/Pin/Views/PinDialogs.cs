using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Pin.Model;

namespace OpenPin.Pin.Views;

/// <summary>
/// Base for the Pin module's small Win11-style dialogs: borderless rounded surface, title row with close button,
/// content, right-aligned buttons; centered on the monitor under the cursor; Enter = OK, Esc = cancel.
/// </summary>
internal class PinDialog : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    private readonly ContentControl _body = new();

    protected PinDialog(string title)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        header.Children.Add(titleText);
        var close = new Button { Content = "", HorizontalAlignment = HorizontalAlignment.Right, Width = 28, Height = 28, FontSize = 12, IsCancel = false };
        close.SetResourceReference(StyleProperty, "Style.IconButton");
        close.Click += (_, _) => { DialogResult = false; };
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var stack = new StackPanel { MinWidth = 320 };
        stack.Children.Add(header);
        stack.Children.Add(_body);
        stack.Children.Add(_buttons);

        var border = new Border { Child = stack, Margin = new Thickness(16), Padding = new Thickness(20, 16, 20, 20) };
        border.SetResourceReference(StyleProperty, "Style.Pin.Dialog");
        Content = border;

        SourceInitialized += (_, _) => WindowHelper.SetToolWindow(this, true);
        Loaded += (_, _) => CenterOnCursorMonitor();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    protected object? Body { get => _body.Content; set => _body.Content = value; }

    protected Button AddButton(string text, bool accent, Action onClick, bool isDefault = false)
    {
        var b = new Button { Content = text, MinWidth = 88, Height = 32, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault };
        b.SetResourceReference(StyleProperty, accent ? "Style.Pin.AccentButton" : "Style.Pin.Button");
        b.Click += (_, _) => onClick();
        _buttons.Children.Add(b);
        return b;
    }

    private void CenterOnCursorMonitor()
    {
        try
        {
            var work = Monitors.UnderCursor.WorkAreaPx;
            var b = WindowHelper.GetBoundsPx(this);
            WindowHelper.SetPositionPx(this, work.X + (work.Width - b.Width) / 2, work.Y + (work.Height - b.Height) / 3);
            WindowHelper.ForceForeground(this);
        }
        catch (Exception ex) { Log.Warn($"Dialog placement failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- small widgets

    public static TextBlock Label(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 6), FontSize = 13 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        return t;
    }

    public static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        return t;
    }

    public static TextBox Input(string text)
    {
        var tb = new TextBox { Text = text, MinWidth = 300, Height = 32 };
        tb.SetResourceReference(StyleProperty, "Style.Pin.TextBox");
        return tb;
    }

    public static CheckBox Check(string text, bool value)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 12, 0, 0) };
        c.SetResourceReference(StyleProperty, "Style.Pin.CheckBox");
        return c;
    }

    /// <summary>Group color presets (user data, not theme colors). null = no color.</summary>
    public static readonly string?[] GroupColors =
    {
        null, "#1677FF", "#13C2C2", "#52C41A", "#FADB14", "#FA8C16", "#F5222D", "#EB2F96", "#722ED1", "#8C8C8C",
    };

    /// <summary>A row of round color swatches; returns the panel and a getter for the selected value.</summary>
    public static (Panel Panel, Func<string?> Selected) ColorPicker(string? initial)
    {
        var panel = new WrapPanel();
        string? selected = GroupColors.Contains(initial) ? initial : initial ?? null;
        var buttons = new List<ToggleButton>();
        foreach (var c in GroupColors.Concat(initial != null && !GroupColors.Contains(initial) ? new[] { initial } : Array.Empty<string?>()))
        {
            var tb = new ToggleButton { Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 0), ToolTip = c ?? Loc.Get("Pin.Group.NoColor"), Tag = c };
            tb.SetResourceReference(StyleProperty, "Style.Pin.Swatch");
            if (c != null && ColorText.TryParse(c, out var col)) tb.Background = BitmapUtil.Frozen(new SolidColorBrush(col));
            else
            {
                tb.SetResourceReference(BackgroundProperty, "Brush.SurfaceAlt");
                tb.Content = "";
            }
            tb.IsChecked = c == selected;
            tb.Click += (_, _) =>
            {
                selected = (string?)tb.Tag;
                foreach (var o in buttons) o.IsChecked = ReferenceEquals(o, tb);
            };
            buttons.Add(tb);
            panel.Children.Add(tb);
        }
        return (panel, () => selected);
    }

    public static bool Confirm(string title, string message, string? okText = null)
    {
        var d = new MessageDialog(title, message, okText ?? Loc.Get("Common.OK"));
        return d.ShowDialog() == true;
    }
}

internal sealed class MessageDialog : PinDialog
{
    public MessageDialog(string title, string message, string okText) : base(title)
    {
        var t = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 13 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        Body = t;
        AddButton(okText, true, () => DialogResult = true, isDefault: true);
        AddButton(Loc.Get("Common.Cancel"), false, () => DialogResult = false);
    }
}

/// <summary>Single-line text input (pin title, group rename).</summary>
internal sealed class InputDialog : PinDialog
{
    private readonly TextBox _box;
    private readonly Func<string, string?>? _validate;
    private readonly TextBlock _error = Hint("");

    public InputDialog(string title, string prompt, string initial, Func<string, string?>? validate = null) : base(title)
    {
        _validate = validate;
        _box = Input(initial);
        var stack = new StackPanel();
        stack.Children.Add(Label(prompt));
        stack.Children.Add(_box);
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);
        Body = stack;
        AddButton(Loc.Get("Common.OK"), true, Ok, isDefault: true);
        AddButton(Loc.Get("Common.Cancel"), false, () => DialogResult = false);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public string Value => _box.Text;

    private void Ok()
    {
        var err = _validate?.Invoke(_box.Text);
        if (err != null)
        {
            _error.Text = err;
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}

/// <summary>New pin group: unique name, color, "switch now" (PIN-11).</summary>
internal sealed class NewGroupDialog : PinDialog
{
    private readonly TextBox _name;
    private readonly Func<string?> _color;
    private readonly CheckBox _switch;
    private readonly TextBlock _error = Hint("");
    private readonly PinGroupBook _book;

    public NewGroupDialog(PinGroupBook book) : base(Loc.Get("Pin.Group.NewTitle"))
    {
        _book = book;
        _name = Input(book.UniqueName(Loc.Get("Pin.Group.DefaultNewName")));
        var (panel, getter) = ColorPicker(null);
        _color = getter;
        _switch = Check(Loc.Get("Pin.Group.SwitchNow"), true);

        var stack = new StackPanel();
        stack.Children.Add(Label(Loc.Get("Pin.Group.Name")));
        stack.Children.Add(_name);
        _error.Visibility = Visibility.Collapsed;
        stack.Children.Add(_error);
        stack.Children.Add(Label(Loc.Get("Pin.Group.Color"), 12));
        stack.Children.Add(panel);
        stack.Children.Add(_switch);
        Body = stack;
        AddButton(Loc.Get("Pin.Group.Create"), true, Ok, isDefault: true);
        AddButton(Loc.Get("Common.Cancel"), false, () => DialogResult = false);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    public string GroupName => PinGroupBook.NormalizeName(_name.Text);
    public string? GroupColor => _color();
    public bool SwitchNow => _switch.IsChecked == true;

    private void Ok()
    {
        if (!_book.IsNameAvailable(_name.Text))
        {
            _error.Text = Loc.Get(PinGroupBook.NormalizeName(_name.Text).Length == 0 ? "Pin.Group.NameEmpty" : "Pin.Group.NameTaken");
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}

/// <summary>Choose what happens to the pins of a deleted group.</summary>
internal sealed class DeleteGroupDialog : PinDialog
{
    private readonly ComboBox _target = new() { MinWidth = 300, Height = 32 };

    public DeleteGroupDialog(PinGroupBook book, PinGroup group, int pinCount) : base(Loc.Get("Pin.Group.DeleteTitle"))
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(Loc.Format("Pin.Group.DeletePrompt", group.Name, pinCount)));
        _target.SetResourceReference(StyleProperty, "Style.Pin.ComboBox");
        _target.Items.Add(new ComboBoxItem { Content = Loc.Get("Pin.Group.DeletePins"), Tag = null });
        foreach (var g in book.Groups.Where(g => g.Id != group.Id))
            _target.Items.Add(new ComboBoxItem { Content = Loc.Format("Pin.Group.MoveTo", g.Name), Tag = g.Id });
        _target.SelectedIndex = pinCount > 0 ? 1 : 0;
        stack.Children.Add(_target);
        Body = stack;
        AddButton(Loc.Get("Common.Delete"), true, () => DialogResult = true, isDefault: true);
        AddButton(Loc.Get("Common.Cancel"), false, () => DialogResult = false);
    }

    /// <summary>Group id to move the pins to, or null to destroy them.</summary>
    public string? MoveToId => (_target.SelectedItem as ComboBoxItem)?.Tag as string;
}
