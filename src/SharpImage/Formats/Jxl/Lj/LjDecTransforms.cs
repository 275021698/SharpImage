// libjxl 0.12 inverse transforms (dct-inl.h, dec_transforms-inl.h): the recursive scaled (I)DCT of every size, the
// 2x2 / 4x4 / 4x8 / identity / AFV block types and LowestFrequenciesFromDC. The SIMD code runs the same per-column
// arithmetic on several columns at once, so a scalar per-column evaluation with fused multiply-adds where libjxl uses
// MulAdd/NegMulAdd gives identical results.
using System;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjDecTransforms
{
    private const float Sqrt2 = 1.41421356237f;

    // ---------------------------------------------------------------- 1-D kernels (one column)

    /// <summary>IDCT1DImpl&lt;N&gt; on one column.</summary>
    private static void Idct1D(int n, float[] from, int fo, int fs, float[] to, int tOff, int ts, float[] tmp, int tmpOff)
    {
        if (n == 1)
        {
            to[tOff] = from[fo];
            return;
        }

        if (n == 2)
        {
            float a = from[fo], b = from[fo + fs];
            to[tOff] = a + b;
            to[tOff + ts] = a - b;
            return;
        }

        int h = n / 2;
        for (int i = 0; i < h; i++)
        {
            tmp[tmpOff + i] = from[fo + (2 * i * fs)];
        }

        for (int i = h; i < n; i++)
        {
            tmp[tmpOff + i] = from[fo + (((2 * (i - h)) + 1) * fs)];
        }

        Idct1D(h, tmp, tmpOff, 1, tmp, tmpOff, 1, tmp, tmpOff + n);
        int b0 = tmpOff + h;
        for (int i = h - 1; i > 0; i--)
        {
            tmp[b0 + i] = tmp[b0 + i] + tmp[b0 + i - 1];
        }

        tmp[b0] = tmp[b0] * Sqrt2;
        Idct1D(h, tmp, b0, 1, tmp, b0, 1, tmp, tmpOff + n);
        float[] wc = LjDctScales.Wc(n);
        for (int i = 0; i < h; i++)
        {
            float mul = wc[i];
            float in1 = tmp[tmpOff + i], in2 = tmp[b0 + i];
            to[tOff + (i * ts)] = MathF.FusedMultiplyAdd(mul, in2, in1);
            to[tOff + ((n - i - 1) * ts)] = MathF.FusedMultiplyAdd(-mul, in2, in1);
        }
    }

    /// <summary>DCT1DImpl&lt;N&gt; on one contiguous column, in place.</summary>
    private static void Dct1D(int n, float[] mem, int mo, float[] tmp, int tmpOff)
    {
        if (n == 1)
        {
            return;
        }

        if (n == 2)
        {
            float a = mem[mo], b = mem[mo + 1];
            mem[mo] = a + b;
            mem[mo + 1] = a - b;
            return;
        }

        int h = n / 2;
        for (int i = 0; i < h; i++)
        {
            tmp[tmpOff + i] = mem[mo + i] + mem[mo + n - 1 - i];
        }

        Dct1D(h, tmp, tmpOff, tmp, tmpOff + n);
        for (int i = 0; i < h; i++)
        {
            tmp[tmpOff + h + i] = mem[mo + i] - mem[mo + n - 1 - i];
        }

        float[] wc = LjDctScales.Wc(n);
        for (int i = 0; i < h; i++)
        {
            tmp[tmpOff + h + i] = tmp[tmpOff + h + i] * wc[i];
        }

        Dct1D(h, tmp, tmpOff + h, tmp, tmpOff + n);
        int c = tmpOff + h;
        tmp[c] = MathF.FusedMultiplyAdd(tmp[c], Sqrt2, tmp[c + 1]);
        for (int i = 1; i + 1 < h; i++)
        {
            tmp[c + i] = tmp[c + i] + tmp[c + i + 1];
        }

        for (int i = 0; i < h; i++)
        {
            mem[mo + (2 * i)] = tmp[tmpOff + i];
        }

        for (int i = h; i < n; i++)
        {
            mem[mo + (2 * (i - h)) + 1] = tmp[tmpOff + i];
        }
    }

    [ThreadStatic] private static float[]? tmpBuf;
    [ThreadStatic] private static float[]? colBuf;

    private static float[] Tmp => tmpBuf ??= new float[4096];

    private static float[] Col => colBuf ??= new float[512];

    /// <summary>IDCT1D&lt;N, M&gt;: M columns, each an N-point inverse DCT.</summary>
    private static void Idct1DCols(int n, int m, float[] from, int fo, int fs, float[] to, int tOff, int ts)
    {
        float[] tmp = Tmp;
        for (int j = 0; j < m; j++)
        {
            Idct1D(n, from, fo + j, fs, to, tOff + j, ts, tmp, 0);
        }
    }

    /// <summary>DCT1D&lt;N, M&gt;: M columns, each an N-point forward DCT, scaled by 1/N.</summary>
    private static void Dct1DCols(int n, int m, float[] from, int fo, int fs, float[] to, int tOff, int ts)
    {
        float[] tmp = Tmp;
        float[] col = Col;
        float mul = 1.0f / n;
        for (int j = 0; j < m; j++)
        {
            for (int i = 0; i < n; i++)
            {
                col[i] = from[fo + (i * fs) + j];
            }

            Dct1D(n, col, 0, tmp, 0);
            for (int i = 0; i < n; i++)
            {
                to[tOff + (i * ts) + j] = mul * col[i];
            }
        }
    }

    /// <summary>Transpose&lt;N, M&gt;: to(m, n) = from(n, m).</summary>
    private static void Transpose(int n, int m, float[] from, int fo, int fs, float[] to, int tOff, int ts)
    {
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                to[tOff + (j * ts) + i] = from[fo + (i * fs) + j];
            }
        }
    }

    /// <summary>ComputeScaledIDCT&lt;ROWS, COLS&gt;. Overwrites the coefficients.</summary>
    public static void ScaledIdct(int rows, int cols, float[] from, int fo, float[] to, int tOff, int ts, float[] scratch)
    {
        float[] block = scratch;
        if (rows < cols)
        {
            Transpose(rows, cols, from, fo, cols, block, 0, rows);
            Idct1DCols(cols, rows, block, 0, rows, from, fo, rows);
            Transpose(cols, rows, from, fo, rows, block, 0, cols);
            Idct1DCols(rows, cols, block, 0, cols, to, tOff, ts);
        }
        else
        {
            Idct1DCols(cols, rows, from, fo, rows, block, 0, rows);
            Transpose(cols, rows, block, 0, rows, from, fo, cols);
            Idct1DCols(rows, cols, from, fo, cols, to, tOff, ts);
        }
    }

    /// <summary>ComputeScaledDCT&lt;ROWS, COLS&gt;.</summary>
    private static void ScaledDct(int rows, int cols, float[] from, int fo, int fs, float[] to, float[] block)
    {
        if (rows < cols)
        {
            Dct1DCols(rows, cols, from, fo, fs, block, 0, cols);
            Transpose(rows, cols, block, 0, cols, to, 0, rows);
            Dct1DCols(cols, rows, to, 0, rows, block, 0, rows);
            Transpose(cols, rows, block, 0, rows, to, 0, cols);
        }
        else
        {
            Dct1DCols(rows, cols, from, fo, fs, to, 0, cols);
            Transpose(rows, cols, to, 0, cols, block, 0, rows);
            Dct1DCols(cols, rows, block, 0, rows, to, 0, rows);
        }
    }

    // ---------------------------------------------------------------- AC strategies

    public const int Dct = 0, Identity = 1, Dct2X2 = 2, Dct4X4 = 3, Dct16X16 = 4, Dct32X32 = 5, Dct16X8 = 6, Dct8X16 = 7, Dct32X8 = 8,
        Dct8X32 = 9, Dct32X16 = 10, Dct16X32 = 11, Dct4X8 = 12, Dct8X4 = 13, Afv0 = 14, Afv1 = 15, Afv2 = 16, Afv3 = 17, Dct64X64 = 18,
        Dct64X32 = 19, Dct32X64 = 20, Dct128X128 = 21, Dct128X64 = 22, Dct64X128 = 23, Dct256X256 = 24, Dct256X128 = 25, Dct128X256 = 26;

    public const int NumValidStrategies = 27;

    public static readonly byte[] CoveredBlocksX = [1, 1, 1, 1, 2, 4, 1, 2, 1, 4, 2, 4, 1, 1, 1, 1, 1, 1, 8, 4, 8, 16, 8, 16, 32, 16, 32];
    public static readonly byte[] CoveredBlocksY = [1, 1, 1, 1, 2, 4, 2, 1, 4, 1, 4, 2, 1, 1, 1, 1, 1, 1, 8, 8, 4, 16, 16, 8, 32, 32, 16];
    public static readonly byte[] Log2CoveredBlocks = [0, 0, 0, 0, 2, 4, 1, 1, 2, 2, 3, 3, 0, 0, 0, 0, 0, 0, 6, 5, 5, 8, 7, 7, 10, 9, 9];

    /// <summary>kStrategyOrder.</summary>
    public static readonly byte[] StrategyOrder = [0, 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 1, 1, 1, 1, 1, 1, 7, 8, 8, 9, 10, 10, 11, 12, 12];

    /// <summary>kCoeffOrderOffset.</summary>
    public static readonly int[] CoeffOrderOffset =
    [
        0, 1, 2, 3, 4, 5, 6, 10, 14, 18, 34, 50, 66, 68, 70, 72, 76, 80, 84, 92, 100, 108, 172, 236, 300, 332, 364, 396, 652, 908,
        1164, 1292, 1420, 1548, 2572, 3596, 4620, 5132, 5644, 6156,
    ];

    public static int CoeffOrderOffsetOf(int ord, int c) => CoeffOrderOffset[(3 * ord) + c] * 64;

    /// <summary>CoeffOrderAndLut&lt;false&gt; (AcStrategy::ComputeNaturalCoeffOrder).</summary>
    public static void ComputeNaturalCoeffOrder(int raw, uint[] output)
    {
        int cx = CoveredBlocksX[raw], cy = CoveredBlocksY[raw];
        if (cy > cx)
        {
            (cx, cy) = (cy, cx);   // CoefficientLayout: rows = min, columns = max
        }

        int xs = cx / cy;
        int xsm = xs - 1;
        int xss = LjDec.CeilLog2Nonzero(xs);
        int cur = cx * cy;
        for (int i = 0; i < cx * 8; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                int x = j, y = i - j;
                if ((i & 1) != 0)
                {
                    (x, y) = (y, x);
                }

                if ((y & xsm) != 0)
                {
                    continue;
                }

                y >>= xss;
                int val = x < cx && y < cy ? (y * cx) + x : cur++;
                output[val] = (uint)((y * cx * 8) + x);
            }
        }

        for (int ip = (cx * 8) - 1; ip > 0; ip--)
        {
            int i = ip - 1;
            for (int j = 0; j <= i; j++)
            {
                int x = (cx * 8) - 1 - (i - j);
                int y = (cx * 8) - 1 - j;
                if ((i & 1) != 0)
                {
                    (x, y) = (y, x);
                }

                if ((y & xsm) != 0)
                {
                    continue;
                }

                y >>= xss;
                int val = cur++;
                output[val] = (uint)((y * cx * 8) + x);
            }
        }
    }

    private static readonly float[] AfvBasis = BuildAfvBasis();

    private static float[] BuildAfvBasis()
    {
        float[][] b =
        [
            [0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f],
            [0.876902929799142f, 0.2206518106944235f, -0.10140050393753763f, -0.1014005039375375f, 0.2206518106944236f, -0.10140050393753777f,
             -0.10140050393753772f, -0.10140050393753763f, -0.10140050393753758f, -0.10140050393753769f, -0.1014005039375375f,
             -0.10140050393753768f, -0.10140050393753768f, -0.10140050393753759f, -0.10140050393753763f, -0.10140050393753741f],
            [0.0f, 0.0f, 0.40670075830260755f, 0.44444816619734445f, 0.0f, 0.0f, 0.19574399372042936f, 0.2929100136981264f,
             -0.40670075830260716f, -0.19574399372042872f, 0.0f, 0.11379074460448091f, -0.44444816619734384f, -0.29291001369812636f,
             -0.1137907446044814f, 0.0f],
            [0.0f, 0.0f, -0.21255748058288748f, 0.3085497062849767f, 0.0f, 0.4706702258572536f, -0.1621205195722993f, 0.0f,
             -0.21255748058287047f, -0.16212051957228327f, -0.47067022585725277f, -0.1464291867126764f, 0.3085497062849487f, 0.0f,
             -0.14642918671266536f, 0.4251149611657548f],
            [0.0f, -0.7071067811865474f, 0.0f, 0.0f, 0.7071067811865476f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f],
            [-0.4105377591765233f, 0.6235485373547691f, -0.06435071657946274f, -0.06435071657946266f, 0.6235485373547694f,
             -0.06435071657946284f, -0.0643507165794628f, -0.06435071657946274f, -0.06435071657946272f, -0.06435071657946279f,
             -0.06435071657946266f, -0.06435071657946277f, -0.06435071657946277f, -0.06435071657946273f, -0.06435071657946274f,
             -0.0643507165794626f],
            [0.0f, 0.0f, -0.4517556589999482f, 0.15854503551840063f, 0.0f, -0.04038515160822202f, 0.0074182263792423875f,
             0.39351034269210167f, -0.45175565899994635f, 0.007418226379244351f, 0.1107416575309343f, 0.08298163094882051f,
             0.15854503551839705f, 0.3935103426921022f, 0.0829816309488214f, -0.45175565899994796f],
            [0.0f, 0.0f, -0.304684750724869f, 0.5112616136591823f, 0.0f, 0.0f, -0.290480129728998f, -0.06578701549142804f,
             0.304684750724884f, 0.2904801297290076f, 0.0f, -0.23889773523344604f, -0.5112616136592012f, 0.06578701549142545f,
             0.23889773523345467f, 0.0f],
            [0.0f, 0.0f, 0.3017929516615495f, 0.25792362796341184f, 0.0f, 0.16272340142866204f, 0.09520022653475037f, 0.0f,
             0.3017929516615503f, 0.09520022653475055f, -0.16272340142866173f, -0.35312385449816297f, 0.25792362796341295f, 0.0f,
             -0.3531238544981624f, -0.6035859033230976f],
            [0.0f, 0.0f, 0.40824829046386274f, 0.0f, 0.0f, 0.0f, 0.0f, -0.4082482904638628f, -0.4082482904638635f, 0.0f, 0.0f,
             -0.40824829046386296f, 0.0f, 0.4082482904638634f, 0.408248290463863f, 0.0f],
            [0.0f, 0.0f, 0.1747866975480809f, 0.0812611176717539f, 0.0f, 0.0f, -0.3675398009862027f, -0.307882213957909f,
             -0.17478669754808135f, 0.3675398009862011f, 0.0f, 0.4826689115059883f, -0.08126111767175039f, 0.30788221395790305f,
             -0.48266891150598584f, 0.0f],
            [0.0f, 0.0f, -0.21105601049335784f, 0.18567180916109802f, 0.0f, 0.0f, 0.49215859013738733f, -0.38525013709251915f,
             0.21105601049335806f, -0.49215859013738905f, 0.0f, 0.17419412659916217f, -0.18567180916109904f, 0.3852501370925211f,
             -0.1741941265991621f, 0.0f],
            [0.0f, 0.0f, -0.14266084808807264f, -0.3416446842253372f, 0.0f, 0.7367497537172237f, 0.24627107722075148f,
             -0.08574019035519306f, -0.14266084808807344f, 0.24627107722075137f, 0.14883399227113567f, -0.04768680350229251f,
             -0.3416446842253373f, -0.08574019035519267f, -0.047686803502292804f, -0.14266084808807242f],
            [0.0f, 0.0f, -0.13813540350758585f, 0.3302282550303788f, 0.0f, 0.08755115000587084f, -0.07946706605909573f,
             -0.4613374887461511f, -0.13813540350758294f, -0.07946706605910261f, 0.49724647109535086f, 0.12538059448563663f,
             0.3302282550303805f, -0.4613374887461554f, 0.12538059448564315f, -0.13813540350758452f],
            [0.0f, 0.0f, -0.17437602599651067f, 0.0702790691196284f, 0.0f, -0.2921026642334881f, 0.3623817333531167f, 0.0f,
             -0.1743760259965108f, 0.36238173335311646f, 0.29210266423348785f, -0.4326608024727445f, 0.07027906911962818f, 0.0f,
             -0.4326608024727457f, 0.34875205199302267f],
            [0.0f, 0.0f, 0.11354987314994337f, -0.07417504595810355f, 0.0f, 0.19402893032594343f, -0.435190496523228f,
             0.21918684838857466f, 0.11354987314994257f, -0.4351904965232251f, 0.5550443808910661f, -0.25468277124066463f,
             -0.07417504595810233f, 0.2191868483885728f, -0.25468277124066413f, 0.1135498731499429f],
        ];
        var flat = new float[256];
        for (int j = 0; j < 16; j++)
        {
            Array.Copy(b[j], 0, flat, j * 16, 16);
        }

        return flat;
    }

    private static void AfvIdct4x4(float[] coeffs, float[] pixels)
    {
        for (int i = 0; i < 16; i++)
        {
            float pixel = 0;
            for (int j = 0; j < 16; j++)
            {
                pixel = MathF.FusedMultiplyAdd(coeffs[j], AfvBasis[(j * 16) + i], pixel);
            }

            pixels[i] = pixel;
        }
    }

    private static void AfvTransformToPixels(int afvKind, float[] coefficients, int co, float[] pixels, int po, int ps, float[] scratch)
    {
        int afvX = afvKind & 1;
        int afvY = afvKind / 2;
        float block00 = coefficients[co], block01 = coefficients[co + 1], block10 = coefficients[co + 8];
        float dcs0 = (block00 + block10 + block01) * 4.0f;
        float dcs1 = block00 + block10 - block01;
        float dcs2 = block00 - block10;
        var coeff = new float[16];
        coeff[0] = dcs0;
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if (ix == 0 && iy == 0)
                {
                    continue;
                }

                coeff[(iy * 4) + ix] = coefficients[co + (iy * 2 * 8) + (ix * 2)];
            }
        }

        var block = new float[4 * 8];
        AfvIdct4x4(coeff, block);
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                pixels[po + ((iy + (afvY * 4)) * ps) + (afvX * 4) + ix] = block[((afvY == 1 ? 3 - iy : iy) * 4) + (afvX == 1 ? 3 - ix : ix)];
            }
        }

        block[0] = dcs1;
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if (ix == 0 && iy == 0)
                {
                    continue;
                }

                block[(iy * 4) + ix] = coefficients[co + (iy * 2 * 8) + (ix * 2) + 1];
            }
        }

        ScaledIdct(4, 4, block, 0, pixels, po + (afvY * 4 * ps) + (afvX == 1 ? 0 : 4), ps, scratch);
        block[0] = dcs2;
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 8; ix++)
            {
                if (ix == 0 && iy == 0)
                {
                    continue;
                }

                block[(iy * 8) + ix] = coefficients[co + (((1 + (iy * 2)) * 8) + ix)];
            }
        }

        ScaledIdct(4, 8, block, 0, pixels, po + ((afvY == 1 ? 0 : 4) * ps), ps, scratch);
    }

    private static void Idct2TopBlock(int s, float[] block, int bo, float[] output, int oo, int outStride)
    {
        var temp = new float[64];
        int num2x2 = s / 2;
        for (int y = 0; y < num2x2; y++)
        {
            for (int x = 0; x < num2x2; x++)
            {
                float c00 = block[bo + (y * 8) + x];
                float c01 = block[bo + (y * 8) + num2x2 + x];
                float c10 = block[bo + ((y + num2x2) * 8) + x];
                float c11 = block[bo + ((y + num2x2) * 8) + num2x2 + x];
                float r00 = c00 + c01 + c10 + c11;
                float r01 = c00 + c01 - c10 - c11;
                float r10 = c00 - c01 + c10 - c11;
                float r11 = c00 - c01 - c10 + c11;
                temp[(y * 2 * 8) + (x * 2)] = r00;
                temp[(y * 2 * 8) + (x * 2) + 1] = r01;
                temp[(((y * 2) + 1) * 8) + (x * 2)] = r10;
                temp[(((y * 2) + 1) * 8) + (x * 2) + 1] = r11;
            }
        }

        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                output[oo + (y * outStride) + x] = temp[(y * 8) + x];
            }
        }
    }

    /// <summary>TransformToPixels. <paramref name="coefficients"/> starting at <paramref name="co"/> is overwritten.</summary>
    public static void TransformToPixels(int strategy, float[] coefficients, int co, float[] pixels, int po, int ps, float[] scratch)
    {
        switch (strategy)
        {
            case Identity:
            {
                float block00 = coefficients[co], block01 = coefficients[co + 1], block10 = coefficients[co + 8], block11 = coefficients[co + 9];
                Span<float> dcs = stackalloc float[4];
                dcs[0] = block00 + block01 + block10 + block11;
                dcs[1] = block00 + block01 - block10 - block11;
                dcs[2] = block00 - block01 + block10 - block11;
                dcs[3] = block00 - block01 - block10 + block11;
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        float blockDc = dcs[(y * 2) + x];
                        float residualSum = 0;
                        for (int iy = 0; iy < 4; iy++)
                        {
                            for (int ix = 0; ix < 4; ix++)
                            {
                                if (ix == 0 && iy == 0)
                                {
                                    continue;
                                }

                                residualSum += coefficients[co + ((y + (iy * 2)) * 8) + x + (ix * 2)];
                            }
                        }

                        int center = po + (((4 * y) + 1) * ps) + (4 * x) + 1;
                        pixels[center] = blockDc - (residualSum * (1.0f / 16));
                        for (int iy = 0; iy < 4; iy++)
                        {
                            for (int ix = 0; ix < 4; ix++)
                            {
                                if (ix == 1 && iy == 1)
                                {
                                    continue;
                                }

                                pixels[po + (((y * 4) + iy) * ps) + (x * 4) + ix] = coefficients[co + ((y + (iy * 2)) * 8) + x + (ix * 2)] + pixels[center];
                            }
                        }

                        pixels[po + (y * 4 * ps) + (x * 4)] = coefficients[co + ((y + 2) * 8) + x + 2] + pixels[center];
                    }
                }

                break;
            }

            case Dct8X4:
            {
                float block0 = coefficients[co], block1 = coefficients[co + 8];
                float d0 = block0 + block1, d1 = block0 - block1;
                var block = new float[32];
                for (int x = 0; x < 2; x++)
                {
                    block[0] = x == 0 ? d0 : d1;
                    for (int iy = 0; iy < 4; iy++)
                    {
                        for (int ix = 0; ix < 8; ix++)
                        {
                            if (ix == 0 && iy == 0)
                            {
                                continue;
                            }

                            block[(iy * 8) + ix] = coefficients[co + ((x + (iy * 2)) * 8) + ix];
                        }
                    }

                    ScaledIdct(8, 4, block, 0, pixels, po + (x * 4), ps, scratch);
                }

                break;
            }

            case Dct4X8:
            {
                float block0 = coefficients[co], block1 = coefficients[co + 8];
                float d0 = block0 + block1, d1 = block0 - block1;
                var block = new float[32];
                for (int y = 0; y < 2; y++)
                {
                    block[0] = y == 0 ? d0 : d1;
                    for (int iy = 0; iy < 4; iy++)
                    {
                        for (int ix = 0; ix < 8; ix++)
                        {
                            if (ix == 0 && iy == 0)
                            {
                                continue;
                            }

                            block[(iy * 8) + ix] = coefficients[co + ((y + (iy * 2)) * 8) + ix];
                        }
                    }

                    ScaledIdct(4, 8, block, 0, pixels, po + (y * 4 * ps), ps, scratch);
                }

                break;
            }

            case Dct4X4:
            {
                float block00 = coefficients[co], block01 = coefficients[co + 1], block10 = coefficients[co + 8], block11 = coefficients[co + 9];
                var dcs = new float[4];
                dcs[0] = block00 + block01 + block10 + block11;
                dcs[1] = block00 + block01 - block10 - block11;
                dcs[2] = block00 - block01 + block10 - block11;
                dcs[3] = block00 - block01 - block10 + block11;
                var block = new float[16];
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        block[0] = dcs[(y * 2) + x];
                        for (int iy = 0; iy < 4; iy++)
                        {
                            for (int ix = 0; ix < 4; ix++)
                            {
                                if (ix == 0 && iy == 0)
                                {
                                    continue;
                                }

                                block[(iy * 4) + ix] = coefficients[co + ((y + (iy * 2)) * 8) + x + (ix * 2)];
                            }
                        }

                        ScaledIdct(4, 4, block, 0, pixels, po + (y * 4 * ps) + (x * 4), ps, scratch);
                    }
                }

                break;
            }

            case Dct2X2:
            {
                var coeffs = new float[64];
                Array.Copy(coefficients, co, coeffs, 0, 64);
                Idct2TopBlock(2, coeffs, 0, coeffs, 0, 8);
                Idct2TopBlock(4, coeffs, 0, coeffs, 0, 8);
                Idct2TopBlock(8, coeffs, 0, coeffs, 0, 8);
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        pixels[po + (y * ps) + x] = coeffs[(y * 8) + x];
                    }
                }

                break;
            }

            case Afv0:
            case Afv1:
            case Afv2:
            case Afv3:
                AfvTransformToPixels(strategy - Afv0, coefficients, co, pixels, po, ps, scratch);
                break;
            default:
            {
                // DCT ROWSxCOLS: rows = covered_blocks_y * 8, columns = covered_blocks_x * 8.
                int rows = CoveredBlocksY[strategy] * 8, cols = CoveredBlocksX[strategy] * 8;
                ScaledIdct(rows, cols, coefficients, co, pixels, po, ps, scratch);
                break;
            }
        }
    }

    /// <summary>LowestFrequenciesFromDC: the low-frequency coefficients of a multi-block transform from the DC image.</summary>
    public static void LowestFrequenciesFromDc(int strategy, float[] dc, int dcOff, int dcStride, float[] llf, int llfOff, float[] scratch)
    {
        int rows = CoveredBlocksY[strategy], cols = CoveredBlocksX[strategy];
        if (rows * cols == 1 || strategy is Dct2X2 or Dct4X4 or Dct4X8 or Dct8X4 or Afv0 or Afv1 or Afv2 or Afv3 or Identity)
        {
            llf[llfOff] = dc[dcOff];
            return;
        }

        // ReinterpretingDCT<DCT_ROWS = 8 rows, DCT_COLS = 8 cols, LF = N = rows x cols>, output stride max(rows, cols) * 8.
        int outStride = Math.Max(rows, cols) * 8;
        var block = new float[rows * cols];
        var tmpBlock = new float[rows * cols];
        ScaledDct(rows, cols, dc, dcOff, dcStride, block, tmpBlock);
        float[] sr = LjDctScales.Resample(rows, rows * 8);
        float[] sc = LjDctScales.Resample(cols, cols * 8);
        if (rows < cols)
        {
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    llf[llfOff + (y * outStride) + x] = block[(y * cols) + x] * sr[y] * sc[x];
                }
            }
        }
        else
        {
            for (int y = 0; y < cols; y++)
            {
                for (int x = 0; x < rows; x++)
                {
                    llf[llfOff + (y * outStride) + x] = block[(y * rows) + x] * sc[y] * sr[x];
                }
            }
        }
    }
}
