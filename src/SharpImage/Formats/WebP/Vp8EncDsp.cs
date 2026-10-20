using System.Runtime.CompilerServices;

namespace SharpImage.Formats.WebP;

/// <summary>VP8Matrix (src/enc/vp8i_enc.h): one quantizer's steps, reciprocals, biases, thresholds and sharpening.</summary>
internal sealed class Vp8Matrix
{
    public readonly ushort[] Q = new ushort[16];         // quantizer steps
    public readonly ushort[] IQ = new ushort[16];        // reciprocals, fixed point.
    public readonly uint[] Bias = new uint[16];          // rounding bias
    public readonly uint[] ZThresh = new uint[16];       // value below which a coefficient is zeroed
    public readonly ushort[] Sharpen = new ushort[16];   // frequency boosters for slight sharpening

    public void CopyFrom(Vp8Matrix o)
    {
        o.Q.CopyTo(Q, 0);
        o.IQ.CopyTo(IQ, 0);
        o.Bias.CopyTo(Bias, 0);
        o.ZThresh.CopyTo(ZThresh, 0);
        o.Sharpen.CopyTo(Sharpen, 0);
    }
}

/// <summary>
/// libwebp 1.6.0 src/dsp/enc.c (C reference versions, which the SSE2 / SSE4.1 versions match bit for bit), the
/// TransformWHT and loop filters of src/dsp/dec.c, VP8SSIMGetClipped of src/dsp/ssim.c and the quantizer helpers of
/// src/dsp/quant.h. Blocks live in BPS-strided work buffers addressed by (array, offset).
/// </summary>
internal static class Vp8EncDsp
{
    public const int Bps = 32;
    public const int QFix = 17;
    public const int MaxLevel = 2047;
    public const int MaxCoeffThresh = 31;

