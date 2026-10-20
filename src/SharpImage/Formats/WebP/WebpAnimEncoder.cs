namespace SharpImage.Formats.WebP;

/// <summary>
/// Options of an animated WebP encode: the WebPConfig every frame uses plus WebPAnimEncoderOptions. Defaults are
/// libwebp's WebPConfigInit / WebPAnimEncoderOptionsInit with lossless frames, i.e. what img2webp writes.
/// </summary>
public sealed class WebpAnimationOptions
{
    /// <summary>Lossless (VP8L) frames; false = lossy (VP8 + ALPH). Default true (img2webp / gif2webp default).</summary>
    public bool Lossless { get; set; } = true;

    /// <summary>0..100: lossy quality, or lossless effort. Default 75.</summary>
    public float Quality { get; set; } = 75;

    /// <summary>0 (fast) .. 6 (slowest). Default 4.</summary>
    public int Method { get; set; } = 4;

    /// <summary>Keep the RGB under fully transparent pixels. Default false.</summary>
    public bool Exact { get; set; }

    /// <summary>Near-lossless preprocessing for lossless frames, 100 = off. Default 100.</summary>
    public int NearLossless { get; set; } = 100;

    /// <summary>Minimum / maximum distance between key-frames. Defaults: key-frames disabled (int.MaxValue - 1 / int.MaxValue).</summary>
    public int Kmin { get; set; } = int.MaxValue - 1;
    public int Kmax { get; set; } = int.MaxValue;

    /// <summary>Try every disposal / blending choice for the smallest file (slower). Default false.</summary>
    public bool MinimizeSize { get; set; }

    /// <summary>Pick lossy or lossless per frame. Default false.</summary>
    public bool AllowMixed { get; set; }

    /// <summary>0 = infinite. Default 0.</summary>
    public int LoopCount { get; set; }

    /// <summary>ANIM background colour, 0xAARRGGBB as stored (libwebp default: white, 0xffffffff).</summary>
    public uint BackgroundColor { get; set; } = 0xffffffff;
}

/// <summary>
/// libwebp 1.6.0 src/mux/anim_encode.c (WebPAnimEncoder) with the parts of muxedit.c / muxinternal.c / muxread.c
/// that WebPMuxPushFrame and WebPMuxAssemble use: sub-frame rectangles, dispose / blend candidates, key-frame
/// selection, duration merging and the single-frame optimisation. Same frames and options give the same file as
/// WebPAnimEncoder (img2webp / gif2webp).
/// </summary>
internal sealed class WebpAnimEncoder
{
    private const int MaxDuration = 1 << 24;
    private const long DeltaInfinity = 1L << 32;
    private const int KeyframeNone = -1;
    private const int MaxCachedFrames = 30;
    private const uint TransparentColor = 0x00000000;

    private struct FrameRectangle
    {
        public int XOffset, YOffset, Width, Height;
    }

    /// <summary>WebPMuxFrameInfo (an ANMF frame: the encoded still plus its placement).</summary>
    private sealed class FrameInfo
    {
        public byte[]? Bitstream;
        public int XOffset, YOffset, Duration;
        public bool DisposeBackground;   // WEBP_MUX_DISPOSE_BACKGROUND
        public bool Blend = true;        // WEBP_MUX_BLEND

        public void CopyFrom(FrameInfo o)
        {
            Bitstream = o.Bitstream;
            XOffset = o.XOffset;
            YOffset = o.YOffset;
            Duration = o.Duration;
            DisposeBackground = o.DisposeBackground;
            Blend = o.Blend;
        }

        public void Clear()
        {
            Bitstream = null;
            XOffset = YOffset = Duration = 0;
            DisposeBackground = false;
            Blend = true;
        }
    }

    private sealed class EncodedFrame
    {
        public readonly FrameInfo SubFrame = new();   // Encoded frame rectangle.
        public readonly FrameInfo KeyFrame = new();   // Encoded frame if it is a key-frame.
        public bool IsKeyFrame;                       // True if 'key_frame' has been chosen.

        public void Release()
        {
            SubFrame.Clear();
            KeyFrame.Clear();
            IsKeyFrame = false;
        }
    }

    /// <summary>The WebPConfig of one encode: lossless or lossy plus the shared fields.</summary>
    private sealed class Config
    {
        public bool Lossless;
        public WebpLossyOptions Lossy = new();
        public WebpLosslessOptions Ll = new();

        public Config With(bool lossless) => new() { Lossless = lossless, Lossy = Lossy, Ll = Ll };
    }

    private readonly int _canvasWidth, _canvasHeight;
    private readonly WebpAnimationOptions _options;
    private readonly int _kmin, _kmax;
    private FrameRectangle _prevRect;
    private Config _lastConfig = null!;
    private Config _lastConfigReversed = null!;
    private uint[]? _currCanvas;                // Only reference; we don't own memory.
    private readonly uint[] _currCanvasCopy;    // Possibly modified current canvas.
    private bool _currCanvasCopyModified;
    private readonly uint[] _prevCanvas;
    private readonly uint[] _prevCanvasDisposed;
    private readonly EncodedFrame[] _encodedFrames;
    private readonly int _size;
    private int _start, _count, _flushCount;
    private long _bestDelta;
    private int _keyframe;
    private int _countSinceKeyFrame;
    private int _firstTimestamp, _prevTimestamp;
    private bool _prevCandidateUndecided;
    private bool _isFirstFrame;
    private bool _gotNullFrame;
    private int _inFrameCount, _outFrameCount;
    private readonly List<MuxImage> _muxImages = [];

