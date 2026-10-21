// SIMD forms of x265 primitives (portable Vector128: SSE2 on x64, AdvSimd on ARM64), each bit-identical to the scalar
// port of x265's C next to it (X265PrimitivesTests compares them).
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;
#if HIGH_BIT_DEPTH
using pixel = ushort;
using sse_t = ulong;
#else
using pixel = byte;
using sse_t = uint;
#endif

internal static unsafe partial class X265Primitives
{
    // per scan type: byte p = raster index of 4x4 scan position p (forward), of position 15 - p (reverse)
    private static readonly Vector128<byte>[] s_scanFwd = BuildScanMasks(false);
    private static readonly Vector128<byte>[] s_scanRev = BuildScanMasks(true);

    private static Vector128<byte>[] BuildScanMasks(bool reverse)
    {
        var masks = new Vector128<byte>[3];
        Span<byte> b = stackalloc byte[16];
        for (int t = 0; t < 3; t++)
        {
            for (int p = 0; p < 16; p++) b[p] = (byte)X265Tables.Scan4x4[(t * 16) + (reverse ? 15 - p : p)];
            masks[t] = Vector128.Create((ReadOnlySpan<byte>)b);
        }

        return masks;
    }

    /// <summary>
    /// scanPosLast: each coefficient group's nonzero flags (first scan position in the top bit, the last group's
    /// ending at its last nonzero coefficient), the signs of its nonzero coefficients (in scan order from bit 0) and
    /// their count (groups up to the last one); returns the scan position of the last nonzero coefficient. A group's 4x4 block packed to bytes
    /// (saturated: zero / sign kept) and put in scan order with pshufb.
    /// </summary>
    public static int ScanPosLast(ushort* scan, short* coeff, ushort* coeffSign, ushort* coeffFlag, byte* coeffNum, int numSig, int scanType, long trSize)
    {
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            return ScanPosLastScalar(scan, coeff, coeffSign, coeffFlag, coeffNum, numSig);
        }

