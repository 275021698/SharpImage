// Port of x265 common/quant.{h,cpp} with flat scaling lists: forward transform + quantization (RDOQ with
// psy-rdoq, or dead-zone quantization with sign-bit hiding), dequantization + inverse transform, and the
// significance-context helpers shared with the entropy coder.
using System;
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
#else
using pixel = byte;
#endif

/// <summary>struct QpParam.</summary>
internal struct QpParam
{
    public int rem, per, qp;
    public long lambda2;   // FIX8
    public int lambda;     // FIX8

    public void SetQpParam(int qpScaled)
    {
        if (qp != qpScaled)
        {
            rem = qpScaled % 6;
            per = qpScaled / 6;
            qp = qpScaled;
            lambda2 = (long)((X265Tables.Lambda2Tab[qp - QP_BD_OFFSET] * 256.0) + 0.5);
            lambda = (int)((X265Tables.LambdaTab[qp - QP_BD_OFFSET] * 256.0) + 0.5);
        }
    }
}

[System.Runtime.CompilerServices.SkipLocalsInit]
internal sealed unsafe class X265Quant
{
    private static readonly int[] s_quantScales = { 26214, 23302, 20560, 18396, 16384, 14564 };
    private static readonly int[] s_invQuantScales = { 40, 45, 51, 57, 64, 72 };

    private readonly X265Entropy m_entropyCoder;
    private readonly QpParam[] m_qpParam = new QpParam[3];
    private int m_rdoqLevel;
    private readonly int m_psyRdoqScale;
    private readonly short* m_resiDctCoeff;
    private readonly short* m_fencDctCoeff;
    private readonly short* m_fencShortBuf;
    private readonly int*[] m_quantCoef = new int*[6];     // flat: [rem], 32x32 entries
    private readonly int*[] m_dequantCoef = new int*[6];

    // rdoQuant scratch
    private readonly long* costCoeff, costUncoded, costSig;
    private readonly int* rateIncUp, rateIncDown, sigRateDelta;
    private readonly long* costCoeffGroupSig;
    private readonly int* deltaUBuf;

    public X265Quant(X265Arena arena, double psyScale, X265Entropy entropy)
    {
        for (int i = 0; i < 3; i++) m_qpParam[i].qp = int.MaxValue;
        m_entropyCoder = entropy;
        m_psyRdoqScale = (int)(psyScale * 256.0);
        m_resiDctCoeff = arena.Alloc<short>(MAX_TR_SIZE * MAX_TR_SIZE * 2);
        m_fencDctCoeff = m_resiDctCoeff + (MAX_TR_SIZE * MAX_TR_SIZE);
        m_fencShortBuf = arena.Alloc<short>(MAX_TR_SIZE * MAX_TR_SIZE);
        for (int rem = 0; rem < 6; rem++)
        {
            m_quantCoef[rem] = arena.Alloc<int>(32 * 32);
            m_dequantCoef[rem] = arena.Alloc<int>(32 * 32);
            for (int i = 0; i < 32 * 32; i++)
            {
                m_quantCoef[rem][i] = s_quantScales[rem];
                m_dequantCoef[rem][i] = s_invQuantScales[rem];
            }
        }

        costCoeff = arena.Alloc<long>(32 * 32);
        costUncoded = arena.Alloc<long>(32 * 32);
        costSig = arena.Alloc<long>(32 * 32);
        rateIncUp = arena.Alloc<int>(32 * 32);
        rateIncDown = arena.Alloc<int>(32 * 32);
        sigRateDelta = arena.Alloc<int>(32 * 32);
        costCoeffGroupSig = arena.Alloc<long>(MLS_GRP_NUM);
        deltaUBuf = arena.Alloc<int>(32 * 32);
    }

    public void SetQPforQuant(X265CuData ctu, int qp)
    {
        m_qpParam[TEXT_LUMA].SetQpParam(qp + QP_BD_OFFSET);
        m_rdoqLevel = ctu.m_encData.m_param.rdoqLevel;
        if (ctu.m_chromaFormat != X265_CSP_I400)
        {
            SetChromaQP(qp + ctu.m_slice.m_pps.chromaQpOffset[0] + ctu.m_slice.m_chromaQpOffset[0], TEXT_CHROMA_U, ctu.m_chromaFormat);
            SetChromaQP(qp + ctu.m_slice.m_pps.chromaQpOffset[1] + ctu.m_slice.m_chromaQpOffset[1], TEXT_CHROMA_V, ctu.m_chromaFormat);
        }
    }

    private void SetChromaQP(int qpin, int ttype, int chFmt)
    {
        int qp = Clip3(-QP_BD_OFFSET, 57, qpin);
        if (qp >= 30)
        {
            qp = chFmt == X265_CSP_I420 ? X265Tables.ChromaScale[qp] : Math.Min(qp, QP_MAX_SPEC);
        }

        m_qpParam[ttype].SetQpParam(qp + QP_BD_OFFSET);
    }

