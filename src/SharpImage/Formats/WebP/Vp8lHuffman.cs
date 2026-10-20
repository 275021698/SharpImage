namespace SharpImage.Formats.WebP;

/// <summary>A Huffman code: per-symbol lengths and (bit-reversed, ready to write) codes.</summary>
internal sealed class HuffmanTreeCode
{
    public int NumSymbols;
    public byte[] CodeLengths = [];
    public ushort[] Codes = [];

    public HuffmanTreeCode(int numSymbols)
    {
        NumSymbols = numSymbols;
        CodeLengths = new byte[numSymbols];
        Codes = new ushort[numSymbols];
    }
}

/// <summary>An RLE token of a code-length sequence: a length (0..15) or escape 16/17/18 with its extra bits.</summary>
internal struct HuffmanTreeToken
{
    public byte Code;
    public byte ExtraBits;
}

/// <summary>
/// src/utils/huffman_encode_utils.c, ported line by line: RLE-friendly count smoothing, the length-limited Huffman
/// tree (raise counts to count_min until the depth fits — always a complete code), canonical bit-reversed codes and
/// the RLE tokenisation of a code-length sequence.
/// </summary>
internal static class Vp8lHuffman
{
    private struct HuffmanTree
    {
        public uint TotalCount;
        public int Value;
        public int PoolIndexLeft;
        public int PoolIndexRight;
    }

    private static bool ValuesShouldBeCollapsedToStrideAverage(int a, int b) => Math.Abs(a - b) < 4;

    // OptimizeHuffmanForRle
    private static void OptimizeHuffmanForRle(int length, byte[] goodForRle, uint[] counts)
    {
        int i;
        for (; length >= 0; --length)
        {
            if (length == 0) return;   // All zeros.
            if (counts[length - 1] != 0) break;
        }
        {
            uint symbol = counts[0];
            int stride = 0;
            for (i = 0; i < length + 1; ++i)
            {
                if (i == length || counts[i] != symbol)
                {
                    if ((symbol == 0 && stride >= 5) || (symbol != 0 && stride >= 7))
                    {
                        for (int k = 0; k < stride; ++k) goodForRle[i - k - 1] = 1;
                    }
                    stride = 1;
                    if (i != length) symbol = counts[i];
                }
                else
                {
                    ++stride;
                }
            }
        }
        {
            uint stride = 0;
            uint limit = counts[0];
            uint sum = 0;
            for (i = 0; i < length + 1; ++i)
            {
                if (i == length || goodForRle[i] != 0 || (i != 0 && goodForRle[i - 1] != 0) ||
                    !ValuesShouldBeCollapsedToStrideAverage((int)counts[i], (int)limit))
                {
                    if (stride >= 4 || (stride >= 3 && sum == 0))
                    {
                        uint count = (sum + stride / 2) / stride;
                        if (count < 1) count = 1;
                        if (sum == 0) count = 0;
                        for (uint k = 0; k < stride; ++k) counts[i - k - 1] = count;
                    }
                    stride = 0;
                    sum = 0;
                    if (i < length - 3)
                        limit = (counts[i] + counts[i + 1] + counts[i + 2] + counts[i + 3] + 2) / 4;
                    else if (i < length)
                        limit = counts[i];
                    else
                        limit = 0;
                }
                ++stride;
                if (i != length)
                {
                    sum += counts[i];
                    if (stride >= 4) limit = (sum + stride / 2) / stride;
                }
            }
        }
    }

    private static int CompareHuffmanTrees(HuffmanTree t1, HuffmanTree t2)
    {
        if (t1.TotalCount > t2.TotalCount) return -1;
        if (t1.TotalCount < t2.TotalCount) return 1;
        return t1.Value < t2.Value ? -1 : 1;
    }

    private static void SetBitDepths(HuffmanTree[] pool, int poolBase, in HuffmanTree tree, byte[] bitDepths, int level)
    {
        if (tree.PoolIndexLeft >= 0)
        {
            SetBitDepths(pool, poolBase, pool[poolBase + tree.PoolIndexLeft], bitDepths, level + 1);
            SetBitDepths(pool, poolBase, pool[poolBase + tree.PoolIndexRight], bitDepths, level + 1);
        }
        else
        {
            bitDepths[tree.Value] = (byte)level;
        }
    }

