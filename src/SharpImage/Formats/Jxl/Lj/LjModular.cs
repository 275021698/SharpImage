// libjxl 0.12 Modular encoder pieces used by VarDCT frames (DC, AC metadata and raw quant-table streams):
// context_predict.h (properties, predictors, weighted predictor), dec_ma.h / enc_ma.cc (MA trees: predefined,
// learned, tokenized), enc_encoding.cc (stream tokenization) and encoding.h (GroupHeader). Line-by-line ports so
// the produced tokens, trees and contexts match cjxl's.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal enum LjPredictor
{
    Zero = 0, Left = 1, Top = 2, Average0 = 3, Select = 4, Gradient = 5, Weighted = 6, TopRight = 7, TopLeft = 8,
    LeftLeft = 9, Average1 = 10, Average2 = 11, Average3 = 12, Average4 = 13, Best = 14, Variable = 15,
    Undefined = -1,
}

internal sealed class LjChannel
{
    public int W, H, HShift, VShift;
    public int[] Plane;
    public int Stride;

    public LjChannel(int w, int h, int hshift = 0, int vshift = 0)
    {
        W = w;
        H = h;
        HShift = hshift;
        VShift = vshift;
        Stride = Math.Max(w, 1);
        Plane = new int[Stride * Math.Max(h, 1)];
    }

    public Span<int> Row(int y) => Plane.AsSpan(y * Stride, Stride);

    public int this[int x, int y]
    {
        get => Plane[(y * Stride) + x];
        set => Plane[(y * Stride) + x] = value;
    }
}

internal sealed class LjModularImage
{
    public List<LjChannel> Channel = new();
    public int W, H, Bitdepth;
    public int NbMetaChannels;
    public List<LjTransform> Transform = new();

    public bool Empty => Channel.Count == 0;

    public static LjModularImage Create(int w, int h, int bitdepth, int nbChans)
    {
        var img = new LjModularImage { W = w, H = h, Bitdepth = bitdepth };
        for (int i = 0; i < nbChans; i++)
        {
            img.Channel.Add(new LjChannel(w, h));
        }

        return img;
    }
}

internal sealed class LjTransform : ILjBundle
{
    public const uint Rct = 0, Palette = 1, Squeeze = 2, Invalid = 3;
    public uint Id = Rct;
    public uint BeginC, RctType = 6, NumC = 3, NbColors = 256, NbDeltas, Predictor;
    public List<LjSqueezeParams> Squeezes = new();

    public void VisitFields(LjVisitor v)
    {
        v.U32(LjU32.Val(Rct), LjU32.Val(Palette), LjU32.Val(Squeeze), LjU32.Val(Invalid), Rct, ref Id);
        if (Id == Invalid)
        {
            throw new System.IO.InvalidDataException("Invalid transform ID");
        }

        if (v.Conditional(Id == Rct || Id == Palette))
        {
            v.U32(LjU32.BitsOnly(3), LjU32.BitsOffset(6, 8), LjU32.BitsOffset(10, 72), LjU32.BitsOffset(13, 1096), 0, ref BeginC);
        }

        if (v.Conditional(Id == Rct))
        {
            v.U32(LjU32.Val(6), LjU32.BitsOnly(2), LjU32.BitsOffset(4, 2), LjU32.BitsOffset(6, 10), 6, ref RctType);
        }

        if (v.Conditional(Id == Palette))
        {
            v.U32(LjU32.Val(1), LjU32.Val(3), LjU32.Val(4), LjU32.BitsOffset(13, 1), 3, ref NumC);
            v.U32(LjU32.BitsOffset(8, 0), LjU32.BitsOffset(10, 256), LjU32.BitsOffset(12, 1280), LjU32.BitsOffset(16, 5376), 256, ref NbColors);
            v.U32(LjU32.Val(0), LjU32.BitsOffset(8, 1), LjU32.BitsOffset(10, 257), LjU32.BitsOffset(16, 1281), 0, ref NbDeltas);
            v.Bits(4, 0, ref Predictor);
        }

        if (v.Conditional(Id == Squeeze))
        {
            uint n = (uint)Squeezes.Count;
            v.U32(LjU32.Val(0), LjU32.BitsOffset(4, 1), LjU32.BitsOffset(6, 9), LjU32.BitsOffset(8, 41), 0, ref n);
            if (v.IsReading)
            {
                Squeezes.Clear();
                for (int i = 0; i < n; i++)
                {
                    Squeezes.Add(new LjSqueezeParams());
                }
            }

            foreach (LjSqueezeParams s in Squeezes)
            {
                v.VisitNested(s);
            }
        }
    }
}

