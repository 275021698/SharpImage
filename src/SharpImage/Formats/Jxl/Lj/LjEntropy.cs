// libjxl 0.12 entropy-coder encoder (enc_ans.cc, enc_cluster.cc, enc_context_map.cc, enc_huffman.cc,
// enc_huffman_tree.cc, the RLE path of enc_lz77.cc), ported line by line so the histograms, clustering and token
// streams are bit-identical to cjxl's. Float arithmetic follows the AVX2 build cjxl runs on x64 (8-lane sums,
// fused multiply-add) where libjxl dispatches dynamically, and the SSE2 baseline where it dispatches statically.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal struct LjToken
{
    public bool IsLz77Length;
    public uint Context;
    public uint Value;

    public LjToken(uint context, uint value)
    {
        IsLz77Length = false;
        Context = context;
        Value = value;
    }
}

internal readonly struct LjHybridUint
{
    public readonly uint SplitExponent, SplitToken, MsbInToken, LsbInToken;

    public LjHybridUint(uint splitExponent = 4, uint msbInToken = 2, uint lsbInToken = 0)
    {
        SplitExponent = splitExponent;
        SplitToken = 1u << (int)splitExponent;
        MsbInToken = msbInToken;
        LsbInToken = lsbInToken;
    }

    public void Encode(uint value, out uint token, out uint nbits, out uint bits)
    {
        if (value < SplitToken)
        {
            token = value;
            nbits = 0;
            bits = 0;
        }
        else
        {
            uint n = (uint)LjMath.FloorLog2(value);
            uint m = value - (1u << (int)n);
            token = SplitToken + ((n - SplitExponent) << (int)(MsbInToken + LsbInToken)) +
                    ((m >> (int)(n - MsbInToken)) << (int)LsbInToken) + (m & ((1u << (int)LsbInToken) - 1));
            nbits = n - MsbInToken - LsbInToken;
            bits = (uint)((value >> (int)LsbInToken) & ((1UL << (int)nbits) - 1));
        }
    }

    public uint LsbMask => (1u << (int)LsbInToken) - 1;

    /// <summary>HybridUintConfig() = (4, 2, 0); a C# struct `new()` would zero-initialize instead.</summary>
    public static LjHybridUint Default => new(4, 2, 0);
}

internal sealed class LjLz77Params
{
    public bool Enabled;
    public uint MinSymbol = 224;
    public uint MinLength = 3;
    public LjHybridUint LengthUintConfig = new(0, 0, 0);
    public int NonserializedDistanceContext;

    public void Write(LjBitWriter w)
    {
        w.Write(1, Enabled ? 1u : 0u);
        if (!Enabled)
        {
            return;
        }

        LjFields.WriteU32(w, MinSymbol, LjU32.Val(224), LjU32.Val(512), LjU32.Val(4096), LjU32.BitsOffset(15, 8));
        LjFields.WriteU32(w, MinLength, LjU32.Val(3), LjU32.Val(4), LjU32.BitsOffset(2, 5), LjU32.BitsOffset(8, 9));
    }

    public int Bits => !Enabled ? 1 : 1 + LjFields.U32Bits(MinSymbol, LjU32.Val(224), LjU32.Val(512), LjU32.Val(4096), LjU32.BitsOffset(15, 8)) +
        LjFields.U32Bits(MinLength, LjU32.Val(3), LjU32.Val(4), LjU32.BitsOffset(2, 5), LjU32.BitsOffset(8, 9));
}

internal sealed class LjHistogram
{
    public const int Rounding = 8;
    public int[] Counts = Array.Empty<int>();
    public long TotalCount;
    public float Entropy;

    public LjHistogram()
    {
    }

    public LjHistogram(int length) => EnsureCapacity(length);

    public LjHistogram Clone() => new() { Counts = (int[])Counts.Clone(), TotalCount = TotalCount, Entropy = Entropy };

    public void Add(int symbol)
    {
        if (Counts.Length <= symbol)
        {
            Array.Resize(ref Counts, LjMath.DivCeil(symbol + 1, Rounding) * Rounding);
        }

        Counts[symbol]++;
        TotalCount++;
    }

    public void EnsureCapacity(int length) => Array.Resize(ref Counts, LjMath.DivCeil(length, Rounding) * Rounding);

    public void FastAdd(int symbol) => Counts[symbol]++;

    // HistogramCondition: total from the counts, trimmed to the last 8-wide chunk holding a non-zero count.
    public void Condition()
    {
        int nzPos = -Rounding;
        long total = 0;
        for (int i = 0; i < Counts.Length; i += Rounding)
        {
            bool nz = false;
            for (int l = 0; l < Rounding; l++)
            {
                total += Counts[i + l];
                nz |= Counts[i + l] != 0;
            }

            if (nz)
            {
                nzPos = i;
            }
        }

        Array.Resize(ref Counts, nzPos + Rounding);
        TotalCount = total;
    }

    public void AddHistogram(LjHistogram other)
    {
        if (other.Counts.Length > Counts.Length)
        {
            Array.Resize(ref Counts, other.Counts.Length);
        }

        for (int i = 0; i < other.Counts.Length; i++)
        {
            Counts[i] += other.Counts[i];
        }

        TotalCount += other.TotalCount;
    }

    public int AlphabetSize
    {
        get
        {
            for (int i = Counts.Length - 1; i >= 0; i--)
            {
                if (Counts[i] > 0)
                {
                    return i + 1;
                }
            }

            return 0;
        }
    }

    public int MaxSymbol
    {
        get
        {
            if (TotalCount == 0)
            {
                return 0;
            }

            for (int i = Counts.Length - 1; i > 0; i--)
            {
                if (Counts[i] != 0)
                {
                    return i;
                }
            }

            return 0;
        }
    }

    public static LjHistogram Flat(int length, int totalCount) =>
        new() { Counts = LjAns.CreateFlatHistogram(length, totalCount), TotalCount = totalCount };

    // HistogramEntropy (AVX2, 8 lanes).
    public float ShannonEntropy()
    {
        Entropy = 0;
        if (TotalCount == 0)
        {
            return 0;
        }

        float invTot = 1.0f / TotalCount;
        float total = TotalCount;
        Span<float> lanes = stackalloc float[8];
        for (int i = 0; i < Counts.Length; i += Rounding)
        {
            for (int l = 0; l < 8; l++)
            {
                lanes[l] += LjCluster.EntropyLane(Counts[i + l], invTot, total);
            }
        }

        Entropy += LjMath.SumOfLanes8(lanes);
        return Entropy;
    }
}

internal sealed class LjHistogramParams
{
    public enum Clustering { Fastest, Fast, Best }
    public enum UintMethod { None, K000, Fast, ContextMap, Best }
    public enum Lz77Method { None, Rle, Lz77, Optimal }
    public enum AnsStrategy { Fast, Approximate, Precise }

    public Clustering ClusteringType = Clustering.Best;
    public UintMethod HybridUintMethod = UintMethod.Best;
    public Lz77Method Lz77 = Lz77Method.Rle;
    public AnsStrategy AnsHistogramStrategy = AnsStrategy.Precise;
    public int[]? ImageWidths;
    public int MaxHistograms = int.MaxValue;
    public bool ForceHuffman;
    public bool InitializeGlobalState = true;
    public bool StreamingMode;
    public bool AddMissingSymbols;
    public bool AddFixedHistograms;

    public LjHistogramParams()
    {
    }

    // HistogramParams(SpeedTier, num_ctx). speedTier: 0 = glacier .. 9 = lightning (kSquirrel = 3).
    public LjHistogramParams(int speedTier)
    {
        if (speedTier > LjSpeed.Falcon)
        {
            ClusteringType = Clustering.Fastest;
            Lz77 = Lz77Method.None;
        }
        else if (speedTier > LjSpeed.Tortoise)
        {
            ClusteringType = Clustering.Fast;
        }
        else
        {
            ClusteringType = Clustering.Best;
        }

        if (speedTier > LjSpeed.Tortoise)
        {
            HybridUintMethod = UintMethod.None;
        }

        if (speedTier >= LjSpeed.Squirrel)
        {
            AnsHistogramStrategy = AnsStrategy.Approximate;
        }
    }

    public LjHybridUint UintConfig() => HybridUintMethod switch
    {
        UintMethod.ContextMap => new LjHybridUint(2, 0, 1),
        UintMethod.K000 => new LjHybridUint(0, 0, 0),
        _ => LjHybridUint.Default,
    };

    // HistogramParams::ForModular for a VarDCT (non-modular-mode) frame.
    public static LjHistogramParams ForModular(int speedTier, bool modularMode, bool extraDcPrecision, bool predictorZero, bool streamingMode)
    {
        var p = new LjHistogramParams { StreamingMode = streamingMode };
        if (speedTier > LjSpeed.Kitten)
        {
            p.ClusteringType = Clustering.Fast;
            p.AnsHistogramStrategy = speedTier > LjSpeed.Thunder ? AnsStrategy.Fast : AnsStrategy.Approximate;
            p.Lz77 = modularMode && speedTier <= LjSpeed.Hare ? Lz77Method.Rle : Lz77Method.None;
            p.HybridUintMethod = extraDcPrecision || (modularMode && speedTier < LjSpeed.Cheetah) ? UintMethod.Fast : UintMethod.None;
        }
        else if (speedTier <= LjSpeed.Tortoise)
        {
            p.Lz77 = Lz77Method.Optimal;
        }
        else
        {
            p.Lz77 = Lz77Method.Lz77;
        }

        if (predictorZero && modularMode)
        {
            p.Lz77 = speedTier >= LjSpeed.Falcon ? Lz77Method.None
                : speedTier >= LjSpeed.Hare ? Lz77Method.Rle
                : speedTier >= LjSpeed.Kitten ? Lz77Method.Lz77
                : Lz77Method.Optimal;
        }

        return p;
    }
}

