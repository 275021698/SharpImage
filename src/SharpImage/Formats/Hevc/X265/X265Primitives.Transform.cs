// SIMD forms of x265's DCT / IDCT (what its asm computes: the same integer sums as the C butterflies, so the
// coefficients are bit-identical). Each pass is a matrix product taken two inputs at a time with pmaddwd against an
// interleaved coefficient table; the passes' transposes sit in between.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;

internal static unsafe partial class X265Primitives
{
    // [sizeIdx] for each input pair m, for each group g of 4 outputs: the 8 coefficients
    // M[4g + q][2m], M[4g + q][2m + 1] (q = 0..3); forward M = T, inverse M = T transposed
    private static readonly short*[] s_dctFwd = BuildDctTables(inverse: false);
    private static readonly short*[] s_dctInv = BuildDctTables(inverse: true);

    private static short*[] BuildDctTables(bool inverse)
    {
        var tables = new short*[4];
        short[][] t = [X265Tables.T4, X265Tables.T8, X265Tables.T16, X265Tables.T32];
        for (int s = 0; s < 4; s++)
        {
            int n = 4 << s;
            short* tab = (short*)NativeMemory.AlignedAlloc((nuint)(n * n * sizeof(short)), 64);
            short* p = tab;
            for (int m = 0; m < n / 2; m++)
            {
                for (int g = 0; g < n / 4; g++)
                {
                    for (int q = 0; q < 4; q++)
                    {
                        int row = (4 * g) + q;
                        *p++ = inverse ? t[s][(2 * m * n) + row] : t[s][(row * n) + (2 * m)];
                        *p++ = inverse ? t[s][(((2 * m) + 1) * n) + row] : t[s][(row * n) + (2 * m) + 1];
                    }
                }
            }

            tables[s] = tab;
        }

        return tables;
    }