internal sealed class LjSqueezeParams : ILjBundle
{
    public bool Horizontal, InPlace;
    public uint BeginC, NumC = 2;

    public void VisitFields(LjVisitor v)
    {
        v.Bool(false, ref Horizontal);
        v.Bool(false, ref InPlace);
        v.U32(LjU32.BitsOnly(3), LjU32.BitsOffset(6, 8), LjU32.BitsOffset(10, 72), LjU32.BitsOffset(13, 1096), 0, ref BeginC);
        v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), LjU32.BitsOffset(4, 4), 2, ref NumC);
    }
}

internal sealed class LjWpHeader : ILjBundle
{
    public bool AllDefaultField = true;
    public uint P1C = 16, P2C = 10, P3Ca = 7, P3Cb = 7, P3Cc = 7, P3Cd, P3Ce;
    public uint[] W = [0xd, 0xc, 0xc, 0xc];

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        v.Bits(5, 16, ref P1C);
        v.Bits(5, 10, ref P2C);
        v.Bits(5, 7, ref P3Ca);
        v.Bits(5, 7, ref P3Cb);
        v.Bits(5, 7, ref P3Cc);
        v.Bits(5, 0, ref P3Cd);
        v.Bits(5, 0, ref P3Ce);
        v.Bits(4, 0xd, ref W[0]);
        v.Bits(4, 0xc, ref W[1]);
        v.Bits(4, 0xc, ref W[2]);
        v.Bits(4, 0xc, ref W[3]);
    }

    /// <summary>weighted::PredictorMode.</summary>
    public static LjWpHeader ForMode(int mode)
    {
        var h = new LjWpHeader();
        switch (mode)
        {
            case 0:
                h.W = [0xd, 0xc, 0xc, 0xc];
                (h.P1C, h.P2C, h.P3Ca, h.P3Cb, h.P3Cc, h.P3Cd, h.P3Ce) = (16, 10, 7, 7, 7, 0, 0);
                break;
            case 1:
                h.W = [0xd, 0xc, 0xc, 0xb];
                (h.P1C, h.P2C, h.P3Ca, h.P3Cb, h.P3Cc, h.P3Cd, h.P3Ce) = (8, 8, 4, 0, 3, 23, 2);
                break;
            case 2:
                h.W = [0xd, 0xc, 0xd, 0xc];
                (h.P1C, h.P2C, h.P3Ca, h.P3Cb, h.P3Cc, h.P3Cd, h.P3Ce) = (10, 9, 7, 0, 0, 16, 9);
                break;
            case 3:
                h.W = [0xd, 0xd, 0xc, 0xc];
                (h.P1C, h.P2C, h.P3Ca, h.P3Cb, h.P3Cc, h.P3Cd, h.P3Ce) = (16, 8, 0, 16, 0, 23, 0);
                break;
            default:
                h.W = [0xd, 0xc, 0xc, 0xc];
                (h.P1C, h.P2C, h.P3Ca, h.P3Cb, h.P3Cc, h.P3Cd, h.P3Ce) = (10, 10, 5, 5, 5, 12, 4);
                break;
        }

        return h;
    }
}

/// <summary>weighted::State: the self-correcting weighted predictor.</summary>
internal sealed class LjWpState
{
    private const int NumPredictors = 4;
    private const int PredExtraBits = 3;
    private const int PredictionRound = ((1 << PredExtraBits) >> 1) - 1;

    private static readonly uint[] DivLookup = BuildDivLookup();

    private static uint[] BuildDivLookup()
    {
        var t = new uint[64];
        for (int i = 0; i < 64; i++)
        {
            t[i] = (uint)((1 << 24) / (i + 1));
        }

        return t;
    }

    public readonly long[] Prediction = new long[NumPredictors];
    public long Pred;
    private readonly uint[][] predErrors = new uint[NumPredictors][];
    private readonly int[] error;
    private readonly LjWpHeader header;

    public LjWpState(LjWpHeader header, int xsize, int ysize)
    {
        this.header = header;
        for (int i = 0; i < NumPredictors; i++)
        {
            predErrors[i] = new uint[(xsize + 2) * 2];
        }

        error = new int[(xsize + 2) * 2];
    }

    private static long AddBits(long x) => (long)((ulong)x << PredExtraBits);

