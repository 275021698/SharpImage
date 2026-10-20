namespace SharpImage.Formats.WebP;

/// <summary>src/utils/palette.c, ported line by line.</summary>
internal static class Vp8lPalette
{
    public const int SortedDefault = 0, MinimizeDelta = 1, ModifiedZeng = 2, UnusedPalette = 3, PaletteSortingNum = 4;
    private const int ColorHashSize = Vp8lCommon.MaxPaletteSize * 4;
    private const int ColorHashRightShift = 22;

    public static int SearchColorNoIdx(uint[] sorted, uint color, int numColors)
    {
        int low = 0, hi = numColors;
        if (sorted[low] == color) return low;
        while (true)
        {
            int mid = (low + hi) >> 1;
            if (sorted[mid] == color) return mid;
            if (sorted[mid] < color) low = mid;
            else hi = mid;
        }
    }

    public static void PrepareMapToPalette(uint[] palette, int numColors, uint[] sorted, uint[] idxMap)
    {
        Array.Copy(palette, sorted, numColors);
        Array.Sort(sorted, 0, numColors);
        for (int i = 0; i < numColors; ++i) idxMap[SearchColorNoIdx(sorted, palette[i], numColors)] = (uint)i;
    }

    /// <summary>GetColorPalette: the sorted palette (if &lt;= 256 colors), else MAX_PALETTE_SIZE + 1.</summary>
    public static int GetColorPalette(uint[] argb, int width, int height, int stride, uint[]? palette)
    {
        int numColors = 0;
        var inUse = new byte[ColorHashSize];
        var colors = new uint[ColorHashSize];
        int a = 0;
        uint lastPix = ~argb[0];
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                if (argb[a + x] == lastPix) continue;
                lastPix = argb[a + x];
                int key = Vp8lCommon.HashPix(lastPix, ColorHashRightShift);
                while (true)
                {
                    if (inUse[key] == 0)
                    {
                        colors[key] = lastPix;
                        inUse[key] = 1;
                        ++numColors;
                        if (numColors > Vp8lCommon.MaxPaletteSize) return Vp8lCommon.MaxPaletteSize + 1;
                        break;
                    }
                    if (colors[key] == lastPix) break;
                    ++key;
                    key &= ColorHashSize - 1;
                }
            }
            a += stride;
        }
        if (palette != null)
        {
            numColors = 0;
            for (int i = 0; i < ColorHashSize; ++i)
                if (inUse[i] != 0) palette[numColors++] = colors[i];
            Array.Sort(palette, 0, numColors);
        }
        return numColors;
    }

    private static uint PaletteComponentDistance(uint v) => v <= 128 ? v : 256 - v;

    private static uint PaletteColorDistance(uint col1, uint col2)
    {
        uint diff = Vp8lCommon.SubPixels(col1, col2);
        const int kMoreWeightForRGBThanForAlpha = 9;
        uint score = PaletteComponentDistance(diff & 0xff);
        score += PaletteComponentDistance((diff >> 8) & 0xff);
        score += PaletteComponentDistance((diff >> 16) & 0xff);
        score *= kMoreWeightForRGBThanForAlpha;
        score += PaletteComponentDistance((diff >> 24) & 0xff);
        return score;
    }

    private static bool PaletteHasNonMonotonousDeltas(uint[] palette, int numColors)
    {
        uint predict = 0x000000;
        byte signFound = 0x00;
        for (int i = 0; i < numColors; ++i)
        {
            uint diff = Vp8lCommon.SubPixels(palette[i], predict);
            byte rd = (byte)(diff >> 16);
            byte gd = (byte)(diff >> 8);
            byte bd = (byte)diff;
            if (rd != 0x00) signFound |= (byte)(rd < 0x80 ? 1 : 2);
            if (gd != 0x00) signFound |= (byte)(gd < 0x80 ? 8 : 16);
            if (bd != 0x00) signFound |= (byte)(bd < 0x80 ? 64 : 128);
            predict = palette[i];
        }
        return (signFound & (signFound << 1)) != 0;
    }

    private static void PaletteSortMinimizeDeltas(uint[] paletteSorted, int numColors, uint[] palette)
    {
        uint predict = 0x00000000;
        Array.Copy(paletteSorted, palette, numColors);
        if (!PaletteHasNonMonotonousDeltas(paletteSorted, numColors)) return;
        if (numColors > 17)
        {
            if (palette[0] == 0)
            {
                --numColors;
                (palette[numColors], palette[0]) = (palette[0], palette[numColors]);
            }
        }
        for (int i = 0; i < numColors; ++i)
        {
            int bestIx = i;
            uint bestScore = ~0U;
            for (int k = i; k < numColors; ++k)
            {
                uint curScore = PaletteColorDistance(palette[k], predict);
                if (bestScore > curScore)
                {
                    bestScore = curScore;
                    bestIx = k;
                }
            }
            (palette[bestIx], palette[i]) = (palette[i], palette[bestIx]);
            predict = palette[i];
        }
    }

    private static void CoOccurrenceFindMax(uint[] cooccurrence, int numColors, out byte c1, out byte c2)
    {
        uint bestSum = 0u;
        c1 = 0;
        for (int i = 0; i < numColors; ++i)
        {
            uint sum = 0;
            for (int j = 0; j < numColors; ++j) sum += cooccurrence[i * numColors + j];
            if (sum > bestSum)
            {
                bestSum = sum;
                c1 = (byte)i;
            }
        }
        c2 = 0;
        uint bestCooccurrence = 0u;
        for (int i = 0; i < numColors; ++i)
        {
            if (cooccurrence[c1 * numColors + i] > bestCooccurrence)
            {
                bestCooccurrence = cooccurrence[c1 * numColors + i];
                c2 = (byte)i;
            }
        }
    }

    private static void CoOccurrenceBuild(uint[] argb, int width, int height, int stride, uint[] palette, int numColors,
        uint[] cooccurrence)
    {
        var lines = new uint[2 * width];
        int lineTop = 0;
        int lineCurrent = width;
        int src = 0;
        uint prevPix = ~argb[0];
        uint prevIdx = 0u;
        var idxMap = new uint[Vp8lCommon.MaxPaletteSize];
        var paletteSorted = new uint[Vp8lCommon.MaxPaletteSize];
        PrepareMapToPalette(palette, numColors, paletteSorted, idxMap);
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                uint pix = argb[src + x];
                if (pix != prevPix)
                {
                    prevIdx = idxMap[SearchColorNoIdx(paletteSorted, pix, numColors)];
                    prevPix = pix;
                }
                lines[lineCurrent + x] = prevIdx;
                if (x > 0 && prevIdx != lines[lineCurrent + x - 1])
                {
                    uint leftIdx = lines[lineCurrent + x - 1];
                    ++cooccurrence[prevIdx * numColors + leftIdx];
                    ++cooccurrence[leftIdx * numColors + prevIdx];
                }
                if (y > 0 && prevIdx != lines[lineTop + x])
                {
                    uint topIdx = lines[lineTop + x];
                    ++cooccurrence[prevIdx * numColors + topIdx];
                    ++cooccurrence[topIdx * numColors + prevIdx];
                }
            }
            (lineTop, lineCurrent) = (lineCurrent, lineTop);
            src += stride;
        }
    }

    private struct Sum
    {
        public byte Index;
        public uint Value;
    }

    private static void PaletteSortModifiedZeng(uint[] argb, int width, int height, int stride, uint[] paletteIn,
        int numColors, uint[] palette)
    {
        if (numColors <= 1) return;
        var remapping = new byte[Vp8lCommon.MaxPaletteSize];
        var cooccurrence = new uint[numColors * numColors];
        var sums = new Sum[Vp8lCommon.MaxPaletteSize];
        CoOccurrenceBuild(argb, width, height, stride, paletteIn, numColors, cooccurrence);
        CoOccurrenceFindMax(cooccurrence, numColors, out remapping[0], out remapping[1]);

        uint first = 0, last = 1;
        uint numSums = (uint)numColors - 2;
        if (numSums > 0)
        {
            int bestSum = 0;
            sums[0].Index = 0;
            sums[0].Value = 0;
            for (int i = 0, j = 0; i < numColors; ++i)
            {
                if (i == remapping[0] || i == remapping[1]) continue;
                sums[j].Index = (byte)i;
                sums[j].Value = cooccurrence[i * numColors + remapping[0]] + cooccurrence[i * numColors + remapping[1]];
                if (sums[j].Value > sums[bestSum].Value) bestSum = j;
                ++j;
            }

            while (numSums > 0)
            {
                byte bestIndex = sums[bestSum].Index;
                int delta = 0;
                int n = numColors - (int)numSums;
                for (uint ind = first, j = 0; (ind + j) % numColors != last + 1; ++j)
                {
                    ushort lJ = remapping[(ind + j) % numColors];
                    delta += (n - 1 - 2 * (int)j) * (int)cooccurrence[bestIndex * numColors + lJ];
                }
                if (delta > 0)
                {
                    first = first == 0 ? (uint)numColors - 1 : first - 1;
                    remapping[first] = bestIndex;
                }
                else
                {
                    ++last;
                    remapping[last] = bestIndex;
                }
                sums[bestSum] = sums[numSums - 1];
                --numSums;
                bestSum = 0;
                for (int i = 0; i < numSums; ++i)
                {
                    sums[i].Value += cooccurrence[bestIndex * numColors + sums[i].Index];
                    if (sums[i].Value > sums[bestSum].Value) bestSum = i;
                }
            }
        }
        for (int i = 0; i < numColors; ++i) palette[i] = paletteIn[remapping[(first + i) % numColors]];
    }

    /// <summary>PaletteSort.</summary>
    public static void Sort(int method, uint[] argb, int width, int height, int stride, uint[] paletteSorted, int numColors,
        uint[] palette)
    {
        switch (method)
        {
            case SortedDefault:
                if (paletteSorted[0] == 0 && numColors > 17)
                {
                    Array.Copy(paletteSorted, 1, palette, 0, numColors - 1);
                    palette[numColors - 1] = 0;
                }
                else
                {
                    Array.Copy(paletteSorted, palette, numColors);
                }
                return;
            case MinimizeDelta:
                PaletteSortMinimizeDeltas(paletteSorted, numColors, palette);
                return;
            case ModifiedZeng:
                PaletteSortModifiedZeng(argb, width, height, stride, paletteSorted, numColors, palette);
                return;
        }
    }
}
