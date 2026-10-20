using System.Runtime.CompilerServices;

namespace SharpImage.Formats.WebP;

/// <summary>PixOrCopy: a literal ARGB, a color-cache index or a (distance, length) copy.</summary>
internal struct PixOrCopy
{
    public const byte KLiteral = 0, KCacheIdx = 1, KCopy = 2;
    public byte Mode;
    public ushort Len;
    public uint ArgbOrDistance;

    public readonly bool IsLiteral => Mode == KLiteral;
    public readonly bool IsCacheIdx => Mode == KCacheIdx;
    public readonly bool IsCopy => Mode == KCopy;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Literal(int component) => (int)((ArgbOrDistance >> (component * 8)) & 0xff);

    public static PixOrCopy CreateCopy(uint distance, int len) => new() { Mode = KCopy, ArgbOrDistance = distance, Len = (ushort)len };
    public static PixOrCopy CreateCacheIdx(int idx) => new() { Mode = KCacheIdx, ArgbOrDistance = (uint)idx, Len = 1 };
    public static PixOrCopy CreateLiteral(uint argb) => new() { Mode = KLiteral, ArgbOrDistance = argb, Len = 1 };
}

/// <summary>VP8LBackwardRefs: the ordered list of PixOrCopy (libwebp's block chain only manages memory).</summary>
internal sealed class Vp8lBackwardRefs
{
    public PixOrCopy[] Items = new PixOrCopy[256];
    public int Count;

    public void Clear() => Count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in PixOrCopy v)
    {
        if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
        Items[Count++] = v;
    }

    /// <summary>BackwardRefsClone (to = from).</summary>
    public static void CloneTo(Vp8lBackwardRefs from, Vp8lBackwardRefs to)
    {
        if (to.Items.Length < from.Count) to.Items = new PixOrCopy[from.Items.Length];
        Array.Copy(from.Items, to.Items, from.Count);
        to.Count = from.Count;
    }

    /// <summary>BackwardRefsSwap.</summary>
    public static void Swap(Vp8lBackwardRefs a, Vp8lBackwardRefs b)
    {
        (a.Items, b.Items) = (b.Items, a.Items);
        (a.Count, b.Count) = (b.Count, a.Count);
    }
}

/// <summary>VP8LHashChain: per pixel, the best match as (offset &lt;&lt; 12) | length.</summary>
internal sealed class Vp8lHashChain
{
    public uint[] OffsetLength;
    public int Size;

