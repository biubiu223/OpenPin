using System.IO;
using System.Windows.Media;
using OpenPin.Core;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.UI;

/// <summary>
/// Plays the recorded audio tracks in the playback window, kept in sync with the video playhead. Each track is converted to
/// a temporary 16-bit WAV in the background and played with a WPF <see cref="MediaPlayer"/> (so each one can be muted with
/// its toggle). UI thread only.
/// </summary>
internal sealed class AudioPreview : IDisposable
{
    private readonly List<(MediaPlayer Player, string Wav, Func<bool> Enabled)> _players = new();
    private readonly Task _ready;
    private bool _disposed;

    public AudioPreview(RecordingData data, Func<bool> micEnabled, Func<bool> systemEnabled)
    {
        var tracks = new List<(AudioTrack, Func<bool>)>();
        if (data.Microphone != null) tracks.Add((data.Microphone, micEnabled));
        if (data.SystemAudio != null) tracks.Add((data.SystemAudio, systemEnabled));
        _ready = PrepareAsync(tracks);
    }

    private async Task PrepareAsync(List<(AudioTrack Track, Func<bool> Enabled)> tracks)
    {
        foreach (var (track, enabled) in tracks)
        {
            var wav = Path.ChangeExtension(track.Path, ".preview.wav");
            try
            {
                await Task.Run(() => WriteWav(track, wav));
                if (_disposed) { TryDelete(wav); return; }
                var p = new MediaPlayer();
                p.Open(new Uri(wav));
                p.Volume = enabled() ? 1 : 0;
                _players.Add((p, wav, enabled));
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio preview unavailable: {ex.Message}");
                TryDelete(wav);
            }
        }
    }

    private static void WriteWav(AudioTrack track, string path)
    {
        using var reader = track.OpenReader();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        using var bw = new BinaryWriter(fs);
        long dataBytes = track.Frames * AudioFormat.Channels * 2;
        bw.Write("RIFF"u8); bw.Write((uint)(36 + dataBytes)); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16u); bw.Write((ushort)1); bw.Write((ushort)AudioFormat.Channels);
        bw.Write((uint)AudioFormat.SampleRate); bw.Write((uint)(AudioFormat.SampleRate * AudioFormat.Channels * 2));
        bw.Write((ushort)(AudioFormat.Channels * 2)); bw.Write((ushort)16);
        bw.Write("data"u8); bw.Write((uint)dataBytes);
        var buf = new float[48000 * AudioFormat.Channels];
        var pcm = new byte[buf.Length * 2];
        for (long pos = 0; pos < track.Frames; pos += 48000)
        {
            int n = (int)Math.Min(48000, track.Frames - pos) * AudioFormat.Channels;
            reader.Read(pos, buf.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Clamp((int)MathF.Round(buf[i] * 32767f), short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)v;
                pcm[i * 2 + 1] = (byte)(v >> 8);
            }
            bw.Write(pcm, 0, n * 2);
        }
    }

    public void Play(double positionMs, double speed)
    {
        if (!_ready.IsCompleted) return;
        foreach (var (p, _, enabled) in _players)
        {
            p.Volume = enabled() ? 1 : 0;
            p.SpeedRatio = speed;
            p.Position = TimeSpan.FromMilliseconds(positionMs);
            p.Play();
        }
    }

    public void Pause()
    {
        foreach (var (p, _, _) in _players) p.Pause();
    }

    /// <summary>Re-sync if a player drifted more than 120 ms from the video clock, and apply mute toggles.</summary>
    public void Sync(double positionMs)
    {
        foreach (var (p, _, enabled) in _players)
        {
            p.Volume = enabled() ? 1 : 0;
            if (Math.Abs(p.Position.TotalMilliseconds - positionMs) > 120) p.Position = TimeSpan.FromMilliseconds(positionMs);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var (p, wav, _) in _players)
        {
            p.Stop();
            p.Close();
            TryDelete(wav);
        }
        _players.Clear();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* in use: swept with the session's temp files */ }
    }
}
