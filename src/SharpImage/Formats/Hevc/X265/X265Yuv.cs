// Port of x265 common/picyuv.{h,cpp} (the parts a single intra picture uses), common/yuv.{h,cpp} and
// common/shortyuv.{h,cpp}: picture planes with x265's margins and CTU / 4x4-unit offset tables, and the CU-sized
// pixel and residual buffers the analysis works in. 8-bit pixels; 4:2:0 or 4:0:0.
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

/// <summary>class PicYuv.</summary>
internal sealed unsafe class X265PicYuv
{
    public readonly pixel*[] m_picOrg = new pixel*[3];
    public int m_picWidth, m_picHeight;
    public int m_stride, m_strideC;
    public int m_hChromaShift, m_vChromaShift;
    public int m_picCsp;
    public int m_lumaMarginX, m_lumaMarginY, m_chromaMarginX, m_chromaMarginY;
    public long* m_cuOffsetY, m_cuOffsetC, m_buOffsetY, m_buOffsetC;

    /// <summary>PicYuv::create (sourceWidth / sourceHeight are the multiple-of-8 coded size).</summary>
    public X265PicYuv(X265Arena arena, int picWidth, int picHeight, int csp, int maxCUSize)
    {
        m_picWidth = picWidth;
        m_picHeight = picHeight;
        m_picCsp = csp;
        m_hChromaShift = csp == X265_CSP_I420 ? 1 : 0;
        m_vChromaShift = csp == X265_CSP_I420 ? 1 : 0;
        int numCuInWidth = (picWidth + maxCUSize - 1) / maxCUSize;
        int numCuInHeight = (picHeight + maxCUSize - 1) / maxCUSize;
        m_lumaMarginX = maxCUSize + 32;
        m_lumaMarginY = maxCUSize + 16;
        m_stride = (numCuInWidth * maxCUSize) + (m_lumaMarginX << 1);
        int maxHeight = numCuInHeight * maxCUSize;
        pixel* buf0 = arena.Alloc<pixel>((long)m_stride * (maxHeight + (m_lumaMarginY * 2)));
        m_picOrg[0] = buf0 + (m_lumaMarginY * m_stride) + m_lumaMarginX;
        if (csp != X265_CSP_I400)
        {
            m_chromaMarginX = m_lumaMarginX;
            m_chromaMarginY = m_lumaMarginY >> m_vChromaShift;
            m_strideC = ((numCuInWidth * maxCUSize) >> m_hChromaShift) + (m_chromaMarginX * 2);
            long sizeC = (long)m_strideC * ((maxHeight >> m_vChromaShift) + (m_chromaMarginY * 2));
            pixel* buf1 = arena.Alloc<pixel>(sizeC);
            pixel* buf2 = arena.Alloc<pixel>(sizeC);
            m_picOrg[1] = buf1 + (m_chromaMarginY * m_strideC) + m_chromaMarginX;
            m_picOrg[2] = buf2 + (m_chromaMarginY * m_strideC) + m_chromaMarginX;
        }
    }

    /// <summary>PicYuv::createOffsets.</summary>
    public void CreateOffsets(X265Arena arena, int numCuInWidth, int numCuInHeight, int maxCUSize, int unitSizeDepth)
    {
        int numPartitions = 1 << (unitSizeDepth * 2);
        m_cuOffsetY = arena.Alloc<long>(numCuInWidth * numCuInHeight);
        m_buOffsetY = arena.Alloc<long>(numPartitions);
        if (m_picCsp != X265_CSP_I400)
        {
            m_cuOffsetC = arena.Alloc<long>(numCuInWidth * numCuInHeight);
            m_buOffsetC = arena.Alloc<long>(numPartitions);
        }

        for (int cuRow = 0; cuRow < numCuInHeight; cuRow++)
        {
            for (int cuCol = 0; cuCol < numCuInWidth; cuCol++)
            {
                m_cuOffsetY[(cuRow * numCuInWidth) + cuCol] = ((long)m_stride * cuRow * maxCUSize) + (cuCol * maxCUSize);
                if (m_picCsp != X265_CSP_I400)
                {
                    m_cuOffsetC[(cuRow * numCuInWidth) + cuCol] = ((long)m_strideC * cuRow * (maxCUSize >> m_vChromaShift)) + (cuCol * (maxCUSize >> m_hChromaShift));
                }
            }
        }

        for (int idx = 0; idx < numPartitions; ++idx)
        {
            long x = X265Tables.ZscanToPelX[idx];
            long y = X265Tables.ZscanToPelY[idx];
            m_buOffsetY[idx] = (m_stride * y) + x;
            if (m_picCsp != X265_CSP_I400)
            {
                m_buOffsetC[idx] = (m_strideC * (y >> m_vChromaShift)) + (x >> m_hChromaShift);
            }
        }
    }