    private static uint ErrorWeight(ulong x, uint maxweight)
    {
        int shift = LjMath.FloorLog2(x + 1) - 5;
        if (shift < 0)
        {
            shift = 0;
        }

        return 4 + ((maxweight * DivLookup[x >> shift]) >> shift);
    }

    private static long WeightedAverage(long[] p, Span<uint> w)
    {
        uint weightSum = 0;
        for (int i = 0; i < NumPredictors; i++)
        {
            weightSum += w[i];
        }

        int logWeight = LjMath.FloorLog2(weightSum);
        weightSum = 0;
        for (int i = 0; i < NumPredictors; i++)
        {
            w[i] >>= logWeight - 4;
            weightSum += w[i];
        }

        long sum = (weightSum >> 1) - 1;
        for (int i = 0; i < NumPredictors; i++)
        {
            sum += p[i] * w[i];
        }

        return (sum * DivLookup[weightSum - 1]) >> 24;
    }

    /// <summary>Predict; when <paramref name="properties"/> is given, writes the WP property at <paramref name="offset"/>.</summary>
    public long Predict(int x, int y, int xsize, long n, long w, long ne, long nw, long nn, int[]? properties, int offset)
    {
        int curRow = (y & 1) != 0 ? 0 : xsize + 2;
        int prevRow = (y & 1) != 0 ? xsize + 2 : 0;
        int posN = prevRow + x;
        int posNE = x < xsize - 1 ? posN + 1 : posN;
        int posNW = x > 0 ? posN - 1 : posN;
        Span<uint> weights = stackalloc uint[NumPredictors];
        for (int i = 0; i < NumPredictors; i++)
        {
            uint s = predErrors[i][posN] + predErrors[i][posNE] + predErrors[i][posNW];
            weights[i] = ErrorWeight(s, header.W[i]);
        }

        n = AddBits(n);
        w = AddBits(w);
        ne = AddBits(ne);
        nw = AddBits(nw);
        nn = AddBits(nn);
        long teW = x == 0 ? 0 : error[curRow + x - 1];
        long teN = error[posN];
        long teNW = error[posNW];
        long sumWN = teN + teW;
        long teNE = error[posNE];
        if (properties != null)
        {
            long p = teW;
            if (Math.Abs(teN) > Math.Abs(p))
            {
                p = teN;
            }

            if (Math.Abs(teNW) > Math.Abs(p))
            {
                p = teNW;
            }

            if (Math.Abs(teNE) > Math.Abs(p))
            {
                p = teNE;
            }

            properties[offset] = (int)p;
        }

        Prediction[0] = w + ne - n;
        Prediction[1] = n - (((sumWN + teNE) * header.P1C) >> 5);
        Prediction[2] = w - (((sumWN + teNW) * header.P2C) >> 5);
        Prediction[3] = n - (((teNW * header.P3Ca) + (teN * header.P3Cb) + (teNE * header.P3Cc) + ((nn - n) * header.P3Cd) + ((nw - w) * header.P3Ce)) >> 5);
        Pred = WeightedAverage(Prediction, weights);
        if (((teN ^ teW) | (teN ^ teNW)) > 0)
        {
            return (Pred + PredictionRound) >> PredExtraBits;
        }

        long mx = Math.Max(w, Math.Max(ne, n));
        long mn = Math.Min(w, Math.Min(ne, n));
        Pred = Math.Max(mn, Math.Min(mx, Pred));
        return (Pred + PredictionRound) >> PredExtraBits;
    }

    public void UpdateErrors(long val, int x, int y, int xsize)
    {
        int curRow = (y & 1) != 0 ? 0 : xsize + 2;
        int prevRow = (y & 1) != 0 ? xsize + 2 : 0;
        val = AddBits(val);
        error[curRow + x] = (int)(Pred - val);
        for (int i = 0; i < NumPredictors; i++)
        {
            long err = (Math.Abs(Prediction[i] - val) + PredictionRound) >> PredExtraBits;
            predErrors[i][curRow + x] = (uint)err;
            predErrors[i][prevRow + x + 1] += (uint)err;
        }
    }
}

internal struct LjTreeNode
{
    public int SplitVal;
    public short Property;   // -1: leaf
    public uint LChild, RChild;
    public LjPredictor Predictor;
    public long PredictorOffset;
    public uint Multiplier;

    public static LjTreeNode Leaf(LjPredictor p, long offset = 0, uint multiplier = 1) =>
        new() { Property = -1, Predictor = p, PredictorOffset = offset, Multiplier = multiplier };

