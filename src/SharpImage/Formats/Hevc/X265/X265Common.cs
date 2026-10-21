// Port of x265 4.x common definitions (common/common.h, constants.h, cudata.h CUGeom) used by the HEVC still-image
// encoder port: the 8-bit (X265_DEPTH 8) build, 4:2:0 / 4:0:0. Pointers mirror x265's so the search code reads like
// the C++; every buffer lives in an X265Arena (native memory freed with the encoder).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Hevc.X265;

internal static class X265Const
{
    public const int X265_DEPTH = 8;
    public const int QP_BD_OFFSET = 0;
    public const int PIXEL_MAX = 255;
    public const int QP_MIN = 0;
    public const int QP_MAX_SPEC = 51;
    public const int QP_MAX_MAX = 69;
    public const int MAX_CHROMA_LAMBDA_OFFSET = 36;

    public const int NUM_CU_DEPTH = 4;
    public const int NUM_FULL_DEPTH = 5;
    public const int MIN_LOG2_CU_SIZE = 3;
    public const int MAX_LOG2_CU_SIZE = 6;
    public const int MAX_CU_SIZE = 1 << MAX_LOG2_CU_SIZE;
    public const int LOG2_UNIT_SIZE = 2;
    public const int UNIT_SIZE = 1 << LOG2_UNIT_SIZE;
    public const int LOG2_RASTER_SIZE = MAX_LOG2_CU_SIZE - LOG2_UNIT_SIZE;
    public const int RASTER_SIZE = 1 << LOG2_RASTER_SIZE;
    public const int MAX_NUM_PARTITIONS = RASTER_SIZE * RASTER_SIZE;
    public const int MAX_LOG2_TR_SIZE = 5;
    public const int MAX_TR_SIZE = 1 << MAX_LOG2_TR_SIZE;
    public const int MAX_LOG2_TS_SIZE = 2;
    public const int MAX_TS_SIZE = 1 << MAX_LOG2_TS_SIZE;

    public const int COEF_REMAIN_BIN_REDUCTION = 3;
    public const int SBH_THRESHOLD = 4;
    public const int C1FLAG_NUMBER = 8;
    public const int C2FLAG_NUMBER = 1;
    public const int MLS_GRP_NUM = 64;
    public const int MLS_CG_SIZE = 4;          // used both as 4x4 group side and log2 of its coefficient count
    public const int MLS_CG_BLK_SIZE = MLS_CG_SIZE * MLS_CG_SIZE;
    public const int MLS_CG_LOG2_SIZE = 2;
    public const int QUANT_IQUANT_SHIFT = 20;
    public const int QUANT_SHIFT = 14;
    public const int SCALE_BITS = 15;
    public const int MAX_TR_DYNAMIC_RANGE = 15;
    public const int SCAN_SET_SIZE = 16;
    public const int LOG2_SCAN_SET_SIZE = 4;

    public const int ALL_IDX = -1;
    public const int PLANAR_IDX = 0;
    public const int VER_IDX = 26;
    public const int HOR_IDX = 10;
    public const int DC_IDX = 1;
    public const int NUM_CHROMA_MODE = 5;
    public const int DM_CHROMA_IDX = 36;
    public const int MDCS_LOG2_MAX_SIZE = 3;
    public const int MAX_RD_INTRA_MODES = 16;
    public const int NUM_INTRA_MODE = 35;

    public const int CU_DQP_TU_CMAX = 5;
    public const int CU_DQP_EG_k = 0;

    public const int SAO_NUM_OFFSET = 4;
    public const int SAO_EO_LEN = 4;
    public const int SAO_BO_LEN = 4;
    public const int SAO_NUM_BO_CLASSES = 32;
    public const int SAO_BIT_INC = 0;
    public const int MAX_NUM_SAO_TYPE = 5;   // SAO_EO_0..3, SAO_BO

