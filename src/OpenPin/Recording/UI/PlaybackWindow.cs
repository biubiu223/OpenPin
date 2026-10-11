using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Recording.Codec;
using OpenPin.Services;

namespace OpenPin.Recording.UI;

/// <summary>
/// Playback / export window shown after a recording stops (REC-03, PRO-07): preview with play/pause and 0.5×/1×/2× speed,
/// timeline with trim handles, overlay toggles (cursor, clicks, wheel, keys, webcam), audio toggles + audio preview,
/// GIF/MP4 and 轻量/标准/原始, then 保存 / 快速保存 / 复制 / 重新录制 / 关闭. Exports run in the background with progress
/// and cancel. Closing the window deletes the recording's temp files.
/// </summary>
internal sealed class PlaybackWindow : Window
{
    private readonly ScreenRecorder _owner;
    private readonly RecordingData _data;
    private readonly Int32Rect _region;
    private readonly DateTime _recordedAt = DateTime.Now;
    private readonly PreviewRenderer _renderer;
    private readonly AudioPreview? _audio;
    private readonly TrimTimeline _timeline = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Button _play;
    private readonly TextBlock _time, _info;
    private readonly StackPanel _actions;
    private readonly Grid _progressRow;
    private readonly ProgressBar _progress;
    private readonly TextBlock _progressText;
    private readonly CheckBox? _micCheck, _sysCheck;
    private OverlayOptions _overlays;
    private IReadOnlyDictionary<string, CursorShape>? _sprites;
    private RecordingFormat _format;
    private RecordingQuality _quality;
    private double _speed = 1, _playBase, _position;
    private bool _playing, _saved, _closingConfirmed, _micOn = true, _sysOn = true;
    private CancellationTokenSource? _exportCts;
    private int _syncTick;

    public PlaybackWindow(ScreenRecorder owner, RecordingData data, Int32Rect region)
    {
        _owner = owner;
        _data = data;
        _region = region;
        var s = RecordingConfig.Current;
        _format = string.Equals(s.DefaultFormat, "mp4", StringComparison.OrdinalIgnoreCase) ? RecordingFormat.Mp4 : RecordingFormat.Gif;
        _quality = s.Quality;
        _overlays = RecordingConfig.Overlays(s) with
        {
            ShowKeys = s.ShowKeys && data.Input.HasKeyboard,
            ShowWebcam = data.Webcam != null,
        };

        SetResourceReference(TitleProperty, "Recording.PlaybackTitle");
        SetResourceReference(BackgroundProperty, "Brush.SurfaceAlt");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        if (Application.Current?.TryFindResource("AppIcon") is ImageSource icon) Icon = icon;
        ShowInTaskbar = true;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _renderer = new PreviewRenderer(data, Dispatcher);
        if (data.HasAudio) _audio = new AudioPreview(data, () => _micOn, () => _sysOn);
        KeyPillRenderer.EnsureInitialized();
        if (data.Input.HasKeyboard) RenderSprites();

        // ---------------- preview
        var image = new Image { Source = _renderer.Bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) TogglePlay(); };
        var previewHost = new Border { Child = image, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Margin = new Thickness(12, 12, 12, 6) };
        previewHost.SetResourceReference(Border.BackgroundProperty, "Brush.Recording.PreviewBackground");
        Loaded += (_, _) =>
        {
            // 100 % = 1 recorded pixel per physical pixel
            double scale = WindowHelper.GetDpiScale(this);
            image.MaxWidth = _data.Video.Width / scale;
            image.MaxHeight = _data.Video.Height / scale;
        };

        // ---------------- transport row
        _play = RecUi.IconButton(Glyphs.Play, "Recording.Play", (_, _) => TogglePlay());
        _time = RecUi.Styled(new TextBlock { Text = "00:00.0 / 00:00.0", MinWidth = 120 }, "Style.Recording.Timer");
        var (speedSeg, _) = RecUi.Segmented(new[] { (0.5, "0.5×"), (1.0, "1×"), (2.0, "2×") }, 1.0, v => SetSpeed(v));
        speedSeg.Margin = new Thickness(8, 0, 0, 0);
        var trimHint = RecUi.Text("Recording.TrimHint");
        trimHint.TextTrimming = TextTrimming.CharacterEllipsis;
        trimHint.Margin = new Thickness(12, 0, 0, 0);
        var transport = new DockPanel { Margin = new Thickness(12, 0, 12, 0), LastChildFill = true };
        foreach (var e in new UIElement[] { _play, _time, speedSeg }) { DockPanel.SetDock(e, Dock.Left); transport.Children.Add(e); }
        transport.Children.Add(trimHint);

