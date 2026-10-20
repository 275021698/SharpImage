namespace SharpImage.Formats.WebP;

/// <summary>The WebPConfig fields the lossless encoder reads (cwebp defaults: quality 75, method 4).</summary>
public sealed class WebpLosslessOptions
{
    /// <summary>0..100: effort spent on compression (cwebp -q with -lossless). Default 75.</summary>
    public float Quality { get; set; } = 75;

    /// <summary>0 (fast) .. 6 (slowest, best). Default 4.</summary>
    public int Method { get; set; } = 4;

    /// <summary>Keep the RGB of fully transparent pixels (cwebp -exact). Default false: they become 0.</summary>
    public bool Exact { get; set; }

    /// <summary>Near-lossless preprocessing 0..100, 100 = off (cwebp -near_lossless). Default 100.</summary>
    public int NearLossless { get; set; } = 100;
}

/// <summary>
/// The WebP lossless (VP8L) encoder: libwebp 1.6.0 src/enc/vp8l_enc.c ported line by line, single-threaded as cwebp
/// runs by default (thread_level 0). Same input pixels and options give the same bitstream as libwebp's WebPEncode.
/// </summary>
internal sealed class Vp8lEncoder
{
    // EntropyIx
    private const int KDirect = 0, KSpatial = 1, KSubGreen = 2, KSpatialSubGreen = 3, KPalette = 4, KPaletteAndSpatial = 5,
        KNumEntropyIx = 6;

    // HistoIx
    private const int KHistoAlpha = 0, KHistoAlphaPred = 1, KHistoGreen = 2, KHistoGreenPred = 3, KHistoRed = 4,
        KHistoRedPred = 5, KHistoBlue = 6, KHistoBluePred = 7, KHistoRedSubGreen = 8, KHistoRedPredSubGreen = 9,
        KHistoBlueSubGreen = 10, KHistoBluePredSubGreen = 11, KHistoPalette = 12, KHistoTotal = 13;

    private const int NumBuckets = 256;
    private const int MaxHuffImageSize = 2600;
    private const int MaxPredictorImageSize = 1 << 14;
    private const int MaxRefsBlockPerImage = 16;

    private sealed class CrunchConfig
    {
        public int EntropyIdx;
        public int PaletteSortingType;
        public readonly int[] Lz77 = new int[2];
        public readonly bool[] DoNoCache = new bool[2];
        public int SubConfigsSize;
    }

    private readonly WebpLosslessOptions _config;
    private readonly uint[] _picArgb;   // the picture (width x height, stride = width)
    private readonly int _width, _height;

    // VP8LEncoder
    private uint[] _argb = [];
    private uint[] _rows = [];          // argb_scratch: two rows of width + 1
    private byte[] _maxDiffs = [];      // argb_scratch: near-lossless max diffs
    private uint[] _transformData = [];
    private int _currentWidth;
    private int _histoBits;
    private int _predictorTransformBits;
    private int _crossColorTransformBits;
    private int _cacheBits;
    private bool _useCrossColor, _useSubtractGreen, _usePredict, _usePalette;
    private int _paletteSize;
    private readonly uint[] _palette = new uint[Vp8lCommon.MaxPaletteSize];
    private readonly uint[] _paletteSorted = new uint[Vp8lCommon.MaxPaletteSize];
    private readonly Vp8lBackwardRefs[] _refs = [new(), new(), new(), new()];
    private Vp8lHashChain _hashChain = null!;

    private Vp8lEncoder(WebpLosslessOptions config, uint[] picArgb, int width, int height)
    {
        _config = config;
        _picArgb = picArgb;
        _width = width;
        _height = height;
    }

    /// <summary>VP8LEncodeImage + WebPEncode's lossless preparation: the RIFF / VP8L file for an ARGB picture
    /// (0xAARRGGBB, width * height). The array is modified (transparent pixels cleaned unless Exact).</summary>
    public static byte[] EncodeImage(uint[] argb, int width, int height, WebpLosslessOptions config)
    {
        if (!config.Exact)
        {
            // WebPReplaceTransparentPixels(pic, 0x000000)
            for (int i = 0; i < width * height; ++i)
                if ((argb[i] >> 24) == 0) argb[i] = 0;
        }
        var bw = new Vp8lBitWriter(width * height * 2);
        bw.PutBits((uint)(width - 1), Vp8lCommon.Vp8lImageSizeBits);
        bw.PutBits((uint)(height - 1), Vp8lCommon.Vp8lImageSizeBits);
        bool hasAlpha = false;
        for (int i = 0; i < width * height && !hasAlpha; ++i) hasAlpha = (argb[i] >> 24) != 0xff;
        bw.PutBits(hasAlpha ? 1u : 0u, 1);
        bw.PutBits(0, Vp8lCommon.Vp8lVersionBits);

        var enc = new Vp8lEncoder(config, argb, width, height);
        enc.EncodeStream(bw);
        byte[] data = bw.Finish();

        // WriteImage: RIFF header + "VP8L" chunk (signature byte + data), padded to even size
        int vp8lSize = 1 + data.Length;
        int pad = vp8lSize & 1;
        int riffSize = 4 + 8 + vp8lSize + pad;
        var output = new byte[8 + riffSize];
        "RIFF"u8.CopyTo(output);
        BitConverter.TryWriteBytes(output.AsSpan(4), (uint)riffSize);
        "WEBPVP8L"u8.CopyTo(output.AsSpan(8));
        BitConverter.TryWriteBytes(output.AsSpan(16), (uint)vp8lSize);
        output[20] = Vp8lCommon.Vp8lMagicByte;
        Buffer.BlockCopy(data, 0, output, 21, data.Length);
        return output;
    }

