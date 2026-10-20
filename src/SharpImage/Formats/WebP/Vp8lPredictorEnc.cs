namespace SharpImage.Formats.WebP;

/// <summary>
/// src/enc/predictor_enc.c, ported line by line. The scratch rows (upper / current, each width + 1 so the top-right
/// of the last pixel is the next row's first) live in one array addressed by offsets that swap like the C pointers;
/// the near-lossless max-diff bytes have their own array.
/// </summary>
internal static class Vp8lPredictorEnc
{
    private const int HistoSize = 4 * 256;
    private static readonly long KSpatialPredictorBias = 15L << Vp8lCommon.Log2PrecisionBits;
    private const int KPredLowEffort = 11;
    private const uint KMaskAlpha = 0xff000000;
    private const int KNumPredModes = 14;

    private static long PredictionCostBias(uint[] counts, int off, ulong weight0, ulong expVal)
    {
        const int significantSymbols = 256 >> 4;
        const ulong expDecayFactor = 6;
        ulong bits = (weight0 * counts[off]) << Vp8lCommon.Log2PrecisionBits;
        expVal <<= Vp8lCommon.Log2PrecisionBits;
        for (int i = 1; i < significantSymbols; ++i)
        {
            bits += (ulong)Vp8lCommon.DivRound((long)(expVal * (counts[off + i] + counts[off + 256 - i])), 100);
            expVal = (ulong)Vp8lCommon.DivRound((long)(expDecayFactor * expVal), 10);
        }
        return -Vp8lCommon.DivRound((long)bits, 10);
    }

    private static long PredictionCostSpatialHistogram(uint[] accumulated, int accOff, uint[] tile, int tileOff, int mode,
        int leftMode, int aboveMode)
    {
        long retval = 0;
        for (int i = 0; i < 4; ++i)
        {
            const ulong kExpValue = 94;
            retval += PredictionCostBias(tile, tileOff + i * 256, 1, kExpValue);
            retval += (long)Vp8lCommon.CombinedShannonEntropy(tile, tileOff + i * 256, accumulated, accOff + i * 256);
        }
        if (mode == leftMode) retval -= KSpatialPredictorBias;
        if (mode == aboveMode) retval -= KSpatialPredictorBias;
        return retval;
    }

    private static void UpdateHisto(uint[] histoArgb, int off, uint argb)
    {
        ++histoArgb[off + 0 * 256 + (int)(argb >> 24)];
        ++histoArgb[off + 1 * 256 + (int)((argb >> 16) & 0xff)];
        ++histoArgb[off + 2 * 256 + (int)((argb >> 8) & 0xff)];
        ++histoArgb[off + 3 * 256 + (int)(argb & 0xff)];
    }

    // PredictBatch: 'current' / 'upper' are rows of 'rows' at the given offsets.
    private static void PredictBatch(int mode, int xStart, int y, int numPixels, uint[] rows, int current, int upper,
        uint[] output, int outPos)
    {
        if (xStart == 0)
        {
            if (y == 0) Vp8lCommon.PredictorSub(0, rows, current, rows, 0, 1, output, outPos);
            else Vp8lCommon.PredictorSub(2, rows, current, rows, upper, 1, output, outPos);
            ++xStart;
            ++outPos;
            --numPixels;
        }
        if (y == 0) Vp8lCommon.PredictorSub(1, rows, current + xStart, rows, 0, numPixels, output, outPos);
        else Vp8lCommon.PredictorSub(mode, rows, current + xStart, rows, upper + xStart, numPixels, output, outPos);
    }

    private static int MaxDiffBetweenPixels(uint p1, uint p2)
    {
        int diffA = Math.Abs((int)(p1 >> 24) - (int)(p2 >> 24));
        int diffR = Math.Abs((int)((p1 >> 16) & 0xff) - (int)((p2 >> 16) & 0xff));
        int diffG = Math.Abs((int)((p1 >> 8) & 0xff) - (int)((p2 >> 8) & 0xff));
        int diffB = Math.Abs((int)(p1 & 0xff) - (int)(p2 & 0xff));
        return Math.Max(Math.Max(diffA, diffR), Math.Max(diffG, diffB));
    }

    private static int MaxDiffAroundPixel(uint current, uint up, uint down, uint left, uint right)
    {
        int diffUp = MaxDiffBetweenPixels(current, up);
        int diffDown = MaxDiffBetweenPixels(current, down);
        int diffLeft = MaxDiffBetweenPixels(current, left);
        int diffRight = MaxDiffBetweenPixels(current, right);
        return Math.Max(Math.Max(diffUp, diffDown), Math.Max(diffLeft, diffRight));
    }

