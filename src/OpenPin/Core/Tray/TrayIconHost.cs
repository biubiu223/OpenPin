using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using OpenPin.Core.Actions;
using OpenPin.Core.Settings;

namespace OpenPin.Core.Tray;

/// <summary>Notification-area icon + context menu. The menu is rebuilt every time it opens (live counts).</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ContextMenu _menu = new();

    public TrayIconHost()
    {
        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/AppIcon.ico")),
            ToolTipText = "OpenPin",
            NoLeftClickDelay = true,
            ContextMenu = _menu,
            MenuActivation = PopupActivationMode.RightClick,
        };
        _menu.SetResourceReference(FrameworkElement.StyleProperty, "Style.ContextMenu"); // light / dark (Theme.xaml)
        _icon.TrayLeftMouseUp += (_, _) => OnLeftClick();
        _icon.PreviewTrayContextMenuOpen += (_, _) => BuildMenu();
        BuildMenu();
    }

    public TaskbarIcon Icon => _icon;

    private static void OnLeftClick()
    {
        switch (AppServices.Config.General.TrayLeftClick)
        {
            case TrayClickAction.Capture: AppServices.Actions.Execute(ActionIds.Capture); break;
            case TrayClickAction.PinClipboard: AppServices.Actions.Execute(ActionIds.PinClipboard); break;
        }
    }

    private void BuildMenu()
    {
        var items = _menu.Items;
        items.Clear();

        items.Add(ActionItem(ActionIds.Capture));
        items.Add(ActionItem(ActionIds.CaptureCustom));
        items.Add(ActionItem(ActionIds.CaptureLong));
        items.Add(ActionItem(ActionIds.CaptureRecord));
        items.Add(ActionItem(ActionIds.TranslateMirror));
        items.Add(new Separator());
        items.Add(ActionItem(ActionIds.PinClipboard));
        items.Add(ActionItem(ActionIds.PinFromFile));
        items.Add(ActionItem(ActionIds.PinRestore));

        try { AppServices.Pins?.PopulateTrayMenu(items); }
        catch (Exception ex) { Log.Error("PopulateTrayMenu failed", ex); }

        items.Add(new Separator());
        var toggle = ActionItem(ActionIds.ToggleHotkeys);
        toggle.IsCheckable = true;
        toggle.IsChecked = AppServices.Hotkeys.Suspended;
        items.Add(toggle);
        items.Add(ActionItem(ActionIds.OpenSettings));
        items.Add(new MenuItem
        {
            Header = Loc.Get("Tray.OpenDataFolder"),
            Command = new RelayCommand(() => Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true })),
        });
        items.Add(new Separator());
        items.Add(ActionItem(ActionIds.Exit));
    }

    private static MenuItem ActionItem(string actionId)
    {
        var action = AppServices.Actions.Get(actionId);
        var item = new MenuItem { Header = action?.DisplayName ?? actionId };
        if (AppServices.Hotkeys.Bindings.TryGetValue(actionId, out var hk) && !hk.IsEmpty)
            item.InputGestureText = hk.ToString();
        item.Click += (_, _) => AppServices.Actions.Execute(actionId);
        return item;
    }

    public void ShowBalloon(string title, string message, bool error = false) =>
        _icon.ShowBalloonTip(title, message, error ? BalloonIcon.Error : BalloonIcon.Info);

    public void Dispose() => _icon.Dispose();
}
