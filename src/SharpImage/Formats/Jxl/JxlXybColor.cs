// Colour handling for XYB (lossy) encoding, as libjxl's encoder does it: the image's colour encoding (its ICC
// profile, CICP tags or SharpImage colour space) decides what the codestream declares and how pixels reach XYB.
//  - Metadata: ColorEncoding::DecideIfWantICC - when libjxl's CMS can describe the ICC profile with enumerated
//    fields (and could recreate it), those fields are written instead of the ICC bytes; SetIntensityTarget gives
//    PQ content 10000 nits and HLG 1000.
//  - Pixels: ToXYB - linear sRGB and sRGB inputs go straight to the opsin transform; anything else is converted to
//    linear sRGB through the CMS (skcms + libjxl's JxlCms), keeping out-of-gamut values (XYB has room for them).
using System;
using SharpImage.Formats.Jxl.Lj;
using SharpImage.Image;

namespace SharpImage.Formats.Jxl;

internal sealed class JxlXybColor
{
    /// <summary>The colour encoding the codestream declares (its fields, or only colour space kind with an ICC).</summary>
    public LjColorEncoding Encoding = LjCe.Srgb(false);

    /// <summary>The ICC profile to embed (want_icc), or null.</summary>
    public byte[]? Icc;

    /// <summary>ToneMapping intensity_target: the luminance of linear 1.0, scaling XYB for HDR content.</summary>
    public float IntensityTarget = 255f;

    private LjCms.Transform? toLinearSrgb;
    private bool inputLinear;

    public bool IsPlainSrgb => Icc == null && toLinearSrgb == null && !inputLinear && IntensityTarget == 255f && !LjCe.IsGray(Encoding) &&
                               LjCe.TfIsSrgb(Encoding) && Encoding.Primaries == LjColorEncoding.PrSrgb && Encoding.WhitePoint == LjColorEncoding.WpD65 &&
                               Encoding.RenderingIntent == LjColorEncoding.RiRelative;

    /// <summary>sRGB RGB at the same intensity target: what the encoder's own scoring decodes against (the planes
    /// are sRGB-referred, so this decodes to the values they were compared with).</summary>
    public JxlXybColor Scoring => new() { IntensityTarget = IntensityTarget };

    /// <param name="image">The image.</param>
    /// <param name="contentGray">Every pixel has R == G == B (the frame may hold a grayscale source as RGB).</param>
    public static JxlXybColor Plan(ImageFrame image, bool contentGray)
    {
        bool oneChannel = image.NumberOfChannels - (image.HasAlpha ? 1 : 0) == 1;
        if (image.IccProfile is { Length: > 0 } icc)
        {
            // ColorEncoding::SetICC(icc, cms): an ICC libjxl cannot use, or of the wrong kind, is not embedded.
            LjColorEncoding? fields = LjCms.FromIcc(icc);
            bool iccGray = fields != null && LjCe.IsGray(fields);
            bool gray = oneChannel || (contentGray && iccGray);
            if (fields == null || fields.Cmyk || iccGray != gray ||
                (fields.ColorSpace != LjColorEncoding.CsRgb && fields.ColorSpace != LjColorEncoding.CsGray))
            {
                return Plain(oneChannel || contentGray);
            }

            var plan = new JxlXybColor();
            if (LjCms.MaybeCreateProfile(LjCms.ToExternal(fields)) != null)
            {
                // DecideIfWantICC: the fields describe the profile.
                LjColorEncoding enc = fields.Clone();
                enc.WantIcc = false;
                enc.Icc = null;
                plan.Encoding = enc;
            }
            else
            {
                plan.Encoding = new LjColorEncoding { AllDefaultField = false, WantIcc = true, ColorSpace = fields.ColorSpace };
                plan.Icc = icc;
            }

            // Fields recovered from an ICC keep it: libjxl transforms from the original profile (c_current.ICC()).
            return plan.WithConversion(fields, gray) ? plan : Plain(gray);
        }

        bool g = oneChannel || contentGray;
        LjColorEncoding? e = image.Metadata.Cicp is { } cicp && image.Colorspace == Core.ColorspaceType.SRGB
            ? FromCicp(cicp, g)
            : FromColorspace(image.Colorspace, g);
        if (e == null)
        {
            return Plain(g);
        }

        var p = new JxlXybColor { Encoding = e };
        return p.WithConversion(e, g) ? p : Plain(g);
    }

    private static JxlXybColor Plain(bool gray) => new() { Encoding = LjCe.Srgb(gray) };

    // SharpImage colour spaces with an enumerated JPEG XL encoding (the inverse of LjDecoder.MapColorspace).
    internal static LjColorEncoding? FromColorspace(Core.ColorspaceType cs, bool gray)
    {
        switch (cs)
        {
            case Core.ColorspaceType.SRGB:
            case Core.ColorspaceType.Gray:
                return LjCe.Srgb(gray);
            case Core.ColorspaceType.ScRGB:
            case Core.ColorspaceType.LinearGray:
                return LjCe.LinearSrgb(gray);
            case Core.ColorspaceType.DisplayP3 when !gray:
            {
                LjColorEncoding p3 = LjCe.Srgb(false);
                p3.Primaries = LjColorEncoding.PrP3;
                return p3;
            }

            case Core.ColorspaceType.Adobe98 when !gray:
                return LjCms.ParseDescription("Adobe98");
            default:
                return null;
        }
    }

