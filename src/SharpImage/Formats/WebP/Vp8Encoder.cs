namespace SharpImage.Formats.WebP;

/// <summary>The WebPConfig fields the lossy (VP8) encoder reads; defaults are WebPConfigInit's (= cwebp's).</summary>
public sealed class WebpLossyOptions
{
    /// <summary>0..100 (cwebp -q). Default 75.</summary>
    public float Quality { get; set; } = 75;

    /// <summary>0 (fast) .. 6 (slowest, best) (cwebp -m). Default 4.</summary>
    public int Method { get; set; } = 4;

    /// <summary>Target size in bytes; non-zero enables the multi-pass size search (cwebp -size). Default 0.</summary>
    public int TargetSize { get; set; }

    /// <summary>Target PSNR in dB; non-zero enables the multi-pass PSNR search (cwebp -psnr). Default 0.</summary>
    public float TargetPsnr { get; set; }

    /// <summary>Number of segments 1..4 (cwebp -segments). Default 4.</summary>
    public int Segments { get; set; } = 4;

    /// <summary>Spatial noise shaping 0..100 (cwebp -sns). Default 50.</summary>
    public int SnsStrength { get; set; } = 50;

    /// <summary>Loop filter strength 0..100 (cwebp -f). Default 60.</summary>
    public int FilterStrength { get; set; } = 60;

    /// <summary>Loop filter sharpness 0..7 (cwebp -sharpness). Default 0.</summary>
    public int FilterSharpness { get; set; }

    /// <summary>0 = simple, 1 = strong filter (cwebp -strong / -nostrong). Default 1.</summary>
    public int FilterType { get; set; } = 1;

    /// <summary>Auto-adjust the filter strength (cwebp -af). Default false.</summary>
    public bool Autofilter { get; set; }

    /// <summary>Number of entropy-analysis passes 1..10 (cwebp -pass). Default 1.</summary>
    public int Pass { get; set; } = 1;

    /// <summary>Minimum / maximum quality for the size / PSNR search (cwebp -qrange). Defaults 0 / 100.</summary>
    public int QMin { get; set; }
    public int QMax { get; set; } = 100;

    /// <summary>Bit 0: segment smoothing, bit 1: pseudo-random dithering (cwebp -pre). Default 0.</summary>
    public int Preprocessing { get; set; }

    /// <summary>log2 of the number of token partitions 0..3 (cwebp -partitions). Default 0.</summary>
    public int Partitions { get; set; }

    /// <summary>Quality degradation allowed to fit the 512k partition-0 limit, 0..100 (cwebp -partition_limit).</summary>
    public int PartitionLimit { get; set; }

    /// <summary>Match the expected size of a JPEG of the same quality (cwebp -jpeg_like). Default false.</summary>
    public bool EmulateJpegSize { get; set; }

    /// <summary>Alpha plane: 0 = raw, 1 = lossless-compressed (cwebp -alpha_method). Default 1.</summary>
    public int AlphaCompression { get; set; } = 1;

    /// <summary>Alpha predictive filter: 0 none, 1 fast, 2 best (cwebp -alpha_filter). Default 1.</summary>
    public int AlphaFiltering { get; set; } = 1;

    /// <summary>Alpha quality 0..100, below 100 the levels are quantized (cwebp -alpha_q). Default 100.</summary>
    public int AlphaQuality { get; set; } = 100;

    /// <summary>Keep the RGB under fully transparent pixels (cwebp -exact). Default false.</summary>
    public bool Exact { get; set; }

    internal void Validate()
    {
        static void Check(bool ok, string what)
        {
            if (!ok) throw new ArgumentOutOfRangeException(what);
        }
        Check(Quality >= 0 && Quality <= 100, nameof(Quality));
        Check(TargetSize >= 0, nameof(TargetSize));
        Check(TargetPsnr >= 0, nameof(TargetPsnr));
        Check(Method >= 0 && Method <= 6, nameof(Method));
        Check(Segments >= 1 && Segments <= 4, nameof(Segments));
        Check(SnsStrength >= 0 && SnsStrength <= 100, nameof(SnsStrength));
        Check(FilterStrength >= 0 && FilterStrength <= 100, nameof(FilterStrength));
        Check(FilterSharpness >= 0 && FilterSharpness <= 7, nameof(FilterSharpness));
        Check(FilterType >= 0 && FilterType <= 1, nameof(FilterType));
        Check(Pass >= 1 && Pass <= 10, nameof(Pass));
        Check(QMin >= 0 && QMax <= 100 && QMin <= QMax, nameof(QMin));
        Check(Preprocessing >= 0 && Preprocessing <= 3, nameof(Preprocessing));
        Check(Partitions >= 0 && Partitions <= 3, nameof(Partitions));
        Check(PartitionLimit >= 0 && PartitionLimit <= 100, nameof(PartitionLimit));
        Check(AlphaCompression >= 0 && AlphaCompression <= 1, nameof(AlphaCompression));
        Check(AlphaFiltering >= 0 && AlphaFiltering <= 2, nameof(AlphaFiltering));
        Check(AlphaQuality >= 0 && AlphaQuality <= 100, nameof(AlphaQuality));
    }
}

/// <summary>VP8MBInfo.</summary>
internal struct Vp8MbInfo
{
    public byte Type;     // 0=i4x4, 1=i16x16
    public byte UvMode;
    public byte Skip;
    public byte Segment;
    public byte Alpha;    // quantization-susceptibility
}

