// The colour description of an image's samples, for writers: its ICC profile, or its CICP tags / SharpImage colour
// space as an enumerated encoding (with libjxl's ICC profile for it, so formats without CICP describe
// it too), and a conversion to sRGB through the CMS for formats that can carry no colour description at all.
using System;
using SharpImage.Formats.Jxl;
using SharpImage.Formats.Jxl.Lj;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Formats;

internal static class ColorDescription
{
    /// <summary>
    /// What a writer should attach to <paramref name="frame"/>'s samples: its ICC profile (and CICP tags, if it has
    /// them too); otherwise, for a non-sRGB colour encoding, libjxl's ICC profile for it and its CICP tags where
    /// H.273 has code points. Both null for sRGB, and for a profile that cannot describe the samples (see
    /// <see cref="NeedsConversion"/>).
    /// </summary>
    public static (byte[]? Icc, CicpInfo? Cicp) Resolve(ImageFrame frame)
    {
        if (frame.IccProfile is { Length: > 0 })
        {
            byte[]? icc = MatchedIcc(frame, !IsGrayFrame(frame), out _);
            return (icc, icc != null ? frame.Metadata.Cicp : null);
        }

        LjColorEncoding? enc = Encoding(frame);
        if (enc == null || IsSrgb(enc))
        {
            return (null, null);
        }

        return (LjCms.MaybeCreateProfile(LjCms.ToExternal(enc)), CicpOf(enc));
    }

    /// <summary>
    /// True when the frame's ICC profile cannot be attached to its samples as they are, or as RGB for
    /// <paramref name="rgbOutput"/> (a gray profile without an RGB equivalent on gray samples stored as RGB): writers
    /// get the samples converted to sRGB.
    /// </summary>
    public static bool NeedsConversion(ImageFrame frame, bool rgbOutput = false)
    {
        if (frame.IccProfile is not { Length: > 0 })
        {
            return false;
        }

        MatchedIcc(frame, rgbOutput || !IsGrayFrame(frame), out bool convert);
        return convert;
    }

    /// <summary>
    /// The ICC profile for <paramref name="frame"/>'s samples when a writer stores them as RGB (gray expanded to
    /// R = G = B): a gray profile becomes the RGB profile with the same white point and transfer curve (its neutral
    /// axis). <paramref name="convert"/> is set when no such profile exists and the samples must be converted to sRGB.
    /// </summary>
    public static byte[]? ResolveRgbIcc(ImageFrame frame, out bool convert)
    {
        convert = false;
        if (frame.IccProfile is { Length: > 0 })
        {
            return MatchedIcc(frame, rgbSamples: true, out convert);
        }

        byte[]? icc = Resolve(frame).Icc;
        if (icc == null || !IsGrayFrame(frame))
        {
            return icc;
        }

        // gray colour space without a profile: libjxl's gray profile for it, as RGB
        return GrayToRgbIcc(icc, out convert);
    }

    // The frame's ICC profile for samples stored as RGB (or as gray), as is when its colour space matches them; a gray
    // profile on RGB samples as its RGB equivalent (convert when there is none); null for a profile that does not
    // apply (CMYK on RGB, unparseable gray) or is plain sRGB.
    private static byte[]? MatchedIcc(ImageFrame frame, bool rgbSamples, out bool convert)
    {
        convert = false;
        byte[] icc = frame.IccProfile!;
        string space = icc.Length >= 20 ? System.Text.Encoding.ASCII.GetString(icc, 16, 4) : "";
        if (space == "CMYK")
        {
            return frame.Colorspace == Core.ColorspaceType.CMYK ? icc : null;
        }

        if (space != "GRAY" || !rgbSamples)
        {
            return icc;
        }

        return GrayToRgbIcc(icc, out convert);
    }

    // A gray profile for RGB samples: null for gray sRGB; else the RGB profile with the same curve and white
    // (GrayIccToRgb), else libjxl's RGB profile for its fields; convert when there is neither.
    private static byte[]? GrayToRgbIcc(byte[] grayIcc, out bool convert)
    {
        convert = false;
        LjColorEncoding? enc = LjCms.FromIcc(grayIcc);
        if (enc is { Cmyk: false } && LjCe.IsGray(enc) && IsSrgbFields(enc))
        {
            return null;
        }

        if (GrayIccToRgb(grayIcc) is { } rgb)
        {
            return rgb;
        }

        if (enc == null || !LjCe.IsGray(enc))
        {
            convert = true;
            return null;
        }

        return RgbEquivalent(enc, out convert);
    }

