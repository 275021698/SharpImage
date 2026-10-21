// Port of the x265 C primitives the intra encoder uses (common/pixel.cpp, dct.cpp, intrapred.cpp): SAD / SSE /
// SATD / SA8D (x264's SWAR form, kept exact: 16-bit lanes in a 32-bit word), psy cost, residual / reconstruction
// helpers, the forward and inverse DCT / DST, quantizers, the entropy cost kernels and intra prediction.
// 8-bit build: pixel = byte, sum_t = ushort, sum2_t = uint, sse_t = uint; HIGH_BIT_DEPTH: ushort, uint, ulong, ulong.
using System;
using System.Runtime.CompilerServices;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;
#if HIGH_BIT_DEPTH
using pixel = ushort;
using sum_t = uint;
using sum2_t = ulong;
using sse_t = ulong;
#else
using pixel = byte;
using sum_t = ushort;
using sum2_t = uint;
using sse_t = uint;
#endif

[SkipLocalsInit]
internal static unsafe partial class X265Primitives
{
    private const int BITS_PER_SUM = 8 * sizeof(sum_t);

    // ---- pixel comparisons ----

    public static int Sad(pixel* pix1, long stride1, pixel* pix2, long stride2, int lx, int ly)
    {
        int sum = 0;
        for (int y = 0; y < ly; y++)
        {
            for (int x = 0; x < lx; x++)
            {
                sum += Math.Abs(pix1[x] - pix2[x]);
            }

            pix1 += stride1;
            pix2 += stride2;
        }

        return sum;
    }

    internal static sse_t SsePPScalar(pixel* pix1, long stride1, pixel* pix2, long stride2, int lx, int ly)
    {
        sse_t sum = 0;
        for (int y = 0; y < ly; y++)
        {
            for (int x = 0; x < lx; x++)
            {
                int tmp = pix1[x] - pix2[x];
                sum += (sse_t)(tmp * tmp);
            }

            pix1 += stride1;
            pix2 += stride2;
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Hadamard4(out sum2_t d0, out sum2_t d1, out sum2_t d2, out sum2_t d3, sum2_t s0, sum2_t s1, sum2_t s2, sum2_t s3)
    {
        sum2_t t0 = s0 + s1;
        sum2_t t1 = s0 - s1;
        sum2_t t2 = s2 + s3;
        sum2_t t3 = s2 - s3;
        d0 = t0 + t2;
        d2 = t0 - t2;
        d1 = t1 + t3;
        d3 = t1 - t3;
    }

    /// <summary>abs2: |x| + (|y| &lt;&lt; BITS_PER_SUM) of a pseudo-SIMD x + (y &lt;&lt; BITS_PER_SUM).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sum2_t Abs2(sum2_t a)
    {
        sum2_t s = ((a >> (BITS_PER_SUM - 1)) & (((sum2_t)1 << BITS_PER_SUM) + 1)) * sum_t.MaxValue;
        return (a + s) ^ s;
    }

    internal static int Satd4x4Scalar(pixel* pix1, long stride1, pixel* pix2, long stride2)
    {
        sum2_t* tmp = stackalloc sum2_t[8];
        sum2_t sum = 0;
        for (int i = 0; i < 4; i++, pix1 += stride1, pix2 += stride2)
        {
            sum2_t a0 = (sum2_t)(pix1[0] - pix2[0]);
            sum2_t a1 = (sum2_t)(pix1[1] - pix2[1]);
            sum2_t b0 = (a0 + a1) + ((a0 - a1) << BITS_PER_SUM);
            sum2_t a2 = (sum2_t)(pix1[2] - pix2[2]);
            sum2_t a3 = (sum2_t)(pix1[3] - pix2[3]);
            sum2_t b1 = (a2 + a3) + ((a2 - a3) << BITS_PER_SUM);
            tmp[(i * 2) + 0] = b0 + b1;
            tmp[(i * 2) + 1] = b0 - b1;
        }

        for (int i = 0; i < 2; i++)
        {
            Hadamard4(out sum2_t a0, out sum2_t a1, out sum2_t a2, out sum2_t a3, tmp[i], tmp[2 + i], tmp[4 + i], tmp[6 + i]);
            a0 = Abs2(a0) + Abs2(a1) + Abs2(a2) + Abs2(a3);
            sum += (sum_t)a0 + (a0 >> BITS_PER_SUM);
        }

        return (int)(sum >> 1);
    }

    internal static int Sa8d8x8RawScalar(pixel* pix1, long i1, pixel* pix2, long i2)
    {
        sum2_t* tmp = stackalloc sum2_t[32];
        sum2_t sum = 0;
        for (int i = 0; i < 8; i++, pix1 += i1, pix2 += i2)
        {
            sum2_t a0 = (sum2_t)(pix1[0] - pix2[0]);
            sum2_t a1 = (sum2_t)(pix1[1] - pix2[1]);
            sum2_t b0 = (a0 + a1) + ((a0 - a1) << BITS_PER_SUM);
            sum2_t a2 = (sum2_t)(pix1[2] - pix2[2]);
            sum2_t a3 = (sum2_t)(pix1[3] - pix2[3]);
            sum2_t b1 = (a2 + a3) + ((a2 - a3) << BITS_PER_SUM);
            sum2_t a4 = (sum2_t)(pix1[4] - pix2[4]);
            sum2_t a5 = (sum2_t)(pix1[5] - pix2[5]);
            sum2_t b2 = (a4 + a5) + ((a4 - a5) << BITS_PER_SUM);
            sum2_t a6 = (sum2_t)(pix1[6] - pix2[6]);
            sum2_t a7 = (sum2_t)(pix1[7] - pix2[7]);
            sum2_t b3 = (a6 + a7) + ((a6 - a7) << BITS_PER_SUM);
            Hadamard4(out tmp[(i * 4) + 0], out tmp[(i * 4) + 1], out tmp[(i * 4) + 2], out tmp[(i * 4) + 3], b0, b1, b2, b3);
        }

        for (int i = 0; i < 4; i++)
        {
            Hadamard4(out sum2_t a0, out sum2_t a1, out sum2_t a2, out sum2_t a3, tmp[i], tmp[4 + i], tmp[8 + i], tmp[12 + i]);
            Hadamard4(out sum2_t a4, out sum2_t a5, out sum2_t a6, out sum2_t a7, tmp[16 + i], tmp[20 + i], tmp[24 + i], tmp[28 + i]);
            sum2_t b0 = Abs2(a0 + a4) + Abs2(a0 - a4);
            b0 += Abs2(a1 + a5) + Abs2(a1 - a5);
            b0 += Abs2(a2 + a6) + Abs2(a2 - a6);
            b0 += Abs2(a3 + a7) + Abs2(a3 - a7);
            sum += (sum_t)b0 + (b0 >> BITS_PER_SUM);
        }

        return (int)sum;
    }

    public static int Sa8d8x8(pixel* pix1, long i1, pixel* pix2, long i2) => (Sa8d8x8Raw(pix1, i1, pix2, i2) + 2) >> 2;

    private static int Sa8d16x16(pixel* pix1, long i1, pixel* pix2, long i2)
    {
        int sum = Sa8d8x8Raw(pix1, i1, pix2, i2)
                  + Sa8d8x8Raw(pix1 + 8, i1, pix2 + 8, i2)
                  + Sa8d8x8Raw(pix1 + (8 * i1), i1, pix2 + (8 * i2), i2)
                  + Sa8d8x8Raw(pix1 + 8 + (8 * i1), i1, pix2 + 8 + (8 * i2), i2);
        return (sum + 2) >> 2;
    }

    /// <summary>primitives.cu[sizeIdx].sa8d (4x4: satd).</summary>
    public static int Sa8d(int sizeIdx, pixel* pix1, long i1, pixel* pix2, long i2)
    {
        switch (sizeIdx)
        {
            case 0: return Satd4x4(pix1, i1, pix2, i2);
            case 1: return Sa8d8x8(pix1, i1, pix2, i2);
            case 2: return Sa8d16x16(pix1, i1, pix2, i2);
            default:
            {
                int w = 4 << sizeIdx;
                int cost = 0;
                for (int y = 0; y < w; y += 16)
                {
                    for (int x = 0; x < w; x += 16)
                    {
                        cost += Sa8d16x16(pix1 + (i1 * y) + x, i1, pix2 + (i2 * y) + x, i2);
                    }
                }

                return cost;
            }
        }
    }

    private static readonly pixel* ZeroBuf = (pixel*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(64 * sizeof(pixel));

    /// <summary>psyCost_pp: |AC energy(source) - AC energy(recon)| per 8x8 (4x4 uses satd).</summary>
    public static int PsyCostPP(int sizeIdx, pixel* source, long sstride, pixel* recon, long rstride)
    {
        if (sizeIdx != 0)
        {
            int dim = 1 << (sizeIdx + 2);
            uint totEnergy = 0;
            for (int i = 0; i < dim; i += 8)
            {
                for (int j = 0; j < dim; j += 8)
                {
                    int sourceEnergy = Sa8d8x8(source + (i * sstride) + j, sstride, ZeroBuf, 0) - (Sad(source + (i * sstride) + j, sstride, ZeroBuf, 0, 8, 8) >> 2);
                    int reconEnergy = Sa8d8x8(recon + (i * rstride) + j, rstride, ZeroBuf, 0) - (Sad(recon + (i * rstride) + j, rstride, ZeroBuf, 0, 8, 8) >> 2);
                    totEnergy += (uint)Math.Abs(sourceEnergy - reconEnergy);
                }
            }

            return (int)totEnergy;
        }

        int se = Satd4x4(source, sstride, ZeroBuf, 0) - (Sad(source, sstride, ZeroBuf, 0, 4, 4) >> 2);
        int re = Satd4x4(recon, rstride, ZeroBuf, 0) - (Sad(recon, rstride, ZeroBuf, 0, 4, 4) >> 2);
        return Math.Abs(se - re);
    }

    /// <summary>pixel_var: sum + (sum of squares &lt;&lt; 32) of a size x size block.</summary>
    public static ulong PixelVar(pixel* pix, long stride, int size)
    {
        uint sum = 0, sqr = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                sum += pix[x];
                sqr += (uint)(pix[x] * pix[x]);
            }

            pix += stride;
        }

        return sum + ((ulong)sqr << 32);
    }

    // ---- copies and residuals ----

    public static void CopyPP(pixel* dst, long dstStride, pixel* src, long srcStride, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            Buffer.MemoryCopy(src + (y * srcStride), dst + (y * dstStride), w * sizeof(pixel), w * sizeof(pixel));
        }
    }

