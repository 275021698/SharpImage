// Port of x265 encoder/rdcost.h (RDCost) and the intra half of common/predict.{h,cpp} (Predict): reference sample
// availability and filling, [1 2 1] / strong smoothing, and angular / planar / DC prediction of luma and chroma.
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

/// <summary>class RDCost: all weights and factors as FIX8.</summary>
internal sealed class X265RdCost
{
    public ulong m_lambda2;
    public ulong m_lambda;
    public readonly uint[] m_chromaDistWeight = new uint[2];
    public uint m_psyRdBase;
    public uint m_psyRd;
    public int m_qp;

    private static readonly ushort[] ChromaLambda2OffsetTab = X265Tables.ChromaLambda2OffsetTab;

    public void SetPsyRdScale(double scale) => m_psyRdBase = (uint)Math.Floor(65536.0 * scale * 0.33);

    public void SetQP(X265Slice slice, int qp)
    {
        m_qp = qp;
        SetLambda(X265Tables.Lambda2Tab[qp], X265Tables.LambdaTab[qp]);

        // scale the psy RD factor by a slice type factor (B, P, I)
        uint[] psyScaleFix8 = { 300, 256, 96 };
        m_psyRd = (m_psyRdBase * psyScaleFix8[slice.m_sliceType]) >> 8;

        // at high QP psy-rd can cause artifacts
        if (qp >= 40)
        {
            int scale = qp >= QP_MAX_SPEC ? 0 : (QP_MAX_SPEC - qp) * 23;
            m_psyRd = (uint)((m_psyRd * scale) >> 8);
        }

        int qpCb, qpCr;
        if (slice.m_sps.chromaFormatIdc == X265_CSP_I420)
        {
            qpCb = X265Tables.ChromaScale[Clip3(QP_MIN, QP_MAX_MAX, qp + slice.m_pps.chromaQpOffset[0] + slice.m_chromaQpOffset[0])];
            qpCr = X265Tables.ChromaScale[Clip3(QP_MIN, QP_MAX_MAX, qp + slice.m_pps.chromaQpOffset[1] + slice.m_chromaQpOffset[1])];
        }
        else
        {
            qpCb = Clip3(QP_MIN, QP_MAX_SPEC, qp + slice.m_pps.chromaQpOffset[0] + slice.m_chromaQpOffset[0]);
            qpCr = Clip3(QP_MIN, QP_MAX_SPEC, qp + slice.m_pps.chromaQpOffset[1] + slice.m_chromaQpOffset[1]);
        }

        int chromaOffsetIdx = Math.Min(qp - qpCb + 12, MAX_CHROMA_LAMBDA_OFFSET);
        m_chromaDistWeight[0] = ChromaLambda2OffsetTab[chromaOffsetIdx];
        chromaOffsetIdx = Math.Min(qp - qpCr + 12, MAX_CHROMA_LAMBDA_OFFSET);
        m_chromaDistWeight[1] = ChromaLambda2OffsetTab[chromaOffsetIdx];
    }

    public void SetLambda(double lambda2, double lambda)
    {
        m_lambda2 = (ulong)Math.Floor(256.0 * lambda2);
        m_lambda = (ulong)Math.Floor(256.0 * lambda);
    }

    public ulong CalcRdCost(sse_t distortion, uint bits) => distortion + (((bits * m_lambda2) + 128) >> 8);

    public static unsafe int PsyCost(int size, pixel* source, long sstride, pixel* recon, long rstride) =>
        X265Primitives.PsyCostPP(size, source, sstride, recon, rstride);

    public ulong CalcPsyRdCost(sse_t distortion, uint bits, uint psycost) =>
        distortion + ((m_lambda * m_psyRd * psycost) >> 24) + ((bits * m_lambda2) >> 8);

    public ulong CalcRdSADCost(uint sadCost, uint bits) => sadCost + (((bits * m_lambda) + 128) >> 8);

    public sse_t ScaleChromaDist(uint plane, sse_t dist) => (sse_t)(((dist * (ulong)m_chromaDistWeight[plane - 1]) + 128) >> 8);

