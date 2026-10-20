namespace SharpImage.Formats.WebP;

/// <summary>VP8LHistogram (src/enc/histogram_enc.h).</summary>
internal sealed class Vp8lHistogram
{
    public readonly uint[] Literal;      // green literal, length prefix and color-cache codes
    public readonly uint[] Red = new uint[Vp8lCommon.NumLiteralCodes];
    public readonly uint[] Blue = new uint[Vp8lCommon.NumLiteralCodes];
    public readonly uint[] Alpha = new uint[Vp8lCommon.NumLiteralCodes];
    public readonly uint[] Distance = new uint[Vp8lCommon.NumDistanceCodes];
    public int PaletteCodeBits;
    public readonly ushort[] TrivialSymbol = new ushort[5];
    public ulong BitCost;
    public readonly ulong[] Costs = new ulong[5];
    public readonly byte[] IsUsed = new byte[5];
    public ushort BinId;

    /// <summary>VP8LAllocateHistogram + VP8LHistogramInit(init_arrays = 0).</summary>
    public Vp8lHistogram(int cacheBits)
    {
        Literal = new uint[Vp8lHistogramUtil.NumCodes(cacheBits)];
        PaletteCodeBits = cacheBits;
        StatsClear();
    }

    /// <summary>HistogramStatsClear.</summary>
    public void StatsClear()
    {
        for (int i = 0; i < 5; ++i)
        {
            TrivialSymbol[i] = Vp8lCommon.NonTrivialSym;
            IsUsed[i] = 1;
        }
        BitCost = 0;
        Array.Clear(Costs);
    }

    /// <summary>memset of the histogram's bytes (struct + its literal codes): every field zero.</summary>
    public void ZeroAll()
    {
        Array.Clear(Literal, 0, Vp8lHistogramUtil.NumCodes(PaletteCodeBits));
        Array.Clear(Red);
        Array.Clear(Blue);
        Array.Clear(Alpha);
        Array.Clear(Distance);
        Array.Clear(TrivialSymbol);
        BitCost = 0;
        Array.Clear(Costs);
        Array.Clear(IsUsed);
        BinId = 0;
    }

    /// <summary>HistogramClear: zero, keep the cache bits, then stats cleared (non-trivial, used).</summary>
    public void Clear()
    {
        int bits = PaletteCodeBits;
        ZeroAll();
        PaletteCodeBits = bits;
        StatsClear();
    }

    /// <summary>VP8LHistogramInit.</summary>
    public void Init(int paletteCodeBits, bool initArrays)
    {
        PaletteCodeBits = paletteCodeBits;
        if (initArrays) Clear();
        else StatsClear();
    }

    /// <summary>HistogramCopy (src, dst share cache bits).</summary>
    public static void Copy(Vp8lHistogram src, Vp8lHistogram dst)
    {
        Array.Copy(src.Literal, dst.Literal, Vp8lHistogramUtil.NumCodes(dst.PaletteCodeBits));
        Array.Copy(src.Red, dst.Red, Vp8lCommon.NumLiteralCodes);
        Array.Copy(src.Blue, dst.Blue, Vp8lCommon.NumLiteralCodes);
        Array.Copy(src.Alpha, dst.Alpha, Vp8lCommon.NumLiteralCodes);
        Array.Copy(src.Distance, dst.Distance, Vp8lCommon.NumDistanceCodes);
        dst.PaletteCodeBits = src.PaletteCodeBits;
        Array.Copy(src.TrivialSymbol, dst.TrivialSymbol, 5);
        dst.BitCost = src.BitCost;
        Array.Copy(src.Costs, dst.Costs, 5);
        Array.Copy(src.IsUsed, dst.IsUsed, 5);
        dst.BinId = src.BinId;
    }

    /// <summary>GetPopulationInfo.</summary>
    public uint[] Population(int index, out int length)
    {
        switch (index)
        {
            case 0: length = Vp8lHistogramUtil.NumCodes(PaletteCodeBits); return Literal;
            case 1: length = Vp8lCommon.NumLiteralCodes; return Red;
            case 2: length = Vp8lCommon.NumLiteralCodes; return Blue;
            case 3: length = Vp8lCommon.NumLiteralCodes; return Alpha;
            default: length = Vp8lCommon.NumDistanceCodes; return Distance;
        }
    }
}