        // ---------------- timeline
        _timeline.Duration = data.DurationMs;
        _timeline.SetTrim(0, data.DurationMs);
        _timeline.Margin = new Thickness(12, 2, 12, 2);
        _timeline.Seeked += ms => { if (_playing) Pause(); SeekTo(ms); };
        _timeline.TrimChanged += (_, _) => UpdateInfo();

        // ---------------- overlay / audio toggles
        var toggles = new WrapPanel { Margin = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        var showLabel = RecUi.Text("Recording.Show");
        showLabel.Margin = new Thickness(0, 0, 10, 0);
        toggles.Children.Add(showLabel);
        AddToggle(toggles, "Recording.Cursor", _overlays.ShowCursor, v => _overlays = _overlays with { ShowCursor = v });
        AddToggle(toggles, "Recording.Clicks", _overlays.ShowClicks, v => _overlays = _overlays with { ShowClicks = v });
        AddToggle(toggles, "Recording.Wheel", _overlays.ShowWheel, v => _overlays = _overlays with { ShowWheel = v });
        if (data.Input.HasKeyboard)
            AddToggle(toggles, "Recording.KeysShort", _overlays.ShowKeys, v => _overlays = _overlays with { ShowKeys = v });
        if (data.Webcam != null)
            AddToggle(toggles, "Recording.Webcam", _overlays.ShowWebcam, v => _overlays = _overlays with { ShowWebcam = v });
        if (data.Microphone != null)
            _micCheck = AddToggle(toggles, "Recording.Microphone", true, v => { _micOn = v; _audio?.Sync(_position); }, separate: true);
        if (data.SystemAudio != null)
            _sysCheck = AddToggle(toggles, "Recording.SystemAudio", true, v => { _sysOn = v; _audio?.Sync(_position); }, separate: data.Microphone == null);

        // ---------------- export row
        var (formatSeg, _) = RecUi.Segmented(new[] { (RecordingFormat.Gif, "GIF"), (RecordingFormat.Mp4, "MP4") }, _format, v =>
        {
            _format = v;
            RecordingConfig.Current.DefaultFormat = v == RecordingFormat.Mp4 ? "mp4" : "gif";
            RecordingConfig.Save();
            UpdateInfo();
        });
        var (qualitySeg, _) = RecUi.Segmented(new[]
        {
            (RecordingQuality.Light, "Recording.QualityLight"), (RecordingQuality.Standard, "Recording.QualityStandard"), (RecordingQuality.Original, "Recording.QualityOriginal"),
        }, _quality, v =>
        {
            _quality = v;
            RecordingConfig.Current.Quality = v;
            RecordingConfig.Save();
            UpdateInfo();
        });
        _info = RecUi.Styled(new TextBlock { Margin = new Thickness(4, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis }, "Style.Recording.Label");
        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                RecUi.Button("Recording.Restart", (_, _) => Rerecord(), glyph: Glyphs.Restart),
                RecUi.Button("Recording.Copy", async (_, _) => await CopyAsync(), glyph: Glyphs.Copy),
                RecUi.Button("Recording.QuickSave", async (_, _) => await QuickSaveAsync(), glyph: Glyphs.QuickSave),
                RecUi.Button("Recording.Save", async (_, _) => await SaveAsync(), accent: true, glyph: Glyphs.Save),
                RecUi.Button("Recording.CloseWindow", (_, _) => Close()),
            },
        };
        var exportRow = new DockPanel { Margin = new Thickness(12, 6, 12, 12) };
        DockPanel.SetDock(formatSeg, Dock.Left);
        DockPanel.SetDock(qualitySeg, Dock.Left);
        DockPanel.SetDock(_actions, Dock.Right);
        exportRow.Children.Add(formatSeg);
        exportRow.Children.Add(qualitySeg);
        exportRow.Children.Add(_actions);
        exportRow.Children.Add(_info);