internal static class LjSpeed
{
    public const int TectonicPlate = -1, Glacier = 0, Tortoise = 1, Kitten = 2, Squirrel = 3, Wombat = 4, Hare = 5,
        Cheetah = 6, Falcon = 7, Thunder = 8, Lightning = 9;
}

/// <summary>One symbol's ANS encoding info: frequency and the alias-table slot per offset (or prefix bits/depth).</summary>
internal struct LjAnsSymbolInfo
{
    public int Freq;
    public int[]? ReverseMap;
    public ushort Bits;
    public byte Depth;
}

internal sealed class LjEntropyCode
{
    public LjLz77Params Lz77 = new();
    public bool UsePrefixCode;
    public int LogAlphaSize;
    public List<LjHybridUint> UintConfig = new();
    public List<LjAnsSymbolInfo[]> EncodingInfo = new();
    public List<byte> ContextMap = new();
    public List<LjBitWriter> EncodedHistograms = new();
}

internal static class LjAns
{
    public const int LogTabSize = 12;
    public const int TabSize = 1 << LogTabSize;
    public const int MaxAlphabetSize = 256;
    public const int PrefixMaxBits = 15;
    public const int ClustersLimit = 128;
    public const uint Signature = 0x13;
    private const int MaxNumSymbolsForSmallCode = 2;

    public static int[] CreateFlatHistogram(int length, int totalCount)
    {
        int count = totalCount / length;
        var r = new int[length];
        Array.Fill(r, count);
        int rem = totalCount % length;
        for (int i = 0; i < rem; i++)
        {
            r[i]++;
        }

        return r;
    }

    public static int GetPopulationCountPrecision(int logcount, int shift)
    {
        int r = Math.Min(logcount, shift - ((LogTabSize - logcount) >> 1));
        return r < 0 ? 0 : r;
    }

    public static void StoreVarLenUint8(int n, Action<int, ulong> write)
    {
        if (n == 0)
        {
            write(1, 0);
        }
        else
        {
            write(1, 1);
            int nbits = LjMath.FloorLog2((uint)n);
            write(3, (ulong)nbits);
            write(nbits, (ulong)(n - (1 << nbits)));
        }
    }

    public static void StoreVarLenUint16(int n, Action<int, ulong> write)
    {
        if (n == 0)
        {
            write(1, 0);
        }
        else
        {
            write(1, 1);
            int nbits = LjMath.FloorLog2((uint)n);
            write(4, (ulong)nbits);
            write(nbits, (ulong)(n - (1 << nbits)));
        }
    }

    // ---- ANSEncodingHistogram ----

    private static readonly uint[] Lg2 = BuildLg2();

    private static uint[] BuildLg2()
    {
        var t = new uint[TabSize + 1];
        for (int i = 1; i <= TabSize; i++)
        {
            t[i] = (uint)Math.Round(Math.ScaleB(Math.Log2(i) / LogTabSize, 31), MidpointRounding.AwayFromZero);
        }

        return t;
    }

    private struct CountsEntropy
    {
        public int Count;
        public int StepLog;
        public int DeltaLg2;
    }

    private static readonly CountsEntropy[][] AllowedArray;
    private static readonly ushort[][] AllowedIndex;

    static LjAns()
    {
        AllowedArray = new CountsEntropy[LogTabSize][];
        AllowedIndex = new ushort[LogTabSize][];
        for (int shift = 0; shift < LogTabSize; shift++)
        {
            var ac = new CountsEntropy[TabSize];
            var ai = new ushort[TabSize];
            int last = ~0;
            int slot = 0;
            ac[0].DeltaLg2 = 0;
            ac[0].StepLog = 0;
            for (int i = TabSize - 1; i >= 0; i--)
            {
                int curr = i & ~((1 << SmallestIncrementLog(i, shift)) - 1);
                if (curr == last)
                {
                    continue;
                }

                last = curr;
                ac[slot].Count = curr;
                ai[curr] = (ushort)slot;
                if (curr == 0)
                {
                    ac[slot].DeltaLg2 = int.MaxValue;
                    ac[slot].StepLog = 0;
                }
                else if (slot > 0)
                {
                    int prev = ac[slot - 1].Count;
                    ac[slot].DeltaLg2 = (int)Math.Round(Math.ScaleB(Math.Log2((double)prev / curr) / LogTabSize, 31), MidpointRounding.AwayFromZero);
                    ac[slot].StepLog = LjMath.FloorLog2((uint)(prev - curr));
                }

                slot++;
            }

            AllowedArray[shift] = ac;
            AllowedIndex[shift] = ai;
        }
    }

    private static int SmallestIncrementLog(int count, int shift)
    {
        if (count == 0)
        {
            return 0;
        }

        int bits = LjMath.FloorLog2((uint)count);
        return bits - GetPopulationCountPrecision(bits, shift);
    }

    internal sealed class AnsEncodingHistogram
    {
        public float Cost;
        public int Method;
        public int OmitPos;
        public int AlphabetSize;
        public int NumSymbols;
        public int[] Symbols = new int[MaxNumSymbolsForSmallCode];
        public int[] Counts = Array.Empty<int>();

        public AnsEncodingHistogram Copy() => new()
        {
            Cost = Cost, Method = Method, OmitPos = OmitPos, AlphabetSize = AlphabetSize, NumSymbols = NumSymbols,
            Symbols = (int[])Symbols.Clone(), Counts = (int[])Counts.Clone(),
        };

        public static AnsEncodingHistogram ComputeBest(LjHistogram histo, LjHistogramParams.AnsStrategy strategy)
        {
            var result = new AnsEncodingHistogram { AlphabetSize = histo.AlphabetSize };
            if (result.AlphabetSize > MaxAlphabetSize)
            {
                throw new InvalidOperationException("Too many entries in an ANS histogram");
            }

            if (result.AlphabetSize > 0)
            {
                result.Method = 0;
                result.NumSymbols = result.AlphabetSize;
                int[] flat = CreateFlatHistogram(result.AlphabetSize, TabSize);
                result.Counts = new int[histo.Counts.Length];
                Array.Copy(flat, result.Counts, Math.Min(flat.Length, result.Counts.Length));
                var sw = new LjSizeWriter();
                result.Encode(sw.Write);
                result.Cost = sw.Size + EstimateDataBitsFlat(histo);
            }
            else
            {
                result.Method = 1;
                result.NumSymbols = 0;
                result.Cost = 3;
                return result;
            }

            int symbolCount = 0;
            for (int n = 0; n < result.AlphabetSize; n++)
            {
                if (histo.Counts[n] > 0)
                {
                    if (symbolCount < MaxNumSymbolsForSmallCode)
                    {
                        result.Symbols[symbolCount] = n;
                    }

                    symbolCount++;
                }
            }

            result.NumSymbols = symbolCount;
            if (symbolCount == 1)
            {
                result.Method = 1;
                result.Counts = (int[])histo.Counts.Clone();
                result.Counts[result.Symbols[0]] = TabSize;
                var sw = new LjSizeWriter();
                result.Encode(sw.Write);
                result.Cost = sw.Size;
                return result;
            }

            AnsEncodingHistogram normalized = result.Copy();
            void TryShift(int shift)
            {
                normalized.Method = Math.Min(shift, LogTabSize - 1) + 1;
                if (!normalized.RebalanceHistogram(histo))
                {
                    throw new InvalidOperationException("Logic error: couldn't rebalance a histogram");
                }

                var sw = new LjSizeWriter();
                normalized.Encode(sw.Write);
                normalized.Cost = sw.Size + normalized.EstimateDataBits(histo);
                if (normalized.Cost < result.Cost)
                {
                    result = normalized.Copy();
                }
            }

            switch (strategy)
            {
                case LjHistogramParams.AnsStrategy.Precise:
                    for (int shift = 0; shift < LogTabSize; shift++)
                    {
                        TryShift(shift);
                    }

                    break;
                case LjHistogramParams.AnsStrategy.Approximate:
                    for (int shift = 0; shift <= LogTabSize; shift += 2)
                    {
                        TryShift(shift);
                    }

                    break;
                default:
                    TryShift(0);
                    TryShift(LogTabSize / 2);
                    TryShift(LogTabSize);
                    break;
            }

            return result;
        }

