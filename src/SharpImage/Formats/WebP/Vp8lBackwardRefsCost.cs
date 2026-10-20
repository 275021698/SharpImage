namespace SharpImage.Formats.WebP;

/// <summary>
/// src/enc/backward_references_cost_enc.c, ported line by line: a cost model from the current references, the
/// interval-based cost manager (at most COST_CACHE_INTERVAL_SIZE_MAX live intervals, the rest serialised into the
/// costs right away) and the backwards trace of the cheapest path. libwebp's interval free-lists only recycle
/// memory; here intervals are plain objects with the same list order and count.
/// </summary>
internal static class Vp8lBackwardRefsCost
{
    private const int ValuesInByte = 256;
    private const int CostCacheIntervalSizeMax = 500;

    private sealed class CostModel
    {
        public readonly uint[] Alpha = new uint[ValuesInByte];
        public readonly uint[] Red = new uint[ValuesInByte];
        public readonly uint[] Blue = new uint[ValuesInByte];
        public readonly uint[] Distance = new uint[Vp8lCommon.NumDistanceCodes];
        public uint[] Literal = [];
    }

    private static void ConvertPopulationCountTableToBitEstimates(int numSymbols, uint[] populationCounts, uint[] output)
    {
        uint sum = 0;
        int nonzeros = 0;
        for (int i = 0; i < numSymbols; ++i)
        {
            sum += populationCounts[i];
            if (populationCounts[i] > 0) ++nonzeros;
        }
        if (nonzeros <= 1)
        {
            Array.Clear(output, 0, numSymbols);
        }
        else
        {
            uint logsum = Vp8lCommon.FastLog2(sum);
            for (int i = 0; i < numSymbols; ++i) output[i] = logsum - Vp8lCommon.FastLog2(populationCounts[i]);
        }
    }

    private static void CostModelBuild(CostModel m, int xsize, int cacheBits, Vp8lBackwardRefs refs)
    {
        var histo = new Vp8lHistogram(cacheBits);
        histo.Init(cacheBits, true);
        Vp8lHistogramUtil.StoreRefs(refs, Vp8lBackwardRefsUtil.DistanceToPlaneCode, xsize, histo);
        int numCodes = Vp8lHistogramUtil.NumCodes(histo.PaletteCodeBits);
        m.Literal = new uint[numCodes];
        ConvertPopulationCountTableToBitEstimates(numCodes, histo.Literal, m.Literal);
        ConvertPopulationCountTableToBitEstimates(ValuesInByte, histo.Red, m.Red);
        ConvertPopulationCountTableToBitEstimates(ValuesInByte, histo.Blue, m.Blue);
        ConvertPopulationCountTableToBitEstimates(ValuesInByte, histo.Alpha, m.Alpha);
        ConvertPopulationCountTableToBitEstimates(Vp8lCommon.NumDistanceCodes, histo.Distance, m.Distance);
    }

    private static long GetLiteralCost(CostModel m, uint v) =>
        (long)m.Alpha[v >> 24] + m.Red[(v >> 16) & 0xff] + m.Literal[(v >> 8) & 0xff] + m.Blue[v & 0xff];

    private static long GetCacheCost(CostModel m, uint idx) => m.Literal[ValuesInByte + Vp8lCommon.NumLengthCodes + (int)idx];

    private static long GetLengthCost(CostModel m, uint length)
    {
        Vp8lCommon.PrefixEncodeBits((int)length, out int code, out int extraBits);
        return (long)m.Literal[ValuesInByte + code] + ((long)extraBits << Vp8lCommon.Log2PrecisionBits);
    }

    private static long GetDistanceCost(CostModel m, uint distance)
    {
        Vp8lCommon.PrefixEncodeBits((int)distance, out int code, out int extraBits);
        return (long)m.Distance[code] + ((long)extraBits << Vp8lCommon.Log2PrecisionBits);
    }

