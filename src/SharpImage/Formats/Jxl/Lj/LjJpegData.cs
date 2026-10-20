// JPEG bitstream model used for lossless JPEG recompression: libjxl's JPEGData (jpeg_data.h/.cc), the JPEG parser
// (enc_jpeg_data_reader.cc, enc_jpeg_huffman_decode.cc) and the jbrd box serialization (jpeg_data.cc VisitFields,
// enc_jpeg_data.cc EncodeJPEGData). Everything a decoder needs to rebuild the original JPEG byte for byte.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjJpegQuantTable
{
    public int[] Values = new int[64];
    public uint Precision;
    public uint Index;
    public bool IsLast = true;
}

internal sealed class LjJpegHuffmanCode
{
    public uint[] Counts = new uint[17];
    public uint[] Values = new uint[257];
    public int SlotId;
    public bool IsLast = true;
}

internal struct LjJpegComponentScanInfo
{
    public uint CompIdx, DcTblIdx, AcTblIdx;
}

internal sealed class LjJpegScanInfo
{
    public uint Ss, Se, Ah, Al;
    public uint NumComponents;
    public LjJpegComponentScanInfo[] Components = new LjJpegComponentScanInfo[4];
    public uint LastNeededPass;
    public List<uint> ResetPoints = new();
    public List<(uint BlockIdx, uint NumExtraZeroRuns)> ExtraZeroRuns = new();
}

internal sealed class LjJpegComponent
{
    public uint Id;
    public int HSampFactor = 1, VSampFactor = 1;
    public uint QuantIdx;
    public uint WidthInBlocks, HeightInBlocks;
    public short[] Coeffs = Array.Empty<short>();
}

internal enum LjAppMarkerType : uint
{
    Unknown = 0,
    Icc = 1,
    Exif = 2,
    Xmp = 3,
}

internal sealed class LjJpegData : ILjBundle
{
    public int Width, Height;
    public uint RestartInterval;
    public List<byte[]> AppData = new();
    public List<LjAppMarkerType> AppMarkerType = new();
    public List<byte[]> ComData = new();
    public List<LjJpegQuantTable> Quant = new();
    public List<LjJpegHuffmanCode> HuffmanCode = new();
    public List<LjJpegComponent> Components = new();
    public List<LjJpegScanInfo> ScanInfo = new();
    public List<byte> MarkerOrder = new();
    public List<byte[]> InterMarkerData = new();
    public byte[] TailData = Array.Empty<byte>();
    public bool HasZeroPaddingBit;
    public List<byte> PaddingBits = new();