    private static uint AddGreenToBlueAndRed(uint argb)
    {
        uint green = (argb >> 8) & 0xff;
        uint redBlue = argb & 0x00ff00ffu;
        redBlue += (green << 16) | green;
        redBlue &= 0x00ff00ffu;
        return (argb & 0xff00ff00u) | redBlue;
    }

    // MaxDiffsForRow: argb row at 'a' (stride above / below), results at maxDiffs[md + x].
    private static void MaxDiffsForRow(int width, int stride, uint[] argb, int a, byte[] maxDiffs, int md, bool usedSubtractGreen)
    {
        if (width <= 2) return;
        uint current = argb[a];
        uint right = argb[a + 1];
        if (usedSubtractGreen)
        {
            current = AddGreenToBlueAndRed(current);
            right = AddGreenToBlueAndRed(right);
        }
        for (int x = 1; x < width - 1; ++x)
        {
            uint up = argb[a - stride + x];
            uint down = argb[a + stride + x];
            uint left = current;
            current = right;
            right = argb[a + x + 1];
            if (usedSubtractGreen)
            {
                up = AddGreenToBlueAndRed(up);
                down = AddGreenToBlueAndRed(down);
                right = AddGreenToBlueAndRed(right);
            }
            maxDiffs[md + x] = (byte)MaxDiffAroundPixel(current, up, down, left, right);
        }
    }

    private static byte NearLosslessComponent(byte value, byte predict, byte boundary, int quantization)
    {
        int residual = (value - predict) & 0xff;
        int boundaryResidual = (boundary - predict) & 0xff;
        int lower = residual & ~(quantization - 1);
        int upper = lower + quantization;
        int bias = ((boundary - value) & 0xff) < boundaryResidual ? 1 : 0;
        if (residual - lower < upper - residual + bias)
        {
            if (residual > boundaryResidual && lower <= boundaryResidual) return (byte)(lower + (quantization >> 1));
            return (byte)lower;
        }
        if (residual <= boundaryResidual && upper > boundaryResidual) return (byte)(lower + (quantization >> 1));
        return (byte)(upper & 0xff);
    }

    private static byte NearLosslessDiff(byte a, byte b) => (byte)((a - b) & 0xff);