        public void Encode(Action<int, ulong> write)
        {
            if (Method == 0)
            {
                write(1, 0);
                write(1, 1);
                StoreVarLenUint8(AlphabetSize - 1, write);
                return;
            }

            if (NumSymbols <= MaxNumSymbolsForSmallCode)
            {
                write(1, 1);
                if (NumSymbols == 0)
                {
                    write(1, 0);
                    StoreVarLenUint8(0, write);
                }
                else
                {
                    write(1, (ulong)(NumSymbols - 1));
                    for (int i = 0; i < NumSymbols; i++)
                    {
                        StoreVarLenUint8(Symbols[i], write);
                    }
                }

                if (NumSymbols == 2)
                {
                    write(LogTabSize, (ulong)Counts[Symbols[0]]);
                }

                return;
            }

            write(1, 0);
            write(1, 0);
            int upperBoundLog = LjMath.FloorLog2((uint)(LogTabSize + 1));
            int log = LjMath.FloorLog2((uint)Method);
            write(log, (ulong)((1 << log) - 1));
            if (log != upperBoundLog)
            {
                write(1, 0);
            }

            write(log, (ulong)(((1 << log) - 1) & Method));
            StoreVarLenUint8(AlphabetSize - 3, write);

            var same = new byte[MaxAlphabetSize];
            int last = 0;
            for (int i = 1; i <= AlphabetSize; i++)
            {
                if (i == AlphabetSize || i == OmitPos || i == OmitPos + 1 || Counts[i] != Counts[last])
                {
                    same[last] = (byte)(i - last);
                    last = i;
                }
            }

            var bitWidth = new byte[MaxAlphabetSize];
            int omitWidth = 10;
            for (int i = 0; i < AlphabetSize; i++)
            {
                if (i != OmitPos && Counts[i] > 0)
                {
                    bitWidth[i] = (byte)(LjMath.FloorLog2((uint)Counts[i]) + 1);
                    omitWidth = Math.Max(omitWidth, bitWidth[i] + (i < OmitPos ? 1 : 0));
                }
            }

            bitWidth[OmitPos] = (byte)omitWidth;
            ReadOnlySpan<byte> kBitWidthLengths = [5, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 6, 7, 7];
            ReadOnlySpan<byte> kBitWidthSymbols = [17, 11, 15, 3, 9, 7, 4, 2, 5, 6, 0, 33, 1, 65];
            const int kMinReps = 5;
            const int rep = LogTabSize + 1;
            for (int i = 0; i < AlphabetSize; i++)
            {
                write(kBitWidthLengths[bitWidth[i]], kBitWidthSymbols[bitWidth[i]]);
                if (same[i] >= kMinReps)
                {
                    write(kBitWidthLengths[rep], kBitWidthSymbols[rep]);
                    StoreVarLenUint8(same[i] - kMinReps, write);
                    i += same[i] - 1;
                }
            }

            int shift = Method - 1;
            if (shift != 0)
            {
                for (int i = 0; i < AlphabetSize; i++)
                {
                    if (bitWidth[i] > 1 && i != OmitPos)
                    {
                        int bitcount = GetPopulationCountPrecision(bitWidth[i] - 1, shift);
                        int dropBits = bitWidth[i] - 1 - bitcount;
                        write(bitcount, (ulong)((Counts[i] >> dropBits) - (1 << bitcount)));
                    }

                    if (same[i] >= kMinReps)
                    {
                        i += same[i] - 1;
                    }
                }
            }
        }

        public LjAnsSymbolInfo[] BuildInfoTable(int logAlphaSize)
        {
            int n = Math.Max(1, AlphabetSize);
            var info = new LjAnsSymbolInfo[n];
            var dist = new int[Math.Max(AlphabetSize, 1)];
            for (int s = 0; s < n; s++)
            {
                int freq = s == AlphabetSize ? TabSize : Counts[s];
                info[s].Freq = freq;
                info[s].ReverseMap = new int[freq];
                dist[s] = freq;
            }

            AliasEntry[] table = JxlEntropy.InitAliasTable(dist, logAlphaSize);
            int logEntrySize = LogTabSize - logAlphaSize;
            int entrySizeMinus1 = (1 << logEntrySize) - 1;
            for (int i = 0; i < TabSize; i++)
            {
                int idx = i >> logEntrySize;
                int pos = i & entrySizeMinus1;
                AliasEntry e = table[idx];
                bool greater = pos >= e.Cutoff;
                int offset = (greater ? e.Offsets1 : 0) + pos;
                int value = greater ? e.Right : idx;
                info[value].ReverseMap![offset] = i;
            }

            return info;
        }

        private float EstimateDataBits(LjHistogram histo)
        {
            long sum = 0;
            for (int i = 0; i < AlphabetSize; i++)
            {
                sum += histo.Counts[i] * (long)Lg2[Counts[i]];
            }

            return ((float)histo.TotalCount - MathF.ScaleB((float)sum, -31)) * LogTabSize;
        }

        private static float EstimateDataBitsFlat(LjHistogram histo)
        {
            int len = histo.AlphabetSize;
            long flatBits = (long)Lg2[len] * LogTabSize;
            return MathF.ScaleB((float)(ulong)(histo.TotalCount * flatBits), -31);
        }

        private bool RebalanceHistogram(LjHistogram histo)
        {
            const int tableSize = TabSize;
            int shift = Method - 1;
            var balanceInc = new long[LogTabSize - 1];
            var balanceDec = new long[LogTabSize - 1];
            CountsEntropy[] ac = AllowedArray[shift];
            ushort[] ai = AllowedIndex[shift];

            long DeltaInc(in Bin a) => (a.Freq * (long)ac[a.CountInd].DeltaLg2) - balanceInc[ac[a.CountInd].StepLog];
            long DeltaDec(in Bin a) => (a.Freq * (long)ac[a.CountInd + 1].DeltaLg2) - balanceDec[ac[a.CountInd + 1].StepLog];

            var bins = new List<Bin>(256);
            double norm = (double)tableSize / histo.TotalCount;
            int remainderPos = 0;
            long maxFreq = 0;
            int rest = tableSize;
            for (int n = 0; n < AlphabetSize; n++)
            {
                int freq = histo.Counts[n];
                if (freq > maxFreq)
                {
                    remainderPos = n;
                    maxFreq = freq;
                }

                double target = freq * norm;
                int count = Math.Max((int)Math.Round(target, MidpointRounding.AwayFromZero), freq > 0 ? 1 : 0);
                count = Math.Min(count, tableSize - 1);
                int stepLog = SmallestIncrementLog(count, shift);
                int inc = 1 << stepLog;
                count &= ~(inc - 1);
                Counts[n] = count;
                rest -= count;
                if (target > 1.0)
                {
                    bins.Add(new Bin { Freq = freq, CountInd = ai[count], BinInd = n });
                }
            }

            for (int i = 0; i < bins.Count; i++)
            {
                if (bins[i].BinInd == remainderPos)
                {
                    bins.RemoveAt(i);
                    break;
                }
            }

            rest += Counts[remainderPos];
            if (bins.Count > 0)
            {
                int maxLog = ac[1].StepLog;
                Bin[] b = bins.ToArray();
                while (true)
                {
                    for (int log = 0; log <= maxLog; log++)
                    {
                        int delta = 1 << log;
                        if (rest >= tableSize)
                        {
                            balanceInc[log] = 0;
                            balanceDec[log] = 0;
                        }
                        else if (rest > 1)
                        {
                            balanceInc[log] = rest > delta ? maxFreq * (long)(Lg2[rest] - (long)Lg2[rest - delta]) : long.MaxValue;
                            balanceDec[log] = rest + delta < tableSize ? maxFreq * (long)(Lg2[rest + delta] - (long)Lg2[rest]) : 0;
                        }
                        else
                        {
                            balanceInc[log] = long.MaxValue;
                            balanceDec[log] = long.MaxValue;
                        }
                    }

                    // std::max_element (first maximum) under IncLess.
                    int bestInc = 0;
                    for (int i = 1; i < b.Length; i++)
                    {
                        if ((DeltaInc(b[bestInc]) >> ac[b[bestInc].CountInd].StepLog) < (DeltaInc(b[i]) >> ac[b[i].CountInd].StepLog))
                        {
                            bestInc = i;
                        }
                    }

                    if (DeltaInc(b[bestInc]) > 0)
                    {
                        rest -= 1 << ac[b[bestInc].CountInd--].StepLog;
                    }
                    else
                    {
                        // std::min_element (first minimum) under DecLess.
                        int bestDec = 0;
                        for (int i = 1; i < b.Length; i++)
                        {
                            if ((DeltaDec(b[i]) >> ac[b[i].CountInd + 1].StepLog) < (DeltaDec(b[bestDec]) >> ac[b[bestDec].CountInd + 1].StepLog))
                            {
                                bestDec = i;
                            }
                        }

                        if (DeltaDec(b[bestDec]) >= 0)
                        {
                            break;
                        }

                        rest += 1 << ac[++b[bestDec].CountInd].StepLog;
                    }
                }

                foreach (Bin a in b)
                {
                    Counts[a.BinInd] = ac[a.CountInd].Count;
                }

                for (int n = 0; n < remainderPos; n++)
                {
                    if (Counts[n] >= 2048)
                    {
                        Counts[remainderPos] = Counts[n];
                        remainderPos = n;
                        break;
                    }
                }
            }

            Counts[remainderPos] = rest;
            OmitPos = remainderPos;
            return Counts[remainderPos] > 0;
        }

        private struct Bin
        {
            public int Freq;
            public int CountInd;
            public int BinInd;
        }
    }

    public static float AnsPopulationCost(LjHistogram h)
    {
        if (h.Counts.Length > MaxAlphabetSize)
        {
            return float.MaxValue;
        }

        return AnsEncodingHistogram.ComputeBest(h, LjHistogramParams.AnsStrategy.Fast).Cost;
    }

    // EntropyEncodingData::BuildAndStoreANSEncodingData. Returns the cost; fills `info`.
    private static long BuildAndStoreAnsEncodingData(LjEntropyCode codes, LjHistogramParams.AnsStrategy strategy, LjHistogram histogram,
        LjBitWriter? writer, out LjAnsSymbolInfo[] info)
    {
        int size = histogram.AlphabetSize;
        if (codes.UsePrefixCode)
        {
            info = new LjAnsSymbolInfo[size];
            long cost = 0;
            if (size <= 1)
            {
                return 0;
            }

            var histo = new uint[size];
            for (int i = 0; i < size; i++)
            {
                histo[i] = (uint)histogram.Counts[i];
            }

            var depths = new byte[size];
            var bits = new ushort[size];
            if (writer == null)
            {
                var tmp = new LjBitWriter();
                LjHuffman.BuildAndStoreHuffmanTree(histo, size, depths, bits, tmp);
                cost = tmp.BitsWritten;
            }
            else
            {
                long start = writer.BitsWritten;
                LjHuffman.BuildAndStoreHuffmanTree(histo, size, depths, bits, writer);
                cost = writer.BitsWritten - start;
            }

            for (int i = 0; i < size; i++)
            {
                info[i].Bits = depths[i] == 0 ? (ushort)0 : bits[i];
                info[i].Depth = depths[i];
            }

            for (int i = 0; i < size; i++)
            {
                cost += histo[i] * info[i].Depth;
            }

            return cost;
        }

        AnsEncodingHistogram normalized = AnsEncodingHistogram.ComputeBest(histogram, strategy);
        info = normalized.BuildInfoTable(codes.LogAlphaSize);
        if (writer != null)
        {
            normalized.Encode(writer.Write);
        }

        return (long)MathF.Ceiling(normalized.Cost);
    }

