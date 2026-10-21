// Port of x265 common/deblock.{h,cpp} and the deblocking primitives of common/loopfilter.cpp (intra pictures:
// boundary strength 2 on every TU / CU edge, 0 at picture edges).
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
internal static unsafe class X265Deblock
{
    public const int EDGE_VER = 0, EDGE_HOR = 1;
    private const int DEBLOCK_SMALLEST_BLOCK = 8;
    private const int DEFAULT_INTRA_TC_OFFSET = 2;

    private static readonly byte[] s_tcTable =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2,
        2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 24,
    };

    private static readonly byte[] s_betaTable =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17,
        18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    };

    public static void DeblockCTU(X265CuData ctu, CUGeom* cuGeom, int dir)
    {
        byte* blockStrength = stackalloc byte[MAX_NUM_PARTITIONS];
        new Span<byte>(blockStrength, (int)cuGeom->numPartitions).Clear();
        DeblockCU(ctu, cuGeom, dir, blockStrength);
    }

    private static byte BsCuEdge(X265CuData cu, uint absPartIdx, int dir)
    {
        if (dir == EDGE_VER)
        {
            if (cu.m_cuPelX + X265Tables.ZscanToPelXPtr[absPartIdx] > 0)
            {
                return cu.GetPULeft(out _, absPartIdx) != null ? (byte)2 : (byte)0;
            }
        }
        else
        {
            if (cu.m_cuPelY + X265Tables.ZscanToPelYPtr[absPartIdx] > 0)
            {
                return cu.GetPUAbove(out _, absPartIdx) != null ? (byte)2 : (byte)0;
            }
        }

        return 0;
    }

    private static void DeblockCU(X265CuData cu, CUGeom* cuGeom, int dir, byte* blockStrength)
    {
        uint absPartIdx = cuGeom->absPartIdx;
        uint depth = cuGeom->depth;
        if (cu.m_predMode[absPartIdx] == MODE_NONE)
        {
            return;
        }

        if (cu.m_cuDepth[absPartIdx] > depth)
        {
            for (uint subPartIdx = 0; subPartIdx < 4; subPartIdx++)
            {
                CUGeom* childGeom = cuGeom + cuGeom->childOffset + subPartIdx;
                if ((childGeom->flags & CUGeom.PRESENT) != 0)
                {
                    DeblockCU(cu, childGeom, dir, blockStrength);
                }
            }

            return;
        }

        uint numUnits = 1u << (int)(cuGeom->log2CUSize - LOG2_UNIT_SIZE);
        SetEdgefilterPU(cu, absPartIdx, dir, blockStrength, numUnits);
        SetEdgefilterTU(cu, absPartIdx, 0, dir, blockStrength);
        SetEdgefilterMultiple(absPartIdx, dir, 0, BsCuEdge(cu, absPartIdx, dir), blockStrength, numUnits);

        uint numParts = cuGeom->numPartitions;
        for (uint partIdx = absPartIdx; partIdx < absPartIdx + numParts; partIdx++)
        {
            bool bsCheck = (partIdx & (1u << dir)) == 0;
            if (bsCheck && blockStrength[partIdx] != 0)
            {
                blockStrength[partIdx] = GetBoundaryStrength(cu, dir, partIdx);
            }
        }

        const uint partIdxIncr = DEBLOCK_SMALLEST_BLOCK >> LOG2_UNIT_SIZE;
        int shiftFactor = dir == EDGE_VER ? cu.m_hChromaShift : cu.m_vChromaShift;
        uint chromaMask = (uint)(((DEBLOCK_SMALLEST_BLOCK << shiftFactor) >> LOG2_UNIT_SIZE) - 1);
        uint e0 = (uint)(dir == EDGE_VER ? X265Tables.ZscanToPelXPtr[absPartIdx] : X265Tables.ZscanToPelYPtr[absPartIdx]) >> LOG2_UNIT_SIZE;
        for (uint e = 0; e < numUnits; e += partIdxIncr)
        {
            EdgeFilterLuma(cu, absPartIdx, depth, dir, (int)e, blockStrength);
            if (((e0 + e) & chromaMask) == 0 && cu.m_chromaFormat != X265_CSP_I400)
            {
                EdgeFilterChroma(cu, absPartIdx, depth, dir, (int)e, blockStrength);
            }
        }
    }

    private static uint CalcBsIdx(uint absPartIdx, int dir, int edgeIdx, int baseUnitIdx)
    {
        if (dir != 0)
        {
            return (uint)X265Tables.RasterToZscanPtr[X265Tables.ZscanToRasterPtr[absPartIdx] + (edgeIdx << LOG2_RASTER_SIZE) + baseUnitIdx];
        }

        return (uint)X265Tables.RasterToZscanPtr[X265Tables.ZscanToRasterPtr[absPartIdx] + (baseUnitIdx << LOG2_RASTER_SIZE) + edgeIdx];
    }

    private static void SetEdgefilterMultiple(uint scanIdx, int dir, int edgeIdx, byte value, byte* blockStrength, uint numUnits)
    {
        for (uint i = 0; i < numUnits; i++)
        {
            blockStrength[CalcBsIdx(scanIdx, dir, edgeIdx, (int)i)] = value;
        }
    }

    private static void SetEdgefilterTU(X265CuData cu, uint absPartIdx, uint tuDepth, int dir, byte* blockStrength)
    {
        uint log2TrSize = cu.m_log2CUSize[absPartIdx] - tuDepth;
        if (cu.m_tuDepth[absPartIdx] > tuDepth)
        {
            uint qNumParts = 1u << (int)((log2TrSize - LOG2_UNIT_SIZE - 1) * 2);
            for (uint qIdx = 0; qIdx < 4; ++qIdx, absPartIdx += qNumParts)
            {
                SetEdgefilterTU(cu, absPartIdx, tuDepth + 1, dir, blockStrength);
            }

            return;
        }

        uint numUnits = 1u << (int)(log2TrSize - LOG2_UNIT_SIZE);
        SetEdgefilterMultiple(absPartIdx, dir, 0, 2, blockStrength, numUnits);
    }

    private static void SetEdgefilterPU(X265CuData cu, uint absPartIdx, int dir, byte* blockStrength, uint numUnits)
    {
        uint hNumUnits = numUnits >> 1;
        if (cu.m_partSize[absPartIdx] == SIZE_NxN)
        {
            SetEdgefilterMultiple(absPartIdx, dir, (int)hNumUnits, 1, blockStrength, numUnits);
        }
    }

    private static byte GetBoundaryStrength(X265CuData cuQ, int dir, uint partQ)
    {
        X265CuData cuP = (dir == EDGE_VER ? cuQ.GetPULeft(out uint partP, partQ) : cuQ.GetPUAbove(out partP, partQ))!;
        if (cuP.IsIntra(partP) || cuQ.IsIntra(partQ))
        {
            return 2;
        }

        return 1;   // intra pictures only
    }

    private static int CalcDP(pixel* src, long offset) => Math.Abs(src[-offset * 3] - (2 * src[-offset * 2]) + src[-offset]);

    private static int CalcDQ(pixel* src, long offset) => Math.Abs(src[0] - (2 * src[offset]) + src[offset * 2]);

    private static bool UseStrongFiltering(long offset, int beta, int tc, pixel* src)
    {
        short m4 = (short)src[0];
        short m3 = (short)src[-offset];
        short m7 = (short)src[offset * 3];
        short m0 = (short)src[-offset * 4];
        int strong = Math.Abs(m0 - m3) + Math.Abs(m7 - m4);
        return strong < (beta >> 3) && Math.Abs(m3 - m4) < (((tc * 5) + 1) >> 1);
    }

    private static void PelFilterLuma(pixel* src, long srcStep, long offset, int tc, int maskP, int maskQ, int maskP1, int maskQ1)
    {
        int thrCut = tc * 10;
        int tc2 = tc >> 1;
        maskP1 &= maskP;
        maskQ1 &= maskQ;
        for (int i = 0; i < UNIT_SIZE; i++, src += srcStep)
        {
            short m4 = (short)src[0];
            short m3 = (short)src[-offset];
            short m5 = (short)src[offset];
            short m2 = (short)src[-offset * 2];
            int delta = ((9 * (m4 - m3)) - (3 * (m5 - m2)) + 8) >> 4;
            if (Math.Abs(delta) < thrCut)
            {
                delta = Clip3(-tc, tc, delta);
                src[-offset] = ClipPixel(m3 + (delta & maskP));
                src[0] = ClipPixel(m4 - (delta & maskQ));
                if (maskP1 != 0)
                {
                    short m1 = (short)src[-offset * 3];
                    int delta1 = Clip3(-tc2, tc2, ((((m1 + m3 + 1) >> 1) - m2 + delta) >> 1));
                    src[-offset * 2] = ClipPixel(m2 + delta1);
                }

                if (maskQ1 != 0)
                {
                    short m6 = (short)src[offset * 2];
                    int delta2 = Clip3(-tc2, tc2, ((((m6 + m4 + 1) >> 1) - m5 - delta) >> 1));
                    src[offset] = ClipPixel(m5 + delta2);
                }
            }
        }
    }

    private static void PelFilterLumaStrong(pixel* src, long srcStep, long offset, int tcP, int tcQ)
    {
        for (int i = 0; i < UNIT_SIZE; i++, src += srcStep)
        {
            short m4 = (short)src[0];
            short m3 = (short)src[-offset];
            short m5 = (short)src[offset];
            short m2 = (short)src[-offset * 2];
            short m6 = (short)src[offset * 2];
            short m1 = (short)src[-offset * 3];
            short m7 = (short)src[offset * 3];
            short m0 = (short)src[-offset * 4];
            src[-offset * 3] = (pixel)(Clip3(-tcP, tcP, (((2 * m0) + (3 * m1) + m2 + m3 + m4 + 4) >> 3) - m1) + m1);
            src[-offset * 2] = (pixel)(Clip3(-tcP, tcP, ((m1 + m2 + m3 + m4 + 2) >> 2) - m2) + m2);
            src[-offset] = (pixel)(Clip3(-tcP, tcP, ((m1 + (2 * m2) + (2 * m3) + (2 * m4) + m5 + 4) >> 3) - m3) + m3);
            src[0] = (pixel)(Clip3(-tcQ, tcQ, ((m2 + (2 * m3) + (2 * m4) + (2 * m5) + m6 + 4) >> 3) - m4) + m4);
            src[offset] = (pixel)(Clip3(-tcQ, tcQ, ((m3 + m4 + m5 + m6 + 2) >> 2) - m5) + m5);
            src[offset * 2] = (pixel)(Clip3(-tcQ, tcQ, ((m3 + m4 + m5 + (3 * m6) + (2 * m7) + 4) >> 3) - m6) + m6);
        }
    }

    private static void EdgeFilterLuma(X265CuData cuQ, uint absPartIdx, uint depth, int dir, int edge, byte* blockStrength)
    {
        X265PicYuv reconPic = cuQ.m_encData.m_reconPic;
        pixel* src = reconPic.GetLumaAddr(cuQ.m_cuAddr, absPartIdx);
        long stride = reconPic.m_stride;
        X265Pps pps = cuQ.m_slice.m_pps;
        long offset, srcStep;
        const int maskP = -1, maskQ = -1;
        int betaOffset = pps.deblockingFilterBetaOffsetDiv2 << 1;
        int tcOffset = pps.deblockingFilterTcOffsetDiv2 << 1;

        if (dir == EDGE_VER)
        {
            offset = 1;
            srcStep = stride;
            src += edge << LOG2_UNIT_SIZE;
        }
        else
        {
            offset = stride;
            srcStep = 1;
            src += (edge << LOG2_UNIT_SIZE) * stride;
        }

        uint numUnits = cuQ.m_slice.m_sps.numPartInCUSize >> (int)depth;
        for (uint idx = 0; idx < numUnits; idx++)
        {
            uint partQ = CalcBsIdx(absPartIdx, dir, edge, (int)idx);
            uint bs = blockStrength[partQ];
            if (bs == 0)
            {
                continue;
            }

            X265CuData cuP = (dir == EDGE_VER ? cuQ.GetPULeft(out uint partP, partQ) : cuQ.GetPUAbove(out partP, partQ))!;
            int qpQ = cuQ.m_qp[partQ];
            int qpP = cuP.m_qp[partP];
            int qp = (qpP + qpQ + 1) >> 1;
            int indexB = Clip3(0, QP_MAX_SPEC, qp + betaOffset);
            int beta = s_betaTable[indexB] << (X265_DEPTH - 8);

            long unitOffset = (idx * srcStep) << LOG2_UNIT_SIZE;
            int dp0 = CalcDP(src + unitOffset, offset);
            int dq0 = CalcDQ(src + unitOffset, offset);
            int dp3 = CalcDP(src + unitOffset + (srcStep * 3), offset);
            int dq3 = CalcDQ(src + unitOffset + (srcStep * 3), offset);
            int d0 = dp0 + dq0;
            int d3 = dp3 + dq3;
            int d = d0 + d3;
            if (d >= beta)
            {
                continue;
            }

            int indexTC = Clip3(0, QP_MAX_SPEC + DEFAULT_INTRA_TC_OFFSET, (int)(qp + (DEFAULT_INTRA_TC_OFFSET * (bs - 1)) + tcOffset));
            int tc = s_tcTable[indexTC] << (X265_DEPTH - 8);
            bool sw = (2 * d0) < (beta >> 2) && (2 * d3) < (beta >> 2) &&
                      UseStrongFiltering(offset, beta, tc, src + unitOffset) &&
                      UseStrongFiltering(offset, beta, tc, src + unitOffset + (srcStep * 3));
            if (sw)
            {
                int tc2 = 2 * tc;
                PelFilterLumaStrong(src + unitOffset, srcStep, offset, tc2 & maskP, tc2 & maskQ);
            }
            else
            {
                int sideThreshold = (beta + (beta >> 1)) >> 3;
                int dp = dp0 + dp3;
                int dq = dq0 + dq3;
                int maskP1 = dp < sideThreshold ? -1 : 0;
                int maskQ1 = dq < sideThreshold ? -1 : 0;
                PelFilterLuma(src + unitOffset, srcStep, offset, tc, maskP, maskQ, maskP1, maskQ1);
            }
        }
    }

    private static void PelFilterChroma(pixel* src, long srcStep, long offset, int tc, int maskP, int maskQ)
    {
        for (int i = 0; i < 4; i++)
        {
            pixel* s = src + (i * srcStep);
            short m2 = (short)s[-offset * 2];
            short m3 = (short)s[-offset];
            short m4 = (short)s[0];
            short m5 = (short)s[offset];
            int delta = Clip3(-tc, tc, (((m4 - m3) * 4) + m2 - m5 + 4) >> 3);
            s[-offset] = ClipPixel(m3 + (delta & maskP));
            s[0] = ClipPixel(m4 - (delta & maskQ));
        }
    }

    private static void EdgeFilterChroma(X265CuData cuQ, uint absPartIdx, uint depth, int dir, int edge, byte* blockStrength)
    {
        int chFmt = cuQ.m_chromaFormat, chromaShift;
        long offset, srcStep;
        X265Pps pps = cuQ.m_slice.m_pps;
        const int maskP = -1, maskQ = -1;
        int tcOffset = pps.deblockingFilterTcOffsetDiv2 << 1;
        X265PicYuv reconPic = cuQ.m_encData.m_reconPic;
        long stride = reconPic.m_strideC;
        long srcOffset = reconPic.m_cuOffsetC[cuQ.m_cuAddr] + reconPic.m_buOffsetC[absPartIdx];

        if (dir == EDGE_VER)
        {
            chromaShift = cuQ.m_vChromaShift;
            srcOffset += edge << (LOG2_UNIT_SIZE - cuQ.m_hChromaShift);
            offset = 1;
            srcStep = stride;
        }
        else
        {
            chromaShift = cuQ.m_hChromaShift;
            srcOffset += (edge * stride) << (LOG2_UNIT_SIZE - cuQ.m_vChromaShift);
            offset = stride;
            srcStep = 1;
        }

        pixel* srcChroma0 = reconPic.m_picOrg[1] + srcOffset;
        pixel* srcChroma1 = reconPic.m_picOrg[2] + srcOffset;
        uint numUnits = cuQ.m_slice.m_sps.numPartInCUSize >> (int)(depth + (uint)chromaShift);
        for (uint idx = 0; idx < numUnits; idx++)
        {
            uint partQ = CalcBsIdx(absPartIdx, dir, edge, (int)(idx << chromaShift));
            uint bs = blockStrength[partQ];
            if (bs <= 1)
            {
                continue;
            }

            X265CuData cuP = (dir == EDGE_VER ? cuQ.GetPULeft(out uint partP, partQ) : cuQ.GetPUAbove(out partP, partQ))!;
            int qpQ = cuQ.m_qp[partQ];
            int qpP = cuP.m_qp[partP];
            int qpA = (qpP + qpQ + 1) >> 1;
            long unitOffset = (idx * srcStep) << LOG2_UNIT_SIZE;
            for (int chromaIdx = 0; chromaIdx < 2; chromaIdx++)
            {
                int qp = qpA + pps.chromaQpOffset[chromaIdx];
                if (qp >= 30)
                {
                    qp = chFmt == X265_CSP_I420 ? X265Tables.ChromaScale[qp] : Math.Min(qp, QP_MAX_SPEC);
                }

                int indexTC = Clip3(0, QP_MAX_SPEC + DEFAULT_INTRA_TC_OFFSET, qp + DEFAULT_INTRA_TC_OFFSET + tcOffset);
                int tc = s_tcTable[indexTC] << (X265_DEPTH - 8);
                pixel* srcC = chromaIdx == 0 ? srcChroma0 : srcChroma1;
                PelFilterChroma(srcC + unitOffset, srcStep, offset, tc, maskP, maskQ);
            }
        }
    }
}
