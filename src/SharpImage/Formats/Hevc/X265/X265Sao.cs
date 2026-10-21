// Port of x265 encoder/sao.{h,cpp} (class SAO) and the SAO primitives of common/loopfilter.cpp: per-CTU
// statistics on the deblocked picture, rate-distortion choice of edge / band offsets (with merge left / up), and the
// in-place application using the saved above / left neighbour lines. One instance per CTU row, as in x265's
// FrameFilter::ParallelFilter. Only the generic (non-SIMD-shaped) application paths are needed: x265's 16-wide
// fast paths give the same samples.
using System;
using static SharpImage.Formats.Hevc.X265.X265Const;

namespace SharpImage.Formats.Hevc.X265;

[System.Runtime.CompilerServices.SkipLocalsInit]
internal sealed unsafe class X265Sao
{
    public const int SAO_EO_0 = 0, SAO_EO_1 = 1, SAO_EO_2 = 2, SAO_EO_3 = 3, SAO_BO = 4;
    public const int SAO_MERGE_NONE = 0, SAO_MERGE_LEFT = 1, SAO_MERGE_UP = 2;
    private const int MAX_NUM_SAO_CLASS = 32;
    private const int SAO_BIT_INC = 0;
    private const int OFFSET_THRESH = 1 << 3;   // 1 << min(X265_DEPTH - 5, 5)
    private const int NUM_EDGETYPE = 5;
    private const int SAO_BO_BITS = 5;
    private static readonly uint[] s_eoTable = { 1, 2, 0, 3, 4 };

    // PerPlane: [plane][type][class]
    private const int PerPlaneSize = 3 * MAX_NUM_SAO_TYPE * MAX_NUM_SAO_CLASS;
    private readonly int* m_count;
    private readonly int* m_offset;
    private readonly int* m_offsetOrg;
    private readonly sbyte* m_offsetBo;   // [3][32]
    private readonly sbyte* m_offsetEo;   // [3][5]
    private readonly byte*[] m_tmpU = new byte*[3];
    private readonly byte*[] m_tmpL1 = new byte*[3];
    private readonly byte*[] m_tmpL2 = new byte*[3];
    private readonly int m_numCuInWidth;
    private readonly int m_hChromaShift, m_vChromaShift;
    private readonly X265Param m_param;
    private X265FrameData m_frame = null!;

    public readonly X265Entropy m_entropyCoder;
    private readonly X265Entropy rdCur, rdNext, rdTemp;

    public X265Sao(X265Arena arena, X265Param param)
    {
        m_param = param;
        m_hChromaShift = m_vChromaShift = param.internalCsp == X265_CSP_I420 ? 1 : 0;
        m_numCuInWidth = (param.sourceWidth + param.maxCUSize - 1) / param.maxCUSize;
        m_count = arena.Alloc<int>(PerPlaneSize);
        m_offset = arena.Alloc<int>(PerPlaneSize);
        m_offsetOrg = arena.Alloc<int>(PerPlaneSize);
        m_offsetBo = arena.Alloc<sbyte>(3 * MAX_NUM_SAO_CLASS);
        m_offsetEo = arena.Alloc<sbyte>(3 * NUM_EDGETYPE);
        for (int i = 0; i < (param.internalCsp != X265_CSP_I400 ? 3 : 1); i++)
        {
            m_tmpL1[i] = arena.Alloc<byte>(param.maxCUSize + 1);
            m_tmpL2[i] = arena.Alloc<byte>(param.maxCUSize + 1);
            m_tmpU[i] = arena.Alloc<byte>((m_numCuInWidth * param.maxCUSize) + 2 + 32) + 1;
        }

        m_entropyCoder = new X265Entropy(arena);
        rdCur = new X265Entropy(arena);
        rdNext = new X265Entropy(arena);
        rdTemp = new X265Entropy(arena);
    }

    private static int Idx(int plane, int type, int cls) => (((plane * MAX_NUM_SAO_TYPE) + type) * MAX_NUM_SAO_CLASS) + cls;

    private static int SignOf(int x) => (x >> 31) | (int)((uint)-x >> 31);

    private static int RoundIBDI(int num, int den) => num >= 0 ? ((num * 2) + den) / (den * 2) : -(((-num * 2) + den) / (den * 2));

    // int arithmetic, widened only on return (as in x265)
    private static long EstSaoDist(int count, int offset, int offsetOrg) => unchecked(((count * offset) - (offsetOrg * 2)) * offset);

    public void StartSlice(X265FrameData frame, X265Entropy initState)
    {
        m_frame = frame;
        m_entropyCoder.Load(initState);
        rdNext.Load(initState);
        rdCur.Load(initState);
        X265SaoParam saoParam = frame.m_saoParam!;
        saoParam.bSaoFlag[0] = true;
        saoParam.bSaoFlag[1] = m_param.internalCsp != X265_CSP_I400;
    }

    // ---- application ----