    private static LjHistogram HistogramFromSymbolInfo(LjAnsSymbolInfo[] encodingInfo, bool usePrefixCode)
    {
        var histo = new LjHistogram { Counts = new int[LjMath.DivCeil(encodingInfo.Length, LjHistogram.Rounding) * LjHistogram.Rounding] };
        for (int i = 0; i < encodingInfo.Length; i++)
        {
            int count = usePrefixCode ? (encodingInfo[i].Depth != 0 ? 1 << (PrefixMaxBits - encodingInfo[i].Depth) : 0) : encodingInfo[i].Freq;
            histo.Counts[i] = count;
            histo.TotalCount += count;
        }

        return histo;
    }

    private static void ChooseUintConfigs(LjHistogramParams p, List<List<LjToken>> tokens, List<LjHistogram> clustered, LjEntropyCode codes)
    {
        if (codes.UsePrefixCode)
        {
            codes.LogAlphaSize = PrefixMaxBits;
        }
        else if (p.StreamingMode)
        {
            codes.LogAlphaSize = 8;
        }
        else if (codes.Lz77.Enabled)
        {
            codes.LogAlphaSize = 8;
        }
        else
        {
            codes.LogAlphaSize = 7;
        }

        codes.UintConfig = new List<LjHybridUint>();
        for (int i = 0; i < clustered.Count; i++)
        {
            codes.UintConfig.Add(p.UintConfig());
        }

        if (p.HybridUintMethod != LjHistogramParams.UintMethod.Best && p.HybridUintMethod != LjHistogramParams.UintMethod.Fast)
        {
            return;
        }

        if (p.StreamingMode)
        {
            return;
        }

        LjHybridUint[] configs = p.HybridUintMethod == LjHistogramParams.UintMethod.Best
            ? [
                new(4, 2, 0), new(4, 1, 0), new(4, 2, 1), new(4, 2, 2), new(4, 1, 2),
                new(5, 2, 0), new(5, 1, 0), new(5, 2, 1), new(5, 2, 2), new(5, 1, 2),
                new(3, 2, 0), new(3, 1, 0), new(3, 2, 1), new(3, 1, 2),
                new(4, 1, 3), new(5, 1, 4), new(5, 2, 3), new(6, 1, 5), new(6, 2, 4), new(6, 0, 0),
                new(0, 0, 0), new(2, 0, 1), new(7, 0, 0), new(8, 0, 0), new(9, 0, 0), new(10, 0, 0), new(11, 0, 0), new(12, 0, 0),
            ]
            : [new(4, 2, 0), new(4, 1, 2), new(0, 0, 0), new(2, 0, 1)];

        int numHisto = clustered.Count;
        var histoVolume = new int[2 * numHisto];
        var histoOffset = new int[(2 * numHisto) + 1];
        var maxValuePerHisto = new uint[2 * numHisto];
        foreach (List<LjToken> stream in tokens)
        {
            foreach (LjToken t in stream)
            {
                int h = codes.ContextMap[(int)t.Context];
                histoVolume[h + (t.IsLz77Length ? numHisto : 0)]++;
            }
        }

        for (int h = 0; h < 2 * numHisto; h++)
        {
            histoOffset[h + 1] = histoOffset[h] + histoVolume[h];
        }

        var transposed = new uint[histoOffset[numHisto * 2]];
        {
            var next = (int[])histoOffset.Clone();
            foreach (List<LjToken> stream in tokens)
            {
                foreach (LjToken t in stream)
                {
                    int h = codes.ContextMap[(int)t.Context] + (t.IsLz77Length ? numHisto : 0);
                    transposed[next[h]++] = t.Value;
                }
            }
        }

        for (int h = 0; h < 2 * numHisto; h++)
        {
            uint mx = 0;
            for (int i = 0; i < histoVolume[h]; i++)
            {
                mx = Math.Max(mx, transposed[histoOffset[h] + i]);
            }

            maxValuePerHisto[h] = mx;
        }

        const int maxAlpha = MaxAlphabetSize;
        for (int h = 0; h < numHisto; h++)
        {
            float bestCost = float.MaxValue;
            foreach (LjHybridUint cfg in configs)
            {
                uint maxV = maxValuePerHisto[h];
                int capacity;
                {
                    cfg.Encode(maxV, out uint tok, out _, out _);
                    tok |= cfg.LsbMask;
                    if (tok >= maxAlpha || (codes.Lz77.Enabled && tok >= codes.Lz77.MinSymbol))
                    {
                        continue;
                    }

                    capacity = (int)tok + 1;
                }

                var histo = new LjHistogram(capacity);
                int len = histoVolume[h];
                long extraBits = 0;
                for (int i = 0; i < len; i++)
                {
                    cfg.Encode(transposed[histoOffset[h] + i], out uint tok, out uint nbits, out _);
                    extraBits += nbits;
                    histo.FastAdd((int)tok);
                }

                histo.Condition();
                float cost = AnsPopulationCost(histo);
                cost += extraBits;
                cost += LjMath.CeilLog2Nonzero(cfg.SplitExponent + 1);
                cost += LjMath.CeilLog2Nonzero(cfg.SplitExponent - cfg.MsbInToken + 1);
                if (cost < bestCost)
                {
                    codes.UintConfig[h] = cfg;
                    bestCost = cost;
                    clustered[h] = histo;
                }
            }
        }

        int maxTok = 0;
        for (int h = 0; h < numHisto; h++)
        {
            LjHistogram histo = clustered[h];
            maxTok = Math.Max(maxTok, histo.MaxSymbol);
            int len = histoVolume[numHisto + h];
            if (len == 0)
            {
                continue;
            }

            uint maxHistoTok = maxValuePerHisto[numHisto + h];
            codes.Lz77.LengthUintConfig.Encode(maxHistoTok, out uint tk, out _, out _);
            tk |= codes.Lz77.LengthUintConfig.LsbMask;
            tk += codes.Lz77.MinSymbol;
            histo.EnsureCapacity((int)tk + 1);
            for (int i = 0; i < len; i++)
            {
                codes.Lz77.LengthUintConfig.Encode(transposed[histoOffset[numHisto + h] + i], out uint t2, out _, out _);
                histo.FastAdd((int)(t2 + codes.Lz77.MinSymbol));
            }

            histo.Condition();
            maxTok = Math.Max(maxTok, histo.MaxSymbol);
        }

        int logSize = 5;
        while (maxTok >= (1 << logSize))
        {
            logSize++;
        }

        codes.LogAlphaSize = codes.UsePrefixCode ? PrefixMaxBits : logSize;
    }

    private static long BuildAndStoreEntropyCodes(LjHistogramParams p, List<List<LjToken>> tokens, List<LjHistogram> builder,
        LjEntropyCode codes, LjBitWriter? writer)
    {
        int prevHistograms = codes.EncodingInfo.Count;
        var clustered = new List<LjHistogram>();
        for (int i = 0; i < prevHistograms; i++)
        {
            clustered.Add(HistogramFromSymbolInfo(codes.EncodingInfo[i], codes.UsePrefixCode));
        }

        int contextOffset = codes.ContextMap.Count;
        for (int i = 0; i < builder.Count; i++)
        {
            codes.ContextMap.Add(0);
        }

        if (builder.Count > 1)
        {
            uint[] histogramSymbols = LjCluster.ClusterHistograms(p, builder, ClustersLimit, clustered);
            for (int c = 0; c < builder.Count; c++)
            {
                codes.ContextMap[contextOffset + c] = (byte)histogramSymbols[c];
            }

            if (writer != null)
            {
                LjContextMap.EncodeContextMap(codes.ContextMap, clustered.Count, writer);
            }
        }
        else
        {
            clustered.Add(builder[0]);
        }

        ChooseUintConfigs(p, tokens, clustered, codes);

        var sizeWriter = new LjSizeWriter();
        long cost = codes.UsePrefixCode ? 1 : 3;
        writer?.Write(1, codes.UsePrefixCode ? 1u : 0u);
        if (writer == null)
        {
            EncodeUintConfigs(codes.UintConfig, sizeWriter.Write, codes.LogAlphaSize);
        }
        else
        {
            if (!codes.UsePrefixCode)
            {
                writer.Write(2, (ulong)(codes.LogAlphaSize - 5));
            }

            EncodeUintConfigs(codes.UintConfig, writer.Write, codes.LogAlphaSize);
        }

        if (codes.UsePrefixCode)
        {
            foreach (LjHistogram h in clustered)
            {
                int alphabetSize = Math.Max(1, h.AlphabetSize);
                if (writer != null)
                {
                    StoreVarLenUint16(alphabetSize - 1, writer.Write);
                }
                else
                {
                    StoreVarLenUint16(alphabetSize - 1, sizeWriter.Write);
                }
            }
        }

        cost += sizeWriter.Size;
        for (int c = prevHistograms; c < clustered.Count; c++)
        {
            LjBitWriter? histoWriter = writer;
            if (p.StreamingMode)
            {
                histoWriter = new LjBitWriter();
                codes.EncodedHistograms.Add(histoWriter);
            }

            cost += BuildAndStoreAnsEncodingData(codes, p.AnsHistogramStrategy, clustered[c], histoWriter, out LjAnsSymbolInfo[] info);
            codes.EncodingInfo.Add(info);
            if (p.StreamingMode)
            {
                writer!.AppendUnaligned(histoWriter!);
            }
        }

        return cost;
    }

