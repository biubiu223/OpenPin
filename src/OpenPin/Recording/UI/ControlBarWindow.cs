using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.UI;

/// <summary>
/// Win11-style floating control bar of a recording session (REC-01 / PRO-03): record/stop, pause/resume, re-record, timer,
/// audio / webcam / keystroke / mouse options, fps, start delay, close. Drag the grip (or any empty part) to move it.
/// Excluded from screen capture.
/// </summary>
internal sealed class ControlBarWindow : Window
{
    /// <summary>Transparent room around the bar for its shadow.</summary>
    public const int Pad = 10;

    private readonly RecordingSession _session;
    private readonly Button _record, _pause, _restart, _audio, _camera, _keys, _mouse, _fps, _delay;
    private readonly TextBlock _timer;
    private readonly Ellipse _dot;
    private bool _userMoved;
    private SessionState _state = SessionState.Ready;

    public ControlBarWindow(RecordingSession session)
    {
        _session = session;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "OpenPin Recording";

        var s = RecordingConfig.Current;
        var grip = RecUi.Icon(Glyphs.Gripper, 14);
        grip.Margin = new Thickness(4, 0, 2, 0);
        grip.Cursor = Cursors.SizeAll;
        grip.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        grip.SetResourceReference(ToolTipProperty, "Recording.Move");

        _record = RecUi.IconButton(Glyphs.Record, "Recording.Start", (_, _) => _session.ToggleRecord(), "Style.Recording.RecordButton");
        _pause = RecUi.IconButton(Glyphs.Pause, "Recording.Pause", (_, _) => _session.TogglePause());
        _restart = RecUi.IconButton(Glyphs.Restart, "Recording.Restart", (_, _) => _session.Restart());

        _dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
        _dot.SetResourceReference(Shape.FillProperty, "Brush.Danger");
        _timer = RecUi.Styled(new TextBlock { Text = "00:00" }, "Style.Recording.Timer");

        _audio = RecUi.IconButton(Glyphs.Microphone, "Recording.Audio", (_, _) => OpenAudioFlyout());
        _camera = RecUi.IconButton(Glyphs.Camera, "Recording.Webcam", (_, _) => OpenCameraFlyout());
        _keys = RecUi.IconButton(Glyphs.Keyboard, "Recording.Keys", (_, _) => OpenKeysMenu());
        _mouse = RecUi.IconButton(Glyphs.Mouse, "Recording.Mouse", (_, _) => OpenMouseFlyout());
        _fps = RecUi.Styled(new Button(), "Style.Recording.TextButton");
        _fps.SetResourceReference(ToolTipProperty, "Recording.Fps");
        _fps.Click += (_, _) => OpenFpsMenu();
        _delay = RecUi.Styled(new Button(), "Style.Recording.TextButton");
        _delay.SetResourceReference(ToolTipProperty, "Recording.Delay");
        _delay.Click += (_, _) => OpenDelayMenu();
        var close = RecUi.IconButton(Glyphs.Close, "Recording.Close", (_, _) => _session.Cancel());

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                grip, _record, _pause, _restart, Separator(),
                _dot, _timer, Separator(),
                _audio, _camera, _keys, _mouse, _fps, _delay, Separator(),
                close,
            },
        };
        var bar = RecUi.Styled(new Border { Child = panel }, "Style.Recording.Bar");
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) return;
            try
            {
                DragMove();
                _userMoved = true;
            }
            catch (InvalidOperationException) { /* mouse already released */ }
        };
        Content = new Grid { Margin = new Thickness(Pad), Children = { bar } };

        RefreshOptions();
        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            ExcludedFromCapture = s.ExcludeControlsFromCapture && WindowHelper.SetExcludeFromCapture(this, true);
        };
        PreviewKeyDown += (_, e) => _session.HandleKey(e);
    }

    public bool ExcludedFromCapture { get; private set; }

    private static Rectangle Separator() => RecUi.Styled(new Rectangle(), "Style.ToolbarSeparator");

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    // ------------------------------------------------------------------ state

    public void SetState(SessionState state)
    {
        _state = state;
        bool active = state is SessionState.Recording or SessionState.Paused;
        bool idle = state == SessionState.Ready;
        _record.Content = active ? Glyphs.Stop : Glyphs.Record;
        _record.SetResourceReference(ToolTipProperty, active ? "Recording.Stop" : "Recording.Start");
        _record.ToolTip = Loc.Format(active ? "Recording.StopHint" : "Recording.StartHint", RecordingConfig.CaptureHotkey);
        _record.IsEnabled = state != SessionState.Stopping;
        _pause.IsEnabled = active;
        _pause.Content = state == SessionState.Paused ? Glyphs.Play : Glyphs.Pause;
        _pause.SetResourceReference(ToolTipProperty, state == SessionState.Paused ? "Recording.Resume" : "Recording.Pause");
        _restart.IsEnabled = active;
        _fps.IsEnabled = idle;
        _delay.IsEnabled = idle;
        _keys.IsEnabled = idle;
        _camera.IsEnabled = idle || active;
        _dot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _dot.Opacity = state == SessionState.Paused ? 0.4 : 1;
    }

    public void SetTimer(string text) => _timer.Text = text;

    /// <summary>Blink the recording dot (called by the session's UI timer).</summary>
    public void Blink(bool on)
    {
        if (_state == SessionState.Recording) _dot.Opacity = on ? 1 : 0.25;
    }

    /// <summary>Update the option buttons from the settings (accent color = feature on).</summary>
    public void RefreshOptions()
    {
        var s = RecordingConfig.Current;
        Accent(_audio, s.RecordMicrophone || s.RecordSystemAudio);
        _audio.Content = s.RecordMicrophone || !s.RecordSystemAudio ? Glyphs.Microphone : Glyphs.Volume;
        Accent(_camera, s.WebcamEnabled);
        Accent(_keys, s.ShowKeys);
        Accent(_mouse, s.HighlightClicks || s.ShowWheel);
        _fps.Content = Loc.Format("Recording.FpsValue", RecordingSettings.NormalizeFps(s.Fps));
        var delayContent = new StackPanel { Orientation = Orientation.Horizontal };
        delayContent.Children.Add(RecUi.Icon(Glyphs.Timer, 13));
        if (s.StartDelaySeconds > 0)
            delayContent.Children.Add(new TextBlock { Text = $" {s.StartDelaySeconds}s", VerticalAlignment = VerticalAlignment.Center });
        _delay.Content = delayContent;
        Accent(_delay, s.StartDelaySeconds > 0);
    }

    private static void Accent(Control c, bool on)
    {
        if (on) c.SetResourceReference(ForegroundProperty, "Brush.Accent");
        else c.SetResourceReference(ForegroundProperty, "Brush.Text");
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Place the bar under the region (right-aligned), else above, else inside its bottom edge; inside the monitor's work area.</summary>
    public void PlaceNear(Int32Rect region, bool force = false)
    {
        if (_userMoved && !force) return;
        UpdateLayout();
        var b = WindowHelper.GetBoundsPx(this);
        var work = Monitors.FromPointPx(region.X + region.Width / 2, region.Y + region.Height / 2).WorkAreaPx;
        int gap = RegionFrameWindow.Band + 4;
        int x = region.X + region.Width - b.Width + Pad;
        int y = region.Y + region.Height + gap - Pad;
        if (y + b.Height - Pad > work.Y + work.Height)
        {
            y = region.Y - gap - b.Height + Pad;
            if (y + Pad < work.Y) y = region.Y + region.Height - b.Height - 8 + Pad; // inside, bottom
        }
        x = Math.Clamp(x, work.X - Pad, Math.Max(work.X - Pad, work.X + work.Width - b.Width + Pad));
        y = Math.Clamp(y, work.Y - Pad, Math.Max(work.Y - Pad, work.Y + work.Height - b.Height + Pad));
        WindowHelper.SetPositionPx(this, x, y);
    }

    /// <summary>True if the visible bar overlaps the region (matters only if capture exclusion is unavailable).</summary>
    public bool Overlaps(Int32Rect region)
    {
        var b = WindowHelper.GetBoundsPx(this);
        var vis = new Rect(b.X + Pad, b.Y + Pad, b.Width - 2 * Pad, b.Height - 2 * Pad);
        return vis.IntersectsWith(new Rect(region.X, region.Y, region.Width, region.Height));
    }

    // ------------------------------------------------------------------ flyouts / menus

    private void ShowMenu(Button target, ContextMenu menu)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -12;
        menu.IsOpen = true;
    }

    private void OpenFpsMenu()
    {
        var s = RecordingConfig.Current;
        ShowMenu(_fps, RecUi.Menu(RecordingSettings.FpsChoices.Select(f => (f, Loc.Format("Recording.FpsValue", f))), RecordingSettings.NormalizeFps(s.Fps), f =>
        {
            s.Fps = f;
            RecordingConfig.Save();
            RefreshOptions();
        }));
    }

    private void OpenDelayMenu()
    {
        var s = RecordingConfig.Current;
        ShowMenu(_delay, RecUi.Menu(RecordingSettings.DelayChoices.Select(d => (d, d == 0 ? Loc.Get("Recording.DelayNone") : Loc.Format("Recording.DelaySeconds", d))),
            s.StartDelaySeconds, d =>
            {
                s.StartDelaySeconds = d;
                RecordingConfig.Save();
                RefreshOptions();
            }));
    }

    private void OpenKeysMenu()
    {
        var s = RecordingConfig.Current;
        int current = !s.ShowKeys ? 0 : s.KeyFilter == KeystrokeFilter.All ? 1 : 2;
        ShowMenu(_keys, RecUi.Menu(new[] { (0, Loc.Get("Recording.KeysOff")), (1, Loc.Get("Recording.KeysAll")), (2, Loc.Get("Recording.KeysCombos")) }, current, v =>
        {
            s.ShowKeys = v != 0;
            if (v != 0) s.KeyFilter = v == 1 ? KeystrokeFilter.All : KeystrokeFilter.CombosOnly;
            RecordingConfig.Save();
            RefreshOptions();
        }));
    }

    private void OpenAudioFlyout()
    {
        var s = RecordingConfig.Current;
        bool editable = _state == SessionState.Ready;
        var mic = RecUi.Check("Recording.Microphone", s.RecordMicrophone, v => { s.RecordMicrophone = v; RecordingConfig.Save(); RefreshOptions(); });
        mic.IsEnabled = editable;
        var micVol = RecUi.Slider(0, 2, s.MicrophoneVolume, v => { s.MicrophoneVolume = v; _session.SetGains(); }, 180);
        var sys = RecUi.Check("Recording.SystemAudio", s.RecordSystemAudio, v => { s.RecordSystemAudio = v; RecordingConfig.Save(); RefreshOptions(); });
        sys.IsEnabled = editable;
        var sysVol = RecUi.Slider(0, 2, s.SystemAudioVolume, v => { s.SystemAudioVolume = v; _session.SetGains(); }, 180);
        var hint = RecUi.Text("Recording.AudioMp4Only");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.MaxWidth = 220;
        hint.Margin = new Thickness(0, 8, 0, 0);
        var content = new StackPanel
        {
            Children =
            {
                RecUi.Text("Recording.Audio", "Style.Recording.FlyoutHeader"),
                Row(RecUi.Icon(Glyphs.Microphone), mic), Indent(micVol),
                Row(RecUi.Icon(Glyphs.Volume), sys), Indent(sysVol),
                hint,
            },
        };
        var popup = RecUi.Flyout(_audio, content);
        popup.Closed += (_, _) => RecordingConfig.Save();
        popup.IsOpen = true;
    }

    private void OpenCameraFlyout()
    {
        var s = RecordingConfig.Current;
        var enable = RecUi.Check("Recording.WebcamOn", s.WebcamEnabled, v =>
        {
            s.WebcamEnabled = v;
            RecordingConfig.Save();
            RefreshOptions();
            _session.WebcamToggled();
        });
        enable.IsEnabled = _state == SessionState.Ready;
        var (shape, _) = RecUi.Segmented(new[] { (WebcamShape.Circle, "Recording.WebcamCircle"), (WebcamShape.RoundedRect, "Recording.WebcamRoundedRect") },
            s.WebcamShape, v => { s.WebcamShape = v; _session.WebcamLayoutChanged(); });
        var (corner, _) = RecUi.Segmented(new[]
        {
            (WebcamCorner.TopLeft, "Recording.CornerTopLeft"), (WebcamCorner.TopRight, "Recording.CornerTopRight"),
            (WebcamCorner.BottomLeft, "Recording.CornerBottomLeft"), (WebcamCorner.BottomRight, "Recording.CornerBottomRight"),
        }, s.WebcamCorner, v => { s.WebcamCorner = v; _session.WebcamLayoutChanged(); });
        var size = RecUi.Slider(0.1, 0.5, s.WebcamSize, v => { s.WebcamSize = v; _session.WebcamLayoutChanged(); }, 160);
        shape.Margin = corner.Margin = new Thickness(0, 6, 0, 0);
        var content = new StackPanel
        {
            Children =
            {
                RecUi.Text("Recording.Webcam", "Style.Recording.FlyoutHeader"),
                enable, shape, corner,
                Row(RecUi.Text("Recording.WebcamSize"), size),
            },
        };
        var popup = RecUi.Flyout(_camera, content);
        popup.Closed += (_, _) => RecordingConfig.Save();
        popup.IsOpen = true;
    }

    private void OpenMouseFlyout()
    {
        var s = RecordingConfig.Current;
        var cursor = RecUi.Check("Recording.ShowCursor", s.ShowCursor, v => { s.ShowCursor = v; RefreshOptions(); });
        var clicks = RecUi.Check("Recording.ClickHighlight", s.HighlightClicks, v => { s.HighlightClicks = v; RefreshOptions(); });
        var wheel = RecUi.Check("Recording.ShowWheel", s.ShowWheel, v => { s.ShowWheel = v; RefreshOptions(); });
        int speed = s.ClickAnimationMs >= 700 ? 800 : s.ClickAnimationMs <= 350 ? 300 : 500;
        var (seg, _) = RecUi.Segmented(new[] { (800, "Recording.SpeedSlow"), (500, "Recording.SpeedNormal"), (300, "Recording.SpeedFast") },
            speed, v => s.ClickAnimationMs = v);
        var content = new StackPanel
        {
            Children =
            {
                RecUi.Text("Recording.Mouse", "Style.Recording.FlyoutHeader"),
                cursor, clicks, wheel,
                Row(RecUi.Text("Recording.ClickSpeed"), seg),
            },
        };
        var popup = RecUi.Flyout(_mouse, content);
        popup.Closed += (_, _) => RecordingConfig.Save();
        popup.IsOpen = true;
    }

    private static StackPanel Row(FrameworkElement a, FrameworkElement b)
    {
        a.Margin = new Thickness(0, 0, 8, 0);
        a.VerticalAlignment = b.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { a, b } };
    }

    private static FrameworkElement Indent(FrameworkElement e)
    {
        e.Margin = new Thickness(22, 0, 0, 2);
        e.HorizontalAlignment = HorizontalAlignment.Left;
        return e;
    }
}