    private void ApplyPixelOffsets(int addr, int typeIdx, int plane)
    {
        X265PicYuv reconPic = m_frame.m_reconPic;
        byte* rec = reconPic.GetPlaneAddr((uint)plane, (uint)addr);
        long stride = plane != 0 ? reconPic.m_strideC : reconPic.m_stride;
        uint picWidth = (uint)m_param.sourceWidth;
        uint picHeight = (uint)m_param.sourceHeight;
        X265CuData cu = m_frame.GetPicCTU((uint)addr);
        int ctuWidth = m_param.maxCUSize;
        int ctuHeight = m_param.maxCUSize;
        uint lpelx = cu.m_cuPelX;
        uint tpely = cu.m_cuPelY;
        uint firstRowInSlice = cu.m_bFirstRowInSlice;
        uint lastRowInSlice = cu.m_bLastRowInSlice;
        bool bAboveUnavail = tpely == 0 || firstRowInSlice != 0;

        if (lastRowInSlice != 0)
        {
            picHeight = Math.Min(picHeight, tpely + (uint)ctuHeight);
        }

        if (plane != 0)
        {
            picWidth >>= m_hChromaShift;
            picHeight >>= m_vChromaShift;
            ctuWidth >>= m_hChromaShift;
            ctuHeight >>= m_vChromaShift;
            lpelx >>= m_hChromaShift;
            tpely >>= m_vChromaShift;
        }

        uint rpelx = Math.Min(lpelx + (uint)ctuWidth, picWidth);
        uint bpely = Math.Min(tpely + (uint)ctuHeight, picHeight);
        ctuWidth = (int)(rpelx - lpelx);
        ctuHeight = (int)(bpely - tpely);

        sbyte* upBuff1Base = stackalloc sbyte[MAX_CU_SIZE + 2];
        sbyte* upBufftBase = stackalloc sbyte[MAX_CU_SIZE + 2];
        sbyte* upBuff1 = upBuff1Base + 1;
        sbyte* upBufft = upBufftBase + 1;
        byte* tmpL = m_tmpL1[plane];
        byte* tmpU = m_tmpU[plane] + lpelx;
        sbyte* offsetEo = m_offsetEo + (plane * NUM_EDGETYPE);

        switch (typeIdx)
        {
            case SAO_EO_0:
            {
                int startX = lpelx == 0 ? 1 : 0;
                int endX = rpelx == picWidth ? ctuWidth - 1 : ctuWidth;
                for (int y = 0; y < ctuHeight; y++, rec += stride)
                {
                    int signLeft = SignOf(rec[startX] - tmpL[y]);
                    for (int x = startX; x < endX; x++)
                    {
                        int signRight = SignOf(rec[x] - rec[x + 1]);
                        int edgeType = signRight + signLeft + 2;
                        signLeft = -signRight;
                        rec[x] = ClipPixel(rec[x] + offsetEo[edgeType]);
                    }
                }

                break;
            }

            case SAO_EO_1:
            {
                int startY = bAboveUnavail ? 1 : 0;
                int endY = bpely == picHeight ? ctuHeight - 1 : ctuHeight;
                if (startY != 0)
                {
                    rec += stride;
                }

                for (int x = 0; x < ctuWidth; x++)
                {
                    upBuff1[x] = (sbyte)SignOf(rec[x] - tmpU[x]);
                }

                for (int y = startY; y < endY; y++, rec += stride)
                {
                    for (int x = 0; x < ctuWidth; x++)
                    {
                        int signDown = SignOf(rec[x] - rec[x + stride]);
                        int edgeType = signDown + upBuff1[x] + 2;
                        upBuff1[x] = (sbyte)-signDown;
                        rec[x] = ClipPixel(rec[x] + offsetEo[edgeType]);
                    }
                }

                break;
            }

            case SAO_EO_2:
            {
                int startX = lpelx == 0 ? 1 : 0;
                int endX = rpelx == picWidth ? ctuWidth - 1 : ctuWidth;
                int startY = bAboveUnavail ? 1 : 0;
                int endY = bpely == picHeight ? ctuHeight - 1 : ctuHeight;
                if (startY != 0)
                {
                    rec += stride;
                }

                for (int x = startX; x < endX; x++)
                {
                    upBuff1[x] = (sbyte)SignOf(rec[x] - tmpU[x - 1]);
                }

                for (int y = startY; y < endY; y++, rec += stride)
                {
                    upBufft[startX] = (sbyte)SignOf(rec[stride + startX] - tmpL[y]);
                    for (int x = startX; x < endX; x++)
                    {
                        int signDown = SignOf(rec[x] - rec[x + stride + 1]);
                        int edgeType = signDown + upBuff1[x] + 2;
                        upBufft[x + 1] = (sbyte)-signDown;
                        rec[x] = ClipPixel(rec[x] + offsetEo[edgeType]);
                    }

                    sbyte* t = upBuff1;
                    upBuff1 = upBufft;
                    upBufft = t;
                }

                break;
            }

            case SAO_EO_3:
            {
                int startX = lpelx == 0 ? 1 : 0;
                int endX = rpelx == picWidth ? ctuWidth - 1 : ctuWidth;
                int startY = bAboveUnavail ? 1 : 0;
                int endY = bpely == picHeight ? ctuHeight - 1 : ctuHeight;
                if (startY != 0)
                {
                    rec += stride;
                }

                for (int x = startX - 1; x < endX; x++)
                {
                    upBuff1[x] = (sbyte)SignOf(rec[x] - tmpU[x + 1]);
                }

                for (int y = startY; y < endY; y++, rec += stride)
                {
                    int x = startX;
                    int signDown = SignOf(rec[x] - tmpL[y + 1]);
                    int edgeType = signDown + upBuff1[x] + 2;
                    upBuff1[x - 1] = (sbyte)-signDown;
                    rec[x] = ClipPixel(rec[x] + offsetEo[edgeType]);
                    for (x = startX + 1; x < endX; x++)
                    {
                        signDown = SignOf(rec[x] - rec[x + stride - 1]);
                        edgeType = signDown + upBuff1[x] + 2;
                        upBuff1[x - 1] = (sbyte)-signDown;
                        rec[x] = ClipPixel(rec[x] + offsetEo[edgeType]);
                    }

                    upBuff1[endX - 1] = (sbyte)SignOf(rec[endX - 1 + stride] - rec[endX]);
                }

                break;
            }

            case SAO_BO:
            {
                sbyte* offsetBo = m_offsetBo + (plane * MAX_NUM_SAO_CLASS);
                const int boShift = X265_DEPTH - SAO_BO_BITS;
                for (int y = 0; y < ctuHeight; y++, rec += stride)
                {
                    for (int x = 0; x < ctuWidth; x++)
                    {
                        rec[x] = ClipPixel(rec[x] + offsetBo[rec[x] >> boShift]);
                    }
                }

                break;
            }
        }
    }

    private void SetOffsets(int plane, in SaoCtuParam p)
    {
        if (p.typeIdx == SAO_BO)
        {
            sbyte* bo = m_offsetBo + (plane * MAX_NUM_SAO_CLASS);
            new Span<sbyte>(bo, MAX_NUM_SAO_CLASS).Clear();
            for (int i = 0; i < SAO_NUM_OFFSET; i++)
            {
                bo[(int)((p.bandPos + (uint)i) & (MAX_NUM_SAO_CLASS - 1))] = (sbyte)(p.GetOffset(i) << SAO_BIT_INC);
            }
        }
        else
        {
            int* offset = stackalloc int[NUM_EDGETYPE];
            offset[0] = 0;
            for (int i = 0; i < SAO_NUM_OFFSET; i++)
            {
                offset[i + 1] = p.GetOffset(i) << SAO_BIT_INC;
            }

            for (int edgeType = 0; edgeType < NUM_EDGETYPE; edgeType++)
            {
                m_offsetEo[(plane * NUM_EDGETYPE) + edgeType] = (sbyte)offset[s_eoTable[edgeType]];
            }
        }
    }