    public WebpAnimEncoder(int width, int height, WebpAnimationOptions options)
    {
        if (width <= 0 || height <= 0 || width * (long)height >= 1L << 32) throw new ArgumentException("Bad canvas size.");
        _canvasWidth = width;
        _canvasHeight = height;
        _options = options;
        // SanitizeEncoderOptions
        int kmin = options.Kmin, kmax = options.Kmax;
        if (options.MinimizeSize)
        {
            kmax = int.MaxValue;
            kmin = kmax - 1;
        }
        if (kmax == 1)
        {
            // All frames will be key-frames.
            kmin = 0;
            kmax = 0;
        }
        else
        {
            if (kmax <= 0)
            {
                kmax = int.MaxValue;
                kmin = kmax - 1;
            }
            if (kmin >= kmax)
            {
                kmin = kmax - 1;
            }
            else
            {
                int kminLimit = kmax / 2 + 1;
                if (kmin < kminLimit && kminLimit < kmax) kmin = kminLimit;
            }
            if (kmax - kmin > MaxCachedFrames) kmin = kmax - MaxCachedFrames;
        }
        _kmin = kmin;
        _kmax = kmax;

        _currCanvasCopy = new uint[width * height];
        _prevCanvas = new uint[width * height];   // WebPUtilClearPic: transparent
        _prevCanvasDisposed = new uint[width * height];
        _currCanvasCopyModified = true;
        ResetCounters();
        _size = _kmax - _kmin + 1;
        if (_size < 2) _size = 2;
        _encodedFrames = new EncodedFrame[_size];
        for (int i = 0; i < _size; ++i) _encodedFrames[i] = new EncodedFrame();
        _countSinceKeyFrame = 0;
        _firstTimestamp = 0;
        _prevTimestamp = 0;
        _prevCandidateUndecided = false;
        _isFirstFrame = true;
        _gotNullFrame = false;
    }

    private void ResetCounters()
    {
        _start = 0;
        _count = 0;
        _flushCount = 0;
        _bestDelta = DeltaInfinity;
        _keyframe = KeyframeNone;
    }

    private EncodedFrame GetFrame(int position) => _encodedFrames[_start + position];

    // ---- pixel comparisons ---------------------------------------------------------------------------------------------

    private static bool PixelsAreSimilar(uint src, uint dst, int maxAllowedDiff)
    {
        int srcA = (int)(src >> 24) & 0xff, srcR = (int)(src >> 16) & 0xff, srcG = (int)(src >> 8) & 0xff, srcB = (int)src & 0xff;
        int dstA = (int)(dst >> 24) & 0xff, dstR = (int)(dst >> 16) & 0xff, dstG = (int)(dst >> 8) & 0xff, dstB = (int)dst & 0xff;
        return srcA == dstA &&
               Math.Abs(srcR - dstR) * dstA <= maxAllowedDiff * 255 &&
               Math.Abs(srcG - dstG) * dstA <= maxAllowedDiff * 255 &&
               Math.Abs(srcB - dstB) * dstA <= maxAllowedDiff * 255;
    }

    private static bool ComparePixels(bool isLossless, uint[] src, int s, int srcStep, uint[] dst, int d, int dstStep,
        int length, int maxAllowedDiff)
    {
        while (length-- > 0)
        {
            if (isLossless ? src[s] != dst[d] : !PixelsAreSimilar(src[s], dst[d], maxAllowedDiff)) return false;
            s += srcStep;
            d += dstStep;
        }
        return true;
    }

    private static bool IsEmptyRect(in FrameRectangle rect) => rect.Width == 0 || rect.Height == 0;

    private static int QualityToMaxDiff(float quality)
    {
        double val = Math.Pow(quality / 100.0, 0.5);
        double maxDiff = 31 * (1 - val) + 1 * val;
        return (int)(maxDiff + 0.5);
    }

    // Assumes that an initial valid guess of change rectangle 'rect' is passed.
    private void MinimizeChangeRectangle(uint[] src, uint[] dst, ref FrameRectangle rect, bool isLossless, float quality)
    {
        int stride = _canvasWidth;
        int maxAllowedDiffLossy = QualityToMaxDiff(quality);
        int maxAllowedDiff = isLossless ? 0 : maxAllowedDiffLossy;

        // Left boundary.
        for (int i = rect.XOffset; i < rect.XOffset + rect.Width; ++i)
        {
            int o = rect.YOffset * stride + i;
            if (ComparePixels(isLossless, src, o, stride, dst, o, stride, rect.Height, maxAllowedDiff))
            {
                --rect.Width;  // Redundant column.
                ++rect.XOffset;
            }
            else
            {
                break;
            }
        }
        if (rect.Width == 0) goto NoChange;

        // Right boundary.
        for (int i = rect.XOffset + rect.Width - 1; i >= rect.XOffset; --i)
        {
            int o = rect.YOffset * stride + i;
            if (ComparePixels(isLossless, src, o, stride, dst, o, stride, rect.Height, maxAllowedDiff))
                --rect.Width;  // Redundant column.
            else
                break;
        }
        if (rect.Width == 0) goto NoChange;

        // Top boundary.
        for (int j = rect.YOffset; j < rect.YOffset + rect.Height; ++j)
        {
            int o = j * stride + rect.XOffset;
            if (ComparePixels(isLossless, src, o, 1, dst, o, 1, rect.Width, maxAllowedDiff))
            {
                --rect.Height;  // Redundant row.
                ++rect.YOffset;
            }
            else
            {
                break;
            }
        }
        if (rect.Height == 0) goto NoChange;

        // Bottom boundary.
        for (int j = rect.YOffset + rect.Height - 1; j >= rect.YOffset; --j)
        {
            int o = j * stride + rect.XOffset;
            if (ComparePixels(isLossless, src, o, 1, dst, o, 1, rect.Width, maxAllowedDiff))
                --rect.Height;  // Redundant row.
            else
                break;
        }
        if (rect.Height == 0) goto NoChange;

        if (!IsEmptyRect(rect)) return;
        NoChange:
        rect.XOffset = 0;
        rect.YOffset = 0;
        rect.Width = 0;
        rect.Height = 0;
    }

    // Snap rectangle to even offsets (and adjust dimensions if needed).
    private static void SnapToEvenOffsets(ref FrameRectangle rect)
    {
        rect.Width += rect.XOffset & 1;
        rect.Height += rect.YOffset & 1;
        rect.XOffset &= ~1;
        rect.YOffset &= ~1;
    }

    private sealed class SubFrameParams
    {
        public bool ShouldTry;              // Should try this set of parameters.
        public bool EmptyRectAllowed;       // Frame with empty rectangle can be skipped.
        public FrameRectangle RectLl;       // Frame rectangle for lossless compression.
        public FrameRectangle RectLossy;    // Frame rectangle for lossy compression.
    }