    // ---- significance contexts (static, shared with Entropy) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalcPatternSigCtx(ulong sigCoeffGroupFlag64, uint cgPosX, uint cgPosY, uint cgBlkPos, uint trSizeCG)
    {
        if (trSizeCG == 1)
        {
            return 0;
        }

        uint sigPos = (uint)(sigCoeffGroupFlag64 >> (int)(cgBlkPos + 1));
        uint sigRight = (cgPosX != (trSizeCG - 1) ? 1u : 0u) & sigPos;
        uint sigLower = (cgPosY != (trSizeCG - 1) ? 1u : 0u) & (sigPos >> (int)(trSizeCG - 1));
        return sigRight + (sigLower * 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetSigCoeffGroupCtxInc(ulong cgGroupMask, uint cgPosX, uint cgPosY, uint cgBlkPos, uint trSizeCG)
    {
        uint sigPos = (uint)(cgGroupMask >> (int)(cgBlkPos + 1));
        uint sigRight = (cgPosX != (trSizeCG - 1) ? 1u : 0u) & sigPos;
        uint sigLower = (cgPosY != (trSizeCG - 1) ? 1u : 0u) & (sigPos >> (int)(trSizeCG - 1));
        return sigRight | sigLower;
    }

    // ---- sign bit hiding (no rate) ----

    private uint SignBitHidingHDQ(short* coeff, int* deltaU, uint numSig, in TUEntropyCodingParameters codeParams, uint log2TrSize)
    {
        ushort* scan = codeParams.scan;
        byte* coeffNum = stackalloc byte[MLS_GRP_NUM];
        ushort* coeffSign = stackalloc ushort[MLS_GRP_NUM];
        ushort* coeffFlag = stackalloc ushort[MLS_GRP_NUM];
        int lastScanPos = X265Primitives.ScanPosLast(codeParams.scan, coeff, coeffSign, coeffFlag, coeffNum, (int)numSig, (int)codeParams.scanType, 1L << (int)log2TrSize);
        int cgLastScanPos = lastScanPos >> LOG2_SCAN_SET_SIZE;

        // first CG needs special processing
        uint correctOffset = 0x0Fu & (uint)(lastScanPos ^ 0xF);
        coeffFlag[cgLastScanPos] = (ushort)(coeffFlag[cgLastScanPos] << (int)correctOffset);

        for (int cg = cgLastScanPos; cg >= 0; cg--)
        {
            int cgStartPos = cg << LOG2_SCAN_SET_SIZE;
            int n;
            if (coeffNum[cg] == 0)
            {
                continue;
            }

            int firstNZPosInCG = 15 ^ Bsr(coeffFlag[cg]);
            int lastNZPosInCG = 15 ^ Bsf(coeffFlag[cg]);
            if (lastNZPosInCG - firstNZPosInCG >= SBH_THRESHOLD)
            {
                uint signbit = coeff[scan[cgStartPos + firstNZPosInCG]] > 0 ? 0u : 1u;
                uint absSum = 0;
                for (n = firstNZPosInCG; n <= lastNZPosInCG; n++)
                {
                    absSum += (uint)coeff[scan[n + cgStartPos]];
                }

                if (signbit != (absSum & 0x1))
                {
                    int minCostInc = MAX_INT, minPos = -1, curCost;
                    int finalChange = 0, curChange = 0;
                    uint cgFlags = coeffFlag[cg];
                    if (cg == cgLastScanPos)
                    {
                        cgFlags >>= (int)correctOffset;
                    }

                    for (n = cg == cgLastScanPos ? lastNZPosInCG : SCAN_SET_SIZE - 1; n >= 0; --n)
                    {
                        uint blkPos = scan[n + cgStartPos];
                        if ((cgFlags & 1) != 0)
                        {
                            if (deltaU[blkPos] > 0)
                            {
                                curCost = -deltaU[blkPos];
                                curChange = 1;
                            }
                            else
                            {
                                if (cgFlags == 1 && Math.Abs((int)coeff[blkPos]) == 1)
                                {
                                    curCost = MAX_INT;
                                }
                                else
                                {
                                    curCost = deltaU[blkPos];
                                    curChange = -1;
                                }
                            }
                        }
                        else
                        {
                            if (cgFlags == 0)
                            {
                                uint thisSignBit = m_resiDctCoeff[blkPos] >= 0 ? 0u : 1u;
                                if (thisSignBit != signbit)
                                {
                                    curCost = MAX_INT;
                                }
                                else
                                {
                                    curCost = -deltaU[blkPos];
                                    curChange = 1;
                                }
                            }
                            else
                            {
                                curCost = -deltaU[blkPos];
                                curChange = 1;
                            }
                        }

                        if (curCost < minCostInc)
                        {
                            minCostInc = curCost;
                            finalChange = curChange;
                            minPos = (int)blkPos;
                        }

                        cgFlags >>= 1;
                    }

                    // do not allow the change to violate the coeff clamp
                    if (coeff[minPos] == 32767 || coeff[minPos] == -32768)
                    {
                        finalChange = -1;
                    }

                    if (coeff[minPos] == 0)
                    {
                        numSig++;
                    }
                    else if (finalChange == -1 && Math.Abs((int)coeff[minPos]) == 1)
                    {
                        numSig--;
                    }

                    short sigMask = (short)(m_resiDctCoeff[minPos] >> 15);
                    coeff[minPos] += (short)(((short)finalChange ^ sigMask) - sigMask);
                }
            }
        }

        return numSig;
    }

    public uint TransformNxN(X265CuData cu, pixel* fenc, uint fencStride, short* residual, uint resiStride, short* coeff,
        uint log2TrSize, int ttype, uint absPartIdx, bool useTransformSkip)
    {
        int sizeIdx = (int)log2TrSize - 2;
        if (cu.m_tqBypass[0] != 0)
        {
            int n = 1 << (int)log2TrSize;
            uint numSigBypass = 0;
            for (int k = 0; k < n; k++)
            {
                for (int j = 0; j < n; j++)
                {
                    coeff[(k * n) + j] = residual[(k * resiStride) + j];
                    numSigBypass += residual[(k * resiStride) + j] != 0 ? 1u : 0u;
                }
            }

            return numSigBypass;
        }

        bool isLuma = ttype == TEXT_LUMA;
        bool usePsy = m_psyRdoqScale != 0 && isLuma && !useTransformSkip;
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - (int)log2TrSize;

        if (useTransformSkip)
        {
            int n = 1 << (int)log2TrSize;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    m_resiDctCoeff[(i * n) + j] = (short)(residual[(i * resiStride) + j] << transformShift);
                }
            }
        }
        else
        {
            bool isIntra = cu.IsIntra(absPartIdx);
            if (sizeIdx == 0 && isLuma && isIntra)
            {
                X265Primitives.Dst4(residual, m_resiDctCoeff, resiStride);
            }
            else
            {
                X265Primitives.Dct(sizeIdx, residual, m_resiDctCoeff, resiStride);
            }

            if (usePsy)
            {
                int trSize = 1 << (int)log2TrSize;
                X265Primitives.CopyPS(m_fencShortBuf, trSize, fenc, fencStride, trSize);
                X265Primitives.Dct(sizeIdx, m_fencShortBuf, m_fencDctCoeff, trSize);
            }
        }

        if (m_rdoqLevel != 0)
        {
            return RdoQuant(cu, coeff, ttype, absPartIdx, usePsy, log2TrSize);
        }

        int* deltaU = deltaUBuf;
        int rem = m_qpParam[ttype].rem;
        int per = m_qpParam[ttype].per;
        int* quantCoeff = m_quantCoef[rem];
        int qbits = QUANT_SHIFT + per + transformShift;
        int add = (cu.m_slice.m_sliceType == I_SLICE ? 171 : 85) << (qbits - 9);
        int numCoeff = 1 << (int)(log2TrSize * 2);
        uint numSig = X265Primitives.Quant(m_resiDctCoeff, quantCoeff, deltaU, coeff, qbits, add, numCoeff);
        if (numSig >= 2 && cu.m_slice.m_pps.bSignHideEnabled)
        {
            var codeParams = default(TUEntropyCodingParameters);
            cu.GetTUEntropyCodingParameters(ref codeParams, absPartIdx, log2TrSize, isLuma);
            return SignBitHidingHDQ(coeff, deltaU, numSig, codeParams, log2TrSize);
        }

        return numSig;
    }

