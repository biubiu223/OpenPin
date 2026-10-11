using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenPin.Core;
using OpenPin.Core.Screen;
using OpenPin.Recording.Codec;
using OpenPin.Recording.UI;
using OpenPin.Services;

namespace OpenPin.Recording;

/// <summary>
/// SCREEN RECORDING MODULE entry point (SPEC §7, PRO-03). <see cref="Start"/> opens a recording session over a region:
/// frame + control bar → recording → playback window (trim, overlays, GIF/MP4 export). UI thread only.
/// Temp files live in %TEMP%\OpenPin\Recording and are removed when the session / playback window closes; leftovers of
/// crashed runs are swept when a new session starts.
/// </summary>
public sealed class ScreenRecorder : IScreenRecorder
{
    private RecordingSession? _session;
    private readonly List<PlaybackWindow> _playbacks = new();
    private bool _exitHooked;

    /// <summary>True while a recording session (frame + bar) is open.</summary>
    public bool IsActive => _session != null;

    internal static string TempRoot => AppPaths.Ensure(Path.Combine(AppPaths.TempDir, "Recording"));

    public void Start(Int32Rect regionPx)
    {
        if (_session != null)
        {
            _session.Flash();
            return;
        }
        HookExit();
        CleanupStaleTempFiles();

        var region = ClampToScreen(regionPx);
        if (region.Width < 8 || region.Height < 8)
        {
            Notify.Error(Loc.Get("Recording.TooSmall"));
            return;
        }
        _session = new RecordingSession(this, region);
        try
        {
            _session.Open();
        }
        catch (Exception ex)
        {
            Log.Error("Opening the recording session failed", ex);
            Notify.Error(Loc.Format("Recording.Failed", ex.Message));
            _session.Cancel(force: true);
            _session = null;
        }
    }

    internal void OnSessionClosed(RecordingSession session)
    {
        if (ReferenceEquals(_session, session)) _session = null;
    }

    internal void OpenPlayback(RecordingData data, Int32Rect region)
    {
        var w = new PlaybackWindow(this, data, region);
        _playbacks.Add(w);
        w.Closed += (_, _) => _playbacks.Remove(w);
        w.Show();
        w.Activate();
    }

    /// <summary>Region intersected with the virtual screen (recording off-screen pixels makes no sense).</summary>
    private static Int32Rect ClampToScreen(Int32Rect r)
    {
        var v = Monitors.VirtualScreenPx;
        int x0 = Math.Max(r.X, v.X), y0 = Math.Max(r.Y, v.Y);
        int x1 = Math.Min(r.X + r.Width, v.X + v.Width), y1 = Math.Min(r.Y + r.Height, v.Y + v.Height);
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private void HookExit()
    {
        if (_exitHooked || Application.Current == null) return;
        _exitHooked = true;
        Application.Current.Exit += (_, _) =>
        {
            try
            {
                _session?.Cancel(force: true);
                foreach (var p in _playbacks.ToArray()) p.CloseAndDiscard();
            }
            catch (Exception ex) { Log.Error("Recorder cleanup on exit failed", ex); }
        };
    }

    /// <summary>
    /// Delete temp files of recordings whose process is gone (crash / kill) and clipboard exports older than a day.
    /// Files of running OpenPin processes (this one, or another instance) are kept.
    /// </summary>
    internal static void CleanupStaleTempFiles()
    {
        try
        {
            var root = TempRoot;
            foreach (var f in Directory.EnumerateFiles(root, "rec-*"))
            {
                var name = Path.GetFileName(f);
                var parts = name.Split('-');
                if (parts.Length < 3 || !int.TryParse(parts[1], out int pid)) continue;
                if (pid == Environment.ProcessId || IsRunning(pid)) continue;
                try { File.Delete(f); } catch (Exception ex) { Log.Warn($"Could not delete stale {name}: {ex.Message}"); }
            }
            var clip = Path.Combine(root, "export");
            if (Directory.Exists(clip))
                foreach (var f in Directory.EnumerateFiles(clip, "*", SearchOption.AllDirectories))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1))
                        try { File.Delete(f); } catch { /* in use */ }
        }
        catch (Exception ex) { Log.Warn($"Recording temp cleanup failed: {ex.Message}"); }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
