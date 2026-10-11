using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace OpenPin.Recording.Codec;

/// <summary>Optional audio for an MP4: 48 kHz stereo 16-bit PCM pulled on demand, encoded as AAC.</summary>
internal sealed class Mp4Audio
{
    public Mp4Audio(long totalFrames, Action<long, Span<short>> fill) { TotalFrames = totalFrames; Fill = fill; }

    /// <summary>Length in sample frames (48 kHz).</summary>
    public long TotalFrames { get; }
    /// <summary>Write interleaved stereo samples starting at the given frame into the span.</summary>
    public Action<long, Span<short>> Fill { get; }
    public uint Bitrate { get; init; } = 192_000;
}

/// <summary>
/// H.264/AAC MP4 encoder on top of the OS media stack (WinRT <see cref="MediaTranscoder"/> fed by a <see cref="MediaStreamSource"/>
/// with an uncompressed BGRA video stream and an optional PCM audio stream; Media Foundation converts to NV12 and picks a
/// hardware or the software H.264 encoder and the AAC encoder). No extra binaries. Falls back to the software video encoder
/// if hardware encoding fails.
/// </summary>
internal static class Mp4Encoder
{
    /// <summary>
    /// Encode <paramref name="frameCount"/> frames at a constant <paramref name="fps"/>. <paramref name="fill"/> writes frame k
    /// (BGRA, top-down, stride = width*4, width/height even) into the given buffer; it is called sequentially from a worker thread.
    /// </summary>
    public static async Task EncodeAsync(string path, int width, int height, int fps, int frameCount, uint bitrate,
        Action<int, byte[]> fill, IProgress<double>? progress, CancellationToken ct, Mp4Audio? audio = null)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("MP4 frame size must be even");
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));

        Exception? first = null;
        foreach (bool hardware in new[] { true, false })
        {
            try
            {
                await EncodeOnceAsync(path, width, height, fps, frameCount, bitrate, fill, audio, progress, hardware, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (hardware)
            {
                first = ex;
                Core.Log.Warn($"Hardware H.264 encoding failed, retrying with the software encoder: {ex.Message}");
                progress?.Report(0);
            }
        }
        throw first ?? new InvalidOperationException("MP4 encoding failed");
    }

    private static async Task EncodeOnceAsync(string path, int width, int height, int fps, int frameCount, uint bitrate,
        Action<int, byte[]> fill, Mp4Audio? audio, IProgress<double>? progress, bool hardware, CancellationToken ct)
    {
        int frameBytes = width * height * 4;
        long frameTicks = TimeSpan.TicksPerSecond / fps;

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        input.FrameRate.Numerator = (uint)fps;
        input.FrameRate.Denominator = 1;
        var videoDesc = new VideoStreamDescriptor(input);
        const int audioChunk = 960; // 20 ms
        AudioStreamDescriptor? audioDesc = audio != null && audio.TotalFrames > 0
            ? new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioFormat.SampleRate, AudioFormat.Channels, 16))
            : null;
        var source = audioDesc != null ? new MediaStreamSource(videoDesc, audioDesc) : new MediaStreamSource(videoDesc);
        source.BufferTime = TimeSpan.Zero;
        source.Duration = TimeSpan.FromTicks(frameTicks * frameCount);
        source.CanSeek = false;

        var pool = new ConcurrentBag<byte[]>();
        int next = 0;
        long audioPos = 0;
        Exception? fillError = null;
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            if (audioDesc != null && e.Request.StreamDescriptor is AudioStreamDescriptor)
            {
                if (audioPos >= audio!.TotalFrames || ct.IsCancellationRequested || fillError != null)
                {
                    e.Request.Sample = null;
                    return;
                }
                try
                {
                    int n = (int)Math.Min(audioChunk, audio.TotalFrames - audioPos);
                    var bytes = new byte[n * AudioFormat.Channels * 2];
                    audio.Fill(audioPos, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan()));
                    var s = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), TimeSpan.FromTicks(audioPos * TimeSpan.TicksPerSecond / AudioFormat.SampleRate));
                    s.Duration = TimeSpan.FromTicks(n * TimeSpan.TicksPerSecond / AudioFormat.SampleRate);
                    s.KeyFrame = true;
                    e.Request.Sample = s;
                    audioPos += n;
                }
                catch (Exception ex)
                {
                    fillError = ex;
                    e.Request.Sample = null;
                }
                return;
            }
            if (next >= frameCount || ct.IsCancellationRequested || fillError != null)
            {
                e.Request.Sample = null; // end of stream
                return;
            }
            try
            {
                if (!pool.TryTake(out var buf)) buf = new byte[frameBytes];
                fill(next, buf);
                // Uncompressed RGB media types are bottom-up in Media Foundation (positive default stride).
                FlipRows(buf, width * 4, height);
                var sample = MediaStreamSample.CreateFromBuffer(buf.AsBuffer(0, frameBytes), TimeSpan.FromTicks(next * frameTicks));
                sample.Duration = TimeSpan.FromTicks(frameTicks);
                sample.KeyFrame = true;
                sample.Processed += (_, _) => pool.Add(buf);
                e.Request.Sample = sample;
                next++;
                progress?.Report((double)next / frameCount);
            }
            catch (Exception ex)
            {
                fillError = ex;
                e.Request.Sample = null;
            }
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Audio = audioDesc != null ? AudioEncodingProperties.CreateAac(AudioFormat.SampleRate, AudioFormat.Channels, audio!.Bitrate) : null;
        profile.Video.Subtype = MediaEncodingSubtypes.H264;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.Bitrate = bitrate;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var stream = fs.AsRandomAccessStream();
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware, AlwaysReencode = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile).AsTask(ct).ConfigureAwait(false);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException($"Cannot transcode to MP4: {prepared.FailureReason}");
            await prepared.TranscodeAsync().AsTask(ct).ConfigureAwait(false);
            await stream.FlushAsync().AsTask(ct).ConfigureAwait(false);
        }
        if (fillError != null) throw new InvalidOperationException("Preparing a video frame failed", fillError);
        ct.ThrowIfCancellationRequested();
        if (next < frameCount) throw new InvalidOperationException($"Encoder stopped after {next} of {frameCount} frames");
    }

    internal static void FlipRows(byte[] buf, int stride, int height)
    {
        var tmp = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            var a = buf.AsSpan(top * stride, stride);
            var b = buf.AsSpan(bottom * stride, stride);
            a.CopyTo(tmp);
            b.CopyTo(a);
            tmp.AsSpan().CopyTo(b);
        }
    }
}
