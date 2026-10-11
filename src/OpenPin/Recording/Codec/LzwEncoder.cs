using System.IO;

namespace OpenPin.Recording.Codec;

/// <summary>
/// GIF-flavoured LZW compressor (variable code size up to 12 bits, clear code when the table is full), writing the
/// "image data" part of a GIF frame: the minimum code size byte followed by ≤255-byte sub-blocks and a 0 terminator.
/// Hashing scheme after the classic GIFENCOD / compress (open addressing, double hashing, table size 5003).
/// </summary>
internal sealed class LzwEncoder
{
    private const int MaxBits = 12;
    private const int MaxMaxCode = 1 << MaxBits;
    private const int HashSize = 5003;

    private readonly int[] _htab = new int[HashSize];
    private readonly int[] _codetab = new int[HashSize];
    private readonly byte[] _block = new byte[256];

    private Stream _out = null!;
    private int _blockLen;
    private int _curAccum, _curBits;
    private int _nBits, _maxCode, _freeEnt, _clearCode, _eofCode, _initBits;
    private bool _clearFlag;

    /// <summary>Encode <paramref name="indices"/> (each &lt; 2^<paramref name="colorBits"/>) to <paramref name="output"/>.</summary>
    public void Encode(ReadOnlySpan<byte> indices, int colorBits, Stream output)
    {
        int minCodeSize = Math.Max(2, colorBits);
        output.WriteByte((byte)minCodeSize);
        _out = output;
        _blockLen = 0;
        _curAccum = 0;
        _curBits = 0;
        _initBits = minCodeSize + 1;
        _nBits = _initBits;
        _maxCode = MaxCode(_nBits);
        _clearCode = 1 << minCodeSize;
        _eofCode = _clearCode + 1;
        _freeEnt = _clearCode + 2;
        _clearFlag = false;
        Array.Fill(_htab, -1);

        Output(_clearCode);
        if (indices.Length > 0)
        {
            int ent = indices[0];
            const int hshift = 4; // log2(HashSize) ≈ 12.3 → 8 - hshift = 4 bits of k go into the hash
            for (int p = 1; p < indices.Length; p++)
            {
                int c = indices[p];
                int fcode = (c << MaxBits) + ent;
                int i = (c << hshift) ^ ent;
                if (_htab[i] == fcode) { ent = _codetab[i]; continue; }
                if (_htab[i] >= 0)
                {
                    int disp = i == 0 ? 1 : HashSize - i;
                    bool found = false;
                    do
                    {
                        i -= disp;
                        if (i < 0) i += HashSize;
                        if (_htab[i] == fcode) { ent = _codetab[i]; found = true; break; }
                    } while (_htab[i] >= 0);
                    if (found) continue;
                }
                Output(ent);
                ent = c;
                if (_freeEnt < MaxMaxCode)
                {
                    _codetab[i] = _freeEnt++;
                    _htab[i] = fcode;
                }
                else ClearTable();
            }
            Output(ent);
        }
        Output(_eofCode);
        output.WriteByte(0); // block terminator
    }

    private static int MaxCode(int bits) => (1 << bits) - 1;

    private void ClearTable()
    {
        Array.Fill(_htab, -1);
        _freeEnt = _clearCode + 2;
        _clearFlag = true;
        Output(_clearCode);
    }

    private void Output(int code)
    {
        _curAccum |= code << _curBits;
        _curBits += _nBits;
        while (_curBits >= 8)
        {
            AddByte((byte)_curAccum);
            _curAccum >>= 8;
            _curBits -= 8;
        }

        if (_freeEnt > _maxCode || _clearFlag)
        {
            if (_clearFlag)
            {
                _nBits = _initBits;
                _maxCode = MaxCode(_nBits);
                _clearFlag = false;
            }
            else
            {
                _nBits++;
                _maxCode = _nBits == MaxBits ? MaxMaxCode : MaxCode(_nBits);
            }
        }

        if (code == _eofCode)
        {
            while (_curBits > 0)
            {
                AddByte((byte)_curAccum);
                _curAccum >>= 8;
                _curBits -= 8;
            }
            FlushBlock();
        }
    }

    private void AddByte(byte b)
    {
        _block[_blockLen++] = b;
        if (_blockLen >= 255) FlushBlock();
    }

    private void FlushBlock()
    {
        if (_blockLen == 0) return;
        _out.WriteByte((byte)_blockLen);
        _out.Write(_block, 0, _blockLen);
        _blockLen = 0;
    }
}
