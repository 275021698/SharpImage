namespace SharpImage.Formats.WebP;

/// <summary>
/// The ALPH chunk payload of a lossy WebP: libwebp 1.6.0 src/enc/alpha_enc.c with src/utils/quant_levels_utils.c
/// (alpha_quality &lt; 100), src/utils/filters_utils.c and the src/dsp/filters.c predictive filters; the filtered
/// plane is coded as a header-less VP8L stream of green pixels by the ported lossless encoder.
/// </summary>
internal static class WebpAlphaEncoder
{
    // WEBP_FILTER_TYPE
    private const int FilterNone = 0, FilterHorizontal = 1, FilterVertical = 2, FilterGradient = 3, FilterLast = 4;
    private const int FilterBest = 5, FilterFast = 6;
    private const int AlphaNoCompression = 0, AlphaLosslessCompression = 1;
    private const int AlphaPreprocessedLevels = 1;

    /// <summary>CompressAlphaJob / EncodeAlpha: the ALPH chunk data for the picture's alpha plane.</summary>
    public static byte[] Encode(Vp8EncPicture pic, WebpLossyOptions config)
    {
        int effortLevel = config.Method;  // maps to [0..6]
        int filter = config.AlphaFiltering == 0 ? FilterNone
                   : config.AlphaFiltering == 1 ? FilterFast
                   : FilterBest;
        int quality = config.AlphaQuality;
        int method = config.AlphaCompression;
        int width = pic.Width, height = pic.Height;
        bool reduceLevels = quality < 100;

        if (method == AlphaNoCompression)
        {
            // Don't filter, as filtering will make no impact on compressed size.
            filter = FilterNone;
        }

        var quantAlpha = new byte[width * height];
        for (int y = 0; y < height; ++y) Array.Copy(pic.A!, y * pic.AStride, quantAlpha, y * width, width);

        if (reduceLevels)
        {
            // No Quantization required for 'quality = 100'.
            // 16 alpha levels gives quite a low MSE w.r.t original alpha plane hence
            // mapped to moderate quality 70. Hence Quality:[0, 70] -> Levels:[2, 16]
            // and Quality:]70, 100] -> Levels:]16, 256].
            int alphaLevels = quality <= 70 ? 2 + quality / 5 : 16 + (quality - 70) * 8;
            QuantizeLevels(quantAlpha, width, height, alphaLevels);
        }

        return ApplyFiltersAndEncode(quantAlpha, width, height, method, filter, reduceLevels, effortLevel);
    }

    private static int GetNumColors(byte[] data, int width, int height, int stride)
    {
        int colors = 0;
        Span<byte> color = stackalloc byte[256];
        color.Clear();
        for (int j = 0; j < height; ++j)
            for (int i = 0; i < width; ++i)
                color[data[j * stride + i]] = 1;
        for (int j = 0; j < 256; ++j)
            if (color[j] > 0) ++colors;
        return colors;
    }

    private const uint FilterTryNone = 1u << FilterNone;
    private const uint FilterTryAll = (1u << FilterLast) - 1;

    // Given the input 'filter' option, return an OR'd bit-set of filters to try.
    private static uint GetFilterMap(byte[] alpha, int width, int height, int filter, int effortLevel)
    {
        uint bitMap = 0u;
        if (filter == FilterFast)
        {
            // Quick estimate of the best candidate.
            bool tryFilterNone = effortLevel > 3;
            const int kMinColorsForFilterNone = 16;
            const int kMaxColorsForFilterNone = 192;
            int numColors = GetNumColors(alpha, width, height, width);
            // For low number of colors, NONE yields better compression.
            filter = numColors <= kMinColorsForFilterNone ? FilterNone : EstimateBestFilter(alpha, width, height, width);
            bitMap |= 1u << filter;
            // For large number of colors, try FILTER_NONE in addition to the best
            // filter as well.
            if (tryFilterNone || numColors > kMaxColorsForFilterNone) bitMap |= FilterTryNone;
        }
        else if (filter == FilterNone)
        {
            bitMap = FilterTryNone;
        }
        else
        {
            // WEBP_FILTER_BEST -> try all
            bitMap = FilterTryAll;
        }
        return bitMap;
    }

    private static byte[] ApplyFiltersAndEncode(byte[] alpha, int width, int height, int method, int filter,
        bool reduceLevels, int effortLevel)
    {
        byte[]? best = null;
        uint tryMap = GetFilterMap(alpha, width, height, filter, effortLevel);

        if (tryMap != FilterTryNone)
        {
            var filteredAlpha = new byte[width * height];
            for (filter = FilterNone; tryMap != 0; ++filter, tryMap >>= 1)
            {
                if ((tryMap & 1) != 0)
                {
                    byte[] trial = EncodeAlphaInternal(alpha, width, height, method, filter, reduceLevels, effortLevel,
                        filteredAlpha);
                    if (best == null || trial.Length < best.Length) best = trial;
                }
            }
        }
        else
        {
            best = EncodeAlphaInternal(alpha, width, height, method, FilterNone, reduceLevels, effortLevel, null);
        }
        return best!;
    }

