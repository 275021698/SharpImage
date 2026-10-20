// Lossless JPEG -> JPEG XL recompression, a line-by-line port of what cjxl 0.12 does by default for JPEG input
// (lossless_jpeg=1, effort 7): the JPEG's own quantized DCT coefficients become a YCbCr VarDCT frame (enc_frame.cc
// ComputeJPEGTranscodingData), and a jbrd box carries everything else needed to rebuild the original file byte for
// byte. Output is a standard JPEG XL container that libjxl decodes and `djxl x.jxl x.jpg` turns back into the input.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjJpegTranscoder
{
    private const int SpeedTier = LjSpeed.Squirrel;   // effort 7
    private const int BrotliEffort = 9;               // cjxl --brotli_effort default (jbrd and brob boxes)
    private const int NumOrders = 13;
    private const int NonZeroBuckets = 37;
    private const int ZeroDensityContextCount = 458;
    private const int GroupDim = 256;
    private const int GroupDimInBlocks = 32;
    private const int ColorTileDimInBlocks = 8;
    private const int NumQuantTables = 17;
    private const int CflFixedPointPrecision = 11;
    private const int CflFixedPointRatioMax = (256 << CflFixedPointPrecision) - 1;
    private const int DefaultColorFactor = 84;

    private sealed class FrameDims
    {
        public int Xsize, Ysize, XsizeBlocks, YsizeBlocks, XsizeGroups, YsizeGroups, XsizeDcGroups, YsizeDcGroups, NumGroups, NumDcGroups;

        public FrameDims(int xsize, int ysize, int maxHShift, int maxVShift)
        {
            Xsize = xsize;
            Ysize = ysize;
            XsizeBlocks = LjMath.DivCeil(xsize, 8 << maxHShift) << maxHShift;
            YsizeBlocks = LjMath.DivCeil(ysize, 8 << maxVShift) << maxVShift;
            XsizeGroups = LjMath.DivCeil(xsize, GroupDim);
            YsizeGroups = LjMath.DivCeil(ysize, GroupDim);
            XsizeDcGroups = LjMath.DivCeil(XsizeBlocks, GroupDim);
            YsizeDcGroups = LjMath.DivCeil(YsizeBlocks, GroupDim);
            NumGroups = XsizeGroups * YsizeGroups;
            NumDcGroups = XsizeDcGroups * YsizeDcGroups;
        }

        // Rect(x0, y0, w, h, xend, yend): size clamped to the extent.
        public (int X0, int Y0, int W, int H) BlockGroupRect(int g)
        {
            int gx = g % XsizeGroups, gy = g / XsizeGroups;
            int x0 = gx * GroupDimInBlocks, y0 = gy * GroupDimInBlocks;
            return (x0, y0, Math.Min(GroupDimInBlocks, XsizeBlocks - x0), Math.Min(GroupDimInBlocks, YsizeBlocks - y0));
        }

        public (int X0, int Y0, int W, int H) DcGroupRect(int g)
        {
            int gx = g % XsizeDcGroups, gy = g / XsizeDcGroups;
            int x0 = gx * GroupDim, y0 = gy * GroupDim;
            return (x0, y0, Math.Min(GroupDim, XsizeBlocks - x0), Math.Min(GroupDim, YsizeBlocks - y0));
        }

        public int NumStreams => 1 + (3 * NumDcGroups) + NumQuantTables + NumGroups;   // one pass
    }

    /// <summary>True when the bytes start with a JPEG SOI marker.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;

    /// <summary>
    /// Recompresses a JPEG into a JPEG XL container with JPEG reconstruction data. Throws
    /// <see cref="NotSupportedException"/> for JPEGs libjxl cannot recompress (arithmetic coding, CMYK, 12-bit,
    /// unusual sampling, corrupt streams); callers then encode the decoded pixels instead, as cjxl does.
    /// </summary>
    public static byte[] Transcode(byte[] jpegBytes)
    {
        LjProf.Mark("start");
        LjJpegData jpg;
        try
        {
            jpg = LjJpegReader.Read(jpegBytes);
        }
        catch (InvalidDataException e)
        {
            throw new NotSupportedException("JPEG cannot be recompressed losslessly: " + e.Message, e);
        }

        LjProf.Mark("read jpeg");
        int nbcomp = jpg.Components.Count;
        if (nbcomp != 1 && nbcomp != 3)
        {
            throw new NotSupportedException("Cannot recompress JPEGs with neither 1 nor 3 channels");
        }

        // ---- codestream metadata (JxlEncoderAddJPEGFrame) ----
        var metadata = new LjImageMetadata { XybEncoded = false };
        byte[]? icc = LjJpegJbrd.ExtractIcc(jpg);
        if (icc == null)
        {
            if (nbcomp == 1)
            {
                metadata.ColorEncoding.ColorSpace = LjColorEncoding.CsGray;
            }
        }
        else
        {
            metadata.ColorEncoding.WantIcc = true;
            metadata.ColorEncoding.ColorSpace = IccColorSpace(icc);
            metadata.ColorEncoding.Icc = icc;
        }

        (byte[]? exif, byte[]? xmp) = LjJpegJbrd.ExtractBlobs(jpg);
        if (exif != null)
        {
            metadata.Orientation = InterpretExifOrientation(exif, metadata.Orientation);
        }

        var size = new LjSizeHeader();
        size.Set((uint)jpg.Width, (uint)jpg.Height);
        byte[] jbrd = LjJpegJbrd.Encode(jpg, BrotliEffort);

        // ---- frame header (MakeFrameHeader with jpeg_data) ----
        bool ycbcr = LjJpegJbrd.IsYCbCr(jpg);
        var fh = new LjFrameHeader
        {
            NonserializedMetadata = metadata,
            NonserializedXsize = (uint)jpg.Width,
            NonserializedYsize = (uint)jpg.Height,
            Encoding = LjFrameHeader.EncVarDct,
            ColorTransform = ycbcr ? LjFrameHeader.CtYCbCr : LjFrameHeader.CtNone,
            Flags = LjFrameHeader.FlagSkipAdaptiveDcSmoothing,
            IsLast = true,
        };
        fh.LoopFilter.Gab = false;
        fh.LoopFilter.EpfIters = 0;
        Span<int> hs = stackalloc int[3];
        Span<int> vs = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            LjJpegComponent c = jpg.Components[nbcomp == 3 ? i : 0];
            hs[i] = c.HSampFactor;
            vs[i] = c.VSampFactor;
        }

        if (!fh.ChromaSubsampling.Set(hs, vs))
        {
            throw new NotSupportedException("Invalid subsample mode");
        }

        if (!ycbcr && (fh.ChromaSubsampling.MaxHShift != 0 || fh.ChromaSubsampling.MaxVShift != 0))
        {
            throw new NotSupportedException("Chroma subsampling is not supported when color transform is not YCbCr");
        }

        LjProf.Mark("jbrd+header setup");
        byte[] frame = EncodeFrame(jpg, fh, nbcomp == 1);

        // ---- codestream headers ----
        var hw = new LjBitWriter();
        hw.Write(8, 0xFF);
        hw.Write(8, 0x0A);
        LjFields.Write(size, hw);
        LjFields.Write(metadata, hw);
        var transformData = new LjCustomTransformData { NonserializedXybEncoded = false };
        LjFields.Write(transformData, hw);
        if (metadata.ColorEncoding.WantIcc)
        {
            LjIccEncoder.WriteIcc(icc!, hw);
        }

        hw.ZeroPadToByte();
        byte[] headerBytes = hw.ToArray();

        // ---- container (ProcessOneEnqueuedInput) ----
        var o = new MemoryStream();
        o.Write([0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A]);
        WriteBox(o, "ftyp", [(byte)'j', (byte)'x', (byte)'l', (byte)' ', 0, 0, 0, 0, (byte)'j', (byte)'x', (byte)'l', (byte)' ']);
        long px = (long)jpg.Width * jpg.Height;
        bool level10 = jpg.Width > (1 << 18) || jpg.Height > (1 << 18) || px > (1L << 28) || (icc != null && icc.Length > (1 << 22));
        if (level10)
        {
            WriteBox(o, "jxll", [10]);
        }

        uint jxlpCounter = 0;
        WriteJxlp(o, jxlpCounter++, false, headerBytes, false);
        WriteBox(o, "jbrd", jbrd);
        if (exif != null)
        {
            var exifBox = new byte[exif.Length + 4];
            exif.CopyTo(exifBox, 4);
            WriteBrob(o, "Exif", exifBox);
        }

        if (xmp != null)
        {
            WriteBrob(o, "xml ", xmp);
        }

        long upperBits = (long)jpg.Width * jpg.Height * (metadata.ColorEncoding.ColorSpace == LjColorEncoding.CsGray ? 1 : 3) * 24;
        bool useLargeBox = 0x100000 + (upperBits >> 3) >= 0x100000000L - 8;
        WriteJxlp(o, jxlpCounter++, true, frame, useLargeBox);
        return o.ToArray();
    }

    // ICC header colour space ('RGB ', 'GRAY', ...) as JxlColorSpace.
    private static uint IccColorSpace(byte[] icc)
    {
        if (icc.Length < 20)
        {
            throw new NotSupportedException("Error decoding the ICC profile embedded in the input JPEG");
        }

        string cs = System.Text.Encoding.ASCII.GetString(icc, 16, 4);
        return cs switch
        {
            "RGB " or "CMYK" => LjColorEncoding.CsRgb,   // CMYK is coded as RGB (a kBlack extra channel would mark it)
            "GRAY" => LjColorEncoding.CsGray,
            _ => LjColorEncoding.CsUnknown,
        };
    }

    private static uint InterpretExifOrientation(byte[] exif, uint current)
    {
        if (exif.Length < 12)
        {
            return current;
        }

        bool be;
        uint magic = BitConverter.ToUInt32(exif, 0);
        if (magic == 0x2A004D4D)
        {
            be = true;
        }
        else if (magic == 0x002A4949)
        {
            be = false;
        }
        else
        {
            return current;
        }

        uint Load32(int p) => be ? (uint)((exif[p] << 24) | (exif[p + 1] << 16) | (exif[p + 2] << 8) | exif[p + 3]) : BitConverter.ToUInt32(exif, p);
        ushort Load16(int p) => be ? (ushort)((exif[p] << 8) | exif[p + 1]) : BitConverter.ToUInt16(exif, p);
        ulong offset = Load32(4);
        if ((ulong)exif.Length < 12 + offset + 2 || offset < 8)
        {
            return current;
        }

        int t = 4 + (int)offset - 4;
        if (offset + 2 >= (ulong)exif.Length)
        {
            return current;
        }

        int nbTags = Load16(t);
        t += 2;
        while (nbTags > 0)
        {
            if (t + 12 >= exif.Length)
            {
                return current;
            }

            ushort tag = Load16(t);
            t += 2;
            if (tag == 274)
            {
                ushort type = Load16(t);
                uint count = Load32(t + 2);
                ushort value = Load16(t + 6);
                if (type == 3 && count == 1 && value >= 1 && value <= 8)
                {
                    return value;
                }

                return current;
            }

            t += 10;
            nbTags--;
        }

        return current;
    }

    private static void WriteBoxHeader(Stream o, string type, long contentSize, bool large)
    {
        if (large)
        {
            o.Write([0, 0, 0, 1]);
            o.Write(System.Text.Encoding.ASCII.GetBytes(type));
            long boxSize = contentSize + 16;
            for (int i = 7; i >= 0; i--)
            {
                o.WriteByte((byte)(boxSize >> (8 * i)));
            }
        }
        else
        {
            long boxSize = contentSize + 8;
            o.Write([(byte)(boxSize >> 24), (byte)(boxSize >> 16), (byte)(boxSize >> 8), (byte)boxSize]);
            o.Write(System.Text.Encoding.ASCII.GetBytes(type));
        }
    }

    private static void WriteBox(Stream o, string type, ReadOnlySpan<byte> content)
    {
        WriteBoxHeader(o, type, content.Length, false);
        o.Write(content);
    }

    private static void WriteJxlp(Stream o, uint counter, bool last, ReadOnlySpan<byte> content, bool large)
    {
        WriteBoxHeader(o, "jxlp", content.Length + 4, large);
        if (last)
        {
            counter |= 0x80000000;
        }

        o.Write([(byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter]);
        o.Write(content);
    }

    private static void WriteBrob(Stream o, string type, byte[] content)
    {
        byte[] compressed = LjBrotli.CompressStream([content], BrotliEffort, 22);
        var payload = new byte[4 + compressed.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(payload, 0);
        compressed.CopyTo(payload, 4);
        WriteBox(o, "brob", payload);
    }

    // ================================================================== frame

    private sealed class BlockCtxMap
    {
        public List<int>[] DcThresholds = [new(), new(), new()];
        public List<uint> QfThresholds = new();
        public byte[] CtxMap = Array.Empty<byte>();
        public int NumCtxs, NumDcCtxs;

        public int Context(int dcIdx, uint qf, int ord, int c)
        {
            int qfIdx = 0;
            foreach (uint t in QfThresholds)
            {
                if (qf > t)
                {
                    qfIdx++;
                }
            }

            int idx = c < 2 ? c ^ 1 : 2;
            idx = (idx * NumOrders) + ord;
            idx = (idx * (QfThresholds.Count + 1)) + qfIdx;
            idx = (idx * NumDcCtxs) + dcIdx;
            return CtxMap[idx];
        }

        public int ZeroDensityContextsOffset(int blockCtx) => (NumCtxs * NonZeroBuckets) + (ZeroDensityContextCount * blockCtx);

        public int NumAcContexts => NumCtxs * (NonZeroBuckets + ZeroDensityContextCount);

        public int NonZeroContext(int nonZeros, int blockCtx)
        {
            if (nonZeros >= 64)
            {
                nonZeros = 64;
            }

            int ctx = nonZeros < 8 ? nonZeros : 4 + (nonZeros / 2);
            return (ctx * NumCtxs) + blockCtx;
        }
    }

    private static readonly ushort[] CoeffFreqContext =
    [
        0xBAD, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
        15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22,
        23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26,
        27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    ];

    private static readonly ushort[] CoeffNumNonzeroContext =
    [
        0xBAD, 0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123,
        152, 152, 152, 152, 152, 152, 152, 152, 180, 180, 180, 180, 180,
        180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
    ];

    private static int ZeroDensityContext(int nonzerosLeft, int k, int prev) =>
        ((CoeffNumNonzeroContext[nonzerosLeft] + CoeffFreqContext[k]) * 2) + prev;   // 8x8 blocks only

    // AcStrategy::ComputeNaturalCoeffOrder for DCT8 (CoeffOrderAndLut with cx = cy = 1).
    internal static int[] NaturalOrder8x8(bool lut)
    {
        var o = new int[64];
        const int cx = 1, kBlock = 8;
        int cur = 1;
        for (int i = 0; i < cx * kBlock; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                int x = j, y = i - j;
                if ((i & 1) != 0)
                {
                    (x, y) = (y, x);
                }

                int val = x < 1 && y < 1 ? 0 : cur++;
                if (lut)
                {
                    o[(y * kBlock) + x] = val;
                }
                else
                {
                    o[val] = (y * kBlock) + x;
                }
            }
        }

        for (int ip = (cx * kBlock) - 1; ip > 0; ip--)
        {
            int i = ip - 1;
            for (int j = 0; j <= i; j++)
            {
                int x = (cx * kBlock) - 1 - (i - j);
                int y = (cx * kBlock) - 1 - j;
                if ((i & 1) != 0)
                {
                    (x, y) = (y, x);
                }

                int val = cur++;
                if (lut)
                {
                    o[(y * kBlock) + x] = val;
                }
                else
                {
                    o[val] = (y * kBlock) + x;
                }
            }
        }

        return o;
    }

    private static int RoundAwayF(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);

    private static byte[] EncodeFrame(LjJpegData jpg, LjFrameHeader fh, bool isGray)
    {
        LjChromaSubsampling cs = fh.ChromaSubsampling;
        var fd = new FrameDims(jpg.Width, jpg.Height, cs.MaxHShift, cs.MaxVShift);
        int[] cmap = isGray ? [0, 0, 0] : fh.ColorTransform == LjFrameHeader.CtYCbCr ? [1, 0, 2] : [0, 1, 2];

        // Quantization tables (JPEG XL transposes the DCT).
        var qt = new int[64 * 3];
        var dcquantization = new float[3];
        var qtDc = new int[3];
        for (int c = 0; c < 3; c++)
        {
            int jc = cmap[c];
            int[] quant = jpg.Quant[(int)jpg.Components[jc].QuantIdx].Values;
            dcquantization[c] = 255 * 8.0f / quant[0];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    qt[(64 * c) + (8 * x) + y] = quant[(8 * y) + x];
                }
            }

            qtDc[c] = qt[64 * c];
        }

        // DequantMatricesSetCustomDC + its encode/decode roundtrip.
        var dcQuantWritten = new float[3];
        var invDcQuant = new float[3];
        for (int c = 0; c < 3; c++)
        {
            float dcq = 1.0f / dcquantization[c];
            dcQuantWritten[c] = dcq;
            var tw = new LjBitWriter();
            LjFields.WriteF16(tw, dcq * 128.0f);
            tw.ZeroPadToByte();
            byte[] b = tw.ToArray();
            float dec = LjFields.F16ToFloat((uint)(b[0] | (b[1] << 8))) * (1.0f / 128.0f);
            invDcQuant[c] = 1.0f / dec;
        }

        var dcquantizationR = new float[3];
        for (int c = 0; c < 3; c++)
        {
            dcquantizationR[c] = 1.0f / dcquantization[c];
        }

        var scaledQtable = new int[64 * 3];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int coeffpos = (y * 8) + x;
                    int ratio = (1 << CflFixedPointPrecision) * qt[64 + coeffpos] / qt[(64 * c) + coeffpos];
                    if (ratio > CflFixedPointRatioMax)
                    {
                        throw new NotSupportedException("Ratio of two entries in a JPEG quantization table is too large");
                    }

                    scaledQtable[(64 * c) + (8 * x) + y] = ratio;
                }
            }
        }

        short[] JpegCoeffs(int c) => jpg.Components[cmap[c]].Coeffs;
        int JpegRowStart(int c, int y) => (int)(jpg.Components[cmap[c]].WidthInBlocks * 64 * y);

        // Chroma-from-luma maps (4:4:4 colour JPEGs).
        int cmapXs = LjMath.DivCeil(fd.XsizeBlocks * 8, 64), cmapYs = LjMath.DivCeil(fd.YsizeBlocks * 8, 64);
        var ytox = new sbyte[cmapXs * cmapYs];
        var ytob = new sbyte[cmapXs * cmapYs];
        bool cfl = cs.Is444 && jpg.Components.Count == 3;
        if (cfl)
        {
            foreach (int c in (int[])[0, 2])
            {
                sbyte[] map = c == 0 ? ytox : ytob;
                const float kScale = DefaultColorFactor;
                const int kOffset = 127;
                const float kBase = 0f;   // base correlation x and b are both 0 for JPEG
                float kZeroThresh = kScale * 0.5f * 0.9999f;
                for (int ty = 0; ty < cmapYs; ty++)
                {
                    for (int tx = 0; tx < cmapXs; tx++)
                    {
                        int y0 = ty * ColorTileDimInBlocks, x0 = tx * ColorTileDimInBlocks;
                        int y1 = Math.Min(fd.YsizeBlocks, (ty + 1) * ColorTileDimInBlocks);
                        int x1 = Math.Min(fd.XsizeBlocks, (tx + 1) * ColorTileDimInBlocks);
                        var dNumZeros = new int[257];
                        short[] m = JpegCoeffs(1), sco = JpegCoeffs(c);
                        for (int y = y0; y < y1; y++)
                        {
                            int rm = JpegRowStart(1, y), rs = JpegRowStart(c, y);
                            for (int x = x0; x < x1; x++)
                            {
                                for (int coeffpos = 1; coeffpos < 64; coeffpos++)
                                {
                                    float scaledM = m[rm + (x * 64) + coeffpos] * (1.0f / (1 << CflFixedPointPrecision)) * scaledQtable[(64 * c) + coeffpos];
                                    float scaledS = (kScale * sco[rs + (x * 64) + coeffpos]) + ((kOffset - (kBase * kScale)) * scaledM);
                                    if (MathF.Abs(scaledM) > 1e-8f)
                                    {
                                        float from, to;
                                        if (scaledM > 0)
                                        {
                                            from = (scaledS - kZeroThresh) / scaledM;
                                            to = (scaledS + kZeroThresh) / scaledM;
                                        }
                                        else
                                        {
                                            from = (scaledS + kZeroThresh) / scaledM;
                                            to = (scaledS - kZeroThresh) / scaledM;
                                        }

                                        if (from < 0.0f)
                                        {
                                            from = 0.0f;
                                        }

                                        if (to > 255.0f)
                                        {
                                            to = 255.0f;
                                        }

                                        if (from <= to)
                                        {
                                            dNumZeros[(int)MathF.Ceiling(from)]++;
                                            dNumZeros[(int)MathF.Floor(to + 1)]--;
                                        }
                                    }
                                }
                            }
                        }

                        // FindAvgIndexOfSumMaximum<256>
                        int maxval = 0, val = 0, maxBegin = 0, maxEnd = 0;
                        for (int i = 0; i < 256; i++)
                        {
                            val += dNumZeros[i];
                            if (val > maxval)
                            {
                                maxval = val;
                                maxBegin = i;
                            }

                            if (val == maxval)
                            {
                                maxEnd = i;
                            }
                        }

                        int best = (maxBegin + maxEnd + 1) >> 1;
                        int offsetSum = 0;
                        for (int i = 0; i <= kOffset; i++)
                        {
                            offsetSum += dNumZeros[i];
                        }

                        map[(ty * cmapXs) + tx] = 0;
                        if (maxval > offsetSum + 1)
                        {
                            map[(ty * cmapXs) + tx] = (sbyte)(best - kOffset);
                        }
                    }
                }
            }
        }

        // DC (as libjxl's float image) and the AC coefficients per group / channel, in block order.
        var dc = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            dc[c] = new float[fd.XsizeBlocks * fd.YsizeBlocks];
        }

        var coeffs = new int[fd.NumGroups][][];
        for (int g = 0; g < fd.NumGroups; g++)
        {
            coeffs[g] = [new int[GroupDim * GroupDim], new int[GroupDim * GroupDim], new int[GroupDim * GroupDim]];
        }

        var dcCounts = new long[2048];
        var totalDc = new long[3];
        bool dcZero = fh.ColorTransform == LjFrameHeader.CtYCbCr;
        var block = new int[64];
        foreach (int c in (int[])[1, 0, 2])
        {
            if (jpg.Components.Count == 1 && c != 1)
            {
                totalDc[c] = 1;
                continue;
            }

            int hshift = cs.HShift(c), vshift = cs.VShift(c);
            sbyte[] map = c == 0 ? ytox : ytob;
            short[] jc = JpegCoeffs(c), jy = JpegCoeffs(1);
            for (int g = 0; g < fd.NumGroups; g++)
            {
                int gx = g % fd.XsizeGroups, gy = g / fd.XsizeGroups;
                int coeffPos = 0;
                int[] dst = coeffs[g][c];
                for (int by = gy * GroupDimInBlocks; by < fd.YsizeBlocks && by < (gy + 1) * GroupDimInBlocks; by++)
                {
                    if ((by >> vshift) << vshift != by)
                    {
                        continue;
                    }

                    int rowIn = JpegRowStart(c, by >> vshift);
                    int rowY = JpegRowStart(1, by);
                    for (int bx = gx * GroupDimInBlocks; bx < fd.XsizeBlocks && bx < (gx + 1) * GroupDimInBlocks; bx++)
                    {
                        if ((bx >> hshift) << hshift != bx)
                        {
                            continue;
                        }

                        int b = rowIn + ((bx >> hshift) * 64);
                        int idc = dcZero ? jc[b] : jc[b] + (1024 / qtDc[c]);
                        if (c == 1)
                        {
                            dcCounts[Math.Min(idc + 1024, 2047)]++;
                        }

                        totalDc[c]++;
                        dc[c][((by >> vshift) * fd.XsizeBlocks) + (bx >> hshift)] = idc * dcquantizationR[c];
                        if (c == 1 || !cs.Is444)
                        {
                            for (int y = 0; y < 8; y++)
                            {
                                for (int x = 0; x < 8; x++)
                                {
                                    block[(x * 8) + y] = jc[b + (y * 8) + x];
                                }
                            }
                        }
                        else
                        {
                            int factor = map[((by / ColorTileDimInBlocks) * cmapXs) + (bx / ColorTileDimInBlocks)];
                            int scale = factor * (1 << CflFixedPointPrecision) / DefaultColorFactor;
                            for (int y = 0; y < 8; y++)
                            {
                                for (int x = 0; x < 8; x++)
                                {
                                    int coeffpos = (y * 8) + x;
                                    int yv = jy[(64 * bx) + rowY + coeffpos];
                                    int qChroma = jc[(64 * bx) + rowIn + coeffpos];
                                    int coeffScale = ((scale * scaledQtable[(64 * c) + coeffpos]) + (1 << (CflFixedPointPrecision - 1))) >> CflFixedPointPrecision;
                                    int cflFactor = ((yv * coeffScale) + (1 << (CflFixedPointPrecision - 1))) >> CflFixedPointPrecision;
                                    block[(x * 8) + y] = qChroma - cflFactor;
                                }
                            }
                        }

                        Array.Copy(block, 0, dst, coeffPos, 64);
                        coeffPos += 64;
                    }
                }
            }
        }

        LjProf.Mark("cfl+dc/ac split");
        // Block context map: luma-DC buckets.
        var bcm = new BlockCtxMap();
        int numThresholds = LjMath.CeilLog2Nonzero((ulong)totalDc[1]) - LjMath.CeilLog2Nonzero((uint)(qt[1] + qt[2] + qt[3] + qt[4] + qt[5])) - 7;
        numThresholds = Math.Clamp(numThresholds, 1, 7);
        long cumsum = 0;
        long cut = totalDc[1] / (numThresholds + 1);
        for (int j = 0; j < 2048; j++)
        {
            cumsum += dcCounts[j];
            if (cumsum > cut)
            {
                bcm.DcThresholds[1].Add(j - 1025);
                cut = totalDc[1] * (bcm.DcThresholds[1].Count + 1) / (numThresholds + 1);
            }
        }

        bcm.NumDcCtxs = bcm.DcThresholds[1].Count + 1;
        int ndc = bcm.NumDcCtxs;
        bcm.CtxMap = new byte[3 * NumOrders * ndc];
        for (int i = 0; i < ndc; i++)
        {
            bcm.CtxMap[i] = (byte)i;
            if (jpg.Components.Count == 1)
            {
                bcm.CtxMap[(NumOrders * ndc) + i] = bcm.CtxMap[(2 * NumOrders * ndc) + i] = (byte)ndc;
            }
            else
            {
                bcm.CtxMap[(NumOrders * ndc) + i] = (byte)(ndc + (i / 2));
                bcm.CtxMap[(2 * NumOrders * ndc) + i] = (byte)(ndc + ((ndc - 1) / 2) + 1 + (i / 2));
            }
        }

        int maxCtx = 0;
        foreach (byte b in bcm.CtxMap)
        {
            maxCtx = Math.Max(maxCtx, b);
        }

        bcm.NumCtxs = maxCtx + 1;

        // ---- modular streams: VarDCT DC, AC metadata, raw quant table ----
        int numStreams = fd.NumStreams;
        var streamImages = new List<LjModularImage>(numStreams);
        for (int i = 0; i < numStreams; i++)
        {
            streamImages.Add(new LjModularImage());
        }

        var baseOptions = ModularBaseOptions(numStreams);
        var streamOptions = new List<LjModularOptions>(numStreams);
        for (int i = 0; i < numStreams; i++)
        {
            streamOptions.Add(baseOptions.Clone());
        }

        int qt0Stream = 1 + (3 * fd.NumDcGroups);
        {
            var img = LjModularImage.Create(8, 8, 8, 3);
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        img.Channel[c][x, y] = qt[(c * 64) + (y * 8) + x];
                    }
                }
            }

            streamImages[qt0Stream] = img;
        }

        var qdc = new byte[fd.XsizeBlocks * fd.YsizeBlocks];
        var acMetadataSize = new int[fd.NumDcGroups];
        for (int g = 0; g < fd.NumDcGroups; g++)
        {
            (int rx0, int ry0, int rw, int rh) = fd.DcGroupRect(g);

            // AddVarDCTDC
            int sid = 1 + g;
            LjModularOptions so = streamOptions[sid];
            so.MaxChanSize = 0xFFFFFF;
            so.Predictor = LjPredictor.Weighted;
            so.WpTreeMode = LjModularOptions.TreeMode.WpOnly;
            so.Kind = LjModularOptions.TreeKind.WpFixedDc;
            var dimg = LjModularImage.Create(rw, rh, 8, 3);
            foreach (int c in (int[])[1, 0, 2])
            {
                float invFactor = invDcQuant[c] * (1.0f * 1);
                int hsh = cs.Is444 ? 0 : cs.HShift(c), vsh = cs.Is444 ? 0 : cs.VShift(c);
                int xs = rw >> hsh, ys = rh >> vsh;
                var ch = new LjChannel(xs, ys);
                for (int y = 0; y < ys; y++)
                {
                    for (int x = 0; x < xs; x++)
                    {
                        float v = dc[c][(((ry0 >> vsh) + y) * fd.XsizeBlocks) + (rx0 >> hsh) + x];
                        ch[x, y] = RoundAwayF(v * invFactor);
                    }
                }

                dimg.Channel[c < 2 ? c ^ 1 : c] = ch;
            }

            streamImages[sid] = dimg;

            // DequantDC: the quantized-DC context per block (luma thresholds only for JPEG).
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    int bucketY = 0;
                    int yq = dimg.Channel[0][x >> cs.HShift(1), y >> cs.VShift(1)];
                    foreach (int t in bcm.DcThresholds[1])
                    {
                        if (yq > t)
                        {
                            bucketY++;
                        }
                    }

                    qdc[((ry0 + y) * fd.XsizeBlocks) + rx0 + x] = (byte)bucketY;
                }
            }

            // AddACMetadata (jpeg_transcode = true)
            int aid = 1 + (2 * fd.NumDcGroups) + g;
            LjModularOptions ao = streamOptions[aid];
            ao.MaxChanSize = 0xFFFFFF;
            if (ao.Predictor != LjPredictor.Weighted)
            {
                ao.WpTreeMode = LjModularOptions.TreeMode.NoWp;
            }

            ao.Kind = LjModularOptions.TreeKind.JpegTranscodeAcMeta;
            int crx0 = rx0 >> 3, cry0 = ry0 >> 3, crw = (rw + 7) >> 3, crh = (rh + 7) >> 3;
            var aimg = LjModularImage.Create(rw, rh, 8, 4);
            aimg.Channel[0] = new LjChannel(crw, crh, 3, 3);
            aimg.Channel[1] = new LjChannel(crw, crh, 3, 3);
            for (int y = 0; y < crh; y++)
            {
                for (int x = 0; x < crw; x++)
                {
                    aimg.Channel[0][x, y] = ytox[((cry0 + y) * cmapXs) + crx0 + x];
                    aimg.Channel[1][x, y] = ytob[((cry0 + y) * cmapXs) + crx0 + x];
                }
            }

            int num = rw * rh;
            aimg.Channel[2] = new LjChannel(num, 2);   // raw strategy 0 (DCT8), qf - 1 = 0
            acMetadataSize[g] = num;
            streamImages[aid] = aimg;
        }

        LjProf.Mark("modular images");
        // ---- coefficient orders (ComputeUsedOrders / ComputeCoeffOrder) ----
        int[] natural = NaturalOrder8x8(false);
        int[] naturalLut = NaturalOrder8x8(true);
        bool smallImage = fd.XsizeBlocks < 5 && fd.YsizeBlocks < 5;
        uint currentUsedOrders = smallImage ? 0u : 1u;
        var orders = new int[3][];
        {
            var numZeros = new long[3 * 64];
            if (currentUsedOrders != 0)
            {
                double blockFraction = 0.5f;   // kSquirrel and only 8x8 DCTs
                ulong threshold = (ulong)((ulong.MaxValue >> 32) * blockFraction);
                ulong s0 = 0x94D049BB133111EBul, s1 = 0xBF58476D1CE4E5B9ul;
                for (int g = 0; g < fd.NumGroups; g++)
                {
                    (_, _, int gw, int gh) = fd.BlockGroupRect(g);
                    int acOffset = 0;
                    for (int by = 0; by < gh; by++)
                    {
                        for (int bx = 0; bx < gw; bx++)
                        {
                            ulong a = s0, bb = s1;
                            ulong bits = a + bb;
                            s0 = bb;
                            a ^= a << 23;
                            a ^= bb ^ (a >> 18) ^ (bb >> 5);
                            s1 = a;
                            if ((bits >> 32) > threshold)
                            {
                                continue;
                            }

                            for (int c = 0; c < 3; c++)
                            {
                                int[] rows = coeffs[g][c];
                                for (int k = 0; k < 64; k++)
                                {
                                    if (rows[acOffset + k] == 0)
                                    {
                                        numZeros[(c * 64) + k]++;
                                    }
                                }

                                numZeros[c * 64] = -1;
                            }

                            acOffset += 64;
                        }
                    }
                }
            }

            bool isNondefault = false;
            for (int c = 0; c < 3; c++)
            {
                orders[c] = new int[64];
                if (currentUsedOrders == 0)
                {
                    Array.Copy(natural, orders[c], 64);
                    continue;
                }

                const float invSqrtSz = 0.125f;
                var keys = new (int Pos, ulong CountAndIdx)[64];
                for (int i = 0; i < 64; i++)
                {
                    int pos = natural[i];
                    float cf = (numZeros[(c * 64) + pos] * invSqrtSz) + 0.1f;
                    ulong count = cf <= 0 ? 0UL : (ulong)cf;
                    keys[i] = (pos, (count << 16) | (ulong)i);
                }

                Array.Sort(keys, (p, q) => p.CountAndIdx.CompareTo(q.CountAndIdx));
                for (int i = 0; i < 64; i++)
                {
                    orders[c][i] = keys[i].Pos;
                    isNondefault |= natural[i] != keys[i].Pos;
                }
            }

            if (!isNondefault)
            {
                currentUsedOrders = 0;
            }
        }

        LjProf.Mark("coeff orders");
        // ---- AC tokens (TokenizeCoefficients) ----
        var acTokens = new List<List<LjToken>>(fd.NumGroups);
        for (int g = 0; g < fd.NumGroups; g++)
        {
            (int gx0, int gy0, int gw, int gh) = fd.BlockGroupRect(g);
            var outTok = new List<LjToken>(gw * gh * 3 * 8);
            var nzeros = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                nzeros[c] = new int[GroupDimInBlocks * GroupDimInBlocks];
            }

            var offset = new int[3];
            for (int by = 0; by < gh; by++)
            {
                for (int bx = 0; bx < gw; bx++)
                {
                    foreach (int c in (int[])[1, 0, 2])
                    {
                        int sbx = bx >> cs.HShift(c), sby = by >> cs.VShift(c);
                        if (sbx << cs.HShift(c) != bx || sby << cs.VShift(c) != by)
                        {
                            continue;
                        }

                        int[] blk = coeffs[g][c];
                        int bo = offset[c];
                        int nz = 0;
                        for (int k = 1; k < 64; k++)
                        {
                            if (blk[bo + k] != 0)
                            {
                                nz++;
                            }
                        }

                        nzeros[c][(sby * GroupDimInBlocks) + sbx] = nz;
                        int[] order = orders[c];
                        int predicted;
                        if (sbx == 0)
                        {
                            predicted = sby == 0 ? 32 : nzeros[c][((sby - 1) * GroupDimInBlocks) + sbx];
                        }
                        else if (sby == 0)
                        {
                            predicted = nzeros[c][(sby * GroupDimInBlocks) + sbx - 1];
                        }
                        else
                        {
                            predicted = (nzeros[c][((sby - 1) * GroupDimInBlocks) + sbx] + nzeros[c][(sby * GroupDimInBlocks) + sbx - 1] + 1) / 2;
                        }

                        int blockCtx = bcm.Context(qdc[((gy0 + by) * fd.XsizeBlocks) + gx0 + bx], 1, 0, c);
                        outTok.Add(new LjToken((uint)bcm.NonZeroContext(predicted, blockCtx), (uint)nz));
                        int histoOffset = bcm.ZeroDensityContextsOffset(blockCtx);
                        int prev = nz > 64 / 16 ? 0 : 1;
                        for (int k = 1; k < 64 && nz != 0; k++)
                        {
                            int coeff = blk[bo + order[k]];
                            int ctx = histoOffset + ZeroDensityContext(nz, k, prev);
                            outTok.Add(new LjToken((uint)ctx, LjMath.PackSigned(coeff)));
                            prev = coeff != 0 ? 1 : 0;
                            nz -= prev;
                        }

                        offset[c] += 64;
                    }
                }
            }

            acTokens.Add(outTok);
        }

        LjProf.Mark("ac tokenize");
        // ---- modular tree + tokens (ModularFrameEncoder::ComputeTree / ComputeTokens) ----
        List<LjTreeNode> tree = ComputeTree(streamImages, streamOptions, fd);
        var treeTokens = new List<LjToken>();
        tree = LjMaTree.TokenizeTree(tree, treeTokens);
        var streamHeaders = new LjGroupHeader[numStreams];
        var tokens = new List<List<LjToken>>(numStreams);
        var imageWidths = new int[numStreams];
        for (int i = 0; i < numStreams; i++)
        {
            streamHeaders[i] = new LjGroupHeader();
            var t = new List<LjToken>();
            imageWidths[i] = LjModularCompress.Compress(streamImages[i], streamOptions[i], i, tree, streamHeaders[i], t);
            tokens.Add(t);
        }

        LjProf.Mark("modular tree+tokens");
        // ---- sections ----
        bool isSmall = fd.NumGroups == 1;
        int numToc = isSmall ? 1 : 2 + fd.NumDcGroups + fd.NumGroups;
        var groupCodes = new LjBitWriter[numToc];
        for (int i = 0; i < numToc; i++)
        {
            groupCodes[i] = new LjBitWriter();
        }

        LjBitWriter Out(int index) => groupCodes[isSmall ? 0 : index];

        // DC global
        LjEntropyCode modularCode = new();
        {
            LjBitWriter w = Out(0);
            // DequantMatricesEncodeDC
            w.Write(1, 0);
            for (int c = 0; c < 3; c++)
            {
                LjFields.WriteF16(w, dcQuantWritten[c] * 128.0f);
            }

            // Quantizer params: global_scale 65536, quant_dc 1.
            LjFields.WriteU32(w, 65536, LjU32.BitsOffset(11, 1), LjU32.BitsOffset(11, 2049), LjU32.BitsOffset(12, 4097), LjU32.BitsOffset(16, 8193));
            LjFields.WriteU32(w, 1, LjU32.Val(16), LjU32.BitsOffset(5, 1), LjU32.BitsOffset(8, 1), LjU32.BitsOffset(16, 1));
            // EncodeBlockCtxMap (never the default map for JPEG)
            w.Write(1, 0);
            for (int j = 0; j < 3; j++)
            {
                w.Write(4, (ulong)bcm.DcThresholds[j].Count);
                foreach (int t in bcm.DcThresholds[j])
                {
                    LjFields.WriteU32(w, LjMath.PackSigned(t), LjU32.BitsOnly(4), LjU32.BitsOffset(8, 16), LjU32.BitsOffset(16, 272), LjU32.BitsOffset(32, 65808));
                }
            }

            w.Write(4, 0);
            LjContextMap.EncodeContextMap(new List<byte>(bcm.CtxMap), bcm.NumCtxs, w);
            // ColorCorrelationEncodeDC: base correlation b = 0 is not the default.
            w.Write(1, 0);
            LjFields.WriteU32(w, DefaultColorFactor, LjU32.Val(84), LjU32.Val(256), LjU32.BitsOffset(8, 2), LjU32.BitsOffset(16, 258));
            LjFields.WriteF16(w, 0f);
            LjFields.WriteF16(w, 0f);
            w.Write(8, 128);
            w.Write(8, 128);
            // ModularFrameEncoder::EncodeGlobalInfo
            if (treeTokens.Count == 0)
            {
                w.Write(1, 0);
            }
            else
            {
                w.Write(1, 1);
                var p = LjHistogramParams.ForModular(SpeedTier, false, false, false, false);
                var treeCode = new LjEntropyCode();
                var tt = new List<List<LjToken>> { treeTokens };
                LjAns.BuildAndEncodeHistograms(p, LjMaTree.NumTreeContexts, ref tt, treeCode, w);
                LjAns.WriteTokens(tt[0], treeCode, 0, w);
                p.ImageWidths = imageWidths;
                LjAns.BuildAndEncodeHistograms(p, (tree.Count + 1) / 2, ref tokens, modularCode, w);
            }
        }

        void EncodeStream(LjBitWriter w, int sid)
        {
            if (streamImages[sid].Channel.Count == 0)
            {
                return;
            }

            LjFields.Write(streamHeaders[sid], w);
            LjAns.WriteTokens(tokens[sid], modularCode, 0, w);
        }

        LjProf.Mark("dc global");
        // DC groups
        for (int g = 0; g < fd.NumDcGroups; g++)
        {
            LjBitWriter w = Out(1 + g);
            w.Write(2, 0);                      // extra_dc_precision
            EncodeStream(w, 1 + g);             // VarDCT DC
            EncodeStream(w, 1 + fd.NumDcGroups + g);   // modular DC (empty)
            (_, _, int rw, int rh) = fd.DcGroupRect(g);
            int nbBits = LjMath.CeilLog2Nonzero((uint)(rw * rh));
            if (nbBits != 0)
            {
                w.Write(nbBits, (ulong)(acMetadataSize[g] - 1));
            }

            EncodeStream(w, 1 + (2 * fd.NumDcGroups) + g);
        }

        LjProf.Mark("dc groups");
        // AC global
        LjEntropyCode acCode;
        {
            LjBitWriter w = Out(1 + fd.NumDcGroups);
            // DequantMatricesEncode: table 0 RAW, the rest library 0.
            w.Write(1, 0);
            for (int i = 0; i < NumQuantTables; i++)
            {
                if (i == 0)
                {
                    w.Write(3, 7);
                    LjFields.WriteF16(w, 1.0f / (8 * 255));
                    EncodeStream(w, qt0Stream);
                }
                else
                {
                    w.Write(3, 0);
                }
            }

            int numHistoBits = LjMath.CeilLog2Nonzero((uint)fd.NumGroups);
            if (numHistoBits != 0)
            {
                w.Write(numHistoBits, 0);
            }

            LjFields.WriteU32(w, currentUsedOrders, LjU32.Val(0x5F), LjU32.Val(0x13), LjU32.Val(0), LjU32.BitsOnly(NumOrders));
            if (currentUsedOrders != 0)
            {
                var ordTokens = new List<List<LjToken>> { new() };
                var orderZigzag = new int[64];
                for (int c = 0; c < 3; c++)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        orderZigzag[i] = naturalLut[orders[c][i]];
                    }

                    TokenizePermutation(orderZigzag, 1, 64, ordTokens[0]);
                }

                var oc = new LjEntropyCode();
                LjAns.BuildAndEncodeHistograms(new LjHistogramParams(), 8, ref ordTokens, oc, w);
                LjAns.WriteTokens(ordTokens[0], oc, 0, w);
            }

            var hp = new LjHistogramParams(SpeedTier) { Lz77 = LjHistogramParams.Lz77Method.None };
            acCode = new LjEntropyCode();
            LjAns.BuildAndEncodeHistograms(hp, bcm.NumAcContexts, ref acTokens, acCode, w);
        }

        LjProf.Mark("ac global (histograms)");
        // AC groups
        for (int g = 0; g < fd.NumGroups; g++)
        {
            LjBitWriter w = Out(2 + fd.NumDcGroups + g);
            LjAns.WriteTokens(acTokens[g], acCode, 0, w);
            EncodeStream(w, 1 + (3 * fd.NumDcGroups) + NumQuantTables + g);
        }

        foreach (LjBitWriter bw in groupCodes)
        {
            bw.ZeroPadToByte();
        }

        LjProf.Mark("ac groups (ans)");
        // ---- frame header + TOC ----
        var fw = new LjBitWriter();
        LjFields.Write(fh, fw);
        fw.Write(1, 0);   // no TOC permutation
        fw.ZeroPadToByte();
        foreach (LjBitWriter bw in groupCodes)
        {
            LjFields.WriteU32(fw, (uint)(bw.BitsWritten / 8), LjU32.BitsOnly(10), LjU32.BitsOffset(14, 1024), LjU32.BitsOffset(22, 17408), LjU32.BitsOffset(30, 4211712));
        }

        fw.ZeroPadToByte();
        foreach (LjBitWriter bw in groupCodes)
        {
            fw.AppendByteAligned(bw);
        }

        return fw.ToArray();
    }

    private static void TokenizePermutation(int[] order, int skip, int size, List<LjToken> tokens)
    {
        var lehmer = new uint[size];
        var temp = new uint[size + 1];
        for (int idx = 0; idx < size; idx++)
        {
            int s = order[idx];
            uint penalty = 0;
            uint i = (uint)(s + 1);
            while (i != 0)
            {
                penalty += temp[i];
                i &= i - 1;
            }

            lehmer[idx] = (uint)s - penalty;
            i = (uint)(s + 1);
            while (i < size + 1)
            {
                temp[i] += 1;
                i += i & (uint)-(int)i;
            }
        }

        int end = size;
        while (end > skip && lehmer[end - 1] == 0)
        {
            --end;
        }

        tokens.Add(new LjToken(CoeffOrderContext((uint)size), (uint)(end - skip)));
        uint last = 0;
        for (int i = skip; i < end; i++)
        {
            tokens.Add(new LjToken(CoeffOrderContext(last), lehmer[i]));
            last = lehmer[i];
        }
    }

    private static uint CoeffOrderContext(uint val)
    {
        new LjHybridUint(0, 0, 0).Encode(val, out uint token, out _, out _);
        return Math.Min(token, 7u);
    }

    // ModularFrameEncoder::Init for a VarDCT frame at effort 7 (cparams of a lossless JPEG transcode).
    private static LjModularOptions ModularBaseOptions(int numStreams)
    {
        var o = new LjModularOptions
        {
            FastDecodeMultiplier = 1.0f,
            SplittingHeuristicsNodeThreshold = 75 + (14 * SpeedTier),
            MaxPropertyValues = 96,
            NbRepeats = 0.5f,
            Predictor = LjPredictor.Gradient,
            MaxChanSize = GroupDim,
            GroupDim = GroupDim,
        };
        var propOrder = new List<uint> { 0, 1, 15, 9, 10, 11, 12, 13, 14, 2, 3, 4, 5, 6, 7, 8 };
        if (numStreams < 30)
        {
            propOrder.RemoveAt(1);
        }

        o.SplittingHeuristicsProperties = propOrder.GetRange(0, 7);
        return o;
    }

    private static List<LjTreeNode> ComputeTree(List<LjModularImage> images, List<LjModularOptions> options, FrameDims fd)
    {
        int numStreams = images.Count;
        var treeSplits = new List<int>
        {
            0, 1, 1 + fd.NumDcGroups, 1 + (2 * fd.NumDcGroups), 1 + (3 * fd.NumDcGroups), 1 + (3 * fd.NumDcGroups) + NumQuantTables, numStreams,
        };
        var useful = new List<int>();
        for (int chunk = 0; chunk < treeSplits.Count - 1; chunk++)
        {
            bool hasPixels = false;
            for (int i = treeSplits[chunk]; i < treeSplits[chunk + 1]; i++)
            {
                if (!images[i].Empty)
                {
                    hasPixels = true;
                }
            }

            if (hasPixels)
            {
                useful.Add(treeSplits[chunk]);
            }
        }

        if (useful.Count == 0)
        {
            return new List<LjTreeNode>();
        }

        useful.Add(treeSplits[^1]);
        var trees = new List<List<LjTreeNode>>();
        for (int chunk = 0; chunk < useful.Count - 1; chunk++)
        {
            int start = useful[chunk], stop = useful[chunk + 1];
            while (start < stop && images[start].Empty)
            {
                ++start;
            }

            while (start < stop && images[stop - 1].Empty)
            {
                --stop;
            }

            if (options[start].Kind == LjModularOptions.TreeKind.Learn)
            {
                trees.Add(LjMaLearn.LearnTree(images, options, start, stop));
            }
            else
            {
                long totalPixels = 0;
                for (int i = start; i < stop; i++)
                {
                    foreach (LjChannel ch in images[i].Channel)
                    {
                        totalPixels += (long)ch.W * ch.H;
                    }
                }

                totalPixels = Math.Max(totalPixels, 1);
                trees.Add(LjMaTree.PredefinedTree(options[start].Kind, totalPixels, 8, 0));
            }
        }

        var tree = new List<LjTreeNode>();
        LjMaTree.MergeTrees(trees, useful, 0, useful.Count - 1, tree);
        return tree;
    }
}