    public static readonly int[] NaturalOrder =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    ];

    public static readonly int[] ZigZagOrder =
    [
        0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42,
        3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53,
        10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60,
        21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
    ];

    public void CalculateMcuSize(LjJpegScanInfo scan, out int mcusPerRow, out int mcuRows)
    {
        bool interleaved = scan.NumComponents > 1;
        LjJpegComponent bc = Components[(int)scan.Components[0].CompIdx];
        int hGroup = interleaved ? 1 : bc.HSampFactor;
        int vGroup = interleaved ? 1 : bc.VSampFactor;
        int maxH = 1, maxV = 1;
        foreach (LjJpegComponent c in Components)
        {
            maxH = Math.Max(c.HSampFactor, maxH);
            maxV = Math.Max(c.VSampFactor, maxV);
        }

        mcusPerRow = LjMath.DivCeil(Width * hGroup, 8 * maxH);
        mcuRows = LjMath.DivCeil(Height * vGroup, 8 * maxV);
    }

    // ---- jbrd serialization (JPEGData::VisitFields) ----

    public void VisitFields(LjVisitor v)
    {
        bool isGray = Components.Count == 1;
        v.Bool(false, ref isGray);
        if (v.IsReading)
        {
            Resize(Components, isGray ? 1 : 3);
        }

        int numApp = 0, numCom = 0, numScans = 0, numIntermarker = 0;
        bool hasDri = false;
        void VisitMarker(ref byte marker)
        {
            uint m32 = (uint)(marker - 0xc0);
            v.Bits(6, 0x00, ref m32);
            marker = (byte)(m32 + 0xc0);
            if ((marker & 0xf0) == 0xe0)
            {
                numApp++;
            }

            if (marker == 0xfe)
            {
                numCom++;
            }

            if (marker == 0xda)
            {
                numScans++;
            }

            if (marker == 0xff)
            {
                numIntermarker++;
            }

            if (marker == 0xdd)
            {
                hasDri = true;
            }
        }

        if (v.IsReading)
        {
            byte marker = 0xc0;
            do
            {
                VisitMarker(ref marker);
                MarkerOrder.Add(marker);
                if (MarkerOrder.Count > 16384)
                {
                    throw new InvalidDataException("Too many markers");
                }
            }
            while (marker != 0xd9);
        }
        else
        {
            for (int i = 0; i < MarkerOrder.Count; i++)
            {
                byte m = MarkerOrder[i];
                VisitMarker(ref m);
            }
        }

        if (numScans == 0)
        {
            throw new InvalidDataException("JPEG: no scans");
        }

        if (v.IsReading)
        {
            Resize(AppData, numApp, () => Array.Empty<byte>());
            Resize(AppMarkerType, numApp, () => LjAppMarkerType.Unknown);
            Resize(ComData, numCom, () => Array.Empty<byte>());
            Resize(ScanInfo, numScans);
        }

        for (int i = 0; i < AppData.Count; i++)
        {
            uint t = (uint)AppMarkerType[i];
            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.BitsOffset(1, 2), LjU32.BitsOffset(2, 4), 0, ref t);
            if (t > 3)
            {
                throw new InvalidDataException("Unknown app marker type");
            }

            AppMarkerType[i] = (LjAppMarkerType)t;
            uint len = (uint)(AppData[i].Length - 1);
            v.Bits(16, 0, ref len);
            if (v.IsReading)
            {
                AppData[i] = new byte[len + 1];
            }
        }

        for (int i = 0; i < ComData.Count; i++)
        {
            uint len = (uint)(ComData[i].Length - 1);
            v.Bits(16, 0, ref len);
            if (v.IsReading)
            {
                ComData[i] = new byte[len + 1];
            }
        }

        uint numQuant = (uint)Quant.Count;
        v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), LjU32.Val(4), 2, ref numQuant);
        if (numQuant == 4)
        {
            throw new InvalidDataException("Invalid number of quant tables");
        }

        if (v.IsReading)
        {
            Resize(Quant, (int)numQuant);
        }

        for (int i = 0; i < numQuant; i++)
        {
            v.Bits(1, 0, ref Quant[i].Precision);
            v.Bits(2, (uint)i, ref Quant[i].Index);
            v.Bool(true, ref Quant[i].IsLast);
        }

        // component type: 0 gray, 1 YCbCr, 2 RGB, 3 custom
        uint componentType =
            Components.Count == 1 && Components[0].Id == 1 ? 0u
            : Components.Count == 3 && Components[0].Id == 1 && Components[1].Id == 2 && Components[2].Id == 3 ? 1u
            : Components.Count == 3 && Components[0].Id == 'R' && Components[1].Id == 'G' && Components[2].Id == 'B' ? 2u
            : 3u;
        v.Bits(2, 1, ref componentType);
        uint numComponents;
        if (componentType == 0)
        {
            numComponents = 1;
        }
        else if (componentType != 3)
        {
            numComponents = 3;
        }
        else
        {
            numComponents = (uint)Components.Count;
            v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), LjU32.Val(4), 3, ref numComponents);
            if (numComponents != 1 && numComponents != 3)
            {
                throw new InvalidDataException("Invalid number of components");
            }
        }

        if (v.IsReading)
        {
            Resize(Components, (int)numComponents);
        }

        if (componentType == 3)
        {
            foreach (LjJpegComponent c in Components)
            {
                v.Bits(8, 0, ref c.Id);
            }
        }
        else if (componentType == 0)
        {
            Components[0].Id = 1;
        }
        else if (componentType == 2)
        {
            Components[0].Id = 'R';
            Components[1].Id = 'G';
            Components[2].Id = 'B';
        }
        else
        {
            Components[0].Id = 1;
            Components[1].Id = 2;
            Components[2].Id = 3;
        }

        int usedTables = 0;
        foreach (LjJpegComponent c in Components)
        {
            v.Bits(2, 0, ref c.QuantIdx);
            if (c.QuantIdx >= Quant.Count)
            {
                throw new InvalidDataException("Invalid quant table for component");
            }

            usedTables |= 1 << (int)c.QuantIdx;
        }

        for (int i = 0; i < Quant.Count; i++)
        {
            if ((usedTables & (1 << i)) != 0)
            {
                continue;
            }

            if (i == 0)
            {
                throw new InvalidDataException("First quant table unused.");
            }

            for (int j = 0; j < 64; j++)
            {
                if (Quant[i].Values[j] != Quant[i - 1].Values[j])
                {
                    throw new InvalidDataException("Non-trivial unused quant table");
                }
            }
        }

        uint numHuff = (uint)HuffmanCode.Count;
        v.U32(LjU32.Val(4), LjU32.BitsOffset(3, 2), LjU32.BitsOffset(4, 10), LjU32.BitsOffset(6, 26), 4, ref numHuff);
        if (v.IsReading)
        {
            Resize(HuffmanCode, (int)numHuff);
        }

        foreach (LjJpegHuffmanCode hc in HuffmanCode)
        {
            bool isAc = (hc.SlotId >> 4) != 0;
            uint id = (uint)(hc.SlotId & 0xF);
            v.Bool(false, ref isAc);
            v.Bits(2, 0, ref id);
            hc.SlotId = ((isAc ? 1 : 0) << 4) | (int)id;
            v.Bool(true, ref hc.IsLast);
            int numSymbols = 0;
            for (int i = 0; i <= 16; i++)
            {
                v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.BitsOffset(3, 2), LjU32.BitsOnly(8), 0, ref hc.Counts[i]);
                numSymbols += (int)hc.Counts[i];
            }

            if (numSymbols == 0)
            {
                continue;
            }

            if (numSymbols > hc.Values.Length)
            {
                throw new InvalidDataException("Huffman code too large");
            }

            for (int i = 0; i < numSymbols; i++)
            {
                v.U32(LjU32.BitsOnly(2), LjU32.BitsOffset(2, 4), LjU32.BitsOffset(4, 8), LjU32.BitsOffset(8, 1), 0, ref hc.Values[i]);
            }

            if (hc.Values[numSymbols - 1] != 256)
            {
                throw new InvalidDataException("Missing EOI symbol");
            }
        }

        foreach (LjJpegScanInfo scan in ScanInfo)
        {
            v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), LjU32.Val(4), 1, ref scan.NumComponents);
            if (scan.NumComponents >= 4)
            {
                throw new InvalidDataException("Invalid number of components in SOS marker");
            }

            v.Bits(6, 0, ref scan.Ss);
            v.Bits(6, 63, ref scan.Se);
            v.Bits(4, 0, ref scan.Al);
            v.Bits(4, 0, ref scan.Ah);
            for (int i = 0; i < scan.NumComponents; i++)
            {
                v.Bits(2, 0, ref scan.Components[i].CompIdx);
                if (scan.Components[i].CompIdx >= Components.Count)
                {
                    throw new InvalidDataException("Invalid component idx in SOS marker");
                }

                v.Bits(2, 0, ref scan.Components[i].AcTblIdx);
                v.Bits(2, 0, ref scan.Components[i].DcTblIdx);
            }

            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.Val(2), LjU32.BitsOffset(3, 3), 10, ref scan.LastNeededPass);
        }

        if (hasDri)
        {
            v.Bits(16, 0, ref RestartInterval);
        }

        foreach (LjJpegScanInfo scan in ScanInfo)
        {
            uint numResetPoints = (uint)scan.ResetPoints.Count;
            v.U32(LjU32.Val(0), LjU32.BitsOffset(2, 1), LjU32.BitsOffset(4, 4), LjU32.BitsOffset(16, 20), 0, ref numResetPoints);
            if (v.IsReading)
            {
                scan.ResetPoints = new List<uint>(new uint[numResetPoints]);
            }

            int lastBlockIdx = -1;
            for (int k = 0; k < scan.ResetPoints.Count; k++)
            {
                uint blockIdx = (uint)(scan.ResetPoints[k] - (lastBlockIdx + 1));
                v.U32(LjU32.Val(0), LjU32.BitsOffset(3, 1), LjU32.BitsOffset(5, 9), LjU32.BitsOffset(28, 41), 0, ref blockIdx);
                blockIdx = (uint)(blockIdx + lastBlockIdx + 1);
                if (blockIdx >= 3u << 26)
                {
                    throw new InvalidDataException("Invalid block ID");
                }

                scan.ResetPoints[k] = blockIdx;
                lastBlockIdx = (int)blockIdx;
            }

            uint numExtraZeroRuns = (uint)scan.ExtraZeroRuns.Count;
            v.U32(LjU32.Val(0), LjU32.BitsOffset(2, 1), LjU32.BitsOffset(4, 4), LjU32.BitsOffset(16, 20), 0, ref numExtraZeroRuns);
            if (v.IsReading)
            {
                scan.ExtraZeroRuns = new List<(uint, uint)>(new (uint, uint)[numExtraZeroRuns]);
            }

            lastBlockIdx = -1;
            for (int k = 0; k < scan.ExtraZeroRuns.Count; k++)
            {
                (uint blockIdx, uint runs) = scan.ExtraZeroRuns[k];
                v.U32(LjU32.Val(1), LjU32.BitsOffset(2, 2), LjU32.BitsOffset(4, 5), LjU32.BitsOffset(8, 20), 1, ref runs);
                blockIdx = (uint)(blockIdx - (lastBlockIdx + 1));
                v.U32(LjU32.Val(0), LjU32.BitsOffset(3, 1), LjU32.BitsOffset(5, 9), LjU32.BitsOffset(28, 41), 0, ref blockIdx);
                blockIdx = (uint)(blockIdx + lastBlockIdx + 1);
                if (runs > 4)
                {
                    throw new InvalidDataException("Invalid number of extra zero runs");
                }

                if (blockIdx > 3u << 26)
                {
                    throw new InvalidDataException("Invalid block ID");
                }

                scan.ExtraZeroRuns[k] = (blockIdx, runs);
                lastBlockIdx = (int)blockIdx;
            }
        }

        var interMarkerSizes = new List<uint>();
        for (int i = 0; i < numIntermarker; i++)
        {
            uint len = v.IsReading ? 0 : (uint)InterMarkerData[i].Length;
            v.Bits(16, 0, ref len);
            if (v.IsReading)
            {
                interMarkerSizes.Add(len);
            }
        }

        uint tailLen = (uint)TailData.Length;
        if (!v.IsReading && tailLen > 4260096)
        {
            throw new InvalidDataException("Tail data too large");
        }

        v.U32(LjU32.Val(0), LjU32.BitsOffset(8, 1), LjU32.BitsOffset(16, 257), LjU32.BitsOffset(22, 65793), 0, ref tailLen);
        v.Bool(false, ref HasZeroPaddingBit);
        if (HasZeroPaddingBit)
        {
            uint nbit = (uint)PaddingBits.Count;
            v.Bits(24, 0, ref nbit);
            if (v.IsReading)
            {
                PaddingBits = new List<byte>((int)Math.Min(1024u, nbit));
                for (uint i = 0; i < nbit; i++)
                {
                    bool b = false;
                    v.Bool(false, ref b);
                    PaddingBits.Add(b ? (byte)1 : (byte)0);
                }
            }
            else
            {
                for (int i = 0; i < PaddingBits.Count; i++)
                {
                    bool b = PaddingBits[i] != 0;
                    v.Bool(false, ref b);
                }
            }
        }

        ValidateTableUse();

        if (v.IsReading)
        {
            TailData = new byte[tailLen];
            InterMarkerData = new List<byte[]>();
            foreach (uint s in interMarkerSizes)
            {
                InterMarkerData.Add(new byte[s]);
            }
        }
    }

    private void ValidateTableUse()
    {
        int dhtIndex = 0, scanIndex = 0;
        bool isProgressive = false;
        var acOk = new bool[4];
        var dcOk = new bool[4];
        foreach (byte marker in MarkerOrder)
        {
            if (marker == 0xC2)
            {
                isProgressive = true;
            }
            else if (marker == 0xC4)
            {
                while (dhtIndex < HuffmanCode.Count)
                {
                    LjJpegHuffmanCode huff = HuffmanCode[dhtIndex++];
                    int index = huff.SlotId;
                    if ((index & 0x10) != 0)
                    {
                        acOk[index - 0x10] = true;
                    }
                    else
                    {
                        dcOk[index] = true;
                    }

                    if (huff.IsLast)
                    {
                        break;
                    }
                }
            }
            else if (marker == 0xDA)
            {
                LjJpegScanInfo si = ScanInfo[scanIndex++];
                for (int i = 0; i < si.NumComponents; i++)
                {
                    LjJpegComponentScanInfo csi = si.Components[i];
                    bool wantDc = !isProgressive || si.Ss == 0;
                    if (wantDc && !dcOk[csi.DcTblIdx])
                    {
                        throw new InvalidDataException("DC Huffman table used before defined");
                    }

                    bool wantAc = !isProgressive || si.Ss != 0 || si.Se != 0;
                    if (wantAc && !acOk[csi.AcTblIdx])
                    {
                        throw new InvalidDataException("AC Huffman table used before defined");
                    }
                }
            }
        }
    }

    private static void Resize<T>(List<T> list, int n) where T : new()
    {
        while (list.Count > n)
        {
            list.RemoveAt(list.Count - 1);
        }

        while (list.Count < n)
        {
            list.Add(new T());
        }
    }

    private static void Resize<T>(List<T> list, int n, Func<T> make)
    {
        while (list.Count > n)
        {
            list.RemoveAt(list.Count - 1);
        }

        while (list.Count < n)
        {
            list.Add(make());
        }
    }
}