    public static int Bias(int b) => b << (QFix - 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int QuantDiv(uint n, uint iQ, uint b) => (int)((n * iQ + b) >> QFix);

    // VP8DspScan
    public static readonly int[] DspScan =
    [
        0 + 0 * Bps, 4 + 0 * Bps, 8 + 0 * Bps, 12 + 0 * Bps,
        0 + 4 * Bps, 4 + 4 * Bps, 8 + 4 * Bps, 12 + 4 * Bps,
        0 + 8 * Bps, 4 + 8 * Bps, 8 + 8 * Bps, 12 + 8 * Bps,
        0 + 12 * Bps, 4 + 12 * Bps, 8 + 12 * Bps, 12 + 12 * Bps,
        0 + 0 * Bps, 4 + 0 * Bps, 0 + 4 * Bps, 4 + 4 * Bps,    // U
        8 + 0 * Bps, 12 + 0 * Bps, 8 + 4 * Bps, 12 + 4 * Bps,  // V
    ];

    public static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    // prediction buffer layout (vp8i_enc.h)
    public const int I16DC16 = 0 * 16 * Bps;
    public const int I16TM16 = I16DC16 + 16;
    public const int I16VE16 = 1 * 16 * Bps;
    public const int I16HE16 = I16VE16 + 16;
    public const int C8DC8 = 2 * 16 * Bps;
    public const int C8TM8 = C8DC8 + 1 * 16;
    public const int C8VE8 = 2 * 16 * Bps + 8 * Bps;
    public const int C8HE8 = C8VE8 + 1 * 16;
    public const int I4DC4 = 3 * 16 * Bps + 0;
    public const int I4TM4 = I4DC4 + 4;
    public const int I4VE4 = I4DC4 + 8;
    public const int I4HE4 = I4DC4 + 12;
    public const int I4RD4 = I4DC4 + 16;
    public const int I4VR4 = I4DC4 + 20;
    public const int I4LD4 = I4DC4 + 24;
    public const int I4VL4 = I4DC4 + 28;
    public const int I4HD4 = 3 * 16 * Bps + 4 * Bps;
    public const int I4HU4 = I4HD4 + 4;
    public const int I4TMP = I4HD4 + 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8b(int v) => (v & ~0xff) == 0 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

    private static int Mul1(int a) => ((a * 20091) >> 16) + a;
    private static int Mul2(int a) => (a * 35468) >> 16;

    // ---- histogram ---------------------------------------------------------------------------------------------------

    /// <summary>CollectHistogram_C + VP8SetHistogramData: returns (max_value, last_non_zero).</summary>
    public static (int MaxValue, int LastNonZero) CollectHistogram(byte[] refBuf, int refOff, byte[] predBuf, int predOff,
        int startBlock, int endBlock)
    {
        Span<int> distribution = stackalloc int[MaxCoeffThresh + 1];
        distribution.Clear();
        Span<short> o = stackalloc short[16];
        for (int j = startBlock; j < endBlock; ++j)
        {
            FTransform(refBuf, refOff + DspScan[j], predBuf, predOff + DspScan[j], o);
            for (int k = 0; k < 16; ++k)
            {
                int v = Math.Abs((int)o[k]) >> 3;
                int clipped = v > MaxCoeffThresh ? MaxCoeffThresh : v;
                ++distribution[clipped];
            }
        }
        int maxValue = 0, lastNonZero = 1;
        for (int k = 0; k <= MaxCoeffThresh; ++k)
        {
            int value = distribution[k];
            if (value > 0)
            {
                if (value > maxValue) maxValue = value;
                lastNonZero = k;
            }
        }
        return (maxValue, lastNonZero);
    }

    // ---- transforms --------------------------------------------------------------------------------------------------

    private static void ITransformOne(byte[] refBuf, int refOff, ReadOnlySpan<short> input, byte[] dst, int dstOff)
    {
        Span<int> c = stackalloc int[16];
        int t = 0, ip = 0;
        for (int i = 0; i < 4; ++i)
        {
            // vertical pass
            int a = input[ip + 0] + input[ip + 8];
            int b = input[ip + 0] - input[ip + 8];
            int cc = Mul2(input[ip + 4]) - Mul1(input[ip + 12]);
            int d = Mul1(input[ip + 4]) + Mul2(input[ip + 12]);
            c[t + 0] = a + d;
            c[t + 1] = b + cc;
            c[t + 2] = b - cc;
            c[t + 3] = a - d;
            t += 4;
            ip++;
        }
        t = 0;
        for (int i = 0; i < 4; ++i)
        {
            // horizontal pass
            int dc = c[t + 0] + 4;
            int a = dc + c[t + 8];
            int b = dc - c[t + 8];
            int cc = Mul2(c[t + 4]) - Mul1(c[t + 12]);
            int d = Mul1(c[t + 4]) + Mul2(c[t + 12]);
            int r = refOff + i * Bps, o = dstOff + i * Bps;
            dst[o + 0] = Clip8b(refBuf[r + 0] + ((a + d) >> 3));
            dst[o + 1] = Clip8b(refBuf[r + 1] + ((b + cc) >> 3));
            dst[o + 2] = Clip8b(refBuf[r + 2] + ((b - cc) >> 3));
            dst[o + 3] = Clip8b(refBuf[r + 3] + ((a - d) >> 3));
            t++;
        }
    }

    /// <summary>ITransform_C.</summary>
    public static void ITransform(byte[] refBuf, int refOff, ReadOnlySpan<short> input, byte[] dst, int dstOff, bool doTwo)
    {
        ITransformOne(refBuf, refOff, input, dst, dstOff);
        if (doTwo) ITransformOne(refBuf, refOff + 4, input.Slice(16), dst, dstOff + 4);
    }

    /// <summary>FTransform_C.</summary>
    public static void FTransform(byte[] src, int srcOff, byte[] refBuf, int refOff, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; ++i, srcOff += Bps, refOff += Bps)
        {
            int d0 = src[srcOff + 0] - refBuf[refOff + 0];   // 9bit dynamic range ([-255,255])
            int d1 = src[srcOff + 1] - refBuf[refOff + 1];
            int d2 = src[srcOff + 2] - refBuf[refOff + 2];
            int d3 = src[srcOff + 3] - refBuf[refOff + 3];
            int a0 = d0 + d3;         // 10b                      [-510,510]
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            tmp[0 + i * 4] = (a0 + a1) * 8;   // 14b                      [-8160,8160]
            tmp[1 + i * 4] = (a2 * 2217 + a3 * 5352 + 1812) >> 9;      // [-7536,7542]
            tmp[2 + i * 4] = (a0 - a1) * 8;
            tmp[3 + i * 4] = (a3 * 2217 - a2 * 5352 + 937) >> 9;
        }
        for (int i = 0; i < 4; ++i)
        {
            int a0 = tmp[0 + i] + tmp[12 + i];  // 15b
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[0 + i] - tmp[12 + i];
            output[0 + i] = (short)((a0 + a1 + 7) >> 4);            // 12b
            output[4 + i] = (short)(((a2 * 2217 + a3 * 5352 + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)((a3 * 2217 - a2 * 5352 + 51000) >> 16);
        }
    }

    /// <summary>FTransform2_C.</summary>
    public static void FTransform2(byte[] src, int srcOff, byte[] refBuf, int refOff, Span<short> output)
    {
        FTransform(src, srcOff, refBuf, refOff, output);
        FTransform(src, srcOff + 4, refBuf, refOff + 4, output.Slice(16));
    }

    /// <summary>FTransformWHT_C: 'input' holds the 16 DC coefficients at stride 16 (tmp[16][16]).</summary>
    public static void FTransformWHT(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        int ip = 0;
        for (int i = 0; i < 4; ++i, ip += 64)
        {
            int a0 = input[ip + 0 * 16] + input[ip + 2 * 16];  // 13b
            int a1 = input[ip + 1 * 16] + input[ip + 3 * 16];
            int a2 = input[ip + 1 * 16] - input[ip + 3 * 16];
            int a3 = input[ip + 0 * 16] - input[ip + 2 * 16];
            tmp[0 + i * 4] = a0 + a1;   // 14b
            tmp[1 + i * 4] = a3 + a2;
            tmp[2 + i * 4] = a3 - a2;
            tmp[3 + i * 4] = a0 - a1;
        }
        for (int i = 0; i < 4; ++i)
        {
            int a0 = tmp[0 + i] + tmp[8 + i];  // 15b
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[0 + i] - tmp[8 + i];
            int b0 = a0 + a1;    // 16b
            int b1 = a3 + a2;
            int b2 = a3 - a2;
            int b3 = a0 - a1;
            output[0 + i] = (short)(b0 >> 1);     // 15b
            output[4 + i] = (short)(b1 >> 1);
            output[8 + i] = (short)(b2 >> 1);
            output[12 + i] = (short)(b3 >> 1);
        }
    }

    /// <summary>TransformWHT_C (src/dsp/dec.c): writes the 16 DCs at stride 16 of 'output'.</summary>
    public static void TransformWHT(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; ++i)
        {
            int a0 = input[0 + i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[0 + i] - input[12 + i];
            tmp[0 + i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }
        int o = 0;
        for (int i = 0; i < 4; ++i)
        {
            int dc = tmp[0 + i * 4] + 3;    // w/ rounder
            int a0 = dc + tmp[3 + i * 4];
            int a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
            int a2 = tmp[1 + i * 4] - tmp[2 + i * 4];
            int a3 = dc - tmp[3 + i * 4];
            output[o + 0] = (short)((a0 + a1) >> 3);
            output[o + 16] = (short)((a3 + a2) >> 3);
            output[o + 32] = (short)((a0 - a1) >> 3);
            output[o + 48] = (short)((a3 - a2) >> 3);
            o += 64;
        }
    }

    // ---- intra predictions -------------------------------------------------------------------------------------------

    private static void Fill(byte[] dst, int d, int value, int size)
    {
        for (int j = 0; j < size; ++j) dst.AsSpan(d + j * Bps, size).Fill((byte)value);
    }

    private static void VerticalPred(byte[] dst, int d, byte[]? top, int t, int size)
    {
        if (top != null)
        {
            for (int j = 0; j < size; ++j) Array.Copy(top, t, dst, d + j * Bps, size);
        }
        else
        {
            Fill(dst, d, 127, size);
        }
    }

    private static void HorizontalPred(byte[] dst, int d, byte[]? left, int l, int size)
    {
        if (left != null)
        {
            for (int j = 0; j < size; ++j) dst.AsSpan(d + j * Bps, size).Fill(left[l + j]);
        }
        else
        {
            Fill(dst, d, 129, size);
        }
    }

    private static void TrueMotion(byte[] dst, int d, byte[]? left, int l, byte[]? top, int t, int size)
    {
        if (left != null)
        {
            if (top != null)
            {
                int tl = left[l - 1];
                for (int y = 0; y < size; ++y)
                {
                    int lv = left[l + y] - tl;
                    for (int x = 0; x < size; ++x) dst[d + x] = Clip8b(top[t + x] + lv);
                    d += Bps;
                }
            }
            else
            {
                HorizontalPred(dst, d, left, l, size);
            }
        }
        else
        {
            if (top != null)
                VerticalPred(dst, d, top, t, size);
            else
                Fill(dst, d, 129, size);
        }
    }

    private static void DCMode(byte[] dst, int d, byte[]? left, int l, byte[]? top, int t, int size, int round, int shift)
    {
        int dc = 0;
        if (top != null)
        {
            for (int j = 0; j < size; ++j) dc += top[t + j];
            if (left != null)
            {
                // top and left present
                for (int j = 0; j < size; ++j) dc += left[l + j];
            }
            else
            {
                // top, but no left
                dc += dc;
            }
            dc = (dc + round) >> shift;
        }
        else if (left != null)
        {
            // left but no top
            for (int j = 0; j < size; ++j) dc += left[l + j];
            dc += dc;
            dc = (dc + round) >> shift;
        }
        else
        {
            // no top, no left, nothing.
            dc = 0x80;
        }
        Fill(dst, d, dc, size);
    }

    /// <summary>IntraChromaPreds_C: left points at u_left (v_left is 16 bytes further), top at uv_top.</summary>
    public static void IntraChromaPreds(byte[] dst, int d, byte[]? left, int l, byte[]? top, int t)
    {
        DCMode(dst, C8DC8 + d, left, l, top, t, 8, 8, 4);
        VerticalPred(dst, C8VE8 + d, top, t, 8);
        HorizontalPred(dst, C8HE8 + d, left, l, 8);
        TrueMotion(dst, C8TM8 + d, left, l, top, t, 8);
        // V block
        d += 8;
        t += 8;
        l += 16;
        DCMode(dst, C8DC8 + d, left, l, top, t, 8, 8, 4);
        VerticalPred(dst, C8VE8 + d, top, t, 8);
        HorizontalPred(dst, C8HE8 + d, left, l, 8);
        TrueMotion(dst, C8TM8 + d, left, l, top, t, 8);
    }

    /// <summary>Intra16Preds_C.</summary>
    public static void Intra16Preds(byte[] dst, int d, byte[]? left, int l, byte[]? top, int t)
    {
        DCMode(dst, I16DC16 + d, left, l, top, t, 16, 16, 5);
        VerticalPred(dst, I16VE16 + d, top, t, 16);
        HorizontalPred(dst, I16HE16 + d, left, l, 16);
        TrueMotion(dst, I16TM16 + d, left, l, top, t, 16);
    }

    private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    /// <summary>Intra4Preds_C: 'top' is the i4 boundary pointer (top[-5..7] valid).</summary>
    public static void Intra4Preds(byte[] dst, int d, byte[] top, int t)
    {
        int X = top[t - 1], I = top[t - 2], J = top[t - 3], K = top[t - 4], L = top[t - 5];
        int A = top[t + 0], B = top[t + 1], C = top[t + 2], D = top[t + 3];
        int E = top[t + 4], F = top[t + 5], G = top[t + 6], H = top[t + 7];

        // DC4
        {
            uint dc = 4;
            for (int i = 0; i < 4; ++i) dc += (uint)(top[t + i] + top[t - 5 + i]);
            Fill(dst, I4DC4 + d, (int)(dc >> 3), 4);
        }
        // TM4
        {
            int o = I4TM4 + d;
            for (int y = 0; y < 4; ++y)
            {
                int lv = top[t - 2 - y] - X;
                for (int x = 0; x < 4; ++x) dst[o + x] = Clip8b(top[t + x] + lv);
                o += Bps;
            }
        }
        // VE4
        {
            int o = I4VE4 + d;
            byte v0 = Avg3(X, A, B), v1 = Avg3(A, B, C), v2 = Avg3(B, C, D), v3 = Avg3(C, D, E);
            for (int i = 0; i < 4; ++i)
            {
                dst[o + i * Bps + 0] = v0;
                dst[o + i * Bps + 1] = v1;
                dst[o + i * Bps + 2] = v2;
                dst[o + i * Bps + 3] = v3;
            }
        }
        // HE4
        {
            int o = I4HE4 + d;
            dst.AsSpan(o + 0 * Bps, 4).Fill(Avg3(X, I, J));
            dst.AsSpan(o + 1 * Bps, 4).Fill(Avg3(I, J, K));
            dst.AsSpan(o + 2 * Bps, 4).Fill(Avg3(J, K, L));
            dst.AsSpan(o + 3 * Bps, 4).Fill(Avg3(K, L, L));
        }
        // RD4
        {
            int o = I4RD4 + d;
            dst[o + 0 + 3 * Bps] = Avg3(J, K, L);
            dst[o + 0 + 2 * Bps] = dst[o + 1 + 3 * Bps] = Avg3(I, J, K);
            dst[o + 0 + 1 * Bps] = dst[o + 1 + 2 * Bps] = dst[o + 2 + 3 * Bps] = Avg3(X, I, J);
            dst[o + 0 + 0 * Bps] = dst[o + 1 + 1 * Bps] = dst[o + 2 + 2 * Bps] = dst[o + 3 + 3 * Bps] = Avg3(A, X, I);
            dst[o + 1 + 0 * Bps] = dst[o + 2 + 1 * Bps] = dst[o + 3 + 2 * Bps] = Avg3(B, A, X);
            dst[o + 2 + 0 * Bps] = dst[o + 3 + 1 * Bps] = Avg3(C, B, A);
            dst[o + 3 + 0 * Bps] = Avg3(D, C, B);
        }
        // VR4
        {
            int o = I4VR4 + d;
            dst[o + 0 + 0 * Bps] = dst[o + 1 + 2 * Bps] = Avg2(X, A);
            dst[o + 1 + 0 * Bps] = dst[o + 2 + 2 * Bps] = Avg2(A, B);
            dst[o + 2 + 0 * Bps] = dst[o + 3 + 2 * Bps] = Avg2(B, C);
            dst[o + 3 + 0 * Bps] = Avg2(C, D);
            dst[o + 0 + 3 * Bps] = Avg3(K, J, I);
            dst[o + 0 + 2 * Bps] = Avg3(J, I, X);
            dst[o + 0 + 1 * Bps] = dst[o + 1 + 3 * Bps] = Avg3(I, X, A);
            dst[o + 1 + 1 * Bps] = dst[o + 2 + 3 * Bps] = Avg3(X, A, B);
            dst[o + 2 + 1 * Bps] = dst[o + 3 + 3 * Bps] = Avg3(A, B, C);
            dst[o + 3 + 1 * Bps] = Avg3(B, C, D);
        }
        // LD4
        {
            int o = I4LD4 + d;
            dst[o + 0 + 0 * Bps] = Avg3(A, B, C);
            dst[o + 1 + 0 * Bps] = dst[o + 0 + 1 * Bps] = Avg3(B, C, D);
            dst[o + 2 + 0 * Bps] = dst[o + 1 + 1 * Bps] = dst[o + 0 + 2 * Bps] = Avg3(C, D, E);
            dst[o + 3 + 0 * Bps] = dst[o + 2 + 1 * Bps] = dst[o + 1 + 2 * Bps] = dst[o + 0 + 3 * Bps] = Avg3(D, E, F);
            dst[o + 3 + 1 * Bps] = dst[o + 2 + 2 * Bps] = dst[o + 1 + 3 * Bps] = Avg3(E, F, G);
            dst[o + 3 + 2 * Bps] = dst[o + 2 + 3 * Bps] = Avg3(F, G, H);
            dst[o + 3 + 3 * Bps] = Avg3(G, H, H);
        }
        // VL4
        {
            int o = I4VL4 + d;
            dst[o + 0 + 0 * Bps] = Avg2(A, B);
            dst[o + 1 + 0 * Bps] = dst[o + 0 + 2 * Bps] = Avg2(B, C);
            dst[o + 2 + 0 * Bps] = dst[o + 1 + 2 * Bps] = Avg2(C, D);
            dst[o + 3 + 0 * Bps] = dst[o + 2 + 2 * Bps] = Avg2(D, E);
            dst[o + 0 + 1 * Bps] = Avg3(A, B, C);
            dst[o + 1 + 1 * Bps] = dst[o + 0 + 3 * Bps] = Avg3(B, C, D);
            dst[o + 2 + 1 * Bps] = dst[o + 1 + 3 * Bps] = Avg3(C, D, E);
            dst[o + 3 + 1 * Bps] = dst[o + 2 + 3 * Bps] = Avg3(D, E, F);
            dst[o + 3 + 2 * Bps] = Avg3(E, F, G);
            dst[o + 3 + 3 * Bps] = Avg3(F, G, H);
        }
        // HD4
        {
            int o = I4HD4 + d;
            dst[o + 0 + 0 * Bps] = dst[o + 2 + 1 * Bps] = Avg2(I, X);
            dst[o + 0 + 1 * Bps] = dst[o + 2 + 2 * Bps] = Avg2(J, I);
            dst[o + 0 + 2 * Bps] = dst[o + 2 + 3 * Bps] = Avg2(K, J);
            dst[o + 0 + 3 * Bps] = Avg2(L, K);
            dst[o + 3 + 0 * Bps] = Avg3(A, B, C);
            dst[o + 2 + 0 * Bps] = Avg3(X, A, B);
            dst[o + 1 + 0 * Bps] = dst[o + 3 + 1 * Bps] = Avg3(I, X, A);
            dst[o + 1 + 1 * Bps] = dst[o + 3 + 2 * Bps] = Avg3(J, I, X);
            dst[o + 1 + 2 * Bps] = dst[o + 3 + 3 * Bps] = Avg3(K, J, I);
            dst[o + 1 + 3 * Bps] = Avg3(L, K, J);
        }
        // HU4
        {
            int o = I4HU4 + d;
            dst[o + 0 + 0 * Bps] = Avg2(I, J);
            dst[o + 2 + 0 * Bps] = dst[o + 0 + 1 * Bps] = Avg2(J, K);
            dst[o + 2 + 1 * Bps] = dst[o + 0 + 2 * Bps] = Avg2(K, L);
            dst[o + 1 + 0 * Bps] = Avg3(I, J, K);
            dst[o + 3 + 0 * Bps] = dst[o + 1 + 1 * Bps] = Avg3(J, K, L);
            dst[o + 3 + 1 * Bps] = dst[o + 1 + 2 * Bps] = Avg3(K, L, L);
            dst[o + 3 + 2 * Bps] = dst[o + 2 + 2 * Bps] =
                dst[o + 0 + 3 * Bps] = dst[o + 1 + 3 * Bps] = dst[o + 2 + 3 * Bps] = dst[o + 3 + 3 * Bps] = (byte)L;
        }
    }

    // ---- metrics -----------------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSse(byte[] a, int ao, byte[] b, int bo, int w, int h)
    {
        int count = 0;
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                int diff = a[ao + x] - b[bo + x];
                count += diff * diff;
            }
            ao += Bps;
            bo += Bps;
        }
        return count;
    }

    public static int Sse16x16(byte[] a, int ao, byte[] b, int bo) => GetSse(a, ao, b, bo, 16, 16);
    public static int Sse16x8(byte[] a, int ao, byte[] b, int bo) => GetSse(a, ao, b, bo, 16, 8);
    public static int Sse8x8(byte[] a, int ao, byte[] b, int bo) => GetSse(a, ao, b, bo, 8, 8);
    public static int Sse4x4(byte[] a, int ao, byte[] b, int bo) => GetSse(a, ao, b, bo, 4, 4);

    /// <summary>Mean16x4_C.</summary>
    public static void Mean16x4(byte[] refBuf, int r, Span<uint> dc)
    {
        for (int k = 0; k < 4; ++k)
        {
            uint avg = 0;
            for (int y = 0; y < 4; ++y)
                for (int x = 0; x < 4; ++x)
                    avg += refBuf[r + x + y * Bps];
            dc[k] = avg;
            r += 4;   // go to next 4x4 block.
        }
    }

    private static int TTransform(byte[] input, int ip, ReadOnlySpan<ushort> w)
    {
        int sum = 0;
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; ++i, ip += Bps)
        {
            int a0 = input[ip + 0] + input[ip + 2];
            int a1 = input[ip + 1] + input[ip + 3];
            int a2 = input[ip + 1] - input[ip + 3];
            int a3 = input[ip + 0] - input[ip + 2];
            tmp[0 + i * 4] = a0 + a1;
            tmp[1 + i * 4] = a3 + a2;
            tmp[2 + i * 4] = a3 - a2;
            tmp[3 + i * 4] = a0 - a1;
        }
        for (int i = 0; i < 4; ++i)
        {
            int a0 = tmp[0 + i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[0 + i] - tmp[8 + i];
            int b0 = a0 + a1;
            int b1 = a3 + a2;
            int b2 = a3 - a2;
            int b3 = a0 - a1;
            sum += w[i + 0] * Math.Abs(b0);
            sum += w[i + 4] * Math.Abs(b1);
            sum += w[i + 8] * Math.Abs(b2);
            sum += w[i + 12] * Math.Abs(b3);
        }
        return sum;
    }

    /// <summary>Disto4x4_C.</summary>
    public static int TDisto4x4(byte[] a, int ao, byte[] b, int bo, ReadOnlySpan<ushort> w)
    {
        int sum1 = TTransform(a, ao, w);
        int sum2 = TTransform(b, bo, w);
        return Math.Abs(sum2 - sum1) >> 5;
    }

    /// <summary>Disto16x16_C.</summary>
    public static int TDisto16x16(byte[] a, int ao, byte[] b, int bo, ReadOnlySpan<ushort> w)
    {
        int d = 0;
        for (int y = 0; y < 16 * Bps; y += 4 * Bps)
            for (int x = 0; x < 16; x += 4)
                d += TDisto4x4(a, ao + x + y, b, bo + x + y, w);
        return d;
    }

    // ---- quantization ------------------------------------------------------------------------------------------------

    /// <summary>QuantizeBlock_C (also VP8EncQuantizeBlockWHT).</summary>
    public static int QuantizeBlock(Span<short> input, Span<short> output, Vp8Matrix mtx)
    {
        int last = -1;
        for (int n = 0; n < 16; ++n)
        {
            int j = Zigzag[n];
            bool sign = input[j] < 0;
            uint coeff = (uint)(sign ? -input[j] : input[j]) + mtx.Sharpen[j];
            if (coeff > mtx.ZThresh[j])
            {
                uint q = mtx.Q[j];
                uint iQ = mtx.IQ[j];
                uint b = mtx.Bias[j];
                int level = QuantDiv(coeff, iQ, b);
                if (level > MaxLevel) level = MaxLevel;
                if (sign) level = -level;
                input[j] = (short)(level * (int)q);
                output[n] = (short)level;
                if (level != 0) last = n;
            }
            else
            {
                output[n] = 0;
                input[j] = 0;
            }
        }
        return last >= 0 ? 1 : 0;
    }

    /// <summary>Quantize2Blocks_C.</summary>
    public static int Quantize2Blocks(Span<short> input, Span<short> output, Vp8Matrix mtx)
    {
        int nz = QuantizeBlock(input.Slice(0, 16), output.Slice(0, 16), mtx) << 0;
        nz |= QuantizeBlock(input.Slice(16, 16), output.Slice(16, 16), mtx) << 1;
        return nz;
    }

    /// <summary>IsFlat_C (src/dsp/quant.h).</summary>
    public static bool IsFlat(ReadOnlySpan<short> levels, int numBlocks, int thresh)
    {
        int score = 0;
        int o = 0;
        while (numBlocks-- > 0)
        {
            for (int i = 1; i < 16; ++i)
            {
                // omit DC, we're only interested in AC
                score += levels[o + i] != 0 ? 1 : 0;
                if (score > thresh) return false;
            }
            o += 16;
        }
        return true;
    }

    /// <summary>IsFlatSource16 (src/dsp/quant.h).</summary>
    public static bool IsFlatSource16(byte[] src, int s)
    {
        byte v = src[s];
        for (int i = 0; i < 16; ++i)
        {
            for (int x = 0; x < 16; ++x)
                if (src[s + x] != v) return false;
            s += Bps;
        }
        return true;
    }

    public static void Copy(byte[] src, int s, byte[] dst, int d, int w, int h)
    {
        for (int y = 0; y < h; ++y)
        {
            Array.Copy(src, s, dst, d, w);
            s += Bps;
            d += Bps;
        }
    }

    // ---- loop filters (src/dsp/dec.c), used by the autofilter search -------------------------------------------------

    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;   // VP8ksclip1: [-1020,1020] -> [-128,127]
    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;       // VP8ksclip2: [-112,112] -> [-16,15]
    private static byte Clip1(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    private static void DoFilter2(byte[] p, int o, int step)
    {
        int p1 = p[o - 2 * step], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        int a = 3 * (q0 - p0) + SClip1(p1 - q1);  // in [-893,892]
        int a1 = SClip2((a + 4) >> 3);            // in [-16,15]
        int a2 = SClip2((a + 3) >> 3);
        p[o - step] = Clip1(p0 + a2);
        p[o] = Clip1(q0 - a1);
    }

    private static void DoFilter4(byte[] p, int o, int step)
    {
        int p1 = p[o - 2 * step], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        p[o - 2 * step] = Clip1(p1 + a3);
        p[o - step] = Clip1(p0 + a2);
        p[o] = Clip1(q0 - a1);
        p[o + step] = Clip1(q1 - a3);
    }

    private static void DoFilter6(byte[] p, int o, int step)
    {
        int p2 = p[o - 3 * step], p1 = p[o - 2 * step], p0 = p[o - step];
        int q0 = p[o], q1 = p[o + step], q2 = p[o + 2 * step];
        int a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
        int a1 = (27 * a + 63) >> 7;
        int a2 = (18 * a + 63) >> 7;
        int a3 = (9 * a + 63) >> 7;
        p[o - 3 * step] = Clip1(p2 + a3);
        p[o - 2 * step] = Clip1(p1 + a2);
        p[o - step] = Clip1(p0 + a1);
        p[o] = Clip1(q0 - a1);
        p[o + step] = Clip1(q1 - a2);
        p[o + 2 * step] = Clip1(q2 - a3);
    }

    private static bool Hev(byte[] p, int o, int step, int thresh)
    {
        int p1 = p[o - 2 * step], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        return Math.Abs(p1 - p0) > thresh || Math.Abs(q1 - q0) > thresh;
    }

    private static bool NeedsFilter(byte[] p, int o, int step, int t)
    {
        int p1 = p[o - 2 * step], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        return 4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) <= t;
    }

    private static bool NeedsFilter2(byte[] p, int o, int step, int t, int it)
    {
        int p3 = p[o - 4 * step], p2 = p[o - 3 * step], p1 = p[o - 2 * step];
        int p0 = p[o - step], q0 = p[o];
        int q1 = p[o + step], q2 = p[o + 2 * step], q3 = p[o + 3 * step];
        if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > t) return false;
        return Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it &&
               Math.Abs(p1 - p0) <= it && Math.Abs(q3 - q2) <= it &&
               Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
    }

