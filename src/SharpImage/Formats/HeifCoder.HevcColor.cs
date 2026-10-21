using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    /// <summary>
    /// The colour of a decoded HEVC picture as libheif takes it: the colr nclx when the file has one, else what the
    /// bitstream signals (libde265's VUI values: unspecified primaries, transfer and matrix, limited range when absent).
    /// </summary>
    private static (int Cp, int Tc, int Mc, bool Full) HevcNclx((int Cp, int Tc, int Mc, bool Full)? colr, Hevc.HevcSequenceParameterSet sps)
    {
        if (colr is { } c) return c;
        var vui = sps.VuiParametersPresent ? sps.Vui : null;
        bool signal = vui is { VideoSignalTypePresent: true };
        bool desc = signal && vui!.ColourDescriptionPresent;
        return (desc ? vui!.ColourPrimaries : 2, desc ? vui!.TransferCharacteristics : 2, desc ? vui!.MatrixCoefficients : 2,
            signal && vui!.VideoFullRangeFlag);
    }

    /// <summary>
    /// A decoded HEVC picture to RGB exactly as libheif converts it for heif-dec, by its pipeline choice: 8-bit 4:0:0
    /// copied (Op_mono_to_RGB24_32); 8-bit full-range 4:2:0 in 8.8 fixed point (Op_YCbCr420_to_RGB24/32); deeper 4:2:0
    /// (and 4:0:0 with neutral chroma, Op_mono_to_YCbCr420) in float (Op_YCbCr420_to_RRGGBBaa); everything else through
    /// Op_YCbCr_to_RGB. Chroma is nearest-neighbour throughout. Samples deeper than 8 bits are scaled to 16 as heif-dec
    /// writes its PNG (<c>v &lt;&lt; s | v &gt;&gt; (16 - s)</c>, s = 16 - depth).
    /// </summary>
    private static void ConvertHevcPicture(Hevc.DecodedVideoFrame yuv, Hevc.HevcChromaFormat chroma, int bpp,
        (int Cp, int Tc, int Mc, bool Full) nclx, ImageFrame frame)
    {
        int w = (int)frame.Columns, h = (int)frame.Rows, nch = frame.NumberOfChannels;
        bool hbd = bpp > 8;
        bool mono = chroma == Hevc.HevcChromaFormat.Monochrome;
        int shiftH = chroma is Hevc.HevcChromaFormat.Chroma420 or Hevc.HevcChromaFormat.Chroma422 ? 1 : 0;
        int shiftV = chroma == Hevc.HevcChromaFormat.Chroma420 ? 1 : 0;
        int maxVal = (1 << bpp) - 1, half = 1 << (bpp - 1);
        int outShift = 16 - bpp;
        ReadOnlySpan<byte> yp = yuv.YPlane.Span;
        ReadOnlySpan<byte> up = mono ? default : yuv.UPlane.Span, vp = mono ? default : yuv.VPlane.Span;

        int Sample(ReadOnlySpan<byte> p, int stride, int x, int y)
        {
            if (!hbd) return p[(y * stride) + x];
            int o = (y * stride) + (x * 2);
            return p[o] | (p[o + 1] << 8);
        }

        ushort Out(int v) => hbd ? (ushort)((v << outShift) | (v >> (16 - outShift))) : (ushort)(v * 257);

        // Op_mono_to_RGB24_32: the luma as R = G = B, whatever the range and matrix
        if (mono && !hbd)
        {
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    ushort v = Out(yp[(y * yuv.YStride) + x]);
                    row[x * nch] = row[(x * nch) + 1] = row[(x * nch) + 2] = v;
                }
            }

            return;
        }

        int mc = nclx.Mc;
        var (rCr, gCb, gCr, bCb) = YCbCrToRgbCoefficients(mc, nclx.Cp);
        bool fixedPoint = !hbd && chroma == Hevc.HevcChromaFormat.Chroma420 && nclx.Full && mc is not (0 or 8 or 11 or 14);
        bool hdr420 = hbd && (mono || chroma == Hevc.HevcChromaFormat.Chroma420) && mc is not (0 or 8 or 11 or 14);
        if (fixedPoint)
        {
            int r = (int)MathF.Round(256 * rCr, MidpointRounding.AwayFromZero), gr = (int)MathF.Round(256 * gCr, MidpointRounding.AwayFromZero);
            int gb = (int)MathF.Round(256 * gCb, MidpointRounding.AwayFromZero), b = (int)MathF.Round(256 * bCb, MidpointRounding.AwayFromZero);
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    int cb = up[((y >> 1) * yuv.UStride) + (x >> 1)] - 128, cr = vp[((y >> 1) * yuv.VStride) + (x >> 1)] - 128;
                    int yv = yp[(y * yuv.YStride) + x];
                    row[x * nch] = Out(Clamp8(yv + (((r * cr) + 128) >> 8)));
                    row[(x * nch) + 1] = Out(Clamp8(yv + (((gb * cb) + (gr * cr) + 128) >> 8)));
                    row[(x * nch) + 2] = Out(Clamp8(yv + (((b * cb) + 128) >> 8)));
                }
            }

            return;
        }

        float limitedOffset = 16 << (bpp - 8);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int cy = y >> shiftV;
            for (int x = 0; x < w; x++)
            {
                int cx = x >> shiftH;
                int yi = Sample(yp, yuv.YStride, x, y);
                int cbi = mono ? half : Sample(up, yuv.UStride, cx, cy);
                int cri = mono ? half : Sample(vp, yuv.VStride, cx, cy);
                int rr, gg, bb;
                if (!hdr420 && mc == 0)
                {
                    if (nclx.Full)
                    {
                        (rr, gg, bb) = (cri, yi, cbi);
                    }
                    else
                    {
                        rr = ClipF(((cri - limitedOffset) * 1.1429f), maxVal);
                        gg = ClipF(((yi - limitedOffset) * 1.1689f), maxVal);
                        bb = ClipF(((cbi - limitedOffset) * 1.1429f), maxVal);
                    }
                }
                else if (!hdr420 && mc == 8)
                {
                    int cb = cbi - half, cr = cri - half;
                    rr = ClipI(yi - cb + cr, maxVal);
                    gg = ClipI(yi + cb, maxVal);
                    bb = ClipI(yi - cb - cr, maxVal);
                }
                else if (!hdr420 && mc == 16)
                {
                    short cb = (short)(cbi - half), cr = (short)(cri - half);
                    short t = (short)(yi - (cb >> 1));
                    short g = (short)(t + cb);
                    short b = (short)(t - (cr >> 1));
                    short r = (short)(b + cr);
                    rr = ClipI(r * 4, maxVal);
                    gg = ClipI(g * 4, maxVal);
                    bb = ClipI(b * 4, maxVal);
                }
                else
                {
                    float yv = yi, cb = cbi - half, cr = cri - half;
                    if (!nclx.Full)
                    {
                        yv = (yv - limitedOffset) * 1.1689f;
                        cb *= 1.1429f;
                        cr *= 1.1429f;
                    }

                    rr = ClipF(yv + (rCr * cr), maxVal);
                    gg = ClipF(yv + (gCb * cb) + (gCr * cr), maxVal);
                    bb = ClipF(yv + (bCb * cb), maxVal);
                }

                row[x * nch] = Out(rr);
                row[(x * nch) + 1] = Out(gg);
                row[(x * nch) + 2] = Out(bb);
            }
        }

        static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
        static int ClipI(int v, int max) => v < 0 ? 0 : v > max ? max : v;
        static int ClipF(float v, int max)
        {
            int x = (int)(v + 0.5f);
            return x < 0 ? 0 : x > max ? max : x;
        }
    }

    // libheif get_YCbCr_to_RGB_coefficients (float): from get_Kr_Kb, or BT.601's defaults when the matrix has none.
    private static (float RCr, float GCb, float GCr, float BCb) YCbCrToRgbCoefficients(int matrix, int primaries)
    {
        var (kr, kb) = LibheifKrKb(matrix, primaries);
        if (kb == 0 && kr == 0) return (1.402f, -0.344136f, -0.714136f, 1.772f);
        return (2 * (-kr + 1), 2 * kb * (-kb + 1) / (kb + kr - 1), 2 * kr * (-kr + 1) / (kb + kr - 1), 2 * (-kb + 1));
    }

    // libheif get_Kr_Kb: fixed values per matrix, derived from the primaries for the chromaticity-derived matrices 12 / 13.
    private static (float Kr, float Kb) LibheifKrKb(int matrix, int primaries)
    {
        if (matrix is 12 or 13)
        {
            // libheif get_colour_primaries: green, blue, red, white (x, y)
            float[]? p = primaries switch
            {
                1 => [0.300f, 0.600f, 0.150f, 0.060f, 0.640f, 0.330f, 0.3127f, 0.3290f],
                4 => [0.21f, 0.71f, 0.14f, 0.08f, 0.67f, 0.33f, 0.310f, 0.316f],
                5 => [0.29f, 0.60f, 0.15f, 0.06f, 0.64f, 0.33f, 0.3127f, 0.3290f],
                6 or 7 => [0.310f, 0.595f, 0.155f, 0.070f, 0.630f, 0.340f, 0.3127f, 0.3290f],
                8 => [0.243f, 0.692f, 0.145f, 0.049f, 0.681f, 0.319f, 0.310f, 0.316f],
                9 => [0.170f, 0.797f, 0.131f, 0.046f, 0.708f, 0.292f, 0.3127f, 0.3290f],
                10 => [0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.333333f, 0.33333f],
                11 => [0.265f, 0.690f, 0.150f, 0.060f, 0.680f, 0.320f, 0.314f, 0.351f],
                12 => [0.265f, 0.690f, 0.150f, 0.060f, 0.680f, 0.320f, 0.3127f, 0.3290f],
                22 => [0.295f, 0.605f, 0.155f, 0.077f, 0.630f, 0.340f, 0.3127f, 0.3290f],
                _ => null,
            };
            float gX = 0, gY = 0, bX = 0, bY = 0, rX = 0, rY = 0, wX = 0, wY = 0;
            if (p != null) (gX, gY, bX, bY, rX, rY, wX, wY) = (p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
            float zr = 1 - (rX + rY), zg = 1 - (gX + gY), zb = 1 - (bX + bY), zw = 1 - (wX + wY);
            float denom = wY * ((rX * ((gY * zb) - (bY * zg))) + (gX * ((bY * zr) - (rY * zb))) + (bX * ((rY * zg) - (gY * zr))));
            if (denom == 0.0f) return (0, 0);
            float kr = rY * ((wX * ((gY * zb) - (bY * zg))) + (wY * ((bX * zg) - (gX * zb))) + (zw * ((gX * bY) - (bX * gY)))) / denom;
            float kb = bY * ((wX * ((rY * zg) - (gY * zr))) + (wY * ((gX * zr) - (rX * zg))) + (zw * ((rX * gY) - (gX * rY)))) / denom;
            return (kr, kb);
        }

        return matrix switch
        {
            1 => (0.2126f, 0.0722f),
            4 => (0.30f, 0.11f),
            5 or 6 => (0.299f, 0.114f),
            7 => (0.212f, 0.087f),
            9 or 10 => (0.2627f, 0.0593f),
            _ => (0, 0),
        };
    }
}
