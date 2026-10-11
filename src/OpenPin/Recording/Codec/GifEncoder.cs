using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace OpenPin.Recording.Codec;

/// <summary>
/// Streaming animated-GIF writer (GIF89a, infinite loop). Each frame is diffed against the previously emitted frame:
/// only the changed bounding rectangle is written, unchanged pixels inside it become transparent, and each frame gets its
/// own local palette (<see cref="ColorQuantizer"/>, exact when ≤ 255 colors). Frames identical to the previous one are merged
/// (the previous frame's delay grows). Delays are computed from absolute timestamps (<see cref="Timeline.GifDelays"/>), so
/// centisecond rounding never accumulates. Frames are written one step late because a delay is known only once the next
/// frame (or <see cref="Finish"/>) arrives.
/// </summary>
internal sealed class GifEncoder : IDisposable
{
    private readonly Stream _out;
    private readonly int _w, _h;
    private readonly GifDitherMode _dither;
    private readonly LzwEncoder _lzw = new();
    private readonly uint[] _shown; // source colors of what the decoder currently displays (0x00RRGGBB)
    private readonly MemoryStream _pendingData = new();
    private uint[] _rectPixels = Array.Empty<uint>();
    private bool[] _rectInclude = Array.Empty<bool>();
    private byte[] _indices = Array.Empty<byte>();
    private bool _hasPending, _finished, _started;
    private double _pendingStartMs;