    public void InvtransformNxN(X265CuData cu, short* residual, uint resiStride, short* coeff, uint log2TrSize, int ttype,
        bool bIntra, bool useTransformSkip, uint numSig)
    {
        int sizeIdx = (int)log2TrSize - 2;
        int n = 1 << (int)log2TrSize;
        if (cu.m_tqBypass[0] != 0)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    residual[(i * resiStride) + j] = coeff[(i * n) + j];
                }
            }

            return;
        }

        int rem = m_qpParam[ttype].rem;
        int per = m_qpParam[ttype].per;
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - (int)log2TrSize;
        int shift = QUANT_IQUANT_SHIFT - QUANT_SHIFT - transformShift;
        int numCoeff = 1 << (int)(log2TrSize * 2);
        int scale = s_invQuantScales[rem] << per;
        X265Primitives.DequantNormal(coeff, m_resiDctCoeff, numCoeff, scale, shift);

        if (useTransformSkip)
        {
            int rnd = 1 << (transformShift - 1);
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    residual[(i * resiStride) + j] = (short)((m_resiDctCoeff[(i * n) + j] + rnd) >> transformShift);
                }
            }

            return;
        }

        bool useDST = sizeIdx == 0 && ttype == TEXT_LUMA && bIntra;
        if (numSig == 1 && coeff[0] != 0 && !useDST)
        {
            // DC only
            const int shift1st = 7 - 6;
            const int add1st = 1 << (shift1st - 1);
            const int shift2nd = 12 - (X265_DEPTH - 8) - 3;
            const int add2nd = 1 << (shift2nd - 1);
            int dcVal = ((((m_resiDctCoeff[0] * (64 >> 6)) + add1st) >> shift1st) * (64 >> 3) + add2nd) >> shift2nd;
            X265Primitives.BlockFillS(residual, resiStride, (short)dcVal, n);
            return;
        }

        if (useDST)
        {
            X265Primitives.Idst4(m_resiDctCoeff, residual, resiStride);
        }
        else
        {
            X265Primitives.Idct(sizeIdx, m_resiDctCoeff, residual, resiStride);
        }
    }

    // ---- RDOQ helpers ----

    private static int FastMin(int x, int y) => y + ((x - y) & ((x - y) >> 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetICRate(uint absLevel, int diffLevel, int* greaterOneBits, int* levelAbsBits, uint absGoRice, uint maxVlc, uint c1c2Rate)
    {
        if (absLevel == 0)
        {
            return 0;
        }

        int rate = 0;
        if (diffLevel < 0)
        {
            rate += greaterOneBits[absLevel == 2 ? 1 : 0];
            if (absLevel == 2)
            {
                rate += levelAbsBits[0];
            }
        }
        else
        {
            uint symbol = (uint)diffLevel;
            bool expGolomb = symbol > maxVlc;
            if (expGolomb)
            {
                absLevel = symbol - maxVlc;
                int size = Bsr(absLevel);
                int egs = (size * 2) + 1;
                rate += egs << 15;
                symbol = maxVlc + 1;
            }

            uint prefLen = (symbol >> (int)absGoRice) + 1;
            uint numBins = (uint)FastMin((int)(prefLen + absGoRice), 8);
            rate += (int)(numBins << 15);
            rate += (int)c1c2Rate;
        }

        return rate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetICRateLessVlc(uint absLevel, int diffLevel, uint absGoRice)
    {
        if (absLevel == 0)
        {
            return 0;
        }

        uint symbol = (uint)diffLevel;
        uint prefLen = (symbol >> (int)absGoRice) + 1;
        uint numBins = (uint)FastMin((int)(prefLen + absGoRice), 8);
        return (int)(numBins << 15);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetICRateCost(uint absLevel, int diffLevel, int* greaterOneBits, int* levelAbsBits, uint absGoRice, uint c1c2Rate)
    {
        if (diffLevel < 0)
        {
            uint rate = (uint)greaterOneBits[absLevel == 2 ? 1 : 0];
            if (absLevel == 2)
            {
                rate += (uint)levelAbsBits[0];
            }

            return rate;
        }
        else
        {
            uint rate;
            uint symbol = (uint)diffLevel;
            if ((symbol >> (int)absGoRice) < COEF_REMAIN_BIN_REDUCTION)
            {
                uint length = symbol >> (int)absGoRice;
                rate = (length + 1 + absGoRice) << 15;
            }
            else
            {
                uint length = 0;
                symbol = (symbol >> (int)absGoRice) - COEF_REMAIN_BIN_REDUCTION;
                if (symbol != 0)
                {
                    length = (uint)Bsr(symbol + 1);
                }

                rate = (COEF_REMAIN_BIN_REDUCTION + length + absGoRice + 1 + length) << 15;
            }

            rate += c1c2Rate;
            return rate;
        }
    }

    private const int IEP_RATE = 32768;
    private static readonly ulong[] TableCnt64 = { 0x0000000100110112UL, 0x0000000011112222UL, 0x0012001200120012UL, 0x2222222222222222UL };

    private struct CoeffGroupRDStats
    {
        public int nnzBeforePos0;
        public long codedLevelAndDist;
        public long uncodedDist;
        public long sigCost;
        public long sigCost0;
        public bool nonZero;
    }

    /// <summary>Rate-distortion optimized quantization (Quant::rdoQuant&lt;log2TrSize&gt;).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SigCost(long lambda2, long bits) => (lambda2 * bits) >> 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RdCost(long lambda2, int scaleBits, int d, long bits) => (((long)d * d) << scaleBits) + SigCost(lambda2, bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DeltaRdCost(long lambda2, int scaleBits, long d0, int dd, long deltabits) => ((((long)dd * dd) - d0) << scaleBits) + ((lambda2 * deltabits) >> 8);

    // all ones when b, else zero (no branch)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Mask(bool b) => -(long)Unsafe.As<bool, byte>(ref b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PsyValue(long psyScale, int psyShift, long rec) => (psyScale * rec) >> psyShift;

    // the per-block constants of rdoQuant, kept in memory (address-exposed) so the JIT leaves its registers to the
    // per-coefficient state
    private struct RdoqBlock
    {
        public long lambda2, psyScale;
        public int scaleBits, psyShift, per, unquantRound, unquantShift, lastScanPos, log2TrSize;
        public uint usePsyMask, trSize;
        public short* resiDct, fencDct, dstCoeff;
        public ushort* scan, scan4;
        public int unquantStep;   // the dequantization step (flat: x265 without scaling lists)
        public int* sigBits0, sigBits1, rateIncUp, rateIncDown, sigRateDelta;
        public long* costCoeff, costUncoded, costSig;
        public EstBitsSbac* est;
        public System.Runtime.Intrinsics.Vector128<byte> scan4Bytes;   // the 4x4 scan as raster indices
        public bool signHide;
    }

    private uint RdoQuant(X265CuData cu, short* dstCoeff, int ttype, uint absPartIdx, bool usePsy, uint log2TrSize)
    {
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - (int)log2TrSize;
        int rem = m_qpParam[ttype].rem;
        int per = m_qpParam[ttype].per;
        int qbits = QUANT_SHIFT + per + transformShift;
        int add = 1 << (qbits - 1);
        int numCoeff = 1 << (int)(log2TrSize * 2);
        uint numSig = X265Primitives.NQuant(m_resiDctCoeff, m_quantCoef[rem], dstCoeff, qbits, add, numCoeff);
        if (numSig == 0)
        {
            return 0;
        }

        RdoqBlock k;
        k.trSize = 1u << (int)log2TrSize;
        k.log2TrSize = (int)log2TrSize;
        k.usePsyMask = usePsy ? ~0u : 0u;
        k.lambda2 = m_qpParam[ttype].lambda2;
        k.psyScale = (long)m_psyRdoqScale * m_qpParam[ttype].lambda;
        k.unquantStep = s_invQuantScales[rem] << per;
        k.per = per;
        k.unquantShift = QUANT_IQUANT_SHIFT - QUANT_SHIFT - transformShift;
        k.unquantRound = k.unquantShift > per ? 1 << (k.unquantShift - per - 1) : 0;
        k.scaleBits = SCALE_BITS - (2 * transformShift);
        k.psyShift = Math.Max(0, (2 * transformShift) + 1);
        k.resiDct = m_resiDctCoeff;
        k.fencDct = m_fencDctCoeff;
        k.dstCoeff = dstCoeff;
        k.costCoeff = costCoeff;
        k.costUncoded = costUncoded;
        k.costSig = costSig;
        k.rateIncUp = rateIncUp;
        k.rateIncDown = rateIncDown;
        k.sigRateDelta = sigRateDelta;
        bool signHide = cu.m_slice.m_pps.bSignHideEnabled;
        long* costCoeffGroupSig = this.costCoeffGroupSig;
        ulong sigCoeffGroupFlag64 = 0;
        const uint cgSize = 1 << MLS_CG_SIZE;
        bool bIsLuma = ttype == TEXT_LUMA;
        long totalUncodedCost = 0;
        long totalRdCost = 0;

        var codeParams = default(TUEntropyCodingParameters);
        cu.GetTUEntropyCodingParameters(ref codeParams, absPartIdx, log2TrSize, bIsLuma);
        k.scan = codeParams.scan;
        k.scan4 = X265Scan.Scan4x4[codeParams.scanType];
        k.scan4Bytes = System.Runtime.Intrinsics.X86.Sse2.IsSupported
            ? System.Runtime.Intrinsics.X86.Sse2.PackUnsignedSaturate(System.Runtime.Intrinsics.X86.Sse2.LoadVector128((short*)k.scan4), System.Runtime.Intrinsics.X86.Sse2.LoadVector128((short*)k.scan4 + 8))
            : default;
        k.signHide = cu.m_slice.m_pps.bSignHideEnabled;
        uint log2TrSizeCG = log2TrSize - 2;
        uint cgNum = 1u << (int)(log2TrSizeCG * 2);
        uint cgStride = k.trSize >> MLS_CG_LOG2_SIZE;

        byte* coeffNum = stackalloc byte[MLS_GRP_NUM];
        ushort* coeffSign = stackalloc ushort[MLS_GRP_NUM];
        ushort* coeffFlag = stackalloc ushort[MLS_GRP_NUM];
        int lastScanPos = X265Primitives.ScanPosLast(codeParams.scan, dstCoeff, coeffSign, coeffFlag, coeffNum, (int)numSig, codeParams.scanType, k.trSize);
        k.lastScanPos = lastScanPos;
        int cgLastScanPos = lastScanPos >> LOG2_SCAN_SET_SIZE;

        fixed (EstBitsSbac* est = &m_entropyCoder.m_estBitsSbac)
        {
            k.est = est;
            k.sigBits0 = est->significantBits;
            k.sigBits1 = est->significantBits + NUM_SIG_FLAG_CTX;
            uint scanPos = 0;
            uint c1 = 1;

            // (x265 zeroes costCoeff / costSig after the last coded group here; nothing reads those entries)

            // uncoded cost of the trailing all-zero groups
            for (int cgScanPos = cgLastScanPos + 1; cgScanPos < (int)cgNum; cgScanPos++)
            {
                long u = UncodedGroupCost(&k, codeParams.scan[(uint)cgScanPos << MLS_CG_SIZE]);
                totalUncodedCost += u;
                totalRdCost += u;
            }

            for (int cgScanPos = cgLastScanPos; cgScanPos >= 0; cgScanPos--)
            {
                uint ctxSet = (cgScanPos != 0 && bIsLuma) ? 2u : 0u;
                uint cgBlkPos = codeParams.scanCG[cgScanPos];
                uint cgPosY = cgBlkPos >> (int)log2TrSizeCG;
                uint cgPosX = cgBlkPos & ((1u << (int)log2TrSizeCG) - 1);
                ulong cgBlkPosMask = 1UL << (int)cgBlkPos;
                int patternSigCtx = (int)CalcPatternSigCtx(sigCoeffGroupFlag64, cgPosX, cgPosY, cgBlkPos, cgStride);
                int ctxSigOffset = (int)codeParams.firstSignificanceMapContext + (cgScanPos != 0 && bIsLuma ? 3 : 0);

                if (c1 == 0)
                {
                    ctxSet++;
                }

                c1 = 1;

                if (cgScanPos != 0 && coeffNum[cgScanPos] == 0)
                {
                    // no coded coefficients in this group
                    long u = UncodedGroup(&k, (uint)cgScanPos << MLS_CG_SIZE, patternSigCtx, ctxSigOffset);
                    totalUncodedCost += u;
                    totalRdCost += u;
                    uint ctxSigCg = GetSigCoeffGroupCtxInc(sigCoeffGroupFlag64, cgPosX, cgPosY, cgBlkPos, cgStride);
                    costCoeffGroupSig[cgScanPos] = SigCost(k.lambda2, est->significantCoeffGroupBits[(ctxSigCg * 2) + 0]);
                    totalRdCost += costCoeffGroupSig[cgScanPos];
                    continue;
                }

                CoeffGroupRDStats cgRdStats = default;
                ulong ctxCnt = k.trSize == 4 ? 0x8877886654325410UL : TableCnt64[patternSigCtx];
                uint scanPosBase = (uint)cgScanPos << MLS_CG_SIZE;
                var totals = new RdoqTotals { uncoded = totalUncodedCost, rd = totalRdCost };
                c1 = CodedGroup(&k, &totals, &cgRdStats, scanPosBase, coeffFlag[cgScanPos], ctxSet, ctxCnt, ctxSigOffset);
                totalUncodedCost = totals.uncoded;
                totalRdCost = totals.rd;
                if (cgRdStats.nonZero)
                {
                    sigCoeffGroupFlag64 |= cgBlkPosMask;
                }

                scanPos = scanPosBase;

                cgRdStats.sigCost0 = k.costSig[scanPos];
                costCoeffGroupSig[cgScanPos] = 0;

                if (cgScanPos == 0 || cgScanPos == cgLastScanPos)
                {
                    // group 0 and the group with the last NZ are implied present
                }
                else if ((sigCoeffGroupFlag64 & cgBlkPosMask) != 0)
                {
                    if (cgRdStats.nnzBeforePos0 == 0)
                    {
                        // only coeff 0 of this CG is coded: its significant coeff bit is implied
                        totalRdCost -= cgRdStats.sigCost0;
                        cgRdStats.sigCost -= cgRdStats.sigCost0;
                    }

                    uint sigCtx = GetSigCoeffGroupCtxInc(sigCoeffGroupFlag64, cgPosX, cgPosY, cgBlkPos, cgStride);
                    long costZeroCG = totalRdCost + SigCost(k.lambda2, est->significantCoeffGroupBits[(sigCtx * 2) + 0]);
                    costZeroCG += cgRdStats.uncodedDist;
                    costZeroCG -= cgRdStats.codedLevelAndDist;
                    costZeroCG -= cgRdStats.sigCost;
                    costCoeffGroupSig[cgScanPos] = SigCost(k.lambda2, est->significantCoeffGroupBits[(sigCtx * 2) + 1]);
                    totalRdCost += costCoeffGroupSig[cgScanPos];

                    if (costZeroCG < totalRdCost && m_rdoqLevel > 1)
                    {
                        sigCoeffGroupFlag64 &= ~cgBlkPosMask;
                        totalRdCost = costZeroCG;
                        costCoeffGroupSig[cgScanPos] = SigCost(k.lambda2, est->significantCoeffGroupBits[(sigCtx * 2) + 0]);
                        uint blkPos0 = codeParams.scan[cgScanPos * cgSize];
                        for (int r = 0; r < 4; r++)
                        {
                            *(long*)&dstCoeff[blkPos0 + (r * k.trSize)] = 0;
                        }
                    }
                }
                else
                {
                    uint ctxSigCg = GetSigCoeffGroupCtxInc(sigCoeffGroupFlag64, cgPosX, cgPosY, cgBlkPos, cgStride);
                    costCoeffGroupSig[cgScanPos] = SigCost(k.lambda2, est->significantCoeffGroupBits[(ctxSigCg * 2) + 0]);
                    totalRdCost += costCoeffGroupSig[cgScanPos];
                    totalRdCost -= cgRdStats.sigCost;
                }
            }

            // RD cost of CBF = 0, and the cost of CBF = 1 added to the total
            long bestCost;
            {
                int ctx = (int)CtxCbf[(ttype * 5) + cu.m_tuDepth[absPartIdx]];
                bestCost = totalUncodedCost + SigCost(k.lambda2, est->blockCbpBits[(ctx * 2) + 0]);
                totalRdCost += SigCost(k.lambda2, est->blockCbpBits[(ctx * 2) + 1]);
            }

            int bestLastIdx = RefineLast(&k, in codeParams, cgLastScanPos, sigCoeffGroupFlag64, totalRdCost, bestCost);

            // recount and re-apply the DCT signs
            numSig = 0;
            short* resiDct = k.resiDct;
            for (int pos = 0; pos < bestLastIdx; pos++)
            {
                int blkPos = codeParams.scan[pos];
                int level = dstCoeff[blkPos];
                numSig += level != 0 ? 1u : 0u;
                int mask = resiDct[blkPos] >> 31;
                dstCoeff[blkPos] = (short)((level ^ mask) - mask);
            }

            // clean uncoded coefficients
            for (int pos = bestLastIdx; pos <= (FastMin(lastScanPos, bestLastIdx) | (SCAN_SET_SIZE - 1)); pos++)
            {
                dstCoeff[codeParams.scan[pos]] = 0;
            }

            for (int pos = (bestLastIdx & ~(SCAN_SET_SIZE - 1)) + SCAN_SET_SIZE; pos <= lastScanPos; pos += SCAN_SET_SIZE)
            {
                uint blkPos = codeParams.scan[pos];
                for (int r = 0; r < 4; r++)
                {
                    *(long*)&dstCoeff[blkPos + (r * k.trSize)] = 0;
                }
            }

            // rate-distortion based sign hiding
            if (signHide && numSig >= 2)
            {
                numSig = SignHide(&k, in codeParams, sigCoeffGroupFlag64, bestLastIdx, numSig);
            }
        }

        return numSig;
    }

    private struct CtxBytes
    {
        public fixed byte v[16];
    }

    private struct RdoqTotals
    {
        public long uncoded, rd;
    }

    // the coefficients of one coded group (from its last scan position down): levels, the per-position costs, the
    // group's RD statistics; returns c1 (whether a level > 1 was coded: 0)
    private static uint CodedGroup(RdoqBlock* k, RdoqTotals* totals, CoeffGroupRDStats* cgRdStats, uint scanPosBase, uint subFlagMask, uint ctxSet, ulong ctxCnt, int ctxSigOffset)
    {
        // the significance contexts of the 16 positions (ctxCnt's nibble of each position, plus the offset; 0 at DC)
        CtxBytes cb;
        byte* ctxs = cb.v;
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            var nib = System.Runtime.Intrinsics.X86.Sse2.UnpackLow(
                System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(ctxCnt & 0x0F0F0F0F0F0F0F0FUL).AsByte(),
                System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe((ctxCnt >> 4) & 0x0F0F0F0F0F0F0F0FUL).AsByte());
            var v = System.Runtime.Intrinsics.X86.Sse2.Add(System.Runtime.Intrinsics.X86.Ssse3.Shuffle(nib, k->scan4Bytes), System.Runtime.Intrinsics.Vector128.Create((byte)ctxSigOffset));
            System.Runtime.Intrinsics.X86.Sse2.Store(ctxs, v);
        }
        else
        {
            for (int p = 0; p < 16; p++) ctxs[p] = (byte)(((ctxCnt >> (4 * k->scan4[p])) & 0xF) + (ulong)ctxSigOffset);
        }

        if (k->scan[scanPosBase] == 0)
        {
            ctxs[0] = 0;
        }

        const uint cgSize = 1 << MLS_CG_SIZE;
        long totalUncoded = totals->uncoded, totalRd = totals->rd;
        uint c1 = 1;
        int c2 = 0;
        uint goRiceParam = 0;
        uint levelThreshold = 3;
        uint c1Idx = 0;
        uint c2Idx = 0;

        // positions past the last coefficient: their uncoded cost only
        int top = Math.Min((int)cgSize - 1, k->lastScanPos - (int)scanPosBase);
        for (int p = (int)cgSize - 1; p > top; p--)
        {
            uint scanPos = scanPosBase + (uint)p;
            uint blkPos = k->scan[scanPos];
            int signCoef = k->resiDct[blkPos];
            long uncoded = ((long)signCoef * signCoef) << k->scaleBits;
            if ((k->usePsyMask & scanPos) != 0)
            {
                uncoded -= PsyValue(k->psyScale, k->psyShift, k->fencDct[blkPos] - signCoef);
            }

            k->costUncoded[blkPos] = uncoded;
            totalUncoded += uncoded;
            k->costCoeff[scanPos] = 0;
            k->costSig[scanPos] = 0;
            totalRd += uncoded;
        }

        for (int scanPosinCG = top; scanPosinCG >= 0; scanPosinCG--)
        {
            uint scanPos = scanPosBase + (uint)scanPosinCG;
            uint blkPos = k->scan[scanPos];
            uint maxAbsLevel = (uint)k->dstCoeff[blkPos];
            int signCoef = k->resiDct[blkPos];
            int predictedCoef = k->fencDct[blkPos] - signCoef;

            // cost of not coding this coefficient (all distortion, no signal bits)
            long uncoded = ((long)signCoef * signCoef) << k->scaleBits;
            if ((k->usePsyMask & scanPos) != 0)
            {
                uncoded -= PsyValue(k->psyScale, k->psyShift, predictedCoef);
            }

            k->costUncoded[blkPos] = uncoded;
            totalUncoded += uncoded;

            int* greaterOneBits = k->est->greaterOneBits + ((4 * ctxSet + c1) * 2);
            uint ctxSig = ctxs[scanPosinCG];

            if ((subFlagMask & 1) == 0)
            {
                // fast zero coeff path
                long sigCost = SigCost(k->lambda2, k->sigBits0[ctxSig]);
                k->costSig[scanPos] = sigCost;
                k->costCoeff[scanPos] = uncoded + sigCost;
                k->sigRateDelta[blkPos] = k->sigBits1[ctxSig] - k->sigBits0[ctxSig];
                totalRd += uncoded + sigCost;
                k->rateIncUp[blkPos] = greaterOneBits[0];
                subFlagMask >>= 1;
            }
            else
            {
                subFlagMask >>= 1;
                uint c1c2idx = ((c1Idx - 8) >> 31) + (uint)((((-(int)c2Idx) >> 31) + 1) * 2);
                uint baseLevel = (0xD9u >> (int)(c1c2idx * 2)) & 3;   // {1, 2, 1, 3}
                int* levelAbsBits = k->est->levelAbsBits + ((ctxSet + c2) * 2);
                uint c1c2Rate = ((c1c2idx & 1) != 0 ? (uint)greaterOneBits[1] : 0) + (c1c2idx == 3 ? (uint)levelAbsBits[1] : 0);

                uint level = 0;
                uint sigCoefBits = 0;
                long best = long.MaxValue;
                long bestSig = 0;

                if ((int)scanPos == k->lastScanPos)
                {
                    k->sigRateDelta[blkPos] = 0;
                }
                else
                {
                    if (maxAbsLevel < 3)
                    {
                        bestSig = SigCost(k->lambda2, k->sigBits0[ctxSig]);
                        best = uncoded + bestSig;
                    }

                    k->sigRateDelta[blkPos] = k->sigBits1[ctxSig] - k->sigBits0[ctxSig];
                    sigCoefBits = (uint)k->sigBits1[ctxSig];
                }

                int unquantStep = k->unquantStep;
                uint unQuantLevel = (uint)((maxAbsLevel * (uint)unquantStep) + k->unquantRound);
                int absSignCoef = Math.Abs(signCoef);
                if (maxAbsLevel == 1)
                {
                    uint levelBits = (c1c2idx & 1) != 0 ? (uint)greaterOneBits[0] + IEP_RATE : ((1 + goRiceParam) << 15) + IEP_RATE;
                    int unquantAbsLevel = (int)(unQuantLevel >> k->unquantShift);
                    long curCost = RdCost(k->lambda2, k->scaleBits, absSignCoef - unquantAbsLevel, sigCoefBits + levelBits);
                    if ((k->usePsyMask & scanPos) != 0)
                    {
                        int reconCoef = Math.Abs(unquantAbsLevel + ((predictedCoef ^ (signCoef >> 31)) - (signCoef >> 31)));
                        curCost -= PsyValue(k->psyScale, k->psyShift, reconCoef);
                    }

                    if (curCost < best)
                    {
                        level = 1;
                        best = curCost;
                        bestSig = SigCost(k->lambda2, sigCoefBits);
                    }
                }
                else if (maxAbsLevel != 0)
                {
                    uint levelBits0 = GetICRateCost(maxAbsLevel, (int)(maxAbsLevel - baseLevel), greaterOneBits, levelAbsBits, goRiceParam, c1c2Rate) + IEP_RATE;
                    uint levelBits1 = GetICRateCost(maxAbsLevel - 1, (int)(maxAbsLevel - 1 - baseLevel), greaterOneBits, levelAbsBits, goRiceParam, c1c2Rate) + IEP_RATE;
                    int unquantAbsLevel0 = (int)(unQuantLevel >> k->unquantShift);
                    long curCost0 = RdCost(k->lambda2, k->scaleBits, absSignCoef - unquantAbsLevel0, sigCoefBits + levelBits0);
                    int unquantAbsLevel1 = (int)((unQuantLevel - (uint)unquantStep) >> k->unquantShift);
                    long curCost1 = RdCost(k->lambda2, k->scaleBits, absSignCoef - unquantAbsLevel1, sigCoefBits + levelBits1);
                    if ((k->usePsyMask & scanPos) != 0)
                    {
                        int sgn = (predictedCoef ^ (signCoef >> 31)) - (signCoef >> 31);
                        curCost0 -= PsyValue(k->psyScale, k->psyShift, Math.Abs(unquantAbsLevel0 + sgn));
                        curCost1 -= PsyValue(k->psyScale, k->psyShift, Math.Abs(unquantAbsLevel1 + sgn));
                    }

                    if (curCost0 < best)
                    {
                        level = maxAbsLevel;
                        best = curCost0;
                        bestSig = SigCost(k->lambda2, sigCoefBits);
                    }

                    if (curCost1 < best)
                    {
                        level = maxAbsLevel - 1;
                        best = curCost1;
                        bestSig = SigCost(k->lambda2, sigCoefBits);
                    }
                }

                k->costCoeff[scanPos] = best;
                k->costSig[scanPos] = bestSig;

                k->dstCoeff[blkPos] = (short)level;
                totalRd += best;

                // costs for the sign hiding performed at the end
                if (k->signHide && level != 0)
                {
                    SignHideRates(k, blkPos, level, baseLevel, goRiceParam, greaterOneBits, levelAbsBits, c1c2Rate);
                }
                else
                {
                    k->rateIncUp[blkPos] = greaterOneBits[0];
                    k->rateIncDown[blkPos] = 0;
                }

                // update CABAC estimation state
                if (level >= baseLevel && goRiceParam < 4 && level > levelThreshold)
                {
                    goRiceParam++;
                    levelThreshold <<= 1;
                }

                uint isNonZero = (uint)(-(int)level) >> 31;
                c1Idx += isNonZero;
                if (level > 1)
                {
                    c1 = 0;
                    c2 += (int)((uint)(c2 - 2) >> 31);
                    c2Idx++;
                }
                else if ((c1 == 1 || c1 == 2) && isNonZero != 0)
                {
                    c1++;
                }

                if (level != 0)
                {
                    cgRdStats->nonZero = true;
                    cgRdStats->codedLevelAndDist += best - k->costSig[scanPos];
                    cgRdStats->uncodedDist += uncoded;
                    cgRdStats->nnzBeforePos0 += scanPosinCG;
                }
            }

            cgRdStats->sigCost += k->costSig[scanPos];
        }

        totals->uncoded = totalUncoded;
        totals->rd = totalRd;
        return c1;
    }

    // the uncoded cost of a 4x4 group without coefficients (psyRdoQuant_1p / 2p or nonPsyRdoQuant), costUncoded filled
    private static long UncodedGroupCost(RdoqBlock* k, uint blkPos)
    {
        return k->usePsyMask != 0
            ? X265Primitives.PsyRdoQuant12(k->log2TrSize, k->resiDct, k->fencDct, k->costUncoded, k->psyScale, blkPos)
            : X265Primitives.NonPsyRdoQuant(k->log2TrSize, k->resiDct, k->costUncoded, blkPos);
    }

    // a coded-flag-free group before the last: its uncoded cost (returned), the significance costs of its positions
    private static long UncodedGroup(RdoqBlock* k, uint scanPosBase, int patternSigCtx, int ctxSigOffset)
    {
        uint blkPos = k->scan[scanPosBase];
        long u = UncodedGroupCost(k, blkPos);
        byte* tabCnt = X265Entropy.TableCntPtr + (patternSigCtx * 16);
        long lambda2 = k->lambda2;
        int* sigBits0 = k->sigBits0, sigBits1 = k->sigBits1;
        ushort* scan4 = k->scan4;
        for (int y = 0; y < MLS_CG_SIZE; y++)
        {
            for (int x = 0; x < MLS_CG_SIZE; x++)
            {
                uint scanPosOffset = (uint)((y * MLS_CG_SIZE) + x);
                uint ctxSig = (uint)(tabCnt[scan4[scanPosOffset]] + ctxSigOffset);
                k->costSig[scanPosBase + scanPosOffset] = SigCost(lambda2, sigBits0[ctxSig]);
                k->costCoeff[scanPosBase + scanPosOffset] = k->costUncoded[blkPos + x];
                k->sigRateDelta[blkPos + x] = sigBits1[ctxSig] - sigBits0[ctxSig];
            }

            blkPos += k->trSize;
        }

        return u;
    }

    // rateIncUp / rateIncDown of a coded level for the sign hiding
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SignHideRates(RdoqBlock* k, uint blkPos, uint level, uint baseLevel, uint goRiceParam, int* greaterOneBits, int* levelAbsBits, uint c1c2Rate)
    {
        int diff0 = (int)level - 1 - (int)baseLevel;
        int diff2 = (int)level + 1 - (int)baseLevel;
        int maxVlc = X265Tables.GoRiceRangePtr[goRiceParam];
        int rate0, rate1, rate2;
        if (diff0 < -2)
        {
            int rateEqual2 = greaterOneBits[1] + levelAbsBits[0];
            int rateNotEqual2 = greaterOneBits[0];
            rate0 = 0;
            rate2 = rateEqual2;
            rate1 = rateNotEqual2;
        }
        else if (diff0 >= 0 && diff2 <= maxVlc)
        {
            rate1 = GetICRateLessVlc(level + 0, diff0 + 1, goRiceParam);
            rate2 = GetICRateLessVlc(level + 1, diff0 + 2, goRiceParam);
            rate0 = GetICRateLessVlc(level - 1, diff0 + 0, goRiceParam);
        }
        else
        {
            rate1 = GetICRate(level + 0, diff0 + 1, greaterOneBits, levelAbsBits, goRiceParam, (uint)maxVlc, c1c2Rate);
            rate2 = GetICRate(level + 1, diff0 + 2, greaterOneBits, levelAbsBits, goRiceParam, (uint)maxVlc, c1c2Rate);
            rate0 = GetICRate(level - 1, diff0 + 0, greaterOneBits, levelAbsBits, goRiceParam, (uint)maxVlc, c1c2Rate);
        }

        k->rateIncUp[blkPos] = rate2 - rate1;
        k->rateIncDown[blkPos] = rate0 - rate1;
    }

    // refine the last NZ position: the scan position after the best last coefficient (0: none)
    private int RefineLast(RdoqBlock* k, in TUEntropyCodingParameters codeParams, int cgLastScanPos, ulong sigCoeffGroupFlag64, long totalRdCost, long bestCost)
    {
        const uint cgSize = 1 << MLS_CG_SIZE;
        int bestLastIdx = 0;
        bool foundLast = false;
        long* costCoeffGroupSig = this.costCoeffGroupSig;
        for (int cgScanPos = cgLastScanPos; cgScanPos >= 0 && !foundLast; cgScanPos--)
        {
            if (cgScanPos == 0 || cgScanPos == cgLastScanPos)
            {
                // presence inferred
            }
            else if ((sigCoeffGroupFlag64 & (1UL << codeParams.scanCG[cgScanPos])) != 0)
            {
                totalRdCost -= costCoeffGroupSig[cgScanPos];
            }
            else
            {
                totalRdCost -= costCoeffGroupSig[cgScanPos];
                continue;
            }

            for (int scanPosinCG = (int)cgSize - 1; scanPosinCG >= 0; scanPosinCG--)
            {
                uint scanPos = ((uint)cgScanPos * cgSize) + (uint)scanPosinCG;
                if ((int)scanPos > k->lastScanPos)
                {
                    continue;
                }

                uint blkPos = codeParams.scan[scanPos];
                if (k->dstCoeff[blkPos] != 0)
                {
                    uint pos0 = blkPos & (k->trSize - 1), pos1 = blkPos >> k->log2TrSize;
                    if (codeParams.scanType == SCAN_VER)
                    {
                        (pos0, pos1) = (pos1, pos0);
                    }

                    uint bitsLastNZ = 0;
                    for (int i = 0; i < 2; i++)
                    {
                        int temp = X265Tables.LastCoeffTablePtr[i == 0 ? pos0 : pos1];
                        int prefixOnes = temp & 15;
                        int suffixLen = temp >> 4;
                        bitsLastNZ += (uint)k->est->lastBits[(i * 10) + prefixOnes];
                        bitsLastNZ += (uint)(IEP_RATE * suffixLen);
                    }

                    long costAsLast = totalRdCost - k->costSig[scanPos] + SigCost(k->lambda2, bitsLastNZ);
                    if (costAsLast < bestCost)
                    {
                        bestLastIdx = (int)scanPos + 1;
                        bestCost = costAsLast;
                    }

                    if (k->dstCoeff[blkPos] > 1 || m_rdoqLevel == 1)
                    {
                        foundLast = true;
                        break;
                    }

                    totalRdCost -= k->costCoeff[scanPos];
                    totalRdCost += k->costUncoded[blkPos];
                }
                else
                {
                    totalRdCost -= k->costSig[scanPos];
                }
            }
        }

        return bestLastIdx;
    }

    // rate-distortion based sign hiding; returns the updated number of nonzero levels
    private static uint SignHide(RdoqBlock* k, in TUEntropyCodingParameters codeParams, ulong sigCoeffGroupFlag64, int bestLastIdx, uint numSig)
    {
        short* dstCoeff = k->dstCoeff;
        short* resiDct = k->resiDct;
        int step = k->unquantStep, unquantRound = k->unquantRound, unquantShift = k->unquantShift, scaleBits = k->scaleBits;
        long lambda2 = k->lambda2;
        ushort* scan4 = k->scan4;
        int realLastScanPos = (bestLastIdx - 1) >> LOG2_SCAN_SET_SIZE;
        int lastCG = 1;
        for (int subSet = realLastScanPos; subSet >= 0; subSet--)
        {
            int subPos = subSet << LOG2_SCAN_SET_SIZE;
            int n;
            if ((sigCoeffGroupFlag64 & (1UL << codeParams.scanCG[subSet])) == 0)
            {
                continue;
            }

            uint posFirstLast = X265Primitives.FindPosFirstLast(&dstCoeff[codeParams.scan[subPos]], k->trSize, scan4);
            int firstNZPosInCG = (byte)posFirstLast;
            int lastNZPosInCG = (sbyte)(posFirstLast >> 8);
            uint absSumSign = posFirstLast;

            if (lastNZPosInCG - firstNZPosInCG >= SBH_THRESHOLD)
            {
                int signbit = dstCoeff[codeParams.scan[subPos + firstNZPosInCG]];
                if ((signbit ^ (int)absSumSign) < 0)
                {
                    long minCostInc = long.MaxValue, curCost = long.MaxValue;
                    uint minPos = 0;
                    sbyte finalChange = 0;
                    int curChange = 0;
                    uint lastCoeffAdjust = (uint)((lastCG & (Math.Abs((int)dstCoeff[codeParams.scan[lastNZPosInCG + subPos]]) == 1 ? 1 : 0)) * 4 * IEP_RATE);

                    // every position's best change, both candidates computed and selected without branches: up (a
                    // zero level raised to 1 is the same step), down (nonzero levels only)
                    int* rateIncUp = k->rateIncUp, rateIncDown = k->rateIncDown, sigRateDelta = k->sigRateDelta;
                    for (n = lastCG != 0 ? lastNZPosInCG : SCAN_SET_SIZE - 1; n >= 0; --n)
                    {
                        uint blkPos = codeParams.scan[n + subPos];
                        int level = dstCoeff[blkPos];
                        int signCoef = resiDct[blkPos];
                        int sm = signCoef >> 31;
                        int absS = (signCoef ^ sm) - sm;
                        int lm = level >> 31;
                        int absLevel = (level ^ lm) - lm;
                        uint unQuantLevel = (uint)((absLevel * step) + unquantRound);
                        int d = absS - (int)(unQuantLevel >> unquantShift);
                        long origDist = (long)d * d;
                        bool nz = level != 0;
                        bool isOne = absLevel == 1;
                        int extra = IEP_RATE + sigRateDelta[blkPos];
                        int dUp = absS - (int)((unQuantLevel + (uint)step) >> unquantShift);
                        long costUp = DeltaRdCost(lambda2, scaleBits, origDist, dUp, rateIncUp[blkPos] + (extra & ~(int)Mask(nz)));
                        int dDown = absS - (int)((unQuantLevel - (uint)step) >> unquantShift);
                        long costDown = DeltaRdCost(lambda2, scaleBits, origDist, dDown, rateIncDown[blkPos] - (extra & (int)Mask(isOne))) - lastCoeffAdjust;
                        bool up = costUp < costDown;
                        long mUp = Mask(up);
                        long mFirstOne = Mask((n == firstNZPosInCG) & isOne);
                        long downOrMax = (costDown & ~mFirstOne) | (long.MaxValue & mFirstOne);
                        long nzCost = (costUp & mUp) | (downOrMax & ~mUp);
                        long mBlocked = Mask((n < firstNZPosInCG) & ((signbit ^ signCoef) < 0));
                        long zeroCost = (costUp & ~mBlocked) | (long.MaxValue & mBlocked);
                        long mNz = Mask(nz);
                        curCost = (nzCost & mNz) | (zeroCost & ~mNz);
                        curChange = 1 - (2 & (int)(mNz & ~mUp));

                        if (curCost < minCostInc)
                        {
                            minCostInc = curCost;
                            finalChange = (sbyte)curChange;
                            minPos = blkPos + ((uint)absLevel << 16);
                        }

                        lastCoeffAdjust = 0;
                    }

                    int absInMinPos = (int)(minPos >> 16);
                    minPos = (ushort)minPos;
                    if (absInMinPos >= 32767)
                    {
                        finalChange = -1;
                    }

                    numSig = (uint)((int)numSig + (absInMinPos == 0 ? 1 : 0) - ((finalChange == -1 && absInMinPos == 1) ? 1 : 0));
                    short resiCoeffSign = (short)(resiDct[minPos] >> 16);
                    dstCoeff[minPos] += (short)((finalChange ^ resiCoeffSign) - resiCoeffSign);
                }
            }

            lastCG = 0;
        }

        return numSig;
    }
}
