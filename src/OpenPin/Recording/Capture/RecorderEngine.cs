using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenPin.Core;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.Capture;

/// <summary>What to record besides the screen.</summary>
internal sealed class RecorderOptions
{
    public required Int32Rect RegionPx { get; init; }
    public int Fps { get; init; } = 16;
    /// <summary>0 = unlimited.</summary>
    public double MaxDurationMs { get; init; }
    /// <summary>Folder for the temp files.</summary>
    public required string TempDir { get; init; }
    /// <summary>Install the keyboard hook (keystroke overlay).</summary>
    public bool RecordKeys { get; init; }
    public bool Microphone { get; init; }
    public string? MicrophoneId { get; init; }
    public float MicrophoneGain { get; init; } = 1f;
    public bool SystemAudio { get; init; }
    public float SystemAudioGain { get; init; } = 1f;
    /// <summary>An already running camera (owned by the caller, who also shows its live preview).</summary>
    public WebcamCapture? Webcam { get; init; }
    public int WebcamFps { get; init; } = 15;
}

/// <summary>
/// The recording pipeline (REC-02, PRO-03). Threads:
/// <list type="number">
/// <item>Capture: Stopwatch-scheduled slots at 1/fps (timer resolution raised to 1 ms). If it falls behind it skips slots
/// instead of drifting; each frame is stamped with its slot time, so durations stay exact. Grabs the region (GDI, persistent
/// DIB) and samples the cursor.</item>
/// <item>Encoder: delta-encodes + compresses frames into the <see cref="FrameStoreWriter"/> temp file.</item>
/// <item>Input hooks (WH_MOUSE_LL / WH_KEYBOARD_LL): clicks, wheel, keys with recording-clock timestamps.</item>
/// <item>WASAPI microphone / loopback threads → <see cref="AudioTrackWriter"/>s aligned to the recording clock.</item>
/// <item>Webcam frames (MediaCapture worker) → a second frame store at ≤ 15 fps.</item>
/// </list>
/// Screen frames travel through a fixed pool of buffers: if the encoder cannot keep up the capture thread drops frames, so
/// memory never grows. Pausing stops the recording clock; audio/input arriving while paused is discarded.
/// </summary>
internal sealed class RecorderEngine : IDisposable
{
    private const int PoolSize = 4;

    private readonly RecorderOptions _o;
    private readonly Int32Rect _region;
    private readonly string _id = $"rec-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly FrameStoreWriter _writer;
    private readonly InputLog _input = new();
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly BlockingCollection<(byte[] Buffer, double TimeMs, CursorSample Cursor)> _queue = new(PoolSize);
    private readonly Stopwatch _sw = new();
    private readonly object _clockGate = new();
    private readonly ManualResetEventSlim _running = new(true);
    private Thread? _captureThread, _encoderThread;
    private InputHooks? _hooks;
    private WasapiCapture? _micCapture, _sysCapture;
    private AudioTrackWriter? _micWriter, _sysWriter;
    private readonly object _camGate = new();
    private FrameStoreWriter? _camWriter;
    private byte[]? _camSpare;
    private double _camLast = double.NegativeInfinity;
    private volatile bool _stop;
    private long _pausedTicks, _pauseStartTicks = -1;
    private int _captured, _dropped;
    private double _lastFrameTime;
    private RecordingData? _result;
    private volatile Exception? _error;

    public RecorderEngine(RecorderOptions options)
    {
        _o = options;
        _region = options.RegionPx;
        Fps = RecordingSettings.NormalizeFps(options.Fps);
        Directory.CreateDirectory(options.TempDir);
        _writer = new FrameStoreWriter(TempPath("screen.oprec"), _region.Width, _region.Height, Fps);
        int bytes = _region.Width * _region.Height * 4;
        for (int i = 0; i < PoolSize; i++) _pool.Add(new byte[bytes]);
    }

    private string TempPath(string suffix) => Path.Combine(_o.TempDir, $"{_id}-{suffix}");

    public int Fps { get; }
    public int FramesCaptured => Volatile.Read(ref _captured);
    public int FramesDropped => Volatile.Read(ref _dropped);
    public long BytesWritten => _writer.BytesWritten;
    public bool IsPaused { get { lock (_clockGate) return _pauseStartTicks >= 0; } }

