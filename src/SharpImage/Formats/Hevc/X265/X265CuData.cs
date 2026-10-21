// Port of x265 common/cudata.{h,cpp}: per-4x4-part CU data (intra subset: no motion fields), its init / copy
// operations, neighbour derivation, MPM, QP prediction and the TU entropy-coding parameters. The per-part arrays
// keep x265's contiguous order, which initCTU / initSubCU memsets rely on.
using System;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;
#if HIGH_BIT_DEPTH
using sse_t = ulong;
#else
using sse_t = uint;
#endif

/// <summary>struct TUEntropyCodingParameters.</summary>
internal unsafe struct TUEntropyCodingParameters
{
    public ushort* scan;
    public ushort* scanCG;
    public int scanType;
    public uint firstSignificanceMapContext;
}

/// <summary>The scan tables as pinned native arrays (g_scanOrder / g_scanOrderCG / g_scan4x4).</summary>
internal static unsafe class X265Scan
{
    public static readonly ushort*[] ScanOrder = new ushort*[3 * 4];
    public static readonly ushort*[] ScanOrderCG = new ushort*[3 * 4];
    public static readonly ushort*[] Scan4x4 = new ushort*[4];
    public static readonly ushort* Scan8x8Diag;

    static X265Scan()
    {
        static ushort* Pin(ushort[] a, int offset, int count)
        {
            ushort* p = (ushort*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(count * 2 + 32), 32);
            for (int i = 0; i < count; i++) p[i] = a[offset + i];
            for (int i = count; i < count + 16; i++) p[i] = 0;
            return p;
        }

        for (int t = 0; t < 4; t++) Scan4x4[t] = Pin(X265Tables.Scan4x4, t * 16, 16);
        ushort* scan16 = Pin(X265Tables.Scan16x16, 0, 256);
        ushort* scan32 = Pin(X265Tables.Scan32x32, 0, 1024);
        Scan8x8Diag = Pin(X265Tables.Scan8x8Diag, 0, 64);
        ushort*[] scan8 = { Pin(X265Tables.Scan8x8, 0, 64), Pin(X265Tables.Scan8x8, 64, 64), Pin(X265Tables.Scan8x8, 128, 64) };
        ushort*[] scan2 = { Pin(X265Tables.Scan2x2, 0, 4), Pin(X265Tables.Scan2x2, 4, 4) };
        for (int t = 0; t < 3; t++)
        {
            ScanOrder[(t * 4) + 0] = Scan4x4[t];
            ScanOrder[(t * 4) + 1] = scan8[t];
            ScanOrder[(t * 4) + 2] = scan16;
            ScanOrder[(t * 4) + 3] = scan32;
            ScanOrderCG[(t * 4) + 0] = Scan4x4[t];
            ScanOrderCG[(t * 4) + 1] = scan2[t == 1 ? 1 : 0];
            ScanOrderCG[(t * 4) + 2] = Scan4x4[0];
            ScanOrderCG[(t * 4) + 3] = Scan8x8Diag;
        }
    }
}

/// <summary>class CUData.</summary>
internal sealed unsafe class X265CuData
{
    public const int BytesPerPartition = 24;

    public X265FrameData m_encData = null!;
    public X265Slice m_slice = null!;
    public uint m_cuAddr;
    public uint m_absIdxInCTU;
    public uint m_cuPelX, m_cuPelY;
    public uint m_numPartitions;
    public int m_chromaFormat;
    public int m_hChromaShift, m_vChromaShift;
    public byte m_bFirstRowInSlice, m_bLastRowInSlice, m_bLastCuInSlice;

    public sbyte* m_qp;
    public sbyte* m_qpAnalysis;
    public byte* m_log2CUSize;
    public byte* m_lumaIntraDir;
    public byte* m_tqBypass;
    public readonly sbyte*[] m_refIdx = new sbyte*[2];
    public byte* m_cuDepth;
    public byte* m_predMode;
    public byte* m_partSize;
    public readonly byte*[] m_skipFlag = new byte*[2];
    public byte* m_mergeFlag;
    public byte* m_interDir;
    public readonly byte*[] m_mvpIdx = new byte*[2];
    public byte* m_tuDepth;
    public readonly byte*[] m_transformSkip = new byte*[3];
    public readonly byte*[] m_cbf = new byte*[3];
    public byte* m_chromaIntraDir;
    public sse_t* m_distortion;
    public readonly short*[] m_trCoeff = new short*[3];
    public readonly sbyte[] m_refTuDepth = new sbyte[21];

    public X265CuData? m_cuAboveLeft, m_cuAboveRight, m_cuAbove, m_cuLeft;
    public double m_meanQP;

    // x265's globals g_unitSizeDepth (as parts per CTU side), per encoder instead of process-wide
    private uint s_numPartInCUSize;

