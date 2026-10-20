// libjxl 0.12 LZ77 backward-reference search (enc_lz77.cc HashChain, ApplyLZ77_LZ77, ApplyLZ77_Optimal).
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal static partial class LjLz77
{
    private const int WindowSize = 1 << 20;
    private const int NumSpecialDistances = 120;

    private static readonly sbyte[,] SpecialDistances =
    {
        { 0, 1 }, { 1, 0 }, { 1, 1 }, { -1, 1 }, { 0, 2 }, { 2, 0 }, { 1, 2 }, { -1, 2 },
        { 2, 1 }, { -2, 1 }, { 2, 2 }, { -2, 2 }, { 0, 3 }, { 3, 0 }, { 1, 3 }, { -1, 3 },
        { 3, 1 }, { -3, 1 }, { 2, 3 }, { -2, 3 }, { 3, 2 }, { -3, 2 }, { 0, 4 }, { 4, 0 },
        { 1, 4 }, { -1, 4 }, { 4, 1 }, { -4, 1 }, { 3, 3 }, { -3, 3 }, { 2, 4 }, { -2, 4 },
        { 4, 2 }, { -4, 2 }, { 0, 5 }, { 3, 4 }, { -3, 4 }, { 4, 3 }, { -4, 3 }, { 5, 0 },
        { 1, 5 }, { -1, 5 }, { 5, 1 }, { -5, 1 }, { 2, 5 }, { -2, 5 }, { 5, 2 }, { -5, 2 },
        { 4, 4 }, { -4, 4 }, { 3, 5 }, { -3, 5 }, { 5, 3 }, { -5, 3 }, { 0, 6 }, { 6, 0 },
        { 1, 6 }, { -1, 6 }, { 6, 1 }, { -6, 1 }, { 2, 6 }, { -2, 6 }, { 6, 2 }, { -6, 2 },
        { 4, 5 }, { -4, 5 }, { 5, 4 }, { -5, 4 }, { 3, 6 }, { -3, 6 }, { 6, 3 }, { -6, 3 },
        { 0, 7 }, { 7, 0 }, { 1, 7 }, { -1, 7 }, { 5, 5 }, { -5, 5 }, { 7, 1 }, { -7, 1 },
        { 4, 6 }, { -4, 6 }, { 6, 4 }, { -6, 4 }, { 2, 7 }, { -2, 7 }, { 7, 2 }, { -7, 2 },
        { 3, 7 }, { -3, 7 }, { 7, 3 }, { -7, 3 }, { 5, 6 }, { -5, 6 }, { 6, 5 }, { -6, 5 },
        { 8, 0 }, { 4, 7 }, { -4, 7 }, { 7, 4 }, { -7, 4 }, { 8, 1 }, { 8, 2 }, { 6, 6 },
        { -6, 6 }, { 8, 3 }, { 5, 7 }, { -5, 7 }, { 7, 5 }, { -7, 5 }, { 8, 4 }, { 6, 7 },
        { -6, 7 }, { 7, 6 }, { -7, 6 }, { 8, 5 }, { 7, 7 }, { -7, 7 }, { 8, 6 }, { 8, 7 },
    };

    public static int SpecialDistance(int index, int multiplier)
    {
        int dist = SpecialDistances[index, 0] + (multiplier * SpecialDistances[index, 1]);
        return dist > 1 ? dist : 1;
    }

    private sealed class HashChain
    {
        private readonly int size;
        private readonly uint[] data;
        private const uint HashNumValues = 32768;
        private const uint HashMask = HashNumValues - 1;
        private const int HashShift = 5;
        private readonly int[] head;
        private readonly uint[] chain;
        private readonly int[] val;
        private readonly int[] headz;
        private readonly uint[] chainz;
        private readonly uint[] zeros;
        private uint numzeros;
        private readonly int windowSize;
        private readonly int windowMask;
        private readonly int minLength;
        private readonly int maxLength;
        private readonly Dictionary<int, int> specialDistTable = new();
        private readonly int numSpecialDistances;
        private const uint MaxChainLength = 256;

        public HashChain(List<LjToken> tokens, int windowSize, int minLength, int maxLength, int distanceMultiplier)
        {
            size = tokens.Count;
            this.windowSize = windowSize;
            windowMask = windowSize - 1;
            this.minLength = minLength;
            this.maxLength = maxLength;
            data = new uint[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = tokens[i].Value;
            }

            head = new int[HashNumValues];
            Array.Fill(head, -1);
            val = new int[windowSize];
            Array.Fill(val, -1);
            chain = new uint[windowSize];
            for (uint i = 0; i < windowSize; i++)
            {
                chain[i] = i;
            }

            zeros = new uint[windowSize];
            headz = new int[windowSize + 1];
            Array.Fill(headz, -1);
            chainz = new uint[windowSize];
            for (uint i = 0; i < windowSize; i++)
            {
                chainz[i] = i;
            }

            if (distanceMultiplier != 0)
            {
                for (int i = NumSpecialDistances - 1; i >= 0; --i)
                {
                    specialDistTable[SpecialDistance(i, distanceMultiplier)] = i;
                }

                numSpecialDistances = NumSpecialDistances;
            }
        }

        private uint GetHash(int pos)
        {
            uint result = 0;
            if (pos + 2 < size)
            {
                result ^= data[pos];
                result ^= data[pos + 1] << HashShift;
                result ^= data[pos + 2] << (HashShift * 2);
            }
            else
            {
                return 0;
            }

            return result & HashMask;
        }

        private uint CountZeros(int pos, uint prevzeros)
        {
            int end = pos + windowSize;
            if (end > size)
            {
                end = size;
            }

            if (prevzeros > 0)
            {
                if (prevzeros >= windowMask && data[end - 1] == 0 && end == pos + windowSize)
                {
                    return prevzeros;
                }

                return prevzeros - 1;
            }

            uint num = 0;
            while (pos + num < end && data[pos + num] == 0)
            {
                num++;
            }

            return num;
        }

        public void Update(int pos)
        {
            uint hashval = GetHash(pos);
            int wpos = pos & windowMask;
            val[wpos] = (int)hashval;
            if (head[hashval] != -1)
            {
                chain[wpos] = (uint)head[hashval];
            }

            head[hashval] = wpos;
            if (pos > 0 && data[pos] != data[pos - 1])
            {
                numzeros = 0;
            }

            numzeros = CountZeros(pos, numzeros);
            zeros[wpos] = numzeros;
            if (headz[numzeros] != -1)
            {
                chainz[wpos] = (uint)headz[numzeros];
            }

            headz[numzeros] = wpos;
        }

        public void Update(int pos, int len)
        {
            for (int i = 0; i < len; i++)
            {
                Update(pos + i);
            }
        }

        public void FindMatches(int pos, Action<int, int> foundMatch)
        {
            uint wpos = (uint)(pos & windowMask);
            uint hashval = GetHash(pos);
            uint hashpos = chain[wpos];
            int prevDist = 0;
            int end = (int)Math.Min((long)pos + maxLength, size);
            uint chainlength = 0;
            uint bestLen = 0;
            while (true)
            {
                int dist = hashpos <= wpos ? (int)(wpos - hashpos) : (int)(wpos - hashpos + windowMask + 1);
                if (dist < prevDist)
                {
                    break;
                }

                prevDist = dist;
                uint len = 0;
                if (dist > 0)
                {
                    int i = pos;
                    int j = pos - dist;
                    if (numzeros > 3)
                    {
                        int r = (int)Math.Min(numzeros - 1, zeros[hashpos]);
                        if (i + r >= end)
                        {
                            r = end - i - 1;
                        }

                        i += r;
                        j += r;
                    }

                    while (i < end && data[i] == data[j])
                    {
                        i++;
                        j++;
                    }

                    len = (uint)(i - pos);
                    if (len >= minLength && len + 2 >= bestLen)
                    {
                        int distSymbol = specialDistTable.TryGetValue(dist, out int sd) ? sd : numSpecialDistances + dist - 1;
                        foundMatch((int)len, distSymbol);
                        if (len > bestLen)
                        {
                            bestLen = len;
                        }
                    }
                }

                chainlength++;
                if (chainlength >= MaxChainLength)
                {
                    break;
                }

                if (numzeros >= 3 && len > numzeros)
                {
                    if (hashpos == chainz[hashpos])
                    {
                        break;
                    }

                    hashpos = chainz[hashpos];
                    if (zeros[hashpos] != numzeros)
                    {
                        break;
                    }
                }
                else
                {
                    if (hashpos == chain[hashpos])
                    {
                        break;
                    }

                    hashpos = chain[hashpos];
                    if (val[hashpos] != (int)hashval)
                    {
                        break;
                    }
                }
            }
        }

        public void FindMatch(int pos, out int resultDistSymbol, out int resultLen)
        {
            int ds = 0, ln = 1;
            FindMatches(pos, (len, distSymbol) =>
            {
                if (len > ln || (len == ln && ds > distSymbol))
                {
                    ln = len;
                    ds = distSymbol;
                }
            });
            resultDistSymbol = ds;
            resultLen = ln;
        }
    }

    private static readonly float[] LenCostTable =
    [
        2.797667318563126f, 3.213177690381199f, 2.5706009246743737f, 2.408392498667534f, 2.829649191872326f, 3.3923087753324577f,
        4.029267451554331f, 4.415576699706408f, 4.509357574741465f, 9.21481543803004f, 10.020590190114898f, 11.858671627804766f,
        12.45853300490526f, 11.713105831990857f, 12.561996324849314f, 13.775477692278367f, 13.174027068768641f,
    ];

    private static float LenCost(int len)
    {
        new LjHybridUint(1, 0, 0).Encode((uint)len, out uint tok, out uint nbits, out _);
        if (tok >= LenCostTable.Length)
        {
            tok = (uint)LenCostTable.Length - 1;
        }

        return LenCostTable[tok] + nbits;
    }

    private static readonly float[] DistCostTable =
    [
        6.368282626312716f, 5.680793277090298f, 8.347404197105247f, 7.641619201599141f, 6.914328374119438f, 7.959808291537444f,
        8.70023120759855f, 8.71378518934703f, 9.379132523982769f, 9.110472749092708f, 9.159029569270908f, 9.430936766731973f,
        7.278284055315169f, 7.8278514904267755f, 10.026641158289236f, 9.976049229827066f, 9.64351607048908f, 9.563403863480442f,
        10.171474111762747f, 10.45950155077234f, 9.994813912104219f, 10.322524683741156f, 8.465808729388186f, 8.756254166066853f,
        10.160930174662234f, 10.247329273413435f, 10.04090403724809f, 10.129398517544082f, 9.342311691539546f, 9.07608009102374f,
        10.104799540677513f, 10.378079384990906f, 10.165828974075072f, 10.337595322341553f, 7.940557464567944f, 10.575665823319431f,
        11.023344321751955f, 10.736144698831827f, 11.118277044595054f, 7.468468230648442f, 10.738305230932939f, 10.906980780216568f,
        10.163468216353817f, 10.17805759656433f, 11.167283670483565f, 11.147050200274544f, 10.517921919244333f, 10.651764778156886f,
        10.17074446448919f, 11.217636876224745f, 11.261630721139484f, 11.403140815247259f, 10.892472096873417f, 11.1859607804481f,
        8.017346947551262f, 7.895143720278828f, 11.036577113822025f, 11.170562110315794f, 10.326988722591086f, 10.40872184751056f,
        11.213498225466386f, 11.30580635516863f, 10.672272515665442f, 10.768069466228063f, 11.145257364153565f, 11.64668307145549f,
        10.593156194627339f, 11.207499484844943f, 10.767517766396908f, 10.826629811407042f, 10.737764794499988f, 10.6200448518045f,
        10.191315385198092f, 8.468384171390085f, 11.731295299170432f, 11.824619886654398f, 10.41518844301179f, 10.16310536548649f,
        10.539423685097576f, 10.495136599328031f, 10.469112847728267f, 11.72057686174922f, 10.910326337834674f, 11.378921834673758f,
        11.847759036098536f, 11.92071647623854f, 10.810628276345282f, 11.008601085273893f, 11.910326337834674f, 11.949212023423133f,
        11.298614839104337f, 11.611603659010392f, 10.472930394619985f, 11.835564720850282f, 11.523267392285337f, 12.01055816679611f,
        8.413029688994023f, 11.895784139536406f, 11.984679534970505f, 11.220654278717394f, 11.716311684833672f, 10.61036646226114f,
        10.89849965960364f, 10.203762898863669f, 10.997560826267238f, 11.484217379438984f, 11.792836176993665f, 12.24310468755171f,
        11.464858097919262f, 12.212747017409377f, 11.425595666074955f, 11.572048533398757f, 12.742093965163013f, 11.381874288645637f,
        12.191870445817015f, 11.683156920035426f, 11.152442115262197f, 11.90303691580457f, 11.653292787169159f, 11.938615382266098f,
        16.970641701570223f, 16.853602280380002f, 17.26240782594733f, 16.644655390108507f, 17.14310889757499f, 16.910935455445955f,
        17.505678976959697f, 17.213498225466388f, 2.4162310293553024f, 3.494587244462329f, 3.5258600986408344f, 3.4959806589517095f,
        3.098390886949687f, 3.343454654302911f, 3.588847442290287f, 4.14614790111827f, 5.152948641990529f, 7.433696808092598f,
        9.716311684833672f,
    ];

    private static float DistCost(int dist)
    {
        new LjHybridUint(7, 0, 0).Encode((uint)dist, out uint tok, out uint nbits, out _);
        if (tok >= DistCostTable.Length)
        {
            tok = (uint)DistCostTable.Length - 1;
        }

        return DistCostTable[tok] + nbits;
    }

    private static List<List<LjToken>>? ApplyLz77Lz77(LjHistogramParams p, int numContexts, List<List<LjToken>> tokens, LjLz77Params lz77)
    {
        var tokensLz77 = new List<List<LjToken>>(tokens.Count);
        var sce = new SymbolCostEstimator(numContexts, p.ForceHuffman, tokens, lz77);
        float bitDecrease = 0;
        long totalSymbols = 0;
        LjHybridUint uintConfig = LjHybridUint.Default;
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

            int maxDistance = input.Count;
            int minLength = (int)lz77.MinLength;
            int maxLength = input.Count;
            int windowSize = 1;
            while (windowSize < maxDistance && windowSize < WindowSize)
            {
                windowSize <<= 1;
            }

            var chain = new HashChain(input, windowSize, minLength, maxLength, distanceMultiplier);
            const int maxLazyMatchLen = 256;
            bool alreadyUpdated = false;
            for (int i = 0; i < input.Count; i++)
            {
                output.Add(input[i]);
                if (!alreadyUpdated)
                {
                    chain.Update(i);
                }

                alreadyUpdated = false;
                chain.FindMatch(i, out int distSymbol, out int len);
                if (len >= minLength)
                {
                    if (len < maxLazyMatchLen && i + 1 < input.Count)
                    {
                        chain.Update(i + 1);
                        alreadyUpdated = true;
                        chain.FindMatch(i + 1, out int distSymbol2, out int len2);
                        if (len2 > len)
                        {
                            ++i;
                            alreadyUpdated = false;
                            len = len2;
                            distSymbol = distSymbol2;
                            output.Add(input[i]);
                        }
                    }

                    float cost = symCost[i + len] - symCost[i];
                    int lz77Len = len - (int)lz77.MinLength;
                    float lz77Cost = LenCost(lz77Len) + DistCost(distSymbol) + sce.AddSymbolCost((int)output[^1].Context);
                    if (lz77Cost <= cost)
                    {
                        LjToken last = output[^1];
                        last.Value = (uint)(len - minLength);
                        last.IsLz77Length = true;
                        output[^1] = last;
                        output.Add(new LjToken((uint)lz77.NonserializedDistanceContext, (uint)distSymbol));
                        bitDecrease += cost - lz77Cost;
                    }
                    else
                    {
                        for (int j = 1; j < len; j++)
                        {
                            output.Add(input[i + j]);
                        }
                    }

                    if (alreadyUpdated)
                    {
                        chain.Update(i + 2, len - 2);
                        alreadyUpdated = false;
                    }
                    else
                    {
                        chain.Update(i + 1, len - 1);
                    }

                    i += len - 1;
                }
            }
        }

        if (bitDecrease > (totalSymbols * 0.2) + 16)
        {
            return tokensLz77;
        }

        return null;
    }

    private static List<List<LjToken>>? ApplyLz77Optimal(LjHistogramParams p, int numContexts, List<List<LjToken>> tokens, LjLz77Params lz77)
    {
        List<List<LjToken>>? forCost = ApplyLz77Lz77(p, numContexts, tokens, lz77);
        if (forCost == null)
        {
            return null;
        }

        var sce = new SymbolCostEstimator(numContexts + 1, p.ForceHuffman, forCost, lz77);
        var tokensLz77 = new List<List<LjToken>>(tokens.Count);
        LjHybridUint uintConfig = LjHybridUint.Default;
        var distSymbols = new List<int>();
        for (int stream = 0; stream < tokens.Count; stream++)
        {
            int distanceMultiplier = p.ImageWidths != null && p.ImageWidths.Length > stream ? p.ImageWidths[stream] : 0;
            List<LjToken> input = tokens[stream];
            var output = new List<LjToken>(input.Count);
            tokensLz77.Add(output);
            var symCost = new float[input.Count + 1];
            for (int i = 0; i < input.Count; i++)
            {
                uintConfig.Encode(input[i].Value, out uint tok, out uint nbits, out _);
                symCost[i + 1] = sce.Bits((int)input[i].Context, (int)tok) + nbits + symCost[i];
            }

            int maxDistance = input.Count;
            int minLength = (int)lz77.MinLength;
            int maxLength = input.Count;
            int windowSize = 1;
            while (windowSize < maxDistance && windowSize < WindowSize)
            {
                windowSize <<= 1;
            }

            var chain = new HashChain(input, windowSize, minLength, maxLength, distanceMultiplier);
            var pcLen = new uint[input.Count + 1];
            var pcDist = new uint[input.Count + 1];
            var pcCtx = new uint[input.Count + 1];
            var pcCost = new float[input.Count + 1];
            Array.Fill(pcCost, float.MaxValue);
            pcCost[0] = 0;
            int rleLength = 0;
            int skipLz77 = 0;
            for (int i = 0; i < input.Count; i++)
            {
                chain.Update(i);
                float litCost = pcCost[i] + symCost[i + 1] - symCost[i];
                if (pcCost[i + 1] > litCost)
                {
                    pcDist[i + 1] = 0;
                    pcLen[i + 1] = 1;
                    pcCtx[i + 1] = input[i].Context;
                    pcCost[i + 1] = litCost;
                }

                if (skipLz77 > 0)
                {
                    skipLz77--;
                    continue;
                }

                distSymbols.Clear();
                chain.FindMatches(i, (len, distSymbol) =>
                {
                    while (distSymbols.Count <= len)
                    {
                        distSymbols.Add(distSymbol);
                    }

                    if (distSymbol < distSymbols[len])
                    {
                        distSymbols[len] = distSymbol;
                    }
                });
                if (distSymbols.Count <= minLength)
                {
                    continue;
                }

                {
                    int bestCost = distSymbols[^1];
                    for (int j = distSymbols.Count - 1; j >= minLength; j--)
                    {
                        if (distSymbols[j] < bestCost)
                        {
                            bestCost = distSymbols[j];
                        }

                        distSymbols[j] = bestCost;
                    }
                }

                for (int j = minLength; j < distSymbols.Count; j++)
                {
                    float lz77Cost = sce.LenCost((int)input[i].Context, j - minLength, lz77) + sce.DistCost(distSymbols[j], lz77);
                    float cost = pcCost[i] + lz77Cost;
                    if (pcCost[i + j] > cost)
                    {
                        pcLen[i + j] = (uint)j;
                        pcDist[i + j] = (uint)(distSymbols[j] + 1);
                        pcCtx[i + j] = input[i].Context;
                        pcCost[i + j] = cost;
                    }
                }

                if ((distSymbols[^1] == 0 && distanceMultiplier == 0) || (distSymbols[^1] == 1 && distanceMultiplier != 0))
                {
                    rleLength++;
                }
                else
                {
                    rleLength = 0;
                }

                if (rleLength >= 8 && distSymbols.Count > 9)
                {
                    skipLz77 = distSymbols.Count - 10;
                    rleLength = 0;
                }
            }

            int pos = input.Count;
            while (pos > 0)
            {
                bool isLz77Length = pcDist[pos] != 0;
                if (isLz77Length)
                {
                    output.Add(new LjToken((uint)lz77.NonserializedDistanceContext, pcDist[pos] - 1));
                }

                uint v = isLz77Length ? pcLen[pos] - (uint)minLength : input[pos - 1].Value;
                output.Add(new LjToken(pcCtx[pos], v) { IsLz77Length = isLz77Length });
                pos -= (int)pcLen[pos];
            }

            output.Reverse();
        }

        return tokensLz77;
    }
}