    public uint GetCost(uint bits) => (uint)(((bits * m_lambda) + 128) >> 8);
}

/// <summary>struct Predict::IntraNeighbors.</summary>
internal unsafe struct IntraNeighbors
{
    public int numIntraNeighbor;
    public int totalUnits;
    public int aboveUnits;
    public int leftUnits;
    public int unitWidth;
    public int unitHeight;
    public int log2TrSize;
    public fixed bool bNeighborFlags[4 * 16 + 1];
}

/// <summary>class Predict (intra prediction).</summary>
internal unsafe class X265Predict
{
    // unfiltered / filtered neighbours of the current partition
    protected readonly pixel* intraNeighbourBuf0;
    protected readonly pixel* intraNeighbourBuf1;
    protected int m_csp;
    protected int m_hChromaShift, m_vChromaShift;

    protected X265Predict(X265Arena arena, int csp)
    {
        intraNeighbourBuf0 = arena.Alloc<pixel>(258 + 16);
        intraNeighbourBuf1 = arena.Alloc<pixel>(258 + 16);
        m_csp = csp;
        m_hChromaShift = m_vChromaShift = csp == X265_CSP_I420 ? 1 : 0;
    }

    protected pixel* NeighbourBuf(int filter) => filter != 0 ? intraNeighbourBuf1 : intraNeighbourBuf0;

    public void PredIntraLumaAng(uint dirMode, pixel* dst, long stride, uint log2TrSize)
    {
        int tuSize = 1 << (int)log2TrSize;
        int sizeIdx = (int)log2TrSize - 2;
        int filter = (X265Tables.IntraFilterFlags[dirMode] & tuSize) != 0 ? 1 : 0;
        int bFilter = log2TrSize <= 4 ? 1 : 0;
        X265Primitives.IntraPred(sizeIdx, (int)dirMode, dst, stride, NeighbourBuf(filter), bFilter);
    }

    public void PredIntraChromaAng(uint dirMode, pixel* dst, long stride, uint log2TrSizeC)
    {
        int tuSize = 1 << (int)log2TrSizeC;
        int sizeIdx = (int)log2TrSizeC - 2;
        int filter = (m_csp == X265_CSP_I444 && (X265Tables.IntraFilterFlags[dirMode] & tuSize) != 0) ? 1 : 0;
        X265Primitives.IntraPred(sizeIdx, (int)dirMode, dst, stride, NeighbourBuf(filter), 0);
    }

    public void InitAdiPattern(X265CuData cu, in CUGeom cuGeom, uint puAbsPartIdx, in IntraNeighbors intraNeighbors, int dirMode)
    {
        int tuSize = 1 << intraNeighbors.log2TrSize;
        int tuSize2 = tuSize << 1;
        X265PicYuv reconPic = cu.m_encData.m_reconPic;
        pixel* adiOrigin = reconPic.GetLumaAddr(cu.m_cuAddr, cuGeom.absPartIdx + puAbsPartIdx);
        long picStride = reconPic.m_stride;

        FillReferenceSamples(adiOrigin, picStride, intraNeighbors, intraNeighbourBuf0);

        pixel* refBuf = intraNeighbourBuf0;
        pixel* fltBuf = intraNeighbourBuf1;
        pixel topLeft = refBuf[0], topLast = refBuf[tuSize2], leftLast = refBuf[tuSize2 + tuSize2];

        bool doFilter = dirMode == ALL_IDX ? ((8 | 16 | 32) & tuSize) != 0 : (X265Tables.IntraFilterFlags[dirMode] & tuSize) != 0;
        if (doFilter)
        {
            if (cu.m_slice.m_sps.bUseStrongIntraSmoothing && tuSize == 32)
            {
                const int threshold = 1 << (X265_DEPTH - 5);
                pixel topMiddle = refBuf[32], leftMiddle = refBuf[tuSize2 + 32];
                if (Math.Abs(topLeft + topLast - (topMiddle << 1)) < threshold &&
                    Math.Abs(topLeft + leftLast - (leftMiddle << 1)) < threshold)
                {
                    // "strong" bilinear interpolation
                    const int shift = 5 + 1;
                    int init = (topLeft << shift) + tuSize;
                    int deltaL = leftLast - topLeft, deltaR = topLast - topLeft;
                    fltBuf[0] = topLeft;
                    for (int i = 1; i < tuSize2; i++)
                    {
                        fltBuf[i + tuSize2] = (pixel)((init + (deltaL * i)) >> shift);
                        fltBuf[i] = (pixel)((init + (deltaR * i)) >> shift);
                    }

                    fltBuf[tuSize2] = topLast;
                    fltBuf[tuSize2 + tuSize2] = leftLast;
                    return;
                }
            }

            X265Primitives.IntraFilter(tuSize, refBuf, fltBuf);
        }
    }