    /// <summary>CUData::initialize: this CU's arrays in its own block of the given depth's size.</summary>
    public void Initialize(X265Arena arena, uint depth, int csp, uint num4x4Partitions, uint maxCUSize, uint unitSizeDepth, int maxLog2CUSize)
    {
        m_chromaFormat = csp;
        m_hChromaShift = m_vChromaShift = csp == X265_CSP_I420 ? 1 : 0;
        m_numPartitions = num4x4Partitions >> (int)(depth * 2);
        s_numPartInCUSize = 1u << (int)unitSizeDepth;
        uint n = m_numPartitions;
        bool i400 = csp == X265_CSP_I400;
        byte* charBuf = arena.Alloc<byte>(n * (i400 ? BytesPerPartition - 4 : BytesPerPartition));
        m_qp = (sbyte*)charBuf; charBuf += n;
        m_qpAnalysis = (sbyte*)charBuf; charBuf += n;
        m_log2CUSize = charBuf; charBuf += n;
        m_lumaIntraDir = charBuf; charBuf += n;
        m_tqBypass = charBuf; charBuf += n;
        m_refIdx[0] = (sbyte*)charBuf; charBuf += n;
        m_refIdx[1] = (sbyte*)charBuf; charBuf += n;
        m_cuDepth = charBuf; charBuf += n;
        m_predMode = charBuf; charBuf += n;
        m_partSize = charBuf; charBuf += n;
        m_skipFlag[0] = charBuf; charBuf += n;
        m_skipFlag[1] = charBuf; charBuf += n;
        m_mergeFlag = charBuf; charBuf += n;
        m_interDir = charBuf; charBuf += n;
        m_mvpIdx[0] = charBuf; charBuf += n;
        m_mvpIdx[1] = charBuf; charBuf += n;
        m_tuDepth = charBuf; charBuf += n;
        m_transformSkip[0] = charBuf; charBuf += n;
        if (!i400)
        {
            m_transformSkip[1] = charBuf; charBuf += n;
            m_transformSkip[2] = charBuf; charBuf += n;
        }

        m_cbf[0] = charBuf; charBuf += n;
        if (!i400)
        {
            m_cbf[1] = charBuf; charBuf += n;
            m_cbf[2] = charBuf; charBuf += n;
        }

        m_chromaIntraDir = charBuf;
        m_distortion = arena.Alloc<sse_t>(n);
        uint cuSize = maxCUSize >> (int)depth;
        uint sizeL = cuSize * cuSize;
        if (i400)
        {
            m_trCoeff[0] = arena.Alloc<short>(sizeL);
        }
        else
        {
            uint sizeC = sizeL >> (m_hChromaShift + m_vChromaShift);
            m_trCoeff[0] = arena.Alloc<short>(sizeL + (sizeC * 2));
            m_trCoeff[1] = m_trCoeff[0] + sizeL;
            m_trCoeff[2] = m_trCoeff[0] + sizeL + sizeC;
        }
    }

    private static void PartSet(byte* dst, byte val, uint n) => new Span<byte>(dst, (int)n).Fill(val);

    private static void PartCopy(byte* dst, byte* src, uint n) => Buffer.MemoryCopy(src, dst, n, n);

    /// <summary>s_partSet[depth]: number of parts covered by a CU at an absolute depth.</summary>
    private uint PartsAtDepth(uint depth) => (s_numPartInCUSize * s_numPartInCUSize) >> (int)(depth * 2);

    public void InitCTU(X265FrameData encData, uint cuAddr, int qp, uint firstRowInSlice, uint lastRowInSlice, uint lastCuInSlice)
    {
        m_encData = encData;
        m_slice = encData.m_slice;
        m_cuAddr = cuAddr;
        m_cuPelX = (cuAddr % m_slice.m_sps.numCuInWidth) << m_slice.m_param.maxLog2CUSize;
        m_cuPelY = (cuAddr / m_slice.m_sps.numCuInWidth) << m_slice.m_param.maxLog2CUSize;
        m_absIdxInCTU = 0;
        m_numPartitions = (uint)encData.m_param.num4x4Partitions;
        m_bFirstRowInSlice = (byte)firstRowInSlice;
        m_bLastRowInSlice = (byte)lastRowInSlice;
        m_bLastCuInSlice = (byte)lastCuInSlice;

        uint n = m_numPartitions;
        PartSet((byte*)m_qp, (byte)qp, n);
        PartSet((byte*)m_qpAnalysis, (byte)qp, n);
        PartSet(m_log2CUSize, (byte)m_slice.m_param.maxLog2CUSize, n);
        PartSet(m_lumaIntraDir, unchecked((byte)ALL_IDX), n);
        PartSet(m_chromaIntraDir, unchecked((byte)ALL_IDX), n);
        PartSet(m_tqBypass, (byte)(encData.m_param.bLossless ? 1 : 0), n);

        // the remaining CU data in one memset (m_cuDepth .. the last cbf array)
        new Span<byte>(m_cuDepth, (int)((m_chromaFormat == X265_CSP_I400 ? BytesPerPartition - 12 : BytesPerPartition - 8) * n)).Clear();
        for (int i = 0; i < m_refTuDepth.Length; i++) m_refTuDepth[i] = -1;

        uint widthInCU = m_slice.m_sps.numCuInWidth;
        m_cuLeft = (m_cuAddr % widthInCU) != 0 ? encData.GetPicCTU(m_cuAddr - 1) : null;
        m_cuAbove = (m_cuAddr >= widthInCU) && m_bFirstRowInSlice == 0 ? encData.GetPicCTU(m_cuAddr - widthInCU) : null;
        m_cuAboveLeft = (m_cuLeft != null && m_cuAbove != null) ? encData.GetPicCTU(m_cuAddr - widthInCU - 1) : null;
        m_cuAboveRight = (m_cuAbove != null && ((m_cuAddr % widthInCU) < (widthInCU - 1))) ? encData.GetPicCTU(m_cuAddr - widthInCU + 1) : null;
        new Span<sse_t>(m_distortion, (int)n).Clear();
    }

