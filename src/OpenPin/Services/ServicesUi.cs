using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.Services;

/// <summary>Small UI helpers shared by the Services windows (Win11 look via Theme / Services.xaml tokens).</summary>
internal static class ServicesUi
{
    /// <summary>Common setup for a dialog-like tool window: themed background/font, Esc closes, centred on the monitor under the cursor.</summary>
    public static void SetupDialog(Window w, double width, double height)
    {
        w.Width = width;
        w.Height = height;
        w.MinWidth = 360;
        w.MinHeight = 240;
        w.ShowInTaskbar = true;
        w.Topmost = true;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SetResourceReference(Window.BackgroundProperty, "Brush.SurfaceAlt");
        w.SetResourceReference(Control.FontFamilyProperty, "Font.UI");
        w.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        if (Application.Current?.Resources["AppIcon"] is System.Windows.Media.ImageSource icon) w.Icon = icon;
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { w.Close(); e.Handled = true; }
        };
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var mon = Monitors.UnderCursor;
                var b = WindowHelper.GetBoundsPx(w);
                var wa = mon.WorkAreaPx;
                // Size is in DIPs of the current monitor; re-measure after the move if the DPI differs (WPF handles it).
                int x = wa.X + Math.Max(0, (wa.Width - b.Width) / 2), y = wa.Y + Math.Max(0, (wa.Height - b.Height) / 2);
                WindowHelper.SetPositionPx(w, x, y);
            }
            catch (Exception ex) { Log.Warn($"Placing window failed: {ex.Message}"); }
        };
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border { Child = child, Padding = padding ?? new Thickness(0) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.Services.Card");
        b.Effect = null; // cards inside a window: border only (shadow is for floating surfaces)
        return b;
    }

    public static Button Button(string textKey, bool accent, RoutedEventHandler onClick, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
        {
            var icon = new TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            content.Children.Add(icon);
        }
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.TextProperty, textKey);
        content.Children.Add(label);
        var btn = new Button { Content = content };
        btn.SetResourceReference(FrameworkElement.StyleProperty, accent ? "Style.Services.AccentButton" : "Style.Services.Button");
        btn.Click += onClick;
        return btn;
    }

    /// <summary>Segmented group of toggle buttons; exactly one checked. Returns the panel.</summary>
    public static StackPanel Segmented<T>(IReadOnlyList<(T Value, string TextKey)> items, T selected, Action<T> changed) where T : struct
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        foreach (var (value, key) in items)
        {
            var tb = new ToggleButton { IsChecked = EqualityComparer<T>.Default.Equals(value, selected), Margin = new Thickness(0, 0, 4, 0) };
            tb.SetResourceReference(ContentControl.ContentProperty, key);
            tb.SetResourceReference(FrameworkElement.StyleProperty, "Style.Services.Button");
            var v = value;
            tb.Click += (_, _) =>
            {
                foreach (var other in buttons) other.IsChecked = ReferenceEquals(other, tb);
                changed(v);
            };
            buttons.Add(tb);
            panel.Children.Add(tb);
        }
        return panel;
    }

    public static TextBlock Label(string text, bool secondary = true, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "Brush.TextSecondary" : "Brush.Text");
        return t;
    }

    /// <summary>Open an http/https URL in the default browser. Other schemes are refused.</summary>
    public static bool OpenUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Opening link failed", ex);
            Notify.Error(Loc.Format("Services.QrOpenFailed", ex.Message));
            return false;
        }
    }

    public static bool TryCopyText(string text)
    {
        try
        {
            ClipboardService.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Copy failed", ex);
            Notify.Error(Loc.Format("Services.CopyFailed", ex.Message));
            return false;
        }
    }
}
