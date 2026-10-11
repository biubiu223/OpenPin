using System.IO;
using System.Runtime.InteropServices;

namespace OpenPin.Recording.Codec;

/// <summary>Fixed internal audio format: 48 kHz, stereo, 32-bit float (interleaved).</summary>
internal static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public static long MsToFrames(double ms) => (long)Math.Round(ms * SampleRate / 1000.0);
    public static double FramesToMs(long frames) => frames * 1000.0 / SampleRate;
}

/// <summary>
/// Writes one audio source (mic or system) as raw float PCM aligned to the recording clock. Each packet carries the clock
/// time of its first sample; if the file is behind (WASAPI loopback delivers nothing while the system is silent, or the
/// device hiccupped) silence is inserted, if it is ahead (clock drift) the overlap is dropped. Packets that arrive while
/// the recording is paused must not be appended (the caller checks) — the clock does not advance during a pause, so the
/// track stays aligned. Single writer thread.
/// </summary>
internal sealed class AudioTrackWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _toleranceFrames;
    private float[] _scratch = Array.Empty<float>();
    private bool _completed;

    public AudioTrackWriter(string path, double toleranceMs = 60)
        : this(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1 << 16), toleranceMs, true)
    {
        Path = path;
    }

    internal AudioTrackWriter(Stream stream, double toleranceMs, bool ownsStream)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _toleranceFrames = AudioFormat.MsToFrames(toleranceMs);
        Path = "";
    }

    public string Path { get; }
    public long FramesWritten { get; private set; }

    /// <summary>Linear gain applied while writing (volume slider; may change during the recording).</summary>
    public float Gain { get; set; } = 1f;

    /// <summary>Peak level (0..1) of the last packet, for a level meter.</summary>
    public float LastPeak { get; private set; }

    /// <summary>Append interleaved stereo 48 kHz samples whose first frame was captured at <paramref name="startMs"/> (recording clock).</summary>
    public void Append(ReadOnlySpan<float> stereo, double startMs)
    {
        if (_completed) return;
        int frames = stereo.Length / AudioFormat.Channels;
        if (frames == 0) return;
        long expected = AudioFormat.MsToFrames(startMs);
        if (expected < 0)
        {
            int skip = (int)Math.Min(frames, -expected);
            stereo = stereo[(skip * AudioFormat.Channels)..];
            frames -= skip;
            expected = 0;
        }
        long diff = expected - FramesWritten;
        if (diff > _toleranceFrames) WriteSilence(diff);
        else if (diff < -_toleranceFrames)
        {
            int skip = (int)Math.Min(frames, -diff);
            stereo = stereo[(skip * AudioFormat.Channels)..];
            frames -= skip;
        }
        if (frames > 0) Write(stereo);
    }

    private void Write(ReadOnlySpan<float> samples)
    {
        if (_scratch.Length < samples.Length) _scratch = new float[samples.Length];
        var s = _scratch.AsSpan(0, samples.Length);
        float g = Gain, peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * g;
            s[i] = v;
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        LastPeak = Math.Min(1f, peak);
        _stream.Write(MemoryMarshal.AsBytes(s));
        FramesWritten += samples.Length / AudioFormat.Channels;
    }

    private void WriteSilence(long frames)
    {
        Span<byte> zeros = stackalloc byte[4096];
        long bytes = frames * AudioFormat.Channels * sizeof(float);
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, zeros.Length);
            _stream.Write(zeros[..n]);
            bytes -= n;
        }
        FramesWritten += frames;
    }

    /// <summary>Pad/truncate to <paramref name="durationMs"/> and close. Returns the readable track.</summary>
    public AudioTrack Complete(double durationMs)
    {
        long target = AudioFormat.MsToFrames(durationMs);
        if (!_completed)
        {
            if (FramesWritten < target) WriteSilence(target - FramesWritten);
            else if (FramesWritten > target)
            {
                _stream.SetLength(target * AudioFormat.Channels * sizeof(float));
                FramesWritten = target;
            }
            _stream.Flush();
            _completed = true;
            if (_ownsStream) _stream.Dispose();
        }
        return new AudioTrack(Path, FramesWritten);
    }

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        if (_ownsStream) _stream.Dispose();
    }
}

/// <summary>A finished audio track (raw float stereo 48 kHz file).</summary>
internal sealed class AudioTrack
{
    public AudioTrack(string path, long frames) { Path = path; Frames = frames; }

    public string Path { get; }
    public long Frames { get; }
    public double DurationMs => AudioFormat.FramesToMs(Frames);