/// <summary>VP8LHistogramSet: 'Home' is the set's own storage, 'Histograms' the (swappable) slot pointers.</summary>
internal sealed class Vp8lHistogramSet
{
    public int Size;
    public readonly int MaxSize;
    public readonly Vp8lHistogram[] Histograms;
    private readonly Vp8lHistogram[] _home;

    /// <summary>VP8LAllocateHistogramSet.</summary>
    public Vp8lHistogramSet(int size, int cacheBits)
    {
        MaxSize = Size = size;
        Histograms = new Vp8lHistogram[size];
        _home = new Vp8lHistogram[size];
        for (int i = 0; i < size; ++i) Histograms[i] = _home[i] = new Vp8lHistogram(cacheBits);
    }

    /// <summary>VP8LHistogramSetClear: every slot back to its own zeroed storage (is_used / trivial / costs 0).</summary>
    public void Clear()
    {
        int cacheBits = Histograms[0].PaletteCodeBits;
        for (int i = 0; i < MaxSize; ++i)
        {
            var h = _home[i];
            h.PaletteCodeBits = cacheBits;
            h.ZeroAll();
            h.PaletteCodeBits = cacheBits;
            Histograms[i] = h;
        }
        Size = MaxSize;
    }

    /// <summary>HistogramSetRemoveHistogram.</summary>
    public void Remove(int i)
    {
        Histograms[i] = Histograms[Size - 1];
        --Size;
    }
}

/// <summary>src/enc/histogram_enc.c, ported line by line.</summary>
internal static class Vp8lHistogramUtil
{
    private const int NumPartitions = 4;
    private const int BinSize = NumPartitions * NumPartitions * NumPartitions;
    private const int MaxHistoGreedy = 100;
    private const int Literal = 0, Red = 1, Blue = 2, Alpha = 3, Distance = 4;

    public static int NumCodes(int paletteCodeBits) =>
        Vp8lCommon.NumLiteralCodes + Vp8lCommon.NumLengthCodes + (paletteCodeBits > 0 ? 1 << paletteCodeBits : 0);

    /// <summary>VP8LHistogramCreate.</summary>
    public static void Create(Vp8lHistogram h, Vp8lBackwardRefs refs, int paletteCodeBits)
    {
        if (paletteCodeBits >= 0) h.PaletteCodeBits = paletteCodeBits;
        h.Clear();
        StoreRefs(refs, null, 0, h);
    }

    private static void AddSinglePixOrCopy(Vp8lHistogram histo, in PixOrCopy v, Func<int, int, int>? distanceModifier,
        int distanceModifierArg0)
    {
        if (v.IsLiteral)
        {
            ++histo.Alpha[v.Literal(3)];
            ++histo.Red[v.Literal(2)];
            ++histo.Literal[v.Literal(1)];
            ++histo.Blue[v.Literal(0)];
        }
        else if (v.IsCacheIdx)
        {
            ++histo.Literal[Vp8lCommon.NumLiteralCodes + Vp8lCommon.NumLengthCodes + (int)v.ArgbOrDistance];
        }
        else
        {
            Vp8lCommon.PrefixEncodeBits(v.Len, out int code, out _);
            ++histo.Literal[Vp8lCommon.NumLiteralCodes + code];
            int dist = (int)v.ArgbOrDistance;
            if (distanceModifier != null) dist = distanceModifier(distanceModifierArg0, dist);
            Vp8lCommon.PrefixEncodeBits(dist, out code, out _);
            ++histo.Distance[code];
        }
    }

    /// <summary>VP8LHistogramStoreRefs.</summary>
    public static void StoreRefs(Vp8lBackwardRefs refs, Func<int, int, int>? distanceModifier, int distanceModifierArg0,
        Vp8lHistogram histo)
    {
        var items = refs.Items;
        for (int i = 0; i < refs.Count; ++i) AddSinglePixOrCopy(histo, items[i], distanceModifier, distanceModifierArg0);
    }

    // ---- entropy ------------------------------------------------------------------------------------------------

    private static ulong BitsEntropyRefine(in Vp8lCommon.BitEntropy entropy)
    {
        ulong mix;
        if (entropy.Nonzeros < 5)
        {
            if (entropy.Nonzeros <= 1) return 0;
            if (entropy.Nonzeros == 2)
                return (ulong)Vp8lCommon.DivRound((long)(99 * ((ulong)entropy.Sum << Vp8lCommon.Log2PrecisionBits) + entropy.Entropy), 100);
            mix = entropy.Nonzeros == 3 ? 950ul : 700ul;
        }
        else
        {
            mix = 627;
        }
        ulong minLimit = (ulong)(2 * entropy.Sum - entropy.MaxVal) << Vp8lCommon.Log2PrecisionBits;
        minLimit = (ulong)Vp8lCommon.DivRound((long)(mix * minLimit + (1000 - mix) * entropy.Entropy), 1000);
        return entropy.Entropy < minLimit ? minLimit : entropy.Entropy;
    }

