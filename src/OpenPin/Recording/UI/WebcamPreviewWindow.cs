using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Native;

namespace OpenPin.Recording.UI;

/// <summary>
/// Live webcam preview shown where the picture-in-picture will appear in the video (same size/shape/corner math as
/// <see cref="Codec.FrameComposer"/>). Click-through and excluded from screen capture — the recording stores the camera
/// stream separately and composites it at export time, so the preview itself must never end up in the frames.
/// </summary>
internal sealed class WebcamPreviewWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _border;
    private WriteableBitmap? _bitmap;
    private byte[] _back = Array.Empty<byte>();
    private int _backW, _backH;
    private int _pending;
    private readonly object _gate = new();

    public WebcamPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "OpenPin Webcam";
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _border = new Border { Child = _image, ClipToBounds = true, BorderThickness = new Thickness(2) };
        _border.SetResourceReference(Border.BorderBrushProperty, "Brush.OnAccent");
        _border.SetResourceReference(Border.BackgroundProperty, "Brush.Recording.Countdown");
        Content = _border;
        SourceInitialized += (_, _) =>
        {
            WindowHelper.SetToolWindow(this, true);
            WindowHelper.SetNoActivate(this, true);
            WindowHelper.SetClickThrough(this, true);
            Excluded = WindowHelper.SetExcludeFromCapture(this, true);
        };
    }

    public bool Excluded { get; private set; }
    public double CameraAspect { get; private set; } = 4.0 / 3.0;

    /// <summary>Position and shape for a region (physical px).</summary>
    public void Layout(Int32Rect region, RecordingSettings s)
    {
        double unit = Math.Clamp(Math.Min(region.Width, region.Height) / 720.0, 0.6, 2.5);
        int th = Math.Max(16, (int)Math.Round(region.Height * Math.Clamp(s.WebcamSize, 0.1, 0.5)));
        int tw = s.WebcamShape == WebcamShape.Circle ? th : (int)Math.Round(th * CameraAspect);
        tw = Math.Min(tw, region.Width);
        th = Math.Min(th, region.Height);
        int margin = (int)Math.Round(16 * unit);
        int x = s.WebcamCorner is WebcamCorner.TopLeft or WebcamCorner.BottomLeft ? region.X + margin : region.X + region.Width - tw - margin;
        int y = s.WebcamCorner is WebcamCorner.TopLeft or WebcamCorner.TopRight ? region.Y + margin : region.Y + region.Height - th - margin;
        WindowHelper.SetBoundsPx(this, new Int32Rect(x, y, tw, th), topmost: true);
        double scale = WindowHelper.GetDpiScale(this);
        double r = s.WebcamShape == WebcamShape.Circle ? th / 2.0 / scale : 12 * unit / scale;
        _border.CornerRadius = new CornerRadius(r);
        _image.Clip = new RectangleGeometry(new Rect(0, 0, tw / scale - 4, th / scale - 4), Math.Max(0, r - 2), Math.Max(0, r - 2));
    }

    /// <summary>Called on the camera thread with a BGRA frame.</summary>
    public void PushFrame(byte[] bgra, int w, int h)
    {
        lock (_gate)
        {
            if (_back.Length != bgra.Length) _back = new byte[bgra.Length];
            Buffer.BlockCopy(bgra, 0, _back, 0, bgra.Length);
            _backW = w;
            _backH = h;
        }
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _pending, 0);
            lock (_gate)
            {
                if (_backW == 0) return;
                CameraAspect = (double)_backW / _backH;
                if (_bitmap == null || _bitmap.PixelWidth != _backW || _bitmap.PixelHeight != _backH)
                {
                    _bitmap = new WriteableBitmap(_backW, _backH, 96, 96, PixelFormats.Bgr32, null);
                    _image.Source = _bitmap;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, _backW, _backH), _back, _backW * 4, 0);
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
}
