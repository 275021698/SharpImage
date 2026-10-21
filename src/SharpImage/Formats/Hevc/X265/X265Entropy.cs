// Port of x265 encoder/entropy.{h,cpp}: the CABAC engine (real bitstream or fractional-bit counting when no
// bitstream is attached), CU / transform tree / coefficient syntax, SAO syntax and the RDOQ bit estimates.
// Headers (VPS / SPS / PPS / slice header) are in X265Entropy.Headers.cs.
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
#else
using pixel = byte;
#endif

/// <summary>struct EstBitsSbac (flattened).</summary>
internal unsafe struct EstBitsSbac
{
    public fixed int significantCoeffGroupBits[NUM_SIG_CG_FLAG_CTX * 2];   // [ctx][bin]
    public fixed int significantBits[2 * NUM_SIG_FLAG_CTX];                // [bin][ctx]
    public fixed int lastBits[2 * 10];                                     // [xy][idx]
    public fixed int greaterOneBits[NUM_ONE_FLAG_CTX * 2];                 // [ctx][bin]
    public fixed int levelAbsBits[NUM_ABS_FLAG_CTX * 2];                   // [ctx][bin]
    public fixed int blockCbpBits[NUM_QT_CBF_CTX * 2];                     // [ctx][bin]
    public fixed int blockRootCbpBits[2];
}

/// <summary>struct TURecurse.</summary>
internal struct TURecurse
{
    public const int DONT_SPLIT = 0, VERTICAL_SPLIT = 1, QUAD_SPLIT = 2;
    public uint section;
    public uint splitMode;
    public uint absPartIdxTURelCU;
    public uint absPartIdxStep;

    public TURecurse(int splitType, uint absPartIdxStep, uint absPartIdxTU)
    {
        section = 0;
        absPartIdxTURelCU = absPartIdxTU;
        splitMode = (uint)splitType;
        this.absPartIdxStep = absPartIdxStep >> (int)splitMode;   // partIdxStepShift = {0, 1, 2}
    }

    public bool IsNextSection()
    {
        if (splitMode == DONT_SPLIT)
        {
            section++;
            return false;
        }

        absPartIdxTURelCU += absPartIdxStep;
        section++;
        return section < (1u << (int)splitMode);
    }

    public readonly bool IsLastSection() => (section + 1) >= (1u << (int)splitMode);
}

/// <summary>class Entropy.</summary>
internal sealed unsafe partial class X265Entropy
{
    public readonly byte* m_contextState;
    public uint m_low;
    public uint m_range;
    public uint m_bufferedByte;
    public int m_numBufferedBytes;
    public int m_bitsLeft;
    public ulong m_fracBits;
    public EstBitsSbac m_estBitsSbac;
    public double m_meanQP;
    public X265Bitstream? m_bitIf;

    public X265Entropy(X265Arena arena)
    {
        m_contextState = arena.Alloc<byte>(160);
    }

    public void SetBitstream(X265Bitstream? p) => m_bitIf = p;

    public uint GetNumberOfWrittenBits() => (uint)(m_fracBits >> 15);

    public void ZeroFract() => m_fracBits = 0;

    public void Load(X265Entropy src) => CopyFrom(src);

    public void Store(X265Entropy dest) => dest.CopyFrom(this);

    public void LoadContexts(X265Entropy src) => CopyContextsFrom(src);

    public void LoadIntraDirModeLuma(X265Entropy src)
    {
        m_fracBits = src.m_fracBits;
        m_contextState[OFF_ADI_CTX] = src.m_contextState[OFF_ADI_CTX];
    }

    public void CopyState(X265Entropy other)
    {
        m_low = other.m_low;
        m_range = other.m_range;
        m_bitsLeft = other.m_bitsLeft;
        m_bufferedByte = other.m_bufferedByte;
        m_numBufferedBytes = other.m_numBufferedBytes;
        m_fracBits = other.m_fracBits;
    }

    private void CopyFrom(X265Entropy src)
    {
        CopyState(src);
        Buffer.MemoryCopy(src.m_contextState, m_contextState, MAX_OFF_CTX_MOD, MAX_OFF_CTX_MOD);
    }

    private void CopyContextsFrom(X265Entropy src) => Buffer.MemoryCopy(src.m_contextState, m_contextState, MAX_OFF_CTX_MOD, MAX_OFF_CTX_MOD);

    public void ResetBits()
    {
        m_low = 0;
        m_bitsLeft = -12;
        m_numBufferedBytes = 0;
        m_bufferedByte = 0xff;
        m_fracBits &= 32767;
        m_bitIf?.ResetBits();
    }

    // ---- context initialization ----

    private static byte SbacInit(int qp, int initValue)
    {
        qp = Clip3(QP_MIN, QP_MAX_SPEC, qp);
        int slope = ((initValue >> 4) * 5) - 45;
        int offset = ((initValue & 15) << 3) - 16;
        int initState = Math.Min(Math.Max(1, ((slope * qp) >> 4) + offset), 126);
        uint mpState = initState >= 64 ? 1u : 0u;
        uint state = ((mpState != 0 ? (uint)(initState - 64) : (uint)(63 - initState)) << 1) + mpState;
        return (byte)state;
    }

    private void InitBuffer(int off, int sliceType, int qp, byte[] ctxModel, int size)
    {
        for (int n = 0; n < size; n++)
        {
            m_contextState[off + n] = SbacInit(qp, ctxModel[(sliceType * size) + n]);
        }
    }

    public void ResetEntropy(X265Slice slice)
    {
        int qp = slice.m_sliceQp;
        int st = slice.m_sliceType;
        InitBuffer(OFF_SPLIT_FLAG_CTX, st, qp, X265Tables.InitSplitFlag, NUM_SPLIT_FLAG_CTX);
        InitBuffer(OFF_SKIP_FLAG_CTX, st, qp, X265Tables.InitSkipFlag, NUM_SKIP_FLAG_CTX);
        InitBuffer(OFF_MERGE_FLAG_EXT_CTX, st, qp, X265Tables.InitMergeFlagExt, NUM_MERGE_FLAG_EXT_CTX);
        InitBuffer(OFF_MERGE_IDX_EXT_CTX, st, qp, X265Tables.InitMergeIdxExt, NUM_MERGE_IDX_EXT_CTX);
        InitBuffer(OFF_PART_SIZE_CTX, st, qp, X265Tables.InitPartSize, NUM_PART_SIZE_CTX);
        InitBuffer(OFF_PRED_MODE_CTX, st, qp, X265Tables.InitPredMode, NUM_PRED_MODE_CTX);
        InitBuffer(OFF_ADI_CTX, st, qp, X265Tables.InitIntraPredMode, NUM_ADI_CTX);
        InitBuffer(OFF_CHROMA_PRED_CTX, st, qp, X265Tables.InitChromaPredMode, NUM_CHROMA_PRED_CTX);
        InitBuffer(OFF_DELTA_QP_CTX, st, qp, X265Tables.InitDqp, NUM_DELTA_QP_CTX);
        InitBuffer(OFF_INTER_DIR_CTX, st, qp, X265Tables.InitInterDir, NUM_INTER_DIR_CTX);
        InitBuffer(OFF_REF_NO_CTX, st, qp, X265Tables.InitRefPic, NUM_REF_NO_CTX);
        InitBuffer(OFF_MV_RES_CTX, st, qp, X265Tables.InitMvd, NUM_MV_RES_CTX);
        InitBuffer(OFF_QT_CBF_CTX, st, qp, X265Tables.InitQtCbf, NUM_QT_CBF_CTX);
        InitBuffer(OFF_TRANS_SUBDIV_FLAG_CTX, st, qp, X265Tables.InitTransSubdivFlag, NUM_TRANS_SUBDIV_FLAG_CTX);
        InitBuffer(OFF_QT_ROOT_CBF_CTX, st, qp, X265Tables.InitQtRootCbf, NUM_QT_ROOT_CBF_CTX);
        InitBuffer(OFF_SIG_CG_FLAG_CTX, st, qp, X265Tables.InitSigCgFlag, 2 * NUM_SIG_CG_FLAG_CTX);
        InitBuffer(OFF_SIG_FLAG_CTX, st, qp, X265Tables.InitSigFlag, NUM_SIG_FLAG_CTX);
        InitBuffer(OFF_CTX_LAST_FLAG_X, st, qp, X265Tables.InitLast, NUM_CTX_LAST_FLAG_XY);
        InitBuffer(OFF_CTX_LAST_FLAG_Y, st, qp, X265Tables.InitLast, NUM_CTX_LAST_FLAG_XY);
        InitBuffer(OFF_ONE_FLAG_CTX, st, qp, X265Tables.InitOneFlag, NUM_ONE_FLAG_CTX);
        InitBuffer(OFF_ABS_FLAG_CTX, st, qp, X265Tables.InitAbsFlag, NUM_ABS_FLAG_CTX);
        InitBuffer(OFF_MVP_IDX_CTX, st, qp, X265Tables.InitMvpIdx, NUM_MVP_IDX_CTX);
        InitBuffer(OFF_SAO_MERGE_FLAG_CTX, st, qp, X265Tables.InitSaoMergeFlag, NUM_SAO_MERGE_FLAG_CTX);
        InitBuffer(OFF_SAO_TYPE_IDX_CTX, st, qp, X265Tables.InitSaoTypeIdx, NUM_SAO_TYPE_IDX_CTX);
        InitBuffer(OFF_TRANSFORMSKIP_FLAG_CTX, st, qp, X265Tables.InitTransformskipFlag, 2 * NUM_TRANSFORMSKIP_FLAG_CTX);
        InitBuffer(OFF_TQUANT_BYPASS_FLAG_CTX, st, qp, X265Tables.InitCuTransquantBypassFlag, NUM_TQUANT_BYPASS_FLAG_CTX);
        Start();
    }