    /// <summary>VP8LBitsEntropy.</summary>
    public static ulong BitsEntropy(uint[] array, int off, int n)
    {
        var entropy = default(Vp8lCommon.BitEntropy);
        Vp8lCommon.BitsEntropyUnrefined(array, off, n, ref entropy);
        return BitsEntropyRefine(entropy);
    }

    private static ulong InitialHuffmanCost()
    {
        const ulong kHuffmanCodeOfHuffmanCodeSize = Vp8lCommon.CodeLengthCodes * 3;
        return (kHuffmanCodeOfHuffmanCodeSize << Vp8lCommon.Log2PrecisionBits) -
               (ulong)Vp8lCommon.DivRound(91L << Vp8lCommon.Log2PrecisionBits, 10);
    }

    private static ulong FinalHuffmanCost(in Vp8lCommon.Streaks stats)
    {
        ulong retval = InitialHuffmanCost();
        uint retvalExtra = (uint)(stats.Counts0 * 1600 + 240 * stats.S01);
        retvalExtra += (uint)(stats.Counts1 * 2640 + 720 * stats.S11);
        retvalExtra += (uint)(1840 * stats.S00);
        retvalExtra += (uint)(3360 * stats.S10);
        return retval + ((ulong)retvalExtra << (Vp8lCommon.Log2PrecisionBits - 10));
    }

    private static ulong PopulationCost(uint[] population, int length, ref ushort trivialSym, ref byte isUsed, bool want)
    {
        var bitEntropy = default(Vp8lCommon.BitEntropy);
        var stats = default(Vp8lCommon.Streaks);
        Vp8lCommon.GetEntropyUnrefined(population, 0, length, ref bitEntropy, ref stats);
        if (want)
        {
            trivialSym = bitEntropy.Nonzeros == 1 ? (ushort)bitEntropy.NonzeroCode : Vp8lCommon.NonTrivialSym;
            isUsed = (byte)(stats.S10 != 0 || stats.S11 != 0 ? 1 : 0);
        }
        return BitsEntropyRefine(bitEntropy) + FinalHuffmanCost(stats);
    }

    private static ulong GetCombinedEntropy(Vp8lHistogram h1, Vp8lHistogram h2, int index)
    {
        bool isH1Used = h1.IsUsed[index] != 0;
        bool isH2Used = h2.IsUsed[index] != 0;
        bool isTrivial = h1.TrivialSymbol[index] != Vp8lCommon.NonTrivialSym &&
                         h1.TrivialSymbol[index] == h2.TrivialSymbol[index];
        if (isTrivial || !isH1Used || !isH2Used)
        {
            if (isH1Used) return h1.Costs[index];
            return h2.Costs[index];
        }
        var x = h1.Population(index, out int length);
        var y = h2.Population(index, out length);
        var bitEntropy = default(Vp8lCommon.BitEntropy);
        var stats = default(Vp8lCommon.Streaks);
        Vp8lCommon.GetCombinedEntropyUnrefined(x, 0, y, 0, length, ref bitEntropy, ref stats);
        return BitsEntropyRefine(bitEntropy) + FinalHuffmanCost(stats);
    }

    /// <summary>VP8LHistogramEstimateBits.</summary>
    public static ulong EstimateBits(Vp8lHistogram h)
    {
        ulong cost = 0;
        ushort ts = 0;
        byte iu = 0;
        for (int i = 0; i < 5; ++i)
        {
            var population = h.Population(i, out int length);
            cost += PopulationCost(population, length, ref ts, ref iu, false);
        }
        cost += (ulong)(Vp8lCommon.ExtraCost(h.Literal, Vp8lCommon.NumLiteralCodes, Vp8lCommon.NumLengthCodes) +
                        Vp8lCommon.ExtraCost(h.Distance, 0, Vp8lCommon.NumDistanceCodes)) << Vp8lCommon.Log2PrecisionBits;
        return cost;
    }

