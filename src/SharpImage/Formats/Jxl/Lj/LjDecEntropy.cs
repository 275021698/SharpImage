// libjxl 0.12 entropy decoding (dec_bit_reader.h, dec_ans.h/.cc, dec_huffman.cc, huffman_table.cc,
// dec_context_map.cc), ported line by line.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Jxl.Lj;

/// <summary>LSB-first bit reader (libjxl BitReader). Reads past the end return zeros and are reported by
/// <see cref="AllReadsWithinBounds"/>.</summary>
internal sealed class LjBitReader
{
    private readonly byte[] data;
    private readonly int begin;
    private readonly int end;
    private long bitPos;   // bits consumed from begin

    public LjBitReader(byte[] data, int offset = 0, int length = -1)
    {
        this.data = data;
        begin = offset;
        end = length < 0 ? data.Length : offset + length;
    }

    public long TotalBitsConsumed => bitPos;

    public long TotalBytes => end - begin;

    public bool AllReadsWithinBounds => bitPos <= (long)(end - begin) * 8;

    public void Refill()
    {
    }

    public ulong PeekBits(int nbits)
    {
        if (nbits == 0)
        {
            return 0;
        }

        long byteIdx = begin + (bitPos >> 3);
        int shift = (int)(bitPos & 7);
        ulong v = 0;
        // Gather up to 9 bytes (enough for 56 bits at any shift).
        int need = (shift + nbits + 7) >> 3;
        for (int i = 0; i < need; i++)
        {
            long p = byteIdx + i;
            ulong b = p < end ? data[p] : 0UL;
            v |= b << (8 * i);
            if (i == 7 && need > 8)
            {
                long p8 = byteIdx + 8;
                ulong b8 = p8 < end ? data[p8] : 0UL;
                return ((v >> shift) | (b8 << (64 - shift))) & (nbits == 64 ? ulong.MaxValue : (1UL << nbits) - 1);
            }
        }

        return (v >> shift) & ((1UL << nbits) - 1);
    }

    public void Consume(int nbits) => bitPos += nbits;

    public ulong ReadBits(int nbits)
    {
        ulong v = PeekBits(nbits);
        bitPos += nbits;
        return v;
    }

    public uint Read(int nbits) => (uint)ReadBits(nbits);

    public void SkipBits(long nbits) => bitPos += nbits;

    public void JumpToByteBoundary()
    {
        long r = bitPos & 7;
        if (r != 0)
        {
            ulong pad = ReadBits((int)(8 - r));
            if (pad != 0)
            {
                throw new InvalidDataException("Non-zero padding bits");
            }
        }
    }

    public int BytePosition => (int)(bitPos >> 3);

    public Func<int, uint> AsReadFunc() => n => (uint)ReadBits(n);
}

internal struct LjHuffmanCode
{
    public byte Bits;
    public ushort Value;

    public LjHuffmanCode(byte bits, ushort value)
    {
        Bits = bits;
        Value = value;
    }
}

internal sealed class LjHuffmanDecodingData
{
    public const int TableBits = 8;
    public LjHuffmanCode[] Table = Array.Empty<LjHuffmanCode>();

    public ushort ReadSymbol(LjBitReader br)
    {
        int idx = (int)br.PeekBits(TableBits);
        int n = Table[idx].Bits;
        if (n > TableBits)
        {
            br.Consume(TableBits);
            n -= TableBits;
            idx += Table[idx].Value;
            idx += (int)br.PeekBits(n);
        }

        br.Consume(Table[idx].Bits);
        return Table[idx].Value;
    }