    public void GenerateLumaOffsets(SaoCtuParam[] ctuParam, int idxY, int idxX)
    {
        X265PicYuv reconPic = m_frame.m_reconPic;
        long stride = reconPic.m_stride;
        int ctuWidth = m_param.maxCUSize;
        int ctuHeight = m_param.maxCUSize;
        int addr = (idxY * m_numCuInWidth) + idxX;
        byte* rec = reconPic.GetLumaAddr((uint)addr);
        if (idxX == 0)
        {
            for (int i = 0; i < ctuHeight + 1; i++)
            {
                m_tmpL1[0][i] = rec[0];
                rec += stride;
            }
        }

        bool mergeLeftFlag = ctuParam[addr].mergeMode == SAO_MERGE_LEFT;
        int typeIdx = ctuParam[addr].typeIdx;
        if (idxX != m_numCuInWidth - 1)
        {
            rec = reconPic.GetLumaAddr((uint)addr);
            for (int i = 0; i < ctuHeight + 1; i++)
            {
                m_tmpL2[0][i] = rec[ctuWidth - 1];
                rec += stride;
            }
        }

        if (typeIdx >= 0)
        {
            if (!mergeLeftFlag)
            {
                SetOffsets(0, ctuParam[addr]);
            }

            ApplyPixelOffsets(addr, typeIdx, 0);
        }

        byte* t0 = m_tmpL1[0]; m_tmpL1[0] = m_tmpL2[0]; m_tmpL2[0] = t0;
    }

    public void GenerateChromaOffsets(SaoCtuParam[][] ctuParam, int idxY, int idxX)
    {
        X265PicYuv reconPic = m_frame.m_reconPic;
        long stride = reconPic.m_strideC;
        int ctuWidth = m_param.maxCUSize >> m_hChromaShift;
        int ctuHeight = m_param.maxCUSize >> m_vChromaShift;
        int addr = (idxY * m_numCuInWidth) + idxX;
        byte* recCb = reconPic.GetChromaAddr(1, (uint)addr);
        byte* recCr = reconPic.GetChromaAddr(2, (uint)addr);
        if (idxX == 0)
        {
            for (int i = 0; i < ctuHeight + 1; i++)
            {
                m_tmpL1[1][i] = recCb[0];
                m_tmpL1[2][i] = recCr[0];
                recCb += stride;
                recCr += stride;
            }
        }

        bool mergeLeftFlagCb = ctuParam[1][addr].mergeMode == SAO_MERGE_LEFT;
        int typeIdxCb = ctuParam[1][addr].typeIdx;
        bool mergeLeftFlagCr = ctuParam[2][addr].mergeMode == SAO_MERGE_LEFT;
        int typeIdxCr = ctuParam[2][addr].typeIdx;
        if (idxX != m_numCuInWidth - 1)
        {
            recCb = reconPic.GetChromaAddr(1, (uint)addr);
            recCr = reconPic.GetChromaAddr(2, (uint)addr);
            for (int i = 0; i < ctuHeight + 1; i++)
            {
                m_tmpL2[1][i] = recCb[ctuWidth - 1];
                m_tmpL2[2][i] = recCr[ctuWidth - 1];
                recCb += stride;
                recCr += stride;
            }
        }

        if (typeIdxCb >= 0)
        {
            if (!mergeLeftFlagCb)
            {
                SetOffsets(1, ctuParam[1][addr]);
            }

            ApplyPixelOffsets(addr, typeIdxCb, 1);
        }

        if (typeIdxCr >= 0)
        {
            if (!mergeLeftFlagCr)
            {
                SetOffsets(2, ctuParam[2][addr]);
            }

            ApplyPixelOffsets(addr, typeIdxCb, 2);   // x265 passes the Cb type (chroma types are always equal)
        }

        byte* t1 = m_tmpL1[1]; m_tmpL1[1] = m_tmpL2[1]; m_tmpL2[1] = t1;
        byte* t2 = m_tmpL1[2]; m_tmpL1[2] = m_tmpL2[2]; m_tmpL2[2] = t2;
    }

    /// <summary>FrameFilter::ParallelFilter::copySaoAboveRef: the deblocked line above a CTU before SAO changes it.</summary>
    public void CopySaoAboveRef(X265CuData ctu, X265PicYuv reconPic, uint cuAddr, int col)
    {
        int ctuWidth = m_param.maxCUSize;
        byte* recY = reconPic.GetPlaneAddr(0, cuAddr) - (ctu.m_bFirstRowInSlice != 0 ? 0 : reconPic.m_stride);
        Buffer.MemoryCopy(recY, m_tmpU[0] + (col * ctuWidth), ctuWidth, ctuWidth);
        if (m_param.internalCsp != X265_CSP_I400)
        {
            ctuWidth >>= m_hChromaShift;
            byte* recU = reconPic.GetPlaneAddr(1, cuAddr) - (ctu.m_bFirstRowInSlice != 0 ? 0 : reconPic.m_strideC);
            byte* recV = reconPic.GetPlaneAddr(2, cuAddr) - (ctu.m_bFirstRowInSlice != 0 ? 0 : reconPic.m_strideC);
            Buffer.MemoryCopy(recU, m_tmpU[1] + (col * ctuWidth), ctuWidth, ctuWidth);
            Buffer.MemoryCopy(recV, m_tmpU[2] + (col * ctuWidth), ctuWidth, ctuWidth);
        }
    }

    // ---- statistics ----

