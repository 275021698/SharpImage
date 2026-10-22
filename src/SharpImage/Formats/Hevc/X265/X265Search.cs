// Port of the intra half of x265 encoder/search.{h,cpp} (class Search): the luma mode pre-selection by SA8D cost,
// RDO of the best candidates, the luma and chroma transform quadtree RDO with RDOQ, psy-rd costs, and the
// delta-QP / split-flag cost helpers the CU analysis uses.
using System;

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

/// <summary>struct Mode.</summary>
internal sealed class X265Mode
{
    public readonly X265CuData cu = new();
    public X265Yuv fencYuv = null!;
    public X265Yuv predYuv = null!;
    public X265Yuv reconYuv = null!;
    public X265Entropy contexts = null!;

    public ulong rdCost;
    public ulong sa8dCost;
    public uint sa8dBits;
    public uint psyEnergy;
    public uint ssimEnergy;
    public sse_t resEnergy;
    public sse_t lumaDistortion;
    public sse_t chromaDistortion;
    public sse_t distortion;
    public uint totalBits;
    public uint mvBits;
    public uint coeffBits;

    public void InitCosts()
    {
        rdCost = 0;
        sa8dCost = 0;
        sa8dBits = 0;
        psyEnergy = 0;
        ssimEnergy = 0;
        resEnergy = 0;
        lumaDistortion = 0;
        chromaDistortion = 0;
        distortion = 0;
        totalBits = 0;
        mvBits = 0;
        coeffBits = 0;
    }

    public void AddSubCosts(X265Mode subMode)
    {
        rdCost += subMode.rdCost;
        sa8dCost += subMode.sa8dCost;
        sa8dBits += subMode.sa8dBits;
        psyEnergy += subMode.psyEnergy;
        ssimEnergy += subMode.ssimEnergy;
        resEnergy += subMode.resEnergy;
        lumaDistortion += subMode.lumaDistortion;
        chromaDistortion += subMode.chromaDistortion;
        distortion += subMode.distortion;
        totalBits += subMode.totalBits;
        mvBits += subMode.mvBits;
        coeffBits += subMode.coeffBits;
    }
}

/// <summary>struct Cost.</summary>
internal struct X265Cost
{
    public ulong rdcost;
    public uint bits;
    public sse_t distortion;
    public uint energy;
}

/// <summary>
/// The full-size luma TU of one intra candidate as EstIntraPredQT's RDO pass codes it (no TU splits): the coder state
/// after it, its prediction, reconstruction, coefficients, cost and cbf.
/// </summary>
internal sealed unsafe class X265LumaTu
{
    public X265Entropy entropy = null!;
    public pixel* pred;
    public pixel* recon;
    public short* coeff;
    public X265Cost cost;
    public uint cbf;
}

/// <summary>struct RQTData.</summary>
internal sealed unsafe class X265RqtData
{
    public X265Entropy cur = null!;
    public X265Entropy rqtRoot = null!;
    public X265Entropy rqtTemp = null!;
    public X265Entropy rqtTest = null!;
    public readonly short*[] coeffRQT = new short*[3];
    public X265Yuv reconQtYuv = null!;
    public X265ShortYuv tmpResiYuv = null!;
}

[System.Runtime.CompilerServices.SkipLocalsInit]
internal unsafe class X265Search : X265Predict
{
    protected readonly X265Param m_param;
    protected X265Slice m_slice = null!;
    protected X265FrameData m_frame = null!;
    public readonly X265RdCost m_rdCost = new();
    public readonly X265Quant m_quant;
    public readonly X265Entropy m_entropyCoder;
    protected readonly X265RqtData[] m_rqt = new X265RqtData[NUM_FULL_DEPTH];
    protected readonly byte*[] m_qtTempCbf = new byte*[3];
    protected readonly byte*[] m_qtTempTransformSkipFlag = new byte*[3];
    protected readonly pixel* m_intraPred;
    protected readonly pixel* m_fencScaled;
    protected readonly pixel* m_fencTransposed;
    protected readonly pixel* m_intraPredAngs;
    protected readonly int m_numLayers;

    // EstIntraPredQT remeasures its best candidate allowing TU splits, which first codes the same full-size TU from the
    // same state as the RDO pass did: that TU is kept from the RDO pass (slot m_lumaTuCur while coding a candidate, the
    // other slot for the best so far) and replayed instead of coded again
    private readonly X265LumaTu[] m_lumaTu = new X265LumaTu[2];
    private int m_lumaTuCur;
    private bool m_lumaTuCapture, m_lumaTuCaptured, m_lumaTuReplay;

    protected X265Search(X265Arena arena, X265Param param) : base(arena, param.internalCsp)
    {
        m_param = param;
        int maxLog2CUSize = X265Tables.Log2Size[param.maxCUSize];
        m_numLayers = maxLog2CUSize - 2;
        m_rdCost.SetPsyRdScale(param.psyRd);
        m_entropyCoder = new X265Entropy(arena);
        m_quant = new X265Quant(arena, param.psyRdoq, m_entropyCoder);

        uint sizeL = 1u << (maxLog2CUSize * 2);
        uint sizeC = sizeL >> (m_hChromaShift + m_vChromaShift);
        uint numPartitions = 1u << ((maxLog2CUSize - LOG2_UNIT_SIZE) * 2);
        for (int i = 0; i < NUM_FULL_DEPTH; i++)
        {
            var r = new X265RqtData
            {
                cur = new X265Entropy(arena),
                rqtRoot = new X265Entropy(arena),
                rqtTemp = new X265Entropy(arena),
                rqtTest = new X265Entropy(arena),
            };
            m_rqt[i] = r;
        }

        for (int i = 0; i <= m_numLayers; i++)
        {
            if (param.internalCsp != X265_CSP_I400)
            {
                m_rqt[i].coeffRQT[0] = arena.Alloc<short>(sizeL + (sizeC * 2));
                m_rqt[i].coeffRQT[1] = m_rqt[i].coeffRQT[0] + sizeL;
                m_rqt[i].coeffRQT[2] = m_rqt[i].coeffRQT[0] + sizeL + sizeC;
            }
            else
            {
                m_rqt[i].coeffRQT[0] = arena.Alloc<short>(sizeL);
            }

            m_rqt[i].reconQtYuv = new X265Yuv(arena, (uint)param.maxCUSize, param.internalCsp);
        }

        for (int i = 0; i <= param.maxCUDepth; i++)
        {
            m_rqt[i].tmpResiYuv = new X265ShortYuv(arena, (uint)(param.maxCUSize >> i), param.internalCsp);
        }

        int planes = param.internalCsp != X265_CSP_I400 ? 3 : 1;
        for (int i = 0; i < planes; i++)
        {
            m_qtTempCbf[i] = arena.Alloc<byte>(numPartitions);
            m_qtTempTransformSkipFlag[i] = arena.Alloc<byte>(numPartitions);
        }

        for (int i = 0; i < 2; i++)
        {
            m_lumaTu[i] = new X265LumaTu
            {
                entropy = new X265Entropy(arena),
                pred = arena.Alloc<pixel>(32 * 32),
                recon = arena.Alloc<pixel>(32 * 32),
                coeff = arena.Alloc<short>(32 * 32),
            };
        }

        m_intraPred = arena.Alloc<pixel>((32 * 32) * (33 + 3));
        m_fencScaled = m_intraPred + (32 * 32);
        m_fencTransposed = m_fencScaled + (32 * 32);
        m_intraPredAngs = m_fencTransposed + (32 * 32);
    }