    public void InitSubCU(X265CuData ctu, in CUGeom cuGeom, int qp)
    {
        m_absIdxInCTU = cuGeom.absPartIdx;
        m_encData = ctu.m_encData;
        m_slice = ctu.m_slice;
        m_cuAddr = ctu.m_cuAddr;
        m_cuPelX = ctu.m_cuPelX + X265Tables.ZscanToPelX[cuGeom.absPartIdx];
        m_cuPelY = ctu.m_cuPelY + X265Tables.ZscanToPelY[cuGeom.absPartIdx];
        m_cuLeft = ctu.m_cuLeft;
        m_cuAbove = ctu.m_cuAbove;
        m_cuAboveLeft = ctu.m_cuAboveLeft;
        m_cuAboveRight = ctu.m_cuAboveRight;
        m_bFirstRowInSlice = ctu.m_bFirstRowInSlice;
        m_bLastRowInSlice = ctu.m_bLastRowInSlice;
        m_bLastCuInSlice = ctu.m_bLastCuInSlice;

        uint n = m_numPartitions;
        PartSet((byte*)m_qp, (byte)qp, n);
        PartSet((byte*)m_qpAnalysis, (byte)qp, n);
        PartSet(m_log2CUSize, (byte)cuGeom.log2CUSize, n);
        PartSet(m_lumaIntraDir, unchecked((byte)ALL_IDX), n);
        PartSet(m_chromaIntraDir, unchecked((byte)ALL_IDX), n);
        PartSet(m_tqBypass, (byte)(m_encData.m_param.bLossless ? 1 : 0), n);
        PartSet((byte*)m_refIdx[0], unchecked((byte)-1), n);
        PartSet((byte*)m_refIdx[1], unchecked((byte)-1), n);
        PartSet(m_cuDepth, (byte)cuGeom.depth, n);

        // the remaining CU data in one memset (m_predMode .. the last cbf array)
        new Span<byte>(m_predMode, (int)((ctu.m_chromaFormat == X265_CSP_I400 ? BytesPerPartition - 13 : BytesPerPartition - 9) * n)).Clear();
        new Span<sse_t>(m_distortion, (int)n).Clear();
    }

    /// <summary>Copy the results of a sub-part (split) CU to the parent CU.</summary>
    public void CopyPartFrom(X265CuData subCU, in CUGeom childGeom, uint subPartIdx)
    {
        uint offset = childGeom.numPartitions * subPartIdx;
        uint q = m_numPartitions >> 2;
        m_bFirstRowInSlice = subCU.m_bFirstRowInSlice;
        m_bLastCuInSlice = subCU.m_bLastCuInSlice;

        PartCopy((byte*)m_qp + offset, (byte*)subCU.m_qp, q);
        PartCopy((byte*)m_qpAnalysis + offset, (byte*)subCU.m_qpAnalysis, q);
        PartCopy(m_log2CUSize + offset, subCU.m_log2CUSize, q);
        PartCopy(m_lumaIntraDir + offset, subCU.m_lumaIntraDir, q);
        PartCopy(m_tqBypass + offset, subCU.m_tqBypass, q);
        PartCopy((byte*)m_refIdx[0] + offset, (byte*)subCU.m_refIdx[0], q);
        PartCopy((byte*)m_refIdx[1] + offset, (byte*)subCU.m_refIdx[1], q);
        PartCopy(m_cuDepth + offset, subCU.m_cuDepth, q);
        PartCopy(m_predMode + offset, subCU.m_predMode, q);
        PartCopy(m_partSize + offset, subCU.m_partSize, q);
        PartCopy(m_mergeFlag + offset, subCU.m_mergeFlag, q);
        PartCopy(m_interDir + offset, subCU.m_interDir, q);
        PartCopy(m_mvpIdx[0] + offset, subCU.m_mvpIdx[0], q);
        PartCopy(m_mvpIdx[1] + offset, subCU.m_mvpIdx[1], q);
        PartCopy(m_tuDepth + offset, subCU.m_tuDepth, q);
        PartCopy(m_transformSkip[0] + offset, subCU.m_transformSkip[0], q);
        PartCopy(m_cbf[0] + offset, subCU.m_cbf[0], q);
        Buffer.MemoryCopy(subCU.m_distortion, m_distortion + offset, childGeom.numPartitions * sizeof(sse_t), childGeom.numPartitions * sizeof(sse_t));

        uint tmp = 1u << (int)((m_slice.m_param.maxLog2CUSize - childGeom.depth) * 2);
        uint tmp2 = subPartIdx * tmp;
        Buffer.MemoryCopy(subCU.m_trCoeff[0], m_trCoeff[0] + tmp2, tmp * 2, tmp * 2);

        if (subCU.m_chromaFormat != X265_CSP_I400)
        {
            PartCopy(m_transformSkip[1] + offset, subCU.m_transformSkip[1], q);
            PartCopy(m_transformSkip[2] + offset, subCU.m_transformSkip[2], q);
            PartCopy(m_cbf[1] + offset, subCU.m_cbf[1], q);
            PartCopy(m_cbf[2] + offset, subCU.m_cbf[2], q);
            PartCopy(m_chromaIntraDir + offset, subCU.m_chromaIntraDir, q);
            uint tmpC = tmp >> (m_hChromaShift + m_vChromaShift);
            uint tmpC2 = tmp2 >> (m_hChromaShift + m_vChromaShift);
            Buffer.MemoryCopy(subCU.m_trCoeff[1], m_trCoeff[1] + tmpC2, tmpC * 2, tmpC * 2);
            Buffer.MemoryCopy(subCU.m_trCoeff[2], m_trCoeff[2] + tmpC2, tmpC * 2, tmpC * 2);
        }
    }