    private void CalcSaoStatsCTU(int addr, int plane)
    {
        X265PicYuv reconPic = m_frame.m_reconPic;
        X265CuData cu = m_frame.GetPicCTU((uint)addr);
        byte* fenc0 = m_frame.m_fencPic.GetPlaneAddr((uint)plane, (uint)addr);
        byte* rec0 = reconPic.GetPlaneAddr((uint)plane, (uint)addr);
        long stride = plane != 0 ? reconPic.m_strideC : reconPic.m_stride;
        uint picWidth = (uint)m_param.sourceWidth;
        uint picHeight = (uint)m_param.sourceHeight;
        int ctuWidth = m_param.maxCUSize;
        int ctuHeight = m_param.maxCUSize;
        uint lpelx = cu.m_cuPelX;
        uint tpely = cu.m_cuPelY;
        uint firstRowInSlice = cu.m_bFirstRowInSlice;
        uint lastRowInSlice = cu.m_bLastRowInSlice;
        bool bAboveUnavail = tpely == 0 || firstRowInSlice != 0;
        if (plane != 0)
        {
            picWidth >>= m_hChromaShift;
            picHeight >>= m_vChromaShift;
            ctuWidth >>= m_hChromaShift;
            ctuHeight >>= m_vChromaShift;
            lpelx >>= m_hChromaShift;
            tpely >>= m_vChromaShift;
        }

        uint rpelx = Math.Min(lpelx + (uint)ctuWidth, picWidth);
        uint bpely = Math.Min(tpely + (uint)ctuHeight, picHeight);
        ctuWidth = (int)(rpelx - lpelx);
        ctuHeight = (int)(bpely - tpely);
        if (lastRowInSlice != 0)
        {
            picHeight = bpely;
        }

        int planeOffset = plane != 0 ? 2 : 0;
        const int skipB = 4;
        const int skipR = 5;
        sbyte* upBuffBase = stackalloc sbyte[2 * (MAX_CU_SIZE + 16 + 16)];
        sbyte* upBuff1 = upBuffBase + 16;
        sbyte* upBufft = upBuff1 + (MAX_CU_SIZE + 16 + 16);
        short* diff = stackalloc short[MAX_CU_SIZE * MAX_CU_SIZE];

        // (fenc - rec) into diff
        for (int y = 0; y < ctuHeight; y++)
        {
            for (int x = 0; x < ctuWidth; x++)
            {
                diff[(y * MAX_CU_SIZE) + x] = (short)(fenc0[(y * stride) + x] - rec0[(y * stride) + x]);
            }
        }

        int* offsetOrg = m_offsetOrg;
        int* count = m_count;

        // SAO_BO
        {
            int endX = rpelx == picWidth ? ctuWidth : ctuWidth - skipR + planeOffset;
            int endY = bpely == picHeight ? ctuHeight : ctuHeight - skipB + planeOffset;
            StatsBO(diff, rec0, stride, endX, endY, offsetOrg + Idx(plane, SAO_BO, 0), count + Idx(plane, SAO_BO, 0));
        }

        // SAO_EO_0: dir -
        {
            int startX = lpelx == 0 ? 1 : 0;
            int endX = rpelx == picWidth ? ctuWidth - 1 : ctuWidth - skipR + planeOffset;
            StatsE0(diff + startX, rec0 + startX, stride, endX - startX, ctuHeight - skipB + planeOffset,
                offsetOrg + Idx(plane, SAO_EO_0, 0), count + Idx(plane, SAO_EO_0, 0));
        }

        // SAO_EO_1: dir |
        {
            byte* rec = rec0;
            int startY = bAboveUnavail ? 1 : 0;
            int endX = rpelx == picWidth ? ctuWidth : ctuWidth - skipR + planeOffset;
            int endY = bpely == picHeight ? ctuHeight - 1 : ctuHeight - skipB + planeOffset;
            if (startY != 0)
            {
                rec += stride;
            }

            for (int x = 0; x < ctuWidth; x++)
            {
                upBuff1[x] = (sbyte)SignOf(rec[x] - rec[x - stride]);
            }

            StatsE1(diff + (startY * MAX_CU_SIZE), rec0 + (startY * stride), stride, upBuff1, endX, endY - startY,
                offsetOrg + Idx(plane, SAO_EO_1, 0), count + Idx(plane, SAO_EO_1, 0));
        }

        // SAO_EO_2: dir 135
        {
            byte* rec = rec0;
            int startX = lpelx == 0 ? 1 : 0;
            int endX = rpelx == picWidth ? ctuWidth - 1 : ctuWidth - skipR + planeOffset;
            int startY = bAboveUnavail ? 1 : 0;
            int endY = bpely == picHeight ? ctuHeight - 1 : ctuHeight - skipB + planeOffset;
            if (startY != 0)
            {
                rec += stride;
            }

            for (int x = 0; x < endX - startX; x++)
            {
                upBuff1[x] = (sbyte)SignOf(rec[startX + x] - rec[startX - stride - 1 + x]);
            }

            StatsE2(diff + startX + (startY * MAX_CU_SIZE), rec0 + startX + (startY * stride), stride, upBuff1, upBufft, endX - startX, endY - startY,
                offsetOrg + Idx(plane, SAO_EO_2, 0), count + Idx(plane, SAO_EO_2, 0));
        }

        // SAO_EO_3: dir 45
        {
            byte* rec = rec0;
            int startX = lpelx == 0 ? 1 : 0;
            int endX = rpelx == picWidth ? ctuWidth - 1 : ctuWidth - skipR + planeOffset;
            int startY = bAboveUnavail ? 1 : 0;
            int endY = bpely == picHeight ? ctuHeight - 1 : ctuHeight - skipB + planeOffset;
            if (startY != 0)
            {
                rec += stride;
            }

            for (int x = 0; x < endX - startX + 1; x++)
            {
                upBuff1[x] = (sbyte)SignOf(rec[startX - 1 + x] - rec[startX - 1 - stride + 1 + x]);
            }

            StatsE3(diff + startX + (startY * MAX_CU_SIZE), rec0 + startX + (startY * stride), stride, upBuff1 + 1, endX - startX, endY - startY,
                offsetOrg + Idx(plane, SAO_EO_3, 0), count + Idx(plane, SAO_EO_3, 0));
        }
    }

    private static int SignOf2(int a, int b)
    {
        int r = 0;
        if (a < b) r = -1;
        if (a > b) r = 1;
        return r;
    }

