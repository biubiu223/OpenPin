using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Pin.Native;

namespace OpenPin.Pin.Views;

/// <summary>
/// Transient overlay listing the pin groups with the current one highlighted (PIN-11). Stays while Ctrl is held
/// (Ctrl+` cycling) and at least ~0.9 s; click-through and non-activating.
/// </summary>
internal sealed class GroupSwitchOverlay : Window
{
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _shownAt;

    public GroupSwitchOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        var title = new TextBlock { Text = Loc.Get("Pin.Group.SwitchOverlayTitle"), FontSize = 12, Margin = new Thickness(8, 2, 8, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var stack = new StackPanel { MinWidth = 220 };
        stack.Children.Add(title);
        stack.Children.Add(_list);
        var border = new Border { Child = stack, Margin = new Thickness(16), Padding = new Thickness(4, 8, 4, 4) };
        border.SetResourceReference(StyleProperty, "Style.Pin.Flyout");
        Content = border;
        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            WindowHelper.SetClickThrough(this, true);
        };
        _timer.Tick += (_, _) =>
        {
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 900 || PinNative.IsKeyDown(PinNative.VK_CONTROL)) return;
            _timer.Stop();
            Hide();
        };
    }

    public void ShowGroups(IPinGroupHost host)
    {
        _list.Children.Clear();
        foreach (var g in host.Groups.Groups)
        {
            bool current = g.Id == host.Groups.CurrentId;
            var (on, total) = host.CountGroup(g.Id);
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), MinHeight = 32 };
            var item = new Border { Child = row, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 0, 10, 0) };
            if (current) item.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");

            var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (current) bar.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            row.Children.Add(bar);
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            if (g.Color != null && ColorText.TryParse(g.Color, out var c)) dot.Background = BitmapUtil.Frozen(new SolidColorBrush(c));
            else { dot.BorderThickness = new Thickness(1); dot.SetResourceReference(Border.BorderBrushProperty, "Brush.TextSecondary"); }
            row.Children.Add(dot);
            var count = new TextBlock { Text = $"{on}/{total}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), FontSize = 12 };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var name = new TextBlock { Text = g.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal };
            name.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.Accent" : "Brush.Text");
            row.Children.Add(name);
            _list.Children.Add(item);
        }

        if (!IsVisible) Show();
        UpdateLayout();
        var work = Monitors.UnderCursor.WorkAreaPx;
        var b = WindowHelper.GetBoundsPx(this);
        WindowHelper.SetPositionPx(this, work.X + (work.Width - b.Width) / 2, work.Y + (work.Height - b.Height) / 2);
        WindowHelper.SetTopmost(this, true);
        _shownAt = DateTime.Now;
        _timer.Start();
    }
}
