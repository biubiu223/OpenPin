using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.Services;

/// <summary>
/// Win11-style toast: borderless, topmost, never activated (ShowActivated=false + WS_EX_NOACTIVATE + MA_NOACTIVATE),
/// auto-hides after <see cref="NotifyOptions.DurationMs"/> (hover pauses), optional thumbnail and action buttons.
/// </summary>
internal sealed class ToastWindow : Window
{
    /// <summary>Transparent margin around the card that holds the drop shadow (DIPs).</summary>
    public const double ShadowMargin = 16;
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    private readonly DispatcherTimer _timer = new();
    private readonly int _durationMs;
    private DateTime _deadline;
    private bool _closing;

    public ToastWindow(NotifyOptions o)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;
        Title = o.Title;

        _durationMs = o.DurationMs <= 0 ? 0 : o.IsError ? Math.Max(o.DurationMs, 5000) : o.DurationMs;
        Content = BuildContent(o);

        _timer.Tick += (_, _) =>
        {
            if (DateTime.Now >= _deadline) Dismiss();
        };
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => StartTimer(Math.Max(1500, _durationMs / 2));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.SetNoActivate(this, true);
        WindowHelper.SetToolWindow(this, true);
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    public void FadeIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        if (_durationMs > 0) StartTimer(_durationMs);
    }

    private void StartTimer(int ms)
    {
        if (_durationMs <= 0 || _closing) return;
        _deadline = DateTime.Now.AddMilliseconds(ms);
        _timer.Start();
    }

    /// <summary>Fade out and close.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private FrameworkElement BuildContent(NotifyOptions o)
    {
        var card = new Border { Width = 360, Padding = new Thickness(14, 12, 8, 12) };
        card.SetResourceReference(StyleProperty, "Style.Services.Card");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Leading visual: thumbnail or icon glyph.
        FrameworkElement lead;
        if (o.Thumbnail != null)
        {
            var img = new Image
            {
                Source = o.Thumbnail,
                Stretch = Stretch.Uniform,
                MaxWidth = 64,
                MaxHeight = 64,
                VerticalAlignment = VerticalAlignment.Top,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var frame = new Border
            {
                Child = img,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 12, 0),
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceAlt");
            lead = frame;
        }
        else
        {
            var icon = new TextBlock
            {
                Text = o.IsError ? "" : "", // ErrorBadge / Info
                FontSize = 18,
                Margin = new Thickness(0, 1, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            icon.SetResourceReference(TextBlock.ForegroundProperty, o.IsError ? "Brush.Danger" : "Brush.Accent");
            lead = icon;
        }
        Grid.SetColumn(lead, 0);
        grid.Children.Add(lead);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = o.Title };
        title.SetResourceReference(StyleProperty, "Style.Services.ToastTitle");
        if (o.IsError) title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        stack.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(o.Message))
        {
            var msg = new TextBlock { Text = o.Message, Margin = new Thickness(0, 3, 0, 0), ToolTip = o.Message };
            msg.SetResourceReference(StyleProperty, "Style.Services.ToastMessage");
            stack.Children.Add(msg);
        }
        if (o.Actions.Count > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var a in o.Actions)
            {
                var btn = new Button { Content = a.Text, Margin = new Thickness(0, 0, 8, 0) };
                btn.SetResourceReference(StyleProperty, "Style.Services.Button");
                var action = a;
                btn.Click += (_, _) =>
                {
                    try { action.Execute(); }
                    catch (Exception ex) { Log.Error($"Toast action '{action.Text}' failed", ex); }
                    Dismiss();
                };
                actions.Children.Add(btn);
            }
            stack.Children.Add(actions);
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        var close = new Button { Content = "", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -4, 0, 0) };
        close.SetResourceReference(StyleProperty, "Style.Services.SmallIconButton");
        close.SetResourceReference(ToolTipProperty, "Services.Close");
        close.Click += (_, _) => Dismiss();
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        card.Child = grid;
        return new Border { Padding = new Thickness(ShadowMargin), Background = Brushes.Transparent, Child = card };
    }
}

/// <summary>Stacks toast windows bottom-right on the work area of the monitor under the cursor. UI thread only.</summary>
internal static class ToastHost
{
    private const int MaxToasts = 5;
    /// <summary>Distance between stacked cards = 2*ShadowMargin - overlap (DIPs).</summary>
    private const double StackOverlap = 22;

    private static readonly List<ToastWindow> Open = new();
    private static MonitorInfo? _monitor;

    public static void Show(NotifyOptions options)
    {
        while (Open.Count >= MaxToasts)
        {
            var oldest = Open[0];
            Open.RemoveAt(0);
            oldest.Dismiss();
        }

        if (Open.Count == 0 || _monitor == null) _monitor = Monitors.UnderCursor;
        var mon = _monitor;

        var w = new ToastWindow(options);
        // Put the (still invisible) window on the target monitor first so it gets that monitor's DPI before layout.
        WindowHelper.GetHandle(w);
        var work = mon.WorkAreaPx;
        WindowHelper.SetPositionPx(w, work.X + work.Width - (int)(400 * mon.Scale), work.Y + work.Height - (int)(160 * mon.Scale));

        Open.Add(w);
        w.Closed += (_, _) =>
        {
            Open.Remove(w);
            if (Open.Count == 0) _monitor = null;
            else Layout();
        };
        w.SizeChanged += (_, _) => Layout();
        w.DpiChanged += (_, _) => w.Dispatcher.BeginInvoke(Layout, DispatcherPriority.Loaded);
        w.Show();
        Layout();
        w.Dispatcher.BeginInvoke(() =>
        {
            Layout();
            w.FadeIn();
        }, DispatcherPriority.Loaded);
    }

    private static void Layout()
    {
        if (_monitor == null || Open.Count == 0) return;
        var work = _monitor.WorkAreaPx;
        int right = work.X + work.Width;
        int bottom = work.Y + work.Height;
        // Newest toast at the bottom, older ones stack upwards.
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            try
            {
                var b = WindowHelper.GetBoundsPx(t);
                double scale = WindowHelper.GetDpiScale(t);
                int x = right - b.Width;
                int y = bottom - b.Height;
                if (y < work.Y) y = work.Y;
                if (b.X != x || b.Y != y) WindowHelper.SetPositionPx(t, x, y);
                bottom = y + (int)Math.Round(StackOverlap * scale);
            }
            catch (Exception ex) { Log.Warn($"Toast layout failed: {ex.Message}"); }
        }
    }

    /// <summary>Close all toasts immediately (e.g. on exit).</summary>
    public static void CloseAll()
    {
        foreach (var t in Open.ToList()) t.Close();
        Open.Clear();
        _monitor = null;
    }
}
