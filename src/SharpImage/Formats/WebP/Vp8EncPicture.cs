namespace SharpImage.Formats.WebP;

/// <summary>
/// The YUV420(A) WebPPicture the lossy encoder works on, built from ARGB exactly like libwebp 1.6.0
/// src/enc/picture_csp_enc.c ImportYUVAFromRGBA (gamma-compressed 2x2 chroma averaging, alpha-weighted where
/// transparent, optional dithering) and cleaned up like src/enc/picture_tools_enc.c WebPCleanupTransparentArea.
/// </summary>
internal sealed class Vp8EncPicture
{
    public readonly int Width, Height;
    public readonly byte[] Y, U, V;
    public readonly byte[]? A;   // only when the picture has transparency (WEBP_YUV420A)
    public readonly int YStride, UvStride, AStride;

    private Vp8EncPicture(int width, int height, bool hasAlpha)
    {
        Width = width;
        Height = height;
        YStride = width;
        UvStride = (width + 1) >> 1;
        AStride = width;
        int uvHeight = (height + 1) >> 1;
        Y = new byte[YStride * height];
        U = new byte[UvStride * uvHeight];
        V = new byte[UvStride * uvHeight];
        if (hasAlpha) A = new byte[AStride * height];
    }

    // ---- gamma (USE_GAMMA_COMPRESSION, USE_INVERSE_ALPHA_TABLE) -------------------------------------------------------

    private const int GammaTabFix = 7;
    private const int GammaTabScale = 1 << GammaTabFix;
    private const int GammaTabRounder = GammaTabScale >> 1;
    private const int AlphaFix = 19;
    private const int YuvFix = 16;
    private const int YuvHalf = 1 << (YuvFix - 1);

    private static uint GammaToLinear(int v) => Vp8EncTables.GammaToLinear[v];

    private static int Interpolate(int v)
    {
        int tabPos = v >> (GammaTabFix + 2);    // integer part
        int x = v & ((GammaTabScale << 2) - 1);  // fractional part
        int v0 = Vp8EncTables.LinearToGamma[tabPos];
        int v1 = Vp8EncTables.LinearToGamma[tabPos + 1];
        return v1 * x + v0 * ((GammaTabScale << 2) - x);   // interpolate
    }

    private static int LinearToGamma(uint baseValue, int shift)
    {
        int y = Interpolate((int)(baseValue << shift));   // final uplifted value
        return (y + GammaTabRounder) >> GammaTabFix;    // descale
    }

    // ---- RGB -> YUV (src/dsp/yuv.h) -----------------------------------------------------------------------------------

    private static int ClipUV(int uv, int rounding)
    {
        uv = (uv + rounding + (128 << (YuvFix + 2))) >> (YuvFix + 2);
        return (uv & ~0xff) == 0 ? uv : uv < 0 ? 0 : 255;
    }

    private static int RgbToY(int r, int g, int b, int rounding)
    {
        int luma = 16839 * r + 33059 * g + 6420 * b;
        return (luma + rounding + (16 << YuvFix)) >> YuvFix;  // no need to clip
    }

    private static int RgbToU(int r, int g, int b, int rounding) => ClipUV(-9719 * r - 19081 * g + 28800 * b, rounding);
    private static int RgbToV(int r, int g, int b, int rounding) => ClipUV(+28800 * r - 24116 * g - 4684 * b, rounding);

    /// <summary>VP8Random (src/utils/random_utils.[ch]).</summary>
    private sealed class Random
    {
        private static readonly uint[] KRandomTable =
        [
            0x0de15230, 0x03b31886, 0x775faccb, 0x1c88626a, 0x68385c55, 0x14b3b828,
            0x4a85fef8, 0x49ddb84b, 0x64fcf397, 0x5c550289, 0x4a290000, 0x0d7ec1da,
            0x5940b7ab, 0x5492577d, 0x4e19ca72, 0x38d38c69, 0x0c01ee65, 0x32a1755f,
            0x5437f652, 0x5abb2c32, 0x0faa57b1, 0x73f533e7, 0x685feeda, 0x7563cce2,
            0x6e990e83, 0x4730a7ed, 0x4fc0d9c6, 0x496b153c, 0x4f1403fa, 0x541afb0c,
            0x73990b32, 0x26d7cb1c, 0x6fcc3706, 0x2cbb77d8, 0x75762f2a, 0x6425ccdd,
            0x24b35461, 0x0a7d8715, 0x220414a8, 0x141ebf67, 0x56b41583, 0x73e502e3,
            0x44cab16f, 0x28264d42, 0x73baaefb, 0x0a50ebed, 0x1d6ab6fb, 0x0d3ad40b,
            0x35db3b68, 0x2b081e83, 0x77ce6b95, 0x5181e5f0, 0x78853bbc, 0x009f9494,
            0x27e5ed3c,
        ];