    private static readonly byte[] CodeLengthCodeOrder = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static int GetNextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return (key & (step - 1)) + step;
    }

    private static void ReplicateValue(LjHuffmanCode[] table, int off, int step, int end, LjHuffmanCode code)
    {
        do
        {
            end -= step;
            table[off + end] = code;
        }
        while (end > 0);
    }

    private static int NextTableBitSize(ushort[] count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < LjAns.PrefixMaxBits)
        {
            if (left <= count[len])
            {
                break;
            }

            left -= count[len];
            ++len;
            left <<= 1;
        }

        return len - rootBits;
    }

    /// <summary>huffman_table.cc BuildHuffmanTable. Returns the table size (0 on failure).</summary>
    public static int BuildHuffmanTable(LjHuffmanCode[] rootTable, int rootBits, byte[] codeLengths, int codeLengthsSize, ushort[] count)
    {
        var offset = new ushort[LjAns.PrefixMaxBits + 1];
        int maxLength = 1;
        if (codeLengthsSize > 1 << LjAns.PrefixMaxBits)
        {
            return 0;
        }

        var sorted = new ushort[codeLengthsSize];
        {
            ushort sum = 0;
            for (int len = 1; len <= LjAns.PrefixMaxBits; len++)
            {
                offset[len] = sum;
                if (count[len] != 0)
                {
                    sum = (ushort)(sum + count[len]);
                    maxLength = len;
                }
            }
        }

        for (int symbol = 0; symbol < codeLengthsSize; symbol++)
        {
            if (codeLengths[symbol] != 0)
            {
                sorted[offset[codeLengths[symbol]]++] = (ushort)symbol;
            }
        }

        int table = 0;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;
        LjHuffmanCode code;
        if (offset[LjAns.PrefixMaxBits] == 1)
        {
            code = new LjHuffmanCode(0, sorted[0]);
            for (int key = 0; key < totalSize; ++key)
            {
                rootTable[key] = code;
            }

            return totalSize;
        }

        if (tableBits > maxLength)
        {
            tableBits = maxLength;
            tableSize = 1 << tableBits;
        }

        int k = 0, sym = 0, step = 2;
        code = new LjHuffmanCode(1, 0);
        do
        {
            for (; count[code.Bits] != 0; --count[code.Bits])
            {
                code.Value = sorted[sym++];
                ReplicateValue(rootTable, table + k, step, tableSize, code);
                k = GetNextKey(k, code.Bits);
            }

            step <<= 1;
        }
        while (++code.Bits <= tableBits);

        while (totalSize != tableSize)
        {
            Array.Copy(rootTable, table, rootTable, table + tableSize, tableSize);
            tableSize <<= 1;
        }

        int mask = totalSize - 1;
        int low = -1;
        step = 2;
        for (int len = rootBits + 1; len <= maxLength; ++len, step <<= 1)
        {
            for (; count[len] != 0; --count[len])
            {
                if ((k & mask) != low)
                {
                    table += tableSize;
                    tableBits = NextTableBitSize(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = k & mask;
                    rootTable[low].Bits = (byte)(tableBits + rootBits);
                    rootTable[low].Value = (ushort)(table - low);
                }

                code = new LjHuffmanCode((byte)(len - rootBits), sorted[sym++]);
                ReplicateValue(rootTable, table + (k >> rootBits), step, tableSize, code);
                k = GetNextKey(k, len);
            }
        }

        return totalSize;
    }

    private static bool ReadHuffmanCodeLengths(byte[] codeLengthCodeLengths, int numSymbols, byte[] codeLengths, LjBitReader br)
    {
        int symbol = 0;
        byte prevCodeLen = 8;
        int repeat = 0;
        byte repeatCodeLen = 0;
        int space = 32768;
        var table = new LjHuffmanCode[32];
        var counts = new ushort[16];
        for (int i = 0; i < 18; i++)
        {
            ++counts[codeLengthCodeLengths[i]];
        }

        if (BuildHuffmanTable(table, 5, codeLengthCodeLengths, 18, counts) == 0)
        {
            return false;
        }

        while (symbol < numSymbols && space > 0)
        {
            LjHuffmanCode p = table[br.PeekBits(5)];
            br.Consume(p.Bits);
            byte codeLen = (byte)p.Value;
            if (codeLen < 16)
            {
                repeat = 0;
                codeLengths[symbol++] = codeLen;
                if (codeLen != 0)
                {
                    prevCodeLen = codeLen;
                    space -= 32768 >> codeLen;
                }
            }
            else
            {
                int extraBits = codeLen - 14;
                byte newLen = 0;
                if (codeLen == 16)
                {
                    newLen = prevCodeLen;
                }

                if (repeatCodeLen != newLen)
                {
                    repeat = 0;
                    repeatCodeLen = newLen;
                }

                int oldRepeat = repeat;
                if (repeat > 0)
                {
                    repeat -= 2;
                    repeat <<= extraBits;
                }

                repeat += (int)br.ReadBits(extraBits) + 3;
                int repeatDelta = repeat - oldRepeat;
                if (symbol + repeatDelta > numSymbols)
                {
                    return false;
                }

                for (int i = 0; i < repeatDelta; i++)
                {
                    codeLengths[symbol + i] = repeatCodeLen;
                }

                symbol += repeatDelta;
                if (repeatCodeLen != 0)
                {
                    space -= repeatDelta << (15 - repeatCodeLen);
                }
            }
        }

        if (space != 0)
        {
            return false;
        }

        for (int i = symbol; i < numSymbols; i++)
        {
            codeLengths[i] = 0;
        }

        return true;
    }

    private bool ReadSimpleCode(int alphabetSize, LjBitReader br)
    {
        int maxBits = alphabetSize > 1 ? LjMath.FloorLog2((uint)(alphabetSize - 1)) + 1 : 0;
        int numSymbols = (int)br.ReadBits(2) + 1;
        var symbols = new ushort[4];
        for (int i = 0; i < numSymbols; i++)
        {
            ushort s = (ushort)br.ReadBits(maxBits);
            if (s >= alphabetSize)
            {
                return false;
            }

            symbols[i] = s;
        }

        for (int i = 0; i < numSymbols - 1; i++)
        {
            for (int j = i + 1; j < numSymbols; j++)
            {
                if (symbols[i] == symbols[j])
                {
                    return false;
                }
            }
        }

        if (numSymbols == 4)
        {
            numSymbols += (int)br.ReadBits(1);
        }

        void Swap(int i, int j) => (symbols[i], symbols[j]) = (symbols[j], symbols[i]);
        Table = new LjHuffmanCode[1 << TableBits];
        int tableSize = 1;
        switch (numSymbols)
        {
            case 1:
                Table[0] = new LjHuffmanCode(0, symbols[0]);
                break;
            case 2:
                if (symbols[0] > symbols[1])
                {
                    Swap(0, 1);
                }

                Table[0] = new LjHuffmanCode(1, symbols[0]);
                Table[1] = new LjHuffmanCode(1, symbols[1]);
                tableSize = 2;
                break;
            case 3:
                if (symbols[1] > symbols[2])
                {
                    Swap(1, 2);
                }

                Table[0] = new LjHuffmanCode(1, symbols[0]);
                Table[2] = new LjHuffmanCode(1, symbols[0]);
                Table[1] = new LjHuffmanCode(2, symbols[1]);
                Table[3] = new LjHuffmanCode(2, symbols[2]);
                tableSize = 4;
                break;
            case 4:
                for (int i = 0; i < 3; i++)
                {
                    for (int j = i + 1; j < 4; j++)
                    {
                        if (symbols[i] > symbols[j])
                        {
                            Swap(i, j);
                        }
                    }
                }

                Table[0] = new LjHuffmanCode(2, symbols[0]);
                Table[2] = new LjHuffmanCode(2, symbols[1]);
                Table[1] = new LjHuffmanCode(2, symbols[2]);
                Table[3] = new LjHuffmanCode(2, symbols[3]);
                tableSize = 4;
                break;
            case 5:
                if (symbols[2] > symbols[3])
                {
                    Swap(2, 3);
                }

                Table[0] = new LjHuffmanCode(1, symbols[0]);
                Table[1] = new LjHuffmanCode(2, symbols[1]);
                Table[2] = new LjHuffmanCode(1, symbols[0]);
                Table[3] = new LjHuffmanCode(3, symbols[2]);
                Table[4] = new LjHuffmanCode(1, symbols[0]);
                Table[5] = new LjHuffmanCode(2, symbols[1]);
                Table[6] = new LjHuffmanCode(1, symbols[0]);
                Table[7] = new LjHuffmanCode(3, symbols[3]);
                tableSize = 8;
                break;
            default:
                return false;
        }

        const int goal = 1 << TableBits;
        while (tableSize != goal)
        {
            Array.Copy(Table, 0, Table, tableSize, tableSize);
            tableSize <<= 1;
        }

        return true;
    }

    public bool ReadFromBitStream(int alphabetSize, LjBitReader br)
    {
        if (alphabetSize > 1 << LjAns.PrefixMaxBits)
        {
            return false;
        }

        uint simpleCodeOrSkip = (uint)br.ReadBits(2);
        if (simpleCodeOrSkip == 1)
        {
            return ReadSimpleCode(alphabetSize, br);
        }

        var codeLengths = new byte[alphabetSize];
        var codeLengthCodeLengths = new byte[18];
        int space = 32;
        int numCodes = 0;
        ReadOnlySpan<byte> huffBits = [2, 2, 2, 3, 2, 2, 2, 4, 2, 2, 2, 3, 2, 2, 2, 4];
        ReadOnlySpan<byte> huffVals = [0, 4, 3, 2, 0, 4, 3, 1, 0, 4, 3, 2, 0, 4, 3, 5];
        for (int i = (int)simpleCodeOrSkip; i < 18 && space > 0; i++)
        {
            int idx = CodeLengthCodeOrder[i];
            int p = (int)br.PeekBits(4);
            br.Consume(huffBits[p]);
            byte v = huffVals[p];
            codeLengthCodeLengths[idx] = v;
            if (v != 0)
            {
                space -= 32 >> v;
                ++numCodes;
            }
        }

        bool ok = (numCodes == 1 || space == 0) && ReadHuffmanCodeLengths(codeLengthCodeLengths, alphabetSize, codeLengths, br);
        if (!ok)
        {
            return false;
        }

        var counts = new ushort[16];
        for (int i = 0; i < alphabetSize; i++)
        {
            ++counts[codeLengths[i]];
        }

        Table = new LjHuffmanCode[alphabetSize + 376];
        int size = BuildHuffmanTable(Table, TableBits, codeLengths, alphabetSize, counts);
        Array.Resize(ref Table, size);
        return size > 0;
    }
}

