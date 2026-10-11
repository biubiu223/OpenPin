namespace OpenPin.Recording.Codec;

/// <summary>
/// Produces displayable frames: decoded screen frame + overlays drawn from the recorded side data — webcam picture-in-picture,
/// click ripples, wheel indicator, keystroke pill, cursor. Used by the playback preview and the exporters, so every overlay
/// can be toggled after recording. Not thread-safe (owns readers). Key pill images are rendered beforehand (WPF text needs
/// the UI thread) and passed in as sprites keyed by label.
/// </summary>
internal sealed class FrameComposer : IDisposable
{
    public const uint LeftClickColor = 0x1677FF;   // Theme Color.Accent
    public const uint RightClickColor = 0xFA8C16;  // Theme Color.PinLocked (orange)
    public const uint MiddleClickColor = 0x52C41A; // Theme Color.PinClickThrough (green)
    public const double WheelMs = 450;

    private readonly RecordingData _data;
    private readonly FrameReader _reader;
    private readonly FrameReader? _webcamReader;
    private readonly double[]? _webcamTimes;
    private readonly IReadOnlyDictionary<string, CursorShape> _keySprites;
    private readonly Dictionary<KeystrokeFilter, List<KeySegment>> _segments = new();
    private byte[] _camScaled = Array.Empty<byte>();
    private (int Index, int W, int H) _camCache = (-1, 0, 0);

    public FrameComposer(RecordingData data, IReadOnlyDictionary<string, CursorShape>? keySprites = null)
    {
        _data = data;
        _reader = data.Video.OpenReader();
        if (data.Webcam is { Frames.Count: > 0 } cam)
        {
            _webcamReader = cam.OpenReader();
            _webcamTimes = cam.FrameTimes();
        }
        _keySprites = keySprites ?? new Dictionary<string, CursorShape>();
        Output = new byte[data.Video.Width * data.Video.Height * 4];
    }

    public int Width => _data.Video.Width;
    public int Height => _data.Video.Height;

    /// <summary>The last composed frame (BGRA, alpha 255).</summary>
    public byte[] Output { get; }

    public List<KeySegment> KeySegments(KeystrokeFilter filter)
    {
        if (!_segments.TryGetValue(filter, out var s))
            _segments[filter] = s = Keystrokes.BuildTimeline(_data.Input.Keys, filter, KeyNameFallback);
        return s;
    }

    /// <summary>Resolves OEM key names with the current keyboard layout (set by the UI layer; null in tests).</summary>
    public static Func<int, string?>? KeyNameFallback { get; set; }

    /// <summary>True if any time-animated overlay (ripple, wheel) is visible at <paramref name="timeMs"/>.</summary>
    public bool IsAnimatingAt(double timeMs, OverlayOptions o)
    {
        if (o.ShowClicks)
            foreach (var c in _data.Input.Clicks)
                if (c.TimeMs <= timeMs && timeMs < c.TimeMs + o.ClickAnimationMs) return true;
        if (o.ShowWheel)
            foreach (var w in _data.Input.Wheels)
                if (w.TimeMs <= timeMs && timeMs < w.TimeMs + WheelMs) return true;
        return false;
    }

    /// <summary>Identity of everything time-dependent except the screen frame (to skip redundant re-composition).</summary>
    public (int Webcam, string? Key) TimeState(double timeMs, OverlayOptions o)
    {
        int cam = o.ShowWebcam && _webcamTimes != null && timeMs >= _webcamTimes[0] ? Timeline.FrameAt(_webcamTimes, timeMs) : -1;
        string? key = o.ShowKeys ? Keystrokes.At(KeySegments(o.KeyFilter), timeMs)?.Label : null;
        return (cam, key);
    }