    // chroma formats (x265_param.internalCsp)
    public const int X265_CSP_I400 = 0;
    public const int X265_CSP_I420 = 1;
    public const int X265_CSP_I422 = 2;
    public const int X265_CSP_I444 = 3;

    // TextType
    public const int TEXT_LUMA = 0;
    public const int TEXT_CHROMA_U = 1;
    public const int TEXT_CHROMA_V = 2;

    // ScanType
    public const int SCAN_DIAG = 0;
    public const int SCAN_HOR = 1;
    public const int SCAN_VER = 2;

    // PartSize
    public const int SIZE_2Nx2N = 0;
    public const int SIZE_NxN = 3;

    // PredMode
    public const int MODE_NONE = 0;
    public const int MODE_INTER = 1;
    public const int MODE_INTRA = 2;

    // SliceType
    public const int B_SLICE = 0;
    public const int P_SLICE = 1;
    public const int I_SLICE = 2;

    // context model counts and offsets (common/contexts.h)
    public const int NUM_SPLIT_FLAG_CTX = 3, NUM_SKIP_FLAG_CTX = 3, NUM_MERGE_FLAG_EXT_CTX = 1, NUM_MERGE_IDX_EXT_CTX = 1,
        NUM_PART_SIZE_CTX = 4, NUM_PRED_MODE_CTX = 1, NUM_ADI_CTX = 1, NUM_CHROMA_PRED_CTX = 2, NUM_INTER_DIR_CTX = 5,
        NUM_MV_RES_CTX = 2, NUM_REF_NO_CTX = 2, NUM_TRANS_SUBDIV_FLAG_CTX = 3, NUM_QT_CBF_CTX = 7, NUM_QT_ROOT_CBF_CTX = 1,
        NUM_DELTA_QP_CTX = 3, NUM_SIG_CG_FLAG_CTX = 2, NUM_SIG_FLAG_CTX = 42, NUM_SIG_FLAG_CTX_LUMA = 27,
        NUM_SIG_FLAG_CTX_CHROMA = 15, NUM_CTX_LAST_FLAG_XY = 18, NUM_CTX_LAST_FLAG_XY_LUMA = 15,
        NUM_CTX_LAST_FLAG_XY_CHROMA = 3, NUM_ONE_FLAG_CTX = 24, NUM_ONE_FLAG_CTX_LUMA = 16, NUM_ONE_FLAG_CTX_CHROMA = 8,
        NUM_ABS_FLAG_CTX = 6, NUM_ABS_FLAG_CTX_LUMA = 4, NUM_ABS_FLAG_CTX_CHROMA = 2, NUM_MVP_IDX_CTX = 1,
        NUM_SAO_MERGE_FLAG_CTX = 1, NUM_SAO_TYPE_IDX_CTX = 1, NUM_TRANSFORMSKIP_FLAG_CTX = 1, NUM_TQUANT_BYPASS_FLAG_CTX = 1;

