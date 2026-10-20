using System.Runtime.CompilerServices;

namespace SharpImage.Formats.WebP;

/// <summary>
/// The WebP lossless (VP8L) decoder and the ALPH alpha-plane decoder, following libwebp 1.6.0 src/dec/vp8l_dec.c,
/// alpha_dec.c, utils/huffman_utils.c and dsp/lossless.c / filters.c: the same Huffman table construction (and so the
/// same accepted / rejected codes), the same meta-code, color-cache, LZ77 and transform semantics, the same alpha
/// unfiltering. Whole-image (non-incremental) decoding; output is ARGB (0xAARRGGBB).
/// </summary>
internal sealed class Vp8lDecoder
{
    private const int HuffmanTableBits = 8;
    private const int HuffmanTableMask = (1 << HuffmanTableBits) - 1;
    private const int LengthsTableBits = 7;
    private const int LengthsTableMask = (1 << LengthsTableBits) - 1;
    private const int NumCodeLengthCodes = 19;
    private const int DefaultCodeLength = 8;
    private const int MaxCacheBits = 11;
    private const int CodeToPlaneCodes = 120;
    private const int Green = 0, Red = 1, Blue = 2, Alpha = 3, Dist = 4;
    private static readonly int[] AlphabetSize = [256 + 24, 256, 256, 256, 40];
    private static readonly byte[] LiteralMap = [0, 1, 1, 1, 0];
    private static readonly byte[] CodeLengthCodeOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private static readonly byte[] CodeLengthExtraBits = [2, 3, 7];
    private static readonly byte[] CodeLengthRepeatOffsets = [3, 3, 11];

    private static readonly byte[] CodeToPlane =
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a,
        0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b,
        0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c,
        0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b,
        0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b,
        0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f,
        0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    // ---- bit reader (LSB first; eos = more bits consumed than the data holds) ------------------------------------------

    private readonly byte[] _data;
    private readonly long _end;      // in bits
    private long _bitPos;            // in bits, from the stream start
    private bool _eos;

