// libjxl 0.12 VarDCT decoding: block context map (entropy_coder.cc), quantizer (quantizer.cc), chroma-from-luma
// (chroma_from_luma.cc), coefficient orders (coeff_order.cc), DC dequantization and adaptive smoothing
// (compressed_dc.cc), EPF sigma (epf.cc) and the AC group decoder (dec_group.cc). Line-by-line ports.
using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Jxl.Lj;

/// <summary>BlockCtxMap (ac_context.h) with DecodeBlockCtxMap.</summary>
internal sealed class LjBlockCtxMap
{
    public const int NumOrders = 13, NonZeroBuckets = 37, ZeroDensityContextCount = 458, ZeroDensityContextLimit = 474;

    private static readonly byte[] DefaultCtxMap =
    [
        0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6,
        7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
        7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
    ];

    private static readonly LjU32Enc DcThresholdDist = new(LjU32.BitsOnly(4), LjU32.BitsOffset(8, 16), LjU32.BitsOffset(16, 272), LjU32.BitsOffset(32, 65808));
    private static readonly LjU32Enc QfThresholdDist = new(LjU32.BitsOnly(2), LjU32.BitsOffset(3, 4), LjU32.BitsOffset(5, 12), LjU32.BitsOffset(8, 44));

    public int[][] DcThresholds = [[], [], []];
    public uint[] QfThresholds = [];
    public byte[] CtxMap = (byte[])DefaultCtxMap.Clone();
    public int NumCtxs = 15, NumDcCtxs = 1;

    public static readonly ushort[] CoeffFreqContext =
    [
        0xBAD, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
        15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22,
        23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26,
        27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    public static readonly ushort[] CoeffNumNonzeroContext =
    [
        0xBAD, 0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123,
        152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180, 180,
        180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
    ];

    public static int ZeroDensityContext(int nonzerosLeft, int k, int coveredBlocks, int log2CoveredBlocks, int prev)
    {
        nonzerosLeft = (nonzerosLeft + coveredBlocks - 1) >> log2CoveredBlocks;
        k >>= log2CoveredBlocks;
        return ((CoeffNumNonzeroContext[nonzerosLeft] + CoeffFreqContext[k]) * 2) + prev;
    }

    public int Context(int dcIdx, uint qf, int ord, int c)
    {
        int qfIdx = 0;
        foreach (uint t in QfThresholds)
        {
            if (qf > t)
            {
                qfIdx++;
            }
        }

        int idx = c < 2 ? c ^ 1 : 2;
        idx = (idx * NumOrders) + ord;
        idx = (idx * (QfThresholds.Length + 1)) + qfIdx;
        idx = (idx * NumDcCtxs) + dcIdx;
        return CtxMap[idx];
    }

    public int ZeroDensityContextsOffset(int blockCtx) => (NumCtxs * NonZeroBuckets) + (ZeroDensityContextCount * blockCtx);

    public int NumAcContexts => NumCtxs * (NonZeroBuckets + ZeroDensityContextCount);

    public int NonZeroContext(int nonZeros, int blockCtx)
    {
        if (nonZeros >= 64)
        {
            nonZeros = 64;
        }

        int ctx = nonZeros < 8 ? nonZeros : 4 + (nonZeros / 2);
        return (ctx * NumCtxs) + blockCtx;
    }

    public static LjBlockCtxMap Decode(LjBitReader br)
    {
        var m = new LjBlockCtxMap();
        if (br.ReadBits(1) != 0)
        {
            return m;
        }

        m.NumDcCtxs = 1;
        for (int j = 0; j < 3; j++)
        {
            m.DcThresholds[j] = new int[br.ReadBits(4)];
            m.NumDcCtxs *= m.DcThresholds[j].Length + 1;
            for (int i = 0; i < m.DcThresholds[j].Length; i++)
            {
                m.DcThresholds[j][i] = LjHeaderUtil.UnpackSigned(LjDec.ReadU32(DcThresholdDist, br));
            }
        }

        m.QfThresholds = new uint[br.ReadBits(4)];
        for (int i = 0; i < m.QfThresholds.Length; i++)
        {
            m.QfThresholds[i] = LjDec.ReadU32(QfThresholdDist, br) + 1;
        }

        if (m.NumDcCtxs * (m.QfThresholds.Length + 1) > 64)
        {
            throw LjDec.Fail("Invalid block context map: too big");
        }

        m.CtxMap = new byte[3 * NumOrders * m.NumDcCtxs * (m.QfThresholds.Length + 1)];
        m.NumCtxs = LjDecAns.DecodeContextMap(m.CtxMap, br);
        if (m.NumCtxs > 16)
        {
            throw LjDec.Fail("Invalid block context map: too many distinct contexts");
        }

        return m;
    }
}

/// <summary>Quantizer (decoder side).</summary>
internal sealed class LjQuantizer
{
    public const int GlobalScaleDenom = 1 << 16;
    public const int QuantMax = 256;
    private static readonly LjU32Enc GlobalScaleEnc = new(LjU32.BitsOffset(11, 1), LjU32.BitsOffset(11, 2049), LjU32.BitsOffset(12, 4097), LjU32.BitsOffset(16, 8193));
    private static readonly LjU32Enc QuantDcEnc = new(LjU32.Val(16), LjU32.BitsOffset(5, 1), LjU32.BitsOffset(8, 1), LjU32.BitsOffset(16, 1));

