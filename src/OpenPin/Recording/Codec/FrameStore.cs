using System.IO;
using System.IO.Compression;
using System.Windows;

namespace OpenPin.Recording.Codec;

/// <summary>A cursor image: straight-alpha BGRA plus an optional "invert the screen" mask (monochrome I-beam cursors).</summary>
internal sealed class CursorShape
{
    public CursorShape(int width, int height, byte[] bgra, byte[]? invertMask, int hotspotX, int hotspotY)
    {
        Width = width; Height = height; Bgra = bgra; InvertMask = invertMask; HotspotX = hotspotX; HotspotY = hotspotY;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public byte[]? InvertMask { get; }
    public int HotspotX { get; }
    public int HotspotY { get; }
}

/// <summary>Cursor state of one frame. <see cref="Id"/> indexes <see cref="FrameStore.Cursors"/>; -1 = no cursor visible.
/// X/Y = top-left of the cursor image relative to the recorded region (pixels).</summary>
internal readonly record struct CursorSample(int Id, int X, int Y)
{
    public static readonly CursorSample None = new(-1, 0, 0);
}


/// <summary>Index entry of one stored frame. Rect = changed area (Width 0 = identical to the previous frame).</summary>
internal readonly record struct FrameEntry(double TimeMs, long Offset, int Length, Int32Rect Rect, bool IsKey, CursorSample Cursor);

/// <summary>
/// A finished frame stream on disk: frames (delta encoded, Deflate compressed) in a temp file, index/cursors in memory.
/// Open any number of <see cref="FrameReader"/>s to decode it (each has its own file handle).
/// </summary>
internal sealed class FrameStore
{
    internal FrameStore(string path, int width, int height, IReadOnlyList<FrameEntry> frames, IReadOnlyList<CursorShape> cursors,
        double durationMs, int fps)
    {
        Path = path; Width = width; Height = height; Frames = frames; Cursors = cursors; DurationMs = durationMs; Fps = fps;
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<FrameEntry> Frames { get; }
    public IReadOnlyList<CursorShape> Cursors { get; }

    /// <summary>Recording length (excludes pauses). The last frame is shown until this time.</summary>
    public double DurationMs { get; }
    /// <summary>Nominal capture frame rate.</summary>
    public int Fps { get; }

    public double[] FrameTimes() => Frames.Select(f => f.TimeMs).ToArray();

    public FrameReader OpenReader() => new(this);

    /// <summary>Delete the backing temp file (best effort).</summary>
    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) { Core.Log.Warn($"Could not delete recording temp file {Path}: {ex.Message}"); }
    }
}

/// <summary>
/// Appends frames to a temp file. Each frame stores only the bounding rectangle of the pixels that changed since the previous
/// frame, as BGR bytes XOR-ed with the previous frame (unchanged pixels become zeros) and Deflate compressed. A keyframe (full
/// frame, raw BGR) is written every <see cref="KeyframeIntervalMs"/> so readers can seek. Not thread-safe for <see cref="Append"/>
/// (one writer thread); <see cref="RegisterCursor"/> may be called from another thread.
/// </summary>
internal sealed class FrameStoreWriter : IDisposable
{
    public const int Magic = 0x4345524F; // "OREC"

    private readonly FileStream _file;
    private readonly List<FrameEntry> _frames = new();
    private readonly List<CursorShape> _cursors = new();

    private readonly object _gate = new();
    private readonly MemoryStream _compressed = new();
    private byte[] _packed = Array.Empty<byte>();
    private byte[]? _prev;
    private double _lastKeyMs = double.NegativeInfinity;
    private long _bytesSinceKey, _lastKeySize;
    private bool _completed;

