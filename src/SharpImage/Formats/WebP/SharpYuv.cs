namespace SharpImage.Formats.WebP;

/// <summary>
/// libwebp 1.6.0's sharpyuv library (sharpyuv.c, sharpyuv_dsp.c, sharpyuv_gamma.c with the sRGB transfer and the
/// WebP matrix of sharpyuv_csp.c) for 8-bit RGB to 8-bit YUV420: chroma is chosen iteratively so that the RGB
/// reconstructed from the subsampled planes stays sharp (cwebp -sharp_yuv).
/// </summary>
internal static class SharpYuv
{
    private const int NumIterations = 4;
    private const int YuvFix = 16;  // fixed-point precision for RGB->YUV
    private const int YuvHalf = 1 << (YuvFix - 1);
    private const int MaxBitDepth = 14;

    // kWebpMatrix
    private static readonly int[] RgbToY = [16839, 33059, 6420, 16 << 16];
    private static readonly int[] RgbToU = [-9719, -19081, 28800, 128 << 16];
    private static readonly int[] RgbToV = [28800, -24116, -4684, 128 << 16];

    private static int GetPrecisionShift(int rgbBitDepth) =>
        rgbBitDepth + 2 <= MaxBitDepth ? 2 : MaxBitDepth - rgbBitDepth;

    private static byte Clip8b(int v) => (v & ~0xff) == 0 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

    private static ushort ClipBitDepth(int y, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        return (y & ~max) == 0 ? (ushort)y : y < 0 ? (ushort)0 : (ushort)max;
    }

    private static int RgbToGray(long r, long g, long b)
    {
        long luma = 13933 * r + 46871 * g + 4732 * b + YuvHalf;
        return (int)(luma >> YuvFix);
    }

    private static int Shift(int v, int shift) => shift >= 0 ? v << shift : v >> -shift;

    // ---- sharpyuv_gamma.c (sRGB) ---------------------------------------------------------------------------------------

    private const int GammaToLinearTabBits = 10;
    private const int LinearToGammaTabBits = 9;
    private const int GammaToLinearBits = 16;

    private static uint FixedPointInterpolation(int v, uint[] tab, int tabPosShiftRight, int tabValueShift)
    {
        uint tabPos = (uint)Shift(v, -tabPosShiftRight);
        // fractional part, in 'tab_pos_shift' fixed-point precision
        uint x = (uint)v - (tabPos << tabPosShiftRight);  // fractional part
        // v0 / v1 are in kGammaToLinearBits fixed-point precision (range [0..1])
        uint v0 = (uint)Shift((int)tab[tabPos + 0], tabValueShift);
        uint v1 = (uint)Shift((int)tab[tabPos + 1], tabValueShift);
        // Final interpolation.
        uint v2 = (v1 - v0) * x;  // note: v1 >= v0.
        int half = tabPosShiftRight > 0 ? 1 << (tabPosShiftRight - 1) : 0;
        return v0 + ((v2 + (uint)half) >> tabPosShiftRight);
    }

    // FromLinearSrgb at bit depth 10: kLinearToGammaTabS pre-shifted by (10 - GAMMA_TO_LINEAR_BITS)
    private static readonly uint[] LinearToGamma10 = BuildLinearToGamma10();