    /// <summary>A sub-CU off the picture edge still needs its depth and log2 size.</summary>
    public void SetEmptyPart(in CUGeom childGeom, uint subPartIdx)
    {
        uint offset = childGeom.numPartitions * subPartIdx;
        uint q = m_numPartitions >> 2;
        PartSet(m_cuDepth + offset, (byte)childGeom.depth, q);
        PartSet(m_log2CUSize + offset, (byte)childGeom.log2CUSize, q);
    }

    /// <summary>Copy the completed CU into the picture's CTU.</summary>
    public void CopyToPic(uint depth)
    {
        X265CuData ctu = m_encData.GetPicCTU(m_cuAddr);
        uint n = m_numPartitions;
        uint a = m_absIdxInCTU;
        PartCopy((byte*)ctu.m_qp + a, (byte*)m_qp, n);
        PartCopy((byte*)ctu.m_qpAnalysis + a, (byte*)m_qpAnalysis, n);
        PartCopy(ctu.m_log2CUSize + a, m_log2CUSize, n);
        PartCopy(ctu.m_lumaIntraDir + a, m_lumaIntraDir, n);
        PartCopy(ctu.m_tqBypass + a, m_tqBypass, n);
        PartCopy((byte*)ctu.m_refIdx[0] + a, (byte*)m_refIdx[0], n);
        PartCopy((byte*)ctu.m_refIdx[1] + a, (byte*)m_refIdx[1], n);
        PartCopy(ctu.m_cuDepth + a, m_cuDepth, n);
        PartCopy(ctu.m_predMode + a, m_predMode, n);
        PartCopy(ctu.m_partSize + a, m_partSize, n);
        PartCopy(ctu.m_mergeFlag + a, m_mergeFlag, n);
        PartCopy(ctu.m_interDir + a, m_interDir, n);
        PartCopy(ctu.m_mvpIdx[0] + a, m_mvpIdx[0], n);
        PartCopy(ctu.m_mvpIdx[1] + a, m_mvpIdx[1], n);
        PartCopy(ctu.m_tuDepth + a, m_tuDepth, n);
        PartCopy(ctu.m_transformSkip[0] + a, m_transformSkip[0], n);
        PartCopy(ctu.m_cbf[0] + a, m_cbf[0], n);
        Buffer.MemoryCopy(m_distortion, ctu.m_distortion + a, n * sizeof(sse_t), n * sizeof(sse_t));

        uint tmpY = 1u << (int)((m_slice.m_param.maxLog2CUSize - depth) * 2);
        uint tmpY2 = a << (LOG2_UNIT_SIZE * 2);
        Buffer.MemoryCopy(m_trCoeff[0], ctu.m_trCoeff[0] + tmpY2, tmpY * 2, tmpY * 2);
        if (ctu.m_chromaFormat != X265_CSP_I400)
        {
            PartCopy(ctu.m_transformSkip[1] + a, m_transformSkip[1], n);
            PartCopy(ctu.m_transformSkip[2] + a, m_transformSkip[2], n);
            PartCopy(ctu.m_cbf[1] + a, m_cbf[1], n);
            PartCopy(ctu.m_cbf[2] + a, m_cbf[2], n);
            PartCopy(ctu.m_chromaIntraDir + a, m_chromaIntraDir, n);
            uint tmpC = tmpY >> (m_hChromaShift + m_vChromaShift);
            uint tmpC2 = tmpY2 >> (m_hChromaShift + m_vChromaShift);
            Buffer.MemoryCopy(m_trCoeff[1], ctu.m_trCoeff[1] + tmpC2, tmpC * 2, tmpC * 2);
            Buffer.MemoryCopy(m_trCoeff[2], ctu.m_trCoeff[2] + tmpC2, tmpC * 2, tmpC * 2);
        }
    }

    public void SetPartSizeSubParts(int size) => PartSet(m_partSize, (byte)size, m_numPartitions);