/// <summary>libjxl's JPEG parser (ReadJpeg in kReadAll mode).</summary>
internal static class LjJpegReader
{
    private const int MaxDimPixels = 65535;
    private const int BrunsliMaxSampling = 15;
    private const int HuffmanRootTableBits = 8;
    private const int HuffmanLutSize = 1024;

    private struct HuffmanTableEntry
    {
        public byte Bits;
        public ushort Value;
    }

    private static HuffmanTableEntry[] NewLut(int n)
    {
        var t = new HuffmanTableEntry[n];
        for (int i = 0; i < n; i++)
        {
            t[i].Value = 0xffff;
        }

        return t;
    }

    private static Exception Fail(string m) => new InvalidDataException("JPEG: " + m);

    private static void VerifyLen(int pos, int n, int len)
    {
        if (pos + n > len)
        {
            throw Fail("Unexpected end of input");
        }
    }

    private static int ReadUint8(byte[] d, ref int pos) => d[pos++];

    private static int ReadUint16(byte[] d, ref int pos)
    {
        int v = (d[pos] << 8) + d[pos + 1];
        pos += 2;
        return v;
    }

    private static void ProcessSof(byte[] data, int len, ref int pos, LjJpegData jpg)
    {
        if (jpg.Width != 0)
        {
            throw Fail("Duplicate SOF marker.");
        }

        int startPos = pos;
        VerifyLen(pos, 8, len);
        int markerLen = ReadUint16(data, ref pos);
        int precision = ReadUint8(data, ref pos);
        int height = ReadUint16(data, ref pos);
        int width = ReadUint16(data, ref pos);
        int numComponents = ReadUint8(data, ref pos);
        if (precision != 8 || height < 1 || height > MaxDimPixels || width < 1 || width > MaxDimPixels || numComponents < 1 || numComponents > 4)
        {
            throw Fail("Unsupported SOF parameters");
        }

        VerifyLen(pos, 3 * numComponents, len);
        jpg.Height = height;
        jpg.Width = width;
        jpg.Components.Clear();
        var idsSeen = new bool[256];
        int maxH = 1, maxV = 1;
        for (int i = 0; i < numComponents; i++)
        {
            var c = new LjJpegComponent();
            int id = ReadUint8(data, ref pos);
            if (idsSeen[id])
            {
                throw Fail("Duplicate ID in SOF.");
            }

            idsSeen[id] = true;
            c.Id = (uint)id;
            int factor = ReadUint8(data, ref pos);
            int h = factor >> 4, v = factor & 0xf;
            if (h < 1 || h > BrunsliMaxSampling || v < 1 || v > BrunsliMaxSampling)
            {
                throw Fail("Invalid sampling factor");
            }

            c.HSampFactor = h;
            c.VSampFactor = v;
            c.QuantIdx = (uint)ReadUint8(data, ref pos);
            maxH = Math.Max(maxH, h);
            maxV = Math.Max(maxV, v);
            jpg.Components.Add(c);
        }

        int mcuRows = LjMath.DivCeil(jpg.Height, maxV * 8);
        int mcuCols = LjMath.DivCeil(jpg.Width, maxH * 8);
        foreach (LjJpegComponent c in jpg.Components)
        {
            if (maxH % c.HSampFactor != 0 || maxV % c.VSampFactor != 0)
            {
                throw Fail("Non-integral subsampling ratios.");
            }

            c.WidthInBlocks = (uint)(mcuCols * c.HSampFactor);
            c.HeightInBlocks = (uint)(mcuRows * c.VSampFactor);
        }

        if (startPos + markerLen != pos)
        {
            throw Fail("Invalid marker length");
        }
    }

