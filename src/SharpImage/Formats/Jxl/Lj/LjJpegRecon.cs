// libjxl 0.12 JPEG reconstruction: jbrd decoding (jpeg/dec_jpeg_data.cc), metadata injection (jpeg_data.cc
// SetJPEGDataFromICC, decode_to_jpeg.cc SetExif/SetXmp), the VarDCT coefficient output of DecodeGroupImpl
// (dec_group.cc) and the JPEG bitstream writer (jpeg/dec_jpeg_data_writer.cc).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjJpegRecon
{
    private static readonly byte[] IccProfileTag = "ICC_PROFILE\0"u8.ToArray();
    private static readonly byte[] ExifTag = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpTag = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    /// <summary>DecodeJPEGData: the jbrd box contents.</summary>
    public static LjJpegData DecodeJbrd(byte[] encoded)
    {
        var jpg = new LjJpegData();
        var br = new LjBitReader(encoded);
        LjFields.ReadRaw(jpg, br);
        br.JumpToByteBoundary();
        if (!br.AllReadsWithinBounds)
        {
            throw LjDec.Fail("Truncated jbrd");
        }

        int start = (int)(br.TotalBitsConsumed / 8);
        byte[] plain;
        using (var ms = new MemoryStream(encoded, start, encoded.Length - start))
        using (var bs = new BrotliStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            bs.CopyTo(outMs);
            plain = outMs.ToArray();
        }

        int pos = 0;
        void Read(byte[] data)
        {
            if (pos + data.Length > plain.Length)
            {
                throw LjDec.Fail("Not enough decompressed output");
            }

            Array.Copy(plain, pos, data, 0, data.Length);
            pos += data.Length;
        }

        int numIcc = 0;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            byte[] marker = jpg.AppData[i];
            if (marker.Length < 3)
            {
                throw LjDec.Fail("APP marker too short");
            }

            if (jpg.AppMarkerType[i] != LjAppMarkerType.Unknown)
            {
                int sizeMinus1 = marker.Length - 1;
                marker[1] = (byte)(sizeMinus1 >> 8);
                marker[2] = (byte)(sizeMinus1 & 0xFF);
                if (jpg.AppMarkerType[i] == LjAppMarkerType.Icc)
                {
                    if (marker.Length < 17)
                    {
                        throw LjDec.Fail("ICC markers must be at least 17 bytes");
                    }

                    marker[0] = 0xE2;
                    IccProfileTag.CopyTo(marker, 3);
                    marker[15] = (byte)++numIcc;
                }
            }
            else
            {
                Read(marker);
                if ((marker[1] * 256) + marker[2] + 1 != marker.Length)
                {
                    throw LjDec.Fail("Incorrect marker size");
                }
            }
        }

        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            byte[] marker = jpg.AppData[i];
            if (jpg.AppMarkerType[i] == LjAppMarkerType.Icc)
            {
                marker[16] = (byte)numIcc;
            }

            if (jpg.AppMarkerType[i] == LjAppMarkerType.Exif)
            {
                if (marker.Length < 3 + ExifTag.Length)
                {
                    throw LjDec.Fail("Incorrect Exif marker size");
                }

                marker[0] = 0xE1;
                ExifTag.CopyTo(marker, 3);
            }

            if (jpg.AppMarkerType[i] == LjAppMarkerType.Xmp)
            {
                if (marker.Length < 3 + XmpTag.Length)
                {
                    throw LjDec.Fail("Incorrect XMP marker size");
                }

                marker[0] = 0xE1;
                XmpTag.CopyTo(marker, 3);
            }
        }

        foreach (byte[] marker in jpg.ComData)
        {
            Read(marker);
            if ((marker[1] * 256) + marker[2] + 1 != marker.Length)
            {
                throw LjDec.Fail("Incorrect marker size");
            }
        }

        foreach (byte[] data in jpg.InterMarkerData)
        {
            Read(data);
        }

        Read(jpg.TailData);
        if (pos != plain.Length)
        {
            throw LjDec.Fail("Excess data in compressed stream");
        }

        return jpg;
    }

    /// <summary>SetJPEGDataFromICC.</summary>
    public static void SetIcc(byte[]? icc, LjJpegData jpg)
    {
        icc ??= [];
        int iccPos = 0;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            if (jpg.AppMarkerType[i] != LjAppMarkerType.Icc)
            {
                continue;
            }

            byte[] m = jpg.AppData[i];
            if (m.Length < 17)
            {
                throw LjDec.Fail("ICC APP marker too small");
            }

            int len = m.Length - 17;
            if (iccPos + len > icc.Length)
            {
                throw LjDec.Fail("ICC length is less than APP markers");
            }

            Array.Copy(icc, iccPos, m, 17, len);
            iccPos += len;
        }

        if (iccPos != icc.Length && iccPos != 0)
        {
            throw LjDec.Fail("ICC length is more than APP markers");
        }
    }

    /// <summary>JxlToJpegDecoder::SetExif: <paramref name="data"/> is the Exif box content (4-byte offset + TIFF).</summary>
    public static void SetExif(byte[] data, LjJpegData jpg)
    {
        for (int i = 0; i < jpg.AppData.Count; ++i)
        {
            if (jpg.AppMarkerType[i] == LjAppMarkerType.Exif)
            {
                byte[] m = jpg.AppData[i];
                if (m.Length != data.Length + 3 + ExifTag.Length - 4)
                {
                    throw LjDec.Fail("Exif box size does not match the JPEG marker");
                }

                m[0] = 0xE1;
                ExifTag.CopyTo(m, 3);
                Array.Copy(data, 4, m, 3 + ExifTag.Length, data.Length - 4);
                return;
            }
        }

        throw LjDec.Fail("No Exif marker for the Exif box");
    }

    public static void SetXmp(byte[] data, LjJpegData jpg)
    {
        for (int i = 0; i < jpg.AppData.Count; ++i)
        {
            if (jpg.AppMarkerType[i] == LjAppMarkerType.Xmp)
            {
                byte[] m = jpg.AppData[i];
                if (m.Length != data.Length + 3 + XmpTag.Length)
                {
                    throw LjDec.Fail("XMP box size does not match the JPEG marker");
                }

                m[0] = 0xE1;
                XmpTag.CopyTo(m, 3);
                Array.Copy(data, 0, m, 3 + XmpTag.Length, data.Length);
                return;
            }
        }

        throw LjDec.Fail("No XMP marker for the xml box");
    }

    /// <summary>Container boxes by type (brob boxes decompressed to their inner type); jxlp parts concatenated as "jxlc".</summary>
    public static Dictionary<string, List<byte[]>> ParseBoxes(byte[] data)
    {
        var boxes = new Dictionary<string, List<byte[]>>();
        void Add(string t, byte[] c)
        {
            if (!boxes.TryGetValue(t, out List<byte[]>? l))
            {
                boxes[t] = l = new List<byte[]>();
            }

            l.Add(c);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x0A)
        {
            Add("jxlc", data);
            return boxes;
        }

        int pos = 0;
        var parts = new List<byte[]>();
        while (pos + 8 <= data.Length)
        {
            long boxLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int headerSize = 8;
            if (boxLen == 1 && pos + 16 <= data.Length)
            {
                headerSize = 16;
                boxLen = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
            }
            else if (boxLen == 0)
            {
                boxLen = data.Length - pos;
            }

            if (boxLen < headerSize || pos + boxLen > data.Length)
            {
                throw LjDec.Fail("Truncated JXL container box");
            }

            byte[] content = data.AsSpan(pos + headerSize, (int)boxLen - headerSize).ToArray();
            if (type == "jxlp")
            {
                parts.Add(content.AsSpan(4).ToArray());
            }
            else if (type == "brob" && content.Length >= 4)
            {
                string inner = System.Text.Encoding.ASCII.GetString(content, 0, 4);
                using var ms = new MemoryStream(content, 4, content.Length - 4);
                using var bs = new BrotliStream(ms, CompressionMode.Decompress);
                using var o = new MemoryStream();
                bs.CopyTo(o);
                Add(inner, o.ToArray());
            }
            else
            {
                Add(type, content);
            }

            pos += (int)boxLen;
        }

        if (parts.Count > 0)
        {
            var all = new MemoryStream();
            foreach (byte[] p in parts)
            {
                all.Write(p);
            }

            Add("jxlc", all.ToArray());
        }

        return boxes;
    }

    /// <summary>Rebuilds the original JPEG from a JPEG XL file with JPEG reconstruction data; null when it has none.</summary>
    public static byte[]? Reconstruct(byte[] file)
    {
        Dictionary<string, List<byte[]>> boxes = ParseBoxes(file);
        if (!boxes.TryGetValue("jbrd", out List<byte[]>? jbrd) || !boxes.TryGetValue("jxlc", out List<byte[]>? cs))
        {
            return null;
        }

        LjJpegData jpg = DecodeJbrd(jbrd[0]);
        LjDecoder.DecodeToJpeg(cs[0], jpg);
        if (boxes.TryGetValue("Exif", out List<byte[]>? exif) && NumMarkers(jpg, LjAppMarkerType.Exif) > 0)
        {
            SetExif(exif[0], jpg);
        }

        if (boxes.TryGetValue("xml ", out List<byte[]>? xmp) && NumMarkers(jpg, LjAppMarkerType.Xmp) > 0)
        {
            SetXmp(xmp[0], jpg);
        }

        return WriteJpeg(jpg);
    }

    private static int NumMarkers(LjJpegData jpg, LjAppMarkerType t)
    {
        int n = 0;
        foreach (LjAppMarkerType m in jpg.AppMarkerType)
        {
            if (m == t)
            {
                n++;
            }
        }

        return n;
    }

    // ====================================================================== JPEG writer

    private sealed class HuffTable
    {
        public sbyte[] Depth = new sbyte[256];
        public ushort[] Code = new ushort[256];
        public bool Initialized;
    }

    private sealed class BitWriter
    {
        private readonly MemoryStream o;
        private ulong putBuffer;
        private int putBits = 64;
        public bool Healthy = true;

        public BitWriter(MemoryStream o) => this.o = o;

        private void EmitByte(int b)
        {
            o.WriteByte((byte)b);
            if (b == 0xFF)
            {
                o.WriteByte(0);
            }
        }

        private void Discharge(ulong bits)
        {
            putBuffer |= bits >> -putBits;
            for (int s = 56; s >= 0; s -= 8)
            {
                EmitByte((int)((putBuffer >> s) & 0xFF));
            }

            putBits += 64;
            putBuffer = putBits >= 64 ? 0 : bits << putBits;
        }

        public void WriteBits(int nbits, ulong bits)
        {
            putBits -= nbits;
            if (putBits < 0)
            {
                if (nbits > 64)
                {
                    putBits += nbits;
                    Healthy = false;
                }
                else
                {
                    Discharge(bits);
                }
            }
            else
            {
                putBuffer |= bits << putBits;
            }
        }

        public void EmitMarker(int marker)
        {
            o.WriteByte(0xFF);
            o.WriteByte((byte)marker);
        }

        public bool JumpToByteBoundary(List<byte>? padBits, ref int padPos)
        {
            int nBits = putBits & 7;
            byte danglingBits = 0;
            byte padPattern;
            if (padBits == null)
            {
                padPattern = (byte)((1 << nBits) - 1);
            }
            else
            {
                padPattern = 0;
                while (nBits-- > 0)
                {
                    padPattern <<= 1;
                    if (padPos >= padBits.Count)
                    {
                        return false;
                    }

                    byte bit = padBits[padPos++];
                    danglingBits |= bit;
                    padPattern |= bit;
                }
            }

            if ((danglingBits & ~1) != 0)
            {
                return false;
            }

            while (putBits <= 56)
            {
                EmitByte((int)((putBuffer >> 56) & 0xFF));
                putBuffer <<= 8;
                putBits += 8;
            }

            if (putBits < 64)
            {
                int padMask = 0xFF >> (64 - putBits);
                int c = (int)(((putBuffer >> 56) & (ulong)~padMask) | padPattern);
                EmitByte(c);
            }

            putBuffer = 0;
            putBits = 64;
            return true;
        }
    }

    private sealed class CodingState
    {
        public int EobRun;
        public HuffTable? CurAcHuff;
        public List<ushort> RefinementBits = new();
        public int RefinementBitsCount;
    }

    private static void WriteSymbol(int symbol, HuffTable t, BitWriter bw) => bw.WriteBits(t.Depth[symbol], t.Code[symbol]);

    private static void Flush(CodingState s, BitWriter bw)
    {
        if (s.EobRun > 0)
        {
            int nbits = LjDec.FloorLog2Nonzero(s.EobRun);
            int symbol = nbits << 4;
            WriteSymbol(symbol, s.CurAcHuff!, bw);
            if (nbits > 0)
            {
                bw.WriteBits(nbits, (ulong)(s.EobRun & ((1 << nbits) - 1)));
            }

            s.EobRun = 0;
        }

        int numWords = s.RefinementBitsCount >> 4;
        for (int i = 0; i < numWords; ++i)
        {
            bw.WriteBits(16, s.RefinementBits[i]);
        }

        int tail = s.RefinementBitsCount & 0xF;
        if (tail != 0)
        {
            bw.WriteBits(tail, s.RefinementBits[^1]);
        }

        s.RefinementBits.Clear();
        s.RefinementBitsCount = 0;
    }

    private static void BufferEndOfBand(CodingState s, HuffTable acHuff, int[]? newBitsArray, int newBitsCount, BitWriter bw)
    {
        if (s.EobRun == 0)
        {
            s.CurAcHuff = acHuff;
        }

        ++s.EobRun;
        if (newBitsCount != 0)
        {
            ulong newBits = 0;
            for (int i = 0; i < newBitsCount; ++i)
            {
                newBits = (newBits << 1) | (uint)newBitsArray![i];
            }

            int tail = s.RefinementBitsCount & 0xF;
            if (tail != 0)
            {
                int stuffBitsCount = Math.Min(16 - tail, newBitsCount);
                ushort stuffBits = (ushort)(newBits >> (newBitsCount - stuffBitsCount));
                stuffBits &= (ushort)((1u << stuffBitsCount) - 1);
                s.RefinementBits[^1] = (ushort)((s.RefinementBits[^1] << stuffBitsCount) | stuffBits);
                newBitsCount -= stuffBitsCount;
                s.RefinementBitsCount += stuffBitsCount;
            }

            while (newBitsCount >= 16)
            {
                s.RefinementBits.Add((ushort)(newBits >> (newBitsCount - 16)));
                newBitsCount -= 16;
                s.RefinementBitsCount += 16;
            }

            if (newBitsCount != 0)
            {
                s.RefinementBits.Add((ushort)(newBits & ((1u << newBitsCount) - 1)));
                s.RefinementBitsCount += newBitsCount;
            }
        }

        if (s.EobRun == 0x7FFF)
        {
            Flush(s, bw);
        }
    }

    private static bool BuildHuffmanCodeTable(LjJpegHuffmanCode huff, HuffTable table)
    {
        var huffCode = new int[256];
        var huffSize = new uint[257];
        int p = 0;
        for (int l = 1; l <= 16; ++l)
        {
            int i = (int)huff.Counts[l];
            if (p + i > 256 + 1)
            {
                return false;
            }

            while (i-- > 0)
            {
                huffSize[p++] = (uint)l;
            }
        }

        if (p == 0)
        {
            return true;
        }

        int lastP = p - 1;
        huffSize[lastP] = 0;
        int code = 0;
        uint si = huffSize[0];
        p = 0;
        while (huffSize[p] != 0)
        {
            while (huffSize[p] == si)
            {
                huffCode[p++] = code;
                code++;
            }

            code <<= 1;
            si++;
        }

        for (p = 0; p < lastP; p++)
        {
            int i = (int)huff.Values[p];
            table.Depth[i] = (sbyte)huffSize[p];
            table.Code[i] = (ushort)huffCode[p];
        }

        return true;
    }

    private static bool EncodeDctBlockSequential(short[] coeffs, int co, HuffTable dcHuff, HuffTable acHuff, int numZeroRuns, ref short lastDc, BitWriter bw)
    {
        short temp2 = coeffs[co];
        short temp = (short)(temp2 - lastDc);
        lastDc = temp2;
        temp2 = (short)(temp >> 15);
        temp += temp2;
        temp2 ^= temp;
        int dcNbits = temp2 == 0 ? 0 : LjDec.FloorLog2Nonzero((uint)temp2) + 1;
        WriteSymbol(dcNbits, dcHuff, bw);
        if (dcNbits != 0)
        {
            bw.WriteBits(dcNbits, (ulong)(temp & ((1u << dcNbits) - 1)));
        }

        short litmus = 0;
        short r = 0;
        for (int i = 1; i < 64; i++)
        {
            temp = coeffs[co + LjJpegData.NaturalOrder[i]];
            if (temp == 0)
            {
                r++;
            }
            else
            {
                temp2 = (short)(temp >> 15);
                temp += temp2;
                temp2 ^= temp;
                if (r > 15)
                {
                    WriteSymbol(0xf0, acHuff, bw);
                    r -= 16;
                    if (r > 15)
                    {
                        WriteSymbol(0xf0, acHuff, bw);
                        r -= 16;
                    }

                    if (r > 15)
                    {
                        WriteSymbol(0xf0, acHuff, bw);
                        r -= 16;
                    }
                }

                litmus |= temp2;
                int acNbits = LjDec.FloorLog2Nonzero((ushort)temp2) + 1;
                int symbol = (r << 4) + acNbits;
                bw.WriteBits(acNbits + acHuff.Depth[symbol], (ulong)(temp & ((1 << acNbits) - 1)) | ((ulong)acHuff.Code[symbol] << acNbits));
                r = 0;
            }
        }

        for (int i = 0; i < numZeroRuns; ++i)
        {
            WriteSymbol(0xf0, acHuff, bw);
            r -= 16;
        }

        if (r > 0)
        {
            WriteSymbol(0, acHuff, bw);
        }

        return litmus >= 0;
    }

    private static bool EncodeDctBlockProgressive(short[] coeffs, int co, HuffTable dcHuff, HuffTable acHuff, int ss, int se, int al, int numZeroRuns,
        CodingState cs, ref short lastDc, BitWriter bw)
    {
        bool eobRunAllowed = ss > 0;
        short temp2, temp;
        if (ss == 0)
        {
            temp2 = (short)(coeffs[co] >> al);
            temp = (short)(temp2 - lastDc);
            lastDc = temp2;
            temp2 = temp;
            if (temp < 0)
            {
                temp = (short)-temp;
                if (temp < 0)
                {
                    return false;
                }

                temp2--;
            }

            int nbits = temp == 0 ? 0 : LjDec.FloorLog2Nonzero((uint)temp) + 1;
            WriteSymbol(nbits, dcHuff, bw);
            if (nbits != 0)
            {
                bw.WriteBits(nbits, (ulong)(temp2 & ((1 << nbits) - 1)));
            }

            ++ss;
        }

        if (ss > se)
        {
            return true;
        }

        int r = 0;
        for (int k = ss; k <= se; ++k)
        {
            temp = coeffs[co + LjJpegData.NaturalOrder[k]];
            if (temp == 0)
            {
                r++;
                continue;
            }

            if (temp < 0)
            {
                temp = (short)-temp;
                if (temp < 0)
                {
                    return false;
                }

                temp >>= al;
                temp2 = (short)~temp;
            }
            else
            {
                temp >>= al;
                temp2 = temp;
            }

            if (temp == 0)
            {
                r++;
                continue;
            }

            Flush(cs, bw);
            while (r > 15)
            {
                WriteSymbol(0xf0, acHuff, bw);
                r -= 16;
            }

            int nb = LjDec.FloorLog2Nonzero((uint)temp) + 1;
            int symbol = (r << 4) + nb;
            WriteSymbol(symbol, acHuff, bw);
            bw.WriteBits(nb, (ulong)(temp2 & ((1 << nb) - 1)));
            r = 0;
        }

        if (numZeroRuns > 0)
        {
            Flush(cs, bw);
            for (int i = 0; i < numZeroRuns; ++i)
            {
                WriteSymbol(0xf0, acHuff, bw);
                r -= 16;
            }
        }

        if (r > 0)
        {
            BufferEndOfBand(cs, acHuff, null, 0, bw);
            if (!eobRunAllowed)
            {
                Flush(cs, bw);
            }
        }

        return true;
    }

    private static bool EncodeRefinementBits(short[] coeffs, int co, HuffTable acHuff, int ss, int se, int al, CodingState cs, BitWriter bw)
    {
        bool eobRunAllowed = ss > 0;
        if (ss == 0)
        {
            bw.WriteBits(1, (ulong)((coeffs[co] >> al) & 1));
            ++ss;
        }

        if (ss > se)
        {
            return true;
        }

        var absValues = new int[64];
        int eob = 0;
        for (int k = ss; k <= se; k++)
        {
            int absVal = Math.Abs((int)coeffs[co + LjJpegData.NaturalOrder[k]]);
            absValues[k] = absVal >> al;
            if (absValues[k] == 1)
            {
                eob = k;
            }
        }

        int r = 0;
        var refinementBits = new int[64];
        int refinementBitsCount = 0;
        for (int k = ss; k <= se; k++)
        {
            if (absValues[k] == 0)
            {
                r++;
                continue;
            }

            while (r > 15 && k <= eob)
            {
                Flush(cs, bw);
                WriteSymbol(0xf0, acHuff, bw);
                r -= 16;
                for (int i = 0; i < refinementBitsCount; ++i)
                {
                    bw.WriteBits(1, (ulong)refinementBits[i]);
                }

                refinementBitsCount = 0;
            }

            if (absValues[k] > 1)
            {
                refinementBits[refinementBitsCount++] = absValues[k] & 1;
                continue;
            }

            Flush(cs, bw);
            int symbol = (r << 4) + 1;
            int newNonZeroBit = coeffs[co + LjJpegData.NaturalOrder[k]] < 0 ? 0 : 1;
            WriteSymbol(symbol, acHuff, bw);
            bw.WriteBits(1, (ulong)newNonZeroBit);
            for (int i = 0; i < refinementBitsCount; ++i)
            {
                bw.WriteBits(1, (ulong)refinementBits[i]);
            }

            refinementBitsCount = 0;
            r = 0;
        }

        if (r > 0 || refinementBitsCount != 0)
        {
            BufferEndOfBand(cs, acHuff, refinementBits, refinementBitsCount, bw);
            if (!eobRunAllowed)
            {
                Flush(cs, bw);
            }
        }

        return true;
    }

    /// <summary>WriteJpeg: serializes the reconstructed JPEG.</summary>
    public static byte[] WriteJpeg(LjJpegData jpg)
    {
        var o = new MemoryStream();
        var dcHuff = new HuffTable[4];
        var acHuff = new HuffTable[4];
        for (int i = 0; i < 4; i++)
        {
            dcHuff[i] = new HuffTable();
            acHuff[i] = new HuffTable();
        }

        if (jpg.MarkerOrder.Count == 0)
        {
            throw LjDec.Fail("JPEG serialization error");
        }

        List<byte>? padBits = jpg.HasZeroPaddingBit ? jpg.PaddingBits : null;
        int padPos = 0;
        int dhtIndex = 0, dqtIndex = 0, appIndex = 0, comIndex = 0, dataIndex = 0, scanIndex = 0;
        bool isProgressive = false, seenDri = false;
        o.WriteByte(0xFF);
        o.WriteByte(0xD8);
        foreach (byte marker in jpg.MarkerOrder)
        {
            switch (marker)
            {
                case 0xC0:
                case 0xC1:
                case 0xC2:
                case 0xC9:
                case 0xCA:
                {
                    if (marker <= 0xC2)
                    {
                        isProgressive = marker == 0xC2;
                    }

                    int n = jpg.Components.Count;
                    int len = 8 + (3 * n);
                    o.WriteByte(0xFF);
                    o.WriteByte(marker);
                    o.WriteByte((byte)(len >> 8));
                    o.WriteByte((byte)len);
                    o.WriteByte(8);
                    o.WriteByte((byte)(jpg.Height >> 8));
                    o.WriteByte((byte)jpg.Height);
                    o.WriteByte((byte)(jpg.Width >> 8));
                    o.WriteByte((byte)jpg.Width);
                    o.WriteByte((byte)n);
                    foreach (LjJpegComponent c in jpg.Components)
                    {
                        o.WriteByte((byte)c.Id);
                        o.WriteByte((byte)((c.HSampFactor << 4) | c.VSampFactor));
                        if (c.QuantIdx >= jpg.Quant.Count)
                        {
                            throw LjDec.Fail("JPEG serialization error");
                        }

                        o.WriteByte((byte)jpg.Quant[(int)c.QuantIdx].Index);
                    }

                    break;
                }

                case 0xC4:
                {
                    int len = 2;
                    for (int i = dhtIndex; i < jpg.HuffmanCode.Count; ++i)
                    {
                        LjJpegHuffmanCode h = jpg.HuffmanCode[i];
                        foreach (uint cnt in h.Counts)
                        {
                            len += (int)cnt;
                        }

                        if (len == 2)
                        {
                            break;
                        }

                        len += 16;
                        if (h.IsLast)
                        {
                            break;
                        }
                    }

                    o.WriteByte(0xFF);
                    o.WriteByte(0xC4);
                    o.WriteByte((byte)(len >> 8));
                    o.WriteByte((byte)len);
                    while (true)
                    {
                        int idx = dhtIndex++;
                        if (idx >= jpg.HuffmanCode.Count)
                        {
                            throw LjDec.Fail("JPEG serialization error");
                        }

                        LjJpegHuffmanCode h = jpg.HuffmanCode[idx];
                        int index = h.SlotId;
                        int totalCount = 0, maxLength = 0;
                        for (int i = 0; i < h.Counts.Length; ++i)
                        {
                            if (h.Counts[i] != 0)
                            {
                                maxLength = i;
                            }

                            totalCount += (int)h.Counts[i];
                        }

                        if (totalCount == 0)
                        {
                            break;
                        }

                        HuffTable t;
                        if ((index & 0x10) != 0)
                        {
                            index -= 0x10;
                            t = acHuff[index];
                        }
                        else
                        {
                            t = dcHuff[index];
                        }

                        Array.Fill(t.Depth, (sbyte)127);
                        if (!BuildHuffmanCodeTable(h, t))
                        {
                            throw LjDec.Fail("JPEG serialization error");
                        }

                        t.Initialized = true;
                        --totalCount;
                        o.WriteByte((byte)h.SlotId);
                        for (int i = 1; i <= 16; ++i)
                        {
                            o.WriteByte((byte)(i == maxLength ? h.Counts[i] - 1 : h.Counts[i]));
                        }

                        for (int i = 0; i < totalCount; ++i)
                        {
                            o.WriteByte((byte)h.Values[i]);
                        }

                        if (h.IsLast)
                        {
                            break;
                        }
                    }

                    break;
                }

                case >= 0xD0 and <= 0xD7:
                    o.WriteByte(0xFF);
                    o.WriteByte(marker);
                    break;
                case 0xD9:
                    o.WriteByte(0xFF);
                    o.WriteByte(0xD9);
                    o.Write(jpg.TailData);
                    break;
                case 0xDA:
                    EncodeScan(jpg, jpg.ScanInfo[scanIndex++], isProgressive, seenDri, dcHuff, acHuff, o, padBits, ref padPos);
                    break;
                case 0xDB:
                {
                    int len = 2;
                    for (int i = dqtIndex; i < jpg.Quant.Count; ++i)
                    {
                        len += 1 + ((jpg.Quant[i].Precision != 0 ? 2 : 1) * 64);
                        if (jpg.Quant[i].IsLast)
                        {
                            break;
                        }
                    }

                    o.WriteByte(0xFF);
                    o.WriteByte(0xDB);
                    o.WriteByte((byte)(len >> 8));
                    o.WriteByte((byte)len);
                    while (true)
                    {
                        int idx = dqtIndex++;
                        if (idx >= jpg.Quant.Count)
                        {
                            throw LjDec.Fail("JPEG serialization error");
                        }

                        LjJpegQuantTable t = jpg.Quant[idx];
                        o.WriteByte((byte)((t.Precision << 4) + t.Index));
                        for (int i = 0; i < 64; ++i)
                        {
                            int val = t.Values[LjJpegData.NaturalOrder[i]];
                            if (t.Precision != 0)
                            {
                                o.WriteByte((byte)(val >> 8));
                            }

                            o.WriteByte((byte)val);
                        }

                        if (t.IsLast)
                        {
                            break;
                        }
                    }

                    break;
                }

                case 0xDD:
                    seenDri = true;
                    o.Write([0xFF, 0xDD, 0, 4, (byte)(jpg.RestartInterval >> 8), (byte)jpg.RestartInterval]);
                    break;
                case >= 0xE0 and <= 0xEF:
                    if (appIndex >= jpg.AppData.Count)
                    {
                        throw LjDec.Fail("JPEG serialization error");
                    }

                    o.WriteByte(0xFF);
                    o.Write(jpg.AppData[appIndex++]);
                    break;
                case 0xFE:
                    if (comIndex >= jpg.ComData.Count)
                    {
                        throw LjDec.Fail("JPEG serialization error");
                    }

                    o.WriteByte(0xFF);
                    o.Write(jpg.ComData[comIndex++]);
                    break;
                case 0xFF:
                    if (dataIndex >= jpg.InterMarkerData.Count)
                    {
                        throw LjDec.Fail("JPEG serialization error");
                    }

                    o.Write(jpg.InterMarkerData[dataIndex++]);
                    break;
                default:
                    throw LjDec.Fail("Failed to encode marker");
            }
        }

        if (padBits != null && padPos != padBits.Count)
        {
            throw LjDec.Fail("Invalid number of padding bits.");
        }

        return o.ToArray();
    }

    private static void EncodeScan(LjJpegData jpg, LjJpegScanInfo scan, bool isProgressive, bool seenDri, HuffTable[] dcHuff, HuffTable[] acHuff,
        MemoryStream o, List<byte>? padBits, ref int padPos)
    {
        int al = isProgressive ? (int)scan.Al : 0;
        int ah = isProgressive ? (int)scan.Ah : 0;
        int ss = isProgressive ? (int)scan.Ss : 0;
        int se = isProgressive ? (int)scan.Se : 63;
        int mode = !isProgressive || (ah == 0 && al == 0 && ss == 0 && se == 63) ? 0 : ah == 0 ? 1 : 2;

        // EncodeSOS.
        int nScans = (int)scan.NumComponents;
        int len = 6 + (2 * nScans);
        o.WriteByte(0xFF);
        o.WriteByte(0xDA);
        o.WriteByte((byte)(len >> 8));
        o.WriteByte((byte)len);
        o.WriteByte((byte)nScans);
        for (int i = 0; i < nScans; ++i)
        {
            LjJpegComponentScanInfo si = scan.Components[i];
            if (si.CompIdx >= jpg.Components.Count)
            {
                throw LjDec.Fail("JPEG serialization error");
            }

            o.WriteByte((byte)jpg.Components[(int)si.CompIdx].Id);
            o.WriteByte((byte)((si.DcTblIdx << 4) + si.AcTblIdx));
        }

        o.WriteByte((byte)scan.Ss);
        o.WriteByte((byte)scan.Se);
        o.WriteByte((byte)((scan.Ah << 4) | scan.Al));

        int restartInterval = seenDri ? (int)jpg.RestartInterval : 0;
        var bw = new BitWriter(o);
        var cs = new CodingState();
        int restartsToGo = restartInterval;
        int nextRestartMarker = 0;
        int blockScanIndex = 0;
        int extraZeroRunsPos = 0;
        int NextExtraZeroRunIndex() => extraZeroRunsPos < scan.ExtraZeroRuns.Count ? (int)scan.ExtraZeroRuns[extraZeroRunsPos].BlockIdx : -1;
        int nextResetPointPos = 0;
        int NextResetPoint() => nextResetPointPos < scan.ResetPoints.Count ? (int)scan.ResetPoints[nextResetPointPos++] : -1;
        int nextExtraZeroRunIndex = NextExtraZeroRunIndex();
        int nextResetPoint = NextResetPoint();
        var lastDc = new short[4];
        bool interleaved = scan.NumComponents > 1;
        jpg.CalculateMcuSize(scan, out int mcusPerRow, out int mcuRows);
        bool wantAc = ss != 0 || se != 0;
        bool wantDc = ss == 0;
        for (int mcuY = 0; mcuY < mcuRows; ++mcuY)
        {
            for (int mcuX = 0; mcuX < mcusPerRow; ++mcuX)
            {
                if (restartInterval > 0 && restartsToGo == 0)
                {
                    Flush(cs, bw);
                    if (!bw.JumpToByteBoundary(padBits, ref padPos))
                    {
                        throw LjDec.Fail("JPEG serialization error");
                    }

                    bw.EmitMarker(0xD0 + nextRestartMarker);
                    nextRestartMarker = (nextRestartMarker + 1) & 7;
                    restartsToGo = restartInterval;
                    Array.Clear(lastDc);
                }

                for (int i = 0; i < scan.NumComponents; ++i)
                {
                    LjJpegComponentScanInfo si = scan.Components[i];
                    LjJpegComponent c = jpg.Components[(int)si.CompIdx];
                    HuffTable dch = dcHuff[si.DcTblIdx];
                    HuffTable ach = acHuff[si.AcTblIdx];
                    if ((wantDc && !dch.Initialized) || (wantAc && !ach.Initialized))
                    {
                        throw LjDec.Fail("JPEG serialization error: missing Huffman table");
                    }

                    int nBlocksY = interleaved ? c.VSampFactor : 1;
                    int nBlocksX = interleaved ? c.HSampFactor : 1;
                    for (int iy = 0; iy < nBlocksY; ++iy)
                    {
                        for (int ix = 0; ix < nBlocksX; ++ix)
                        {
                            int blockY = (mcuY * nBlocksY) + iy;
                            int blockX = (mcuX * nBlocksX) + ix;
                            long blockIdx = ((long)blockY * c.WidthInBlocks) + blockX;
                            if (blockScanIndex == nextResetPoint)
                            {
                                Flush(cs, bw);
                                nextResetPoint = NextResetPoint();
                            }

                            int numZeroRuns = 0;
                            if (blockScanIndex == nextExtraZeroRunIndex)
                            {
                                numZeroRuns = (int)scan.ExtraZeroRuns[extraZeroRunsPos].NumExtraZeroRuns;
                                ++extraZeroRunsPos;
                                nextExtraZeroRunIndex = NextExtraZeroRunIndex();
                            }

                            int co = (int)(blockIdx << 6);
                            bool ok = mode switch
                            {
                                0 => EncodeDctBlockSequential(c.Coeffs, co, dch, ach, numZeroRuns, ref lastDc[si.CompIdx], bw),
                                1 => EncodeDctBlockProgressive(c.Coeffs, co, dch, ach, ss, se, al, numZeroRuns, cs, ref lastDc[si.CompIdx], bw),
                                _ => EncodeRefinementBits(c.Coeffs, co, ach, ss, se, al, cs, bw),
                            };
                            if (!ok)
                            {
                                throw LjDec.Fail("JPEG serialization error");
                            }

                            ++blockScanIndex;
                        }
                    }
                }

                --restartsToGo;
            }
        }

        Flush(cs, bw);
        if (!bw.JumpToByteBoundary(padBits, ref padPos))
        {
            throw LjDec.Fail("JPEG serialization error");
        }

        if (!bw.Healthy)
        {
            throw LjDec.Fail("JPEG serialization error");
        }
    }
}