        private readonly uint[] _tab = (uint[])KRandomTable.Clone();
        private int _index1, _index2 = 31;
        private readonly int _amp;

        public Random(float dithering)
        {
            _amp = dithering < 0.0 ? 0
                 : dithering > 1.0 ? 1 << 8
                 : (int)(uint)((1 << 8) * dithering);
        }

        public int Bits(int numBits)
        {
            int diff = (int)(_tab[_index1] - _tab[_index2]);
            if (diff < 0) diff = (int)((uint)diff + (1u << 31));
            _tab[_index1] = (uint)diff;
            if (++_index1 == 55) _index1 = 0;
            if (++_index2 == 55) _index2 = 0;
            // sign-extend, 0-center
            diff = (int)((uint)diff << 1) >> (32 - numBits);
            diff = (diff * _amp) >> 8;  // restrict range
            diff += 1 << (numBits - 1);  // shift back to 0.5-center
            return diff;
        }
    }

    private static int RgbToY(int r, int g, int b, Random? rg) =>
        rg == null ? RgbToY(r, g, b, YuvHalf) : RgbToY(r, g, b, rg.Bits(YuvFix));

    private static int RgbToU(int r, int g, int b, Random? rg) =>
        rg == null ? RgbToU(r, g, b, YuvHalf << 2) : RgbToU(r, g, b, rg.Bits(YuvFix + 2));

    private static int RgbToV(int r, int g, int b, Random? rg) =>
        rg == null ? RgbToV(r, g, b, YuvHalf << 2) : RgbToV(r, g, b, rg.Bits(YuvFix + 2));

    // ---- import ------------------------------------------------------------------------------------------------------

    private static int Ch(uint p, int shift) => (int)((p >> shift) & 0xff);

    // channel shifts in a 0xAARRGGBB pixel
    private const int RShift = 16, GShift = 8, BShift = 0;

    private static int Sum4(uint[] argb, int p, int step, int stride, int sh) => LinearToGamma(
        GammaToLinear(Ch(argb[p], sh)) + GammaToLinear(Ch(argb[p + step], sh)) +
        GammaToLinear(Ch(argb[p + stride], sh)) + GammaToLinear(Ch(argb[p + stride + step], sh)), 0);

    private static int Sum2(uint[] argb, int p, int stride, int sh) =>
        LinearToGamma(GammaToLinear(Ch(argb[p], sh)) + GammaToLinear(Ch(argb[p + stride], sh)), 1);

    private static int LinearToGammaWeighted(uint[] argb, int p, uint totalA, int step, int stride, int sh)
    {
        uint sum =
            (uint)Ch(argb[p], 24) * GammaToLinear(Ch(argb[p], sh)) +
            (uint)Ch(argb[p + step], 24) * GammaToLinear(Ch(argb[p + step], sh)) +
            (uint)Ch(argb[p + stride], 24) * GammaToLinear(Ch(argb[p + stride], sh)) +
            (uint)Ch(argb[p + stride + step], 24) * GammaToLinear(Ch(argb[p + stride + step], sh));
        return LinearToGamma((sum * Vp8EncTables.InvAlpha[totalA]) >> (AlphaFix - 2), 0);
    }

    private static void AccumulateRgba(uint[] argb, int p, int stride, ushort[] dst, int width)
    {
        int i, d = 0;
        for (i = 0; i < (width >> 1); i += 1, p += 2, d += 4)
        {
            uint a = (uint)(Ch(argb[p], 24) + Ch(argb[p + stride], 24) + Ch(argb[p + 1], 24) + Ch(argb[p + 1 + stride], 24));
            int r, g, b;
            if (a == 4 * 0xff || a == 0)
            {
                r = Sum4(argb, p, 1, stride, RShift);
                g = Sum4(argb, p, 1, stride, GShift);
                b = Sum4(argb, p, 1, stride, BShift);
            }
            else
            {
                r = LinearToGammaWeighted(argb, p, a, 1, stride, RShift);
                g = LinearToGammaWeighted(argb, p, a, 1, stride, GShift);
                b = LinearToGammaWeighted(argb, p, a, 1, stride, BShift);
            }
            dst[d + 0] = (ushort)r;
            dst[d + 1] = (ushort)g;
            dst[d + 2] = (ushort)b;
            dst[d + 3] = (ushort)a;
        }
        if ((width & 1) != 0)
        {
            uint a = 2u * (uint)(Ch(argb[p], 24) + Ch(argb[p + stride], 24));
            int r, g, b;
            if (a == 4 * 0xff || a == 0)
            {
                r = Sum2(argb, p, stride, RShift);
                g = Sum2(argb, p, stride, GShift);
                b = Sum2(argb, p, stride, BShift);
            }
            else
            {
                r = LinearToGammaWeighted(argb, p, a, 0, stride, RShift);
                g = LinearToGammaWeighted(argb, p, a, 0, stride, GShift);
                b = LinearToGammaWeighted(argb, p, a, 0, stride, BShift);
            }
            dst[d + 0] = (ushort)r;
            dst[d + 1] = (ushort)g;
            dst[d + 2] = (ushort)b;
            dst[d + 3] = (ushort)a;
        }
    }