    private static void SaturateAdd(ulong a, ref long b)
    {
        if (b < 0 || (long)a <= Vp8lCommon.Int64Max - b) b += (long)a;
        else b = Vp8lCommon.Int64Max;
    }

    private static bool GetCombinedHistogramEntropy(Vp8lHistogram a, Vp8lHistogram b, long costThresholdIn,
        out ulong cost, ulong[] costs)
    {
        ulong costThreshold = (ulong)costThresholdIn;
        cost = 0;
        if (costThresholdIn <= 0) return false;
        for (int i = 0; i < 5; ++i)
        {
            costs[i] = GetCombinedEntropy(a, b, i);
            cost += costs[i];
            if (cost >= costThreshold) return false;
        }
        return true;
    }

    /// <summary>HistogramAdd (h2 may be hout).</summary>
    private static void HistogramAdd(Vp8lHistogram h1, Vp8lHistogram h2, Vp8lHistogram hout)
    {
        for (int i = 0; i < 5; ++i)
        {
            var p1 = h1.Population(i, out int length);
            var p2 = h2.Population(i, out length);
            var pout = hout.Population(i, out length);
            if (h2 == hout)
            {
                if (h1.IsUsed[i] != 0)
                {
                    if (hout.IsUsed[i] != 0)
                    {
                        for (int k = 0; k < length; ++k) pout[k] += p1[k];
                    }
                    else
                    {
                        Array.Copy(p1, pout, length);
                    }
                }
            }
            else
            {
                if (h1.IsUsed[i] != 0)
                {
                    if (h2.IsUsed[i] != 0)
                    {
                        for (int k = 0; k < length; ++k) pout[k] = p1[k] + p2[k];
                    }
                    else
                    {
                        Array.Copy(p1, pout, length);
                    }
                }
                else if (h2.IsUsed[i] != 0)
                {
                    Array.Copy(p2, pout, length);
                }
                else
                {
                    Array.Clear(pout, 0, length);
                }
            }
        }
        for (int i = 0; i < 5; ++i)
        {
            hout.TrivialSymbol[i] = h1.TrivialSymbol[i] == h2.TrivialSymbol[i] ? h1.TrivialSymbol[i] : Vp8lCommon.NonTrivialSym;
            hout.IsUsed[i] = (byte)(h1.IsUsed[i] != 0 || h2.IsUsed[i] != 0 ? 1 : 0);
        }
    }

    private static void UpdateHistogramCost(ulong bitCost, ulong[] costs, Vp8lHistogram h)
    {
        h.BitCost = bitCost;
        for (int i = 0; i < 5; ++i) h.Costs[i] = costs[i];
    }

    [ThreadStatic] private static ulong[]? _costsScratch;

    private static bool HistogramAddEval(Vp8lHistogram a, Vp8lHistogram b, Vp8lHistogram output, long costThreshold)
    {
        ulong sumCost = a.BitCost + b.BitCost;
        var costs = _costsScratch ??= new ulong[5];
        SaturateAdd(sumCost, ref costThreshold);
        if (!GetCombinedHistogramEntropy(a, b, costThreshold, out ulong bitCost, costs)) return false;
        HistogramAdd(a, b, output);
        UpdateHistogramCost(bitCost, costs, output);
        return true;
    }

    private static bool HistogramAddThresh(Vp8lHistogram a, Vp8lHistogram b, long costThreshold, out long costOut)
    {
        var costs = _costsScratch ??= new ulong[5];
        SaturateAdd(a.BitCost, ref costThreshold);
        costOut = 0;
        if (!GetCombinedHistogramEntropy(a, b, costThreshold, out ulong cost, costs)) return false;
        costOut = (long)cost - (long)a.BitCost;
        return true;
    }

    // ---- dominant cost range / bins ------------------------------------------------------------------------------

    private static void ComputeHistogramCost(Vp8lHistogram h)
    {
        for (int i = 0; i < 5; ++i)
        {
            var population = h.Population(i, out int length);
            h.Costs[i] = PopulationCost(population, length, ref h.TrivialSymbol[i], ref h.IsUsed[i], true);
        }
        h.BitCost = h.Costs[Literal] + h.Costs[Red] + h.Costs[Blue] + h.Costs[Alpha] + h.Costs[Distance];
    }

    private static int GetBinIdForEntropy(ulong min, ulong max, ulong val)
    {
        ulong range = max - min;
        if (range > 0)
        {
            ulong delta = val - min;
            return (int)((NumPartitions - 1e-6) * delta / range);
        }
        return 0;
    }