/// <summary>A decoded set of entropy codes (ANSCode).</summary>
internal sealed class LjAnsCode
{
    public AliasEntry[][] AliasTables = Array.Empty<AliasEntry[]>();
    public LjHuffmanDecodingData[] HuffmanData = Array.Empty<LjHuffmanDecodingData>();
    public LjHybridUint[] UintConfig = Array.Empty<LjHybridUint>();
    public int[] DegenerateSymbols = Array.Empty<int>();
    public bool UsePrefixCode;
    public int LogAlphaSize;
    public LjLz77Params Lz77 = new();
    public long MaxNumBits;

    public void UpdateMaxNumBits(int ctx, int symbol)
    {
        LjHybridUint cfg = UintConfig[ctx];
        if (Lz77.Enabled && Lz77.NonserializedDistanceContext != ctx && symbol >= Lz77.MinSymbol)
        {
            symbol -= (int)Lz77.MinSymbol;
            cfg = Lz77.LengthUintConfig;
        }

        if (symbol < cfg.SplitToken)
        {
            MaxNumBits = Math.Max(MaxNumBits, cfg.SplitExponent);
            return;
        }

        long nExtra = cfg.SplitExponent - (cfg.MsbInToken + cfg.LsbInToken) + ((symbol - cfg.SplitToken) >> (int)(cfg.MsbInToken + cfg.LsbInToken));
        long total = cfg.MsbInToken + cfg.LsbInToken + nExtra + 1;
        MaxNumBits = Math.Max(MaxNumBits, total);
    }
}