    // Given previous and current canvas, picks the optimal rectangle for the
    // current frame based on 'is_lossless' and other parameters. Assumes that the
    // initial guess 'rect' is valid.
    private void GetSubRect(uint[] prevCanvas, uint[] currCanvas, bool isKeyFrame, bool isFirstFrame,
        bool emptyRectAllowed, bool isLossless, float quality, ref FrameRectangle rect)
    {
        if (!isKeyFrame || isFirstFrame)
        {
            // Optimize frame rectangle.
            // Note: This behaves as expected for first frame, as 'prev_canvas' is
            // initialized to a fully transparent canvas in the beginning.
            MinimizeChangeRectangle(prevCanvas, currCanvas, ref rect, isLossless, quality);
        }

        if (IsEmptyRect(rect))
        {
            if (emptyRectAllowed) return;  // No need to get 'sub_frame'.
            // Force a 1x1 rectangle.
            rect.Width = 1;
            rect.Height = 1;
        }

        SnapToEvenOffsets(ref rect);
    }

    private void GetSubRects(uint[] prevCanvas, uint[] currCanvas, bool isKeyFrame, bool isFirstFrame, float quality,
        SubFrameParams p)
    {
        // Lossless frame rectangle.
        p.RectLl.XOffset = 0;
        p.RectLl.YOffset = 0;
        p.RectLl.Width = _canvasWidth;
        p.RectLl.Height = _canvasHeight;
        GetSubRect(prevCanvas, currCanvas, isKeyFrame, isFirstFrame, p.EmptyRectAllowed, true, quality, ref p.RectLl);

        // Lossy frame rectangle.
        p.RectLossy = p.RectLl;  // seed with lossless rect.
        GetSubRect(prevCanvas, currCanvas, isKeyFrame, isFirstFrame, p.EmptyRectAllowed, false, quality, ref p.RectLossy);
    }

    private void ClearRectangle(uint[] picture, int left, int top, int width, int height)
    {
        for (int j = top; j < top + height; ++j)
            picture.AsSpan(j * _canvasWidth + left, width).Fill(TransparentColor);
    }

    private static uint RectArea(in FrameRectangle rect) => (uint)rect.Width * (uint)rect.Height;

    private bool IsLosslessBlendingPossible(uint[] src, uint[] dst, in FrameRectangle rect)
    {
        for (int j = rect.YOffset; j < rect.YOffset + rect.Height; ++j)
        {
            for (int i = rect.XOffset; i < rect.XOffset + rect.Width; ++i)
            {
                uint srcPixel = src[j * _canvasWidth + i];
                uint dstPixel = dst[j * _canvasWidth + i];
                uint dstAlpha = dstPixel >> 24;
                if (dstAlpha != 0xff && srcPixel != dstPixel)
                {
                    // In this case, if we use blending, we can't attain the desired
                    // 'dst_pixel' value for this pixel. So, blending is not possible.
                    return false;
                }
            }
        }
        return true;
    }

    private bool IsLossyBlendingPossible(uint[] src, uint[] dst, in FrameRectangle rect, float quality)
    {
        int maxAllowedDiffLossy = QualityToMaxDiff(quality);
        for (int j = rect.YOffset; j < rect.YOffset + rect.Height; ++j)
        {
            for (int i = rect.XOffset; i < rect.XOffset + rect.Width; ++i)
            {
                uint srcPixel = src[j * _canvasWidth + i];
                uint dstPixel = dst[j * _canvasWidth + i];
                uint dstAlpha = dstPixel >> 24;
                if (dstAlpha != 0xff && !PixelsAreSimilar(srcPixel, dstPixel, maxAllowedDiffLossy)) return false;
            }
        }
        return true;
    }

    // For pixels in 'rect', replace those pixels in 'dst' that are same as 'src' by
    // transparent pixels.
    // Returns true if at least one pixel gets modified.
    private bool IncreaseTransparency(uint[] src, in FrameRectangle rect, uint[] dst)
    {
        bool modified = false;
        for (int j = rect.YOffset; j < rect.YOffset + rect.Height; ++j)
        {
            int row = j * _canvasWidth;
            for (int i = rect.XOffset; i < rect.XOffset + rect.Width; ++i)
            {
                if (src[row + i] == dst[row + i] && dst[row + i] != TransparentColor)
                {
                    dst[row + i] = TransparentColor;
                    modified = true;
                }
            }
        }
        return modified;
    }

    // Replace similar blocks of pixels by a 'see-through' transparent block
    // with uniform average color.
    // Assumes lossy compression is being used.
    // Returns true if at least one pixel gets modified.
    private bool FlattenSimilarBlocks(uint[] src, in FrameRectangle rect, uint[] dst, float quality)
    {
        int maxAllowedDiffLossy = QualityToMaxDiff(quality);
        bool modified = false;
        const int blockSize = 8;
        int yStart = (rect.YOffset + blockSize) & ~(blockSize - 1);
        int yEnd = (rect.YOffset + rect.Height) & ~(blockSize - 1);
        int xStart = (rect.XOffset + blockSize) & ~(blockSize - 1);
        int xEnd = (rect.XOffset + rect.Width) & ~(blockSize - 1);
        int stride = _canvasWidth;

        for (int j = yStart; j < yEnd; j += blockSize)
        {
            for (int i = xStart; i < xEnd; i += blockSize)
            {
                int cnt = 0;
                int avgR = 0, avgG = 0, avgB = 0;
                int p = j * stride + i;
                for (int y = 0; y < blockSize; ++y)
                {
                    for (int x = 0; x < blockSize; ++x)
                    {
                        uint srcPixel = src[p + x + y * stride];
                        int alpha = (int)(srcPixel >> 24);
                        if (alpha == 0xff && PixelsAreSimilar(srcPixel, dst[p + x + y * stride], maxAllowedDiffLossy))
                        {
                            ++cnt;
                            avgR += (int)(srcPixel >> 16) & 0xff;
                            avgG += (int)(srcPixel >> 8) & 0xff;
                            avgB += (int)srcPixel & 0xff;
                        }
                    }
                }
                // If we have a fully similar block, we replace it with an
                // average transparent block. This compresses better in lossy mode.
                if (cnt == blockSize * blockSize)
                {
                    uint color = (0x00u << 24) | ((uint)(avgR / cnt) << 16) | ((uint)(avgG / cnt) << 8) | (uint)(avgB / cnt);
                    for (int y = 0; y < blockSize; ++y)
                        for (int x = 0; x < blockSize; ++x)
                            dst[p + x + y * stride] = color;
                    modified = true;
                }
            }
        }
        return modified;
    }

    // ---- encoding ------------------------------------------------------------------------------------------------------