    private static void HistogramBuild(int xsize, int histoBits, Vp8lBackwardRefs backwardRefs, Vp8lHistogramSet imageHisto)
    {
        int x = 0, y = 0;
        int histoXsize = Vp8lCommon.SubSampleSize(xsize, histoBits);
        imageHisto.Clear();
        var histograms = imageHisto.Histograms;
        var items = backwardRefs.Items;
        for (int r = 0; r < backwardRefs.Count; ++r)
        {
            ref readonly PixOrCopy v = ref items[r];
            int ix = (y >> histoBits) * histoXsize + (x >> histoBits);
            AddSinglePixOrCopy(histograms[ix], v, null, 0);
            x += v.Len;
            while (x >= xsize)
            {
                x -= xsize;
                ++y;
            }
        }
    }

    // HistogramCopyAndAnalyze: origHisto slots of empty histograms become null.
    private static void HistogramCopyAndAnalyze(Vp8lHistogramSet origHisto, Vp8lHistogram?[] orig, Vp8lHistogramSet imageHisto)
    {
        var histograms = imageHisto.Histograms;
        imageHisto.Size = 0;
        for (int i = 0; i < origHisto.MaxSize; ++i)
        {
            var histo = origHisto.Histograms[i];
            ComputeHistogramCost(histo);
            if (histo.IsUsed[Literal] == 0 && histo.IsUsed[Red] == 0 && histo.IsUsed[Blue] == 0 &&
                histo.IsUsed[Alpha] == 0 && histo.IsUsed[Distance] == 0)
            {
                orig[i] = null;
            }
            else
            {
                orig[i] = histo;
                Vp8lHistogram.Copy(histo, histograms[imageHisto.Size]);
                ++imageHisto.Size;
            }
        }
    }

    private static void HistogramAnalyzeEntropyBin(Vp8lHistogramSet imageHisto, bool lowEffort)
    {
        var histograms = imageHisto.Histograms;
        int histoSize = imageHisto.Size;
        ulong literalMax = 0, literalMin = ulong.MaxValue, redMax = 0, redMin = ulong.MaxValue, blueMax = 0, blueMin = ulong.MaxValue;
        for (int i = 0; i < histoSize; ++i)
        {
            var h = histograms[i];
            if (literalMax < h.Costs[Literal]) literalMax = h.Costs[Literal];
            if (literalMin > h.Costs[Literal]) literalMin = h.Costs[Literal];
            if (redMax < h.Costs[Red]) redMax = h.Costs[Red];
            if (redMin > h.Costs[Red]) redMin = h.Costs[Red];
            if (blueMax < h.Costs[Blue]) blueMax = h.Costs[Blue];
            if (blueMin > h.Costs[Blue]) blueMin = h.Costs[Blue];
        }
        for (int i = 0; i < histoSize; ++i)
        {
            var h = histograms[i];
            int binId = GetBinIdForEntropy(literalMin, literalMax, h.Costs[Literal]);
            if (!lowEffort)
            {
                binId = binId * NumPartitions + GetBinIdForEntropy(redMin, redMax, h.Costs[Red]);
                binId = binId * NumPartitions + GetBinIdForEntropy(blueMin, blueMax, h.Costs[Blue]);
            }
            h.BinId = (ushort)binId;
        }
    }

