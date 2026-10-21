// SIMD kernels whose exactness depends on the sample range (8-bit: Hadamard sums in 16-bit lanes, as x265's asm) or
// that work on coefficients (count_nonzero, findPosFirstLast; both depths), each bit-identical to the scalar port of
// x265's C next to it (X265PrimitivesTests compares them).
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;
#if HIGH_BIT_DEPTH
using pixel = ushort;
#else
using pixel = byte;
#endif

internal static unsafe partial class X265Primitives
{
    /// <summary>count_nonzero of numCoeff (a multiple of 16) coefficients.</summary>
    public static int CountNonzero(short* quantCoeff, int numCoeff)
    {
        if (!Sse2.IsSupported) return CountNonzeroScalar(quantCoeff, numCoeff);
        int zeros = 0;
        for (int i = 0; i < numCoeff; i += 16)
        {
            var b = Sse2.PackSignedSaturate(Sse2.LoadVector128(quantCoeff + i), Sse2.LoadVector128(quantCoeff + i + 8));
            zeros += BitOperations.PopCount((uint)Sse2.MoveMask(Sse2.CompareEqual(b, Vector128<sbyte>.Zero)));
        }

        return numCoeff - zeros;
    }

    /// <summary>
    /// findPosFirstLast of a 4x4 group: the first and last nonzero scan positions and (bit 31) the parity of the sum
    /// of the levels between them (all x265 keeps of that sum).
    /// </summary>
    public static uint FindPosFirstLast(short* dstCoeff, long trSize, ushort* scanTbl)
    {
        if (!Ssse3.IsSupported) return FindPosFirstLastScalar(dstCoeff, trSize, scanTbl);
        var r01 = Vector128.Create(*(long*)dstCoeff, *(long*)(dstCoeff + trSize)).AsInt16();
        var r23 = Vector128.Create(*(long*)(dstCoeff + (2 * trSize)), *(long*)(dstCoeff + (3 * trSize))).AsInt16();
        var order = Sse2.PackUnsignedSaturate(Sse2.LoadVector128((short*)scanTbl), Sse2.LoadVector128((short*)scanTbl + 8));
        var zero = Sse2.CompareEqual(Sse2.PackSignedSaturate(r01, r23), Vector128<sbyte>.Zero).AsByte();
        var one = Vector128.Create((short)1);
        var odd = Sse2.PackSignedSaturate(Sse2.And(r01, one), Sse2.And(r23, one)).AsByte();
        uint nz = ~(uint)Sse2.MoveMask(Ssse3.Shuffle(zero, order)) & 0xFFFF;
        if (nz == 0)
        {
            return FindPosFirstLastScalar(dstCoeff, trSize, scanTbl);
        }

        uint oddScan = (uint)Sse2.MoveMask(Sse2.ShiftLeftLogical(Ssse3.Shuffle(odd, order).AsUInt16(), 7).AsByte());
        int first = BitOperations.TrailingZeroCount(nz);
        int last = 31 - BitOperations.LeadingZeroCount(nz);
        uint range = ((2u << last) - 1) & ~((1u << first) - 1);
        uint parity = (uint)BitOperations.PopCount(oddScan & range) & 1;
        return (parity << 31) | ((uint)last << 8) | (uint)first;
    }

