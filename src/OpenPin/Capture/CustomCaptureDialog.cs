using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenPin.Core;
using OpenPin.Core.Screen;
using OpenPin.Services;

namespace OpenPin.Capture;

/// <summary>
/// CAP-14 custom / delayed capture (Alt+1): exact X/Y/W/H in screen px, a delay, presets.
/// "Start" either opens the overlay with that region selected or copies it directly.
/// </summary>
internal sealed class CustomCaptureDialog : Window
{
    private static CustomCaptureDialog? _current;

    private readonly CaptureController _owner;
    private readonly CaptureSettings _cfg;
    private readonly TextBox _x = NumberBox(), _y = NumberBox(), _w = NumberBox(), _h = NumberBox(), _delay = NumberBox();
    private readonly ComboBox _presets = new() { MinWidth = 180, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _direct = new() { VerticalAlignment = VerticalAlignment.Center };

    public static void ShowSingle(CaptureController owner)
    {
        if (_current != null)
        {
            _current.Activate();
            return;
        }
        _current = new CustomCaptureDialog(owner);
        _current.Closed += (_, _) => _current = null;
        _current.Show();
        _current.Activate();
    }

    private CustomCaptureDialog(CaptureController owner)
    {
        _owner = owner;
        _cfg = AppServices.Settings?.Current.Capture ?? new CaptureSettings();
        Title = Loc.Get("Capture.Custom.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "Brush.Surface");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MinWidth = 340 };

        // Presets
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        presetRow.Children.Add(Caption("Capture.Custom.Preset", 72));
        presetRow.Children.Add(_presets);
        var del = Ui.Button(Loc.Get("Capture.Custom.DeletePreset"), DeletePreset);
        del.Margin = new Thickness(6, 0, 0, 0);
        presetRow.Children.Add(del);
        root.Children.Add(presetRow);
        _presets.SelectionChanged += (_, _) =>
        {
            if (_presets.SelectedIndex >= 0 && _presets.SelectedIndex < _cfg.CustomPresets.Count) Load(_cfg.CustomPresets[_presets.SelectedIndex]);
        };
        ReloadPresets();

        // Region
        var caption = new TextBlock { Text = Loc.Get("Capture.Custom.Region"), Margin = new Thickness(0, 0, 0, 6) };
        caption.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        root.Children.Add(caption);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(28) : new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition());
        AddCell(grid, "X", _x, 0, 0);
        AddCell(grid, "Y", _y, 0, 2);
        AddCell(grid, Loc.Get("Capture.Size.Width"), _w, 2, 0);
        AddCell(grid, Loc.Get("Capture.Size.Height"), _h, 2, 2);
        root.Children.Add(grid);

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        quick.Children.Add(Ui.Button(Loc.Get("Capture.Custom.UseLast"), UseLast));
        quick.Children.Add(Ui.Button(Loc.Get("Capture.Custom.UseScreen"), UseScreen));
        quick.Children.Add(Ui.Button(Loc.Get("Capture.Custom.SavePreset"), SavePreset));
        root.Children.Add(quick);

        // Delay + direct copy
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        delayRow.Children.Add(Caption("Capture.Custom.Delay", 72));
        _delay.Width = 72;
        delayRow.Children.Add(_delay);
        root.Children.Add(delayRow);
        var directRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        _direct.IsChecked = _cfg.CustomDirectCopy;
        directRow.Children.Add(_direct);
        directRow.Children.Add(new TextBlock { Text = Loc.Get("Capture.Custom.DirectCopy"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(directRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var start = Ui.Button(Loc.Get("Capture.Custom.Start"), StartCapture, accent: true);
        buttons.Children.Add(start);
        var cancel = Ui.Button(Loc.Get("Common.Cancel"), Close);
        cancel.Margin = new Thickness(0);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Load(_cfg.LastCustom is { Width: > 0, Height: > 0 } last ? last : FromRect(Monitors.UnderCursor.BoundsPx));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; StartCapture(); }
        };
        Loaded += (_, _) => { _w.Focus(); _w.SelectAll(); };
    }

    private static TextBox NumberBox()
    {
        var t = new TextBox();
        t.SetResourceReference(StyleProperty, "Style.Capture.TextBox");
        t.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '-');
        return t;
    }

    private static TextBlock Caption(string key, double width)
    {
        var t = new TextBlock { Text = Loc.Get(key), Width = width };
        t.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        return t;
    }

    private static void AddCell(Grid g, string label, TextBox box, int row, int col)
    {
        var t = new TextBlock { Text = label };
        t.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        Grid.SetRow(t, row); Grid.SetColumn(t, col);
        box.Margin = new Thickness(0, 0, col == 0 ? 12 : 0, 0);
        Grid.SetRow(box, row); Grid.SetColumn(box, col + 1);
        g.Children.Add(t);
        g.Children.Add(box);
    }

    private static CustomCapturePreset FromRect(Int32Rect r) => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };

    private void Load(CustomCapturePreset p)
    {
        _x.Text = p.X.ToString(CultureInfo.InvariantCulture);
        _y.Text = p.Y.ToString(CultureInfo.InvariantCulture);
        _w.Text = p.Width.ToString(CultureInfo.InvariantCulture);
        _h.Text = p.Height.ToString(CultureInfo.InvariantCulture);
        _delay.Text = p.DelaySeconds.ToString(CultureInfo.InvariantCulture);
    }

    private CustomCapturePreset? Read()
    {
        if (!int.TryParse(_x.Text, out var x) || !int.TryParse(_y.Text, out var y) ||
            !int.TryParse(_w.Text, out var w) || !int.TryParse(_h.Text, out var h)) return null;
        int.TryParse(_delay.Text, out var delay);
        if (w <= 0 || h <= 0) return null;
        if (SelectionMath.IsEmpty(SelectionMath.Intersect(new Int32Rect(x, y, w, h), Monitors.VirtualScreenPx))) return null;
        return new CustomCapturePreset { X = x, Y = y, Width = w, Height = h, DelaySeconds = Math.Clamp(delay, 0, 600) };
    }

    private void ReloadPresets()
    {
        _presets.Items.Clear();
        foreach (var p in _cfg.CustomPresets)
            _presets.Items.Add($"{p.Name}  ({p.Width}×{p.Height} @ {p.X},{p.Y}{(p.DelaySeconds > 0 ? $", {p.DelaySeconds}s" : "")})");
    }

    private void UseLast()
    {
        if (CaptureController.History.LastRegion is { } r) Load(new CustomCapturePreset { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, DelaySeconds = ParseDelay() });
        else Notify.Info(Loc.Get("Capture.NoLastRegion"));
    }

    private void UseScreen()
    {
        var r = Monitors.FromPointPx((int)Monitors.CursorPx.X, (int)Monitors.CursorPx.Y).BoundsPx;
        Load(new CustomCapturePreset { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, DelaySeconds = ParseDelay() });
    }

    private int ParseDelay() => int.TryParse(_delay.Text, out var d) ? d : 0;

    private void SavePreset()
    {
        if (Read() is not { } p) { Notify.Error(Loc.Get("Capture.Custom.Invalid")); return; }
        p.Name = Loc.Format("Capture.Custom.PresetName", _cfg.CustomPresets.Count + 1);
        _cfg.CustomPresets.Add(p);
        AppServices.Settings?.Save();
        ReloadPresets();
        _presets.SelectedIndex = _cfg.CustomPresets.Count - 1;
    }

    private void DeletePreset()
    {
        int i = _presets.SelectedIndex;
        if (i < 0 || i >= _cfg.CustomPresets.Count) return;
        _cfg.CustomPresets.RemoveAt(i);
        AppServices.Settings?.Save();
        ReloadPresets();
    }

    private void StartCapture()
    {
        if (Read() is not { } p) { Notify.Error(Loc.Get("Capture.Custom.Invalid")); return; }
        bool direct = _direct.IsChecked == true;
        _cfg.LastCustom = p;
        _cfg.CustomDirectCopy = direct;
        AppServices.Settings?.Save();
        Close();

        var rect = new Int32Rect(p.X, p.Y, p.Width, p.Height);
        var owner = _owner;
        void Go()
        {
            if (direct) CaptureController.CaptureNoUi(rect, "Capture.Copied");
            else owner.StartInternal(Core.CaptureMode.Edit, rect);
        }
        if (p.DelaySeconds > 0)
        {
            Notify.Info(Loc.Format("Capture.Custom.Countdown", p.DelaySeconds));
            CaptureController.AfterDelay(p.DelaySeconds * 1000, Go, "delayed capture");
        }
        else
        {
            // Give the dialog time to disappear from the screen before freezing it.
            CaptureController.AfterDelay(200, Go, "custom capture");
        }
    }
}
