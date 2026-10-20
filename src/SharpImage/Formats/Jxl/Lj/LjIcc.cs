// ICC profile compression for JPEG XL headers: libjxl 0.12 enc_icc_codec.cc (PredictICC, WriteICC) and
// icc_codec_common.cc, ported line by line.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjIccEncoder
{
    private const int IccHeaderSize = 128;
    private const int NumIccContexts = 41;
    private const uint SizeLimit = uint.MaxValue >> 2;

    private const byte CommandTagUnknown = 1, CommandTagTrc = 2, CommandTagXyz = 3, CommandTagStringFirst = 4;
    private const byte CommandInsert = 1, CommandShuffle2 = 2, CommandShuffle4 = 3, CommandPredict = 4, CommandXyz = 10, CommandTypeStartFirst = 16;
    private const byte FlagBitOffset = 64, FlagBitSize = 128;

    private static readonly string[] TagStrings =
        ["cprt", "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "kXYZ", "rTRC", "gTRC", "bTRC", "kTRC", "chad", "desc", "chrm", "dmnd", "dmdd", "lumi"];

    private static readonly string[] TypeStrings = ["XYZ ", "desc", "text", "mluc", "para", "curv", "sf32", "gbd "];

    private static readonly byte[] InitialHeaderPrediction =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, (byte)'m', (byte)'n', (byte)'t', (byte)'r',
        (byte)'R', (byte)'G', (byte)'B', (byte)' ', (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, (byte)'a', (byte)'c', (byte)'s', (byte)'p', 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 246, 214, 0, 1, 0, 0, 0, 0, 211, 45,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static uint DecodeUint32(byte[] data, long size, long pos) =>
        pos + 4 > size ? 0 : (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);

    private static string DecodeKeyword(byte[] data, long size, long pos) =>
        pos + 4 > size ? "    " : new string([(char)data[pos], (char)data[pos + 1], (char)data[pos + 2], (char)data[pos + 3]]);

    private static void EncodeVarInt(ulong value, List<byte> data)
    {
        while (value > 127)
        {
            data.Add((byte)((value & 127) | 128));
            value >>= 7;
        }

        data.Add((byte)(value & 127));
    }

    private static void ICCPredictHeader(byte[] icc, int size, byte[] header, int pos)
    {
        if (pos == 8 && size >= 8)
        {
            header[80] = icc[4];
            header[81] = icc[5];
            header[82] = icc[6];
            header[83] = icc[7];
        }

        if (pos == 41 && size >= 41)
        {
            if (icc[40] == 'A')
            {
                header[41] = (byte)'P';
                header[42] = (byte)'P';
                header[43] = (byte)'L';
            }

            if (icc[40] == 'M')
            {
                header[41] = (byte)'S';
                header[42] = (byte)'F';
                header[43] = (byte)'T';
            }
        }

        if (pos == 42 && size >= 42)
        {
            if (icc[40] == 'S' && icc[41] == 'G')
            {
                header[42] = (byte)'I';
                header[43] = (byte)' ';
            }

            if (icc[40] == 'S' && icc[41] == 'U')
            {
                header[42] = (byte)'N';
                header[43] = (byte)'W';
            }
        }
    }

    private static byte LinearPredictICCValue(byte[] data, long start, long i, long stride, long width, int order)
    {
        long pos = start + i;
        if (width == 1)
        {
            byte p1 = data[pos - stride], p2 = data[pos - (stride * 2)], p3 = data[pos - (stride * 3)];
            return (byte)(order == 0 ? p1 : order == 1 ? (2 * p1) - p2 : order == 2 ? (3 * p1) - (3 * p2) + p3 : 0);
        }

        if (width == 2)
        {
            long p = start + (i & ~1L);
            ushort p1 = (ushort)((data[p - stride] << 8) + data[p - stride + 1]);
            ushort p2 = (ushort)((data[p - (stride * 2)] << 8) + data[p - (stride * 2) + 1]);
            ushort p3 = (ushort)((data[p - (stride * 3)] << 8) + data[p - (stride * 3) + 1]);
            ushort pred = (ushort)(order == 0 ? p1 : order == 1 ? (2 * p1) - p2 : order == 2 ? (3 * p1) - (3 * p2) + p3 : 0);
            return (i & 1) != 0 ? (byte)(pred & 255) : (byte)((pred >> 8) & 255);
        }
        else
        {
            long p = start + (i & ~3L);
            uint p1 = DecodeUint32(data, pos, p - stride);
            uint p2 = DecodeUint32(data, pos, p - (stride * 2));
            uint p3 = DecodeUint32(data, pos, p - (stride * 3));
            uint pred = order == 0 ? p1 : order == 1 ? (2 * p1) - p2 : order == 2 ? (3 * p1) - (3 * p2) + p3 : 0;
            int shiftbytes = 3 - (int)(i & 3);
            return (byte)((pred >> (shiftbytes * 8)) & 255);
        }
    }

    private static void Unshuffle(List<byte> data, int start, int size, int width)
    {
        int height = (size + width - 1) / width;
        var result = new byte[size];
        int s = 0, j = 0;
        for (int i = 0; i < size; i++)
        {
            result[j] = data[start + i];
            j += height;
            if (j >= size)
            {
                j = ++s;
            }
        }

        for (int i = 0; i < size; i++)
        {
            data[start + i] = result[i];
        }
    }

    private static void PredictAndShuffle(long stride, long width, int order, long num, byte[] data, long size, ref long pos, List<byte> result)
    {
        if (pos + num > size)
        {
            throw new NotSupportedException("ICC: out of bounds");
        }

        if (pos == 0 || ((pos - 1) >> 2) < stride)
        {
            throw new NotSupportedException("ICC: invalid stride");
        }

        if (pos < stride * 4)
        {
            throw new NotSupportedException("ICC: too large stride");
        }

        int start = result.Count;
        for (long i = 0; i < num; i++)
        {
            byte predicted = LinearPredictICCValue(data, pos, i, stride, width, order);
            result.Add((byte)(data[pos + i] - predicted));
        }

        pos += num;
        if (width > 1)
        {
            Unshuffle(result, start, (int)num, (int)width);
        }
    }

    /// <summary>PredictICC: the transformed byte stream that WriteICC entropy-codes.</summary>
    public static List<byte> PredictIcc(byte[] icc)
    {
        int size = icc.Length;
        var result = new List<byte>();
        var commands = new List<byte>();
        var data = new List<byte>();
        if ((uint)size > SizeLimit)
        {
            throw new NotSupportedException("ICC profile is too large");
        }

        EncodeVarInt((ulong)size, result);
        var header = (byte[])InitialHeaderPrediction.Clone();
        header[0] = (byte)(size >> 24);
        header[1] = (byte)(size >> 16);
        header[2] = (byte)(size >> 8);
        header[3] = (byte)size;
        for (int i = 0; i < IccHeaderSize && i < size; i++)
        {
            ICCPredictHeader(icc, size, header, i);
            data.Add((byte)(icc[i] - header[i]));
        }

        if (size <= IccHeaderSize)
        {
            EncodeVarInt(0, result);
            result.AddRange(data);
            return result;
        }

        var tags = new List<string>();
        var tagstarts = new List<uint>();
        var tagsizes = new List<uint>();
        var tagmap = new Dictionary<long, int>();
        long pos = IccHeaderSize;
        if (pos + 4 <= size)
        {
            ulong numtags = DecodeUint32(icc, size, pos);
            pos += 4;
            EncodeVarInt(numtags + 1, commands);
            ulong prevtagstart = IccHeaderSize + (numtags * 12);
            uint prevtagsize = 0;
            for (ulong i = 0; i < numtags; i++)
            {
                if (pos + 12 > size)
                {
                    break;
                }

                string tag = DecodeKeyword(icc, size, pos);
                uint tagstart = DecodeUint32(icc, size, pos + 4);
                uint tagsize = DecodeUint32(icc, size, pos + 8);
                pos += 12;
                tags.Add(tag);
                tagstarts.Add(tagstart);
                tagsizes.Add(tagsize);
                tagmap[tagstart] = tags.Count - 1;
                byte tagcode = CommandTagUnknown;
                for (int j = 0; j < TagStrings.Length; j++)
                {
                    if (tag == TagStrings[j])
                    {
                        tagcode = (byte)(j + CommandTagStringFirst);
                        break;
                    }
                }

                if (tag == "rTRC" && pos + 24 < size)
                {
                    bool ok = DecodeKeyword(icc, size, pos) == "gTRC" && DecodeKeyword(icc, size, pos + 12) == "bTRC";
                    if (ok)
                    {
                        for (int kk = 0; kk < 8; kk++)
                        {
                            if (icc[pos - 8 + kk] != icc[pos + 4 + kk])
                            {
                                ok = false;
                            }

                            if (icc[pos - 8 + kk] != icc[pos + 16 + kk])
                            {
                                ok = false;
                            }
                        }
                    }

                    if (ok)
                    {
                        tagcode = CommandTagTrc;
                        pos += 24;
                        i += 2;
                    }
                }

                if (tag == "rXYZ" && pos + 24 < size)
                {
                    bool ok = DecodeKeyword(icc, size, pos) == "gXYZ" && DecodeKeyword(icc, size, pos + 12) == "bXYZ";
                    uint offsetr = tagstart;
                    uint offsetg = DecodeUint32(icc, size, pos + 4);
                    uint offsetb = DecodeUint32(icc, size, pos + 16);
                    uint sizer = tagsize;
                    uint sizeg = DecodeUint32(icc, size, pos + 8);
                    uint sizeb = DecodeUint32(icc, size, pos + 20);
                    ok &= sizer == 20;
                    ok &= sizeg == 20;
                    ok &= sizeb == 20;
                    ok &= offsetg == offsetr + 20;
                    ok &= offsetb == offsetr + 40;
                    if (ok)
                    {
                        tagcode = CommandTagXyz;
                        pos += 24;
                        i += 2;
                    }
                }

                byte command = tagcode;
                ulong predictedTagstart = prevtagstart + prevtagsize;
                if (predictedTagstart != tagstart)
                {
                    command |= FlagBitOffset;
                }

                uint predictedTagsize = prevtagsize;
                if (tag is "rXYZ" or "gXYZ" or "bXYZ" or "kXYZ" or "wtpt" or "bkpt" or "lumi")
                {
                    predictedTagsize = 20;
                }

                if (predictedTagsize != tagsize)
                {
                    command |= FlagBitSize;
                }

                commands.Add(command);
                if (tagcode == 1)
                {
                    foreach (char ch in tag)
                    {
                        data.Add((byte)ch);
                    }
                }

                if ((command & FlagBitOffset) != 0)
                {
                    EncodeVarInt(tagstart, commands);
                }

                if ((command & FlagBitSize) != 0)
                {
                    EncodeVarInt(tagsize, commands);
                }

                prevtagstart = tagstart;
                prevtagsize = tagsize;
            }
        }

        commands.Add(0);

        string curTag = "\0\0\0\0";
        long curTagstart = 0;
        long curTagsize = 0;
        long clutstart = 0;
        bool TagSane() => curTagsize > 8 && curTagsize < SizeLimit;
        long last0 = pos;
        while (pos <= size)
        {
            long last1 = pos;
            var commandsAdd = new List<byte>();
            var dataAdd = new List<byte>();
            if (pos > curTagstart + curTagsize && curTagsize < SizeLimit)
            {
                curTag = "\0\0\0\0";
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && tagmap.TryGetValue(pos, out int index) && pos + 4 <= size)
            {
                curTag = DecodeKeyword(icc, size, pos);
                curTagstart = tagstarts[index];
                curTagsize = tagsizes[index];
                if (curTag == "mluc" && TagSane() && pos + curTagsize <= size && icc[pos + 4] == 0 && icc[pos + 5] == 0 && icc[pos + 6] == 0 && icc[pos + 7] == 0)
                {
                    long num = curTagsize - 8;
                    commandsAdd.Add(CommandTypeStartFirst + 3);
                    pos += 8;
                    commandsAdd.Add(CommandShuffle2);
                    EncodeVarInt((ulong)num, commandsAdd);
                    int start = dataAdd.Count;
                    for (long k = 0; k < num; k++)
                    {
                        dataAdd.Add(icc[pos]);
                        pos++;
                    }

                    Unshuffle(dataAdd, start, (int)num, 2);
                }

                if (curTag == "curv" && TagSane() && pos + curTagsize <= size && icc[pos + 4] == 0 && icc[pos + 5] == 0 && icc[pos + 6] == 0 && icc[pos + 7] == 0)
                {
                    long num = curTagsize - 8;
                    if (num > 16 && num < (1 << 28) && pos + num <= size && pos > 0)
                    {
                        commandsAdd.Add(CommandTypeStartFirst + 5);
                        pos += 8;
                        commandsAdd.Add(CommandPredict);
                        const int order = 1, width = 2, stride = width;
                        commandsAdd.Add((order << 2) | (width - 1));
                        EncodeVarInt((ulong)num, commandsAdd);
                        PredictAndShuffle(stride, width, order, num, icc, size, ref pos, dataAdd);
                    }
                }
            }

            if (curTag == "mAB " || curTag == "mBA ")
            {
                string subTag = DecodeKeyword(icc, size, pos);
                if (pos + 12 < size && (subTag == "curv" || subTag == "vcgt") && DecodeUint32(icc, size, pos + 4) == 0)
                {
                    uint num = DecodeUint32(icc, size, pos + 8) * 2;
                    if (num > 16 && num < (1 << 28) && pos + 12 + num <= size)
                    {
                        pos += 12;
                        last1 = pos;
                        commandsAdd.Add(CommandPredict);
                        const int order = 1, width = 2, stride = width;
                        commandsAdd.Add((order << 2) | (width - 1));
                        EncodeVarInt(num, commandsAdd);
                        PredictAndShuffle(stride, width, order, num, icc, size, ref pos, dataAdd);
                    }
                }

                if (pos == curTagstart + 24 && pos + 4 < size)
                {
                    clutstart = curTagstart + DecodeUint32(icc, size, pos);
                }

                if (pos == clutstart && clutstart + 16 < size)
                {
                    long numi = icc[curTagstart + 8];
                    long numo = icc[curTagstart + 9];
                    long width = icc[clutstart + 16];
                    long stride = width * numo;
                    long num = width * numo;
                    for (long k = 0; k < numi && clutstart + k < size; k++)
                    {
                        num *= icc[clutstart + k];
                    }

                    if ((width == 1 || width == 2) && num > 64 && num < (1 << 28) && pos + num <= size && pos > stride * 4)
                    {
                        commandsAdd.Add(CommandPredict);
                        const int order = 1;
                        byte flags = (byte)((order << 2) | (width - 1) | (stride == width ? 0 : 16));
                        commandsAdd.Add(flags);
                        if ((flags & 16) != 0)
                        {
                            EncodeVarInt((ulong)stride, commandsAdd);
                        }

                        EncodeVarInt((ulong)num, commandsAdd);
                        PredictAndShuffle(stride, width, order, num, icc, size, ref pos, dataAdd);
                    }
                }
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && curTag == "gbd " && TagSane() && pos == curTagstart + 8 && pos + curTagsize - 8 <= size && pos > 16)
            {
                const long width = 4, stride = width;
                const int order = 0;
                long num = curTagsize - 8;
                byte flags = (byte)((order << 2) | (width - 1) | (stride == width ? 0 : 16));
                commandsAdd.Add(CommandPredict);
                commandsAdd.Add(flags);
                EncodeVarInt((ulong)num, commandsAdd);
                PredictAndShuffle(stride, width, order, num, icc, size, ref pos, dataAdd);
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && pos + 20 <= size)
            {
                string subTag = DecodeKeyword(icc, size, pos);
                if (subTag == "XYZ " && DecodeUint32(icc, size, pos + 4) == 0)
                {
                    commandsAdd.Add(CommandXyz);
                    pos += 8;
                    for (int j = 0; j < 12; j++)
                    {
                        dataAdd.Add(icc[pos++]);
                    }
                }
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0 && pos + 8 <= size)
            {
                if (DecodeUint32(icc, size, pos + 4) == 0)
                {
                    string subTag = DecodeKeyword(icc, size, pos);
                    for (int i = 0; i < TypeStrings.Length; i++)
                    {
                        if (subTag == TypeStrings[i])
                        {
                            commandsAdd.Add((byte)(CommandTypeStartFirst + i));
                            pos += 8;
                            break;
                        }
                    }
                }
            }

            if (!(commandsAdd.Count == 0 && dataAdd.Count == 0) || pos == size)
            {
                if (last0 < last1)
                {
                    commands.Add(CommandInsert);
                    EncodeVarInt((ulong)(last1 - last0), commands);
                    while (last0 < last1)
                    {
                        data.Add(icc[last0++]);
                    }
                }

                commands.AddRange(commandsAdd);
                data.AddRange(dataAdd);
                last0 = pos;
            }

            if (commandsAdd.Count == 0 && dataAdd.Count == 0)
            {
                pos++;
            }
        }

        EncodeVarInt((ulong)commands.Count, result);
        result.AddRange(commands);
        result.AddRange(data);
        return result;
    }

    private static int ByteKind1(byte b)
    {
        if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z'))
        {
            return 0;
        }

        if ((b >= '0' && b <= '9') || b == '.' || b == ',')
        {
            return 1;
        }

        if (b == 0)
        {
            return 2;
        }

        if (b == 1)
        {
            return 3;
        }

        if (b < 16)
        {
            return 4;
        }

        if (b == 255)
        {
            return 6;
        }

        if (b > 240)
        {
            return 5;
        }

        return 7;
    }

    private static int ByteKind2(byte b)
    {
        if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z'))
        {
            return 0;
        }

        if ((b >= '0' && b <= '9') || b == '.' || b == ',')
        {
            return 1;
        }

        if (b < 16)
        {
            return 2;
        }

        if (b > 240)
        {
            return 3;
        }

        return 4;
    }

    private static int IccAnsContext(int i, byte b1, byte b2) => i <= 128 ? 0 : 1 + ByteKind1(b1) + (ByteKind2(b2) * 8);

    /// <summary>WriteICC: U64 length of the predicted stream, then its tokens (prefix codes with LZ77).</summary>
    public static void WriteIcc(byte[] icc, LjBitWriter writer)
    {
        if (icc.Length == 0)
        {
            throw new NotSupportedException("ICC must be non-empty");
        }

        List<byte> enc = PredictIcc(icc);
        LjFields.WriteU64(writer, (ulong)enc.Count);
        var tokens = new List<List<LjToken>> { new(enc.Count) };
        for (int i = 0; i < enc.Count; i++)
        {
            int ctx = IccAnsContext(i, i > 0 ? enc[i - 1] : (byte)0, i > 1 ? enc[i - 2] : (byte)0);
            tokens[0].Add(new LjToken((uint)ctx, enc[i]));
        }

        var p = new LjHistogramParams
        {
            Lz77 = enc.Count < 16384 ? LjHistogramParams.Lz77Method.Optimal : LjHistogramParams.Lz77Method.Lz77,
            ForceHuffman = true,
        };
        var code = new LjEntropyCode();
        LjAns.BuildAndEncodeHistograms(p, NumIccContexts, ref tokens, code, writer);
        LjAns.WriteTokens(tokens[0], code, 0, writer);
    }
}