    private static void ProcessSos(byte[] data, int len, ref int pos, LjJpegData jpg)
    {
        int startPos = pos;
        VerifyLen(pos, 3, len);
        int markerLen = ReadUint16(data, ref pos);
        int compsInScan = ReadUint8(data, ref pos);
        if (compsInScan < 1 || compsInScan > jpg.Components.Count)
        {
            throw Fail("Invalid COMPS_IN_SCAN");
        }

        var si = new LjJpegScanInfo { NumComponents = (uint)compsInScan };
        VerifyLen(pos, 2 * compsInScan, len);
        var idsSeen = new bool[256];
        for (int i = 0; i < compsInScan; i++)
        {
            int id = ReadUint8(data, ref pos);
            if (idsSeen[id])
            {
                throw Fail("Duplicate ID in SOS.");
            }

            idsSeen[id] = true;
            bool found = false;
            for (int j = 0; j < jpg.Components.Count; j++)
            {
                if (jpg.Components[j].Id == id)
                {
                    si.Components[i].CompIdx = (uint)j;
                    found = true;
                }
            }

            if (!found)
            {
                throw Fail("SOS marker: Could not find component");
            }

            int c = ReadUint8(data, ref pos);
            int dc = c >> 4, ac = c & 0xf;
            if (dc > 3 || ac > 3)
            {
                throw Fail("Invalid HUFFMAN_INDEX");
            }

            si.Components[i].DcTblIdx = (uint)dc;
            si.Components[i].AcTblIdx = (uint)ac;
        }

        VerifyLen(pos, 3, len);
        si.Ss = (uint)ReadUint8(data, ref pos);
        si.Se = (uint)ReadUint8(data, ref pos);
        if (si.Ss > 63 || si.Se < si.Ss || si.Se > 63)
        {
            throw Fail("Invalid scan bounds");
        }

        int cc = ReadUint8(data, ref pos);
        si.Ah = (uint)(cc >> 4);
        si.Al = (uint)(cc & 0xf);
        for (int i = 0; i < compsInScan; i++)
        {
            bool foundDc = false, foundAc = false;
            foreach (LjJpegHuffmanCode code in jpg.HuffmanCode)
            {
                int slot = code.SlotId;
                if (slot == si.Components[i].DcTblIdx)
                {
                    foundDc = true;
                }
                else if (slot == si.Components[i].AcTblIdx + 16)
                {
                    foundAc = true;
                }
            }

            if (si.Ss == 0 && !foundDc)
            {
                throw Fail("SOS marker: Could not find DC Huffman table");
            }

            if (si.Se > 0 && !foundAc)
            {
                throw Fail("SOS marker: Could not find AC Huffman table");
            }
        }

        jpg.ScanInfo.Add(si);
        if (startPos + markerLen != pos)
        {
            throw Fail("Invalid marker length");
        }
    }

    private static int NextTableBitSize(int[] count, int len)
    {
        int left = 1 << (len - HuffmanRootTableBits);
        while (len < 16)
        {
            left -= count[len];
            if (left <= 0)
            {
                break;
            }

            ++len;
            left <<= 1;
        }

        return len - HuffmanRootTableBits;
    }

    private static void BuildJpegHuffmanTable(uint[] count, uint[] symbols, HuffmanTableEntry[] lut, int lutOffset)
    {
        var tmp = new int[17];
        int totalCount = 0;
        for (int len = 1; len <= 16; len++)
        {
            tmp[len] = (int)count[len];
            totalCount += tmp[len];
        }

        int table = lutOffset;
        int tableBits = HuffmanRootTableBits;
        int tableSize = 1 << tableBits;
        HuffmanTableEntry code;
        if (totalCount == 1)
        {
            code.Bits = 0;
            code.Value = (ushort)symbols[0];
            for (int key = 0; key < tableSize; key++)
            {
                lut[table + key] = code;
            }

            return;
        }

        int k = 0, idx = 0;
        for (int len = 1; len <= HuffmanRootTableBits; len++)
        {
            for (; tmp[len] > 0; --tmp[len])
            {
                code.Bits = (byte)len;
                code.Value = (ushort)symbols[idx++];
                int reps = 1 << (HuffmanRootTableBits - len);
                while (reps-- > 0)
                {
                    lut[table + k++] = code;
                }
            }
        }

        table += tableSize;
        tableSize = 0;
        int low = 0;
        for (int len = HuffmanRootTableBits + 1; len <= 16; len++)
        {
            for (; tmp[len] > 0; --tmp[len])
            {
                if (low >= tableSize)
                {
                    table += tableSize;
                    tableBits = NextTableBitSize(tmp, len);
                    tableSize = 1 << tableBits;
                    low = 0;
                    lut[lutOffset + k].Bits = (byte)(tableBits + HuffmanRootTableBits);
                    lut[lutOffset + k].Value = (ushort)(table - lutOffset - k);
                    ++k;
                }

                code.Bits = (byte)(len - HuffmanRootTableBits);
                code.Value = (ushort)symbols[idx++];
                int reps = 1 << (tableBits - code.Bits);
                while (reps-- > 0)
                {
                    lut[table + low++] = code;
                }
            }
        }
    }

    private static void ProcessDht(byte[] data, int len, HuffmanTableEntry[] dcLut, HuffmanTableEntry[] acLut, ref int pos, LjJpegData jpg)
    {
        int startPos = pos;
        VerifyLen(pos, 2, len);
        int markerLen = ReadUint16(data, ref pos);
        if (markerLen == 2)
        {
            jpg.HuffmanCode.Add(new LjJpegHuffmanCode { IsLast = true });
            return;
        }

        while (pos < startPos + markerLen)
        {
            VerifyLen(pos, 1 + 16, len);
            var huff = new LjJpegHuffmanCode { SlotId = ReadUint8(data, ref pos) };
            int huffmanIndex = huff.SlotId;
            bool isAc = (huff.SlotId & 0x10) != 0;
            HuffmanTableEntry[] lut;
            if (isAc)
            {
                huffmanIndex -= 0x10;
                lut = acLut;
            }
            else
            {
                lut = dcLut;
            }

            if (huffmanIndex < 0 || huffmanIndex > 3)
            {
                throw Fail("Invalid HUFFMAN_INDEX");
            }

            int lutOffset = huffmanIndex * HuffmanLutSize;
            huff.Counts[0] = 0;
            int totalCount = 0;
            int space = 1 << 16;
            int maxDepth = 1;
            for (int i = 1; i <= 16; i++)
            {
                int count = ReadUint8(data, ref pos);
                if (count != 0)
                {
                    maxDepth = i;
                }

                huff.Counts[i] = (uint)count;
                totalCount += count;
                space -= count * (1 << (16 - i));
            }

            if (totalCount > (isAc ? 256 : 12))
            {
                throw Fail("Invalid HUFFMAN_CODE");
            }

            VerifyLen(pos, totalCount, len);
            var valuesSeen = new bool[256];
            for (int i = 0; i < totalCount; i++)
            {
                int value = ReadUint8(data, ref pos);
                if (!isAc && value > 11)
                {
                    throw Fail("Invalid HUFFMAN_CODE");
                }

                if (valuesSeen[value])
                {
                    throw Fail("Duplicate Huffman code value");
                }

                valuesSeen[value] = true;
                huff.Values[i] = (uint)value;
            }

            ++huff.Counts[maxDepth];
            huff.Values[totalCount] = 256;
            space -= 1 << (16 - maxDepth);
            if (space < 0)
            {
                throw Fail("Invalid Huffman code lengths.");
            }
            else if (space > 0 && lut[lutOffset].Value != 0xffff)
            {
                for (int i = 0; i < HuffmanLutSize; i++)
                {
                    lut[lutOffset + i].Bits = 0;
                    lut[lutOffset + i].Value = 0xffff;
                }
            }

            huff.IsLast = pos == startPos + markerLen;
            BuildJpegHuffmanTable(huff.Counts, huff.Values, lut, lutOffset);
            jpg.HuffmanCode.Add(huff);
        }

        if (startPos + markerLen != pos)
        {
            throw Fail("Invalid marker length");
        }
    }