    // ---- CABAC engine ----

    public void Start()
    {
        m_low = 0;
        m_range = 510;
        m_bitsLeft = -12;
        m_numBufferedBytes = 0;
        m_bufferedByte = 0xff;
    }

    public void Finish()
    {
        X265Bitstream bs = m_bitIf!;
        if ((m_low >> (21 + m_bitsLeft)) != 0)
        {
            bs.WriteByte(m_bufferedByte + 1);
            while (m_numBufferedBytes > 1)
            {
                bs.WriteByte(0x00);
                m_numBufferedBytes--;
            }

            m_low -= 1u << (21 + m_bitsLeft);
        }
        else
        {
            if (m_numBufferedBytes > 0)
            {
                bs.WriteByte(m_bufferedByte);
            }

            while (m_numBufferedBytes > 1)
            {
                bs.WriteByte(0xff);
                m_numBufferedBytes--;
            }
        }

        bs.Write(m_low >> 8, (uint)(13 + m_bitsLeft));
    }

    public void FinishSlice()
    {
        EncodeBinTrm(1);
        Finish();
        m_bitIf!.WriteByteAlignment();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SbacGetEntropyBits(uint state, uint bin) => X265Tables.EntropyBits[state ^ bin];

    public void EncodeBin(uint binValue, byte* ctxModel)
    {
        uint mstate = *ctxModel;
        *ctxModel = X265Tables.NextState[(mstate * 2) + binValue];
        if (m_bitIf == null)
        {
            m_fracBits += X265Tables.EntropyBits[mstate ^ binValue];
            return;
        }

        uint range = m_range;
        uint state = mstate >> 1;
        uint lps = X265Tables.LpsTable[(state * 4) + ((byte)range >> 6)];
        range -= lps;
        int numBits = (int)((range - 256) >> 31);
        uint low = m_low;
        if (((binValue ^ mstate) & 1) != 0)
        {
            int idx = Bsr(lps);
            numBits = 8 - idx;
            if (state >= 63)
            {
                numBits = 6;
            }

            low += range;
            range = lps;
        }

        m_low = low << numBits;
        m_range = range << numBits;
        m_bitsLeft += numBits;
        if (m_bitsLeft >= 0)
        {
            WriteOut();
        }
    }

    public void EncodeBinEP(uint binValue)
    {
        if (m_bitIf == null)
        {
            m_fracBits += 32768;
            return;
        }

        m_low <<= 1;
        if (binValue != 0)
        {
            m_low += m_range;
        }

        m_bitsLeft++;
        if (m_bitsLeft >= 0)
        {
            WriteOut();
        }
    }

    public void EncodeBinsEP(uint binValues, int numBins)
    {
        if (m_bitIf == null)
        {
            m_fracBits += 32768u * (uint)numBins;
            return;
        }

        while (numBins > 8)
        {
            numBins -= 8;
            uint pattern = binValues >> numBins;
            m_low <<= 8;
            m_low += m_range * pattern;
            binValues -= pattern << numBins;
            m_bitsLeft += 8;
            if (m_bitsLeft >= 0)
            {
                WriteOut();
            }
        }

        m_low <<= numBins;
        m_low += m_range * binValues;
        m_bitsLeft += numBins;
        if (m_bitsLeft >= 0)
        {
            WriteOut();
        }
    }

    public void EncodeBinTrm(uint binValue)
    {
        if (m_bitIf == null)
        {
            m_fracBits += X265Tables.EntropyBits[126 ^ binValue];
            return;
        }

        m_range -= 2;
        if (binValue != 0)
        {
            m_low += m_range;
            m_low <<= 7;
            m_range = 2 << 7;
            m_bitsLeft += 7;
        }
        else if (m_range >= 256)
        {
            return;
        }
        else
        {
            m_low <<= 1;
            m_range <<= 1;
            m_bitsLeft++;
        }

        if (m_bitsLeft >= 0)
        {
            WriteOut();
        }
    }

    private void WriteOut()
    {
        uint leadByte = m_low >> (13 + m_bitsLeft);
        uint lowMask = 0xFFFFFFFFu >> (11 + 8 - m_bitsLeft);
        m_bitsLeft -= 8;
        m_low &= lowMask;
        if (leadByte == 0xff)
        {
            m_numBufferedBytes++;
        }
        else
        {
            int numBufferedBytes = m_numBufferedBytes;
            if (numBufferedBytes > 0)
            {
                uint carry = leadByte >> 8;
                uint byteTowrite = m_bufferedByte + carry;
                m_bitIf!.WriteByte(byteTowrite);
                byteTowrite = (0xff + carry) & 0xff;
                while (numBufferedBytes > 1)
                {
                    m_bitIf.WriteByte(byteTowrite);
                    numBufferedBytes--;
                }
            }

            m_numBufferedBytes = 1;
            m_bufferedByte = (byte)leadByte;
        }
    }

    /// <summary>The bits of a context-coded bin, without updating the context.</summary>
    public uint BitsCodeBin(uint binValue, uint ctxModel)
    {
        ulong fracBits = (m_fracBits & 32767) + X265Tables.EntropyBits[ctxModel ^ binValue];
        return (uint)(fracBits >> 15);
    }

    public uint BitsIntraModeNonMPM() => BitsCodeBin(0, m_contextState[OFF_ADI_CTX]) + 5;

    public uint BitsIntraModeMPM(uint* preds, uint dir) => BitsCodeBin(1, m_contextState[OFF_ADI_CTX]) + (dir == preds[0] ? 1u : 2u);

    // ---- simple syntax elements ----

    public void CodeSaoMerge(uint code) => EncodeBin(code, m_contextState + OFF_SAO_MERGE_FLAG_CTX);

    public void CodeSplitFlag(X265CuData cu, uint absPartIdx, uint depth) =>
        EncodeBin(cu.m_cuDepth[absPartIdx] > depth ? 1u : 0u, m_contextState + OFF_SPLIT_FLAG_CTX + cu.GetCtxSplitFlag(absPartIdx, depth));

    public void CodeTransformSubdivFlag(uint symbol, uint ctx) => EncodeBin(symbol, m_contextState + OFF_TRANS_SUBDIV_FLAG_CTX + ctx);

    public void CodeCUTransquantBypassFlag(uint symbol) => EncodeBin(symbol, m_contextState + OFF_TQUANT_BYPASS_FLAG_CTX);

    public void CodeQtCbfLuma(uint cbf, uint tuDepth) => EncodeBin(cbf, m_contextState + OFF_QT_CBF_CTX + (tuDepth == 0 ? 1 : 0));

    public void CodeTransformSkipFlags(uint transformSkip, int ttype) =>
        EncodeBin(transformSkip, m_contextState + OFF_TRANSFORMSKIP_FLAG_CTX + (ttype != 0 ? NUM_TRANSFORMSKIP_FLAG_CTX : 0));

    // ---- CU syntax ----

    public void EncodeCTU(X265CuData ctu, CUGeom* cuGeom)
    {
        bool bEncodeDQP = ctu.m_slice.m_pps.bUseDQP;
        EncodeCU(ctu, cuGeom, 0, 0, ref bEncodeDQP);
    }

    private void EncodeCU(X265CuData ctu, CUGeom* cuGeom, uint absPartIdx, uint depth, ref bool bEncodeDQP)
    {
        X265Slice slice = ctu.m_slice;
        bool cuSplitFlag = (cuGeom->flags & CUGeom.LEAF) == 0;
        bool cuUnsplitFlag = (cuGeom->flags & CUGeom.SPLIT_MANDATORY) == 0;

        if (!cuUnsplitFlag)
        {
            uint qNumParts = cuGeom->numPartitions >> 2;
            if (depth == slice.m_pps.maxCuDQPDepth && slice.m_pps.bUseDQP)
            {
                bEncodeDQP = true;
            }

            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                CUGeom* childGeom = cuGeom + cuGeom->childOffset + qIdx;
                if ((childGeom->flags & CUGeom.PRESENT) != 0)
                {
                    EncodeCU(ctu, childGeom, absPartIdx, depth + 1, ref bEncodeDQP);
                }
            }

            return;
        }

        if (cuSplitFlag)
        {
            CodeSplitFlag(ctu, absPartIdx, depth);
        }

        if (depth < ctu.m_cuDepth[absPartIdx] && depth < (uint)ctu.m_encData.m_param.maxCUDepth)
        {
            uint qNumParts = cuGeom->numPartitions >> 2;
            if (depth == slice.m_pps.maxCuDQPDepth && slice.m_pps.bUseDQP)
            {
                bEncodeDQP = true;
            }

            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                CUGeom* childGeom = cuGeom + cuGeom->childOffset + qIdx;
                EncodeCU(ctu, childGeom, absPartIdx, depth + 1, ref bEncodeDQP);
            }

            return;
        }