    private static void AccumulateRgb(uint[] argb, int p, int stride, ushort[] dst, int width)
    {
        int i, d = 0;
        for (i = 0; i < (width >> 1); i += 1, p += 2, d += 4)
        {
            dst[d + 0] = (ushort)Sum4(argb, p, 1, stride, RShift);
            dst[d + 1] = (ushort)Sum4(argb, p, 1, stride, GShift);
            dst[d + 2] = (ushort)Sum4(argb, p, 1, stride, BShift);
        }
        if ((width & 1) != 0)
        {
            dst[d + 0] = (ushort)Sum2(argb, p, stride, RShift);
            dst[d + 1] = (ushort)Sum2(argb, p, stride, GShift);
            dst[d + 2] = (ushort)Sum2(argb, p, stride, BShift);
        }
    }

    private static void ConvertRowToY(uint[] argb, int p, byte[] dstY, int y0, int width, Random? rg)
    {
        for (int i = 0; i < width; i += 1, p += 1)
            dstY[y0 + i] = (byte)RgbToY(Ch(argb[p], RShift), Ch(argb[p], GShift), Ch(argb[p], BShift), rg);
    }

    private static void ConvertRowsToUV(ushort[] rgb, byte[] dstU, byte[] dstV, int o, int width, Random? rg)
    {
        for (int i = 0, s = 0; i < width; i += 1, s += 4)
        {
            int r = rgb[s + 0], g = rgb[s + 1], b = rgb[s + 2];
            dstU[o + i] = (byte)RgbToU(r, g, b, rg);
            dstV[o + i] = (byte)RgbToV(r, g, b, rg);
        }
    }

    /// <summary>WebPExtractAlpha (ExtractAlpha_C): copies the alpha of 'height' rows, true if all of it is 0xff.</summary>
    private static bool ExtractAlpha(uint[] argb, int p, int stride, int width, int height, byte[] alpha, int a, int aStride)
    {
        uint alphaMask = 0xff;
        for (int j = 0; j < height; ++j)
        {
            for (int i = 0; i < width; ++i)
            {
                uint alphaValue = argb[p + i] >> 24;
                alpha[a + i] = (byte)alphaValue;
                alphaMask &= alphaValue;
            }
            p += stride;
            a += aStride;
        }
        return alphaMask == 0xff;
    }

    /// <summary>
    /// WebPPictureImportRGBA (lossy, use_argb = 0) / WebPPictureARGBToYUVADithered / WebPPictureSharpARGBToYUVA:
    /// ImportYUVAFromRGBA on a 0xAARRGGBB picture (width x height, stride = width).
    /// </summary>
    public static Vp8EncPicture FromArgb(uint[] argb, int width, int height, float dithering, bool useIterativeConversion)
    {
        bool hasAlpha = false;
        for (int i = 0; i < width * height && !hasAlpha; ++i) hasAlpha = (argb[i] >> 24) != 0xff;
        var pic = new Vp8EncPicture(width, height, hasAlpha);

        const int kMinDimensionIterativeConversion = 4;
        if (width < kMinDimensionIterativeConversion || height < kMinDimensionIterativeConversion)
            useIterativeConversion = false;
        if (useIterativeConversion)
        {
            // sharp YUV (PreprocessARGB -> SharpYuvConvert)
            SharpYuv.Convert(argb, width, height, pic.Y, pic.YStride, pic.U, pic.V, pic.UvStride);
            if (hasAlpha) ExtractAlpha(argb, 0, width, width, height, pic.A!, 0, pic.AStride);
            return pic;
        }

        int uvWidth = (width + 1) >> 1;
        var tmpRgb = new ushort[4 * uvWidth];
        int dstY = 0, dstUV = 0, dstA = 0;
        Random? rg = dithering > 0.0 ? new Random(dithering) : null;
        int p = 0;
        int y;
        for (y = 0; y < (height >> 1); ++y)
        {
            bool rowsHaveAlpha = hasAlpha;
            ConvertRowToY(argb, p, pic.Y, dstY, width, rg);
            ConvertRowToY(argb, p + width, pic.Y, dstY + pic.YStride, width, rg);
            dstY += 2 * pic.YStride;
            if (hasAlpha)
            {
                rowsHaveAlpha &= !ExtractAlpha(argb, p, width, width, 2, pic.A!, dstA, pic.AStride);
                dstA += 2 * pic.AStride;
            }
            if (!rowsHaveAlpha)
                AccumulateRgb(argb, p, width, tmpRgb, width);
            else
                AccumulateRgba(argb, p, width, tmpRgb, width);
            ConvertRowsToUV(tmpRgb, pic.U, pic.V, dstUV, uvWidth, rg);
            dstUV += pic.UvStride;
            p += 2 * width;
        }
        if ((height & 1) != 0)
        {
            // extra last row
            bool rowHasAlpha = hasAlpha;
            ConvertRowToY(argb, p, pic.Y, dstY, width, rg);
            if (rowHasAlpha) rowHasAlpha &= !ExtractAlpha(argb, p, 0, width, 1, pic.A!, dstA, 0);
            if (!rowHasAlpha)
                AccumulateRgb(argb, p, 0, tmpRgb, width);
            else
                AccumulateRgba(argb, p, 0, tmpRgb, width);
            ConvertRowsToUV(tmpRgb, pic.U, pic.V, dstUV, uvWidth, rg);
        }
        return pic;
    }

