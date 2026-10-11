using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.UI;

/// <summary>Null-safe access to the Recording settings section (settings may be absent in tests / harnesses).</summary>
internal static class RecordingConfig
{
    private static readonly RecordingSettings Fallback = new();

    public static RecordingSettings Current
    {
        get
        {
            try { return AppServices.Settings?.Current?.Recording ?? Fallback; }
            catch { return Fallback; }
        }
    }

    public static void Save()
    {
        try { AppServices.Settings?.Save(); }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }

    /// <summary>Hotkey text of the capture action ("Ctrl+1"), for tooltips.</summary>
    public static string CaptureHotkey
    {
        get
        {
            try { return AppServices.Config.Hotkeys.Bindings.GetValueOrDefault(Core.Actions.ActionIds.Capture, ""); }
            catch { return "Ctrl+1"; }
        }
    }

    public static OverlayOptions Overlays(RecordingSettings s) => new()
    {
        ShowCursor = s.ShowCursor,
        ShowClicks = s.HighlightClicks,
        ShowWheel = s.ShowWheel,
        ClickAnimationMs = Math.Clamp(s.ClickAnimationMs, 150, 2000),
        ShowKeys = s.ShowKeys,
        KeyFilter = s.KeyFilter,
        ShowWebcam = true,
        WebcamShape = s.WebcamShape,
        WebcamCorner = s.WebcamCorner,
        WebcamSize = Math.Clamp(s.WebcamSize, 0.1, 0.5),
    };
}

/// <summary>Segoe Fluent Icons / MDL2 code points used by the module.</summary>
internal static class Glyphs
{
    public const string Record = "";
    public const string Stop = "";
    public const string Pause = "";
    public const string Play = "";
    public const string Restart = "";
    public const string Close = "";
    public const string Gripper = "";
    public const string Microphone = "";
    public const string Volume = "";
    public const string Camera = "";
    public const string Keyboard = "";
    public const string Mouse = "";
    public const string Timer = "";
    public const string ChevronDown = "";
    public const string Save = "";
    public const string Copy = "";
    public const string QuickSave = "";
}