    public static void CopyPS(short* dst, long dstStride, pixel* src, long srcStride, int n)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                dst[x] = (short)src[x];
            }

            dst += dstStride;
            src += srcStride;
        }
    }

    public static void GetResidual(pixel* fenc, pixel* pred, short* residual, long stride, int n)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                residual[x] = (short)(fenc[x] - pred[x]);
            }

            fenc += stride;
            pred += stride;
            residual += stride;
        }
    }

    public static void AddPS(pixel* a, long dstride, pixel* b0, short* b1, long sstride0, long sstride1, int n)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                a[x] = ClipPixel(b0[x] + b1[x]);
            }

            b0 += sstride0;
            b1 += sstride1;
            a += dstride;
        }
    }

    public static void Transpose(pixel* dst, pixel* src, long stride, int n)
    {
        for (int k = 0; k < n; k++)
        {
            for (int l = 0; l < n; l++)
            {
                dst[(k * n) + l] = src[(l * stride) + k];
            }
        }
    }

    public static void BlockFillS(short* dst, long dstride, short val, int n)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                dst[(y * dstride) + x] = val;
            }
        }
    }

    public static int CountNonzero(short* quantCoeff, int numCoeff)
    {
        int count = 0;
        for (int i = 0; i < numCoeff; i++)
        {
            count += quantCoeff[i] != 0 ? 1 : 0;
        }

        return count;
    }

    // ---- transforms ----

    private static readonly short[] T4 = X265Tables.T4, T8 = X265Tables.T8, T16 = X265Tables.T16, T32 = X265Tables.T32;

    private static void FastForwardDst(short* block, short* coeff, int shift)
    {
        int rnd = 1 << (shift - 1);
        for (int i = 0; i < 4; i++)
        {
            int c0 = block[(4 * i) + 0] + block[(4 * i) + 3];
            int c1 = block[(4 * i) + 1] + block[(4 * i) + 3];
            int c2 = block[(4 * i) + 0] - block[(4 * i) + 1];
            int c3 = 74 * block[(4 * i) + 2];
            coeff[i] = (short)(((29 * c0) + (55 * c1) + c3 + rnd) >> shift);
            coeff[4 + i] = (short)(((74 * (block[(4 * i) + 0] + block[(4 * i) + 1] - block[(4 * i) + 3])) + rnd) >> shift);
            coeff[8 + i] = (short)(((29 * c2) + (55 * c0) - c3 + rnd) >> shift);
            coeff[12 + i] = (short)(((55 * c2) - (29 * c1) + c3 + rnd) >> shift);
        }
    }

    private static void InverseDst(short* tmp, short* block, int shift)
    {
        int rnd = 1 << (shift - 1);
        for (int i = 0; i < 4; i++)
        {
            int c0 = tmp[i] + tmp[8 + i];
            int c1 = tmp[8 + i] + tmp[12 + i];
            int c2 = tmp[i] - tmp[12 + i];
            int c3 = 74 * tmp[4 + i];
            block[(4 * i) + 0] = (short)Clip3(-32768, 32767, ((29 * c0) + (55 * c1) + c3 + rnd) >> shift);
            block[(4 * i) + 1] = (short)Clip3(-32768, 32767, ((55 * c2) - (29 * c1) + c3 + rnd) >> shift);
            block[(4 * i) + 2] = (short)Clip3(-32768, 32767, ((74 * (tmp[i] - tmp[8 + i] + tmp[12 + i])) + rnd) >> shift);
            block[(4 * i) + 3] = (short)Clip3(-32768, 32767, ((55 * c0) + (29 * c2) - c3 + rnd) >> shift);
        }
    }

    private static void PartialButterfly4(short* src, short* dst, int shift, int line)
    {
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            int e0 = src[0] + src[3], o0 = src[0] - src[3];
            int e1 = src[1] + src[2], o1 = src[1] - src[2];
            dst[0] = (short)(((T4[0] * e0) + (T4[1] * e1) + add) >> shift);
            dst[2 * line] = (short)(((T4[8] * e0) + (T4[9] * e1) + add) >> shift);
            dst[line] = (short)(((T4[4] * o0) + (T4[5] * o1) + add) >> shift);
            dst[3 * line] = (short)(((T4[12] * o0) + (T4[13] * o1) + add) >> shift);
            src += 4;
            dst++;
        }
    }

    private static void PartialButterfly8(short* src, short* dst, int shift, int line)
    {
        int* e = stackalloc int[4];
        int* o = stackalloc int[4];
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            for (int k = 0; k < 4; k++)
            {
                e[k] = src[k] + src[7 - k];
                o[k] = src[k] - src[7 - k];
            }

            int ee0 = e[0] + e[3], eo0 = e[0] - e[3];
            int ee1 = e[1] + e[2], eo1 = e[1] - e[2];
            dst[0] = (short)(((T8[0] * ee0) + (T8[1] * ee1) + add) >> shift);
            dst[4 * line] = (short)(((T8[32] * ee0) + (T8[33] * ee1) + add) >> shift);
            dst[2 * line] = (short)(((T8[16] * eo0) + (T8[17] * eo1) + add) >> shift);
            dst[6 * line] = (short)(((T8[48] * eo0) + (T8[49] * eo1) + add) >> shift);
            for (int k = 1; k < 8; k += 2)
            {
                dst[k * line] = (short)(((T8[(k * 8) + 0] * o[0]) + (T8[(k * 8) + 1] * o[1]) + (T8[(k * 8) + 2] * o[2]) + (T8[(k * 8) + 3] * o[3]) + add) >> shift);
            }

            src += 8;
            dst++;
        }
    }

    private static void PartialButterfly16(short* src, short* dst, int shift, int line)
    {
        int* e = stackalloc int[8];
        int* o = stackalloc int[8];
        int* ee = stackalloc int[4];
        int* eo = stackalloc int[4];
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            for (int k = 0; k < 8; k++)
            {
                e[k] = src[k] + src[15 - k];
                o[k] = src[k] - src[15 - k];
            }

            for (int k = 0; k < 4; k++)
            {
                ee[k] = e[k] + e[7 - k];
                eo[k] = e[k] - e[7 - k];
            }

            int eee0 = ee[0] + ee[3], eeo0 = ee[0] - ee[3];
            int eee1 = ee[1] + ee[2], eeo1 = ee[1] - ee[2];
            dst[0] = (short)(((T16[0] * eee0) + (T16[1] * eee1) + add) >> shift);
            dst[8 * line] = (short)(((T16[8 * 16] * eee0) + (T16[(8 * 16) + 1] * eee1) + add) >> shift);
            dst[4 * line] = (short)(((T16[4 * 16] * eeo0) + (T16[(4 * 16) + 1] * eeo1) + add) >> shift);
            dst[12 * line] = (short)(((T16[12 * 16] * eeo0) + (T16[(12 * 16) + 1] * eeo1) + add) >> shift);
            for (int k = 2; k < 16; k += 4)
            {
                int r = k * 16;
                dst[k * line] = (short)(((T16[r] * eo[0]) + (T16[r + 1] * eo[1]) + (T16[r + 2] * eo[2]) + (T16[r + 3] * eo[3]) + add) >> shift);
            }

            for (int k = 1; k < 16; k += 2)
            {
                int r = k * 16;
                dst[k * line] = (short)(((T16[r] * o[0]) + (T16[r + 1] * o[1]) + (T16[r + 2] * o[2]) + (T16[r + 3] * o[3]) +
                                          (T16[r + 4] * o[4]) + (T16[r + 5] * o[5]) + (T16[r + 6] * o[6]) + (T16[r + 7] * o[7]) + add) >> shift);
            }

            src += 16;
            dst++;
        }
    }

    private static void PartialButterfly32(short* src, short* dst, int shift, int line)
    {
        int* e = stackalloc int[16];
        int* o = stackalloc int[16];
        int* ee = stackalloc int[8];
        int* eo = stackalloc int[8];
        int* eee = stackalloc int[4];
        int* eeo = stackalloc int[4];
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            for (int k = 0; k < 16; k++)
            {
                e[k] = src[k] + src[31 - k];
                o[k] = src[k] - src[31 - k];
            }

            for (int k = 0; k < 8; k++)
            {
                ee[k] = e[k] + e[15 - k];
                eo[k] = e[k] - e[15 - k];
            }

            for (int k = 0; k < 4; k++)
            {
                eee[k] = ee[k] + ee[7 - k];
                eeo[k] = ee[k] - ee[7 - k];
            }

            int eeee0 = eee[0] + eee[3], eeeo0 = eee[0] - eee[3];
            int eeee1 = eee[1] + eee[2], eeeo1 = eee[1] - eee[2];
            dst[0] = (short)(((T32[0] * eeee0) + (T32[1] * eeee1) + add) >> shift);
            dst[16 * line] = (short)(((T32[16 * 32] * eeee0) + (T32[(16 * 32) + 1] * eeee1) + add) >> shift);
            dst[8 * line] = (short)(((T32[8 * 32] * eeeo0) + (T32[(8 * 32) + 1] * eeeo1) + add) >> shift);
            dst[24 * line] = (short)(((T32[24 * 32] * eeeo0) + (T32[(24 * 32) + 1] * eeeo1) + add) >> shift);
            for (int k = 4; k < 32; k += 8)
            {
                int r = k * 32;
                dst[k * line] = (short)(((T32[r] * eeo[0]) + (T32[r + 1] * eeo[1]) + (T32[r + 2] * eeo[2]) + (T32[r + 3] * eeo[3]) + add) >> shift);
            }

            for (int k = 2; k < 32; k += 4)
            {
                int r = k * 32;
                dst[k * line] = (short)(((T32[r] * eo[0]) + (T32[r + 1] * eo[1]) + (T32[r + 2] * eo[2]) + (T32[r + 3] * eo[3]) +
                                          (T32[r + 4] * eo[4]) + (T32[r + 5] * eo[5]) + (T32[r + 6] * eo[6]) + (T32[r + 7] * eo[7]) + add) >> shift);
            }

            for (int k = 1; k < 32; k += 2)
            {
                int r = k * 32;
                int s = 0;
                for (int m = 0; m < 16; m++)
                {
                    s += T32[r + m] * o[m];
                }

                dst[k * line] = (short)((s + add) >> shift);
            }

            src += 32;
            dst++;
        }
    }

    private static void PartialButterflyInverse4(short* src, short* dst, int shift, int line)
    {
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            int o0 = (T4[4] * src[line]) + (T4[12] * src[3 * line]);
            int o1 = (T4[5] * src[line]) + (T4[13] * src[3 * line]);
            int e0 = (T4[0] * src[0]) + (T4[8] * src[2 * line]);
            int e1 = (T4[1] * src[0]) + (T4[9] * src[2 * line]);
            dst[0] = (short)Clip3(-32768, 32767, (e0 + o0 + add) >> shift);
            dst[1] = (short)Clip3(-32768, 32767, (e1 + o1 + add) >> shift);
            dst[2] = (short)Clip3(-32768, 32767, (e1 - o1 + add) >> shift);
            dst[3] = (short)Clip3(-32768, 32767, (e0 - o0 + add) >> shift);
            src++;
            dst += 4;
        }
    }

    private static void PartialButterflyInverse8(short* src, short* dst, int shift, int line)
    {
        int* e = stackalloc int[4];
        int* o = stackalloc int[4];
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            for (int k = 0; k < 4; k++)
            {
                o[k] = (T8[8 + k] * src[line]) + (T8[24 + k] * src[3 * line]) + (T8[40 + k] * src[5 * line]) + (T8[56 + k] * src[7 * line]);
            }

            int eo0 = (T8[16] * src[2 * line]) + (T8[48] * src[6 * line]);
            int eo1 = (T8[17] * src[2 * line]) + (T8[49] * src[6 * line]);
            int ee0 = (T8[0] * src[0]) + (T8[32] * src[4 * line]);
            int ee1 = (T8[1] * src[0]) + (T8[33] * src[4 * line]);
            e[0] = ee0 + eo0;
            e[3] = ee0 - eo0;
            e[1] = ee1 + eo1;
            e[2] = ee1 - eo1;
            for (int k = 0; k < 4; k++)
            {
                dst[k] = (short)Clip3(-32768, 32767, (e[k] + o[k] + add) >> shift);
                dst[k + 4] = (short)Clip3(-32768, 32767, (e[3 - k] - o[3 - k] + add) >> shift);
            }

            src++;
            dst += 8;
        }
    }

    private static void PartialButterflyInverse16(short* src, short* dst, int shift, int line)
    {
        int* e = stackalloc int[8];
        int* o = stackalloc int[8];
        int* ee = stackalloc int[4];
        int* eo = stackalloc int[4];
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            for (int k = 0; k < 8; k++)
            {
                o[k] = (T16[16 + k] * src[line]) + (T16[48 + k] * src[3 * line]) + (T16[80 + k] * src[5 * line]) + (T16[112 + k] * src[7 * line]) +
                       (T16[144 + k] * src[9 * line]) + (T16[176 + k] * src[11 * line]) + (T16[208 + k] * src[13 * line]) + (T16[240 + k] * src[15 * line]);
            }

            for (int k = 0; k < 4; k++)
            {
                eo[k] = (T16[32 + k] * src[2 * line]) + (T16[96 + k] * src[6 * line]) + (T16[160 + k] * src[10 * line]) + (T16[224 + k] * src[14 * line]);
            }

            int eeo0 = (T16[64] * src[4 * line]) + (T16[192] * src[12 * line]);
            int eee0 = (T16[0] * src[0]) + (T16[128] * src[8 * line]);
            int eeo1 = (T16[65] * src[4 * line]) + (T16[193] * src[12 * line]);
            int eee1 = (T16[1] * src[0]) + (T16[129] * src[8 * line]);
            ee[0] = eee0 + eeo0;
            ee[2] = eee1 - eeo1;
            ee[1] = eee1 + eeo1;
            ee[3] = eee0 - eeo0;
            for (int k = 0; k < 4; k++)
            {
                e[k] = ee[k] + eo[k];
                e[k + 4] = ee[3 - k] - eo[3 - k];
            }

            for (int k = 0; k < 8; k++)
            {
                dst[k] = (short)Clip3(-32768, 32767, (e[k] + o[k] + add) >> shift);
                dst[k + 8] = (short)Clip3(-32768, 32767, (e[7 - k] - o[7 - k] + add) >> shift);
            }

            src++;
            dst += 16;
        }
    }

    private static void PartialButterflyInverse32(short* src, short* dst, int shift, int line)
    {
        int* e = stackalloc int[16];
        int* o = stackalloc int[16];
        int* ee = stackalloc int[8];
        int* eo = stackalloc int[8];
        int* eee = stackalloc int[4];
        int* eeo = stackalloc int[4];
        int add = 1 << (shift - 1);
        for (int j = 0; j < line; j++)
        {
            for (int k = 0; k < 16; k++)
            {
                int s = 0;
                for (int m = 1; m < 32; m += 2)
                {
                    s += T32[(m * 32) + k] * src[m * line];
                }

                o[k] = s;
            }

            for (int k = 0; k < 8; k++)
            {
                eo[k] = (T32[(2 * 32) + k] * src[2 * line]) + (T32[(6 * 32) + k] * src[6 * line]) + (T32[(10 * 32) + k] * src[10 * line]) + (T32[(14 * 32) + k] * src[14 * line]) +
                        (T32[(18 * 32) + k] * src[18 * line]) + (T32[(22 * 32) + k] * src[22 * line]) + (T32[(26 * 32) + k] * src[26 * line]) + (T32[(30 * 32) + k] * src[30 * line]);
            }

            for (int k = 0; k < 4; k++)
            {
                eeo[k] = (T32[(4 * 32) + k] * src[4 * line]) + (T32[(12 * 32) + k] * src[12 * line]) + (T32[(20 * 32) + k] * src[20 * line]) + (T32[(28 * 32) + k] * src[28 * line]);
            }

            int eeeo0 = (T32[8 * 32] * src[8 * line]) + (T32[24 * 32] * src[24 * line]);
            int eeeo1 = (T32[(8 * 32) + 1] * src[8 * line]) + (T32[(24 * 32) + 1] * src[24 * line]);
            int eeee0 = (T32[0] * src[0]) + (T32[16 * 32] * src[16 * line]);
            int eeee1 = (T32[1] * src[0]) + (T32[(16 * 32) + 1] * src[16 * line]);
            eee[0] = eeee0 + eeeo0;
            eee[3] = eeee0 - eeeo0;
            eee[1] = eeee1 + eeeo1;
            eee[2] = eeee1 - eeeo1;
            for (int k = 0; k < 4; k++)
            {
                ee[k] = eee[k] + eeo[k];
                ee[k + 4] = eee[3 - k] - eeo[3 - k];
            }

            for (int k = 0; k < 8; k++)
            {
                e[k] = ee[k] + eo[k];
                e[k + 8] = ee[7 - k] - eo[7 - k];
            }

            for (int k = 0; k < 16; k++)
            {
                dst[k] = (short)Clip3(-32768, 32767, (e[k] + o[k] + add) >> shift);
                dst[k + 16] = (short)Clip3(-32768, 32767, (e[15 - k] - o[15 - k] + add) >> shift);
            }

            src++;
            dst += 32;
        }
    }

    public static void Dst4(short* src, short* dst, long srcStride)
    {
        short* coef = stackalloc short[16];
        short* block = stackalloc short[16];
        for (int i = 0; i < 4; i++)
        {
            for (int x = 0; x < 4; x++) block[(i * 4) + x] = src[(i * srcStride) + x];
        }

        FastForwardDst(block, coef, 1 + X265_DEPTH - 8);
        FastForwardDst(coef, dst, 8);
    }

    /// <summary>primitives.cu[sizeIdx].dct.</summary>
    public static void Dct(int sizeIdx, short* src, short* dst, long srcStride)
    {
        if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            DctSimd(sizeIdx, src, dst, srcStride);
        }
        else
        {
            DctScalar(sizeIdx, src, dst, srcStride);
        }
    }

    // x265's C partial butterflies
    internal static void DctScalar(int sizeIdx, short* src, short* dst, long srcStride)
    {
        int n = 4 << sizeIdx;
        short* coef = stackalloc short[n * n];
        short* block = stackalloc short[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int x = 0; x < n; x++) block[(i * n) + x] = src[(i * srcStride) + x];
        }

        switch (sizeIdx)
        {
            case 0:
                PartialButterfly4(block, coef, 1 + X265_DEPTH - 8, 4);
                PartialButterfly4(coef, dst, 8, 4);
                break;
            case 1:
                PartialButterfly8(block, coef, 2 + X265_DEPTH - 8, 8);
                PartialButterfly8(coef, dst, 9, 8);
                break;
            case 2:
                PartialButterfly16(block, coef, 3 + X265_DEPTH - 8, 16);
                PartialButterfly16(coef, dst, 10, 16);
                break;
            default:
                PartialButterfly32(block, coef, 4 + X265_DEPTH - 8, 32);
                PartialButterfly32(coef, dst, 11, 32);
                break;
        }
    }

    public static void Idst4(short* src, short* dst, long dstStride)
    {
        short* coef = stackalloc short[16];
        short* block = stackalloc short[16];
        InverseDst(src, coef, 7);
        InverseDst(coef, block, 12 - (X265_DEPTH - 8));
        for (int i = 0; i < 4; i++)
        {
            for (int x = 0; x < 4; x++) dst[(i * dstStride) + x] = block[(i * 4) + x];
        }
    }

    /// <summary>primitives.cu[sizeIdx].idct.</summary>
    public static void Idct(int sizeIdx, short* src, short* dst, long dstStride)
    {
        if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
        {
            IdctSimd(sizeIdx, src, dst, dstStride);
        }
        else
        {
            IdctScalar(sizeIdx, src, dst, dstStride);
        }
    }

    // x265's C partial butterflies
    internal static void IdctScalar(int sizeIdx, short* src, short* dst, long dstStride)
    {
        int n = 4 << sizeIdx;
        short* coef = stackalloc short[n * n];
        short* block = stackalloc short[n * n];
        const int shift2 = 12 - (X265_DEPTH - 8);
        switch (sizeIdx)
        {
            case 0:
                PartialButterflyInverse4(src, coef, 7, 4);
                PartialButterflyInverse4(coef, block, shift2, 4);
                break;
            case 1:
                PartialButterflyInverse8(src, coef, 7, 8);
                PartialButterflyInverse8(coef, block, shift2, 8);
                break;
            case 2:
                PartialButterflyInverse16(src, coef, 7, 16);
                PartialButterflyInverse16(coef, block, shift2, 16);
                break;
            default:
                PartialButterflyInverse32(src, coef, 7, 32);
                PartialButterflyInverse32(coef, block, shift2, 32);
                break;
        }

        for (int i = 0; i < n; i++)
        {
            for (int x = 0; x < n; x++) dst[(i * dstStride) + x] = block[(i * n) + x];
        }
    }

    // ---- quantization ----

    public static void DequantNormal(short* quantCoef, short* coef, int num, int scale, int shift)
    {
        int add = 1 << (shift - 1);
        for (int n = 0; n < num; n++)
        {
            int coeffQ = ((quantCoef[n] * scale) + add) >> shift;
            coef[n] = (short)Clip3(-32768, 32767, coeffQ);
        }
    }

    public static uint Quant(short* coef, int* quantCoeff, int* deltaU, short* qCoef, int qBits, int add, int numCoeff)
    {
        int qBits8 = qBits - 8;
        uint numSig = 0;
        for (int blockpos = 0; blockpos < numCoeff; blockpos++)
        {
            int level = coef[blockpos];
            int sign = level < 0 ? -1 : 1;
            int tmplevel = Math.Abs(level) * quantCoeff[blockpos];
            level = (tmplevel + add) >> qBits;
            deltaU[blockpos] = (tmplevel - (level << qBits)) >> qBits8;
            if (level != 0)
            {
                ++numSig;
            }

            level *= sign;
            qCoef[blockpos] = (short)Clip3(-32768, 32767, level);
        }

        return numSig;
    }

    internal static uint NQuantScalar(short* coef, int* quantCoeff, short* qCoef, int qBits, int add, int numCoeff)
    {
        uint numSig = 0;
        for (int blockpos = 0; blockpos < numCoeff; blockpos++)
        {
            int level = coef[blockpos];
            int sign = level < 0 ? -1 : 1;
            int tmplevel = Math.Abs(level) * quantCoeff[blockpos];
            level = (tmplevel + add) >> qBits;
            if (level != 0)
            {
                ++numSig;
            }

            level *= sign;
            qCoef[blockpos] = (short)Math.Abs(Clip3(-32768, 32767, level));
        }

        return numSig;
    }

    /// <summary>nonPsyRdoQuant_c: uncoded costs of one 4x4 coefficient group.</summary>
    public static void NonPsyRdoQuant(int log2TrSize, short* resiDctCoeff, long* costUncoded, ref long totalUncodedCost, ref long totalRdCost, uint blkPos)
    {
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - log2TrSize;
        int scaleBits = SCALE_BITS - (2 * transformShift);
        uint trSize = 1u << log2TrSize;
        for (int y = 0; y < MLS_CG_SIZE; y++)
        {
            for (int x = 0; x < MLS_CG_SIZE; x++)
            {
                long signCoef = resiDctCoeff[blkPos + x];
                costUncoded[blkPos + x] = (long)(double)((signCoef * signCoef) << scaleBits);
                totalUncodedCost += costUncoded[blkPos + x];
                totalRdCost += costUncoded[blkPos + x];
            }

            blkPos += trSize;
        }
    }

    /// <summary>psyRdoQuant_c_1 then psyRdoQuant_c_2 (x265's non-AVX-512 path, which adds each group's cost to the
    /// totals twice: once without and once with the psy term).</summary>
    public static void PsyRdoQuant12(int log2TrSize, short* resiDctCoeff, short* fencDctCoeff, long* costUncoded, ref long totalUncodedCost,
        ref long totalRdCost, long psyScale, uint blkPos)
    {
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - log2TrSize;
        int scaleBits = SCALE_BITS - (2 * transformShift);
        uint trSize = 1u << log2TrSize;
        int max = Math.Max(0, (2 * transformShift) + 1);
        uint p = blkPos;
        for (int y = 0; y < MLS_CG_SIZE; y++)
        {
            for (int x = 0; x < MLS_CG_SIZE; x++)
            {
                long signCoef = resiDctCoeff[p + x];
                costUncoded[p + x] = (long)(double)((signCoef * signCoef) << scaleBits);
                totalUncodedCost += costUncoded[p + x];
                totalRdCost += costUncoded[p + x];
            }

            p += trSize;
        }

        p = blkPos;
        for (int y = 0; y < MLS_CG_SIZE; y++)
        {
            for (int x = 0; x < MLS_CG_SIZE; x++)
            {
                long signCoef = resiDctCoeff[p + x];
                long predictedCoef = fencDctCoeff[p + x] - signCoef;
                costUncoded[p + x] -= (long)(double)((psyScale * predictedCoef) >> max);
                totalUncodedCost += costUncoded[p + x];
                totalRdCost += costUncoded[p + x];
            }

            p += trSize;
        }
    }

    // ---- entropy cost kernels ----

    public static int ScanPosLast(ushort* scan, short* coeff, ushort* coeffSign, ushort* coeffFlag, byte* coeffNum, int numSig)
    {
        new Span<byte>(coeffNum, MLS_GRP_NUM).Clear();
        new Span<ushort>(coeffFlag, MLS_GRP_NUM).Clear();
        new Span<ushort>(coeffSign, MLS_GRP_NUM).Clear();
        int scanPosLast = 0;
        do
        {
            uint cgIdx = (uint)scanPosLast >> MLS_CG_SIZE;
            uint posLast = scan[scanPosLast++];
            int curCoeff = coeff[posLast];
            uint isNZCoeff = curCoeff != 0 ? 1u : 0u;
            numSig -= (int)isNZCoeff;
            coeffSign[cgIdx] += (ushort)(((uint)curCoeff >> 31) << coeffNum[cgIdx]);
            coeffFlag[cgIdx] = (ushort)((coeffFlag[cgIdx] << 1) + isNZCoeff);
            coeffNum[cgIdx] += (byte)isNZCoeff;
        }
        while (numSig > 0);
        return scanPosLast - 1;
    }

    public static uint FindPosFirstLast(short* dstCoeff, long trSize, ushort* scanTbl)
    {
        int n;
        for (n = SCAN_SET_SIZE - 1; n >= 0; n--)
        {
            uint idx = scanTbl[n];
            if (dstCoeff[((idx / MLS_CG_SIZE) * trSize) + (idx % MLS_CG_SIZE)] != 0)
            {
                break;
            }
        }

        uint lastNZPosInCG = (uint)n;
        for (n = 0; n < SCAN_SET_SIZE; n++)
        {
            uint idx = scanTbl[n];
            if (dstCoeff[((idx / MLS_CG_SIZE) * trSize) + (idx % MLS_CG_SIZE)] != 0)
            {
                break;
            }
        }

        uint firstNZPosInCG = (uint)n;
        uint absSumSign = 0;
        for (n = (int)firstNZPosInCG; n <= (int)lastNZPosInCG; n++)
        {
            uint idx = scanTbl[n];
            absSumSign += (uint)dstCoeff[((idx / MLS_CG_SIZE) * trSize) + (idx % MLS_CG_SIZE)];
        }

        return (absSumSign << 31) | (lastNZPosInCG << 8) | firstNZPosInCG;
    }

    public static uint CostCoeffNxN(ushort* scan, short* coeff, long trSize, ushort* absCoeff, byte* tabSigCtx, uint scanFlagMask,
        byte* baseCtx, int offset, int scanPosSigOff, int subPosBase)
    {
        ushort* tmpCoeff = stackalloc ushort[SCAN_SET_SIZE];
        uint numNonZero = scanPosSigOff < SCAN_SET_SIZE - 1 ? 1u : 0u;
        uint sum = 0;
        absCoeff -= numNonZero;   // correct offset to match assembly
        for (int i = 0; i < MLS_CG_SIZE; i++)
        {
            tmpCoeff[(i * MLS_CG_SIZE) + 0] = (ushort)Math.Abs((int)coeff[(i * trSize) + 0]);
            tmpCoeff[(i * MLS_CG_SIZE) + 1] = (ushort)Math.Abs((int)coeff[(i * trSize) + 1]);
            tmpCoeff[(i * MLS_CG_SIZE) + 2] = (ushort)Math.Abs((int)coeff[(i * trSize) + 2]);
            tmpCoeff[(i * MLS_CG_SIZE) + 3] = (ushort)Math.Abs((int)coeff[(i * trSize) + 3]);
        }

        do
        {
            uint blkPos = scan[scanPosSigOff];
            uint posZeroMask = (subPosBase + scanPosSigOff) != 0 ? ~0u : 0u;
            uint sig = scanFlagMask & 1;
            scanFlagMask >>= 1;
            if (scanPosSigOff != 0 || subPosBase == 0 || numNonZero != 0)
            {
                uint cnt = (uint)(tabSigCtx[blkPos] + offset);
                uint ctxSig = cnt & posZeroMask;
                uint mstate = baseCtx[ctxSig];
                uint mps = mstate & 1;
                uint stateBits = X265Tables.EntropyStateBits[mstate ^ sig];
                uint nextState = (stateBits >> 24) + mps;
                if ((mstate ^ sig) == 1)
                {
                    nextState = sig;
                }

                baseCtx[ctxSig] = (byte)nextState;
                sum += stateBits;
            }

            absCoeff[numNonZero] = tmpCoeff[blkPos];
            numNonZero += sig;
            scanPosSigOff--;
        }
        while (scanPosSigOff >= 0);

        return sum & 0xFFFFFF;
    }

    public static uint CostCoeffRemain(ushort* absCoeff, int numNonZero, int idx)
    {
        uint goRiceParam = 0;
        uint sum = 0;
        int baseLevel = 3;
        do
        {
            if (idx >= C1FLAG_NUMBER)
            {
                baseLevel = 1;
            }

            int codeNumber = absCoeff[idx] - baseLevel;
            if (codeNumber >= 0)
            {
                uint length = 0;
                codeNumber = (int)((uint)codeNumber >> (int)goRiceParam) - COEF_REMAIN_BIN_REDUCTION;
                if (codeNumber >= 0)
                {
                    length = (uint)Bsr((uint)codeNumber + 1);
                    codeNumber = (int)(length + length);
                }

                sum += (uint)(COEF_REMAIN_BIN_REDUCTION + 1 + (int)goRiceParam + codeNumber);
                if (absCoeff[idx] > (COEF_REMAIN_BIN_REDUCTION << (int)goRiceParam))
                {
                    goRiceParam = (goRiceParam + 1) - (goRiceParam >> 2);
                }
            }

            baseLevel = 2;
            idx++;
        }
        while (idx < numNonZero);

        return sum;
    }

    public static uint CostC1C2Flag(ushort* absCoeff, uint numC1Flag, byte* baseCtxMod, int ctxOffset)
    {
        uint sum = 0;
        uint c1 = 1;
        uint firstC2Idx = 8;
        uint firstC2Flag = 2;
        uint c1Next = 0xFFFFFFFE;
        int idx = 0;
        do
        {
            uint symbol1 = absCoeff[idx] > 1 ? 1u : 0u;
            uint symbol2 = absCoeff[idx] > 2 ? 1u : 0u;
            uint mstate = baseCtxMod[c1];
            baseCtxMod[c1] = X265Tables.NextState[(mstate * 2) + symbol1];
            sum += X265Tables.EntropyBits[mstate ^ symbol1];
            if (symbol1 != 0)
            {
                c1Next = 0;
            }

            if (symbol1 + firstC2Flag == 3)
            {
                firstC2Flag = symbol2;
            }

            if (symbol1 + firstC2Idx == 9)
            {
                firstC2Idx = (uint)idx;
            }

            c1 = c1Next & 3;
            c1Next >>= 2;
            idx++;
        }
        while (idx < numC1Flag);

        if (c1 == 0)
        {
            baseCtxMod += ctxOffset;
            uint mstate = baseCtxMod[0];
            baseCtxMod[0] = X265Tables.NextState[(mstate * 2) + firstC2Flag];
            sum += X265Tables.EntropyBits[mstate ^ firstC2Flag];
        }

        return (sum & 0x00FFFFFF) + (c1 << 26) + (firstC2Idx << 28);
    }

    // ---- intra prediction ----

    /// <summary>intraFilter: [1 2 1] filtering of the left and top reference samples.</summary>
    public static void IntraFilter(int tuSize, pixel* samples, pixel* filtered)
    {
        int tuSize2 = tuSize << 1;
        pixel topLeft = samples[0], topLast = samples[tuSize2], leftLast = samples[tuSize2 + tuSize2];
        for (int i = 1; i < tuSize2; i++)
        {
            filtered[i] = (pixel)(((samples[i] << 1) + samples[i - 1] + samples[i + 1] + 2) >> 2);
        }

        filtered[tuSize2] = topLast;
        filtered[0] = (pixel)(((topLeft << 1) + samples[1] + samples[tuSize2 + 1] + 2) >> 2);
        filtered[tuSize2 + 1] = (pixel)(((samples[tuSize2 + 1] << 1) + topLeft + samples[tuSize2 + 2] + 2) >> 2);
        for (int i = tuSize2 + 2; i < tuSize2 + tuSize2; i++)
        {
            filtered[i] = (pixel)(((samples[i] << 1) + samples[i - 1] + samples[i + 1] + 2) >> 2);
        }

        filtered[tuSize2 + tuSize2] = leftLast;
    }

    private static void DcPredFilter(pixel* above, pixel* left, pixel* dst, long dststride, int size)
    {
        dst[0] = (pixel)((above[0] + left[0] + (2 * dst[0]) + 2) >> 2);
        for (int x = 1; x < size; x++)
        {
            dst[x] = (pixel)((above[x] + (3 * dst[x]) + 2) >> 2);
        }

        dst += dststride;
        for (int y = 1; y < size; y++)
        {
            *dst = (pixel)((left[y] + (3 * *dst) + 2) >> 2);
            dst += dststride;
        }
    }

    public static void IntraPredDc(int width, pixel* dst, long dstStride, pixel* srcPix, int bFilter)
    {
        int dcVal = width;
        for (int i = 0; i < width; i++)
        {
            dcVal += srcPix[1 + i] + srcPix[(2 * width) + 1 + i];
        }

        dcVal /= width + width;
        for (int k = 0; k < width; k++)
        {
            for (int l = 0; l < width; l++)
            {
                dst[(k * dstStride) + l] = (pixel)dcVal;
            }
        }

        if (bFilter != 0)
        {
            DcPredFilter(srcPix + 1, srcPix + (2 * width) + 1, dst, dstStride, width);
        }
    }

    public static void PlanarPred(int log2Size, pixel* dst, long dstStride, pixel* srcPix)
    {
        int blkSize = 1 << log2Size;
        pixel* above = srcPix + 1;
        pixel* left = srcPix + (2 * blkSize) + 1;
        int topRight = above[blkSize];
        int bottomLeft = left[blkSize];
        for (int y = 0; y < blkSize; y++)
        {
            for (int x = 0; x < blkSize; x++)
            {
                dst[(y * dstStride) + x] = (pixel)((((blkSize - 1 - x) * left[y]) + ((blkSize - 1 - y) * above[x]) + ((x + 1) * topRight) + ((y + 1) * bottomLeft) + blkSize) >> (log2Size + 1));
            }
        }
    }

    private static readonly sbyte[] AngleTable = { -32, -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32 };
    private static readonly short[] InvAngleTable = { 4096, 1638, 910, 630, 482, 390, 315, 256 };

    public static void IntraPredAng(int width, pixel* dst, long dstStride, pixel* srcPix0, int dirMode, int bFilter)
        => IntraPredAng(width, dst, dstStride, srcPix0, dirMode, bFilter, transposeHorizontal: true);

    // transposeHorizontal false: a horizontal mode's prediction is left transposed (what all_angs_pred stores)
    private static void IntraPredAng(int width, pixel* dst, long dstStride, pixel* srcPix0, int dirMode, int bFilter, bool transposeHorizontal)
    {
        int width2 = width << 1;
        bool horMode = dirMode < 18;
        pixel* neighbourBuf = stackalloc pixel[129];
        pixel* srcPix = srcPix0;
        if (horMode)
        {
            neighbourBuf[0] = srcPix[0];
            for (int i = 0; i < width << 1; i++)
            {
                neighbourBuf[1 + i] = srcPix[width2 + 1 + i];
                neighbourBuf[width2 + 1 + i] = srcPix[1 + i];
            }

            srcPix = neighbourBuf;
        }

        int angleOffset = horMode ? 10 - dirMode : dirMode - 26;
        int angle = AngleTable[8 + angleOffset];
        if (angle == 0)
        {
            for (int y = 0; y < width; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    dst[(y * dstStride) + x] = srcPix[1 + x];
                }
            }

            if (bFilter != 0)
            {
                int topLeft = srcPix[0], top = srcPix[1];
                for (int y = 0; y < width; y++)
                {
                    dst[y * dstStride] = ClipPixel((short)(top + ((srcPix[width2 + 1 + y] - topLeft) >> 1)));
                }
            }
        }
        else
        {
            pixel* refBuf = stackalloc pixel[64];
            pixel* refp;
            if (angle < 0)
            {
                int nbProjected = -((width * angle) >> 5) - 1;
                pixel* refPix = refBuf + nbProjected + 1;
                int invAngle = InvAngleTable[-angleOffset - 1];
                int invAngleSum = 128;
                for (int i = 0; i < nbProjected; i++)
                {
                    invAngleSum += invAngle;
                    refPix[-2 - i] = srcPix[width2 + (invAngleSum >> 8)];
                }

                for (int i = 0; i < width + 1; i++)
                {
                    refPix[-1 + i] = srcPix[i];
                }

                refp = refPix;
            }
            else
            {
                refp = srcPix + 1;
            }

            int angleSum = 0;
            for (int y = 0; y < width; y++)
            {
                angleSum += angle;
                int offset = angleSum >> 5;
                int fraction = angleSum & 31;
                if (fraction != 0)
                {
                    AngularRow(dst + (y * dstStride), refp + offset, fraction, width);
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        dst[(y * dstStride) + x] = refp[offset + x];
                    }
                }
            }
        }

        if (horMode && transposeHorizontal)
        {
            for (int y = 0; y < width - 1; y++)
            {
                for (int x = y + 1; x < width; x++)
                {
                    pixel tmp = dst[(y * dstStride) + x];
                    dst[(y * dstStride) + x] = dst[(x * dstStride) + y];
                    dst[(x * dstStride) + y] = tmp;
                }
            }
        }
    }

    /// <summary>primitives.cu[sizeIdx].intra_pred[dirMode].</summary>
    public static void IntraPred(int sizeIdx, int dirMode, pixel* dst, long dstStride, pixel* srcPix, int bFilter)
    {
        int width = 4 << sizeIdx;
        if (dirMode == PLANAR_IDX)
        {
            PlanarPred(sizeIdx + 2, dst, dstStride, srcPix);
        }
        else if (dirMode == DC_IDX)
        {
            IntraPredDc(width, dst, dstStride, srcPix, bFilter);
        }
        else
        {
            IntraPredAng(width, dst, dstStride, srcPix, dirMode, bFilter);
        }
    }

    /// <summary>all_angs_pred_c: modes 2..34, horizontal modes stored transposed.</summary>
    public static void AllAngsPred(int log2Size, pixel* dest, pixel* refPix, pixel* filtPix, int bLuma)
    {
        int size = 1 << log2Size;
        for (int mode = 2; mode <= 34; mode++)
        {
            pixel* srcPix = (X265Tables.IntraFilterFlags[mode] & size) != 0 ? filtPix : refPix;
            pixel* outp = dest + ((mode - 2) << (log2Size * 2));
            // (all_angs_pred transposes the horizontal modes back: they stay as computed)
            IntraPredAng(size, outp, size, srcPix, mode, bLuma, transposeHorizontal: false);
        }
    }
}