/// <summary>VP8SegmentInfo.</summary>
internal sealed class Vp8SegmentInfo
{
    public readonly Vp8Matrix Y1 = new(), Y2 = new(), Uv = new();   // quantization matrices
    public int Alpha;       // quant-susceptibility, range [-127,127]. Zero is neutral.
    public int Beta;        // filter-susceptibility, range [0,255].
    public int Quant;       // final segment quantizer.
    public int FStrength;   // final in-loop filtering strength
    public int MaxEdge;     // max edge delta (for filtering strength)
    public int MinDisto;    // minimum distortion required to trigger filtering record
    public int LambdaI16, LambdaI4, LambdaUv;
    public int LambdaMode, LambdaTrellis, TLambda;
    public int LambdaTrellisI16, LambdaTrellisI4, LambdaTrellisUv;
    public long I4Penalty;  // penalty for using Intra4

    public void CopyFrom(Vp8SegmentInfo o)
    {
        Y1.CopyFrom(o.Y1);
        Y2.CopyFrom(o.Y2);
        Uv.CopyFrom(o.Uv);
        Alpha = o.Alpha;
        Beta = o.Beta;
        Quant = o.Quant;
        FStrength = o.FStrength;
        MaxEdge = o.MaxEdge;
        MinDisto = o.MinDisto;
        LambdaI16 = o.LambdaI16;
        LambdaI4 = o.LambdaI4;
        LambdaUv = o.LambdaUv;
        LambdaMode = o.LambdaMode;
        LambdaTrellis = o.LambdaTrellis;
        TLambda = o.TLambda;
        LambdaTrellisI16 = o.LambdaTrellisI16;
        LambdaTrellisI4 = o.LambdaTrellisI4;
        LambdaTrellisUv = o.LambdaTrellisUv;
        I4Penalty = o.I4Penalty;
    }
}

/// <summary>VP8EncProba: coefficient probabilities [type][band][ctx][proba] (flat), their statistics and costs.</summary>
internal sealed class Vp8EncProba
{
    public const int NumTypes = 4, NumBands = 8, NumCtx = 3, NumProbas = 11;
    public const int MaxVariableLevel = 67;
    public const int CostRow = MaxVariableLevel + 1;

    public readonly byte[] Segments = new byte[3];   // probabilities for segment tree
    public int SkipProba;                             // final probability of being skipped.
    public readonly byte[] Coeffs = new byte[NumTypes * NumBands * NumCtx * NumProbas];
    public readonly uint[] Stats = new uint[NumTypes * NumBands * NumCtx * NumProbas];
    public readonly ushort[] LevelCost = new ushort[NumTypes * NumBands * NumCtx * CostRow];
    public readonly int[] RemappedCosts = new int[NumTypes * 16 * NumCtx];   // offsets of LevelCost rows
    public bool Dirty;          // if true, need to call VP8CalculateLevelCosts()
    public bool UseSkipProba;   // Note: we always use skip_proba for now.
    public int NbSkip;          // number of skipped blocks

    public static int ProbaIdx(int t, int b, int c) => ((t * NumBands + b) * NumCtx + c) * NumProbas;
    public static int CostIdx(int t, int b, int c) => ((t * NumBands + b) * NumCtx + c) * CostRow;

    /// <summary>costs[n][ctx] of coefficient type t (remapped_costs): the offset of a LevelCost row.</summary>
    public int Costs(int t, int n, int ctx) => RemappedCosts[(t * 16 + n) * NumCtx + ctx];
}

/// <summary>VP8ModeScore.</summary>
internal sealed class Vp8ModeScore
{
    public long D, SD;              // Distortion, spectral distortion
    public long H, R, Score;        // header bits, rate, score.
    public readonly short[] YDcLevels = new short[16];         // Quantized levels for luma-DC, luma-AC, chroma.
    public readonly short[] YAcLevels = new short[16 * 16];
    public readonly short[] UvLevels = new short[(4 + 4) * 16];
    public int ModeI16;                                        // mode number for intra16 prediction
    public readonly byte[] ModesI4 = new byte[16];             // mode numbers for intra4 predictions
    public int ModeUv;                                         // mode number of chroma prediction
    public uint Nz;                                            // non-zero blocks
    public readonly sbyte[] Derr = new sbyte[2 * 3];           // DC diffusion errors for U/V for blocks #1/2/3

    /// <summary>memcpy(this, o, sizeof(VP8ModeScore)).</summary>
    public void CopyAllFrom(Vp8ModeScore o)
    {
        D = o.D;
        SD = o.SD;
        H = o.H;
        R = o.R;
        Score = o.Score;
        o.YDcLevels.CopyTo(YDcLevels, 0);
        o.YAcLevels.CopyTo(YAcLevels, 0);
        o.UvLevels.CopyTo(UvLevels, 0);
        ModeI16 = o.ModeI16;
        o.ModesI4.CopyTo(ModesI4, 0);
        ModeUv = o.ModeUv;
        Nz = o.Nz;
        o.Derr.CopyTo(Derr, 0);
    }
}

/// <summary>
/// The WebP lossy (VP8) encoder: libwebp 1.6.0 src/enc (webp_enc.c, analysis_enc.c, iterator_enc.c, quant_enc.c,
/// cost_enc.c, frame_enc.c, token_enc.c, tree_enc.c, filter_enc.c, syntax_enc.c, alpha_enc.c) ported line by line,
/// single-threaded (thread_level only changes scheduling in libwebp, never the output). Same pixels and options give
/// the same file as WebPEncode.
/// </summary>
internal sealed partial class Vp8Encoder
{
    private const int Bps = Vp8EncDsp.Bps;
    private const int NumMbSegments = 4;
    private const int MaxLfLevels = 64;
    private const int NumBModes = 10;
    private const int NumPredModes = 4;
    private const int MaxNumPartitions = 8;
    private const long MaxCost = 0x7fffffffffffffL;
    private const int ErrorDiffusionQuality = 98;