    private static void StatsBO(short* diff, byte* rec, long stride, int endX, int endY, int* stats, int* count)
    {
        const int boShift = X265_DEPTH - SAO_BO_BITS;
        for (int y = 0; y < endY; y++)
        {
            for (int x = 0; x < endX; x++)
            {
                int classIdx = rec[x] >> boShift;
                stats[classIdx] += diff[x];
                count[classIdx]++;
            }

            diff += MAX_CU_SIZE;
            rec += stride;
        }
    }

    private static void StatsE0(short* diff, byte* rec, long stride, int endX, int endY, int* stats, int* count)
    {
        int* tmpStats = stackalloc int[NUM_EDGETYPE];
        int* tmpCount = stackalloc int[NUM_EDGETYPE];
        for (int i = 0; i < NUM_EDGETYPE; i++) tmpStats[i] = tmpCount[i] = 0;
        for (int y = 0; y < endY; y++)
        {
            int signLeft = SignOf(rec[0] - rec[-1]);
            for (int x = 0; x < endX; x++)
            {
                int signRight = SignOf2(rec[x], rec[x + 1]);
                uint edgeType = (uint)(signRight + signLeft + 2);
                signLeft = -signRight;
                tmpStats[edgeType] += diff[x];
                tmpCount[edgeType]++;
            }

            diff += MAX_CU_SIZE;
            rec += stride;
        }

        for (int x = 0; x < NUM_EDGETYPE; x++)
        {
            stats[s_eoTable[x]] += tmpStats[x];
            count[s_eoTable[x]] += tmpCount[x];
        }
    }

    private static void StatsE1(short* diff, byte* rec, long stride, sbyte* upBuff1, int endX, int endY, int* stats, int* count)
    {
        int* tmpStats = stackalloc int[NUM_EDGETYPE];
        int* tmpCount = stackalloc int[NUM_EDGETYPE];
        for (int i = 0; i < NUM_EDGETYPE; i++) tmpStats[i] = tmpCount[i] = 0;
        for (int y = 0; y < endY; y++)
        {
            for (int x = 0; x < endX; x++)
            {
                int signDown = SignOf2(rec[x], rec[x + stride]);
                uint edgeType = (uint)(signDown + upBuff1[x] + 2);
                upBuff1[x] = (sbyte)-signDown;
                tmpStats[edgeType] += diff[x];
                tmpCount[edgeType]++;
            }

            diff += MAX_CU_SIZE;
            rec += stride;
        }

        for (int x = 0; x < NUM_EDGETYPE; x++)
        {
            stats[s_eoTable[x]] += tmpStats[x];
            count[s_eoTable[x]] += tmpCount[x];
        }
    }

    private static void StatsE2(short* diff, byte* rec, long stride, sbyte* upBuff1, sbyte* upBufft, int endX, int endY, int* stats, int* count)
    {
        int* tmpStats = stackalloc int[NUM_EDGETYPE];
        int* tmpCount = stackalloc int[NUM_EDGETYPE];
        for (int i = 0; i < NUM_EDGETYPE; i++) tmpStats[i] = tmpCount[i] = 0;
        for (int y = 0; y < endY; y++)
        {
            upBufft[0] = (sbyte)SignOf(rec[stride] - rec[-1]);
            for (int x = 0; x < endX; x++)
            {
                int signDown = SignOf2(rec[x], rec[x + stride + 1]);
                uint edgeType = (uint)(signDown + upBuff1[x] + 2);
                upBufft[x + 1] = (sbyte)-signDown;
                tmpStats[edgeType] += diff[x];
                tmpCount[edgeType]++;
            }

            sbyte* t = upBuff1;
            upBuff1 = upBufft;
            upBufft = t;
            rec += stride;
            diff += MAX_CU_SIZE;
        }

        for (int x = 0; x < NUM_EDGETYPE; x++)
        {
            stats[s_eoTable[x]] += tmpStats[x];
            count[s_eoTable[x]] += tmpCount[x];
        }
    }

    private static void StatsE3(short* diff, byte* rec, long stride, sbyte* upBuff1, int endX, int endY, int* stats, int* count)
    {
        int* tmpStats = stackalloc int[NUM_EDGETYPE];
        int* tmpCount = stackalloc int[NUM_EDGETYPE];
        for (int i = 0; i < NUM_EDGETYPE; i++) tmpStats[i] = tmpCount[i] = 0;
        for (int y = 0; y < endY; y++)
        {
            for (int x = 0; x < endX; x++)
            {
                int signDown = SignOf2(rec[x], rec[x + stride - 1]);
                uint edgeType = (uint)(signDown + upBuff1[x] + 2);
                upBuff1[x - 1] = (sbyte)-signDown;
                tmpStats[edgeType] += diff[x];
                tmpCount[edgeType]++;
            }

            upBuff1[endX - 1] = (sbyte)SignOf(rec[endX - 1 + stride] - rec[endX]);
            rec += stride;
            diff += MAX_CU_SIZE;
        }

        for (int x = 0; x < NUM_EDGETYPE; x++)
        {
            stats[s_eoTable[x]] += tmpStats[x];
            count[s_eoTable[x]] += tmpCount[x];
        }
    }

    // ---- RDO ----