    public static void EncodeUintConfig(LjHybridUint cfg, Action<int, ulong> write, int logAlphaSize)
    {
        write(LjMath.CeilLog2Nonzero((uint)(logAlphaSize + 1)), cfg.SplitExponent);
        if (cfg.SplitExponent == logAlphaSize)
        {
            return;
        }

        int nbits = LjMath.CeilLog2Nonzero(cfg.SplitExponent + 1);
        write(nbits, cfg.MsbInToken);
        nbits = LjMath.CeilLog2Nonzero(cfg.SplitExponent - cfg.MsbInToken + 1);
        write(nbits, cfg.LsbInToken);
    }

    public static void EncodeUintConfigs(List<LjHybridUint> cfgs, Action<int, ulong> write, int logAlphaSize)
    {
        foreach (LjHybridUint c in cfgs)
        {
            EncodeUintConfig(c, write, logAlphaSize);
        }
    }

    /// <summary>BuildAndEncodeHistograms. May replace <paramref name="tokens"/> with its LZ77-transformed form.</summary>
    public static long BuildAndEncodeHistograms(LjHistogramParams p, int numContexts, ref List<List<LjToken>> tokens,
        LjEntropyCode codes, LjBitWriter? writer)
    {
        codes.Lz77.NonserializedDistanceContext = numContexts;
        codes.Lz77.MinSymbol = p.ForceHuffman ? 512u : 224u;
        List<List<LjToken>>? tokensLz77 = LjLz77.ApplyLz77(p, numContexts, tokens, codes.Lz77);
        if (tokensLz77 != null)
        {
            codes.Lz77.Enabled = true;
        }

        long cost = 0;
        if (writer != null)
        {
            codes.Lz77.Write(writer);
        }
        else
        {
            cost += codes.Lz77.Bits;
        }

        if (codes.Lz77.Enabled)
        {
            if (writer != null)
            {
                long b = writer.BitsWritten;
                EncodeUintConfig(codes.Lz77.LengthUintConfig, writer.Write, 8);
                cost += writer.BitsWritten - b;
            }
            else
            {
                var sw = new LjSizeWriter();
                EncodeUintConfig(codes.Lz77.LengthUintConfig, sw.Write, 8);
                cost += sw.Size;
            }

            numContexts += 1;
            tokens = tokensLz77!;
        }

        long totalTokens = 0;
        var builder = new List<LjHistogram>(numContexts);
        for (int i = 0; i < numContexts; i++)
        {
            builder.Add(new LjHistogram());
        }

        LjHybridUint uintConfig = p.UintConfig();
        foreach (List<LjToken> stream in tokens)
        {
            foreach (LjToken t in stream)
            {
                totalTokens++;
                (t.IsLz77Length ? codes.Lz77.LengthUintConfig : uintConfig).Encode(t.Value, out uint tok, out _, out _);
                tok += t.IsLz77Length ? codes.Lz77.MinSymbol : 0;
                builder[codes.Lz77.Enabled || numContexts != 1 ? (int)t.Context : 0].Add((int)tok);
            }
        }

        if (p.AddMissingSymbols)
        {
            for (int c = 0; c < numContexts; c++)
            {
                for (int s = 0; s < MaxAlphabetSize; s++)
                {
                    builder[c].Add(s);
                }
            }
        }

        if (p.InitializeGlobalState)
        {
            bool usePrefix = p.ForceHuffman || totalTokens < 100 || p.ClusteringType == LjHistogramParams.Clustering.Fastest;
            if (!usePrefix)
            {
                bool allSingleton = true;
                for (int i = 0; i < numContexts; i++)
                {
                    if (builder[i].ShannonEntropy() >= 1e-5f)
                    {
                        allSingleton = false;
                    }
                }

                if (allSingleton)
                {
                    usePrefix = true;
                }
            }

            codes.UsePrefixCode = usePrefix;
        }

        if (p.AddFixedHistograms)
        {
            const int alphabetSize = MaxAlphabetSize;
            codes.LogAlphaSize = 8;
            var hw = new LjBitWriter();
            codes.EncodedHistograms.Add(hw);
            BuildAndStoreAnsEncodingData(codes, p.AnsHistogramStrategy, LjHistogram.Flat(alphabetSize, TabSize), hw, out LjAnsSymbolInfo[] info);
            codes.EncodingInfo.Add(info);
        }

        cost += BuildAndStoreEntropyCodes(p, tokens, builder, codes, writer);
        return cost;
    }

    public static long WriteTokens(List<LjToken> tokens, LjEntropyCode codes, int contextOffset, LjBitWriter writer)
    {
        long numExtraBits = 0;
        if (codes.UsePrefixCode)
        {
            foreach (LjToken t in tokens)
            {
                int histo = codes.ContextMap[contextOffset + (int)t.Context];
                (t.IsLz77Length ? codes.Lz77.LengthUintConfig : codes.UintConfig[histo]).Encode(t.Value, out uint tok, out uint nbits, out uint bits);
                tok += t.IsLz77Length ? codes.Lz77.MinSymbol : 0;
                LjAnsSymbolInfo info = codes.EncodingInfo[histo][tok];
                ulong data = info.Bits | ((ulong)bits << info.Depth);
                writer.Write(info.Depth + (int)nbits, data);
                numExtraBits += nbits;
            }

            return numExtraBits;
        }

        var outBits = new List<ulong>(tokens.Count);
        var outN = new List<int>(tokens.Count);
        ulong allbits = 0;
        int numallbits = 0;
        void AddBits(uint bits, int nbits)
        {
            if (nbits != 0)
            {
                if (numallbits + nbits > 56)
                {
                    outBits.Add(allbits);
                    outN.Add(numallbits);
                    numallbits = 0;
                    allbits = 0;
                }

                allbits <<= nbits;
                allbits |= bits;
                numallbits += nbits;
            }
        }

        uint state = Signature << 16;
        bool generic = codes.Lz77.Enabled || codes.ContextMap.Count > 1;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            LjToken t = tokens[i];
            int histo = generic ? codes.ContextMap[contextOffset + (int)t.Context] : 0;
            (t.IsLz77Length ? codes.Lz77.LengthUintConfig : codes.UintConfig[histo]).Encode(t.Value, out uint tok, out uint nbits, out uint bits);
            tok += t.IsLz77Length ? codes.Lz77.MinSymbol : 0;
            LjAnsSymbolInfo info = codes.EncodingInfo[histo][tok];
            AddBits(bits, (int)nbits);
            numExtraBits += nbits;
            uint ansBits = 0;
            int ansN = 0;
            if ((state >> (32 - LogTabSize)) >= (uint)info.Freq)
            {
                ansBits = state & 0xffff;
                state >>= 16;
                ansN = 16;
            }

            state = ((state / (uint)info.Freq) << LogTabSize) + (uint)info.ReverseMap![state % (uint)info.Freq];
            AddBits(ansBits, ansN);
        }

        writer.Write(32, state);
        writer.Write(numallbits, allbits);
        for (int i = outBits.Count; i > 0; i--)
        {
            writer.Write(outN[i - 1], outBits[i - 1]);
        }

        return numExtraBits;
    }

    /// <summary>EncodeHistograms (streaming-mode AC global output).</summary>
    public static void EncodeHistograms(LjEntropyCode codes, LjBitWriter writer)
    {
        codes.Lz77.Write(writer);
        if (codes.Lz77.Enabled)
        {
            EncodeUintConfig(codes.Lz77.LengthUintConfig, writer.Write, 8);
        }

        LjContextMap.EncodeContextMap(codes.ContextMap, codes.EncodingInfo.Count, writer);
        writer.Write(1, codes.UsePrefixCode ? 1u : 0u);
        int logAlphaSize = 8;
        if (codes.UsePrefixCode)
        {
            logAlphaSize = PrefixMaxBits;
        }
        else
        {
            writer.Write(2, (ulong)(logAlphaSize - 5));
        }

        EncodeUintConfigs(codes.UintConfig, writer.Write, logAlphaSize);
        if (codes.UsePrefixCode)
        {
            foreach (LjAnsSymbolInfo[] info in codes.EncodingInfo)
            {
                StoreVarLenUint16(info.Length - 1, writer.Write);
            }
        }

        foreach (LjBitWriter hw in codes.EncodedHistograms)
        {
            writer.AppendUnaligned(hw);
        }
    }
}

internal static class LjCluster
{
    // Entropy() lane: 0 when count == total, else -count * log2(count / total) (FMA FastLog2f).
    public static float EntropyLane(int countI, float invTotal, float total)
    {
        float count = countI;
        if (count == total)
        {
            return 0;
        }

        return 0.0f - (count * LjMath.FastLog2f(invTotal * count, true));
    }

    public static float HistogramDistance(LjHistogram a, LjHistogram b)
    {
        if (a.TotalCount == 0 || b.TotalCount == 0)
        {
            return 0;
        }

        float invTot = 1.0f / (a.TotalCount + b.TotalCount);
        float total = a.TotalCount + b.TotalCount;
        Span<float> lanes = stackalloc float[8];
        int len = Math.Max(a.Counts.Length, b.Counts.Length);
        for (int i = 0; i < len; i += LjHistogram.Rounding)
        {
            for (int l = 0; l < 8; l++)
            {
                int ac = a.Counts.Length > i ? a.Counts[i + l] : 0;
                int bc = b.Counts.Length > i ? b.Counts[i + l] : 0;
                lanes[l] += EntropyLane(ac + bc, invTot, total);
            }
        }

        float totalDistance = LjMath.SumOfLanes8(lanes);
        return totalDistance - a.Entropy - b.Entropy;
    }