    private static void AddSingleLiteralWithCostModel(uint[] argb, Vp8lColorCache? hashers, CostModel costModel, int idx,
        long prevCost, long[] cost, ushort[] distArray)
    {
        long costVal = prevCost;
        uint color = argb[idx];
        int ix = hashers != null ? hashers.Contains(color) : -1;
        if (ix >= 0)
        {
            costVal += Vp8lCommon.DivRound(GetCacheCost(costModel, (uint)ix) * 68, 100);
        }
        else
        {
            hashers?.Insert(color);
            costVal += Vp8lCommon.DivRound(GetLiteralCost(costModel, color) * 82, 100);
        }
        if (cost[idx] > costVal)
        {
            cost[idx] = costVal;
            distArray[idx] = 1;
        }
    }

    private sealed class CostInterval
    {
        public long Cost;
        public int Start;
        public int End;
        public int Index;
        public CostInterval? Previous;
        public CostInterval? Next;
    }

    private struct CostCacheInterval
    {
        public long Cost;
        public int Start;
        public int End;
    }

    private sealed class CostManager
    {
        public CostInterval? Head;
        public int Count;
        public CostCacheInterval[] CacheIntervals = [];
        public int CacheIntervalsSize;
        public readonly long[] CostCache = new long[Vp8lBackwardRefsUtil.MaxLength];
        public long[] Costs = [];
        public ushort[] DistArray = [];
    }

    private static void CostManagerInit(CostManager manager, ushort[] distArray, int pixCount, CostModel costModel)
    {
        int costCacheSize = pixCount > Vp8lBackwardRefsUtil.MaxLength ? Vp8lBackwardRefsUtil.MaxLength : pixCount;
        manager.Head = null;
        manager.Count = 0;
        manager.DistArray = distArray;
        for (int i = 0; i < costCacheSize; ++i) manager.CostCache[i] = GetLengthCost(costModel, (uint)i);
        manager.CacheIntervalsSize = 1;
        for (int i = 1; i < costCacheSize; ++i)
            if (manager.CostCache[i] != manager.CostCache[i - 1]) ++manager.CacheIntervalsSize;

        manager.CacheIntervals = new CostCacheInterval[manager.CacheIntervalsSize];
        int cur = 0;
        manager.CacheIntervals[cur].Start = 0;
        manager.CacheIntervals[cur].End = 1;
        manager.CacheIntervals[cur].Cost = manager.CostCache[0];
        for (int i = 1; i < costCacheSize; ++i)
        {
            long costVal = manager.CostCache[i];
            if (costVal != manager.CacheIntervals[cur].Cost)
            {
                ++cur;
                manager.CacheIntervals[cur].Start = i;
                manager.CacheIntervals[cur].Cost = costVal;
            }
            manager.CacheIntervals[cur].End = i + 1;
        }
        manager.Costs = new long[pixCount];
        Array.Fill(manager.Costs, Vp8lCommon.Int64Max);
    }

    private static void UpdateCost(CostManager manager, int i, int position, long cost)
    {
        int k = i - position;
        if (manager.Costs[i] > cost)
        {
            manager.Costs[i] = cost;
            manager.DistArray[i] = (ushort)(k + 1);
        }
    }

    private static void UpdateCostPerInterval(CostManager manager, int start, int end, int position, long cost)
    {
        for (int i = start; i < end; ++i) UpdateCost(manager, i, position, cost);
    }

    private static void ConnectIntervals(CostManager manager, CostInterval? prev, CostInterval? next)
    {
        if (prev != null) prev.Next = next;
        else manager.Head = next;
        if (next != null) next.Previous = prev;
    }

    private static void PopInterval(CostManager manager, CostInterval? interval)
    {
        if (interval == null) return;
        ConnectIntervals(manager, interval.Previous, interval.Next);
        --manager.Count;
    }

    private static void UpdateCostAtIndex(CostManager manager, int i, bool doCleanIntervals)
    {
        var current = manager.Head;
        while (current != null && current.Start <= i)
        {
            var next = current.Next;
            if (current.End <= i)
            {
                if (doCleanIntervals) PopInterval(manager, current);
            }
            else
            {
                UpdateCost(manager, i, current.Index, current.Cost);
            }
            current = next;
        }
    }