    public FrameStoreWriter(string path, int width, int height, int fps)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Path = path; Width = width; Height = height; Fps = fps;
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1 << 16);
        Span<byte> header = stackalloc byte[16];
        BitConverter.TryWriteBytes(header, Magic);
        BitConverter.TryWriteBytes(header[4..], 1);
        BitConverter.TryWriteBytes(header[8..], width);
        BitConverter.TryWriteBytes(header[12..], height);
        _file.Write(header);
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int KeyframeIntervalMs { get; init; } = 3000;
    public CompressionLevel Level { get; init; } = CompressionLevel.Fastest;

    public int FrameCount => _frames.Count;
    public long BytesWritten => _file.Length;

    public int RegisterCursor(CursorShape shape)
    {
        lock (_gate)
        {
            _cursors.Add(shape);
            return _cursors.Count - 1;
        }
    }


    /// <summary>
    /// Append a frame (BGRA, stride = Width*4). The writer keeps <paramref name="frame"/> as its reference for the next delta
    /// and returns the buffer it no longer needs (the previous frame, or null on the first call) so the caller can reuse it.
    /// </summary>
    public byte[]? Append(byte[] frame, double timeMs, CursorSample cursor)
    {
        if (_completed) throw new InvalidOperationException("Writer completed");
        int w = Width, h = Height;
        if (frame.Length < w * h * 4) throw new ArgumentException("Frame buffer too small", nameof(frame));

        Int32Rect rect;
        bool key;
        if (_prev == null)
        {
            key = true;
            rect = new Int32Rect(0, 0, w, h);
        }
        else
        {
            rect = PixelOps.FindChangedRect(_prev, frame, w, h);
            key = !rect.IsEmpty && (timeMs - _lastKeyMs >= KeyframeIntervalMs || _bytesSinceKey > Math.Max(_lastKeySize, 256 * 1024));
            if (key) rect = new Int32Rect(0, 0, w, h);
        }

        long offset = _file.Position;
        int length = 0;
        if (!rect.IsEmpty)
        {
            Pack(frame, key ? null : _prev, rect);
            length = WriteCompressed(rect.Width * rect.Height * 3);
        }
        if (key)
        {
            _lastKeyMs = timeMs;
            _lastKeySize = length;
            _bytesSinceKey = 0;
        }
        else _bytesSinceKey += length;

        _frames.Add(new FrameEntry(timeMs, offset, length, rect.IsEmpty ? new Int32Rect(0, 0, 0, 0) : rect, key, cursor));
        var released = _prev;
        _prev = frame;
        return released;
    }

    /// <summary>Flush and close the file; returns the finished store.</summary>
    public FrameStore Complete(double durationMs)
    {
        if (!_completed)
        {
            _completed = true;
            _file.Flush();
            _file.Dispose();
        }
        double last = _frames.Count > 0 ? _frames[^1].TimeMs : 0;
        lock (_gate)
            return new FrameStore(Path, Width, Height, _frames.ToArray(), _cursors.ToArray(),
                Math.Max(durationMs, last + 1), Fps);
    }

    private void Pack(byte[] cur, byte[]? prev, Int32Rect r)
    {
        int need = r.Width * r.Height * 3;
        if (_packed.Length < need) _packed = new byte[need];
        var dst = _packed;
        int k = 0, w = Width;
        if (prev == null)
        {
            for (int y = r.Y; y < r.Y + r.Height; y++)
            {
                int i = (y * w + r.X) * 4, end = i + r.Width * 4;
                for (; i < end; i += 4)
                {
                    dst[k] = cur[i]; dst[k + 1] = cur[i + 1]; dst[k + 2] = cur[i + 2];
                    k += 3;
                }
            }
        }
        else
        {
            for (int y = r.Y; y < r.Y + r.Height; y++)
            {
                int i = (y * w + r.X) * 4, end = i + r.Width * 4;
                for (; i < end; i += 4)
                {
                    dst[k] = (byte)(cur[i] ^ prev[i]);
                    dst[k + 1] = (byte)(cur[i + 1] ^ prev[i + 1]);
                    dst[k + 2] = (byte)(cur[i + 2] ^ prev[i + 2]);
                    k += 3;
                }
            }
        }
    }

    private int WriteCompressed(int count)
    {
        _compressed.SetLength(0);
        using (var z = new DeflateStream(_compressed, Level, leaveOpen: true))
            z.Write(_packed, 0, count);
        int len = (int)_compressed.Length;
        _file.Write(_compressed.GetBuffer(), 0, len);
        return len;
    }

    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        try { _file.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>
/// Decodes frames of a <see cref="FrameStore"/> into <see cref="Canvas"/> (BGRA, alpha 255). Sequential access is cheap;
/// random access decodes forward from the nearest keyframe. Not thread-safe; use one reader per thread.
/// </summary>
internal sealed class FrameReader : IDisposable
{
    private readonly FrameStore _store;
    private readonly FileStream _file;
    private byte[] _compressed = Array.Empty<byte>();
    private byte[] _packed = Array.Empty<byte>();

    public FrameReader(FrameStore store)
    {
        _store = store;
        _file = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        Canvas = new byte[store.Width * store.Height * 4];
    }

    /// <summary>The decoded frame <see cref="CurrentIndex"/>.</summary>
    public byte[] Canvas { get; }
    public int CurrentIndex { get; private set; } = -1;
    public int Width => _store.Width;
    public int Height => _store.Height;

    /// <summary>Decode so that <see cref="Canvas"/> holds frame <paramref name="index"/>.</summary>
    public void Seek(int index)
    {
        var frames = _store.Frames;
        if ((uint)index >= (uint)frames.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == CurrentIndex) return;

        int start;
        if (index > CurrentIndex && CurrentIndex >= 0)
        {
            start = CurrentIndex + 1;
            // jump to a later keyframe if there is one in between
            for (int i = index; i > CurrentIndex; i--)
                if (frames[i].IsKey) { start = i; break; }
        }
        else
        {
            start = index;
            while (start > 0 && !frames[start].IsKey) start--;
        }
        for (int i = start; i <= index; i++) Apply(frames[i]);
        CurrentIndex = index;
    }

    private void Apply(FrameEntry f)
    {
        if (f.Length == 0 || f.Rect.Width == 0) return;
        if (_compressed.Length < f.Length) _compressed = new byte[f.Length];
        _file.Position = f.Offset;
        _file.ReadExactly(_compressed, 0, f.Length);
        int count = f.Rect.Width * f.Rect.Height * 3;
        if (_packed.Length < count) _packed = new byte[count];
        using (var ms = new MemoryStream(_compressed, 0, f.Length, writable: false))
        using (var z = new DeflateStream(ms, CompressionMode.Decompress))
            z.ReadExactly(_packed, 0, count);

        var c = Canvas;
        var p = _packed;
        int w = _store.Width, k = 0;
        var r = f.Rect;
        for (int y = r.Y; y < r.Y + r.Height; y++)
        {
            int i = (y * w + r.X) * 4, end = i + r.Width * 4;
            if (f.IsKey)
            {
                for (; i < end; i += 4, k += 3)
                {
                    c[i] = p[k]; c[i + 1] = p[k + 1]; c[i + 2] = p[k + 2]; c[i + 3] = 255;
                }
            }
            else
            {
                for (; i < end; i += 4, k += 3)
                {
                    c[i] ^= p[k]; c[i + 1] ^= p[k + 1]; c[i + 2] ^= p[k + 2];
                }
            }
        }
    }

    public void Dispose() => _file.Dispose();
}