    /// <summary>Returns the real quant QP in the valid spec range.</summary>
    public int SetLambdaFromQP(X265CuData ctu, int qp, int lambdaQp = -1)
    {
        m_rdCost.SetQP(m_slice, lambdaQp < 0 ? qp : lambdaQp);
        int quantQP = Clip3(QP_MIN, QP_MAX_SPEC, qp);
        m_quant.SetQPforQuant(ctu, quantQP);
        return quantQP;
    }

    protected void UpdateModeCost(X265Mode m) =>
        m.rdCost = m_rdCost.m_psyRd != 0 ? m_rdCost.CalcPsyRdCost(m.distortion, m.totalBits, m.psyEnergy) : m_rdCost.CalcRdCost(m.distortion, m.totalBits);

    private void CodeSubdivCbfQTChroma(X265CuData cu, uint tuDepth, uint absPartIdx)
    {
        bool subdiv = tuDepth < cu.m_tuDepth[absPartIdx];
        uint log2TrSize = cu.m_log2CUSize[0] - tuDepth;
        if (!((int)log2TrSize - m_hChromaShift < 2))
        {
            uint parentIdx = absPartIdx & (0xFFu << (int)((log2TrSize + 1 - LOG2_UNIT_SIZE) * 2));
            if (tuDepth == 0 || cu.GetCbf(parentIdx, TEXT_CHROMA_U, tuDepth - 1) != 0)
            {
                m_entropyCoder.CodeQtCbfChroma(cu, absPartIdx, TEXT_CHROMA_U, tuDepth, !subdiv);
            }

            if (tuDepth == 0 || cu.GetCbf(parentIdx, TEXT_CHROMA_V, tuDepth - 1) != 0)
            {
                m_entropyCoder.CodeQtCbfChroma(cu, absPartIdx, TEXT_CHROMA_V, tuDepth, !subdiv);
            }
        }

        if (subdiv)
        {
            uint qNumParts = 1u << (int)((log2TrSize - 1 - LOG2_UNIT_SIZE) * 2);
            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                CodeSubdivCbfQTChroma(cu, tuDepth + 1, absPartIdx);
            }
        }
    }

    private void CodeCoeffQTChroma(X265CuData cu, uint tuDepth, uint absPartIdx, int ttype)
    {
        if (cu.GetCbf(absPartIdx, ttype, tuDepth) == 0)
        {
            return;
        }

        uint log2TrSize = cu.m_log2CUSize[0] - tuDepth;
        if (tuDepth < cu.m_tuDepth[absPartIdx])
        {
            uint qNumParts = 1u << (int)((log2TrSize - 1 - LOG2_UNIT_SIZE) * 2);
            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                CodeCoeffQTChroma(cu, tuDepth + 1, absPartIdx, ttype);
            }

            return;
        }

        uint log2TrSizeC = log2TrSize - (uint)m_hChromaShift;
        if (log2TrSizeC < 2)
        {
            if ((absPartIdx & 3) != 0)
            {
                return;
            }

            log2TrSizeC = 2;
        }

        uint qtLayer = log2TrSize - 2;
        int shift = m_csp == X265_CSP_I420 ? 2 : 0;
        uint coeffOffset = absPartIdx << (LOG2_UNIT_SIZE * 2 - shift);
        short* coeff = m_rqt[qtLayer].coeffRQT[ttype] + coeffOffset;
        m_entropyCoder.CodeCoeffNxN(cu, coeff, absPartIdx, log2TrSizeC, ttype);
    }

    private void CodeIntraLumaQT(X265Mode mode, CUGeom* cuGeom, uint tuDepth, uint absPartIdx, bool bAllowSplit, ref X265Cost outCost, uint* depthRange)
    {
        X265CuData cu = mode.cu;
        uint fullDepth = cuGeom->depth + tuDepth;
        uint log2TrSize = cuGeom->log2CUSize - tuDepth;
        uint qtLayer = log2TrSize - 2;
        int sizeIdx = (int)log2TrSize - 2;
        // (only the call EstIntraPredQT makes keeps or replays its full-size TU, not the split TUs below it)
        bool capture = m_lumaTuCapture, replay = m_lumaTuReplay;
        m_lumaTuCapture = m_lumaTuReplay = false;
        bool mightNotSplit = log2TrSize <= depthRange[1];
        bool mightSplit = (log2TrSize > depthRange[0]) && (bAllowSplit || !mightNotSplit);
        bool bEnableRDOQ = m_param.rdoqLevel != 0;

        X265Cost fullCost = default;
        uint bCBF = 0;
        pixel* reconQt = m_rqt[qtLayer].reconQtYuv.GetLumaAddr(absPartIdx);
        uint reconQtStride = m_rqt[qtLayer].reconQtYuv.m_size;

        if (mightNotSplit)
        {
            if (mightSplit)
            {
                m_entropyCoder.Store(m_rqt[fullDepth].rqtRoot);
            }

            int tuSize = 1 << (int)log2TrSize;
            if (replay)
            {
                X265LumaTu kept = m_lumaTu[m_lumaTuCur ^ 1];
                cu.SetTransformSkipSubParts(0, TEXT_LUMA, absPartIdx, fullDepth);
                cu.SetTUDepthSubParts(tuDepth, absPartIdx, fullDepth);
                bCBF = kept.cbf;
                cu.SetCbfSubParts(bCBF, TEXT_LUMA, absPartIdx, fullDepth);
                X265Primitives.CopyPP(mode.predYuv.GetLumaAddr(absPartIdx), mode.fencYuv.m_size, kept.pred, (uint)tuSize, tuSize, tuSize);
                X265Primitives.CopyPP(reconQt, reconQtStride, kept.recon, (uint)tuSize, tuSize, tuSize);
                uint coeffBytes = 2u << (int)(log2TrSize * 2);
                Buffer.MemoryCopy(kept.coeff, m_rqt[qtLayer].coeffRQT[0] + (absPartIdx << (LOG2_UNIT_SIZE * 2)), coeffBytes, coeffBytes);
                m_entropyCoder.Load(kept.entropy);
                fullCost = kept.cost;
            }
            else
            {
                pixel* fenc = mode.fencYuv.GetLumaAddr(absPartIdx);
                pixel* pred = mode.predYuv.GetLumaAddr(absPartIdx);
                short* residual = m_rqt[cuGeom->depth].tmpResiYuv.GetLumaAddr(absPartIdx);
                uint stride = mode.fencYuv.m_size;

                uint lumaPredMode = cu.m_lumaIntraDir[absPartIdx];
                IntraNeighbors intraNeighbors;
                InitIntraNeighbors(cu, absPartIdx, tuDepth, true, &intraNeighbors);
                InitAdiPattern(cu, *cuGeom, absPartIdx, intraNeighbors, (int)lumaPredMode);
                PredIntraLumaAng(lumaPredMode, pred, stride, log2TrSize);

                cu.SetTransformSkipSubParts(0, TEXT_LUMA, absPartIdx, fullDepth);
                cu.SetTUDepthSubParts(tuDepth, absPartIdx, fullDepth);

                uint coeffOffsetY = absPartIdx << (LOG2_UNIT_SIZE * 2);
                short* coeffY = m_rqt[qtLayer].coeffRQT[0] + coeffOffsetY;

                if (bEnableRDOQ)
                {
                    m_entropyCoder.EstBit(ref m_entropyCoder.m_estBitsSbac, log2TrSize, true);
                }

                X265Primitives.GetResidual(fenc, pred, residual, stride, 1 << (int)log2TrSize);
                uint numSig = m_quant.TransformNxN(cu, fenc, stride, residual, stride, coeffY, log2TrSize, TEXT_LUMA, absPartIdx, false);
                if (numSig != 0)
                {
                    m_quant.InvtransformNxN(cu, residual, stride, coeffY, log2TrSize, TEXT_LUMA, true, false, numSig);
                    X265Primitives.AddPS(reconQt, reconQtStride, pred, residual, stride, stride, 1 << (int)log2TrSize);
                }
                else
                {
                    X265Primitives.CopyPP(reconQt, reconQtStride, pred, stride, 1 << (int)log2TrSize, 1 << (int)log2TrSize);
                }

                bCBF = (numSig != 0 ? 1u : 0u) << (int)tuDepth;
                cu.SetCbfSubParts(bCBF, TEXT_LUMA, absPartIdx, fullDepth);
                fullCost.distortion = X265Primitives.SsePP(reconQt, reconQtStride, fenc, stride, 1 << (int)log2TrSize, 1 << (int)log2TrSize);

                m_entropyCoder.ResetBits();
                if (absPartIdx == 0)
                {
                    m_entropyCoder.CodePartSize(cu, 0, cuGeom->depth);
                }

                if (cu.m_partSize[0] == SIZE_2Nx2N)
                {
                    if (absPartIdx == 0)
                    {
                        m_entropyCoder.CodeIntraDirLumaAng(cu, 0, false);
                    }
                }
                else
                {
                    uint qNumParts = cuGeom->numPartitions >> 2;
                    if (tuDepth == 0)
                    {
                        for (uint qIdx = 0; qIdx < 4; ++qIdx)
                        {
                            m_entropyCoder.CodeIntraDirLumaAng(cu, qIdx * qNumParts, false);
                        }
                    }
                    else if ((absPartIdx & (qNumParts - 1)) == 0)
                    {
                        m_entropyCoder.CodeIntraDirLumaAng(cu, absPartIdx, false);
                    }
                }

                if (log2TrSize != depthRange[0])
                {
                    m_entropyCoder.CodeTransformSubdivFlag(0, 5 - log2TrSize);
                }

                m_entropyCoder.CodeQtCbfLuma(numSig != 0 ? 1u : 0u, tuDepth);
                if (cu.GetCbf(absPartIdx, TEXT_LUMA, tuDepth) != 0)
                {
                    m_entropyCoder.CodeCoeffNxN(cu, coeffY, absPartIdx, log2TrSize, TEXT_LUMA);
                }

                fullCost.bits = m_entropyCoder.GetNumberOfWrittenBits();
                if (m_rdCost.m_psyRd != 0)
                {
                    fullCost.energy = (uint)X265RdCost.PsyCost(sizeIdx, fenc, mode.fencYuv.m_size, reconQt, reconQtStride);
                    fullCost.rdcost = m_rdCost.CalcPsyRdCost(fullCost.distortion, fullCost.bits, fullCost.energy);
                }
                else
                {
                    fullCost.rdcost = m_rdCost.CalcRdCost(fullCost.distortion, fullCost.bits);
                }

                if (capture && !mightSplit)
                {
                    X265LumaTu keep = m_lumaTu[m_lumaTuCur];
                    m_entropyCoder.Store(keep.entropy);
                    X265Primitives.CopyPP(keep.pred, (uint)tuSize, pred, stride, tuSize, tuSize);
                    X265Primitives.CopyPP(keep.recon, (uint)tuSize, reconQt, reconQtStride, tuSize, tuSize);
                    uint coeffBytes = 2u << (int)(log2TrSize * 2);
                    Buffer.MemoryCopy(coeffY, keep.coeff, coeffBytes, coeffBytes);
                    keep.cost = fullCost;
                    keep.cbf = bCBF;
                    m_lumaTuCaptured = true;
                }
            }
        }
        else
        {
            fullCost.rdcost = MAX_INT64;
        }

        if (mightSplit)
        {
            if (mightNotSplit)
            {
                m_entropyCoder.Store(m_rqt[fullDepth].rqtTest);   // state after the full TU encode
                m_entropyCoder.Load(m_rqt[fullDepth].rqtRoot);    // state for the split encode
            }

            uint qNumParts = 1u << (int)((log2TrSize - 1 - LOG2_UNIT_SIZE) * 2);
            X265Cost splitCost = default;
            uint cbf = 0;
            for (uint qIdx = 0, qPartIdx = absPartIdx; qIdx < 4; ++qIdx, qPartIdx += qNumParts)
            {
                CodeIntraLumaQT(mode, cuGeom, tuDepth + 1, qPartIdx, bAllowSplit, ref splitCost, depthRange);
                cbf |= cu.GetCbf(qPartIdx, TEXT_LUMA, tuDepth + 1);
            }

            cu.m_cbf[0][absPartIdx] |= (byte)(cbf << (int)tuDepth);

            if (mightNotSplit && log2TrSize != depthRange[0])
            {
                // include the cost of the subdiv flag that could have been coded at this depth
                m_entropyCoder.ResetBits();
                m_entropyCoder.CodeTransformSubdivFlag(1, 5 - log2TrSize);
                splitCost.bits += m_entropyCoder.GetNumberOfWrittenBits();
                splitCost.rdcost = m_rdCost.m_psyRd != 0
                    ? m_rdCost.CalcPsyRdCost(splitCost.distortion, splitCost.bits, splitCost.energy)
                    : m_rdCost.CalcRdCost(splitCost.distortion, splitCost.bits);
            }

            if (splitCost.rdcost < fullCost.rdcost)
            {
                outCost.rdcost += splitCost.rdcost;
                outCost.distortion += splitCost.distortion;
                outCost.bits += splitCost.bits;
                outCost.energy += splitCost.energy;
                return;
            }

            // recover the entropy state of the full-size TU encode, its tu depth and cbf
            m_entropyCoder.Load(m_rqt[fullDepth].rqtTest);
            cu.SetTUDepthSubParts(tuDepth, absPartIdx, fullDepth);
            cu.SetCbfSubParts(bCBF, TEXT_LUMA, absPartIdx, fullDepth);
            cu.SetTransformSkipSubParts(0, TEXT_LUMA, absPartIdx, fullDepth);
        }

        // reconstruction for the next intra predictions (the full TU won)
        X265PicYuv reconPic = m_frame.m_reconPic;
        pixel* picReconY = reconPic.GetLumaAddr(cu.m_cuAddr, cuGeom->absPartIdx + absPartIdx);
        X265Primitives.CopyPP(picReconY, reconPic.m_stride, reconQt, reconQtStride, 1 << (int)log2TrSize, 1 << (int)log2TrSize);

        outCost.rdcost += fullCost.rdcost;
        outCost.distortion += fullCost.distortion;
        outCost.bits += fullCost.bits;
        outCost.energy += fullCost.energy;
    }

    private void ExtractIntraResultQT(X265CuData cu, X265Yuv reconYuv, uint tuDepth, uint absPartIdx)
    {
        uint log2TrSize = cu.m_log2CUSize[0] - tuDepth;
        if (tuDepth == cu.m_tuDepth[absPartIdx])
        {
            uint qtLayer = log2TrSize - 2;
            uint coeffOffsetY = absPartIdx << (LOG2_UNIT_SIZE * 2);
            short* coeffSrcY = m_rqt[qtLayer].coeffRQT[0] + coeffOffsetY;
            short* coeffDestY = cu.m_trCoeff[0] + coeffOffsetY;
            uint bytes = 2u << (int)(log2TrSize * 2);
            Buffer.MemoryCopy(coeffSrcY, coeffDestY, bytes, bytes);
            m_rqt[qtLayer].reconQtYuv.CopyPartToPartLuma(reconYuv, absPartIdx, log2TrSize);
        }
        else
        {
            uint qNumParts = 1u << (int)((log2TrSize - 1 - LOG2_UNIT_SIZE) * 2);
            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                ExtractIntraResultQT(cu, reconYuv, tuDepth + 1, absPartIdx);
            }
        }
    }

    private void CodeIntraChromaQt(X265Mode mode, CUGeom* cuGeom, uint tuDepth, uint absPartIdx, ref X265Cost outCost)
    {
        X265CuData cu = mode.cu;
        uint log2TrSize = cuGeom->log2CUSize - tuDepth;
        bool bEnableRDOQ = m_param.rdoqLevel != 0;

        if (tuDepth < cu.m_tuDepth[absPartIdx])
        {
            uint qNumParts = 1u << (int)((log2TrSize - 1 - LOG2_UNIT_SIZE) * 2);
            uint splitCbfU = 0, splitCbfV = 0;
            for (uint qIdx = 0, qPartIdx = absPartIdx; qIdx < 4; ++qIdx, qPartIdx += qNumParts)
            {
                CodeIntraChromaQt(mode, cuGeom, tuDepth + 1, qPartIdx, ref outCost);
                splitCbfU |= cu.GetCbf(qPartIdx, TEXT_CHROMA_U, tuDepth + 1);
                splitCbfV |= cu.GetCbf(qPartIdx, TEXT_CHROMA_V, tuDepth + 1);
            }

            cu.m_cbf[1][absPartIdx] |= (byte)(splitCbfU << (int)tuDepth);
            cu.m_cbf[2][absPartIdx] |= (byte)(splitCbfV << (int)tuDepth);
            return;
        }

        uint log2TrSizeC = log2TrSize - (uint)m_hChromaShift;
        uint tuDepthC = tuDepth;
        if (log2TrSizeC < 2)
        {
            if ((absPartIdx & 3) != 0)
            {
                return;
            }

            log2TrSizeC = 2;
            tuDepthC--;
        }

        if (bEnableRDOQ)
        {
            m_entropyCoder.EstBit(ref m_entropyCoder.m_estBitsSbac, log2TrSizeC, false);
        }

        X265ShortYuv resiYuv = m_rqt[cuGeom->depth].tmpResiYuv;
        uint qtLayer = log2TrSize - 2;
        uint stride = mode.fencYuv.m_csize;
        int sizeIdxC = (int)log2TrSizeC - 2;
        int nC = 1 << (int)log2TrSizeC;
        uint curPartNum = cuGeom->numPartitions >> (int)(tuDepthC * 2);
        var tuIterator = new TURecurse(TURecurse.DONT_SPLIT, curPartNum, absPartIdx);
        do
        {
            uint absPartIdxC = tuIterator.absPartIdxTURelCU;
            IntraNeighbors intraNeighbors;
            InitIntraNeighbors(cu, absPartIdxC, tuDepthC, false, &intraNeighbors);

            for (uint chromaId = TEXT_CHROMA_U; chromaId <= TEXT_CHROMA_V; chromaId++)
            {
                int ttype = (int)chromaId;
                pixel* fenc = mode.fencYuv.GetChromaAddr(chromaId, absPartIdxC);
                pixel* pred = mode.predYuv.GetChromaAddr(chromaId, absPartIdxC);
                short* residual = resiYuv.GetChromaAddr(chromaId, absPartIdxC);
                uint coeffOffsetC = absPartIdxC << (LOG2_UNIT_SIZE * 2 - (m_hChromaShift + m_vChromaShift));
                short* coeffC = m_rqt[qtLayer].coeffRQT[chromaId] + coeffOffsetC;
                pixel* reconQt = m_rqt[qtLayer].reconQtYuv.GetChromaAddr(chromaId, absPartIdxC);
                uint reconQtStride = m_rqt[qtLayer].reconQtYuv.m_csize;
                X265PicYuv reconPic = m_frame.m_reconPic;
                pixel* picReconC = reconPic.GetChromaAddr(chromaId, cu.m_cuAddr, cuGeom->absPartIdx + absPartIdxC);
                long picStride = reconPic.m_strideC;

                uint chromaPredMode = cu.m_chromaIntraDir[absPartIdxC];
                if (chromaPredMode == DM_CHROMA_IDX)
                {
                    chromaPredMode = cu.m_lumaIntraDir[m_csp == X265_CSP_I444 ? absPartIdxC : 0];
                }

                InitAdiPatternChroma(cu, *cuGeom, absPartIdxC, intraNeighbors, chromaId);
                PredIntraChromaAng(chromaPredMode, pred, stride, log2TrSizeC);
                cu.SetTransformSkipPartRange(0, ttype, absPartIdxC, tuIterator.absPartIdxStep);

                X265Primitives.GetResidual(fenc, pred, residual, stride, nC);
                uint numSig = m_quant.TransformNxN(cu, fenc, stride, residual, stride, coeffC, log2TrSizeC, ttype, absPartIdxC, false);
                if (numSig != 0)
                {
                    m_quant.InvtransformNxN(cu, residual, stride, coeffC, log2TrSizeC, ttype, true, false, numSig);
                    X265Primitives.AddPS(reconQt, reconQtStride, pred, residual, stride, stride, nC);
                    cu.SetCbfPartRange(1u << (int)tuDepth, ttype, absPartIdxC, tuIterator.absPartIdxStep);
                }
                else
                {
                    X265Primitives.CopyPP(reconQt, reconQtStride, pred, stride, nC, nC);
                    cu.SetCbfPartRange(0, ttype, absPartIdxC, tuIterator.absPartIdxStep);
                }

                outCost.distortion += m_rdCost.ScaleChromaDist(chromaId, X265Primitives.SsePP(reconQt, reconQtStride, fenc, stride, nC, nC));
                if (m_rdCost.m_psyRd != 0)
                {
                    outCost.energy += (uint)X265RdCost.PsyCost(sizeIdxC, fenc, stride, reconQt, reconQtStride);
                }

                X265Primitives.CopyPP(picReconC, picStride, reconQt, reconQtStride, nC, nC);
            }
        }
        while (tuIterator.IsNextSection());
    }

    private void ExtractIntraResultChromaQT(X265CuData cu, X265Yuv reconYuv, uint absPartIdx, uint tuDepth)
    {
        uint tuDepthL = cu.m_tuDepth[absPartIdx];
        uint log2TrSize = cu.m_log2CUSize[0] - tuDepth;
        uint log2TrSizeC = log2TrSize - (uint)m_hChromaShift;
        if (tuDepthL == tuDepth || log2TrSizeC == 2)
        {
            uint numCoeffC = 1u << (int)(log2TrSizeC * 2);
            uint coeffOffsetC = absPartIdx << (LOG2_UNIT_SIZE * 2 - (m_hChromaShift + m_vChromaShift));
            uint qtLayer = log2TrSize - 2 - (tuDepthL - tuDepth);
            short* coeffSrcU = m_rqt[qtLayer].coeffRQT[1] + coeffOffsetC;
            short* coeffSrcV = m_rqt[qtLayer].coeffRQT[2] + coeffOffsetC;
            short* coeffDstU = cu.m_trCoeff[1] + coeffOffsetC;
            short* coeffDstV = cu.m_trCoeff[2] + coeffOffsetC;
            Buffer.MemoryCopy(coeffSrcU, coeffDstU, numCoeffC * 2, numCoeffC * 2);
            Buffer.MemoryCopy(coeffSrcV, coeffDstV, numCoeffC * 2, numCoeffC * 2);
            m_rqt[qtLayer].reconQtYuv.CopyPartToPartChroma(reconYuv, absPartIdx, log2TrSizeC + (uint)m_hChromaShift);
        }
        else
        {
            uint qNumParts = 1u << (int)((log2TrSize - 1 - LOG2_UNIT_SIZE) * 2);
            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                ExtractIntraResultChromaQT(cu, reconYuv, absPartIdx, tuDepth + 1);
            }
        }
    }

    public void CheckIntra(X265Mode intraMode, CUGeom* cuGeom, int partSize)
    {
        X265CuData cu = intraMode.cu;
        cu.SetPartSizeSubParts(partSize);
        cu.SetPredModeSubParts(MODE_INTRA);
        uint* tuDepthRange = stackalloc uint[2];
        cu.GetIntraTUQtDepthRange(tuDepthRange, 0);

        intraMode.InitCosts();
        intraMode.lumaDistortion += EstIntraPredQT(intraMode, cuGeom, tuDepthRange);
        if (m_csp != X265_CSP_I400)
        {
            intraMode.chromaDistortion += EstIntraPredChromaQT(intraMode, cuGeom);
            intraMode.distortion += intraMode.lumaDistortion + intraMode.chromaDistortion;
        }
        else
        {
            intraMode.distortion += intraMode.lumaDistortion;
        }

        cu.m_distortion[0] = intraMode.distortion;
        m_entropyCoder.ResetBits();
        if (m_slice.m_pps.bTransquantBypassEnabled)
        {
            m_entropyCoder.CodeCUTransquantBypassFlag(cu.m_tqBypass[0]);
        }

        m_entropyCoder.CodePartSize(cu, 0, cuGeom->depth);
        m_entropyCoder.CodePredInfo(cu, 0);
        intraMode.mvBits = m_entropyCoder.GetNumberOfWrittenBits();

        bool bCodeDQP = m_slice.m_pps.bUseDQP;
        m_entropyCoder.CodeCoeff(cu, 0, ref bCodeDQP, tuDepthRange);
        m_entropyCoder.Store(intraMode.contexts);
        intraMode.totalBits = m_entropyCoder.GetNumberOfWrittenBits();
        intraMode.coeffBits = intraMode.totalBits - intraMode.mvBits;
        X265Yuv fencYuv = intraMode.fencYuv;
        if (m_rdCost.m_psyRd != 0)
        {
            intraMode.psyEnergy = (uint)X265RdCost.PsyCost((int)cuGeom->log2CUSize - 2, fencYuv.m_buf[0], fencYuv.m_size, intraMode.reconYuv.m_buf[0], intraMode.reconYuv.m_size);
        }

        int n = 1 << (int)cuGeom->log2CUSize;
        intraMode.resEnergy = X265Primitives.SsePP(intraMode.fencYuv.m_buf[0], intraMode.fencYuv.m_size, intraMode.predYuv.m_buf[0], intraMode.predYuv.m_size, n, n);
        UpdateModeCost(intraMode);
        CheckDQP(intraMode, cuGeom);
    }

    private sse_t EstIntraPredQT(X265Mode intraMode, CUGeom* cuGeom, uint* depthRange)
    {
        X265CuData cu = intraMode.cu;
        X265Yuv reconYuv = intraMode.reconYuv;
        X265Yuv predYuv = intraMode.predYuv;
        X265Yuv fencYuv = intraMode.fencYuv;

        uint depth = cuGeom->depth;
        uint initTuDepth = cu.m_partSize[0] != SIZE_2Nx2N ? 1u : 0u;
        uint numPU = 1u << (int)(2 * initTuDepth);
        uint log2TrSize = cuGeom->log2CUSize - initTuDepth;
        uint tuSize = 1u << (int)log2TrSize;
        uint qNumParts = cuGeom->numPartitions >> 2;
        int sizeIdx = (int)log2TrSize - 2;
        uint absPartIdx = 0;
        sse_t totalDistortion = 0;

        ulong* candCostList = stackalloc ulong[MAX_RD_INTRA_MODES];
        uint* rdModeList = stackalloc uint[MAX_RD_INTRA_MODES];
        ulong* modeCosts = stackalloc ulong[35];
        uint* mpmModes = stackalloc uint[3];

        for (uint puIdx = 0; puIdx < numPU; puIdx++, absPartIdx += qNumParts)
        {
            uint bmode = 0;
            bool haveBestTu = false;
            if (intraMode.cu.m_lumaIntraDir[puIdx] != unchecked((byte)ALL_IDX))
            {
                bmode = intraMode.cu.m_lumaIntraDir[puIdx];
            }
            else
            {
                ulong bcost;
                int maxCandCount = 2 + m_param.rdLevel + (int)((depth + initTuDepth) >> 1);
                {
                    // reference sample smoothing
                    IntraNeighbors intraNeighbors;
                    InitIntraNeighbors(cu, absPartIdx, initTuDepth, true, &intraNeighbors);
                    InitAdiPattern(cu, *cuGeom, absPartIdx, intraNeighbors, ALL_IDX);

                    // the set of modes to test, from the prediction signal only
                    pixel* fenc = fencYuv.GetLumaAddr(absPartIdx);
                    uint stride = predYuv.m_size;
                    int scaleTuSize = (int)tuSize;
                    int scaleStride = (int)stride;
                    const int costShift = 0;

                    m_entropyCoder.LoadIntraDirModeLuma(m_rqt[depth].cur);

                    // three cost tiers: mpm[0]; mpm[1], mpm[2]; non-mpm modes (rbits)
                    uint rbits = GetIntraRemModeBits(cu, absPartIdx, mpmModes, out ulong mpms);

                    // DC
                    X265Primitives.IntraPred(sizeIdx, DC_IDX, m_intraPred, scaleStride, intraNeighbourBuf0, scaleTuSize <= 16 ? 1 : 0);
                    uint bits = (mpms & (1UL << DC_IDX)) != 0 ? m_entropyCoder.BitsIntraModeMPM(mpmModes, DC_IDX) : rbits;
                    uint sad = (uint)X265Primitives.Sa8d(sizeIdx, fenc, scaleStride, m_intraPred, scaleStride) << costShift;
                    modeCosts[DC_IDX] = bcost = m_rdCost.CalcRdSADCost(sad, bits);

                    // PLANAR
                    pixel* planar = intraNeighbourBuf0;
                    if (tuSize >= 8 && tuSize <= 32)
                    {
                        planar = intraNeighbourBuf1;
                    }

                    X265Primitives.IntraPred(sizeIdx, PLANAR_IDX, m_intraPred, scaleStride, planar, 0);
                    bits = (mpms & (1UL << PLANAR_IDX)) != 0 ? m_entropyCoder.BitsIntraModeMPM(mpmModes, PLANAR_IDX) : rbits;
                    sad = (uint)X265Primitives.Sa8d(sizeIdx, fenc, scaleStride, m_intraPred, scaleStride) << costShift;
                    modeCosts[PLANAR_IDX] = m_rdCost.CalcRdSADCost(sad, bits);
                    if (modeCosts[PLANAR_IDX] < bcost) bcost = modeCosts[PLANAR_IDX];

                    // angular predictions (all at once; horizontal modes transposed, compared to the transposed source)
                    X265Primitives.Transpose(m_fencTransposed, fenc, scaleStride, scaleTuSize);
                    X265Primitives.AllAngsPred(sizeIdx + 2, m_intraPredAngs, intraNeighbourBuf0, intraNeighbourBuf1, scaleTuSize <= 16 ? 1 : 0);
                    for (int mode = 2; mode < 35; mode++)
                    {
                        bits = (mpms & (1UL << mode)) != 0 ? m_entropyCoder.BitsIntraModeMPM(mpmModes, (uint)mode) : rbits;
                        if (mode < 18)
                        {
                            sad = (uint)X265Primitives.Sa8d(sizeIdx, m_fencTransposed, scaleTuSize, &m_intraPredAngs[(mode - 2) * (scaleTuSize * scaleTuSize)], scaleTuSize) << costShift;
                        }
                        else
                        {
                            sad = (uint)X265Primitives.Sa8d(sizeIdx, fenc, scaleStride, &m_intraPredAngs[(mode - 2) * (scaleTuSize * scaleTuSize)], scaleTuSize) << costShift;
                        }

                        modeCosts[mode] = m_rdCost.CalcRdSADCost(sad, bits);
                        if (modeCosts[mode] < bcost) bcost = modeCosts[mode];
                    }

                    // the top maxCandCount modes within 25% of the best, or mpm[0]
                    for (int i = 0; i < maxCandCount; i++)
                    {
                        candCostList[i] = MAX_INT64;
                    }

                    ulong paddedBcost = bcost + (bcost >> 2);
                    for (int mode = 0; mode < 35; mode++)
                    {
                        if (modeCosts[mode] < paddedBcost || (uint)mode == mpmModes[0])
                        {
                            UpdateCandList((uint)mode, modeCosts[mode], maxCandCount, rdModeList, candCostList);
                        }
                    }
                }

                // measure the best candidates with simple RDO (no TU splits)
                bcost = MAX_INT64;
                for (int i = 0; i < maxCandCount; i++)
                {
                    if (candCostList[i] == MAX_INT64)
                    {
                        break;
                    }

                    m_entropyCoder.Load(m_rqt[depth].cur);
                    cu.SetLumaIntraDirSubParts(rdModeList[i], absPartIdx, depth + initTuDepth);
                    X265Cost icosts = default;
                    m_lumaTuCapture = true;
                    m_lumaTuCaptured = false;
                    CodeIntraLumaQT(intraMode, cuGeom, initTuDepth, absPartIdx, false, ref icosts, depthRange);
                    if (icosts.rdcost < bcost)
                    {
                        bcost = icosts.rdcost;
                        bmode = rdModeList[i];
                        if (m_lumaTuCaptured)
                        {
                            m_lumaTuCur ^= 1;   // the slot just written holds the best
                        }

                        haveBestTu = m_lumaTuCaptured;
                    }
                }
            }

            // remeasure the best mode, allowing TU splits
            cu.SetLumaIntraDirSubParts(bmode, absPartIdx, depth + initTuDepth);
            m_entropyCoder.Load(m_rqt[depth].cur);
            X265Cost icosts2 = default;
            m_lumaTuReplay = haveBestTu;
            CodeIntraLumaQT(intraMode, cuGeom, initTuDepth, absPartIdx, true, ref icosts2, depthRange);
            totalDistortion += icosts2.distortion;
            ExtractIntraResultQT(cu, reconYuv, initTuDepth, absPartIdx);

            // reconstruction for the next intra prediction blocks
            if (puIdx != numPU - 1)
            {
                X265PicYuv reconPic = m_frame.m_reconPic;
                pixel* dst = reconPic.GetLumaAddr(cu.m_cuAddr, cuGeom->absPartIdx + absPartIdx);
                pixel* src = reconYuv.GetLumaAddr(absPartIdx);
                X265Primitives.CopyPP(dst, reconPic.m_stride, src, reconYuv.m_size, (int)tuSize, (int)tuSize);
            }
        }

        if (numPU > 1)
        {
            uint combCbfY = 0;
            for (uint qIdx = 0, qPartIdx = 0; qIdx < 4; ++qIdx, qPartIdx += qNumParts)
            {
                combCbfY |= cu.GetCbf(qPartIdx, TEXT_LUMA, 1);
            }

            cu.m_cbf[0][0] |= (byte)combCbfY;
        }

        m_entropyCoder.Load(m_rqt[depth].cur);
        return totalDistortion;
    }

    private sse_t EstIntraPredChromaQT(X265Mode intraMode, CUGeom* cuGeom)
    {
        X265CuData cu = intraMode.cu;
        X265Yuv reconYuv = intraMode.reconYuv;
        uint depth = cuGeom->depth;
        uint initTuDepth = (cu.m_partSize[0] != SIZE_2Nx2N && m_csp == X265_CSP_I444) ? 1u : 0u;
        uint log2TrSize = cuGeom->log2CUSize - initTuDepth;
        uint absPartStep = cuGeom->numPartitions;
        sse_t totalDistortion = 0;
        int sizeC = (1 << (int)log2TrSize) >> m_hChromaShift;
        uint* modeList = stackalloc uint[NUM_CHROMA_MODE];

        var tuIterator = new TURecurse(initTuDepth == 0 ? TURecurse.DONT_SPLIT : TURecurse.QUAD_SPLIT, absPartStep, 0);
        do
        {
            uint absPartIdxC = tuIterator.absPartIdxTURelCU;
            uint bestMode = 0;
            sse_t bestDist = 0;
            ulong bestCost = MAX_INT64;
            uint minMode = 0;
            uint maxMode = NUM_CHROMA_MODE;

            if (intraMode.cu.m_chromaIntraDir[0] != unchecked((byte)ALL_IDX) && initTuDepth == 0)
            {
                for (int l = 0; l < NUM_CHROMA_MODE; l++)
                {
                    modeList[l] = intraMode.cu.m_chromaIntraDir[0];
                }

                maxMode = 1;
            }
            else
            {
                cu.GetAllowedChromaDir(absPartIdxC, modeList);
            }

            for (uint mode = minMode; mode < maxMode; mode++)
            {
                m_entropyCoder.Load(m_rqt[depth].cur);
                cu.SetChromIntraDirSubParts(modeList[mode], absPartIdxC, depth + initTuDepth);
                X265Cost outCost = default;
                CodeIntraChromaQt(intraMode, cuGeom, initTuDepth, absPartIdxC, ref outCost);
                if (m_slice.m_pps.bTransformSkipEnabled)
                {
                    m_entropyCoder.Load(m_rqt[depth].cur);
                }

                m_entropyCoder.ResetBits();
                if (cu.m_partSize[0] == SIZE_2Nx2N || m_csp != X265_CSP_I444)
                {
                    if (absPartIdxC == 0)
                    {
                        m_entropyCoder.CodeIntraDirChroma(cu, absPartIdxC, modeList);
                    }
                }

                CodeSubdivCbfQTChroma(cu, initTuDepth, absPartIdxC);
                CodeCoeffQTChroma(cu, initTuDepth, absPartIdxC, TEXT_CHROMA_U);
                CodeCoeffQTChroma(cu, initTuDepth, absPartIdxC, TEXT_CHROMA_V);
                uint bits = m_entropyCoder.GetNumberOfWrittenBits();
                ulong cost = m_rdCost.m_psyRd != 0 ? m_rdCost.CalcPsyRdCost(outCost.distortion, bits, outCost.energy) : m_rdCost.CalcRdCost(outCost.distortion, bits);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestDist = outCost.distortion;
                    bestMode = modeList[mode];
                    ExtractIntraResultChromaQT(cu, reconYuv, absPartIdxC, initTuDepth);
                    uint step = tuIterator.absPartIdxStep;
                    Buffer.MemoryCopy(cu.m_cbf[1] + absPartIdxC, m_qtTempCbf[1], step, step);
                    Buffer.MemoryCopy(cu.m_cbf[2] + absPartIdxC, m_qtTempCbf[2], step, step);
                    Buffer.MemoryCopy(cu.m_transformSkip[1] + absPartIdxC, m_qtTempTransformSkipFlag[1], step, step);
                    Buffer.MemoryCopy(cu.m_transformSkip[2] + absPartIdxC, m_qtTempTransformSkipFlag[2], step, step);
                }
            }

            if (!tuIterator.IsLastSection())
            {
                uint zorder = cuGeom->absPartIdx + absPartIdxC;
                X265PicYuv reconPic = m_frame.m_reconPic;
                X265Primitives.CopyPP(reconPic.GetCbAddr(cu.m_cuAddr, zorder), reconPic.m_strideC, reconYuv.GetCbAddr(absPartIdxC), reconYuv.m_csize, sizeC, sizeC);
                X265Primitives.CopyPP(reconPic.GetCrAddr(cu.m_cuAddr, zorder), reconPic.m_strideC, reconYuv.GetCrAddr(absPartIdxC), reconYuv.m_csize, sizeC, sizeC);
            }

            uint st = tuIterator.absPartIdxStep;
            Buffer.MemoryCopy(m_qtTempCbf[1], cu.m_cbf[1] + absPartIdxC, st, st);
            Buffer.MemoryCopy(m_qtTempCbf[2], cu.m_cbf[2] + absPartIdxC, st, st);
            Buffer.MemoryCopy(m_qtTempTransformSkipFlag[1], cu.m_transformSkip[1] + absPartIdxC, st, st);
            Buffer.MemoryCopy(m_qtTempTransformSkipFlag[2], cu.m_transformSkip[2] + absPartIdxC, st, st);
            cu.SetChromIntraDirSubParts(bestMode, absPartIdxC, depth + initTuDepth);
            totalDistortion += bestDist;
        }
        while (tuIterator.IsNextSection());

        if (initTuDepth != 0)
        {
            uint combCbfU = 0, combCbfV = 0;
            uint qNumParts = tuIterator.absPartIdxStep;
            for (uint qIdx = 0, qPartIdx = 0; qIdx < 4; ++qIdx, qPartIdx += qNumParts)
            {
                combCbfU |= cu.GetCbf(qPartIdx, TEXT_CHROMA_U, 1);
                combCbfV |= cu.GetCbf(qPartIdx, TEXT_CHROMA_V, 1);
            }

            cu.m_cbf[1][0] |= (byte)combCbfU;
            cu.m_cbf[2][0] |= (byte)combCbfV;
        }

        m_entropyCoder.Load(m_rqt[depth].cur);
        return totalDistortion;
    }

    private uint GetIntraRemModeBits(X265CuData cu, uint absPartIdx, uint* mpmModes, out ulong mpms)
    {
        cu.GetIntraDirLumaPredictor(absPartIdx, mpmModes);
        mpms = 0;
        for (int i = 0; i < 3; ++i)
        {
            mpms |= 1UL << (int)mpmModes[i];
        }

        return m_entropyCoder.BitsIntraModeNonMPM();
    }

    /// <summary>Keep a top-N list: replace the highest cost entry if this one is better.</summary>
    private static void UpdateCandList(uint mode, ulong cost, int maxCandCount, uint* candModeList, ulong* candCostList)
    {
        uint maxIndex = 0;
        ulong maxValue = 0;
        for (int i = 0; i < maxCandCount; i++)
        {
            if (maxValue < candCostList[i])
            {
                maxValue = candCostList[i];
                maxIndex = (uint)i;
            }
        }

        if (cost < maxValue)
        {
            candCostList[maxIndex] = cost;
            candModeList[maxIndex] = mode;
        }
    }

    protected void CheckDQP(X265Mode mode, CUGeom* cuGeom)
    {
        X265CuData cu = mode.cu;
        if (cu.m_slice.m_pps.bUseDQP && cuGeom->depth <= cu.m_slice.m_pps.maxCuDQPDepth)
        {
            if (cu.GetQtRootCbf(0))
            {
                // rdLevel >= 3
                mode.contexts.ResetBits();
                mode.contexts.CodeDeltaQP(cu, 0);
                uint bits = mode.contexts.GetNumberOfWrittenBits();
                mode.totalBits += bits;
                UpdateModeCost(mode);
            }
            else
            {
                cu.SetQPSubParts(cu.GetRefQP(0), 0, cuGeom->depth);
            }
        }
    }

    protected void CheckDQPForSplitPred(X265Mode mode, CUGeom* cuGeom)
    {
        X265CuData cu = mode.cu;
        if (cuGeom->depth == cu.m_slice.m_pps.maxCuDQPDepth && cu.m_slice.m_pps.bUseDQP)
        {
            bool hasResidual = false;
            for (uint blkIdx = 0; blkIdx < cuGeom->numPartitions; blkIdx++)
            {
                if (cu.GetQtRootCbf(blkIdx))
                {
                    hasResidual = true;
                    break;
                }
            }

            if (hasResidual)
            {
                mode.contexts.ResetBits();
                mode.contexts.CodeDeltaQP(cu, 0);
                uint bits = mode.contexts.GetNumberOfWrittenBits();
                mode.totalBits += bits;
                UpdateModeCost(mode);

                // zero-cbf sub-CUs get the reference QP (no delta QP is signalled) until the first coded one
                cu.SetQPSubCUs(cu.GetRefQP(0), 0, cuGeom->depth);
            }
            else
            {
                cu.SetQPSubParts(cu.GetRefQP(0), 0, cuGeom->depth);
            }
        }
    }
}