internal sealed partial class LjFrameDecoder
{
    private int[] jpegCMap = [0, 1, 2];
    private bool jpegIsGray;
    private int[] jpegDcoff = new int[3];
    private int[] scaledQtable = new int[64 * 3];
    private sbyte currentYtox, currentYtob;

    private void InitJpegOutput()
    {
        if (Fh.Encoding == LjFrameHeader.EncModular)
        {
            throw LjDec.Fail("Cannot output JPEG from Modular");
        }

        LjJpegData jd = JpegData!;
        int numComponents = jd.Components.Count;
        if (numComponents != 1 && numComponents != 3)
        {
            throw LjDec.Fail("Invalid number of components");
        }

        if (st.Meta.XybEncoded)
        {
            throw LjDec.Fail("Cannot decode to JPEG an XYB image");
        }

        jpegIsGray = numComponents == 1;
        jpegCMap = jpegIsGray ? [0, 0, 0] : [1, 0, 2];   // JpegOrder(kYCbCr) for the initial component sizing
        jd.Width = Fd.Xsize;
        jd.Height = Fd.Ysize;
        for (int c = 0; c < numComponents; c++)
        {
            LjJpegComponent comp = jd.Components[jpegCMap[c]];
            comp.WidthInBlocks = (uint)(Fd.XsizeBlocks >> Fh.ChromaSubsampling.HShift(c));
            comp.HeightInBlocks = (uint)(Fd.YsizeBlocks >> Fh.ChromaSubsampling.VShift(c));
            comp.HSampFactor = 1 << Fh.ChromaSubsampling.RawHShift(c);
            comp.VSampFactor = 1 << Fh.ChromaSubsampling.RawVShift(c);
            comp.Coeffs = new short[(long)comp.WidthInBlocks * comp.HeightInBlocks * 64];
        }
    }

