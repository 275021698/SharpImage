// libjxl 0.12 MA tree learning (enc_ma.cc TreeSamples / ComputeBestTree / FindBestSplit, enc_encoding.cc
// GatherTreeData / LearnTree, CollectPixelSamples), ported line by line. FindBestSplit's EstimateBits runs in
// libjxl's AVX2 dispatch: 8-lane sums and fused FastLog2f, reproduced here.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjTreeSamples
{
    private readonly List<byte>[] residualTok;
    private readonly List<byte>[] residualNbits;
    private List<byte>[] ResidualTok => residualTok;
    private readonly List<ushort> sampleCounts = new();
    private int numStaticProps;
    private readonly List<uint>[] staticProps = [new(), new()];
    private List<byte>[] props = Array.Empty<List<byte>>();
    private List<int>[] compactProperties = Array.Empty<List<int>>();
    private uint[] propsToUse = Array.Empty<uint>();
    private readonly LjPredictor[] predictors;
    private const int PropertyRange = 511;
    private readonly ushort[][] staticPropertyMapping = new ushort[2][];
    private byte[][] propertyMapping = Array.Empty<byte[]>();
    private long numSamples;
    private uint[] dedupTable = Array.Empty<uint>();
    private const uint DedupEntryUnused = uint.MaxValue;

    public LjTreeSamples(LjPredictor predictor, LjModularOptions.TreeMode wpTreeMode, List<uint> properties)
    {
        // SetPredictor
        List<LjPredictor> preds;
        if (wpTreeMode == LjModularOptions.TreeMode.WpOnly)
        {
            preds = [LjPredictor.Weighted];
        }
        else
        {
            if (wpTreeMode == LjModularOptions.TreeMode.NoWp && predictor == LjPredictor.Weighted)
            {
                throw new InvalidOperationException("Invalid predictor settings");
            }

            if (predictor == LjPredictor.Variable)
            {
                preds = new List<LjPredictor>();
                for (int i = 0; i < LjPredict.NumModularPredictors; i++)
                {
                    preds.Add((LjPredictor)i);
                }

                (preds[0], preds[(int)LjPredictor.Weighted]) = (preds[(int)LjPredictor.Weighted], preds[0]);
                (preds[1], preds[(int)LjPredictor.Gradient]) = (preds[(int)LjPredictor.Gradient], preds[1]);
            }
            else if (predictor == LjPredictor.Best)
            {
                preds = [LjPredictor.Weighted, LjPredictor.Gradient];
            }
            else
            {
                preds = [predictor];
            }

            if (wpTreeMode == LjModularOptions.TreeMode.NoWp)
            {
                preds.RemoveAll(p => p == LjPredictor.Weighted);
            }
        }

        predictors = preds.ToArray();
        residualTok = new List<byte>[predictors.Length];
        residualNbits = new List<byte>[predictors.Length];
        for (int i = 0; i < predictors.Length; i++)
        {
            residualTok[i] = new List<byte>();
            residualNbits[i] = new List<byte>();
        }

        // SetProperties
        var p2u = new List<uint>(properties);
        if (wpTreeMode == LjModularOptions.TreeMode.WpOnly)
        {
            p2u = [(uint)LjPredict.WpProp];
        }

        if (wpTreeMode == LjModularOptions.TreeMode.GradientOnly)
        {
            p2u = [(uint)LjPredict.GradientProp];
        }

        if (wpTreeMode == LjModularOptions.TreeMode.NoWp)
        {
            p2u.RemoveAll(p => p == LjPredict.WpProp);
        }

        if (p2u.Count == 0)
        {
            throw new InvalidOperationException("Invalid property set configuration");
        }

        propsToUse = p2u.ToArray();
        numStaticProps = 0;
        for (int i = 0; i < propsToUse.Length; i++)
        {
            if (propsToUse[i] < LjPredict.NumStaticProperties)
            {
                numStaticProps++;
            }
        }

        props = new List<byte>[propsToUse.Length - numStaticProps];
        for (int i = 0; i < props.Length; i++)
        {
            props[i] = new List<byte>();
        }
    }

    public bool HasSamples => residualTok.Length > 0 && residualTok[0].Count > 0;

    public int NumDistinctSamples => sampleCounts.Count;

    public long NumSamples => numSamples;

    public int NumPredictors => predictors.Length;

    public int NumProperties => propsToUse.Length;

    public int NumStaticProps => numStaticProps;

    public LjPredictor PredictorFromIndex(int i) => predictors[i];

    public int PredictorIndex(LjPredictor p) => Array.IndexOf(predictors, p);

    public uint PropertyFromIndex(int i) => propsToUse[i];

    public int NumPropertyValues(int i) => compactProperties[i].Count + 1;

    public int Token(int pred, int i) => residualTok[pred][i];

    public int Nbits(int pred, int i) => residualNbits[pred][i];

    public int Count(int i) => sampleCounts[i];

    public int Property(bool isStatic, int propIndex, int i) => isStatic ? (int)staticProps[propIndex][i] : props[propIndex][i];

    public int UnquantizeProperty(int i, int quant) => compactProperties[i][quant];

    public uint QuantizeProperty(int prop, int v)
    {
        v = Math.Clamp(v, -PropertyRange, PropertyRange) + PropertyRange;
        return propertyMapping[prop - numStaticProps][v];
    }

    public uint QuantizeStaticProperty(int prop, int v)
    {
        v = Math.Clamp(v, -PropertyRange, PropertyRange) + PropertyRange;
        return staticPropertyMapping[prop][v];
    }

    private void InitTable(int logSize)
    {
        int size = 1 << logSize;
        if (dedupTable.Length == size)
        {
            return;
        }

        // std::vector::resize keeps existing entries and fills new ones with "unused".
        int old = dedupTable.Length;
        Array.Resize(ref dedupTable, size);
        for (int i = old; i < size; i++)
        {
            dedupTable[i] = DedupEntryUnused;
        }

        for (int i = 0; i < NumDistinctSamples; i++)
        {
            if (sampleCounts[i] != ushort.MaxValue)
            {
                AddToTable(i);
            }
        }
    }

    private bool IsSameSample(int a, int b)
    {
        for (int r = 0; r < residualTok.Length; r++)
        {
            if (residualTok[r][a] != residualTok[r][b] || residualNbits[r][a] != residualNbits[r][b])
            {
                return false;
            }
        }

        for (int i = 0; i < numStaticProps; i++)
        {
            if (staticProps[i][a] != staticProps[i][b])
            {
                return false;
            }
        }

        foreach (List<byte> p in props)
        {
            if (p[a] != p[b])
            {
                return false;
            }
        }

        return true;
    }

    private int Hash1(int a)
    {
        const ulong constant = 0x1e35a7bd;
        ulong h = constant;
        for (int r = 0; r < residualTok.Length; r++)
        {
            h = (h * constant) + residualTok[r][a];
            h = (h * constant) + residualNbits[r][a];
        }

        for (int i = 0; i < numStaticProps; i++)
        {
            h = (h * constant) + staticProps[i][a];
        }

        foreach (List<byte> p in props)
        {
            h = (h * constant) + p[a];
        }

        return (int)((h >> 16) & (ulong)(dedupTable.Length - 1));
    }

    private int Hash2(int a)
    {
        const ulong constant = 0x1e35a7bd1e35a7bd;
        ulong h = constant;
        for (int i = 0; i < numStaticProps; i++)
        {
            h = (h * constant) ^ staticProps[i][a];
        }

        foreach (List<byte> p in props)
        {
            h = (h * constant) ^ p[a];
        }

        for (int r = 0; r < residualTok.Length; r++)
        {
            h = (h * constant) ^ residualTok[r][a];
            h = (h * constant) ^ residualNbits[r][a];
        }

        return (int)((h >> 16) & (ulong)(dedupTable.Length - 1));
    }

    private bool AddToTableAndMerge(int a)
    {
        int pos1 = Hash1(a);
        int pos2 = Hash2(a);
        if (dedupTable[pos1] != DedupEntryUnused && IsSameSample(a, (int)dedupTable[pos1]))
        {
            sampleCounts[(int)dedupTable[pos1]]++;
            if (sampleCounts[(int)dedupTable[pos1]] == ushort.MaxValue)
            {
                dedupTable[pos1] = DedupEntryUnused;
            }

            return true;
        }

        if (dedupTable[pos2] != DedupEntryUnused && IsSameSample(a, (int)dedupTable[pos2]))
        {
            sampleCounts[(int)dedupTable[pos2]]++;
            if (sampleCounts[(int)dedupTable[pos2]] == ushort.MaxValue)
            {
                dedupTable[pos2] = DedupEntryUnused;
            }

            return true;
        }

        AddToTable(a);
        return false;
    }

    private void AddToTable(int a)
    {
        int pos1 = Hash1(a);
        int pos2 = Hash2(a);
        if (dedupTable[pos1] == DedupEntryUnused)
        {
            dedupTable[pos1] = (uint)a;
        }
        else if (dedupTable[pos2] == DedupEntryUnused)
        {
            dedupTable[pos2] = (uint)a;
        }
    }

    public void PrepareForSamples(long extraNumSamples)
    {
        long total = extraNumSamples + sampleCounts.Count;
        int nextSize = LjMath.CeilLog2Nonzero((ulong)(total * 3 / 2));
        InitTable(nextSize);
    }

    private static readonly LjHybridUint SampleUint = new(4, 1, 2);

    public void AddSample(long pixel, int[] properties, long[] predictions)
    {
        for (int i = 0; i < predictors.Length; i++)
        {
            int v = (int)(pixel - predictions[(int)predictors[i]]);
            SampleUint.Encode(LjMath.PackSigned(v), out uint tok, out uint nbits, out _);
            residualTok[i].Add((byte)tok);
            residualNbits[i].Add((byte)nbits);
        }

        for (int i = 0; i < numStaticProps; i++)
        {
            staticProps[i].Add(QuantizeStaticProperty(i, properties[i]));
        }

        for (int i = numStaticProps; i < propsToUse.Length; i++)
        {
            props[i - numStaticProps].Add((byte)QuantizeProperty(i, properties[propsToUse[i]]));
        }

        sampleCounts.Add(1);
        numSamples++;
        if (AddToTableAndMerge(sampleCounts.Count - 1))
        {
            for (int r = 0; r < residualTok.Length; r++)
            {
                residualTok[r].RemoveAt(residualTok[r].Count - 1);
                residualNbits[r].RemoveAt(residualNbits[r].Count - 1);
            }

            for (int i = 0; i < numStaticProps; i++)
            {
                staticProps[i].RemoveAt(staticProps[i].Count - 1);
            }

            foreach (List<byte> p in props)
            {
                p.RemoveAt(p.Count - 1);
            }

            sampleCounts.RemoveAt(sampleCounts.Count - 1);
        }
    }

    public void AllSamplesDone() => dedupTable = Array.Empty<uint>();

    public void Swap(int a, int b)
    {
        if (a == b)
        {
            return;
        }

        for (int r = 0; r < residualTok.Length; r++)
        {
            (residualTok[r][a], residualTok[r][b]) = (residualTok[r][b], residualTok[r][a]);
            (residualNbits[r][a], residualNbits[r][b]) = (residualNbits[r][b], residualNbits[r][a]);
        }

        for (int i = 0; i < numStaticProps; i++)
        {
            (staticProps[i][a], staticProps[i][b]) = (staticProps[i][b], staticProps[i][a]);
        }

        foreach (List<byte> p in props)
        {
            (p[a], p[b]) = (p[b], p[a]);
        }

        (sampleCounts[a], sampleCounts[b]) = (sampleCounts[b], sampleCounts[a]);
    }

    private static List<int> QuantizeHistogram(List<uint> histogram, int numChunks)
    {
        if (histogram.Count == 0 || numChunks == 0)
        {
            return new List<int>();
        }

        ulong sum = 0;
        foreach (uint h in histogram)
        {
            sum += h;
        }

        if (sum == 0)
        {
            return new List<int>();
        }

        var thresholds = new List<int>();
        ulong cumsum = 0;
        ulong threshold = 1;
        for (int i = 0; i < histogram.Count; i++)
        {
            cumsum += histogram[i];
            if (cumsum * (ulong)numChunks >= threshold * sum)
            {
                thresholds.Add(i);
                while (cumsum * (ulong)numChunks >= threshold * sum)
                {
                    threshold++;
                }
            }
        }

        thresholds.RemoveAt(thresholds.Count - 1);
        return thresholds;
    }

    private static List<int> QuantizeSamples(List<int> samples, int numChunks)
    {
        if (samples.Count == 0)
        {
            return new List<int>();
        }

        int min = int.MaxValue;
        foreach (int s in samples)
        {
            min = Math.Min(min, s);
        }

        const int kRange = 512;
        min = Math.Clamp(min, -kRange, kRange);
        var counts = new List<uint>(new uint[(2 * kRange) + 1]);
        foreach (int s in samples)
        {
            uint off = (uint)(Math.Clamp(s, -kRange, kRange) - min);
            counts[(int)off]++;
        }

        List<int> thresholds = QuantizeHistogram(counts, numChunks);
        for (int i = 0; i < thresholds.Count; i++)
        {
            thresholds[i] += min;
        }

        return thresholds;
    }

    private static T[] QuantMap<T>(List<int> from, int numPegs, int bias, Func<int, T> conv)
    {
        var to = new T[numPegs];
        int mapped = 0;
        for (int i = 0; i < numPegs; i++)
        {
            while (mapped < from.Count && i - bias > from[mapped])
            {
                mapped++;
            }

            to[i] = conv(mapped);
        }

        return to;
    }

    public void PreQuantizeProperties((uint Lo, uint Hi)[] range, List<uint> groupPixelCount, List<uint> channelPixelCount,
        List<int> pixelSamples, List<int> diffSamples, int maxPropertyValues)
    {
        compactProperties = new List<int>[propsToUse.Length];
        List<int> QuantizeCoordinate()
        {
            var q = new List<int>(maxPropertyValues - 1);
            for (int i = 0; i + 1 < maxPropertyValues; i++)
            {
                q.Add(((i + 1) * 256 / maxPropertyValues) - 1);
            }

            return q;
        }

        List<int> absPixelThresholds = new(), pixelThresholds = new(), absDiffThresholds = new(), diffThresholds = new();
        List<int> QuantizePixel()
        {
            if (pixelThresholds.Count == 0)
            {
                pixelThresholds = QuantizeSamples(pixelSamples, maxPropertyValues);
            }

            return pixelThresholds;
        }

        List<int> QuantizeAbsPixel()
        {
            if (absPixelThresholds.Count == 0)
            {
                QuantizePixel();
                for (int i = 0; i < pixelSamples.Count; i++)
                {
                    pixelSamples[i] = Math.Abs(pixelSamples[i]);
                }

                absPixelThresholds = QuantizeSamples(pixelSamples, maxPropertyValues);
            }

            return absPixelThresholds;
        }

        List<int> QuantizeDiff()
        {
            if (diffThresholds.Count == 0)
            {
                diffThresholds = QuantizeSamples(diffSamples, maxPropertyValues);
            }

            return diffThresholds;
        }

        List<int> QuantizeAbsDiff()
        {
            if (absDiffThresholds.Count == 0)
            {
                QuantizeDiff();
                for (int i = 0; i < diffSamples.Count; i++)
                {
                    diffSamples[i] = Math.Abs(diffSamples[i]);
                }

                absDiffThresholds = QuantizeSamples(diffSamples, maxPropertyValues);
            }

            return absDiffThresholds;
        }

        List<int> QuantizeWp()
        {
            if (maxPropertyValues < 32)
            {
                return [-127, -63, -31, -15, -7, -3, -1, 0, 1, 3, 7, 15, 31, 63, 127];
            }

            if (maxPropertyValues < 64)
            {
                return [-255, -191, -127, -95, -63, -47, -31, -23, -15, -11, -7, -5, -3, -1, 0, 1, 3, 5, 7, 11, 15, 23, 31, 47, 63, 95, 127, 191, 255];
            }

            return
            [
                -255, -223, -191, -159, -127, -111, -95, -79, -63, -55, -47, -39, -31, -27, -23, -19, -15, -13, -11, -9, -7, -6,
                -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 15, 19, 23, 27, 31, 39, 47, 55, 63, 79, 95, 111, 127, 159,
                191, 223, 255,
            ];
        }

        propertyMapping = new byte[propsToUse.Length - numStaticProps][];
        for (int i = 0; i < propsToUse.Length; i++)
        {
            uint p = propsToUse[i];
            const int nr = LjPredict.NumNonrefProperties;
            if (p == 0)
            {
                compactProperties[i] = QuantizeHistogram(channelPixelCount, maxPropertyValues);
            }
            else if (p == 1)
            {
                compactProperties[i] = QuantizeHistogram(groupPixelCount, maxPropertyValues);
            }
            else if (p == 2 || p == 3)
            {
                compactProperties[i] = QuantizeCoordinate();
            }
            else if (p == 6 || p == 7 || p == 8 || (p >= nr && (p - nr) % 4 == 1))
            {
                compactProperties[i] = QuantizePixel();
            }
            else if (p == 4 || p == 5 || (p >= nr && (p - nr) % 4 == 0))
            {
                compactProperties[i] = QuantizeAbsPixel();
            }
            else if (p >= nr && (p - nr) % 4 == 2)
            {
                compactProperties[i] = QuantizeAbsDiff();
            }
            else if (p == LjPredict.WpProp)
            {
                compactProperties[i] = QuantizeWp();
            }
            else
            {
                compactProperties[i] = QuantizeDiff();
            }

            if (i < numStaticProps)
            {
                staticPropertyMapping[i] = QuantMap(compactProperties[i], (PropertyRange * 2) + 1, PropertyRange, m => (ushort)m);
            }
            else
            {
                propertyMapping[i - numStaticProps] = QuantMap(compactProperties[i], (PropertyRange * 2) + 1, PropertyRange, m => (byte)m);
            }
        }
    }
}