internal static class LjDecAns
{
    private static Exception Fail(string m) => new InvalidDataException("JXL: " + m);

    private static int DecodeVarLenUint8(LjBitReader br)
    {
        if (br.ReadBits(1) != 0)
        {
            int nbits = (int)br.ReadBits(3);
            return nbits == 0 ? 1 : (int)br.ReadBits(nbits) + (1 << nbits);
        }

        return 0;
    }

    private static int DecodeVarLenUint16(LjBitReader br)
    {
        if (br.ReadBits(1) != 0)
        {
            int nbits = (int)br.ReadBits(4);
            return nbits == 0 ? 1 : (int)br.ReadBits(nbits) + (1 << nbits);
        }

        return 0;
    }

    private static readonly byte[,] HistHuff =
    {
        { 3, 10 }, { 7, 12 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 5, 0 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 6, 11 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 5, 0 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 7, 13 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 5, 0 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 6, 11 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
        { 3, 10 }, { 5, 0 }, { 3, 7 }, { 4, 3 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 5 },
        { 3, 10 }, { 4, 4 }, { 3, 7 }, { 4, 1 }, { 3, 6 }, { 3, 8 }, { 3, 9 }, { 4, 2 },
    };

    private static int[] ReadHistogram(int precisionBits, LjBitReader br)
    {
        int range = 1 << precisionBits;
        int simpleCode = (int)br.ReadBits(1);
        int[] counts;
        if (simpleCode == 1)
        {
            var symbols = new int[2];
            int maxSymbol = 0;
            int numSymbols = (int)br.ReadBits(1) + 1;
            for (int i = 0; i < numSymbols; i++)
            {
                symbols[i] = DecodeVarLenUint8(br);
                maxSymbol = Math.Max(maxSymbol, symbols[i]);
            }

            counts = new int[maxSymbol + 1];
            if (numSymbols == 1)
            {
                counts[symbols[0]] = range;
            }
            else
            {
                if (symbols[0] == symbols[1])
                {
                    throw Fail("Invalid histogram");
                }

                counts[symbols[0]] = (int)br.ReadBits(precisionBits);
                counts[symbols[1]] = range - counts[symbols[0]];
            }

            return counts;
        }

        int isFlat = (int)br.ReadBits(1);
        if (isFlat == 1)
        {
            int alphabetSize = DecodeVarLenUint8(br) + 1;
            if (alphabetSize > range)
            {
                throw Fail("Invalid flat histogram");
            }

            return LjAns.CreateFlatHistogram(alphabetSize, range);
        }

        uint shift;
        {
            int upperBoundLog = LjMath.FloorLog2((uint)(LjAns.LogTabSize + 1));
            int log = 0;
            for (; log < upperBoundLog; log++)
            {
                if (br.ReadBits(1) == 0)
                {
                    break;
                }
            }

            shift = (uint)((br.ReadBits(log) | (1UL << log)) - 1);
            if (shift > LjAns.LogTabSize + 1)
            {
                throw Fail("Invalid shift value");
            }
        }

        int length = DecodeVarLenUint8(br) + 3;
        counts = new int[length];
        int totalCount = 0;
        var logcounts = new int[length];
        int omitLog = -1, omitPos = -1;
        var same = new int[length];
        for (int i = 0; i < length; ++i)
        {
            int idx = (int)br.PeekBits(7);
            br.Consume(HistHuff[idx, 0]);
            logcounts[i] = HistHuff[idx, 1] - 1;
            if (logcounts[i] == LjAns.LogTabSize)
            {
                int rleLength = DecodeVarLenUint8(br);
                same[i] = rleLength + 5;
                i += rleLength + 3;
                continue;
            }

            if (logcounts[i] > omitLog)
            {
                omitLog = logcounts[i];
                omitPos = i;
            }
        }

        if (omitPos < 0)
        {
            throw Fail("Invalid histogram.");
        }

        if (omitPos + 1 < length && logcounts[omitPos + 1] == LjAns.LogTabSize)
        {
            throw Fail("Invalid histogram.");
        }

        int prev = 0, numsame = 0;
        for (int i = 0; i < length; ++i)
        {
            if (same[i] != 0)
            {
                numsame = same[i] - 1;
                prev = i > 0 ? counts[i - 1] : 0;
            }

            if (numsame > 0)
            {
                counts[i] = prev;
                numsame--;
            }
            else
            {
                int code = logcounts[i];
                if (i == omitPos || code < 0)
                {
                    continue;
                }
                else if (shift == 0 || code == 0)
                {
                    counts[i] = 1 << code;
                }
                else
                {
                    int bitcount = LjAns.GetPopulationCountPrecision(code, (int)shift);
                    counts[i] = (1 << code) + ((int)br.ReadBits(bitcount) << (code - bitcount));
                }
            }

            totalCount += counts[i];
        }

        counts[omitPos] = range - totalCount;
        if (counts[omitPos] <= 0)
        {
            throw Fail("Invalid histogram count.");
        }

        return counts;
    }

    private static void DecodeAnsCodes(int numHistograms, int maxAlphabetSize, LjBitReader br, LjAnsCode result)
    {
        result.DegenerateSymbols = new int[numHistograms];
        Array.Fill(result.DegenerateSymbols, -1);
        if (result.UsePrefixCode)
        {
            result.HuffmanData = new LjHuffmanDecodingData[numHistograms];
            var alphabetSizes = new int[numHistograms];
            for (int c = 0; c < numHistograms; c++)
            {
                alphabetSizes[c] = DecodeVarLenUint16(br) + 1;
                if (alphabetSizes[c] > maxAlphabetSize)
                {
                    throw Fail("Alphabet size is too long");
                }
            }

            for (int c = 0; c < numHistograms; c++)
            {
                var hd = new LjHuffmanDecodingData();
                result.HuffmanData[c] = hd;
                if (alphabetSizes[c] > 1)
                {
                    if (!hd.ReadFromBitStream(alphabetSizes[c], br))
                    {
                        throw Fail("Invalid huffman tree");
                    }
                }
                else
                {
                    hd.Table = new LjHuffmanCode[1 << LjHuffmanDecodingData.TableBits];
                }

                foreach (LjHuffmanCode h in hd.Table)
                {
                    if (h.Bits <= LjHuffmanDecodingData.TableBits)
                    {
                        result.UpdateMaxNumBits(c, h.Value);
                    }
                }
            }
        }
        else
        {
            result.AliasTables = new AliasEntry[numHistograms][];
            for (int c = 0; c < numHistograms; c++)
            {
                int[] counts = ReadHistogram(LjAns.LogTabSize, br);
                if (counts.Length > maxAlphabetSize)
                {
                    throw Fail("Alphabet size is too long");
                }

                int n = counts.Length;
                while (n > 0 && counts[n - 1] == 0)
                {
                    n--;
                }

                for (int s = 0; s < n; s++)
                {
                    if (counts[s] != 0)
                    {
                        result.UpdateMaxNumBits(c, s);
                    }
                }

                int degenerate = n == 0 ? 0 : n - 1;
                for (int s = 0; s < degenerate; s++)
                {
                    if (counts[s] != 0)
                    {
                        degenerate = -1;
                        break;
                    }
                }

                result.DegenerateSymbols[c] = degenerate;
                result.AliasTables[c] = JxlEntropy.InitAliasTable(counts[..n], result.LogAlphaSize);
            }
        }
    }

    private static LjHybridUint DecodeUintConfig(int logAlphaSize, LjBitReader br)
    {
        uint splitExponent = (uint)br.ReadBits(LjMath.CeilLog2Nonzero((uint)(logAlphaSize + 1)));
        uint msb = 0, lsb = 0;
        if (splitExponent != logAlphaSize)
        {
            int nbits = LjMath.CeilLog2Nonzero(splitExponent + 1);
            msb = (uint)br.ReadBits(nbits);
            if (msb > splitExponent)
            {
                throw Fail("Invalid HybridUintConfig");
            }

            nbits = LjMath.CeilLog2Nonzero(splitExponent - msb + 1);
            lsb = (uint)br.ReadBits(nbits);
        }

        if (lsb + msb > splitExponent)
        {
            throw Fail("Invalid HybridUintConfig");
        }

        return new LjHybridUint(splitExponent, msb, lsb);
    }

    /// <summary>DecodeHistograms: LZ77 params, context map, prefix/ANS choice, uint configs and the codes.</summary>
    public static LjAnsCode DecodeHistograms(LjBitReader br, int numContexts, out byte[] contextMap, bool disallowLz77 = false)
    {
        var code = new LjAnsCode();
        code.Lz77.Read(br);
        if (code.Lz77.Enabled)
        {
            numContexts++;
            code.Lz77.LengthUintConfig = DecodeUintConfig(8, br);
        }

        if (code.Lz77.Enabled && disallowLz77)
        {
            throw Fail("Using LZ77 when explicitly disallowed");
        }

        int numHistograms = 1;
        contextMap = new byte[numContexts];
        if (numContexts > 1)
        {
            numHistograms = DecodeContextMap(contextMap, br);
        }

        code.Lz77.NonserializedDistanceContext = contextMap[^1];
        code.UsePrefixCode = br.ReadBits(1) != 0;
        code.LogAlphaSize = code.UsePrefixCode ? LjAns.PrefixMaxBits : (int)br.ReadBits(2) + 5;
        code.UintConfig = new LjHybridUint[numHistograms];
        for (int i = 0; i < numHistograms; i++)
        {
            code.UintConfig[i] = DecodeUintConfig(code.LogAlphaSize, br);
        }

        DecodeAnsCodes(numHistograms, 1 << code.LogAlphaSize, br, code);
        return code;
    }

    private static void InverseMoveToFront(byte[] v)
    {
        var mtf = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            mtf[i] = (byte)i;
        }

        for (int i = 0; i < v.Length; i++)
        {
            byte index = v[i];
            v[i] = mtf[index];
            if (index != 0)
            {
                byte value = mtf[index];
                for (int k = index; k != 0; k--)
                {
                    mtf[k] = mtf[k - 1];
                }

                mtf[0] = value;
            }
        }
    }