    private static void ProcessDqt(byte[] data, int len, ref int pos, LjJpegData jpg)
    {
        int startPos = pos;
        VerifyLen(pos, 2, len);
        int markerLen = ReadUint16(data, ref pos);
        if (markerLen == 2)
        {
            throw Fail("DQT marker: no quantization table found");
        }

        while (pos < startPos + markerLen && jpg.Quant.Count < 4)
        {
            VerifyLen(pos, 1, len);
            int qti = ReadUint8(data, ref pos);
            int precision = qti >> 4;
            if (precision > 1)
            {
                throw Fail("Invalid QUANT_TBL_PRECISION");
            }

            qti &= 0xf;
            if (qti > 3)
            {
                throw Fail("Invalid QUANT_TBL_INDEX");
            }

            VerifyLen(pos, (precision + 1) * 64, len);
            var table = new LjJpegQuantTable { Index = (uint)qti, Precision = (uint)precision };
            for (int i = 0; i < 64; i++)
            {
                int q = precision != 0 ? ReadUint16(data, ref pos) : ReadUint8(data, ref pos);
                if (q < 1)
                {
                    throw Fail("Invalid QUANT_VAL");
                }

                table.Values[LjJpegData.NaturalOrder[i]] = q;
            }

            table.IsLast = pos == startPos + markerLen;
            jpg.Quant.Add(table);
        }

        if (startPos + markerLen != pos)
        {
            throw Fail("Invalid marker length");
        }
    }

    private static void ProcessDri(byte[] data, int len, ref int pos, ref bool foundDri, LjJpegData jpg)
    {
        if (foundDri)
        {
            throw Fail("Duplicate DRI marker.");
        }

        foundDri = true;
        int startPos = pos;
        VerifyLen(pos, 4, len);
        int markerLen = ReadUint16(data, ref pos);
        jpg.RestartInterval = (uint)ReadUint16(data, ref pos);
        if (startPos + markerLen != pos)
        {
            throw Fail("Invalid marker length");
        }
    }

    private static byte[] ProcessMarkerData(byte[] data, int len, ref int pos)
    {
        VerifyLen(pos, 2, len);
        int markerLen = ReadUint16(data, ref pos);
        if (markerLen < 2)
        {
            throw Fail("Invalid MARKER_LEN");
        }

        VerifyLen(pos, markerLen - 2, len);
        var s = new byte[markerLen + 1];
        Array.Copy(data, pos - 3, s, 0, markerLen + 1);
        pos += markerLen - 2;
        return s;
    }

    private sealed class BitReaderState
    {
        private readonly byte[] data;
        private readonly int len;
        public int Pos;
        public ulong Val;
        public int BitsLeft;
        public int NextMarkerPos;

        public BitReaderState(byte[] data, int len, int pos)
        {
            this.data = data;
            this.len = len;
            Reset(pos);
        }

        public void Reset(int pos)
        {
            Pos = pos;
            Val = 0;
            BitsLeft = 0;
            NextMarkerPos = len - 2;
            FillBitWindow();
        }

        private byte GetNextByte()
        {
            if (Pos >= NextMarkerPos)
            {
                ++Pos;
                return 0;
            }

            byte c = data[Pos++];
            if (c == 0xff)
            {
                byte escape = data[Pos];
                if (escape == 0)
                {
                    ++Pos;
                }
                else
                {
                    NextMarkerPos = Pos - 1;
                }
            }

            return c;
        }

        public void FillBitWindow()
        {
            if (BitsLeft <= 16)
            {
                while (BitsLeft <= 56)
                {
                    Val <<= 8;
                    Val |= GetNextByte();
                    BitsLeft += 8;
                }
            }
        }

        public int ReadBits(int nbits)
        {
            FillBitWindow();
            ulong v = (Val >> (BitsLeft - nbits)) & ((1UL << nbits) - 1);
            BitsLeft -= nbits;
            return (int)v;
        }

        public bool FinishStream(LjJpegData jpg, out int pos)
        {
            int npadbits = BitsLeft & 7;
            if (npadbits > 0)
            {
                ulong padmask = (1UL << npadbits) - 1;
                ulong padbits = (Val >> (BitsLeft - npadbits)) & padmask;
                if (padbits != padmask)
                {
                    jpg.HasZeroPaddingBit = true;
                }

                for (int i = npadbits - 1; i >= 0; --i)
                {
                    jpg.PaddingBits.Add((byte)((padbits >> i) & 1));
                }
            }

            int unusedBytesLeft = BitsLeft >> 3;
            while (unusedBytesLeft-- > 0)
            {
                --Pos;
                if (Pos < NextMarkerPos && data[Pos] == 0 && data[Pos - 1] == 0xff)
                {
                    --Pos;
                }
            }

            pos = Pos;
            return Pos <= NextMarkerPos;
        }
    }

    private static int ReadSymbol(HuffmanTableEntry[] lut, int table, BitReaderState br)
    {
        br.FillBitWindow();
        int val = (int)((br.Val >> (br.BitsLeft - 8)) & 0xff);
        table += val;
        int nbits = lut[table].Bits - 8;
        if (nbits > 0)
        {
            br.BitsLeft -= 8;
            table += lut[table].Value;
            val = (int)((br.Val >> (br.BitsLeft - nbits)) & (ulong)((1 << nbits) - 1));
            table += val;
        }

        br.BitsLeft -= lut[table].Bits;
        return lut[table].Value;
    }

    private static int HuffExtend(int x, int s)
    {
        int half = 1 << (s - 1);
        return x >= half ? x : x - (1 << s) + 1;
    }

    private static void DecodeDctBlock(HuffmanTableEntry[] dcLut, int dcOff, HuffmanTableEntry[] acLut, int acOff, int ss, int se, int al,
        ref int eobrun, ref bool resetState, ref int numZeroRuns, BitReaderState br, ref short lastDc, short[] coeffs, int co)
    {
        int am = 1 << al;
        bool eobrunAllowed = ss > 0;
        if (ss == 0)
        {
            int s = ReadSymbol(dcLut, dcOff, br);
            if (s >= 12)
            {
                throw Fail("Invalid Huffman symbol for DC coefficient.");
            }

            int diff = 0;
            if (s > 0)
            {
                int bits = br.ReadBits(s);
                diff = HuffExtend(bits, s);
            }

            int coeff = diff + lastDc;
            int dcCoeff = coeff * am;
            coeffs[co] = (short)dcCoeff;
            if (dcCoeff != coeffs[co])
            {
                throw Fail("Invalid DC coefficient");
            }

            lastDc = (short)coeff;
            ++ss;
        }

        if (ss > se)
        {
            return;
        }

        if (eobrun > 0)
        {
            --eobrun;
            return;
        }

        numZeroRuns = 0;
        for (int k = ss; k <= se; k++)
        {
            int sr = ReadSymbol(acLut, acOff, br);
            if (sr >= 256)
            {
                throw Fail("Invalid Huffman symbol for AC coefficient");
            }

            int r = sr >> 4;
            int s = sr & 15;
            if (s > 0)
            {
                k += r;
                if (k > se)
                {
                    throw Fail("Out-of-band coefficient");
                }

                if (s + al >= 12)
                {
                    throw Fail("Out of range AC coefficient value");
                }

                int bits = br.ReadBits(s);
                int coeff = HuffExtend(bits, s);
                coeffs[co + LjJpegData.NaturalOrder[k]] = (short)(coeff * am);
                numZeroRuns = 0;
            }
            else if (r == 15)
            {
                k += 15;
                ++numZeroRuns;
            }
            else
            {
                if (eobrunAllowed && k == ss && eobrun == 0)
                {
                    resetState = true;
                }

                eobrun = 1 << r;
                if (r > 0)
                {
                    if (!eobrunAllowed)
                    {
                        throw Fail("End-of-block run crossing DC coeff.");
                    }

                    eobrun += br.ReadBits(r);
                }

                break;
            }
        }

        --eobrun;
    }

