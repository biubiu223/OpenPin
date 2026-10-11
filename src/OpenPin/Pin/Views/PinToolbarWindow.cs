using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Pin.Logic;

namespace OpenPin.Pin.Views;

/// <summary>Small borderless topmost window hosting the <see cref="AnnotationToolbar"/> of a pin (PIN-05).</summary>
internal sealed class PinToolbarWindow : Window
{
    /// <summary>Transparent room around the toolbar for its own shadow.</summary>
    public const int Pad = 10;

    public PinToolbarWindow(AnnotationToolbar toolbar, PinWindow owner)
    {
        Toolbar = toolbar;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Owner = owner;
        Content = new Grid { Margin = new Thickness(Pad), Children = { toolbar } };
        SourceInitialized += (_, _) => WindowHelper.SetToolWindow(this, true);
        // keyboard shortcuts typed while the toolbar has focus go to the pin (and from there to the surface)
        KeyDown += (_, e) => owner.HandleKey(e);
        SizeChanged += (_, _) => owner.PositionToolbar();
    }

    public AnnotationToolbar Toolbar { get; }

    /// <summary>Places the toolbar under (or above / inside) the pin's visible rect, inside that monitor's work area.</summary>
    public void PlaceNear(Int32Rect anchorPx)
    {
        if (!IsVisible) return;
        var b = WindowHelper.GetBoundsPx(this);
        var work = Monitors.FromPointPx(anchorPx.X + anchorPx.Width / 2, anchorPx.Y + anchorPx.Height / 2).WorkAreaPx;
        // the window has Pad px of transparent room; let the visible toolbar sit ~6px from the pin
        var r = PinGeometry.PlaceToolbar(PinGeometry.Inflate(anchorPx, Pad), b.Width, b.Height, PinGeometry.Inflate(work, Pad), 6 - 2 * Pad);
        WindowHelper.SetPositionPx(this, r.X, r.Y);
    }
}