    // PsyRdoQuant12 with 64-bit lanes: c = s * s << scaleBits, c' = c - ((psyScale * (f - s)) >> max) (the arithmetic
    // shift as a logical one of the value biased by 2^62), the sum of c + c'
    private static long PsyRdoQuant12Sse41(int log2TrSize, short* resi, short* fenc, long* costUncoded, int psyScale, uint blkPos)
    {
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - log2TrSize;
        byte scaleBits = (byte)(SCALE_BITS - (2 * transformShift));
        int max = Math.Max(0, (2 * transformShift) + 1);
        uint trSize = 1u << log2TrSize;
        var vScale = Vector128.Create(psyScale);
        var bias = Vector128.Create(1L << 62);
        var unbias = Vector128.Create((1L << 62) >> max);
        byte m = (byte)max;
        var sum = Vector128<long>.Zero;
        for (int y = 0; y < MLS_CG_SIZE; y++, blkPos += trSize)
        {
            var s = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(*(long*)(resi + blkPos)).AsInt16());
            var f = Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(*(long*)(fenc + blkPos)).AsInt16());
            var sq = Sse41.MultiplyLow(s, s);                                  // < 2^31
            var pred = Sse2.Subtract(f, s);
            var c0 = Sse2.ShiftLeftLogical(Sse41.ConvertToVector128Int64(sq), scaleBits);
            var c1 = Sse2.ShiftLeftLogical(Sse41.ConvertToVector128Int64(Sse2.ShiftRightLogical128BitLane(sq, 8)), scaleBits);
            var p0 = Sse41.Multiply(pred, vScale);                             // lanes 0, 2
            var p1 = Sse41.Multiply(Sse2.ShiftRightLogical128BitLane(pred, 4), vScale);   // lanes 1, 3
            var q0 = Sse2.Subtract(Sse2.ShiftRightLogical(Sse2.Add(p0, bias), m), unbias);
            var q1 = Sse2.Subtract(Sse2.ShiftRightLogical(Sse2.Add(p1, bias), m), unbias);
            // q0 = [psy0, psy2], q1 = [psy1, psy3] -> in order
            var psyA = Sse2.UnpackLow(q0, q1);   // psy0, psy1
            var psyB = Sse2.UnpackHigh(q0, q1);  // psy2, psy3
            var cp0 = Sse2.Subtract(c0, psyA);
            var cp1 = Sse2.Subtract(c1, psyB);
            Sse2.Store(costUncoded + blkPos, cp0);
            Sse2.Store(costUncoded + blkPos + 2, cp1);
            sum = Sse2.Add(sum, Sse2.Add(Sse2.Add(c0, cp0), Sse2.Add(c1, cp1)));
        }

        return sum.GetElement(0) + sum.GetElement(1);
    }