    private static void HistogramCombineEntropyBin(Vp8lHistogramSet imageHisto, Vp8lHistogram curCombo, int numBins,
        int combineCostFactor, bool lowEffort)
    {
        var histograms = imageHisto.Histograms;
        Span<short> binFirst = stackalloc short[BinSize];
        Span<ushort> binFailures = stackalloc ushort[BinSize];
        for (int i = 0; i < numBins; ++i)
        {
            binFirst[i] = -1;
            binFailures[i] = 0;
        }
        for (int idx = 0; idx < imageHisto.Size;)
        {
            int binId = histograms[idx].BinId;
            int first = binFirst[binId];
            if (first == -1)
            {
                binFirst[binId] = (short)idx;
                ++idx;
            }
            else if (lowEffort)
            {
                HistogramAdd(histograms[idx], histograms[first], histograms[first]);
                imageHisto.Remove(idx);
            }
            else
            {
                ulong bitCost = histograms[idx].BitCost;
                long bitCostThresh = -Vp8lCommon.DivRound((long)bitCost * combineCostFactor, 100);
                if (HistogramAddEval(histograms[first], histograms[idx], curCombo, bitCostThresh))
                {
                    const int maxCombineFailures = 32;
                    bool tryCombine =
                        curCombo.TrivialSymbol[Red] != Vp8lCommon.NonTrivialSym &&
                        curCombo.TrivialSymbol[Blue] != Vp8lCommon.NonTrivialSym &&
                        curCombo.TrivialSymbol[Alpha] != Vp8lCommon.NonTrivialSym;
                    if (!tryCombine)
                    {
                        tryCombine =
                            histograms[idx].TrivialSymbol[Red] == Vp8lCommon.NonTrivialSym ||
                            histograms[idx].TrivialSymbol[Blue] == Vp8lCommon.NonTrivialSym ||
                            histograms[idx].TrivialSymbol[Alpha] == Vp8lCommon.NonTrivialSym;
                        tryCombine &=
                            histograms[first].TrivialSymbol[Red] == Vp8lCommon.NonTrivialSym ||
                            histograms[first].TrivialSymbol[Blue] == Vp8lCommon.NonTrivialSym ||
                            histograms[first].TrivialSymbol[Alpha] == Vp8lCommon.NonTrivialSym;
                    }
                    if (tryCombine || binFailures[binId] >= maxCombineFailures)
                    {
                        (curCombo, histograms[first]) = (histograms[first], curCombo);
                        imageHisto.Remove(idx);
                    }
                    else
                    {
                        ++binFailures[binId];
                        ++idx;
                    }
                }
                else
                {
                    ++idx;
                }
            }
        }
        if (lowEffort)
        {
            for (int idx = 0; idx < imageHisto.Size; ++idx) ComputeHistogramCost(histograms[idx]);
        }
    }

    private static uint MyRand(ref uint seed)
    {
        seed = (uint)(((ulong)seed * 48271u) % 2147483647u);
        return seed;
    }

    // ---- histogram pair queue ------------------------------------------------------------------------------------

    private struct HistogramPair
    {
        public int Idx1;
        public int Idx2;
        public long CostDiff;
        public ulong CostCombo;
        public ulong C0, C1, C2, C3, C4;

        public void SetCosts(ulong[] c) { C0 = c[0]; C1 = c[1]; C2 = c[2]; C3 = c[3]; C4 = c[4]; }
        public void GetCosts(ulong[] c) { c[0] = C0; c[1] = C1; c[2] = C2; c[3] = C3; c[4] = C4; }
    }

    private sealed class HistoQueue
    {
        public HistogramPair[] Queue;
        public int Size;
        public int MaxSize;

        public HistoQueue(int maxSize)
        {
            Size = 0;
            MaxSize = maxSize;
            Queue = new HistogramPair[maxSize + 1];
        }

        public void PopPair(int p)
        {
            Queue[p] = Queue[Size - 1];
            --Size;
        }

        public void UpdateHead(int p)
        {
            if (Queue[p].CostDiff < Queue[0].CostDiff) (Queue[0], Queue[p]) = (Queue[p], Queue[0]);
        }
    }

    private static void HistoQueueFixPair(int badId, int goodId, ref HistogramPair pair)
    {
        if (pair.Idx1 == badId) pair.Idx1 = goodId;
        if (pair.Idx2 == badId) pair.Idx2 = goodId;
        if (pair.Idx1 > pair.Idx2) (pair.Idx1, pair.Idx2) = (pair.Idx2, pair.Idx1);
    }

    private static bool HistoQueueUpdatePair(Vp8lHistogram h1, Vp8lHistogram h2, long costThreshold, ref HistogramPair pair)
    {
        long sumCost = (long)(h1.BitCost + h2.BitCost);
        var costs = _costsScratch ??= new ulong[5];
        SaturateAdd((ulong)sumCost, ref costThreshold);
        if (!GetCombinedHistogramEntropy(h1, h2, costThreshold, out ulong costCombo, costs)) return false;
        pair.CostCombo = costCombo;
        pair.SetCosts(costs);
        pair.CostDiff = (long)pair.CostCombo - sumCost;
        return true;
    }