    /// <summary>VP8LEncodeStream (thread_level 0): the transformed, entropy-coded image after the header.</summary>
    internal static void EncodeStreamTo(Vp8lBitWriter bw, uint[] argb, int width, int height, WebpLosslessOptions config)
        => new Vp8lEncoder(config, argb, width, height).EncodeStream(bw);

    // ---- analysis --------------------------------------------------------------------------------------------------

    private static uint HashPixPalette(uint pix) => (uint)(((((ulong)pix + (pix >> 19)) * 0x39c5fba7ul) & 0xffffffffu) >> 24);

    private static void AddSingleSubGreen(uint p, uint[] h, int r, int b)
    {
        int green = (int)p >> 8;
        ++h[r + ((((int)p >> 16) - green) & 0xff)];
        ++h[b + (((int)p - green) & 0xff)];
    }

    private static void AddSingle(uint p, uint[] h, int a, int r, int g, int b)
    {
        ++h[a + (int)((p >> 24) & 0xff)];
        ++h[r + (int)((p >> 16) & 0xff)];
        ++h[g + (int)((p >> 8) & 0xff)];
        ++h[b + (int)(p & 0xff)];
    }

    private bool AnalyzeEntropy(bool usePalette, int paletteSize, int transformBits, out int minEntropyIx,
        out bool redAndBlueAlwaysZero)
    {
        int width = _width, height = _height;
        uint[] argb = _picArgb;
        if (usePalette && paletteSize <= 16)
        {
            minEntropyIx = KPalette;
            redAndBlueAlwaysZero = true;
            return true;
        }
        var histo = new uint[KHistoTotal * NumBuckets];
        int H(int ix) => ix * NumBuckets;
        int prevRow = -1;
        int currRow = 0;
        uint pixPrev = argb[0];
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                uint pix = argb[currRow + x];
                uint pixDiff = Vp8lCommon.SubPixels(pix, pixPrev);
                pixPrev = pix;
                if (pixDiff == 0 || (prevRow >= 0 && pix == argb[prevRow + x])) continue;
                AddSingle(pix, histo, H(KHistoAlpha), H(KHistoRed), H(KHistoGreen), H(KHistoBlue));
                AddSingle(pixDiff, histo, H(KHistoAlphaPred), H(KHistoRedPred), H(KHistoGreenPred), H(KHistoBluePred));
                AddSingleSubGreen(pix, histo, H(KHistoRedSubGreen), H(KHistoBlueSubGreen));
                AddSingleSubGreen(pixDiff, histo, H(KHistoRedPredSubGreen), H(KHistoBluePredSubGreen));
                ++histo[H(KHistoPalette) + (int)HashPixPalette(pix)];
            }
            prevRow = currRow;
            currRow += width;
        }

        var entropyComp = new ulong[KHistoTotal];
        var entropy = new ulong[KNumEntropyIx];
        int lastModeToAnalyze = usePalette ? KPalette : KSpatialSubGreen;
        ++histo[H(KHistoRedPredSubGreen)];
        ++histo[H(KHistoBluePredSubGreen)];
        ++histo[H(KHistoRedPred)];
        ++histo[H(KHistoGreenPred)];
        ++histo[H(KHistoBluePred)];
        ++histo[H(KHistoAlphaPred)];
        for (int j = 0; j < KHistoTotal; ++j) entropyComp[j] = Vp8lHistogramUtil.BitsEntropy(histo, H(j), NumBuckets);
        entropy[KDirect] = entropyComp[KHistoAlpha] + entropyComp[KHistoRed] + entropyComp[KHistoGreen] + entropyComp[KHistoBlue];
        entropy[KSpatial] = entropyComp[KHistoAlphaPred] + entropyComp[KHistoRedPred] + entropyComp[KHistoGreenPred] +
                            entropyComp[KHistoBluePred];
        entropy[KSubGreen] = entropyComp[KHistoAlpha] + entropyComp[KHistoRedSubGreen] + entropyComp[KHistoGreen] +
                             entropyComp[KHistoBlueSubGreen];
        entropy[KSpatialSubGreen] = entropyComp[KHistoAlphaPred] + entropyComp[KHistoRedPredSubGreen] +
                                    entropyComp[KHistoGreenPred] + entropyComp[KHistoBluePredSubGreen];
        entropy[KPalette] = entropyComp[KHistoPalette];
        ulong tiles = (ulong)Vp8lCommon.SubSampleSize(width, transformBits) * (ulong)Vp8lCommon.SubSampleSize(height, transformBits);
        entropy[KSpatial] += tiles * Vp8lCommon.FastLog2(14);
        entropy[KSpatialSubGreen] += tiles * Vp8lCommon.FastLog2(24);
        entropy[KPalette] += ((ulong)paletteSize * 8ul) << Vp8lCommon.Log2PrecisionBits;

        minEntropyIx = KDirect;
        for (int k = KDirect + 1; k <= lastModeToAnalyze; ++k)
            if (entropy[minEntropyIx] > entropy[k]) minEntropyIx = k;

        redAndBlueAlwaysZero = true;
        int[,] histoPairs = { { KHistoRed, KHistoBlue }, { KHistoRedPred, KHistoBluePred },
            { KHistoRedSubGreen, KHistoBlueSubGreen }, { KHistoRedPredSubGreen, KHistoBluePredSubGreen }, { KHistoRed, KHistoBlue } };
        int redHisto = H(histoPairs[minEntropyIx, 0]);
        int blueHisto = H(histoPairs[minEntropyIx, 1]);
        for (int i = 1; i < NumBuckets; ++i)
        {
            if ((histo[redHisto + i] | histo[blueHisto + i]) != 0)
            {
                redAndBlueAlwaysZero = false;
                break;
            }
        }
        return true;
    }

    private static int ClampBits(int width, int height, int bits, int minBits, int maxBits, int imageSizeMax)
    {
        bits = bits < minBits ? minBits : bits > maxBits ? maxBits : bits;
        int imageSize = Vp8lCommon.SubSampleSize(width, bits) * Vp8lCommon.SubSampleSize(height, bits);
        while (bits < maxBits && imageSize > imageSizeMax)
        {
            ++bits;
            imageSize = Vp8lCommon.SubSampleSize(width, bits) * Vp8lCommon.SubSampleSize(height, bits);
        }
        while (bits > minBits && imageSize == 1)
        {
            imageSize = Vp8lCommon.SubSampleSize(width, bits - 1) * Vp8lCommon.SubSampleSize(height, bits - 1);
            if (imageSize != 1) break;
            --bits;
        }
        return bits;
    }

    private static int GetHistoBits(int method, bool usePalette, int width, int height)
    {
        int histoBits = (usePalette ? 9 : 7) - method;
        return ClampBits(width, height, histoBits, Vp8lCommon.MinHuffmanBits, Vp8lCommon.MaxHuffmanBits, MaxHuffImageSize);
    }

    private static int GetTransformBits(int method, int histoBits)
    {
        int maxTransformBits = method < 4 ? 6 : method > 4 ? 4 : 5;
        return histoBits > maxTransformBits ? maxTransformBits : histoBits;
    }

    private List<CrunchConfig> EncoderAnalyze(out bool redAndBlueAlwaysZero)
    {
        int method = _config.Method;
        bool lowEffort = method == 0;
        int quality = (int)_config.Quality;
        var configs = new List<CrunchConfig>();
        int nLz77s;
        bool doNoCache = false;
        redAndBlueAlwaysZero = false;

        _paletteSize = Vp8lPalette.GetColorPalette(_picArgb, _width, _height, _width, _paletteSorted);
        bool usePalette = _paletteSize <= Vp8lCommon.MaxPaletteSize;
        if (!usePalette) _paletteSize = 0;

        _histoBits = GetHistoBits(method, usePalette, _width, _height);
        int transformBits = GetTransformBits(method, _histoBits);
        _predictorTransformBits = transformBits;
        _crossColorTransformBits = transformBits;

        if (lowEffort)
        {
            configs.Add(new CrunchConfig
            {
                EntropyIdx = usePalette ? KPalette : KSpatialSubGreen,
                PaletteSortingType = usePalette ? Vp8lPalette.SortedDefault : Vp8lPalette.UnusedPalette,
            });
            nLz77s = 1;
        }
        else
        {
            nLz77s = _paletteSize > 0 && _paletteSize <= 16 ? 2 : 1;
            AnalyzeEntropy(usePalette, _paletteSize, transformBits, out int minEntropyIx, out redAndBlueAlwaysZero);
            if (method == 6 && _config.Quality == 100)
            {
                doNoCache = true;
                for (int i = 0; i < KNumEntropyIx; ++i)
                {
                    if ((i != KPalette && i != KPaletteAndSpatial) || usePalette)
                    {
                        if (usePalette && (i == KPalette || i == KPaletteAndSpatial))
                        {
                            for (int sortingMethod = 0; sortingMethod < Vp8lPalette.PaletteSortingNum; ++sortingMethod)
                            {
                                if (sortingMethod == Vp8lPalette.UnusedPalette || sortingMethod == Vp8lPalette.SortedDefault) continue;
                                configs.Add(new CrunchConfig { EntropyIdx = i, PaletteSortingType = sortingMethod });
                            }
                        }
                        else
                        {
                            configs.Add(new CrunchConfig { EntropyIdx = i, PaletteSortingType = Vp8lPalette.UnusedPalette });
                        }
                    }
                }
            }
            else
            {
                configs.Add(new CrunchConfig
                {
                    EntropyIdx = minEntropyIx,
                    PaletteSortingType = usePalette ? Vp8lPalette.MinimizeDelta : Vp8lPalette.UnusedPalette,
                });
                if (quality >= 75 && method == 5)
                {
                    doNoCache = true;
                    if (minEntropyIx == KPalette)
                        configs.Add(new CrunchConfig { EntropyIdx = KPaletteAndSpatial, PaletteSortingType = Vp8lPalette.MinimizeDelta });
                }
            }
        }
        foreach (var c in configs)
        {
            for (int j = 0; j < nLz77s; ++j)
            {
                c.Lz77[j] = j == 0 ? Vp8lBackwardRefsUtil.LZ77Standard | Vp8lBackwardRefsUtil.LZ77Rle : Vp8lBackwardRefsUtil.LZ77Box;
                c.DoNoCache[j] = doNoCache;
            }
            c.SubConfigsSize = nLz77s;
        }
        return configs;
    }

    // ---- Huffman codes ---------------------------------------------------------------------------------------------

    private static HuffmanTreeCode[] GetHuffBitLengthsAndCodes(Vp8lHistogramSet histogramImage)
    {
        int size = histogramImage.Size;
        var codes = new HuffmanTreeCode[5 * size];
        int maxNumSymbols = 0;
        for (int i = 0; i < size; ++i)
        {
            var histo = histogramImage.Histograms[i];
            for (int k = 0; k < 5; ++k)
            {
                int numSymbols = k == 0 ? Vp8lHistogramUtil.NumCodes(histo.PaletteCodeBits) : k == 4 ? Vp8lCommon.NumDistanceCodes : 256;
                codes[5 * i + k] = new HuffmanTreeCode(numSymbols);
                if (maxNumSymbols < numSymbols) maxNumSymbols = numSymbols;
            }
        }
        var bufRle = new byte[maxNumSymbols];
        for (int i = 0; i < size; ++i)
        {
            var histo = histogramImage.Histograms[i];
            Vp8lHuffman.CreateHuffmanTree(histo.Literal, 15, bufRle, codes[5 * i + 0]);
            Vp8lHuffman.CreateHuffmanTree(histo.Red, 15, bufRle, codes[5 * i + 1]);
            Vp8lHuffman.CreateHuffmanTree(histo.Blue, 15, bufRle, codes[5 * i + 2]);
            Vp8lHuffman.CreateHuffmanTree(histo.Alpha, 15, bufRle, codes[5 * i + 3]);
            Vp8lHuffman.CreateHuffmanTree(histo.Distance, 15, bufRle, codes[5 * i + 4]);
        }
        return codes;
    }

    private static readonly byte[] StorageOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static void StoreHuffmanTreeOfHuffmanTreeToBitMask(Vp8lBitWriter bw, byte[] codeLengthBitdepth)
    {
        int codesToStore = Vp8lCommon.CodeLengthCodes;
        for (; codesToStore > 4; --codesToStore)
            if (codeLengthBitdepth[StorageOrder[codesToStore - 1]] != 0) break;
        bw.PutBits((uint)(codesToStore - 4), 4);
        for (int i = 0; i < codesToStore; ++i) bw.PutBits(codeLengthBitdepth[StorageOrder[i]], 3);
    }

    private static void ClearHuffmanTreeIfOnlyOneSymbol(HuffmanTreeCode huffmanCode)
    {
        int count = 0;
        for (int k = 0; k < huffmanCode.NumSymbols; ++k)
        {
            if (huffmanCode.CodeLengths[k] != 0)
            {
                ++count;
                if (count > 1) return;
            }
        }
        for (int k = 0; k < huffmanCode.NumSymbols; ++k)
        {
            huffmanCode.CodeLengths[k] = 0;
            huffmanCode.Codes[k] = 0;
        }
    }

    private static void StoreHuffmanTreeToBitMask(Vp8lBitWriter bw, HuffmanTreeToken[] tokens, int numTokens, HuffmanTreeCode huffmanCode)
    {
        for (int i = 0; i < numTokens; ++i)
        {
            int ix = tokens[i].Code;
            int extraBits = tokens[i].ExtraBits;
            bw.PutBits(huffmanCode.Codes[ix], huffmanCode.CodeLengths[ix]);
            switch (ix)
            {
                case 16: bw.PutBits((uint)extraBits, 2); break;
                case 17: bw.PutBits((uint)extraBits, 3); break;
                case 18: bw.PutBits((uint)extraBits, 7); break;
            }
        }
    }

    private static void StoreFullHuffmanCode(Vp8lBitWriter bw, HuffmanTreeToken[] tokens, HuffmanTreeCode tree)
    {
        var huffmanCode = new HuffmanTreeCode(Vp8lCommon.CodeLengthCodes);
        bw.PutBits(0, 1);
        int numTokens = Vp8lHuffman.CreateCompressedHuffmanTree(tree, tokens);
        {
            var histogram = new uint[Vp8lCommon.CodeLengthCodes];
            var bufRle = new byte[Vp8lCommon.CodeLengthCodes];
            for (int i = 0; i < numTokens; ++i) ++histogram[tokens[i].Code];
            Vp8lHuffman.CreateHuffmanTree(histogram, 7, bufRle, huffmanCode);
        }
        StoreHuffmanTreeOfHuffmanTreeToBitMask(bw, huffmanCode.CodeLengths);
        ClearHuffmanTreeIfOnlyOneSymbol(huffmanCode);
        {
            int trailingZeroBits = 0;
            int trimmedLength = numTokens;
            int i = numTokens;
            while (i-- > 0)
            {
                int ix = tokens[i].Code;
                if (ix == 0 || ix == 17 || ix == 18)
                {
                    --trimmedLength;
                    trailingZeroBits += huffmanCode.CodeLengths[ix];
                    if (ix == 17) trailingZeroBits += 3;
                    else if (ix == 18) trailingZeroBits += 7;
                }
                else
                {
                    break;
                }
            }
            bool writeTrimmedLength = trimmedLength > 1 && trailingZeroBits > 12;
            int length = writeTrimmedLength ? trimmedLength : numTokens;
            bw.PutBits(writeTrimmedLength ? 1u : 0u, 1);
            if (writeTrimmedLength)
            {
                if (trimmedLength == 2)
                {
                    bw.PutBits(0, 3 + 2);
                }
                else
                {
                    int nbits = Vp8lCommon.BitsLog2Floor((uint)(trimmedLength - 2));
                    int nbitpairs = nbits / 2 + 1;
                    bw.PutBits((uint)(nbitpairs - 1), 3);
                    bw.PutBits((uint)(trimmedLength - 2), nbitpairs * 2);
                }
            }
            StoreHuffmanTreeToBitMask(bw, tokens, length, huffmanCode);
        }
    }

    private static void StoreHuffmanCode(Vp8lBitWriter bw, HuffmanTreeToken[] tokens, HuffmanTreeCode huffmanCode)
    {
        int count = 0;
        Span<int> symbols = stackalloc int[2];
        symbols.Clear();
        const int kMaxSymbol = 1 << 8;
        for (int i = 0; i < huffmanCode.NumSymbols && count < 3; ++i)
        {
            if (huffmanCode.CodeLengths[i] != 0)
            {
                if (count < 2) symbols[count] = i;
                ++count;
            }
        }
        if (count == 0)
        {
            bw.PutBits(0x01, 4);
        }
        else if (count <= 2 && symbols[0] < kMaxSymbol && symbols[1] < kMaxSymbol)
        {
            bw.PutBits(1, 1);
            bw.PutBits((uint)(count - 1), 1);
            if (symbols[0] <= 1)
            {
                bw.PutBits(0, 1);
                bw.PutBits((uint)symbols[0], 1);
            }
            else
            {
                bw.PutBits(1, 1);
                bw.PutBits((uint)symbols[0], 8);
            }
            if (count == 2) bw.PutBits((uint)symbols[1], 8);
        }
        else
        {
            StoreFullHuffmanCode(bw, tokens, huffmanCode);
        }
    }

    private static void StoreImageToBitMask(Vp8lBitWriter bw, int width, int histoBits, Vp8lBackwardRefs refs,
        uint[] histogramSymbols, HuffmanTreeCode[] huffmanCodes)
    {
        int histoXsize = histoBits != 0 ? Vp8lCommon.SubSampleSize(width, histoBits) : 1;
        int tileMask = histoBits == 0 ? 0 : -(1 << histoBits);
        int x = 0, y = 0;
        int tileX = x & tileMask;
        int tileY = y & tileMask;
        int histogramIx = (int)((histogramSymbols[0] >> 8) & 0xffff);
        int codes = 5 * histogramIx;
        var items = refs.Items;
        for (int r = 0; r < refs.Count; ++r)
        {
            ref readonly PixOrCopy v = ref items[r];
            if (tileX != (x & tileMask) || tileY != (y & tileMask))
            {
                tileX = x & tileMask;
                tileY = y & tileMask;
                histogramIx = (int)((histogramSymbols[(y >> histoBits) * histoXsize + (x >> histoBits)] >> 8) & 0xffff);
                codes = 5 * histogramIx;
            }
            if (v.IsLiteral)
            {
                // order { 1, 2, 0, 3 }: green, red, blue, alpha
                int g = v.Literal(1), rr = v.Literal(2), b = v.Literal(0), a = v.Literal(3);
                var c0 = huffmanCodes[codes];
                bw.PutBits(c0.Codes[g], c0.CodeLengths[g]);
                var c1 = huffmanCodes[codes + 1];
                bw.PutBits(c1.Codes[rr], c1.CodeLengths[rr]);
                var c2 = huffmanCodes[codes + 2];
                bw.PutBits(c2.Codes[b], c2.CodeLengths[b]);
                var c3 = huffmanCodes[codes + 3];
                bw.PutBits(c3.Codes[a], c3.CodeLengths[a]);
            }
            else if (v.IsCacheIdx)
            {
                int literalIx = 256 + Vp8lCommon.NumLengthCodes + (int)v.ArgbOrDistance;
                var c0 = huffmanCodes[codes];
                bw.PutBits(c0.Codes[literalIx], c0.CodeLengths[literalIx]);
            }
            else
            {
                Vp8lCommon.PrefixEncode(v.Len, out int code, out int nBits, out int bits);
                var c0 = huffmanCodes[codes];
                int depth = c0.CodeLengths[256 + code];
                bw.PutBits(((uint)bits << depth) | c0.Codes[256 + code], depth + nBits);
                Vp8lCommon.PrefixEncode((int)v.ArgbOrDistance, out code, out nBits, out bits);
                var c4 = huffmanCodes[codes + 4];
                bw.PutBits(c4.Codes[code], c4.CodeLengths[code]);
                bw.PutBits((uint)bits, nBits);
            }
            x += v.Len;
            while (x >= width)
            {
                x -= width;
                ++y;
            }
        }
    }

    // EncodeImageNoHuffman: refsArray = [refs to fill, temporary].
    private static void EncodeImageNoHuffman(Vp8lBitWriter bw, uint[] argb, Vp8lHashChain hashChain, Vp8lBackwardRefs[] refsArray,
        int width, int height, int quality, bool lowEffort)
    {
        Vp8lBackwardRefsUtil.HashChainFill(hashChain, quality, argb, width, height, lowEffort);
        Vp8lBackwardRefsUtil.GetBackwardReferences(width, height, argb, quality, false,
            Vp8lBackwardRefsUtil.LZ77Standard | Vp8lBackwardRefsUtil.LZ77Rle, 0, false, hashChain, refsArray, out int cacheBits);
        var refs = refsArray[0];
        var histogramImage = new Vp8lHistogramSet(1, cacheBits);
        histogramImage.Clear();
        Vp8lHistogramUtil.StoreRefs(refs, null, 0, histogramImage.Histograms[0]);
        var huffmanCodes = GetHuffBitLengthsAndCodes(histogramImage);

        bw.PutBits(0, 1);   // no color cache, no Huffman image
        int maxTokens = 0;
        for (int i = 0; i < 5; ++i)
            if (maxTokens < huffmanCodes[i].NumSymbols) maxTokens = huffmanCodes[i].NumSymbols;
        var tokens = new HuffmanTreeToken[maxTokens];
        for (int i = 0; i < 5; ++i)
        {
            StoreHuffmanCode(bw, tokens, huffmanCodes[i]);
            ClearHuffmanTreeIfOnlyOneSymbol(huffmanCodes[i]);
        }
        StoreImageToBitMask(bw, width, 0, refs, [0u], huffmanCodes);
    }

    private static void EncodeImageInternal(Vp8lBitWriter bw, uint[] argb, Vp8lHashChain hashChain, Vp8lBackwardRefs[] refsArray,
        int width, int height, int quality, bool lowEffort, CrunchConfig config, ref int cacheBits, int histogramBitsIn)
    {
        int histogramImageXysize = Vp8lCommon.SubSampleSize(width, histogramBitsIn) * Vp8lCommon.SubSampleSize(height, histogramBitsIn);
        var histogramArgb = new uint[histogramImageXysize];
        var bwInit = bw.Mark();
        var bwBest = new Vp8lBitWriter(0);
        var hashChainHistogram = new Vp8lHashChain(histogramImageXysize);
        long bwSizeBest = long.MaxValue;

        Vp8lBackwardRefsUtil.HashChainFill(hashChain, quality, argb, width, height, lowEffort);

        int cacheBitsInit = cacheBits == 0 ? Vp8lCommon.MaxColorCacheBits : cacheBits;
        if (config.SubConfigsSize > 1 || config.DoNoCache[0]) bw.CloneTo(bwBest);

        for (int subConfigsIdx = 0; subConfigsIdx < config.SubConfigsSize; ++subConfigsIdx)
        {
            bool doNoCache = config.DoNoCache[subConfigsIdx];
            Vp8lBackwardRefsUtil.GetBackwardReferences(width, height, argb, quality, lowEffort, config.Lz77[subConfigsIdx],
                cacheBitsInit, doNoCache, hashChain, refsArray, out int cacheBitsBest);

            for (int iCache = 0; iCache < (doNoCache ? 2 : 1); ++iCache)
            {
                int cacheBitsTmp = iCache == 0 ? cacheBitsBest : 0;
                int histogramBits = histogramBitsIn;
                if (iCache == 1 && cacheBitsBest == 0) break;

                bw.ResetTo(bwInit);

                var histogramImage = new Vp8lHistogramSet(histogramImageXysize, cacheBitsTmp);
                var tmpHisto = new Vp8lHistogram(cacheBitsTmp);
                Vp8lHistogramUtil.GetHistoImageSymbols(width, height, refsArray[iCache], quality, lowEffort, histogramBits,
                    cacheBitsTmp, histogramImage, tmpHisto, histogramArgb);
                var huffmanCodes = GetHuffBitLengthsAndCodes(histogramImage);

                if (cacheBitsTmp > 0)
                {
                    bw.PutBits(1, 1);
                    bw.PutBits((uint)cacheBitsTmp, 4);
                }
                else
                {
                    bw.PutBits(0, 1);
                }

                int histogramImageSize = 0;
                for (int i = 0; i < histogramImageXysize; ++i)
                {
                    if (histogramArgb[i] >= histogramImageSize) histogramImageSize = (int)histogramArgb[i] + 1;
                    histogramArgb[i] <<= 8;
                }

                bool writeHistogramImage = histogramImageSize > 1;
                bw.PutBits(writeHistogramImage ? 1u : 0u, 1);
                if (writeHistogramImage)
                {
                    Vp8lPredictorEnc.OptimizeSampling(histogramArgb, width, height, histogramBitsIn, Vp8lCommon.MaxHuffmanBits,
                        out histogramBits);
                    bw.PutBits((uint)(histogramBits - 2), 3);
                    EncodeImageNoHuffman(bw, histogramArgb, hashChainHistogram, [refsArray[2], refsArray[3]],
                        Vp8lCommon.SubSampleSize(width, histogramBits), Vp8lCommon.SubSampleSize(height, histogramBits), quality,
                        lowEffort);
                }

                int maxTokens = 0;
                for (int i = 0; i < 5 * histogramImageSize; ++i)
                    if (maxTokens < huffmanCodes[i].NumSymbols) maxTokens = huffmanCodes[i].NumSymbols;
                var tokens = new HuffmanTreeToken[maxTokens];
                for (int i = 0; i < 5 * histogramImageSize; ++i)
                {
                    StoreHuffmanCode(bw, tokens, huffmanCodes[i]);
                    ClearHuffmanTreeIfOnlyOneSymbol(huffmanCodes[i]);
                }
                StoreImageToBitMask(bw, width, histogramBits, refsArray[iCache], histogramArgb, huffmanCodes);
                if (bw.NumBytes < bwSizeBest)
                {
                    bwSizeBest = bw.NumBytes;
                    cacheBits = cacheBitsTmp;
                    Vp8lBitWriter.Swap(bw, bwBest);
                }
            }
        }
        Vp8lBitWriter.Swap(bw, bwBest);
    }

    // ---- transforms ------------------------------------------------------------------------------------------------

    // AllocateTransformBuffer: argb (width x height), the predictor scratch (two rows of width + 1 and the max-diff
    // bytes) when predicting, and the transform image at the finest sampling when predicting or cross-coloring.
    private void AllocateTransformBuffer(int width, int height)
    {
        int imageSize = width * height;
        if (_argb.Length < imageSize) _argb = new uint[imageSize];
        if (_usePredict)
        {
            if (_rows.Length < 2 * (width + 1)) _rows = new uint[2 * (width + 1)];
            if (_maxDiffs.Length < 2 * width) _maxDiffs = new byte[2 * width];
        }
        if (_usePredict || _useCrossColor)
        {
            int td = Vp8lCommon.SubSampleSize(width, Vp8lCommon.MinTransformBits) *
                     Vp8lCommon.SubSampleSize(height, Vp8lCommon.MinTransformBits);
            if (_transformData.Length < td) _transformData = new uint[td];
        }
        _currentWidth = width;
    }

    private void MakeInputImageCopy()
    {
        AllocateTransformBuffer(_width, _height);
        Array.Copy(_picArgb, _argb, _width * _height);
    }

    private const int ApplyPaletteGreedyMax = 4;
    private const int PaletteInvSizeBits = 11;
    private const int PaletteInvSize = 1 << PaletteInvSizeBits;

    private static uint SearchColorGreedy(uint[] palette, uint color)
    {
        if (color == palette[0]) return 0;
        if (color == palette[1]) return 1;
        if (color == palette[2]) return 2;
        return 3;
    }

    private static uint ApplyPaletteHash0(uint color) => (color >> 8) & 0xff;

    private static uint ApplyPaletteHash1(uint color) =>
        (uint)((uint)((color & 0x00ffffffu) * 4222244071ul) >> (32 - PaletteInvSizeBits));

    private static uint ApplyPaletteHash2(uint color) =>
        (uint)((uint)((color & 0x00ffffffu) * ((1ul << 31) - 1)) >> (32 - PaletteInvSizeBits));

    private static void ApplyPalette(uint[] src, int srcStride, uint[] dst, int dstStride, uint[] palette, int paletteSize,
        int width, int height, int xbits)
    {
        var tmpRow = new byte[width];
        Func<uint, uint> colorIndex;
        if (paletteSize < ApplyPaletteGreedyMax)
        {
            colorIndex = pix => SearchColorGreedy(palette, pix);
        }
        else
        {
            var buffer = new ushort[PaletteInvSize];
            Func<uint, uint>[] hashFunctions = [ApplyPaletteHash0, ApplyPaletteHash1, ApplyPaletteHash2];
            int i;
            for (i = 0; i < 3; ++i)
            {
                bool useLut = true;
                Array.Fill(buffer, (ushort)0xffff);
                for (int j = 0; j < paletteSize; ++j)
                {
                    uint ind = hashFunctions[i](palette[j]);
                    if (buffer[ind] != 0xffff)
                    {
                        useLut = false;
                        break;
                    }
                    buffer[ind] = (ushort)j;
                }
                if (useLut) break;
            }
            if (i < 3)
            {
                var hash = hashFunctions[i];
                colorIndex = pix => buffer[hash(pix)];
            }
            else
            {
                var idxMap = new uint[Vp8lCommon.MaxPaletteSize];
                var paletteSortedLocal = new uint[Vp8lCommon.MaxPaletteSize];
                Vp8lPalette.PrepareMapToPalette(palette, paletteSize, paletteSortedLocal, idxMap);
                colorIndex = pix => idxMap[Vp8lPalette.SearchColorNoIdx(paletteSortedLocal, pix, paletteSize)];
            }
        }

        // APPLY_PALETTE_FOR: one-pixel cache of the last color
        uint prevPix = palette[0];
        uint prevIdx = 0;
        int s = 0, d = 0;
        for (int y = 0; y < height; ++y)
        {
            for (int x = 0; x < width; ++x)
            {
                uint pix = src[s + x];
                if (pix != prevPix)
                {
                    prevIdx = colorIndex(pix);
                    prevPix = pix;
                }
                tmpRow[x] = (byte)prevIdx;
            }
            Vp8lCommon.BundleColorMap(tmpRow, width, xbits, dst, d);
            s += srcStride;
            d += dstStride;
        }
    }

    private void MapImageFromPalette()
    {
        int xbits = _paletteSize <= 4 ? (_paletteSize <= 2 ? 3 : 2) : (_paletteSize <= 16 ? 1 : 0);
        AllocateTransformBuffer(Vp8lCommon.SubSampleSize(_width, xbits), _height);
        ApplyPalette(_picArgb, _width, _argb, _currentWidth, _palette, _paletteSize, _width, _height, xbits);
    }

    private void EncodePalette(Vp8lBitWriter bw, bool lowEffort)
    {
        var tmpPalette = new uint[Vp8lCommon.MaxPaletteSize];
        int paletteSize = _paletteSize;
        int encodedPaletteSize = _palette[paletteSize - 1] == 0 && paletteSize > 17 ? paletteSize - 1 : paletteSize;
        bw.PutBits(Vp8lCommon.TransformPresent, 1);
        bw.PutBits(Vp8lCommon.ColorIndexingTransform, 2);
        bw.PutBits((uint)(encodedPaletteSize - 1), 8);
        for (int i = encodedPaletteSize - 1; i >= 1; --i) tmpPalette[i] = Vp8lCommon.SubPixels(_palette[i], _palette[i - 1]);
        tmpPalette[0] = _palette[0];
        EncodeImageNoHuffman(bw, tmpPalette, _hashChain, [_refs[0], _refs[1]], encodedPaletteSize, 1, 20, lowEffort);
    }

    private void ApplyPredictFilter(int width, int height, int quality, bool lowEffort, bool usedSubtractGreen, Vp8lBitWriter bw,
        out int bestBits)
    {
        int nearLosslessStrength = _usePalette ? 100 : _config.NearLossless;
        int maxBits = ClampBits(width, height, _predictorTransformBits, Vp8lCommon.MinTransformBits, Vp8lCommon.MaxTransformBits,
            MaxPredictorImageSize);
        int minBits = ClampBits(width, height, maxBits - 2 * (_config.Method > 4 ? _config.Method - 4 : 0),
            Vp8lCommon.MinTransformBits, Vp8lCommon.MaxTransformBits, MaxPredictorImageSize);
        Vp8lPredictorEnc.ResidualImage(width, height, minBits, maxBits, lowEffort, _argb, _rows, _maxDiffs, _transformData,
            nearLosslessStrength, _config.Exact, usedSubtractGreen, out bestBits);
        bw.PutBits(Vp8lCommon.TransformPresent, 1);
        bw.PutBits(Vp8lCommon.PredictorTransform, 2);
        bw.PutBits((uint)(bestBits - Vp8lCommon.MinTransformBits), Vp8lCommon.NumTransformBits);
        EncodeImageNoHuffman(bw, _transformData, _hashChain, [_refs[0], _refs[1]], Vp8lCommon.SubSampleSize(width, bestBits),
            Vp8lCommon.SubSampleSize(height, bestBits), quality, lowEffort);
    }

    private void ApplyCrossColorFilter(int width, int height, int quality, bool lowEffort, Vp8lBitWriter bw, out int bestBits)
    {
        Vp8lPredictorEnc.ColorSpaceTransform(width, height, _crossColorTransformBits, quality, _argb, _transformData, out bestBits);
        bw.PutBits(Vp8lCommon.TransformPresent, 1);
        bw.PutBits(Vp8lCommon.CrossColorTransform, 2);
        bw.PutBits((uint)(bestBits - Vp8lCommon.MinTransformBits), Vp8lCommon.NumTransformBits);
        EncodeImageNoHuffman(bw, _transformData, _hashChain, [_refs[0], _refs[1]], Vp8lCommon.SubSampleSize(width, bestBits),
            Vp8lCommon.SubSampleSize(height, bestBits), quality, lowEffort);
    }

    // ---- main ------------------------------------------------------------------------------------------------------

    private void EncodeStream(Vp8lBitWriter bw)
    {
        var crunchConfigs = EncoderAnalyze(out bool redAndBlueAlwaysZero);
        _hashChain = new Vp8lHashChain(_width * _height);

        int quality = (int)_config.Quality;
        bool lowEffort = _config.Method == 0;
        int height = _height;
        long bestSize = long.MaxValue;
        var bwInit = bw.Mark();
        var bwBest = new Vp8lBitWriter(0);
        if (crunchConfigs.Count > 1) bw.CloneTo(bwBest);

        foreach (var crunch in crunchConfigs)
        {
            int entropyIdx = crunch.EntropyIdx;
            _usePalette = entropyIdx == KPalette || entropyIdx == KPaletteAndSpatial;
            _useSubtractGreen = entropyIdx == KSubGreen || entropyIdx == KSpatialSubGreen;
            _usePredict = entropyIdx == KSpatial || entropyIdx == KSpatialSubGreen || entropyIdx == KPaletteAndSpatial;
            _useCrossColor = !(lowEffort || _usePalette) && !redAndBlueAlwaysZero && _usePredict;
            _cacheBits = 0;
            _refs[0].Clear();
            _refs[1].Clear();

            bool useNearLossless = _config.NearLossless < 100 && !_usePalette && !_usePredict;
            bool argbReady = false;
            if (useNearLossless)
            {
                AllocateTransformBuffer(_width, _height);
                Vp8lNearLossless.Apply(_picArgb, _width, _height, _config.NearLossless, _argb);
                argbReady = true;
            }

            if (_usePalette)
            {
                Vp8lPalette.Sort(crunch.PaletteSortingType, _picArgb, _width, _height, _width, _paletteSorted, _paletteSize, _palette);
                EncodePalette(bw, lowEffort);
                MapImageFromPalette();
                argbReady = true;
                if (_paletteSize < 1 << Vp8lCommon.MaxColorCacheBits)
                    _cacheBits = Vp8lCommon.BitsLog2Floor((uint)_paletteSize) + 1;
            }
            if (!argbReady) MakeInputImageCopy();

            if (_useSubtractGreen)
            {
                bw.PutBits(Vp8lCommon.TransformPresent, 1);
                bw.PutBits(Vp8lCommon.SubtractGreenTransform, 2);
                Vp8lCommon.SubtractGreenFromBlueAndRed(_argb, _currentWidth * height);
            }
            if (_usePredict)
            {
                ApplyPredictFilter(_currentWidth, height, quality, lowEffort, _useSubtractGreen, bw, out _);
            }
            if (_useCrossColor)
                ApplyCrossColorFilter(_currentWidth, height, quality, lowEffort, bw, out _);

            bw.PutBits(0, 1);   // no more transforms

            EncodeImageInternal(bw, _argb, _hashChain, _refs, _currentWidth, height, quality, lowEffort, crunch, ref _cacheBits,
                _histoBits);

            if (bw.NumBytes < bestSize)
            {
                bestSize = bw.NumBytes;
                Vp8lBitWriter.Swap(bw, bwBest);
            }
            if (crunchConfigs.Count > 1) bw.ResetTo(bwInit);
        }
        Vp8lBitWriter.Swap(bwBest, bw);
    }
}
