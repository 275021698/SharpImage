// libjxl 0.12 decoder-side header pieces: ICC profile decoding (icc_codec.cc ICCReader + UnpredictICC), the frame
// table of contents (toc.cc), permutations (coeff_order.cc DecodePermutation, lehmer_code.h) and frame dimensions
// (frame_dimensions.h). Line-by-line ports.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Jxl.Lj;

internal static partial class LjDec
{
    public static InvalidDataException Fail(string m) => new("JXL: " + m);

    /// <summary>U32Coder::Read.</summary>
    public static uint ReadU32(LjU32Enc enc, LjBitReader br)
    {
        LjU32 d = enc.Get((int)br.ReadBits(2));
        return d.Direct ? d.Value : unchecked((uint)br.ReadBits(d.Bits) + d.Value);
    }

    public static int CeilLog2Nonzero(long x)
    {
        int floor = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)x);
        return (x & (x - 1)) == 0 ? floor : floor + 1;
    }

    // C library float functions. powf/logf/log2f go through double precision (correctly rounded, so independent of
    // the C runtime .NET reaches). cbrtf is not correctly rounded in the MSVC runtime djxl links, so the value it
    // gives for the default opsin bias is pinned; other inputs use the system cbrtf.
    public static float CbrtF(float x) =>
        BitConverter.SingleToUInt32Bits(x) == 0xBB789536u
            ? BitConverter.UInt32BitsToSingle(0xBE1FB276u)   // libjxl's cbrtf(-0.0037930732552754493f), the default opsin bias
            : MathF.Cbrt(x);

    public static float PowF(float a, float b) => (float)Math.Pow(a, b);

    public static float LogF(float x) => (float)Math.Log(x);

    public static float Log2F(float x) => (float)Math.Log2(x);

    public static int FloorLog2Nonzero(long x) => 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)x);

    public static int DivCeil(int a, int b) => (a + b - 1) / b;
}

/// <summary>FrameDimensions.</summary>
internal sealed class LjFrameDims
{
    public const int BlockDim = 8, GroupDimDefault = 256;
    public int Xsize, Ysize, XsizeUpsampled, YsizeUpsampled, XsizeUpsampledPadded, YsizeUpsampledPadded, XsizePadded, YsizePadded;
    public int XsizeBlocks, YsizeBlocks, XsizeGroups, YsizeGroups, XsizeDcGroups, YsizeDcGroups, NumGroups, NumDcGroups, GroupDim, DcGroupDim;

    public void Set(int xsizePx, int ysizePx, int groupSizeShift, int maxHShift, int maxVShift, bool modularMode, int upsampling)
    {
        GroupDim = (GroupDimDefault >> 1) << groupSizeShift;
        DcGroupDim = GroupDim * BlockDim;
        XsizeUpsampled = xsizePx;
        YsizeUpsampled = ysizePx;
        Xsize = LjDec.DivCeil(xsizePx, upsampling);
        Ysize = LjDec.DivCeil(ysizePx, upsampling);
        XsizeBlocks = LjDec.DivCeil(Xsize, BlockDim << maxHShift) << maxHShift;
        YsizeBlocks = LjDec.DivCeil(Ysize, BlockDim << maxVShift) << maxVShift;
        XsizePadded = XsizeBlocks * BlockDim;
        YsizePadded = YsizeBlocks * BlockDim;
        if (modularMode)
        {
            XsizePadded = Xsize;
            YsizePadded = Ysize;
        }

        XsizeUpsampledPadded = XsizePadded * upsampling;
        YsizeUpsampledPadded = YsizePadded * upsampling;
        XsizeGroups = LjDec.DivCeil(Xsize, GroupDim);
        YsizeGroups = LjDec.DivCeil(Ysize, GroupDim);
        XsizeDcGroups = LjDec.DivCeil(XsizeBlocks, GroupDim);
        YsizeDcGroups = LjDec.DivCeil(YsizeBlocks, GroupDim);
        NumGroups = XsizeGroups * YsizeGroups;
        NumDcGroups = XsizeDcGroups * YsizeDcGroups;
    }