    // RD levels
    private const int RdOptNone = 0, RdOptBasic = 1, RdOptTrellis = 2, RdOptTrellisAll = 3;

    // block offsets inside the iterator's yuv buffers
    private const int YuvSizeEnc = Bps * 16;
    private const int YOffEnc = 0, UOffEnc = 16, VOffEnc = 16 + 8;

    private readonly WebpLossyOptions _config;
    private readonly Vp8EncPicture _pic;

    // VP8EncFilterHeader
    private bool _filterSimple;
    private int _filterLevel, _filterSharpness, _i4x4LfDelta;

    // VP8EncSegmentHeader
    private int _numSegments;
    private bool _updateMap;
    private int _segmentHdrSize;

    private readonly int _profile;
    private readonly int _mbW, _mbH;
    private readonly int _predsW;
    private int _numParts;
    private Vp8BitWriter _bw = null!;
    private readonly Vp8BitWriter[] _parts = new Vp8BitWriter[MaxNumPartitions];
    private readonly TokenBuffer _tokens = new();

    private bool _hasAlpha;
    private byte[]? _alphaData;

    private readonly Vp8SegmentInfo[] _dqm = [new(), new(), new(), new()];
    private int _baseQuant;
    private int _alpha;       // global susceptibility (<=> complexity)
    private int _uvAlpha;     // U/V quantization susceptibility
    private int _dqY1Dc, _dqY2Dc, _dqY2Ac, _dqUvDc, _dqUvAc;

    private readonly Vp8EncProba _proba = new();

    private readonly int _method;
    private readonly int _rdOptLevel;
    private int _maxI4HeaderBits;
    private readonly long _mbHeaderLimit;
    private readonly bool _doSearch;
    private readonly bool _useTokens;

    private readonly Vp8MbInfo[] _mbInfo;
    private readonly byte[] _predsMem;   // (4*mb_w+1) * (4*mb_h+1)
    private readonly int _preds;         // enc->preds: index of the first block's mode (row -1 / column -1 addressable)
    private readonly uint[] _nzMem;      // nz[-1 .. mb_w-1]
    private readonly byte[] _top;        // y_top (mb_w * 16) then uv_top (mb_w * 16)
    private readonly double[]? _lfStats; // [segment][level], autofilter only
    private readonly sbyte[]? _topDerr;  // [mb_w][2 u/v][2] diffusion error, or null if disabled

    private Vp8Encoder(WebpLossyOptions config, Vp8EncPicture picture)
    {
        _config = config;
        _pic = picture;
        bool useFilter = config.FilterStrength > 0 || config.Autofilter;
        _mbW = (picture.Width + 15) >> 4;
        _mbH = (picture.Height + 15) >> 4;
        _predsW = 4 * _mbW + 1;
        int predsH = 4 * _mbH + 1;
        _mbInfo = new Vp8MbInfo[_mbW * _mbH];
        _predsMem = new byte[_predsW * predsH];
        _preds = 1 + _predsW;
        _nzMem = new uint[_mbW + 1];
        _top = new byte[2 * _mbW * 16];
        if (config.Autofilter) _lfStats = new double[NumMbSegments * MaxLfLevels];
        if (config.Quality <= ErrorDiffusionQuality || config.Pass > 1) _topDerr = new sbyte[_mbW * 4];

        _numParts = 1 << config.Partitions;
        _profile = useFilter ? (config.FilterType == 1 ? 0 : 1) : 2;

        // MapConfigToTools
        int method = config.Method;
        int limit = 100 - config.PartitionLimit;
        _method = method;
        _rdOptLevel = method >= 6 ? RdOptTrellisAll
                    : method >= 5 ? RdOptTrellis
                    : method >= 3 ? RdOptBasic
                    : RdOptNone;
        _maxI4HeaderBits = 256 * 16 * 16 *    // upper bound: up to 16bit per 4x4 block
                           (limit * limit) / (100 * 100);  // ... modulated with a quadratic curve.
        _mbHeaderLimit = (long)256 * 510 * 8 * 1024 / (_mbW * _mbH);
        _doSearch = config.TargetSize > 0 || config.TargetPsnr > 0;
        _useTokens = _rdOptLevel >= RdOptBasic;  // need rd stats
        if (_useTokens) _numParts = 1;   // doesn't work with multi-partition

        DefaultProbas();
        // ResetSegmentHeader
        _numSegments = config.Segments;
        _updateMap = _numSegments > 1;
        _segmentHdrSize = 0;
        // ResetFilterHeader
        _filterSimple = true;
        _filterLevel = 0;
        _filterSharpness = 0;
        _i4x4LfDelta = 0;
        ResetBoundaryPredictions();
        _hasAlpha = picture.HasTransparency();   // VP8EncInitAlpha
        float scale = 1f + config.Quality * 5f / 100f;  // in [1,6]
        _tokens.Init((int)(_mbW * _mbH * 4 * scale));
    }