    // Every row r of the n x n input: out[r][i] = sat16((sum_k M[i][k] * in[r][k] + add) >> shift).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MatRows(short* input, long inStride, short* output, long outStride, int n, short* coef, int add, int shift)
    {
        if (Avx2.IsSupported && n >= 8)
        {
            MatRowsAvx2(input, inStride, output, outStride, n, coef, add, shift);
            return;
        }

        int groups = n >> 2, pairs = n >> 1;
        Vector128<int>* acc = stackalloc Vector128<int>[8];
        var vadd = Vector128.Create(add);
        for (int r = 0; r < n; r++)
        {
            short* x = input + (r * inStride);
            for (int g = 0; g < groups; g++) acc[g] = vadd;
            short* c = coef;
            for (int m = 0; m < pairs; m++)
            {
                var b = Vector128.Create(*(int*)(x + (2 * m))).AsInt16();
                for (int g = 0; g < groups; g++, c += 8)
                {
                    acc[g] = Sse2.Add(acc[g], Sse2.MultiplyAddAdjacent(b, Sse2.LoadAlignedVector128(c)));
                }
            }

            short* o = output + (r * outStride);
            if (groups == 1)
            {
                var v = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(acc[0], (byte)shift), Vector128<int>.Zero);
                *(long*)o = v.AsInt64().ToScalar();
            }
            else
            {
                for (int g = 0; g < groups; g += 2)
                {
                    var v = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(acc[g], (byte)shift), Sse2.ShiftRightArithmetic(acc[g + 1], (byte)shift));
                    Sse2.Store(o + (4 * g), v);
                }
            }
        }
    }

    // MatRows with 8 outputs per pmaddwd (two adjacent groups of the table; n >= 8)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MatRowsAvx2(short* input, long inStride, short* output, long outStride, int n, short* coef, int add, int shift)
    {
        int octets = n >> 3, pairs = n >> 1;
        Vector256<int>* acc = stackalloc Vector256<int>[4];
        var vadd = Vector256.Create(add);
        for (int r = 0; r < n; r++)
        {
            short* x = input + (r * inStride);
            for (int g = 0; g < octets; g++) acc[g] = vadd;
            short* c = coef;
            for (int m = 0; m < pairs; m++)
            {
                var b = Vector256.Create(*(int*)(x + (2 * m))).AsInt16();
                for (int g = 0; g < octets; g++, c += 16)
                {
                    acc[g] = Avx2.Add(acc[g], Avx2.MultiplyAddAdjacent(b, Avx.LoadAlignedVector256(c)));
                }
            }

            short* o = output + (r * outStride);
            for (int g = 0; g < octets; g++)
            {
                var v = Avx2.ShiftRightArithmetic(acc[g], (byte)shift);
                Sse2.Store(o + (8 * g), Sse2.PackSignedSaturate(v.GetLower(), v.GetUpper()));
            }
        }
    }

    // n x n int16 transpose: 8x8 blocks with SSE2 unpacks (n = 4 scalar)
    private static void Transpose16(short* src, short* dst, int n)
    {
        if (n == 4)
        {
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++) dst[(j * 4) + i] = src[(i * 4) + j];
            }

            return;
        }

        for (int bi = 0; bi < n; bi += 8)
        {
            for (int bj = 0; bj < n; bj += 8)
            {
                Transpose8x8(src + (bi * n) + bj, dst + (bj * n) + bi, n);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(short* src, short* dst, int stride)
    {
        var r0 = Sse2.LoadVector128(src);
        var r1 = Sse2.LoadVector128(src + stride);
        var r2 = Sse2.LoadVector128(src + (2 * stride));
        var r3 = Sse2.LoadVector128(src + (3 * stride));
        var r4 = Sse2.LoadVector128(src + (4 * stride));
        var r5 = Sse2.LoadVector128(src + (5 * stride));
        var r6 = Sse2.LoadVector128(src + (6 * stride));
        var r7 = Sse2.LoadVector128(src + (7 * stride));
        var a0 = Sse2.UnpackLow(r0, r1); var a1 = Sse2.UnpackHigh(r0, r1);
        var a2 = Sse2.UnpackLow(r2, r3); var a3 = Sse2.UnpackHigh(r2, r3);
        var a4 = Sse2.UnpackLow(r4, r5); var a5 = Sse2.UnpackHigh(r4, r5);
        var a6 = Sse2.UnpackLow(r6, r7); var a7 = Sse2.UnpackHigh(r6, r7);
        var b0 = Sse2.UnpackLow(a0.AsInt32(), a2.AsInt32()).AsInt64(); var b1 = Sse2.UnpackHigh(a0.AsInt32(), a2.AsInt32()).AsInt64();
        var b2 = Sse2.UnpackLow(a1.AsInt32(), a3.AsInt32()).AsInt64(); var b3 = Sse2.UnpackHigh(a1.AsInt32(), a3.AsInt32()).AsInt64();
        var b4 = Sse2.UnpackLow(a4.AsInt32(), a6.AsInt32()).AsInt64(); var b5 = Sse2.UnpackHigh(a4.AsInt32(), a6.AsInt32()).AsInt64();
        var b6 = Sse2.UnpackLow(a5.AsInt32(), a7.AsInt32()).AsInt64(); var b7 = Sse2.UnpackHigh(a5.AsInt32(), a7.AsInt32()).AsInt64();
        Sse2.Store(dst, Sse2.UnpackLow(b0, b4).AsInt16());
        Sse2.Store(dst + stride, Sse2.UnpackHigh(b0, b4).AsInt16());
        Sse2.Store(dst + (2 * stride), Sse2.UnpackLow(b1, b5).AsInt16());
        Sse2.Store(dst + (3 * stride), Sse2.UnpackHigh(b1, b5).AsInt16());
        Sse2.Store(dst + (4 * stride), Sse2.UnpackLow(b2, b6).AsInt16());
        Sse2.Store(dst + (5 * stride), Sse2.UnpackHigh(b2, b6).AsInt16());
        Sse2.Store(dst + (6 * stride), Sse2.UnpackLow(b3, b7).AsInt16());
        Sse2.Store(dst + (7 * stride), Sse2.UnpackHigh(b3, b7).AsInt16());
    }

    // dct: pass 1 rows of the residual, pass 2 rows of the transposed result, the output transposed back
    internal static void DctSimd(int sizeIdx, short* src, short* dst, long srcStride)
    {
        int n = 4 << sizeIdx;
        short* a = stackalloc short[32 * 32];
        short* b = stackalloc short[32 * 32];
        int shift1 = sizeIdx + 1 + X265_DEPTH - 8, shift2 = sizeIdx + 8;
        MatRows(src, srcStride, a, n, n, s_dctFwd[sizeIdx], 1 << (shift1 - 1), shift1);
        Transpose16(a, b, n);
        MatRows(b, n, a, n, n, s_dctFwd[sizeIdx], 1 << (shift2 - 1), shift2);
        Transpose16(a, dst, n);
    }

    // idct: pass 1 columns of the coefficients, pass 2 columns of its output, written with dstStride
    internal static void IdctSimd(int sizeIdx, short* src, short* dst, long dstStride)
    {
        int n = 4 << sizeIdx;
        short* a = stackalloc short[32 * 32];
        short* b = stackalloc short[32 * 32];
        const int shift2 = 12 - (X265_DEPTH - 8);
        Transpose16(src, a, n);
        MatRows(a, n, b, n, n, s_dctInv[sizeIdx], 1 << 6, 7);
        Transpose16(b, a, n);
        MatRows(a, n, dst, dstStride, n, s_dctInv[sizeIdx], 1 << (shift2 - 1), shift2);
    }
}