    public LjRect GroupRect(int g)
    {
        int gx = g % XsizeGroups, gy = g / XsizeGroups;
        return new LjRect(gx * GroupDim, gy * GroupDim, GroupDim, GroupDim, Xsize, Ysize);
    }

    public LjRect BlockGroupRect(int g)
    {
        int gx = g % XsizeGroups, gy = g / XsizeGroups;
        return new LjRect(gx * (GroupDim >> 3), gy * (GroupDim >> 3), GroupDim >> 3, GroupDim >> 3, XsizeBlocks, YsizeBlocks);
    }

    public LjRect DcGroupRect(int g)
    {
        int gx = g % XsizeDcGroups, gy = g / XsizeDcGroups;
        return new LjRect(gx * GroupDim, gy * GroupDim, GroupDim, GroupDim, XsizeBlocks, YsizeBlocks);
    }

    /// <summary>FrameHeader::ToFrameDimensions.</summary>
    public static LjFrameDims FromHeader(LjFrameHeader fh)
    {
        long xsize = fh.DefaultXsize, ysize = fh.DefaultYsize;
        if (fh.CustomSizeOrOrigin && fh.FrameType != LjFrameHeader.DcFrame)
        {
            xsize = fh.FrameXsize;
            ysize = fh.FrameYsize;
        }

        if (fh.FrameType == LjFrameHeader.DcFrame)
        {
            int shift = 3 * (int)fh.DcLevel;
            xsize = (xsize + (1L << shift) - 1) >> shift;
            ysize = (ysize + (1L << shift) - 1) >> shift;
        }

        var fd = new LjFrameDims();
        fd.Set((int)xsize, (int)ysize, (int)fh.GroupSizeShift, fh.ChromaSubsampling.MaxHShift, fh.ChromaSubsampling.MaxVShift,
            fh.Encoding == LjFrameHeader.EncModular, (int)fh.Upsampling);
        return fd;
    }
}

/// <summary>Rect(x0, y0, xsize, ysize[, xend, yend]): size clamped to the end.</summary>
internal readonly struct LjRect
{
    public readonly int X0, Y0, XSize, YSize;

    public LjRect(int x0, int y0, int xsize, int ysize)
    {
        X0 = x0;
        Y0 = y0;
        XSize = xsize;
        YSize = ysize;
    }

    public LjRect(int x0, int y0, int xsize, int ysize, int xend, int yend)
    {
        X0 = x0;
        Y0 = y0;
        XSize = ClampedSize(x0, xsize, xend);
        YSize = ClampedSize(y0, ysize, yend);
    }

    private static int ClampedSize(int pos, int size, int end) => pos <= end ? Math.Min(size, end - pos) : 0;

    public LjRect Crop(int w, int h) => new(X0, Y0, XSize, YSize, w, h);
}

/// <summary>TOC reading (toc.cc) and permutations (coeff_order.cc, lehmer_code.h).</summary>
internal static class LjToc
{
    public const int PermutationContexts = 8;
    private static readonly LjU32Enc TocDist = new(LjU32.BitsOnly(10), LjU32.BitsOffset(14, 1024), LjU32.BitsOffset(22, 17408), LjU32.BitsOffset(30, 4211712));

    public static int NumTocEntries(int numGroups, int numDcGroups, int numPasses) =>
        numGroups == 1 && numPasses == 1 ? 1 : 2 + numDcGroups + (numGroups * numPasses);

    public static uint CoeffOrderContext(uint val)
    {
        // HybridUintConfig(0, 0, 0).Encode: token = val < 1 ? val : 1 + FloorLog2(val).
        uint token = val == 0 ? 0u : 1u + (uint)LjDec.FloorLog2Nonzero(val);
        return Math.Min(token, PermutationContexts - 1);
    }