        // progress (replaces the export row while exporting)
        _progress = RecUi.Styled(new ProgressBar { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center }, "Style.Recording.ProgressBar");
        _progressText = RecUi.Styled(new TextBlock { Margin = new Thickness(12, 0, 12, 0) }, "Style.Recording.Label");
        var cancel = RecUi.Button("Recording.Cancel", (_, _) => _exportCts?.Cancel());
        _progressRow = new Grid { Margin = new Thickness(12, 6, 12, 12), Visibility = Visibility.Collapsed, MinHeight = 30 };
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition());
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_progressText, 1);
        Grid.SetColumn(cancel, 2);
        _progressRow.Children.Add(_progress);
        _progressRow.Children.Add(_progressText);
        _progressRow.Children.Add(cancel);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bottom = new Grid { Children = { exportRow, _progressRow } };
        int row = 0;
        foreach (var e in new UIElement[] { previewHost, transport, _timeline, toggles, bottom }) { Grid.SetRow(e, row++); root.Children.Add(e); }
        Content = root;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => OnTick();

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => PlaceWindow();
        Closing += OnClosing;
        Closed += (_, _) => Cleanup();
        UpdateInfo();
        SeekTo(0);
        UpdateAudioToggles();
    }

    private CheckBox AddToggle(Panel panel, string key, bool value, Action<bool> changed, bool separate = false)
    {
        var c = RecUi.Check(key, value, v => { changed(v); RequestFrame(); });
        c.Margin = new Thickness(separate ? 18 : 0, 2, 14, 2);
        panel.Children.Add(c);
        return c;
    }

    private void RenderSprites()
    {
        try
        {
            _sprites = KeyPillRenderer.Render(KeyPillRenderer.Labels(_data.Input), _data.Video.Width, _data.Video.Height);
            _renderer.KeySprites = _sprites;
        }
        catch (Exception ex) { Log.Error("Rendering key overlays failed", ex); }
    }

    private void PlaceWindow()
    {
        try
        {
            var mon = Monitors.FromPointPx(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
            var wa = mon.WorkAreaPx;
            double scale = mon.Scale;
            // preview at 100 % if it fits; chrome ≈ 250 DIP
            double w = Math.Clamp(_data.Video.Width / scale + 60, 760, wa.Width / scale * 0.9);
            double h = Math.Clamp(_data.Video.Height / scale + 270, 480, wa.Height / scale * 0.9);
            int wpx = (int)(w * scale), hpx = (int)(h * scale);
            WindowHelper.SetBoundsPx(this, new Int32Rect(wa.X + (wa.Width - wpx) / 2, wa.Y + (wa.Height - hpx) / 2, wpx, hpx));
        }
        catch (Exception ex) { Log.Warn($"Placing playback window failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ playback

    private double TrimStart => _timeline.TrimStart;
    private double TrimEnd => _timeline.TrimEnd;

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_position >= TrimEnd - 1 || _position < TrimStart) _position = TrimStart;
        _playBase = _position;
        _clock.Restart();
        _playing = true;
        _play.Content = Glyphs.Pause;
        _play.SetResourceReference(ToolTipProperty, "Recording.PausePlayback");
        _audio?.Play(_position, _speed);
        _timer.Start();
    }

    private void Pause()
    {
        _playing = false;
        _clock.Stop();
        _timer.Stop();
        _audio?.Pause();
        _play.Content = Glyphs.Play;
        _play.SetResourceReference(ToolTipProperty, "Recording.Play");
    }

    private void SetSpeed(double speed)
    {
        if (_playing)
        {
            _playBase = _position;
            _clock.Restart();
            _audio?.Play(_position, speed);
        }
        _speed = speed;
    }

    private void OnTick()
    {
        if (!_playing) return;
        double pos = _playBase + _clock.Elapsed.TotalMilliseconds * _speed;
        if (pos >= TrimEnd)
        {
            // loop the trimmed range
            pos = TrimStart;
            _playBase = pos;
            _clock.Restart();
            _audio?.Play(pos, _speed);
        }
        _position = pos;
        _timeline.SetPosition(pos);
        UpdateTimeLabel();
        RequestFrame();
        if (++_syncTick % 30 == 0 && _speed == 1) _audio?.Sync(pos);
    }

    private void SeekTo(double ms)
    {
        _position = Math.Clamp(ms, 0, _data.DurationMs);
        _timeline.SetPosition(_position);
        UpdateTimeLabel();
        RequestFrame();
    }

    private void Step(int frames)
    {
        if (_playing) Pause();
        double interval = 1000.0 / Math.Max(1, _data.Video.Fps);
        SeekTo(_position + frames * interval);
    }

    private void RequestFrame() => _renderer.Request(Math.Min(_position, _data.DurationMs - 0.001), _overlays);

    private void UpdateTimeLabel() =>
        _time.Text = $"{Timeline.FormatTime(_position, true)} / {Timeline.FormatTime(_data.DurationMs, true)}";

    private void UpdateInfo()
    {
        var plan = Timeline.Plan(_format, _quality, _data.Video.Fps, _data.Video.Width, _data.Video.Height);
        _info.Text = Loc.Format("Recording.OutputInfo", plan.Width, plan.Height, plan.Fps, Timeline.FormatTime(TrimEnd - TrimStart, true));
        UpdateAudioToggles();
    }

    private void UpdateAudioToggles()
    {
        bool mp4 = _format == RecordingFormat.Mp4;
        foreach (var c in new[] { _micCheck, _sysCheck })
        {
            if (c == null) continue;
            c.IsEnabled = true; // preview still plays; GIF export simply has no audio
            c.ToolTip = mp4 ? null : Loc.Get("Recording.AudioMp4Only");
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_exportCts != null)
        {
            if (e.Key == Key.Escape) { _exportCts.Cancel(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            case Key.Home: if (_playing) Pause(); SeekTo(TrimStart); break;
            case Key.End: if (_playing) Pause(); SeekTo(TrimEnd - 1); break;
            case Key.OemOpenBrackets: _timeline.SetTrim(_position, TrimEnd); UpdateInfo(); break;
            case Key.OemCloseBrackets: _timeline.SetTrim(TrimStart, _position); UpdateInfo(); break;
            case Key.S when ctrl && shift: _ = QuickSaveAsync(); break;
            case Key.S when ctrl: _ = SaveAsync(); break;
            case Key.C when ctrl: _ = CopyAsync(); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ export

    private static string Ext(RecordingFormat f) => f == RecordingFormat.Mp4 ? ".mp4" : ".gif";

    /// <summary>File name (no extension) from the template; may contain sub folders for quick save.</summary>
    private string ExpandName(bool allowFolders)
    {
        var s = RecordingConfig.Current;
        var template = string.IsNullOrWhiteSpace(s.FileNameTemplate) ? "OpenPin_Rec_$yyyy-MM-dd_HH-mm-ss$" : s.FileNameTemplate;
        string name;
        try { name = PathTemplate.Expand(template, new SaveContext { Timestamp = _recordedAt }, _data.Video.Width, _data.Video.Height).TrimEnd('\\'); }
        catch (Exception ex) { Log.Warn($"File name template failed: {ex.Message}"); name = ""; }
        if (!allowFolders || Path.IsPathRooted(name)) name = Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(name) ? "OpenPin_Rec" : name;
    }

    private static string QuickSaveFolder()
    {
        var configured = RecordingConfig.Current.QuickSaveFolder;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
                if (Path.IsPathRooted(expanded)) return Path.GetFullPath(expanded);
            }
            catch { /* fall back */ }
        }
        return AppPaths.DefaultPicturesDir;
    }

    private async Task<bool> ExportAsync(string path, RecordingFormat format)
    {
        if (_exportCts != null) return false;
        if (_playing) Pause();
        _exportCts = new CancellationTokenSource();
        _progress.Value = 0;
        _progressText.Text = Loc.Format("Recording.Exporting", "0%");
        _progressRow.Visibility = Visibility.Visible;
        ((Panel)_progressRow.Parent).Children[0].Visibility = Visibility.Hidden;
        var progress = new Progress<double>(p =>
        {
            _progress.Value = p;
            _progressText.Text = Loc.Format("Recording.Exporting", $"{p * 100:0}%");
        });
        var options = new ExportOptions
        {
            Format = format,
            Quality = _quality,
            TrimStartMs = TrimStart,
            TrimEndMs = TrimEnd,
            Overlays = _overlays,
            KeySprites = _sprites,
            Dither = RecordingConfig.Current.GifDither,
            IncludeMicrophone = _micOn,
            IncludeSystemAudio = _sysOn,
        };
        var sw = Stopwatch.StartNew();
        try
        {
            await RecordingExporter.ExportAsync(_data, options, path, progress, _exportCts.Token);
            Log.Info($"Exported {path} ({format}, {_quality}) in {sw.ElapsedMilliseconds} ms, {new FileInfo(path).Length / 1024} KB");
            _saved = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Export to {path} failed", ex);
            Notify.Error(Loc.Format("Recording.ExportFailed", ex.Message));
            return false;
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            _progressRow.Visibility = Visibility.Collapsed;
            ((Panel)_progressRow.Parent).Children[0].Visibility = Visibility.Visible;
        }
    }

    private async Task SaveAsync()
    {
        if (_exportCts != null) return;
        var s = RecordingConfig.Current;
        string folder = Directory.Exists(s.LastSaveFolder) ? s.LastSaveFolder : QuickSaveFolder();
        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("Recording.SaveDialogTitle"),
            Filter = "GIF (*.gif)|*.gif|MP4 (*.mp4)|*.mp4",
            FilterIndex = _format == RecordingFormat.Mp4 ? 2 : 1,
            FileName = ExpandName(allowFolders: false),
            DefaultExt = Ext(_format),
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = folder,
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var format = ext == ".mp4" ? RecordingFormat.Mp4 : ext == ".gif" ? RecordingFormat.Gif : (dlg.FilterIndex == 2 ? RecordingFormat.Mp4 : RecordingFormat.Gif);
        if (ext is not (".mp4" or ".gif")) path += Ext(format);
        s.LastSaveFolder = Path.GetDirectoryName(path) ?? "";
        RecordingConfig.Save();
        if (await ExportAsync(path, format)) NotifySaved(path);
    }

    private async Task QuickSaveAsync()
    {
        if (_exportCts != null) return;
        string path;
        try
        {
            path = PathTemplate.MakeUnique(Path.GetFullPath(Path.Combine(QuickSaveFolder(), ExpandName(allowFolders: true) + Ext(_format))));
        }
        catch (Exception ex)
        {
            Notify.Error(Loc.Format("Recording.ExportFailed", ex.Message));
            return;
        }
        if (await ExportAsync(path, _format)) NotifySaved(path);
    }

    private async Task CopyAsync()
    {
        if (_exportCts != null) return;
        var dir = AppPaths.Ensure(Path.Combine(ScreenRecorder.TempRoot, "export"));
        var path = PathTemplate.MakeUnique(Path.Combine(dir, ExpandName(allowFolders: false) + Ext(_format)));
        if (!await ExportAsync(path, _format)) return;
        try
        {
            ClipboardService.SetFiles(new[] { path });
            Notify.Info(Loc.Get("Recording.Copied"));
        }
        catch (Exception ex)
        {
            Log.Error("Copying the recording failed", ex);
            Notify.Error(Loc.Format("Recording.ExportFailed", ex.Message));
        }
    }

    private static void NotifySaved(string path)
    {
        Notify.Show(new NotifyOptions
        {
            Title = Loc.Get("Recording.Saved"),
            Message = path,
            DurationMs = 5000,
            Actions = new[] { new NotifyAction(Loc.Get("Recording.OpenFolder"), () => SaveService.RevealInExplorer(path)) },
        });
    }

    private void Rerecord()
    {
        if (_exportCts != null) return;
        if (!_saved && !RecUi.Confirm(this, Loc.Get("Recording.ConfirmRerecord"))) return;
        _closingConfirmed = true;
        Close();
        _owner.Start(_region);
    }

    // ------------------------------------------------------------------ close

    /// <summary>Close without asking and delete the temp files (app exit).</summary>
    public void CloseAndDiscard()
    {
        _closingConfirmed = true;
        _exportCts?.Cancel();
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        if (_exportCts != null)
        {
            _exportCts.Cancel();
            e.Cancel = true; // the export finishes cancelling first; the user can close again
            return;
        }
        if (!_saved && !RecUi.Confirm(this, Loc.Get("Recording.ConfirmCloseUnsaved")))
        {
            e.Cancel = true;
            return;
        }
        _closingConfirmed = true;
    }

    private void Cleanup()
    {
        _timer.Stop();
        _audio?.Dispose();
        _renderer.Dispose();
        _data.Delete();
    }
}
