using System.Runtime.CompilerServices;

namespace SharpImage.Formats.WebP;

/// <summary>
/// VP8LBitWriter (src/utils/bit_writer_utils.c): LSB-first bit packing. libwebp flushes 32-bit words and reports
/// NumBytes = written bytes + ceil(pending bits / 8), which equals ceil(total bits / 8) — the size every "keep the
/// smallest" comparison in the encoder uses — so a plain bit accumulator reproduces both its output and its sizes.
/// Clone / Reset / Swap mirror the C helpers used to try several configurations from the same starting point.
/// </summary>
internal sealed class Vp8lBitWriter
{
    private byte[] _buf;
    private int _pos;        // whole bytes written
    private ulong _bits;     // pending bits (LSB first)
    private int _used;       // number of pending bits (< 8 after each flush)

    public Vp8lBitWriter(int expectedSize = 256) => _buf = new byte[Math.Max(expectedSize, 256)];

    /// <summary>VP8LBitWriterNumBytes.</summary>
    public long NumBytes => _pos + ((_used + 7) >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutBits(uint bits, int nBits)
    {
        if (nBits <= 0) return;
        _bits |= (ulong)bits << _used;
        _used += nBits;
        while (_used >= 8)
        {
            if (_pos == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_pos++] = (byte)_bits;
            _bits >>= 8;
            _used -= 8;
        }
    }

    /// <summary>VP8LBitWriterClone: dst becomes a copy of this writer's state.</summary>
    public void CloneTo(Vp8lBitWriter dst)
    {
        if (dst._buf.Length < _pos + 8) dst._buf = new byte[Math.Max(_pos + 8, _buf.Length)];
        Buffer.BlockCopy(_buf, 0, dst._buf, 0, _pos);
        dst._pos = _pos;
        dst._bits = _bits;
        dst._used = _used;
    }

    /// <summary>State snapshot for VP8LBitWriterReset (bits, used, position).</summary>
    public (int Pos, ulong Bits, int Used) Mark() => (_pos, _bits, _used);

    /// <summary>VP8LBitWriterReset(bw_init, this): rewind to a snapshot taken on the same buffer contents.</summary>
    public void ResetTo((int Pos, ulong Bits, int Used) mark)
    {
        _pos = mark.Pos;
        _bits = mark.Bits;
        _used = mark.Used;
    }

    /// <summary>VP8LBitWriterSwap.</summary>
    public static void Swap(Vp8lBitWriter a, Vp8lBitWriter b)
    {
        (a._buf, b._buf) = (b._buf, a._buf);
        (a._pos, b._pos) = (b._pos, a._pos);
        (a._bits, b._bits) = (b._bits, a._bits);
        (a._used, b._used) = (b._used, a._used);
    }

    /// <summary>VP8LBitWriterFinish: flushes the pending bits (zero-padded) and returns the bytes.</summary>
    public byte[] Finish()
    {
        while (_used > 0)
        {
            if (_pos == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_pos++] = (byte)_bits;
            _bits >>= 8;
            _used = Math.Max(0, _used - 8);
        }
        _used = 0;
        return _buf.AsSpan(0, _pos).ToArray();
    }
}