    private static uint NearLossless(uint value, uint predict, int maxQuantization, int maxDiff, bool usedSubtractGreen)
    {
        byte newGreen = 0;
        byte greenDiff = 0;
        byte a, r, g, b;
        if (maxDiff <= 2) return Vp8lCommon.SubPixels(value, predict);
        int quantization = maxQuantization;
        while (quantization >= maxDiff) quantization >>= 1;
        if ((value >> 24) == 0 || (value >> 24) == 0xff)
            a = NearLosslessDiff((byte)(value >> 24), (byte)(predict >> 24));
        else
            a = NearLosslessComponent((byte)(value >> 24), (byte)(predict >> 24), 0xff, quantization);
        g = NearLosslessComponent((byte)(value >> 8), (byte)(predict >> 8), 0xff, quantization);
        if (usedSubtractGreen)
        {
            newGreen = (byte)(((predict >> 8) + g) & 0xff);
            greenDiff = NearLosslessDiff(newGreen, (byte)(value >> 8));
        }
        r = NearLosslessComponent(NearLosslessDiff((byte)(value >> 16), greenDiff), (byte)(predict >> 16),
            (byte)(0xff - newGreen), quantization);
        b = NearLosslessComponent(NearLosslessDiff((byte)value, greenDiff), (byte)predict, (byte)(0xff - newGreen), quantization);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    // GetResidual: rows hold upper (at 'upper') and current (at 'current'); output at outArr[outPos ..].
    private static void GetResidual(int width, int height, uint[] rows, int upper, int current, byte[] maxDiffs, int md,
        int mode, int xStart, int xEnd, int y, int maxQuantization, bool exact, bool usedSubtractGreen, uint[] outArr, int outPos)
    {
        if (exact)
        {
            PredictBatch(mode, xStart, y, xEnd - xStart, rows, current, upper, outArr, outPos);
            return;
        }
        for (int x = xStart; x < xEnd; ++x)
        {
            uint predict;
            uint residual;
            if (y == 0) predict = x == 0 ? Vp8lCommon.ArgbBlack : rows[current + x - 1];
            else if (x == 0) predict = rows[upper + x];
            else predict = Vp8lCommon.Predict(mode, rows, current + x - 1, rows, upper + x);

            if (maxQuantization == 1 || mode == 0 || y == 0 || y == height - 1 || x == 0 || x == width - 1)
            {
                residual = Vp8lCommon.SubPixels(rows[current + x], predict);
            }
            else
            {
                residual = NearLossless(rows[current + x], predict, maxQuantization, maxDiffs[md + x], usedSubtractGreen);
                rows[current + x] = Vp8lCommon.AddPixels(predict, residual);
            }
            if ((rows[current + x] & KMaskAlpha) == 0)
            {
                residual &= KMaskAlpha;
                rows[current + x] = predict & ~KMaskAlpha;
                if (x == 0 && y != 0) rows[upper + width] = rows[current];
            }
            outArr[outPos + x - xStart] = residual;
        }
    }

    private static int HistoArgbOffset(int subsamplingIndex, int mode) => (subsamplingIndex * KNumPredModes + mode) * HistoSize;

    private static void GetBestPredictorForTile(uint[] allArgb, int subsamplingIndex, int tileX, int tileY, int tilesPerRow,
        uint[] allAccumulatedArgb, uint[][] allModes, uint[] allPredHistos)
    {
        int accOff = subsamplingIndex * HistoSize;
        uint[] modes = allModes[subsamplingIndex];
        int predOff = subsamplingIndex * KNumPredModes;
        int leftMode = tileX > 0 ? (int)((modes[tileY * tilesPerRow + tileX - 1] >> 8) & 0xff) : 0xff;
        int aboveMode = tileY > 0 ? (int)((modes[(tileY - 1) * tilesPerRow + tileX] >> 8) & 0xff) : 0xff;
        long bestDiff = Vp8lCommon.Int64Max;
        uint bestMode = 0;
        int bestHisto = HistoArgbOffset(0, 0);
        for (int mode = 0; mode < KNumPredModes; ++mode)
        {
            int histoOff = HistoArgbOffset(subsamplingIndex, mode);
            long curDiff = PredictionCostSpatialHistogram(allAccumulatedArgb, accOff, allArgb, histoOff, mode, leftMode, aboveMode);
            if (curDiff < bestDiff)
            {
                bestHisto = histoOff;
                bestDiff = curDiff;
                bestMode = (uint)mode;
            }
        }
        for (int i = 0; i < HistoSize; ++i) allAccumulatedArgb[accOff + i] += allArgb[bestHisto + i];
        modes[tileY * tilesPerRow + tileX] = Vp8lCommon.ArgbBlack | (bestMode << 8);
        ++allPredHistos[predOff + (int)bestMode];
    }

    private static void ComputeResidualsForTile(int width, int height, int tileX, int tileY, int minBits,
        uint updateUpToIndex, uint[] allArgb, uint[] rows, byte[] maxDiffs, uint[] argb, int maxQuantization, bool exact,
        bool usedSubtractGreen, uint[] residuals)
    {
        int startX = tileX << minBits;
        int startY = tileY << minBits;
        int tileSize = 1 << minBits;
        int maxY = Math.Min(tileSize, height - startY);
        int maxX = Math.Min(tileSize, width - startX);
        int haveLeft = startX > 0 ? 1 : 0;
        int contextStartX = startX - haveLeft;
        int contextWidth = maxX + haveLeft + (maxX < width - startX ? 1 : 0);
        int upperRow = 0;
        int currentRow = width + 1;
        for (int mode = 0; mode < KNumPredModes; ++mode)
        {
            int histoOff = HistoArgbOffset(0, mode);
            if (startY > 0)
                Array.Copy(argb, (startY - 1) * width + contextStartX, rows, currentRow + contextStartX, maxX + haveLeft + 1);
            for (int relativeY = 0; relativeY < maxY; ++relativeY)
            {
                int y = startY + relativeY;
                (upperRow, currentRow) = (currentRow, upperRow);
                Array.Copy(argb, y * width + contextStartX, rows, currentRow + contextStartX,
                    maxX + haveLeft + (y + 1 < height ? 1 : 0));
                if (maxQuantization > 1 && y >= 1 && y + 1 < height)
                    MaxDiffsForRow(contextWidth, width, argb, y * width + contextStartX, maxDiffs, contextStartX, usedSubtractGreen);

                GetResidual(width, height, rows, upperRow, currentRow, maxDiffs, 0, mode, startX, startX + maxX, y,
                    maxQuantization, exact, usedSubtractGreen, residuals, 0);
                for (int relativeX = 0; relativeX < maxX; ++relativeX) UpdateHisto(allArgb, histoOff, residuals[relativeX]);
                if (updateUpToIndex > 0)
                {
                    for (uint subsamplingIndex = 1; subsamplingIndex <= updateUpToIndex; ++subsamplingIndex)
                    {
                        int superOff = HistoArgbOffset((int)subsamplingIndex, mode);
                        for (int relativeX = 0; relativeX < maxX; ++relativeX) UpdateHisto(allArgb, superOff, residuals[relativeX]);
                    }
                }
            }
        }
    }

    private static void CopyImageWithPrediction(int width, int height, int bits, uint[] modes, uint[] rows, byte[] maxDiffs,
        uint[] argb, bool lowEffort, int maxQuantization, bool exact, bool usedSubtractGreen)
    {
        int tilesPerRow = Vp8lCommon.SubSampleSize(width, bits);
        int upperRow = 0;
        int currentRow = width + 1;
        int currentMaxDiffs = 0;
        int lowerMaxDiffs = width;
        for (int y = 0; y < height; ++y)
        {
            (upperRow, currentRow) = (currentRow, upperRow);
            Array.Copy(argb, y * width, rows, currentRow, width + (y + 1 < height ? 1 : 0));

            if (lowEffort)
            {
                PredictBatch(KPredLowEffort, 0, y, width, rows, currentRow, upperRow, argb, y * width);
            }
            else
            {
                if (maxQuantization > 1)
                {
                    (currentMaxDiffs, lowerMaxDiffs) = (lowerMaxDiffs, currentMaxDiffs);
                    if (y + 2 < height)
                        MaxDiffsForRow(width, width, argb, (y + 1) * width, maxDiffs, lowerMaxDiffs, usedSubtractGreen);
                }
                for (int x = 0; x < width;)
                {
                    int mode = (int)((modes[(y >> bits) * tilesPerRow + (x >> bits)] >> 8) & 0xff);
                    int xEnd = x + (1 << bits);
                    if (xEnd > width) xEnd = width;
                    GetResidual(width, height, rows, upperRow, currentRow, maxDiffs, currentMaxDiffs, mode, x, xEnd, y,
                        maxQuantization, exact, usedSubtractGreen, argb, y * width + x);
                    x = xEnd;
                }
            }
        }
    }

    /// <summary>VP8LOptimizeSampling.</summary>
    public static void OptimizeSampling(uint[] image, int fullWidth, int fullHeight, int bits, int maxBits, out int bestBitsOut)
    {
        int width = Vp8lCommon.SubSampleSize(fullWidth, bits);
        int height = Vp8lCommon.SubSampleSize(fullHeight, bits);
        int squareSize;
        int bestBits = bits;
        bestBitsOut = bits;
        while (bestBits < maxBits)
        {
            int newSquareSize = 1 << (bestBits + 1 - bits);
            bool isGood = true;
            squareSize = 1 << (bestBits - bits);
            for (int y = 0; y + squareSize < height; y += newSquareSize)
            {
                if (!image.AsSpan(y * width, width).SequenceEqual(image.AsSpan((y + squareSize) * width, width)))
                {
                    isGood = false;
                    break;
                }
            }
            if (isGood) ++bestBits;
            else break;
        }
        if (bestBits == bits) return;

        while (bestBits > bits)
        {
            bool isGood = true;
            squareSize = 1 << (bestBits - bits);
            for (int y = 0; isGood && y < height; ++y)
            {
                for (int x = 0; isGood && x < width; x += squareSize)
                {
                    for (int i = x + 1; i < Math.Min(x + squareSize, width); ++i)
                    {
                        if (image[y * width + i] != image[y * width + x])
                        {
                            isGood = false;
                            break;
                        }
                    }
                }
            }
            if (isGood) break;
            --bestBits;
        }
        if (bestBits == bits) return;

        int oldWidth = width;
        squareSize = 1 << (bestBits - bits);
        width = Vp8lCommon.SubSampleSize(fullWidth, bestBits);
        height = Vp8lCommon.SubSampleSize(fullHeight, bestBits);
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
                image[y * width + x] = image[squareSize * (y * oldWidth + x)];
        bestBitsOut = bestBits;
    }

    private static void GetBestPredictorsAndSubSampling(int width, int height, int minBits, int maxBits, uint[] rows,
        byte[] maxDiffs, uint[] argb, int maxQuantization, bool exact, bool usedSubtractGreen, uint[][] allModes,
        out int bestBits, out uint[]? bestMode)
    {
        uint tilesPerRow = (uint)Vp8lCommon.SubSampleSize(width, minBits);
        uint tilesPerCol = (uint)Vp8lCommon.SubSampleSize(height, minBits);
        uint maxSubsamplingIndex = (uint)(maxBits - minBits);
        int numArgb = (int)(maxSubsamplingIndex + 1) * KNumPredModes * HistoSize;
        var allArgb = new uint[numArgb];
        var allAccumulatedArgb = new uint[(maxSubsamplingIndex + 1) * HistoSize];
        var allPredHistos = new uint[(maxSubsamplingIndex + 1) * KNumPredModes];
        int maxTileSize = 1 << (int)maxSubsamplingIndex;
        uint updateUpToIndex = (uint)(Math.Max(Math.Min(4, maxBits), minBits) - minBits);
        uint localTileX = 0, localTileY = 0;
        uint maxTileX = 0, maxTileY = 0;
        uint tileX = 0, tileY = 0;
        var residuals = new uint[1 << Vp8lCommon.MaxTransformBits];

        bestBits = 0;
        bestMode = null;

        while (tileY < tilesPerCol)
        {
            ComputeResidualsForTile(width, height, (int)tileX, (int)tileY, minBits, updateUpToIndex, allArgb, rows, maxDiffs,
                argb, maxQuantization, exact, usedSubtractGreen, residuals);

            uint subsamplingIndex = 0;
            while (true)
            {
                uint superTileX = tileX >> (int)subsamplingIndex;
                uint superTileY = tileY >> (int)subsamplingIndex;
                uint superTilesPerRow = (uint)Vp8lCommon.SubSampleSize(width, minBits + (int)subsamplingIndex);
                GetBestPredictorForTile(allArgb, (int)subsamplingIndex, (int)superTileX, (int)superTileY, (int)superTilesPerRow,
                    allAccumulatedArgb, allModes, allPredHistos);
                if (subsamplingIndex == maxSubsamplingIndex) break;

                ++subsamplingIndex;
                if (subsamplingIndex > updateUpToIndex && subsamplingIndex <= maxSubsamplingIndex)
                {
                    int src = HistoArgbOffset((int)subsamplingIndex - 1, 0);
                    int dst = HistoArgbOffset((int)subsamplingIndex, 0);
                    for (int i = 0; i < HistoSize * KNumPredModes; ++i) allArgb[dst + i] += allArgb[src + i];
                }
                if (!((tileX == tilesPerRow - 1 || (localTileX + 1) % (1u << (int)subsamplingIndex) == 0) &&
                      (tileY == tilesPerCol - 1 || (localTileY + 1) % (1u << (int)subsamplingIndex) == 0)))
                {
                    --subsamplingIndex;
                    break;
                }
            }
            Array.Clear(allArgb, 0, HistoSize * KNumPredModes * (int)(subsamplingIndex + 1));

            if (subsamplingIndex == maxSubsamplingIndex)
            {
                if (tileX == tilesPerRow - 1)
                {
                    maxTileX = 0;
                    ++maxTileY;
                }
                else
                {
                    ++maxTileX;
                }
                localTileX = 0;
                localTileY = 0;
            }
            else
            {
                uint coordX = localTileX >> (int)subsamplingIndex;
                uint coordY = localTileY >> (int)subsamplingIndex;
                if (tileX == tilesPerRow - 1 && coordX % 2 == 0)
                {
                    ++coordY;
                }
                else
                {
                    if (coordX % 2 == 0)
                    {
                        ++coordX;
                    }
                    else
                    {
                        ++coordY;
                        --coordX;
                    }
                }
                localTileX = coordX << (int)subsamplingIndex;
                localTileY = coordY << (int)subsamplingIndex;
            }
            tileX = maxTileX * (uint)maxTileSize + localTileX;
            tileY = maxTileY * (uint)maxTileSize + localTileY;
        }

        long bestCost = Vp8lCommon.Int64Max;
        for (uint subsamplingIndex = 0; subsamplingIndex <= maxSubsamplingIndex; ++subsamplingIndex)
        {
            int accOff = (int)subsamplingIndex * HistoSize;
            long cost = (long)Vp8lCommon.ShannonEntropy(allPredHistos, (int)subsamplingIndex * KNumPredModes, KNumPredModes);
            for (int plane = 0; plane < 4; ++plane)
                cost += (long)Vp8lCommon.ShannonEntropy(allAccumulatedArgb, accOff + plane * 256, 256);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestBits = minBits + (int)subsamplingIndex;
                bestMode = allModes[subsamplingIndex];
            }
        }

        OptimizeSampling(bestMode!, width, height, bestBits, Vp8lCommon.MaxTransformBits, out bestBits);
    }

