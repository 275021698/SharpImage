using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.WebP;

/// <summary>
/// libwebp 1.6.0 lossless shared code, ported line by line: format constants, the fixed-point log2 used by every
/// entropy estimate (src/dsp/lossless_common.h, lossless_enc.c), prefix coding, pixel arithmetic, the 14 spatial
/// predictors (src/dsp/lossless.c) and the encoder-side transforms and entropy functions (lossless_enc.c). The
/// SIMD variants libwebp dispatches compute the same integers, so the C versions are the reference.
/// </summary>
internal static partial class Vp8lCommon
{
    internal const int MaxPaletteSize = 256;
    internal const int NumLiteralCodes = 256;
    internal const int NumLengthCodes = 24;
    internal const int NumDistanceCodes = 40;
    internal const int CodeLengthCodes = 19;
    internal const int MinHuffmanBits = 2;
    internal const int NumHuffmanBits = 3;
    internal const int MaxHuffmanBits = MinHuffmanBits + (1 << NumHuffmanBits) - 1;
    internal const int MinTransformBits = 2;
    internal const int NumTransformBits = 3;
    internal const int MaxTransformBits = MinTransformBits + (1 << NumTransformBits) - 1;
    internal const int MaxColorCacheBits = 10;
    internal const int MaxAllowedCodeLength = 15;
    internal const uint ArgbBlack = 0xff000000u;
    internal const int Vp8lImageSizeBits = 14;
    internal const int Vp8lVersionBits = 3;
    internal const byte Vp8lMagicByte = 0x2f;
    internal const int TransformPresent = 1;
    internal const int PredictorTransform = 0;
    internal const int CrossColorTransform = 1;
    internal const int SubtractGreenTransform = 2;
    internal const int ColorIndexingTransform = 3;

