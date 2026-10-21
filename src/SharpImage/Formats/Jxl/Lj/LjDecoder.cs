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
    /// <summary>Decodes a bare JPEG XL codestream. <paramref name="maxFrames"/> limits the displayed frames returned.
    /// <paramref name="cmykToSrgb"/> renders CMYK images (a black extra channel) to sRGB through the CMS the way djxl does
    /// for output formats without CMYK; otherwise they come out as raw C, M, Y planes plus the black channel.
    /// <paramref name="cmsOutput"/> converts the image to that encoding through the CMS (djxl --color_space).</summary>
    public static LjDecodedImage Decode(byte[] cs, int maxFrames = int.MaxValue, LjColorEncoding? desired = null, bool preferSrgb = false,
        bool cmykToSrgb = false, LjColorEncoding? cmsOutput = null)
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
        if (cmsOutput != null)
        {
            SetOutputColorProfileWithCms(st, cmsOutput);
        }
        else if (desired == null && cmykToSrgb && st.Meta.ExtraChannelInfo.Exists(e => e.Type == LjExtraChannelInfo.TypeBlack))
        {
            SetOutputColorProfileWithCms(st, LjCe.Srgb(false));
        }
        else if (desired != null)
        {
            st.Output.SetColorEncoding(desired);
        }
        else if (preferSrgb && st.Meta.XybEncoded && MapColorspace(st.Meta.ColorEncoding) == null)
        {
            st.Output.SetColorEncoding(LjCe.Srgb(LjCe.IsGray(st.Meta.ColorEncoding)));
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

    /// <summary>JxlDecoderSetCms + JxlDecoderSetOutputColorProfile(color_encoding): the CMS fills the fields of an ICC-only
    /// original encoding, then the output switches to <paramref name="cOut"/>.</summary>
    private static void SetOutputColorProfileWithCms(LjDecoderState st, LjColorEncoding cOut)
    {
        LjOutputEncoding oe = st.Output;
        oe.CmsSet = true;
        LjColorEncoding orig = st.Meta.ColorEncoding;
        if (orig.WantIcc)
        {
            // orig_encoding.SetICC(icc, cms)
            oe.OrigColorEncoding = LjCms.FromIcc(orig.Icc ?? []) ?? throw LjDec.Fail("failed to parse the ICC profile");
        }

        // OutputEncodingInfo::MaybeSetColorEncoding
        if (cOut.ColorSpace == LjColorEncoding.CsXyb && ((oe.ColorEncoding.ColorSpace == LjColorEncoding.CsRgb &&
                                                          oe.ColorEncoding.Primaries != LjColorEncoding.PrSrgb) || LjCe.TfIsPq(oe.ColorEncoding)))
        {
            throw LjDec.Fail("cannot set the output colour encoding");
        }

        if (!oe.XybEncoded && !LjCe.CanOutputTo(cOut))
        {
            throw LjDec.Fail("cannot set the output colour encoding");
        }

        oe.SetColorEncoding(cOut);
    }

    /// <summary>The SharpImage colour space an enumerated encoding corresponds to, or null when there is none.</summary>
    public static Core.ColorspaceType? MapColorspace(LjColorEncoding c)
    {
        if (c.WantIcc || c.WhitePoint != LjColorEncoding.WpD65 || c.ColorSpace == LjColorEncoding.CsXyb || c.ColorSpace == LjColorEncoding.CsUnknown)
        {
            return null;
        }

        bool srgbTf = LjCe.TfIsSrgb(c), linear = LjCe.TfIsLinear(c);
        if (LjCe.IsGray(c))
        {
            return srgbTf ? Core.ColorspaceType.Gray : linear ? Core.ColorspaceType.LinearGray : null;
        }

        if (c.Primaries == LjColorEncoding.PrSrgb)
        {
            return srgbTf ? Core.ColorspaceType.SRGB : linear ? Core.ColorspaceType.ScRGB : null;
        }

        if (c.Primaries == LjColorEncoding.PrP3 && srgbTf)
        {
            return Core.ColorspaceType.DisplayP3;
        }

        // Adobe RGB (1998): its primaries with a 563/256 gamma.
        if (c.Primaries == LjColorEncoding.PrCustom && c.Red.X == 640000 && c.Red.Y == 330000 && c.Green.X == 210000 && c.Green.Y == 710000 &&
            c.Blue.X == 150000 && c.Blue.Y == 60000 && c.Tf.HaveGamma)
        {
            return Core.ColorspaceType.Adobe98;
        }

        return null;
    }

    /// <summary>Decodes the first frame's quantized DCT coefficients into <paramref name="jpg"/> (JPEG reconstruction).</summary>
    public static void DecodeToJpeg(byte[] cs, LjJpegData jpg)
    {
        if (cs.Length < 2 || cs[0] != 0xFF || cs[1] != 0x0A)
        {
            throw LjDec.Fail("invalid signature");
        }

        var st = new LjDecoderState();
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
        st.Output.SetFromMetadata(st.Meta, st.Transform);
        LjJpegRecon.SetIcc(st.Meta.ColorEncoding.WantIcc ? st.Meta.ColorEncoding.Icc : null, jpg);
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

            fd.JpegData = jpg;
            fd.DecodeSections(cs, pos);
            return;
        }

        throw LjDec.Fail("no frame");
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