    public static LjTreeNode Split(int p, int splitVal, int lchild, int rchild = -1)
    {
        if (rchild == -1)
        {
            rchild = lchild + 1;
        }

        return new LjTreeNode { Property = (short)p, SplitVal = splitVal, LChild = (uint)lchild, RChild = (uint)rchild, Predictor = LjPredictor.Zero, Multiplier = 1 };
    }
}

internal sealed class LjModularOptions
{
    public enum TreeMode { GradientOnly, WpOnly, NoWp, Default }
    public enum TreeKind { TrivialTreeNoPredictor, Learn, JpegTranscodeAcMeta, FalconAcMeta, AcMeta, WpFixedDc, GradientFixedDc }

    public int MaxChanSize = 0xFFFFFF;
    public int GroupDim = 0x1FFFFFFF;
    public float NbRepeats = 0.5f;
    public int MaxProperties;
    public List<uint> SplittingHeuristicsProperties = [0, 1, 15, 9, 10, 11, 12, 13];
    public float SplittingHeuristicsNodeThreshold = 96;
    public int MaxPropertyValues = 32;
    public LjPredictor Predictor = LjPredictor.Undefined;
    public int WpMode;
    public float FastDecodeMultiplier = 1.01f;
    public TreeMode WpTreeMode = TreeMode.Default;
    public bool SkipEncoderFastPath;
    public TreeKind Kind = TreeKind.Learn;
    public LjHistogramParams HistogramParams = new();
    public bool ZeroTokens;

    public LjModularOptions Clone()
    {
        var o = (LjModularOptions)MemberwiseClone();
        o.SplittingHeuristicsProperties = new List<uint>(SplittingHeuristicsProperties);
        return o;
    }
}

internal sealed class LjGroupHeader : ILjBundle
{
    public bool UseGlobalTree;
    public LjWpHeader WpHeader = new();
    public List<LjTransform> Transforms = new();

    public void VisitFields(LjVisitor v)
    {
        v.Bool(false, ref UseGlobalTree);
        v.VisitNested(WpHeader);
        uint n = (uint)Transforms.Count;
        v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.BitsOffset(4, 2), LjU32.BitsOffset(8, 18), 0, ref n);
        if (v.IsReading)
        {
            Transforms.Clear();
            for (int i = 0; i < n; i++)
            {
                Transforms.Add(new LjTransform());
            }
        }

        for (int i = 0; i < n; i++)
        {
            v.VisitNested(Transforms[i]);
        }
    }
}

internal static class LjPredict
{
    public const int NumStaticProperties = 2;
    public const int NumNonrefProperties = NumStaticProperties + 13 + 1;
    public const int WpProp = NumNonrefProperties - 1;
    public const int GradientProp = 9;
    public const int ExtraPropsPerChannel = 4;
    public const int NumModularPredictors = (int)LjPredictor.Average4 + 1;

    public static int ClampedGradient(int n, int w, int l)
    {
        int m = Math.Min(n, w);
        int mm = Math.Max(n, w);
        int grad = (int)((uint)n + (uint)w - (uint)l);
        int gradClampM = l < m ? mm : grad;
        return l > mm ? m : gradClampM;
    }

    private static long Select(long a, long b, long c)
    {
        long p = a + b - c;
        long pa = Math.Abs(p - a);
        long pb = Math.Abs(p - b);
        return pa < pb ? a : b;
    }

    public static long PredictOne(LjPredictor p, long left, long top, long toptop, long topleft, long topright, long leftleft, long toprightright, long wpPred) =>
        p switch
        {
            LjPredictor.Zero => 0,
            LjPredictor.Left => left,
            LjPredictor.Top => top,
            LjPredictor.Select => Select(left, top, topleft),
            LjPredictor.Weighted => wpPred,
            LjPredictor.Gradient => ClampedGradient((int)left, (int)top, (int)topleft),
            LjPredictor.TopLeft => topleft,
            LjPredictor.TopRight => topright,
            LjPredictor.LeftLeft => leftleft,
            LjPredictor.Average0 => (left + top) / 2,
            LjPredictor.Average1 => (left + topleft) / 2,
            LjPredictor.Average2 => (topleft + top) / 2,
            LjPredictor.Average3 => (top + topright) / 2,
            LjPredictor.Average4 => ((6 * top) - (2 * toptop) + (7 * left) + leftleft + toprightright + (3 * topright) + 8) / 16,
            _ => 0,
        };