    public void SetPredModeSubParts(int mode) => PartSet(m_predMode, (byte)mode, m_numPartitions);

    public void SetQPSubParts(int qp, uint absPartIdx, uint depth) => PartSet((byte*)m_qp + absPartIdx, (byte)qp, PartsAtDepth(depth));

    public void SetTUDepthSubParts(uint tuDepth, uint absPartIdx, uint depth) => PartSet(m_tuDepth + absPartIdx, (byte)tuDepth, PartsAtDepth(depth));

    public void SetLumaIntraDirSubParts(uint dir, uint absPartIdx, uint depth) => PartSet(m_lumaIntraDir + absPartIdx, (byte)dir, PartsAtDepth(depth));

    public void SetChromIntraDirSubParts(uint dir, uint absPartIdx, uint depth) => PartSet(m_chromaIntraDir + absPartIdx, (byte)dir, PartsAtDepth(depth));

    public void SetCbfSubParts(uint cbf, int ttype, uint absPartIdx, uint depth) => PartSet(m_cbf[ttype] + absPartIdx, (byte)cbf, PartsAtDepth(depth));

    public void SetCbfPartRange(uint cbf, int ttype, uint absPartIdx, uint coveredPartIdxes) => PartSet(m_cbf[ttype] + absPartIdx, (byte)cbf, coveredPartIdxes);

    public void SetTransformSkipSubParts(uint tskip, int ttype, uint absPartIdx, uint depth) => PartSet(m_transformSkip[ttype] + absPartIdx, (byte)tskip, PartsAtDepth(depth));

    public void SetTransformSkipPartRange(uint tskip, int ttype, uint absPartIdx, uint coveredPartIdxes) => PartSet(m_transformSkip[ttype] + absPartIdx, (byte)tskip, coveredPartIdxes);

    public uint GetCbf(uint absPartIdx, int ttype, uint tuDepth) => (uint)((m_cbf[ttype][absPartIdx] >> (int)tuDepth) & 0x1);

    public bool GetQtRootCbf(uint absPartIdx) =>
        m_cbf[0][absPartIdx] != 0 || (m_chromaFormat != X265_CSP_I400 && (m_cbf[1][absPartIdx] != 0 || m_cbf[2][absPartIdx] != 0));

    public bool IsIntra(uint absPartIdx) => m_predMode[absPartIdx] == MODE_INTRA;

    public uint GetSCUAddr() => (m_cuAddr << (int)(m_slice.m_param.unitSizeDepth * 2)) + m_absIdxInCTU;

    // ---- raster helpers ----
    private static bool IsEqualCol(int addrA, int addrB) => ((addrA ^ addrB) & (RASTER_SIZE - 1)) == 0;

    private static bool IsEqualRow(int addrA, int addrB) => (addrA ^ addrB) < RASTER_SIZE;

    private static bool IsEqualRowOrCol(int addrA, int addrB) => IsEqualCol(addrA, addrB) || IsEqualRow(addrA, addrB);

    private static bool IsZeroCol(int addr) => (addr & (RASTER_SIZE - 1)) == 0;

    private static bool IsZeroRow(int addr) => addr < RASTER_SIZE;

    private static bool LessThanCol(int addr, int val) => (addr & (RASTER_SIZE - 1)) < val;

    private static bool LessThanRow(int addr, int val) => (addr >> LOG2_RASTER_SIZE) < val;

    private static int Z2R(uint z) => X265Tables.ZscanToRaster[z];

    private static uint R2Z(int r) => (uint)X265Tables.RasterToZscan[r];

    public X265CuData? GetPULeft(out uint lPartUnitIdx, uint curPartUnitIdx)
    {
        int absPartIdx = Z2R(curPartUnitIdx);
        if (!IsZeroCol(absPartIdx))
        {
            int absZorderCUIdx = Z2R(m_absIdxInCTU);
            lPartUnitIdx = R2Z(absPartIdx - 1);
            if (IsEqualCol(absPartIdx, absZorderCUIdx))
            {
                return m_encData.GetPicCTU(m_cuAddr);
            }

            lPartUnitIdx -= m_absIdxInCTU;
            return this;
        }

        lPartUnitIdx = R2Z(absPartIdx + (int)s_numPartInCUSize - 1);
        return m_cuLeft;
    }

    public X265CuData? GetPUAbove(out uint aPartUnitIdx, uint curPartUnitIdx)
    {
        int absPartIdx = Z2R(curPartUnitIdx);
        if (!IsZeroRow(absPartIdx))
        {
            int absZorderCUIdx = Z2R(m_absIdxInCTU);
            aPartUnitIdx = R2Z(absPartIdx - RASTER_SIZE);
            if (IsEqualRow(absPartIdx, absZorderCUIdx))
            {
                return m_encData.GetPicCTU(m_cuAddr);
            }

            aPartUnitIdx -= m_absIdxInCTU;
            return this;
        }

        aPartUnitIdx = R2Z(absPartIdx + (((int)s_numPartInCUSize - 1) << LOG2_RASTER_SIZE));
        return m_cuAbove;
    }