    /// <summary>VP8LResidualImage: picks the predictors (unless low effort) and turns argb into residuals.
    /// rows / maxDiffs are the argb_scratch.</summary>
    public static void ResidualImage(int width, int height, int minBits, int maxBits, bool lowEffort, uint[] argb,
        uint[] rows, byte[] maxDiffs, uint[] image, int nearLosslessQuality, bool exact, bool usedSubtractGreen,
        out int bestBits)
    {
        int maxQuantization = 1 << Vp8lCommon.NearLosslessBits(nearLosslessQuality);
        if (lowEffort)
        {
            int tilesPerRow = Vp8lCommon.SubSampleSize(width, maxBits);
            int tilesPerCol = Vp8lCommon.SubSampleSize(height, maxBits);
            for (int i = 0; i < tilesPerRow * tilesPerCol; ++i) image[i] = Vp8lCommon.ArgbBlack | (KPredLowEffort << 8);
            bestBits = maxBits;
        }
        else
        {
            var modes = new uint[maxBits - minBits + 1][];
            for (int bits = minBits; bits <= maxBits; ++bits)
                modes[bits - minBits] = new uint[Vp8lCommon.SubSampleSize(width, bits) * Vp8lCommon.SubSampleSize(height, bits)];
            GetBestPredictorsAndSubSampling(width, height, minBits, maxBits, rows, maxDiffs, argb, maxQuantization, exact,
                usedSubtractGreen, modes, out bestBits, out uint[]? bestMode);
            Array.Copy(bestMode!, image, Vp8lCommon.SubSampleSize(width, bestBits) * Vp8lCommon.SubSampleSize(height, bestBits));
        }
        CopyImageWithPrediction(width, height, bestBits, image, rows, maxDiffs, argb, lowEffort, maxQuantization, exact,
            usedSubtractGreen);
    }