    public static void InitPropsRow(int[] p, int chan, int groupId, int y)
    {
        p[0] = chan;
        p[1] = groupId;
        p[2] = y;
        p[9] = 0;
    }

    /// <summary>PrecomputeReferences: the extra properties from previous channels of the same shape.</summary>
    public static void PrecomputeReferences(LjChannel ch, int y, LjModularImage image, int i, int[] references, int numExtraProps)
    {
        Array.Clear(references);
        int offset = 0;
        for (int j = i - 1; j >= 0 && offset < numExtraProps; j--)
        {
            LjChannel cj = image.Channel[j];
            if (cj.W != image.Channel[i].W || cj.H != image.Channel[i].H || cj.HShift != image.Channel[i].HShift || cj.VShift != image.Channel[i].VShift)
            {
                continue;
            }

            for (int x = 0; x < ch.W; x++)
            {
                long v = cj[x, y];
                long vleft = x != 0 ? cj[x - 1, y] : 0;
                long vtop = y != 0 ? cj[x, y - 1] : vleft;
                long vtopleft = x != 0 && y != 0 ? cj[x - 1, y - 1] : vleft;
                long vpredicted = ClampedGradient((int)vleft, (int)vtop, (int)vtopleft);
                int b = (x * numExtraProps) + offset;
                references[b] = (int)Math.Abs(v);
                references[b + 1] = (int)v;
                references[b + 2] = (int)Math.Abs(v - vpredicted);
                references[b + 3] = (int)(v - vpredicted);
            }

            offset += ExtraPropsPerChannel;
        }
    }

    /// <summary>
    /// detail::Predict: neighbour fetch, property computation (when <paramref name="props"/> is given), weighted
    /// prediction (when <paramref name="wp"/> is given) and the guess for <paramref name="predictor"/>. When
    /// <paramref name="allPredictions"/> is given, every predictor's guess is stored in it.
    /// </summary>
    public static long Predict(int[]? props, LjChannel ch, int x, int y, LjPredictor predictor, LjWpState? wp, int[]? references,
        int numExtraProps, long[]? allPredictions, bool nec = false)
    {
        int w = ch.W;
        int[] pl = ch.Plane;
        int s = ch.Stride;
        int pos = (y * s) + x;
        long left = nec || x != 0 ? pl[pos - 1] : (y != 0 ? pl[pos - s] : 0);
        long top = nec || y != 0 ? pl[pos - s] : left;
        long topleft = nec || (x != 0 && y != 0) ? pl[pos - 1 - s] : left;
        long topright = nec || (x + 1 < w && y != 0) ? pl[pos + 1 - s] : top;
        long leftleft = nec || x > 1 ? pl[pos - 2] : left;
        long toptop = nec || y > 1 ? pl[pos - s - s] : top;
        long toprightright = nec || (x + 2 < w && y != 0) ? pl[pos + 2 - s] : topright;
        int offset = 3;
        if (props != null)
        {
            props[offset++] = x;
            props[offset++] = (int)(top > 0 ? top : -top);
            props[offset++] = (int)(left > 0 ? left : -left);
            props[offset++] = (int)top;
            props[offset++] = (int)left;
            props[offset] = (int)(left - props[offset + 1]);
            offset++;
            props[offset++] = (int)(left + top - topleft);
            props[offset++] = (int)(left - topleft);
            props[offset++] = (int)(topleft - top);
            props[offset++] = (int)(top - topright);
            props[offset++] = (int)(top - toptop);
            props[offset++] = (int)(left - leftleft);
        }

        long wpPred = 0;
        if (wp != null)
        {
            wpPred = wp.Predict(x, y, w, top, left, topright, topleft, toptop, props, offset);
        }

        if (!nec && props != null)
        {
            offset += 1;
            if (references != null)
            {
                for (int i = 0; i < numExtraProps; i++)
                {
                    props[offset++] = references[(x * numExtraProps) + i];
                }
            }
        }

        if (allPredictions != null)
        {
            for (int i = 0; i < NumModularPredictors; i++)
            {
                allPredictions[i] = PredictOne((LjPredictor)i, left, top, toptop, topleft, topright, leftleft, toprightright, wpPred);
            }
        }

        return PredictOne(predictor, left, top, toptop, topleft, topright, leftleft, toprightright, wpPred);
    }
}

internal static class LjMaTree
{
    public const int SplitValContext = 0, PropertyContext = 1, PredictorContext = 2, OffsetContext = 3, MultiplierLogContext = 4,
        MultiplierBitsContext = 5, NumTreeContexts = 6;