    public static float HistogramKLDivergence(LjHistogram actual, LjHistogram coding)
    {
        if (actual.TotalCount == 0)
        {
            return 0;
        }

        if (coding.TotalCount == 0)
        {
            return float.PositiveInfinity;
        }

        float codingInv = 1.0f / coding.TotalCount;
        Span<float> lanes = stackalloc float[8];
        for (int i = 0; i < actual.Counts.Length; i += LjHistogram.Rounding)
        {
            for (int l = 0; l < 8; l++)
            {
                int counts = actual.Counts[i + l];
                int codingCounts = coding.Counts.Length > i ? coding.Counts[i + l] : 0;
                float codingProbs = codingCounts * codingInv;
                float negCodingCost = counts == 0 ? 0f : codingCounts == 0 ? float.NegativeInfinity : LjMath.FastLog2f(codingProbs, true);
                lanes[l] = MathF.FusedMultiplyAdd(-(float)counts, negCodingCost, lanes[l]);
            }
        }

        float totalCost = LjMath.SumOfLanes8(lanes);
        return totalCost - actual.Entropy;
    }

    private static uint[] FastClusterHistograms(List<LjHistogram> input, int maxHistograms, List<LjHistogram> output)
    {
        int prevHistograms = output.Count;
        var histogramSymbols = new uint[input.Count];
        Array.Fill(histogramSymbols, (uint)maxHistograms);
        var dists = new float[input.Count];
        Array.Fill(dists, float.MaxValue);
        int largestIdx = 0;
        for (int i = 0; i < input.Count; i++)
        {
            if (input[i].TotalCount == 0)
            {
                histogramSymbols[i] = 0;
                dists[i] = 0.0f;
                continue;
            }

            input[i].ShannonEntropy();
            if (input[i].TotalCount > input[largestIdx].TotalCount)
            {
                largestIdx = i;
            }
        }

        if (prevHistograms > 0)
        {
            for (int j = 0; j < prevHistograms; j++)
            {
                output[j].ShannonEntropy();
            }

            for (int i = 0; i < input.Count; i++)
            {
                if (dists[i] == 0.0f)
                {
                    continue;
                }

                for (int j = 0; j < prevHistograms; j++)
                {
                    dists[i] = Math.Min(HistogramKLDivergence(input[i], output[j]), dists[i]);
                }
            }

            int maxIdx = 0;
            for (int i = 1; i < dists.Length; i++)
            {
                if (dists[i] > dists[maxIdx])
                {
                    maxIdx = i;
                }
            }

            if (dists[maxIdx] > 0.0f)
            {
                largestIdx = maxIdx;
            }
        }

        const float kMinDistanceForDistinct = 48.0f;
        while (output.Count < maxHistograms)
        {
            histogramSymbols[largestIdx] = (uint)output.Count;
            output.Add(input[largestIdx].Clone());
            dists[largestIdx] = 0.0f;
            largestIdx = 0;
            for (int i = 0; i < input.Count; i++)
            {
                if (dists[i] == 0.0f)
                {
                    continue;
                }

                dists[i] = Math.Min(HistogramDistance(input[i], output[^1]), dists[i]);
                if (dists[i] > dists[largestIdx])
                {
                    largestIdx = i;
                }
            }

            if (dists[largestIdx] < kMinDistanceForDistinct)
            {
                break;
            }
        }

        for (int i = 0; i < input.Count; i++)
        {
            if (histogramSymbols[i] != maxHistograms)
            {
                continue;
            }

            int best = 0;
            float bestDist = float.MaxValue;
            for (int j = 0; j < output.Count; j++)
            {
                float dist = j < prevHistograms ? HistogramKLDivergence(input[i], output[j]) : HistogramDistance(input[i], output[j]);
                if (dist < bestDist)
                {
                    best = j;
                    bestDist = dist;
                }
            }

            if (best >= prevHistograms)
            {
                output[best].AddHistogram(input[i]);
                output[best].ShannonEntropy();
            }

            histogramSymbols[i] = (uint)best;
        }

        return histogramSymbols;
    }

    public static uint[] ClusterHistograms(LjHistogramParams p, List<LjHistogram> input, int maxHistograms, List<LjHistogram> output)
    {
        int prevHistograms = output.Count;
        maxHistograms = Math.Min(maxHistograms, p.MaxHistograms);
        maxHistograms = Math.Min(maxHistograms, input.Count);
        if (p.ClusteringType == LjHistogramParams.Clustering.Fastest)
        {
            maxHistograms = Math.Min(maxHistograms, 4);
        }

        uint[] symbols = FastClusterHistograms(input, prevHistograms + maxHistograms, output);

        if (prevHistograms == 0 && p.ClusteringType == LjHistogramParams.Clustering.Best)
        {
            foreach (LjHistogram h in output)
            {
                h.Entropy = LjAns.AnsPopulationCost(h);
            }

            uint nextVersion = 2;
            var version = new uint[output.Count];
            Array.Fill(version, 1u);
            var renumbering = new uint[output.Count];
            for (int i = 0; i < renumbering.Length; i++)
            {
                renumbering[i] = (uint)i;
            }

            // std::priority_queue: the top is the pair with the smallest (cost, first, second, version).
            var pq = new PriorityQueue<(float Cost, uint First, uint Second, uint Version), (float, uint, uint, uint)>();
            for (uint i = 0; i < output.Count; i++)
            {
                for (uint j = i + 1; j < output.Count; j++)
                {
                    var h = new LjHistogram();
                    h.AddHistogram(output[(int)i]);
                    h.AddHistogram(output[(int)j]);
                    float cost = LjAns.AnsPopulationCost(h);
                    cost -= output[(int)i].Entropy + output[(int)j].Entropy;
                    if (cost >= 0)
                    {
                        continue;
                    }

                    var e = (cost, i, j, Math.Max(version[i], version[j]));
                    pq.Enqueue(e, e);
                }
            }

            while (pq.Count > 0)
            {
                var top = pq.Dequeue();
                uint first = top.First, second = top.Second, ver = top.Version;
                if (ver != Math.Max(version[first], version[second]) || version[first] == 0 || version[second] == 0)
                {
                    continue;
                }

                output[(int)first].AddHistogram(output[(int)second]);
                output[(int)first].Entropy = LjAns.AnsPopulationCost(output[(int)first]);
                for (int k = 0; k < renumbering.Length; k++)
                {
                    if (renumbering[k] == second)
                    {
                        renumbering[k] = first;
                    }
                }

                version[second] = 0;
                version[first] = nextVersion++;
                for (uint j = 0; j < output.Count; j++)
                {
                    if (j == first || version[j] == 0)
                    {
                        continue;
                    }

                    var h = new LjHistogram();
                    h.AddHistogram(output[(int)first]);
                    h.AddHistogram(output[(int)j]);
                    float mergeCost = LjAns.AnsPopulationCost(h);
                    mergeCost -= output[(int)first].Entropy + output[(int)j].Entropy;
                    if (mergeCost >= 0)
                    {
                        continue;
                    }

                    var e = (mergeCost, Math.Min(first, j), Math.Max(first, j), Math.Max(version[first], version[j]));
                    pq.Enqueue(e, e);
                }
            }

            var reverse = new uint[output.Count];
            Array.Fill(reverse, uint.MaxValue);
            int numAlive = 0;
            for (int i = 0; i < output.Count; i++)
            {
                if (version[i] == 0)
                {
                    continue;
                }

                output[numAlive++] = output[i];
                reverse[i] = (uint)(numAlive - 1);
            }

            output.RemoveRange(numAlive, output.Count - numAlive);
            for (int i = 0; i < symbols.Length; i++)
            {
                symbols[i] = reverse[renumbering[symbols[i]]];
            }
        }

        // HistogramReindex: renumber the new symbols in order of first appearance.
        var tmp = new List<LjHistogram>(output);
        var newIndex = new Dictionary<uint, uint>();
        for (uint i = 0; i < prevHistograms; i++)
        {
            newIndex[i] = i;
        }

        uint nextIndex = (uint)prevHistograms;
        foreach (uint s in symbols)
        {
            if (!newIndex.ContainsKey(s))
            {
                newIndex[s] = nextIndex;
                output[(int)nextIndex] = tmp[(int)s];
                nextIndex++;
            }
        }

        output.RemoveRange((int)nextIndex, output.Count - (int)nextIndex);
        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = newIndex[symbols[i]];
        }

        return symbols;
    }
}

internal static class LjContextMap
{
    private static byte[] MoveToFrontTransform(List<byte> v)
    {
        if (v.Count == 0)
        {
            return Array.Empty<byte>();
        }

        int maxValue = 0;
        foreach (byte b in v)
        {
            maxValue = Math.Max(maxValue, b);
        }

        var mtf = new byte[maxValue + 1];
        for (int i = 0; i <= maxValue; i++)
        {
            mtf[i] = (byte)i;
        }

        var result = new byte[v.Count];
        for (int i = 0; i < v.Count; i++)
        {
            int index = Array.IndexOf(mtf, v[i]);
            result[i] = (byte)index;
            byte value = mtf[index];
            for (int k = index; k != 0; k--)
            {
                mtf[k] = mtf[k - 1];
            }

            mtf[0] = value;
        }

        return result;
    }

    public static void EncodeContextMap(List<byte> contextMap, int numHistograms, LjBitWriter writer)
    {
        if (numHistograms == 1)
        {
            writer.Write(1, 1);
            writer.Write(2, 0);
            return;
        }

        byte[] transformed = MoveToFrontTransform(contextMap);
        var tokens = new List<List<LjToken>> { new() };
        var mtfTokens = new List<List<LjToken>> { new() };
        foreach (byte c in contextMap)
        {
            tokens[0].Add(new LjToken(0, c));
        }

        foreach (byte s in transformed)
        {
            mtfTokens[0].Add(new LjToken(0, s));
        }

        var p = new LjHistogramParams { HybridUintMethod = LjHistogramParams.UintMethod.ContextMap };
        long ansCost = LjAns.BuildAndEncodeHistograms(p, 1, ref tokens, new LjEntropyCode(), null);
        long mtfCost = LjAns.BuildAndEncodeHistograms(p, 1, ref mtfTokens, new LjEntropyCode(), null);
        bool useMtf = mtfCost < ansCost;
        tokens = new List<List<LjToken>> { new() };
        for (int i = 0; i < transformed.Length; i++)
        {
            tokens[0].Add(new LjToken(0, useMtf ? transformed[i] : contextMap[i]));
        }

        int entryBits = LjMath.CeilLog2Nonzero((uint)numHistograms);
        long simpleCost = (long)entryBits * contextMap.Count;
        if (entryBits < 4 && simpleCost < ansCost && simpleCost < mtfCost)
        {
            writer.Write(1, 1);
            writer.Write(2, (ulong)entryBits);
            foreach (byte e in contextMap)
            {
                writer.Write(entryBits, e);
            }
        }
        else
        {
            writer.Write(1, 0);
            writer.Write(1, useMtf ? 1u : 0u);
            var codes = new LjEntropyCode();
            LjAns.BuildAndEncodeHistograms(p, 1, ref tokens, codes, writer);
            LjAns.WriteTokens(tokens[0], codes, 0, writer);
        }
    }
}