    /// <summary>EncodeFrame (WebPEncode of a WebPPictureView of the canvas): the complete still WebP file.</summary>
    private byte[] EncodeFrame(Config config, uint[] canvas, in FrameRectangle rect)
    {
        var sub = new uint[rect.Width * rect.Height];
        for (int y = 0; y < rect.Height; ++y)
            Array.Copy(canvas, (rect.YOffset + y) * _canvasWidth + rect.XOffset, sub, y * rect.Width, rect.Width);
        if (config.Lossless)
        {
            if (!config.Ll.Exact)
            {
                // WebPEncode's WebPReplaceTransparentPixels runs on the view, i.e. on the canvas itself.
                for (int y = 0; y < rect.Height; ++y)
                {
                    int row = (rect.YOffset + y) * _canvasWidth + rect.XOffset;
                    for (int x = 0; x < rect.Width; ++x)
                        if ((canvas[row + x] >> 24) == 0) canvas[row + x] = 0;
                }
            }
            return Vp8lEncoder.EncodeImage(sub, rect.Width, rect.Height, config.Ll);
        }
        return Vp8Encoder.Encode(sub, rect.Width, rect.Height, config.Lossy);
    }

    private sealed class Candidate
    {
        public byte[]? Mem;
        public readonly FrameInfo Info = new();
        public FrameRectangle Rect;
        public bool Evaluate;   // True if this candidate should be evaluated.
    }

    // Generates a candidate encoded frame given a picture and metadata.
    private void EncodeCandidate(in FrameRectangle rect, Config encoderConfig, bool useBlending, Candidate candidate)
    {
        var config = encoderConfig;
        candidate.Mem = null;
        candidate.Info.Clear();
        candidate.Evaluate = false;
        candidate.Rect = rect;
        candidate.Info.XOffset = rect.XOffset;
        candidate.Info.YOffset = rect.YOffset;
        candidate.Info.DisposeBackground = false;   // Set later.
        candidate.Info.Blend = useBlending;
        candidate.Info.Duration = 0;               // Set in next call to WebPAnimEncoderAdd().

        // Encode picture.
        if (!config.Lossless && useBlending)
        {
            // Disable filtering to avoid blockiness in reconstructed frames at the
            // time of decoding.
            var lossy = config.Lossy.Clone();
            lossy.Autofilter = false;
            lossy.FilterStrength = 0;
            config = new Config { Lossless = false, Lossy = lossy, Ll = config.Ll };
        }
        candidate.Mem = EncodeFrame(config, _currCanvasCopy, rect);
        candidate.Evaluate = true;
    }

    private void CopyCurrentCanvas()
    {
        if (_currCanvasCopyModified)
        {
            _currCanvas!.CopyTo(_currCanvasCopy, 0);
            _currCanvasCopyModified = false;
        }
    }

    private const int LlDispNone = 0, LlDispBg = 1, LossyDispNone = 2, LossyDispBg = 3, CandidateCount = 4;
    private const int MinColorsLossy = 31;     // Don't try lossy below this threshold.
    private const int MaxColorsLossless = 194; // Don't try lossless above this threshold.

    // Generates candidates for a given dispose method given pre-filled sub-frame
    // 'params'.
    private void GenerateCandidates(Candidate[] candidates, bool disposeBackground, bool isLossless, bool isKeyFrame,
        SubFrameParams p, Config configLl, Config configLossy)
    {
        bool isDisposeNone = !disposeBackground;
        var candidateLl = isDisposeNone ? candidates[LlDispNone] : candidates[LlDispBg];
        var candidateLossy = isDisposeNone ? candidates[LossyDispNone] : candidates[LossyDispBg];
        var currCanvas = _currCanvasCopy;
        var prevCanvas = isDisposeNone ? _prevCanvas : _prevCanvasDisposed;
        bool evaluateLl, evaluateLossy;

        CopyCurrentCanvas();
        bool useBlendingLl = !isKeyFrame && IsLosslessBlendingPossible(prevCanvas, currCanvas, p.RectLl);
        bool useBlendingLossy = !isKeyFrame &&
                                IsLossyBlendingPossible(prevCanvas, currCanvas, p.RectLossy, configLossy.Lossy.Quality);

        // Pick candidates to be tried.
        if (!_options.AllowMixed)
        {
            evaluateLl = isLossless;
            evaluateLossy = !isLossless;
        }
        else if (_options.MinimizeSize)
        {
            evaluateLl = true;
            evaluateLossy = true;
        }
        else
        {
            // Use a heuristic for trying lossless and/or lossy compression.
            var sub = new uint[p.RectLl.Width * p.RectLl.Height];
            for (int y = 0; y < p.RectLl.Height; ++y)
                Array.Copy(currCanvas, (p.RectLl.YOffset + y) * _canvasWidth + p.RectLl.XOffset, sub, y * p.RectLl.Width,
                    p.RectLl.Width);
            int numColors = Vp8lPalette.GetColorPalette(sub, p.RectLl.Width, p.RectLl.Height, p.RectLl.Width, null);
            evaluateLl = numColors < MaxColorsLossless;
            evaluateLossy = numColors >= MinColorsLossy;
        }

        // Generate candidates.
        if (evaluateLl)
        {
            CopyCurrentCanvas();
            if (useBlendingLl) _currCanvasCopyModified = IncreaseTransparency(prevCanvas, p.RectLl, currCanvas);
            EncodeCandidate(p.RectLl, configLl, useBlendingLl, candidateLl);
        }
        if (evaluateLossy)
        {
            CopyCurrentCanvas();
            if (useBlendingLossy)
                _currCanvasCopyModified = FlattenSimilarBlocks(prevCanvas, p.RectLossy, currCanvas, configLossy.Lossy.Quality);
            EncodeCandidate(p.RectLossy, configLossy, useBlendingLossy, candidateLossy);
            _currCanvasCopyModified = true;
        }
    }

    private void SetPreviousDisposeMethod(bool disposeBackground)
    {
        int position = _count - 2;
        var prevEncFrame = GetFrame(position);
        if (_prevCandidateUndecided)
        {
            prevEncFrame.SubFrame.DisposeBackground = disposeBackground;
            prevEncFrame.KeyFrame.DisposeBackground = disposeBackground;
        }
        else
        {
            var prevInfo = prevEncFrame.IsKeyFrame ? prevEncFrame.KeyFrame : prevEncFrame.SubFrame;
            prevInfo.DisposeBackground = disposeBackground;
        }
    }