    // ---- color space transform ------------------------------------------------------------------------------------

    private struct Multipliers
    {
        public byte GreenToRed, GreenToBlue, RedToBlue;
    }

    private static Multipliers ColorCodeToMultipliers(uint colorCode) => new()
    {
        GreenToRed = (byte)(colorCode & 0xff),
        GreenToBlue = (byte)((colorCode >> 8) & 0xff),
        RedToBlue = (byte)((colorCode >> 16) & 0xff),
    };

    private static uint MultipliersToColorCode(in Multipliers m) =>
        0xff000000u | ((uint)m.RedToBlue << 16) | ((uint)m.GreenToBlue << 8) | m.GreenToRed;

    private static long PredictionCostCrossColor(uint[] accumulated, uint[] counts)
    {
        const ulong kExpValue = 240;
        return (long)Vp8lCommon.CombinedShannonEntropy(counts, 0, accumulated, 0) + PredictionCostBias(counts, 0, 3, kExpValue);
    }

    [ThreadStatic] private static uint[]? _histo256;

    private static long GetPredictionCostCrossColorRed(uint[] argb, int off, int stride, int tileWidth, int tileHeight,
        Multipliers prevX, Multipliers prevY, int greenToRed, uint[] accumulatedRedHisto)
    {
        var histo = _histo256 ??= new uint[256];
        Array.Clear(histo);
        Vp8lCommon.CollectColorRedTransforms(argb, off, stride, tileWidth, tileHeight, greenToRed, histo);
        long curDiff = PredictionCostCrossColor(accumulatedRedHisto, histo);
        if ((byte)greenToRed == prevX.GreenToRed) curDiff -= 3L << Vp8lCommon.Log2PrecisionBits;
        if ((byte)greenToRed == prevY.GreenToRed) curDiff -= 3L << Vp8lCommon.Log2PrecisionBits;
        if (greenToRed == 0) curDiff -= 3L << Vp8lCommon.Log2PrecisionBits;
        return curDiff;
    }