    private static long HistoQueuePush(HistoQueue histoQueue, Vp8lHistogram[] histograms, int idx1, int idx2, long threshold)
    {
        if (histoQueue.Size == histoQueue.MaxSize) return 0;
        if (idx1 > idx2) (idx1, idx2) = (idx2, idx1);
        var pair = new HistogramPair { Idx1 = idx1, Idx2 = idx2 };
        if (!HistoQueueUpdatePair(histograms[idx1], histograms[idx2], threshold, ref pair)) return 0;
        histoQueue.Queue[histoQueue.Size++] = pair;
        histoQueue.UpdateHead(histoQueue.Size - 1);
        return pair.CostDiff;
    }

    private static void HistogramCombineGreedy(Vp8lHistogramSet imageHisto)
    {
        int imageHistoSize = imageHisto.Size;
        var histograms = imageHisto.Histograms;
        var histoQueue = new HistoQueue(imageHistoSize * imageHistoSize);
        var costs = new ulong[5];
        for (int i = 0; i < imageHistoSize; ++i)
            for (int j = i + 1; j < imageHistoSize; ++j)
                HistoQueuePush(histoQueue, histograms, i, j, 0);

        while (histoQueue.Size > 0)
        {
            int idx1 = histoQueue.Queue[0].Idx1;
            int idx2 = histoQueue.Queue[0].Idx2;
            HistogramAdd(histograms[idx2], histograms[idx1], histograms[idx1]);
            histoQueue.Queue[0].GetCosts(costs);
            UpdateHistogramCost(histoQueue.Queue[0].CostCombo, costs, histograms[idx1]);
            imageHisto.Remove(idx2);
            for (int i = 0; i < histoQueue.Size;)
            {
                ref HistogramPair p = ref histoQueue.Queue[i];
                if (p.Idx1 == idx1 || p.Idx2 == idx1 || p.Idx1 == idx2 || p.Idx2 == idx2)
                {
                    histoQueue.PopPair(i);
                }
                else
                {
                    HistoQueueFixPair(imageHisto.Size, idx2, ref p);
                    histoQueue.UpdateHead(i);
                    ++i;
                }
            }
            for (int i = 0; i < imageHisto.Size; ++i)
            {
                if (i == idx1) continue;
                HistoQueuePush(histoQueue, imageHisto.Histograms, idx1, i, 0);
            }
        }
    }

    private static bool HistogramCombineStochastic(Vp8lHistogramSet imageHisto, int minClusterSize)
    {
        uint seed = 1;
        int triesWithNoSuccess = 0;
        int outerIters = imageHisto.Size;
        int numTriesNoSuccess = outerIters / 2;
        var histograms = imageHisto.Histograms;
        const int kHistoQueueSize = 9;
        var costs = new ulong[5];

        if (imageHisto.Size < minClusterSize) return true;
        var histoQueue = new HistoQueue(kHistoQueueSize);

        for (int iter = 0; iter < outerIters && imageHisto.Size >= minClusterSize &&
                           ++triesWithNoSuccess < numTriesNoSuccess; ++iter)
        {
            long bestCost = histoQueue.Size == 0 ? 0 : histoQueue.Queue[0].CostDiff;
            uint randRange = (uint)((imageHisto.Size - 1) * imageHisto.Size);
            int numTries = imageHisto.Size / 2;

            for (int j = 0; imageHisto.Size >= 2 && j < numTries; ++j)
            {
                uint tmp = MyRand(ref seed) % randRange;
                uint idx1 = tmp / (uint)(imageHisto.Size - 1);
                uint idx2 = tmp % (uint)(imageHisto.Size - 1);
                if (idx2 >= idx1) ++idx2;
                long currCost = HistoQueuePush(histoQueue, histograms, (int)idx1, (int)idx2, bestCost);
                if (currCost < 0)
                {
                    bestCost = currCost;
                    if (histoQueue.Size == histoQueue.MaxSize) break;
                }
            }
            if (histoQueue.Size == 0) continue;

            int bestIdx1 = histoQueue.Queue[0].Idx1;
            int bestIdx2 = histoQueue.Queue[0].Idx2;
            HistogramAdd(histograms[bestIdx2], histograms[bestIdx1], histograms[bestIdx1]);
            histoQueue.Queue[0].GetCosts(costs);
            UpdateHistogramCost(histoQueue.Queue[0].CostCombo, costs, histograms[bestIdx1]);
            imageHisto.Remove(bestIdx2);
            for (int j = 0; j < histoQueue.Size;)
            {
                ref HistogramPair p = ref histoQueue.Queue[j];
                bool isIdx1Best = p.Idx1 == bestIdx1 || p.Idx1 == bestIdx2;
                bool isIdx2Best = p.Idx2 == bestIdx1 || p.Idx2 == bestIdx2;
                if (isIdx1Best && isIdx2Best)
                {
                    histoQueue.PopPair(j);
                    continue;
                }
                if (isIdx1Best || isIdx2Best)
                {
                    HistoQueueFixPair(bestIdx2, bestIdx1, ref p);
                    if (!HistoQueueUpdatePair(histograms[p.Idx1], histograms[p.Idx2], 0, ref p))
                    {
                        histoQueue.PopPair(j);
                        continue;
                    }
                }
                HistoQueueFixPair(imageHisto.Size, bestIdx2, ref p);
                histoQueue.UpdateHead(j);
                ++j;
            }
            triesWithNoSuccess = 0;
        }
        return imageHisto.Size <= minClusterSize;
    }