    public void RdoSaoUnitCu(X265SaoParam saoParam, int rowBaseAddr, int idxX, int addr)
    {
        X265Slice slice = m_frame.m_slice;
        X265CuData cu = m_frame.GetPicCTU((uint)addr);
        int qp = cu.m_qp[0];
        long* lambda = stackalloc long[2];
        int qpCb = qp + slice.m_pps.chromaQpOffset[0] + slice.m_chromaQpOffset[0];
        qpCb = m_param.internalCsp == X265_CSP_I420
            ? Clip3(m_param.qpMin, m_param.qpMax, X265Tables.ChromaScale[Clip3(QP_MIN, QP_MAX_MAX, qpCb)])
            : Clip3(m_param.qpMin, m_param.qpMax, qpCb);
        lambda[0] = (long)Math.Floor(256.0 * X265Tables.Lambda2Tab[qp]);
        lambda[1] = (long)Math.Floor(256.0 * X265Tables.Lambda2Tab[qpCb]);

        bool allowMerge0 = idxX != 0, allowMerge1 = rowBaseAddr != 0;
        int addrMerge0 = idxX != 0 ? addr - 1 : -1, addrMerge1 = rowBaseAddr != 0 ? addr - m_numCuInWidth : -1;
        bool chroma = m_param.internalCsp != X265_CSP_I400;
        int planes = chroma ? 3 : 1;

        new Span<int>(m_count, PerPlaneSize).Clear();
        new Span<int>(m_offsetOrg, PerPlaneSize).Clear();
        for (int i = 0; i < planes; i++)
        {
            saoParam.ctuParam[i][addr].Reset();
        }

        // SAO distortion calculation
        m_entropyCoder.Load(rdCur);
        m_entropyCoder.ResetBits();
        if (allowMerge0) m_entropyCoder.CodeSaoMerge(0);
        if (allowMerge1) m_entropyCoder.CodeSaoMerge(0);
        m_entropyCoder.Store(rdTemp);
        new Span<int>(m_offset, PerPlaneSize).Clear();
        long bestCost = 0;
        long rateDist = 0;

        if (saoParam.bSaoFlag[0])
        {
            CalcSaoStatsCTU(addr, 0);
            SaoStatsInitialOffset(0);
            SaoLumaComponentParamDist(saoParam, addr, ref rateDist, lambda, ref bestCost);
        }

        if (saoParam.bSaoFlag[1])
        {
            CalcSaoStatsCTU(addr, 1);
            CalcSaoStatsCTU(addr, 2);
            SaoStatsInitialOffset(1);
            SaoChromaComponentParamDist(saoParam, addr, ref rateDist, lambda, ref bestCost);
        }

        if (saoParam.bSaoFlag[0] || saoParam.bSaoFlag[1])
        {
            // cost of merge left or up
            for (int mergeIdx = 0; mergeIdx < 2; ++mergeIdx)
            {
                bool allowMerge = mergeIdx == 0 ? allowMerge0 : allowMerge1;
                int addrMerge = mergeIdx == 0 ? addrMerge0 : addrMerge1;
                if (!allowMerge)
                {
                    continue;
                }

                long mergeDist = 0;
                for (int plane = 0; plane < planes; plane++)
                {
                    long estDist = 0;
                    ref SaoCtuParam mergeSrcParam = ref saoParam.ctuParam[plane][addrMerge];
                    int typeIdx = mergeSrcParam.typeIdx;
                    if (typeIdx >= 0)
                    {
                        int bandPos = typeIdx == SAO_BO ? (int)mergeSrcParam.bandPos : 1;
                        for (int classIdx = 0; classIdx < SAO_NUM_OFFSET; classIdx++)
                        {
                            int mergeOffset = mergeSrcParam.GetOffset(classIdx);
                            estDist += EstSaoDist(m_count[Idx(plane, typeIdx, classIdx + bandPos)], mergeOffset, m_offsetOrg[Idx(plane, typeIdx, classIdx + bandPos)]);
                        }
                    }

                    mergeDist += (estDist << 8) / lambda[plane != 0 ? 1 : 0];
                }

                m_entropyCoder.Load(rdCur);
                m_entropyCoder.ResetBits();
                if (allowMerge0)
                {
                    m_entropyCoder.CodeSaoMerge((uint)(1 - mergeIdx));
                }

                if (allowMerge1 && mergeIdx == 1)
                {
                    m_entropyCoder.CodeSaoMerge(1);
                }

                uint estRate = m_entropyCoder.GetNumberOfWrittenBits();
                long mergeCost = mergeDist + estRate;
                if (mergeCost < bestCost)
                {
                    int mergeMode = mergeIdx != 0 ? SAO_MERGE_UP : SAO_MERGE_LEFT;
                    bestCost = mergeCost;
                    m_entropyCoder.Store(rdTemp);
                    for (int plane = 0; plane < planes; plane++)
                    {
                        if (saoParam.bSaoFlag[plane > 0 ? 1 : 0])
                        {
                            ref SaoCtuParam dst = ref saoParam.ctuParam[plane][addr];
                            SaoCtuParam src = saoParam.ctuParam[plane][addrMerge];
                            dst.mergeMode = mergeMode;
                            dst.typeIdx = src.typeIdx;
                            dst.bandPos = src.bandPos;
                            for (int i = 0; i < SAO_NUM_OFFSET; i++)
                            {
                                dst.SetOffset(i, src.GetOffset(i));
                            }
                        }
                    }
                }
            }

            m_entropyCoder.Load(rdTemp);
            m_entropyCoder.Store(rdCur);
        }
    }

    private void SaoStatsInitialOffset(int planes)
    {
        const int maxSaoType = MAX_NUM_SAO_TYPE - 1;
        // EO
        for (int plane = planes; plane <= planes * 2; plane++)
        {
            for (int typeIdx = 0; typeIdx < maxSaoType; typeIdx++)
            {
                for (int classIdx = 1; classIdx < SAO_NUM_OFFSET + 1; classIdx++)
                {
                    int i = Idx(plane, typeIdx, classIdx);
                    if (m_count[i] != 0)
                    {
                        int offsetOut = RoundIBDI(m_offsetOrg[i], m_count[i] << SAO_BIT_INC);
                        offsetOut = Clip3(-OFFSET_THRESH + 1, OFFSET_THRESH - 1, offsetOut);
                        offsetOut = classIdx < 3 ? Math.Max(offsetOut, 0) : Math.Min(offsetOut, 0);
                        m_offset[i] = offsetOut;
                    }
                }
            }
        }

        // BO
        for (int plane = planes; plane <= planes * 2; plane++)
        {
            for (int classIdx = 0; classIdx < MAX_NUM_SAO_CLASS; classIdx++)
            {
                int i = Idx(plane, SAO_BO, classIdx);
                if (m_count[i] != 0)
                {
                    int offsetOut = RoundIBDI(m_offsetOrg[i], m_count[i] << SAO_BIT_INC);
                    m_offset[i] = Clip3(-OFFSET_THRESH + 1, OFFSET_THRESH - 1, offsetOut);
                }
            }
        }
    }