    /// <summary>DecodeContextMap. Returns the number of histograms.</summary>
    public static int DecodeContextMap(byte[] contextMap, LjBitReader br)
    {
        bool isSimple = br.ReadBits(1) != 0;
        if (isSimple)
        {
            int bitsPerEntry = (int)br.ReadBits(2);
            for (int i = 0; i < contextMap.Length; i++)
            {
                contextMap[i] = bitsPerEntry != 0 ? (byte)br.ReadBits(bitsPerEntry) : (byte)0;
            }
        }
        else
        {
            bool useMtf = br.ReadBits(1) != 0;
            LjAnsCode code = DecodeHistograms(br, 1, out byte[] sinkCtxMap, contextMap.Length <= 2);
            var reader = new LjAnsSymbolReader(code, br);
            uint maxsym = 0;
            for (int i = 0; i < contextMap.Length; i++)
            {
                uint sym = reader.ReadHybridUint(0, br, sinkCtxMap);
                maxsym = Math.Max(maxsym, sym);
                contextMap[i] = (byte)sym;
            }

            if (maxsym >= 256)
            {
                throw Fail("Invalid cluster ID");
            }

            if (!reader.CheckAnsFinalState())
            {
                throw Fail("Invalid context map");
            }

            if (useMtf)
            {
                InverseMoveToFront(contextMap);
            }
        }

        int numHtrees = 0;
        foreach (byte b in contextMap)
        {
            numHtrees = Math.Max(numHtrees, b + 1);
        }

        var have = new bool[numHtrees];
        int found = 0;
        foreach (byte b in contextMap)
        {
            if (!have[b])
            {
                have[b] = true;
                found++;
            }
        }

        if (found != numHtrees)
        {
            throw Fail("Incomplete context map.");
        }

        return numHtrees;
    }
}