    private static readonly int[] DcCutoffs =
    [
        -500, -392, -255, -191, -127, -95, -63, -47, -31, -23, -15, -11, -7, -4, -3, -1, 0, 1, 3, 5, 7, 11,
        15, 23, 31, 47, 63, 95, 127, 191, 255, 392, 500,
    ];

    private static List<LjTreeNode> MakeFixedTree(int property, int[] cutoffs, LjPredictor pred, long numPixels, int bitdepth)
    {
        int logPx = LjMath.CeilLog2Nonzero((ulong)numPixels);
        int minGap = 0;
        if (logPx < 14)
        {
            minGap = 8 * (14 - logPx);
        }

        int shift = bitdepth > 11 ? Math.Min(4, bitdepth - 11) : 0;
        int mul = 1 << shift;
        var tree = new List<LjTreeNode> { LjTreeNode.Leaf(pred) };
        var q = new Queue<(int Begin, int End, int Pos)>();
        q.Enqueue((0, cutoffs.Length, 0));
        while (q.Count > 0)
        {
            var info = q.Dequeue();
            if (info.Begin + minGap >= info.End)
            {
                continue;
            }

            int split = (info.Begin + info.End) / 2;
            int cutoff = cutoffs[split] * mul;
            tree[info.Pos] = LjTreeNode.Split(property, cutoff, tree.Count);
            q.Enqueue((split + 1, info.End, tree.Count));
            tree.Add(LjTreeNode.Leaf(pred));
            q.Enqueue((info.Begin, split, tree.Count));
            tree.Add(LjTreeNode.Leaf(pred));
        }

        return tree;
    }

    public static List<LjTreeNode> PredefinedTree(LjModularOptions.TreeKind kind, long totalPixels, int bitdepth, int prevprop)
    {
        switch (kind)
        {
            case LjModularOptions.TreeKind.JpegTranscodeAcMeta:
            case LjModularOptions.TreeKind.TrivialTreeNoPredictor:
                return [LjTreeNode.Leaf(LjPredictor.Zero)];
            case LjModularOptions.TreeKind.FalconAcMeta:
                return [LjTreeNode.Leaf(LjPredictor.Left)];
            case LjModularOptions.TreeKind.AcMeta:
            {
                if (totalPixels < 1024)
                {
                    return [LjTreeNode.Leaf(LjPredictor.Left)];
                }

                return
                [
                    LjTreeNode.Split(0, 1, 1), LjTreeNode.Split(0, 2, 3), LjTreeNode.Split(0, 0, 5), LjTreeNode.Split(6, 3, 21),
                    LjTreeNode.Split(2, 0, 7), LjTreeNode.Leaf(LjPredictor.Gradient), LjTreeNode.Leaf(LjPredictor.Gradient),
                    LjTreeNode.Split(7, 5, 9), LjTreeNode.Split(7, 5, 15), LjTreeNode.Split(7, 11, 11), LjTreeNode.Split(7, 3, 13),
                    LjTreeNode.Leaf(LjPredictor.Left), LjTreeNode.Leaf(LjPredictor.Left), LjTreeNode.Leaf(LjPredictor.Left), LjTreeNode.Leaf(LjPredictor.Left),
                    LjTreeNode.Split(7, 11, 17), LjTreeNode.Split(7, 3, 19),
                    LjTreeNode.Leaf(LjPredictor.Zero), LjTreeNode.Leaf(LjPredictor.Zero), LjTreeNode.Leaf(LjPredictor.Zero), LjTreeNode.Leaf(LjPredictor.Zero),
                    LjTreeNode.Split(7, 3, 23), LjTreeNode.Split(7, 3, 25),
                    LjTreeNode.Leaf(LjPredictor.Zero), LjTreeNode.Leaf(LjPredictor.Zero), LjTreeNode.Leaf(LjPredictor.Zero), LjTreeNode.Leaf(LjPredictor.Zero),
                ];
            }

            case LjModularOptions.TreeKind.WpFixedDc:
                return MakeFixedTree(LjPredict.WpProp, DcCutoffs, LjPredictor.Weighted, totalPixels, bitdepth);
            case LjModularOptions.TreeKind.GradientFixedDc:
                return MakeFixedTree(prevprop > 0 ? LjPredict.NumNonrefProperties + 2 : LjPredict.GradientProp, DcCutoffs, LjPredictor.Gradient,
                    totalPixels, bitdepth);
            default:
                throw new InvalidOperationException("kLearn is not a predefined tree");
        }
    }

