using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Actions;
using OpenPin.Core.Imaging;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.LongCapture.Stitching;
using OpenPin.LongCapture.Views;
using OpenPin.Services;

namespace OpenPin.LongCapture;

/// <summary>
/// One long-capture session: frame + control bar + preview windows, a background grab/stitch thread and the outputs.
/// UI-thread affine except the worker loop. Implements <see cref="IModalSession"/> while open.
/// </summary>
internal sealed class LongCaptureSession : IModalSession
{
    private enum State { Ready, Capturing, Stopped, Closed }

    private const int EscKeyId = 0x4C43;
    private const uint VK_ESCAPE = 0x1B;

    private readonly LongCaptureSettings _cfg;
    private readonly RegionFrameWindow _frame;
    private readonly LongCaptureBar _bar;
    private readonly PreviewWindow? _preview;
    private readonly SaveContext _context;
    private readonly object _gate = new();
    private readonly StitchOptions _options;
    private readonly DispatcherTimer _autoScrollTimer;
    private LongCaptureNative.GlobalKey? _escKey;

    private Int32Rect _region;
    private LongCaptureDirection _direction;
    private State _state = State.Ready;
    private ScrollStitcher? _stitcher;
    private Thread? _worker;
    private volatile bool _stopWorker;
    private volatile bool _autoCrop;
    private bool _busy;

    // latest worker snapshot (coalesced into one dispatcher post)
    private Snapshot? _pending;
    private int _postQueued;
    private bool? _lastGood;
    private bool _limitReached;
    private int _length, _width;
    private long _lastMotionTicks;

    private sealed record Snapshot(StitchResult Result, BitmapSource? Preview, double Scale, int Length, int Width, int ViewportStart, int FrameLines);

