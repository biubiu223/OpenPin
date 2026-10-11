using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Actions;
using OpenPin.Recording.Capture;
using OpenPin.Recording.Codec;
using OpenPin.Services;

namespace OpenPin.Recording.UI;

internal enum SessionState { Ready, Countdown, Recording, Paused, Stopping, Closed }

/// <summary>
/// One recording session (REC-01): region frame + control bar (+ webcam preview), countdown, the <see cref="RecorderEngine"/>,
/// and the hand-over to the <see cref="PlaybackWindow"/>. Implements <see cref="IModalSession"/>: the capture hotkey starts /
/// stops, every other capture action is swallowed so no capture overlay can open on top of a recording.
/// </summary>
internal sealed class RecordingSession : IModalSession
{
    private readonly ScreenRecorder _owner;
    private readonly RegionFrameWindow _frame;
    private readonly ControlBarWindow _bar;
    private readonly DispatcherTimer _uiTimer;
    private WebcamPreviewWindow? _preview;
    private WebcamCapture? _webcam;
    private Task? _webcamStarting;
    private RecorderEngine? _engine;
    private DispatcherTimer? _countdownTimer;
    private int _countdownLeft;
    private int _blink;

    public RecordingSession(ScreenRecorder owner, Int32Rect regionPx)
    {
        _owner = owner;
        Region = regionPx;
        _frame = new RegionFrameWindow(regionPx);
        _frame.RegionChanged += r =>
        {
            Region = r;
            _bar?.PlaceNear(r);
            LayoutPreview();
            _bar?.SetTimer(SizeText);
        };
        _frame.KeyPressed += (_, e) => HandleKey(e);
        _bar = new ControlBarWindow(this);
        _bar.Closed += (_, _) => { if (State != SessionState.Closed) Cancel(force: true); };
        _uiTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => Tick();
    }

    public SessionState State { get; private set; } = SessionState.Ready;
    public Int32Rect Region { get; private set; }
    private string SizeText => $"{Region.Width} × {Region.Height}";

    public void Open()
    {
        _frame.Show();
        _bar.Show();
        _bar.SetState(SessionState.Ready);
        _bar.SetTimer(SizeText);
        _bar.PlaceNear(Region);
        WindowHelperActivate(_bar);
        AppServices.ActiveSession = this;
        if (RecordingConfig.Current.WebcamEnabled) WebcamToggled();
    }

    private static void WindowHelperActivate(Window w)
    {
        try { Core.Native.WindowHelper.ForceForeground(w); }
        catch (Exception ex) { Log.Warn($"Activate bar failed: {ex.Message}"); }
    }

    /// <summary>Bring the session to the user's attention (second Start while active).</summary>
    public void Flash()
    {
        WindowHelperActivate(_bar);
        Notify.Info(Loc.Get("Recording.AlreadyActive"));
    }

    // ------------------------------------------------------------------ IModalSession