    // D50-adapted sRGB primaries (the colorants of the usual sRGB profiles) as s15Fixed16: their sum is the D50
    // PCS white, so R = G = B maps exactly where the gray curve maps the gray value.
    private static readonly int[][] SrgbColorantsD50 =
    [
        [0x6FA2, 0x38F5, 0x0390],
        [0x6299, 0xB785, 0x18DA],
        [0x24A0, 0x0F84, 0xB6CF],
    ];

    /// <summary>
    /// The RGB matrix/TRC profile whose neutral axis is a gray profile: its grayTRC as rTRC = gTRC = bTRC and
    /// D50-adapted sRGB colorants, every other tag (description, white point, chad, copyright ...) copied. R = G = B
    /// renders exactly as the gray value does through the gray profile, whatever its curve (gamma, PQ, HLG, a
    /// table). Null when the profile is not a TRC-only gray profile.
    /// </summary>
    internal static byte[]? GrayIccToRgb(byte[] icc)
    {
        if (icc.Length < 132 || System.Text.Encoding.ASCII.GetString(icc, 16, 4) != "GRAY")
        {
            return null;
        }

        static uint U32(byte[] b, int p) => (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
        uint count = U32(icc, 128);
        if (count > 100 || 132 + (count * 12) > icc.Length)
        {
            return null;
        }

        var tags = new List<(string Sig, byte[] Data)>();
        byte[]? trc = null;
        for (int i = 0; i < count; i++)
        {
            int e = 132 + (i * 12);
            string sig = System.Text.Encoding.ASCII.GetString(icc, e, 4);
            uint off = U32(icc, e + 4), size = U32(icc, e + 8);
            if (off + (ulong)size > (ulong)icc.Length)
            {
                return null;
            }

            byte[] data = icc.AsSpan((int)off, (int)size).ToArray();
            if (sig is "A2B0" or "A2B1" or "A2B2" or "B2A0" or "B2A1" or "B2A2" or "D2B0" or "B2D0")
            {
                return null;   // a LUT-based gray profile: no TRC equivalent
            }

            if (sig == "kTRC")
            {
                trc = data;
            }
            else
            {
                if (sig == "cicp" && data.Length >= 12)
                {
                    data[8] = 1;   // colour primaries: the sRGB colorants below (gray has none)
                }

                tags.Add((sig, data));
            }
        }

        if (trc == null)
        {
            return null;
        }

        string[] xyz = ["rXYZ", "gXYZ", "bXYZ"];
        for (int c = 0; c < 3; c++)
        {
            var d = new byte[20];
            "XYZ "u8.CopyTo(d);
            for (int k = 0; k < 3; k++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8 + (k * 4)), SrgbColorantsD50[c][k]);
            }

            tags.Add((xyz[c], d));
        }