    public Vp8lHashChain(int size)
    {
        OffsetLength = new uint[size];
        Size = size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindOffset(int basePosition) => (int)(OffsetLength[basePosition] >> Vp8lBackwardRefsUtil.MaxLengthBits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindLength(int basePosition) => (int)(OffsetLength[basePosition] & ((1u << Vp8lBackwardRefsUtil.MaxLengthBits) - 1));
}

/// <summary>VP8LColorCache.</summary>
internal sealed class Vp8lColorCache
{
    public readonly uint[] Colors;
    public readonly int HashShift;
    public readonly int HashBits;

    public Vp8lColorCache(int hashBits)
    {
        Colors = new uint[1 << hashBits];
        HashShift = 32 - hashBits;
        HashBits = hashBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Lookup(uint key) => Colors[key];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(uint key, uint argb) => Colors[key] = argb;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(uint argb) => Colors[Vp8lCommon.HashPix(argb, HashShift)] = argb;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetIndex(uint argb) => Vp8lCommon.HashPix(argb, HashShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Contains(uint argb)
    {
        int key = Vp8lCommon.HashPix(argb, HashShift);
        return Colors[key] == argb ? key : -1;
    }
}

/// <summary>src/enc/backward_references_enc.c and backward_references_cost_enc.c, ported line by line.</summary>
internal static class Vp8lBackwardRefsUtil
{
    public const int HashBits = 18;
    public const int HashSize = 1 << HashBits;
    public const int MaxLengthBits = 12;
    public const int WindowSizeBits = 20;
    public const int MaxLength = (1 << MaxLengthBits) - 1;
    private const int WindowSize = (1 << WindowSizeBits) - 120;
    private const int MinLength = 4;
    public const int LZ77Standard = 1, LZ77Rle = 2, LZ77Box = 4;

    private static readonly byte[] PlaneToCodeLut =
    [
        96, 73, 55, 39, 23, 13, 5, 1, 255, 255, 255, 255, 255, 255, 255, 255,
        101, 78, 58, 42, 26, 16, 8, 2, 0, 3, 9, 17, 27, 43, 59, 79,
        102, 86, 62, 46, 32, 20, 10, 6, 4, 7, 11, 21, 33, 47, 63, 87,
        105, 90, 70, 52, 37, 28, 18, 14, 12, 15, 19, 29, 38, 53, 71, 91,
        110, 99, 82, 66, 48, 35, 30, 24, 22, 25, 31, 36, 49, 67, 83, 100,
        115, 108, 94, 76, 64, 50, 44, 40, 34, 41, 45, 51, 65, 77, 95, 109,
        118, 113, 103, 92, 80, 68, 60, 56, 54, 57, 61, 69, 81, 93, 104, 114,
        119, 116, 111, 106, 97, 88, 84, 74, 72, 75, 85, 89, 98, 107, 112, 117,
    ];

    public static int DistanceToPlaneCode(int xsize, int dist)
    {
        int yoffset = dist / xsize;
        int xoffset = dist - yoffset * xsize;
        if (xoffset <= 8 && yoffset < 8) return PlaneToCodeLut[yoffset * 16 + 8 - xoffset] + 1;
        if (xoffset > xsize - 8 && yoffset < 7) return PlaneToCodeLut[(yoffset + 1) * 16 + 8 + (xsize - xoffset)] + 1;
        return dist + 120;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindMatchLength(uint[] argb, int a1, int a2, int bestLenMatch, int maxLimit)
    {
        if (argb[a1 + bestLenMatch] != argb[a2 + bestLenMatch]) return 0;
        return Vp8lCommon.VectorMismatch(argb, a1, argb, a2, maxLimit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetPixPairHash64(uint a0, uint a1)
    {
        uint key = a1 * 0xc6a4a793u;
        key += a0 * 0x5bd1e996u;
        return key >> (32 - HashBits);
    }

    private static int GetMaxItersForQuality(int quality) => 8 + (quality * quality) / 128;

    private static int GetWindowSizeForHashChain(int quality, int xsize)
    {
        int maxWindowSize = quality > 75 ? WindowSize : quality > 50 ? (xsize << 8) : quality > 25 ? (xsize << 6) : (xsize << 4);
        return maxWindowSize > WindowSize ? WindowSize : maxWindowSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MaxFindCopyLength(int len) => len < MaxLength ? len : MaxLength;

    /// <summary>VP8LHashChainFill.</summary>
    public static void HashChainFill(Vp8lHashChain p, int quality, uint[] argb, int xsize, int ysize, bool lowEffort)
    {
        int size = xsize * ysize;
        int iterMax = GetMaxItersForQuality(quality);
        uint windowSize = (uint)GetWindowSizeForHashChain(quality, xsize);
        int pos;
        var chain = p.OffsetLength;   // used as an int32 chain first (bit-cast)

        if (size <= 2)
        {
            p.OffsetLength[0] = p.OffsetLength[size - 1] = 0;
            return;
        }

        var hashToFirstIndex = new int[HashSize];
        Array.Fill(hashToFirstIndex, -1);
        bool argbComp = argb[0] == argb[1];
        for (pos = 0; pos < size - 2;)
        {
            uint hashCode;
            bool argbCompNext = argb[pos + 1] == argb[pos + 2];
            if (argbComp && argbCompNext)
            {
                uint tmp0 = argb[pos];
                uint len = 1;
                while (pos + (int)len + 2 < size && argb[pos + (int)len + 2] == argb[pos]) ++len;
                if (len > MaxLength)
                {
                    for (int k = 0; k < len - MaxLength; ++k) chain[pos + k] = 0xffffffffu;
                    pos += (int)len - MaxLength;
                    len = MaxLength;
                }
                while (len != 0)
                {
                    uint tmp1 = len--;
                    hashCode = GetPixPairHash64(tmp0, tmp1);
                    chain[pos] = (uint)hashToFirstIndex[hashCode];
                    hashToFirstIndex[hashCode] = pos++;
                }
                argbComp = false;
            }
            else
            {
                hashCode = GetPixPairHash64(argb[pos], argb[pos + 1]);
                chain[pos] = (uint)hashToFirstIndex[hashCode];
                hashToFirstIndex[hashCode] = pos++;
                argbComp = argbCompNext;
            }
        }
        chain[pos] = (uint)hashToFirstIndex[GetPixPairHash64(argb[pos], argb[pos + 1])];

        p.OffsetLength[0] = p.OffsetLength[size - 1] = 0;
        for (uint basePosition = (uint)size - 2; basePosition > 0;)
        {
            int maxLen = MaxFindCopyLength(size - 1 - (int)basePosition);
            int argbStart = (int)basePosition;
            int iter = iterMax;
            int bestLength = 0;
            uint bestDistance = 0;
            uint bestArgb;
            int minPos = basePosition > windowSize ? (int)(basePosition - windowSize) : 0;
            int lengthMax = maxLen < 256 ? maxLen : 256;
            uint maxBasePosition;

            pos = (int)chain[basePosition];
            if (!lowEffort)
            {
                int currLength;
                if (basePosition >= (uint)xsize)
                {
                    currLength = FindMatchLength(argb, argbStart - xsize, argbStart, bestLength, maxLen);
                    if (currLength > bestLength)
                    {
                        bestLength = currLength;
                        bestDistance = (uint)xsize;
                    }
                    --iter;
                }
                currLength = FindMatchLength(argb, argbStart - 1, argbStart, bestLength, maxLen);
                if (currLength > bestLength)
                {
                    bestLength = currLength;
                    bestDistance = 1;
                }
                --iter;
                if (bestLength == MaxLength) pos = minPos - 1;
            }
            bestArgb = argb[argbStart + bestLength];

            for (; pos >= minPos && --iter != 0; pos = (int)chain[pos])
            {
                if (argb[pos + bestLength] != bestArgb) continue;
                int currLength = Vp8lCommon.VectorMismatch(argb, pos, argb, argbStart, maxLen);
                if (bestLength < currLength)
                {
                    bestLength = currLength;
                    bestDistance = basePosition - (uint)pos;
                    bestArgb = argb[argbStart + bestLength];
                    if (bestLength >= lengthMax) break;
                }
            }
            maxBasePosition = basePosition;
            while (true)
            {
                p.OffsetLength[basePosition] = (bestDistance << MaxLengthBits) | (uint)bestLength;
                --basePosition;
                if (bestDistance == 0 || basePosition == 0) break;
                if (basePosition < bestDistance || argb[basePosition - bestDistance] != argb[basePosition]) break;
                if (bestLength == MaxLength && bestDistance != 1 && basePosition + MaxLength < maxBasePosition) break;
                if (bestLength < MaxLength)
                {
                    ++bestLength;
                    maxBasePosition = basePosition;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddSingleLiteral(uint pixel, Vp8lColorCache? hashers, Vp8lBackwardRefs refs)
    {
        PixOrCopy v;
        if (hashers != null)
        {
            uint key = (uint)hashers.GetIndex(pixel);
            if (hashers.Lookup(key) == pixel)
            {
                v = PixOrCopy.CreateCacheIdx((int)key);
            }
            else
            {
                v = PixOrCopy.CreateLiteral(pixel);
                hashers.Set(key, pixel);
            }
        }
        else
        {
            v = PixOrCopy.CreateLiteral(pixel);
        }
        refs.Add(v);
    }

    private static void BackwardReferencesRle(int xsize, int ysize, uint[] argb, int cacheBits, Vp8lBackwardRefs refs)
    {
        int pixCount = xsize * ysize;
        var hashers = cacheBits > 0 ? new Vp8lColorCache(cacheBits) : null;
        refs.Clear();
        AddSingleLiteral(argb[0], hashers, refs);
        int i = 1;
        while (i < pixCount)
        {
            int maxLen = MaxFindCopyLength(pixCount - i);
            int rleLen = FindMatchLength(argb, i, i - 1, 0, maxLen);
            int prevRowLen = i < xsize ? 0 : FindMatchLength(argb, i, i - xsize, 0, maxLen);
            if (rleLen >= prevRowLen && rleLen >= MinLength)
            {
                refs.Add(PixOrCopy.CreateCopy(1, rleLen));
                i += rleLen;
            }
            else if (prevRowLen >= MinLength)
            {
                refs.Add(PixOrCopy.CreateCopy((uint)xsize, prevRowLen));
                if (hashers != null)
                    for (int k = 0; k < prevRowLen; ++k) hashers.Insert(argb[i + k]);
                i += prevRowLen;
            }
            else
            {
                AddSingleLiteral(argb[i], hashers, refs);
                i++;
            }
        }
    }

    private static void BackwardReferencesLz77(int xsize, int ysize, uint[] argb, int cacheBits, Vp8lHashChain hashChain,
        Vp8lBackwardRefs refs)
    {
        int iLastCheck = -1;
        int pixCount = xsize * ysize;
        var hashers = cacheBits > 0 ? new Vp8lColorCache(cacheBits) : null;
        refs.Clear();
        for (int i = 0; i < pixCount;)
        {
            int offset = hashChain.FindOffset(i);
            int len = hashChain.FindLength(i);
            if (len >= MinLength)
            {
                int lenIni = len;
                int maxReach = 0;
                int jMax = i + lenIni >= pixCount ? pixCount - 1 : i + lenIni;
                iLastCheck = i > iLastCheck ? i : iLastCheck;
                for (int j = iLastCheck + 1; j <= jMax; ++j)
                {
                    int lenJ = hashChain.FindLength(j);
                    int reach = j + (lenJ >= MinLength ? lenJ : 1);
                    if (reach > maxReach)
                    {
                        len = j - i;
                        maxReach = reach;
                        if (maxReach >= pixCount) break;
                    }
                }
            }
            else
            {
                len = 1;
            }
            if (len == 1)
            {
                AddSingleLiteral(argb[i], hashers, refs);
            }
            else
            {
                refs.Add(PixOrCopy.CreateCopy((uint)offset, len));
                if (hashers != null)
                    for (int j = i; j < i + len; ++j) hashers.Insert(argb[j]);
            }
            i += len;
        }
    }

    private const int WindowOffsetsSizeMax = 32;

    private static void BackwardReferencesLz77Box(int xsize, int ysize, uint[] argb, int cacheBits,
        Vp8lHashChain hashChainBest, Vp8lHashChain hashChain, Vp8lBackwardRefs refs)
    {
        int pixCount = xsize * ysize;
        Span<int> windowOffsets = stackalloc int[WindowOffsetsSizeMax];
        Span<int> windowOffsetsNew = stackalloc int[WindowOffsetsSizeMax];
        windowOffsets.Clear();
        windowOffsetsNew.Clear();
        int windowOffsetsSize = 0;
        int windowOffsetsNewSize = 0;
        var countsIni = new ushort[pixCount + 1];   // +1: counts[1] at i = pix_count - 2 (as in C) stays in range
        int bestOffsetPrev = -1, bestLengthPrev = -1;

        int i = pixCount - 2;
        int c = i;   // counts = counts_ini + i
        countsIni[c + 1] = 1;
        for (; i >= 0; --i, --c)
        {
            if (argb[i] == argb[i + 1])
                countsIni[c] = (ushort)(countsIni[c + 1] + (countsIni[c + 1] != MaxLength ? 1 : 0));
            else
                countsIni[c] = 1;
        }

        for (int y = 0; y <= 6; ++y)
        {
            for (int x = -6; x <= 6; ++x)
            {
                int offset = y * xsize + x;
                if (offset <= 0) continue;
                int planeCode = DistanceToPlaneCode(xsize, offset) - 1;
                if (planeCode >= WindowOffsetsSizeMax) continue;
                windowOffsets[planeCode] = offset;
            }
        }
        for (i = 0; i < WindowOffsetsSizeMax; ++i)
        {
            if (windowOffsets[i] == 0) continue;
            windowOffsets[windowOffsetsSize++] = windowOffsets[i];
        }
        for (i = 0; i < windowOffsetsSize; ++i)
        {
            bool isReachable = false;
            for (int j = 0; j < windowOffsetsSize && !isReachable; ++j)
                isReachable |= windowOffsets[i] == windowOffsets[j] + 1;
            if (!isReachable) windowOffsetsNew[windowOffsetsNewSize++] = windowOffsets[i];
        }

        hashChain.OffsetLength[0] = 0;
        for (i = 1; i < pixCount; ++i)
        {
            int bestLength = hashChainBest.FindLength(i);
            int bestOffset = 0;
            bool doCompute = true;

            if (bestLength >= MaxLength)
            {
                bestOffset = hashChainBest.FindOffset(i);
                for (int ind = 0; ind < windowOffsetsSize; ++ind)
                {
                    if (bestOffset == windowOffsets[ind])
                    {
                        doCompute = false;
                        break;
                    }
                }
            }
            if (doCompute)
            {
                bool usePrev = bestLengthPrev > 1 && bestLengthPrev < MaxLength;
                int numInd = usePrev ? windowOffsetsNewSize : windowOffsetsSize;
                bestLength = usePrev ? bestLengthPrev - 1 : 0;
                bestOffset = usePrev ? bestOffsetPrev : 0;
                for (int ind = 0; ind < numInd; ++ind)
                {
                    int currLength = 0;
                    int j = i;
                    int jOffset = usePrev ? i - windowOffsetsNew[ind] : i - windowOffsets[ind];
                    if (jOffset < 0 || argb[jOffset] != argb[i]) continue;
                    do
                    {
                        int countsJOffset = countsIni[jOffset];
                        int countsJ = countsIni[j];
                        if (countsJOffset != countsJ)
                        {
                            currLength += countsJOffset < countsJ ? countsJOffset : countsJ;
                            break;
                        }
                        currLength += countsJOffset;
                        jOffset += countsJOffset;
                        j += countsJOffset;
                    } while (currLength <= MaxLength && j < pixCount && argb[jOffset] == argb[j]);
                    if (bestLength < currLength)
                    {
                        bestOffset = usePrev ? windowOffsetsNew[ind] : windowOffsets[ind];
                        if (currLength >= MaxLength)
                        {
                            bestLength = MaxLength;
                            break;
                        }
                        bestLength = currLength;
                    }
                }
            }

            if (bestLength <= MinLength)
            {
                hashChain.OffsetLength[i] = 0;
                bestOffsetPrev = 0;
                bestLengthPrev = 0;
            }
            else
            {
                hashChain.OffsetLength[i] = ((uint)bestOffset << MaxLengthBits) | (uint)bestLength;
                bestOffsetPrev = bestOffset;
                bestLengthPrev = bestLength;
            }
        }
        hashChain.OffsetLength[0] = 0;

        BackwardReferencesLz77(xsize, ysize, argb, cacheBits, hashChain, refs);
    }

    private static void BackwardReferences2DLocality(int xsize, Vp8lBackwardRefs refs)
    {
        var items = refs.Items;
        for (int k = 0; k < refs.Count; ++k)
        {
            if (items[k].IsCopy)
                items[k].ArgbOrDistance = (uint)DistanceToPlaneCode(xsize, (int)items[k].ArgbOrDistance);
        }
    }

    private static int CalculateBestCacheSize(uint[] argb, int quality, Vp8lBackwardRefs refs, int bestCacheBitsIn)
    {
        int cacheBitsMax = quality <= 25 ? 0 : bestCacheBitsIn;
        ulong entropyMin = Vp8lCommon.UInt64Max;
        int bestCacheBits = 0;
        if (cacheBitsMax == 0) return 0;

        var histos = new Vp8lHistogram[cacheBitsMax + 1];
        var hashers = new Vp8lColorCache?[cacheBitsMax + 1];
        for (int i = 0; i <= cacheBitsMax; ++i)
        {
            histos[i] = new Vp8lHistogram(i);
            histos[i].Init(i, true);
            if (i == 0) continue;
            hashers[i] = new Vp8lColorCache(i);
        }

        int a = 0;
        var items = refs.Items;
        for (int r = 0; r < refs.Count; ++r)
        {
            ref readonly PixOrCopy v = ref items[r];
            if (v.IsLiteral)
            {
                uint pix = argb[a++];
                uint av = (pix >> 24) & 0xff, rv = (pix >> 16) & 0xff, gv = (pix >> 8) & 0xff, bv = pix & 0xff;
                int key = Vp8lCommon.HashPix(pix, 32 - cacheBitsMax);
                ++histos[0].Blue[bv];
                ++histos[0].Literal[gv];
                ++histos[0].Red[rv];
                ++histos[0].Alpha[av];
                for (int i = cacheBitsMax; i >= 1; --i, key >>= 1)
                {
                    if (hashers[i]!.Lookup((uint)key) == pix)
                    {
                        ++histos[i].Literal[Vp8lCommon.NumLiteralCodes + Vp8lCommon.NumLengthCodes + key];
                    }
                    else
                    {
                        hashers[i]!.Set((uint)key, pix);
                        ++histos[i].Blue[bv];
                        ++histos[i].Literal[gv];
                        ++histos[i].Red[rv];
                        ++histos[i].Alpha[av];
                    }
                }
            }
            else
            {
                int len = v.Len;
                uint argbPrev = argb[a] ^ 0xffffffffu;
                Vp8lCommon.PrefixEncode(len, out int code, out _, out _);
                for (int i = 0; i <= cacheBitsMax; ++i) ++histos[i].Literal[Vp8lCommon.NumLiteralCodes + code];
                do
                {
                    if (argb[a] != argbPrev)
                    {
                        int key = Vp8lCommon.HashPix(argb[a], 32 - cacheBitsMax);
                        for (int i = cacheBitsMax; i >= 1; --i, key >>= 1) hashers[i]!.Colors[key] = argb[a];
                        argbPrev = argb[a];
                    }
                    a++;
                } while (--len != 0);
            }
        }

        for (int i = 0; i <= cacheBitsMax; ++i)
        {
            ulong entropy = Vp8lHistogramUtil.EstimateBits(histos[i]);
            if (i == 0 || entropy < entropyMin)
            {
                entropyMin = entropy;
                bestCacheBits = i;
            }
        }
        return bestCacheBits;
    }

    private static void BackwardRefsWithLocalCache(uint[] argb, int cacheBits, Vp8lBackwardRefs refs)
    {
        int pixelIndex = 0;
        var hashers = new Vp8lColorCache(cacheBits);
        var items = refs.Items;
        for (int r = 0; r < refs.Count; ++r)
        {
            ref PixOrCopy v = ref items[r];
            if (v.IsLiteral)
            {
                uint argbLiteral = v.ArgbOrDistance;
                int ix = hashers.Contains(argbLiteral);
                if (ix >= 0) v = PixOrCopy.CreateCacheIdx(ix);
                else hashers.Insert(argbLiteral);
                ++pixelIndex;
            }
            else
            {
                for (int k = 0; k < v.Len; ++k) hashers.Insert(argb[pixelIndex++]);
            }
        }
    }

    private static void GetBackwardReferencesInternal(int width, int height, uint[] argb, int quality, int lz77TypesToTry,
        int cacheBitsMax, bool doNoCache, Vp8lHashChain hashChain, Vp8lBackwardRefs[] refs, out int cacheBitsBest)
    {
        cacheBitsBest = 0;
        Span<int> lz77TypesBest = stackalloc int[2];
        Span<ulong> bitCostsBest = stackalloc ulong[2];
        lz77TypesBest.Clear();
        bitCostsBest[0] = bitCostsBest[1] = Vp8lCommon.UInt64Max;
        Vp8lHashChain? hashChainBox = null;
        var refsTmp = refs[doNoCache ? 2 : 1];
        var histo = new Vp8lHistogram(Vp8lCommon.MaxColorCacheBits);

        for (int lz77Type = 1; lz77TypesToTry != 0; lz77TypesToTry &= ~lz77Type, lz77Type <<= 1)
        {
            ulong bitCost = 0;
            if ((lz77TypesToTry & lz77Type) == 0) continue;
            switch (lz77Type)
            {
                case LZ77Rle:
                    BackwardReferencesRle(width, height, argb, 0, refsTmp);
                    break;
                case LZ77Standard:
                    BackwardReferencesLz77(width, height, argb, 0, hashChain, refsTmp);
                    break;
                case LZ77Box:
                    hashChainBox = new Vp8lHashChain(width * height);
                    BackwardReferencesLz77Box(width, height, argb, 0, hashChain, hashChainBox, refsTmp);
                    break;
            }

            for (int i = 1; i >= 0; --i)
            {
                int cacheBits = i == 1 ? 0 : cacheBitsMax;
                if (i == 1 && !doNoCache) continue;
                if (i == 0)
                {
                    cacheBits = CalculateBestCacheSize(argb, quality, refsTmp, cacheBits);
                    if (cacheBits > 0) BackwardRefsWithLocalCache(argb, cacheBits, refsTmp);
                }
                if (i == 0 && doNoCache && cacheBits == 0)
                {
                    // bit_cost from i == 1 is reused
                }
                else
                {
                    Vp8lHistogramUtil.Create(histo, refsTmp, cacheBits);
                    bitCost = Vp8lHistogramUtil.EstimateBits(histo);
                }
                if (bitCost < bitCostsBest[i])
                {
                    if (i == 1) Vp8lBackwardRefs.CloneTo(refsTmp, refs[1]);
                    else Vp8lBackwardRefs.Swap(refsTmp, refs[0]);
                    bitCostsBest[i] = bitCost;
                    lz77TypesBest[i] = lz77Type;
                    if (i == 0) cacheBitsBest = cacheBits;
                }
            }
        }

        for (int i = 1; i >= 0; --i)
        {
            if (i == 1 && !doNoCache) continue;
            if ((lz77TypesBest[i] == LZ77Standard || lz77TypesBest[i] == LZ77Box) && quality >= 25)
            {
                var hashChainTmp = lz77TypesBest[i] == LZ77Standard ? hashChain : hashChainBox!;
                int cacheBits = i == 1 ? 0 : cacheBitsBest;
                Vp8lBackwardRefsCost.TraceBackwards(width, height, argb, cacheBits, hashChainTmp, refs[i], refsTmp);
                Vp8lHistogramUtil.Create(histo, refsTmp, cacheBits);
                ulong bitCostTrace = Vp8lHistogramUtil.EstimateBits(histo);
                if (bitCostTrace < bitCostsBest[i]) Vp8lBackwardRefs.Swap(refsTmp, refs[i]);
            }

            BackwardReferences2DLocality(width, refs[i]);

            if (i == 1 && lz77TypesBest[0] == lz77TypesBest[1] && cacheBitsBest == 0)
            {
                Vp8lBackwardRefs.CloneTo(refs[1], refs[0]);
                break;
            }
        }
    }

    /// <summary>VP8LGetBackwardReferences. refs: [best, best-no-cache (if doNoCache), temporary].</summary>
    public static void GetBackwardReferences(int width, int height, uint[] argb, int quality, bool lowEffort,
        int lz77TypesToTry, int cacheBitsMax, bool doNoCache, Vp8lHashChain hashChain, Vp8lBackwardRefs[] refs,
        out int cacheBitsBest)
    {
        if (lowEffort)
        {
            BackwardReferencesLz77(width, height, argb, 0, hashChain, refs[0]);
            BackwardReferences2DLocality(width, refs[0]);
            cacheBitsBest = 0;
        }
        else
        {
            GetBackwardReferencesInternal(width, height, argb, quality, lz77TypesToTry, cacheBitsMax, doNoCache, hashChain,
                refs, out cacheBitsBest);
        }
    }
}