    public void InitAdiPatternChroma(X265CuData cu, in CUGeom cuGeom, uint puAbsPartIdx, in IntraNeighbors intraNeighbors, uint chromaId)
    {
        X265PicYuv reconPic = cu.m_encData.m_reconPic;
        pixel* adiOrigin = reconPic.GetChromaAddr(chromaId, cu.m_cuAddr, cuGeom.absPartIdx + puAbsPartIdx);
        long picStride = reconPic.m_strideC;
        FillReferenceSamples(adiOrigin, picStride, intraNeighbors, intraNeighbourBuf0);
        if (m_csp == X265_CSP_I444)
        {
            X265Primitives.IntraFilter(1 << intraNeighbors.log2TrSize, intraNeighbourBuf0, intraNeighbourBuf1);
        }
    }

    public static void InitIntraNeighbors(X265CuData cu, uint absPartIdx, uint tuDepth, bool isLuma, IntraNeighbors* intraNeighbors)
    {
        int log2TrSize = cu.m_log2CUSize[0] - (int)tuDepth;
        int log2UnitWidth = LOG2_UNIT_SIZE;
        int log2UnitHeight = LOG2_UNIT_SIZE;
        if (!isLuma)
        {
            log2TrSize -= cu.m_hChromaShift;
            log2UnitWidth -= cu.m_hChromaShift;
            log2UnitHeight -= cu.m_vChromaShift;
        }

        bool* bNeighborFlags = intraNeighbors->bNeighborFlags;
        uint tuSize = 1u << log2TrSize;
        int tuWidthInUnits = (int)(tuSize >> log2UnitWidth);
        int tuHeightInUnits = (int)(tuSize >> log2UnitHeight);
        int aboveUnits = tuWidthInUnits << 1;
        int leftUnits = tuHeightInUnits << 1;
        uint partIdxLT = cu.m_absIdxInCTU + absPartIdx;
        uint partIdxRT = (uint)X265Tables.RasterToZscanPtr[X265Tables.ZscanToRasterPtr[partIdxLT] + tuWidthInUnits - 1];
        uint partIdxLB = (uint)X265Tables.RasterToZscanPtr[X265Tables.ZscanToRasterPtr[partIdxLT] + ((tuHeightInUnits - 1) << LOG2_RASTER_SIZE)];

        // intra slices: no constrained intra prediction
        bNeighborFlags[leftUnits] = IsAboveLeftAvailable(cu, partIdxLT);
        int numIntraNeighbor = bNeighborFlags[leftUnits] ? 1 : 0;
        numIntraNeighbor += IsAboveAvailable(cu, partIdxLT, partIdxRT, bNeighborFlags + leftUnits + 1);
        numIntraNeighbor += IsAboveRightAvailable(cu, partIdxRT, bNeighborFlags + leftUnits + 1 + tuWidthInUnits, (uint)tuWidthInUnits);
        numIntraNeighbor += IsLeftAvailable(cu, partIdxLT, partIdxLB, bNeighborFlags + leftUnits - 1);
        numIntraNeighbor += IsBelowLeftAvailable(cu, partIdxLB, bNeighborFlags + tuHeightInUnits - 1, (uint)tuHeightInUnits);

        intraNeighbors->numIntraNeighbor = numIntraNeighbor;
        intraNeighbors->totalUnits = aboveUnits + leftUnits + 1;
        intraNeighbors->aboveUnits = aboveUnits;
        intraNeighbors->leftUnits = leftUnits;
        intraNeighbors->unitWidth = 1 << log2UnitWidth;
        intraNeighbors->unitHeight = 1 << log2UnitHeight;
        intraNeighbors->log2TrSize = log2TrSize;
    }