/// <summary>ANSSymbolReader with LZ77 support.</summary>
internal sealed class LjAnsSymbolReader
{
    private const int WindowSize = 1 << 20;
    private const int WindowMask = WindowSize - 1;
    private readonly LjAnsCode code;
    private uint state;
    private readonly int logAlphaSize;
    private readonly int logEntrySize;
    private readonly int entrySizeMinus1;
    private readonly uint[]? window;
    private uint numDecoded;
    private uint numToCopy;
    private uint copyPos;
    private readonly int lz77Ctx;
    private readonly uint lz77MinLength;
    private readonly uint lz77Threshold = 1 << 20;
    private readonly LjHybridUint lz77LengthUint;
    private readonly uint[] specialDistances = new uint[120];
    private readonly uint numSpecialDistances;

    public LjAnsSymbolReader(LjAnsCode code, LjBitReader br, int distanceMultiplier = 0)
    {
        this.code = code;
        if (!code.UsePrefixCode)
        {
            state = (uint)br.ReadBits(32);
            logAlphaSize = code.LogAlphaSize;
            logEntrySize = LjAns.LogTabSize - code.LogAlphaSize;
            entrySizeMinus1 = (1 << logEntrySize) - 1;
        }
        else
        {
            state = LjAns.Signature << 16;
        }

        if (!code.Lz77.Enabled)
        {
            return;
        }

        window = new uint[WindowSize];
        lz77Ctx = code.Lz77.NonserializedDistanceContext;
        lz77LengthUint = code.Lz77.LengthUintConfig;
        lz77Threshold = code.Lz77.MinSymbol;
        lz77MinLength = code.Lz77.MinLength;
        numSpecialDistances = distanceMultiplier == 0 ? 0u : 120u;
        for (int i = 0; i < numSpecialDistances; i++)
        {
            specialDistances[i] = (uint)LjLz77.SpecialDistance(i, distanceMultiplier);
        }
    }