    [ThreadStatic] private static HuffmanTree[]? _treeScratch;
    private static readonly IComparer<HuffmanTree> TreeComparer = Comparer<HuffmanTree>.Create(CompareHuffmanTrees);

    // GenerateOptimalTree: 'tree' holds the active nodes at [0, tree_size), the pool after tree_size_orig.
    private static void GenerateOptimalTree(uint[] histogram, int histogramSize, int treeDepthLimit, byte[] bitDepths)
    {
        int treeSizeOrig = 0;
        for (int i = 0; i < histogramSize; ++i)
            if (histogram[i] != 0) ++treeSizeOrig;
        if (treeSizeOrig == 0) return;

        var tree = _treeScratch;
        if (tree == null || tree.Length < 3 * treeSizeOrig) _treeScratch = tree = new HuffmanTree[Math.Max(3 * treeSizeOrig, 3 * 280)];
        int poolBase = treeSizeOrig;

        for (uint countMin = 1; ; countMin *= 2)
        {
            int treeSize = treeSizeOrig;
            int idx = 0;
            for (int j = 0; j < histogramSize; ++j)
            {
                if (histogram[j] != 0)
                {
                    uint count = histogram[j] < countMin ? countMin : histogram[j];
                    tree[idx].TotalCount = count;
                    tree[idx].Value = j;
                    tree[idx].PoolIndexLeft = -1;
                    tree[idx].PoolIndexRight = -1;
                    ++idx;
                }
            }

            // qsort with a strict total order (counts, then distinct values): any sort gives the same result
            Array.Sort(tree, 0, treeSize, TreeComparer);

            if (treeSize > 1)
            {
                int treePoolSize = 0;
                while (treeSize > 1)
                {
                    tree[poolBase + treePoolSize++] = tree[treeSize - 1];
                    tree[poolBase + treePoolSize++] = tree[treeSize - 2];
                    uint count = tree[poolBase + treePoolSize - 1].TotalCount + tree[poolBase + treePoolSize - 2].TotalCount;
                    treeSize -= 2;
                    int k;
                    for (k = 0; k < treeSize; ++k)
                        if (tree[k].TotalCount <= count) break;
                    Array.Copy(tree, k, tree, k + 1, treeSize - k);
                    tree[k].TotalCount = count;
                    tree[k].Value = -1;
                    tree[k].PoolIndexLeft = treePoolSize - 1;
                    tree[k].PoolIndexRight = treePoolSize - 2;
                    treeSize = treeSize + 1;
                }
                SetBitDepths(tree, poolBase, tree[0], bitDepths, 0);
            }
            else if (treeSize == 1)
            {
                bitDepths[tree[0].Value] = 1;
            }

            int maxDepth = bitDepths[0];
            for (int j = 1; j < histogramSize; ++j)
                if (maxDepth < bitDepths[j]) maxDepth = bitDepths[j];
            if (maxDepth <= treeDepthLimit) break;
        }
    }

    private static int CodeRepeatedValues(int repetitions, HuffmanTreeToken[] tokens, int t, int value, int prevValue)
    {
        if (value != prevValue)
        {
            tokens[t].Code = (byte)value;
            tokens[t].ExtraBits = 0;
            ++t;
            --repetitions;
        }
        while (repetitions >= 1)
        {
            if (repetitions < 3)
            {
                for (int i = 0; i < repetitions; ++i)
                {
                    tokens[t].Code = (byte)value;
                    tokens[t].ExtraBits = 0;
                    ++t;
                }
                break;
            }
            else if (repetitions < 7)
            {
                tokens[t].Code = 16;
                tokens[t].ExtraBits = (byte)(repetitions - 3);
                ++t;
                break;
            }
            else
            {
                tokens[t].Code = 16;
                tokens[t].ExtraBits = 3;
                ++t;
                repetitions -= 6;
            }
        }
        return t;
    }