    public GifEncoder(Stream output, int width, int height, GifDitherMode dither = GifDitherMode.FloydSteinberg)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        _out = output;
        _w = width;
        _h = height;
        _dither = dither;
        _shown = new uint[width * height];
    }

    /// <summary>Number of image blocks written or pending (merged duplicates excluded).</summary>
    public int FramesWritten { get; private set; }

    /// <summary>Add a frame (BGRA, stride = width*4) that starts at <paramref name="startMs"/> (relative to the animation start, ascending).</summary>
    public void AddFrame(ReadOnlySpan<byte> bgra, double startMs)
    {
        if (_finished) throw new InvalidOperationException("Already finished");
        if (bgra.Length < _w * _h * 4) throw new ArgumentException("Frame buffer too small", nameof(bgra));
        var src = MemoryMarshal.Cast<byte, uint>(bgra[..(_w * _h * 4)]);

        Int32Rect rect;
        bool first = !_started;
        if (first)
        {
            WriteHeader();
            _started = true;
            rect = new Int32Rect(0, 0, _w, _h);
        }
        else
        {
            rect = ChangedRect(src);
            if (rect.IsEmpty) return; // identical: the pending frame simply lasts longer
        }

        // gather the rect's pixels and which of them changed
        int n = rect.Width * rect.Height;
        if (_rectPixels.Length < n)
        {
            _rectPixels = new uint[n];
            _rectInclude = new bool[n];
            _indices = new byte[n];
        }
        var px = _rectPixels.AsSpan(0, n);
        var inc = _rectInclude.AsSpan(0, n);
        int changed = 0;
        for (int y = 0; y < rect.Height; y++)
        {
            int s = (rect.Y + y) * _w + rect.X;
            for (int x = 0; x < rect.Width; x++)
            {
                uint c = src[s + x] & PixelOps.RgbMask;
                px[y * rect.Width + x] = c;
                bool ch = first || c != _shown[s + x];
                inc[y * rect.Width + x] = ch;
                if (ch) changed++;
                _shown[s + x] = c;
            }
        }
        bool useTransparency = changed < n;

        var q = ColorQuantizer.Build(px, useTransparency ? inc : default, useTransparency ? 255 : 256);
        int colors = q.Palette.Length + (useTransparency ? 1 : 0);
        int bits = 1;
        while ((1 << bits) < colors) bits++;
        byte transparentIndex = (byte)q.Palette.Length;
        q.MapPixels(px, useTransparency ? inc : default, rect.Width, rect.Height, _indices.AsSpan(0, n), _dither, transparentIndex);

        // previous frame now knows its delay
        FlushPending(startMs);

        // stage this frame: GCE is written at flush time (needs the delay); image descriptor + palette + data now
        _pendingData.SetLength(0);
        var d = _pendingData;
        d.WriteByte(0x2C);
        WriteU16(d, rect.X); WriteU16(d, rect.Y); WriteU16(d, rect.Width); WriteU16(d, rect.Height);
        d.WriteByte((byte)(0x80 | (bits - 1))); // local color table, 2^bits entries
        int tableSize = 1 << bits;
        for (int i = 0; i < tableSize; i++)
        {
            uint c = i < q.Palette.Length ? q.Palette[i] : 0;
            d.WriteByte((byte)(c >> 16)); d.WriteByte((byte)(c >> 8)); d.WriteByte((byte)c);
        }
        _lzw.Encode(_indices.AsSpan(0, n), bits, d);

        _pendingTransparent = useTransparency ? transparentIndex : -1;
        _pendingStartMs = startMs;
        _hasPending = true;
        FramesWritten++;
    }

    private int _pendingTransparent = -1;

    /// <summary>Write the last frame (shown until <paramref name="endMs"/>) and the trailer.</summary>
    public void Finish(double endMs)
    {
        if (_finished) return;
        if (!_started) throw new InvalidOperationException("No frames");
        FlushPending(endMs);
        _out.WriteByte(0x3B);
        _out.Flush();
        _finished = true;
    }

    private void FlushPending(double nextStartMs)
    {
        if (!_hasPending) return;
        int delay = Timeline.GifDelays(new[] { _pendingStartMs }, nextStartMs)[0];
        // Graphic Control Extension: disposal 1 (leave in place), optional transparency
        _out.WriteByte(0x21); _out.WriteByte(0xF9); _out.WriteByte(4);
        _out.WriteByte((byte)((1 << 2) | (_pendingTransparent >= 0 ? 1 : 0)));
        WriteU16(_out, delay);
        _out.WriteByte((byte)Math.Max(0, _pendingTransparent));
        _out.WriteByte(0);
        _pendingData.Position = 0;
        _pendingData.CopyTo(_out);
        _hasPending = false;
    }

    private Int32Rect ChangedRect(ReadOnlySpan<uint> src)
    {
        int minX = int.MaxValue, maxX = -1, minY = -1, maxY = -1;
        for (int y = 0; y < _h; y++)
        {
            int row = y * _w;
            int first = -1, last = -1;
            for (int x = 0; x < _w; x++)
                if (((src[row + x] ^ _shown[row + x]) & PixelOps.RgbMask) != 0) { first = x; break; }
            if (first < 0) continue;
            for (int x = _w - 1; x >= first; x--)
                if (((src[row + x] ^ _shown[row + x]) & PixelOps.RgbMask) != 0) { last = x; break; }
            if (minY < 0) minY = y;
            maxY = y;
            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
        }
        return minY < 0 ? Int32Rect.Empty : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private void WriteHeader()
    {
        _out.Write("GIF89a"u8);
        WriteU16(_out, _w);
        WriteU16(_out, _h);
        _out.WriteByte(0x70); // no global color table, 8-bit color resolution
        _out.WriteByte(0);    // background color index
        _out.WriteByte(0);    // pixel aspect ratio
        // NETSCAPE2.0 application extension: loop forever
        _out.WriteByte(0x21); _out.WriteByte(0xFF); _out.WriteByte(11);
        _out.Write("NETSCAPE2.0"u8);
        _out.WriteByte(3); _out.WriteByte(1);
        WriteU16(_out, 0);
        _out.WriteByte(0);
    }

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)(v & 255));
        s.WriteByte((byte)((v >> 8) & 255));
    }

    public void Dispose() => _pendingData.Dispose();
}