    private readonly LjDequantMatrices dequant;
    public int GlobalScale, QuantDc;
    public float InvGlobalScale, GlobalScaleFloat, InvQuantDc;
    public float[] MulDc = new float[4];

    public LjQuantizer(LjDequantMatrices dequant)
    {
        this.dequant = dequant;
        QuantDc = 64;
        GlobalScale = GlobalScaleDenom / 64;
        Recompute();
    }

    public float Scale => GlobalScaleFloat;

    public void Recompute()
    {
        GlobalScaleFloat = (float)(GlobalScale * (1.0 / GlobalScaleDenom));
        InvGlobalScale = (float)(1.0 * GlobalScaleDenom / GlobalScale);
        InvQuantDc = InvGlobalScale / QuantDc;
        for (int c = 0; c < 3; c++)
        {
            MulDc[c] = InvQuantDc * dequant.DcQuant[c];
        }
    }

    public void Decode(LjBitReader br)
    {
        GlobalScale = (int)LjDec.ReadU32(GlobalScaleEnc, br);
        QuantDc = (int)LjDec.ReadU32(QuantDcEnc, br);
        Recompute();
    }

    public void ClearDcMul()
    {
        for (int i = 0; i < 4; i++)
        {
            MulDc[i] = 1f;
        }
    }
}

/// <summary>ColorCorrelation + ColorCorrelationMap.</summary>
internal sealed class LjColorCorrelation
{
    public const int DefaultColorFactor = 84;
    private static readonly LjU32Enc ColorFactorDist = new(LjU32.Val(DefaultColorFactor), LjU32.Val(256), LjU32.BitsOffset(8, 2), LjU32.BitsOffset(16, 258));

    public uint ColorFactor = DefaultColorFactor;
    public float ColorScale = 1.0f / DefaultColorFactor;
    public float BaseCorrelationX;
    public float BaseCorrelationB = 1.0f;   // jxl::cms::kYToBRatio
    public int YtoxDc, YtobDc;
    public float[] DcFactors = new float[4];
    public LjImageSB YtoxMap, YtobMap;

    public LjColorCorrelation(int xsize, int ysize)
    {
        int xb = LjDec.DivCeil(xsize, 64), yb = LjDec.DivCeil(ysize, 64);
        YtoxMap = new LjImageSB(xb, yb);
        YtobMap = new LjImageSB(xb, yb);
        RecomputeDcFactors();
    }

    public float YtoXRatio(int xFactor) => BaseCorrelationX + (xFactor * ColorScale);

    public float YtoBRatio(int bFactor) => BaseCorrelationB + (bFactor * ColorScale);

    public void RecomputeDcFactors()
    {
        DcFactors[0] = YtoXRatio(YtoxDc);
        DcFactors[2] = YtoBRatio(YtobDc);
    }

    public bool IsJpegCompatible => BaseCorrelationX == 0 && BaseCorrelationB == 0 && YtobDc == 0 && YtoxDc == 0 && ColorFactor == DefaultColorFactor;