    /// <summary>WebPEncode (lossy path) for a 0xAARRGGBB picture: the complete RIFF/WebP file.</summary>
    public static byte[] Encode(uint[] argb, int width, int height, WebpLossyOptions config)
    {
        config.Validate();
        if (width <= 0 || height <= 0 || width > 16383 || height > 16383)
            throw new ArgumentException("WebP dimensions must be in 1..16383.");
        float dithering = 0f;
        if ((config.Preprocessing & 2) != 0)
        {
            float x = config.Quality / 100f;
            float x2 = x * x;
            dithering = 1.0f + (0.5f - 1.0f) * x2 * x2;
        }
        var pic = Vp8EncPicture.FromArgb(argb, width, height, dithering);
        if (!config.Exact) pic.CleanupTransparentArea();
        var enc = new Vp8Encoder(config, pic);
        enc.Analyze();
        if (enc._hasAlpha) enc._alphaData = WebpAlphaEncoder.Encode(pic, config);
        if (!enc._useTokens)
            enc.EncLoop();
        else
            enc.EncTokenLoop();
        return enc.EncWrite();
    }

    private void ResetBoundaryPredictions()
    {
        // enc->preds - enc->preds_w and enc->preds - 1
        int top = _preds - _predsW;
        int left = _preds - 1;
        for (int i = -1; i < 4 * _mbW; ++i) _predsMem[top + i] = 0;   // B_DC_PRED
        for (int i = 0; i < 4 * _mbH; ++i) _predsMem[left + i * _predsW] = 0;
        _nzMem[0] = 0;   // nz[-1], constant
    }

    // ---- iterator (iterator_enc.c) -----------------------------------------------------------------------------------

    /// <summary>VP8EncIterator.</summary>
    private sealed class Iterator
    {
        public readonly Vp8Encoder Enc;
        public int X, Y;                         // current macroblock
        public readonly byte[] Yuv = new byte[3 * YuvSizeEnc + 32 * Bps + 16 * Bps + 8 * Bps];
        public const int YuvIn = 0;              // input samples
        public int YuvOut = YuvSizeEnc;          // output samples
        public int YuvOut2 = 2 * YuvSizeEnc;     // secondary buffer swapped with yuv_out.
        public const int YuvP = 3 * YuvSizeEnc;  // scratch buffer for prediction
        public int Mb;                           // current macroblock (index in mb_info)
        public Vp8BitWriter Bw = null!;          // current bit-writer
        public int Preds;                        // intra mode predictors (index in preds_mem)
        public int Nz;                           // non-zero pattern (index in nz_mem; nz[-1] = Nz - 1)
        public readonly byte[] I4Boundary = new byte[37];   // 32+5 boundary samples needed by intra4x4
        public int I4Top;                        // the current top boundary sample (index in i4_boundary)
        public int I4;                           // current intra4x4 mode being tested
        public readonly int[] TopNz = new int[9];   // top-non-zero context.
        public readonly int[] LeftNz = new int[9];  // left-non-zero. left_nz[8] is independent.
        public readonly ulong[] BitCount = new ulong[4 * 3];   // bit counters for coded levels.
        public ulong LumaBits, UvBits;           // macroblock bit-cost for luma / chroma
        public bool DoTrellis;                   // if true, perform extra level optimisation
        public int CountDown;                    // number of mb still to be processed
        public readonly sbyte[] LeftDerr = new sbyte[4];   // left error diffusion [u/v][2]
        public readonly bool HasDerr;            // top_derr != NULL
        // yuv_left_mem: y_left[-1..15] at 0..16, u_left[-1..7] at 32..40, v_left[-1..7] at 48..56
        public readonly byte[] Left = new byte[57];
        public const int YLeft = 1, ULeft = 33, VLeft = 49;
        public byte[] TopBuf;                    // where y_top / uv_top point (enc->y_top or the import scratch)
        public int YTop, UvTop;

        public Iterator(Vp8Encoder enc)
        {
            Enc = enc;
            HasDerr = enc._topDerr != null;
            TopBuf = enc._top;
            Reset();
        }

        private void InitLeft()
        {
            Left[YLeft - 1] = Left[ULeft - 1] = Left[VLeft - 1] = Y > 0 ? (byte)129 : (byte)127;
            Left.AsSpan(YLeft, 16).Fill(129);
            Left.AsSpan(ULeft, 8).Fill(129);
            Left.AsSpan(VLeft, 8).Fill(129);
            LeftNz[8] = 0;
            if (HasDerr) Array.Clear(LeftDerr);
        }

        private void InitTop()
        {
            var enc = Enc;
            Array.Fill(enc._top, (byte)127);
            Array.Clear(enc._nzMem, 1, enc._mbW);
            if (enc._topDerr != null) Array.Clear(enc._topDerr);
        }

        /// <summary>VP8IteratorSetRow.</summary>
        public void SetRow(int y)
        {
            var enc = Enc;
            X = 0;
            Y = y;
            Bw = enc._parts[y & (enc._numParts - 1)];
            Preds = enc._preds + y * 4 * enc._predsW;
            Nz = 1;
            Mb = y * enc._mbW;
            TopBuf = enc._top;
            YTop = 0;
            UvTop = enc._mbW * 16;
            InitLeft();
        }

        private void Reset()
        {
            SetRow(0);
            CountDown = Enc._mbW * Enc._mbH;  // default
            InitTop();
            Array.Clear(BitCount);
            DoTrellis = false;
        }

        public bool IsDone => CountDown <= 0;