    public static void FillReferenceSamples(pixel* adiOrigin, long picStride, in IntraNeighbors intraNeighbors, pixel* dst)
    {
        const pixel dcValue = 1 << (X265_DEPTH - 1);
        int numIntraNeighbor = intraNeighbors.numIntraNeighbor;
        int totalUnits = intraNeighbors.totalUnits;
        uint tuSize = 1u << intraNeighbors.log2TrSize;
        uint refSize = (tuSize * 2) + 1;

        if (numIntraNeighbor == 0)
        {
            for (uint i = 0; i < refSize; i++) dst[i] = dcValue;
            for (uint i = 0; i < refSize - 1; i++) dst[i + refSize] = dcValue;
        }
        else if (numIntraNeighbor == totalUnits)
        {
            pixel* adiTemp = adiOrigin - picStride - 1;
            Buffer.MemoryCopy(adiTemp, dst, refSize * sizeof(pixel), refSize * sizeof(pixel));
            adiTemp = adiOrigin - 1;
            for (uint i = 0; i < refSize - 1; i++)
            {
                dst[i + refSize] = adiTemp[0];
                adiTemp += picStride;
            }
        }
        else
        {
            fixed (bool* bNeighborFlags = intraNeighbors.bNeighborFlags)
            {
                int aboveUnits = intraNeighbors.aboveUnits;
                int leftUnits = intraNeighbors.leftUnits;
                int unitWidth = intraNeighbors.unitWidth;
                int unitHeight = intraNeighbors.unitHeight;
                int totalSamples = (leftUnits * unitHeight) + ((aboveUnits + 1) * unitWidth);
                pixel* adiLineBuffer = stackalloc pixel[5 * MAX_CU_SIZE];
                for (int i = 0; i < totalSamples; i++) adiLineBuffer[i] = dcValue;

                // top-left sample
                pixel* adiTemp = adiOrigin - picStride - 1;
                pixel* adi = adiLineBuffer + (leftUnits * unitHeight);
                bool* pNeighborFlags = bNeighborFlags + leftUnits;
                if (*pNeighborFlags)
                {
                    pixel topLeftVal = adiTemp[0];
                    for (int i = 0; i < unitWidth; i++) adi[i] = topLeftVal;
                }

                // left and below-left samples (x265 over-copies, then pads)
                adiTemp += picStride;
                adi--;
                for (int j = 0; j < leftUnits * unitHeight; j++)
                {
                    adi[-j] = adiTemp[j * picStride];
                }

                // above and above-right samples
                adiTemp = adiOrigin - picStride;
                adi = adiLineBuffer + (leftUnits * unitHeight) + unitWidth;
                Buffer.MemoryCopy(adiTemp, adi, aboveUnits * unitWidth * sizeof(pixel), aboveUnits * unitWidth * sizeof(pixel));

                // pad the unavailable reference samples
                int curr = 0;
                int next = 1;
                adi = adiLineBuffer;
                int pAdiLineTopRowOffset = leftUnits * (unitHeight - unitWidth);
                if (!bNeighborFlags[0])
                {
                    while (next < totalUnits && !bNeighborFlags[next]) next++;
                    pixel* pAdiLineNext = adiLineBuffer + (next < leftUnits ? next * unitHeight : pAdiLineTopRowOffset + (next * unitWidth));
                    pixel refSample = *pAdiLineNext;
                    int nextOrTop = Math.Min(next, leftUnits);
                    if (curr < nextOrTop)
                    {
                        int fillSize = unitHeight * (nextOrTop - curr);
                        new Span<pixel>(adi, fillSize).Fill(refSample);
                        curr = nextOrTop;
                        adi += fillSize;
                    }

                    if (curr < next)
                    {
                        int fillSize = unitWidth * (next - curr);
                        new Span<pixel>(adi, fillSize).Fill(refSample);
                        curr = next;
                        adi += fillSize;
                    }
                }

                while (curr < totalUnits)
                {
                    if (!bNeighborFlags[curr])
                    {
                        int numSamplesInCurrUnit = curr >= leftUnits ? unitWidth : unitHeight;
                        pixel refSample = *(adi - 1);
                        for (int i = 0; i < numSamplesInCurrUnit; i++) adi[i] = refSample;
                        adi += numSamplesInCurrUnit;
                        curr++;
                    }
                    else
                    {
                        adi += curr >= leftUnits ? unitWidth : unitHeight;
                        curr++;
                    }
                }

                // copy the processed samples
                adi = adiLineBuffer + refSize + unitWidth - 2;
                Buffer.MemoryCopy(adi, dst, refSize * sizeof(pixel), refSize * sizeof(pixel));
                adi = adiLineBuffer + refSize - 1;
                for (int i = 0; i < (int)refSize - 1; i++)
                {
                    dst[i + refSize] = adi[-(i + 1)];
                }
            }
        }
    }