internal static class LjMaLearn
{
    /// <summary>libjxl's xorshift128+ Rng.</summary>
    private sealed class Rng
    {
        private ulong s0, s1;

        public Rng(ulong seed)
        {
            s0 = 0x94D049BB133111EBul;
            s1 = 0xBF58476D1CE4E5B9ul + seed;
        }

        public ulong Next()
        {
            ulong a = s0;
            ulong b = s1;
            ulong bits = a + b;
            s0 = b;
            a ^= a << 23;
            a ^= b ^ (a >> 18) ^ (b >> 5);
            s1 = a;
            return bits;
        }

        public float UniformF(float begin, float end)
        {
            uint u = (uint)(Next() >> (64 - 23)) | 0x3F800000;
            float f = BitConverter.UInt32BitsToSingle(u);
            return ((end - begin) * (f - 1.0f)) + begin;
        }

        public static float MakeGeometric(float p) => (float)(1.0 / MathF.Log(1 - p));

        public uint Geometric(float dist)
        {
            float f = UniformF(0, 1);
            float log = MathF.Log(1 - f) * dist;
            return (uint)log;
        }
    }

    public static void CollectPixelSamples(LjModularImage image, LjModularOptions options, int groupId, List<uint> groupPixelCount,
        List<uint> channelPixelCount, List<int> pixelSamples, List<int> diffSamples)
    {
        if (options.NbRepeats == 0)
        {
            return;
        }

        while (groupPixelCount.Count <= groupId)
        {
            groupPixelCount.Add(0);
        }

        while (channelPixelCount.Count < image.Channel.Count)
        {
            channelPixelCount.Add(0);
        }

        var rng = new Rng((ulong)groupId);
        float fraction = (float)Math.Min(options.NbRepeats * 0.1, 0.99);
        float dist = Rng.MakeGeometric(fraction);
        var channelIds = new List<int>();
        for (int i = 0; i < image.Channel.Count; i++)
        {
            if (i >= image.NbMetaChannels && (image.Channel[i].W > options.MaxChanSize || image.Channel[i].H > options.MaxChanSize))
            {
                break;
            }

            if (image.Channel[i].W <= 1 || image.Channel[i].H == 0)
            {
                continue;
            }

            channelIds.Add(i);
            groupPixelCount[groupId] += (uint)(image.Channel[i].W * image.Channel[i].H);
            channelPixelCount[i] += (uint)(image.Channel[i].W * image.Channel[i].H);
        }

        if (channelIds.Count == 0)
        {
            return;
        }

        int ci = 0, y = 0;
        long x = 0;
        void Advance(long amount)
        {
            x += amount;
            while (x >= image.Channel[channelIds[ci]].W)
            {
                x -= image.Channel[channelIds[ci]].W;
                y++;
                if (y == image.Channel[channelIds[ci]].H)
                {
                    ci++;
                    y = 0;
                    if (ci >= channelIds.Count)
                    {
                        return;
                    }
                }
            }
        }

        Advance(rng.Geometric(dist));
        for (; ci < channelIds.Count; Advance(rng.Geometric(dist) + 1L))
        {
            LjChannel ch = image.Channel[channelIds[ci]];
            int v = ch[(int)x, y];
            pixelSamples.Add(v);
            int xp = x == 0 ? 1 : (int)x - 1;
            diffSamples.Add((int)((long)v - ch[xp, y]));
        }
    }