    /// <summary>Compose frame <paramref name="index"/> as seen at <paramref name="timeMs"/> (recording clock) into <see cref="Output"/>.</summary>
    public byte[] Compose(int index, double timeMs, OverlayOptions o)
    {
        _reader.Seek(index);
        _reader.Canvas.AsSpan().CopyTo(Output);
        int w = Width, h = Height;
        double unit = Math.Clamp(Math.Min(w, h) / 720.0, 0.6, 2.5); // overlay scale relative to a 720p video

        if (o.ShowWebcam) DrawWebcam(timeMs, o, w, h, unit);

        if (o.ShowClicks)
        {
            foreach (var c in _data.Input.Clicks)
            {
                double age = timeMs - c.TimeMs;
                if (age < 0 || age >= o.ClickAnimationMs) continue;
                double p = age / o.ClickAnimationMs;
                double eased = 1 - (1 - p) * (1 - p);
                uint color = c.Button switch { MouseButtonKind.Right => RightClickColor, MouseButtonKind.Middle => MiddleClickColor, _ => LeftClickColor };
                PixelOps.DrawRing(Output, w, h, c.X, c.Y, (6 + 18 * eased) * unit, 3 * unit, color, 0.9 * (1 - p), 0.28 * (1 - p));
            }
        }

        if (o.ShowWheel)
        {
            foreach (var e in _data.Input.Wheels)
            {
                double age = timeMs - e.TimeMs;
                if (age < 0 || age >= WheelMs) continue;
                DrawWheel(e, 1 - age / WheelMs, w, h, unit);
            }
        }

        if (o.ShowKeys && Keystrokes.At(KeySegments(o.KeyFilter), timeMs) is { } seg && _keySprites.TryGetValue(seg.Label, out var pill))
        {
            int px = (w - pill.Width) / 2, py = h - pill.Height - (int)(24 * unit);
            PixelOps.DrawCursor(Output, w, h, pill, px, Math.Max(0, py));
        }

        var cursor = _data.Video.Frames[index].Cursor;
        if (o.ShowCursor && cursor.Id >= 0 && cursor.Id < _data.Video.Cursors.Count)
            PixelOps.DrawCursor(Output, w, h, _data.Video.Cursors[cursor.Id], cursor.X, cursor.Y);
        return Output;
    }

    private void DrawWheel(WheelEvent e, double fade, int w, int h, double unit)
    {
        // a small disc next to the pointer with a chevron pointing in the scroll direction
        double r = 13 * unit;
        double cx = e.X + 22 * unit, cy = e.Y + 22 * unit;
        PixelOps.DrawRing(Output, w, h, cx, cy, r / 2, r, 0xFFFFFF, 0.85 * fade, 0); // white disc (band from 0 to r)
        PixelOps.DrawRing(Output, w, h, cx, cy, r - 1, 2 * unit, LeftClickColor, 0.9 * fade, 0);
        double s = 6 * unit;
        int dir = e.Delta > 0 ? -1 : 1; // wheel away from the user = scroll up
        Span<(double, double)> tri = stackalloc (double, double)[3];
        if (!e.Horizontal)
        {
            tri[0] = (cx - s, cy - dir * s * 0.5); tri[1] = (cx + s, cy - dir * s * 0.5); tri[2] = (cx, cy + dir * s * 0.7);
        }
        else
        {
            int hd = e.Delta > 0 ? 1 : -1;
            tri[0] = (cx - hd * s * 0.5, cy - s); tri[1] = (cx - hd * s * 0.5, cy + s); tri[2] = (cx + hd * s * 0.7, cy);
        }
        PixelOps.FillPolygon(Output, w, h, tri, LeftClickColor, fade);
    }

    private void DrawWebcam(double timeMs, OverlayOptions o, int w, int h, double unit)
    {
        if (_webcamReader == null || _webcamTimes == null || timeMs < _webcamTimes[0]) return;
        int idx = Timeline.FrameAt(_webcamTimes, timeMs);
        int camW = _webcamReader.Width, camH = _webcamReader.Height;

        int th = Math.Max(16, (int)Math.Round(h * Math.Clamp(o.WebcamSize, 0.05, 0.9)));
        int tw = o.WebcamShape == WebcamShape.Circle ? th : (int)Math.Round(th * (double)camW / camH);
        tw = Math.Min(tw, w); th = Math.Min(th, h);
        if (tw < 8 || th < 8) return;

        if (_camCache != (idx, tw, th))
        {
            _webcamReader.Seek(idx);
            // circle: centre-crop a square from the camera image first
            int sx = 0, sy = 0, sw = camW, sh = camH;
            if (o.WebcamShape == WebcamShape.Circle)
            {
                int side = Math.Min(camW, camH);
                sx = (camW - side) / 2; sy = (camH - side) / 2; sw = sh = side;
            }
            var crop = new byte[sw * sh * 4];
            PixelOps.CopyRect(_webcamReader.Canvas, camW, new System.Windows.Int32Rect(sx, sy, sw, sh), crop);
            if (_camScaled.Length != tw * th * 4) _camScaled = new byte[tw * th * 4];
            PixelOps.Resize(crop, sw, sh, _camScaled, tw, th);
            _camCache = (idx, tw, th);
        }

        int margin = (int)Math.Round(16 * unit);
        int x = o.WebcamCorner is WebcamCorner.TopLeft or WebcamCorner.BottomLeft ? margin : w - tw - margin;
        int y = o.WebcamCorner is WebcamCorner.TopLeft or WebcamCorner.TopRight ? margin : h - th - margin;
        double radius = o.WebcamShape == WebcamShape.Circle ? th / 2.0 : 12 * unit;
        PixelOps.BlendRounded(Output, w, h, _camScaled, tw, th, Math.Max(0, x), Math.Max(0, y), radius, 2 * unit);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _webcamReader?.Dispose();
    }
}