#if !HIGH_BIT_DEPTH
    // 8 pixels as int16
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen8(byte* p) => Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(ulong*)p).AsByte(), Vector128<byte>.Zero).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void H8(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
        ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        var a0 = Sse2.Add(v0, v1); var a1 = Sse2.Subtract(v0, v1); var a2 = Sse2.Add(v2, v3); var a3 = Sse2.Subtract(v2, v3);
        var a4 = Sse2.Add(v4, v5); var a5 = Sse2.Subtract(v4, v5); var a6 = Sse2.Add(v6, v7); var a7 = Sse2.Subtract(v6, v7);
        var b0 = Sse2.Add(a0, a2); var b2 = Sse2.Subtract(a0, a2); var b1 = Sse2.Add(a1, a3); var b3 = Sse2.Subtract(a1, a3);
        var b4 = Sse2.Add(a4, a6); var b6 = Sse2.Subtract(a4, a6); var b5 = Sse2.Add(a5, a7); var b7 = Sse2.Subtract(a5, a7);
        v0 = Sse2.Add(b0, b4); v4 = Sse2.Subtract(b0, b4); v1 = Sse2.Add(b1, b5); v5 = Sse2.Subtract(b1, b5);
        v2 = Sse2.Add(b2, b6); v6 = Sse2.Subtract(b2, b6); v3 = Sse2.Add(b3, b7); v7 = Sse2.Subtract(b3, b7);
    }

    // sum of |8x8 Hadamard| of int16 rows with |value| <= 255 (every stage stays within int16: at most 64 x 255)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HadamardAbsSum8x8(Vector128<short> r0, Vector128<short> r1, Vector128<short> r2, Vector128<short> r3,
        Vector128<short> r4, Vector128<short> r5, Vector128<short> r6, Vector128<short> r7)
    {
        H8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        var a0 = Sse2.UnpackLow(r0, r1); var a1 = Sse2.UnpackHigh(r0, r1);
        var a2 = Sse2.UnpackLow(r2, r3); var a3 = Sse2.UnpackHigh(r2, r3);
        var a4 = Sse2.UnpackLow(r4, r5); var a5 = Sse2.UnpackHigh(r4, r5);
        var a6 = Sse2.UnpackLow(r6, r7); var a7 = Sse2.UnpackHigh(r6, r7);
        var b0 = Sse2.UnpackLow(a0.AsInt32(), a2.AsInt32()); var b1 = Sse2.UnpackHigh(a0.AsInt32(), a2.AsInt32());
        var b2 = Sse2.UnpackLow(a1.AsInt32(), a3.AsInt32()); var b3 = Sse2.UnpackHigh(a1.AsInt32(), a3.AsInt32());
        var b4 = Sse2.UnpackLow(a4.AsInt32(), a6.AsInt32()); var b5 = Sse2.UnpackHigh(a4.AsInt32(), a6.AsInt32());
        var b6 = Sse2.UnpackLow(a5.AsInt32(), a7.AsInt32()); var b7 = Sse2.UnpackHigh(a5.AsInt32(), a7.AsInt32());
        r0 = Sse2.UnpackLow(b0.AsInt64(), b4.AsInt64()).AsInt16(); r1 = Sse2.UnpackHigh(b0.AsInt64(), b4.AsInt64()).AsInt16();
        r2 = Sse2.UnpackLow(b1.AsInt64(), b5.AsInt64()).AsInt16(); r3 = Sse2.UnpackHigh(b1.AsInt64(), b5.AsInt64()).AsInt16();
        r4 = Sse2.UnpackLow(b2.AsInt64(), b6.AsInt64()).AsInt16(); r5 = Sse2.UnpackHigh(b2.AsInt64(), b6.AsInt64()).AsInt16();
        r6 = Sse2.UnpackLow(b3.AsInt64(), b7.AsInt64()).AsInt16(); r7 = Sse2.UnpackHigh(b3.AsInt64(), b7.AsInt64()).AsInt16();
        H8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        // pairs of |values| <= 2 x 16320 fit int16; pmaddwd against ones widens
        var ones = Vector128.Create((short)1);
        var s0 = Sse2.MultiplyAddAdjacent(Sse2.Add(Ssse3.Abs(r0).AsInt16(), Ssse3.Abs(r1).AsInt16()), ones);
        var s1 = Sse2.MultiplyAddAdjacent(Sse2.Add(Ssse3.Abs(r2).AsInt16(), Ssse3.Abs(r3).AsInt16()), ones);
        var s2 = Sse2.MultiplyAddAdjacent(Sse2.Add(Ssse3.Abs(r4).AsInt16(), Ssse3.Abs(r5).AsInt16()), ones);
        var s3 = Sse2.MultiplyAddAdjacent(Sse2.Add(Ssse3.Abs(r6).AsInt16(), Ssse3.Abs(r7).AsInt16()), ones);
        return Vector128.Sum(Sse2.Add(Sse2.Add(s0, s1), Sse2.Add(s2, s3)));
    }

    // sa8d_8x8 before rounding, 16-bit lanes (8-bit differences); past the SWAR limit, the scalar form's result
    private static int Sa8d8x8Raw16(byte* pix1, long i1, byte* pix2, long i2)
    {
        int total = HadamardAbsSum8x8(
            Sse2.Subtract(Widen8(pix1), Widen8(pix2)),
            Sse2.Subtract(Widen8(pix1 + i1), Widen8(pix2 + i2)),
            Sse2.Subtract(Widen8(pix1 + (2 * i1)), Widen8(pix2 + (2 * i2))),
            Sse2.Subtract(Widen8(pix1 + (3 * i1)), Widen8(pix2 + (3 * i2))),
            Sse2.Subtract(Widen8(pix1 + (4 * i1)), Widen8(pix2 + (4 * i2))),
            Sse2.Subtract(Widen8(pix1 + (5 * i1)), Widen8(pix2 + (5 * i2))),
            Sse2.Subtract(Widen8(pix1 + (6 * i1)), Widen8(pix2 + (6 * i2))),
            Sse2.Subtract(Widen8(pix1 + (7 * i1)), Widen8(pix2 + (7 * i2))));
        return total < 65536 ? total : Sa8d8x8RawScalar(pix1, i1, pix2, i2);
    }

    // psy energy of an 8x8 block: sa8d against zero (rounded) minus a quarter of the pixel sum
    private static int PsyEnergy8x8(byte* p, long stride)
    {
        var r0 = Widen8(p); var r1 = Widen8(p + stride); var r2 = Widen8(p + (2 * stride)); var r3 = Widen8(p + (3 * stride));
        var r4 = Widen8(p + (4 * stride)); var r5 = Widen8(p + (5 * stride)); var r6 = Widen8(p + (6 * stride)); var r7 = Widen8(p + (7 * stride));
        var sum16 = Sse2.Add(Sse2.Add(Sse2.Add(r0, r1), Sse2.Add(r2, r3)), Sse2.Add(Sse2.Add(r4, r5), Sse2.Add(r6, r7)));
        int sad = Vector128.Sum(Sse2.MultiplyAddAdjacent(sum16, Vector128.Create((short)1)));
        int raw = HadamardAbsSum8x8(r0, r1, r2, r3, r4, r5, r6, r7);
        if (raw >= 65536) raw = Sa8d8x8RawScalar(p, stride, ZeroBuf, 0);
        return ((raw + 2) >> 2) - (sad >> 2);
    }

    /// <summary>intra_pred planar (8-bit): every stage within int16 (at most 126 x 255 + 32).</summary>
    private static void PlanarPred8(int log2Size, byte* dst, long dstStride, byte* srcPix)
    {
        int n = 1 << log2Size;
        byte* above = srcPix + 1;
        byte* left = srcPix + (2 * n) + 1;
        var topRight = Vector128.Create((short)above[n]);
        var bottomLeft = Vector128.Create((short)left[n]);
        byte shift = (byte)(log2Size + 1);
        var idx = Vector128.Create((short)1, 2, 3, 4, 5, 6, 7, 8);
        for (int x = 0; x < n; x += 8)
        {
            var ab = Widen8(above + x);
            var xp1 = Sse2.Add(idx, Vector128.Create((short)x));                 // x + 1
            var nx = Sse2.Subtract(Vector128.Create((short)n), xp1);             // n - 1 - x
            // row 0: (n - 1) * above + (x + 1) * topRight + bottomLeft + n
            var acc = Sse2.Add(Sse2.Add(Sse2.MultiplyLow(ab, Vector128.Create((short)(n - 1))), Sse2.MultiplyLow(xp1, topRight)), Sse2.Add(bottomLeft, Vector128.Create((short)n)));
            var step = Sse2.Subtract(bottomLeft, ab);
            for (int y = 0; y < n; y++)
            {
                var v = Sse2.ShiftRightLogical(Sse2.Add(acc, Sse2.MultiplyLow(nx, Vector128.Create((short)left[y]))), shift);
                var p = Sse2.PackUnsignedSaturate(v, v);
                if (n == 4) *(uint*)(dst + (y * dstStride) + x) = p.AsUInt32().ToScalar();
                else *(ulong*)(dst + (y * dstStride) + x) = p.AsUInt64().ToScalar();
                acc = Sse2.Add(acc, step);
            }
        }
    }