    public X265CuData? GetPUAboveLeft(out uint alPartUnitIdx, uint curPartUnitIdx)
    {
        int absPartIdx = Z2R(curPartUnitIdx);
        if (!IsZeroCol(absPartIdx))
        {
            if (!IsZeroRow(absPartIdx))
            {
                int absZorderCUIdx = Z2R(m_absIdxInCTU);
                alPartUnitIdx = R2Z(absPartIdx - RASTER_SIZE - 1);
                if (IsEqualRowOrCol(absPartIdx, absZorderCUIdx))
                {
                    return m_encData.GetPicCTU(m_cuAddr);
                }

                alPartUnitIdx -= m_absIdxInCTU;
                return this;
            }

            alPartUnitIdx = R2Z(absPartIdx + (((int)s_numPartInCUSize - 1) << LOG2_RASTER_SIZE) - 1);
            return m_cuAbove;
        }

        if (!IsZeroRow(absPartIdx))
        {
            alPartUnitIdx = R2Z(absPartIdx - RASTER_SIZE + (int)s_numPartInCUSize - 1);
            return m_cuLeft;
        }

        alPartUnitIdx = (uint)m_encData.m_param.num4x4Partitions - 1;
        return m_cuAboveLeft;
    }

    public X265CuData? GetPUBelowLeftAdi(out uint blPartUnitIdx, uint curPartUnitIdx, uint partUnitOffset)
    {
        blPartUnitIdx = 0;
        if ((m_encData.GetPicCTU(m_cuAddr).m_cuPelY + X265Tables.ZscanToPelY[curPartUnitIdx] + (partUnitOffset << LOG2_UNIT_SIZE)) >= m_slice.m_sps.picHeightInLumaSamples)
        {
            return null;
        }

        int absPartIdxLB = Z2R(curPartUnitIdx);
        if (LessThanRow(absPartIdxLB, (int)(s_numPartInCUSize - partUnitOffset)))
        {
            if (!IsZeroCol(absPartIdxLB))
            {
                if (curPartUnitIdx > R2Z(absPartIdxLB + ((int)partUnitOffset << LOG2_RASTER_SIZE) - 1))
                {
                    int absZorderCUIdxLB = Z2R(m_absIdxInCTU) + (((1 << (m_log2CUSize[0] - LOG2_UNIT_SIZE)) - 1) << LOG2_RASTER_SIZE);
                    blPartUnitIdx = R2Z(absPartIdxLB + ((int)partUnitOffset << LOG2_RASTER_SIZE) - 1);
                    if (IsEqualRowOrCol(absPartIdxLB, absZorderCUIdxLB))
                    {
                        return m_encData.GetPicCTU(m_cuAddr);
                    }

                    blPartUnitIdx -= m_absIdxInCTU;
                    return this;
                }

                return null;
            }

            blPartUnitIdx = R2Z(absPartIdxLB + ((int)partUnitOffset << LOG2_RASTER_SIZE) + (int)s_numPartInCUSize - 1);
            return m_cuLeft;
        }

        return null;
    }

    public X265CuData? GetPUAboveRightAdi(out uint arPartUnitIdx, uint curPartUnitIdx, uint partUnitOffset)
    {
        arPartUnitIdx = 0;
        if ((m_encData.GetPicCTU(m_cuAddr).m_cuPelX + X265Tables.ZscanToPelX[curPartUnitIdx] + (partUnitOffset << LOG2_UNIT_SIZE)) >= m_slice.m_sps.picWidthInLumaSamples)
        {
            return null;
        }

        int absPartIdxRT = Z2R(curPartUnitIdx);
        if (LessThanCol(absPartIdxRT, (int)(s_numPartInCUSize - partUnitOffset)))
        {
            if (!IsZeroRow(absPartIdxRT))
            {
                if (curPartUnitIdx > R2Z(absPartIdxRT - RASTER_SIZE + (int)partUnitOffset))
                {
                    int absZorderCUIdx = Z2R(m_absIdxInCTU) + (1 << (m_log2CUSize[0] - LOG2_UNIT_SIZE)) - 1;
                    arPartUnitIdx = R2Z(absPartIdxRT - RASTER_SIZE + (int)partUnitOffset);
                    if (IsEqualRowOrCol(absPartIdxRT, absZorderCUIdx))
                    {
                        return m_encData.GetPicCTU(m_cuAddr);
                    }

                    arPartUnitIdx -= m_absIdxInCTU;
                    return this;
                }

                return null;
            }

            arPartUnitIdx = R2Z(absPartIdxRT + (((int)s_numPartInCUSize - 1) << LOG2_RASTER_SIZE) + (int)partUnitOffset);
            return m_cuAbove;
        }

        if (!IsZeroRow(absPartIdxRT))
        {
            return null;
        }

        arPartUnitIdx = R2Z((((int)s_numPartInCUSize - 1) << LOG2_RASTER_SIZE) + (int)partUnitOffset - 1);
        return m_cuAboveRight;
    }