    /// <summary>MergeTrees: joins per-chunk trees with stream-id (property 1) decisions.</summary>
    public static void MergeTrees(List<List<LjTreeNode>> trees, List<int> treeSplits, int begin, int end, List<LjTreeNode> tree)
    {
        if (end == begin + 1)
        {
            int sz = tree.Count;
            foreach (LjTreeNode n in trees[begin])
            {
                LjTreeNode m = n;
                m.LChild += (uint)sz;
                m.RChild += (uint)sz;
                tree.Add(m);
            }

            return;
        }

        int mid = (begin + end) / 2;
        int splitval = treeSplits[mid] - 1;
        int cur = tree.Count;
        tree.Add(new LjTreeNode { Property = 1, SplitVal = splitval, Predictor = LjPredictor.Zero, Multiplier = 1 });
        LjTreeNode c = tree[cur];
        c.LChild = (uint)tree.Count;
        tree[cur] = c;
        MergeTrees(trees, treeSplits, mid, end, tree);
        c = tree[cur];
        c.RChild = (uint)tree.Count;
        tree[cur] = c;
        MergeTrees(trees, treeSplits, begin, mid, tree);
    }

    /// <summary>TokenizeTree: BFS order tokens, and the tree as the decoder will see it (leaf contexts in lchild).</summary>
    public static List<LjTreeNode> TokenizeTree(List<LjTreeNode> tree, List<LjToken> tokens)
    {
        var q = new Queue<int>();
        q.Enqueue(0);
        int leafId = 0;
        var decoderTree = new List<LjTreeNode>();
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            LjTreeNode n = tree[cur];
            tokens.Add(new LjToken(PropertyContext, (uint)(n.Property + 1)));
            if (n.Property == -1)
            {
                tokens.Add(new LjToken(PredictorContext, (uint)n.Predictor));
                tokens.Add(new LjToken(OffsetContext, LjMath.PackSigned((int)n.PredictorOffset)));
                int mulLog = System.Numerics.BitOperations.TrailingZeroCount(n.Multiplier);
                uint mulBits = (n.Multiplier >> mulLog) - 1;
                tokens.Add(new LjToken(MultiplierLogContext, (uint)mulLog));
                tokens.Add(new LjToken(MultiplierBitsContext, mulBits));
                decoderTree.Add(new LjTreeNode
                {
                    Property = -1, SplitVal = 0, LChild = (uint)leafId, RChild = 0, Predictor = n.Predictor,
                    PredictorOffset = n.PredictorOffset, Multiplier = n.Multiplier,
                });
                leafId++;
                continue;
            }

            decoderTree.Add(new LjTreeNode
            {
                Property = n.Property, SplitVal = n.SplitVal, LChild = (uint)(decoderTree.Count + q.Count + 1),
                RChild = (uint)(decoderTree.Count + q.Count + 2), Predictor = LjPredictor.Zero, Multiplier = 1,
            });
            q.Enqueue((int)n.LChild);
            q.Enqueue((int)n.RChild);
            tokens.Add(new LjToken(SplitValContext, LjMath.PackSigned(n.SplitVal)));
        }

        return decoderTree;
    }

    /// <summary>Walks a decoded tree: the leaf for the given properties (static props resolved like FilterTree).</summary>
    public static ref readonly LjTreeNode Lookup(List<LjTreeNode> tree, int[] props)
    {
        int pos = 0;
        while (true)
        {
            LjTreeNode n = tree[pos];
            if (n.Property < 0)
            {
                return ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tree)[pos];
            }

            pos = props[n.Property] > n.SplitVal ? (int)n.LChild : (int)n.RChild;
        }
    }

    /// <summary>Largest property index the tree consults for this stream (FilterTree num_props), and whether WP is needed.</summary>
    public static (int NumProps, bool UseWp) Analyze(List<LjTreeNode> tree, int chan, int groupId)
    {
        int numProps = 0;
        bool useWp = false;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            LjTreeNode n = tree[cur];
            while (n.Property >= 0 && n.Property < LjPredict.NumStaticProperties)
            {
                int sp = n.Property == 0 ? chan : groupId;
                cur = sp > n.SplitVal ? (int)n.LChild : (int)n.RChild;
                n = tree[cur];
            }

            if (n.Property < 0)
            {
                useWp |= n.Predictor == LjPredictor.Weighted;
                continue;
            }

            numProps = Math.Max(numProps, n.Property + 1);
            useWp |= n.Property == LjPredict.WpProp;
            stack.Push((int)n.LChild);
            stack.Push((int)n.RChild);
        }

        return (numProps, useWp);
    }
}