    private static void GetBestGreenToRed(uint[] argb, int off, int stride, int tileWidth, int tileHeight, Multipliers prevX,
        Multipliers prevY, int quality, uint[] accumulatedRedHisto, ref Multipliers bestTx)
    {
        int kMaxIters = 4 + ((7 * quality) >> 8);
        int greenToRedBest = 0;
        long bestDiff = GetPredictionCostCrossColorRed(argb, off, stride, tileWidth, tileHeight, prevX, prevY, greenToRedBest,
            accumulatedRedHisto);
        for (int iter = 0; iter < kMaxIters; ++iter)
        {
            int delta = 32 >> iter;
            for (int offset = -delta; offset <= delta; offset += 2 * delta)
            {
                int greenToRedCur = offset + greenToRedBest;
                long curDiff = GetPredictionCostCrossColorRed(argb, off, stride, tileWidth, tileHeight, prevX, prevY,
                    greenToRedCur, accumulatedRedHisto);
                if (curDiff < bestDiff)
                {
                    bestDiff = curDiff;
                    greenToRedBest = greenToRedCur;
                }
            }
        }
        bestTx.GreenToRed = (byte)(greenToRedBest & 0xff);
    }

    private static long GetPredictionCostCrossColorBlue(uint[] argb, int off, int stride, int tileWidth, int tileHeight,
        Multipliers prevX, Multipliers prevY, int greenToBlue, int redToBlue, uint[] accumulatedBlueHisto)
    {
        var histo = _histo256 ??= new uint[256];
        Array.Clear(histo);
        Vp8lCommon.CollectColorBlueTransforms(argb, off, stride, tileWidth, tileHeight, greenToBlue, redToBlue, histo);
        long curDiff = PredictionCostCrossColor(accumulatedBlueHisto, histo);
        const long b3 = 3L << Vp8lCommon.Log2PrecisionBits;
        if ((byte)greenToBlue == prevX.GreenToBlue) curDiff -= b3;
        if ((byte)greenToBlue == prevY.GreenToBlue) curDiff -= b3;
        if ((byte)redToBlue == prevX.RedToBlue) curDiff -= b3;
        if ((byte)redToBlue == prevY.RedToBlue) curDiff -= b3;
        if (greenToBlue == 0) curDiff -= b3;
        if (redToBlue == 0) curDiff -= b3;
        return curDiff;
    }

