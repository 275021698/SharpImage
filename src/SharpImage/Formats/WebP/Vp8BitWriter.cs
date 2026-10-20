using System.Runtime.CompilerServices;

namespace SharpImage.Formats.WebP;

/// <summary>
/// VP8BitWriter (libwebp 1.6.0 src/utils/bit_writer_utils.c): the VP8 boolean (arithmetic) encoder, with its delayed
/// 0xff run handling for carries.
/// </summary>
internal sealed class Vp8BitWriter
{
    private int _range;     // range-1
    private int _value;
    private int _run;       // number of outstanding bits
    private int _nbBits;    // number of pending bits
    private byte[] _buf = [];
    private int _pos;

    public Vp8BitWriter(int expectedSize) => Init(expectedSize);

    /// <summary>VP8BitWriterInit.</summary>
    public void Init(int expectedSize)
    {
        _range = 255 - 1;
        _value = 0;
        _run = 0;
        _nbBits = -8;
        _pos = 0;
        _buf = [];
        if (expectedSize > 0) Resize(expectedSize);
    }

    /// <summary>VP8BitWriterPos: the number of bits written so far.</summary>
    public ulong Pos => (ulong)(_pos + _run) * 8 + (ulong)(8 + _nbBits);

    /// <summary>VP8BitWriterSize.</summary>
    public int Size => _pos;

    /// <summary>VP8BitWriterBuf (only the first Size bytes are meaningful).</summary>
    public byte[] Buffer => _buf;

    private void Resize(int extraSize)
    {
        int neededSize = _pos + extraSize;
        if (neededSize <= _buf.Length) return;
        int newSize = 2 * _buf.Length;
        if (newSize < neededSize) newSize = neededSize;
        if (newSize < 1024) newSize = 1024;
        Array.Resize(ref _buf, newSize);
    }

    private void Flush()
    {
        int s = 8 + _nbBits;
        int bits = _value >> s;
        _value -= bits << s;
        _nbBits -= 8;
        if ((bits & 0xff) != 0xff)
        {
            int pos = _pos;
            Resize(_run + 1);
            if ((bits & 0x100) != 0)
            {
                // overflow -> propagate carry over pending 0xff's
                if (pos > 0) _buf[pos - 1]++;
            }
            if (_run > 0)
            {
                byte value = (bits & 0x100) != 0 ? (byte)0x00 : (byte)0xff;
                for (; _run > 0; --_run) _buf[pos++] = value;
            }
            _buf[pos++] = (byte)bits;
            _pos = pos;
        }
        else
        {
            _run++;   // delay writing of bytes 0xff, pending eventual carry.
        }
    }

    /// <summary>VP8PutBit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PutBit(int bit, int prob)
    {
        int split = (_range * prob) >> 8;
        if (bit != 0)
        {
            _value += split + 1;
            _range -= split + 1;
        }
        else
        {
            _range = split;
        }
        if (_range < 127)
        {
            // emit 'shift' bits out and renormalize
            int shift = Vp8EncTables.Norm[_range];
            _range = Vp8EncTables.NewRange[_range];
            _value <<= shift;
            _nbBits += shift;
            if (_nbBits > 0) Flush();
        }
        return bit;
    }

    public bool PutBit(bool bit, int prob) => PutBit(bit ? 1 : 0, prob) != 0;

    /// <summary>VP8PutBitUniform.</summary>
    public int PutBitUniform(int bit)
    {
        int split = _range >> 1;
        if (bit != 0)
        {
            _value += split + 1;
            _range -= split + 1;
        }
        else
        {
            _range = split;
        }
        if (_range < 127)
        {
            _range = Vp8EncTables.NewRange[_range];
            _value <<= 1;
            _nbBits += 1;
            if (_nbBits > 0) Flush();
        }
        return bit;
    }

    public bool PutBitUniform(bool bit) => PutBitUniform(bit ? 1 : 0) != 0;

    /// <summary>VP8PutBits.</summary>
    public void PutBits(uint value, int nbBits)
    {
        for (uint mask = 1u << (nbBits - 1); mask != 0; mask >>= 1)
            PutBitUniform((value & mask) != 0 ? 1 : 0);
    }

    /// <summary>VP8PutSignedBits.</summary>
    public void PutSignedBits(int value, int nbBits)
    {
        if (PutBitUniform(value != 0 ? 1 : 0) == 0) return;
        if (value < 0)
            PutBits((uint)(((-value) << 1) | 1), nbBits + 1);
        else
            PutBits((uint)(value << 1), nbBits + 1);
    }

    /// <summary>VP8BitWriterFinish.</summary>
    public void Finish()
    {
        PutBits(0, 9 - _nbBits);
        _nbBits = 0;   // pad with zeroes
        Flush();
    }

    /// <summary>VP8BitWriterAppend (only valid right after Init).</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        Resize(data.Length);
        data.CopyTo(_buf.AsSpan(_pos));
        _pos += data.Length;
    }
}
