using System.Runtime.InteropServices.WindowsRuntime;
using OpenPin.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace OpenPin.Recording.Capture;

/// <summary>
/// Webcam frames through WinRT <see cref="MediaCapture"/> + <see cref="MediaFrameReader"/> (converted to BGRA8 and scaled
/// down to at most <c>maxWidth</c> by the OS). Raises <see cref="Frame"/> on a worker thread. The camera is on only between
/// <see cref="StartAsync"/> and <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class WebcamCapture : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[] _buffer = Array.Empty<byte>();
    private int _busy;

    private WebcamCapture() { }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>A frame (BGRA top-down, width, height). The buffer is reused after the handler returns.</summary>
    public event Action<byte[], int, int>? Frame;

    public static async Task<IReadOnlyList<(string Id, string Name)>> ListAsync()
    {
        try
        {
            var all = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return all.Where(d => d.IsEnabled).Select(d => (d.Id, d.Name)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing cameras failed: {ex.Message}");
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>Open the camera (empty id = first one). Throws if no camera can be opened.</summary>
    public static async Task<WebcamCapture> StartAsync(string? deviceId, int maxWidth = 480)
    {
        var cams = await ListAsync();
        if (cams.Count == 0) throw new InvalidOperationException("No camera found");
        var cam = cams.FirstOrDefault(c => c.Id == deviceId);
        if (cam.Id == null) cam = cams[0];

        var wc = new WebcamCapture { Name = cam.Name };
        try
        {
            await wc.OpenAsync(cam.Id, maxWidth);
            return wc;
        }
        catch
        {
            await wc.DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync(string deviceId, int maxWidth)
    {
        Exception? last = null;
        bool exclusive = false;
        foreach (var sharing in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
        {
            try
            {
                _capture = new MediaCapture();
                await _capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = sharing,
                });
                exclusive = sharing == MediaCaptureSharingMode.ExclusiveControl;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                _capture?.Dispose();
                _capture = null;
            }
        }
        if (_capture == null) throw new InvalidOperationException("Camera could not be opened: " + last?.Message, last);

        var source = _capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .OrderBy(s => s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview ? 0 :
                          s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoRecord ? 1 : 2)
            .FirstOrDefault() ?? throw new InvalidOperationException("Camera has no color stream");

        // Prefer a modest format (≤ 1280 wide, ~30 fps) so the camera does not waste USB bandwidth.
        try
        {
            if (exclusive)
            {
                var fmt = source.SupportedFormats
                    .Where(f => f.VideoFormat.Width is >= 320 and <= 1280)
                    .OrderBy(f => Math.Abs(f.VideoFormat.Width - 640))
                    .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                    .FirstOrDefault();
                if (fmt != null) await source.SetFormatAsync(fmt);
            }
        }
        catch (Exception ex) { Log.Warn($"Setting camera format failed: {ex.Message}"); }

        var cur = source.CurrentFormat.VideoFormat;
        int sw = (int)Math.Max(1, cur.Width), sh = (int)Math.Max(1, cur.Height);
        int w = Math.Min(maxWidth, sw) & ~1;
        int h = Math.Max(2, (int)Math.Round((double)w * sh / sw) & ~1);
        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, new BitmapSize { Width = (uint)w, Height = (uint)h });
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException($"Camera reader: {status}");
        Width = w;
        Height = h;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bmp == null) return;
            SoftwareBitmap? converted = null;
            try
            {
                if (bmp.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    converted = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                var src = converted ?? bmp;
                int w = src.PixelWidth, h = src.PixelHeight, n = w * h * 4;
                if (_buffer.Length != n) _buffer = new byte[n];
                src.CopyToBuffer(_buffer.AsBuffer());
                Frame?.Invoke(_buffer, w, h);
            }
            finally
            {
                converted?.Dispose();
                bmp.Dispose();
            }
        }
        catch (Exception ex) { Log.Warn($"Camera frame failed: {ex.Message}"); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        var reader = _reader;
        _reader = null;
        if (reader != null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await reader.StopAsync(); } catch (Exception ex) { Log.Warn($"Stopping camera reader: {ex.Message}"); }
            reader.Dispose();
        }
        _capture?.Dispose();
        _capture = null;
    }
}