    private static void GatherTreeData(LjModularImage image, int chan, int groupId, LjWpHeader wpHeader, LjModularOptions options,
        LjTreeSamples samples, ref long totalPixels)
    {
        LjChannel channel = image.Channel[chan];
        int numProps = LjPredict.NumNonrefProperties + (LjPredict.ExtraPropsPerChannel * options.MaxProperties);
        var properties = new int[numProps];
        double pixelFraction = Math.Min(1.0f, options.NbRepeats);
        if (pixelFraction > 0)
        {
            pixelFraction = Math.Max(pixelFraction, Math.Min(1.0, 1024.0 / (channel.W * channel.H)));
        }

        ulong threshold = (ulong)((ulong.MaxValue >> 32) * pixelFraction);
        ulong s0 = 0x94D049BB133111EBul, s1 = 0xBF58476D1CE4E5B9ul;
        bool UseSample()
        {
            ulong a = s0;
            ulong b = s1;
            ulong bits = a + b;
            s0 = b;
            a ^= a << 23;
            a ^= b ^ (a >> 18) ^ (b >> 5);
            s1 = a;
            return (bits >> 32) <= threshold;
        }

        int numExtra = numProps - LjPredict.NumNonrefProperties;
        int[]? references = numExtra > 0 ? new int[channel.W * numExtra] : null;
        var wp = new LjWpState(wpHeader, channel.W, channel.H);
        samples.PrepareForSamples((long)((pixelFraction * channel.H * channel.W) + 64));
        bool multiple = samples.NumPredictors != 1;
        var pred = new long[LjPredict.NumModularPredictors];
        for (int y = 0; y < channel.H; y++)
        {
            if (references != null)
            {
                LjPredict.PrecomputeReferences(channel, y, image, chan, references, numExtra);
            }

            LjPredict.InitPropsRow(properties, chan, groupId, y);
            bool necRow = y > 1 && channel.W > 8 && numExtra == 0;
            for (int x = 0; x < channel.W; x++)
            {
                bool nec = necRow && x >= 2 && x < channel.W - 2;
                if (multiple)
                {
                    LjPredict.Predict(properties, channel, x, y, LjPredictor.Zero, wp, references, numExtra, pred, nec);
                }
                else
                {
                    LjPredictor p0 = samples.PredictorFromIndex(0);
                    pred[(int)p0] = LjPredict.Predict(properties, channel, x, y, p0, wp, references, numExtra, null, nec);
                }

                totalPixels++;
                if (UseSample())
                {
                    samples.AddSample(channel[x, y], properties, pred);
                }

                wp.UpdateErrors(channel[x, y], x, y, channel.W);
            }
        }
    }