    public const int OFF_SPLIT_FLAG_CTX = 0;
    public const int OFF_SKIP_FLAG_CTX = OFF_SPLIT_FLAG_CTX + NUM_SPLIT_FLAG_CTX;
    public const int OFF_MERGE_FLAG_EXT_CTX = OFF_SKIP_FLAG_CTX + NUM_SKIP_FLAG_CTX;
    public const int OFF_MERGE_IDX_EXT_CTX = OFF_MERGE_FLAG_EXT_CTX + NUM_MERGE_FLAG_EXT_CTX;
    public const int OFF_PART_SIZE_CTX = OFF_MERGE_IDX_EXT_CTX + NUM_MERGE_IDX_EXT_CTX;
    public const int OFF_PRED_MODE_CTX = OFF_PART_SIZE_CTX + NUM_PART_SIZE_CTX;
    public const int OFF_ADI_CTX = OFF_PRED_MODE_CTX + NUM_PRED_MODE_CTX;
    public const int OFF_CHROMA_PRED_CTX = OFF_ADI_CTX + NUM_ADI_CTX;
    public const int OFF_DELTA_QP_CTX = OFF_CHROMA_PRED_CTX + NUM_CHROMA_PRED_CTX;
    public const int OFF_INTER_DIR_CTX = OFF_DELTA_QP_CTX + NUM_DELTA_QP_CTX;
    public const int OFF_REF_NO_CTX = OFF_INTER_DIR_CTX + NUM_INTER_DIR_CTX;
    public const int OFF_MV_RES_CTX = OFF_REF_NO_CTX + NUM_REF_NO_CTX;
    public const int OFF_QT_CBF_CTX = OFF_MV_RES_CTX + NUM_MV_RES_CTX;
    public const int OFF_TRANS_SUBDIV_FLAG_CTX = OFF_QT_CBF_CTX + NUM_QT_CBF_CTX;
    public const int OFF_QT_ROOT_CBF_CTX = OFF_TRANS_SUBDIV_FLAG_CTX + NUM_TRANS_SUBDIV_FLAG_CTX;
    public const int OFF_SIG_CG_FLAG_CTX = OFF_QT_ROOT_CBF_CTX + NUM_QT_ROOT_CBF_CTX;
    public const int OFF_SIG_FLAG_CTX = OFF_SIG_CG_FLAG_CTX + 2 * NUM_SIG_CG_FLAG_CTX;
    public const int OFF_CTX_LAST_FLAG_X = OFF_SIG_FLAG_CTX + NUM_SIG_FLAG_CTX;
    public const int OFF_CTX_LAST_FLAG_Y = OFF_CTX_LAST_FLAG_X + NUM_CTX_LAST_FLAG_XY;
    public const int OFF_ONE_FLAG_CTX = OFF_CTX_LAST_FLAG_Y + NUM_CTX_LAST_FLAG_XY;
    public const int OFF_ABS_FLAG_CTX = OFF_ONE_FLAG_CTX + NUM_ONE_FLAG_CTX;
    public const int OFF_MVP_IDX_CTX = OFF_ABS_FLAG_CTX + NUM_ABS_FLAG_CTX;
    public const int OFF_SAO_MERGE_FLAG_CTX = OFF_MVP_IDX_CTX + NUM_MVP_IDX_CTX;
    public const int OFF_SAO_TYPE_IDX_CTX = OFF_SAO_MERGE_FLAG_CTX + NUM_SAO_MERGE_FLAG_CTX;
    public const int OFF_TRANSFORMSKIP_FLAG_CTX = OFF_SAO_TYPE_IDX_CTX + NUM_SAO_TYPE_IDX_CTX;
    public const int OFF_TQUANT_BYPASS_FLAG_CTX = OFF_TRANSFORMSKIP_FLAG_CTX + 2 * NUM_TRANSFORMSKIP_FLAG_CTX;
    public const int MAX_OFF_CTX_MOD = OFF_TQUANT_BYPASS_FLAG_CTX + NUM_TQUANT_BYPASS_FLAG_CTX;

    /// <summary>ctxCbf[ttype][tuDepth] (contexts.h).</summary>
    public static readonly uint[] CtxCbf = { 1, 0, 0, 0, 0, 2, 3, 4, 5, 6, 2, 3, 4, 5, 6 };