/// <summary>Small factory helpers for the module's Win11-styled controls (styles live in Resources/Styles/Recording.xaml).</summary>
internal static class RecUi
{
    public static T Styled<T>(T element, string styleKey) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return element;
    }

    public static Button IconButton(string glyph, string tooltipKey, RoutedEventHandler click, string style = "Style.Recording.IconButton")
    {
        var b = Styled(new Button { Content = glyph }, style);
        b.SetResourceReference(FrameworkElement.ToolTipProperty, tooltipKey);
        b.Click += click;
        return b;
    }

    public static TextBlock Text(string key, string style = "Style.Recording.Label")
    {
        var t = Styled(new TextBlock(), style);
        t.SetResourceReference(TextBlock.TextProperty, key);
        return t;
    }

    public static TextBlock Icon(string glyph, double size = 14)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        return t;
    }

    public static Button Button(string textKey, RoutedEventHandler click, bool accent = false, string? glyph = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
        {
            var i = Icon(glyph, 13);
            i.Margin = new Thickness(0, 0, 6, 0);
            panel.Children.Add(i);
        }
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.TextProperty, textKey);
        panel.Children.Add(label);
        var b = Styled(new Button { Content = panel }, accent ? "Style.Recording.AccentButton" : "Style.Recording.Button");
        b.Click += click;
        return b;
    }

    public static CheckBox Check(string textKey, bool value, Action<bool> changed)
    {
        var c = Styled(new CheckBox { IsChecked = value }, "Style.Recording.CheckBox");
        c.SetResourceReference(ContentControl.ContentProperty, textKey);
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    public static Slider Slider(double min, double max, double value, Action<double> changed, double width = 140)
    {
        var s = Styled(new Slider { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center }, "Style.Recording.Slider");
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }

    /// <summary>Segmented control (one of N). Returns the group border and a setter to change the selection programmatically.</summary>
    public static (Border Group, Action<T> Select) Segmented<T>(IReadOnlyList<(T Value, string TextKey)> items, T selected, Action<T> changed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        string group = "seg" + Guid.NewGuid().ToString("N");
        var buttons = new List<(RadioButton, T)>();
        foreach (var (value, key) in items)
        {
            var rb = Styled(new RadioButton { GroupName = group, IsChecked = EqualityComparer<T>.Default.Equals(value, selected) }, "Style.Recording.Segment");
            if (key.StartsWith("Recording.", StringComparison.Ordinal)) rb.SetResourceReference(ContentControl.ContentProperty, key);
            else rb.Content = key;
            var v = value;
            rb.Checked += (_, _) => changed(v);
            panel.Children.Add(rb);
            buttons.Add((rb, value));
        }
        var border = Styled(new Border { Child = panel }, "Style.Recording.SegmentGroup");
        return (border, v =>
        {
            foreach (var (rb, val) in buttons) rb.IsChecked = EqualityComparer<T>.Default.Equals(val, v);
        });
    }

    /// <summary>A Win11 flyout popup below <paramref name="target"/>; its window is excluded from screen capture.</summary>
    public static Popup Flyout(UIElement target, UIElement content)
    {
        var border = Styled(new Border { Child = content }, "Style.Recording.Flyout");
        var popup = new Popup
        {
            Child = border,
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -12,
        };
        popup.Opened += (_, _) => ExcludeFromCapture(border);
        return popup;
    }

    /// <summary>Exclude the native window hosting <paramref name="visual"/> (popup, menu) from screen capture.</summary>
    public static void ExcludeFromCapture(Visual visual)
    {
        try
        {
            if (PresentationSource.FromVisual(visual) is HwndSource src)
                SetWindowDisplayAffinity(src.Handle, 0x11);
        }
        catch (Exception ex) { Log.Warn($"Exclude popup from capture failed: {ex.Message}"); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// <summary>Context menu with checkable items (fps / delay / key filter), excluded from capture.</summary>
    public static ContextMenu Menu<T>(IEnumerable<(T Value, string Header)> items, T selected, Action<T> chosen)
    {
        var menu = Styled(new ContextMenu(), "Style.Recording.Menu");
        foreach (var (value, header) in items)
        {
            var mi = Styled(new MenuItem { Header = header, IsChecked = EqualityComparer<T>.Default.Equals(value, selected) }, "Style.Recording.MenuItem");
            var v = value;
            mi.Click += (_, _) => chosen(v);
            menu.Items.Add(mi);
        }
        menu.Opened += (_, _) => ExcludeFromCapture(menu);
        return menu;
    }

    /// <summary>Win11-style modal confirmation. Returns true for OK.</summary>
    public static bool Confirm(Window? owner, string message, string? okText = null)
    {
        var dlg = new ConfirmDialog(message, okText ?? Loc.Get("Common.OK"));
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    private sealed class ConfirmDialog : Window
    {
        public ConfirmDialog(string message, string okText)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(0, 0, 0, 18) };
            var ok = Button("Common.OK", (_, _) => { DialogResult = true; }, accent: true);
            ((TextBlock)((StackPanel)ok.Content).Children[0]).Text = okText;
            ok.MinWidth = 80;
            var cancel = Button("Common.Cancel", (_, _) => { DialogResult = false; });
            cancel.MinWidth = 80;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
            var card = Styled(new Border { Child = new StackPanel { Children = { text, buttons } }, MinWidth = 300 }, "Style.Recording.Dialog");
            Content = new Grid { Margin = new Thickness(16), Children = { card } };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
                else if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
            };
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { /* ignore */ } };
            SourceInitialized += (_, _) =>
            {
                WindowHelper.SetToolWindow(this, true);
                WindowHelper.SetExcludeFromCapture(this, true);
            };
            // centre on the monitor under the cursor (after SizeToContent has measured the window); a small owner such as
            // the control bar would otherwise push the dialog off-screen
            Loaded += (_, _) =>
            {
                var wa = Core.Screen.Monitors.UnderCursor.WorkAreaPx;
                var b = WindowHelper.GetBoundsPx(this);
                WindowHelper.SetPositionPx(this, wa.X + (wa.Width - b.Width) / 2, wa.Y + (wa.Height - b.Height) / 2);
            };
        }
    }
}
