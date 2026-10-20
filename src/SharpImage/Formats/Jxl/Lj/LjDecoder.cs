// libjxl 0.12 codestream decoding (decode.cc JxlDecoderProcessCodestream with coalescing, as djxl runs it): headers,
// ICC profile, preview skipping, the frame loop with reference frames and blending, and orientation undo.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjDecodedFrame
{
    /// <summary>Colour planes (3) then extra channels, already oriented, in the output colour encoding.</summary>
    public LjImageF[] Planes = [];
    public uint Duration;
    public int Width, Height;
}

internal sealed class LjDecodedImage
{
    public LjDecoderState State = new();
    public List<LjDecodedFrame> Frames = new();

    /// <summary>The ICC profile stored in the codestream (when the image has one).</summary>
    public byte[]? Icc => State.Meta.ColorEncoding.WantIcc ? State.Meta.ColorEncoding.Icc : null;

    /// <summary>Colour encoding of the pixel data (libjxl JXL_COLOR_PROFILE_TARGET_DATA).</summary>
    public LjColorEncoding DataEncoding => State.Output.ColorEncoding;

    public bool IsGray => LjCe.IsGray(State.Output.ColorEncoding) || (State.Output.ColorEncoding.WantIcc && LjCe.IsGray(State.Meta.ColorEncoding));

    public int AlphaChannel
    {
        get
        {
            List<LjExtraChannelInfo> eci = State.Meta.ExtraChannelInfo;
            for (int i = 0; i < eci.Count; i++)
            {
                if (eci[i].Type == LjExtraChannelInfo.TypeAlpha)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}

internal static class LjDecoder
{
    /// <summary>Decodes a bare JPEG XL codestream. <paramref name="maxFrames"/> limits the displayed frames returned.</summary>
    public static LjDecodedImage Decode(byte[] cs, int maxFrames = int.MaxValue, LjColorEncoding? desired = null)
    {
        if (cs.Length < 2 || cs[0] != 0xFF || cs[1] != 0x0A)
        {
            throw LjDec.Fail("invalid signature");
        }

        var img = new LjDecodedImage();
        LjDecoderState st = img.State;
        var br = new LjBitReader(cs, 2, cs.Length - 2);
        LjFields.ReadInto(st.Size, br);
        LjFields.ReadInto(st.Meta, br);
        st.Transform.NonserializedXybEncoded = st.Meta.XybEncoded;
        LjFields.ReadInto(st.Transform, br);
        if (st.Meta.ColorEncoding.WantIcc)
        {
            st.Meta.ColorEncoding.Icc = LjIccDecoder.Read(br);
        }

        br.JumpToByteBoundary();
        if (!br.AllReadsWithinBounds)
        {
            throw LjDec.Fail("Truncated headers");
        }

        st.Output.SetFromMetadata(st.Meta, st.Transform);
        if (desired != null)
        {
            st.Output.SetColorEncoding(desired);
        }
        int pos = 2 + (int)(br.TotalBitsConsumed / 8);
        bool preview = st.Meta.HavePreview;
        while (pos < cs.Length)
        {
            var fd = new LjFrameDecoder(st);
            long frameSize = fd.InitFrame(cs, pos, preview);
            if (preview)
            {
                pos += (int)frameSize;
                preview = false;
                continue;
            }

            fd.DecodeSections(cs, pos);
            pos += (int)frameSize;
            LjFrameHeader fh = fd.Fh;
            bool isLastOfStill = fh.IsLast || fh.AnimationFrame.Duration > 0;
            if (isLastOfStill && (fh.FrameType == LjFrameHeader.RegularFrame || fh.FrameType == LjFrameHeader.SkipProgressive))
            {
                img.Frames.Add(MakeOutput(st, fd));
                if (img.Frames.Count >= maxFrames)
                {
                    break;
                }
            }

            if (fh.IsLast)
            {
                break;
            }
        }

        if (img.Frames.Count == 0)
        {
            throw LjDec.Fail("no frames");
        }

        return img;
    }

    private static LjDecodedFrame MakeOutput(LjDecoderState st, LjFrameDecoder fd)
    {
        LjImageF[] planes = fd.OutputPlanes;
        int w = st.ImageXsize, h = st.ImageYsize;
        var outPlanes = new LjImageF[planes.Length];
        uint orientation = st.Meta.Orientation;
        bool transpose = orientation is 5 or 6 or 7 or 8;
        bool flipX = orientation is 2 or 3 or 7 or 8;   // kFlipHorizontal, kRotate180, kRotate270, kAntiTranspose
        bool flipY = orientation is 3 or 4 or 6 or 7;   // kRotate180, kFlipVertical, kRotate90, kAntiTranspose

        int ow = transpose ? h : w, oh = transpose ? w : h;
        for (int c = 0; c < planes.Length; c++)
        {
            LjImageF src = planes[c];
            var dst = new LjImageF(ow, oh);
            for (int y = 0; y < h; y++)
            {
                int srow = src.Row(y);
                for (int x = 0; x < w; x++)
                {
                    float v = src.Data[srow + x];
                    // WriteToOutputStage: flip y on the row index, flip x within the row, then transpose.
                    int yy = flipY ? h - 1 - y : y;
                    int xx = flipX ? w - 1 - x : x;
                    if (transpose)
                    {
                        dst.Data[dst.Row(xx) + yy] = v;
                    }
                    else
                    {
                        dst.Data[dst.Row(yy) + xx] = v;
                    }
                }
            }

            outPlanes[c] = dst;
        }

        return new LjDecodedFrame { Planes = outPlanes, Duration = fd.Duration, Width = ow, Height = oh };
    }
}