    /// <summary>LearnTree over streams [start, stop) (enc_encoding.cc).</summary>
    public static List<LjTreeNode> LearnTree(List<LjModularImage> images, List<LjModularOptions> options, int start, int stop)
    {
        LjModularOptions o0 = options[start];
        var samples = new LjTreeSamples(o0.Predictor, o0.WpTreeMode, o0.SplittingHeuristicsProperties);
        int maxC = 0;
        var pixelSamples = new List<int>();
        var diffSamples = new List<int>();
        var groupPixelCount = new List<uint>();
        var channelPixelCount = new List<uint>();
        for (int i = start; i < stop; i++)
        {
            maxC = Math.Max(images[i].Channel.Count, maxC);
            CollectPixelSamples(images[i], options[i], i, groupPixelCount, channelPixelCount, pixelSamples, diffSamples);
        }

        (uint, uint)[] range = [(0u, (uint)maxC), ((uint)start, (uint)stop)];
        samples.PreQuantizeProperties(range, groupPixelCount, channelPixelCount, pixelSamples, diffSamples, o0.MaxPropertyValues);

        long totalPixels = 0;
        for (int i = 0; i < images[start].Channel.Count; i++)
        {
            if (i >= images[start].NbMetaChannels && (images[start].Channel[i].W > o0.MaxChanSize || images[start].Channel[i].H > o0.MaxChanSize))
            {
                break;
            }

            totalPixels += (long)images[start].Channel[i].W * images[start].Channel[i].H;
        }

        totalPixels = Math.Max(totalPixels, 1);
        for (int i = start; i < stop; i++)
        {
            int nb = images[i].Channel.Count;
            if (images[i].W == 0 || images[i].H == 0 || nb < 1)
            {
                continue;
            }

            LjWpHeader wpHeader = LjModularCompress.PredictorHasWeighted(options[i].Predictor) ? LjWpHeader.ForMode(options[i].WpMode) : new LjWpHeader();
            for (int c = 0; c < nb; c++)
            {
                if (c >= images[i].NbMetaChannels && (images[i].Channel[c].W > options[i].MaxChanSize || images[i].Channel[c].H > options[i].MaxChanSize))
                {
                    break;
                }

                if (images[i].Channel[c].W == 0 || images[i].Channel[c].H == 0)
                {
                    continue;
                }

                GatherTreeData(images[i], c, i, wpHeader, options[i], samples, ref totalPixels);
            }
        }

        // LearnTree(TreeSamples&&, ...)
        var tree = new List<LjTreeNode>();
        var spRange = new (uint Lo, uint Hi)[] { range[0], range[1] };
        for (int i = 0; i < 2; i++)
        {
            if (spRange[i].Hi == 0)
            {
                spRange[i].Hi = uint.MaxValue;
            }
        }

        if (!samples.HasSamples)
        {
            tree.Add(LjTreeNode.Leaf(samples.PredictorFromIndex(0)));
            return tree;
        }

        float pixelFrac = samples.NumSamples * 1.0f / totalPixels;
        float requiredCost = (float)((pixelFrac * 0.9) + 0.1);
        samples.AllSamplesDone();
        tree.Add(LjTreeNode.Leaf(samples.PredictorFromIndex(0)));
        FindBestSplit(samples, o0.SplittingHeuristicsNodeThreshold * requiredCost, spRange, o0.FastDecodeMultiplier, tree);
        return tree;
    }