        // (x265 clears the arrays first; every group up to the last is written below and no caller reads further)
        var fwd = s_scanFwd[scanType];
        var rev = s_scanRev[scanType];
        for (int cgIdx = 0; ; cgIdx++)
        {
            short* cg = coeff + scan[cgIdx << 4];
            var r01 = Vector128.Create(*(long*)cg, *(long*)(cg + trSize)).AsInt16();
            var r23 = Vector128.Create(*(long*)(cg + (2 * trSize)), *(long*)(cg + (3 * trSize))).AsInt16();
            var b = System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(r01, r23).AsByte();
            var zero = Vector128.Equals(b, Vector128<byte>.Zero);
            uint nz = ~System.Runtime.Intrinsics.X86.Ssse3.Shuffle(zero, fwd).ExtractMostSignificantBits() & 0xFFFF;
            uint nzRev = ~System.Runtime.Intrinsics.X86.Ssse3.Shuffle(zero, rev).ExtractMostSignificantBits() & 0xFFFF;
            uint neg = System.Runtime.Intrinsics.X86.Ssse3.Shuffle(b, fwd).ExtractMostSignificantBits();
            int num = BitOperations.PopCount(nz);
            coeffNum[cgIdx] = (byte)num;
            coeffSign[cgIdx] = (ushort)ExtractBits(neg, nz);
            numSig -= num;
            if (numSig <= 0)
            {
                int lastPos = 31 - BitOperations.LeadingZeroCount(nz);
                coeffFlag[cgIdx] = (ushort)(nzRev >> (15 - lastPos));
                return (cgIdx << 4) + lastPos;
            }

            coeffFlag[cgIdx] = (ushort)nzRev;
        }
    }

    // the bits of value at the set bits of mask, packed from bit 0 (pext)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ExtractBits(uint value, uint mask)
    {
        if (System.Runtime.Intrinsics.X86.Bmi2.IsSupported)
        {
            return System.Runtime.Intrinsics.X86.Bmi2.ParallelBitExtract(value, mask);
        }

        uint r = 0;
        for (int k = 0; mask != 0; mask &= mask - 1, k++)
        {
            r |= ((value >> BitOperations.TrailingZeroCount(mask)) & 1) << k;
        }

        return r;
    }

    /// <summary>nquant: |clip16(sign * ((|coef| * q + add) >> qBits))| stored as int16 (32768 wraps as in x265), the
    /// number of nonzero levels returned.</summary>
    public static uint NQuant(short* coef, int* quantCoeff, short* qCoef, int qBits, int add, int numCoeff)
    {
        if (!System.Runtime.Intrinsics.X86.Sse41.IsSupported) return NQuantScalar(coef, quantCoeff, qCoef, qBits, add, numCoeff);
        var vadd = Vector128.Create(add);
        byte sh = (byte)qBits;
        int zeros = 0;
        for (int i = 0; i < numCoeff; i += 8)
        {
            var c = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(coef + i);
            var c0 = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int32(c);
            var c1 = System.Runtime.Intrinsics.X86.Sse41.ConvertToVector128Int32(System.Runtime.Intrinsics.X86.Sse2.ShiftRightLogical128BitLane(c, 8));
            var l0 = System.Runtime.Intrinsics.X86.Sse2.ShiftRightArithmetic(System.Runtime.Intrinsics.X86.Sse2.Add(
                System.Runtime.Intrinsics.X86.Sse41.MultiplyLow(System.Runtime.Intrinsics.X86.Ssse3.Abs(c0).AsInt32(), System.Runtime.Intrinsics.X86.Sse2.LoadVector128(quantCoeff + i)), vadd), sh);
            var l1 = System.Runtime.Intrinsics.X86.Sse2.ShiftRightArithmetic(System.Runtime.Intrinsics.X86.Sse2.Add(
                System.Runtime.Intrinsics.X86.Sse41.MultiplyLow(System.Runtime.Intrinsics.X86.Ssse3.Abs(c1).AsInt32(), System.Runtime.Intrinsics.X86.Sse2.LoadVector128(quantCoeff + i + 4)), vadd), sh);
            // level * sign, saturated to int16, then |.| (|-32768| stays 0x8000, as x265's int16 store)
            var m0 = System.Runtime.Intrinsics.X86.Sse2.ShiftRightArithmetic(c0, 31);
            var m1 = System.Runtime.Intrinsics.X86.Sse2.ShiftRightArithmetic(c1, 31);
            var p = System.Runtime.Intrinsics.X86.Sse2.PackSignedSaturate(
                System.Runtime.Intrinsics.X86.Sse2.Subtract(System.Runtime.Intrinsics.X86.Sse2.Xor(l0, m0), m0),
                System.Runtime.Intrinsics.X86.Sse2.Subtract(System.Runtime.Intrinsics.X86.Sse2.Xor(l1, m1), m1));
            System.Runtime.Intrinsics.X86.Sse2.Store(qCoef + i, System.Runtime.Intrinsics.X86.Ssse3.Abs(p).AsInt16());
            zeros += BitOperations.PopCount((uint)System.Runtime.Intrinsics.X86.Sse2.MoveMask(System.Runtime.Intrinsics.X86.Sse2.CompareEqual(p, Vector128<short>.Zero).AsByte()));
        }

        return (uint)(numCoeff - (zeros >> 1));
    }

    // ---- Hadamard costs and SSE (int32 lanes: exact for both depths) ----

    // 8 differences a[x] - b[x] as two vectors of int32
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadDiff8(pixel* a, pixel* b, out Vector128<int> lo, out Vector128<int> hi)
    {
#if HIGH_BIT_DEPTH
        var d = Vector128.Load(a).AsInt16() - Vector128.Load(b).AsInt16();
#else
        var av = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)a).AsByte()).AsInt16();
        var bv = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)b).AsByte()).AsInt16();
        var d = av - bv;
#endif
        (lo, hi) = Vector128.Widen(d);
    }

    // 4 differences as int32
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LoadDiff4(pixel* a, pixel* b)
    {
#if HIGH_BIT_DEPTH
        var av = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)a).AsUInt16()).AsInt32();
        var bv = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)b).AsUInt16()).AsInt32();
#else
        var av = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(uint*)a).AsByte())).AsInt32();
        var bv = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(uint*)b).AsByte())).AsInt32();
