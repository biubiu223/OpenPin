namespace OpenPin.LongCapture.Stitching;

/// <summary>
/// Append-only storage of stitched lines (BGRA, fixed line length) in medium-sized blocks, so a very long result
/// never needs one giant reallocation while capturing (it is composed once at the end). Alpha is forced to 255.
/// Not thread-safe.
/// </summary>
internal sealed class StripStore
{
    private const int TargetBlockBytes = 4 << 20;
    private readonly List<byte[]> _blocks = new();
    private readonly int _lineBytes;
    private readonly int _linesPerBlock;

    public StripStore(int linePixels)
    {
        if (linePixels <= 0) throw new ArgumentOutOfRangeException(nameof(linePixels));
        _lineBytes = linePixels * 4;
        _linesPerBlock = Math.Max(16, TargetBlockBytes / _lineBytes);
    }

    /// <summary>Number of stored lines.</summary>
    public int Count { get; private set; }

    public int LineBytes => _lineBytes;

    /// <summary>Approximate memory held, in bytes.</summary>
    public long AllocatedBytes => (long)_blocks.Count * _linesPerBlock * _lineBytes;

    /// <summary>Appends <paramref name="lines"/> consecutive lines starting at <paramref name="source"/>[0].</summary>
    public void Append(ReadOnlySpan<byte> source, int lines)
    {
        if (source.Length < lines * _lineBytes) throw new ArgumentException("Source too small", nameof(source));
        for (int i = 0; i < lines; i++)
        {
            int block = Count / _linesPerBlock, row = Count % _linesPerBlock;
            if (block == _blocks.Count) _blocks.Add(new byte[_linesPerBlock * _lineBytes]);
            var dst = _blocks[block].AsSpan(row * _lineBytes, _lineBytes);
            source.Slice(i * _lineBytes, _lineBytes).CopyTo(dst);
            for (int a = 3; a < dst.Length; a += 4) dst[a] = 255;
            Count++;
        }
    }

    /// <summary>One stored line (BGRA).</summary>
    public ReadOnlySpan<byte> Line(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _blocks[index / _linesPerBlock].AsSpan(index % _linesPerBlock * _lineBytes, _lineBytes);
    }

    /// <summary>Drops lines from the end so that <paramref name="count"/> lines remain (freed blocks are released).</summary>
    public void Truncate(int count)
    {
        if (count < 0 || count > Count) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
        int blocks = (count + _linesPerBlock - 1) / _linesPerBlock;
        if (_blocks.Count > blocks) _blocks.RemoveRange(blocks, _blocks.Count - blocks);
    }

    public void Clear()
    {
        _blocks.Clear();
        Count = 0;
    }
}