    public AudioReader OpenReader() => new(this);

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) { Core.Log.Warn($"Could not delete audio temp file {Path}: {ex.Message}"); }
    }
}

/// <summary>Random-access reader of an <see cref="AudioTrack"/>. Not thread-safe.</summary>
internal sealed class AudioReader : IDisposable
{
    private readonly AudioTrack _track;
    private readonly FileStream _file;

    public AudioReader(AudioTrack track)
    {
        _track = track;
        _file = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
    }

    /// <summary>Read interleaved frames starting at <paramref name="startFrame"/>; positions outside the track read as silence.</summary>
    public void Read(long startFrame, Span<float> dest)
    {
        dest.Clear();
        int frames = dest.Length / AudioFormat.Channels;
        long first = Math.Max(0, startFrame), last = Math.Min(_track.Frames, startFrame + frames);
        if (last <= first) return;
        var target = dest.Slice((int)(first - startFrame) * AudioFormat.Channels, (int)(last - first) * AudioFormat.Channels);
        _file.Position = first * AudioFormat.Channels * sizeof(float);
        _file.ReadExactly(MemoryMarshal.AsBytes(target));
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Mixes tracks into 16-bit PCM (for the MP4 audio stream).</summary>
internal static class AudioMixer
{
    public static void MixToPcm16(IReadOnlyList<AudioReader> readers, long startFrame, Span<short> output, float[]? scratch = null)
    {
        int n = output.Length;
        scratch ??= new float[n];
        Span<float> acc = n <= 8192 ? stackalloc float[n] : new float[n];
        acc.Clear();
        foreach (var r in readers)
        {
            var buf = scratch.AsSpan(0, n);
            r.Read(startFrame, buf);
            for (int i = 0; i < n; i++) acc[i] += buf[i];
        }
        for (int i = 0; i < n; i++)
            output[i] = (short)Math.Clamp((int)MathF.Round(acc[i] * 32767f), short.MinValue, short.MaxValue);
    }
}

/// <summary>
/// Streaming conversion of device audio (any channel count, float/int samples, any rate) to stereo 48 kHz float with linear
/// interpolation. Keeps the last input frame between packets so there are no clicks at packet borders.
/// </summary>
internal sealed class AudioConverter
{
    private readonly int _channels;
    private readonly double _step;
    private double _pos;
    private float _lastL, _lastR;
    private bool _primed;
    private float[] _stereo = Array.Empty<float>();
    private float[] _out = Array.Empty<float>();

    public AudioConverter(int sourceRate, int sourceChannels)
    {
        _channels = Math.Max(1, sourceChannels);
        _step = (double)sourceRate / AudioFormat.SampleRate;
    }

    /// <summary>Converts float samples (interleaved, source channel count). Returns a span valid until the next call.</summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> input)
    {
        int frames = input.Length / _channels;
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            int i = f * _channels;
            float l = input[i], r = _channels > 1 ? input[i + 1] : l;
            if (_channels >= 3) { float c = input[i + 2] * 0.707f; l += c; r += c; } // 5.1/7.1: fold the centre in
            _stereo[f * 2] = l;
            _stereo[f * 2 + 1] = r;
        }
        return Resample(_stereo.AsSpan(0, frames * 2), frames);
    }

    private ReadOnlySpan<float> Resample(ReadOnlySpan<float> st, int frames)
    {
        if (frames == 0) return ReadOnlySpan<float>.Empty;
        if (!_primed) { _lastL = st[0]; _lastR = st[1]; _primed = true; }
        if (Math.Abs(_step - 1.0) < 1e-9)
        {
            _lastL = st[(frames - 1) * 2]; _lastR = st[(frames - 1) * 2 + 1];
            return st;
        }
        // virtual buffer: index 0 = last frame of the previous packet, 1..frames = this packet
        int maxOut = (int)((frames + 1) / _step) + 2;
        if (_out.Length < maxOut * 2) _out = new float[maxOut * 2];
        int o = 0;
        while (_pos < frames)
        {
            int i0 = (int)_pos;
            double frac = _pos - i0;
            float l0 = i0 == 0 ? _lastL : st[(i0 - 1) * 2], r0 = i0 == 0 ? _lastR : st[(i0 - 1) * 2 + 1];
            float l1 = st[i0 * 2], r1 = st[i0 * 2 + 1];
            _out[o++] = (float)(l0 + (l1 - l0) * frac);
            _out[o++] = (float)(r0 + (r1 - r0) * frac);
            _pos += _step;
        }
        _pos -= frames;
        _lastL = st[(frames - 1) * 2]; _lastR = st[(frames - 1) * 2 + 1];
        return _out.AsSpan(0, o);
    }
}