    public bool TryHandleAction(string actionId)
    {
        if (State == SessionState.Closed) return false;
        if (actionId is ActionIds.Capture or ActionIds.CaptureRecord)
        {
            ToggleRecord();
            return true;
        }
        // no capture overlay (or another recording) on top of a recording session
        return actionId.StartsWith("capture", StringComparison.Ordinal);
    }

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
        else if (e.Key is Key.Enter or Key.Space && State == SessionState.Ready)
        {
            e.Handled = true;
            ToggleRecord();
        }
    }

    // ------------------------------------------------------------------ commands

    public void ToggleRecord()
    {
        switch (State)
        {
            case SessionState.Ready: StartWithDelay(); break;
            case SessionState.Countdown: StopCountdown(); _ = BeginAsync(); break;
            case SessionState.Recording or SessionState.Paused: _ = StopAsync(); break;
        }
    }

    public void TogglePause()
    {
        if (_engine == null) return;
        if (State == SessionState.Recording)
        {
            _engine.Pause();
            SetState(SessionState.Paused);
        }
        else if (State == SessionState.Paused)
        {
            _engine.Resume();
            SetState(SessionState.Recording);
        }
    }

    /// <summary>重新录制: throw away what was recorded and start again (honouring the start delay).</summary>
    public void Restart()
    {
        if (State is not (SessionState.Recording or SessionState.Paused)) return;
        _engine?.Abort();
        _engine?.Dispose();
        _engine = null;
        _uiTimer.Stop();
        SetState(SessionState.Ready);
        StartWithDelay();
    }

    /// <summary>Esc / close: confirm if something was recorded, then discard everything.</summary>
    public void Cancel(bool force = false)
    {
        if (State == SessionState.Closed) return;
        if (!force && State is SessionState.Recording or SessionState.Paused && _engine?.FramesCaptured > 0)
        {
            bool wasRecording = State == SessionState.Recording;
            if (wasRecording) TogglePause();
            if (!RecUi.Confirm(_bar, Loc.Get("Recording.ConfirmDiscard"), Loc.Get("Recording.Discard")))
            {
                if (wasRecording) TogglePause();
                return;
            }
        }
        StopCountdown();
        _engine?.Abort();
        _engine?.Dispose();
        _engine = null;
        Close();
    }

    private void StartWithDelay()
    {
        int delay = Math.Max(0, RecordingConfig.Current.StartDelaySeconds);
        if (delay == 0)
        {
            _ = BeginAsync();
            return;
        }
        _countdownLeft = delay;
        SetState(SessionState.Countdown);
        _frame.SetCountdown(_countdownLeft.ToString());
        _bar.SetTimer(_countdownLeft.ToString());
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            _countdownLeft--;
            if (_countdownLeft <= 0)
            {
                StopCountdown();
                _ = BeginAsync();
                return;
            }
            _frame.SetCountdown(_countdownLeft.ToString());
            _bar.SetTimer(_countdownLeft.ToString());
        };
        _countdownTimer.Start();
    }

    private void StopCountdown()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
        _frame.SetCountdown(null);
    }

    private async Task BeginAsync()
    {
        if (State is SessionState.Recording or SessionState.Paused or SessionState.Stopping or SessionState.Closed) return;
        var s = RecordingConfig.Current;
        if (Region.Width < 8 || Region.Height < 8)
        {
            Notify.Error(Loc.Get("Recording.TooSmall"));
            return;
        }
        SetState(SessionState.Stopping); // transitional: buttons disabled while devices open
        _bar.SetTimer(Loc.Get("Recording.Starting"));
        _frame.SetState(FrameState.Recording);
        if (_webcamStarting != null)
        {
            try { await _webcamStarting; } catch { /* reported already */ }
        }
        // The preview must never be recorded: if it cannot be excluded from capture, hide it while recording.
        if (_preview is { Excluded: false }) _preview.Hide();

        var options = new RecorderOptions
        {
            RegionPx = Region,
            Fps = s.Fps,
            MaxDurationMs = s.MaxDurationMinutes > 0 ? s.MaxDurationMinutes * 60_000.0 : 0,
            TempDir = ScreenRecorder.TempRoot,
            RecordKeys = s.ShowKeys,
            Microphone = s.RecordMicrophone,
            MicrophoneId = s.MicrophoneDeviceId,
            MicrophoneGain = (float)Math.Clamp(s.MicrophoneVolume, 0, 2),
            SystemAudio = s.RecordSystemAudio,
            SystemAudioGain = (float)Math.Clamp(s.SystemAudioVolume, 0, 2),
            Webcam = _webcam,
        };
        RecorderEngine engine;
        try
        {
            engine = new RecorderEngine(options);
            engine.Failed += ex => _frame.Dispatcher.BeginInvoke(() => OnEngineFailed(ex));
            engine.LimitReached += () => _frame.Dispatcher.BeginInvoke(() =>
            {
                Notify.Info(Loc.Format("Recording.LimitReached", s.MaxDurationMinutes));
                _ = StopAsync();
            });
            await Task.Run(engine.Start);
        }
        catch (Exception ex)
        {
            Log.Error("Starting the recording failed", ex);
            Notify.Error(Loc.Format("Recording.Failed", ex.Message));
            _frame.SetState(FrameState.Ready);
            SetState(SessionState.Ready);
            _bar.SetTimer(SizeText);
            return;
        }
        if (State == SessionState.Closed)
        {
            engine.Abort();
            return;
        }
        _engine = engine;
        foreach (var w in engine.Warnings)
            Notify.Error(Loc.Get(w switch { "microphone" => "Recording.WarnMicrophone", "system" => "Recording.WarnSystem", _ => "Recording.WarnKeyboard" }));
        SetState(SessionState.Recording);
        _uiTimer.Start();
        Tick();
    }

    private void OnEngineFailed(Exception ex)
    {
        if (State == SessionState.Closed) return;
        Notify.Error(Loc.Format("Recording.Failed", ex.Message));
        Cancel(force: true);
    }

    private async Task StopAsync()
    {
        var engine = _engine;
        if (engine == null || State is SessionState.Stopping or SessionState.Closed) return;
        SetState(SessionState.Stopping);
        _uiTimer.Stop();
        RecordingData data;
        try
        {
            data = await Task.Run(engine.Stop);
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the recording failed", ex);
            Notify.Error(Loc.Format("Recording.Failed", ex.Message));
            engine.Abort();
            _engine = null;
            Close();
            return;
        }
        _engine = null;
        var region = Region;
        Close();
        if (data.Video.Frames.Count == 0)
        {
            data.Delete();
            Notify.Error(Loc.Get("Recording.NothingRecorded"));
            return;
        }
        _owner.OpenPlayback(data, region);
    }

    private void SetState(SessionState state)
    {
        State = state;
        _bar.SetState(state);
        _frame.SetState(state switch
        {
            SessionState.Ready or SessionState.Countdown => FrameState.Ready,
            SessionState.Paused => FrameState.Paused,
            _ => FrameState.Recording,
        });
        if (state == SessionState.Paused) _bar.SetTimer(Timeline.FormatTime(_engine?.ElapsedMs ?? 0));
    }

    private void Tick()
    {
        if (_engine == null) return;
        _blink++;
        _bar.SetTimer(Timeline.FormatTime(_engine.ElapsedMs));
        _bar.Blink(_blink % 4 < 2);
    }

    public void SetGains()
    {
        var s = RecordingConfig.Current;
        if (_engine == null) return;
        _engine.MicrophoneGain = (float)Math.Clamp(s.MicrophoneVolume, 0, 2);
        _engine.SystemAudioGain = (float)Math.Clamp(s.SystemAudioVolume, 0, 2);
    }

    // ------------------------------------------------------------------ webcam

    /// <summary>Webcam switched on/off in the bar (ready state): open/close the camera and its preview.</summary>
    public void WebcamToggled()
    {
        if (RecordingConfig.Current.WebcamEnabled)
        {
            if (_webcam != null || _webcamStarting != null) return;
            _webcamStarting = StartWebcamAsync();
        }
        else _ = StopWebcamAsync();
    }

    private async Task StartWebcamAsync()
    {
        try
        {
            var cam = await WebcamCapture.StartAsync(RecordingConfig.Current.WebcamDeviceId);
            if (State == SessionState.Closed || !RecordingConfig.Current.WebcamEnabled)
            {
                await cam.DisposeAsync();
                return;
            }
            _webcam = cam;
            _preview = new WebcamPreviewWindow();
            cam.Frame += _preview.PushFrame;
            _preview.Show();
            LayoutPreview();
            _webcam.Frame += FirstFrameRelayout;
        }
        catch (Exception ex)
        {
            Log.Warn($"Webcam unavailable: {ex.Message}");
            Notify.Error(ex.Message.Contains("No camera", StringComparison.Ordinal)
                ? Loc.Get("Recording.NoCamera")
                : Loc.Format("Recording.CameraFailed", ex.Message));
        }
        finally
        {
            _webcamStarting = null;
        }
    }

    private void FirstFrameRelayout(byte[] _, int w, int h)
    {
        if (_webcam != null) _webcam.Frame -= FirstFrameRelayout;
        _frame.Dispatcher.BeginInvoke(LayoutPreview);
    }

    private async Task StopWebcamAsync()
    {
        var cam = _webcam;
        _webcam = null;
        _preview?.Close();
        _preview = null;
        if (cam != null) await cam.DisposeAsync();
    }

    public void WebcamLayoutChanged() => LayoutPreview();

    private void LayoutPreview()
    {
        if (_preview == null) return;
        try { _preview.Layout(Region, RecordingConfig.Current); }
        catch (Exception ex) { Log.Warn($"Webcam preview layout failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ close

    private void Close()
    {
        if (State == SessionState.Closed) return;
        State = SessionState.Closed;
        _uiTimer.Stop();
        StopCountdown();
        _ = StopWebcamAsync();
        _frame.Close();
        _bar.Close();
        if (ReferenceEquals(AppServices.ActiveSession, this)) AppServices.ActiveSession = null;
        _owner.OnSessionClosed(this);
    }
}