    public static void ReadPermutation(int skip, int size, uint[]? order, LjBitReader br, LjAnsSymbolReader reader, byte[] contextMap)
    {
        var lehmer = new uint[size];
        uint end = reader.ReadHybridUint((int)CoeffOrderContext((uint)size), br, contextMap) + (uint)skip;
        if (end > size)
        {
            throw LjDec.Fail("Invalid permutation size");
        }

        uint last = 0;
        for (int i = skip; i < end; ++i)
        {
            lehmer[i] = reader.ReadHybridUint((int)CoeffOrderContext(last), br, contextMap);
            last = lehmer[i];
            if (lehmer[i] >= size - i)
            {
                throw LjDec.Fail("Invalid lehmer code");
            }
        }

        if (order == null)
        {
            return;
        }

        DecodeLehmerCode(lehmer, size, order);
    }

    public static void DecodeLehmerCode(uint[] code, int n, uint[] permutation)
    {
        if (n == 0)
        {
            throw LjDec.Fail("Empty permutation");
        }

        int log2n = LjDec.CeilLog2Nonzero(n);
        int paddedN = 1 << log2n;
        var temp = new uint[paddedN];
        for (int i = 0; i < paddedN; i++)
        {
            int i1 = i + 1;
            temp[i] = (uint)(i1 & -i1);
        }

        for (int i = 0; i < n; i++)
        {
            if (code[i] + (uint)i >= n)
            {
                throw LjDec.Fail("Invalid lehmer code");
            }

            uint rank = code[i] + 1;
            int bit = paddedN;
            int next = 0;
            for (int b = 0; b <= log2n; b++)
            {
                int cand = next + bit;
                bit >>= 1;
                if (temp[cand - 1] < rank)
                {
                    next = cand;
                    rank -= temp[cand - 1];
                }
            }

            permutation[i] = (uint)next;
            next += 1;
            while (next <= paddedN)
            {
                temp[next - 1] -= 1;
                next += next & -next;
            }
        }
    }

    public static void DecodePermutation(int skip, int size, uint[] order, LjBitReader br)
    {
        LjAnsCode code = LjDecAns.DecodeHistograms(br, PermutationContexts, out byte[] contextMap);
        var reader = new LjAnsSymbolReader(code, br);
        ReadPermutation(skip, size, order, br, reader, contextMap);
        if (!reader.CheckAnsFinalState())
        {
            throw LjDec.Fail("Invalid ANS stream");
        }
    }

    /// <summary>ReadToc.</summary>
    public static void Read(int tocEntries, LjBitReader br, out uint[] sizes, out uint[]? permutation)
    {
        if (tocEntries > 65536)
        {
            throw LjDec.Fail("too many toc entries");
        }

        sizes = new uint[tocEntries];
        permutation = null;
        if (br.ReadBits(1) == 1)
        {
            permutation = new uint[tocEntries];
            DecodePermutation(0, tocEntries, permutation, br);
        }

        br.JumpToByteBoundary();
        for (int i = 0; i < tocEntries; ++i)
        {
            sizes[i] = LjDec.ReadU32(TocDist, br);
        }

        br.JumpToByteBoundary();
        if (!br.AllReadsWithinBounds)
        {
            throw LjDec.Fail("Not enough bytes for TOC");
        }
    }
}

/// <summary>ICCReader (icc_codec.cc): entropy-coded, predicted ICC profile.</summary>
internal static class LjIccDecoder
{
    private const int PreambleSize = 20;

    private static ulong DecodeVarInt(byte[] input, long inputSize, ref long pos)
    {
        ulong ret = 0;
        for (int i = 0; i < 9; ++i)
        {
            if (pos >= inputSize)
            {
                throw LjDec.Fail("DecodeVarInt: truncated input");
            }

            byte b = input[pos++];
            ret |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return ret;
            }
        }

        if (pos >= inputSize)
        {
            throw LjDec.Fail("DecodeVarInt: truncated input (10th byte)");
        }

        byte last = input[pos++];
        if ((last & 0x80) != 0 || (last & 0x7E) != 0)
        {
            throw LjDec.Fail("DecodeVarInt: value too large");
        }

