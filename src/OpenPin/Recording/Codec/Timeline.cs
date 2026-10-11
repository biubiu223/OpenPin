namespace OpenPin.Recording.Codec;

public enum RecordingFormat { Gif, Mp4 }

/// <summary>
/// What an export produces: output frame rate, output size (MP4: even, padded), the scaled content size inside it, and the
/// MP4 bitrate (0 for GIF).
/// </summary>
internal readonly record struct ExportPlan(int Fps, int Width, int Height, int ContentWidth, int ContentHeight, uint Bitrate);

/// <summary>UI-free timing math: trimming, resampling to an output frame rate, GIF delays, export presets.</summary>
internal static class Timeline
{
    /// <summary>GIF delays are centiseconds and browsers treat delays &lt; 2 cs as 10 cs, so GIF is capped at 50 fps.</summary>
    public const int GifMaxFps = 50;

    /// <summary>Shortest selectable trimmed length.</summary>
    public const double MinTrimLengthMs = 100;

    /// <summary>Index of the frame visible at <paramref name="timeMs"/> (last frame with time ≤ t; 0 if t precedes all).</summary>
    public static int FrameAt(IReadOnlyList<double> times, double timeMs)
    {
        int lo = 0, hi = times.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (times[mid] <= timeMs) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Clamp a trim selection to [0, duration] with at least <see cref="MinTrimLengthMs"/> (or the whole duration if shorter).</summary>
    public static (double Start, double End) ClampTrim(double start, double end, double durationMs)
    {
        double min = Math.Min(MinTrimLengthMs, durationMs);
        start = Math.Clamp(start, 0, durationMs);
        end = Math.Clamp(end, 0, durationMs);
        if (end < start) (start, end) = (end, start);
        if (end - start < min)
        {
            end = Math.Min(durationMs, start + min);
            start = Math.Max(0, end - min);
        }
        return (start, end);
    }

    /// <summary>Number of output frames for a constant-rate export of [start, end).</summary>
    public static int OutputFrameCount(double startMs, double endMs, double fps) =>
        Math.Max(1, (int)Math.Ceiling((endMs - startMs) * fps / 1000.0 - 1e-6));

    /// <summary>
    /// Resample to a constant output rate: output frame k (shown at start + k/fps) uses the last source frame whose time is
    /// ≤ that instant. Frames that fall between two output instants are dropped; static periods repeat the same index.
    /// </summary>
    public static int[] PlanConstant(IReadOnlyList<double> times, double startMs, double endMs, double fps)
    {
        if (times.Count == 0) return Array.Empty<int>();
        int n = OutputFrameCount(startMs, endMs, fps);
        var result = new int[n];
        for (int k = 0; k < n; k++)
            result[k] = FrameAt(times, startMs + k * 1000.0 / fps);
        return result;
    }

    /// <summary>Centisecond timestamp with symmetric rounding (used for accumulated GIF delays).</summary>
    public static int ToCentiseconds(double ms) => (int)Math.Round(ms / 10.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// GIF delays (centiseconds) for frames starting at <paramref name="startsMs"/> (ascending, relative to the export start)
    /// and an animation ending at <paramref name="endMs"/>. Delays are differences of rounded absolute times, so rounding
    /// errors never accumulate; each delay is at least 2 cs and at most 65535.
    /// </summary>
    public static int[] GifDelays(IReadOnlyList<double> startsMs, double endMs)
    {
        var d = new int[startsMs.Count];
        for (int i = 0; i < d.Length; i++)
        {
            double next = i + 1 < d.Length ? startsMs[i + 1] : endMs;
            d[i] = Math.Clamp(ToCentiseconds(next) - ToCentiseconds(startsMs[i]), 2, 65535);
        }
        return d;
    }

    /// <summary>
    /// Output parameters for a format/quality. 原始 keeps the capture fps and size; 标准 caps the frame rate
    /// (GIF 15, MP4 30); 轻量 caps it further (GIF 10, MP4 15) and halves the size (keeping the long side ≥ 320 px).
    /// MP4 dimensions are rounded up to even numbers (H.264 4:2:0); bitrate ≈ pixels × fps × bits-per-pixel.
    /// </summary>
    public static ExportPlan Plan(RecordingFormat format, RecordingQuality quality, int sourceFps, int width, int height)
    {
        sourceFps = Math.Max(1, sourceFps);
        int fps;
        double scale = 1.0, bpp;
        switch (quality)
        {
            case RecordingQuality.Light:
                fps = Math.Min(sourceFps, format == RecordingFormat.Gif ? 10 : 15);
                int longSide = Math.Max(width, height);
                scale = longSide <= 320 ? 1.0 : Math.Max(0.5, 320.0 / longSide);
                bpp = 0.06;
                break;
            case RecordingQuality.Original:
                fps = sourceFps;
                bpp = 0.2;
                break;
            default:
                fps = Math.Min(sourceFps, format == RecordingFormat.Gif ? 15 : 30);
                bpp = 0.1;
                break;
        }
        if (format == RecordingFormat.Gif) fps = Math.Min(fps, GifMaxFps);

        int cw = Math.Max(1, (int)Math.Round(width * scale));
        int ch = Math.Max(1, (int)Math.Round(height * scale));
        int w = cw, h = ch;
        uint bitrate = 0;
        if (format == RecordingFormat.Mp4)
        {
            w = Math.Max(16, (w + 1) & ~1);
            h = Math.Max(16, (h + 1) & ~1);
            bitrate = (uint)Math.Clamp(w * (double)h * fps * bpp, 500_000, 50_000_000);
        }
        return new ExportPlan(fps, w, h, cw, ch, bitrate);
    }

    /// <summary>Format a time like "01:23" or "1:02:03" (recording timer, playback labels).</summary>
    public static string FormatTime(double ms, bool withTenths = false)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        string s = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        return withTenths ? $"{s}.{t.Milliseconds / 100}" : s;
    }
}