#endif

    /// <summary>dequant_normal: clip16((q * scale + round) >> shift), 32-bit products.</summary>
    public static void DequantNormal(short* quantCoef, short* coef, int num, int scale, int shift)
    {
        if (!Sse41.IsSupported)
        {
            DequantNormalScalar(quantCoef, coef, num, scale, shift);
            return;
        }

        var vs = Vector128.Create(scale);
        var va = Vector128.Create(1 << (shift - 1));
        byte sh = (byte)shift;
        for (int n = 0; n < num; n += 8)
        {
            var q = Sse2.LoadVector128(quantCoef + n);
            var lo = Sse41.ConvertToVector128Int32(q);
            var hi = Sse41.ConvertToVector128Int32(Sse2.ShiftRightLogical128BitLane(q, 8));
            lo = Sse2.ShiftRightArithmetic(Sse2.Add(Sse41.MultiplyLow(lo, vs), va), sh);
            hi = Sse2.ShiftRightArithmetic(Sse2.Add(Sse41.MultiplyLow(hi, vs), va), sh);
            Sse2.Store(coef + n, Sse2.PackSignedSaturate(lo, hi));
        }
    }

#if !HIGH_BIT_DEPTH
    // n x n residual / reconstruction / SSE helpers for 8-bit pixels at any n (4 included)

    private static void GetResidual8(byte* fenc, byte* pred, short* residual, long stride, int n)
    {
        for (int y = 0; y < n; y++, fenc += stride, pred += stride, residual += stride)
        {
            if (n == 4)
            {
                *(long*)residual = Sse2.Subtract(Widen8(fenc), Widen8(pred)).AsInt64().ToScalar();
                continue;
            }

            for (int x = 0; x < n; x += 8) Sse2.Store(residual + x, Sse2.Subtract(Widen8(fenc + x), Widen8(pred + x)));
        }
    }

    // clip(pred + residual): saturating int16 adds, then packuswb (the same clamp for every int16 residual)
    private static void AddPS8(byte* a, long dstride, byte* b0, short* b1, long sstride0, long sstride1, int n)
    {
        for (int y = 0; y < n; y++, a += dstride, b0 += sstride0, b1 += sstride1)
        {
            if (n == 4)
            {
                var v = Sse2.AddSaturate(Widen8(b0), Vector128.CreateScalarUnsafe(*(long*)b1).AsInt16());
                *(uint*)a = Sse2.PackUnsignedSaturate(v, v).AsUInt32().ToScalar();
                continue;
            }

            for (int x = 0; x < n; x += 16)
            {
                if (n == 8)
                {
                    var v = Sse2.AddSaturate(Widen8(b0), Sse2.LoadVector128(b1));
                    *(ulong*)a = Sse2.PackUnsignedSaturate(v, v).AsUInt64().ToScalar();
                    break;
                }

                var p = Sse2.LoadVector128(b0 + x);
                var lo = Sse2.AddSaturate(Sse2.UnpackLow(p, Vector128<byte>.Zero).AsInt16(), Sse2.LoadVector128(b1 + x));
                var hi = Sse2.AddSaturate(Sse2.UnpackHigh(p, Vector128<byte>.Zero).AsInt16(), Sse2.LoadVector128(b1 + x + 8));
                Sse2.Store(a + x, Sse2.PackUnsignedSaturate(lo, hi));
            }
        }
    }

    private static uint SsePP8(byte* pix1, long stride1, byte* pix2, long stride2, int lx, int ly)
    {
        var acc = Vector128<int>.Zero;
        for (int y = 0; y < ly; y++, pix1 += stride1, pix2 += stride2)
        {
            if (lx == 4)
            {
                var d4 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(uint*)pix1).AsByte(), Vector128<byte>.Zero).AsInt16(),
                    Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(uint*)pix2).AsByte(), Vector128<byte>.Zero).AsInt16());
                acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(d4, d4));
                continue;
            }

            for (int x = 0; x < lx; x += 8)
            {
                var d = Sse2.Subtract(Widen8(pix1 + x), Widen8(pix2 + x));
                acc = Sse2.Add(acc, Sse2.MultiplyAddAdjacent(d, d));
            }
        }

        return (uint)Vector128.Sum(acc);
    }

    private static void CopyPS8(short* dst, long dstStride, byte* src, long srcStride, int n)
    {
        for (int y = 0; y < n; y++, dst += dstStride, src += srcStride)
        {
            if (n == 4)
            {
                *(long*)dst = Widen8(src).AsInt64().ToScalar();
                continue;
            }

            for (int x = 0; x < n; x += 8) Sse2.Store(dst + x, Widen8(src + x));
        }
    }
    // 4 pixels of two rows as int16 ([row a | row b])
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Rows4x2(byte* p, long stride) =>
        Sse2.UnpackLow(Vector128.Create(*(uint*)p, *(uint*)(p + stride), 0, 0).AsByte(), Vector128<byte>.Zero).AsInt16();

    // satd_4x4 of 8-bit pixels: the vertical Hadamard in int16, the horizontal one by pmaddwd / phaddd / phsubd
    private static int Satd4x4x8(byte* pix1, long stride1, byte* pix2, long stride2)
    {
        var d01 = Sse2.Subtract(Rows4x2(pix1, stride1), Rows4x2(pix2, stride2));
        var d23 = Sse2.Subtract(Rows4x2(pix1 + (2 * stride1), stride1), Rows4x2(pix2 + (2 * stride2), stride2));
        var s = Sse2.Add(d01, d23);
        var d = Sse2.Subtract(d01, d23);
        var t0 = Sse2.UnpackLow(s.AsInt64(), d.AsInt64()).AsInt16();
        var t1 = Sse2.UnpackHigh(s.AsInt64(), d.AsInt64()).AsInt16();
        return (HorizontalH4Abs(Sse2.Add(t0, t1)) + HorizontalH4Abs(Sse2.Subtract(t0, t1))) >> 1;
    }

    // sum of |4-point Hadamard| of each 4-sample group of v (two groups)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalH4Abs(Vector128<short> v)
    {
        var pa = Sse2.MultiplyAddAdjacent(v, Vector128.Create((short)1));
        var pb = Sse2.MultiplyAddAdjacent(v, Vector128.Create((short)1, -1, 1, -1, 1, -1, 1, -1));
        var sum = Sse2.Add(Ssse3.Abs(Ssse3.HorizontalAdd(pa, pb)).AsInt32(), Ssse3.Abs(Ssse3.HorizontalSubtract(pa, pb)).AsInt32());
        return Vector128.Sum(sum);
    }

    // psy energy of a 4x4 block: satd against zero minus a quarter of the pixel sum
    private static int PsyEnergy4x4(byte* p, long stride)
    {
        var r01 = Rows4x2(p, stride);
        var r23 = Rows4x2(p + (2 * stride), stride);
        int sad = Vector128.Sum(Sse2.MultiplyAddAdjacent(Sse2.Add(r01, r23), Vector128.Create((short)1)));
        var s = Sse2.Add(r01, r23);
        var d = Sse2.Subtract(r01, r23);
        var t0 = Sse2.UnpackLow(s.AsInt64(), d.AsInt64()).AsInt16();
        var t1 = Sse2.UnpackHigh(s.AsInt64(), d.AsInt64()).AsInt16();
        int satd = (HorizontalH4Abs(Sse2.Add(t0, t1)) + HorizontalH4Abs(Sse2.Subtract(t0, t1))) >> 1;
        return satd - (sad >> 2);
    }
#endif
}