    private static void RefineDctBlock(HuffmanTableEntry[] acLut, int acOff, int ss, int se, int al, ref int eobrun, ref bool resetState,
        BitReaderState br, short[] coeffs, int co)
    {
        int am = 1 << al;
        bool eobrunAllowed = ss > 0;
        if (ss == 0)
        {
            int s = br.ReadBits(1);
            short dc = coeffs[co];
            dc |= (short)(s * am);
            coeffs[co] = dc;
            ++ss;
        }

        if (ss > se)
        {
            return;
        }

        int p1 = am;
        int m1 = -am;
        int k = ss;
        bool inZeroRun = false;
        if (eobrun <= 0)
        {
            for (; k <= se; k++)
            {
                int s = ReadSymbol(acLut, acOff, br);
                if (s >= 256)
                {
                    throw Fail("Invalid Huffman symbol for AC coefficient");
                }

                int r = s >> 4;
                s &= 15;
                if (s != 0)
                {
                    if (s != 1)
                    {
                        throw Fail("Invalid Huffman symbol for AC coefficient");
                    }

                    s = br.ReadBits(1) != 0 ? p1 : m1;
                    inZeroRun = false;
                }
                else
                {
                    if (r != 15)
                    {
                        if (eobrunAllowed && k == ss && eobrun == 0)
                        {
                            resetState = true;
                        }

                        eobrun = 1 << r;
                        if (r > 0)
                        {
                            if (!eobrunAllowed)
                            {
                                throw Fail("End-of-block run crossing DC coeff.");
                            }

                            eobrun += br.ReadBits(r);
                        }

                        break;
                    }

                    inZeroRun = true;
                }

                do
                {
                    int thiscoef = coeffs[co + LjJpegData.NaturalOrder[k]];
                    if (thiscoef != 0)
                    {
                        if (br.ReadBits(1) != 0)
                        {
                            if ((thiscoef & p1) == 0)
                            {
                                thiscoef += thiscoef >= 0 ? p1 : m1;
                            }
                        }

                        coeffs[co + LjJpegData.NaturalOrder[k]] = (short)thiscoef;
                    }
                    else
                    {
                        if (--r < 0)
                        {
                            break;
                        }
                    }

                    k++;
                }
                while (k <= se);

                if (s != 0)
                {
                    if (k > se)
                    {
                        throw Fail("Out-of-band coefficient");
                    }

                    coeffs[co + LjJpegData.NaturalOrder[k]] = (short)s;
                }
            }
        }

        if (inZeroRun)
        {
            throw Fail("Extra zero run before end-of-block.");
        }

        if (eobrun > 0)
        {
            for (; k <= se; k++)
            {
                int thiscoef = coeffs[co + LjJpegData.NaturalOrder[k]];
                if (thiscoef != 0)
                {
                    if (br.ReadBits(1) != 0)
                    {
                        if ((thiscoef & p1) == 0)
                        {
                            thiscoef += thiscoef >= 0 ? p1 : m1;
                        }
                    }

                    coeffs[co + LjJpegData.NaturalOrder[k]] = (short)thiscoef;
                }
            }
        }

        --eobrun;
    }

    private static void ProcessRestart(byte[] data, int len, ref int nextRestartMarker, BitReaderState br, LjJpegData jpg)
    {
        if (!br.FinishStream(jpg, out int pos))
        {
            throw Fail("Invalid scan");
        }

        int expected = 0xd0 + nextRestartMarker;
        if (pos + 2 > len || data[pos] != 0xff)
        {
            throw Fail("Marker byte (0xff) expected");
        }

        if (data[pos + 1] != expected)
        {
            throw Fail("Did not find expected restart marker");
        }

        br.Reset(pos + 2);
        nextRestartMarker = (nextRestartMarker + 1) & 7;
    }

    private static void ProcessScan(byte[] data, int len, HuffmanTableEntry[] dcLut, HuffmanTableEntry[] acLut, ushort[,] scanProgression,
        bool isProgressive, ref int pos, LjJpegData jpg)
    {
        ProcessSos(data, len, ref pos, jpg);
        LjJpegScanInfo si = jpg.ScanInfo[^1];
        bool interleaved = si.NumComponents > 1;
        int maxH = 1, maxV = 1;
        foreach (LjJpegComponent c in jpg.Components)
        {
            maxH = Math.Max(maxH, c.HSampFactor);
            maxV = Math.Max(maxV, c.VSampFactor);
        }

        int mcuRows = LjMath.DivCeil(jpg.Height, maxV * 8);
        int mcusPerRow = LjMath.DivCeil(jpg.Width, maxH * 8);
        if (!interleaved)
        {
            LjJpegComponent c = jpg.Components[(int)si.Components[0].CompIdx];
            mcusPerRow = LjMath.DivCeil(jpg.Width * c.HSampFactor, 8 * maxH);
            mcuRows = LjMath.DivCeil(jpg.Height * c.VSampFactor, 8 * maxV);
        }

        var lastDc = new short[4];
        var br = new BitReaderState(data, len, pos);
        int restartsToGo = (int)jpg.RestartInterval;
        int nextRestartMarker = 0;
        int eobrun = -1;
        int blockScanIndex = 0;
        int al = isProgressive ? (int)si.Al : 0;
        int ah = isProgressive ? (int)si.Ah : 0;
        int ss = isProgressive ? (int)si.Ss : 0;
        int se = isProgressive ? (int)si.Se : 63;
        ushort scanBitmask = ah == 0 ? (ushort)(0xffff << al) : (ushort)(1u << al);
        ushort refinementBitmask = (ushort)((1 << al) - 1);
        for (int i = 0; i < si.NumComponents; i++)
        {
            int compIdx = (int)si.Components[i].CompIdx;
            for (int k = ss; k <= se; k++)
            {
                if ((scanProgression[compIdx, k] & scanBitmask) != 0)
                {
                    throw Fail("Overlapping scans");
                }

                if ((scanProgression[compIdx, k] & refinementBitmask) != 0)
                {
                    throw Fail("Invalid scan order, a more refined scan was already done");
                }

                scanProgression[compIdx, k] |= scanBitmask;
            }
        }

        if (al > 10)
        {
            throw Fail("Scan parameter Al is not supported.");
        }

        foreach (LjJpegComponent c in jpg.Components)
        {
            if (c.Coeffs.Length == 0)
            {
                c.Coeffs = new short[(long)c.WidthInBlocks * c.HeightInBlocks * 64];
            }
        }

        for (int mcuY = 0; mcuY < mcuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < mcusPerRow; mcuX++)
            {
                if (jpg.RestartInterval > 0)
                {
                    if (restartsToGo == 0)
                    {
                        ProcessRestart(data, len, ref nextRestartMarker, br, jpg);
                        restartsToGo = (int)jpg.RestartInterval;
                        Array.Clear(lastDc);
                        if (eobrun > 0)
                        {
                            throw Fail("End-of-block run too long.");
                        }

                        eobrun = -1;
                    }

                    --restartsToGo;
                }

                for (int i = 0; i < si.NumComponents; i++)
                {
                    LjJpegComponentScanInfo csi = si.Components[i];
                    LjJpegComponent c = jpg.Components[(int)csi.CompIdx];
                    int dcOff = (int)csi.DcTblIdx * HuffmanLutSize;
                    int acOff = (int)csi.AcTblIdx * HuffmanLutSize;
                    int nby = interleaved ? c.VSampFactor : 1;
                    int nbx = interleaved ? c.HSampFactor : 1;
                    for (int iy = 0; iy < nby; iy++)
                    {
                        for (int ix = 0; ix < nbx; ix++)
                        {
                            int blockY = (mcuY * nby) + iy;
                            int blockX = (mcuX * nbx) + ix;
                            long blockIdx = ((long)blockY * c.WidthInBlocks) + blockX;
                            bool resetState = false;
                            int numZeroRuns = 0;
                            int co = (int)(blockIdx * 64);
                            if (ah == 0)
                            {
                                DecodeDctBlock(dcLut, dcOff, acLut, acOff, ss, se, al, ref eobrun, ref resetState, ref numZeroRuns, br,
                                    ref lastDc[csi.CompIdx], c.Coeffs, co);
                            }
                            else
                            {
                                RefineDctBlock(acLut, acOff, ss, se, al, ref eobrun, ref resetState, br, c.Coeffs, co);
                            }

                            if (resetState)
                            {
                                si.ResetPoints.Add((uint)blockScanIndex);
                            }

                            if (numZeroRuns > 0)
                            {
                                si.ExtraZeroRuns.Add(((uint)blockScanIndex, (uint)numZeroRuns));
                            }

                            ++blockScanIndex;
                        }
                    }
                }
            }
        }