        return ret | ((ulong)(last & 1) << 63);
    }

    private static void CheckIs32Bit(ulong v)
    {
        if (v > uint.MaxValue)
        {
            throw LjDec.Fail("32-bit value expected");
        }
    }

    private static void CheckOutOfBounds(ulong a, ulong b, ulong size)
    {
        ulong pos = a + b;
        if (pos > size || pos < a)
        {
            throw LjDec.Fail("Out of bounds");
        }
    }

    private static void CheckPreamble(byte[] enc, long size, ulong encSize)
    {
        long pos = 0;
        ulong osize = DecodeVarInt(enc, size, ref pos);
        CheckIs32Bit(osize);
        ulong csize = DecodeVarInt(enc, size, ref pos);
        CheckIs32Bit(csize);
        CheckOutOfBounds((ulong)pos, csize, (ulong)size);
        if (osize + 65536 < encSize)
        {
            throw LjDec.Fail("Malformed ICC");
        }

        if (osize > 1 << 28)
        {
            throw LjDec.Fail("Decoded ICC is too large");
        }
    }

    /// <summary>Reads the encoded profile from the codestream and returns the ICC bytes.</summary>
    public static byte[] Read(LjBitReader br)
    {
        ulong encSize = 0;
        new LjReadVisitor(br.AsReadFunc()).U64(0, ref encSize);
        if (encSize > 268435456)
        {
            throw LjDec.Fail("Too large encoded profile");
        }

        LjAnsCode code = LjDecAns.DecodeHistograms(br, LjIccEncoder.NumIccContexts, out byte[] contextMap);
        var reader = new LjAnsSymbolReader(code, br);
        var dec = new byte[encSize];
        for (long i = 0; i < (long)encSize; i++)
        {
            byte b1 = i > 0 ? dec[i - 1] : (byte)0;
            byte b2 = i > 1 ? dec[i - 2] : (byte)0;
            dec[i] = (byte)reader.ReadHybridUint(LjIccEncoder.IccAnsContext((int)Math.Min(i, int.MaxValue), b1, b2), br, contextMap);
            if (i == PreambleSize - 1 && encSize > PreambleSize)
            {
                CheckPreamble(dec, (long)Math.Min(0x400UL, encSize), encSize);
            }
        }

        if (!br.AllReadsWithinBounds)
        {
            throw LjDec.Fail("Not enough bytes for reading ICC profile");
        }

        if (!reader.CheckAnsFinalState())
        {
            throw LjDec.Fail("Corrupted ICC profile");
        }

        return Unpredict(dec, dec.Length);
    }

    private static void Shuffle(byte[] data, int start, int size, int width)
    {
        int height = (size + width - 1) / width;
        var result = new byte[size];
        int s = 0, j = 0;
        for (int i = 0; i < size; i++)
        {
            result[i] = data[start + j];
            j += height;
            if (j >= size)
            {
                j = ++s;
            }
        }

        Array.Copy(result, 0, data, start, size);
    }

    private sealed class Out
    {
        public byte[] Data;
        public int Size;

        public Out(int cap) => Data = new byte[Math.Max(cap, 16)];

        public void Push(byte b)
        {
            if (Size == Data.Length)
            {
                Array.Resize(ref Data, Data.Length * 2);
            }

            Data[Size++] = b;
        }

        public void Keyword(string k)
        {
            foreach (char ch in k)
            {
                Push((byte)ch);
            }
        }

        public void U32(ulong v)
        {
            Push((byte)(v >> 24));
            Push((byte)(v >> 16));
            Push((byte)(v >> 8));
            Push((byte)v);
        }
    }

    /// <summary>UnpredictICC.</summary>
    public static byte[] Unpredict(byte[] enc, long size)
    {
        long pos = 0;
        ulong osize = DecodeVarInt(enc, size, ref pos);
        CheckIs32Bit(osize);
        ulong csize = DecodeVarInt(enc, size, ref pos);
        CheckIs32Bit(csize);
        long cpos = pos;
        CheckOutOfBounds((ulong)pos, csize, (ulong)size);
        long commandsEnd = cpos + (long)csize;
        pos = commandsEnd;
        var result = new Out((int)Math.Min(osize, 1 << 28));
        var header = (byte[])LjIccEncoder.InitialHeaderPrediction.Clone();
        header[0] = (byte)(osize >> 24);
        header[1] = (byte)(osize >> 16);
        header[2] = (byte)(osize >> 8);
        header[3] = (byte)osize;
        const int hdr = LjIccEncoder.IccHeaderSize;
        for (int i = 0; i <= hdr; i++)
        {
            if ((ulong)result.Size == osize)
            {
                if (cpos != commandsEnd)
                {
                    throw LjDec.Fail("Not all commands used");
                }

                if (pos != size)
                {
                    throw LjDec.Fail("Not all data used");
                }

                return result.Data[..result.Size];
            }

            if (i == hdr)
            {
                break;
            }

            LjIccEncoder.ICCPredictHeader(result.Data, result.Size, header, i);
            if (pos >= size)
            {
                throw LjDec.Fail("Out of bounds");
            }

            result.Push((byte)(enc[pos++] + header[i]));
        }

        if (cpos >= commandsEnd)
        {
            throw LjDec.Fail("Out of bounds");
        }

        ulong numtags = DecodeVarInt(enc, commandsEnd, ref cpos);
        if (numtags != 0)
        {
            numtags--;
            CheckIs32Bit(numtags);
            result.U32(numtags);
            ulong prevtagstart = hdr + (numtags * 12);
            ulong prevtagsize = 0;
            while (true)
            {
                if ((ulong)result.Size > osize)
                {
                    throw LjDec.Fail("Invalid result size");
                }

                if (cpos > commandsEnd)
                {
                    throw LjDec.Fail("Out of bounds");
                }

                if (cpos == commandsEnd)
                {
                    break;
                }

                byte command = enc[cpos++];
                int tagcode = command & 63;
                string tag;
                if (tagcode == 0)
                {
                    break;
                }
                else if (tagcode == LjIccEncoder.CommandTagUnknown)
                {
                    CheckOutOfBounds((ulong)pos, 4, (ulong)size);
                    tag = LjIccEncoder.DecodeKeyword(enc, size, pos);
                    pos += 4;
                }
                else if (tagcode == LjIccEncoder.CommandTagTrc)
                {
                    tag = "rTRC";
                }
                else if (tagcode == LjIccEncoder.CommandTagXyz)
                {
                    tag = "rXYZ";
                }
                else
                {
                    if (tagcode - LjIccEncoder.CommandTagStringFirst >= LjIccEncoder.TagStrings.Length)
                    {
                        throw LjDec.Fail("Unknown tagcode");
                    }

                    tag = LjIccEncoder.TagStrings[tagcode - LjIccEncoder.CommandTagStringFirst];
                }

                result.Keyword(tag);
                ulong tagstart;
                ulong tagsize = prevtagsize;
                if (tag is "rXYZ" or "gXYZ" or "bXYZ" or "kXYZ" or "wtpt" or "bkpt" or "lumi")
                {
                    tagsize = 20;
                }

                if ((command & LjIccEncoder.FlagBitOffset) != 0)
                {
                    tagstart = DecodeVarInt(enc, commandsEnd, ref cpos);
                }
                else
                {
                    CheckIs32Bit(prevtagstart);
                    tagstart = prevtagstart + prevtagsize;
                }

                CheckIs32Bit(tagstart);
                result.U32(tagstart);
                if ((command & LjIccEncoder.FlagBitSize) != 0)
                {
                    tagsize = DecodeVarInt(enc, commandsEnd, ref cpos);
                }

                CheckIs32Bit(tagsize);
                result.U32(tagsize);
                prevtagstart = tagstart;
                prevtagsize = tagsize;
                if (tagcode == LjIccEncoder.CommandTagTrc)
                {
                    result.Keyword("gTRC");
                    result.U32(tagstart);
                    result.U32(tagsize);
                    result.Keyword("bTRC");
                    result.U32(tagstart);
                    result.U32(tagsize);
                }

                if (tagcode == LjIccEncoder.CommandTagXyz)
                {
                    CheckIs32Bit(tagstart + (tagsize * 2));
                    result.Keyword("gXYZ");
                    result.U32(tagstart + tagsize);
                    result.U32(tagsize);
                    result.Keyword("bXYZ");
                    result.U32(tagstart + (tagsize * 2));
                    result.U32(tagsize);
                }
            }
        }

        while (true)
        {
            if ((ulong)result.Size > osize)
            {
                throw LjDec.Fail("Invalid result size");
            }

            if (cpos > commandsEnd)
            {
                throw LjDec.Fail("Out of bounds");
            }

            if (cpos == commandsEnd)
            {
                break;
            }

            byte command = enc[cpos++];
            if (command == LjIccEncoder.CommandInsert)
            {
                ulong num = DecodeVarInt(enc, commandsEnd, ref cpos);
                CheckOutOfBounds((ulong)pos, num, (ulong)size);
                for (ulong i = 0; i < num; i++)
                {
                    result.Push(enc[pos++]);
                }
            }
            else if (command == LjIccEncoder.CommandShuffle2 || command == LjIccEncoder.CommandShuffle4)
            {
                ulong num = DecodeVarInt(enc, commandsEnd, ref cpos);
                CheckOutOfBounds((ulong)pos, num, (ulong)size);
                var shuffled = new byte[num];
                Array.Copy(enc, pos, shuffled, 0, (long)num);
                Shuffle(shuffled, 0, (int)num, command == LjIccEncoder.CommandShuffle2 ? 2 : 4);
                for (ulong i = 0; i < num; i++)
                {
                    result.Push(shuffled[i]);
                    pos++;
                }
            }
            else if (command == LjIccEncoder.CommandPredict)
            {
                CheckOutOfBounds((ulong)cpos, 2, (ulong)commandsEnd);
                byte flags = enc[cpos++];
                int width = (flags & 3) + 1;
                if (width == 3)
                {
                    throw LjDec.Fail("Invalid width");
                }

                int order = (flags & 12) >> 2;
                if (order == 3)
                {
                    throw LjDec.Fail("Invalid order");
                }

                ulong stride = (ulong)width;
                if ((flags & 16) != 0)
                {
                    stride = DecodeVarInt(enc, commandsEnd, ref cpos);
                    if (stride < (ulong)width)
                    {
                        throw LjDec.Fail("Invalid stride");
                    }
                }

                if (result.Size == 0 || ((ulong)(result.Size - 1) >> 2) < stride)
                {
                    throw LjDec.Fail("Invalid stride");
                }

                ulong num = DecodeVarInt(enc, commandsEnd, ref cpos);
                CheckOutOfBounds((ulong)pos, num, (ulong)size);
                var shuffled = new byte[num];
                Array.Copy(enc, pos, shuffled, 0, (long)num);
                if (width > 1)
                {
                    Shuffle(shuffled, 0, (int)num, width);
                }

                int start = result.Size;
                for (ulong i = 0; i < num; i++)
                {
                    // LinearPredictICCValue reads already-written output bytes only.
                    if (result.Size == result.Data.Length)
                    {
                        Array.Resize(ref result.Data, result.Data.Length * 2);
                    }

                    byte predicted = LjIccEncoder.LinearPredictICCValue(result.Data, start, (long)i, (long)stride, width, order);
                    result.Push((byte)(predicted + shuffled[i]));
                }

                pos += (long)num;
            }
            else if (command == LjIccEncoder.CommandXyz)
            {
                result.Keyword("XYZ ");
                for (int i = 0; i < 4; i++)
                {
                    result.Push(0);
                }

                CheckOutOfBounds((ulong)pos, 12, (ulong)size);
                for (int i = 0; i < 12; i++)
                {
                    result.Push(enc[pos++]);
                }
            }
            else if (command >= LjIccEncoder.CommandTypeStartFirst && command < LjIccEncoder.CommandTypeStartFirst + LjIccEncoder.TypeStrings.Length)
            {
                result.Keyword(LjIccEncoder.TypeStrings[command - LjIccEncoder.CommandTypeStartFirst]);
                for (int i = 0; i < 4; i++)
                {
                    result.Push(0);
                }
            }
            else
            {
                throw LjDec.Fail("Unknown command");
            }
        }

        if (pos != size)
        {
            throw LjDec.Fail("Not all data used");
        }

        if ((ulong)result.Size != osize)
        {
            throw LjDec.Fail("Invalid result size");
        }

        return result.Data[..result.Size];
    }
}