#endif
        return av - bv;
    }

    // 8-point Hadamard across the 8 vectors (any output order: only absolute values are summed)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Hadamard8(ref Vector128<int> v0, ref Vector128<int> v1, ref Vector128<int> v2, ref Vector128<int> v3,
        ref Vector128<int> v4, ref Vector128<int> v5, ref Vector128<int> v6, ref Vector128<int> v7)
    {
        var a0 = v0 + v1; var a1 = v0 - v1; var a2 = v2 + v3; var a3 = v2 - v3;
        var a4 = v4 + v5; var a5 = v4 - v5; var a6 = v6 + v7; var a7 = v6 - v7;
        var b0 = a0 + a2; var b2 = a0 - a2; var b1 = a1 + a3; var b3 = a1 - a3;
        var b4 = a4 + a6; var b6 = a4 - a6; var b5 = a5 + a7; var b7 = a5 - a7;
        v0 = b0 + b4; v4 = b0 - b4; v1 = b1 + b5; v5 = b1 - b5;
        v2 = b2 + b6; v6 = b2 - b6; v3 = b3 + b7; v7 = b3 - b7;
    }

    // 4x4 transpose of int32 rows
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
    {
        var t0 = System.Runtime.Intrinsics.X86.Sse2.UnpackLow(r0, r1);
        var t1 = System.Runtime.Intrinsics.X86.Sse2.UnpackLow(r2, r3);
        var t2 = System.Runtime.Intrinsics.X86.Sse2.UnpackHigh(r0, r1);
        var t3 = System.Runtime.Intrinsics.X86.Sse2.UnpackHigh(r2, r3);
        r0 = System.Runtime.Intrinsics.X86.Sse2.UnpackLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r1 = System.Runtime.Intrinsics.X86.Sse2.UnpackHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r2 = System.Runtime.Intrinsics.X86.Sse2.UnpackLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
        r3 = System.Runtime.Intrinsics.X86.Sse2.UnpackHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
    }

    /// <summary>sa8d_8x8 before rounding: the sum of the absolute 8x8 Hadamard coefficients of the differences. The
    /// 8-bit scalar form adds them in 16-bit lanes (x264's SWAR), exact while the total stays under 65536; beyond that
    /// its result is taken.</summary>
    internal static int Sa8d8x8Raw(pixel* pix1, long i1, pixel* pix2, long i2)
    {
#if !HIGH_BIT_DEPTH
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported) return Sa8d8x8Raw16(pix1, i1, pix2, i2);
#endif
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported) return Sa8d8x8RawScalar(pix1, i1, pix2, i2);
        LoadDiff8(pix1, pix2, out var l0, out var h0);
        LoadDiff8(pix1 + i1, pix2 + i2, out var l1, out var h1);
        LoadDiff8(pix1 + (2 * i1), pix2 + (2 * i2), out var l2, out var h2);
        LoadDiff8(pix1 + (3 * i1), pix2 + (3 * i2), out var l3, out var h3);
        LoadDiff8(pix1 + (4 * i1), pix2 + (4 * i2), out var l4, out var h4);
        LoadDiff8(pix1 + (5 * i1), pix2 + (5 * i2), out var l5, out var h5);
        LoadDiff8(pix1 + (6 * i1), pix2 + (6 * i2), out var l6, out var h6);
        LoadDiff8(pix1 + (7 * i1), pix2 + (7 * i2), out var l7, out var h7);
        Hadamard8(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7);
        Hadamard8(ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7);
        // transpose: columns 0-3 of rows 0-7 become rows 0-3, columns 4-7 rows 4-7
        Transpose4(ref l0, ref l1, ref l2, ref l3);
        Transpose4(ref l4, ref l5, ref l6, ref l7);
        Transpose4(ref h0, ref h1, ref h2, ref h3);
        Transpose4(ref h4, ref h5, ref h6, ref h7);
        // rows 0-3 now: (l0..l3 | l4..l7) halves; rows 4-7: (h0..h3 | h4..h7)
        Hadamard8(ref l0, ref l1, ref l2, ref l3, ref h0, ref h1, ref h2, ref h3);
        Hadamard8(ref l4, ref l5, ref l6, ref l7, ref h4, ref h5, ref h6, ref h7);
        var sum = Vector128.Abs(l0) + Vector128.Abs(l1) + Vector128.Abs(l2) + Vector128.Abs(l3)
                  + Vector128.Abs(l4) + Vector128.Abs(l5) + Vector128.Abs(l6) + Vector128.Abs(l7)
                  + Vector128.Abs(h0) + Vector128.Abs(h1) + Vector128.Abs(h2) + Vector128.Abs(h3)
                  + Vector128.Abs(h4) + Vector128.Abs(h5) + Vector128.Abs(h6) + Vector128.Abs(h7);
        int total = Vector128.Sum(sum);