    private int[] JpegQtable()
    {
        LjQuantEncoding qe = matrices.Encodings[0];
        if (qe.Mode != LjQuantEncoding.ModeRaw || MathF.Abs(qe.QTableDen - (1.0f / (8 * 255))) > 1e-8f || qe.QTable == null || qe.QTable.Length != 3 * 64)
        {
            throw LjDec.Fail("Quantization table is not a JPEG quantization table.");
        }

        return qe.QTable;
    }

    private void SetJpegQuantTables()
    {
        LjJpegData jd = JpegData!;
        int[] qtable = JpegQtable();
        int numComponents = jd.Components.Count;
        bool isGray = numComponents == 1;
        if (Fh.ColorTransform == LjFrameHeader.CtXyb)
        {
            throw LjDec.Fail("XYB JPEG");
        }

        jpegCMap = isGray ? [0, 0, 0] : Fh.ColorTransform == LjFrameHeader.CtYCbCr ? [1, 0, 2] : [0, 1, 2];
        uint qtSet = 0;
        for (int c = 0; c < numComponents; c++)
        {
            int quantC = isGray ? 1 : c;
            int qpos = (int)jd.Components[jpegCMap[c]].QuantIdx;
            if (qpos >= jd.Quant.Count)
            {
                throw LjDec.Fail("Invalid quant index");
            }

            qtSet |= 1u << qpos;
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    jd.Quant[qpos].Values[(x * 8) + y] = qtable[(quantC * 64) + (y * 8) + x];
                }
            }
        }

        for (int i = 0; i < jd.Quant.Count; i++)
        {
            if ((qtSet & (1u << i)) != 0)
            {
                continue;
            }

            if (i == 0)
            {
                throw LjDec.Fail("First quant table unused.");
            }

            Array.Copy(jd.Quant[i - 1].Values, jd.Quant[i].Values, 64);
        }

        // DecodeGroupImpl's per-frame JPEG setup.
        if (!cmap.IsJpegCompatible)
        {
            throw LjDec.Fail("The CfL map is not JPEG-compatible");
        }

        for (int c = 0; c < 3; c++)
        {
            jpegDcoff[c] = Fh.ColorTransform == LjFrameHeader.CtNone ? 1024 / qtable[64 * c] : 0;
            for (int i = 0; i < 64; i++)
            {
                int num = qtable[64 + i];
                int den = qtable[(64 * c) + i];
                if (num <= 0 || den <= 0 || num >= 65536 || den >= 65536)
                {
                    throw LjDec.Fail("Invalid JPEG quantization table");
                }

                scaledQtable[(64 * c) + ((i % 8) * 8) + (i / 8)] = (1 << 11) * num / den;
            }
        }
    }

    private void WriteJpegBlock(int raw, int bx, int by, int[] sbx, int[] sby, int[] hshift, int[] vshift, LjRect[] r, int absTx, int[] qblock, int size)
    {
        if (raw != LjDecTransforms.Dct)
        {
            throw LjDec.Fail("Can only decode to JPEG if only DCT-8 is used.");
        }

        LjJpegData jd = JpegData!;
        bool is444 = Fh.ChromaSubsampling.Is444;
        var transposedDctY = new int[64];
        sbyte cmX = 0, cmB = 0;
        foreach (int c in new[] { 1, 0, 2 })
        {
            if (jpegIsGray && c != 1)
            {
                continue;
            }

            if ((sbx[c] << hshift[c]) != bx || (sby[c] << vshift[c]) != by)
            {
                continue;
            }

            LjJpegComponent comp = jd.Components[jpegCMap[c]];
            int jpegPos = (int)(((comp.WidthInBlocks * (long)(r[c].Y0 + sby[c])) + r[c].X0 + sbx[c]) * 64);
            int qb = c * size;
            for (int x = 0; x < 8; x++)
            {
                for (int y = x + 1; y < 8; y++)
                {
                    (qblock[qb + (y * 8) + x], qblock[qb + (x * 8) + y]) = (qblock[qb + (x * 8) + y], qblock[qb + (y * 8) + x]);
                }
            }

            cmX = currentYtox;
            cmB = currentYtob;
            if (!is444 || (cmX == 0 && cmB == 0))
            {
                for (int i = 0; i < 64; i++)
                {
                    comp.Coeffs[jpegPos + i] = Sat16(qblock[qb + i]);
                }
            }
            else if (c == 1)
            {
                for (int i = 0; i < 64; i++)
                {
                    transposedDctY[i] = qblock[qb + i];
                    comp.Coeffs[jpegPos + i] = Sat16(qblock[qb + i]);
                }
            }
            else
            {
                int scale = LjColorCorrelation.RatioJpeg(c == 0 ? cmX : cmB);
                const int round = 1 << 10;
                for (int i = 0; i < 64; i++)
                {
                    int qt = scaledQtable[(c * size) + i];
                    int coeffScale = ((qt * scale) + round) >> 11;
                    int cflFactor = ((transposedDctY[i] * coeffScale) + round) >> 11;
                    comp.Coeffs[jpegPos + i] = Sat16(qblock[qb + i] + cflFactor);
                }
            }

            LjImageF dcp = dc.Planes[c];
            float dcv = dcp.Data[dcp.Row(r[c].Y0 + sby[c]) + r[c].X0 + sbx[c]] - jpegDcoff[c];
            comp.Coeffs[jpegPos] = (short)Math.Clamp(dcv, -2047f, 2047f);
            for (int i = 0; i < 64; i++)
            {
                if (comp.Coeffs[jpegPos + i] > 4095 || comp.Coeffs[jpegPos + i] < -4095)
                {
                    throw LjDec.Fail("JPEG DCT coefficients out of range");
                }
            }
        }
    }

    private static short Sat16(int v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
}