        if (depth <= slice.m_pps.maxCuDQPDepth && slice.m_pps.bUseDQP)
        {
            bEncodeDQP = true;
        }

        if (slice.m_pps.bTransquantBypassEnabled)
        {
            CodeCUTransquantBypassFlag(ctu.m_tqBypass[absPartIdx]);
        }

        // intra slices only: no skip flag / pred mode
        CodePartSize(ctu, absPartIdx, depth);
        CodePredInfo(ctu, absPartIdx);

        uint* tuDepthRange = stackalloc uint[2];
        ctu.GetIntraTUQtDepthRange(tuDepthRange, absPartIdx);
        CodeCoeff(ctu, absPartIdx, ref bEncodeDQP, tuDepthRange);
        FinishCU(ctu, absPartIdx, depth, bEncodeDQP);
    }

    private void FinishCU(X265CuData ctu, uint absPartIdx, uint depth, bool bCodeDQP)
    {
        X265Slice slice = ctu.m_slice;
        uint realEndAddress = slice.m_endCUAddr;
        uint cuAddr = ctu.GetSCUAddr() + absPartIdx;
        uint granularityMask = (uint)ctu.m_encData.m_param.maxCUSize - 1;
        uint cuSize = 1u << ctu.m_log2CUSize[absPartIdx];
        uint rpelx = ctu.m_cuPelX + X265Tables.ZscanToPelX[absPartIdx] + cuSize;
        uint bpely = ctu.m_cuPelY + X265Tables.ZscanToPelY[absPartIdx] + cuSize;
        bool granularityBoundary = ((rpelx & granularityMask) == 0 || rpelx == slice.m_sps.picWidthInLumaSamples) &&
                                   ((bpely & granularityMask) == 0 || bpely == slice.m_sps.picHeightInLumaSamples);

        if (slice.m_pps.bUseDQP)
        {
            ctu.SetQPSubParts(bCodeDQP ? ctu.GetRefQP(absPartIdx) : ctu.m_qp[absPartIdx], absPartIdx, depth);
        }

        if (granularityBoundary)
        {
            uint bTerminateSlice = ctu.m_bLastCuInSlice;
            if (cuAddr + ((uint)slice.m_param.num4x4Partitions >> (int)(depth << 1)) == realEndAddress)
            {
                bTerminateSlice = 1;
            }

            // the 1-terminating bit is added to all streams, so don't add it here when it's 1
            if (bTerminateSlice == 0)
            {
                EncodeBinTrm(0);
            }

            if (m_bitIf == null)
            {
                ResetBits();
            }
        }
    }

    public void CodePartSize(X265CuData cu, uint absPartIdx, uint depth)
    {
        int partSize = cu.m_partSize[absPartIdx];
        // intra
        if (depth == (uint)cu.m_encData.m_param.maxCUDepth)
        {
            EncodeBin(partSize == SIZE_2Nx2N ? 1u : 0u, m_contextState + OFF_PART_SIZE_CTX);
        }
    }

    public void CodePredInfo(X265CuData cu, uint absPartIdx)
    {
        CodeIntraDirLumaAng(cu, absPartIdx, true);
        if (cu.m_chromaFormat != X265_CSP_I400)
        {
            uint* chromaDirMode = stackalloc uint[NUM_CHROMA_MODE];
            cu.GetAllowedChromaDir(absPartIdx, chromaDirMode);
            CodeIntraDirChroma(cu, absPartIdx, chromaDirMode);
        }
    }

    public void CodeIntraDirLumaAng(X265CuData cu, uint absPartIdx, bool isMultiple)
    {
        uint* dir = stackalloc uint[4];
        uint* preds = stackalloc uint[12];
        int* predIdx = stackalloc int[4];
        uint partNum = isMultiple && cu.m_partSize[absPartIdx] != SIZE_2Nx2N ? 4u : 1u;
        uint qNumParts = 1u << ((cu.m_log2CUSize[absPartIdx] - 1 - LOG2_UNIT_SIZE) * 2);

        uint j;
        for (j = 0; j < partNum; j++, absPartIdx += qNumParts)
        {
            dir[j] = cu.m_lumaIntraDir[absPartIdx];
            cu.GetIntraDirLumaPredictor(absPartIdx, preds + (j * 3));
            predIdx[j] = -1;
            for (int i = 0; i < 3; i++)
            {
                if (dir[j] == preds[(j * 3) + i])
                {
                    predIdx[j] = i;
                }
            }

            EncodeBin(predIdx[j] != -1 ? 1u : 0u, m_contextState + OFF_ADI_CTX);
        }

        for (j = 0; j < partNum; j++)
        {
            if (predIdx[j] != -1)
            {
                uint nonzero = predIdx[j] != 0 ? 1u : 0u;
                EncodeBinsEP((uint)predIdx[j] + nonzero, (int)(1 + nonzero));
            }
            else
            {
                uint* p = preds + (j * 3);
                if (p[0] > p[1]) (p[0], p[1]) = (p[1], p[0]);
                if (p[0] > p[2]) (p[0], p[2]) = (p[2], p[0]);
                if (p[1] > p[2]) (p[1], p[2]) = (p[2], p[1]);
                dir[j] += dir[j] > p[2] ? unchecked((uint)-1) : 0;
                dir[j] += dir[j] > p[1] ? unchecked((uint)-1) : 0;
                dir[j] += dir[j] > p[0] ? unchecked((uint)-1) : 0;
                EncodeBinsEP(dir[j], 5);
            }
        }
    }

    public void CodeIntraDirChroma(X265CuData cu, uint absPartIdx, uint* chromaDirMode)
    {
        uint intraDirChroma = cu.m_chromaIntraDir[absPartIdx];
        if (intraDirChroma == DM_CHROMA_IDX)
        {
            EncodeBin(0, m_contextState + OFF_CHROMA_PRED_CTX);
        }
        else
        {
            for (uint i = 0; i < NUM_CHROMA_MODE - 1; i++)
            {
                if (intraDirChroma == chromaDirMode[i])
                {
                    intraDirChroma = i;
                    break;
                }
            }

            EncodeBin(1, m_contextState + OFF_CHROMA_PRED_CTX);
            EncodeBinsEP(intraDirChroma, 2);
        }
    }

    public void CodeCoeff(X265CuData cu, uint absPartIdx, ref bool bCodeDQP, uint* depthRange)
    {
        uint log2CUSize = cu.m_log2CUSize[absPartIdx];
        if (cu.m_chromaFormat == X265_CSP_I400)
        {
            EncodeTransformLuma(cu, absPartIdx, 0, log2CUSize, ref bCodeDQP, depthRange);
        }
        else
        {
            EncodeTransform(cu, absPartIdx, 0, log2CUSize, ref bCodeDQP, depthRange);
        }
    }

    private void EncodeTransform(X265CuData cu, uint absPartIdx, uint curDepth, uint log2CurSize, ref bool bCodeDQP, uint* depthRange)
    {
        bool subdiv = cu.m_tuDepth[absPartIdx] > curDepth;

        // the subdiv flag is implied (not signaled) for intra NxN at the minimum CU size, TUs larger than the
        // maximum and the minimum TU size
        if (cu.IsIntra(absPartIdx) && cu.m_partSize[absPartIdx] != SIZE_2Nx2N && log2CurSize == MIN_LOG2_CU_SIZE)
        {
        }
        else if (log2CurSize > depthRange[1])
        {
        }
        else if (log2CurSize == cu.m_slice.m_sps.quadtreeTULog2MinSize || log2CurSize == depthRange[0])
        {
        }
        else
        {
            CodeTransformSubdivFlag(subdiv ? 1u : 0u, 5 - log2CurSize);
        }

        int hChromaShift = cu.m_hChromaShift;
        int vChromaShift = cu.m_vChromaShift;
        bool bSmallChroma = ((int)log2CurSize - hChromaShift) < 2;
        if (curDepth == 0 || !bSmallChroma)
        {
            uint parentIdx = absPartIdx & (0xFFu << (int)((log2CurSize + 1 - LOG2_UNIT_SIZE) * 2));
            if (curDepth == 0 || cu.GetCbf(parentIdx, TEXT_CHROMA_U, curDepth - 1) != 0)
            {
                CodeQtCbfChroma(cu, absPartIdx, TEXT_CHROMA_U, curDepth, !subdiv);
            }

            if (curDepth == 0 || cu.GetCbf(parentIdx, TEXT_CHROMA_V, curDepth - 1) != 0)
            {
                CodeQtCbfChroma(cu, absPartIdx, TEXT_CHROMA_V, curDepth, !subdiv);
            }
        }

        if (subdiv)
        {
            --log2CurSize;
            ++curDepth;
            uint qNumParts = 1u << (int)((log2CurSize - LOG2_UNIT_SIZE) * 2);
            EncodeTransform(cu, absPartIdx + (0 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            EncodeTransform(cu, absPartIdx + (1 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            EncodeTransform(cu, absPartIdx + (2 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            EncodeTransform(cu, absPartIdx + (3 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            return;
        }

        uint absPartIdxC = bSmallChroma ? absPartIdx & 0xFC : absPartIdx;
        CodeQtCbfLuma(cu.GetCbf(absPartIdx, TEXT_LUMA, curDepth), curDepth);

        uint cbfY = cu.GetCbf(absPartIdx, TEXT_LUMA, curDepth);
        uint cbfU = cu.GetCbf(absPartIdxC, TEXT_CHROMA_U, curDepth);
        uint cbfV = cu.GetCbf(absPartIdxC, TEXT_CHROMA_V, curDepth);
        if ((cbfY | cbfU | cbfV) == 0)
        {
            return;
        }

        if (cu.m_slice.m_pps.bUseDQP && bCodeDQP)
        {
            uint log2CUSize = cu.m_log2CUSize[absPartIdx];
            uint absPartIdxLT = absPartIdx & (0xFFu << (int)((log2CUSize - LOG2_UNIT_SIZE) * 2));
            CodeDeltaQP(cu, absPartIdxLT);
            bCodeDQP = false;
        }

        if (cbfY != 0)
        {
            uint coeffOffset = absPartIdx << (LOG2_UNIT_SIZE * 2);
            CodeCoeffNxN(cu, cu.m_trCoeff[0] + coeffOffset, absPartIdx, log2CurSize, TEXT_LUMA);
            if ((cbfU | cbfV) == 0)
            {
                return;
            }
        }

        if (bSmallChroma)
        {
            if ((absPartIdx & 3) != 3)
            {
                return;
            }

            const uint log2CurSizeC = 2;
            const uint curPartNum = 4;
            uint coeffOffsetC = absPartIdxC << (LOG2_UNIT_SIZE * 2 - (hChromaShift + vChromaShift));
            for (int chromaId = TEXT_CHROMA_U; chromaId <= TEXT_CHROMA_V; chromaId++)
            {
                var tuIterator = new TURecurse(TURecurse.DONT_SPLIT, curPartNum, absPartIdxC);
                short* coeffChroma = cu.m_trCoeff[chromaId];
                do
                {
                    if (cu.GetCbf(tuIterator.absPartIdxTURelCU, chromaId, curDepth) != 0)
                    {
                        uint subTUOffset = tuIterator.section << (int)(log2CurSizeC * 2);
                        CodeCoeffNxN(cu, coeffChroma + coeffOffsetC + subTUOffset, tuIterator.absPartIdxTURelCU, log2CurSizeC, chromaId);
                    }
                }
                while (tuIterator.IsNextSection());
            }
        }
        else
        {
            uint log2CurSizeC = log2CurSize - (uint)hChromaShift;
            uint curPartNum = 1u << (int)((log2CurSize - LOG2_UNIT_SIZE) * 2);
            uint coeffOffsetC = absPartIdxC << (LOG2_UNIT_SIZE * 2 - (hChromaShift + vChromaShift));
            for (int chromaId = TEXT_CHROMA_U; chromaId <= TEXT_CHROMA_V; chromaId++)
            {
                var tuIterator = new TURecurse(TURecurse.DONT_SPLIT, curPartNum, absPartIdxC);
                short* coeffChroma = cu.m_trCoeff[chromaId];
                do
                {
                    if (cu.GetCbf(tuIterator.absPartIdxTURelCU, chromaId, curDepth) != 0)
                    {
                        uint subTUOffset = tuIterator.section << (int)(log2CurSizeC * 2);
                        CodeCoeffNxN(cu, coeffChroma + coeffOffsetC + subTUOffset, tuIterator.absPartIdxTURelCU, log2CurSizeC, chromaId);
                    }
                }
                while (tuIterator.IsNextSection());
            }
        }
    }

    private void EncodeTransformLuma(X265CuData cu, uint absPartIdx, uint curDepth, uint log2CurSize, ref bool bCodeDQP, uint* depthRange)
    {
        bool subdiv = cu.m_tuDepth[absPartIdx] > curDepth;
        if (cu.IsIntra(absPartIdx) && cu.m_partSize[absPartIdx] != SIZE_2Nx2N && log2CurSize == MIN_LOG2_CU_SIZE)
        {
        }
        else if (log2CurSize > depthRange[1])
        {
        }
        else if (log2CurSize == cu.m_slice.m_sps.quadtreeTULog2MinSize || log2CurSize == depthRange[0])
        {
        }
        else
        {
            CodeTransformSubdivFlag(subdiv ? 1u : 0u, 5 - log2CurSize);
        }

        if (subdiv)
        {
            --log2CurSize;
            ++curDepth;
            uint qNumParts = 1u << (int)((log2CurSize - LOG2_UNIT_SIZE) * 2);
            EncodeTransformLuma(cu, absPartIdx + (0 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            EncodeTransformLuma(cu, absPartIdx + (1 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            EncodeTransformLuma(cu, absPartIdx + (2 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            EncodeTransformLuma(cu, absPartIdx + (3 * qNumParts), curDepth, log2CurSize, ref bCodeDQP, depthRange);
            return;
        }

        CodeQtCbfLuma(cu.GetCbf(absPartIdx, TEXT_LUMA, curDepth), curDepth);
        uint cbfY = cu.GetCbf(absPartIdx, TEXT_LUMA, curDepth);
        if (cbfY == 0)
        {
            return;
        }

        if (cu.m_slice.m_pps.bUseDQP && bCodeDQP)
        {
            uint log2CUSize = cu.m_log2CUSize[absPartIdx];
            uint absPartIdxLT = absPartIdx & (0xFFu << (int)((log2CUSize - LOG2_UNIT_SIZE) * 2));
            CodeDeltaQP(cu, absPartIdxLT);
            bCodeDQP = false;
        }

        uint coeffOffset = absPartIdx << (LOG2_UNIT_SIZE * 2);
        CodeCoeffNxN(cu, cu.m_trCoeff[0] + coeffOffset, absPartIdx, log2CurSize, TEXT_LUMA);
    }

    public void CodeDeltaQP(X265CuData cu, uint absPartIdx)
    {
        int dqp = cu.m_qp[absPartIdx] - cu.GetRefQP(absPartIdx);
        const int qpBdOffsetY = QP_BD_OFFSET;
        dqp = ((dqp + 78 + qpBdOffsetY + (qpBdOffsetY / 2)) % (52 + qpBdOffsetY)) - 26 - (qpBdOffsetY / 2);
        uint absDQp = (uint)(dqp > 0 ? dqp : -dqp);
        uint tuValue = Math.Min(absDQp, CU_DQP_TU_CMAX);
        WriteUnaryMaxSymbol(tuValue, m_contextState + OFF_DELTA_QP_CTX, 1, CU_DQP_TU_CMAX);
        if (absDQp >= CU_DQP_TU_CMAX)
        {
            WriteEpExGolomb(absDQp - CU_DQP_TU_CMAX, CU_DQP_EG_k);
        }

        if (absDQp > 0)
        {
            EncodeBinEP(dqp > 0 ? 0u : 1u);
        }
    }

    public void CodeQtCbfChroma(X265CuData cu, uint absPartIdx, int ttype, uint tuDepth, bool lowestLevel)
    {
        uint ctx = tuDepth + 2;
        uint log2TrSize = cu.m_log2CUSize[absPartIdx] - tuDepth;
        bool canQuadSplit = ((int)log2TrSize - cu.m_hChromaShift) > 2;
        uint lowestTUDepth = tuDepth + ((!lowestLevel && !canQuadSplit) ? 1u : 0u);
        EncodeBin(cu.GetCbf(absPartIdx, ttype, lowestTUDepth), m_contextState + OFF_QT_CBF_CTX + ctx);
    }

    private void WriteUnaryMaxSymbol(uint symbol, byte* scmModel, int offset, uint maxSymbol)
    {
        EncodeBin(symbol != 0 ? 1u : 0u, scmModel);
        if (symbol == 0)
        {
            return;
        }

        bool bCodeLast = maxSymbol > symbol;
        while (--symbol != 0)
        {
            EncodeBin(1, scmModel + offset);
        }

        if (bCodeLast)
        {
            EncodeBin(0, scmModel + offset);
        }
    }

    private void WriteEpExGolomb(uint symbol, int count)
    {
        uint bins = 0;
        int numBins = 0;
        while (symbol >= (1u << count))
        {
            bins = (2 * bins) + 1;
            numBins++;
            symbol -= 1u << count;
            count++;
        }

        bins = (2 * bins) + 0;
        numBins++;
        bins = (bins << count) | symbol;
        numBins += count;
        EncodeBinsEP(bins, numBins);
    }

    private void WriteCoefRemainExGolomb(uint codeNumber, uint absGoRice)
    {
        uint length;
        uint codeRemain = codeNumber & ((1u << (int)absGoRice) - 1);
        if ((codeNumber >> (int)absGoRice) < COEF_REMAIN_BIN_REDUCTION)
        {
            length = codeNumber >> (int)absGoRice;
            EncodeBinsEP((((1u << (int)(length + 1)) - 2) << (int)absGoRice) + codeRemain, (int)(length + 1 + absGoRice));
        }
        else
        {
            codeNumber = (codeNumber >> (int)absGoRice) - COEF_REMAIN_BIN_REDUCTION;
            int idx = Bsr(codeNumber + 1);
            length = (uint)idx;
            codeNumber -= (1u << idx) - 1;
            codeNumber = (codeNumber << (int)absGoRice) + codeRemain;
            EncodeBinsEP((1u << (int)(COEF_REMAIN_BIN_REDUCTION + length + 1)) - 2, (int)(COEF_REMAIN_BIN_REDUCTION + length + 1));
            EncodeBinsEP(codeNumber, (int)(length + absGoRice));
        }
    }

    // [patternSigCtx][posXinSubset][posYinSubset] (codeCoeffNxN / rdoQuant table_cnt)
    internal static readonly byte[] TableCnt =
    {
        2, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 1, 0, 0, 2, 1, 0, 0, 2, 1, 0, 0, 2, 1, 0, 0,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8,
    };

    internal static readonly byte* TableCntPtr = Pin(TableCnt);

    private static byte* Pin(byte[] a)
    {
        byte* p = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)a.Length);
        for (int i = 0; i < a.Length; i++) p[i] = a[i];
        return p;
    }

    public void CodeCoeffNxN(X265CuData cu, short* coeff, uint absPartIdx, uint log2TrSize, int ttype)
    {
        uint trSize = 1u << (int)log2TrSize;
        uint tqBypass = cu.m_tqBypass[absPartIdx];
        uint numSig = (uint)X265Primitives.CountNonzero(coeff, (int)(trSize * trSize));
        bool bHideFirstSign = cu.m_slice.m_pps.bSignHideEnabled & tqBypass == 0;

        if (log2TrSize <= MAX_LOG2_TS_SIZE && tqBypass == 0 && cu.m_slice.m_pps.bTransformSkipEnabled)
        {
            CodeTransformSkipFlags(cu.m_transformSkip[ttype][absPartIdx], ttype);
        }

        bool bIsLuma = ttype == TEXT_LUMA;
        var codingParameters = default(TUEntropyCodingParameters);
        cu.GetTUEntropyCodingParameters(ref codingParameters, absPartIdx, log2TrSize, bIsLuma);

        byte* coeffNum = stackalloc byte[MLS_GRP_NUM];
        ushort* coeffSign = stackalloc ushort[MLS_GRP_NUM];
        ushort* coeffFlag = stackalloc ushort[MLS_GRP_NUM];

        ulong sigCoeffGroupFlag64 = 0;
        int scanPosLast = X265Primitives.ScanPosLast(codingParameters.scan, coeff, coeffSign, coeffFlag, coeffNum, (int)numSig);
        uint posLast = codingParameters.scan[scanPosLast];
        int lastScanSet = scanPosLast >> MLS_CG_SIZE;

        // CG non-zero mask; the last CG is always flagged in the CG scan loop
        for (int idx = 0; idx < lastScanSet; idx++)
        {
            byte subSet = (byte)codingParameters.scanCG[idx];
            ulong nonZero = coeffNum[idx] != 0 ? 1UL : 0UL;
            sigCoeffGroupFlag64 |= nonZero << subSet;
        }

        // position of the last coefficient: context coded prefixes, then the bypass suffixes of x and y together
        {
            uint packedSuffixBits = 0, packedSuffixLen = 0;
            uint pos0 = posLast & (trSize - 1), pos1 = posLast >> (int)log2TrSize;
            if (codingParameters.scanType == SCAN_VER)
            {
                (pos0, pos1) = (pos1, pos0);
            }

            int ctxIdx = bIsLuma ? (3 * ((int)log2TrSize - 2)) + (log2TrSize == 5 ? 1 : 0) : NUM_CTX_LAST_FLAG_XY_LUMA;
            int ctxShift = bIsLuma ? (log2TrSize > 2 ? 1 : 0) : (int)log2TrSize - 2;
            uint maxGroupIdx = (log2TrSize << 1) - 1;
            byte* ctx = m_contextState + OFF_CTX_LAST_FLAG_X;
            for (uint i = 0; i < 2; i++, ctxIdx += NUM_CTX_LAST_FLAG_XY)
            {
                uint pos = i == 0 ? pos0 : pos1;
                uint temp = X265Tables.LastCoeffTable[pos];
                uint prefixOnes = temp & 15;
                uint suffixLen = temp >> 4;
                for (uint ctxLast = 0; ctxLast < prefixOnes; ctxLast++)
                {
                    EncodeBin(1, ctx + ctxIdx + (ctxLast >> ctxShift));
                }

                if (prefixOnes < maxGroupIdx)
                {
                    EncodeBin(0, ctx + ctxIdx + (prefixOnes >> ctxShift));
                }

                packedSuffixBits <<= (int)suffixLen;
                packedSuffixBits |= pos & ((1u << (int)suffixLen) - 1);
                packedSuffixLen += suffixLen;
            }

            EncodeBinsEP(packedSuffixBits, (int)packedSuffixLen);
        }

        byte* baseCoeffGroupCtx = m_contextState + OFF_SIG_CG_FLAG_CTX + (bIsLuma ? 0 : NUM_SIG_CG_FLAG_CTX);
        byte* baseCtx = bIsLuma ? m_contextState + OFF_SIG_FLAG_CTX : m_contextState + OFF_SIG_FLAG_CTX + NUM_SIG_FLAG_CTX_LUMA;
        uint c1 = 1;
        int scanPosSigOff = scanPosLast - (lastScanSet << MLS_CG_SIZE) - 1;
        ushort* absCoeff = stackalloc ushort[(1 << MLS_CG_SIZE) + 2];
        ushort* tmpCoeff = stackalloc ushort[SCAN_SET_SIZE];
        uint numNonZero = 1;
        absCoeff[0] = (ushort)Math.Abs((int)coeff[posLast]);

        for (int subSet = lastScanSet; subSet >= 0; subSet--)
        {
            uint subCoeffFlag = coeffFlag[subSet];
            uint scanFlagMask = subCoeffFlag;
            int subPosBase = subSet << MLS_CG_SIZE;
            if (subSet == lastScanSet)
            {
                scanFlagMask >>= 1;
            }

            // significant_coeffgroup_flag
            int cgBlkPos = codingParameters.scanCG[subSet];
            int cgPosY = (int)((uint)cgBlkPos >> (int)(log2TrSize - MLS_CG_LOG2_SIZE));
            int cgPosX = cgBlkPos & ((1 << (int)(log2TrSize - MLS_CG_LOG2_SIZE)) - 1);
            ulong cgBlkPosMask = 1UL << cgBlkPos;
            if (subSet == lastScanSet || subSet == 0)
            {
                sigCoeffGroupFlag64 |= cgBlkPosMask;
            }
            else
            {
                uint sigCoeffGroup = (sigCoeffGroupFlag64 & cgBlkPosMask) != 0 ? 1u : 0u;
                uint ctxSig = X265Quant.GetSigCoeffGroupCtxInc(sigCoeffGroupFlag64, (uint)cgPosX, (uint)cgPosY, (uint)cgBlkPos, trSize >> MLS_CG_LOG2_SIZE);
                EncodeBin(sigCoeffGroup, baseCoeffGroupCtx + ctxSig);
            }

            // significant_coeff_flag
            if (scanPosSigOff >= 0 && (sigCoeffGroupFlag64 & cgBlkPosMask) != 0)
            {
                int patternSigCtx = (int)X265Quant.CalcPatternSigCtx(sigCoeffGroupFlag64, (uint)cgPosX, (uint)cgPosY, (uint)cgBlkPos, trSize >> MLS_CG_LOG2_SIZE);
                uint posOffset = (bIsLuma && subSet != 0) ? 3u : 0u;
                int offset = (int)codingParameters.firstSignificanceMapContext;
                uint blkPosBase = codingParameters.scan[subPosBase];
                ushort* scan4 = X265Scan.Scan4x4[codingParameters.scanType];

                if (m_bitIf != null)
                {
                    for (int i = 0; i < MLS_CG_SIZE; i++)
                    {
                        tmpCoeff[(i * MLS_CG_SIZE) + 0] = (ushort)Math.Abs((int)coeff[blkPosBase + (i * trSize) + 0]);
                        tmpCoeff[(i * MLS_CG_SIZE) + 1] = (ushort)Math.Abs((int)coeff[blkPosBase + (i * trSize) + 1]);
                        tmpCoeff[(i * MLS_CG_SIZE) + 2] = (ushort)Math.Abs((int)coeff[blkPosBase + (i * trSize) + 2]);
                        tmpCoeff[(i * MLS_CG_SIZE) + 3] = (ushort)Math.Abs((int)coeff[blkPosBase + (i * trSize) + 3]);
                    }

                    if (log2TrSize == 2)
                    {
                        do
                        {
                            uint blkPos = scan4[scanPosSigOff];
                            uint sig = scanFlagMask & 1;
                            scanFlagMask >>= 1;
                            uint ctxSig = TableCnt[(4 * 16) + blkPos];
                            EncodeBin(sig, baseCtx + ctxSig);
                            absCoeff[numNonZero] = tmpCoeff[blkPos];
                            numNonZero += sig;
                            scanPosSigOff--;
                        }
                        while (scanPosSigOff >= 0);
                    }
                    else
                    {
                        int tabSigCtx = patternSigCtx * 16;
                        do
                        {
                            uint blkPos = scan4[scanPosSigOff];
                            uint posZeroMask = (subPosBase + scanPosSigOff) != 0 ? ~0u : 0u;
                            uint sig = scanFlagMask & 1;
                            scanFlagMask >>= 1;
                            if (scanPosSigOff != 0 || subSet == 0 || numNonZero != 0)
                            {
                                uint cnt = (uint)(TableCnt[tabSigCtx + blkPos] + offset);
                                uint ctxSig = (cnt + posOffset) & posZeroMask;
                                EncodeBin(sig, baseCtx + ctxSig);
                            }

                            absCoeff[numNonZero] = tmpCoeff[blkPos];
                            numNonZero += sig;
                            scanPosSigOff--;
                        }
                        while (scanPosSigOff >= 0);
                    }
                }
                else
                {
                    // fast RD path
                    byte* tabSigCtx = TableCntPtr + ((log2TrSize == 2 ? 4 : patternSigCtx) * 16);
                    uint sum = X265Primitives.CostCoeffNxN(scan4, coeff + blkPosBase, trSize, absCoeff + numNonZero, tabSigCtx,
                        scanFlagMask, baseCtx, offset + (int)posOffset, scanPosSigOff, subPosBase);
                    m_fracBits += sum;
                }
            }

            uint coeffSigns = coeffSign[subSet];
            numNonZero = coeffNum[subSet];
            if (numNonZero > 0)
            {
                uint idx = 0;
                int lastNZPosInCG = Bsr(subCoeffFlag);
                int firstNZPosInCG = Bsf(subCoeffFlag);
                bool signHidden = lastNZPosInCG - firstNZPosInCG >= SBH_THRESHOLD;
                uint ctxSet = (uint)((((subSet > 0 ? 1 : 0) + (bIsLuma ? 1 : 0)) & 2) + ((c1 & 3) == 0 ? 1 : 0));
                c1 = 1;
                byte* baseCtxMod = m_contextState + (bIsLuma ? 0 : NUM_ONE_FLAG_CTX_LUMA) + OFF_ONE_FLAG_CTX + (4 * ctxSet);
                uint numC1Flag = Math.Min(numNonZero, C1FLAG_NUMBER);

                if (m_bitIf == null)
                {
                    uint sum = X265Primitives.CostC1C2Flag(absCoeff, numC1Flag, baseCtxMod,
                        (bIsLuma ? 0 : NUM_ABS_FLAG_CTX_LUMA - NUM_ONE_FLAG_CTX_LUMA) + (OFF_ABS_FLAG_CTX - OFF_ONE_FLAG_CTX) - (3 * (int)ctxSet));
                    uint firstC2Idx = sum >> 28;
                    c1 = (sum >> 26) & 3;
                    m_fracBits += sum & 0x00FFFFFF;
                    int hiddenShift = (bHideFirstSign & signHidden) ? -1 : 0;
                    m_fracBits += (ulong)((long)(numNonZero + hiddenShift) << 15);
                    if (numNonZero > firstC2Idx)
                    {
                        sum = X265Primitives.CostCoeffRemain(absCoeff, (int)numNonZero, (int)firstC2Idx);
                        m_fracBits += (ulong)sum << 15;
                    }
                }
                else
                {
                    uint firstC2Idx = 8;
                    uint firstC2Flag = 2;
                    uint c1Next = 0xFFFFFFFE;
                    idx = 0;
                    do
                    {
                        uint symbol1 = absCoeff[idx] > 1 ? 1u : 0u;
                        uint symbol2 = absCoeff[idx] > 2 ? 1u : 0u;
                        EncodeBin(symbol1, baseCtxMod + c1);
                        if (symbol1 != 0)
                        {
                            c1Next = 0;
                        }

                        firstC2Flag = (symbol1 + firstC2Flag == 3) ? symbol2 : firstC2Flag;
                        firstC2Idx = (symbol1 + firstC2Idx == 9) ? idx : firstC2Idx;
                        c1 = c1Next & 3;
                        c1Next >>= 2;
                        idx++;
                    }
                    while (idx < numC1Flag);

                    if (c1 == 0)
                    {
                        baseCtxMod = m_contextState + (bIsLuma ? 0 : NUM_ABS_FLAG_CTX_LUMA) + OFF_ABS_FLAG_CTX + ctxSet;
                        EncodeBin(firstC2Flag, baseCtxMod);
                    }

                    int hiddenShift = (bHideFirstSign && signHidden) ? 1 : 0;
                    EncodeBinsEP(coeffSigns >> hiddenShift, (int)numNonZero - hiddenShift);

                    if (c1 == 0 || numNonZero > C1FLAG_NUMBER)
                    {
                        uint goRiceParam = 0;
                        int baseLevel = 3;
                        uint threshold = COEF_REMAIN_BIN_REDUCTION;
                        idx = firstC2Idx;
                        do
                        {
                            if (idx >= C1FLAG_NUMBER)
                            {
                                baseLevel = 1;
                            }

                            if (absCoeff[idx] >= baseLevel)
                            {
                                WriteCoefRemainExGolomb((uint)(absCoeff[idx] - baseLevel), goRiceParam);
                                uint adjust = (absCoeff[idx] > threshold && goRiceParam <= 3) ? 1u : 0u;
                                goRiceParam += adjust;
                                threshold += adjust != 0 ? threshold : 0;
                            }

                            baseLevel = 2;
                            idx++;
                        }
                        while (idx < numNonZero);
                    }
                }
            }

            // next CG
            numNonZero = 0;
            scanPosSigOff = (1 << MLS_CG_SIZE) - 1;
        }
    }

    // ---- SAO ----

    public void CodeSaoOffset(in SaoCtuParam ctuParam, int plane)
    {
        int typeIdx = ctuParam.typeIdx;
        if (plane != 2)
        {
            EncodeBin(typeIdx >= 0 ? 1u : 0u, m_contextState + OFF_SAO_TYPE_IDX_CTX);
            if (typeIdx >= 0)
            {
                EncodeBinEP(typeIdx < X265Sao.SAO_BO ? 1u : 0u);
            }
        }

        if (typeIdx >= 0)
        {
            const uint OFFSET_THRESH = 1u << (X265_DEPTH - 5);   // 1 << min(X265_DEPTH - 5, 5)
            if (typeIdx == X265Sao.SAO_BO)
            {
                for (int i = 0; i < SAO_NUM_OFFSET; i++)
                {
                    CodeSaoMaxUvlc((uint)Math.Abs(ctuParam.GetOffset(i)), OFFSET_THRESH - 1);
                }

                for (int i = 0; i < SAO_NUM_OFFSET; i++)
                {
                    if (ctuParam.GetOffset(i) != 0)
                    {
                        EncodeBinEP(ctuParam.GetOffset(i) < 0 ? 1u : 0u);
                    }
                }

                EncodeBinsEP(ctuParam.bandPos, 5);
            }
            else
            {
                CodeSaoMaxUvlc((uint)ctuParam.offset0, OFFSET_THRESH - 1);
                CodeSaoMaxUvlc((uint)ctuParam.offset1, OFFSET_THRESH - 1);
                CodeSaoMaxUvlc((uint)-ctuParam.offset2, OFFSET_THRESH - 1);
                CodeSaoMaxUvlc((uint)-ctuParam.offset3, OFFSET_THRESH - 1);
                if (plane != 2)
                {
                    EncodeBinsEP((uint)typeIdx, 2);
                }
            }
        }
    }

    public void CodeSaoOffsetEO(int* offset, int typeIdx, int plane)
    {
        if (plane != 2)
        {
            EncodeBin(1, m_contextState + OFF_SAO_TYPE_IDX_CTX);
            EncodeBinEP(1);
        }

        const uint OFFSET_THRESH = 1u << (X265_DEPTH - 5);   // 1 << min(X265_DEPTH - 5, 5)
        CodeSaoMaxUvlc((uint)offset[0], OFFSET_THRESH - 1);
        CodeSaoMaxUvlc((uint)offset[1], OFFSET_THRESH - 1);
        CodeSaoMaxUvlc((uint)-offset[2], OFFSET_THRESH - 1);
        CodeSaoMaxUvlc((uint)-offset[3], OFFSET_THRESH - 1);
        if (plane != 2)
        {
            EncodeBinsEP((uint)typeIdx, 2);
        }
    }

    public void CodeSaoOffsetBO(int* offset, int bandPos, int plane)
    {
        if (plane != 2)
        {
            EncodeBin(1, m_contextState + OFF_SAO_TYPE_IDX_CTX);
            EncodeBinEP(0);
        }

        const uint OFFSET_THRESH = 1u << (X265_DEPTH - 5);   // 1 << min(X265_DEPTH - 5, 5)
        for (int i = 0; i < SAO_NUM_OFFSET; i++)
        {
            CodeSaoMaxUvlc((uint)Math.Abs(offset[i]), OFFSET_THRESH - 1);
        }

        for (int i = 0; i < SAO_NUM_OFFSET; i++)
        {
            if (offset[i] != 0)
            {
                EncodeBinEP(offset[i] < 0 ? 1u : 0u);
            }
        }

        EncodeBinsEP((uint)bandPos, 5);
    }

    private void CodeSaoMaxUvlc(uint code, uint maxSymbol)
    {
        uint isCodeNonZero = code != 0 ? 1u : 0u;
        EncodeBinEP(isCodeNonZero);
        if (isCodeNonZero != 0)
        {
            uint isCodeLast = maxSymbol > code ? 1u : 0u;
            uint mask = (1u << (int)(code - 1)) - 1;
            uint len = code - 1 + isCodeLast;
            mask <<= (int)isCodeLast;
            EncodeBinsEP(mask, (int)len);
        }
    }

    // ---- RDO bit estimates ----

    public void EstBit(ref EstBitsSbac est, uint log2TrSize, bool bIsLuma)
    {
        EstCBFBit(ref est);
        EstSignificantCoeffGroupMapBit(ref est, bIsLuma);
        EstSignificantMapBit(ref est, log2TrSize, bIsLuma);
        EstSignificantCoefficientsBit(ref est, bIsLuma);
    }

    private void EstCBFBit(ref EstBitsSbac est)
    {
        byte* ctx = m_contextState + OFF_QT_CBF_CTX;
        for (uint ctxInc = 0; ctxInc < NUM_QT_CBF_CTX; ctxInc++)
        {
            est.blockCbpBits[(ctxInc * 2) + 0] = (int)SbacGetEntropyBits(ctx[ctxInc], 0);
            est.blockCbpBits[(ctxInc * 2) + 1] = (int)SbacGetEntropyBits(ctx[ctxInc], 1);
        }

        ctx = m_contextState + OFF_QT_ROOT_CBF_CTX;
        est.blockRootCbpBits[0] = (int)SbacGetEntropyBits(ctx[0], 0);
        est.blockRootCbpBits[1] = (int)SbacGetEntropyBits(ctx[0], 1);
    }

    private void EstSignificantCoeffGroupMapBit(ref EstBitsSbac est, bool bIsLuma)
    {
        for (int ctxIdx = 0; ctxIdx < NUM_SIG_CG_FLAG_CTX; ctxIdx++)
        {
            for (uint bin = 0; bin < 2; bin++)
            {
                est.significantCoeffGroupBits[(ctxIdx * 2) + bin] =
                    (int)SbacGetEntropyBits(m_contextState[OFF_SIG_CG_FLAG_CTX + (bIsLuma ? 0 : NUM_SIG_CG_FLAG_CTX) + ctxIdx], bin);
            }
        }
    }

    private void EstSignificantMapBit(ref EstBitsSbac est, uint log2TrSize, bool bIsLuma)
    {
        int firstCtx = 1, numCtx = 8;
        if (log2TrSize >= 4)
        {
            firstCtx = bIsLuma ? 21 : 12;
            numCtx = bIsLuma ? 6 : 3;
        }
        else if (log2TrSize == 3)
        {
            firstCtx = 9;
            numCtx = bIsLuma ? 12 : 3;
        }

        int ctxSigOffset = OFF_SIG_FLAG_CTX + (bIsLuma ? 0 : NUM_SIG_FLAG_CTX_LUMA);
        est.significantBits[0] = (int)SbacGetEntropyBits(m_contextState[ctxSigOffset], 0);
        est.significantBits[NUM_SIG_FLAG_CTX] = (int)SbacGetEntropyBits(m_contextState[ctxSigOffset], 1);
        for (int ctxIdx = firstCtx; ctxIdx < firstCtx + numCtx; ctxIdx++)
        {
            est.significantBits[ctxIdx] = (int)SbacGetEntropyBits(m_contextState[ctxSigOffset + ctxIdx], 0);
            est.significantBits[NUM_SIG_FLAG_CTX + ctxIdx] = (int)SbacGetEntropyBits(m_contextState[ctxSigOffset + ctxIdx], 1);
        }

        uint maxGroupIdx = (log2TrSize * 2) - 1;
        if (bIsLuma)
        {
            if (log2TrSize == 2)
            {
                for (int i = 0, ctxIdx = 0; i < 2; i++, ctxIdx += NUM_CTX_LAST_FLAG_XY)
                {
                    int bits = 0;
                    byte* ctxState = m_contextState + OFF_CTX_LAST_FLAG_X + ctxIdx;
                    for (uint ctx = 0; ctx < 3; ctx++)
                    {
                        est.lastBits[(i * 10) + (int)ctx] = bits + (int)SbacGetEntropyBits(ctxState[ctx], 0);
                        bits += (int)SbacGetEntropyBits(ctxState[ctx], 1);
                    }

                    est.lastBits[(i * 10) + (int)maxGroupIdx] = bits;
                }
            }
            else
            {
                int blkSizeOffset = ((int)(log2TrSize - 2) * 3) + (log2TrSize == 5 ? 1 : 0);
                for (int i = 0, ctxIdx = 0; i < 2; i++, ctxIdx += NUM_CTX_LAST_FLAG_XY)
                {
                    int bits = 0;
                    byte* ctxState = m_contextState + OFF_CTX_LAST_FLAG_X + ctxIdx;
                    for (uint ctx = 0; ctx < (maxGroupIdx >> 1) + 1; ctx++)
                    {
                        int cost0 = (int)SbacGetEntropyBits(ctxState[blkSizeOffset + ctx], 0);
                        int cost1 = (int)SbacGetEntropyBits(ctxState[blkSizeOffset + ctx], 1);
                        est.lastBits[(i * 10) + (int)(ctx * 2) + 0] = bits + cost0;
                        est.lastBits[(i * 10) + (int)(ctx * 2) + 1] = bits + cost1 + cost0;
                        bits += 2 * cost1;
                    }

                    // the last bit cost has no cost0
                    est.lastBits[(i * 10) + (int)maxGroupIdx] -= (int)SbacGetEntropyBits(ctxState[blkSizeOffset + (maxGroupIdx >> 1)], 0);
                }
            }
        }
        else
        {
            const int blkSizeOffset = NUM_CTX_LAST_FLAG_XY_LUMA;
            int ctxShift = (int)log2TrSize - 2;
            for (int i = 0, ctxIdx = 0; i < 2; i++, ctxIdx += NUM_CTX_LAST_FLAG_XY)
            {
                int bits = 0;
                byte* ctxState = m_contextState + OFF_CTX_LAST_FLAG_X + ctxIdx;
                for (uint ctx = 0; ctx < maxGroupIdx; ctx++)
                {
                    int ctxOffset = blkSizeOffset + (int)(ctx >> ctxShift);
                    est.lastBits[(i * 10) + (int)ctx] = bits + (int)SbacGetEntropyBits(ctxState[ctxOffset], 0);
                    bits += (int)SbacGetEntropyBits(ctxState[ctxOffset], 1);
                }

                est.lastBits[(i * 10) + (int)maxGroupIdx] = bits;
            }
        }
    }

    private void EstSignificantCoefficientsBit(ref EstBitsSbac est, bool bIsLuma)
    {
        byte* ctxOne = m_contextState + OFF_ONE_FLAG_CTX + (bIsLuma ? 0 : NUM_ONE_FLAG_CTX_LUMA);
        byte* ctxAbs = m_contextState + OFF_ABS_FLAG_CTX + (bIsLuma ? 0 : NUM_ABS_FLAG_CTX_LUMA);
        int nOne = bIsLuma ? NUM_ONE_FLAG_CTX_LUMA : NUM_ONE_FLAG_CTX_CHROMA;
        int nAbs = bIsLuma ? NUM_ABS_FLAG_CTX_LUMA : NUM_ABS_FLAG_CTX_CHROMA;
        for (int ctxIdx = 0; ctxIdx < nOne; ctxIdx++)
        {
            est.greaterOneBits[(ctxIdx * 2) + 0] = (int)SbacGetEntropyBits(ctxOne[ctxIdx], 0);
            est.greaterOneBits[(ctxIdx * 2) + 1] = (int)SbacGetEntropyBits(ctxOne[ctxIdx], 1);
        }

        for (int ctxIdx = 0; ctxIdx < nAbs; ctxIdx++)
        {
            est.levelAbsBits[(ctxIdx * 2) + 0] = (int)SbacGetEntropyBits(ctxAbs[ctxIdx], 0);
            est.levelAbsBits[(ctxIdx * 2) + 1] = (int)SbacGetEntropyBits(ctxAbs[ctxIdx], 1);
        }
    }
}
