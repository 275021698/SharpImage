namespace SharpImage.Formats.WebP;

/// <summary>src/enc/near_lossless_enc.c (VP8ApplyNearLossless), ported line by line.</summary>
internal static class Vp8lNearLossless
{
    private const int MinDimForNearLossless = 64;

    private static uint FindClosestDiscretized(uint a, int bits)
    {
        uint mask = (1u << bits) - 1;
        uint biased = a + (mask >> 1) + ((a >> bits) & 1);
        if (biased > 0xff) return 0xff;
        return biased & ~mask;
    }

    private static uint ClosestDiscretizedArgb(uint a, int bits) =>
        (FindClosestDiscretized(a >> 24, bits) << 24) |
        (FindClosestDiscretized((a >> 16) & 0xff, bits) << 16) |
        (FindClosestDiscretized((a >> 8) & 0xff, bits) << 8) |
        FindClosestDiscretized(a & 0xff, bits);

    private static bool IsNear(uint a, uint b, int limit)
    {
        for (int k = 0; k < 4; ++k)
        {
            int delta = (int)((a >> (k * 8)) & 0xff) - (int)((b >> (k * 8)) & 0xff);
            if (delta >= limit || delta <= -limit) return false;
        }
        return true;
    }

    private static bool IsSmooth(uint[] buf, int prevRow, int currRow, int nextRow, int ix, int limit) =>
        IsNear(buf[currRow + ix], buf[currRow + ix - 1], limit) &&
        IsNear(buf[currRow + ix], buf[currRow + ix + 1], limit) &&
        IsNear(buf[currRow + ix], buf[prevRow + ix], limit) &&
        IsNear(buf[currRow + ix], buf[nextRow + ix], limit);

    private static void NearLossless(int xsize, int ysize, uint[] src, int stride, int limitBits, uint[] copyBuffer, uint[] dst)
    {
        int limit = 1 << limitBits;
        int prevRow = 0;
        int currRow = xsize;
        int nextRow = 2 * xsize;
        int s = 0, d = 0;
        Array.Copy(src, s, copyBuffer, currRow, xsize);
        Array.Copy(src, s + stride, copyBuffer, nextRow, xsize);
        for (int y = 0; y < ysize; ++y, s += stride, d += xsize)
        {
            if (y == 0 || y == ysize - 1)
            {
                Array.Copy(src, s, dst, d, xsize);
            }
            else
            {
                Array.Copy(src, s + stride, copyBuffer, nextRow, xsize);
                dst[d] = src[s];
                dst[d + xsize - 1] = src[s + xsize - 1];
                for (int x = 1; x < xsize - 1; ++x)
                {
                    if (IsSmooth(copyBuffer, prevRow, currRow, nextRow, x, limit))
                        dst[d + x] = copyBuffer[currRow + x];
                    else
                        dst[d + x] = ClosestDiscretizedArgb(copyBuffer[currRow + x], limitBits);
                }
            }
            int temp = prevRow;
            prevRow = currRow;
            currRow = nextRow;
            nextRow = temp;
        }
    }

    /// <summary>VP8ApplyNearLossless (stride = width).</summary>
    public static void Apply(uint[] argb, int xsize, int ysize, int quality, uint[] argbDst)
    {
        var copyBuffer = new uint[xsize * 3];
        int limitBits = Vp8lCommon.NearLosslessBits(quality);
        if ((xsize < MinDimForNearLossless && ysize < MinDimForNearLossless) || ysize < 3)
        {
            Array.Copy(argb, argbDst, xsize * ysize);
            return;
        }
        NearLossless(xsize, ysize, argb, xsize, limitBits, copyBuffer, argbDst);
        for (int i = limitBits - 1; i != 0; --i) NearLossless(xsize, ysize, argbDst, xsize, i, copyBuffer, argbDst);
    }
}
