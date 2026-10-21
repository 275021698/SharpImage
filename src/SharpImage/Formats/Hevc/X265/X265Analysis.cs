// Port of the intra-slice half of x265 encoder/analysis.{h,cpp} (class Analysis): the CTU entry (per-CU QP from
// AQ, lambda), the recursive intra CU quadtree decision (2Nx2N, NxN at 8x8, split) and its split-flag and
// delta-QP costs.
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

[System.Runtime.CompilerServices.SkipLocalsInit]
internal sealed unsafe class X265Analysis : X265Search
{
    private const int PRED_INTRA = 0, PRED_INTRA_NxN = 1, PRED_SPLIT = 2, NUM_PREDS = 3;

    private sealed class ModeDepth
    {
        public readonly X265Mode[] pred = new X265Mode[NUM_PREDS];
        public X265Mode? bestMode;
        public X265Yuv fencYuv = null!;
    }

    private readonly ModeDepth[] m_modeDepth = new ModeDepth[NUM_CU_DEPTH];

    /// <summary>AQ offsets per 16x16 (qgSize 32) block of the frame (Lowres::qpAqOffset), or null.</summary>
    public double[]? QpAqOffset;

    public X265Analysis(X265Arena arena, X265Param param) : base(arena, param)
    {
        int csp = param.internalCsp;
        uint cuSize = (uint)param.maxCUSize;
        for (uint depth = 0; depth <= (uint)param.maxCUDepth; depth++, cuSize >>= 1)
        {
            var md = new ModeDepth { fencYuv = new X265Yuv(arena, cuSize, csp) };
            for (int j = 0; j < NUM_PREDS; j++)
            {
                var m = new X265Mode
                {
                    predYuv = new X265Yuv(arena, cuSize, csp),
                    reconYuv = new X265Yuv(arena, cuSize, csp),
                    contexts = new X265Entropy(arena),
                    fencYuv = md.fencYuv,
                };
                m.cu.Initialize(arena, depth, csp, (uint)param.num4x4Partitions, (uint)param.maxCUSize, (uint)param.unitSizeDepth, param.maxLog2CUSize);
                md.pred[j] = m;
            }

            m_modeDepth[depth] = md;
        }
    }

    public void SetFrame(X265FrameData frame)
    {
        m_frame = frame;
        m_slice = frame.m_slice;
    }

    /// <summary>Analysis::compressCTU for an I slice.</summary>
    public X265Mode CompressCTU(X265CuData ctu, CUGeom* cuGeom, X265Entropy initialContext)
    {
        int qp = SetLambdaFromQP(ctu, m_slice.m_pps.bUseDQP ? CalculateQpforCuSize(ctu, cuGeom) : m_slice.m_sliceQp);
        ctu.SetQPSubParts(qp, 0, 0);
        m_rqt[0].cur.Load(initialContext);
        ctu.m_meanQP = initialContext.m_meanQP;
        m_modeDepth[0].fencYuv.CopyFromPicYuv(m_frame.m_fencPic, ctu.m_cuAddr, 0);
        CompressIntraCU(ctu, cuGeom, qp);
        return m_modeDepth[0].bestMode!;
    }

    private void CheckBestMode(X265Mode mode, uint depth)
    {
        ModeDepth md = m_modeDepth[depth];
        if (md.bestMode != null)
        {
            if (mode.rdCost < md.bestMode.rdCost)
            {
                md.bestMode = mode;
            }
        }
        else
        {
            md.bestMode = mode;
        }
    }

    private void AddSplitFlagCost(X265Mode mode, uint depth)
    {
        // rdLevel >= 3: code the split flag and update the bit cost
        mode.contexts.ResetBits();
        mode.contexts.CodeSplitFlag(mode.cu, 0, depth);
        uint bits = mode.contexts.GetNumberOfWrittenBits();
        mode.totalBits += bits;
        UpdateModeCost(mode);
    }