    /// <summary>Non-fatal problems while starting (e.g. microphone unavailable). Raised on the calling thread of <see cref="Start"/>.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Raised (on a worker thread) when capturing or encoding failed; capturing has stopped.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised (on the capture thread) when the maximum duration was reached; capturing has stopped.</summary>
    public event Action? LimitReached;

    public float MicrophoneGain { set { if (_micWriter != null) _micWriter.Gain = value; } }
    public float SystemAudioGain { set { if (_sysWriter != null) _sysWriter.Gain = value; } }
    public float MicrophoneLevel => _micWriter?.LastPeak ?? 0;
    public float SystemAudioLevel => _sysWriter?.LastPeak ?? 0;
    public bool HasMicrophone => _micWriter != null;
    public bool HasSystemAudio => _sysWriter != null;

    /// <summary>Recording clock (pauses excluded), ms.</summary>
    public double ElapsedMs
    {
        get
        {
            lock (_clockGate) return ClockAt(_sw.ElapsedTicks);
        }
    }

    private double ClockAt(long swTicks)
    {
        long paused = _pausedTicks + (_pauseStartTicks >= 0 ? swTicks - _pauseStartTicks : 0);
        return (swTicks - paused) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Recording clock at a <see cref="Stopwatch.GetTimestamp"/> value (audio packet times).</summary>
    private double ClockAtTimestamp(long timestamp)
    {
        lock (_clockGate)
        {
            long now = _sw.ElapsedTicks;
            long ago = Stopwatch.GetTimestamp() - timestamp;
            return ClockAt(now) - ago * 1000.0 / Stopwatch.Frequency;
        }
    }

    public void Start()
    {
        if (_captureThread != null) throw new InvalidOperationException("Already started");

        // audio first: opening a device can take a moment and must not eat into the recording
        if (_o.Microphone) _micWriter = StartAudio(false, _o.MicrophoneId, _o.MicrophoneGain, "mic.pcm", out _micCapture);
        if (_o.SystemAudio) _sysWriter = StartAudio(true, null, _o.SystemAudioGain, "system.pcm", out _sysCapture);

        _hooks = new InputHooks(mouse: true, keyboard: _o.RecordKeys);
        _hooks.MouseDown += OnMouseDown;
        _hooks.Wheel += OnWheel;
        _hooks.Key += OnKey;
        _hooks.Start();
        _input.HasKeyboard = _hooks.KeyboardHooked;
        if (_o.RecordKeys && !_hooks.KeyboardHooked) Warnings.Add("keyboard");

        if (_o.Webcam != null) _o.Webcam.Frame += OnWebcamFrame;

        _sw.Start();
        _encoderThread = new Thread(EncoderLoop) { IsBackground = true, Name = "OpenPin recorder encoder", Priority = ThreadPriority.BelowNormal };
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "OpenPin recorder capture", Priority = ThreadPriority.AboveNormal };
        _encoderThread.Start();
        _captureThread.Start();
    }

    private AudioTrackWriter? StartAudio(bool loopback, string? deviceId, float gain, string suffix, out WasapiCapture? capture)
    {
        capture = null;
        AudioTrackWriter? writer = null;
        try
        {
            writer = new AudioTrackWriter(TempPath(suffix)) { Gain = gain };
            capture = new WasapiCapture(loopback, deviceId);
            var w = writer;
            capture.Data += (samples, ts) =>
            {
                if (_stop || IsPaused || !_sw.IsRunning) return;
                double t = ClockAtTimestamp(ts);
                lock (w) w.Append(samples.Span, t);
            };
            capture.Start();
            Log.Info($"{(loopback ? "System audio" : "Microphone")} capture: {capture.Description}");
            return writer;
        }
        catch (Exception ex)
        {
            Log.Error($"{(loopback ? "System audio" : "Microphone")} unavailable", ex);
            Warnings.Add(loopback ? "system" : "microphone");
            capture?.Dispose();
            capture = null;
            if (writer != null)
            {
                writer.Dispose();
                try { File.Delete(writer.Path); } catch { /* ignore */ }
            }
            return null;
        }
    }