        if (eobrun > 0)
        {
            throw Fail("End-of-block run too long.");
        }

        if (!br.FinishStream(jpg, out pos))
        {
            throw Fail("Invalid scan.");
        }

        if (pos > len)
        {
            throw Fail("Unexpected end of file during scan.");
        }
    }

    private static readonly byte[] IsValidMarker =
    [
        1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
        1, 1, 0, 1, 1, 1, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0,
    ];

    private static int FindNextMarker(byte[] data, int len, int pos)
    {
        int skipped = 0;
        while (pos + 1 < len && (data[pos] != 0xff || data[pos + 1] < 0xc0 || IsValidMarker[data[pos + 1] - 0xc0] == 0))
        {
            ++pos;
            ++skipped;
        }

        return skipped;
    }

    /// <summary>Parses a JPEG into libjxl's JPEGData; throws InvalidDataException for anything libjxl rejects.</summary>
    public static LjJpegData Read(byte[] data)
    {
        int len = data.Length;
        var jpg = new LjJpegData();
        int pos = 0;
        if (pos + 2 > len || data[pos] != 0xff || data[pos + 1] != 0xd8)
        {
            throw Fail("Did not find expected SOI marker");
        }

        pos += 2;
        HuffmanTableEntry[] dcLut = NewLut(4 * HuffmanLutSize);
        HuffmanTableEntry[] acLut = NewLut(4 * HuffmanLutSize);
        bool foundSof = false, foundSos = false, foundDri = false;
        var scanProgression = new ushort[4, 64];
        bool isProgressive = false;
        int marker;
        do
        {
            int numSkipped = FindNextMarker(data, len, pos);
            if (numSkipped > 0)
            {
                jpg.MarkerOrder.Add(0xff);
                jpg.InterMarkerData.Add(data.AsSpan(pos, numSkipped).ToArray());
                pos += numSkipped;
            }

            if (pos + 2 > len || data[pos] != 0xff)
            {
                throw Fail("Marker byte (0xff) expected");
            }

            marker = data[pos + 1];
            pos += 2;
            switch (marker)
            {
                case 0xc0:
                case 0xc1:
                case 0xc2:
                    isProgressive = marker == 0xc2;
                    ProcessSof(data, len, ref pos, jpg);
                    foundSof = true;
                    break;
                case 0xc4:
                    ProcessDht(data, len, dcLut, acLut, ref pos, jpg);
                    break;
                case >= 0xd0 and <= 0xd7:
                    break;
                case 0xd9:
                    break;
                case 0xda:
                    ProcessScan(data, len, dcLut, acLut, scanProgression, isProgressive, ref pos, jpg);
                    foundSos = true;
                    break;
                case 0xdb:
                    ProcessDqt(data, len, ref pos, jpg);
                    break;
                case 0xdd:
                    ProcessDri(data, len, ref pos, ref foundDri, jpg);
                    break;
                case >= 0xe0 and <= 0xef:
                    jpg.AppData.Add(ProcessMarkerData(data, len, ref pos));
                    break;
                case 0xfe:
                    jpg.ComData.Add(ProcessMarkerData(data, len, ref pos));
                    break;
                default:
                    throw Fail($"Unsupported marker: {marker}");
            }

            jpg.MarkerOrder.Add((byte)marker);
        }
        while (marker != 0xd9);

        if (!foundSof)
        {
            throw Fail("Missing SOF marker.");
        }

        if (!foundSos)
        {
            throw Fail("Missing SOS marker.");
        }

        if (pos < len)
        {
            jpg.TailData = data.AsSpan(pos).ToArray();
        }

        // FixupIndexes
        foreach (LjJpegComponent c in jpg.Components)
        {
            bool found = false;
            for (int j = 0; j < jpg.Quant.Count; j++)
            {
                if (jpg.Quant[j].Index == c.QuantIdx)
                {
                    c.QuantIdx = (uint)j;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw Fail("Quantization table not found");
            }
        }

        if (jpg.HuffmanCode.Count == 0)
        {
            throw Fail("Need at least one Huffman code table.");
        }

        if (jpg.HuffmanCode.Count >= 512)
        {
            throw Fail("Too many Huffman tables.");
        }

        return jpg;
    }
}

/// <summary>enc_jpeg_data.cc: metadata detection and the jbrd box payload.</summary>
internal static class LjJpegJbrd
{
    private static readonly byte[] IccTag = "ICC_PROFILE\0"u8.ToArray();
    private static readonly byte[] ExifTag = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpTag = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    private static void DetectIccProfile(LjJpegData jpg)
    {
        int numIcc = 0, numIccJpeg = 0;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            byte[] app = jpg.AppData[i];
            int pos = 0;
            if (app[pos++] != 0xE2)
            {
                continue;
            }

            int tagLength = (app[pos] << 8) + app[pos + 1];
            pos += 2;
            if (tagLength < 2 + IccTag.Length + 2)
            {
                continue;
            }

            if (!app.AsSpan(pos, IccTag.Length).SequenceEqual(IccTag))
            {
                continue;
            }

            pos += IccTag.Length;
            int chunkId = app[pos++];
            int numChunks = app[pos++];
            if (chunkId != numIcc + 1)
            {
                continue;
            }

            if (numIccJpeg == 0)
            {
                numIccJpeg = numChunks;
            }

            if (numIccJpeg != numChunks)
            {
                continue;
            }

            numIcc++;
            jpg.AppMarkerType[i] = LjAppMarkerType.Icc;
        }