    private static void PositionOrphanInterval(CostManager manager, CostInterval current, CostInterval? previous)
    {
        if (previous == null) previous = manager.Head;
        while (previous != null && current.Start < previous.Start) previous = previous.Previous;
        while (previous != null && previous.Next != null && previous.Next.Start < current.Start) previous = previous.Next;
        if (previous != null) ConnectIntervals(manager, current, previous.Next);
        else ConnectIntervals(manager, current, manager.Head);
        ConnectIntervals(manager, previous, current);
    }

    private static void InsertInterval(CostManager manager, CostInterval? intervalIn, long cost, int position, int start, int end)
    {
        if (start >= end) return;
        if (manager.Count >= CostCacheIntervalSizeMax)
        {
            UpdateCostPerInterval(manager, start, end, position, cost);
            return;
        }
        var intervalNew = new CostInterval { Cost = cost, Index = position, Start = start, End = end };
        PositionOrphanInterval(manager, intervalNew, intervalIn);
        ++manager.Count;
    }

    private static void PushInterval(CostManager manager, long distanceCost, int position, int len)
    {
        var interval = manager.Head;
        CostInterval? intervalNext;
        var costCacheIntervals = manager.CacheIntervals;
        const int kSkipDistance = 10;

        if (len < kSkipDistance)
        {
            for (int j = position; j < position + len; ++j)
            {
                int k = j - position;
                long costTmp = distanceCost + manager.CostCache[k];
                if (manager.Costs[j] > costTmp)
                {
                    manager.Costs[j] = costTmp;
                    manager.DistArray[j] = (ushort)(k + 1);
                }
            }
            return;
        }

        for (int i = 0; i < manager.CacheIntervalsSize && costCacheIntervals[i].Start < len; ++i)
        {
            int start = position + costCacheIntervals[i].Start;
            int end = position + (costCacheIntervals[i].End > len ? len : costCacheIntervals[i].End);
            long cost = distanceCost + costCacheIntervals[i].Cost;

            for (; interval != null && interval.Start < end; interval = intervalNext)
            {
                intervalNext = interval.Next;
                if (start >= interval.End) continue;

                if (cost >= interval.Cost)
                {
                    int startNew = interval.End;
                    InsertInterval(manager, interval, cost, position, start, interval.Start);
                    start = startNew;
                    if (start >= end) break;
                    continue;
                }

                if (start <= interval.Start)
                {
                    if (interval.End <= end)
                    {
                        PopInterval(manager, interval);
                    }
                    else
                    {
                        interval.Start = end;
                        break;
                    }
                }
                else
                {
                    if (end < interval.End)
                    {
                        int endOriginal = interval.End;
                        interval.End = start;
                        InsertInterval(manager, interval, interval.Cost, interval.Index, end, endOriginal);
                        interval = interval.Next;
                        break;
                    }
                    interval.End = start;
                }
            }
            InsertInterval(manager, interval, cost, position, start, end);
        }
    }