    private static readonly sbyte[,] GreenRedToBlueOffset = { { 0, -1 }, { 0, 1 }, { -1, 0 }, { 1, 0 }, { -1, -1 }, { -1, 1 }, { 1, -1 }, { 1, 1 } };
    private static readonly sbyte[] GreenRedToBlueDeltaLut = [16, 16, 8, 4, 2, 2, 2];

    private static void GetBestGreenRedToBlue(uint[] argb, int off, int stride, int tileWidth, int tileHeight,
        Multipliers prevX, Multipliers prevY, int quality, uint[] accumulatedBlueHisto, ref Multipliers bestTx)
    {
        const int kGreenRedToBlueNumAxis = 8, kGreenRedToBlueMaxIters = 7;
        int iters = quality < 25 ? 1 : quality > 50 ? kGreenRedToBlueMaxIters : 4;
        int greenToBlueBest = 0;
        int redToBlueBest = 0;
        long bestDiff = GetPredictionCostCrossColorBlue(argb, off, stride, tileWidth, tileHeight, prevX, prevY,
            greenToBlueBest, redToBlueBest, accumulatedBlueHisto);
        for (int iter = 0; iter < iters; ++iter)
        {
            int delta = GreenRedToBlueDeltaLut[iter];
            for (int axis = 0; axis < kGreenRedToBlueNumAxis; ++axis)
            {
                int greenToBlueCur = GreenRedToBlueOffset[axis, 0] * delta + greenToBlueBest;
                int redToBlueCur = GreenRedToBlueOffset[axis, 1] * delta + redToBlueBest;
                long curDiff = GetPredictionCostCrossColorBlue(argb, off, stride, tileWidth, tileHeight, prevX, prevY,
                    greenToBlueCur, redToBlueCur, accumulatedBlueHisto);
                if (curDiff < bestDiff)
                {
                    bestDiff = curDiff;
                    greenToBlueBest = greenToBlueCur;
                    redToBlueBest = redToBlueCur;
                }
                if (quality < 25 && iter == 4) break;
            }
            if (delta == 2 && greenToBlueBest == 0 && redToBlueBest == 0) break;
        }
        bestTx.GreenToBlue = (byte)(greenToBlueBest & 0xff);
        bestTx.RedToBlue = (byte)(redToBlueBest & 0xff);
    }