    /// <summary>The same offset tables as another picture of the same geometry.</summary>
    public void ShareOffsets(X265PicYuv other)
    {
        m_cuOffsetY = other.m_cuOffsetY;
        m_cuOffsetC = other.m_cuOffsetC;
        m_buOffsetY = other.m_buOffsetY;
        m_buOffsetC = other.m_buOffsetC;
    }

    public pixel* GetLumaAddr(uint ctuAddr) => m_picOrg[0] + m_cuOffsetY[ctuAddr];

    public pixel* GetLumaAddr(uint ctuAddr, uint absPartIdx) => m_picOrg[0] + m_cuOffsetY[ctuAddr] + m_buOffsetY[absPartIdx];

    public pixel* GetCbAddr(uint ctuAddr, uint absPartIdx) => m_picOrg[1] + m_cuOffsetC[ctuAddr] + m_buOffsetC[absPartIdx];

    public pixel* GetCrAddr(uint ctuAddr, uint absPartIdx) => m_picOrg[2] + m_cuOffsetC[ctuAddr] + m_buOffsetC[absPartIdx];

    public pixel* GetChromaAddr(uint chromaId, uint ctuAddr, uint absPartIdx) => m_picOrg[chromaId] + m_cuOffsetC[ctuAddr] + m_buOffsetC[absPartIdx];

    public pixel* GetChromaAddr(uint chromaId, uint ctuAddr) => m_picOrg[chromaId] + m_cuOffsetC[ctuAddr];

    public pixel* GetPlaneAddr(uint plane, uint ctuAddr) => m_picOrg[plane] + (plane != 0 ? m_cuOffsetC[ctuAddr] : m_cuOffsetY[ctuAddr]);

    /// <summary>PicYuv::copyFromPicture for planes at the build's depth: the visible <paramref name="width"/> x <paramref name="height"/>
    /// samples, then the right / bottom edges replicated to a multiple of 16 plus one (the lowres downscale and AQ
    /// read them; the coded area is only padded to the minimum CU size).</summary>
    public void CopyFromPicture(ReadOnlySpan<pixel> y, ReadOnlySpan<pixel> u, ReadOnlySpan<pixel> v, int srcStride, int srcStrideC,
        int padx, int pady)
    {
        int width = m_picWidth - padx;
        int height = m_picHeight - pady;
        int rem = width & 15;
        padx = rem != 0 ? 16 - rem : padx;
        rem = height & 15;
        pady = rem != 0 ? 16 - rem : pady;
        padx++;
        pady++;

        pixel* Y = m_picOrg[0];
        for (int r = 0; r < height; r++)
        {
            y.Slice(r * srcStride, width).CopyTo(new Span<pixel>(Y + ((long)r * m_stride), width));
        }

        if (m_picCsp != X265_CSP_I400)
        {
            int cwid = width >> m_hChromaShift;
            for (int r = 0; r < height >> m_vChromaShift; r++)
            {
                u.Slice(r * srcStrideC, cwid).CopyTo(new Span<pixel>(m_picOrg[1] + ((long)r * m_strideC), cwid));
                v.Slice(r * srcStrideC, cwid).CopyTo(new Span<pixel>(m_picOrg[2] + ((long)r * m_strideC), cwid));
            }
        }

        // extend the right edge, then the bottom
        for (int r = 0; r < height; r++)
        {
            for (int x = 0; x < padx; x++)
            {
                Y[width + x] = Y[width - 1];
            }

            Y += m_stride;
        }

        Y = m_picOrg[0] + ((long)(height - 1) * m_stride);
        for (int i = 1; i <= pady; i++)
        {
            Buffer.MemoryCopy(Y, Y + ((long)i * m_stride), (width + padx) * sizeof(pixel), (width + padx) * sizeof(pixel));
        }

        if (m_picCsp != X265_CSP_I400)
        {
            pixel* U = m_picOrg[1];
            pixel* V = m_picOrg[2];
            int cw = width >> m_hChromaShift;
            for (int r = 0; r < height >> m_vChromaShift; r++)
            {
                for (int x = 0; x < padx >> m_hChromaShift; x++)
                {
                    U[cw + x] = U[cw - 1];
                    V[cw + x] = V[cw - 1];
                }

                U += m_strideC;
                V += m_strideC;
            }

            U = m_picOrg[1] + ((long)((height >> m_vChromaShift) - 1) * m_strideC);
            V = m_picOrg[2] + ((long)((height >> m_vChromaShift) - 1) * m_strideC);
            int n = (width + padx) >> m_hChromaShift;
            for (int j = 1; j <= pady >> m_vChromaShift; j++)
            {
                Buffer.MemoryCopy(U, U + ((long)j * m_strideC), n * sizeof(pixel), n * sizeof(pixel));
                Buffer.MemoryCopy(V, V + ((long)j * m_strideC), n * sizeof(pixel), n * sizeof(pixel));
            }
        }
    }
}