    private Vp8lDecoder(byte[] data, int offset, int length)
    {
        _data = data;
        _bitPos = (long)offset * 8;
        _end = (long)(offset + length) * 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint PeekBits(int n)
    {
        // up to 24 bits at the current position (zeros past the end)
        long bytePos = _bitPos >> 3;
        int shift = (int)(_bitPos & 7);
        ulong v = 0;
        long endByte = _end >> 3;
        for (int i = 0; i < 4; ++i)
        {
            long p = bytePos + i;
            if (p < endByte) v |= (ulong)_data[p] << (8 * i);
        }
        return (uint)(v >> shift) & (uint)((1L << n) - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Skip(int n)
    {
        _bitPos += n;
        if (_bitPos > _end) _eos = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadBits(int n)
    {
        if (n == 0) return 0;
        uint v = PeekBits(n);
        Skip(n);
        return v;
    }

    // ---- Huffman tables (huffman_utils.c) ---------------------------------------------------------------------------

    private struct HuffmanCode
    {
        public byte Bits;
        public ushort Value;
    }

    private static uint GetNextKey(uint key, int len)
    {
        uint step = 1u << (len - 1);
        while ((key & step) != 0) step >>= 1;
        return step != 0 ? (key & (step - 1)) + step : key;
    }

    private static void ReplicateValue(HuffmanCode[] table, int t, int step, int end, HuffmanCode code)
    {
        do
        {
            end -= step;
            table[t + end] = code;
        } while (end > 0);
    }

    private static int NextTableBitSize(int[] count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < Vp8lCommon.MaxAllowedCodeLength)
        {
            left -= count[len];
            if (left <= 0) break;
            ++len;
            left <<= 1;
        }
        return len - rootBits;
    }

    /// <summary>BuildHuffmanTable: fills rootTable (when non-null) and returns its total size, or 0 for an invalid code.</summary>
    private static int BuildHuffmanTable(HuffmanCode[]? rootTable, int rootBits, int[] codeLengths, int codeLengthsSize)
    {
        int table = 0;
        int totalSize = 1 << rootBits;
        var count = new int[Vp8lCommon.MaxAllowedCodeLength + 1];
        var offset = new int[Vp8lCommon.MaxAllowedCodeLength + 1];
        int len, symbol;

        for (symbol = 0; symbol < codeLengthsSize; ++symbol)
        {
            if (codeLengths[symbol] > Vp8lCommon.MaxAllowedCodeLength) return 0;
            ++count[codeLengths[symbol]];
        }
        if (count[0] == codeLengthsSize) return 0;

        offset[1] = 0;
        for (len = 1; len < Vp8lCommon.MaxAllowedCodeLength; ++len)
        {
            if (count[len] > (1 << len)) return 0;
            offset[len + 1] = offset[len] + count[len];
        }

        var sorted = new ushort[codeLengthsSize];
        for (symbol = 0; symbol < codeLengthsSize; ++symbol)
        {
            int symbolCodeLength = codeLengths[symbol];
            if (symbolCodeLength > 0)
            {
                if (offset[symbolCodeLength] >= codeLengthsSize) return 0;
                sorted[offset[symbolCodeLength]++] = (ushort)symbol;
            }
        }

        if (offset[Vp8lCommon.MaxAllowedCodeLength] == 1)
        {
            if (rootTable != null) ReplicateValue(rootTable, table, 1, totalSize, new HuffmanCode { Bits = 0, Value = sorted[0] });
            return totalSize;
        }

        int step;
        uint low = 0xffffffffu;
        uint mask = (uint)totalSize - 1;
        uint key = 0;
        int numNodes = 1;
        int numOpen = 1;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        symbol = 0;
        for (len = 1, step = 2; len <= rootBits; ++len, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0) return 0;
            if (rootTable == null) continue;
            for (; count[len] > 0; --count[len])
            {
                var code = new HuffmanCode { Bits = (byte)len, Value = sorted[symbol++] };
                ReplicateValue(rootTable, table + (int)key, step, tableSize, code);
                key = GetNextKey(key, len);
            }
        }

        for (len = rootBits + 1, step = 2; len <= Vp8lCommon.MaxAllowedCodeLength; ++len, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0) return 0;
            for (; count[len] > 0; --count[len])
            {
                if ((key & mask) != low)
                {
                    if (rootTable != null) table += tableSize;
                    tableBits = NextTableBitSize(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = key & mask;
                    if (rootTable != null)
                    {
                        rootTable[low].Bits = (byte)(tableBits + rootBits);
                        rootTable[low].Value = (ushort)(table - low);
                    }
                }
                if (rootTable != null)
                {
                    var code = new HuffmanCode { Bits = (byte)(len - rootBits), Value = sorted[symbol++] };
                    ReplicateValue(rootTable, table + (int)(key >> rootBits), step, tableSize, code);
                }
                key = GetNextKey(key, len);
            }
        }
        if (numNodes != 2 * offset[Vp8lCommon.MaxAllowedCodeLength] - 1) return 0;
        return totalSize;
    }

    private static HuffmanCode[]? BuildTable(int rootBits, int[] codeLengths, int size)
    {
        int total = BuildHuffmanTable(null, rootBits, codeLengths, size);
        if (total == 0) return null;
        var table = new HuffmanCode[total];
        if (BuildHuffmanTable(table, rootBits, codeLengths, size) == 0) return null;
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadSymbol(HuffmanCode[] table)
    {
        uint val = PeekBits(15);
        int t = (int)(val & HuffmanTableMask);
        int nbits = table[t].Bits - HuffmanTableBits;
        if (nbits > 0)
        {
            Skip(HuffmanTableBits);
            val = PeekBits(15);
            t += table[t].Value;
            t += (int)(val & ((1u << nbits) - 1));
        }
        Skip(table[t].Bits);
        return table[t].Value;
    }

    private sealed class HTreeGroup
    {
        public readonly HuffmanCode[][] HTrees = new HuffmanCode[5][];
        public bool IsTrivialLiteral;
        public uint LiteralArb;
        public bool IsTrivialCode;
    }

    private bool ReadHuffmanCodeLengths(int[] codeLengthCodeLengths, int numSymbols, int[] codeLengths)
    {
        int prevCodeLen = DefaultCodeLength;
        var table = BuildTable(LengthsTableBits, codeLengthCodeLengths, NumCodeLengthCodes);
        if (table == null) return false;
        int maxSymbol;
        if (ReadBits(1) != 0)
        {
            int lengthNbits = 2 + 2 * (int)ReadBits(3);
            maxSymbol = 2 + (int)ReadBits(lengthNbits);
            if (maxSymbol > numSymbols) return false;
        }
        else
        {
            maxSymbol = numSymbols;
        }
        int symbol = 0;
        while (symbol < numSymbols)
        {
            if (maxSymbol-- == 0) break;
            var p = table[PeekBits(LengthsTableBits) & LengthsTableMask];
            Skip(p.Bits);
            int codeLen = p.Value;
            if (codeLen < 16)
            {
                codeLengths[symbol++] = codeLen;
                if (codeLen != 0) prevCodeLen = codeLen;
            }
            else
            {
                bool usePrev = codeLen == 16;
                int slot = codeLen - 16;
                int repeat = (int)ReadBits(CodeLengthExtraBits[slot]) + CodeLengthRepeatOffsets[slot];
                if (symbol + repeat > numSymbols) return false;
                int length = usePrev ? prevCodeLen : 0;
                while (repeat-- > 0) codeLengths[symbol++] = length;
            }
        }
        return true;
    }

    private HuffmanCode[]? ReadHuffmanCode(int alphabetSize, int[] codeLengths, out int maxCodeLength)
    {
        maxCodeLength = 0;
        bool simpleCode = ReadBits(1) != 0;
        Array.Clear(codeLengths, 0, alphabetSize);
        bool ok;
        if (simpleCode)
        {
            int numSymbols = (int)ReadBits(1) + 1;
            int firstSymbolLenCode = (int)ReadBits(1);
            int symbol = (int)ReadBits(firstSymbolLenCode == 0 ? 1 : 8);
            codeLengths[symbol] = 1;
            if (numSymbols == 2)
            {
                symbol = (int)ReadBits(8);
                codeLengths[symbol] = 1;
            }
            ok = true;
        }
        else
        {
            var codeLengthCodeLengths = new int[NumCodeLengthCodes];
            int numCodes = (int)ReadBits(4) + 4;
            for (int i = 0; i < numCodes; ++i) codeLengthCodeLengths[CodeLengthCodeOrder[i]] = (int)ReadBits(3);
            ok = ReadHuffmanCodeLengths(codeLengthCodeLengths, alphabetSize, codeLengths);
        }
        ok = ok && !_eos;
        if (!ok) return null;
        for (int k = 0; k < alphabetSize; ++k) if (codeLengths[k] > maxCodeLength) maxCodeLength = codeLengths[k];
        return BuildTable(HuffmanTableBits, codeLengths, alphabetSize);
    }

    // ---- metadata / image streams ----------------------------------------------------------------------------------

    private uint[]? _huffmanImage;
    private int _huffmanSubsampleBits;
    private int _huffmanXsize;
    private int _huffmanMask;
    private HTreeGroup[] _htreeGroups = [];
    private Vp8lColorCache? _colorCache;

    private bool ReadHuffmanCodes(int xsize, int ysize, int colorCacheBits, bool allowRecursion)
    {
        uint[]? huffmanImage = null;
        int numHtreeGroups = 1;
        int numHtreeGroupsMax = 1;
        int[]? mapping = null;
        int huffmanPrecision = 0;

        if (allowRecursion && ReadBits(1) != 0)
        {
            huffmanPrecision = Vp8lCommon.MinHuffmanBits + (int)ReadBits(Vp8lCommon.NumHuffmanBits);
            int huffmanXsize = Vp8lCommon.SubSampleSize(xsize, huffmanPrecision);
            int huffmanYsize = Vp8lCommon.SubSampleSize(ysize, huffmanPrecision);
            int huffmanPixs = huffmanXsize * huffmanYsize;
            huffmanImage = DecodeSubImage(huffmanXsize, huffmanYsize);
            if (huffmanImage == null) return false;
            for (int i = 0; i < huffmanPixs; ++i)
            {
                int group = (int)((huffmanImage[i] >> 8) & 0xffff);
                huffmanImage[i] = (uint)group;
                if (group >= numHtreeGroupsMax) numHtreeGroupsMax = group + 1;
            }
            if (numHtreeGroupsMax > 1000 || numHtreeGroupsMax > xsize * ysize)
            {
                mapping = new int[numHtreeGroupsMax];
                Array.Fill(mapping, -1);
                numHtreeGroups = 0;
                for (int i = 0; i < huffmanPixs; ++i)
                {
                    ref int mappedGroup = ref mapping[huffmanImage[i]];
                    if (mappedGroup == -1) mappedGroup = numHtreeGroups++;
                    huffmanImage[i] = (uint)mappedGroup;
                }
            }
            else
            {
                numHtreeGroups = numHtreeGroupsMax;
            }
        }
        if (_eos) return false;

        int maxAlphabetSize = AlphabetSize[0] + (colorCacheBits > 0 ? 1 << colorCacheBits : 0);
        var codeLengths = new int[maxAlphabetSize];
        var groups = new HTreeGroup[numHtreeGroups];
        for (int i = 0; i < numHtreeGroupsMax; ++i)
        {
            if (mapping != null && mapping[i] == -1)
            {
                for (int j = 0; j < 5; ++j)
                {
                    int alphabetSize = AlphabetSize[j];
                    if (j == 0 && colorCacheBits > 0) alphabetSize += 1 << colorCacheBits;
                    if (ReadHuffmanCode(alphabetSize, codeLengths, out _) == null) return false;
                }
            }
            else
            {
                var group = new HTreeGroup();
                groups[mapping == null ? i : mapping[i]] = group;
                int totalSize = 0;
                bool isTrivialLiteral = true;
                for (int j = 0; j < 5; ++j)
                {
                    int alphabetSize = AlphabetSize[j];
                    if (j == 0 && colorCacheBits > 0) alphabetSize += 1 << colorCacheBits;
                    var table = ReadHuffmanCode(alphabetSize, codeLengths, out _);
                    if (table == null) return false;
                    group.HTrees[j] = table;
                    if (isTrivialLiteral && LiteralMap[j] == 1) isTrivialLiteral = table[0].Bits == 0;
                    totalSize += table[0].Bits;
                }
                group.IsTrivialLiteral = isTrivialLiteral;
                group.IsTrivialCode = false;
                if (isTrivialLiteral)
                {
                    uint red = group.HTrees[Red][0].Value;
                    uint blue = group.HTrees[Blue][0].Value;
                    uint alpha = group.HTrees[Alpha][0].Value;
                    group.LiteralArb = (alpha << 24) | (red << 16) | blue;
                    if (totalSize == 0 && group.HTrees[Green][0].Value < Vp8lCommon.NumLiteralCodes)
                    {
                        group.IsTrivialCode = true;
                        group.LiteralArb |= (uint)group.HTrees[Green][0].Value << 8;
                    }
                }
            }
        }
        _huffmanImage = huffmanImage;
        _huffmanSubsampleBits = huffmanPrecision;
        _htreeGroups = groups;
        return true;
    }

    private void UpdateDecoder(int width)
    {
        int numBits = _huffmanSubsampleBits;
        _huffmanXsize = Vp8lCommon.SubSampleSize(width, numBits);
        _huffmanMask = numBits == 0 ? ~0 : (1 << numBits) - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HTreeGroup GetHtreeGroupForPos(int x, int y)
    {
        int metaIndex = _huffmanSubsampleBits == 0 ? 0 : (int)_huffmanImage![_huffmanXsize * (y >> _huffmanSubsampleBits) + (x >> _huffmanSubsampleBits)];
        return _htreeGroups[metaIndex];
    }

    private int GetCopyDistance(int distanceSymbol)
    {
        if (distanceSymbol < 4) return distanceSymbol + 1;
        int extraBits = (distanceSymbol - 2) >> 1;
        int offset = (2 + (distanceSymbol & 1)) << extraBits;
        return offset + (int)ReadBits(extraBits) + 1;
    }

    private static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > CodeToPlaneCodes) return planeCode - CodeToPlaneCodes;
        int distCode = CodeToPlane[planeCode - 1];
        int yoffset = distCode >> 4;
        int xoffset = 8 - (distCode & 0xf);
        int dist = yoffset * xsize + xoffset;
        return dist >= 1 ? dist : 1;
    }

    /// <summary>DecodeImageData (whole image): false on a bitstream error.</summary>
    private bool DecodeImageData(uint[] data, int width, int height)
    {
        int row = 0, col = 0;
        int src = 0;
        int lastCached = 0;
        int srcEnd = width * height;
        const int lenCodeLimit = Vp8lCommon.NumLiteralCodes + Vp8lCommon.NumLengthCodes;
        int colorCacheLimit = lenCodeLimit + (_colorCache != null ? 1 << _colorCache.HashBits : 0);
        int mask = _huffmanMask;
        HTreeGroup? htreeGroup = src < srcEnd ? GetHtreeGroupForPos(col, row) : null;

        while (src < srcEnd)
        {
            if ((col & mask) == 0) htreeGroup = GetHtreeGroupForPos(col, row);
            bool advanceByOne;
            if (htreeGroup!.IsTrivialCode)
            {
                data[src] = htreeGroup.LiteralArb;
                advanceByOne = true;
            }
            else
            {
                int code = ReadSymbol(htreeGroup.HTrees[Green]);
                if (_eos) break;
                if (code < Vp8lCommon.NumLiteralCodes)
                {
                    if (htreeGroup.IsTrivialLiteral)
                    {
                        data[src] = htreeGroup.LiteralArb | ((uint)code << 8);
                    }
                    else
                    {
                        int red = ReadSymbol(htreeGroup.HTrees[Red]);
                        int blue = ReadSymbol(htreeGroup.HTrees[Blue]);
                        int alpha = ReadSymbol(htreeGroup.HTrees[Alpha]);
                        if (_eos) break;
                        data[src] = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;
                    }
                    advanceByOne = true;
                }
                else if (code < lenCodeLimit)
                {
                    int lengthSym = code - Vp8lCommon.NumLiteralCodes;
                    int length = GetCopyDistance(lengthSym);
                    int distSymbol = ReadSymbol(htreeGroup.HTrees[Dist]);
                    int distCode = GetCopyDistance(distSymbol);
                    int dist = PlaneCodeToDistance(width, distCode);
                    if (_eos) break;
                    if (src < dist || srcEnd - src < length) return false;
                    for (int i = 0; i < length; ++i) data[src + i] = data[src + i - dist];
                    src += length;
                    col += length;
                    while (col >= width)
                    {
                        col -= width;
                        ++row;
                    }
                    if ((col & mask) != 0) htreeGroup = GetHtreeGroupForPos(col, row);
                    if (_colorCache != null)
                        while (lastCached < src) _colorCache.Insert(data[lastCached++]);
                    advanceByOne = false;
                }
                else if (code < colorCacheLimit)
                {
                    int key = code - lenCodeLimit;
                    while (lastCached < src) _colorCache!.Insert(data[lastCached++]);
                    data[src] = _colorCache!.Lookup((uint)key);
                    advanceByOne = true;
                }
                else
                {
                    return false;
                }
            }
            if (advanceByOne)
            {
                ++src;
                ++col;
                if (col >= width)
                {
                    col = 0;
                    ++row;
                    if (_colorCache != null)
                        while (lastCached < src) _colorCache.Insert(data[lastCached++]);
                }
            }
        }
        return !_eos;
    }

    /// <summary>DecodeImageStream for a non-level-0 image (transform data / Huffman image): its own color cache and
    /// Huffman codes, which are discarded afterwards (ClearMetadata).</summary>
    private uint[]? DecodeSubImage(int xsize, int ysize)
    {
        var saved = (_huffmanImage, _huffmanSubsampleBits, _huffmanXsize, _huffmanMask, _htreeGroups, _colorCache);
        _huffmanImage = null;
        _huffmanSubsampleBits = 0;
        _colorCache = null;
        uint[]? data = null;
        int colorCacheBits = 0;
        bool ok = true;
        if (ReadBits(1) != 0)
        {
            colorCacheBits = (int)ReadBits(4);
            ok = colorCacheBits >= 1 && colorCacheBits <= MaxCacheBits;
        }
        ok = ok && ReadHuffmanCodes(xsize, ysize, colorCacheBits, false);
        if (ok)
        {
            _colorCache = colorCacheBits > 0 ? new Vp8lColorCache(colorCacheBits) : null;
            UpdateDecoder(xsize);
            data = new uint[xsize * ysize];
            ok = DecodeImageData(data, xsize, ysize) && !_eos;
        }
        (_huffmanImage, _huffmanSubsampleBits, _huffmanXsize, _huffmanMask, _htreeGroups, _colorCache) = saved;
        return ok ? data : null;
    }

    // ---- transforms ------------------------------------------------------------------------------------------------

    private sealed class Transform
    {
        public int Type;
        public int Bits;
        public int Xsize;
        public int Ysize;
        public uint[] Data = [];
    }

    private readonly List<Transform> _transforms = [];
    private int _transformsSeen;

    private bool ReadTransform(ref int xsize, int ysize)
    {
        int type = (int)ReadBits(2);
        if ((_transformsSeen & (1 << type)) != 0) return false;
        _transformsSeen |= 1 << type;
        var transform = new Transform { Type = type, Xsize = xsize, Ysize = ysize };
        _transforms.Add(transform);
        switch (type)
        {
            case Vp8lCommon.PredictorTransform:
            case Vp8lCommon.CrossColorTransform:
            {
                transform.Bits = Vp8lCommon.MinTransformBits + (int)ReadBits(Vp8lCommon.NumTransformBits);
                var d = DecodeSubImage(Vp8lCommon.SubSampleSize(transform.Xsize, transform.Bits),
                    Vp8lCommon.SubSampleSize(transform.Ysize, transform.Bits));
                if (d == null) return false;
                transform.Data = d;
                break;
            }
            case Vp8lCommon.ColorIndexingTransform:
            {
                int numColors = (int)ReadBits(8) + 1;
                int bits = numColors > 16 ? 0 : numColors > 4 ? 1 : numColors > 2 ? 2 : 3;
                xsize = Vp8lCommon.SubSampleSize(transform.Xsize, bits);
                transform.Bits = bits;
                var d = DecodeSubImage(numColors, 1);
                if (d == null) return false;
                // ExpandColorMap: delta-decoded, black tail up to the bundled range
                int finalNumColors = 1 << (8 >> transform.Bits);
                var newColorMap = new uint[finalNumColors];
                newColorMap[0] = d[0];
                for (int i = 1; i < numColors; ++i) newColorMap[i] = Vp8lCommon.AddPixels(d[i], newColorMap[i - 1]);
                transform.Data = newColorMap;
                break;
            }
        }
        return true;
    }

    private static void InverseTransform(Transform transform, uint[] input, uint[] output, int width, int height)
    {
        switch (transform.Type)
        {
            case Vp8lCommon.SubtractGreenTransform:
                for (int i = 0; i < width * height; ++i)
                {
                    uint argb = input[i];
                    uint green = (argb >> 8) & 0xff;
                    uint redBlue = argb & 0x00ff00ffu;
                    redBlue += (green << 16) | green;
                    redBlue &= 0x00ff00ffu;
                    output[i] = (argb & 0xff00ff00u) | redBlue;
                }
                break;
            case Vp8lCommon.PredictorTransform:
                PredictorInverseTransform(transform, input, output, width, height);
                break;
            case Vp8lCommon.CrossColorTransform:
                ColorSpaceInverseTransform(transform, input, output, width, height);
                break;
            case Vp8lCommon.ColorIndexingTransform:
                ColorIndexInverseTransform(transform, input, output, height);
                break;
        }
    }

    private static void PredictorInverseTransform(Transform transform, uint[] input, uint[] output, int width, int height)
    {
        // row 0: first pixel + black, then left prediction
        output[0] = Vp8lCommon.AddPixels(input[0], Vp8lCommon.ArgbBlack);
        for (int x = 1; x < width; ++x) output[x] = Vp8lCommon.AddPixels(input[x], output[x - 1]);
        int tileWidth = 1 << transform.Bits;
        int tilesPerRow = Vp8lCommon.SubSampleSize(width, transform.Bits);
        for (int y = 1; y < height; ++y)
        {
            int cur = y * width;
            int up = cur - width;
            output[cur] = Vp8lCommon.AddPixels(input[cur], output[up]);
            int predRow = (y >> transform.Bits) * tilesPerRow;
            for (int x = 1; x < width;)
            {
                int mode = (int)((transform.Data[predRow + (x >> transform.Bits)] >> 8) & 0xf);
                int xEnd = (x & ~(tileWidth - 1)) + tileWidth;
                if (xEnd > width) xEnd = width;
                for (; x < xEnd; ++x)
                {
                    uint pred = mode switch
                    {
                        0 or 14 or 15 => Vp8lCommon.ArgbBlack,
                        1 => output[cur + x - 1],
                        _ => Vp8lCommon.Predict(mode, output, cur + x - 1, output, up + x),
                    };
                    output[cur + x] = Vp8lCommon.AddPixels(input[cur + x], pred);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorTransformDelta(sbyte colorPred, sbyte color) => (colorPred * color) >> 5;

    private static void ColorSpaceInverseTransform(Transform transform, uint[] input, uint[] output, int width, int height)
    {
        int tilesPerRow = Vp8lCommon.SubSampleSize(width, transform.Bits);
        for (int y = 0; y < height; ++y)
        {
            int predRow = (y >> transform.Bits) * tilesPerRow;
            for (int x = 0; x < width; ++x)
            {
                uint code = transform.Data[predRow + (x >> transform.Bits)];
                sbyte greenToRed = (sbyte)(code & 0xff);
                sbyte greenToBlue = (sbyte)((code >> 8) & 0xff);
                sbyte redToBlue = (sbyte)((code >> 16) & 0xff);
                uint argb = input[y * width + x];
                sbyte green = (sbyte)(argb >> 8);
                uint red = argb >> 16;
                int newRed = (int)(red & 0xff);
                int newBlue = (int)(argb & 0xff);
                newRed += ColorTransformDelta(greenToRed, green);
                newRed &= 0xff;
                newBlue += ColorTransformDelta(greenToBlue, green);
                newBlue += ColorTransformDelta(redToBlue, (sbyte)newRed);
                newBlue &= 0xff;
                output[y * width + x] = (argb & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
            }
        }
    }

    private static void ColorIndexInverseTransform(Transform transform, uint[] input, uint[] output, int height)
    {
        int bitsPerPixel = 8 >> transform.Bits;
        int countMask = (1 << transform.Bits) - 1;
        uint bitMask = (1u << bitsPerPixel) - 1;
        var colorMap = transform.Data;
        int width = transform.Xsize;
        int packedWidth = Vp8lCommon.SubSampleSize(width, transform.Bits);
        // in may alias out only when unpacking is 1:1; use the packed input row by row
        for (int y = height - 1; y >= 0; --y)
        {
            int src = y * packedWidth;
            int dst = y * width;
            if (bitsPerPixel < 8)
            {
                uint packedPixels = 0;
                for (int x = width - 1; x >= 0; --x)
                {
                    // unpack in reverse so in-place works; recompute the source word per pixel
                    packedPixels = (input[src + (x >> transform.Bits)] >> 8) >> (bitsPerPixel * (x & countMask));
                    output[dst + x] = colorMap[packedPixels & bitMask];
                }
            }
            else
            {
                for (int x = width - 1; x >= 0; --x) output[dst + x] = colorMap[(input[src + x] >> 8) & 0xff];
            }
        }
    }

    // ---- entry points ----------------------------------------------------------------------------------------------

    /// <summary>Decodes a VP8L bitstream (starting with the 0x2f signature): ARGB pixels, or throws on a bad stream.</summary>
    public static uint[] Decode(byte[] data, int offset, int length, out int width, out int height, out bool hasAlpha)
    {
        var dec = new Vp8lDecoder(data, offset, length);
        if (length < 5 || dec.ReadBits(8) != Vp8lCommon.Vp8lMagicByte) throw new InvalidDataException("Invalid VP8L signature.");
        width = (int)dec.ReadBits(Vp8lCommon.Vp8lImageSizeBits) + 1;
        height = (int)dec.ReadBits(Vp8lCommon.Vp8lImageSizeBits) + 1;
        hasAlpha = dec.ReadBits(1) != 0;
        if (dec.ReadBits(Vp8lCommon.Vp8lVersionBits) != 0 || dec._eos) throw new InvalidDataException("Invalid VP8L header.");
        return dec.DecodeLevel0(width, height) ?? throw new InvalidDataException("Invalid VP8L bitstream.");
    }

    // DecodeImageStream(is_level0) + DecodeImageData + the inverse transforms over the whole image.
    private uint[]? DecodeLevel0(int width, int height)
    {
        int transformXsize = width;
        while (ReadBits(1) != 0)
        {
            if (_transforms.Count >= 4 || !ReadTransform(ref transformXsize, height)) return null;
        }
        int colorCacheBits = 0;
        if (ReadBits(1) != 0)
        {
            colorCacheBits = (int)ReadBits(4);
            if (colorCacheBits < 1 || colorCacheBits > MaxCacheBits) return null;
        }
        if (!ReadHuffmanCodes(transformXsize, height, colorCacheBits, true)) return null;
        _colorCache = colorCacheBits > 0 ? new Vp8lColorCache(colorCacheBits) : null;
        UpdateDecoder(transformXsize);
        if (_eos) return null;

        var pixels = new uint[Math.Max(transformXsize, width) * height];
        if (!DecodeImageData(pixels, transformXsize, height)) return null;

        // inverse transforms, last read first
        var cur = pixels;
        int curWidth = transformXsize;
        for (int n = _transforms.Count - 1; n >= 0; --n)
        {
            var t = _transforms[n];
            var output = new uint[t.Xsize * height];
            if (t.Type == Vp8lCommon.ColorIndexingTransform)
                ColorIndexInverseTransform(t, cur, output, height);
            else
                InverseTransform(t, cur, output, t.Xsize, height);
            cur = output;
            curWidth = t.Xsize;
        }
        if (curWidth != width) return null;
        if (cur.Length != width * height) Array.Resize(ref cur, width * height);
        return cur;
    }

    /// <summary>Decodes an ALPH chunk payload into width x height alpha bytes (alpha_dec.c: raw or lossless-compressed
    /// levels, then horizontal / vertical / gradient unfiltering). Throws on a bad chunk.</summary>
    public static byte[] DecodeAlpha(byte[] chunk, int offset, int length, int width, int height)
    {
        if (length <= 1) throw new InvalidDataException("Invalid ALPH chunk.");
        int header = chunk[offset];
        int method = header & 0x03;
        int filter = (header >> 2) & 0x03;
        int preProcessing = (header >> 4) & 0x03;
        int rsrv = (header >> 6) & 0x03;
        if (method > 1 || preProcessing > 1 || rsrv != 0) throw new InvalidDataException("Invalid ALPH header.");
        var output = new byte[width * height];
        if (method == 0)
        {
            if (length - 1 < width * height) throw new InvalidDataException("Truncated ALPH data.");
            Buffer.BlockCopy(chunk, offset + 1, output, 0, width * height);
        }
        else
        {
            var dec = new Vp8lDecoder(chunk, offset + 1, length - 1);
            var argb = dec.DecodeLevel0(width, height) ?? throw new InvalidDataException("Invalid ALPH bitstream.");
            for (int i = 0; i < width * height; ++i) output[i] = (byte)(argb[i] >> 8);   // WebPExtractGreen
        }
        // unfilter row by row (prev = the previous output row; none for row 0)
        for (int y = 0; y < height; ++y)
        {
            int row = y * width;
            int prev = y == 0 ? -1 : row - width;
            switch (filter)
            {
                case 1:   // horizontal
                    HorizontalUnfilter(output, prev, row, width);
                    break;
                case 2:   // vertical
                    if (prev < 0) HorizontalUnfilter(output, -1, row, width);
                    else for (int i = 0; i < width; ++i) output[row + i] = (byte)(output[prev + i] + output[row + i]);
                    break;
                case 3:   // gradient
                    if (prev < 0)
                    {
                        HorizontalUnfilter(output, -1, row, width);
                    }
                    else
                    {
                        byte top = output[prev], topLeft = top, left = top;
                        for (int i = 0; i < width; ++i)
                        {
                            top = output[prev + i];
                            left = (byte)(output[row + i] + GradientPredictor(left, top, topLeft));
                            topLeft = top;
                            output[row + i] = left;
                        }
                    }
                    break;
            }
        }
        return output;
    }

    private static void HorizontalUnfilter(byte[] buf, int prev, int row, int width)
    {
        byte pred = prev < 0 ? (byte)0 : buf[prev];
        for (int i = 0; i < width; ++i)
        {
            buf[row + i] = (byte)(pred + buf[row + i]);
            pred = buf[row + i];
        }
    }

    private static int GradientPredictor(byte a, byte b, byte c)
    {
        int g = a + b - c;
        return (g & ~0xff) == 0 ? g : g < 0 ? 0 : 255;
    }
}