    private static readonly byte[] Lossless1x1Bytes =
    [
        0x52, 0x49, 0x46, 0x46, 0x14, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50,
        0x56, 0x50, 0x38, 0x4c, 0x08, 0x00, 0x00, 0x00, 0x2f, 0x00, 0x00, 0x00,
        0x10, 0x88, 0x88, 0x08,
    ];

    private static readonly byte[] Lossy1x1Bytes =
    [
        0x52, 0x49, 0x46, 0x46, 0x40, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50,
        0x56, 0x50, 0x38, 0x58, 0x0a, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x4c, 0x50, 0x48, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x56, 0x50, 0x38, 0x20, 0x18, 0x00, 0x00, 0x00,
        0x30, 0x01, 0x00, 0x9d, 0x01, 0x2a, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00,
        0x34, 0x25, 0xa4, 0x00, 0x03, 0x70, 0x00, 0xfe, 0xfb, 0xfd, 0x50, 0x00,
    ];

    private void IncreasePreviousDuration(int duration)
    {
        int position = _count - 1;
        var prevEncFrame = GetFrame(position);
        int newDuration = prevEncFrame.SubFrame.Duration + duration;
        if (newDuration >= MaxDuration)
        {
            // Special case.
            // Separate out previous frame from earlier merged frames to avoid overflow.
            // We add a 1x1 transparent frame for the previous frame, with blending on.
            var rect = new FrameRectangle { XOffset = 0, YOffset = 0, Width = 1, Height = 1 };
            bool canUseLossless = _lastConfig.Lossless || _options.AllowMixed;
            var currEncFrame = GetFrame(_count);
            currEncFrame.IsKeyFrame = false;
            currEncFrame.SubFrame.XOffset = 0;
            currEncFrame.SubFrame.YOffset = 0;
            currEncFrame.SubFrame.DisposeBackground = false;
            currEncFrame.SubFrame.Blend = true;
            currEncFrame.SubFrame.Duration = duration;
            currEncFrame.SubFrame.Bitstream = (byte[])(canUseLossless ? Lossless1x1Bytes : Lossy1x1Bytes).Clone();
            ++_count;
            ++_countSinceKeyFrame;
            _flushCount = _count - 1;
            _prevCandidateUndecided = false;
            _prevRect = rect;
        }
        else
        {
            // Regular case.
            // Increase duration of the previous frame by 'duration'.
            prevEncFrame.SubFrame.Duration = newDuration;
            prevEncFrame.KeyFrame.Duration = newDuration;
        }
    }

    // Pick the candidate encoded frame with smallest size and release other
    // candidates.
    private void PickBestCandidate(Candidate[] candidates, bool isKeyFrame, EncodedFrame encodedFrame)
    {
        int bestIdx = -1;
        long bestSize = long.MaxValue;
        for (int i = 0; i < CandidateCount; ++i)
        {
            if (candidates[i].Evaluate)
            {
                long candidateSize = candidates[i].Mem!.Length;
                if (candidateSize < bestSize)
                {
                    bestIdx = i;
                    bestSize = candidateSize;
                }
            }
        }
        for (int i = 0; i < CandidateCount; ++i)
        {
            if (!candidates[i].Evaluate) continue;
            if (i == bestIdx)
            {
                var dst = isKeyFrame ? encodedFrame.KeyFrame : encodedFrame.SubFrame;
                dst.CopyFrom(candidates[i].Info);
                dst.Bitstream = candidates[i].Mem;
                if (!isKeyFrame)
                {
                    // Note: Previous dispose method only matters for non-keyframes.
                    // Also, we don't want to modify previous dispose method that was
                    // selected when a non key-frame was assumed.
                    bool prevDisposeBackground = !(bestIdx == LlDispNone || bestIdx == LossyDispNone);
                    SetPreviousDisposeMethod(prevDisposeBackground);
                }
                _prevRect = candidates[i].Rect;  // save for next frame.
            }
            else
            {
                candidates[i].Mem = null;
                candidates[i].Evaluate = false;
            }
        }
    }

    // Depending on the configuration, tries different compressions
    // (lossy/lossless), dispose methods, blending methods etc to encode the current
    // frame and outputs the best one in 'encoded_frame'.
    // 'frame_skipped' will be set to true if this frame should actually be skipped
    // as it's not very different from the previous frame.
    private bool SetFrame(Config config, bool isKeyFrame, EncodedFrame encodedFrame)
    {
        var currCanvas = _currCanvasCopy;
        var prevCanvas = _prevCanvas;
        var candidates = new Candidate[CandidateCount];
        for (int i = 0; i < CandidateCount; ++i) candidates[i] = new Candidate();
        bool isLossless = config.Lossless;
        bool considerLossless = isLossless || _options.AllowMixed;
        bool considerLossy = !isLossless || _options.AllowMixed;
        bool isFirstFrame = _isFirstFrame;

        // First frame cannot be skipped as there is no 'previous frame' to merge it
        // to. So, empty rectangle is not allowed for the first frame.
        bool emptyRectAllowedNone = !isFirstFrame;

        // Even if there is exact pixel match between 'disposed previous canvas' and
        // 'current canvas', we can't skip current frame, as there may not be exact
        // pixel match between 'previous canvas' and 'current canvas'. So, we don't
        // allow empty rectangle in this case.
        const bool emptyRectAllowedBg = false;

        // If current frame is a key-frame, dispose method of previous frame doesn't
        // matter, so we don't try dispose to background.
        // Also, if key-frame insertion is on, and previous frame could be picked as
        // either a sub-frame or a key-frame, then we can't be sure about what frame
        // rectangle would be disposed. In that case too, we don't try dispose to
        // background.
        bool disposeBgPossible = !isKeyFrame && !_prevCandidateUndecided;

        var disposeNoneParams = new SubFrameParams { ShouldTry = true, EmptyRectAllowed = emptyRectAllowedNone };
        var disposeBgParams = new SubFrameParams { ShouldTry = false, EmptyRectAllowed = emptyRectAllowedBg };
        var configLl = config.With(true);
        var configLossy = config.With(false);
        _lastConfig = config;
        _lastConfigReversed = config.Lossless ? configLossy : configLl;

        GetSubRects(prevCanvas, currCanvas, isKeyFrame, isFirstFrame, configLossy.Lossy.Quality, disposeNoneParams);

        if ((considerLossless && IsEmptyRect(disposeNoneParams.RectLl)) ||
            (considerLossy && IsEmptyRect(disposeNoneParams.RectLossy)))
        {
            // Don't encode the frame at all. Instead, the duration of the previous
            // frame will be increased later.
            return true;   // frame skipped
        }

        if (disposeBgPossible)
        {
            // Change-rectangle assuming previous frame was DISPOSE_BACKGROUND.
            var prevCanvasDisposed = _prevCanvasDisposed;
            prevCanvas.CopyTo(prevCanvasDisposed, 0);
            ClearRectangle(prevCanvasDisposed, _prevRect.XOffset, _prevRect.YOffset, _prevRect.Width, _prevRect.Height);

            GetSubRects(prevCanvasDisposed, currCanvas, isKeyFrame, isFirstFrame, configLossy.Lossy.Quality, disposeBgParams);

            if (_options.MinimizeSize)
            {
                // Try both dispose methods.
                disposeBgParams.ShouldTry = true;
                disposeNoneParams.ShouldTry = true;
            }
            else if ((isLossless && RectArea(disposeBgParams.RectLl) < RectArea(disposeNoneParams.RectLl)) ||
                     (!isLossless && RectArea(disposeBgParams.RectLossy) < RectArea(disposeNoneParams.RectLossy)))
            {
                disposeBgParams.ShouldTry = true;  // Pick DISPOSE_BACKGROUND.
                disposeNoneParams.ShouldTry = false;
            }
        }

        if (disposeNoneParams.ShouldTry)
            GenerateCandidates(candidates, false, isLossless, isKeyFrame, disposeNoneParams, configLl, configLossy);

        if (disposeBgParams.ShouldTry)
            GenerateCandidates(candidates, true, isLossless, isKeyFrame, disposeBgParams, configLl, configLossy);

        PickBestCandidate(candidates, isKeyFrame, encodedFrame);
        return false;
    }