    internal const int Log2PrecisionBits = 23;
    internal const double Log2ReciprocalFixedDouble = 12102203.161561485379934310913085937500;
    internal const ulong Log2ReciprocalFixed = 12102203;
    private const int ApproxLogWithCorrectionMax = 65536;
    private const int ApproxLogMax = 4096;
    internal const long Int64Max = long.MaxValue;
    internal const ulong UInt64Max = ulong.MaxValue;
    internal const ushort NonTrivialSym = 0xffff;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int BitsLog2Floor(uint n) => BitOperations.Log2(n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SubSampleSize(int size, int samplingBits) => (int)(((uint)size + (1u << samplingBits) - 1) >> samplingBits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NearLosslessBits(int nearLosslessQuality) => 5 - nearLosslessQuality / 20;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint FastLog2(uint v) => v < 256 ? KLog2Table[v] : FastLog2Slow(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong FastSLog2(uint v) => v < 256 ? KSLog2Table[v] : FastSLog2Slow(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong RightShiftRound(ulong v, int shift) => (v + (1ul << shift >> 1)) >> shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long DivRound(long a, long b) => ((a < 0) == (b < 0)) ? ((a + b / 2) / b) : ((a - b / 2) / b);

    private static ulong FastSLog2Slow(uint v)
    {
        if (v < ApproxLogWithCorrectionMax)
        {
            ulong origV = v;
            ulong logCnt = (ulong)(BitsLog2Floor(v) - 7);
            uint y = 1u << (int)logCnt;
            v >>= (int)logCnt;
            ulong correction = Log2ReciprocalFixed * (origV & (y - 1));
            return origV * (KLog2Table[v] + (logCnt << Log2PrecisionBits)) + correction;
        }
        return (ulong)(Log2ReciprocalFixedDouble * v * Math.Log(v) + .5);
    }

    private static uint FastLog2Slow(uint v)
    {
        if (v < ApproxLogWithCorrectionMax)
        {
            uint origV = v;
            uint logCnt = (uint)(BitsLog2Floor(v) - 7);
            uint y = 1u << (int)logCnt;
            v >>= (int)logCnt;
            uint log2 = KLog2Table[v] + (logCnt << Log2PrecisionBits);
            if (origV >= ApproxLogMax)
            {
                ulong correction = Log2ReciprocalFixed * (origV & (y - 1));
                log2 += (uint)DivRound((long)correction, origV);
            }
            return log2;
        }
        return (uint)(Log2ReciprocalFixedDouble * Math.Log(v) + .5);
    }

    // ---- prefix coding -------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PrefixEncodeBits(int distance, out int code, out int extraBits)
    {
        if (distance <= 3)
        {
            // kPrefixEncodeCode's first entries (0 and 1 -> 0, 2 -> 1, 3 -> 2), no extra bits
            code = distance > 0 ? distance - 1 : 0;
            extraBits = 0;
            return;
        }
        int highestBit = BitsLog2Floor((uint)--distance);
        int secondHighestBit = (distance >> (highestBit - 1)) & 1;
        extraBits = highestBit - 1;
        code = 2 * highestBit + secondHighestBit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PrefixEncode(int distance, out int code, out int extraBits, out int extraBitsValue)
    {
        PrefixEncodeBits(distance, out code, out extraBits);
        extraBitsValue = (distance - 1) & ((1 << extraBits) - 1);
    }

    // ---- pixel arithmetic ----------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint AddPixels(uint a, uint b)
    {
        uint alphaAndGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
        uint redAndBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint SubPixels(uint a, uint b)
    {
        uint alphaAndGreen = 0x00ff00ffu + (a & 0xff00ff00u) - (b & 0xff00ff00u);
        uint redAndBlue = 0xff00ff00u + (a & 0x00ff00ffu) - (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average2(uint a0, uint a1) => (((a0 ^ a1) & 0xfefefefeu) >> 1) + (a0 & a1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average3(uint a0, uint a1, uint a2) => Average2(Average2(a0, a2), a1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average4(uint a0, uint a1, uint a2, uint a3) => Average2(Average2(a0, a1), Average2(a2, a3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Clip255(uint a) => a < 256 ? a : ~a >> 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AddSubtractComponentFull(int a, int b, int c) => (int)Clip255((uint)(a + b - c));

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        int a = AddSubtractComponentFull((int)(c0 >> 24), (int)(c1 >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentFull((int)((c0 >> 16) & 0xff), (int)((c1 >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        int g = AddSubtractComponentFull((int)((c0 >> 8) & 0xff), (int)((c1 >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        int b = AddSubtractComponentFull((int)(c0 & 0xff), (int)(c1 & 0xff), (int)(c2 & 0xff));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AddSubtractComponentHalf(int a, int b) => (int)Clip255((uint)(a + (a - b) / 2));

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint ave = Average2(c0, c1);
        int a = AddSubtractComponentHalf((int)(ave >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentHalf((int)((ave >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        int g = AddSubtractComponentHalf((int)((ave >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        int b = AddSubtractComponentHalf((int)(ave & 0xff), (int)(c2 & 0xff));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sub3(int a, int b, int c)
    {
        int pb = b - c;
        int pa = a - c;
        return Math.Abs(pb) - Math.Abs(pa);
    }

    private static uint Select(uint a, uint b, uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff)) +
            Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff)) +
            Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
        return paMinusPb <= 0 ? a : b;
    }

    /// <summary>VP8LPredictors[mode](left, top): left = the pixel before the current one, top = the one above it
    /// (top[-1] / top[1] its neighbours).</summary>
    internal static uint Predict(int mode, uint[] cur, int left, uint[] up, int top)
    {
        switch (mode)
        {
            case 0: return ArgbBlack;
            case 1: return cur[left];
            case 2: return up[top];
            case 3: return up[top + 1];
            case 4: return up[top - 1];
            case 5: return Average3(cur[left], up[top], up[top + 1]);
            case 6: return Average2(cur[left], up[top - 1]);
            case 7: return Average2(cur[left], up[top]);
            case 8: return Average2(up[top - 1], up[top]);
            case 9: return Average2(up[top], up[top + 1]);
            case 10: return Average4(cur[left], up[top - 1], up[top], up[top + 1]);
            case 11: return Select(up[top], cur[left], up[top - 1]);
            case 12: return ClampedAddSubtractFull(cur[left], up[top], up[top - 1]);
            case 13: return ClampedAddSubtractHalf(cur[left], up[top], up[top - 1]);
            default: return ArgbBlack;   // 14, 15: padding sentinels (PredictorSub0)
        }
    }

    /// <summary>VP8LPredictorsSub[mode](in, upper, num_pixels, out): out[x] = in[x] - prediction(in[x - 1], upper[x]).
    /// Modes 0 and 1 never read upper.</summary>
    internal static void PredictorSub(int mode, uint[] input, int inPos, uint[] upper, int upPos, int numPixels,
        uint[] output, int outPos)
    {
        for (int x = 0; x < numPixels; ++x)
        {
            uint pred = mode switch
            {
                0 or 14 or 15 => ArgbBlack,
                1 => input[inPos + x - 1],
                _ => Predict(mode, input, inPos + x - 1, upper, upPos + x),
            };
            output[outPos + x] = SubPixels(input[inPos + x], pred);
        }
    }

    // ---- entropy -------------------------------------------------------------------------------------------------

    internal struct BitEntropy
    {
        public ulong Entropy;
        public uint Sum;
        public int Nonzeros;
        public uint MaxVal;
        public uint NonzeroCode;

        public void Init()
        {
            Entropy = 0;
            Sum = 0;
            Nonzeros = 0;
            MaxVal = 0;
            NonzeroCode = NonTrivialSym;
        }
    }

    internal struct Streaks
    {
        public int Counts0, Counts1;          // counts[2]: 0 = zero streak, 1 = non-zero streak
        public int S00, S01, S10, S11;        // streaks[zero/non-zero][streak<=3 / >3]
    }

    /// <summary>CombinedShannonEntropy_C: entropy of {X} and {X+Y}.</summary>
    internal static ulong CombinedShannonEntropy(uint[] x, int xOff, uint[] y, int yOff)
    {
        ulong retval = 0;
        uint sumX = 0, sumXY = 0;
        for (int i = 0; i < 256; ++i)
        {
            uint xv = x[xOff + i];
            if (xv != 0)
            {
                uint xy = xv + y[yOff + i];
                sumX += xv;
                retval += FastSLog2(xv);
                sumXY += xy;
                retval += FastSLog2(xy);
            }
            else if (y[yOff + i] != 0)
            {
                sumXY += y[yOff + i];
                retval += FastSLog2(y[yOff + i]);
            }
        }
        return FastSLog2(sumX) + FastSLog2(sumXY) - retval;
    }

    internal static ulong ShannonEntropy(uint[] x, int off, int n)
    {
        ulong retval = 0;
        uint sumX = 0;
        for (int i = 0; i < n; ++i)
        {
            uint v = x[off + i];
            if (v != 0)
            {
                sumX += v;
                retval += FastSLog2(v);
            }
        }
        return FastSLog2(sumX) - retval;
    }

    internal static void BitsEntropyUnrefined(uint[] array, int off, int n, ref BitEntropy entropy)
    {
        entropy.Init();
        for (int i = 0; i < n; ++i)
        {
            uint v = array[off + i];
            if (v != 0)
            {
                entropy.Sum += v;
                entropy.NonzeroCode = (uint)i;
                ++entropy.Nonzeros;
                entropy.Entropy += FastSLog2(v);
                if (entropy.MaxVal < v) entropy.MaxVal = v;
            }
        }
        entropy.Entropy = FastSLog2(entropy.Sum) - entropy.Entropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetEntropyUnrefinedHelper(uint val, int i, ref uint valPrev, ref int iPrev,
        ref BitEntropy bitEntropy, ref Streaks stats)
    {
        int streak = i - iPrev;
        if (valPrev != 0)
        {
            bitEntropy.Sum += valPrev * (uint)streak;
            bitEntropy.Nonzeros += streak;
            bitEntropy.NonzeroCode = (uint)iPrev;
            bitEntropy.Entropy += FastSLog2(valPrev) * (ulong)streak;
            if (bitEntropy.MaxVal < valPrev) bitEntropy.MaxVal = valPrev;
        }
        int big = streak > 3 ? 1 : 0;
        if (valPrev != 0)
        {
            stats.Counts1 += big;
            if (big != 0) stats.S11 += streak; else stats.S10 += streak;
        }
        else
        {
            stats.Counts0 += big;
            if (big != 0) stats.S01 += streak; else stats.S00 += streak;
        }
        valPrev = val;
        iPrev = i;
    }

    internal static void GetEntropyUnrefined(uint[] x, int off, int length, ref BitEntropy bitEntropy, ref Streaks stats)
    {
        int i;
        int iPrev = 0;
        uint xPrev = x[off];
        stats = default;
        bitEntropy.Init();
        for (i = 1; i < length; ++i)
        {
            uint v = x[off + i];
            if (v != xPrev) GetEntropyUnrefinedHelper(v, i, ref xPrev, ref iPrev, ref bitEntropy, ref stats);
        }
        GetEntropyUnrefinedHelper(0, i, ref xPrev, ref iPrev, ref bitEntropy, ref stats);
        bitEntropy.Entropy = FastSLog2(bitEntropy.Sum) - bitEntropy.Entropy;
    }

    internal static void GetCombinedEntropyUnrefined(uint[] x, int xOff, uint[] y, int yOff, int length,
        ref BitEntropy bitEntropy, ref Streaks stats)
    {
        int i;
        int iPrev = 0;
        uint xyPrev = x[xOff] + y[yOff];
        stats = default;
        bitEntropy.Init();
        for (i = 1; i < length; ++i)
        {
            uint xy = x[xOff + i] + y[yOff + i];
            if (xy != xyPrev) GetEntropyUnrefinedHelper(xy, i, ref xyPrev, ref iPrev, ref bitEntropy, ref stats);
        }
        GetEntropyUnrefinedHelper(0, i, ref xyPrev, ref iPrev, ref bitEntropy, ref stats);
        bitEntropy.Entropy = FastSLog2(bitEntropy.Sum) - bitEntropy.Entropy;
    }

    internal static uint ExtraCost(uint[] population, int off, int length)
    {
        uint cost = population[off + 4] + population[off + 5];
        for (int i = 2; i < length / 2 - 1; ++i)
            cost += (uint)i * (population[off + 2 * i + 2] + population[off + 2 * i + 3]);
        return cost;
    }

    // ---- transforms ----------------------------------------------------------------------------------------------

    internal static void SubtractGreenFromBlueAndRed(uint[] argbData, int numPixels)
    {
        for (int i = 0; i < numPixels; ++i)
        {
            int argb = (int)argbData[i];
            int green = (argb >> 8) & 0xff;
            uint newR = (uint)((((argb >> 16) & 0xff) - green) & 0xff);
            uint newB = (uint)(((argb & 0xff) - green) & 0xff);
            argbData[i] = ((uint)argb & 0xff00ff00u) | (newR << 16) | newB;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorTransformDelta(sbyte colorPred, sbyte color) => (colorPred * color) >> 5;

    internal static void TransformColor(byte greenToRed, byte greenToBlue, byte redToBlue, uint[] data, int off, int numPixels)
    {
        for (int i = 0; i < numPixels; ++i)
        {
            uint argb = data[off + i];
            sbyte green = (sbyte)(argb >> 8);
            sbyte red = (sbyte)(argb >> 16);
            int newRed = red & 0xff;
            int newBlue = (int)(argb & 0xff);
            newRed -= ColorTransformDelta((sbyte)greenToRed, green);
            newRed &= 0xff;
            newBlue -= ColorTransformDelta((sbyte)greenToBlue, green);
            newBlue -= ColorTransformDelta((sbyte)redToBlue, red);
            newBlue &= 0xff;
            data[off + i] = (argb & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte TransformColorRed(byte greenToRed, uint argb)
    {
        sbyte green = (sbyte)(argb >> 8);
        int newRed = (int)(argb >> 16);
        newRed -= ColorTransformDelta((sbyte)greenToRed, green);
        return (byte)(newRed & 0xff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte TransformColorBlue(byte greenToBlue, byte redToBlue, uint argb)
    {
        sbyte green = (sbyte)(argb >> 8);
        sbyte red = (sbyte)(argb >> 16);
        int newBlue = (int)(argb & 0xff);
        newBlue -= ColorTransformDelta((sbyte)greenToBlue, green);
        newBlue -= ColorTransformDelta((sbyte)redToBlue, red);
        return (byte)(newBlue & 0xff);
    }

    internal static void CollectColorRedTransforms(uint[] argb, int off, int stride, int tileWidth, int tileHeight,
        int greenToRed, uint[] histo)
    {
        while (tileHeight-- > 0)
        {
            for (int x = 0; x < tileWidth; ++x) ++histo[TransformColorRed((byte)greenToRed, argb[off + x])];
            off += stride;
        }
    }

    internal static void CollectColorBlueTransforms(uint[] argb, int off, int stride, int tileWidth, int tileHeight,
        int greenToBlue, int redToBlue, uint[] histo)
    {
        while (tileHeight-- > 0)
        {
            for (int x = 0; x < tileWidth; ++x)
                ++histo[TransformColorBlue((byte)greenToBlue, (byte)redToBlue, argb[off + x])];
            off += stride;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VectorMismatch(uint[] a, int aOff, uint[] b, int bOff, int length)
    {
        int matchLen = 0;
        while (matchLen < length && a[aOff + matchLen] == b[bOff + matchLen]) ++matchLen;
        return matchLen;
    }

    /// <summary>VP8LBundleColorMap: packs 1, 2, 4 or 8 palette indices per pixel.</summary>
    internal static void BundleColorMap(byte[] row, int width, int xbits, uint[] dst, int dstOff)
    {
        if (xbits > 0)
        {
            int bitDepth = 1 << (3 - xbits);
            int mask = (1 << xbits) - 1;
            uint code = 0xff000000;
            for (int x = 0; x < width; ++x)
            {
                int xsub = x & mask;
                if (xsub == 0) code = 0xff000000;
                code |= (uint)row[x] << (8 + bitDepth * xsub);
                dst[dstOff + (x >> xbits)] = code;
            }
        }
        else
        {
            for (int x = 0; x < width; ++x) dst[dstOff + x] = 0xff000000 | ((uint)row[x] << 8);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HashPix(uint argb, int shift) => (int)((argb * 0x1e35a7bdu) >> shift);
}