    private static void HistogramRemap(Vp8lHistogram?[] inHisto, Vp8lHistogramSet output, uint[] symbols)
    {
        var outHisto = output.Histograms;
        int inSize = output.MaxSize;
        int outSize = output.Size;
        if (outSize > 1)
        {
            for (int i = 0; i < inSize; ++i)
            {
                int bestOut = 0;
                long bestBits = Vp8lCommon.Int64Max;
                if (inHisto[i] == null)
                {
                    symbols[i] = symbols[i - 1];
                    continue;
                }
                for (int k = 0; k < outSize; ++k)
                {
                    if (HistogramAddThresh(outHisto[k], inHisto[i]!, bestBits, out long curBits))
                    {
                        bestBits = curBits;
                        bestOut = k;
                    }
                }
                symbols[i] = (uint)bestOut;
            }
        }
        else
        {
            for (int i = 0; i < inSize; ++i) symbols[i] = 0;
        }

        output.Clear();
        output.Size = outSize;
        for (int i = 0; i < inSize; ++i)
        {
            if (inHisto[i] == null) continue;
            int idx = (int)symbols[i];
            HistogramAdd(inHisto[i]!, outHisto[idx], outHisto[idx]);
        }
    }

    private static int GetCombineCostFactor(int histoSize, int quality)
    {
        int combineCostFactor = 16;
        if (quality < 90)
        {
            if (histoSize > 256) combineCostFactor /= 2;
            if (histoSize > 512) combineCostFactor /= 2;
            if (histoSize > 1024) combineCostFactor /= 2;
            if (quality <= 50) combineCostFactor /= 2;
        }
        return combineCostFactor;
    }

    /// <summary>VP8LGetHistoImageSymbols.</summary>
    public static void GetHistoImageSymbols(int xsize, int ysize, Vp8lBackwardRefs refs, int quality, bool lowEffort,
        int histogramBits, int cacheBits, Vp8lHistogramSet imageHisto, Vp8lHistogram tmpHisto, uint[] histogramSymbols)
    {
        int histoXsize = histogramBits != 0 ? Vp8lCommon.SubSampleSize(xsize, histogramBits) : 1;
        int histoYsize = histogramBits != 0 ? Vp8lCommon.SubSampleSize(ysize, histogramBits) : 1;
        int imageHistoRawSize = histoXsize * histoYsize;
        var origHisto = new Vp8lHistogramSet(imageHistoRawSize, cacheBits);
        int entropyCombineNumBins = lowEffort ? NumPartitions : BinSize;

        HistogramBuild(xsize, histogramBits, refs, origHisto);
        var orig = new Vp8lHistogram?[imageHistoRawSize];
        HistogramCopyAndAnalyze(origHisto, orig, imageHisto);
        bool entropyCombine = imageHisto.Size > entropyCombineNumBins * 2 && quality < 100;

        if (entropyCombine)
        {
            int combineCostFactor = GetCombineCostFactor(imageHistoRawSize, quality);
            HistogramAnalyzeEntropyBin(imageHisto, lowEffort);
            HistogramCombineEntropyBin(imageHisto, tmpHisto, entropyCombineNumBins, combineCostFactor, lowEffort);
        }

        if (!lowEffort || !entropyCombine)
        {
            int thresholdSize = (int)(1 + Vp8lCommon.DivRound(quality * quality * quality * (MaxHistoGreedy - 1), 100 * 100 * 100));
            bool doGreedy = HistogramCombineStochastic(imageHisto, thresholdSize);
            if (doGreedy) HistogramCombineGreedy(imageHisto);
        }

        HistogramRemap(orig, imageHisto, histogramSymbols);
    }
}
