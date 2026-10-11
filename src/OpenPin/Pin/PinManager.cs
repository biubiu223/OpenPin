using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Services;

namespace OpenPin.Pin;

/// <summary>
/// PIN MODULE entry point (implements <see cref="IPinManager"/>).
/// PHASE-0 STUB: shows a bare image window so other modules can be tested. The Pin module replaces it.
/// </summary>
public sealed class PinManager : IPinManager
{
    private readonly List<Window> _windows = new();

    public int VisibleCount => _windows.Count;

    public void Initialize() { }

    public void Shutdown() { }

    public void PinImage(BitmapSource image, Int32Rect? screenRectPx = null, string? title = null)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Image { Source = image, Stretch = Stretch.None },
        };
        w.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) w.Close(); else w.DragMove(); };
        w.Closed += (_, _) => _windows.Remove(w);
        w.Show();
        if (screenRectPx is { } r) WindowHelper.SetPositionPx(w, r.X, r.Y);
        _windows.Add(w);
    }

    public void PinContent(PinContent content, Int32Rect? screenRectPx = null)
    {
        if (content is ImagePinContent img) PinImage(img.Image, screenRectPx);
        else Notify.Info(Loc.Get("Common.NotImplemented"));
    }

    public void PinFromClipboard()
    {
        var content = ClipboardService.GetPinContent();
        if (content == null) Notify.Info(Loc.Get("Pin.ClipboardEmpty"));
        else PinContent(content);
    }

    public void PinFromFile() => Notify.Info(Loc.Get("Common.NotImplemented"));
    public void RestoreLastClosed() => Notify.Info(Loc.Get("Common.NotImplemented"));
    public void CloseAll() { foreach (var w in _windows.ToList()) w.Close(); }
    public void DestroyAll() => CloseAll();
    public void ToggleHideAll() { }
    public void ToggleClickThroughUnderCursor() { }
    public void SwitchToNextGroup() { }
    public void PopulateTrayMenu(ItemCollection items) { }
}