/// <summary>enc_encoding.cc: ModularCompress / ModularGenericCompress and tree learning entry points.</summary>
internal static class LjModularCompress
{
    /// <summary>EncodeModularChannelMAANS (all fast paths produce these same tokens).</summary>
    private static void EncodeChannel(LjModularImage image, int chan, LjWpHeader wpHeader, List<LjTreeNode> tree, List<LjToken> tokens, int groupId)
    {
        LjChannel channel = image.Channel[chan];
        (int numProps, bool useWp) = LjMaTree.Analyze(tree, chan, groupId);
        int numExtra = Math.Max(0, numProps - LjPredict.NumNonrefProperties);
        numExtra = (numExtra + 3) / 4 * 4;
        var props = new int[LjPredict.NumNonrefProperties + numExtra];
        int[]? references = numExtra > 0 ? new int[channel.W * numExtra] : null;
        LjWpState? wp = useWp ? new LjWpState(wpHeader, channel.W, channel.H) : null;
        for (int y = 0; y < channel.H; y++)
        {
            if (references != null)
            {
                LjPredict.PrecomputeReferences(channel, y, image, chan, references, numExtra);
            }

            LjPredict.InitPropsRow(props, chan, groupId, y);
            for (int x = 0; x < channel.W; x++)
            {
                // Compute every property the tree may consult, then walk the tree.
                LjPredict.Predict(props, channel, x, y, LjPredictor.Zero, wp, references, numExtra, null);
                ref readonly LjTreeNode leaf = ref LjMaTree.Lookup(tree, props);
                long guess = leaf.PredictorOffset + PredictGuess(channel, x, y, leaf.Predictor, wp);
                long residual = channel[x, y] - guess;
                tokens.Add(new LjToken(leaf.LChild, LjMath.PackSigned((int)(residual / leaf.Multiplier))));
                wp?.UpdateErrors(channel[x, y], x, y, channel.W);
            }
        }
    }

    // The leaf predictor's guess, reusing the weighted prediction already computed for this pixel.
    private static long PredictGuess(LjChannel ch, int x, int y, LjPredictor p, LjWpState? wp)
    {
        if (p == LjPredictor.Weighted)
        {
            return (wp!.Pred + 3) >> 3;
        }

        return LjPredict.Predict(null, ch, x, y, p, null, null, 0, null);
    }

    /// <summary>ModularCompress: fills the group header and appends the stream's tokens; returns the max channel width.</summary>
    public static int Compress(LjModularImage image, LjModularOptions options, int groupId, List<LjTreeNode> tree, LjGroupHeader header,
        List<LjToken> tokens)
    {
        int nb = image.Channel.Count;
        if (image.W == 0 || image.H == 0 || nb < 1)
        {
            return 0;
        }

        header.UseGlobalTree = false;
        header.WpHeader = new LjWpHeader();
        header.Transforms = new List<LjTransform>();
        if (PredictorHasWeighted(options.Predictor))
        {
            header.WpHeader = LjWpHeader.ForMode(options.WpMode);
        }

        header.Transforms = new List<LjTransform>(image.Transform);
        header.UseGlobalTree = true;
        int imageWidth = 0;
        for (int i = 0; i < nb; i++)
        {
            if (i >= image.NbMetaChannels && (image.Channel[i].W > options.MaxChanSize || image.Channel[i].H > options.MaxChanSize))
            {
                break;
            }

            imageWidth = Math.Max(imageWidth, image.Channel[i].W);
        }

        for (int i = 0; i < nb; i++)
        {
            if (i >= image.NbMetaChannels && (image.Channel[i].W > options.MaxChanSize || image.Channel[i].H > options.MaxChanSize))
            {
                break;
            }

            if (image.Channel[i].W == 0 || image.Channel[i].H == 0)
            {
                continue;
            }

            if (options.ZeroTokens)
            {
                for (int k = 0; k < image.Channel[i].W * image.Channel[i].H; k++)
                {
                    tokens.Add(new LjToken(0, 0));
                }

                continue;
            }

            EncodeChannel(image, i, header.WpHeader, tree, tokens, groupId);
        }

        return imageWidth;
    }

    public static bool PredictorHasWeighted(LjPredictor p) => p is LjPredictor.Weighted or LjPredictor.Best or LjPredictor.Variable;
}
