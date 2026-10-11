using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Screen;
using OpenPin.Services;

namespace OpenPin.Capture;

/// <summary>
/// CAPTURE MODULE entry point (implements <see cref="ICaptureController"/>): freezes the screen, opens the selection
/// overlay, custom/delayed capture, and the no-UI captures (full screen / last region). See docs/SPEC.md §1.
/// </summary>
public sealed class CaptureController : ICaptureController
{
    private static CaptureHistoryStore? _history;
    private CaptureOverlay? _overlay;

    internal static CaptureHistoryStore History => _history ??= new CaptureHistoryStore(AppPaths.CaptureHistoryDir);

    private static CaptureSettings Config => AppServices.Settings?.Current.Capture ?? new CaptureSettings();

    public CaptureController()
    {
        // Warm up the overlay code paths once the app is idle after startup (first capture latency).
        if (Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (_overlay != null) return;
                try { CaptureOverlay.Warmup(this); }
                catch (Exception ex) { Log.Warn($"Capture warm-up failed: {ex.Message}"); }
            });
    }

    public bool IsActive => _overlay != null;

    public void Start(CaptureMode mode = CaptureMode.Edit) => StartInternal(mode, null);

    /// <summary>Open the overlay, optionally with a pre-selected region (screen px).</summary>
    internal void StartInternal(CaptureMode mode, Int32Rect? initialSelectionPx)
    {
        if (_overlay != null) return; // a capture is already open (or hidden behind the save dialog)
        var clock = Stopwatch.StartNew();
        ScreenSnapshot? snap = null;
        CaptureOverlay? overlay = null;
        try
        {
            // Freeze first: nothing of ours may be on screen yet.
            snap = ScreenCapture.CaptureVirtualScreen();
            long grabbed = clock.ElapsedMilliseconds;
            overlay = new CaptureOverlay(this, snap, mode, Config, clock);
            _overlay = overlay;
            Log.Info($"Capture start ({mode}): snapshot {snap.Image.PixelWidth}x{snap.Image.PixelHeight} in {grabbed} ms, " +
                     $"overlay built at {clock.ElapsedMilliseconds} ms");
            overlay.ShowOverlay(initialSelectionPx);
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed to start", ex);
            Notify.Error(Loc.Format("Capture.Failed", ex.Message));
            if (overlay != null)
            {
                try { overlay.CloseOverlay(); } catch (Exception e2) { Log.Error("Overlay close after failure", e2); }
            }
            else snap?.Dispose();
            _overlay = null;
            if (ReferenceEquals(AppServices.ActiveSession, overlay) && overlay != null) AppServices.ActiveSession = null;
        }
    }

    internal void OnOverlayClosed(CaptureOverlay overlay)
    {
        if (ReferenceEquals(_overlay, overlay)) _overlay = null;
        // The snapshot (tens of MB on 4K / multi-monitor) is unreachable now: give the memory back promptly.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers(); // WIC bitmap memory is released by finalizers
            GC.Collect();
        });
    }

    public void StartCustom()
    {
        if (_overlay != null) return;
        CustomCaptureDialog.ShowSingle(this);
    }

    public void CaptureFullScreenToClipboard()
    {
        var mon = Monitors.UnderCursor;
        CaptureNoUi(mon.BoundsPx, "Capture.FullScreenCopied");
    }

    public void CaptureLastRegionToClipboard()
    {
        if (History.LastRegion is not { } rect)
        {
            Notify.Info(Loc.Get("Capture.NoLastRegion"));
            return;
        }
        CaptureNoUi(rect, "Capture.LastRegionCopied");
    }

    /// <summary>Capture a screen rectangle straight to the clipboard (CAP-15 / CAP-14 direct copy).</summary>
    internal static void CaptureNoUi(Int32Rect rectPx, string messageKey)
    {
        try
        {
            var clipped = SelectionMath.Clamp(rectPx, Monitors.VirtualScreenPx);
            if (SelectionMath.IsEmpty(clipped))
            {
                Notify.Error(Loc.Get("Capture.Custom.Invalid"));
                return;
            }
            var cfg = Config;
            var bmp = ScreenCapture.CaptureRegion(clipped, includeCursor: cfg.ShowCursor);
            ClipboardService.SetImage(bmp);
            var ctx = new SaveContext { FromCapture = true, WindowTitle = WindowUnder(clipped)?.Title, ProcessName = WindowUnder(clipped)?.ProcessName };
            SaveService.AutoSave(bmp, ctx);
            History.AddRegion(clipped, cfg.HistoryRegionCount);
            _ = History.AddImageAsync(bmp, clipped, cfg.HistoryImageCount);
            Notify.Show(new NotifyOptions { Message = Loc.Get(messageKey), Thumbnail = bmp });
        }
        catch (Exception ex)
        {
            Log.Error("No-UI capture failed", ex);
            Notify.Error(Loc.Format("Capture.Failed", ex.Message));
        }
    }

    private static WindowInfo? WindowUnder(Int32Rect rectPx)
    {
        try
        {
            var center = new Point(rectPx.X + rectPx.Width / 2.0, rectPx.Y + rectPx.Height / 2.0);
            return WindowEnumerator.HitTest(WindowEnumerator.GetTopLevelWindows(), center);
        }
        catch (Exception ex)
        {
            Log.Warn($"Window lookup failed: {ex.Message}");
            return null;
        }
    }

    internal static void RememberRegion(Int32Rect rectPx)
    {
        try { History.AddRegion(rectPx, Config.HistoryRegionCount); }
        catch (Exception ex) { Log.Error("Failed to record region", ex); }
    }

    /// <summary>OCR an image and copy the text (Shift+C / CaptureMode.Ocr).</summary>
    internal static async Task OcrAndCopyAsync(BitmapSource image)
    {
        try
        {
            // RecognizeAsync never throws: failures come back in result.Error.
            var result = await OcrService.RecognizeAsync(image, Recognition.OcrLanguageChoice.CurrentOrNull);
            if (!string.IsNullOrEmpty(result.Error))
            {
                Notify.Error(OcrService.IsAvailable ? result.Error : Loc.Get("Capture.OcrUnavailable"));
                return;
            }
            var text = !string.IsNullOrWhiteSpace(result.Text)
                ? result.Text
                : string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
            if (result.IsEmpty || string.IsNullOrWhiteSpace(text))
            {
                Notify.Info(Loc.Get(OcrService.IsAvailable ? "Capture.OcrNoText" : "Capture.OcrUnavailable"));
                return;
            }
            ClipboardService.SetText(text); // throws after its retries: handled below
            Notify.Show(new NotifyOptions
            {
                Message = Loc.Format("Capture.OcrCopied", text.Length),
                Actions = new[] { new NotifyAction(Loc.Get("Capture.OcrShow"), () => OcrResultWindow.Show(result, image)) },
            });
        }
        catch (Exception ex)
        {
            Log.Error("OCR failed", ex);
            Notify.Error(Loc.Format("Capture.Failed", ex.Message));
        }
    }

    /// <summary>Run <paramref name="action"/> on the UI thread after a short delay, logging failures.</summary>
    internal static void AfterDelay(int ms, Action action, string what)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); }
            catch (Exception ex)
            {
                Log.Error($"{what} failed", ex);
                Notify.Error(Loc.Format("Capture.Failed", ex.Message));
            }
        };
        timer.Start();
    }
}