    private static void BackwardReferencesHashChainDistanceOnly(int xsize, int ysize, uint[] argb, int cacheBits,
        Vp8lHashChain hashChain, Vp8lBackwardRefs refs, ushort[] distArray)
    {
        int pixCount = xsize * ysize;
        var costModel = new CostModel();
        var hashers = cacheBits > 0 ? new Vp8lColorCache(cacheBits) : null;
        var costManager = new CostManager();
        int offsetPrev = -1, lenPrev = -1;
        long offsetCost = -1;
        int firstOffsetIsConstant = -1;
        int reach = 0;

        CostModelBuild(costModel, xsize, cacheBits, refs);
        CostManagerInit(costManager, distArray, pixCount, costModel);

        distArray[0] = 0;
        AddSingleLiteralWithCostModel(argb, hashers, costModel, 0, 0, costManager.Costs, distArray);

        for (int i = 1; i < pixCount; ++i)
        {
            long prevCost = costManager.Costs[i - 1];
            int offset = hashChain.FindOffset(i);
            int len = hashChain.FindLength(i);

            AddSingleLiteralWithCostModel(argb, hashers, costModel, i, prevCost, costManager.Costs, distArray);

            if (len >= 2)
            {
                if (offset != offsetPrev)
                {
                    int code = Vp8lBackwardRefsUtil.DistanceToPlaneCode(xsize, offset);
                    offsetCost = GetDistanceCost(costModel, (uint)code);
                    firstOffsetIsConstant = 1;
                    PushInterval(costManager, prevCost + offsetCost, i, len);
                }
                else
                {
                    if (firstOffsetIsConstant != 0)
                    {
                        reach = i - 1 + lenPrev - 1;
                        firstOffsetIsConstant = 0;
                    }
                    if (i + len - 1 > reach)
                    {
                        int offsetJ, lenJ = 0;
                        int j;
                        for (j = i; j <= reach; ++j)
                        {
                            offsetJ = hashChain.FindOffset(j + 1);
                            lenJ = hashChain.FindLength(j + 1);
                            if (offsetJ != offset)
                            {
                                offsetJ = hashChain.FindOffset(j);
                                lenJ = hashChain.FindLength(j);
                                break;
                            }
                        }
                        UpdateCostAtIndex(costManager, j - 1, false);
                        UpdateCostAtIndex(costManager, j, false);
                        PushInterval(costManager, costManager.Costs[j - 1] + offsetCost, j, lenJ);
                        reach = j + lenJ - 1;
                    }
                }
            }

            UpdateCostAtIndex(costManager, i, true);
            offsetPrev = offset;
            lenPrev = len;
        }
    }

    private static void BackwardReferencesHashChainFollowChosenPath(uint[] argb, int cacheBits, ushort[] chosenPath,
        int pathStart, int chosenPathSize, Vp8lHashChain hashChain, Vp8lBackwardRefs refs)
    {
        var hashers = cacheBits > 0 ? new Vp8lColorCache(cacheBits) : null;
        int i = 0;
        refs.Clear();
        for (int ix = 0; ix < chosenPathSize; ++ix)
        {
            int len = chosenPath[pathStart + ix];
            if (len != 1)
            {
                int offset = hashChain.FindOffset(i);
                refs.Add(PixOrCopy.CreateCopy((uint)offset, len));
                if (hashers != null)
                    for (int k = 0; k < len; ++k) hashers.Insert(argb[i + k]);
                i += len;
            }
            else
            {
                PixOrCopy v;
                int idx = hashers != null ? hashers.Contains(argb[i]) : -1;
                if (idx >= 0)
                {
                    v = PixOrCopy.CreateCacheIdx(idx);
                }
                else
                {
                    hashers?.Insert(argb[i]);
                    v = PixOrCopy.CreateLiteral(argb[i]);
                }
                refs.Add(v);
                ++i;
            }
        }
    }

    /// <summary>VP8LBackwardReferencesTraceBackwards.</summary>
    public static void TraceBackwards(int xsize, int ysize, uint[] argb, int cacheBits, Vp8lHashChain hashChain,
        Vp8lBackwardRefs refsSrc, Vp8lBackwardRefs refsDst)
    {
        int distArraySize = xsize * ysize;
        var distArray = new ushort[distArraySize];
        BackwardReferencesHashChainDistanceOnly(xsize, ysize, argb, cacheBits, hashChain, refsSrc, distArray);

        // TraceBackwards: pack the chosen path at the end of dist_array
        int path = distArraySize;
        int cur = distArraySize - 1;
        while (cur >= 0)
        {
            int k = distArray[cur];
            --path;
            distArray[path] = (ushort)k;
            cur -= k;
        }
        BackwardReferencesHashChainFollowChosenPath(argb, cacheBits, distArray, path, distArraySize - path, hashChain, refsDst);
    }
}