        // the three TRC tags share one data block
        int n = tags.Count + 3;
        int pos = 132 + (n * 12);
        var outp = new MemoryStream();
        outp.Write(icc, 0, 128);
        var table = new byte[4 + (n * 12)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(table, (uint)n);
        var body = new MemoryStream();
        int t = 4;
        void Entry(string sig, int off, int size)
        {
            System.Text.Encoding.ASCII.GetBytes(sig).CopyTo(table, t);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(t + 4), (uint)off);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(t + 8), (uint)size);
            t += 12;
        }

        int Add(byte[] data)
        {
            int off = pos + (int)body.Length;
            body.Write(data);
            while (body.Length % 4 != 0) body.WriteByte(0);
            return off;
        }

        foreach (var (sig, data) in tags)
        {
            Entry(sig, Add(data), data.Length);
        }

        int trcOff = Add(trc);
        foreach (string sig in new[] { "rTRC", "gTRC", "bTRC" })
        {
            Entry(sig, trcOff, trc.Length);
        }

        outp.Write(table);
        outp.Write(body.ToArray());
        byte[] result = outp.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        "RGB "u8.CopyTo(result.AsSpan(16));
        Array.Clear(result, 84, 16);   // profile ID: unset
        return result;
    }

    // The RGB encoding with a gray encoding's white point and transfer curve (its neutral axis is the gray one) as an
    // ICC profile; null for sRGB, or with convert set when libjxl cannot make a profile for it.
    private static byte[]? RgbEquivalent(LjColorEncoding gray, out bool convert)
    {
        LjColorEncoding rgb = gray.Clone();
        rgb.WantIcc = false;
        rgb.Icc = null;
        rgb.Cmyk = false;
        rgb.ColorSpace = LjColorEncoding.CsRgb;
        rgb.Tf.NonserializedColorSpace = LjColorEncoding.CsRgb;
        rgb.Primaries = LjColorEncoding.PrSrgb;
        if (IsSrgb(rgb))
        {
            convert = false;
            return null;
        }

        byte[]? icc = LjCms.MaybeCreateProfile(LjCms.ToExternal(rgb));
        convert = icc == null;
        return icc;
    }

    /// <summary>
    /// The enumerated encoding of the samples (CICP tags / colour space) with its chromaticities, or null when an ICC
    /// profile describes them or there is none.
    /// </summary>
    /// <remarks>An ICC profile that is libjxl's own profile for an enumerated encoding (byte for byte: what JPEG XL
    /// decoding attaches for encodings without a SharpImage colour space) counts as that encoding.</remarks>
    public static LjColorEncoding? EnumEncoding(ImageFrame frame)
    {
        if (frame.IccProfile is not { Length: > 0 } icc)
        {
            return Encoding(frame);
        }

        // libjxl names its profiles by their encoding ("RGB_D65_SRG_Rel_g0.45455"); the CMS would only recover the
        // fields approximately from the curves. A profile a JPEG XL file stored itself stays the image's own.
        if (frame.Metadata.IccFromEncoding == false || IccDescription(icc) is not { } desc || LjCms.ParseDescription(desc) is not { } e)
        {
            return null;
        }

        return LjCms.MaybeCreateProfile(LjCms.ToExternal(e)) is { } p && p.AsSpan().SequenceEqual(icc) ? e : null;
    }

    // The 'desc' tag text of an ICC profile (v4 'mluc': its first record; v2 'desc': the ASCII string).
    private static string? IccDescription(byte[] icc)
    {
        static uint U32(byte[] b, int p) => (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
        if (icc.Length < 132)
        {
            return null;
        }

        uint count = U32(icc, 128);
        for (int i = 0; i < count && 132 + (i * 12) + 12 <= icc.Length; i++)
        {
            int e = 132 + (i * 12);
            if (icc[e] != 'd' || icc[e + 1] != 'e' || icc[e + 2] != 's' || icc[e + 3] != 'c')
            {
                continue;
            }

            long off = U32(icc, e + 4), size = U32(icc, e + 8);
            if (off + size > icc.Length || size < 12)
            {
                return null;
            }

            string type = System.Text.Encoding.ASCII.GetString(icc, (int)off, 4);
            if (type == "mluc" && size >= 28)
            {
                long len = U32(icc, (int)off + 20), so = U32(icc, (int)off + 24);
                return so + len <= size ? System.Text.Encoding.BigEndianUnicode.GetString(icc, (int)(off + so), (int)len) : null;
            }

            if (type == "desc")
            {
                long len = U32(icc, (int)off + 8);
                return 12 + len <= size && len > 0 ? System.Text.Encoding.ASCII.GetString(icc, (int)off + 12, (int)len - 1) : null;
            }

            return null;
        }

        return null;
    }

    /// <summary>True for an sRGB (or sRGB gray) enumerated encoding.</summary>
    public static bool IsSrgbEncoding(LjColorEncoding e) => IsSrgbFields(e);

    /// <summary>True when the samples are plain sRGB (or sRGB gray): no description needed, no conversion.</summary>
    public static bool IsSrgbFrame(ImageFrame frame)
    {
        if (frame.IccProfile is { Length: > 0 } icc)
        {
            return LjCms.FromIcc(icc) is { Cmyk: false } f && IsSrgbFields(f);
        }

        LjColorEncoding? enc = Encoding(frame);
        return enc == null || IsSrgb(enc);
    }

    private static bool IsGrayFrame(ImageFrame f) => f.NumberOfChannels - (f.HasAlpha ? 1 : 0) == 1;

    // The enumerated encoding of the samples without an ICC profile: CICP tags, else the SharpImage colour space;
    // null when there is none (sRGB is assumed).
    private static LjColorEncoding? Encoding(ImageFrame f)
    {
        bool gray = IsGrayFrame(f);
        if (f.Metadata.Cicp is { } cicp && f.Colorspace is Core.ColorspaceType.SRGB or Core.ColorspaceType.Gray)
        {
            return JxlXybColor.FromCicp(cicp, gray);
        }

        return f.Colorspace is not (Core.ColorspaceType.SRGB or Core.ColorspaceType.Gray) ? JxlXybColor.FromColorspace(f.Colorspace, gray) : null;
    }

    private static bool IsSrgb(LjColorEncoding e) => !e.WantIcc && IsSrgbFields(e);

    // sRGB by its fields (an ICC profile the CMS reads as sRGB counts)
    private static bool IsSrgbFields(LjColorEncoding e) =>
        !e.Cmyk && LjCe.TfIsSrgb(e) && e.WhitePoint == LjColorEncoding.WpD65 &&
        (LjCe.IsGray(e) || (e.ColorSpace == LjColorEncoding.CsRgb && e.Primaries == LjColorEncoding.PrSrgb));

    /// <summary>H.273 code points of an enumerated encoding (RGB samples, full range), or null when it has none.</summary>
    public static CicpInfo? CicpOf(LjColorEncoding e)
    {
        if (e.WantIcc || e.Tf.HaveGamma)
        {
            return null;
        }

        int tc = (int)e.Tf.TransferFunction;
        if (tc is not (1 or 8 or 13 or 16 or 17 or 18))
        {
            return null;
        }

        int cp;
        if (LjCe.IsGray(e))
        {
            cp = 2;   // unspecified: gray has no primaries
        }
        else if (e.Primaries == LjColorEncoding.PrP3 && e.WhitePoint == LjColorEncoding.WpDci)
        {
            cp = 11;
        }
        else if (e.WhitePoint != LjColorEncoding.WpD65)
        {
            return null;
        }
        else
        {
            cp = e.Primaries switch
            {
                LjColorEncoding.PrSrgb => 1,
                LjColorEncoding.Pr2100 => 9,
                LjColorEncoding.PrP3 => 12,
                _ => 0,
            };
            if (cp == 0)
            {
                return null;
            }
        }

        return new CicpInfo(cp, tc, 0, true);
    }

    /// <summary>
    /// The frame converted to sRGB through the CMS (skcms + libjxl's JxlCms), for formats that cannot describe any
    /// other colour space; null when it already is sRGB or its description cannot be used (then it is written as is).
    /// Exif / XMP / IPTC are kept; the profile and CICP tags no longer apply and are dropped.
    /// </summary>
    public static ImageFrame? ToSrgb(ImageFrame frame)
    {
        if (IsSrgbFrame(frame))
        {
            return null;
        }

        bool gray = IsGrayFrame(frame);
        LjColorEncoding? src = frame.IccProfile is { Length: > 0 } icc ? LjCms.FromIcc(icc) : Encoding(frame);
        if (src == null || src.Cmyk || (LjCe.IsGray(src) && !gray && frame.Colorspace == Core.ColorspaceType.CMYK) ||
            (!LjCe.IsGray(src) && gray))
        {
            return null;
        }

        // A gray profile on gray samples a reader expanded to RGB: each channel through the gray transform.
        bool perChannelGray = LjCe.IsGray(src) && !gray;
        float intensity = LjCe.TfIsPq(src) ? 10000f : LjCe.TfIsHlg(src) ? 1000f : 255f;
        LjCms.Transform? t = LjCms.Transform.Create(src, LjCe.Srgb(LjCe.IsGray(src)), intensity);
        if (t == null)
        {
            return null;
        }

        int w = (int)frame.Columns, h = (int)frame.Rows, nc = frame.NumberOfChannels, colorCh = gray ? 1 : 3;
        int runs = perChannelGray ? 3 : 1, runCh = perChannelGray ? 1 : colorCh;
        var outFrame = new ImageFrame();
        outFrame.Initialize(w, h, gray ? Core.ColorspaceType.Gray : Core.ColorspaceType.SRGB, frame.HasAlpha);
        outFrame.Metadata = frame.Metadata.Clone();
        outFrame.Metadata.IccProfile = null;
        outFrame.Metadata.Cicp = null;
        outFrame.Metadata.IccFromEncoding = null;
        outFrame.Orientation = frame.Orientation;
        int onc = outFrame.NumberOfChannels;
        var srcBuf = new float[w * 4];
        var dstBuf = new float[w * 3];
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = frame.GetPixelRow(y);
            Span<ushort> o = outFrame.GetPixelRowForWrite(y);
            for (int run = 0; run < runs; run++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < runCh; c++)
                    {
                        srcBuf[(x * runCh) + c] = row[(x * nc) + run + c] * (1f / 65535f);
                    }
                }

                t.Run(srcBuf, dstBuf, w);
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < runCh; c++)
                    {
                        float v = dstBuf[(x * runCh) + c];
                        o[(x * onc) + run + c] = (ushort)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f, MidpointRounding.ToEven);
                    }
                }
            }

            if (frame.HasAlpha)
            {
                for (int x = 0; x < w; x++)
                {
                    o[(x * onc) + onc - 1] = row[(x * nc) + nc - 1];
                }
            }
        }

        return outFrame;
    }
}