    public bool UsesLz77 => window != null;

    private int ReadSymbolAns(int histo, LjBitReader br)
    {
        uint res = state & (LjAns.TabSize - 1u);
        AliasEntry e = code.AliasTables[histo][res >> logEntrySize];
        int pos = (int)(res & entrySizeMinus1);
        bool greater = pos >= e.Cutoff;
        int freq = e.Freq0 ^ (greater ? e.Freq1XorFreq0 : 0);
        int offset = (greater ? e.Offsets1 : 0) + pos;
        int value = greater ? e.Right : (int)(res >> logEntrySize);
        state = (uint)(freq * (state >> LjAns.LogTabSize)) + (uint)offset;
        if (state < (1u << 16))
        {
            state = (state << 16) | (uint)br.PeekBits(16);
            br.Consume(16);
        }

        return value;
    }

    public int ReadSymbol(int histo, LjBitReader br) =>
        code.UsePrefixCode ? code.HuffmanData[histo].ReadSymbol(br) : ReadSymbolAns(histo, br);

    public static uint ReadHybridUintConfig(LjHybridUint config, int token, LjBitReader br)
    {
        uint splitToken = config.SplitToken;
        uint msb = config.MsbInToken, lsb = config.LsbInToken, splitExponent = config.SplitExponent;
        if ((uint)token < splitToken)
        {
            return (uint)token;
        }

        uint nbits = splitExponent - (msb + lsb) + (((uint)token - splitToken) >> (int)(msb + lsb));
        nbits &= 31;
        uint low = (uint)token & ((1u << (int)lsb) - 1);
        uint t = (uint)token >> (int)lsb;
        ulong bits = br.PeekBits((int)nbits);
        br.Consume((int)nbits);
        ulong ret = ((((1UL << (int)msb) | (t & ((1UL << (int)msb) - 1))) << (int)nbits) | bits) << (int)lsb | low;
        return (uint)ret;
    }