    private static uint[] BuildLinearToGamma10()
    {
        var t = new uint[Vp8EncTables.SharpLinearToGamma.Length];
        for (int i = 0; i < t.Length; ++i) t[i] = Vp8EncTables.SharpLinearToGamma[i] >> (GammaToLinearBits - 10);
        return t;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint ToLinear10(int v) => Vp8EncTables.SharpGammaToLinear[v];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int FromLinear10(uint value)
    {
        const int tabPosShiftRight = GammaToLinearBits - LinearToGammaTabBits;
        uint tabPos = value >> tabPosShiftRight;
        uint x = value - (tabPos << tabPosShiftRight);
        var tab = LinearToGamma10;
        uint v0 = tab[tabPos];
        uint v1 = tab[tabPos + 1];
        return (ushort)(v0 + (((v1 - v0) * x + (1u << (tabPosShiftRight - 1))) >> tabPosShiftRight));
    }

    private static uint GammaToLinear(int v, int bitDepth)
    {
        int shift = GammaToLinearTabBits - bitDepth;
        if (shift > 0) return Vp8EncTables.SharpGammaToLinear[v << shift];
        return FixedPointInterpolation(v, Vp8EncTables.SharpGammaToLinear, -shift, 0);
    }

    private static int LinearToGamma(uint value, int bitDepth) =>
        (ushort)FixedPointInterpolation((int)value, Vp8EncTables.SharpLinearToGamma,
            GammaToLinearBits - LinearToGammaTabBits, bitDepth - GammaToLinearBits);

    // ---- sharpyuv.c ----------------------------------------------------------------------------------------------------

    private static uint ScaleDown(int a, int b, int c, int d, int rgbBitDepth)
    {
        if (rgbBitDepth == 8) return (uint)FromLinear10((ToLinear10(a) + ToLinear10(b) + ToLinear10(c) + ToLinear10(d) + 2) >> 2);
        int bitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        uint A = GammaToLinear(a, bitDepth);
        uint B = GammaToLinear(b, bitDepth);
        uint C = GammaToLinear(c, bitDepth);
        uint D = GammaToLinear(d, bitDepth);
        return (uint)LinearToGamma((A + B + C + D + 2) >> 2, bitDepth);
    }

    private static void UpdateW(ushort[] src, int s, ushort[] dst, int d, int w, int rgbBitDepth)
    {
        int bitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        int i = 0;
        if (bitDepth == 10)
        {
            var r = src.AsSpan(s, w);
            var g = src.AsSpan(s + w, w);
            var b = src.AsSpan(s + 2 * w, w);
            var o = dst.AsSpan(d, w);
            for (; i < w; ++i)
                o[i] = (ushort)FromLinear10((uint)RgbToGray(ToLinear10(r[i]), ToLinear10(g[i]), ToLinear10(b[i])));
            return;
        }
        do
        {
            uint R = GammaToLinear(src[s + 0 * w + i], bitDepth);
            uint G = GammaToLinear(src[s + 1 * w + i], bitDepth);
            uint B = GammaToLinear(src[s + 2 * w + i], bitDepth);
            uint Y = (uint)RgbToGray(R, G, B);
            dst[d + i] = (ushort)LinearToGamma(Y, bitDepth);
        } while (++i < w);
    }

    private static void UpdateChroma(ushort[] src1, int s1, ushort[] src2, int s2, short[] dst, int d, int uvW, int rgbBitDepth)
    {
        int i = 0;
        do
        {
            int r = (int)ScaleDown(src1[s1 + 0 * uvW + 0], src1[s1 + 0 * uvW + 1], src2[s2 + 0 * uvW + 0],
                src2[s2 + 0 * uvW + 1], rgbBitDepth);
            int g = (int)ScaleDown(src1[s1 + 2 * uvW + 0], src1[s1 + 2 * uvW + 1], src2[s2 + 2 * uvW + 0],
                src2[s2 + 2 * uvW + 1], rgbBitDepth);
            int b = (int)ScaleDown(src1[s1 + 4 * uvW + 0], src1[s1 + 4 * uvW + 1], src2[s2 + 4 * uvW + 0],
                src2[s2 + 4 * uvW + 1], rgbBitDepth);
            int W = RgbToGray(r, g, b);
            dst[d + 0 * uvW] = (short)(r - W);
            dst[d + 1 * uvW] = (short)(g - W);
            dst[d + 2 * uvW] = (short)(b - W);
            d += 1;
            s1 += 2;
            s2 += 2;
        } while (++i < uvW);
    }

    private static void StoreGray(ushort[] rgb, int s, ushort[] y, int d, int w)
    {
        int i = 0;
        do
        {
            y[d + i] = (ushort)RgbToGray(rgb[s + 0 * w + i], rgb[s + 1 * w + i], rgb[s + 2 * w + i]);
        } while (++i < w);
    }

    private static ushort Filter2(int A, int B, int W0, int bitDepth)
    {
        int v0 = (A * 3 + B + 2) >> 2;
        return ClipBitDepth(v0 + W0, bitDepth);
    }

    private static void ImportOneRow(uint[] argb, int p, int rgbBitDepth, int picWidth, ushort[] dst, int d)
    {
        int i = 0;
        int w = (picWidth + 1) & ~1;
        int shift = GetPrecisionShift(rgbBitDepth);
        do
        {
            uint px = argb[p + i];
            dst[d + i + 0 * w] = (ushort)Shift((int)((px >> 16) & 0xff), shift);
            dst[d + i + 1 * w] = (ushort)Shift((int)((px >> 8) & 0xff), shift);
            dst[d + i + 2 * w] = (ushort)Shift((int)(px & 0xff), shift);
        } while (++i < picWidth);
        if ((picWidth & 1) != 0)
        {
            // replicate rightmost pixel
            dst[d + picWidth + 0 * w] = dst[d + picWidth + 0 * w - 1];
            dst[d + picWidth + 1 * w] = dst[d + picWidth + 1 * w - 1];
            dst[d + picWidth + 2 * w] = dst[d + picWidth + 2 * w - 1];
        }
    }

    // SharpYuvFilterRow_C
    private static void FilterRow(short[] uv, int a, int b, int len, ushort[] bestY, int y, ushort[] o, int op, int bitDepth)
    {
        int maxY = (1 << bitDepth) - 1;
        for (int i = 0; i < len; ++i, ++a, ++b)
        {
            int v0 = (uv[a + 0] * 9 + uv[a + 1] * 3 + uv[b + 0] * 3 + uv[b + 1] + 8) >> 4;
            int v1 = (uv[a + 1] * 9 + uv[a + 0] * 3 + uv[b + 1] * 3 + uv[b + 0] + 8) >> 4;
            int y0 = bestY[y + 2 * i + 0] + v0;
            int y1 = bestY[y + 2 * i + 1] + v1;
            o[op + 2 * i + 0] = (ushort)(y0 < 0 ? 0 : y0 > maxY ? maxY : y0);
            o[op + 2 * i + 1] = (ushort)(y1 < 0 ? 0 : y1 > maxY ? maxY : y1);
        }
    }

    private static void InterpolateTwoRows(ushort[] bestY, int by, short[] uv, int prevUv, int curUv, int nextUv, int w,
        ushort[] out1Buf, int out1, int out2, int rgbBitDepth)
    {
        int uvW = w >> 1;
        int len = (w - 1) >> 1;   // length to filter
        int k = 3;
        int bitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        while (k-- > 0)
        {
            // process each R/G/B segments in turn
            // special boundary case for i==0
            out1Buf[out1] = Filter2(uv[curUv], uv[prevUv], bestY[by], bitDepth);
            out1Buf[out2] = Filter2(uv[curUv], uv[nextUv], bestY[by + w], bitDepth);

            FilterRow(uv, curUv, prevUv, len, bestY, by + 0 + 1, out1Buf, out1 + 1, bitDepth);
            FilterRow(uv, curUv, nextUv, len, bestY, by + w + 1, out1Buf, out2 + 1, bitDepth);

            // special boundary case for i == w - 1 when w is even
            if ((w & 1) == 0)
            {
                out1Buf[out1 + w - 1] = Filter2(uv[curUv + uvW - 1], uv[prevUv + uvW - 1], bestY[by + w - 1 + 0], bitDepth);
                out1Buf[out2 + w - 1] = Filter2(uv[curUv + uvW - 1], uv[nextUv + uvW - 1], bestY[by + w - 1 + w], bitDepth);
            }
            out1 += w;
            out2 += w;
            prevUv += uvW;
            curUv += uvW;
            nextUv += uvW;
        }
    }

    private static int RgbToYuvComponent(int r, int g, int b, int[] coeffs, int c3, int sfix)
    {
        int srounder = 1 << (YuvFix + sfix - 1);
        int luma = coeffs[0] * r + coeffs[1] * g + coeffs[2] * b + c3 + srounder;
        return luma >> (YuvFix + sfix);
    }

    /// <summary>SharpYuvConvert(…, rgb_bit_depth 8, …, yuv_bit_depth 8, kSharpYuvMatrixWebp) on a 0xAARRGGBB picture.</summary>
    public static void Convert(uint[] argb, int width, int height, byte[] yPlane, int yStride, byte[] uPlane, byte[] vPlane,
        int uvStride)
    {
        const int rgbBitDepth = 8;
        int sfix = GetPrecisionShift(rgbBitDepth);
        // rgb_bit_depth == yuv_bit_depth: the matrix is used as is, with the offsets shifted by sfix
        int y3 = Shift(RgbToY[3], sfix), u3 = Shift(RgbToU[3], sfix), v3 = Shift(RgbToV[3], sfix);

        int w = (width + 1) & ~1;
        int h = (height + 1) & ~1;
        int uvW = w >> 1;
        int uvH = h >> 1;
        int yBitDepth = rgbBitDepth + GetPrecisionShift(rgbBitDepth);
        ulong prevDiffYSum = ~0ul;
        var tmpBuffer = new ushort[w * 3 * 2];   // scratch
        var bestYBase = new ushort[w * h];
        var targetYBase = new ushort[w * h];
        var bestRgbY = new ushort[w * 2];
        var bestUvBase = new short[uvW * 3 * uvH];
        var targetUvBase = new short[uvW * 3 * uvH];
        var bestRgbUv = new short[uvW * 3];
        int bestY = 0, targetY = 0, bestUv = 0, targetUv = 0;
        ulong diffYThreshold = (ulong)(3.0 * w * h);
        int p = 0;

        // Import RGB samples to W/RGB representation.
        for (int j = 0; j < height; j += 2)
        {
            bool isLastRow = j == height - 1;
            const int src1 = 0;
            int src2 = 3 * w;

            // prepare two rows of input
            ImportOneRow(argb, p, rgbBitDepth, width, tmpBuffer, src1);
            if (!isLastRow)
                ImportOneRow(argb, p + width, rgbBitDepth, width, tmpBuffer, src2);
            else
                Array.Copy(tmpBuffer, src1, tmpBuffer, src2, 3 * w);
            StoreGray(tmpBuffer, src1, bestYBase, bestY + 0, w);
            StoreGray(tmpBuffer, src2, bestYBase, bestY + w, w);

            UpdateW(tmpBuffer, src1, targetYBase, targetY, w, rgbBitDepth);
            UpdateW(tmpBuffer, src2, targetYBase, targetY + w, w, rgbBitDepth);
            UpdateChroma(tmpBuffer, src1, tmpBuffer, src2, targetUvBase, targetUv, uvW, rgbBitDepth);
            Array.Copy(targetUvBase, targetUv, bestUvBase, bestUv, 3 * uvW);
            bestY += 2 * w;
            bestUv += 3 * uvW;
            targetY += 2 * w;
            targetUv += 3 * uvW;
            p += 2 * width;
        }

        // Iterate and resolve clipping conflicts.
        for (int iter = 0; iter < NumIterations; ++iter)
        {
            int curUv = 0;
            int prevUv = 0;
            ulong diffYSum = 0;

            bestY = 0;
            bestUv = 0;
            targetY = 0;
            targetUv = 0;
            int j = 0;
            do
            {
                const int src1 = 0;
                int src2 = 3 * w;
                {
                    int nextUv = curUv + (j < h - 2 ? 3 * uvW : 0);
                    InterpolateTwoRows(bestYBase, bestY, bestUvBase, prevUv, curUv, nextUv, w, tmpBuffer, src1, src2,
                        rgbBitDepth);
                    prevUv = curUv;
                    curUv = nextUv;
                }

                UpdateW(tmpBuffer, src1, bestRgbY, 0 * w, w, rgbBitDepth);
                UpdateW(tmpBuffer, src2, bestRgbY, 1 * w, w, rgbBitDepth);
                UpdateChroma(tmpBuffer, src1, tmpBuffer, src2, bestRgbUv, 0, uvW, rgbBitDepth);

                // update two rows of Y and one row of RGB
                {
                    // SharpYuvUpdateY_C
                    int maxY = (1 << yBitDepth) - 1;
                    for (int i = 0; i < 2 * w; ++i)
                    {
                        int diffY = targetYBase[targetY + i] - bestRgbY[i];
                        int newY = bestYBase[bestY + i] + diffY;
                        bestYBase[bestY + i] = (ushort)(newY < 0 ? 0 : newY > maxY ? maxY : newY);
                        diffYSum += (ulong)Math.Abs(diffY);
                    }
                }
                // SharpYuvUpdateRGB_C
                for (int i = 0; i < 3 * uvW; ++i)
                {
                    int diffUv = targetUvBase[targetUv + i] - bestRgbUv[i];
                    bestUvBase[bestUv + i] += (short)diffUv;
                }

                bestY += 2 * w;
                bestUv += 3 * uvW;
                targetY += 2 * w;
                targetUv += 3 * uvW;
                j += 2;
            } while (j < h);
            // test exit condition
            if (iter > 0)
            {
                if (diffYSum < diffYThreshold) break;
                if (diffYSum > prevDiffYSum) break;
            }
            prevDiffYSum = diffYSum;
        }

        // final reconstruction (ConvertWRGBToYUV)
        {
            int by = 0, buv = 0, yo = 0;
            for (int j = 0; j < height; ++j)
            {
                for (int i = 0; i < width; ++i)
                {
                    int off = i >> 1;
                    int W = bestYBase[by + i];
                    int r = bestUvBase[buv + off + 0 * uvW] + W;
                    int g = bestUvBase[buv + off + 1 * uvW] + W;
                    int b = bestUvBase[buv + off + 2 * uvW] + W;
                    yPlane[yo + i] = Clip8b(RgbToYuvComponent(r, g, b, RgbToY, y3, sfix));
                }
                by += w;
                buv += (j & 1) * 3 * uvW;
                yo += yStride;
            }
            buv = 0;
            int uvo = 0;
            for (int j = 0; j < uvH; ++j)
            {
                for (int i = 0; i < uvW; ++i)
                {
                    int r = bestUvBase[buv + i + 0 * uvW];
                    int g = bestUvBase[buv + i + 1 * uvW];
                    int b = bestUvBase[buv + i + 2 * uvW];
                    uPlane[uvo + i] = Clip8b(RgbToYuvComponent(r, g, b, RgbToU, u3, sfix));
                    vPlane[uvo + i] = Clip8b(RgbToYuvComponent(r, g, b, RgbToV, v3, sfix));
                }
                buv += 3 * uvW;
                uvo += uvStride;
            }
        }
    }
}