    public static int RatioJpeg(int factor) => factor * (1 << 11) / DefaultColorFactor;

    public void DecodeDc(LjBitReader br)
    {
        if (br.ReadBits(1) == 1)
        {
            return;
        }

        ColorFactor = LjDec.ReadU32(ColorFactorDist, br);
        ColorScale = 1.0f / ColorFactor;
        BaseCorrelationX = LjFields.F16ToFloat((uint)br.ReadBits(16));
        if (MathF.Abs(BaseCorrelationX) > 4.0f)
        {
            throw LjDec.Fail("Base X correlation is out of range");
        }

        BaseCorrelationB = LjFields.F16ToFloat((uint)br.ReadBits(16));
        if (MathF.Abs(BaseCorrelationB) > 4.0f)
        {
            throw LjDec.Fail("Base B correlation is out of range");
        }

        YtoxDc = (int)br.ReadBits(8) + sbyte.MinValue;
        YtobDc = (int)br.ReadBits(8) + sbyte.MinValue;
        RecomputeDcFactors();
    }
}

internal static class LjDecVarDct
{
    private static readonly LjU32Enc OrderEnc = new(LjU32.Val(0x5F), LjU32.Val(0x13), LjU32.Val(0), LjU32.BitsOnly(13));

    public static uint ReadUsedOrders(LjBitReader br) => LjDec.ReadU32(OrderEnc, br);

    /// <summary>DecodeCoeffOrders.</summary>
    public static void DecodeCoeffOrders(uint usedOrders, uint usedAcs, uint[] order, int orderOffset, LjBitReader br)
    {
        uint computed = 0;
        byte[] contextMap = [];
        LjAnsSymbolReader? reader = null;
        if (usedOrders != 0)
        {
            LjAnsCode code = LjDecAns.DecodeHistograms(br, LjToc.PermutationContexts, out contextMap);
            reader = new LjAnsSymbolReader(code, br);
        }

        uint acsMask = 0;
        for (int o = 0; o < LjDecTransforms.NumValidStrategies; ++o)
        {
            if ((usedAcs & (1u << o)) == 0)
            {
                continue;
            }

            acsMask |= 1u << LjDecTransforms.StrategyOrder[o];
        }

        var naturalOrder = new uint[0];
        for (int o = 0; o < LjDecTransforms.NumValidStrategies; ++o)
        {
            int ord = LjDecTransforms.StrategyOrder[o];
            if ((computed & (1u << ord)) != 0)
            {
                continue;
            }

            computed |= 1u << ord;
            bool used = (acsMask & (1u << ord)) != 0;
            int llf = LjDecTransforms.CoveredBlocksX[o] * LjDecTransforms.CoveredBlocksY[o];
            int size = 64 * llf;
            if (used || (usedOrders & (1u << ord)) != 0)
            {
                if (naturalOrder.Length < size)
                {
                    naturalOrder = new uint[size];
                }

                LjDecTransforms.ComputeNaturalCoeffOrder(o, naturalOrder);
            }

            if ((usedOrders & (1u << ord)) == 0)
            {
                if (used)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        Array.Copy(naturalOrder, 0, order, orderOffset + LjDecTransforms.CoeffOrderOffsetOf(ord, c), size);
                    }
                }
            }
            else
            {
                for (int c = 0; c < 3; c++)
                {
                    uint[]? dest = used ? new uint[size] : null;
                    LjToc.ReadPermutation(llf, size, dest, br, reader!, contextMap);
                    if (dest != null)
                    {
                        int off = orderOffset + LjDecTransforms.CoeffOrderOffsetOf(ord, c);
                        for (int k = 0; k < size; ++k)
                        {
                            order[off + k] = naturalOrder[dest[k]];
                        }
                    }
                }
            }
        }