    /// <summary>ReadHybridUintClustered (histogram already resolved).</summary>
    public uint ReadHybridUintClustered(int ctx, LjBitReader br)
    {
        if (window != null && numToCopy > 0)
        {
            uint r = window[copyPos++ & WindowMask];
            numToCopy--;
            window[numDecoded++ & WindowMask] = r;
            return r;
        }

        int token = ReadSymbol(ctx, br);
        if (window != null && token >= lz77Threshold)
        {
            numToCopy = ReadHybridUintConfig(lz77LengthUint, (int)(token - lz77Threshold), br) + lz77MinLength;
            int dToken = ReadSymbol(lz77Ctx, br);
            uint distance = ReadHybridUintConfig(code.UintConfig[lz77Ctx], dToken, br);
            distance = distance < numSpecialDistances ? specialDistances[distance] : distance + 1 - numSpecialDistances;
            if (distance > numDecoded)
            {
                distance = numDecoded;
            }

            if (distance > WindowSize)
            {
                distance = WindowSize;
            }

            copyPos = numDecoded - distance;
            if (distance == 0)
            {
                Array.Clear(window, 0, (int)Math.Min(numToCopy, (uint)WindowSize));
            }

            if (numToCopy < lz77MinLength)
            {
                throw new InvalidDataException("JXL: invalid LZ77 length");
            }

            uint r = window[copyPos++ & WindowMask];
            numToCopy--;
            window[numDecoded++ & WindowMask] = r;
            return r;
        }

        uint ret = ReadHybridUintConfig(code.UintConfig[ctx], token, br);
        if (window != null)
        {
            window[numDecoded++ & WindowMask] = ret;
        }

        return ret;
    }

    public uint ReadHybridUint(int ctx, LjBitReader br, byte[] contextMap) => ReadHybridUintClustered(contextMap[ctx], br);

    public bool CheckAnsFinalState() => state == LjAns.Signature << 16;

    /// <summary>IsSingleValueAndAdvance: the context codes a single value that needs no bits.</summary>
    public bool IsSingleValueAndAdvance(int ctx, out uint value, long count)
    {
        value = 0;
        if (code.UsePrefixCode || numToCopy != 0)
        {
            return false;
        }

        uint res = state & (LjAns.TabSize - 1u);
        AliasEntry e = code.AliasTables[ctx][res >> logEntrySize];
        int pos = (int)(res & entrySizeMinus1);
        bool greater = pos >= e.Cutoff;
        int freq = e.Freq0 ^ (greater ? e.Freq1XorFreq0 : 0);
        int symbol = greater ? e.Right : (int)(res >> logEntrySize);
        if (freq != LjAns.TabSize)
        {
            return false;
        }

        if (code.UintConfig[ctx].SplitToken <= symbol)
        {
            return false;
        }

        if (symbol >= lz77Threshold)
        {
            return false;
        }

        value = (uint)symbol;
        if (window != null)
        {
            for (long i = 0; i < count; i++)
            {
                window[numDecoded++ & WindowMask] = (uint)symbol;
            }
        }

        return true;
    }
}