    // ---- WebPCleanupTransparentArea (YUV path) ------------------------------------------------------------------------

    private const int Size = 8, Size2 = Size / 2;

    private static void Flatten(byte[] ptr, int o, int v, int stride, int size)
    {
        for (int y = 0; y < size; ++y)
        {
            ptr.AsSpan(o, size).Fill((byte)v);
            o += stride;
        }
    }

    private static bool SmoothenBlock(byte[] a, int ao, int aStride, byte[] yb, int yo, int yStride, int width, int height)
    {
        int sum = 0, count = 0;
        int alphaPtr = ao, lumaPtr = yo;
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                if (a[alphaPtr + x] != 0)
                {
                    ++count;
                    sum += yb[lumaPtr + x];
                }
            }
            alphaPtr += aStride;
            lumaPtr += yStride;
        }
        if (count > 0 && count < width * height)
        {
            byte avgU8 = (byte)(sum / count);
            alphaPtr = ao;
            lumaPtr = yo;
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                    if (a[alphaPtr + x] == 0) yb[lumaPtr + x] = avgU8;
                alphaPtr += aStride;
                lumaPtr += yStride;
            }
        }
        return count == 0;
    }

    /// <summary>WebPCleanupTransparentArea: flattens fully transparent 8x8 blocks (and smooths partial ones).</summary>
    public void CleanupTransparentArea()
    {
        if (A == null) return;
        int width = Width, height = Height;
        int yPtr = 0, uvPtr = 0, aPtr = 0;
        int v0 = 0, v1 = 0, v2 = 0;
        int x, y;
        for (y = 0; y + Size <= height; y += Size)
        {
            bool needReset = true;
            for (x = 0; x + Size <= width; x += Size)
            {
                if (SmoothenBlock(A, aPtr + x, AStride, Y, yPtr + x, YStride, Size, Size))
                {
                    if (needReset)
                    {
                        v0 = Y[yPtr + x];
                        v1 = U[uvPtr + (x >> 1)];
                        v2 = V[uvPtr + (x >> 1)];
                        needReset = false;
                    }
                    Flatten(Y, yPtr + x, v0, YStride, Size);
                    Flatten(U, uvPtr + (x >> 1), v1, UvStride, Size2);
                    Flatten(V, uvPtr + (x >> 1), v2, UvStride, Size2);
                }
                else
                {
                    needReset = true;
                }
            }
            if (x < width) SmoothenBlock(A, aPtr + x, AStride, Y, yPtr + x, YStride, width - x, Size);
            aPtr += Size * AStride;
            yPtr += Size * YStride;
            uvPtr += Size2 * UvStride;
        }
        if (y < height)
        {
            int subHeight = height - y;
            for (x = 0; x + Size <= width; x += Size)
                SmoothenBlock(A, aPtr + x, AStride, Y, yPtr + x, YStride, Size, subHeight);
            if (x < width) SmoothenBlock(A, aPtr + x, AStride, Y, yPtr + x, YStride, width - x, subHeight);
        }
    }

    /// <summary>WebPPictureHasTransparency on the YUVA picture.</summary>
    public bool HasTransparency()
    {
        if (A == null) return false;
        for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
                if (A[y * AStride + x] != 0xff) return true;
        return false;
    }
}