    public LongCaptureSession(Int32Rect regionPx, LongCaptureSettings cfg)
    {
        _cfg = cfg;
        _region = regionPx;
        _direction = cfg.DefaultDirection;
        _autoCrop = cfg.AutoCrop;
        _options = new StitchOptions { MaxLength = Math.Max(1000, cfg.MaxLength), AutoCrop = cfg.AutoCrop };
        // File-name template info (%title%, %process%) of the window being captured, read before our windows exist.
        var (title, process) = LongCaptureNative.WindowInfoAt(regionPx.X + regionPx.Width / 2, regionPx.Y + regionPx.Height / 2);
        _context = new SaveContext { WindowTitle = title, ProcessName = process, FromCapture = true };

        _frame = new RegionFrameWindow(regionPx) { FrameBrush = (Brush?)Application.Current.TryFindResource("Brush.Accent") ?? Brushes.DodgerBlue };
        _frame.RegionDragged += r => Guard("move region", () => { _region = r; Layout(); });
        _bar = new LongCaptureBar();
        _bar.SetDirection(_direction);
        _bar.SetCapturing(false);
        _bar.AutoCrop.IsChecked = cfg.AutoCrop;
        if (cfg.ShowPreview)
        {
            _preview = new PreviewWindow();
            _preview.TrimChanged += () => Guard("trim", UpdateInfo);
            _preview.DoneButton.Click += (_, _) => Guard("crop done", () => SetCropMode(false));
            _preview.SizeChanged += (_, _) => Guard("layout", Layout); // e.g. the crop row appears
        }

        _bar.StartStop.Click += (_, _) => Guard("start/stop", ToggleCapture);
        _bar.Direction.Click += (_, _) => Guard("direction", ToggleDirection);
        _bar.AutoCrop.Click += (_, _) => Guard("auto-crop", () => _autoCrop = _bar.AutoCrop.IsChecked == true);
        _bar.AutoScroll.Click += (_, _) => Guard("auto-scroll", () => SetAutoScroll(_bar.AutoScroll.IsChecked == true));
        _bar.Crop.Click += (_, _) => Guard("crop", () => SetCropMode(_bar.Crop.IsChecked == true));
        _bar.PinButton.Click += (_, _) => _ = OutputAsync(Output.Pin);
        _bar.SaveButton.Click += (_, _) => _ = OutputAsync(Output.Save);
        _bar.QuickSaveButton.Click += (_, _) => _ = OutputAsync(Output.QuickSave);
        _bar.CopyButton.Click += (_, _) => _ = OutputAsync(Output.Copy);
        _bar.CloseButton.Click += (_, _) => Guard("close", () => RequestClose());
        _bar.SizeChanged += (_, _) => Guard("layout", Layout);

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(cfg.AutoScrollIntervalMs, 50, 2000)) };
        _autoScrollTimer.Tick += (_, _) => Guard("auto-scroll tick", AutoScrollTick);
    }

    public event Action? Closed;

    public bool IsOpen => _state != State.Closed;

    private bool Vertical => _direction == LongCaptureDirection.Vertical;

    // ================================================================================== open / layout

    public void Open()
    {
        _frame.ShowFrame();
        _bar.Show();
        _preview?.Show();
        AppServices.ActiveSession = this;
        _escKey = new LongCaptureNative.GlobalKey(VK_ESCAPE, EscKeyId, () => Guard("esc", () => RequestClose()));
        if (!_frame.ExcludedFromCapture) Log.Warn("Long capture: WDA_EXCLUDEFROMCAPTURE unsupported; relying on placement outside the region");
        UpdateInfo();
        Layout();
        // SizeToContent windows know their real size only after the first layout pass.
        _bar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Guard("layout", Layout));
        Log.Info($"Long capture opened at {_region} ({_direction})");
    }

    private void Layout()
    {
        if (_state == State.Closed) return;
        var mon = Monitors.FromPointPx(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        double s = mon.Scale;
        _frame.SetRegion(_region);
        var keepOut = LongCapturePlacement.Inflate(_region, _frame.RingPx);
        // Place the VISIBLE parts (windows minus their transparent shadow padding) inside the work area.
        int pad = (int)Math.Round(LongCaptureBar.Pad * s);
        int gap = (int)Math.Round(4 * s);
        var work = mon.WorkAreaPx;

        var barWin = WindowHelper.GetBoundsPx(_bar);
        var bar = LongCapturePlacement.PlaceBar(keepOut, Math.Max(1, barWin.Width - 2 * pad), Math.Max(1, barWin.Height - 2 * pad), work, gap);
        WindowHelper.SetPositionPx(_bar, bar.X - pad, bar.Y - pad);

        if (_preview == null) return;
        // Preview area: a narrow column (vertical) / strip (horizontal) of a fixed size.
        double areaW, areaH;
        if (Vertical)
        {
            areaW = 160;
            areaH = Math.Clamp(_region.Height, 240 * s, mon.WorkAreaPx.Height * 0.8) / s;
        }
        else
        {
            areaH = 120;
            areaW = Math.Clamp(_region.Width, 320 * s, mon.WorkAreaPx.Width * 0.8) / s;
        }
        _preview.SetArea(areaW, areaH, Vertical);
        _preview.UpdateLayout();
        var pvWin = WindowHelper.GetBoundsPx(_preview);
        var pr = LongCapturePlacement.PlacePreview(keepOut, bar, Math.Max(1, pvWin.Width - 2 * pad), Math.Max(1, pvWin.Height - 2 * pad), work, gap, Vertical);
        WindowHelper.SetPositionPx(_preview, pr.X - pad, pr.Y - pad);
    }

    private (int W, int H) PreviewPixels()
    {
        double s = Monitors.FromPointPx(_region.X + _region.Width / 2, _region.Y + _region.Height / 2).Scale;
        var mon = Monitors.FromPointPx(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        return Vertical
            ? ((int)(160 * s), (int)Math.Clamp(_region.Height, 240 * s, mon.WorkAreaPx.Height * 0.8))
            : ((int)Math.Clamp(_region.Width, 320 * s, mon.WorkAreaPx.Width * 0.8), (int)(120 * s));
    }

    // ================================================================================== start / stop

    private void ToggleCapture()
    {
        if (_state == State.Capturing) StopCapture();
        else StartCapture();
    }

    private void StartCapture()
    {
        if (_state is State.Capturing or State.Closed || _busy) return;
        SetCropMode(false);
        lock (_gate)
        {
            if (_stitcher == null || _stitcher.Direction != _direction)
                _stitcher = new ScrollStitcher(_region.Width, _region.Height, _direction, _options);
            else
                _stitcher.ResetDirection(); // resuming: the first real movement sets the stitch direction again
        }
        _frame.Editable = false;
        _bar.Direction.IsEnabled = false;
        _state = State.Capturing;
        _lastMotionTicks = Environment.TickCount64;
        _bar.SetCapturing(true);
        _stopWorker = false;
        var region = _region;
        var stitcher = _stitcher;
        var (pw, ph) = PreviewPixels();
        _worker = new Thread(() => WorkerLoop(region, stitcher, pw, ph)) { IsBackground = true, Name = "OpenPin long capture" };
        _worker.Start();
        if (_bar.AutoScroll.IsChecked == true) SetAutoScroll(true);
        UpdateInfo();
        Log.Info($"Long capture started ({region.Width}×{region.Height}, {_direction})");
    }

    private void StopCapture()
    {
        if (_state != State.Capturing) return;
        _stopWorker = true;
        if (_worker != null && !_worker.Join(2000)) Log.Warn("Long capture worker did not stop in time");
        _worker = null;
        _autoScrollTimer.Stop();
        _bar.AutoScroll.IsChecked = false;
        _state = State.Stopped;
        _bar.SetCapturing(false);
        // The direction can be switched again only while nothing has been stitched yet.
        lock (_gate) _bar.Direction.IsEnabled = _stitcher is not { HasStitched: true };
        UpdateInfo();
    }

    private void ToggleDirection()
    {
        if (_state == State.Capturing) return;
        lock (_gate)
        {
            if (_stitcher is { HasStitched: true }) return;
            _stitcher = null; // recreated on start (direction re-established)
        }
        _direction = Vertical ? LongCaptureDirection.Horizontal : LongCaptureDirection.Vertical;
        _bar.SetDirection(_direction);
        _preview?.Update(null, 0, 0, 0, 0, null);
        Layout();
    }

    // ================================================================================== worker

    private void WorkerLoop(Int32Rect region, ScrollStitcher stitcher, int previewW, int previewH)
    {
        var buffer = new byte[region.Width * region.Height * 4];
        int interval = 1000 / Math.Clamp(_cfg.FramesPerSecond, 5, 60);
        var clock = Stopwatch.StartNew();
        long lastPreview = -1000;
        int errors = 0;
        StitchStatus lastStatus = StitchStatus.Unchanged;
        long frames = 0, totalMs = 0;
        while (!_stopWorker)
        {
            long t0 = clock.ElapsedMilliseconds;
            try
            {
                ScreenCapture.CaptureRegionInto(region, buffer);
                StitchResult r;
                BitmapSource? preview = null;
                double scale = 0;
                int length, width, viewport;
                lock (_gate)
                {
                    _options.AutoCrop = _autoCrop;
                    r = stitcher.Push(buffer);
                    length = stitcher.Length;
                    width = Vertical ? stitcher.FrameWidth : stitcher.FrameHeight;
                    viewport = stitcher.ViewportStart;
                    bool due = clock.ElapsedMilliseconds - lastPreview >= 120;
                    if (_preview != null && (r.Status != StitchStatus.Unchanged || lastPreview < 0) && (due || r.Status != lastStatus))
                    {
                        var (px, w, h) = stitcher.RenderPreview(previewW, previewH, out scale);
                        if (w > 0) preview = BitmapUtil.FromPixels(px, w, h);
                        lastPreview = clock.ElapsedMilliseconds;
                    }
                }
                if (r.Status != StitchStatus.Unchanged) lastStatus = r.Status;
                frames++;
                totalMs += clock.ElapsedMilliseconds - t0;
                Post(new Snapshot(r, preview, scale, length, width, viewport, stitcher.FrameLines));
                errors = 0;
            }
            catch (Exception ex)
            {
                Log.Error("Long capture frame failed", ex);
                if (++errors >= 10)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => Guard("worker failure", () =>
                    {
                        Notify.Error(Loc.Format("LongCapture.Failed", ex.Message));
                        StopCapture();
                    }));
                    return;
                }
            }
            int wait = interval - (int)(clock.ElapsedMilliseconds - t0);
            if (wait > 0) Thread.Sleep(wait);
        }
        if (frames > 0) Log.Info($"Long capture worker: {frames} frames, {totalMs / (double)frames:0.0} ms avg grab+stitch");
    }

    private void Post(Snapshot s)
    {
        // Keep a preview from an earlier, not yet shown snapshot when the newer one has none.
        var prev = Interlocked.Exchange(ref _pending, s);
        if (s.Preview == null && prev?.Preview != null)
            Interlocked.CompareExchange(ref _pending, s with { Preview = prev.Preview, Scale = prev.Scale }, s);
        if (Interlocked.Exchange(ref _postQueued, 1) == 1) return;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _postQueued, 0);
            var snap = Interlocked.Exchange(ref _pending, null);
            if (snap != null) Guard("frame update", () => OnSnapshot(snap));
        });
    }

    private void OnSnapshot(Snapshot s)
    {
        if (_state == State.Closed) return;
        var st = s.Result.Status;
        if (st != StitchStatus.Unchanged) _lastGood = s.Result.IsGood;
        if (st is StitchStatus.Appended or StitchStatus.Moved or StitchStatus.Trimmed or StitchStatus.First)
            _lastMotionTicks = Environment.TickCount64;
        _limitReached = st == StitchStatus.LimitReached;
        _length = s.Length;
        _width = s.Width;
        if (s.Preview != null) _lastScale = s.Scale;
        if (s.Preview != null || st != StitchStatus.Unchanged)
            _preview?.Update(s.Preview, _lastScale, s.Length, s.ViewportStart, s.FrameLines, _lastGood);
        if (_limitReached && _autoScrollTimer.IsEnabled) SetAutoScroll(false);
        UpdateInfo();
    }

    private double _lastScale;

    private void UpdateInfo()
    {
        int trimmed = _preview == null ? 0 : _preview.TrimStart + _preview.TrimEnd;
        int len = Math.Max(0, _length - trimmed);
        string size = _length == 0 ? "" : Vertical ? Loc.Format("LongCapture.Size", _width, len) : Loc.Format("LongCapture.Size", len, _width);
        string status = _state switch
        {
            State.Ready => Loc.Get("LongCapture.Status.Ready"),
            State.Stopped => Loc.Get("LongCapture.Status.Stopped"),
            _ when _limitReached => Loc.Get("LongCapture.Status.Limit"),
            _ when _lastGood == false => Loc.Get("LongCapture.Status.Failed"),
            _ => Loc.Get("LongCapture.Status.Capturing"),
        };
        if (_busy) status = Loc.Get("LongCapture.Status.Working");
        _bar.SetInfo(size, status, _state == State.Capturing && (_lastGood == false || _limitReached));
    }

    // ================================================================================== auto-scroll (LNG-04)

    private void SetAutoScroll(bool on)
    {
        _bar.AutoScroll.IsChecked = on;
        if (!on) { _autoScrollTimer.Stop(); return; }
        if (_state != State.Capturing) { StartCapture(); if (_state != State.Capturing) return; }
        // Wheel input goes to the window under the cursor: put it in the middle of the region.
        LongCaptureNative.MoveCursor(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        _lastMotionTicks = Environment.TickCount64;
        _autoScrollTimer.Start();
    }

    private void AutoScrollTick()
    {
        if (_state != State.Capturing) { SetAutoScroll(false); return; }
        var (x, y) = LongCaptureNative.CursorPx;
        if (x < _region.X || y < _region.Y || x >= _region.X + _region.Width || y >= _region.Y + _region.Height)
        {
            SetAutoScroll(false); // the user moved the mouse away: they take over
            return;
        }
        if (_limitReached || Environment.TickCount64 - _lastMotionTicks > Math.Max(300, _cfg.AutoScrollStopAfterIdleMs))
        {
            // End of the content (nothing moves any more) or maximum length: done.
            SetAutoScroll(false);
            StopCapture();
            return;
        }
        int delta = Math.Clamp(_cfg.AutoScrollWheelDelta, 1, 1200);
        LongCaptureNative.SendWheel(Vertical ? -delta : delta, horizontal: !Vertical);
    }

    // ================================================================================== crop mode

    private void SetCropMode(bool on)
    {
        if (_preview == null) { _bar.Crop.IsChecked = false; return; }
        if (on && _state == State.Capturing) StopCapture();
        _bar.Crop.IsChecked = on;
        _preview.CropMode = on;
        UpdateInfo();
    }

    // ================================================================================== outputs

    private enum Output { Pin, Save, QuickSave, Copy }

    private async Task OutputAsync(Output kind)
    {
        if (_busy || _state == State.Closed) return;
        try
        {
            var image = await ComposeAsync();
            if (image == null || _state == State.Closed) return;
            switch (kind)
            {
                case Output.Pin:
                    Close();
                    AppServices.Pins.PinImage(image);
                    SaveService.AutoSave(image, _context);
                    break;
                case Output.Copy:
                    Close();
                    try
                    {
                        ClipboardService.SetImage(image);
                        SaveService.AutoSave(image, _context);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Long capture copy failed", ex);
                        Notify.Error(Loc.Format("LongCapture.Failed", ex.Message));
                    }
                    break;
                case Output.QuickSave:
                    Close();
                    SaveService.QuickSave(image, _context);
                    break;
                case Output.Save:
                    // Our topmost windows would cover the dialog: hide frame + preview while it is open.
                    _frame.Hide();
                    _preview?.Hide();
                    string? path = SaveService.SaveWithDialog(image, _context, _bar);
                    if (path != null) { Close(); return; }
                    if (_state == State.Closed) return;
                    _frame.Show();
                    _preview?.Show();
                    Layout();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Long capture output {kind} failed", ex);
            Notify.Error(Loc.Format("LongCapture.Failed", ex.Message));
        }
    }

    /// <summary>Stops capturing and composes the (trimmed) result off the UI thread.</summary>
    private async Task<BitmapSource?> ComposeAsync()
    {
        StopCapture();
        ScrollStitcher? stitcher;
        lock (_gate) stitcher = _stitcher;
        if (stitcher == null || stitcher.Length == 0)
        {
            // Nothing captured yet: the result is the region as it is now.
            var region = _region;
            stitcher ??= new ScrollStitcher(region.Width, region.Height, _direction, _options);
            var buffer = new byte[region.Width * region.Height * 4];
            ScreenCapture.CaptureRegionInto(region, buffer);
            lock (_gate)
            {
                stitcher.Push(buffer);
                _stitcher = stitcher;
            }
        }
        int trimStart = _preview?.TrimStart ?? 0, trimEnd = _preview?.TrimEnd ?? 0;
        _busy = true;
        _bar.SetBusy(true);
        UpdateInfo();
        try
        {
            var sw = Stopwatch.StartNew();
            var image = await Task.Run(() =>
            {
                (byte[] Pixels, int Width, int Height) composed;
                lock (_gate) composed = stitcher.Compose(trimStart, trimEnd);
                return BitmapUtil.FromPixels(composed.Pixels, composed.Width, composed.Height);
            });
            Log.Info($"Long capture composed {image.PixelWidth}×{image.PixelHeight} in {sw.ElapsedMilliseconds} ms");
            return image;
        }
        finally
        {
            _busy = false;
            if (_state != State.Closed)
            {
                _bar.SetBusy(false);
                UpdateInfo();
            }
        }
    }

    // ================================================================================== close

    /// <summary>Esc / close button: asks first when something has been stitched.</summary>
    public void RequestClose()
    {
        if (_state == State.Closed || _busy) return;
        bool hasContent;
        lock (_gate) hasContent = _stitcher is { HasStitched: true };
        if (hasContent)
        {
            bool wasCapturing = _state == State.Capturing;
            StopCapture();
            var r = MessageBox.Show(_bar, Loc.Get("LongCapture.ConfirmClose"), "OpenPin", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK)
            {
                if (wasCapturing) StartCapture();
                return;
            }
        }
        Close();
    }

    public void Close()
    {
        if (_state == State.Closed) return;
        StopCapture();
        _state = State.Closed;
        _autoScrollTimer.Stop();
        _escKey?.Dispose();
        _escKey = null;
        if (ReferenceEquals(AppServices.ActiveSession, this)) AppServices.ActiveSession = null;
        foreach (var w in new Window?[] { _frame, _bar, _preview })
        {
            try { w?.Close(); }
            catch (Exception ex) { Log.Error("Closing long capture window failed", ex); }
        }
        lock (_gate) _stitcher = null; // release the (possibly huge) strips
        Log.Info("Long capture closed");
        Closed?.Invoke();
    }

    // ================================================================================== IModalSession

    public bool TryHandleAction(string actionId)
    {
        if (_state == State.Closed) return false;
        switch (actionId)
        {
            case ActionIds.Capture:
                Guard("capture action", ToggleCapture);
                return true;
            case ActionIds.CapturePin:
            case ActionIds.PinClipboard:
                _ = OutputAsync(Output.Pin);
                return true;
            default:
                // Other capture actions would open the overlay over the session: ignore them.
                return actionId.StartsWith("capture", StringComparison.Ordinal);
        }
    }

    private static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error($"Long capture: {what} failed", ex);
            Notify.Error(Loc.Format("LongCapture.Failed", ex.Message));
        }
    }
}
