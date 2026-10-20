// libjxl 0.12 Modular decoding: dec_ma.cc (tree), encoding.cc (ModularDecode / channel decoding), the inverse
// transforms (rct.cc, palette.cc/.h, squeeze.cc/.h) and MetaApply. Line-by-line ports; only the general decoding
// path is kept, the libjxl fast paths decode identically.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjDecModular
{
    private static Exception Fail(string m) => new InvalidDataException("JXL modular: " + m);

    private static int UnpackSigned(uint v) => (int)((v >> 1) ^ (uint)-(int)(v & 1));

    // ---- MA tree ----

    private static void ValidateTree(List<LjTreeNode> tree)
    {
        if (tree.Count == 0)
        {
            return;
        }

        int numProperties = 0;
        foreach (LjTreeNode n in tree)
        {
            numProperties = Math.Max(numProperties, n.Property + 1);
        }

        var lo = new int[numProperties];
        var hi = new int[numProperties];
        Array.Fill(lo, int.MinValue);
        Array.Fill(hi, int.MaxValue);
        var stack = new List<(int Node, int OrigL, int OrigU, int Action)> { (0, 0, 0, 0) };
        while (stack.Count > 0)
        {
            if (stack.Count >= 2048)
            {
                throw Fail("Tree too tall");
            }

            var item = stack[^1];
            LjTreeNode node = tree[item.Node];
            switch (item.Action)
            {
                case 0:
                {
                    int p = node.Property;
                    if (p == -1)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }

                    int v = node.SplitVal;
                    int l = lo[p], u = hi[p];
                    if (l > v || u <= v)
                    {
                        throw Fail("Invalid tree");
                    }

                    stack[^1] = (item.Node, l, u, 1);
                    lo[p] = v + 1;
                    stack.Add(((int)node.LChild, 0, 0, 0));
                    continue;
                }

                case 1:
                    stack[^1] = (item.Node, item.OrigL, item.OrigU, 2);
                    lo[node.Property] = item.OrigL;
                    hi[node.Property] = node.SplitVal;
                    stack.Add(((int)node.RChild, 0, 0, 0));
                    continue;
                default:
                    hi[node.Property] = item.OrigU;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
            }
        }
    }

    public static List<LjTreeNode> DecodeTree(LjBitReader br, long treeSizeLimit)
    {
        LjAnsCode code = LjDecAns.DecodeHistograms(br, LjMaTree.NumTreeContexts, out byte[] ctxMap);
        if (code.DegenerateSymbols[ctxMap[LjMaTree.PropertyContext]] > 0)
        {
            throw Fail("Infinite tree");
        }

        var reader = new LjAnsSymbolReader(code, br);
        treeSizeLimit = Math.Min(treeSizeLimit, 1L << 22);
        int leafId = 0;
        long toDecode = 1;
        var tree = new List<LjTreeNode>();
        while (toDecode > 0)
        {
            if (!br.AllReadsWithinBounds)
            {
                throw Fail("Truncated tree");
            }

            if (tree.Count > treeSizeLimit)
            {
                throw Fail("Tree is too large");
            }

            toDecode--;
            uint prop1 = reader.ReadHybridUint(LjMaTree.PropertyContext, br, ctxMap);
            if (prop1 > 256)
            {
                throw Fail("Invalid tree property value");
            }

            int property = (int)prop1 - 1;
            if (property == -1)
            {
                uint predictor = reader.ReadHybridUint(LjMaTree.PredictorContext, br, ctxMap);
                if (predictor >= LjPredict.NumModularPredictors)
                {
                    throw Fail("Invalid predictor");
                }

                long offset = UnpackSigned(reader.ReadHybridUint(LjMaTree.OffsetContext, br, ctxMap));
                uint mulLog = reader.ReadHybridUint(LjMaTree.MultiplierLogContext, br, ctxMap);
                if (mulLog >= 31)
                {
                    throw Fail("Invalid multiplier logarithm");
                }

                uint mulBits = reader.ReadHybridUint(LjMaTree.MultiplierBitsContext, br, ctxMap);
                if (mulBits >= (1u << (int)(31 - mulLog)) - 1u)
                {
                    throw Fail("Invalid multiplier");
                }

                uint multiplier = (mulBits + 1) << (int)mulLog;
                tree.Add(new LjTreeNode
                {
                    Property = -1, LChild = (uint)leafId, Predictor = (LjPredictor)predictor, PredictorOffset = offset, Multiplier = multiplier,
                });
                leafId++;
                continue;
            }

            int splitval = UnpackSigned(reader.ReadHybridUint(LjMaTree.SplitValContext, br, ctxMap));
            tree.Add(new LjTreeNode
            {
                Property = (short)property, SplitVal = splitval, LChild = (uint)(tree.Count + toDecode + 1), RChild = (uint)(tree.Count + toDecode + 2),
                Predictor = LjPredictor.Zero, Multiplier = 1,
            });
            toDecode += 2;
        }

        ValidateTree(tree);
        if (!reader.CheckAnsFinalState())
        {
            throw Fail("ANS decode final state failed");
        }

        return tree;
    }

    // ---- channel decoding ----

    private static void DecodeChannel(LjBitReader br, LjAnsSymbolReader reader, byte[] contextMap, List<LjTreeNode> tree, LjWpHeader wpHeader,
        int chan, int groupId, LjModularImage image)
    {
        LjChannel channel = image.Channel[chan];
        if (channel.W == 0 || channel.H == 0)
        {
            return;
        }

        (int numProps, bool useWp) = LjMaTree.Analyze(tree, chan, groupId);
        int numExtra = 0;
        if (numProps > LjPredict.NumNonrefProperties)
        {
            numExtra = LjMath.DivCeil(numProps - LjPredict.NumNonrefProperties, LjPredict.ExtraPropsPerChannel) * LjPredict.ExtraPropsPerChannel;
        }

        var props = new int[LjPredict.NumNonrefProperties + numExtra];
        int[]? references = numExtra > 0 ? new int[channel.W * numExtra] : null;
        LjWpState? wp = useWp ? new LjWpState(wpHeader, channel.W, channel.H) : null;
        int[] pl = channel.Plane;
        int s = channel.Stride;
        for (int y = 0; y < channel.H; y++)
        {
            if (references != null)
            {
                LjPredict.PrecomputeReferences(channel, y, image, chan, references, numExtra);
            }

            LjPredict.InitPropsRow(props, chan, groupId, y);
            for (int x = 0; x < channel.W; x++)
            {
                LjPredict.Predict(props, channel, x, y, LjPredictor.Zero, wp, references, numExtra, null);
                ref readonly LjTreeNode leaf = ref LjMaTree.Lookup(tree, props);
                long guess = leaf.PredictorOffset + (leaf.Predictor == LjPredictor.Weighted
                    ? (wp!.Pred + 3) >> 3
                    : LjPredict.Predict(null, channel, x, y, leaf.Predictor, null, null, 0, null));
                uint v = reader.ReadHybridUintClustered(contextMap[leaf.LChild], br);
                long val = ((long)UnpackSigned(v) * (int)leaf.Multiplier) + guess;
                pl[(y * s) + x] = (int)val;
                wp?.UpdateErrors(pl[(y * s) + x], x, y, channel.W);
            }
        }
    }

    // ---- transforms ----

    private static bool CheckEqualChannels(LjModularImage image, uint c1, uint c2)
    {
        if (c1 > image.Channel.Count || c2 >= image.Channel.Count || c2 < c1)
        {
            throw Fail("Invalid channel range");
        }

        if (c1 < image.NbMetaChannels && c2 >= image.NbMetaChannels)
        {
            throw Fail("Invalid: transforming mix of meta and nonmeta");
        }

        LjChannel ch1 = image.Channel[(int)c1];
        for (int c = (int)c1 + 1; c <= c2; c++)
        {
            LjChannel ch2 = image.Channel[c];
            if (ch1.W != ch2.W || ch1.H != ch2.H || ch1.HShift != ch2.HShift || ch1.VShift != ch2.VShift)
            {
                return false;
            }
        }

        return true;
    }

    private static void ShrinkChannel(LjChannel ch)
    {
        // Channel::shrink: reallocate to w x h (contents are not preserved in a way that matters here).
        var n = new LjChannel(ch.W, ch.H, ch.HShift, ch.VShift);
        ch.Plane = n.Plane;
        ch.Stride = n.Stride;
    }

    public static void MetaApply(LjModularImage image, LjTransform t)
    {
        switch (t.Id)
        {
            case LjTransform.Rct:
                if (!CheckEqualChannels(image, t.BeginC, t.BeginC + 2))
                {
                    throw Fail("RCT on channels of different shapes");
                }

                break;
            case LjTransform.Squeeze:
                MetaSqueeze(image, t.Squeezes);
                break;
            case LjTransform.Palette:
                MetaPalette(image, t.BeginC, t.BeginC + t.NumC - 1, t.NbColors, t.NbDeltas);
                break;
            default:
                throw Fail("Unknown transformation");
        }
    }

    public static void Inverse(LjModularImage image, LjTransform t, LjWpHeader wpHeader)
    {
        switch (t.Id)
        {
            case LjTransform.Rct:
                InvRct(image, (int)t.BeginC, (int)t.RctType);
                break;
            case LjTransform.Squeeze:
                InvSqueeze(image, t.Squeezes);
                break;
            case LjTransform.Palette:
                InvPalette(image, t.BeginC, t.NbColors, t.NbDeltas, (LjPredictor)t.Predictor, wpHeader);
                break;
            default:
                throw Fail("Unknown transformation");
        }
    }

    public static void UndoTransforms(LjModularImage image, LjWpHeader wpHeader)
    {
        while (image.Transform.Count > 0)
        {
            LjTransform t = image.Transform[^1];
            Inverse(image, t, wpHeader);
            image.Transform.RemoveAt(image.Transform.Count - 1);
        }
    }

    private static int PixelAdd(int a, int b) => (int)((uint)a + (uint)b);

    private static void InvRct(LjModularImage input, int beginC, int rctType)
    {
        if (!CheckEqualChannels(input, (uint)beginC, (uint)beginC + 2))
        {
            throw Fail("RCT on channels of different shapes");
        }

        int m = beginC;
        LjChannel c0 = input.Channel[m];
        int w = c0.W, h = c0.H;
        if (rctType == 0)
        {
            return;
        }

        int permutation = rctType / 7;
        if (permutation >= 6)
        {
            throw Fail("Invalid RCT permutation");
        }

        int custom = rctType % 7;
        LjChannel ch0 = input.Channel[m], ch1 = input.Channel[m + 1], ch2 = input.Channel[m + 2];
        int o0 = m + (permutation % 3);
        int o1 = m + ((permutation + 1 + (permutation / 3)) % 3);
        int o2 = m + ((permutation + 2 - (permutation / 3)) % 3);
        if (custom == 0)
        {
            input.Channel[o0] = ch0;
            input.Channel[o1] = ch1;
            input.Channel[o2] = ch2;
            return;
        }

        int second = custom >> 1;
        int third = custom & 1;
        // Outputs are written in place over the inputs (same shapes), after all three inputs of a pixel are read.
        var out0 = new LjChannel(w, h, ch0.HShift, ch0.VShift);
        var out1 = new LjChannel(w, h, ch0.HShift, ch0.VShift);
        var out2 = new LjChannel(w, h, ch0.HShift, ch0.VShift);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int a = ch0[x, y], b = ch1[x, y], c = ch2[x, y];
                if (custom == 6)
                {
                    int tmp = PixelAdd(a, -(c >> 1));
                    int g = PixelAdd(c, tmp);
                    int bb = PixelAdd(tmp, -(b >> 1));
                    int r = PixelAdd(bb, b);
                    out0[x, y] = r;
                    out1[x, y] = g;
                    out2[x, y] = bb;
                }
                else
                {
                    if (third != 0)
                    {
                        c = PixelAdd(c, a);
                    }

                    if (second == 1)
                    {
                        b = PixelAdd(b, a);
                    }
                    else if (second == 2)
                    {
                        b = PixelAdd(b, PixelAdd(a, c) >> 1);
                    }

                    out0[x, y] = a;
                    out1[x, y] = b;
                    out2[x, y] = c;
                }
            }
        }

        input.Channel[o0] = out0;
        input.Channel[o1] = out1;
        input.Channel[o2] = out2;
    }

    private static readonly int[,] DeltaPalette =
    {
        { 0, 0, 0 }, { 4, 4, 4 }, { 11, 0, 0 }, { 0, 0, -13 }, { 0, -12, 0 }, { -10, -10, -10 },
        { -18, -18, -18 }, { -27, -27, -27 }, { -18, -18, 0 }, { 0, 0, -32 }, { -32, 0, 0 }, { -37, -37, -37 },
        { 0, -32, -32 }, { 24, 24, 45 }, { 50, 50, 50 }, { -45, -24, -24 }, { -24, -45, -45 }, { 0, -24, -24 },
        { -34, -34, 0 }, { -24, 0, -24 }, { -45, -45, -24 }, { 64, 64, 64 }, { -32, 0, -32 }, { 0, -32, 0 },
        { -32, 0, 32 }, { -24, -45, -24 }, { 45, 24, 45 }, { 24, -24, -45 }, { -45, -24, 24 }, { 80, 80, 80 },
        { 64, 0, 0 }, { 0, 0, -64 }, { 0, -64, -64 }, { -24, -24, 45 }, { 96, 96, 96 }, { 64, 64, 0 },
        { 45, -24, -24 }, { 34, -34, 0 }, { 112, 112, 112 }, { 24, -45, -45 }, { 45, 45, -24 }, { 0, -32, 32 },
        { 24, -24, 45 }, { 0, 96, 96 }, { 45, -24, 24 }, { 24, -45, -24 }, { -24, -45, 24 }, { 0, -64, 0 },
        { 96, 0, 0 }, { 128, 128, 128 }, { 64, 0, 64 }, { 144, 144, 144 }, { 96, 96, 0 }, { -36, -36, 36 },
        { 45, -24, -45 }, { 45, -45, -24 }, { 0, 0, -96 }, { 0, 128, 128 }, { 0, 96, 0 }, { 45, 24, -45 },
        { -128, 0, 0 }, { 24, -45, 24 }, { -45, 24, -45 }, { 64, 0, -64 }, { 64, -64, -64 }, { 96, 0, 96 },
        { 45, -45, 24 }, { 24, 45, -45 }, { 64, 64, -64 }, { 128, 128, 0 }, { 0, 0, -128 }, { -24, 45, -45 },
    };

    private static int PaletteValue(LjChannel palette, int index, int c, int paletteSize, int bitDepth)
    {
        const int kLargeCubeOffset = 64;
        if (index < 0)
        {
            if (c >= 3)
            {
                return 0;
            }

            index = -(index + 1);
            index %= 1 + (2 * (72 - 1));
            int result = DeltaPalette[(index + 1) >> 1, c] * ((index & 1) != 0 ? 1 : -1);
            if (bitDepth > 8)
            {
                result *= 1 << (bitDepth - 8);
            }

            return result;
        }

        if (paletteSize <= index && index < paletteSize + kLargeCubeOffset)
        {
            if (c >= 3)
            {
                return 0;
            }

            index -= paletteSize;
            index >>= c * 2;
            return (int)(((ulong)(index % 4) * ((1UL << bitDepth) - 1)) >> 2) + (1 << Math.Max(0, bitDepth - 3));
        }

        if (paletteSize + kLargeCubeOffset <= index)
        {
            if (c >= 3)
            {
                return 0;
            }

            index -= paletteSize + kLargeCubeOffset;
            if (c == 1)
            {
                index /= 5;
            }
            else if (c == 2)
            {
                index /= 25;
            }

            return (int)(((ulong)(index % 5) * ((1UL << bitDepth) - 1)) >> 2);
        }

        return palette[index, c];
    }

    private static void InvPalette(LjModularImage input, uint beginC, uint nbColors, uint nbDeltas, LjPredictor predictor, LjWpHeader wpHeader)
    {
        if (input.NbMetaChannels < 1)
        {
            throw Fail("Palette transform without palette.");
        }

        int nb = input.Channel[0].H;
        int c0 = (int)beginC + 1;
        if (c0 >= input.Channel.Count)
        {
            throw Fail("Channel is out of range.");
        }

        int w = input.Channel[c0].W, h = input.Channel[c0].H;
        if (nb < 1)
        {
            throw Fail("Corrupted transforms");
        }

        for (int i = 1; i < nb; i++)
        {
            input.Channel.Insert(c0 + 1, new LjChannel(w, h, input.Channel[c0].HShift, input.Channel[c0].VShift));
        }

        LjChannel palette = input.Channel[0];
        int bitDepth = Math.Min(input.Bitdepth, 24);
        if (w == 0)
        {
        }
        else if (nbDeltas == 0 && predictor == LjPredictor.Zero)
        {
            if (nb == 1)
            {
                LjChannel ch = input.Channel[c0];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int index = Math.Clamp(ch[x, y], 0, palette.W - 1);
                        ch[x, y] = PaletteValue(palette, index, 0, palette.W, bitDepth);
                    }
                }
            }
            else
            {
                LjChannel idxCh = input.Channel[c0];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int index = idxCh[x, y];
                        for (int c = 0; c < nb; c++)
                        {
                            input.Channel[c0 + c][x, y] = PaletteValue(palette, index, c, palette.W, bitDepth);
                        }
                    }
                }
            }
        }
        else
        {
            LjChannel indexCh = input.Channel[c0];
            var indices = new LjChannel(indexCh.W, indexCh.H);
            Array.Copy(indexCh.Plane, indices.Plane, Math.Min(indexCh.Plane.Length, indices.Plane.Length));
            indices.Stride = indexCh.Stride;
            indices.Plane = (int[])indexCh.Plane.Clone();
            for (int c = 0; c < nb; c++)
            {
                LjChannel channel = input.Channel[c0 + c];
                if (c == 0)
                {
                    channel.Plane = new int[channel.Plane.Length];
                }

                LjWpState? wp = predictor == LjPredictor.Weighted ? new LjWpState(wpHeader, channel.W, channel.H) : null;
                for (int y = 0; y < channel.H; y++)
                {
                    for (int x = 0; x < channel.W; x++)
                    {
                        int index = indices.Plane[(y * indices.Stride) + x];
                        int entry = PaletteValue(palette, index, c, palette.W, bitDepth);
                        long val;
                        if (wp != null)
                        {
                            long guess = LjPredict.Predict(null, channel, x, y, predictor, wp, null, 0, null);
                            val = index < (int)nbDeltas ? guess + entry : entry;
                            channel[x, y] = (int)val;
                            wp.UpdateErrors(channel[x, y], x, y, channel.W);
                        }
                        else
                        {
                            if (index < (int)nbDeltas)
                            {
                                long guess = LjPredict.Predict(null, channel, x, y, predictor, null, null, 0, null);
                                val = guess + entry;
                            }
                            else
                            {
                                val = entry;
                            }

                            channel[x, y] = (int)val;
                        }
                    }
                }
            }
        }

        if (c0 >= input.NbMetaChannels)
        {
            input.NbMetaChannels--;
        }
        else
        {
            input.NbMetaChannels -= 2 - nb;
        }

        input.Channel.RemoveAt(0);
    }

    private static void MetaPalette(LjModularImage input, uint beginC, uint endC, uint nbColors, uint nbDeltas)
    {
        if (!CheckEqualChannels(input, beginC, endC))
        {
            throw Fail("Palette on channels of different shapes");
        }

        int nb = (int)(endC - beginC + 1);
        if (beginC >= input.NbMetaChannels)
        {
            input.NbMetaChannels++;
        }
        else
        {
            if (endC >= input.NbMetaChannels)
            {
                throw Fail("Invalid palette channel range");
            }

            input.NbMetaChannels += 2 - nb;
        }

        input.Channel.RemoveRange((int)beginC + 1, nb - 1);
        var pch = new LjChannel((int)(nbColors + nbDeltas), nb, -1, -1);
        input.Channel.Insert(0, pch);
    }

    private static long SmoothTendency(long b, long a, long n)
    {
        long diff = 0;
        if (b >= a && a >= n)
        {
            diff = ((4 * b) - (3 * n) - a + 6) / 12;
            if (diff - (diff & 1) > 2 * (b - a))
            {
                diff = (2 * (b - a)) + 1;
            }

            if (diff + (diff & 1) > 2 * (a - n))
            {
                diff = 2 * (a - n);
            }
        }
        else if (b <= a && a <= n)
        {
            diff = ((4 * b) - (3 * n) - a - 6) / 12;
            if (diff + (diff & 1) < 2 * (b - a))
            {
                diff = (2 * (b - a)) - 1;
            }

            if (diff - (diff & 1) < 2 * (a - n))
            {
                diff = 2 * (a - n);
            }
        }

        return diff;
    }

    private static void InvHSqueeze(LjModularImage input, int c, int rc)
    {
        LjChannel chin = input.Channel[c];
        LjChannel res = input.Channel[rc];
        if (res.W == 0)
        {
            chin.HShift--;
            return;
        }

        var chout = new LjChannel(chin.W + res.W, chin.H, chin.HShift - 1, chin.VShift);
        if (res.H == 0)
        {
            input.Channel[c] = chout;
            return;
        }

        for (int y = 0; y < chin.H; y++)
        {
            for (int x = 0; x < res.W; x++)
            {
                long diffMinusTendency = res[x, y];
                long avg = chin[x, y];
                long nextAvg = x + 1 < chin.W ? chin[x + 1, y] : avg;
                long left = x != 0 ? chout[(x << 1) - 1, y] : avg;
                long tendency = SmoothTendency(left, avg, nextAvg);
                long diff = diffMinusTendency + tendency;
                long aa = avg + (diff / 2);
                chout[x << 1, y] = (int)aa;
                chout[(x << 1) + 1, y] = (int)(aa - diff);
            }

            if ((chout.W & 1) != 0)
            {
                chout[chout.W - 1, y] = chin[chin.W - 1, y];
            }
        }

        input.Channel[c] = chout;
    }

    private static void InvVSqueeze(LjModularImage input, int c, int rc)
    {
        LjChannel chin = input.Channel[c];
        LjChannel res = input.Channel[rc];
        if (res.H == 0)
        {
            chin.VShift--;
            return;
        }

        var chout = new LjChannel(chin.W, chin.H + res.H, chin.HShift, chin.VShift - 1);
        if (res.W == 0)
        {
            input.Channel[c] = chout;
            return;
        }

        for (int y = 0; y < res.H; y++)
        {
            for (int x = 0; x < chin.W; x++)
            {
                long avg = chin[x, y];
                long nextAvg = chin[x, y + 1 < chin.H ? y + 1 : y];
                long top = y > 0 ? chout[x, (y << 1) - 1] : avg;
                long tendency = SmoothTendency(top, avg, nextAvg);
                long diff = res[x, y] + tendency;
                long o = avg + (diff / 2);
                chout[x, y << 1] = (int)o;
                chout[x, (y << 1) + 1] = (int)(o - diff);
            }
        }

        if ((chout.H & 1) != 0)
        {
            int y = chin.H - 1;
            for (int x = 0; x < chin.W; x++)
            {
                chout[x, y << 1] = chin[x, y];
            }
        }

        input.Channel[c] = chout;
    }

    private static void InvSqueeze(LjModularImage input, List<LjSqueezeParams> parameters)
    {
        for (int i = parameters.Count - 1; i >= 0; i--)
        {
            LjSqueezeParams p = parameters[i];
            CheckMetaSqueezeParams(p, input.Channel.Count);
            uint beginc = p.BeginC;
            uint endc = p.BeginC + p.NumC - 1;
            uint offset = p.InPlace ? endc + 1 : (uint)(input.Channel.Count + beginc - endc - 1);
            if (beginc < input.NbMetaChannels)
            {
                input.NbMetaChannels -= (int)p.NumC;
            }

            for (uint c = beginc; c <= endc; c++)
            {
                uint rc = offset + c - beginc;
                if (input.Channel[(int)c].W < input.Channel[(int)rc].W || input.Channel[(int)c].H < input.Channel[(int)rc].H)
                {
                    throw Fail("Corrupted squeeze transform");
                }

                if (p.Horizontal)
                {
                    InvHSqueeze(input, (int)c, (int)rc);
                }
                else
                {
                    InvVSqueeze(input, (int)c, (int)rc);
                }
            }

            input.Channel.RemoveRange((int)offset, (int)(endc - beginc + 1));
        }
    }

    private static void CheckMetaSqueezeParams(LjSqueezeParams p, int numChannels)
    {
        long c1 = p.BeginC, c2 = p.BeginC + (long)p.NumC - 1;
        if (c1 < 0 || c1 >= numChannels || c2 < 0 || c2 >= numChannels || c2 < c1)
        {
            throw Fail("Invalid channel range");
        }
    }

    private static void DefaultSqueezeParameters(List<LjSqueezeParams> parameters, LjModularImage image)
    {
        int nbChannels = image.Channel.Count - image.NbMetaChannels;
        parameters.Clear();
        int w = image.Channel[image.NbMetaChannels].W;
        int h = image.Channel[image.NbMetaChannels].H;
        bool wide = w > h;
        if (nbChannels > 2 && image.Channel[image.NbMetaChannels + 1].W == w && image.Channel[image.NbMetaChannels + 1].H == h)
        {
            parameters.Add(new LjSqueezeParams { Horizontal = true, InPlace = false, BeginC = (uint)image.NbMetaChannels + 1, NumC = 2 });
            parameters.Add(new LjSqueezeParams { Horizontal = false, InPlace = false, BeginC = (uint)image.NbMetaChannels + 1, NumC = 2 });
        }

        const int kMaxFirstPreviewSize = 8;
        var basep = new LjSqueezeParams { BeginC = (uint)image.NbMetaChannels, NumC = (uint)nbChannels, InPlace = true };
        if (!wide)
        {
            if (h > kMaxFirstPreviewSize)
            {
                parameters.Add(new LjSqueezeParams { BeginC = basep.BeginC, NumC = basep.NumC, InPlace = true, Horizontal = false });
                h = (h + 1) / 2;
            }
        }

        while (w > kMaxFirstPreviewSize || h > kMaxFirstPreviewSize)
        {
            if (w > kMaxFirstPreviewSize)
            {
                parameters.Add(new LjSqueezeParams { BeginC = basep.BeginC, NumC = basep.NumC, InPlace = true, Horizontal = true });
                w = (w + 1) / 2;
            }

            if (h > kMaxFirstPreviewSize)
            {
                parameters.Add(new LjSqueezeParams { BeginC = basep.BeginC, NumC = basep.NumC, InPlace = true, Horizontal = false });
                h = (h + 1) / 2;
            }
        }
    }

    private static void MetaSqueeze(LjModularImage image, List<LjSqueezeParams> parameters)
    {
        if (parameters.Count == 0)
        {
            DefaultSqueezeParameters(parameters, image);
        }

        foreach (LjSqueezeParams p in parameters)
        {
            CheckMetaSqueezeParams(p, image.Channel.Count);
            uint beginc = p.BeginC, endc = p.BeginC + p.NumC - 1;
            uint offset;
            if (beginc < image.NbMetaChannels)
            {
                if (endc >= image.NbMetaChannels)
                {
                    throw Fail("Invalid squeeze: mix of meta and nonmeta channels");
                }

                if (!p.InPlace)
                {
                    throw Fail("Invalid squeeze: meta channels require in-place residuals");
                }

                image.NbMetaChannels += (int)p.NumC;
            }

            offset = p.InPlace ? endc + 1 : (uint)image.Channel.Count;
            for (uint c = beginc; c <= endc; c++)
            {
                LjChannel ch = image.Channel[(int)c];
                if (ch.HShift > 30 || ch.VShift > 30)
                {
                    throw Fail("Too many squeezes: shift > 30");
                }

                int w = ch.W, h = ch.H;
                if (w == 0 || h == 0)
                {
                    throw Fail("Squeezing empty channel");
                }

                if (p.Horizontal)
                {
                    ch.W = (w + 1) / 2;
                    if (ch.HShift >= 0)
                    {
                        ch.HShift++;
                    }

                    w -= (w + 1) / 2;
                }
                else
                {
                    ch.H = (h + 1) / 2;
                    if (ch.VShift >= 0)
                    {
                        ch.VShift++;
                    }

                    h -= (h + 1) / 2;
                }

                ShrinkChannel(ch);
                var placeholder = new LjChannel(w, h, ch.HShift, ch.VShift);
                image.Channel.Insert((int)(offset + (c - beginc)), placeholder);
            }
        }
    }

    // ---- ModularDecode / ModularGenericDecompress ----

    private static void ValidateChannelDimensions(LjModularImage image, int groupDimOpt)
    {
        int nb = image.Channel.Count;
        foreach (bool isDc in (bool[])[true, false])
        {
            long groupDim = (long)groupDimOpt * (isDc ? 8 : 1);
            int c = image.NbMetaChannels;
            for (; c < nb; c++)
            {
                LjChannel ch = image.Channel[c];
                if (ch.W > groupDimOpt || ch.H > groupDimOpt)
                {
                    break;
                }
            }

            for (; c < nb; c++)
            {
                LjChannel ch = image.Channel[c];
                if (ch.W == 0 || ch.H == 0)
                {
                    continue;
                }

                bool isDcChannel = Math.Min(ch.HShift, ch.VShift) >= 3;
                if (isDcChannel != isDc)
                {
                    continue;
                }

                long tileDim = groupDim >> Math.Max(ch.HShift, ch.VShift);
                if (tileDim == 0)
                {
                    throw Fail("Inconsistent transforms");
                }
            }
        }
    }

    public sealed class GlobalTree
    {
        public List<LjTreeNode> Tree = new();
        public LjAnsCode? Code;
        public byte[] ContextMap = Array.Empty<byte>();
    }

    /// <summary>ModularDecode. Returns false when the image has no channels to decode.</summary>
    public static void ModularDecode(LjBitReader br, LjModularImage image, LjGroupHeader header, int groupId, int maxChanSize, int groupDim,
        GlobalTree? global)
    {
        if (image.Channel.Count == 0)
        {
            return;
        }

        LjFields.ReadInto(header, br);
        if (!br.AllReadsWithinBounds)
        {
            throw Fail("Read overrun before ModularDecode");
        }

        image.Transform = new List<LjTransform>(header.Transforms);
        foreach (LjTransform t in image.Transform)
        {
            MetaApply(image, t);
        }

        ValidateChannelDimensions(image, groupDim);
        int nb = image.Channel.Count;
        int numChans = 0;
        int distanceMultiplier = 0;
        for (int i = 0; i < nb; i++)
        {
            LjChannel ch = image.Channel[i];
            if (i >= image.NbMetaChannels && (ch.W > maxChanSize || ch.H > maxChanSize))
            {
                break;
            }

            if (ch.W == 0 || ch.H == 0)
            {
                continue;
            }

            distanceMultiplier = Math.Max(distanceMultiplier, ch.W);
            numChans++;
        }

        if (numChans == 0)
        {
            return;
        }

        List<LjTreeNode> tree;
        LjAnsCode code;
        byte[] ctxMap;
        if (!header.UseGlobalTree)
        {
            long maxTreeSize = 1024;
            for (int i = 0; i < nb; i++)
            {
                LjChannel ch = image.Channel[i];
                if (i >= image.NbMetaChannels && (ch.W > maxChanSize || ch.H > maxChanSize))
                {
                    break;
                }

                maxTreeSize += (long)ch.W * ch.H;
            }

            maxTreeSize = Math.Min(1L << 20, maxTreeSize);
            tree = DecodeTree(br, maxTreeSize);
            code = LjDecAns.DecodeHistograms(br, (tree.Count + 1) / 2, out ctxMap);
        }
        else
        {
            if (global == null || global.Code == null || global.Tree.Count == 0)
            {
                throw Fail("No global tree available but one was requested");
            }

            tree = global.Tree;
            code = global.Code;
            ctxMap = global.ContextMap;
        }

        var reader = new LjAnsSymbolReader(code, br, distanceMultiplier);
        for (int c = 0; c < nb; c++)
        {
            LjChannel ch = image.Channel[c];
            if (c >= image.NbMetaChannels && (ch.W > maxChanSize || ch.H > maxChanSize))
            {
                break;
            }

            if (ch.W == 0 || ch.H == 0)
            {
                continue;
            }

            DecodeChannel(br, reader, ctxMap, tree, header.WpHeader, c, groupId, image);
            if (!br.AllReadsWithinBounds)
            {
                throw Fail("Truncated input");
            }
        }

        if (!reader.CheckAnsFinalState())
        {
            throw Fail("ANS decode final state failed");
        }
    }

    /// <summary>ModularGenericDecompress.</summary>
    public static void GenericDecompress(LjBitReader br, LjModularImage image, LjGroupHeader? header, int groupId, int maxChanSize, int groupDim,
        bool undoTransforms, GlobalTree? global)
    {
        var reqSizes = new List<(int W, int H)>();
        foreach (LjChannel c in image.Channel)
        {
            reqSizes.Add((c.W, c.H));
        }

        header ??= new LjGroupHeader();
        ModularDecode(br, image, header, groupId, maxChanSize, groupDim, global);
        if (undoTransforms)
        {
            UndoTransforms(image, header.WpHeader);
            if (image.Channel.Count != reqSizes.Count)
            {
                throw Fail("Transforms changed the channel count");
            }

            for (int c = 0; c < reqSizes.Count; c++)
            {
                if (reqSizes[c].W != image.Channel[c].W || reqSizes[c].H != image.Channel[c].H)
                {
                    throw Fail("Transforms changed the channel size");
                }
            }
        }
    }
}