/// <summary>class Yuv: pixels for a square CU (64x64 down to 8x8) in all planes.</summary>
internal sealed unsafe class X265Yuv
{
    public readonly pixel*[] m_buf = new pixel*[3];
    public uint m_size;
    public uint m_csize;
    public int m_part;   // log2(size) - 2 (x265's partition enum for square sizes is used only as a size index here)
    public int m_csp;
    public int m_hChromaShift, m_vChromaShift;

    public X265Yuv(X265Arena arena, uint size, int csp)
    {
        m_csp = csp;
        m_hChromaShift = m_vChromaShift = csp == X265_CSP_I420 ? 1 : 0;
        m_size = size;
        m_part = X265Tables.Log2Size[size] - 2;
        if (csp == X265_CSP_I400)
        {
            m_buf[0] = arena.Alloc<pixel>((size * size) + 64);
            m_csize = 0;
        }
        else
        {
            m_csize = size >> m_hChromaShift;
            uint sizeL = size * size;
            uint sizeC = sizeL >> (m_vChromaShift + m_hChromaShift);
            m_buf[0] = arena.Alloc<pixel>(sizeL + (sizeC * 2) + 8 + 64);
            m_buf[1] = m_buf[0] + sizeL;
            m_buf[2] = m_buf[0] + sizeL + sizeC;
        }
    }

    public static int GetAddrOffset(uint absPartIdx, uint width) => X265Tables.ZscanToPelX[absPartIdx] + (X265Tables.ZscanToPelY[absPartIdx] * (int)width);

    public int GetChromaAddrOffset(uint absPartIdx) =>
        (X265Tables.ZscanToPelX[absPartIdx] >> m_hChromaShift) + ((X265Tables.ZscanToPelY[absPartIdx] >> m_vChromaShift) * (int)m_csize);

    public pixel* GetLumaAddr(uint absPartIdx) => m_buf[0] + GetAddrOffset(absPartIdx, m_size);

    public pixel* GetCbAddr(uint absPartIdx) => m_buf[1] + GetChromaAddrOffset(absPartIdx);

    public pixel* GetCrAddr(uint absPartIdx) => m_buf[2] + GetChromaAddrOffset(absPartIdx);

    public pixel* GetChromaAddr(uint chromaId, uint absPartIdx) => m_buf[chromaId] + GetChromaAddrOffset(absPartIdx);