    public X265CuData? GetQpMinCuLeft(out uint lPartUnitIdx, uint curAbsIdxInCTU)
    {
        lPartUnitIdx = 0;
        uint absZorderQpMinCUIdx = curAbsIdxInCTU & (0xFFu << (int)((m_encData.m_param.unitSizeDepth - m_slice.m_pps.maxCuDQPDepth) * 2));
        int absRorderQpMinCUIdx = Z2R(absZorderQpMinCUIdx);
        if (IsZeroCol(absRorderQpMinCUIdx))
        {
            return null;
        }

        lPartUnitIdx = R2Z(absRorderQpMinCUIdx - 1);
        return m_encData.GetPicCTU(m_cuAddr);
    }

    public X265CuData? GetQpMinCuAbove(out uint aPartUnitIdx, uint curAbsIdxInCTU)
    {
        aPartUnitIdx = 0;
        uint absZorderQpMinCUIdx = curAbsIdxInCTU & (0xFFu << (int)((m_encData.m_param.unitSizeDepth - m_slice.m_pps.maxCuDQPDepth) * 2));
        int absRorderQpMinCUIdx = Z2R(absZorderQpMinCUIdx);
        if (IsZeroRow(absRorderQpMinCUIdx))
        {
            return null;
        }

        aPartUnitIdx = R2Z(absRorderQpMinCUIdx - RASTER_SIZE);
        return m_encData.GetPicCTU(m_cuAddr);
    }

    /// <summary>The reference QP: from the left / above QG of this CTU, else the last coded QP.</summary>
    public int GetRefQP(uint curAbsIdxInCTU)
    {
        X265CuData? cULeft = GetQpMinCuLeft(out uint lPartIdx, m_absIdxInCTU + curAbsIdxInCTU);
        X265CuData? cUAbove = GetQpMinCuAbove(out uint aPartIdx, m_absIdxInCTU + curAbsIdxInCTU);
        return (sbyte)(((cULeft != null ? cULeft.m_qp[lPartIdx] : GetLastCodedQP(curAbsIdxInCTU)) +
                        (cUAbove != null ? cUAbove.m_qp[aPartIdx] : GetLastCodedQP(curAbsIdxInCTU)) + 1) >> 1);
    }

    private int GetLastValidPartIdx(int absPartIdx)
    {
        int lastValidPartIdx = absPartIdx - 1;
        while (lastValidPartIdx >= 0 && m_predMode[lastValidPartIdx] == MODE_NONE)
        {
            uint depth = m_cuDepth[lastValidPartIdx];
            lastValidPartIdx -= (int)(m_numPartitions >> (int)(depth << 1));
        }

        return lastValidPartIdx;
    }

    public int GetLastCodedQP(uint absPartIdx)
    {
        uint quPartIdxMask = 0xFFu << (int)((m_encData.m_param.unitSizeDepth - m_slice.m_pps.maxCuDQPDepth) * 2);
        int lastValidPartIdx = GetLastValidPartIdx((int)(absPartIdx & quPartIdxMask));
        if (lastValidPartIdx >= 0)
        {
            return m_qp[lastValidPartIdx];
        }

        if (m_absIdxInCTU != 0)
        {
            return m_encData.GetPicCTU(m_cuAddr).GetLastCodedQP(m_absIdxInCTU);
        }

        if (m_cuAddr > 0 && !(m_slice.m_pps.bEntropyCodingSyncEnabled && (m_cuAddr % m_slice.m_sps.numCuInWidth) == 0))
        {
            return m_encData.GetPicCTU(m_cuAddr - 1).GetLastCodedQP((uint)m_encData.m_param.num4x4Partitions);
        }

        return (sbyte)m_slice.m_sliceQp;
    }

    /// <summary>Allowed chroma intra modes (mode 4 = DM).</summary>
    public void GetAllowedChromaDir(uint absPartIdx, uint* modeList)
    {
        modeList[0] = PLANAR_IDX;
        modeList[1] = VER_IDX;
        modeList[2] = HOR_IDX;
        modeList[3] = DC_IDX;
        modeList[4] = DM_CHROMA_IDX;
        uint lumaMode = m_lumaIntraDir[absPartIdx];
        for (int i = 0; i < NUM_CHROMA_MODE - 1; i++)
        {
            if (lumaMode == modeList[i])
            {
                modeList[i] = 34;
                break;
            }
        }
    }

