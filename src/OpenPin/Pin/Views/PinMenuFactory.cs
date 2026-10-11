using System.Windows;
using System.Windows.Controls;
using OpenPin.Core;

namespace OpenPin.Pin.Views;

/// <summary>Builds Win11-style menu items (icon column + text + shortcut) for pin menus and the tray section.</summary>
internal sealed class PinMenuFactory
{
    public PinMenuFactory(PinWindow window) => Window = window;

    public PinWindow Window { get; }

    public static ContextMenu NewContextMenu()
    {
        var cm = new ContextMenu();
        if (Application.Current?.TryFindResource("Style.Pin.ContextMenu") is Style s) cm.Style = s;
        return cm;
    }

    public static MenuItem Make(string header, string? glyph, Action? execute, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture != null) mi.InputGestureText = gesture;
        if (glyph != null) mi.Icon = Glyph(glyph);
        if (execute != null)
        {
            mi.Click += (_, e) =>
            {
                e.Handled = true;
                try { execute(); }
                catch (Exception ex)
                {
                    Log.Error($"Pin menu action '{header}' failed", ex);
                    Services.Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
                }
            };
        }
        return mi;
    }

    public static TextBlock Glyph(string glyph)
    {
        var tb = new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        return tb;
    }

    public MenuItem Item(string key, string? glyph, string? gesture, Action execute, bool enabled = true) =>
        Make(Loc.Get(key), glyph, execute, gesture, enabled);

    public MenuItem Raw(string header, string? glyph, Action execute, string? gesture = null) => Make(header, glyph, execute, gesture);

    public MenuItem Check(string key, bool isChecked, Action execute, string? gesture = null, bool enabled = true)
    {
        var mi = Make(Loc.Get(key), null, execute, gesture, enabled);
        mi.IsCheckable = false; // we toggle state ourselves; IsChecked only drives the check glyph
        mi.IsChecked = isChecked;
        return mi;
    }

    public MenuItem Sub(string key, string? glyph, bool enabled = true) => Make(Loc.Get(key), glyph, null, null, enabled);

    public static Separator Sep() => new();
}