        if (numIcc != numIccJpeg)
        {
            throw new InvalidDataException("Invalid ICC chunks");
        }
    }

    private static bool GetMarkerPayload(byte[] marker, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (marker.Length < 3)
        {
            return false;
        }

        int internalSize = (marker[1] << 8) | marker[2];
        if (internalSize != marker.Length - 1)
        {
            return false;
        }

        payload = marker.AsSpan(3);
        return true;
    }

    private static void DetectBlobs(LjJpegData jpg)
    {
        bool haveExif = false, haveXmp = false;
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            byte[] marker = jpg.AppData[i];
            if (marker.Length == 0 || marker[0] != 0xE1)
            {
                continue;
            }

            if (!GetMarkerPayload(marker, out ReadOnlySpan<byte> payload))
            {
                continue;
            }

            if (!haveExif && payload.Length > ExifTag.Length && payload[..ExifTag.Length].SequenceEqual(ExifTag))
            {
                jpg.AppMarkerType[i] = LjAppMarkerType.Exif;
                haveExif = true;
            }

            if (!haveXmp && payload.Length >= XmpTag.Length && payload[..XmpTag.Length].SequenceEqual(XmpTag))
            {
                jpg.AppMarkerType[i] = LjAppMarkerType.Xmp;
                haveXmp = true;
            }
        }
    }

    /// <summary>The ICC profile assembled from APP2 chunks (ParseChunkedMarker), or null when absent/corrupt.</summary>
    public static byte[]? ExtractIcc(LjJpegData jpg)
    {
        var chunks = new List<byte[]?>();
        int expected = 0;
        bool first = true;
        int ordinal = 0;
        foreach (byte[] marker in jpg.AppData)
        {
            if (marker.Length == 0 || marker[0] != 0xE2)
            {
                continue;
            }

            if (!GetMarkerPayload(marker, out ReadOnlySpan<byte> payload))
            {
                continue;
            }

            if (payload.Length < IccTag.Length || !payload[..IccTag.Length].SequenceEqual(IccTag))
            {
                continue;
            }

            payload = payload[IccTag.Length..];
            if (payload.Length < 2)
            {
                return null;
            }

            int index = payload[0], total = payload[1];
            ordinal++;
            if (index != ordinal || total == 0)
            {
                return null;
            }

            payload = payload[2..];
            if (first)
            {
                first = false;
                expected = total;
                for (int i = 0; i <= total; i++)
                {
                    chunks.Add(null);
                }
            }
            else if (expected != total)
            {
                return null;
            }

            if (index == 0 || index > total || chunks[index] != null)
            {
                return null;
            }

            chunks[index] = payload.ToArray();
        }

        if (expected == 0)
        {
            return null;
        }

        var output = new List<byte>();
        for (int i = 1; i <= expected; i++)
        {
            if (chunks[i] == null)
            {
                return null;
            }

            output.AddRange(chunks[i]!);
        }

        return output.ToArray();
    }

    /// <summary>SetBlobsFromJpegData: the first Exif (without "Exif\0\0") and XMP payloads.</summary>
    public static (byte[]? Exif, byte[]? Xmp) ExtractBlobs(LjJpegData jpg)
    {
        byte[]? exif = null, xmp = null;
        foreach (byte[] marker in jpg.AppData)
        {
            if (marker.Length == 0 || marker[0] != 0xE1)
            {
                continue;
            }

            if (!GetMarkerPayload(marker, out ReadOnlySpan<byte> payload))
            {
                continue;
            }

            if (payload.Length >= ExifTag.Length && payload[..ExifTag.Length].SequenceEqual(ExifTag) && exif == null)
            {
                exif = payload[ExifTag.Length..].ToArray();
            }

            if (payload.Length >= XmpTag.Length && payload[..XmpTag.Length].SequenceEqual(XmpTag) && xmp == null)
            {
                xmp = payload[XmpTag.Length..].ToArray();
            }
        }

        return (exif, xmp);
    }

    /// <summary>SetColorTransformFromJpegData: true for YCbCr, false for RGB (no transform).</summary>
    public static bool IsYCbCr(LjJpegData jpg)
    {
        int nb = jpg.Components.Count;
        if (nb != 1 && nb != 3)
        {
            throw new NotSupportedException("Cannot recompress JPEGs with neither 1 nor 3 channels");
        }

        bool isRgb = false;
        List<byte> markers = jpg.MarkerOrder;
        if (!markers.Contains(0xE0))
        {
            int appMarkers = 0;
            int i = 0;
            for (; i < markers.Count; i++)
            {
                if ((markers[i] & 0xF0) == 0xE0)
                {
                    if (markers[i] == 0xEE)
                    {
                        byte[] d = jpg.AppData[appMarkers];
                        if (d.Length == 15 && d[3] == 'A' && d[4] == 'd' && d[5] == 'o' && d[6] == 'b' && d[7] == 'e')
                        {
                            isRgb = d[14] == 0;
                            break;
                        }
                    }

                    appMarkers++;
                }
            }

            if (i == markers.Count)
            {
                isRgb = nb == 3 && jpg.Components[0].Id == 'R' && jpg.Components[1].Id == 'G' && jpg.Components[2].Id == 'B';
            }
        }

        return !isRgb || nb == 1;
    }

    /// <summary>EncodeJPEGData: the jbrd box contents (header bundle + brotli-compressed marker payloads).</summary>
    public static byte[] Encode(LjJpegData jpg, int brotliQuality)
    {
        jpg.AppMarkerType.Clear();
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            jpg.AppMarkerType.Add(LjAppMarkerType.Unknown);
        }

        DetectIccProfile(jpg);
        DetectBlobs(jpg);

        var w = new LjBitWriter();
        LjFields.Write(jpg, w);
        w.ZeroPadToByte();
        var output = new MemoryStream();
        output.Write(w.GetSpan());

        var pieces = new List<byte[]>();
        for (int i = 0; i < jpg.AppData.Count; i++)
        {
            if (jpg.AppMarkerType[i] == LjAppMarkerType.Unknown)
            {
                pieces.Add(jpg.AppData[i]);
            }
        }

        pieces.AddRange(jpg.ComData);
        pieces.AddRange(jpg.InterMarkerData);
        pieces.Add(jpg.TailData);
        output.Write(LjBrotli.CompressStream(pieces, brotliQuality, 22));
        return output.ToArray();
    }
}

/// <summary>Brotli via the runtime's native encoder, fed as a stream the way libjxl feeds it.</summary>
internal static class LjBrotli
{
    public static byte[] CompressStream(List<byte[]> pieces, int quality, int window)
    {
        using var enc = new BrotliEncoder(quality, window);
        var outBuf = new byte[1 << 16];
        var result = new MemoryStream();
        for (int p = 0; p < pieces.Count; p++)
        {
            bool last = p == pieces.Count - 1;
            ReadOnlySpan<byte> src = pieces[p];
            while (true)
            {
                var status = enc.Compress(src, outBuf, out int consumed, out int written, last);
                result.Write(outBuf, 0, written);
                src = src[consumed..];
                if (status == System.Buffers.OperationStatus.DestinationTooSmall)
                {
                    continue;
                }

                if (status != System.Buffers.OperationStatus.Done)
                {
                    throw new InvalidOperationException("Brotli compression failed");
                }

                if (src.Length == 0)
                {
                    break;
                }
            }
        }

        return result.ToArray();
    }
}