#if !HIGH_BIT_DEPTH
        if (total >= 65536) return Sa8d8x8RawScalar(pix1, i1, pix2, i2);
#endif
        return total;
    }

    /// <summary>satd_4x4: half the sum of the absolute 4x4 Hadamard coefficients (the 8-bit SWAR lanes cannot
    /// overflow: at most 16 x 4080).</summary>
    public static int Satd4x4(pixel* pix1, long stride1, pixel* pix2, long stride2)
    {
#if !HIGH_BIT_DEPTH
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported) return Satd4x4x8(pix1, stride1, pix2, stride2);
#endif
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported) return Satd4x4Scalar(pix1, stride1, pix2, stride2);
        var r0 = LoadDiff4(pix1, pix2);
        var r1 = LoadDiff4(pix1 + stride1, pix2 + stride2);
        var r2 = LoadDiff4(pix1 + (2 * stride1), pix2 + (2 * stride2));
        var r3 = LoadDiff4(pix1 + (3 * stride1), pix2 + (3 * stride2));
        var a0 = r0 + r1; var a1 = r0 - r1; var a2 = r2 + r3; var a3 = r2 - r3;
        r0 = a0 + a2; r2 = a0 - a2; r1 = a1 + a3; r3 = a1 - a3;
        Transpose4(ref r0, ref r1, ref r2, ref r3);
        a0 = r0 + r1; a1 = r0 - r1; a2 = r2 + r3; a3 = r2 - r3;
        var sum = Vector128.Abs(a0 + a2) + Vector128.Abs(a0 - a2) + Vector128.Abs(a1 + a3) + Vector128.Abs(a1 - a3);
        return Vector128.Sum(sum) >> 1;
    }

    /// <summary>sad: the sum of absolute differences of a lx x ly block.</summary>
    public static int Sad(pixel* pix1, long stride1, pixel* pix2, long stride2, int lx, int ly)
    {
        if (!Vector128.IsHardwareAccelerated || lx < 4) return SadScalar(pix1, stride1, pix2, stride2, lx, ly);
        var acc = Vector128<int>.Zero;
        for (int y = 0; y < ly; y++, pix1 += stride1, pix2 += stride2)
        {
            int x = 0;
            for (; x + 8 <= lx; x += 8)
            {
                LoadDiff8(pix1 + x, pix2 + x, out var lo, out var hi);
                acc += Vector128.Abs(lo) + Vector128.Abs(hi);
            }

            if (x < lx)
            {
                acc += Vector128.Abs(LoadDiff4(pix1 + x, pix2 + x));
            }
        }

        return Vector128.Sum(acc);
    }

    /// <summary>sse_pp: the sum of squared differences of a lx x ly block.</summary>
    public static sse_t SsePP(pixel* pix1, long stride1, pixel* pix2, long stride2, int lx, int ly)
    {
#if !HIGH_BIT_DEPTH
        if (System.Runtime.Intrinsics.X86.Sse2.IsSupported && (lx & 3) == 0)
        {
            return SsePP8(pix1, stride1, pix2, stride2, lx, ly);
        }
#endif
        if (!Vector128.IsHardwareAccelerated || lx < 4) return SsePPScalar(pix1, stride1, pix2, stride2, lx, ly);
        var acc = Vector128<int>.Zero;
        for (int y = 0; y < ly; y++, pix1 += stride1, pix2 += stride2)
        {
            int x = 0;
            for (; x + 8 <= lx; x += 8)
            {
                LoadDiff8(pix1 + x, pix2 + x, out var lo, out var hi);
                acc += (lo * lo) + (hi * hi);
            }

            if (x < lx)
            {
                var d = LoadDiff4(pix1 + x, pix2 + x);
                acc += d * d;
            }
        }

        return (sse_t)(uint)Vector128.Sum(acc);
    }

    // ---- residuals and reconstruction ----

    // 8 pixels as int16
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load8(pixel* p)
    {
#if HIGH_BIT_DEPTH
        return Vector128.Load(p).AsInt16();
#else
        return Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)p).AsByte()).AsInt16();