        if (usedOrders != 0 && !reader!.CheckAnsFinalState())
        {
            throw LjDec.Fail("Invalid ANS stream");
        }
    }

    // ------------------------------------------------------------------ DC

    /// <summary>DequantDC for one DC group.</summary>
    public static void DequantDc(LjRect r, LjImage3F dc, LjImageB quantDc, LjModularImage input, float[] dcFactors, float mul, float[] cflFactors,
        LjChromaSubsampling cs, LjBlockCtxMap bctx)
    {
        if (cs.Is444)
        {
            float facX = dcFactors[0] * mul, facY = dcFactors[1] * mul, facB = dcFactors[2] * mul;
            float cflX = cflFactors[0], cflB = cflFactors[2];
            for (int y = 0; y < r.YSize; y++)
            {
                int dx = dc.Planes[0].Row(r.Y0 + y) + r.X0, dy = dc.Planes[1].Row(r.Y0 + y) + r.X0, db = dc.Planes[2].Row(r.Y0 + y) + r.X0;
                LjChannel cx = input.Channel[1], cy = input.Channel[0], cb = input.Channel[2];
                int qx = y * cx.Stride, qy = y * cy.Stride, qb = y * cb.Stride;
                for (int x = 0; x < r.XSize; x++)
                {
                    float inX = cx.Plane[qx + x] * facX;
                    float inY = cy.Plane[qy + x] * facY;
                    float inB = cb.Plane[qb + x] * facB;
                    dc.Planes[1].Data[dy + x] = inY;
                    dc.Planes[0].Data[dx + x] = MathF.FusedMultiplyAdd(inY, cflX, inX);
                    dc.Planes[2].Data[db + x] = MathF.FusedMultiplyAdd(inY, cflB, inB);
                }
            }
        }
        else
        {
            foreach (int c in new[] { 1, 0, 2 })
            {
                var rect = new LjRect(r.X0 >> cs.HShift(c), r.Y0 >> cs.VShift(c), r.XSize >> cs.HShift(c), r.YSize >> cs.VShift(c));
                float fac = dcFactors[c] * mul;
                LjChannel ch = input.Channel[c < 2 ? c ^ 1 : c];
                for (int y = 0; y < rect.YSize; y++)
                {
                    int q = y * ch.Stride;
                    int o = dc.Planes[c].Row(rect.Y0 + y) + rect.X0;
                    for (int x = 0; x < rect.XSize; x++)
                    {
                        dc.Planes[c].Data[o + x] = ch.Plane[q + x] * fac;
                    }
                }
            }
        }

        if (bctx.NumDcCtxs <= 1)
        {
            for (int y = 0; y < r.YSize; y++)
            {
                Array.Clear(quantDc.Data, quantDc.Row(r.Y0 + y) + r.X0, r.XSize);
            }
        }
        else
        {
            for (int y = 0; y < r.YSize; y++)
            {
                LjChannel chx = input.Channel[1], chy = input.Channel[0], chb = input.Channel[2];
                int rx = (y >> cs.VShift(0)) * chx.Stride, ry = (y >> cs.VShift(1)) * chy.Stride, rb = (y >> cs.VShift(2)) * chb.Stride;
                int o = quantDc.Row(r.Y0 + y) + r.X0;
                for (int x = 0; x < r.XSize; x++)
                {
                    int bucketX = 0, bucketY = 0, bucketB = 0;
                    foreach (int t in bctx.DcThresholds[0])
                    {
                        if (chx.Plane[rx + (x >> cs.HShift(0))] > t)
                        {
                            bucketX++;
                        }
                    }

                    foreach (int t in bctx.DcThresholds[1])
                    {
                        if (chy.Plane[ry + (x >> cs.HShift(1))] > t)
                        {
                            bucketY++;
                        }
                    }

                    foreach (int t in bctx.DcThresholds[2])
                    {
                        if (chb.Plane[rb + (x >> cs.HShift(2))] > t)
                        {
                            bucketB++;
                        }
                    }

                    int bucket = bucketX;
                    bucket *= bctx.DcThresholds[2].Length + 1;
                    bucket += bucketB;
                    bucket *= bctx.DcThresholds[1].Length + 1;
                    bucket += bucketY;
                    quantDc.Data[o + x] = (byte)bucket;
                }
            }
        }
    }

    private static readonly float DcW1 = 0.20345139757231578f;
    private static readonly float DcW2 = 0.0334829185968739f;
    private static readonly float DcW0 = 1.0f - (4.0f * (DcW1 + DcW2));

    /// <summary>AdaptiveDCSmoothing.</summary>
    public static void AdaptiveDcSmoothing(float[] dcFactors, LjImage3F dc)
    {
        int xsize = dc.XSize, ysize = dc.YSize;
        if (ysize <= 2 || xsize <= 2)
        {
            return;
        }

        float w1 = DcW1, w2 = DcW2, w0 = DcW0;
        var smoothed = new LjImage3F(xsize, ysize);
        for (int c = 0; c < 3; c++)
        {
            foreach (int y in new[] { 0, ysize - 1 })
            {
                Array.Copy(dc.Planes[c].Data, dc.Planes[c].Row(y), smoothed.Planes[c].Data, smoothed.Planes[c].Row(y), xsize);
            }
        }

        var mc = new float[3];
        var sm = new float[3];
        for (int y = 1; y < ysize - 1; y++)
        {
            for (int c = 0; c < 3; c++)
            {
                LjImageF p = dc.Planes[c];
                smoothed.Planes[c].Data[smoothed.Planes[c].Row(y)] = p.Data[p.Row(y)];
                smoothed.Planes[c].Data[smoothed.Planes[c].Row(y) + xsize - 1] = p.Data[p.Row(y) + xsize - 1];
            }

            for (int x = 1; x < xsize - 1; x++)
            {
                float gap = 0.5f;
                for (int c = 0; c < 3; c++)
                {
                    float[] d = dc.Planes[c].Data;
                    int t = dc.Planes[c].Row(y - 1) + x, m = dc.Planes[c].Row(y) + x, b = dc.Planes[c].Row(y + 1) + x;
                    float tl = d[t - 1], tc = d[t], tr = d[t + 1];
                    float ml = d[m - 1], mcv = d[m], mr = d[m + 1];
                    float bl = d[b - 1], bc = d[b], br = d[b + 1];
                    float corner = (tl + tr) + (bl + br);
                    float side = (ml + mr) + (tc + bc);
                    float s = MathF.FusedMultiplyAdd(corner, w2, MathF.FusedMultiplyAdd(side, w1, mcv * w0));
                    mc[c] = mcv;
                    sm[c] = s;
                    gap = MathF.Max(gap, MathF.Abs((mcv - s) / dcFactors[c]));
                }

                float factor = MathF.FusedMultiplyAdd(-4.0f, gap, 3.0f);
                if (factor < 0)
                {
                    factor = 0;
                }

                for (int c = 0; c < 3; c++)
                {
                    smoothed.Planes[c].Data[smoothed.Planes[c].Row(y) + x] = MathF.FusedMultiplyAdd(sm[c] - mc[c], factor, mc[c]);
                }
            }
        }

        dc.Planes = smoothed.Planes;
    }

    // ------------------------------------------------------------------ AC dequantization helpers

    /// <summary>ApproximateReciprocal: the hardware reciprocal estimate libjxl's AVX2 build uses (rcpps).</summary>
    public static float ApproxReciprocal(float v)
    {
        if (Sse.IsSupported)
        {
            return Sse.ReciprocalScalar(Vector128.CreateScalarUnsafe(v)).ToScalar();
        }

        if (AdvSimd.IsSupported)
        {
            return AdvSimd.ReciprocalEstimate(Vector64.Create(v)).ToScalar();
        }

        return 1.0f / v;
    }

    /// <summary>AdjustQuantBias.</summary>
    public static float AdjustQuantBias(int c, int quantI, float[] biases)
    {
        float quant = quantI;
        float absQuant = MathF.Abs(quant);
        if (absQuant < 1.125f)
        {
            if (!(absQuant > 0))
            {
                return 0;
            }

            return quant < 0 ? -biases[c] : biases[c];
        }

        return MathF.FusedMultiplyAdd(-biases[3], ApproxReciprocal(quant), quant);
    }

    public static int PredictFromTopAndLeft(int[]? rowTop, int topOff, int[] row, int rowOff, int x, int defaultVal)
    {
        if (x == 0)
        {
            return rowTop == null ? defaultVal : rowTop[topOff + x];
        }

        if (rowTop == null)
        {
            return row[rowOff + x - 1];
        }

        return (rowTop[topOff + x] + row[rowOff + x - 1] + 1) / 2;
    }
}