        private static void ImportBlock(byte[] src, int s, int srcStride, byte[] dst, int d, int w, int h, int size)
        {
            for (int i = 0; i < h; ++i)
            {
                Array.Copy(src, s, dst, d, w);
                if (w < size) dst.AsSpan(d + w, size - w).Fill(dst[d + w - 1]);
                d += Bps;
                s += srcStride;
            }
            for (int i = h; i < size; ++i)
            {
                Array.Copy(dst, d - Bps, dst, d, size);
                d += Bps;
            }
        }

        private static void ImportLine(byte[] src, int s, int srcStride, byte[] dst, int d, int len, int totalLen)
        {
            int i;
            for (i = 0; i < len; ++i, s += srcStride) dst[d + i] = src[s];
            for (; i < totalLen; ++i) dst[d + i] = dst[d + len - 1];
        }

        /// <summary>VP8IteratorImport. With 'tmp32' (analysis), the left / top samples come from the source.</summary>
        public void Import(byte[]? tmp32)
        {
            var pic = Enc._pic;
            int x = X, y = Y;
            int ysrc = (y * pic.YStride + x) * 16;
            int usrc = (y * pic.UvStride + x) * 8;
            int vsrc = usrc;
            int w = Math.Min(pic.Width - x * 16, 16);
            int h = Math.Min(pic.Height - y * 16, 16);
            int uvW = (w + 1) >> 1;
            int uvH = (h + 1) >> 1;
            ImportBlock(pic.Y, ysrc, pic.YStride, Yuv, YuvIn + YOffEnc, w, h, 16);
            ImportBlock(pic.U, usrc, pic.UvStride, Yuv, YuvIn + UOffEnc, uvW, uvH, 8);
            ImportBlock(pic.V, vsrc, pic.UvStride, Yuv, YuvIn + VOffEnc, uvW, uvH, 8);
            if (tmp32 == null) return;

            // Import source (uncompressed) samples into boundary.
            if (x == 0)
            {
                InitLeft();
            }
            else
            {
                if (y == 0)
                {
                    Left[YLeft - 1] = Left[ULeft - 1] = Left[VLeft - 1] = 127;
                }
                else
                {
                    Left[YLeft - 1] = pic.Y[ysrc - 1 - pic.YStride];
                    Left[ULeft - 1] = pic.U[usrc - 1 - pic.UvStride];
                    Left[VLeft - 1] = pic.V[vsrc - 1 - pic.UvStride];
                }
                ImportLine(pic.Y, ysrc - 1, pic.YStride, Left, YLeft, h, 16);
                ImportLine(pic.U, usrc - 1, pic.UvStride, Left, ULeft, uvH, 8);
                ImportLine(pic.V, vsrc - 1, pic.UvStride, Left, VLeft, uvH, 8);
            }

            TopBuf = tmp32;
            YTop = 0;
            UvTop = 16;
            if (y == 0)
            {
                Array.Fill(tmp32, (byte)127, 0, 32);
            }
            else
            {
                ImportLine(pic.Y, ysrc - pic.YStride, 1, tmp32, 0, w, 16);
                ImportLine(pic.U, usrc - pic.UvStride, 1, tmp32, 16, uvW, 8);
                ImportLine(pic.V, vsrc - pic.UvStride, 1, tmp32, 16 + 8, uvW, 8);
            }
        }

        private static int Bit(uint nz, int n) => (nz & (1u << n)) != 0 ? 1 : 0;

        /// <summary>VP8IteratorNzToBytes.</summary>
        public void NzToBytes()
        {
            uint tnz = Enc._nzMem[Nz], lnz = Enc._nzMem[Nz - 1];
            TopNz[0] = Bit(tnz, 12);
            TopNz[1] = Bit(tnz, 13);
            TopNz[2] = Bit(tnz, 14);
            TopNz[3] = Bit(tnz, 15);
            // Top-U
            TopNz[4] = Bit(tnz, 18);
            TopNz[5] = Bit(tnz, 19);
            // Top-V
            TopNz[6] = Bit(tnz, 22);
            TopNz[7] = Bit(tnz, 23);
            // DC
            TopNz[8] = Bit(tnz, 24);

            // left-Y
            LeftNz[0] = Bit(lnz, 3);
            LeftNz[1] = Bit(lnz, 7);
            LeftNz[2] = Bit(lnz, 11);
            LeftNz[3] = Bit(lnz, 15);
            // left-U
            LeftNz[4] = Bit(lnz, 17);
            LeftNz[5] = Bit(lnz, 19);
            // left-V
            LeftNz[6] = Bit(lnz, 21);
            LeftNz[7] = Bit(lnz, 23);
            // left-DC is special, iterated separately
        }

        /// <summary>VP8IteratorBytesToNz.</summary>
        public void BytesToNz()
        {
            uint nz = 0;
            // top
            nz |= (uint)((TopNz[0] << 12) | (TopNz[1] << 13));
            nz |= (uint)((TopNz[2] << 14) | (TopNz[3] << 15));
            nz |= (uint)((TopNz[4] << 18) | (TopNz[5] << 19));
            nz |= (uint)((TopNz[6] << 22) | (TopNz[7] << 23));
            nz |= (uint)(TopNz[8] << 24);  // we propagate the _top_ bit, esp. for intra4
            // left
            nz |= (uint)((LeftNz[0] << 3) | (LeftNz[1] << 7));
            nz |= (uint)(LeftNz[2] << 11);
            nz |= (uint)((LeftNz[4] << 17) | (LeftNz[6] << 21));
            Enc._nzMem[Nz] = nz;
        }