    private static byte[] EncodeAlphaInternal(byte[] data, int width, int height, int method, int filter,
        bool reduceLevels, int effortLevel, byte[]? tmpAlpha)
    {
        byte[] alphaSrc;
        int dataSize = width * height;
        byte[]? output = null;

        if (filter != FilterNone)
        {
            ApplyFilter(filter, data, width, height, width, tmpAlpha!);
            alphaSrc = tmpAlpha!;
        }
        else
        {
            alphaSrc = data;
        }

        if (method != AlphaNoCompression)
        {
            output = EncodeLossless(alphaSrc, width, height, effortLevel, !reduceLevels);
            if (output.Length > dataSize)
            {
                // compressed size is larger than source! Revert to uncompressed mode.
                method = AlphaNoCompression;
            }
        }

        int outputSize;
        if (method == AlphaNoCompression)
        {
            output = alphaSrc;
            outputSize = dataSize;
        }
        else
        {
            outputSize = output!.Length;
        }

        // Emit final result.
        byte header = (byte)(method | (filter << 2));
        if (reduceLevels) header |= AlphaPreprocessedLevels << 4;

        var result = new byte[1 + outputSize];
        result[0] = header;
        Array.Copy(output!, 0, result, 1, outputSize);
        return result;
    }

    private static byte[] EncodeLossless(byte[] data, int width, int height, int effortLevel, bool useQuality100)
    {
        // WebPDispatchAlphaToGreen: leave A/R/B channels zero'd.
        var argb = new uint[width * height];
        for (int i = 0; i < argb.Length; ++i) argb[i] = (uint)data[i] << 8;
        var config = new WebpLosslessOptions
        {
            Exact = true,
            Method = effortLevel,  // impact is very small
            // Set a low default quality for encoding alpha. Ensure that Alpha quality at
            // lower methods (3 and below) is less than the threshold for triggering
            // costly 'BackwardReferencesTraceBackwards'.
            // If the alpha quality is set to 100 and the method to 6, allow for a high
            // lossless quality to trigger the cruncher.
            Quality = useQuality100 && effortLevel == 6 ? 100 : 8f * effortLevel,
        };
        var bw = new Vp8lBitWriter((width * height) >> 3);
        Vp8lEncoder.EncodeStreamTo(bw, argb, width, height, config);
        return bw.Finish();
    }

    // ---- filters (src/dsp/filters.c, C versions) ---------------------------------------------------------------------

    private static void PredictLine(byte[] src, int s, byte[] pred, int p, byte[] dst, int d, int length)
    {
        for (int i = 0; i < length; ++i) dst[d + i] = (byte)(src[s + i] - pred[p + i]);
    }

    private static int GradientPredictor(int a, int b, int c)
    {
        int g = a + b - c;
        return (g & ~0xff) == 0 ? g : g < 0 ? 0 : 255;  // clip to 8bit
    }

    private static void ApplyFilter(int filter, byte[] input, int width, int height, int stride, byte[] output)
    {
        int preds = 0, inp = 0, outp = 0;
        // leftmost pixel is the same as input for topmost scanline
        output[0] = input[0];
        PredictLine(input, 1, input, preds, output, 1, width - 1);
        if (filter != FilterVertical) preds += stride;
        inp += stride;
        outp += stride;
        for (int row = 1; row < height; ++row)
        {
            switch (filter)
            {
                case FilterHorizontal:
                    // leftmost pixel: predict from above.
                    PredictLine(input, inp, input, preds - stride, output, outp, 1);
                    PredictLine(input, inp + 1, input, preds, output, outp + 1, width - 1);
                    break;
                case FilterVertical:
                    PredictLine(input, inp, input, preds, output, outp, width);
                    break;
                default: // FilterGradient
                    // leftmost pixel: predict from above.
                    PredictLine(input, inp, input, preds - stride, output, outp, 1);
                    for (int w = 1; w < width; ++w)
                    {
                        int pred = GradientPredictor(input[preds + w - 1], input[preds + w - stride],
                            input[preds + w - stride - 1]);
                        output[outp + w] = (byte)(input[inp + w] - pred);
                    }
                    break;
            }
            preds += stride;
            inp += stride;
            outp += stride;
        }
    }

    // ---- WebPEstimateBestFilter (src/utils/filters_utils.c) ----------------------------------------------------------

    private const int SMax = 16;

    private static int SDiff(int a, int b) => Math.Abs(a - b) >> 4;   // Scoring diff, in [0..SMAX)