    /// <summary>The three most probable luma modes; returns 1 (left == above) or 2.</summary>
    public int GetIntraDirLumaPredictor(uint absPartIdx, uint* intraDirPred)
    {
        X265CuData? tempCU = GetPULeft(out uint tempPartIdx, m_absIdxInCTU + absPartIdx);
        uint leftIntraDir = (tempCU != null && tempCU.IsIntra(tempPartIdx)) ? tempCU.m_lumaIntraDir[tempPartIdx] : (uint)DC_IDX;
        tempCU = X265Tables.ZscanToPelY[m_absIdxInCTU + absPartIdx] > 0 ? GetPUAbove(out tempPartIdx, m_absIdxInCTU + absPartIdx) : null;
        uint aboveIntraDir = (tempCU != null && tempCU.IsIntra(tempPartIdx)) ? tempCU.m_lumaIntraDir[tempPartIdx] : (uint)DC_IDX;

        if (leftIntraDir == aboveIntraDir)
        {
            if (leftIntraDir >= 2)
            {
                intraDirPred[0] = leftIntraDir;
                intraDirPred[1] = ((leftIntraDir - 2 + 31) & 31) + 2;
                intraDirPred[2] = ((leftIntraDir - 2 + 1) & 31) + 2;
            }
            else
            {
                intraDirPred[0] = PLANAR_IDX;
                intraDirPred[1] = DC_IDX;
                intraDirPred[2] = VER_IDX;
            }

            return 1;
        }

        intraDirPred[0] = leftIntraDir;
        intraDirPred[1] = aboveIntraDir;
        if (leftIntraDir != 0 && aboveIntraDir != 0)
        {
            intraDirPred[2] = PLANAR_IDX;
        }
        else
        {
            intraDirPred[2] = (leftIntraDir + aboveIntraDir) < 2 ? (uint)VER_IDX : DC_IDX;
        }

        return 2;
    }

    public uint GetCtxSplitFlag(uint absPartIdx, uint depth)
    {
        X265CuData? tempCU = GetPULeft(out uint tempPartIdx, m_absIdxInCTU + absPartIdx);
        uint ctx = tempCU != null ? (tempCU.m_cuDepth[tempPartIdx] > depth ? 1u : 0u) : 0u;
        tempCU = GetPUAbove(out tempPartIdx, m_absIdxInCTU + absPartIdx);
        ctx += tempCU != null ? (tempCU.m_cuDepth[tempPartIdx] > depth ? 1u : 0u) : 0u;
        return ctx;
    }

    public void GetIntraTUQtDepthRange(uint* tuDepthRange, uint absPartIdx)
    {
        uint log2CUSize = m_log2CUSize[absPartIdx];
        uint splitFlag = m_partSize[absPartIdx] != SIZE_2Nx2N ? 1u : 0u;
        tuDepthRange[0] = m_slice.m_sps.quadtreeTULog2MinSize;
        tuDepthRange[1] = m_slice.m_sps.quadtreeTULog2MaxSize;
        tuDepthRange[0] = (uint)Clip3((int)tuDepthRange[0], (int)tuDepthRange[1], (int)log2CUSize - (int)(m_slice.m_sps.quadtreeTUMaxDepthIntra - 1 + splitFlag));
    }

    /// <summary>Set the QP of the zero-cbf sub-CUs until the first coded one.</summary>
    public bool SetQPSubCUs(int qp, uint absPartIdx, uint depth)
    {
        uint curPartNumb = (uint)m_encData.m_param.num4x4Partitions >> (int)(depth << 1);
        uint curPartNumQ = curPartNumb >> 2;
        if (m_cuDepth[absPartIdx] > depth)
        {
            for (uint subPartIdx = 0; subPartIdx < 4; subPartIdx++)
            {
                if (SetQPSubCUs(qp, absPartIdx + (subPartIdx * curPartNumQ), depth + 1))
                {
                    return true;
                }
            }
        }
        else
        {
            if (GetQtRootCbf(absPartIdx))
            {
                return true;
            }

            SetQPSubParts(qp, absPartIdx, depth);
        }

        return false;
    }

    public void GetTUEntropyCodingParameters(ref TUEntropyCodingParameters result, uint absPartIdx, uint log2TrSize, bool bIsLuma)
    {
        bool bIsIntra = IsIntra(absPartIdx);
        uint log2TrSizeCG = log2TrSize - 2;
        if (bIsIntra)
        {
            uint dirMode;
            if (bIsLuma)
            {
                dirMode = m_lumaIntraDir[absPartIdx];
            }
            else
            {
                dirMode = m_chromaIntraDir[absPartIdx];
                if (dirMode == DM_CHROMA_IDX)
                {
                    dirMode = m_lumaIntraDir[m_chromaFormat == X265_CSP_I444 ? absPartIdx : absPartIdx & 0xFC];
                }
            }

            if (log2TrSize <= (uint)(MDCS_LOG2_MAX_SIZE - m_hChromaShift) || (bIsLuma && log2TrSize == MDCS_LOG2_MAX_SIZE))
            {
                result.scanType = dirMode >= 22 && dirMode <= 30 ? SCAN_HOR : dirMode >= 6 && dirMode <= 14 ? SCAN_VER : SCAN_DIAG;
            }
            else
            {
                result.scanType = SCAN_DIAG;
            }
        }
        else
        {
            result.scanType = SCAN_DIAG;
        }

        result.scan = X265Scan.ScanOrder[(result.scanType * 4) + (int)log2TrSize - 2];
        result.scanCG = X265Scan.ScanOrderCG[(result.scanType * 4) + (int)log2TrSizeCG];
        if (log2TrSize == 2)
        {
            result.firstSignificanceMapContext = 0;
        }
        else if (log2TrSize == 3)
        {
            result.firstSignificanceMapContext = (result.scanType != SCAN_DIAG && bIsLuma) ? 15u : 9u;
        }
        else
        {
            result.firstSignificanceMapContext = bIsLuma ? 21u : 12u;
        }
    }
}