    private static int Padded(int x) => (x + 7) / 8 * 8;

    // EstimateBits (AVX2, 8 lanes, FMA FastLog2f).
    private static float EstimateBits(int[] counts, int off, int numSymbols)
    {
        Span<int> totalV = stackalloc int[8];
        for (int i = 0; i < numSymbols; i += 8)
        {
            for (int l = 0; l < 8; l++)
            {
                totalV[l] += counts[off + i + l];
            }
        }

        // SumOfLanes on integers is exact.
        int total = 0;
        for (int l = 0; l < 8; l++)
        {
            total += totalV[l];
        }

        float minprob = 1.0f / LjAns.TabSize;
        float invTotal = 1.0f / total;
        Span<float> bits = stackalloc float[8];
        for (int i = 0; i < numSymbols; i += 8)
        {
            for (int l = 0; l < 8; l++)
            {
                int ci = counts[off + i + l];
                float cf = ci;
                float probs = cf * invTotal;
                float mprobs = MathF.Max(probs, minprob);
                float nbps = ci == total ? 0f : LjMath.FastLog2f(mprobs, true);
                bits[l] = bits[l] - (cf * nbps);
            }
        }

        return LjMath.SumOfLanes8(bits);
    }

    private struct SplitInfo
    {
        public int Prop;
        public int Val;
        public int Pos;
        public float LCost, RCost;
        public LjPredictor LPred, RPred;

