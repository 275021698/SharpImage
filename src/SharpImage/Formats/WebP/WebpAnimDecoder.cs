namespace SharpImage.Formats.WebP;

/// <summary>One ANMF frame: its rectangle on the canvas, timing, disposal / blending and decoded ARGB pixels.</summary>
internal sealed class WebpAnimFrame
{
    public int X, Y, Width, Height;
    public int DurationMs;
    public bool DisposeToBackground;   // WEBP_MUX_DISPOSE_BACKGROUND
    public bool Blend;                 // WEBP_MUX_BLEND (flag bit 1 clear)
    public bool HasAlpha;
    public uint[] Argb = [];
}

/// <summary>
/// libwebp 1.6.0 src/demux/anim_decode.c (WebPAnimDecoderGetNext, RGBA mode): composites each frame onto the canvas —
/// key frames start from a zero-filled canvas, others from the previous canvas after its disposal; the frame's pixels
/// replace the canvas rectangle and, when blending, non-opaque pixels are blended (non-premultiplied) with what was
/// there. Returns one full-canvas ARGB image per frame.
/// </summary>
internal static class WebpAnimDecoder
{
    private static bool IsFullFrame(int width, int height, int canvasWidth, int canvasHeight) =>
        width == canvasWidth && height == canvasHeight;

    private static bool IsKeyFrame(WebpAnimFrame curr, int frameNum, WebpAnimFrame? prev, bool prevFrameWasKeyFrame,
        int canvasWidth, int canvasHeight)
    {
        if (frameNum == 1) return true;
        if ((!curr.HasAlpha || !curr.Blend) && IsFullFrame(curr.Width, curr.Height, canvasWidth, canvasHeight)) return true;
        return prev!.DisposeToBackground &&
               (IsFullFrame(prev.Width, prev.Height, canvasWidth, canvasHeight) || prevFrameWasKeyFrame);
    }

    private static byte BlendChannelNonPremult(uint src, byte srcA, uint dst, byte dstA, uint scale, int shift)
    {
        byte srcChannel = (byte)(src >> shift);
        byte dstChannel = (byte)(dst >> shift);
        uint blendUnscaled = (uint)(srcChannel * srcA + dstChannel * dstA);
        return (byte)((blendUnscaled * scale) >> 24);
    }

    private static uint BlendPixelNonPremult(uint src, uint dst)
    {
        byte srcA = (byte)(src >> 24);
        if (srcA == 0) return dst;
        byte dstA = (byte)(dst >> 24);
        byte dstFactorA = (byte)((dstA * (256 - srcA)) >> 8);
        byte blendA = (byte)(srcA + dstFactorA);
        uint scale = (1u << 24) / blendA;
        byte blendR = BlendChannelNonPremult(src, srcA, dst, dstFactorA, scale, 16);
        byte blendG = BlendChannelNonPremult(src, srcA, dst, dstFactorA, scale, 8);
        byte blendB = BlendChannelNonPremult(src, srcA, dst, dstFactorA, scale, 0);
        return ((uint)blendA << 24) | ((uint)blendR << 16) | ((uint)blendG << 8) | blendB;
    }

    private static void BlendPixelRowNonPremult(uint[] src, uint[] dst, int offset, int numPixels)
    {
        for (int i = 0; i < numPixels; ++i)
        {
            byte srcAlpha = (byte)(src[offset + i] >> 24);
            if (srcAlpha != 0xff) src[offset + i] = BlendPixelNonPremult(src[offset + i], dst[offset + i]);
        }
    }

    private static void FindBlendRangeAtRow(WebpAnimFrame src, WebpAnimFrame dst, int canvasY, out int left1, out int width1,
        out int left2, out int width2)
    {
        int srcMaxX = src.X + src.Width;
        int dstMaxX = dst.X + dst.Width;
        int dstMaxY = dst.Y + dst.Height;
        left1 = -1;
        width1 = 0;
        left2 = -1;
        width2 = 0;
        if (canvasY < dst.Y || canvasY >= dstMaxY || src.X >= dstMaxX || srcMaxX <= dst.X)
        {
            left1 = src.X;
            width1 = src.Width;
            return;
        }
        if (src.X < dst.X)
        {
            left1 = src.X;
            width1 = dst.X - src.X;
        }
        if (srcMaxX > dstMaxX)
        {
            left2 = dstMaxX;
            width2 = srcMaxX - dstMaxX;
        }
    }

    /// <summary>The composited canvas after each frame.</summary>
    public static List<uint[]> Composite(IReadOnlyList<WebpAnimFrame> frames, int canvasWidth, int canvasHeight)
    {
        var result = new List<uint[]>(frames.Count);
        var prevFrameDisposed = new uint[canvasWidth * canvasHeight];
        WebpAnimFrame? prevIter = null;
        bool prevFrameWasKeyframe = false;
        for (int f = 0; f < frames.Count; ++f)
        {
            var iter = frames[f];
            int frameNum = f + 1;
            bool isKeyFrame = IsKeyFrame(iter, frameNum, prevIter, prevFrameWasKeyframe, canvasWidth, canvasHeight);
            var currFrame = isKeyFrame ? new uint[canvasWidth * canvasHeight] : (uint[])prevFrameDisposed.Clone();

            // WebPDecode into the canvas rectangle (clipped to the canvas, as the demuxer validates frames)
            int w = Math.Min(iter.Width, canvasWidth - iter.X);
            int h = Math.Min(iter.Height, canvasHeight - iter.Y);
            for (int y = 0; y < h; ++y)
                Array.Copy(iter.Argb, y * iter.Width, currFrame, (iter.Y + y) * canvasWidth + iter.X, w);

            if (frameNum > 1 && iter.Blend && !isKeyFrame)
            {
                if (!prevIter!.DisposeToBackground)
                {
                    for (int y = 0; y < h; ++y)
                        BlendPixelRowNonPremult(currFrame, prevFrameDisposed, (iter.Y + y) * canvasWidth + iter.X, w);
                }
                else
                {
                    for (int y = 0; y < h; ++y)
                    {
                        int canvasY = iter.Y + y;
                        FindBlendRangeAtRow(iter, prevIter, canvasY, out int left1, out int width1, out int left2, out int width2);
                        if (width1 > 0) BlendPixelRowNonPremult(currFrame, prevFrameDisposed, canvasY * canvasWidth + left1, width1);
                        if (width2 > 0) BlendPixelRowNonPremult(currFrame, prevFrameDisposed, canvasY * canvasWidth + left2, width2);
                    }
                }
            }

            prevIter = iter;
            prevFrameWasKeyframe = isKeyFrame;
            prevFrameDisposed = (uint[])currFrame.Clone();
            if (prevIter.DisposeToBackground)
            {
                for (int y = 0; y < h; ++y)
                    Array.Clear(prevFrameDisposed, (prevIter.Y + y) * canvasWidth + prevIter.X, w);
            }
            result.Add(currFrame);
        }
        return result;
    }
}