    private static int CodeRepeatedZeros(int repetitions, HuffmanTreeToken[] tokens, int t)
    {
        while (repetitions >= 1)
        {
            if (repetitions < 3)
            {
                for (int i = 0; i < repetitions; ++i)
                {
                    tokens[t].Code = 0;
                    tokens[t].ExtraBits = 0;
                    ++t;
                }
                break;
            }
            else if (repetitions < 11)
            {
                tokens[t].Code = 17;
                tokens[t].ExtraBits = (byte)(repetitions - 3);
                ++t;
                break;
            }
            else if (repetitions < 139)
            {
                tokens[t].Code = 18;
                tokens[t].ExtraBits = (byte)(repetitions - 11);
                ++t;
                break;
            }
            else
            {
                tokens[t].Code = 18;
                tokens[t].ExtraBits = 0x7f;
                ++t;
                repetitions -= 138;
            }
        }
        return t;
    }

    /// <summary>VP8LCreateCompressedHuffmanTree: the RLE tokens of tree's code lengths; returns the token count.</summary>
    public static int CreateCompressedHuffmanTree(HuffmanTreeCode tree, HuffmanTreeToken[] tokens)
    {
        int depthSize = tree.NumSymbols;
        int prevValue = 8;
        int i = 0, t = 0;
        while (i < depthSize)
        {
            int value = tree.CodeLengths[i];
            int k = i + 1;
            while (k < depthSize && tree.CodeLengths[k] == value) ++k;
            int runs = k - i;
            if (value == 0)
            {
                t = CodeRepeatedZeros(runs, tokens, t);
            }
            else
            {
                t = CodeRepeatedValues(runs, tokens, t, value, prevValue);
                prevValue = value;
            }
            i += runs;
        }
        return t;
    }

    private static readonly byte[] ReversedBits = [0x0, 0x8, 0x4, 0xc, 0x2, 0xa, 0x6, 0xe, 0x1, 0x9, 0x5, 0xd, 0x3, 0xb, 0x7, 0xf];

    private static uint ReverseBits(int numBits, uint bits)
    {
        uint retval = 0;
        int i = 0;
        while (i < numBits)
        {
            i += 4;
            retval |= (uint)ReversedBits[bits & 0xf] << (Vp8lCommon.MaxAllowedCodeLength + 1 - i);
            bits >>= 4;
        }
        retval >>= Vp8lCommon.MaxAllowedCodeLength + 1 - numBits;
        return retval;
    }

    private static void ConvertBitDepthsToSymbols(HuffmanTreeCode tree)
    {
        Span<uint> nextCode = stackalloc uint[Vp8lCommon.MaxAllowedCodeLength + 1];
        Span<int> depthCount = stackalloc int[Vp8lCommon.MaxAllowedCodeLength + 1];
        depthCount.Clear();
        int len = tree.NumSymbols;
        for (int i = 0; i < len; ++i) ++depthCount[tree.CodeLengths[i]];
        depthCount[0] = 0;
        nextCode[0] = 0;
        uint code = 0;
        for (int i = 1; i <= Vp8lCommon.MaxAllowedCodeLength; ++i)
        {
            code = (code + (uint)depthCount[i - 1]) << 1;
            nextCode[i] = code;
        }
        for (int i = 0; i < len; ++i)
        {
            int codeLength = tree.CodeLengths[i];
            tree.Codes[i] = (ushort)ReverseBits(codeLength, nextCode[codeLength]++);
        }
    }

    /// <summary>VP8LCreateHuffmanTree: modifies 'histogram' (RLE smoothing) and fills huffCode's lengths and codes.
    /// huffCode's lengths must be zero on entry (as libwebp's calloc'd buffers are).</summary>
    public static void CreateHuffmanTree(uint[] histogram, int treeDepthLimit, byte[] bufRle, HuffmanTreeCode huffCode)
    {
        int numSymbols = huffCode.NumSymbols;
        Array.Clear(bufRle, 0, numSymbols);
        OptimizeHuffmanForRle(numSymbols, bufRle, histogram);
        GenerateOptimalTree(histogram, numSymbols, treeDepthLimit, huffCode.CodeLengths);
        ConvertBitDepthsToSymbols(huffCode);
    }
}