    private static long CalcSaoRdoCost(long distortion, uint bits, long lambda) => distortion + (((bits * lambda) + 128) >> 8);

    private static void EstIterOffset(int typeIdx, long lambda, int count, int offsetOrg, ref int offset, ref int distClasses, ref long costClasses)
    {
        int bestOffset = 0;
        distClasses = 0;
        long bestCost = CalcSaoRdoCost(0, 1, lambda);
        while (offset != 0)
        {
            uint rate = typeIdx == SAO_BO ? (uint)(Math.Abs(offset) + 2) : (uint)(Math.Abs(offset) + 1);
            if (Math.Abs(offset) == OFFSET_THRESH - 1)
            {
                rate--;
            }

            long dist = EstSaoDist(count, offset << SAO_BIT_INC, offsetOrg);
            long cost = CalcSaoRdoCost(dist, rate, lambda);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestOffset = offset;
                distClasses = (int)dist;
            }

            offset = offset > 0 ? offset - 1 : offset + 1;
        }

        costClasses = bestCost;
        offset = bestOffset;
    }

    private void SaoLumaComponentParamDist(X265SaoParam saoParam, int addr, ref long rateDist, long* lambda, ref long bestCost)
    {
        long bestDist = 0;
        int bestTypeIdx = -1;
        ref SaoCtuParam lcl = ref saoParam.ctuParam[0][addr];
        int* distClasses = stackalloc int[MAX_NUM_SAO_CLASS];
        long* costClasses = stackalloc long[MAX_NUM_SAO_CLASS];

        // RDO SAO_NA
        m_entropyCoder.Load(rdTemp);
        m_entropyCoder.ResetBits();
        m_entropyCoder.EncodeBin(0, m_entropyCoder.m_contextState + OFF_SAO_TYPE_IDX_CTX);   // codeSaoType(0)
        long costPartBest = CalcSaoRdoCost(0, m_entropyCoder.GetNumberOfWrittenBits(), lambda[0]);
        const int maxSaoType = MAX_NUM_SAO_TYPE - 1;

        // EO
        for (int typeIdx = 0; typeIdx < maxSaoType; typeIdx++)
        {
            long estDist = 0;
            for (int classIdx = 1; classIdx < SAO_NUM_OFFSET + 1; classIdx++)
            {
                int i = Idx(0, typeIdx, classIdx);
                EstIterOffset(typeIdx, lambda[0], m_count[i], m_offsetOrg[i], ref m_offset[i], ref distClasses[classIdx], ref costClasses[classIdx]);
                estDist += distClasses[classIdx];
            }

            m_entropyCoder.Load(rdTemp);
            m_entropyCoder.ResetBits();
            m_entropyCoder.CodeSaoOffsetEO(m_offset + Idx(0, typeIdx, 0) + 1, typeIdx, 0);
            long cost = CalcSaoRdoCost(estDist, m_entropyCoder.GetNumberOfWrittenBits(), lambda[0]);
            if (cost < costPartBest)
            {
                costPartBest = cost;
                bestDist = estDist;
                bestTypeIdx = typeIdx;
            }
        }

        if (bestTypeIdx != -1)
        {
            lcl.mergeMode = SAO_MERGE_NONE;
            lcl.typeIdx = bestTypeIdx;
            lcl.bandPos = 0;
            for (int classIdx = 0; classIdx < SAO_NUM_OFFSET; classIdx++)
            {
                lcl.SetOffset(classIdx, m_offset[Idx(0, bestTypeIdx, classIdx + 1)]);
            }
        }

        // BO
        for (int classIdx = 0; classIdx < MAX_NUM_SAO_CLASS; classIdx++)
        {
            int i = Idx(0, SAO_BO, classIdx);
            EstIterOffset(SAO_BO, lambda[0], m_count[i], m_offsetOrg[i], ref m_offset[i], ref distClasses[classIdx], ref costClasses[classIdx]);
        }

        // best band position
        int bestClassBO = 0;
        long currentRDCost = costClasses[0] + costClasses[1] + costClasses[2] + costClasses[3];
        long bestRDCostBO = currentRDCost;
        for (int i = 1; i < MAX_NUM_SAO_CLASS - SAO_NUM_OFFSET + 1; i++)
        {
            currentRDCost -= costClasses[i - 1];
            currentRDCost += costClasses[i + 3];
            if (currentRDCost < bestRDCostBO)
            {
                bestRDCostBO = currentRDCost;
                bestClassBO = i;
            }
        }

        long estDistBO = 0;
        for (int classIdx = bestClassBO; classIdx < bestClassBO + SAO_NUM_OFFSET; classIdx++)
        {
            estDistBO += distClasses[classIdx];
        }

        m_entropyCoder.Load(rdTemp);
        m_entropyCoder.ResetBits();
        m_entropyCoder.CodeSaoOffsetBO(m_offset + Idx(0, SAO_BO, 0) + bestClassBO, bestClassBO, 0);
        long costBO = CalcSaoRdoCost(estDistBO, m_entropyCoder.GetNumberOfWrittenBits(), lambda[0]);
        if (costBO < costPartBest)
        {
            costPartBest = costBO;
            bestDist = estDistBO;
            lcl.mergeMode = SAO_MERGE_NONE;
            lcl.typeIdx = SAO_BO;
            lcl.bandPos = (uint)bestClassBO;
            for (int classIdx = 0; classIdx < SAO_NUM_OFFSET; classIdx++)
            {
                lcl.SetOffset(classIdx, m_offset[Idx(0, SAO_BO, classIdx + bestClassBO)]);
            }
        }

        rateDist = (bestDist << 8) / lambda[0];
        m_entropyCoder.Load(rdTemp);
        m_entropyCoder.CodeSaoOffset(lcl, 0);
        m_entropyCoder.Store(rdTemp);
        if (m_param.internalCsp == X265_CSP_I400)
        {
            bestCost = rateDist + m_entropyCoder.GetNumberOfWrittenBits();
        }
    }

    private void SaoChromaComponentParamDist(X265SaoParam saoParam, int addr, ref long rateDist, long* lambda, ref long bestCost)
    {
        long bestDist = 0;
        int bestTypeIdx = -1;
        long* costClasses = stackalloc long[MAX_NUM_SAO_CLASS];
        int* distClasses = stackalloc int[MAX_NUM_SAO_CLASS];
        int* bestClassBO = stackalloc int[2];
        bestClassBO[0] = bestClassBO[1] = 0;

        m_entropyCoder.Load(rdTemp);
        m_entropyCoder.ResetBits();
        m_entropyCoder.EncodeBin(0, m_entropyCoder.m_contextState + OFF_SAO_TYPE_IDX_CTX);   // codeSaoType(0)
        uint bits = m_entropyCoder.GetNumberOfWrittenBits();
        long costPartBest = CalcSaoRdoCost(0, bits, lambda[1]);
        const int maxSaoType = MAX_NUM_SAO_TYPE - 1;

        // EO
        for (int typeIdx = 0; typeIdx < maxSaoType; typeIdx++)
        {
            long estDist0 = 0, estDist1 = 0;
            for (int compIdx = 1; compIdx < 3; compIdx++)
            {
                for (int classIdx = 1; classIdx < SAO_NUM_OFFSET + 1; classIdx++)
                {
                    int i = Idx(compIdx, typeIdx, classIdx);
                    EstIterOffset(typeIdx, lambda[1], m_count[i], m_offsetOrg[i], ref m_offset[i], ref distClasses[classIdx], ref costClasses[classIdx]);
                    if (compIdx == 1) estDist0 += distClasses[classIdx];
                    else estDist1 += distClasses[classIdx];
                }
            }

            m_entropyCoder.Load(rdTemp);
            m_entropyCoder.ResetBits();
            for (int compIdx = 0; compIdx < 2; compIdx++)
            {
                m_entropyCoder.CodeSaoOffsetEO(m_offset + Idx(compIdx + 1, typeIdx, 0) + 1, typeIdx, compIdx + 1);
            }

            uint estRate = m_entropyCoder.GetNumberOfWrittenBits();
            long cost = CalcSaoRdoCost(estDist0 + estDist1, estRate, lambda[1]);
            if (cost < costPartBest)
            {
                costPartBest = cost;
                bestDist = estDist0 + estDist1;
                bestTypeIdx = typeIdx;
            }
        }

        if (bestTypeIdx != -1)
        {
            for (int compIdx = 0; compIdx < 2; compIdx++)
            {
                ref SaoCtuParam lcl = ref saoParam.ctuParam[compIdx + 1][addr];
                lcl.mergeMode = SAO_MERGE_NONE;
                lcl.typeIdx = bestTypeIdx;
                lcl.bandPos = 0;
                for (int classIdx = 0; classIdx < SAO_NUM_OFFSET; classIdx++)
                {
                    lcl.SetOffset(classIdx, m_offset[Idx(compIdx + 1, bestTypeIdx, classIdx + 1)]);
                }
            }
        }

        // BO: best position per component
        long* estDist = stackalloc long[2];
        for (int compIdx = 1; compIdx < 3; compIdx++)
        {
            long bestRDCostBO = long.MaxValue;
            for (int classIdx = 0; classIdx < MAX_NUM_SAO_CLASS; classIdx++)
            {
                int i = Idx(compIdx, SAO_BO, classIdx);
                EstIterOffset(SAO_BO, lambda[1], m_count[i], m_offsetOrg[i], ref m_offset[i], ref distClasses[classIdx], ref costClasses[classIdx]);
            }

            for (int i = 0; i < MAX_NUM_SAO_CLASS - SAO_NUM_OFFSET + 1; i++)
            {
                long currentRDCost = 0;
                for (int j = i; j < i + SAO_NUM_OFFSET; j++)
                {
                    currentRDCost += costClasses[j];
                }

                if (currentRDCost < bestRDCostBO)
                {
                    bestRDCostBO = currentRDCost;
                    bestClassBO[compIdx - 1] = i;
                }
            }

            estDist[compIdx - 1] = 0;
            for (int classIdx = bestClassBO[compIdx - 1]; classIdx < bestClassBO[compIdx - 1] + SAO_NUM_OFFSET; classIdx++)
            {
                estDist[compIdx - 1] += distClasses[classIdx];
            }
        }

        m_entropyCoder.Load(rdTemp);
        m_entropyCoder.ResetBits();
        for (int compIdx = 0; compIdx < 2; compIdx++)
        {
            m_entropyCoder.CodeSaoOffsetBO(m_offset + Idx(compIdx + 1, SAO_BO, 0) + bestClassBO[compIdx], bestClassBO[compIdx], compIdx + 1);
        }

        uint estRateBO = m_entropyCoder.GetNumberOfWrittenBits();
        long costBO = CalcSaoRdoCost(estDist[0] + estDist[1], estRateBO, lambda[1]);
        if (costBO < costPartBest)
        {
            costPartBest = costBO;
            bestDist = estDist[0] + estDist[1];
            for (int compIdx = 0; compIdx < 2; compIdx++)
            {
                ref SaoCtuParam lcl = ref saoParam.ctuParam[compIdx + 1][addr];
                lcl.mergeMode = SAO_MERGE_NONE;
                lcl.typeIdx = SAO_BO;
                lcl.bandPos = (uint)bestClassBO[compIdx];
                for (int classIdx = 0; classIdx < SAO_NUM_OFFSET; classIdx++)
                {
                    lcl.SetOffset(classIdx, m_offset[Idx(compIdx + 1, SAO_BO, classIdx + bestClassBO[compIdx])]);
                }
            }
        }

        rateDist += (bestDist << 8) / lambda[1];
        m_entropyCoder.Load(rdTemp);
        if (saoParam.bSaoFlag[1])
        {
            m_entropyCoder.CodeSaoOffset(saoParam.ctuParam[1][addr], 1);
            m_entropyCoder.CodeSaoOffset(saoParam.ctuParam[2][addr], 2);
            m_entropyCoder.Store(rdTemp);
            uint rate = m_entropyCoder.GetNumberOfWrittenBits();
            bestCost = rateDist + rate;
        }
        else
        {
            uint rate = m_entropyCoder.GetNumberOfWrittenBits();
            bestCost = rateDist + rate;
        }
    }
}