    private ulong CompressIntraCU(X265CuData parentCTU, CUGeom* cuGeom, int qp)
    {
        uint depth = cuGeom->depth;
        ModeDepth md = m_modeDepth[depth];
        md.bestMode = null;

        bool mightSplit = (cuGeom->flags & CUGeom.LEAF) == 0;
        bool mightNotSplit = (cuGeom->flags & CUGeom.SPLIT_MANDATORY) == 0;

        // no analysis reuse: never already decided
        if (cuGeom->log2CUSize != MAX_LOG2_CU_SIZE && mightNotSplit)
        {
            md.pred[PRED_INTRA].cu.InitSubCU(parentCTU, *cuGeom, qp);
            CheckIntra(md.pred[PRED_INTRA], cuGeom, SIZE_2Nx2N);
            CheckBestMode(md.pred[PRED_INTRA], depth);

            if (cuGeom->log2CUSize == 3 && m_slice.m_sps.quadtreeTULog2MinSize < 3)
            {
                md.pred[PRED_INTRA_NxN].cu.InitSubCU(parentCTU, *cuGeom, qp);
                CheckIntra(md.pred[PRED_INTRA_NxN], cuGeom, SIZE_NxN);
                CheckBestMode(md.pred[PRED_INTRA_NxN], depth);
            }

            if (mightSplit)
            {
                AddSplitFlagCost(md.bestMode!, cuGeom->depth);
            }
        }

        if (mightSplit)
        {
            X265Mode splitPred = md.pred[PRED_SPLIT];
            splitPred.InitCosts();
            X265CuData splitCU = splitPred.cu;
            splitCU.InitSubCU(parentCTU, *cuGeom, qp);

            uint nextDepth = depth + 1;
            ModeDepth nd = m_modeDepth[nextDepth];
            X265Entropy nextContext = m_rqt[depth].cur;
            int nextQP = qp;

            for (uint subPartIdx = 0; subPartIdx < 4; subPartIdx++)
            {
                CUGeom* childGeom = cuGeom + cuGeom->childOffset + subPartIdx;
                if ((childGeom->flags & CUGeom.PRESENT) != 0)
                {
                    m_modeDepth[0].fencYuv.CopyPartToYuv(nd.fencYuv, childGeom->absPartIdx);
                    m_rqt[nextDepth].cur.Load(nextContext);

                    if (m_slice.m_pps.bUseDQP && nextDepth <= m_slice.m_pps.maxCuDQPDepth)
                    {
                        nextQP = SetLambdaFromQP(parentCTU, CalculateQpforCuSize(parentCTU, childGeom));
                    }

                    CompressIntraCU(parentCTU, childGeom, nextQP);

                    // save the best CU and its prediction data for this sub CU
                    splitCU.CopyPartFrom(nd.bestMode!.cu, *childGeom, subPartIdx);
                    splitPred.AddSubCosts(nd.bestMode);
                    nd.bestMode.reconYuv.CopyToPartYuv(splitPred.reconYuv, childGeom->numPartitions * subPartIdx);
                    nextContext = nd.bestMode.contexts;
                }
                else
                {
                    // record the depth of this non-present sub-CU
                    splitCU.SetEmptyPart(*childGeom, subPartIdx);
                }
            }

            nextContext.Store(splitPred.contexts);
            if (mightNotSplit)
            {
                AddSplitFlagCost(splitPred, cuGeom->depth);
            }
            else
            {
                UpdateModeCost(splitPred);
            }

            CheckDQPForSplitPred(splitPred, cuGeom);
            CheckBestMode(splitPred, depth);
        }

        // copy the best data to the CTU and the recon picture
        md.bestMode!.cu.CopyToPic(depth);
        if (md.bestMode != md.pred[PRED_SPLIT])
        {
            md.bestMode.reconYuv.CopyToPicYuv(m_frame.m_reconPic, parentCTU.m_cuAddr, cuGeom->absPartIdx);
        }

        return md.bestMode.rdCost;
    }

    /// <summary>Analysis::calculateQpforCuSize (no cutree, no hevc-aq): base QP plus the CU's mean AQ offset.</summary>
    private int CalculateQpforCuSize(X265CuData ctu, CUGeom* cuGeom)
    {
        double qp = m_frame.m_cuStatBaseQp[ctu.m_cuAddr];
        int loopIncr = m_param.qgSize == 8 ? 8 : 16;
        double[]? qpoffs = QpAqOffset;
        if (qpoffs != null)
        {
            uint width = (uint)m_frame.m_fencPic.m_picWidth;
            uint height = (uint)m_frame.m_fencPic.m_picHeight;
            uint blockX = ctu.m_cuPelX + X265Tables.ZscanToPelXPtr[cuGeom->absPartIdx];
            uint blockY = ctu.m_cuPelY + X265Tables.ZscanToPelYPtr[cuGeom->absPartIdx];
            uint maxCols = (width + (uint)(loopIncr - 1)) / (uint)loopIncr;
            uint blockSize = (uint)m_param.maxCUSize >> (int)cuGeom->depth;
            double dQpOffset = 0;
            uint cnt = 0;
            for (uint blockYY = blockY; blockYY < blockY + blockSize && blockYY < height; blockYY += (uint)loopIncr)
            {
                for (uint blockXX = blockX; blockXX < blockX + blockSize && blockXX < width; blockXX += (uint)loopIncr)
                {
                    uint idx = ((blockYY / (uint)loopIncr) * maxCols) + (blockXX / (uint)loopIncr);
                    dQpOffset += qpoffs[idx];
                    cnt++;
                }
            }

            dQpOffset /= cnt;
            qp += dQpOffset;
        }

        return Clip3(m_param.qpMin, m_param.qpMax, (int)(qp + 0.5));
    }
}