    private static void Copy(pixel* dst, long dstStride, pixel* src, long srcStride, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            Buffer.MemoryCopy(src + (y * srcStride), dst + (y * dstStride), w * sizeof(pixel), w * sizeof(pixel));
        }
    }

    public void CopyToPicYuv(X265PicYuv dstPic, uint cuAddr, uint absPartIdx)
    {
        Copy(dstPic.GetLumaAddr(cuAddr, absPartIdx), dstPic.m_stride, m_buf[0], m_size, (int)m_size, (int)m_size);
        if (m_csp != X265_CSP_I400)
        {
            Copy(dstPic.GetCbAddr(cuAddr, absPartIdx), dstPic.m_strideC, m_buf[1], m_csize, (int)m_csize, (int)m_csize);
            Copy(dstPic.GetCrAddr(cuAddr, absPartIdx), dstPic.m_strideC, m_buf[2], m_csize, (int)m_csize, (int)m_csize);
        }
    }

    public void CopyFromPicYuv(X265PicYuv srcPic, uint cuAddr, uint absPartIdx)
    {
        Copy(m_buf[0], m_size, srcPic.GetLumaAddr(cuAddr, absPartIdx), srcPic.m_stride, (int)m_size, (int)m_size);
        if (m_csp != X265_CSP_I400)
        {
            Copy(m_buf[1], m_csize, srcPic.GetCbAddr(cuAddr, absPartIdx), srcPic.m_strideC, (int)m_csize, (int)m_csize);
            Copy(m_buf[2], m_csize, srcPic.GetCrAddr(cuAddr, absPartIdx), srcPic.m_strideC, (int)m_csize, (int)m_csize);
        }
    }

    /// <summary>Copy this (small) buffer into a part of a bigger one.</summary>
    public void CopyToPartYuv(X265Yuv dstYuv, uint absPartIdx)
    {
        Copy(dstYuv.GetLumaAddr(absPartIdx), dstYuv.m_size, m_buf[0], m_size, (int)m_size, (int)m_size);
        if (m_csp != X265_CSP_I400)
        {
            Copy(dstYuv.GetCbAddr(absPartIdx), dstYuv.m_csize, m_buf[1], m_csize, (int)m_csize, (int)m_csize);
            Copy(dstYuv.GetCrAddr(absPartIdx), dstYuv.m_csize, m_buf[2], m_csize, (int)m_csize, (int)m_csize);
        }
    }

    /// <summary>Copy a part of this (big) buffer into a smaller one (the destination's size).</summary>
    public void CopyPartToYuv(X265Yuv dstYuv, uint absPartIdx)
    {
        Copy(dstYuv.m_buf[0], dstYuv.m_size, m_buf[0] + GetAddrOffset(absPartIdx, m_size), m_size, (int)dstYuv.m_size, (int)dstYuv.m_size);
        if (m_csp != X265_CSP_I400)
        {
            Copy(dstYuv.m_buf[1], dstYuv.m_csize, m_buf[1] + GetChromaAddrOffset(absPartIdx), m_csize, (int)dstYuv.m_csize, (int)dstYuv.m_csize);
            Copy(dstYuv.m_buf[2], dstYuv.m_csize, m_buf[2] + GetChromaAddrOffset(absPartIdx), m_csize, (int)dstYuv.m_csize, (int)dstYuv.m_csize);
        }
    }

    public void CopyPartToPartLuma(X265Yuv dstYuv, uint absPartIdx, uint log2Size)
    {
        int n = 1 << (int)log2Size;
        Copy(dstYuv.GetLumaAddr(absPartIdx), dstYuv.m_size, GetLumaAddr(absPartIdx), m_size, n, n);
    }

    public void CopyPartToPartChroma(X265Yuv dstYuv, uint absPartIdx, uint log2SizeL)
    {
        int n = (1 << (int)log2SizeL) >> m_hChromaShift;
        Copy(dstYuv.GetCbAddr(absPartIdx), dstYuv.m_csize, GetCbAddr(absPartIdx), m_csize, n, n);
        Copy(dstYuv.GetCrAddr(absPartIdx), dstYuv.m_csize, GetCrAddr(absPartIdx), m_csize, n, n);
    }
}

/// <summary>class ShortYuv: int16 residual buffers for a square CU.</summary>
internal sealed unsafe class X265ShortYuv
{
    public readonly short*[] m_buf = new short*[3];
    public uint m_size;
    public uint m_csize;
    public int m_csp;
    public int m_hChromaShift, m_vChromaShift;

    public X265ShortYuv(X265Arena arena, uint size, int csp)
    {
        m_csp = csp;
        m_hChromaShift = m_vChromaShift = csp == X265_CSP_I420 ? 1 : 0;
        m_size = size;
        uint sizeL = size * size;
        if (csp == X265_CSP_I400)
        {
            m_buf[0] = arena.Alloc<short>(sizeL);
            m_csize = 0;
        }
        else
        {
            m_csize = size >> m_hChromaShift;
            uint sizeC = sizeL >> (m_vChromaShift + m_hChromaShift);
            m_buf[0] = arena.Alloc<short>(sizeL + (sizeC * 2));
            m_buf[1] = m_buf[0] + sizeL;
            m_buf[2] = m_buf[0] + sizeL + sizeC;
        }
    }

    public int GetChromaAddrOffset(uint idx) =>
        (X265Tables.ZscanToPelX[idx] >> m_hChromaShift) + ((X265Tables.ZscanToPelY[idx] >> m_vChromaShift) * (int)m_csize);

    public short* GetLumaAddr(uint absPartIdx) => m_buf[0] + X265Yuv.GetAddrOffset(absPartIdx, m_size);

    public short* GetChromaAddr(uint chromaId, uint absPartIdx) => m_buf[chromaId] + GetChromaAddrOffset(absPartIdx);
}