    private static long KeyFramePenalty(EncodedFrame encodedFrame) =>
        (long)encodedFrame.KeyFrame.Bitstream!.Length - encodedFrame.SubFrame.Bitstream!.Length;

    // Encode the current frame and add it to the cache.
    private void CacheFrame(Config config)
    {
        bool frameSkipped;
        int position = _count;
        var encodedFrame = GetFrame(position);

        ++_count;

        if (_isFirstFrame)
        {
            // Add this as a key-frame.
            SetFrame(config, true, encodedFrame);
            encodedFrame.IsKeyFrame = true;
            _flushCount = 0;
            _countSinceKeyFrame = 0;
            _prevCandidateUndecided = false;
        }
        else
        {
            ++_countSinceKeyFrame;
            if (_countSinceKeyFrame <= _kmin)
            {
                // Add this as a frame rectangle.
                frameSkipped = SetFrame(config, false, encodedFrame);
                if (frameSkipped) goto Skip;
                encodedFrame.IsKeyFrame = false;
                _flushCount = _count - 1;
                _prevCandidateUndecided = false;
            }
            else
            {
                // Add this as a frame rectangle to enc.
                frameSkipped = SetFrame(config, false, encodedFrame);
                if (frameSkipped) goto Skip;
                var prevRectSub = _prevRect;

                // Add this as a key-frame to enc, too.
                SetFrame(config, true, encodedFrame);
                var prevRectKey = _prevRect;

                // Analyze size difference of the two variants.
                long currDelta = KeyFramePenalty(encodedFrame);
                if (currDelta <= _bestDelta)
                {
                    // Pick this as the key-frame.
                    if (_keyframe != KeyframeNone)
                    {
                        var oldKeyframe = GetFrame(_keyframe);
                        oldKeyframe.IsKeyFrame = false;
                    }
                    encodedFrame.IsKeyFrame = true;
                    _prevCandidateUndecided = true;
                    _keyframe = position;
                    _bestDelta = currDelta;
                    _flushCount = _count - 1;  // We can flush previous frames.
                }
                else
                {
                    encodedFrame.IsKeyFrame = false;
                    _prevCandidateUndecided = false;
                }
                // Note: We need '>=' below because when kmin and kmax are both zero,
                // count_since_key_frame will always be > kmax.
                if (_countSinceKeyFrame >= _kmax)
                {
                    _flushCount = _count - 1;
                    _countSinceKeyFrame = 0;
                    _keyframe = KeyframeNone;
                    _bestDelta = DeltaInfinity;
                }
                if (!_prevCandidateUndecided) _prevRect = encodedFrame.IsKeyFrame ? prevRectKey : prevRectSub;
            }
        }

        // Update previous to previous and previous canvases for next call.
        _currCanvas!.CopyTo(_prevCanvas, 0);
        _isFirstFrame = false;
        ++_inFrameCount;
        return;

        Skip:
        ++_inFrameCount;
        encodedFrame.Release();
        --_count;
        if (!_isFirstFrame) --_countSinceKeyFrame;
    }

    private void FlushFrames()
    {
        while (_flushCount > 0)
        {
            var curr = GetFrame(0);
            var info = curr.IsKeyFrame ? curr.KeyFrame : curr.SubFrame;
            MuxPushFrame(info);
            ++_outFrameCount;
            curr.Release();
            ++_start;
            --_flushCount;
            --_count;
            if (_keyframe != KeyframeNone) --_keyframe;
        }

        if (_count == 1 && _start != 0)
        {
            // Move enc->start to index 0.
            int encStartTmp = _start;
            (_encodedFrames[0], _encodedFrames[encStartTmp]) = (_encodedFrames[encStartTmp], _encodedFrames[0]);
            _encodedFrames[encStartTmp].Release();
            _start = 0;
        }
    }