internal static partial class LjLz77
{
    private sealed class SymbolCostEstimator
    {
        private readonly int maxAlphabetSize;
        private readonly float[] bits;
        private readonly float[] addSymbolCost;

        public SymbolCostEstimator(int numContexts, bool forceHuffman, List<List<LjToken>> tokens, LjLz77Params lz77)
        {
            var builder = new LjHistogram[numContexts];
            for (int i = 0; i < numContexts; i++)
            {
                builder[i] = new LjHistogram();
            }

            var uintConfig = LjHybridUint.Default;
            foreach (List<LjToken> stream in tokens)
            {
                foreach (LjToken t in stream)
                {
                    (t.IsLz77Length ? lz77.LengthUintConfig : uintConfig).Encode(t.Value, out uint tok, out _, out _);
                    tok += t.IsLz77Length ? lz77.MinSymbol : 0;
                    builder[t.Context].Add((int)tok);
                }
            }

            maxAlphabetSize = 0;
            for (int i = 0; i < numContexts; i++)
            {
                maxAlphabetSize = Math.Max(maxAlphabetSize, builder[i].Counts.Length);
            }

            bits = new float[numContexts * maxAlphabetSize];
            addSymbolCost = new float[numContexts];
            for (int i = 0; i < numContexts; i++)
            {
                float invTotal = 1.0f / (builder[i].TotalCount + 1e-8f);
                float totalCost = 0;
                for (int j = 0; j < builder[i].Counts.Length; j++)
                {
                    long cnt = builder[i].Counts[j];
                    float cost = 0;
                    if (cnt != 0 && cnt != builder[i].TotalCount)
                    {
                        cost = -LjMath.FastLog2f(cnt * invTotal, false);
                        if (forceHuffman)
                        {
                            cost = MathF.Ceiling(cost);
                        }
                    }
                    else if (cnt == 0)
                    {
                        cost = LjAns.LogTabSize;
                    }

                    bits[(i * maxAlphabetSize) + j] = cost;
                    totalCost += cost * builder[i].Counts[j];
                }

                addSymbolCost[i] = Math.Max(0.0f, 6.0f - (totalCost * invTotal));
            }
        }

        public float Bits(int ctx, int sym) => bits[(ctx * maxAlphabetSize) + sym];

        public float LenCost(int ctx, int len, LjLz77Params lz77)
        {
            lz77.LengthUintConfig.Encode((uint)len, out uint tok, out uint nbits, out _);
            tok += lz77.MinSymbol;
            return nbits + Bits(ctx, (int)tok);
        }

        public float DistCost(int len, LjLz77Params lz77)
        {
            LjHybridUint.Default.Encode((uint)len, out uint tok, out uint nbits, out _);
            return nbits + Bits(lz77.NonserializedDistanceContext, (int)tok);
        }

        public float AddSymbolCost(int idx) => addSymbolCost[idx];
    }

    public static List<List<LjToken>>? ApplyLz77(LjHistogramParams p, int numContexts, List<List<LjToken>> tokens, LjLz77Params lz77) =>
        p.Lz77 switch
        {
            LjHistogramParams.Lz77Method.Rle => ApplyLz77Rle(p, numContexts, tokens, lz77),
            LjHistogramParams.Lz77Method.Lz77 or LjHistogramParams.Lz77Method.Optimal =>
                p.Lz77 == LjHistogramParams.Lz77Method.Lz77 ? ApplyLz77Lz77(p, numContexts, tokens, lz77) : ApplyLz77Optimal(p, numContexts, tokens, lz77),
            _ => null,
        };

    private static List<List<LjToken>>? ApplyLz77Rle(LjHistogramParams p, int numContexts, List<List<LjToken>> tokens, LjLz77Params lz77)
    {
        var tokensLz77 = new List<List<LjToken>>(tokens.Count);
        var sce = new SymbolCostEstimator(numContexts, p.ForceHuffman, tokens, lz77);
        float bitDecrease = 0;
        long totalSymbols = 0;
        var uintConfig = LjHybridUint.Default;
        for (int stream = 0; stream < tokens.Count; stream++)
        {
            int distanceMultiplier = p.ImageWidths != null && p.ImageWidths.Length > stream ? p.ImageWidths[stream] : 0;
            List<LjToken> input = tokens[stream];
            var output = new List<LjToken>(input.Count);
            tokensLz77.Add(output);
            totalSymbols += input.Count;
            var symCost = new float[input.Count + 1];
            for (int i = 0; i < input.Count; i++)
            {
                uintConfig.Encode(input[i].Value, out uint tok, out uint nbits, out _);
                symCost[i + 1] = sce.Bits((int)input[i].Context, (int)tok) + nbits + symCost[i];
            }

            for (int i = 0; i < input.Count; i++)
            {
                int numToCopy = 0;
                uint distanceSymbol = distanceMultiplier != 0 ? 1u : 0u;
                if (i > 0)
                {
                    for (; i + numToCopy < input.Count; numToCopy++)
                    {
                        if (input[i + numToCopy].Value != input[i - 1].Value)
                        {
                            break;
                        }
                    }
                }

                if (numToCopy == 0)
                {
                    output.Add(input[i]);
                    continue;
                }

                float cost = symCost[i + numToCopy] - symCost[i];
                uint lz77Len = (uint)numToCopy - lz77.MinLength;
                float lz77Cost = numToCopy >= lz77.MinLength ? LjMath.CeilLog2Nonzero(lz77Len + 1) + 1 : 0;
                if (numToCopy < lz77.MinLength || cost <= lz77Cost)
                {
                    for (int j = 0; j < numToCopy; j++)
                    {
                        output.Add(input[i + j]);
                    }

                    i += numToCopy - 1;
                    continue;
                }

                output.Add(new LjToken(input[i].Context, lz77Len) { IsLz77Length = true });
                i += numToCopy - 1;
                bitDecrease += cost - lz77Cost;
                output.Add(new LjToken((uint)lz77.NonserializedDistanceContext, distanceSymbol));
            }
        }

        if (bitDecrease > (totalSymbols * 0.2) + 16)
        {
            return tokensLz77;
        }

        return null;
    }
}

internal static class LjHuffman
{
    private struct Node
    {
        public uint TotalCount;
        public short IndexLeft;
        public short IndexRightOrValue;

        public Node(uint count, short left, short right)
        {
            TotalCount = count;
            IndexLeft = left;
            IndexRightOrValue = right;
        }
    }

    private static void SetDepth(Node p, Node[] pool, byte[] depth, byte level)
    {
        if (p.IndexLeft >= 0)
        {
            level++;
            SetDepth(pool[p.IndexLeft], pool, depth, level);
            SetDepth(pool[p.IndexRightOrValue], pool, depth, level);
        }
        else
        {
            depth[p.IndexRightOrValue] = level;
        }
    }

    public static void CreateHuffmanTree(uint[] data, int length, int treeLimit, byte[] depth)
    {
        for (uint countLimit = 1; ; countLimit *= 2)
        {
            var tree = new List<Node>((2 * length) + 1);
            for (int i = length; i != 0;)
            {
                --i;
                if (data[i] != 0)
                {
                    uint count = Math.Max(data[i], countLimit - 1);
                    tree.Add(new Node(count, -1, (short)i));
                }
            }

            int n = tree.Count;
            if (n == 1)
            {
                depth[tree[0].IndexRightOrValue] = 1;
                break;
            }

            tree.Sort((v0, v1) =>
            {
                if (v0.TotalCount != v1.TotalCount)
                {
                    return v0.TotalCount < v1.TotalCount ? -1 : 1;
                }

                return v0.IndexRightOrValue > v1.IndexRightOrValue ? -1 : v0.IndexRightOrValue < v1.IndexRightOrValue ? 1 : 0;
            });

            var sentinel = new Node(uint.MaxValue, -1, -1);
            tree.Add(sentinel);
            tree.Add(sentinel);
            int ii = 0;
            int j = n + 1;
            for (int k = n - 1; k != 0; --k)
            {
                int left, right;
                if (tree[ii].TotalCount <= tree[j].TotalCount)
                {
                    left = ii++;
                }
                else
                {
                    left = j++;
                }

                if (tree[ii].TotalCount <= tree[j].TotalCount)
                {
                    right = ii++;
                }
                else
                {
                    right = j++;
                }

                int jEnd = tree.Count - 1;
                tree[jEnd] = new Node(tree[left].TotalCount + tree[right].TotalCount, (short)left, (short)right);
                tree.Add(sentinel);
            }

            Node[] arr = tree.ToArray();
            SetDepth(arr[(2 * n) - 1], arr, depth, 0);
            int maxDepth = 0;
            for (int i = 0; i < length; i++)
            {
                maxDepth = Math.Max(maxDepth, depth[i]);
            }

            if (maxDepth <= treeLimit)
            {
                break;
            }
        }
    }

    private static void Reverse(byte[] v, int start, int end)
    {
        --end;
        while (start < end)
        {
            (v[start], v[end]) = (v[end], v[start]);
            ++start;
            --end;
        }
    }