    private static Multipliers GetBestColorTransformForTile(int tileX, int tileY, int bits, Multipliers prevX,
        Multipliers prevY, int quality, int xsize, int ysize, uint[] accumulatedRedHisto, uint[] accumulatedBlueHisto,
        uint[] argb)
    {
        int maxTileSize = 1 << bits;
        int tileYOffset = tileY * maxTileSize;
        int tileXOffset = tileX * maxTileSize;
        int allXMax = Math.Min(tileXOffset + maxTileSize, xsize);
        int allYMax = Math.Min(tileYOffset + maxTileSize, ysize);
        int tileWidth = allXMax - tileXOffset;
        int tileHeight = allYMax - tileYOffset;
        int tileArgb = tileYOffset * xsize + tileXOffset;
        var bestTx = default(Multipliers);
        GetBestGreenToRed(argb, tileArgb, xsize, tileWidth, tileHeight, prevX, prevY, quality, accumulatedRedHisto, ref bestTx);
        GetBestGreenRedToBlue(argb, tileArgb, xsize, tileWidth, tileHeight, prevX, prevY, quality, accumulatedBlueHisto, ref bestTx);
        return bestTx;
    }

    private static void CopyTileWithColorTransform(int xsize, int ysize, int tileX, int tileY, int maxTileSize,
        Multipliers colorTransform, uint[] argb)
    {
        int xscan = Math.Min(maxTileSize, xsize - tileX);
        int yscan = Math.Min(maxTileSize, ysize - tileY);
        int a = tileY * xsize + tileX;
        while (yscan-- > 0)
        {
            Vp8lCommon.TransformColor(colorTransform.GreenToRed, colorTransform.GreenToBlue, colorTransform.RedToBlue, argb, a, xscan);
            a += xsize;
        }
    }

    /// <summary>VP8LColorSpaceTransform.</summary>
    public static void ColorSpaceTransform(int width, int height, int bits, int quality, uint[] argb, uint[] image, out int bestBits)
    {
        int maxTileSize = 1 << bits;
        int tileXsize = Vp8lCommon.SubSampleSize(width, bits);
        int tileYsize = Vp8lCommon.SubSampleSize(height, bits);
        var accumulatedRedHisto = new uint[256];
        var accumulatedBlueHisto = new uint[256];
        var prevY = default(Multipliers);
        var prevX = default(Multipliers);
        for (int tileY = 0; tileY < tileYsize; ++tileY)
        {
            for (int tileX = 0; tileX < tileXsize; ++tileX)
            {
                int tileXOffset = tileX * maxTileSize;
                int tileYOffset = tileY * maxTileSize;
                int allXMax = Math.Min(tileXOffset + maxTileSize, width);
                int allYMax = Math.Min(tileYOffset + maxTileSize, height);
                int offset = tileY * tileXsize + tileX;
                if (tileY != 0) prevY = ColorCodeToMultipliers(image[offset - tileXsize]);
                prevX = GetBestColorTransformForTile(tileX, tileY, bits, prevX, prevY, quality, width, height,
                    accumulatedRedHisto, accumulatedBlueHisto, argb);
                image[offset] = MultipliersToColorCode(prevX);
                CopyTileWithColorTransform(width, height, tileXOffset, tileYOffset, maxTileSize, prevX, argb);

                for (int y = tileYOffset; y < allYMax; ++y)
                {
                    int ix = y * width + tileXOffset;
                    int ixEnd = ix + allXMax - tileXOffset;
                    for (; ix < ixEnd; ++ix)
                    {
                        uint pix = argb[ix];
                        if (ix >= 2 && pix == argb[ix - 2] && pix == argb[ix - 1]) continue;
                        if (ix >= width + 2 && argb[ix - 2] == argb[ix - width - 2] && argb[ix - 1] == argb[ix - width - 1] &&
                            pix == argb[ix - width])
                            continue;
                        ++accumulatedRedHisto[(pix >> 16) & 0xff];
                        ++accumulatedBlueHisto[pix & 0xff];
                    }
                }
            }
        }
        OptimizeSampling(image, width, height, bits, Vp8lCommon.MaxTransformBits, out bestBits);
    }
}
