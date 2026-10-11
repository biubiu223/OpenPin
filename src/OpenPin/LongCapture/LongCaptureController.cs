using System.Windows;
using OpenPin.Core;
using OpenPin.Core.Screen;
using OpenPin.Services;

namespace OpenPin.LongCapture;

/// <summary>
/// LONG CAPTURE MODULE entry point (SPEC §6). <see cref="Start"/> opens a session on a physical-pixel region: a frame
/// around the region, a control bar and a live preview; the user scrolls the app below while frames are grabbed and
/// stitched on a background thread (see <see cref="Stitching.ScrollStitcher"/>). UI thread only.
/// </summary>
public sealed class LongCaptureController : ILongCaptureController
{
    private const int MinRegionPx = 40;
    private LongCaptureSession? _session;

    public bool IsActive => _session is { IsOpen: true };

    public void Start(Int32Rect regionPx)
    {
        try
        {
            if (IsActive) return; // one session at a time
            var region = Clip(regionPx, Monitors.VirtualScreenPx);
            if (region.Width < MinRegionPx || region.Height < MinRegionPx)
            {
                Notify.Info(Loc.Get("LongCapture.TooSmall"));
                return;
            }
            var cfg = AppServices.Settings?.Current.LongCapture ?? new LongCaptureSettings();
            var session = new LongCaptureSession(region, cfg);
            session.Closed += () => { if (ReferenceEquals(_session, session)) _session = null; };
            _session = session;
            session.Open();
        }
        catch (Exception ex)
        {
            Log.Error("Long capture failed to start", ex);
            Notify.Error(Loc.Format("LongCapture.Failed", ex.Message));
            try { _session?.Close(); }
            catch (Exception closeEx) { Log.Error("Long capture cleanup failed", closeEx); }
            _session = null;
        }
    }

    private static Int32Rect Clip(Int32Rect r, Int32Rect bounds)
    {
        int x = Math.Max(r.X, bounds.X), y = Math.Max(r.Y, bounds.Y);
        int right = Math.Min(r.X + r.Width, bounds.X + bounds.Width), bottom = Math.Min(r.Y + r.Height, bounds.Y + bounds.Height);
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