    private bool Accepting => !_stop && _sw.IsRunning && !IsPaused;

    private void OnMouseDown(int x, int y, int button)
    {
        if (!Accepting) return;
        int rx = x - _region.X, ry = y - _region.Y;
        if (rx < 0 || ry < 0 || rx >= _region.Width || ry >= _region.Height) return;
        _input.Add(new ClickEvent(ElapsedMs, rx, ry, (MouseButtonKind)button));
    }

    private void OnWheel(int x, int y, int delta, bool horizontal)
    {
        if (!Accepting) return;
        int rx = x - _region.X, ry = y - _region.Y;
        if (rx < 0 || ry < 0 || rx >= _region.Width || ry >= _region.Height) return;
        _input.Add(new WheelEvent(ElapsedMs, rx, ry, delta, horizontal));
    }

    private void OnKey(int vk, bool down)
    {
        if (!Accepting) return;
        _input.Add(new KeyEvent(ElapsedMs, vk, down));
    }

    private void OnWebcamFrame(byte[] bgra, int w, int h)
    {
        if (!Accepting) return;
        double t = ElapsedMs;
        lock (_camGate)
        {
            if (_stop || t - _camLast < 1000.0 / Math.Max(1, _o.WebcamFps)) return;
            try
            {
                _camWriter ??= new FrameStoreWriter(TempPath("webcam.oprec"), w, h, _o.WebcamFps) { KeyframeIntervalMs = 0 };
                if (_camWriter.Width != w || _camWriter.Height != h) return;
                var buf = _camSpare is { } s && s.Length == w * h * 4 ? s : new byte[w * h * 4];
                Buffer.BlockCopy(bgra, 0, buf, 0, w * h * 4);
                _camSpare = _camWriter.Append(buf, t, CursorSample.None);
                _camLast = t;
            }
            catch (Exception ex)
            {
                Log.Error("Webcam frame could not be stored", ex);
                _camWriter?.Dispose();
                _camWriter = null;
            }
        }
    }

    public void Pause()
    {
        lock (_clockGate)
        {
            if (_pauseStartTicks >= 0) return;
            _pauseStartTicks = _sw.ElapsedTicks;
            _running.Reset();
        }
    }

    public void Resume()
    {
        lock (_clockGate)
        {
            if (_pauseStartTicks < 0) return;
            _pausedTicks += _sw.ElapsedTicks - _pauseStartTicks;
            _pauseStartTicks = -1;
            _running.Set();
        }
    }

    /// <summary>Stop everything and finish the temp files. Safe to call more than once. Throws if the screen pipeline failed.</summary>
    public RecordingData Stop()
    {
        if (_result != null) return _result;
        double duration = ElapsedMs;
        _stop = true;
        _running.Set();
        _captureThread?.Join();
        _queue.CompleteAdding();
        _encoderThread?.Join();
        StopSideStreams();
        if (_error != null) throw new InvalidOperationException("Recording failed", _error);

        // the clock may have advanced past the last slot; never claim more than one interval after the last frame
        double lastFrame = _writer.FrameCount > 0 ? _lastFrameTime : 0;
        duration = Math.Min(duration, lastFrame + 1000.0 / Fps);
        var video = _writer.Complete(duration);
        AudioTrack? mic = null, sys = null;
        if (_micWriter != null) lock (_micWriter) mic = _micWriter.Complete(video.DurationMs);
        if (_sysWriter != null) lock (_sysWriter) sys = _sysWriter.Complete(video.DurationMs);
        FrameStore? cam = null;
        lock (_camGate)
        {
            if (_camWriter is { FrameCount: > 0 }) cam = _camWriter.Complete(video.DurationMs);
            else if (_camWriter != null) { _camWriter.Complete(0).Delete(); }
            _camWriter = null;
        }
        _result = new RecordingData(video, _input.Snapshot(), mic, sys, cam);
        Log.Info($"Recording stopped: {_writer.FrameCount} frames stored, {FramesCaptured} captured, {FramesDropped} dropped, " +
                 $"{video.DurationMs / 1000:0.0}s, {_writer.BytesWritten / 1024} KB; clicks {_result.Input.Clicks.Count}, " +
                 $"keys {_result.Input.Keys.Count}, mic {(mic != null)}, system {(sys != null)}, webcam frames {cam?.Frames.Count ?? 0}");
        return _result;
    }