    private static void SimpleVFilter16(byte[] p, int o, int stride, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; ++i)
            if (NeedsFilter(p, o + i, stride, thresh2)) DoFilter2(p, o + i, stride);
    }

    private static void SimpleHFilter16(byte[] p, int o, int stride, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; ++i)
            if (NeedsFilter(p, o + i * stride, 1, thresh2)) DoFilter2(p, o + i * stride, 1);
    }

    public static void SimpleVFilter16i(byte[] p, int o, int stride, int thresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4 * stride;
            SimpleVFilter16(p, o, stride, thresh);
        }
    }

    public static void SimpleHFilter16i(byte[] p, int o, int stride, int thresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4;
            SimpleHFilter16(p, o, stride, thresh);
        }
    }

    private static void FilterLoop24(byte[] p, int o, int hstride, int vstride, int size, int thresh, int ithresh,
        int hevThresh)
    {
        int thresh2 = 2 * thresh + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, o, hstride, thresh2, ithresh))
            {
                if (Hev(p, o, hstride, hevThresh))
                    DoFilter2(p, o, hstride);
                else
                    DoFilter4(p, o, hstride);
            }
            o += vstride;
        }
    }

    public static void VFilter16i(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4 * stride;
            FilterLoop24(p, o, stride, 1, 16, thresh, ithresh, hevThresh);
        }
    }

    public static void HFilter16i(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4;
            FilterLoop24(p, o, 1, stride, 16, thresh, ithresh, hevThresh);
        }
    }

    public static void VFilter8i(byte[] p, int u, int v, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop24(p, u + 4 * stride, stride, 1, 8, thresh, ithresh, hevThresh);
        FilterLoop24(p, v + 4 * stride, stride, 1, 8, thresh, ithresh, hevThresh);
    }

    public static void HFilter8i(byte[] p, int u, int v, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop24(p, u + 4, 1, stride, 8, thresh, ithresh, hevThresh);
        FilterLoop24(p, v + 4, 1, stride, 8, thresh, ithresh, hevThresh);
    }

    // ---- SSIM (src/dsp/ssim.c) ---------------------------------------------------------------------------------------

    public const int SsimKernel = 3;
    private static readonly uint[] SsimWeight = [1, 2, 3, 4, 3, 2, 1];

    /// <summary>SSIMGetClipped_C + VP8SSIMFromStatsClipped.</summary>
    public static double SsimGetClipped(byte[] src1, int o1, int stride1, byte[] src2, int o2, int stride2,
        int xo, int yo, int W, int H)
    {
        uint sw = 0, xm = 0, ym = 0, xxm = 0, xym = 0, yym = 0;
        int ymin = yo - SsimKernel < 0 ? 0 : yo - SsimKernel;
        int ymax = yo + SsimKernel > H - 1 ? H - 1 : yo + SsimKernel;
        int xmin = xo - SsimKernel < 0 ? 0 : xo - SsimKernel;
        int xmax = xo + SsimKernel > W - 1 ? W - 1 : xo + SsimKernel;
        o1 += ymin * stride1;
        o2 += ymin * stride2;
        for (int y = ymin; y <= ymax; ++y, o1 += stride1, o2 += stride2)
        {
            for (int x = xmin; x <= xmax; ++x)
            {
                uint w = SsimWeight[SsimKernel + x - xo] * SsimWeight[SsimKernel + y - yo];
                uint s1 = src1[o1 + x];
                uint s2 = src2[o2 + x];
                sw += w;
                xm += w * s1;
                ym += w * s2;
                xxm += w * s1 * s1;
                xym += w * s1 * s2;
                yym += w * s2 * s2;
            }
        }
        // SSIMCalculation(stats, stats->w)
        uint n = sw;
        uint w2 = n * n;
        uint c1 = 20 * w2;
        uint c2 = 60 * w2;
        uint c3 = 8 * 8 * w2;   // 'dark' limit ~= 6
        ulong xmxm = (ulong)xm * xm;
        ulong ymym = (ulong)ym * ym;
        if (xmxm + ymym >= c3)
        {
            long xmym = (long)xm * ym;
            long sxy = (long)xym * n - xmym;    // can be negative
            ulong sxx = (ulong)xxm * n - xmxm;
            ulong syy = (ulong)yym * n - ymym;
            ulong numS = (2 * (ulong)(sxy < 0 ? 0 : sxy) + c2) >> 8;
            ulong denS = (sxx + syy + c2) >> 8;
            ulong fnum = (ulong)(2 * xmym + c1) * numS;
            ulong fden = (xmxm + ymym + c1) * denS;
            return (double)fnum / fden;
        }
        return 1.0;   // area is too dark to contribute meaningfully
    }
}