        /// <summary>VP8IteratorSaveBoundary.</summary>
        public void SaveBoundary()
        {
            var enc = Enc;
            int x = X, y = Y;
            int ysrc = YuvOut + YOffEnc;
            int uvsrc = YuvOut + UOffEnc;
            if (x < enc._mbW - 1)
            {
                // left
                for (int i = 0; i < 16; ++i) Left[YLeft + i] = Yuv[ysrc + 15 + i * Bps];
                for (int i = 0; i < 8; ++i)
                {
                    Left[ULeft + i] = Yuv[uvsrc + 7 + i * Bps];
                    Left[VLeft + i] = Yuv[uvsrc + 15 + i * Bps];
                }
                // top-left (before 'top'!)
                Left[YLeft - 1] = TopBuf[YTop + 15];
                Left[ULeft - 1] = TopBuf[UvTop + 0 + 7];
                Left[VLeft - 1] = TopBuf[UvTop + 8 + 7];
            }
            if (y < enc._mbH - 1)
            {
                // top
                Array.Copy(Yuv, ysrc + 15 * Bps, TopBuf, YTop, 16);
                Array.Copy(Yuv, uvsrc + 7 * Bps, TopBuf, UvTop, 8 + 8);
            }
        }

        /// <summary>VP8IteratorNext.</summary>
        public bool Next()
        {
            if (++X == Enc._mbW)
            {
                SetRow(++Y);
            }
            else
            {
                Preds += 4;
                Mb += 1;
                Nz += 1;
                YTop += 16;
                UvTop += 16;
            }
            return 0 < --CountDown;
        }

        // Helper functions to set mode properties
        public void SetIntra16Mode(int mode)
        {
            int preds = Preds;
            for (int y = 0; y < 4; ++y)
            {
                Enc._predsMem.AsSpan(preds, 4).Fill((byte)mode);
                preds += Enc._predsW;
            }
            Enc._mbInfo[Mb].Type = 1;
        }

        public void SetIntra4Mode(byte[] modes)
        {
            int preds = Preds;
            for (int y = 0; y < 4; ++y)
            {
                Array.Copy(modes, y * 4, Enc._predsMem, preds, 4);
                preds += Enc._predsW;
            }
            Enc._mbInfo[Mb].Type = 0;
        }

        public void SetIntraUVMode(int mode) => Enc._mbInfo[Mb].UvMode = (byte)mode;
        public void SetSkip(bool skip) => Enc._mbInfo[Mb].Skip = skip ? (byte)1 : (byte)0;
        public void SetSegment(int segment) => Enc._mbInfo[Mb].Segment = (byte)segment;

        private static readonly byte[] TopLeftI4 = [17, 21, 25, 29, 13, 17, 21, 25, 9, 13, 17, 21, 5, 9, 13, 17];

        /// <summary>VP8IteratorStartI4.</summary>
        public void StartI4()
        {
            var enc = Enc;
            I4 = 0;    // first 4x4 sub-block
            I4Top = TopLeftI4[0];
            // Import the boundary samples
            for (int i = 0; i < 17; ++i) I4Boundary[i] = Left[YLeft + 15 - i];   // left
            for (int i = 0; i < 16; ++i) I4Boundary[17 + i] = TopBuf[YTop + i];   // top
            // top-right samples have a special case on the far right of the picture
            if (X < enc._mbW - 1)
            {
                for (int i = 16; i < 16 + 4; ++i) I4Boundary[17 + i] = TopBuf[YTop + i];
            }
            else
            {
                // else, replicate the last valid pixel four times
                for (int i = 16; i < 16 + 4; ++i) I4Boundary[17 + i] = I4Boundary[17 + 15];
            }
            NzToBytes();  // import the non-zero context
        }

        /// <summary>VP8IteratorRotateI4: 'yuvOut' is the offset (in Yuv) of the reconstructed luma block.</summary>
        public bool RotateI4(int yuvOut)
        {
            int blk = yuvOut + Vp8EncDsp.DspScan[I4];
            int top = I4Top;
            for (int i = 0; i <= 3; ++i) I4Boundary[top - 4 + i] = Yuv[blk + i + 3 * Bps];   // store future top samples
            if ((I4 & 3) != 3)
            {
                // if not on the right sub-blocks #3, #7, #11, #15
                for (int i = 0; i <= 2; ++i) I4Boundary[top + i] = Yuv[blk + 3 + (2 - i) * Bps];   // store future left samples
            }
            else
            {
                // else replicate top-right samples, as says the specs.
                for (int i = 0; i <= 3; ++i) I4Boundary[top + i] = I4Boundary[top + i + 4];
            }
            // move pointers to next sub-block
            ++I4;
            if (I4 == 16) return false;  // we're done
            I4Top = TopLeftI4[I4];
            return true;
        }

        // the top / left predictor samples, null when not available
        public byte[]? YLeftBuf => X != 0 ? Left : null;
        public byte[]? YTopBuf => Y != 0 ? TopBuf : null;
    }

    // ---- analysis (analysis_enc.c) -----------------------------------------------------------------------------------

    private const int MaxItersKMeans = 6;
    private const int MaxAlpha = 255;               // 8b of precision for susceptibilities.
    private const int AlphaScale = 2 * MaxAlpha;    // scaling factor for alpha.
    private const int DefaultAlpha = -1;

    private static int Clip(int v, int m, int M) => v < m ? m : v > M ? M : v;