    private static bool IsAboveLeftAvailable(X265CuData cu, uint partIdxLT) => cu.GetPUAboveLeft(out _, partIdxLT) != null;

    private static int IsAboveAvailable(X265CuData cu, uint partIdxLT, uint partIdxRT, bool* bValidFlags)
    {
        int rasterPartBegin = X265Tables.ZscanToRasterPtr[partIdxLT];
        int rasterPartEnd = X265Tables.ZscanToRasterPtr[partIdxRT];
        int numIntra = 0;
        for (int rasterPart = rasterPartBegin; rasterPart <= rasterPartEnd; rasterPart++, bValidFlags++)
        {
            if (cu.GetPUAbove(out _, (uint)X265Tables.RasterToZscanPtr[rasterPart]) != null)
            {
                numIntra++;
                *bValidFlags = true;
            }
            else
            {
                *bValidFlags = false;
            }
        }

        return numIntra;
    }

    private static int IsLeftAvailable(X265CuData cu, uint partIdxLT, uint partIdxLB, bool* bValidFlags)
    {
        int rasterPartBegin = X265Tables.ZscanToRasterPtr[partIdxLT];
        int rasterPartEnd = X265Tables.ZscanToRasterPtr[partIdxLB];
        int numIntra = 0;
        for (int rasterPart = rasterPartBegin; rasterPart <= rasterPartEnd; rasterPart += RASTER_SIZE, bValidFlags--)
        {
            if (cu.GetPULeft(out _, (uint)X265Tables.RasterToZscanPtr[rasterPart]) != null)
            {
                numIntra++;
                *bValidFlags = true;
            }
            else
            {
                *bValidFlags = false;
            }
        }

        return numIntra;
    }

    private static int IsAboveRightAvailable(X265CuData cu, uint partIdxRT, bool* bValidFlags, uint numUnits)
    {
        int numIntra = 0;
        for (uint offset = 1; offset <= numUnits; offset++, bValidFlags++)
        {
            if (cu.GetPUAboveRightAdi(out _, partIdxRT, offset) != null)
            {
                numIntra++;
                *bValidFlags = true;
            }
            else
            {
                *bValidFlags = false;
            }
        }

        return numIntra;
    }

    private static int IsBelowLeftAvailable(X265CuData cu, uint partIdxLB, bool* bValidFlags, uint numUnits)
    {
        int numIntra = 0;
        for (uint offset = 1; offset <= numUnits; offset++, bValidFlags--)
        {
            if (cu.GetPUBelowLeftAdi(out _, partIdxLB, offset) != null)
            {
                numIntra++;
                *bValidFlags = true;
            }
            else
            {
                *bValidFlags = false;
            }
        }

        return numIntra;
    }
}
