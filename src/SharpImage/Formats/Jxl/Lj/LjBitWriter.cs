// libjxl BitWriter (enc_bit_writer.cc): bits are packed least-significant-bit first; Write takes up to 56 bits
// at a time. Part of the exact libjxl 0.12 encoder port used for JPEG recompression (see LjJpegTranscoder).
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjBitWriter
{
    private byte[] buf = new byte[256];
    private long bits;       // bits written
    private ulong acc;       // pending bits (< 8 valid when flushed)
    private int accBits;

    public long BitsWritten => bits;

    public void Write(int nbits, ulong v)
    {
        if (nbits == 0)
        {
            return;
        }

        if (nbits > 56 || nbits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nbits));
        }

        acc |= (v & ((1UL << nbits) - 1)) << accBits;
        accBits += nbits;
        bits += nbits;
        while (accBits >= 8)
        {
            Put((byte)acc);
            acc >>= 8;
            accBits -= 8;
        }
    }

    private int pos; // bytes committed

    private void Put(byte b)
    {
        if (pos == buf.Length)
        {
            Array.Resize(ref buf, buf.Length * 2);
        }

        buf[pos++] = b;
    }

    public void ZeroPadToByte()
    {
        int r = (int)(bits & 7);
        if (r != 0)
        {
            Write(8 - r, 0);
        }
    }

    public void AppendByteAligned(ReadOnlySpan<byte> data)
    {
        if ((bits & 7) != 0)
        {
            throw new InvalidOperationException("AppendByteAligned on an unaligned writer.");
        }

        while (pos + data.Length > buf.Length)
        {
            Array.Resize(ref buf, buf.Length * 2);
        }

        data.CopyTo(buf.AsSpan(pos));
        pos += data.Length;
        bits += 8L * data.Length;
    }

    public void AppendByteAligned(LjBitWriter other) => AppendByteAligned(other.GetSpan());

    /// <summary>Appends another writer's bits at the current (possibly unaligned) position.</summary>
    public void AppendUnaligned(LjBitWriter other)
    {
        ReadOnlySpan<byte> s = other.buf.AsSpan(0, other.pos);
        foreach (byte b in s)
        {
            Write(8, b);
        }

        if (other.accBits > 0)
        {
            Write(other.accBits, other.acc);
        }
    }

    /// <summary>The bytes written so far (the writer must be byte-aligned).</summary>
    public ReadOnlySpan<byte> GetSpan()
    {
        if (accBits != 0)
        {
            throw new InvalidOperationException($"Writer is not byte-aligned (bits={bits}, acc={accBits}, pos={pos}).");
        }

        return buf.AsSpan(0, pos);
    }

    public byte[] ToArray() => GetSpan().ToArray();
}

/// <summary>Counts bits instead of writing them (libjxl SizeWriter).</summary>
internal sealed class LjSizeWriter
{
    public long Size;

    public void Write(int nbits, ulong v) => Size += nbits;
}

internal static class LjMath
{
    public static int FloorLog2(uint v) => 31 - BitOperations.LeadingZeroCount(v);

    public static int FloorLog2(ulong v) => 63 - BitOperations.LeadingZeroCount(v);

    public static int CeilLog2Nonzero(uint v)
    {
        int f = FloorLog2(v);
        return (v & (v - 1)) == 0 ? f : f + 1;
    }

    public static int CeilLog2Nonzero(ulong v)
    {
        int f = FloorLog2(v);
        return (v & (v - 1)) == 0 ? f : f + 1;
    }

    public static uint PackSigned(int v) => (uint)((v << 1) ^ (v >> 31));

    public static int DivCeil(int a, int b) => (a + b - 1) / b;

    // FastLog2f (base/fast_math-inl.h), a (2,2) rational polynomial after range reduction. `fma` selects how
    // libjxl's MulAdd was compiled: true for the dynamically dispatched AVX2 code (fused multiply-add), false for
    // the statically dispatched SSE2 baseline (separate multiply and add).
    public static float FastLog2f(float x, bool fma)
    {
        int xBits = BitConverter.SingleToInt32Bits(x);
        int expBits = xBits - 0x3f2aaaab;
        int expShifted = expBits >> 23;
        float mantissa = BitConverter.Int32BitsToSingle(xBits - (expShifted << 23));
        float expVal = expShifted;
        float m = mantissa - 1.0f;
        const float p0 = -1.8503833400518310E-06f, p1 = 1.4287160470083755E+00f, p2 = 7.4245873327820566E-01f;
        const float q0 = 9.9032814277590719E-01f, q1 = 1.0096718572241148E+00f, q2 = 1.7409343003366853E-01f;
        float yp, yq;
        if (fma)
        {
            yp = MathF.FusedMultiplyAdd(p2, m, p1);
            yq = MathF.FusedMultiplyAdd(q2, m, q1);
            yp = MathF.FusedMultiplyAdd(yp, m, p0);
            yq = MathF.FusedMultiplyAdd(yq, m, q0);
        }
        else
        {
            yp = (p2 * m) + p1;
            yq = (q2 * m) + q1;
            yp = (yp * m) + p0;
            yq = (yq * m) + q0;
        }

        return (yp / yq) + expVal;
    }

    // Highway SumOfLanes for 8 float lanes on AVX2: swap the 128-bit blocks, then reduce within a block.
    public static float SumOfLanes8(ReadOnlySpan<float> v)
    {
        float u0 = v[0] + v[4], u1 = v[1] + v[5], u2 = v[2] + v[6], u3 = v[3] + v[7];
        return (u0 + u3) + (u1 + u2);
    }
}