        public static SplitInfo New() => new() { LCost = float.MaxValue, RCost = float.MaxValue };

        public readonly float Cost => LCost + RCost;
    }

    private struct CostInfo
    {
        public float CostV;
        public float ExtraCost;
        public LjPredictor Pred;

        public readonly float Cost => CostV + ExtraCost;
    }

    private static void FindBestSplit(LjTreeSamples ts, float threshold, (uint Lo, uint Hi)[] initialRange, float fastDecodeMultiplier, List<LjTreeNode> tree)
    {
        var nodes = new List<(int Pos, int Begin, int End, (uint Lo, uint Hi)[] Range)>
        {
            (0, 0, ts.NumDistinctSamples, initialRange),
        };
        int numPredictors = ts.NumPredictors;
        int numProperties = ts.NumProperties;
        while (nodes.Count > 0)
        {
            var nb = nodes[^1];
            nodes.RemoveAt(nodes.Count - 1);
            int pos = nb.Pos, begin = nb.Begin, end = nb.End;
            var staticPropRange = nb.Range;
            if (begin == end)
            {
                continue;
            }

            SplitInfo bestStaticConstant = SplitInfo.New(), bestStatic = SplitInfo.New(), bestNonstatic = SplitInfo.New(), bestNowp = SplitInfo.New();
            int maxSymbols = 0;
            for (int p = 0; p < numPredictors; p++)
            {
                for (int i = begin; i < end; i++)
                {
                    int tok = ts.Token(p, i);
                    maxSymbols = maxSymbols > tok + 1 ? maxSymbols : tok + 1;
                }
            }

            maxSymbols = Padded(maxSymbols);
            var counts = new int[maxSymbols * numPredictors];
            var totExtraBits = new uint[numPredictors];
            for (int p = 0; p < numPredictors; p++)
            {
                long extra = 0;
                for (int i = begin; i < end; i++)
                {
                    int cnt = ts.Count(i);
                    counts[(p * maxSymbols) + ts.Token(p, i)] += cnt;
                    extra += ts.Nbits(p, i) * cnt;
                }

                totExtraBits[p] = (uint)extra;
            }

            float baseBits;
            {
                int p = ts.PredictorIndex(tree[pos].Predictor);
                baseBits = EstimateBits(counts, p * maxSymbols, maxSymbols) + totExtraBits[p];
            }

            int bestKind = 2; // 0 static-constant, 1 static, 2 nonstatic, 3 nowp
            {
                var propValueUsedCount = new List<int>();
                var countIncrease = new List<int>();
                var extraBitsIncrease = new List<long>();
                var costsL = new List<CostInfo>();
                var costsR = new List<CostInfo>();
                var countsAbove = new int[maxSymbols];
                var countsBelow = new int[maxSymbols];
                float changePredPenalty = 800.0f / (100.0f + threshold);
                for (int prop = 0; prop < numProperties && baseBits > threshold; prop++)
                {
                    costsL.Clear();
                    costsR.Clear();
                    int propSize = ts.NumPropertyValues(prop);
                    if (extraBitsIncrease.Count < propSize)
                    {
                        while (countIncrease.Count < propSize * maxSymbols)
                        {
                            countIncrease.Add(0);
                        }

                        while (extraBitsIncrease.Count < propSize)
                        {
                            extraBitsIncrease.Add(0);
                        }
                    }

                    propValueUsedCount.Clear();
                    for (int i = 0; i < propSize; i++)
                    {
                        propValueUsedCount.Add(0);
                    }

                    int firstUsed = propSize, lastUsed = 0;
                    bool isStatic = prop < ts.NumStaticProps;
                    int propIdx = isStatic ? prop : prop - ts.NumStaticProps;
                    for (int i = begin; i < end; i++)
                    {
                        int pv = ts.Property(isStatic, propIdx, i);
                        propValueUsedCount[pv]++;
                        lastUsed = Math.Max(lastUsed, pv);
                        firstUsed = Math.Min(firstUsed, pv);
                    }

                    for (int i = 0; i < lastUsed - firstUsed; i++)
                    {
                        costsL.Add(new CostInfo { CostV = float.MaxValue });
                        costsR.Add(new CostInfo { CostV = float.MaxValue });
                    }

                    for (int p = 0; p < numPredictors; p++)
                    {
                        for (int i2 = begin; i2 < end; i2++)
                        {
                            int cnt = ts.Count(i2);
                            int pv = ts.Property(isStatic, propIdx, i2);
                            countIncrease[(pv * maxSymbols) + ts.Token(p, i2)] += cnt;
                            extraBitsIncrease[pv] += ts.Nbits(p, i2) * cnt;
                        }

                        Array.Copy(counts, p * maxSymbols, countsAbove, 0, maxSymbols);
                        Array.Clear(countsBelow);
                        long extraBitsBelow = 0;
                        for (int i = firstUsed; i < lastUsed; i++)
                        {
                            if (propValueUsedCount[i] == 0)
                            {
                                continue;
                            }

                            extraBitsBelow += extraBitsIncrease[i];
                            extraBitsIncrease[i] = 0;
                            for (int sym = 0; sym < maxSymbols; sym++)
                            {
                                countsAbove[sym] -= countIncrease[(i * maxSymbols) + sym];
                                countsBelow[sym] += countIncrease[(i * maxSymbols) + sym];
                                countIncrease[(i * maxSymbols) + sym] = 0;
                            }

                            float rcost = (EstimateBits(countsAbove, 0, maxSymbols) + totExtraBits[p]) - (float)extraBitsBelow;
                            float lcost = EstimateBits(countsBelow, 0, maxSymbols) + (float)extraBitsBelow;
                            float penalty = 0;
                            if (ts.PredictorFromIndex(p) != tree[pos].Predictor && tree[pos].Predictor != LjPredictor.Weighted)
                            {
                                penalty = changePredPenalty;
                            }

                            if (ts.PredictorFromIndex(p) == LjPredictor.Weighted)
                            {
                                penalty = (float)(penalty + 1e-8);
                            }

                            if (ts.PredictorFromIndex(p) == LjPredictor.Zero)
                            {
                                penalty = (float)(penalty - 1e-8);
                            }

                            CostInfo cr = costsR[i - firstUsed];
                            if (rcost + penalty < cr.Cost)
                            {
                                costsR[i - firstUsed] = new CostInfo { CostV = rcost, ExtraCost = penalty, Pred = ts.PredictorFromIndex(p) };
                            }

                            CostInfo cl = costsL[i - firstUsed];
                            if (lcost + penalty < cl.Cost)
                            {
                                costsL[i - firstUsed] = new CostInfo { CostV = lcost, ExtraCost = penalty, Pred = ts.PredictorFromIndex(p) };
                            }
                        }
                    }

                    int split = begin;
                    for (int i = firstUsed; i < lastUsed; i++)
                    {
                        if (propValueUsedCount[i] == 0)
                        {
                            continue;
                        }

                        split += propValueUsedCount[i];
                        float rcost = costsR[i - firstUsed].CostV;
                        float lcost = costsL[i - firstUsed].CostV;
                        bool usesWp = ts.PropertyFromIndex(prop) == LjPredict.WpProp || costsL[i - firstUsed].Pred == LjPredictor.Weighted ||
                                      costsR[i - firstUsed].Pred == LjPredictor.Weighted;
                        bool zeroEntropySide = rcost == 0 || lcost == 0;
                        ref SplitInfo bestRef = ref bestNonstatic;
                        if (ts.PropertyFromIndex(prop) < LjPredict.NumStaticProperties)
                        {
                            if (zeroEntropySide)
                            {
                                bestRef = ref bestStaticConstant;
                            }
                            else
                            {
                                bestRef = ref bestStatic;
                            }
                        }
                        else if (!usesWp)
                        {
                            bestRef = ref bestNowp;
                        }

                        if (lcost + rcost < bestRef.Cost)
                        {
                            bestRef.Prop = prop;
                            bestRef.Val = i;
                            bestRef.Pos = split;
                            bestRef.LCost = lcost;
                            bestRef.LPred = costsL[i - firstUsed].Pred;
                            bestRef.RCost = rcost;
                            bestRef.RPred = costsR[i - firstUsed].Pred;
                        }
                    }

                    extraBitsIncrease[lastUsed] = 0;
                    for (int sym = 0; sym < maxSymbols; sym++)
                    {
                        countIncrease[(lastUsed * maxSymbols) + sym] = 0;
                    }
                }

                SplitInfo bestCur = bestNonstatic;
                if (bestNowp.Cost + threshold < baseBits && bestNowp.Cost <= fastDecodeMultiplier * bestCur.Cost)
                {
                    bestKind = 3;
                    bestCur = bestNowp;
                }

                if (bestStatic.Cost + threshold < baseBits && bestStatic.Cost <= fastDecodeMultiplier * bestCur.Cost)
                {
                    bestKind = 1;
                    bestCur = bestStatic;
                }

                if (bestStaticConstant.Cost + threshold < baseBits)
                {
                    bestKind = 0;
                }
            }

            SplitInfo best = bestKind switch { 0 => bestStaticConstant, 1 => bestStatic, 3 => bestNowp, _ => bestNonstatic };
            if (best.Cost + threshold < baseBits)
            {
                int p = (int)ts.PropertyFromIndex(best.Prop);
                int dequant = ts.UnquantizeProperty(best.Prop, best.Val);
                // MakeSplitNode
                LjTreeNode n = tree[pos];
                n.LChild = (uint)tree.Count;
                n.RChild = (uint)(tree.Count + 1);
                n.SplitVal = dequant;
                n.Property = (short)p;
                tree[pos] = n;
                tree.Add(LjTreeNode.Leaf(best.RPred));
                tree.Add(LjTreeNode.Leaf(best.LPred));
                bool isStatic = best.Prop < ts.NumStaticProps;
                SplitTreeSamples(ts, begin, best.Pos, end, isStatic, isStatic ? best.Prop : best.Prop - ts.NumStaticProps, best.Val);
                var newRange = ((uint Lo, uint Hi)[])staticPropRange.Clone();
                if (p < LjPredict.NumStaticProperties)
                {
                    newRange[p].Hi = (uint)(dequant + 1);
                }

                nodes.Add(((int)tree[pos].RChild, begin, best.Pos, newRange));
                newRange = ((uint Lo, uint Hi)[])staticPropRange.Clone();
                if (p < LjPredict.NumStaticProperties)
                {
                    newRange[p].Lo = (uint)(dequant + 1);
                }

                nodes.Add(((int)tree[pos].LChild, best.Pos, end, newRange));
            }
        }
    }

    private static void SplitTreeSamples(LjTreeSamples ts, int begin, int pos, int end, bool isStatic, int prop, int val)
    {
        int beginPos = begin;
        int endPos = pos;
        do
        {
            while (beginPos < pos && ts.Property(isStatic, prop, beginPos) <= val)
            {
                ++beginPos;
            }

            while (endPos < end && ts.Property(isStatic, prop, endPos) > val)
            {
                ++endPos;
            }

            if (beginPos < pos && endPos < end)
            {
                ts.Swap(beginPos, endPos);
            }

            ++beginPos;
            ++endPos;
        }
        while (beginPos < pos && endPos < end);
    }
}