    private void SmoothSegmentMap()
    {
        int w = _mbW, h = _mbH;
        const int majorityCnt3x3Grid = 5;
        var tmp = new byte[w * h];
        Span<int> cnt = stackalloc int[NumMbSegments];
        for (int y = 1; y < h - 1; ++y)
        {
            for (int x = 1; x < w - 1; ++x)
            {
                cnt.Clear();
                int mb = x + w * y;
                int majoritySeg = _mbInfo[mb].Segment;
                // Check the 8 neighbouring segment values.
                cnt[_mbInfo[mb - w - 1].Segment]++;  // top-left
                cnt[_mbInfo[mb - w + 0].Segment]++;  // top
                cnt[_mbInfo[mb - w + 1].Segment]++;  // top-right
                cnt[_mbInfo[mb - 1].Segment]++;      // left
                cnt[_mbInfo[mb + 1].Segment]++;      // right
                cnt[_mbInfo[mb + w - 1].Segment]++;  // bottom-left
                cnt[_mbInfo[mb + w + 0].Segment]++;  // bottom
                cnt[_mbInfo[mb + w + 1].Segment]++;  // bottom-right
                for (int n = 0; n < NumMbSegments; ++n)
                {
                    if (cnt[n] >= majorityCnt3x3Grid)
                    {
                        majoritySeg = n;
                        break;
                    }
                }
                tmp[x + y * w] = (byte)majoritySeg;
            }
        }
        for (int y = 1; y < h - 1; ++y)
            for (int x = 1; x < w - 1; ++x)
                _mbInfo[x + w * y].Segment = tmp[x + y * w];
    }

    private void SetSegmentAlphas(ReadOnlySpan<int> centers, int mid)
    {
        int nb = _numSegments;
        int min = centers[0], max = centers[0];
        if (nb > 1)
        {
            for (int n = 0; n < nb; ++n)
            {
                if (min > centers[n]) min = centers[n];
                if (max < centers[n]) max = centers[n];
            }
        }
        if (max == min) max = min + 1;
        for (int n = 0; n < nb; ++n)
        {
            int alpha = 255 * (centers[n] - mid) / (max - min);
            int beta = 255 * (centers[n] - min) / (max - min);
            _dqm[n].Alpha = Clip(alpha, -127, 127);
            _dqm[n].Beta = Clip(beta, 0, 255);
        }
    }

    private static int FinalAlphaValue(int alpha)
    {
        alpha = MaxAlpha - alpha;
        return Clip(alpha, 0, MaxAlpha);
    }

    private static int GetAlpha((int MaxValue, int LastNonZero) histo)
    {
        // 'alpha' will later be clipped to [0..MAX_ALPHA] range, clamping outer values.
        return histo.MaxValue > 1 ? AlphaScale * histo.LastNonZero / histo.MaxValue : 0;
    }

    private void AssignSegments(int[] alphas)
    {
        // 'num_segments' is previously validated and <= NUM_MB_SEGMENTS, but an
        // explicit check is needed to avoid spurious warning about 'n + 1' exceeding
        // array bounds of 'centers' with some compilers (noticed with gcc-4.9).
        int nb = _numSegments < NumMbSegments ? _numSegments : NumMbSegments;
        Span<int> centers = stackalloc int[NumMbSegments];
        int weightedAverage = 0;
        var map = new int[MaxAlpha + 1];
        int a, n, k;
        Span<int> accum = stackalloc int[NumMbSegments];
        Span<int> distAccum = stackalloc int[NumMbSegments];
        // bracket the input
        for (n = 0; n <= MaxAlpha && alphas[n] == 0; ++n) { }
        int minA = n;
        for (n = MaxAlpha; n > minA && alphas[n] == 0; --n) { }
        int maxA = n;
        int rangeA = maxA - minA;

        // Spread initial centers evenly
        for (k = 0, n = 1; k < nb; ++k, n += 2) centers[k] = minA + (n * rangeA) / (2 * nb);

        for (k = 0; k < MaxItersKMeans; ++k)
        {
            // few iters are enough
            for (n = 0; n < nb; ++n)
            {
                accum[n] = 0;
                distAccum[n] = 0;
            }
            // Assign nearest center for each 'a'
            n = 0;    // track the nearest center for current 'a'
            for (a = minA; a <= maxA; ++a)
            {
                if (alphas[a] != 0)
                {
                    while (n + 1 < nb && Math.Abs(a - centers[n + 1]) < Math.Abs(a - centers[n])) n++;
                    map[a] = n;
                    // accumulate contribution into best centroid
                    distAccum[n] += a * alphas[a];
                    accum[n] += alphas[a];
                }
            }
            // All point are classified. Move the centroids to the
            // center of their respective cloud.
            int displaced = 0;
            weightedAverage = 0;
            int totalWeight = 0;
            for (n = 0; n < nb; ++n)
            {
                if (accum[n] != 0)
                {
                    int newCenter = (distAccum[n] + accum[n] / 2) / accum[n];
                    displaced += Math.Abs(centers[n] - newCenter);
                    centers[n] = newCenter;
                    weightedAverage += newCenter * accum[n];
                    totalWeight += accum[n];
                }
            }
            weightedAverage = (weightedAverage + totalWeight / 2) / totalWeight;
            if (displaced < 5) break;   // no need to keep on looping...
        }

        // Map each original value to the closest centroid
        for (n = 0; n < _mbW * _mbH; ++n)
        {
            int alpha = _mbInfo[n].Alpha;
            _mbInfo[n].Segment = (byte)map[alpha];
            _mbInfo[n].Alpha = (byte)centers[map[alpha]];  // for the record.
        }

        if (nb > 1)
        {
            bool smooth = (_config.Preprocessing & 1) != 0;
            if (smooth) SmoothSegmentMap();
        }

        SetSegmentAlphas(centers, weightedAverage);  // pick some alphas.
    }