    private void StopSideStreams()
    {
        if (_o.Webcam != null) _o.Webcam.Frame -= OnWebcamFrame;
        _hooks?.Dispose();
        _hooks = null;
        _micCapture?.Dispose();
        _sysCapture?.Dispose();
        _micCapture = _sysCapture = null;
    }

    /// <summary>Stop and delete all temp files.</summary>
    public void Abort()
    {
        try { Stop(); }
        catch (Exception ex) { Log.Warn($"Recorder abort: {ex.Message}"); }
        if (_result != null) _result.Delete();
        else
        {
            StopSideStreams();
            _writer.Dispose();
            _micWriter?.Dispose();
            _sysWriter?.Dispose();
            lock (_camGate) _camWriter?.Dispose();
            foreach (var f in Directory.EnumerateFiles(_o.TempDir, _id + "-*"))
                try { File.Delete(f); } catch { /* ignore */ }
        }
    }

    private void CaptureLoop()
    {
        RecNative.timeBeginPeriod(1);
        try
        {
            using var grabber = new ScreenGrabber(_region);
            var cursor = new CursorTracker(_writer.RegisterCursor);
            double interval = 1000.0 / Fps;
            long slot = 0;
            bool pollClicks = _hooks is not { MouseHooked: true }; // fallback if the hook could not be installed
            bool wasLeft = IsDown(RecNative.VK_LBUTTON), wasRight = IsDown(RecNative.VK_RBUTTON);

            while (!_stop)
            {
                if (!_running.IsSet)
                {
                    _running.Wait(100);
                    continue;
                }
                double now = ElapsedMs;
                if (_o.MaxDurationMs > 0 && now >= _o.MaxDurationMs)
                {
                    LimitReached?.Invoke();
                    break;
                }

                if (pollClicks)
                {
                    bool left = IsDown(RecNative.VK_LBUTTON), right = IsDown(RecNative.VK_RBUTTON);
                    if (left && !wasLeft || right && !wasRight)
                    {
                        Core.Native.NativeMethods.GetCursorPos(out var p);
                        OnMouseDown(p.X, p.Y, right && !wasRight ? 1 : 0);
                    }
                    wasLeft = left;
                    wasRight = right;
                }

                double due = slot * interval;
                if (now < due)
                {
                    if (due - now > 1.5) Thread.Sleep(1);
                    else Thread.Yield();
                    continue;
                }

                long current = (long)Math.Floor(now / interval);
                if (current > slot)
                {
                    Interlocked.Add(ref _dropped, (int)(current - slot));
                    slot = current;
                }
                if (!_pool.TryTake(out var buffer))
                {
                    Interlocked.Increment(ref _dropped); // encoder is behind: drop rather than queue unbounded
                    slot++;
                    continue;
                }
                grabber.Grab(buffer);
                var sample = cursor.Sample(_region.X, _region.Y, out _, out _);
                double t = slot * interval;
                _lastFrameTime = t;
                _queue.Add((buffer, t, sample));
                Interlocked.Increment(ref _captured);
                slot++;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            RecNative.timeEndPeriod(1);
            _queue.CompleteAdding();
        }
    }

    private static bool IsDown(int vk) => (RecNative.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void EncoderLoop()
    {
        try
        {
            foreach (var (buffer, time, cursor) in _queue.GetConsumingEnumerable())
            {
                if (_error != null) { _pool.Add(buffer); continue; }
                var released = _writer.Append(buffer, time, cursor);
                if (released != null) _pool.Add(released);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            foreach (var item in _queue.GetConsumingEnumerable()) _pool.Add(item.Buffer); // keep draining
        }
    }

    private void Fail(Exception ex)
    {
        if (_error != null) return;
        _error = ex;
        _stop = true;
        Log.Error("Recording pipeline failed", ex);
        try { Failed?.Invoke(ex); } catch (Exception inner) { Log.Error("Recorder Failed handler threw", inner); }
    }

    public void Dispose()
    {
        if (_result == null) Abort();
        _running.Dispose();
        _queue.Dispose();
    }
}
