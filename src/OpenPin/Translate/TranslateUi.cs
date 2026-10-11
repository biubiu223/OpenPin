using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.Translate;

/// <summary>Small UI helpers for the Translate / Recognition windows (Win11 look through Theme + Translate.xaml tokens).</summary>
internal static class TranslateUi
{
    /// <summary>Themed tool window: Esc closes, centred on the monitor under the cursor.</summary>
    public static void SetupWindow(Window w, double width, double height)
    {
        w.Width = width;
        w.Height = height;
        w.MinWidth = 420;
        w.MinHeight = 260;
        w.ShowInTaskbar = true;
        w.Topmost = true;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SetResourceReference(Window.BackgroundProperty, "Brush.SurfaceAlt");
        w.SetResourceReference(Control.FontFamilyProperty, "Font.UI");
        w.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        if (Application.Current?.Resources["AppIcon"] is System.Windows.Media.ImageSource icon) w.Icon = icon;
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled) { w.Close(); e.Handled = true; }
        };
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var wa = Monitors.UnderCursor.WorkAreaPx;
                var b = WindowHelper.GetBoundsPx(w);
                WindowHelper.SetPositionPx(w, wa.X + Math.Max(0, (wa.Width - b.Width) / 2), wa.Y + Math.Max(0, (wa.Height - b.Height) / 2));
            }
            catch (Exception ex) { Log.Warn($"Placing window failed: {ex.Message}"); }
        };
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border { Child = child, Padding = padding ?? new Thickness(0) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.Translate.Card");
        return b;
    }

    public static Button Button(string textKey, bool accent, Action onClick, string? glyph = null)
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
        btn.SetResourceReference(FrameworkElement.StyleProperty, accent ? "Style.Translate.AccentButton" : "Style.Translate.Button");
        btn.Click += (_, _) => Run(textKey, onClick);
        return btn;
    }

    public static ButtonBase IconButton(string glyph, string toolTipKey, Action onClick, bool toggle = false)
    {
        ButtonBase b = toggle ? new ToggleButton() : new Button();
        b.Content = glyph;
        b.SetResourceReference(FrameworkElement.ToolTipProperty, toolTipKey);
        b.SetResourceReference(FrameworkElement.StyleProperty, "Style.Translate.IconButton");
        b.Click += (_, _) => Run(toolTipKey, onClick);
        return b;
    }

    public static TextBlock Header(string textKey)
    {
        var t = new TextBlock();
        t.SetResourceReference(TextBlock.TextProperty, textKey);
        t.SetResourceReference(FrameworkElement.StyleProperty, "Style.Translate.Header");
        return t;
    }

    public static TextBlock Caption(string? textKey = null, string? text = null)
    {
        var t = new TextBlock();
        if (textKey != null) t.SetResourceReference(TextBlock.TextProperty, textKey);
        else t.Text = text ?? "";
        t.SetResourceReference(FrameworkElement.StyleProperty, "Style.Translate.Caption");
        return t;
    }

    public static TextBox TextArea(bool readOnly = false)
    {
        var tb = new TextBox { IsReadOnly = readOnly };
        tb.SetResourceReference(FrameworkElement.StyleProperty, "Style.Translate.TextArea");
        return tb;
    }

    public static ComboBox Combo()
    {
        var c = new ComboBox();
        c.SetResourceReference(FrameworkElement.StyleProperty, "Style.Translate.ComboBox");
        return c;
    }

    /// <summary>Status text in the normal or error colour.</summary>
    public static void SetStatus(TextBlock status, string text, bool error = false)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.Danger" : "Brush.TextSecondary");
    }

    private static void Run(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error($"UI action '{what}' failed", ex);
            Services.Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    /// <summary>Copy text (UI thread); false + notification on failure.</summary>
    public static bool TryCopyText(string text)
    {
        try
        {
            Services.ClipboardService.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Copy failed", ex);
            Services.Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
            return false;
        }
    }
}