#endif
    }

    /// <summary>getResidual: fenc - pred, n x n.</summary>
    public static void GetResidual(pixel* fenc, pixel* pred, short* residual, long stride, int n)
    {
#if !HIGH_BIT_DEPTH
        if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            GetResidual8(fenc, pred, residual, stride, n);
            return;
        }
#endif
        if (!Vector128.IsHardwareAccelerated || n < 8)
        {
            GetResidualScalar(fenc, pred, residual, stride, n);
            return;
        }

        for (int y = 0; y < n; y++, fenc += stride, pred += stride, residual += stride)
        {
            for (int x = 0; x < n; x += 8) (Load8(fenc + x) - Load8(pred + x)).Store(residual + x);
        }
    }

    /// <summary>add_ps: clip(pred + residual) into a, n x n.</summary>
    public static void AddPS(pixel* a, long dstride, pixel* b0, short* b1, long sstride0, long sstride1, int n)
    {
#if !HIGH_BIT_DEPTH
        if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            AddPS8(a, dstride, b0, b1, sstride0, sstride1, n);
            return;
        }
#endif
        if (!Vector128.IsHardwareAccelerated || n < 8)
        {
            AddPSScalar(a, dstride, b0, b1, sstride0, sstride1, n);
            return;
        }

        var max = Vector128.Create(PIXEL_MAX);
        for (int y = 0; y < n; y++, a += dstride, b0 += sstride0, b1 += sstride1)
        {
            for (int x = 0; x < n; x += 8)
            {
                var (p0, p1) = Vector128.Widen(Load8(b0 + x));
                var (r0, r1) = Vector128.Widen(Vector128.Load(b1 + x));
                var s0 = Vector128.Clamp(p0 + r0, Vector128<int>.Zero, max);
                var s1 = Vector128.Clamp(p1 + r1, Vector128<int>.Zero, max);
                var v = Vector128.Narrow(s0, s1).AsUInt16();
#if HIGH_BIT_DEPTH
                v.Store(a + x);
#else
                *(ulong*)(a + x) = Vector128.Narrow(v, v).AsUInt64().ToScalar();
#endif
            }
        }
    }

    /// <summary>copy_ps: pixels to int16, n x n.</summary>
    public static void CopyPS(short* dst, long dstStride, pixel* src, long srcStride, int n)
    {
#if !HIGH_BIT_DEPTH
        if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            CopyPS8(dst, dstStride, src, srcStride, n);
            return;
        }
#endif
        if (!Vector128.IsHardwareAccelerated || n < 8)
        {
            CopyPSScalar(dst, dstStride, src, srcStride, n);
            return;
        }

        for (int y = 0; y < n; y++, dst += dstStride, src += srcStride)
        {
            for (int x = 0; x < n; x += 8) Load8(src + x).Store(dst + x);
        }
    }

    // One row of angular prediction: dst[x] = ((32 - f) * r[x] + f * r[x + 1] + 16) >> 5.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AngularRow(pixel* dst, pixel* r, int f, int width)
    {
        int x = 0;
        if (Vector128.IsHardwareAccelerated && width >= 8)
        {
            var f0 = Vector128.Create((ushort)(32 - f));
            var f1 = Vector128.Create((ushort)f);
            var rnd = Vector128.Create((ushort)16);
            for (; x + 8 <= width; x += 8)
            {
#if HIGH_BIT_DEPTH
                var a = Vector128.Load(r + x);
                var b = Vector128.Load(r + x + 1);
                Vector128.ShiftRightLogical((a * f0) + (b * f1) + rnd, 5).Store(dst + x);
#else
                var a = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(r + x)).AsByte());
                var b = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(r + x + 1)).AsByte());
                var v = Vector128.ShiftRightLogical((a * f0) + (b * f1) + rnd, 5);
                *(ulong*)(dst + x) = Vector128.Narrow(v, v).AsUInt64().ToScalar();
#endif
            }
        }

        for (; x < width; x++)
        {
            dst[x] = (pixel)((((32 - f) * r[x]) + (f * r[x + 1]) + 16) >> 5);
        }
    }
}
