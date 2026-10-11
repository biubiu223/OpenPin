using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.UI;

/// <summary>
/// Decodes + composes preview frames on a background thread (latest request wins) and shows them in a
/// <see cref="WriteableBitmap"/>. Keeps the UI thread free even for keyframes of large recordings.
/// </summary>
internal sealed class PreviewRenderer : IDisposable
{
    private readonly RecordingData _data;
    private readonly double[] _times;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private readonly byte[] _front;
    private (double Time, OverlayOptions Options)? _request;
    private volatile bool _stop;
    private int _uiPending;
    private IReadOnlyDictionary<string, Codec.CursorShape>? _sprites;

    public PreviewRenderer(RecordingData data, Dispatcher dispatcher)
    {
        _data = data;
        _times = data.Video.FrameTimes();
        _dispatcher = dispatcher;
        _front = new byte[data.Video.Width * data.Video.Height * 4];
        Bitmap = new WriteableBitmap(data.Video.Width, data.Video.Height, 96, 96, PixelFormats.Bgr32, null);
        _thread = new Thread(Run) { IsBackground = true, Name = "OpenPin recording preview" };
        _thread.Start();
    }

    public WriteableBitmap Bitmap { get; }

    /// <summary>Key pill sprites (rendered on the UI thread); takes effect for the next frame.</summary>
    public IReadOnlyDictionary<string, Codec.CursorShape>? KeySprites
    {
        set { lock (_gate) _sprites = value; }
    }

    public void Request(double timeMs, OverlayOptions options)
    {
        lock (_gate) _request = (timeMs, options);
        _signal.Set();
    }

    private void Run()
    {
        FrameComposer? composer = null;
        IReadOnlyDictionary<string, Codec.CursorShape>? composerSprites = null;
        (int Index, OverlayOptions? Options, (int, string?) State, bool Animating) last = (-1, null, default, false);
        try
        {
            while (!_stop)
            {
                _signal.WaitOne();
                if (_stop) break;
                (double Time, OverlayOptions Options) req;
                IReadOnlyDictionary<string, Codec.CursorShape>? sprites;
                lock (_gate)
                {
                    if (_request == null) continue;
                    req = _request.Value;
                    _request = null;
                    sprites = _sprites;
                }
                if (composer == null || !ReferenceEquals(sprites, composerSprites))
                {
                    composer?.Dispose();
                    composer = new FrameComposer(_data, sprites);
                    composerSprites = sprites;
                    last = (-1, null, default, false);
                }
                int idx = Timeline.FrameAt(_times, req.Time);
                bool animating = composer.IsAnimatingAt(req.Time, req.Options);
                var state = composer.TimeState(req.Time, req.Options);
                if (idx == last.Index && Equals(req.Options, last.Options) && state == last.State && !animating && !last.Animating) continue;
                var frame = composer.Compose(idx, req.Time, req.Options);
                lock (_front) Buffer.BlockCopy(frame, 0, _front, 0, _front.Length);
                last = (idx, req.Options, state, animating);
                if (Interlocked.Exchange(ref _uiPending, 1) == 0)
                    _dispatcher.BeginInvoke(Present, DispatcherPriority.Render);
            }
        }
        catch (Exception ex) { Log.Error("Preview renderer failed", ex); }
        finally { composer?.Dispose(); }
    }

    private void Present()
    {
        Volatile.Write(ref _uiPending, 0);
        if (_stop) return;
        lock (_front)
            Bitmap.WritePixels(new Int32Rect(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight), _front, Bitmap.PixelWidth * 4, 0);
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        _thread.Join(2000);
        _signal.Dispose();
    }
}