    // CICP tags of decoded RGB samples (H.273 code points; the matrix and range were undone by the decoder).
    internal static LjColorEncoding? FromCicp(SharpImage.Metadata.CicpInfo cicp, bool gray)
    {
        uint tf = cicp.TransferCharacteristics switch
        {
            1 or 6 or 14 or 15 => LjCustomTransferFunction.Tf709,
            8 => LjCustomTransferFunction.TfLinear,
            13 => LjCustomTransferFunction.TfSrgb,
            16 => LjCustomTransferFunction.TfPq,
            17 => LjCustomTransferFunction.TfDci,
            18 => LjCustomTransferFunction.TfHlg,
            _ => 0,
        };
        if (tf == 0)
        {
            return null;
        }

        LjColorEncoding e = LjCe.Srgb(gray);
        e.Tf.TransferFunction = tf;
        if (!gray)
        {
            switch (cicp.ColorPrimaries)
            {
                case 1:
                    break;
                case 9:
                    e.Primaries = LjColorEncoding.Pr2100;
                    break;
                case 11:
                    e.Primaries = LjColorEncoding.PrP3;
                    e.WhitePoint = LjColorEncoding.WpDci;
                    break;
                case 12:
                    e.Primaries = LjColorEncoding.PrP3;
                    break;
                default:
                    return null;
            }
        }

        return e;
    }

    // ToXYB: which input path the pixels take. SetIntensityTarget first: PQ 10000 nits, HLG 1000, else 255.
    private bool WithConversion(LjColorEncoding current, bool gray)
    {
        IntensityTarget = LjCe.TfIsPq(current) ? 10000f : LjCe.TfIsHlg(current) ? 1000f : 255f;
        bool srgbish = (LjCe.IsGray(current) || current.ColorSpace == LjColorEncoding.CsRgb) && current.WhitePoint == LjColorEncoding.WpD65 &&
                       current.Primaries == LjColorEncoding.PrSrgb;
        if (srgbish && LjCe.TfIsLinear(current))
        {
            inputLinear = true;
            return true;
        }

        if (srgbish && LjCe.TfIsSrgb(current))
        {
            return true;
        }

        toLinearSrgb = LjCms.Transform.Create(current.Clone(), LjCe.LinearSrgb(gray), IntensityTarget);
        return toLinearSrgb != null;
    }

    /// <summary>
    /// Brings the image's samples (in its own colour space, [0,1], gray expanded to three planes) to sRGB-referred
    /// values the encoder's sRGB path takes, converting through the CMS when needed. Values may leave [0,1].
    /// </summary>
    public float[][] ToSrgbReferred(float[][] planes, int w, int h)
    {
        if (toLinearSrgb == null && !inputLinear)
        {
            return planes;
        }

        var outp = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            outp[c] = new float[w * h];
        }

        bool gray = LjCe.IsGray(Encoding);
        var src = new float[w * 4];
        var dst = new float[w * 3];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            if (inputLinear)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        outp[c][row + x] = SrgbFromLinear(planes[c][row + x]);
                    }
                }

                continue;
            }

            if (gray)
            {
                Array.Copy(planes[0], row, src, 0, w);
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    src[3 * x] = planes[0][row + x];
                    src[(3 * x) + 1] = planes[1][row + x];
                    src[(3 * x) + 2] = planes[2][row + x];
                }
            }

            toLinearSrgb!.Run(src, dst, w);
            for (int x = 0; x < w; x++)
            {
                if (gray)
                {
                    float v = SrgbFromLinear(dst[x]);
                    outp[0][row + x] = outp[1][row + x] = outp[2][row + x] = v;
                }
                else
                {
                    outp[0][row + x] = SrgbFromLinear(dst[3 * x]);
                    outp[1][row + x] = SrgbFromLinear(dst[(3 * x) + 1]);
                    outp[2][row + x] = SrgbFromLinear(dst[(3 * x) + 2]);
                }
            }
        }

        return outp;
    }

    // The sRGB transfer function extended to all reals (odd), the exact inverse of the encoder's linearisation.
    private static float SrgbFromLinear(float v)
    {
        double a = Math.Abs((double)v);
        double e = a <= 0.0031308 ? a * 12.92 : (1.055 * Math.Pow(a, 1 / 2.4)) - 0.055;
        return (float)(v < 0 ? -e : e);
    }

    /// <summary>
    /// Writes the codestream's ImageMetadata (XYB-encoded, output bit depth, an optional alpha channel, this colour
    /// encoding and intensity target), the default CustomTransformData and the ICC profile, as libjxl does.
    /// </summary>
    public void WriteImageHeader(JxlBitWriter w, bool hasAlpha, int bits)
    {
        var md = new LjImageMetadata { AllDefaultField = false, XybEncoded = true, Modular16BitBufferSufficient = true, ColorEncoding = Encoding };
        md.BitDepth.BitsPerSample = (uint)bits;
        md.ToneMapping.IntensityTarget = IntensityTarget;
        if (hasAlpha)
        {
            md.ExtraChannelInfo.Add(new LjExtraChannelInfo());
        }

        WriteBits(w, lw =>
        {
            LjFields.Write(md, lw);
            LjFields.Write(new LjCustomTransformData { NonserializedXybEncoded = true }, lw);
            if (Icc != null)
            {
                LjIccEncoder.WriteIcc(Icc, lw);
            }
        });
    }

    /// <summary>Appends what <paramref name="write"/> puts in a libjxl-port bit writer to <paramref name="w"/>.</summary>
    internal static void WriteBits(JxlBitWriter w, Action<LjBitWriter> write)
    {
        var lw = new LjBitWriter();
        write(lw);
        long n = lw.BitsWritten;
        lw.ZeroPadToByte();
        ReadOnlySpan<byte> b = lw.GetSpan();
        for (long i = 0; i < n; i++)
        {
            w.WriteBits((uint)((b[(int)(i >> 3)] >> (int)(i & 7)) & 1), 1);
        }
    }
}
