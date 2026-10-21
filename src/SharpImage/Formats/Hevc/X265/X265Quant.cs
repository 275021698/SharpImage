// Port of x265 common/quant.{h,cpp} with flat scaling lists: forward transform + quantization (RDOQ with
// psy-rdoq, or dead-zone quantization with sign-bit hiding), dequantization + inverse transform, and the
// significance-context helpers shared with the entropy coder.
using System;

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
        int lastScanPos = X265Primitives.ScanPosLast(codeParams.scan, coeff, coeffSign, coeffFlag, coeffNum, (int)numSig);
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
    }

    /// <summary>Rate-distortion optimized quantization (Quant::rdoQuant&lt;log2TrSize&gt;).</summary>
    private uint RdoQuant(X265CuData cu, short* dstCoeff, int ttype, uint absPartIdx, bool usePsy, uint log2TrSize)
    {
        int transformShift = MAX_TR_DYNAMIC_RANGE - X265_DEPTH - (int)log2TrSize;
        uint usePsyMask = usePsy ? ~0u : 0u;
        int rem = m_qpParam[ttype].rem;
        int per = m_qpParam[ttype].per;
        int qbits = QUANT_SHIFT + per + transformShift;
        int add = 1 << (qbits - 1);
        int* qCoef = m_quantCoef[rem];
        int numCoeff = 1 << (int)(log2TrSize * 2);
        uint numSig = X265Primitives.NQuant(m_resiDctCoeff, qCoef, dstCoeff, qbits, add, numCoeff);
        if (numSig == 0)
        {
            return 0;
        }

        uint trSize = 1u << (int)log2TrSize;
        long lambda2 = m_qpParam[ttype].lambda2;
        long psyScale = (long)m_psyRdoqScale * m_qpParam[ttype].lambda;
        int* unquantScale = m_dequantCoef[rem];
        int unquantShift = QUANT_IQUANT_SHIFT - QUANT_SHIFT - transformShift;
        int unquantRound = unquantShift > per ? 1 << (unquantShift - per - 1) : 0;
        int scaleBits = SCALE_BITS - (2 * transformShift);
        int psyShift = Math.Max(0, (2 * transformShift) + 1);

        long SIGCOST(long bits) => (lambda2 * bits) >> 8;
        long RDCOST(int d, long bits) => (((long)d * d) << scaleBits) + SIGCOST(bits);
        long PSYVALUE(long rec) => (psyScale * rec) >> psyShift;

        long* costCoeff = this.costCoeff;
        long* costUncoded = this.costUncoded;
        long* costSig = this.costSig;
        int* rateIncUp = this.rateIncUp;
        int* rateIncDown = this.rateIncDown;
        int* sigRateDelta = this.sigRateDelta;
        long* costCoeffGroupSig = this.costCoeffGroupSig;
        ulong sigCoeffGroupFlag64 = 0;
        const uint cgSize = 1 << MLS_CG_SIZE;
        bool bIsLuma = ttype == TEXT_LUMA;
        long totalUncodedCost = 0;
        long totalRdCost = 0;

        var codeParams = default(TUEntropyCodingParameters);
        cu.GetTUEntropyCodingParameters(ref codeParams, absPartIdx, log2TrSize, bIsLuma);
        uint log2TrSizeCG = log2TrSize - 2;
        uint cgNum = 1u << (int)(log2TrSizeCG * 2);
        uint cgStride = trSize >> MLS_CG_LOG2_SIZE;

        byte* coeffNum = stackalloc byte[MLS_GRP_NUM];
        ushort* coeffSign = stackalloc ushort[MLS_GRP_NUM];
        ushort* coeffFlag = stackalloc ushort[MLS_GRP_NUM];
        int lastScanPos = X265Primitives.ScanPosLast(codeParams.scan, dstCoeff, coeffSign, coeffFlag, coeffNum, (int)numSig);
        int cgLastScanPos = lastScanPos >> LOG2_SCAN_SET_SIZE;

        fixed (EstBitsSbac* est = &m_entropyCoder.m_estBitsSbac)
        {
            uint scanPos = 0;
            uint c1 = 1;
            ushort* scan4 = X265Scan.Scan4x4[codeParams.scanType];

            // (x265 zeroes costCoeff / costSig after the last coded group here; nothing reads those entries)

            // uncoded cost of the trailing all-zero groups
            for (int cgScanPos = cgLastScanPos + 1; cgScanPos < (int)cgNum; cgScanPos++)
            {
                uint scanPosBase = (uint)cgScanPos << MLS_CG_SIZE;
                uint blkPos = codeParams.scan[scanPosBase];
                if (usePsyMask != 0)
                {
                    X265Primitives.PsyRdoQuant12((int)log2TrSize, m_resiDctCoeff, m_fencDctCoeff, costUncoded, ref totalUncodedCost, ref totalRdCost, psyScale, blkPos);
                }
                else
                {
                    X265Primitives.NonPsyRdoQuant((int)log2TrSize, m_resiDctCoeff, costUncoded, ref totalUncodedCost, ref totalRdCost, blkPos);
                }
            }

            int* sigBits0 = est->significantBits;
            int* sigBits1 = est->significantBits + NUM_SIG_FLAG_CTX;

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
                    uint scanPosBase = (uint)cgScanPos << MLS_CG_SIZE;
                    uint blkPos = codeParams.scan[scanPosBase];
                    if (usePsyMask != 0)
                    {
                        X265Primitives.PsyRdoQuant12((int)log2TrSize, m_resiDctCoeff, m_fencDctCoeff, costUncoded, ref totalUncodedCost, ref totalRdCost, psyScale, blkPos);
                    }
                    else
                    {
                        X265Primitives.NonPsyRdoQuant((int)log2TrSize, m_resiDctCoeff, costUncoded, ref totalUncodedCost, ref totalRdCost, blkPos);
                    }

                    blkPos = codeParams.scan[scanPosBase];
                    for (int y = 0; y < MLS_CG_SIZE; y++)
                    {
                        for (int x = 0; x < MLS_CG_SIZE; x++)
                        {
                            uint scanPosOffset = (uint)((y * MLS_CG_SIZE) + x);
                            uint ctxSig = (uint)(X265Entropy.TableCnt[(patternSigCtx * 16) + scan4[scanPosOffset]] + ctxSigOffset);
                            costSig[scanPosBase + scanPosOffset] = SIGCOST(sigBits0[ctxSig]);
                            costCoeff[scanPosBase + scanPosOffset] = costUncoded[blkPos + x];
                            sigRateDelta[blkPos + x] = sigBits1[ctxSig] - sigBits0[ctxSig];
                        }

                        blkPos += trSize;
                    }

                    // no coded coefficients in this group
                    uint ctxSigCg = GetSigCoeffGroupCtxInc(sigCoeffGroupFlag64, cgPosX, cgPosY, cgBlkPos, cgStride);
                    costCoeffGroupSig[cgScanPos] = SIGCOST(est->significantCoeffGroupBits[(ctxSigCg * 2) + 0]);
                    totalRdCost += costCoeffGroupSig[cgScanPos];
                    continue;
                }

                CoeffGroupRDStats cgRdStats = default;
                uint subFlagMask = coeffFlag[cgScanPos];
                int c2 = 0;
                uint goRiceParam = 0;
                uint levelThreshold = 3;
                uint c1Idx = 0;
                uint c2Idx = 0;

                for (int scanPosinCG = (int)cgSize - 1; scanPosinCG >= 0; scanPosinCG--)
                {
                    scanPos = ((uint)cgScanPos << MLS_CG_SIZE) + (uint)scanPosinCG;
                    uint blkPos = codeParams.scan[scanPos];
                    uint maxAbsLevel = (uint)dstCoeff[blkPos];
                    int signCoef = m_resiDctCoeff[blkPos];
                    int predictedCoef = m_fencDctCoeff[blkPos] - signCoef;

                    // cost of not coding this coefficient (all distortion, no signal bits)
                    costUncoded[blkPos] = ((long)signCoef * signCoef) << scaleBits;
                    if ((usePsyMask & scanPos) != 0)
                    {
                        costUncoded[blkPos] -= PSYVALUE(predictedCoef);
                    }

                    totalUncodedCost += costUncoded[blkPos];

                    int* greaterOneBits = est->greaterOneBits + ((4 * ctxSet + c1) * 2);
                    ulong ctxCnt = trSize == 4 ? 0x8877886654325410UL : TableCnt64[patternSigCtx];
                    uint ctxSig = blkPos == 0 ? 0 : (uint)(((ctxCnt >> (4 * scan4[scanPosinCG])) & 0xF) + (ulong)ctxSigOffset);

                    if (scanPos > (uint)lastScanPos)
                    {
                        costCoeff[scanPos] = 0;
                        costSig[scanPos] = 0;
                        totalRdCost += costUncoded[blkPos];
                    }
                    else if ((subFlagMask & 1) == 0)
                    {
                        // fast zero coeff path
                        costSig[scanPos] = SIGCOST(sigBits0[ctxSig]);
                        costCoeff[scanPos] = costUncoded[blkPos] + costSig[scanPos];
                        sigRateDelta[blkPos] = sigBits1[ctxSig] - sigBits0[ctxSig];
                        totalRdCost += costCoeff[scanPos];
                        rateIncUp[blkPos] = greaterOneBits[0];
                        subFlagMask >>= 1;
                    }
                    else
                    {
                        subFlagMask >>= 1;
                        uint c1c2idx = ((c1Idx - 8) >> 31) + (uint)((((-(int)c2Idx) >> 31) + 1) * 2);
                        uint baseLevel = (0xD9u >> (int)(c1c2idx * 2)) & 3;   // {1, 2, 1, 3}
                        int* levelAbsBits = est->levelAbsBits + ((ctxSet + c2) * 2);
                        uint c1c2Rate = ((c1c2idx & 1) != 0 ? (uint)greaterOneBits[1] : 0) + (c1c2idx == 3 ? (uint)levelAbsBits[1] : 0);

                        uint level = 0;
                        uint sigCoefBits = 0;
                        costCoeff[scanPos] = long.MaxValue;

                        if ((int)scanPos == lastScanPos)
                        {
                            sigRateDelta[blkPos] = 0;
                        }
                        else
                        {
                            if (maxAbsLevel < 3)
                            {
                                costSig[scanPos] = SIGCOST(sigBits0[ctxSig]);
                                costCoeff[scanPos] = costUncoded[blkPos] + costSig[scanPos];
                            }

                            sigRateDelta[blkPos] = sigBits1[ctxSig] - sigBits0[ctxSig];
                            sigCoefBits = (uint)sigBits1[ctxSig];
                        }

                        uint unQuantLevel = (uint)((maxAbsLevel * (uint)(unquantScale[blkPos] << per)) + unquantRound);
                        if (maxAbsLevel == 1)
                        {
                            uint levelBits = (c1c2idx & 1) != 0 ? (uint)greaterOneBits[0] + IEP_RATE : ((1 + goRiceParam) << 15) + IEP_RATE;
                            int unquantAbsLevel = (int)(unQuantLevel >> unquantShift);
                            int d = Math.Abs(signCoef) - unquantAbsLevel;
                            long curCost = RDCOST(d, sigCoefBits + levelBits);
                            if ((usePsyMask & scanPos) != 0)
                            {
                                int reconCoef = Math.Abs(unquantAbsLevel + ((predictedCoef ^ (signCoef >> 31)) - (signCoef >> 31)));
                                curCost -= PSYVALUE(reconCoef);
                            }

                            if (curCost < costCoeff[scanPos])
                            {
                                level = 1;
                                costCoeff[scanPos] = curCost;
                                costSig[scanPos] = SIGCOST(sigCoefBits);
                            }
                        }
                        else if (maxAbsLevel != 0)
                        {
                            uint levelBits0 = GetICRateCost(maxAbsLevel, (int)(maxAbsLevel - baseLevel), greaterOneBits, levelAbsBits, goRiceParam, c1c2Rate) + IEP_RATE;
                            uint levelBits1 = GetICRateCost(maxAbsLevel - 1, (int)(maxAbsLevel - 1 - baseLevel), greaterOneBits, levelAbsBits, goRiceParam, c1c2Rate) + IEP_RATE;
                            uint preDQuantLevelDiff = (uint)(unquantScale[blkPos] << per);
                            int unquantAbsLevel0 = (int)(unQuantLevel >> unquantShift);
                            int d0 = Math.Abs(signCoef) - unquantAbsLevel0;
                            long curCost0 = RDCOST(d0, sigCoefBits + levelBits0);
                            int unquantAbsLevel1 = (int)((unQuantLevel - preDQuantLevelDiff) >> unquantShift);
                            int d1 = Math.Abs(signCoef) - unquantAbsLevel1;
                            long curCost1 = RDCOST(d1, sigCoefBits + levelBits1);
                            if ((usePsyMask & scanPos) != 0)
                            {
                                int sgn = (predictedCoef ^ (signCoef >> 31)) - (signCoef >> 31);
                                int reconCoef = Math.Abs(unquantAbsLevel0 + sgn);
                                curCost0 -= PSYVALUE(reconCoef);
                                reconCoef = Math.Abs(unquantAbsLevel1 + sgn);
                                curCost1 -= PSYVALUE(reconCoef);
                            }

                            if (curCost0 < costCoeff[scanPos])
                            {
                                level = maxAbsLevel;
                                costCoeff[scanPos] = curCost0;
                                costSig[scanPos] = SIGCOST(sigCoefBits);
                            }

                            if (curCost1 < costCoeff[scanPos])
                            {
                                level = maxAbsLevel - 1;
                                costCoeff[scanPos] = curCost1;
                                costSig[scanPos] = SIGCOST(sigCoefBits);
                            }
                        }

                        dstCoeff[blkPos] = (short)level;
                        totalRdCost += costCoeff[scanPos];

                        // costs for the sign hiding performed at the end
                        if (cu.m_slice.m_pps.bSignHideEnabled && level != 0)
                        {
                            int diff0 = (int)level - 1 - (int)baseLevel;
                            int diff2 = (int)level + 1 - (int)baseLevel;
                            int maxVlc = X265Tables.GoRiceRange[goRiceParam];
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

                            rateIncUp[blkPos] = rate2 - rate1;
                            rateIncDown[blkPos] = rate0 - rate1;
                        }
                        else
                        {
                            rateIncUp[blkPos] = greaterOneBits[0];
                            rateIncDown[blkPos] = 0;
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

                        if (dstCoeff[blkPos] != 0)
                        {
                            sigCoeffGroupFlag64 |= cgBlkPosMask;
                            cgRdStats.codedLevelAndDist += costCoeff[scanPos] - costSig[scanPos];
                            cgRdStats.uncodedDist += costUncoded[blkPos];
                            cgRdStats.nnzBeforePos0 += scanPosinCG;
                        }
                    }

                    cgRdStats.sigCost += costSig[scanPos];
                }

                cgRdStats.sigCost0 = costSig[scanPos];
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
                    long costZeroCG = totalRdCost + SIGCOST(est->significantCoeffGroupBits[(sigCtx * 2) + 0]);
                    costZeroCG += cgRdStats.uncodedDist;
                    costZeroCG -= cgRdStats.codedLevelAndDist;
                    costZeroCG -= cgRdStats.sigCost;
                    costCoeffGroupSig[cgScanPos] = SIGCOST(est->significantCoeffGroupBits[(sigCtx * 2) + 1]);
                    totalRdCost += costCoeffGroupSig[cgScanPos];

                    if (costZeroCG < totalRdCost && m_rdoqLevel > 1)
                    {
                        sigCoeffGroupFlag64 &= ~cgBlkPosMask;
                        totalRdCost = costZeroCG;
                        costCoeffGroupSig[cgScanPos] = SIGCOST(est->significantCoeffGroupBits[(sigCtx * 2) + 0]);
                        uint blkPos0 = codeParams.scan[cgScanPos * cgSize];
                        for (int r = 0; r < 4; r++)
                        {
                            *(long*)&dstCoeff[blkPos0 + (r * trSize)] = 0;
                        }
                    }
                }
                else
                {
                    uint ctxSigCg = GetSigCoeffGroupCtxInc(sigCoeffGroupFlag64, cgPosX, cgPosY, cgBlkPos, cgStride);
                    costCoeffGroupSig[cgScanPos] = SIGCOST(est->significantCoeffGroupBits[(ctxSigCg * 2) + 0]);
                    totalRdCost += costCoeffGroupSig[cgScanPos];
                    totalRdCost -= cgRdStats.sigCost;
                }
            }

            // RD cost of CBF = 0, and the cost of CBF = 1 added to the total
            long bestCost;
            {
                int ctx = (int)CtxCbf[(ttype * 5) + cu.m_tuDepth[absPartIdx]];
                bestCost = totalUncodedCost + SIGCOST(est->blockCbpBits[(ctx * 2) + 0]);
                totalRdCost += SIGCOST(est->blockCbpBits[(ctx * 2) + 1]);
            }

            // refine the last NZ position
            int bestLastIdx = 0;
            bool foundLast = false;
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
                    scanPos = ((uint)cgScanPos * cgSize) + (uint)scanPosinCG;
                    if ((int)scanPos > lastScanPos)
                    {
                        continue;
                    }

                    uint blkPos = codeParams.scan[scanPos];
                    if (dstCoeff[blkPos] != 0)
                    {
                        uint pos0 = blkPos & (trSize - 1), pos1 = blkPos >> (int)log2TrSize;
                        if (codeParams.scanType == SCAN_VER)
                        {
                            (pos0, pos1) = (pos1, pos0);
                        }

                        uint bitsLastNZ = 0;
                        for (int i = 0; i < 2; i++)
                        {
                            int temp = X265Tables.LastCoeffTable[i == 0 ? pos0 : pos1];
                            int prefixOnes = temp & 15;
                            int suffixLen = temp >> 4;
                            bitsLastNZ += (uint)est->lastBits[(i * 10) + prefixOnes];
                            bitsLastNZ += (uint)(IEP_RATE * suffixLen);
                        }

                        long costAsLast = totalRdCost - costSig[scanPos] + SIGCOST(bitsLastNZ);
                        if (costAsLast < bestCost)
                        {
                            bestLastIdx = (int)scanPos + 1;
                            bestCost = costAsLast;
                        }

                        if (dstCoeff[blkPos] > 1 || m_rdoqLevel == 1)
                        {
                            foundLast = true;
                            break;
                        }

                        totalRdCost -= costCoeff[scanPos];
                        totalRdCost += costUncoded[blkPos];
                    }
                    else
                    {
                        totalRdCost -= costSig[scanPos];
                    }
                }
            }

            // recount and re-apply the DCT signs
            numSig = 0;
            for (int pos = 0; pos < bestLastIdx; pos++)
            {
                int blkPos = codeParams.scan[pos];
                int level = dstCoeff[blkPos];
                numSig += level != 0 ? 1u : 0u;
                int mask = m_resiDctCoeff[blkPos] >> 31;
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
                    *(long*)&dstCoeff[blkPos + (r * trSize)] = 0;
                }
            }

            // rate-distortion based sign hiding
            if (cu.m_slice.m_pps.bSignHideEnabled && numSig >= 2)
            {
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

                    uint posFirstLast = X265Primitives.FindPosFirstLast(&dstCoeff[codeParams.scan[subPos]], trSize, scan4);
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

                            for (n = lastCG != 0 ? lastNZPosInCG : SCAN_SET_SIZE - 1; n >= 0; --n)
                            {
                                uint blkPos = codeParams.scan[n + subPos];
                                int signCoef = m_resiDctCoeff[blkPos];
                                int absLevel = Math.Abs((int)dstCoeff[blkPos]);
                                uint preDQuantLevelDiff = (uint)(unquantScale[blkPos] << per);
                                uint unQuantLevel = (uint)((absLevel * (unquantScale[blkPos] << per)) + unquantRound);
                                int d = Math.Abs(signCoef) - (int)(unQuantLevel >> unquantShift);
                                long origDist = (long)d * d;
                                uint isOne = absLevel == 1 ? 1u : 0u;

                                long DELTARDCOST(long d0, int dd, long deltabits) => ((((long)dd * dd) - d0) << scaleBits) + ((lambda2 * deltabits) >> 8);

                                if (dstCoeff[blkPos] != 0)
                                {
                                    d = Math.Abs(signCoef) - (int)((unQuantLevel + preDQuantLevelDiff) >> unquantShift);
                                    long costUp = DELTARDCOST(origDist, d, rateIncUp[blkPos]);
                                    d = Math.Abs(signCoef) - (int)((unQuantLevel - preDQuantLevelDiff) >> unquantShift);
                                    int downBits = rateIncDown[blkPos] - (isOne != 0 ? IEP_RATE + sigRateDelta[blkPos] : 0);
                                    long costDown = DELTARDCOST(origDist, d, downBits);
                                    costDown -= lastCoeffAdjust;
                                    curCost = ((n == firstNZPosInCG) && isOne != 0) ? long.MaxValue : costDown;
                                    curChange = (2 * (costUp < costDown ? 1 : 0)) - 1;
                                    curCost = costUp < costDown ? costUp : curCost;
                                }
                                else if (n < firstNZPosInCG && (signbit ^ signCoef) < 0)
                                {
                                    curCost = long.MaxValue;
                                }
                                else
                                {
                                    d = Math.Abs(signCoef) - (int)((preDQuantLevelDiff + (uint)unquantRound) >> unquantShift);
                                    curCost = DELTARDCOST(origDist, d, rateIncUp[blkPos] + IEP_RATE + sigRateDelta[blkPos]);
                                    curChange = 1;
                                }

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
                            short resiCoeffSign = (short)(m_resiDctCoeff[minPos] >> 16);
                            dstCoeff[minPos] += (short)((finalChange ^ resiCoeffSign) - resiCoeffSign);
                        }
                    }

                    lastCG = 0;
                }
            }
        }

        return numSig;
    }
}
