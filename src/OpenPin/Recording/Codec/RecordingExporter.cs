using System.IO;

namespace OpenPin.Recording.Codec;

/// <summary>Everything an export needs besides the recording and the target path.</summary>
internal sealed record ExportOptions
{
    public RecordingFormat Format { get; init; } = RecordingFormat.Gif;
    public RecordingQuality Quality { get; init; } = RecordingQuality.Standard;
    public double TrimStartMs { get; init; }
    /// <summary>End of the exported range; ≤ 0 = end of the recording.</summary>
    public double TrimEndMs { get; init; }
    public OverlayOptions Overlays { get; init; } = new();
    /// <summary>Pre-rendered keystroke pills (label → image), see <see cref="FrameComposer"/>.</summary>
    public IReadOnlyDictionary<string, CursorShape>? KeySprites { get; init; }
    public GifDitherMode Dither { get; init; } = GifDitherMode.FloydSteinberg;
    /// <summary>MP4 only (GIF is always silent).</summary>
    public bool IncludeMicrophone { get; init; } = true;
    public bool IncludeSystemAudio { get; init; } = true;
}

/// <summary>
/// Exports a <see cref="RecordingData"/> to GIF (own encoder) or MP4 (OS H.264 + AAC). Runs entirely off the UI thread,
/// reports progress 0..1 and honours cancellation. Writes to a temporary file next to the target and renames it at the end,
/// so a cancelled or failed export never leaves a half-written file behind.
/// </summary>
internal static class RecordingExporter
{
    public static async Task ExportAsync(RecordingData data, ExportOptions o, string path, IProgress<double>? progress, CancellationToken ct)
    {
        var store = data.Video;
        if (store.Frames.Count == 0) throw new InvalidOperationException("Empty recording");
        var (start, end) = Timeline.ClampTrim(o.TrimStartMs, o.TrimEndMs <= 0 ? store.DurationMs : o.TrimEndMs, store.DurationMs);
        var plan = Timeline.Plan(o.Format, o.Quality, store.Fps, store.Width, store.Height);
        var times = store.FrameTimes();
        var indices = Timeline.PlanConstant(times, start, end, plan.Fps);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".openpin-tmp";
        try
        {
            if (o.Format == RecordingFormat.Gif)
                await Task.Run(() => ExportGif(data, o, plan, indices, start, end, tmp, progress, ct), ct).ConfigureAwait(false);
            else
                await Task.Run(() => ExportMp4Async(data, o, plan, indices, start, end, tmp, progress, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static void ExportGif(RecordingData data, ExportOptions o, ExportPlan plan, int[] indices, double start, double end,
        string tmp, IProgress<double>? progress, CancellationToken ct)
    {
        var store = data.Video;
        using var composer = new FrameComposer(data, o.KeySprites);
        var scaled = new byte[plan.Width * plan.Height * 4];
        bool needScale = plan.Width != store.Width || plan.Height != store.Height;
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var gif = new GifEncoder(fs, plan.Width, plan.Height, o.Dither);
        int lastIndex = -1;
        bool lastAnimated = false;
        (int, string?) lastState = default;
        for (int k = 0; k < indices.Length; k++)
        {
            ct.ThrowIfCancellationRequested();
            double rel = k * 1000.0 / plan.Fps;
            double t = start + rel;
            bool animated = composer.IsAnimatingAt(t, o.Overlays);
            var state = composer.TimeState(t, o.Overlays);
            // same source frame and no overlay change → identical output; the encoder would merge it anyway
            if (k > 0 && indices[k] == lastIndex && !animated && !lastAnimated && state == lastState) continue;
            var frame = composer.Compose(indices[k], t, o.Overlays);
            if (needScale) PixelOps.Resize(frame, store.Width, store.Height, scaled, plan.Width, plan.Height);
            gif.AddFrame(needScale ? scaled : frame, rel);
            lastIndex = indices[k];
            lastAnimated = animated;
            lastState = state;
            if ((k & 3) == 0) progress?.Report((double)k / indices.Length);
        }
        gif.Finish(end - start);
        progress?.Report(1);
    }

    private static async Task ExportMp4Async(RecordingData data, ExportOptions o, ExportPlan plan, int[] indices, double start, double end,
        string tmp, IProgress<double>? progress, CancellationToken ct)
    {
        var store = data.Video;
        using var composer = new FrameComposer(data, o.KeySprites);
        bool needScale = plan.ContentWidth != store.Width || plan.ContentHeight != store.Height;
        var scaled = needScale ? new byte[plan.ContentWidth * plan.ContentHeight * 4] : null;
        void Fill(int k, byte[] buf)
        {
            double t = start + k * 1000.0 / plan.Fps;
            var frame = composer.Compose(indices[k], t, o.Overlays);
            if (scaled != null) PixelOps.Resize(frame, store.Width, store.Height, scaled, plan.ContentWidth, plan.ContentHeight);
            PixelOps.PadReplicate(scaled ?? frame, plan.ContentWidth, plan.ContentHeight, buf, plan.Width, plan.Height);
        }

        var readers = new List<AudioReader>();
        try
        {
            if (o.IncludeMicrophone && data.Microphone != null) readers.Add(data.Microphone.OpenReader());
            if (o.IncludeSystemAudio && data.SystemAudio != null) readers.Add(data.SystemAudio.OpenReader());
            Mp4Audio? audio = null;
            if (readers.Count > 0)
            {
                // the video is indices.Length frames long; make the audio exactly as long
                long first = AudioFormat.MsToFrames(start);
                long total = AudioFormat.MsToFrames(indices.Length * 1000.0 / plan.Fps);
                var scratch = new float[960 * AudioFormat.Channels];
                audio = new Mp4Audio(total, (pos, dest) => AudioMixer.MixToPcm16(readers, first + pos, dest, scratch));
            }
            await Mp4Encoder.EncodeAsync(tmp, plan.Width, plan.Height, plan.Fps, indices.Length, plan.Bitrate, Fill, progress, ct, audio)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }
    }
}