    /// <summary>
    /// WebPAnimEncoderAdd: 'frame' is the full canvas (0xAARRGGBB, canvas width x height) shown from 'timestamp' (ms),
    /// or null for the final call that sets the last frame's duration.
    /// </summary>
    public void Add(uint[]? frame, int timestamp, WebpAnimationOptions? frameConfig = null)
    {
        if (!_isFirstFrame)
        {
            uint prevFrameDuration = (uint)timestamp - (uint)_prevTimestamp;
            if (prevFrameDuration >= MaxDuration) throw new ArgumentException("Timestamps must be non-decreasing.");
            IncreasePreviousDuration((int)prevFrameDuration);
            // IncreasePreviousDuration() may add a frame to avoid exceeding
            // MAX_DURATION which could cause CacheFrame() to over read 'encoded_frames'
            // before the next flush.
            if (_count == _size) FlushFrames();
        }
        else
        {
            _firstTimestamp = timestamp;
        }

        if (frame == null)
        {
            // Special: last call.
            _gotNullFrame = true;
            _prevTimestamp = timestamp;
            return;
        }

        if (frame.Length != _canvasWidth * _canvasHeight) throw new ArgumentException("Invalid frame dimensions.");

        var o = frameConfig ?? _options;
        var config = new Config
        {
            Lossless = o.Lossless,
            Lossy = new WebpLossyOptions { Quality = o.Quality, Method = o.Method, Exact = o.Exact },
            Ll = new WebpLosslessOptions { Quality = o.Quality, Method = o.Method, Exact = o.Exact, NearLossless = o.NearLossless },
        };

        _currCanvas = frame;  // Store reference.
        CopyCurrentCanvas();

        CacheFrame(config);
        FlushFrames();

        _currCanvas = null;
        _currCanvasCopyModified = true;
        _prevTimestamp = timestamp;
    }

    // ---- assembly ------------------------------------------------------------------------------------------------------

    /// <summary>WebPMuxImage: an ANMF frame (or the single still) with its image chunks.</summary>
    private sealed class MuxImage
    {
        public bool HasHeader;            // ANMF chunk present
        public int XOffset, YOffset, Duration;
        public bool DisposeBackground, Blend;
        public byte[]? Alpha;             // ALPH payload
        public byte[] Img = [];           // VP8 / VP8L payload
        public bool IsLossless;
        public int Width, Height;
        public bool HasAlpha;
    }

    /// <summary>SetAlphaAndImageChunks: the image chunks of a still WebP file (GetImageData + MuxImageFinalize).</summary>
    private static MuxImage ParseStill(byte[] bitstream)
    {
        var wpi = new MuxImage();
        int pos = 12;
        while (pos + 8 <= bitstream.Length)
        {
            uint size = BitConverter.ToUInt32(bitstream, pos + 4);
            var tag = bitstream.AsSpan(pos, 4);
            var payload = bitstream.AsSpan(pos + 8, (int)size).ToArray();
            if (tag.SequenceEqual("ALPH"u8))
            {
                wpi.Alpha = payload;
            }
            else if (tag.SequenceEqual("VP8 "u8) || tag.SequenceEqual("VP8L"u8))
            {
                wpi.Img = payload;
                wpi.IsLossless = tag[3] == (byte)'L';
                break;
            }
            pos += 8 + (int)size + (int)(size & 1);
        }
        if (wpi.IsLossless)
        {
            // VP8LGetInfo; an ALPH chunk accompanying VP8L is ignored.
            uint bits = BitConverter.ToUInt32(wpi.Img, 1);
            wpi.Width = (int)(bits & 0x3fff) + 1;
            wpi.Height = (int)((bits >> 14) & 0x3fff) + 1;
            bool vp8lHasAlpha = ((bits >> 28) & 1) != 0;
            wpi.Alpha = null;
            wpi.HasAlpha = vp8lHasAlpha;
        }
        else
        {
            // VP8GetInfo
            wpi.Width = (wpi.Img[6] | (wpi.Img[7] << 8)) & 0x3fff;
            wpi.Height = (wpi.Img[8] | (wpi.Img[9] << 8)) & 0x3fff;
            wpi.HasAlpha = wpi.Alpha != null;
        }
        return wpi;
    }

    private void MuxPushFrame(FrameInfo info)
    {
        var wpi = ParseStill(info.Bitstream!);
        wpi.HasHeader = true;
        wpi.XOffset = info.XOffset & ~1;  // Snap offsets to even.
        wpi.YOffset = info.YOffset & ~1;
        wpi.Duration = info.Duration;
        wpi.DisposeBackground = info.DisposeBackground;
        wpi.Blend = info.Blend;
        _muxImages.Add(wpi);
    }

    private static int SizeWithPadding(int chunkSize) => 8 + ((chunkSize + 1) & ~1);

    private static int ImageDiskSize(MuxImage wpi)
    {
        int size = 0;
        if (wpi.HasHeader) size += SizeWithPadding(16);
        if (wpi.Alpha != null) size += SizeWithPadding(wpi.Alpha.Length);
        size += SizeWithPadding(wpi.Img.Length);
        return size;
    }

    private static void PutLe24(byte[] d, int o, int v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
    }

    private static int EmitChunk(byte[] dst, int o, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        tag.CopyTo(dst.AsSpan(o));
        BitConverter.TryWriteBytes(dst.AsSpan(o + 4), (uint)data.Length);
        data.CopyTo(dst.AsSpan(o + 8));
        if ((data.Length & 1) != 0) dst[o + 8 + data.Length] = 0;  // Add padding.
        return o + SizeWithPadding(data.Length);
    }