    public const ulong MAX_INT64 = 0x7FFFFFFFFFFFFFFFUL;
    public const int MAX_INT = int.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clip3(int lo, int hi, int v) => v < lo ? lo : v > hi ? hi : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clip3(double lo, double hi, double v) => v < lo ? lo : v > hi ? hi : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClipPixel(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>BSR: index of the highest set bit (x != 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bsr(uint x) => 31 - BitOperations.LeadingZeroCount(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bsf(uint x) => BitOperations.TrailingZeroCount(x);
}

/// <summary>struct CUGeom (cudata.h): the static geometry of one CU position within a CTU.</summary>
internal struct CUGeom
{
    public const uint INTRA = 1 << 0, PRESENT = 1 << 1, SPLIT_MANDATORY = 1 << 2, LEAF = 1 << 3, SPLIT = 1 << 4;
    public const int MAX_GEOMS = 85;

    public uint log2CUSize;
    public uint childOffset;
    public uint absPartIdx;
    public uint numPartitions;
    public uint flags;
    public uint depth;
    public uint geomRecurId;

    /// <summary>CUData::calcCTUGeoms.</summary>
    public static unsafe void CalcCTUGeoms(uint ctuWidth, uint ctuHeight, uint maxCUSize, uint minCUSize, CUGeom* cuDataArray)
    {
        int maxLog2 = X265Tables.Log2Size[maxCUSize], minLog2 = X265Tables.Log2Size[minCUSize];
        uint num4x4Partition = 1u << ((maxLog2 - X265Const.LOG2_UNIT_SIZE) << 1);
        uint rangeCUIdx = 0;
        for (int log2CUSize = maxLog2; log2CUSize >= minLog2; log2CUSize--)
        {
            uint blockSize = 1u << log2CUSize;
            uint sbWidth = 1u << (maxLog2 - log2CUSize);
            bool lastLevelFlag = log2CUSize == minLog2;
            for (uint sbY = 0; sbY < sbWidth; sbY++)
            {
                for (uint sbX = 0; sbX < sbWidth; sbX++)
                {
                    uint depthIdx = (uint)X265Tables.DepthScanIdx[(sbY * 8) + sbX];
                    uint cuIdx = rangeCUIdx + depthIdx;
                    uint childIdx = rangeCUIdx + (sbWidth * sbWidth) + (depthIdx << 2);
                    uint px = sbX * blockSize;
                    uint py = sbY * blockSize;
                    bool presentFlag = px < ctuWidth && py < ctuHeight;
                    bool splitMandatoryFlag = presentFlag && !lastLevelFlag && (px + blockSize > ctuWidth || py + blockSize > ctuHeight);
                    uint xOffset = (sbX * blockSize) >> 3;
                    uint yOffset = (sbY * blockSize) >> 3;

                    CUGeom* cu = cuDataArray + cuIdx;
                    cu->log2CUSize = (uint)log2CUSize;
                    cu->childOffset = childIdx - cuIdx;
                    cu->absPartIdx = (uint)X265Tables.DepthScanIdx[(yOffset * 8) + xOffset] * 4;
                    cu->numPartitions = num4x4Partition >> ((maxLog2 - log2CUSize) * 2);
                    cu->depth = (uint)(maxLog2 - log2CUSize);
                    cu->geomRecurId = cuIdx;
                    cu->flags = 0;
                    if (presentFlag) cu->flags |= PRESENT;
                    if (splitMandatoryFlag) cu->flags |= SPLIT_MANDATORY | SPLIT;
                    if (lastLevelFlag) cu->flags |= LEAF;
                }
            }

            rangeCUIdx += sbWidth * sbWidth;
        }
    }
}

/// <summary>Native allocations for one encode, all released together (x265's X265_MALLOC / X265_FREE).</summary>
internal sealed unsafe class X265Arena : IDisposable
{
    private readonly List<nint> blocks = new();

    /// <summary>Zeroed, 64-byte aligned storage for <paramref name="count"/> elements.</summary>
    public T* Alloc<T>(long count) where T : unmanaged
    {
        nuint bytes = (nuint)Math.Max(1, count * sizeof(T));
        void* p = NativeMemory.AlignedAlloc(bytes, 64);
        NativeMemory.Clear(p, bytes);
        blocks.Add((nint)p);
        return (T*)p;
    }

    public void Dispose()
    {
        foreach (nint p in blocks)
        {
            NativeMemory.AlignedFree((void*)p);
        }

        blocks.Clear();
    }
}