    private static void WriteRepetitions(byte previousValue, byte value, int repetitions, ref int treeSize, byte[] tree, byte[] extra)
    {
        if (previousValue != value)
        {
            tree[treeSize] = value;
            extra[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }

        if (repetitions == 7)
        {
            tree[treeSize] = value;
            extra[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }

        if (repetitions < 3)
        {
            for (int i = 0; i < repetitions; i++)
            {
                tree[treeSize] = value;
                extra[treeSize] = 0;
                ++treeSize;
            }
        }
        else
        {
            repetitions -= 3;
            int start = treeSize;
            while (true)
            {
                tree[treeSize] = 16;
                extra[treeSize] = (byte)(repetitions & 0x3);
                ++treeSize;
                repetitions >>= 2;
                if (repetitions == 0)
                {
                    break;
                }

                --repetitions;
            }

            Reverse(tree, start, treeSize);
            Reverse(extra, start, treeSize);
        }
    }

    private static void WriteRepetitionsZeros(int repetitions, ref int treeSize, byte[] tree, byte[] extra)
    {
        if (repetitions == 11)
        {
            tree[treeSize] = 0;
            extra[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }

        if (repetitions < 3)
        {
            for (int i = 0; i < repetitions; i++)
            {
                tree[treeSize] = 0;
                extra[treeSize] = 0;
                ++treeSize;
            }
        }
        else
        {
            repetitions -= 3;
            int start = treeSize;
            while (true)
            {
                tree[treeSize] = 17;
                extra[treeSize] = (byte)(repetitions & 0x7);
                ++treeSize;
                repetitions >>= 3;
                if (repetitions == 0)
                {
                    break;
                }

                --repetitions;
            }

            Reverse(tree, start, treeSize);
            Reverse(extra, start, treeSize);
        }
    }

    private static void DecideOverRleUse(byte[] depth, int length, out bool useRleForNonZero, out bool useRleForZero)
    {
        int totalRepsZero = 0, totalRepsNonZero = 0, countRepsZero = 1, countRepsNonZero = 1;
        for (int i = 0; i < length;)
        {
            byte value = depth[i];
            int reps = 1;
            for (int k = i + 1; k < length && depth[k] == value; k++)
            {
                reps++;
            }

            if (reps >= 3 && value == 0)
            {
                totalRepsZero += reps;
                countRepsZero++;
            }

            if (reps >= 4 && value != 0)
            {
                totalRepsNonZero += reps;
                countRepsNonZero++;
            }

            i += reps;
        }

        useRleForNonZero = totalRepsNonZero > countRepsNonZero * 2;
        useRleForZero = totalRepsZero > countRepsZero * 2;
    }

    private static void WriteHuffmanTree(byte[] depth, int length, ref int treeSize, byte[] tree, byte[] extra)
    {
        byte previousValue = 8;
        int newLength = length;
        for (int i = 0; i < length; i++)
        {
            if (depth[length - i - 1] == 0)
            {
                --newLength;
            }
            else
            {
                break;
            }
        }

        bool useRleForNonZero = false, useRleForZero = false;
        if (length > 50)
        {
            DecideOverRleUse(depth, newLength, out useRleForNonZero, out useRleForZero);
        }

        for (int i = 0; i < newLength;)
        {
            byte value = depth[i];
            int reps = 1;
            if ((value != 0 && useRleForNonZero) || (value == 0 && useRleForZero))
            {
                for (int k = i + 1; k < newLength && depth[k] == value; k++)
                {
                    reps++;
                }
            }

            if (value == 0)
            {
                WriteRepetitionsZeros(reps, ref treeSize, tree, extra);
            }
            else
            {
                WriteRepetitions(previousValue, value, reps, ref treeSize, tree, extra);
                previousValue = value;
            }

            i += reps;
        }
    }

    private static ushort ReverseBits(int numBits, ushort bits)
    {
        ReadOnlySpan<int> lut = [0x0, 0x8, 0x4, 0xc, 0x2, 0xa, 0x6, 0xe, 0x1, 0x9, 0x5, 0xd, 0x3, 0xb, 0x7, 0xf];
        int retval = lut[bits & 0xf];
        for (int i = 4; i < numBits; i += 4)
        {
            retval <<= 4;
            bits = (ushort)(bits >> 4);
            retval |= lut[bits & 0xf];
        }

        retval >>= -numBits & 0x3;
        return (ushort)retval;
    }

    public static void ConvertBitDepthsToSymbols(byte[] depth, int len, ushort[] bits)
    {
        const int kMaxBits = 16;
        var blCount = new ushort[kMaxBits];
        for (int i = 0; i < len; i++)
        {
            blCount[depth[i]]++;
        }

        blCount[0] = 0;
        var nextCode = new ushort[kMaxBits];
        int code = 0;
        for (int i = 1; i < kMaxBits; i++)
        {
            code = (code + blCount[i - 1]) << 1;
            nextCode[i] = (ushort)code;
        }

        for (int i = 0; i < len; i++)
        {
            if (depth[i] != 0)
            {
                bits[i] = ReverseBits(depth[i], nextCode[depth[i]]++);
            }
        }
    }

    private const int CodeLengthCodes = 18;

    private static void StoreHuffmanTreeOfHuffmanTreeToBitMask(int numCodes, byte[] codeLengthBitdepth, LjBitWriter writer)
    {
        ReadOnlySpan<byte> kStorageOrder = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        ReadOnlySpan<byte> kSymbols = [0, 7, 3, 2, 1, 15];
        ReadOnlySpan<byte> kLengths = [2, 4, 3, 2, 2, 4];
        int codesToStore = CodeLengthCodes;
        if (numCodes > 1)
        {
            for (; codesToStore > 0; --codesToStore)
            {
                if (codeLengthBitdepth[kStorageOrder[codesToStore - 1]] != 0)
                {
                    break;
                }
            }
        }

        int skipSome = 0;
        if (codeLengthBitdepth[kStorageOrder[0]] == 0 && codeLengthBitdepth[kStorageOrder[1]] == 0)
        {
            skipSome = 2;
            if (codeLengthBitdepth[kStorageOrder[2]] == 0)
            {
                skipSome = 3;
            }
        }

        writer.Write(2, (ulong)skipSome);
        for (int i = skipSome; i < codesToStore; i++)
        {
            int l = codeLengthBitdepth[kStorageOrder[i]];
            writer.Write(kLengths[l], kSymbols[l]);
        }
    }

    private static void StoreSimpleHuffmanTree(byte[] depths, int[] symbols, int numSymbols, int maxBits, LjBitWriter writer)
    {
        writer.Write(2, 1);
        writer.Write(2, (ulong)(numSymbols - 1));
        for (int i = 0; i < numSymbols; i++)
        {
            for (int j = i + 1; j < numSymbols; j++)
            {
                if (depths[symbols[j]] < depths[symbols[i]])
                {
                    (symbols[j], symbols[i]) = (symbols[i], symbols[j]);
                }
            }
        }

        for (int i = 0; i < numSymbols; i++)
        {
            writer.Write(maxBits, (ulong)symbols[i]);
        }

        if (numSymbols == 4)
        {
            writer.Write(1, depths[symbols[0]] == 1 ? 1u : 0u);
        }
    }

    private static void StoreHuffmanTree(byte[] depths, int num, LjBitWriter writer)
    {
        var tree = new byte[2 * num];
        var extra = new byte[2 * num];
        int treeSize = 0;
        WriteHuffmanTree(depths, num, ref treeSize, tree, extra);
        var histogram = new uint[CodeLengthCodes];
        for (int i = 0; i < treeSize; i++)
        {
            histogram[tree[i]]++;
        }

        int numCodes = 0, code = 0;
        for (int i = 0; i < CodeLengthCodes; i++)
        {
            if (histogram[i] != 0)
            {
                if (numCodes == 0)
                {
                    code = i;
                    numCodes = 1;
                }
                else if (numCodes == 1)
                {
                    numCodes = 2;
                    break;
                }
            }
        }

        var clDepth = new byte[CodeLengthCodes];
        var clSymbols = new ushort[CodeLengthCodes];
        CreateHuffmanTree(histogram, CodeLengthCodes, 5, clDepth);
        ConvertBitDepthsToSymbols(clDepth, CodeLengthCodes, clSymbols);
        StoreHuffmanTreeOfHuffmanTreeToBitMask(numCodes, clDepth, writer);
        if (numCodes == 1)
        {
            clDepth[code] = 0;
        }

        for (int i = 0; i < treeSize; i++)
        {
            int ix = tree[i];
            writer.Write(clDepth[ix], clSymbols[ix]);
            if (ix == 16)
            {
                writer.Write(2, extra[i]);
            }
            else if (ix == 17)
            {
                writer.Write(3, extra[i]);
            }
        }
    }

    public static void BuildAndStoreHuffmanTree(uint[] histogram, int length, byte[] depth, ushort[] bits, LjBitWriter writer)
    {
        int count = 0;
        var s4 = new int[4];
        for (int i = 0; i < length; i++)
        {
            if (histogram[i] != 0)
            {
                if (count < 4)
                {
                    s4[count] = i;
                }
                else if (count > 4)
                {
                    break;
                }

                count++;
            }
        }

        int maxBitsCounter = length - 1;
        int maxBits = 0;
        while (maxBitsCounter != 0)
        {
            maxBitsCounter >>= 1;
            maxBits++;
        }

        if (count <= 1)
        {
            writer.Write(4, 1);
            writer.Write(maxBits, (ulong)s4[0]);
            return;
        }

        CreateHuffmanTree(histogram, length, 15, depth);
        ConvertBitDepthsToSymbols(depth, length, bits);
        if (count <= 4)
        {
            StoreSimpleHuffmanTree(depth, s4, count, maxBits, writer);
        }
        else
        {
            StoreHuffmanTree(depth, length, writer);
        }
    }
}