    /// <summary>WebPMuxAssemble (MuxCleanup, CreateVP8XChunk, then VP8X, ANIM, images).</summary>
    private static byte[] MuxAssemble(List<MuxImage> images, int canvasWidth, int canvasHeight, bool hasAnim, uint bgcolor,
        int loopCount)
    {
        // MuxCleanup
        int numFrames = 0;
        foreach (var im in images) if (im.HasHeader) ++numFrames;
        if (numFrames == 1)
        {
            var frame = images[0];
            if (frame.HasHeader &&
                ((canvasWidth == 0 && canvasHeight == 0) || (frame.Width == canvasWidth && frame.Height == canvasHeight)))
            {
                frame.HasHeader = false;  // Removes ANMF chunk.
                numFrames = 0;
            }
        }
        if (hasAnim && numFrames == 0) hasAnim = false;

        // CreateVP8XChunk
        uint flags = 0;
        var first = images[0];
        if (first.HasHeader) flags |= 0x00000002;   // ANIMATION_FLAG
        foreach (var im in images)
        {
            if (im.Alpha != null)
            {
                flags |= 0x00000010;   // ALPHA_FLAG: some images have an alpha channel.
                break;
            }
        }
        int width, height;
        if (images.Count > 1)
        {
            int maxX = 0, maxY = 0;
            foreach (var im in images)
            {
                int maxXPos = im.XOffset + im.Width;
                int maxYPos = im.YOffset + im.Height;
                if (maxXPos > maxX) maxX = maxXPos;
                if (maxYPos > maxY) maxY = maxYPos;
            }
            width = maxX;
            height = maxY;
        }
        else
        {
            width = first.Width;
            height = first.Height;
        }
        if (canvasWidth != 0 || canvasHeight != 0)
        {
            if (width > canvasWidth || height > canvasHeight) throw new InvalidOperationException("WebP mux: frame outside the canvas.");
            width = canvasWidth;
            height = canvasHeight;
        }
        bool vp8x = flags != 0;
        if (vp8x)
        {
            foreach (var im in images)
            {
                if (im.HasAlpha)
                {
                    flags |= 0x00000010;
                    break;
                }
            }
        }

        int size = 12 + (vp8x ? SizeWithPadding(10) : 0) + (hasAnim ? SizeWithPadding(6) : 0);
        foreach (var im in images) size += ImageDiskSize(im);
        var data = new byte[size];
        "RIFF"u8.CopyTo(data);
        BitConverter.TryWriteBytes(data.AsSpan(4), (uint)(size - 8));
        "WEBP"u8.CopyTo(data.AsSpan(8));
        int o = 12;
        if (vp8x)
        {
            var c = new byte[10];
            BitConverter.TryWriteBytes(c.AsSpan(0), flags);  // VP8X chunk flags.
            PutLe24(c, 4, width - 1);                         // canvas width.
            PutLe24(c, 7, height - 1);                        // canvas height.
            o = EmitChunk(data, o, "VP8X"u8, c);
        }
        if (hasAnim)
        {
            var c = new byte[6];
            BitConverter.TryWriteBytes(c.AsSpan(0), bgcolor);
            BitConverter.TryWriteBytes(c.AsSpan(4), (ushort)loopCount);
            o = EmitChunk(data, o, "ANIM"u8, c);
        }
        foreach (var im in images)
        {
            if (im.HasHeader)
            {
                // ChunkEmitSpecial: the ANMF size covers the frame's image chunks.
                var c = new byte[16];
                PutLe24(c, 0, im.XOffset / 2);
                PutLe24(c, 3, im.YOffset / 2);
                PutLe24(c, 6, im.Width - 1);
                PutLe24(c, 9, im.Height - 1);
                PutLe24(c, 12, im.Duration);
                c[15] = (byte)((!im.Blend ? 2 : 0) | (im.DisposeBackground ? 1 : 0));
                "ANMF"u8.CopyTo(data.AsSpan(o));
                BitConverter.TryWriteBytes(data.AsSpan(o + 4), (uint)(ImageDiskSize(im) - 8));
                c.CopyTo(data, o + 8);
                o += SizeWithPadding(16);
            }
            if (im.Alpha != null) o = EmitChunk(data, o, "ALPH"u8, im.Alpha);
            o = EmitChunk(data, o, im.IsLossless ? "VP8L"u8 : "VP8 "u8, im.Img);
        }
        return data;
    }

    /// <summary>DecodeFrameOntoCanvas: decodes a still onto a cleared canvas at its offset (WebPDecode, BGRA).</summary>
    private void DecodeFrameOntoCanvas(MuxImage frame, uint[] canvas)
    {
        Array.Clear(canvas);
        uint[] argb;
        int w, h;
        if (frame.IsLossless)
        {
            argb = Vp8lDecoder.Decode(frame.Img, 0, frame.Img.Length, out w, out h, out _);
        }
        else
        {
            using var img = Formats.Vp8LossyCodec.Decode(frame.Img, frame.Alpha);
            w = (int)img.Columns;
            h = (int)img.Rows;
            argb = new uint[w * h];
            int ch = img.NumberOfChannels;
            for (int y = 0; y < h; ++y)
            {
                var row = img.GetPixelRow(y);
                for (int x = 0; x < w; ++x)
                {
                    uint r = (uint)(row[x * ch] >> 8), g = (uint)(row[x * ch + 1] >> 8), b = (uint)(row[x * ch + 2] >> 8);
                    uint a = img.HasAlpha ? (uint)(row[x * ch + ch - 1] >> 8) : 255u;
                    argb[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
        }
        for (int y = 0; y < h; ++y) Array.Copy(argb, y * w, canvas, (frame.YOffset + y) * _canvasWidth + frame.XOffset, w);
    }

    // Convert a single-frame animation to a non-animated image if appropriate.
    // TODO(urvang): Can we pick one of the two heuristically (based on frame
    // rectangle and/or presence of alpha)?
    private byte[] OptimizeSingleFrame(byte[] webpData)
    {
        var frame = _muxImages[0];
        if (!frame.HasHeader) return webpData;  // Non-animation: nothing to do.

        // FrameToFullCanvas
        var canvasBuf = _currCanvasCopy;
        DecodeFrameOntoCanvas(frame, canvasBuf);
        var fullRect = new FrameRectangle { XOffset = 0, YOffset = 0, Width = _canvasWidth, Height = _canvasHeight };
        byte[] fullImage = EncodeFrame(_lastConfig, canvasBuf, fullRect);
        if (_options.AllowMixed)
        {
            byte[] mem2 = EncodeFrame(_lastConfigReversed, canvasBuf, fullRect);
            if (mem2.Length < fullImage.Length) fullImage = mem2;
        }

        // WebPMuxSetImage + WebPMuxAssemble (the ANIM chunk goes with the last frame)
        var still = ParseStill(fullImage);
        byte[] webpData2 = MuxAssemble([still], _canvasWidth, _canvasHeight, true, _options.BackgroundColor, _options.LoopCount);
        return webpData2.Length < webpData.Length ? webpData2 : webpData;
    }

    /// <summary>WebPAnimEncoderAssemble: the animated (or, for one frame, possibly still) WebP file.</summary>
    public byte[] Assemble()
    {
        if (_inFrameCount == 0) throw new InvalidOperationException("No frames to assemble.");

        if (!_gotNullFrame && _inFrameCount > 1 && _count > 0)
        {
            // set duration of the last frame to be avg of durations of previous frames.
            double deltaTime = (uint)_prevTimestamp - (uint)_firstTimestamp;
            int averageDuration = (int)(deltaTime / (_inFrameCount - 1));
            IncreasePreviousDuration(averageDuration);
        }

        // Flush any remaining frames.
        _flushCount = _count;
        FlushFrames();

        byte[] webpData = MuxAssemble(_muxImages, _canvasWidth, _canvasHeight, true, _options.BackgroundColor, _options.LoopCount);
        if (_outFrameCount == 1) webpData = OptimizeSingleFrame(webpData);
        return webpData;
    }
}