    private const int MaxIntra16Mode = 2, MaxUvMode = 2;

    private int MBAnalyzeBestIntra16Mode(Iterator it)
    {
        int bestAlpha = DefaultAlpha;
        int bestMode = 0;
        MakeLuma16Preds(it);
        for (int mode = 0; mode < MaxIntra16Mode; ++mode)
        {
            int alpha = GetAlpha(Vp8EncDsp.CollectHistogram(it.Yuv, Iterator.YuvIn + YOffEnc,
                it.Yuv, Iterator.YuvP + I16ModeOffsets[mode], 0, 16));
            if (alpha > bestAlpha)
            {
                bestAlpha = alpha;
                bestMode = mode;
            }
        }
        it.SetIntra16Mode(bestMode);
        return bestAlpha;
    }

    private int FastMBAnalyze(Iterator it)
    {
        // Empirical cut-off value, should be around 16 (~=block size). We use the
        // [8-17] range and favor intra4 at high quality, intra16 for low quality.
        int q = (int)_config.Quality;
        uint kThreshold = (uint)(8 + (17 - 8) * q / 100);
        Span<uint> dc = stackalloc uint[16];
        for (int k = 0; k < 16; k += 4) Vp8EncDsp.Mean16x4(it.Yuv, Iterator.YuvIn + YOffEnc + k * Bps, dc.Slice(k));
        uint m = 0, m2 = 0;
        for (int k = 0; k < 16; ++k)
        {
            m += dc[k];
            m2 += dc[k] * dc[k];
        }
        if (kThreshold * m2 < m * m)
        {
            it.SetIntra16Mode(0);   // DC16
        }
        else
        {
            it.SetIntra4Mode(ZeroModes);   // DC4
        }
        return 0;
    }

    private static readonly byte[] ZeroModes = new byte[16];

    private int MBAnalyzeBestUVMode(Iterator it)
    {
        int bestAlpha = DefaultAlpha;
        int smallestAlpha = 0;
        int bestMode = 0;
        MakeChroma8Preds(it);
        for (int mode = 0; mode < MaxUvMode; ++mode)
        {
            int alpha = GetAlpha(Vp8EncDsp.CollectHistogram(it.Yuv, Iterator.YuvIn + UOffEnc,
                it.Yuv, Iterator.YuvP + UvModeOffsets[mode], 16, 16 + 4 + 4));
            if (alpha > bestAlpha) bestAlpha = alpha;
            // The best prediction mode tends to be the one with the smallest alpha.
            if (mode == 0 || alpha < smallestAlpha)
            {
                smallestAlpha = alpha;
                bestMode = mode;
            }
        }
        it.SetIntraUVMode(bestMode);
        return bestAlpha;
    }

    private void MBAnalyze(Iterator it, int[] alphas, ref int alpha, ref int uvAlpha)
    {
        it.SetIntra16Mode(0);  // default: Intra16, DC_PRED
        it.SetSkip(false);     // not skipped
        it.SetSegment(0);      // default segment, spec-wise.

        int bestAlpha = _method <= 1 ? FastMBAnalyze(it) : MBAnalyzeBestIntra16Mode(it);
        int bestUvAlpha = MBAnalyzeBestUVMode(it);

        // Final susceptibility mix
        bestAlpha = (3 * bestAlpha + bestUvAlpha + 2) >> 2;
        bestAlpha = FinalAlphaValue(bestAlpha);
        alphas[bestAlpha]++;
        _mbInfo[it.Mb].Alpha = (byte)bestAlpha;   // for later remapping.

        // Accumulate for later complexity analysis.
        alpha += bestAlpha;   // mixed susceptibility (not just luma)
        uvAlpha += bestUvAlpha;
    }

    private void ResetAllMBInfo()
    {
        for (int n = 0; n < _mbW * _mbH; ++n)
        {
            // DefaultMBInfo
            _mbInfo[n].Type = 1;     // I16x16
            _mbInfo[n].UvMode = 0;
            _mbInfo[n].Skip = 0;     // not skipped
            _mbInfo[n].Segment = 0;  // default segment
            _mbInfo[n].Alpha = 0;
        }
        // Default susceptibilities.
        _dqm[0].Alpha = 0;
        _dqm[0].Beta = 0;
        // Note: we can't compute this alpha_ / uv_alpha_ -> set to default value.
        _alpha = 0;
        _uvAlpha = 0;
    }

    /// <summary>VP8EncAnalyze: per-macroblock susceptibilities, then k-means segments.</summary>
    private void Analyze()
    {
        bool doSegments =
            _config.EmulateJpegSize ||   // We need the complexity evaluation.
            _numSegments > 1 ||
            _method <= 1;  // for method 0 - 1, we need preds[] to be filled.
        if (doSegments)
        {
            int totalMb = _mbH * _mbW;
            var alphas = new int[MaxAlpha + 1];
            int alpha = 0, uvAlpha = 0;
            var it = new Iterator(this);
            var scratch = new byte[32];
            if (!it.IsDone)
            {
                do
                {
                    it.Import(scratch);
                    MBAnalyze(it, alphas, ref alpha, ref uvAlpha);
                } while (it.Next());
            }
            _alpha = alpha / totalMb;
            _uvAlpha = uvAlpha / totalMb;
            AssignSegments(alphas);
        }
        else
        {
            // Use only one default segment.
            ResetAllMBInfo();
        }
    }
}