    private static int EstimateBestFilter(byte[] data, int width, int height, int stride)
    {
        var bins = new int[FilterLast * SMax];
        // We only sample every other pixels. That's enough.
        for (int j = 2; j < height - 1; j += 2)
        {
            int p = j * stride;
            int mean = data[p];
            for (int i = 2; i < width - 1; i += 2)
            {
                int diff0 = SDiff(data[p + i], mean);
                int diff1 = SDiff(data[p + i], data[p + i - 1]);
                int diff2 = SDiff(data[p + i], data[p + i - width]);
                int gradPred = GradientPredictor(data[p + i - 1], data[p + i - width], data[p + i - width - 1]);
                int diff3 = SDiff(data[p + i], gradPred);
                bins[FilterNone * SMax + diff0] = 1;
                bins[FilterHorizontal * SMax + diff1] = 1;
                bins[FilterVertical * SMax + diff2] = 1;
                bins[FilterGradient * SMax + diff3] = 1;
                mean = (3 * mean + data[p + i] + 2) >> 2;
            }
        }
        int bestFilter = FilterNone;
        int bestScore = 0x7fffffff;
        for (int filter = FilterNone; filter < FilterLast; ++filter)
        {
            int score = 0;
            for (int i = 0; i < SMax; ++i)
                if (bins[filter * SMax + i] > 0) score += i;
            if (score < bestScore)
            {
                bestScore = score;
                bestFilter = filter;
            }
        }
        return bestFilter;
    }

    // ---- QuantizeLevels (src/utils/quant_levels_utils.c) --------------------------------------------------------------

    private const int NumSymbols = 256;
    private const int MaxIter = 6;             // Maximum number of convergence steps.
    private const double ErrorThreshold = 1e-4;    // MSE stopping criterion.

    private static void QuantizeLevels(byte[] data, int width, int height, int numLevels)
    {
        var freq = new int[NumSymbols];
        var qLevel = new int[NumSymbols];
        var invQLevel = new double[NumSymbols];
        int minS = 255, maxS = 0;
        int dataSize = height * width;
        int i, numLevelsIn, iter;
        double lastErr = 1.0e38, err = 0.0;
        double errThreshold = ErrorThreshold * dataSize;

        numLevelsIn = 0;
        for (int n = 0; n < dataSize; ++n)
        {
            numLevelsIn += freq[data[n]] == 0 ? 1 : 0;
            if (minS > data[n]) minS = data[n];
            if (maxS < data[n]) maxS = data[n];
            ++freq[data[n]];
        }

        if (numLevelsIn <= numLevels) return;  // nothing to do!

        // Start with uniformly spread centroids.
        for (i = 0; i < numLevels; ++i) invQLevel[i] = minS + (double)(maxS - minS) * i / (numLevels - 1);

        // Fixed values. Won't be changed.
        qLevel[minS] = 0;
        qLevel[maxS] = numLevels - 1;

        // k-Means iterations.
        var qSum = new double[NumSymbols];
        var qCount = new double[NumSymbols];
        for (iter = 0; iter < MaxIter; ++iter)
        {
            Array.Clear(qSum);
            Array.Clear(qCount);
            int s, slot = 0;

            // Assign classes to representatives.
            for (s = minS; s <= maxS; ++s)
            {
                // Keep track of the nearest neighbour 'slot'
                while (slot < numLevels - 1 && 2 * s > invQLevel[slot] + invQLevel[slot + 1]) ++slot;
                if (freq[s] > 0)
                {
                    qSum[slot] += s * freq[s];
                    qCount[slot] += freq[s];
                }
                qLevel[s] = slot;
            }

            // Assign new representatives to classes.
            if (numLevels > 2)
            {
                for (slot = 1; slot < numLevels - 1; ++slot)
                {
                    double count = qCount[slot];
                    if (count > 0.0) invQLevel[slot] = qSum[slot] / count;
                }
            }

            // Compute convergence error.
            err = 0.0;
            for (s = minS; s <= maxS; ++s)
            {
                double error = s - invQLevel[qLevel[s]];
                err += freq[s] * error * error;
            }

            // Check for convergence: we stop as soon as the error is no
            // longer improving.
            if (lastErr - err < errThreshold) break;
            lastErr = err;
        }

        // Remap the alpha plane to quantized values.
        {
            // double->int rounding operation can be costly, so we do it
            // once for all before remapping. We also perform the data[] -> slot
            // mapping, while at it (avoid one indirection in the final loop).
            var map = new byte[NumSymbols];
            for (int s = minS; s <= maxS; ++s)
            {
                int slot = qLevel[s];
                map[s] = (byte)(invQLevel[slot] + 0.5);
            }
            // Final pass.
            for (int n = 0; n < dataSize; ++n) data[n] = map[data[n]];
        }
    }
}
