// libjxl 0.12's default colour management (lib/jxl/cms: jxl_cms.cc, jxl_cms_internal.h, color_encoding_cms.h,
// tone_mapping.h, transfer_functions.h) on top of the skcms port: ICC profile creation for enumerated colour
// encodings (MaybeCreateProfile), recovering encoding fields from an ICC profile (JxlCmsSetFieldsFromICC) and the
// colour transform (JxlCmsInit / DoColorSpaceTransform). The ICC and field code is built for libjxl's baseline
// target (no FMA); the pre/post transfer functions run under Highway dispatch (AVX2: fused multiply-adds).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SharpImage.Formats.Jxl.Lj;

/// <summary>JxlColorEncoding: the API's colour encoding with chromaticities spelled out as doubles.</summary>
internal sealed class LjExtEncoding
{
    public const uint TfGamma = 65535;
    public uint ColorSpace, WhitePoint, Primaries, TransferFunction, RenderingIntent;
    public double Wx, Wy, Rx, Ry, Gx, Gy, Bx, By, Gamma;
}

internal static class LjCms
{
    // ---- cms::ColorEncoding on LjColorEncoding ----

    private static bool ApproxEq(double a, double b, double maxL1 = 1E-3) => Math.Abs(a - b) <= maxL1;

    private static bool SetCustomxy(LjCustomxy c, double x, double y)
    {
        const int kMin = -0x200000, kMax = 0x1FFFFF;
        if (!(Math.Abs(x) < 4.0 && Math.Abs(y) < 4.0))
        {
            return false;
        }

        c.X = (int)MathF.Round((float)(x * 1000000), MidpointRounding.AwayFromZero);
        if (c.X < kMin || c.X > kMax)
        {
            return false;
        }

        c.Y = (int)MathF.Round((float)(y * 1000000), MidpointRounding.AwayFromZero);
        return c.Y >= kMin && c.Y <= kMax;
    }

    public static bool SetWhitePoint(LjColorEncoding c, double x, double y)
    {
        if (x == 0.0 || y == 0.0)
        {
            return false;
        }

        if (ApproxEq(x, 0.3127) && ApproxEq(y, 0.3290))
        {
            c.WhitePoint = LjColorEncoding.WpD65;
            return true;
        }

        if (ApproxEq(x, 1.0 / 3) && ApproxEq(y, 1.0 / 3))
        {
            c.WhitePoint = LjColorEncoding.WpE;
            return true;
        }

        if (ApproxEq(x, 0.314) && ApproxEq(y, 0.351))
        {
            c.WhitePoint = LjColorEncoding.WpDci;
            return true;
        }

        c.WhitePoint = LjColorEncoding.WpCustom;
        return SetCustomxy(c.White, x, y);
    }

    public static bool SetPrimaries(LjColorEncoding c, double rx, double ry, double gx, double gy, double bx, double by)
    {
        if (rx == 0.0 || ry == 0.0 || gx == 0.0 || gy == 0.0 || bx == 0.0 || by == 0.0)
        {
            return false;
        }

        if (ApproxEq(rx, 0.64) && ApproxEq(ry, 0.33) && ApproxEq(gx, 0.30) && ApproxEq(gy, 0.60) && ApproxEq(bx, 0.15) && ApproxEq(by, 0.06))
        {
            c.Primaries = LjColorEncoding.PrSrgb;
            return true;
        }

        if (ApproxEq(rx, 0.708) && ApproxEq(ry, 0.292) && ApproxEq(gx, 0.170) && ApproxEq(gy, 0.797) && ApproxEq(bx, 0.131) && ApproxEq(by, 0.046))
        {
            c.Primaries = LjColorEncoding.Pr2100;
            return true;
        }

        if (ApproxEq(rx, 0.680) && ApproxEq(ry, 0.320) && ApproxEq(gx, 0.265) && ApproxEq(gy, 0.690) && ApproxEq(bx, 0.150) && ApproxEq(by, 0.060))
        {
            c.Primaries = LjColorEncoding.PrP3;
            return true;
        }

        c.Primaries = LjColorEncoding.PrCustom;
        return SetCustomxy(c.Red, rx, ry) && SetCustomxy(c.Green, gx, gy) && SetCustomxy(c.Blue, bx, by);
    }

    public static bool SetGamma(LjCustomTransferFunction tf, double gamma)
    {
        if (gamma < 1.0 / 8192 || gamma > 1.0)
        {
            return false;
        }

        tf.HaveGamma = false;
        if (ApproxEq(gamma, 1.0))
        {
            tf.TransferFunction = LjCustomTransferFunction.TfLinear;
            return true;
        }

        if (ApproxEq(gamma, 1.0 / 2.6))
        {
            tf.TransferFunction = LjCustomTransferFunction.TfDci;
            return true;
        }

        tf.HaveGamma = true;
        tf.Gamma = (uint)MathF.Round((float)(gamma * 10000000), MidpointRounding.AwayFromZero);
        tf.TransferFunction = LjCustomTransferFunction.TfUnknown;
        return true;
    }

    private static void SetTransferFunction(LjCustomTransferFunction tf, uint value)
    {
        tf.HaveGamma = false;
        tf.TransferFunction = value;
    }

    public static bool SameColorSpace(LjColorEncoding a, LjColorEncoding b)
    {
        if (a.ColorSpace != b.ColorSpace || a.WhitePoint != b.WhitePoint)
        {
            return false;
        }

        if (a.WhitePoint == LjColorEncoding.WpCustom && (a.White.X != b.White.X || a.White.Y != b.White.Y))
        {
            return false;
        }

        if (a.HasPrimaries != b.HasPrimaries)
        {
            return false;
        }

        if (a.HasPrimaries)
        {
            if (a.Primaries != b.Primaries)
            {
                return false;
            }

            if (a.Primaries == LjColorEncoding.PrCustom && (a.Red.X != b.Red.X || a.Red.Y != b.Red.Y || a.Green.X != b.Green.X ||
                                                            a.Green.Y != b.Green.Y || a.Blue.X != b.Blue.X || a.Blue.Y != b.Blue.Y))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameTf(LjCustomTransferFunction a, LjCustomTransferFunction b) =>
        a.HaveGamma == b.HaveGamma && (a.HaveGamma ? a.Gamma == b.Gamma : a.TransferFunction == b.TransferFunction);

    /// <summary>cms::ColorEncoding::SameColorEncoding: fields only (the ICC bytes are ignored), never for CMYK.</summary>
    public static bool SameColorEncoding(LjColorEncoding a, LjColorEncoding b) => SameColorSpace(a, b) && SameTf(a.Tf, b.Tf) && !a.Cmyk && !b.Cmyk;

    public static LjExtEncoding ToExternal(LjColorEncoding c)
    {
        var e = new LjExtEncoding { ColorSpace = c.ColorSpace, WhitePoint = c.WhitePoint };
        (e.Wx, e.Wy) = LjCe.WhitePointXy(c);
        if (c.ColorSpace == LjColorEncoding.CsRgb || c.ColorSpace == LjColorEncoding.CsUnknown)
        {
            e.Primaries = c.Primaries;
            double[] p = LjCe.PrimariesXy(c);
            (e.Rx, e.Ry, e.Gx, e.Gy, e.Bx, e.By) = (p[0], p[1], p[2], p[3], p[4], p[5]);
        }

        if (c.Tf.HaveGamma)
        {
            e.TransferFunction = LjExtEncoding.TfGamma;
            e.Gamma = c.Tf.Gamma * (1.0 / 10000000);
        }
        else
        {
            e.TransferFunction = c.Tf.TransferFunction;
            e.Gamma = 0;
        }

        e.RenderingIntent = c.RenderingIntent;
        return e;
    }

    public static bool FromExternal(LjExtEncoding e, LjColorEncoding c)
    {
        c.ColorSpace = e.ColorSpace;
        if (e.WhitePoint is not (LjColorEncoding.WpD65 or LjColorEncoding.WpCustom or LjColorEncoding.WpE or LjColorEncoding.WpDci))
        {
            return false;
        }

        c.WhitePoint = e.WhitePoint;
        if (e.WhitePoint == LjColorEncoding.WpCustom && !SetWhitePoint(c, e.Wx, e.Wy))
        {
            return false;
        }

        if (e.ColorSpace == LjColorEncoding.CsRgb || e.ColorSpace == LjColorEncoding.CsUnknown)
        {
            if (e.Primaries is not (LjColorEncoding.PrSrgb or LjColorEncoding.PrCustom or LjColorEncoding.Pr2100 or LjColorEncoding.PrP3))
            {
                return false;
            }

            c.Primaries = e.Primaries;
            if (e.Primaries == LjColorEncoding.PrCustom && !SetPrimaries(c, e.Rx, e.Ry, e.Gx, e.Gy, e.Bx, e.By))
            {
                return false;
            }
        }

        var tf = new LjCustomTransferFunction();
        if (e.TransferFunction == LjExtEncoding.TfGamma)
        {
            if (!SetGamma(tf, e.Gamma))
            {
                return false;
            }
        }
        else
        {
            if (e.TransferFunction is not (LjCustomTransferFunction.Tf709 or LjCustomTransferFunction.TfUnknown or LjCustomTransferFunction.TfLinear or
                LjCustomTransferFunction.TfSrgb or LjCustomTransferFunction.TfPq or LjCustomTransferFunction.TfDci or LjCustomTransferFunction.TfHlg))
            {
                return false;
            }

            SetTransferFunction(tf, e.TransferFunction);
        }

        tf.NonserializedColorSpace = c.ColorSpace;
        c.Tf = tf;
        if (e.RenderingIntent > 3)
        {
            return false;
        }

        c.RenderingIntent = e.RenderingIntent;
        c.AllDefaultField = false;
        return true;
    }

    /// <summary>ParseDescription (extras/dec/color_description.cc): "sRGB", "DisplayP3", "Rec2100PQ", ... or
    /// ColorSpace_WhitePoint_Primaries_RenderingIntent_TransferFunction such as "RGB_D65_SRG_Rel_Lin". Null when invalid.</summary>
    public static LjColorEncoding? ParseDescription(string description)
    {
        description = description switch
        {
            "sRGB" => "RGB_D65_SRG_Rel_SRG",
            "DisplayP3" => "RGB_D65_DCI_Rel_SRG",
            "Adobe98" => "RGB_D65_Ado_Rel_Ado",
            "ProPhoto" => "RGB_D50_Pro_Rel_Pro",
            "Rec2100PQ" => "RGB_D65_202_Rel_PeQ",
            "Rec2100HLG" => "RGB_D65_202_Rel_HLG",
            _ => description,
        };
        string[] tokens = description.Split('_');
        int next = 0;
        string? Next() => next < tokens.Length && tokens[next].Length > 0 ? tokens[next++] : null;
        static bool ParseDouble(string s, out double d) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);

        var e = new LjExtEncoding();
        switch (Next())
        {
            case "RGB": e.ColorSpace = LjColorEncoding.CsRgb; break;
            case "Gra": e.ColorSpace = LjColorEncoding.CsGray; break;
            case "XYB": e.ColorSpace = LjColorEncoding.CsXyb; break;
            case "CS?": e.ColorSpace = LjColorEncoding.CsUnknown; break;
            default: return null;
        }

        if (e.ColorSpace == LjColorEncoding.CsXyb)
        {
            e.WhitePoint = LjColorEncoding.WpD65;
        }
        else
        {
            string? wp = Next();
            switch (wp)
            {
                case null: return null;
                case "D65": e.WhitePoint = LjColorEncoding.WpD65; break;
                case "Cst": e.WhitePoint = LjColorEncoding.WpCustom; break;
                case "EER": e.WhitePoint = LjColorEncoding.WpE; break;
                case "DCI": e.WhitePoint = LjColorEncoding.WpDci; break;
                case "D50": e.WhitePoint = LjColorEncoding.WpCustom; (e.Wx, e.Wy) = (0.345669, 0.358496); break;
                default:
                {
                    string[] xy = wp.Split(';');
                    if (xy.Length < 2 || !ParseDouble(xy[0], out e.Wx) || !ParseDouble(xy[1], out e.Wy))
                    {
                        return null;
                    }

                    e.WhitePoint = LjColorEncoding.WpCustom;
                    break;
                }
            }
        }

        if (e.ColorSpace != LjColorEncoding.CsGray && e.ColorSpace != LjColorEncoding.CsXyb)
        {
            string? p = Next();
            switch (p)
            {
                case null: return null;
                case "SRG": e.Primaries = LjColorEncoding.PrSrgb; break;
                case "Cst": e.Primaries = LjColorEncoding.PrCustom; break;
                case "202": e.Primaries = LjColorEncoding.Pr2100; break;
                case "DCI": e.Primaries = LjColorEncoding.PrP3; break;
                case "Ado": e.Primaries = LjColorEncoding.PrCustom; (e.Rx, e.Ry, e.Gx, e.Gy, e.Bx, e.By) = (0.6400, 0.3300, 0.2100, 0.7100, 0.1500, 0.0600); break;
                case "Pro":
                    e.Primaries = LjColorEncoding.PrCustom;
                    (e.Rx, e.Ry, e.Gx, e.Gy, e.Bx, e.By) = (0.734699, 0.265301, 0.159597, 0.840403, 0.036598, 0.000105);
                    break;
                default:
                {
                    string[] v = p.Split(';');
                    if (v.Length < 6 || !ParseDouble(v[0], out e.Rx) || !ParseDouble(v[1], out e.Ry) || !ParseDouble(v[2], out e.Gx) ||
                        !ParseDouble(v[3], out e.Gy) || !ParseDouble(v[4], out e.Bx) || !ParseDouble(v[5], out e.By))
                    {
                        return null;
                    }

                    e.Primaries = LjColorEncoding.PrCustom;
                    break;
                }
            }
        }

        switch (Next())
        {
            case "Per": e.RenderingIntent = LjColorEncoding.RiPerceptual; break;
            case "Rel": e.RenderingIntent = LjColorEncoding.RiRelative; break;
            case "Sat": e.RenderingIntent = LjColorEncoding.RiSaturation; break;
            case "Abs": e.RenderingIntent = LjColorEncoding.RiAbsolute; break;
            default: return null;
        }

        if (e.ColorSpace == LjColorEncoding.CsXyb)
        {
            e.TransferFunction = LjExtEncoding.TfGamma;
            e.Gamma = 1 / 3.0;
        }
        else
        {
            string? tf = Next();
            switch (tf)
            {
                case null: return null;
                case "709": e.TransferFunction = LjCustomTransferFunction.Tf709; break;
                case "TF?": e.TransferFunction = LjCustomTransferFunction.TfUnknown; break;
                case "Lin": e.TransferFunction = LjCustomTransferFunction.TfLinear; break;
                case "SRG": e.TransferFunction = LjCustomTransferFunction.TfSrgb; break;
                case "PeQ": e.TransferFunction = LjCustomTransferFunction.TfPq; break;
                case "DCI": e.TransferFunction = LjCustomTransferFunction.TfDci; break;
                case "HLG": e.TransferFunction = LjCustomTransferFunction.TfHlg; break;
                case "Ado": e.TransferFunction = LjExtEncoding.TfGamma; e.Gamma = 256.0 / 563.0; break;
                case "Pro": e.TransferFunction = LjExtEncoding.TfGamma; e.Gamma = 1.0 / 1.8; break;
                default:
                    if (tf[0] != 'g' || !ParseDouble(tf[1..], out e.Gamma))
                    {
                        return null;
                    }

                    e.TransferFunction = LjExtEncoding.TfGamma;
                    break;
            }
        }

        var c = new LjColorEncoding { AllDefaultField = false };
        return FromExternal(e, c) ? c : null;
    }

    /// <summary>The ICC profile of an encoding: its own, or the one libjxl creates for enumerated encodings.</summary>
    public static byte[]? Icc(LjColorEncoding c) => c.WantIcc ? c.Icc : MaybeCreateProfile(ToExternal(c));

    // ---- transfer functions (transfer_functions.h, double precision; copysignf returns a float) ----

    private const double HlgA = 0.17883277, HlgRA = 1.0 / HlgA, HlgB = 1 - (4 * HlgA), HlgC = 0.5599107295, HlgInv12 = 1.0 / 12.0;

    private static double CopySignF(double x, double sign) => MathF.CopySign((float)x, (float)sign);

    public static double HlgDisplayFromEncoded(double e)
    {
        if (e == 0.0)
        {
            return 0.0;
        }

        double originalSign = e;
        e = Math.Abs(e);
        if (e <= 0.5)
        {
            return CopySignF(e * e * (1.0 / 3), originalSign);
        }

        double s = (Math.Exp((e - HlgC) * HlgRA) + HlgB) * HlgInv12;
        return CopySignF(s, originalSign);
    }

    /// <summary>TF_HLG_Base::EncodedFromDisplay. <paramref name="fma"/>: inlined into Highway AVX2 code, where clang contracts
    /// <c>kA * log(12 * s - kB) + kC</c> into fused multiply-adds.</summary>
    public static double HlgEncodedFromDisplay(double s, bool fma)
    {
        if (s == 0.0)
        {
            return 0.0;
        }

        double originalSign = s;
        s = Math.Abs(s);
        if (s <= HlgInv12)
        {
            return CopySignF(Math.Sqrt(3.0 * s), originalSign);
        }

        double e = fma ? Math.FusedMultiplyAdd(HlgA, Math.Log(Math.FusedMultiplyAdd(12, s, -HlgB)), HlgC) : (HlgA * Math.Log((12 * s) - HlgB)) + HlgC;
        return CopySignF(e, originalSign);
    }

    private const double PqM1 = 2610.0 / 16384, PqM2 = 2523.0 / 4096 * 128, PqC1 = 3424.0 / 4096, PqC2 = 2413.0 / 4096 * 32, PqC3 = 2392.0 / 4096 * 32;

    public static double PqDisplayFromEncoded(float displayIntensityTarget, double e)
    {
        if (e == 0.0)
        {
            return 0.0;
        }

        double originalSign = e;
        e = Math.Abs(e);
        double xp = Math.Pow(e, 1.0 / PqM2);
        double num = Math.Max(xp - PqC1, 0.0);
        double den = PqC2 - (PqC3 * xp);
        double d = Math.Pow(num / den, 1.0 / PqM1);
        return CopySignF(d * (10000.0f / displayIntensityTarget), originalSign);
    }

    public static double PqEncodedFromDisplay(float displayIntensityTarget, double d)
    {
        if (d == 0.0)
        {
            return 0.0;
        }

        double originalSign = d;
        d = Math.Abs(d);
        double xp = Math.Pow(d * (displayIntensityTarget * (1.0f / 10000.0f)), PqM1);
        double num = PqC1 + (xp * PqC2);
        double den = 1.0 + (xp * PqC3);
        double e = Math.Pow(num / den, PqM2);
        return CopySignF(e, originalSign);
    }

    // ---- tone mapping (tone_mapping.h) ----

    private sealed class Rec2408ToneMapper
    {
        private readonly float[] sourceRange, targetRange;
        private readonly float redY, greenY, blueY;
        private readonly float pqMasteringMin, pqMasteringRange, invPqMasteringRange, minLum, maxLum, ks, invOneMinusKs, normalizer, invTargetPeak;

        public Rec2408ToneMapper(float[] sourceRange, float[] targetRange, float[] luminances)
        {
            this.sourceRange = sourceRange;
            this.targetRange = targetRange;
            redY = luminances[0];
            greenY = luminances[1];
            blueY = luminances[2];
            pqMasteringMin = InvEotf(sourceRange[0]);
            float pqMasteringMax = InvEotf(sourceRange[1]);
            pqMasteringRange = pqMasteringMax - pqMasteringMin;
            invPqMasteringRange = 1.0f / pqMasteringRange;
            minLum = (InvEotf(targetRange[0]) - pqMasteringMin) * invPqMasteringRange;
            maxLum = (InvEotf(targetRange[1]) - pqMasteringMin) * invPqMasteringRange;
            ks = (1.5f * maxLum) - 0.5f;
            invOneMinusKs = 1.0f / Math.Max(1e-6f, 1.0f - ks);
            normalizer = sourceRange[1] / targetRange[1];
            invTargetPeak = 1.0f / targetRange[1];
        }

        private static float InvEotf(float luminance) => (float)PqEncodedFromDisplay(1.0f, luminance);

        private float T(float a) => (a - ks) * invOneMinusKs;

        private float P(float b)
        {
            float tb = T(b), tb2 = tb * tb, tb3 = tb2 * tb;
            return ((2 * tb3) - (3 * tb2) + 1) * ks + ((tb3 - (2 * tb2) + tb) * (1 - ks)) + (((-2 * tb3) + (3 * tb2)) * maxLum);
        }

        public void ToneMap(float[] rgb)
        {
            float luminance = sourceRange[1] * ((redY * rgb[0]) + (greenY * rgb[1]) + (blueY * rgb[2]));
            float normalizedPq = Math.Min(1.0f, (InvEotf(luminance) - pqMasteringMin) * invPqMasteringRange);
            float e2 = normalizedPq < ks ? normalizedPq : P(normalizedPq);
            float oneMinusE2 = 1 - e2;
            float oneMinusE22 = oneMinusE2 * oneMinusE2;
            float oneMinusE24 = oneMinusE22 * oneMinusE22;
            float e3 = (minLum * oneMinusE24) + e2;
            float e4 = (e3 * pqMasteringRange) + pqMasteringMin;
            float d4 = (float)PqDisplayFromEncoded(1.0f, e4);
            float newLuminance = Math.Clamp(d4, 0.0f, targetRange[1]);
            const float minLuminance = 1e-6f;
            bool useCap = luminance <= minLuminance;
            float ratio = newLuminance / Math.Max(luminance, minLuminance);
            float cap = newLuminance * invTargetPeak;
            float multiplier = ratio * normalizer;
            for (int i = 0; i < 3; i++)
            {
                rgb[i] = useCap ? cap : rgb[i] * multiplier;
            }
        }
    }

    private sealed class HlgOotfBase
    {
        private readonly float exponent, redY, greenY, blueY;
        private readonly bool applyOotf;

        public HlgOotfBase(float sourceLuminance, float targetLuminance, float[] luminances)
        {
            float gamma = LjDec.PowF(1.111f, LjDec.Log2F(targetLuminance / sourceLuminance));
            exponent = gamma - 1;
            applyOotf = exponent < -0.01f || 0.01f < exponent;
            redY = luminances[0];
            greenY = luminances[1];
            blueY = luminances[2];
        }

        public void Apply(float[] rgb)
        {
            if (!applyOotf)
            {
                return;
            }

            float luminance = (redY * rgb[0]) + (greenY * rgb[1]) + (blueY * rgb[2]);
            float ratio = Math.Min(LjDec.PowF(luminance, exponent), 1e9f);
            rgb[0] *= ratio;
            rgb[1] *= ratio;
            rgb[2] *= ratio;
        }
    }

    private static void GamutMapScalar(float[] rgb, float[] lum, float preserveSaturation)
    {
        float luminance = (lum[0] * rgb[0]) + (lum[1] * rgb[1]) + (lum[2] * rgb[2]);
        float grayMixSaturation = 0.0f, grayMixLuminance = 0.0f;
        for (int i = 0; i < 3; i++)
        {
            float val = rgb[i];
            float valMinusGray = val - luminance;
            float invValMinusGray = 1.0f / (valMinusGray == 0.0f ? 1.0f : valMinusGray);
            float valOverValMinusGray = val * invValMinusGray;
            grayMixSaturation = valMinusGray >= 0.0f ? grayMixSaturation : Math.Max(grayMixSaturation, valOverValMinusGray);
            grayMixLuminance = Math.Max(grayMixLuminance, valMinusGray <= 0.0f ? grayMixSaturation : valOverValMinusGray - invValMinusGray);
        }

        float grayMix = Clamp1((preserveSaturation * (grayMixSaturation - grayMixLuminance)) + grayMixLuminance, 0.0f, 1.0f);
        for (int i = 0; i < 3; i++)
        {
            rgb[i] = (grayMix * (luminance - rgb[i])) + rgb[i];
        }

        float maxClr = Math.Max(Math.Max(1.0f, rgb[0]), Math.Max(rgb[1], rgb[2]));
        float normalizer = 1.0f / maxClr;
        for (int i = 0; i < 3; i++)
        {
            rgb[i] *= normalizer;
        }
    }

    private static float Clamp1(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    // ---- ICC profile creation (jxl_cms_internal.h) ----

    private static bool PrimariesToXyz(float rx, float ry, float gx, float gy, float bx, float by, float wx, float wy, out float[,] m)
    {
        m = new float[3, 3];
        if (!(wx >= 0 && wx <= 1 && wy > 0 && wy <= 1))
        {
            return false;
        }

        var primaries = new float[,] { { rx, gx, bx }, { ry, gy, by }, { 1.0f - rx - ry, 1.0f - gx - gy, 1.0f - bx - by } };
        var inv = (float[,])primaries.Clone();
        if (!TryInv(inv))
        {
            return false;
        }

        float[] w = [wx / wy, 1.0f, (1.0f - wx - wy) / wy];
        if (!float.IsFinite(w[0]) || !float.IsFinite(w[2]))
        {
            return false;
        }

        float[] xyz = LjMatrix.MulVec(inv, w);
        var a = new float[,] { { xyz[0], 0, 0 }, { 0, xyz[1], 0 }, { 0, 0, xyz[2] } };
        m = LjMatrix.Mul(primaries, a);
        return true;
    }

    private static readonly float[,] Bradford = { { 0.8951f, 0.2664f, -0.1614f }, { -0.7502f, 1.7135f, 0.0367f }, { 0.0389f, -0.0685f, 1.0296f } };
    private static readonly float[,] BradfordInv =
        { { 0.9869929f, -0.1470543f, 0.1599627f }, { 0.4323053f, 0.5183603f, 0.0492912f }, { -0.0085287f, 0.0400428f, 0.9684867f } };

    private static bool AdaptToXyzD50(float wx, float wy, out float[,] m)
    {
        m = new float[3, 3];
        if (!(wx >= 0 && wx <= 1 && wy > 0 && wy <= 1))
        {
            return false;
        }

        float[] w = [wx / wy, 1.0f, (1.0f - wx - wy) / wy];
        if (!float.IsFinite(w[0]) || !float.IsFinite(w[2]))
        {
            return false;
        }

        float[] w50 = [0.96422f, 1.0f, 0.82521f];
        float[] lms = LjMatrix.MulVec(Bradford, w), lms50 = LjMatrix.MulVec(Bradford, w50);
        if (lms[0] == 0 || lms[1] == 0 || lms[2] == 0)
        {
            return false;
        }

        var a = new float[,] { { lms50[0] / lms[0], 0, 0 }, { 0, lms50[1] / lms[1], 0 }, { 0, 0, lms50[2] / lms[2] } };
        if (!float.IsFinite(a[0, 0]) || !float.IsFinite(a[1, 1]) || !float.IsFinite(a[2, 2]))
        {
            return false;
        }

        m = LjMatrix.Mul(BradfordInv, LjMatrix.Mul(a, Bradford));
        return true;
    }

    private static bool PrimariesToXyzD50(float rx, float ry, float gx, float gy, float bx, float by, float wx, float wy, out float[,] m)
    {
        m = new float[3, 3];
        if (!PrimariesToXyz(rx, ry, gx, gy, bx, by, wx, wy, out float[,] toXyz) || !AdaptToXyzD50(wx, wy, out float[,] d50))
        {
            return false;
        }

        m = LjMatrix.Mul(d50, toXyz);
        return true;
    }

    private static bool TryInv(float[,] m)
    {
        try
        {
            LjMatrix.Inv(m);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool ToneMapPixel(LjExtEncoding c, float[] input, byte[] pcslabOut)
    {
        if (!PrimariesToXyz((float)c.Rx, (float)c.Ry, (float)c.Gx, (float)c.Gy, (float)c.Bx, (float)c.By, (float)c.Wx, (float)c.Wy, out float[,] primariesXyz))
        {
            return false;
        }

        float[] luminances = [primariesXyz[1, 0], primariesXyz[1, 1], primariesXyz[1, 2]];
        var linear = new float[3];
        bool pq = c.TransferFunction == LjCustomTransferFunction.TfPq;
        for (int i = 0; i < 3; i++)
        {
            linear[i] = pq ? (float)PqDisplayFromEncoded(10000.0f, input[i]) : (float)HlgDisplayFromEncoded(input[i]);
        }

        if (pq)
        {
            new Rec2408ToneMapper([0.0f, 10000.0f], [0.0f, 250.0f], luminances).ToneMap(linear);
        }
        else
        {
            new HlgOotfBase(300, 80, luminances).Apply(linear);
        }

        GamutMapScalar(linear, luminances, 0.3f);
        if (!AdaptToXyzD50((float)c.Wx, (float)c.Wy, out float[,] chad))
        {
            return false;
        }

        float[,] toXyzd50 = LjMatrix.Mul(chad, primariesXyz);
        var xyz = new float[3];
        for (int xyzC = 0; xyzC < 3; xyzC++)
        {
            for (int rgbC = 0; rgbC < 3; rgbC++)
            {
                xyz[xyzC] += linear[rgbC] * toXyzd50[xyzC, rgbC];
            }
        }

        const float kDelta = (float)(6.0 / 29);
        static float LabF(float x) => x <= kDelta * kDelta * kDelta ? (x * (1 / (3 * kDelta * kDelta))) + (4.0f / 29) : MathF.Cbrt(x);
        const float kXn = 0.964212f, kYn = 1, kZn = 0.825188f;
        float fx = LabF(xyz[0] / kXn), fy = LabF(xyz[1] / kYn), fz = LabF(xyz[2] / kZn);
        pcslabOut[0] = (byte)MathF.Round(255.0f * Clamp1((1.16f * fy) - .16f, 0.0f, 1.0f), MidpointRounding.AwayFromZero);
        pcslabOut[1] = (byte)MathF.Round(128.0f + Clamp1(500 * (fx - fy), -128.0f, 127.0f), MidpointRounding.AwayFromZero);
        pcslabOut[2] = (byte)MathF.Round(128.0f + Clamp1(200 * (fy - fz), -128.0f, 127.0f), MidpointRounding.AwayFromZero);
        return true;
    }

    private static ushort[] CreateTableCurve(int n, bool hlg, bool toneMap)
    {
        const float kPQIntensityTarget = 10000, kDefaultIntensityTarget = 255;
        float[] lum = [1.0f / 3, 1.0f / 3, 1.0f / 3];
        var toneMapper = new Rec2408ToneMapper([0.0f, kPQIntensityTarget], [0.0f, kDefaultIntensityTarget], lum);
        var table = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            float x = (float)i / (n - 1);
            double dx = x;
            double y = hlg ? HlgDisplayFromEncoded(dx) : PqDisplayFromEncoded(kPQIntensityTarget, dx);
            if (toneMap && !hlg)
            {
                float l = (float)(y * 10000 / kPQIntensityTarget);
                float[] gray = [l, l, l];
                toneMapper.ToneMap(gray);
                y = gray[0];
            }

            y = Math.Clamp(y, 0.0, 1.0);
            table[i] = (ushort)MathF.Round((float)(y * 65535.0), MidpointRounding.AwayFromZero);
        }

        return table;
    }

    private static bool CanToneMap(LjExtEncoding e)
    {
        uint tf = e.TransferFunction, p = e.Primaries, wp = e.WhitePoint;
        return e.ColorSpace == LjColorEncoding.CsRgb && (tf == LjCustomTransferFunction.TfPq || tf == LjCustomTransferFunction.TfHlg) &&
               ((p == LjColorEncoding.PrP3 && (wp == LjColorEncoding.WpD65 || wp == LjColorEncoding.WpDci)) ||
                (p != LjColorEncoding.PrCustom && wp == LjColorEncoding.WpD65));
    }

    private static readonly uint[] Md5Sine =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501, 0x698098d8, 0x8b44f7af, 0xffff5bb1,
        0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821, 0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453,
        0xd8a1e681, 0xe7d3fbc8, 0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a, 0xfffa3942,
        0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70, 0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
        0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665, 0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d,
        0x85845dd1, 0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    private static readonly int[] Md5Shift =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static byte[] IccComputeMd5(List<byte> data)
    {
        var data64 = new List<byte>(data) { 128 };
        int extra = (64 - ((data64.Count + 8) & 63)) & 63;
        for (int i = 0; i < extra; i++)
        {
            data64.Add(0);
        }

        ulong bitLen = (ulong)data.Count << 3;
        for (int i = 0; i < 64; i += 8)
        {
            data64.Add((byte)(bitLen >> i));
        }

        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        for (int i = 0; i < data64.Count; i += 64)
        {
            uint a = a0, b = b0, c = c0, d = d0;
            for (int j = 0; j < 64; j++)
            {
                uint f;
                int g;
                if (j < 16)
                {
                    f = (b & c) | (~b & d);
                    g = j;
                }
                else if (j < 32)
                {
                    f = (d & b) | (~d & c);
                    g = ((5 * j) + 1) & 0xf;
                }
                else if (j < 48)
                {
                    f = b ^ c ^ d;
                    g = ((3 * j) + 5) & 0xf;
                }
                else
                {
                    f = c ^ (b | ~d);
                    g = (7 * j) & 0xf;
                }

                uint u = data64[i + (g * 4)] | ((uint)data64[i + (g * 4) + 1] << 8) | ((uint)data64[i + (g * 4) + 2] << 16) |
                         ((uint)data64[i + (g * 4) + 3] << 24);
                f += a + Md5Sine[j] + u;
                a = d;
                d = c;
                c = b;
                b += (f << Md5Shift[j]) | (f >> (32 - Md5Shift[j]));
            }

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
        }

        var sum = new byte[16];
        uint[] words = [a0, b0, c0, d0];
        for (int w = 0; w < 4; w++)
        {
            for (int k = 0; k < 4; k++)
            {
                sum[(w * 4) + k] = (byte)(words[w] >> (8 * k));
            }
        }

        return sum;
    }

    private static void WriteU32(uint value, int pos, List<byte> icc)
    {
        while (icc.Count < pos + 4)
        {
            icc.Add(0);
        }

        icc[pos] = (byte)(value >> 24);
        icc[pos + 1] = (byte)(value >> 16);
        icc[pos + 2] = (byte)(value >> 8);
        icc[pos + 3] = (byte)value;
    }

    private static void WriteU16(uint value, int pos, List<byte> icc)
    {
        while (icc.Count < pos + 2)
        {
            icc.Add(0);
        }

        icc[pos] = (byte)(value >> 8);
        icc[pos + 1] = (byte)value;
    }

    private static void WriteU8(uint value, int pos, List<byte> icc)
    {
        while (icc.Count < pos + 1)
        {
            icc.Add(0);
        }

        icc[pos] = (byte)value;
    }

    private static void WriteTag(string value, int pos, List<byte> icc)
    {
        while (icc.Count < pos + 4)
        {
            icc.Add(0);
        }

        for (int i = 0; i < 4; i++)
        {
            icc[pos + i] = (byte)value[i];
        }
    }

    private static bool WriteS15Fixed16(float value, int pos, List<byte> icc)
    {
        if (!(-32767.995f <= value && value <= 32767.995f))
        {
            return false;
        }

        int i = (int)MathF.Round(value * 65536.0f, MidpointRounding.AwayFromZero);
        WriteU32((uint)i, pos, icc);
        return true;
    }

    private static void AddToTagTable(string tag, int offset, int size, List<byte> tagtable, List<int> offsets)
    {
        WriteTag(tag, tagtable.Count, tagtable);
        WriteU32(0, tagtable.Count, tagtable);
        offsets.Add(offset);
        WriteU32((uint)size, tagtable.Count, tagtable);
    }

    private static void FinalizeTag(List<byte> tags, ref int offset, ref int size)
    {
        while ((tags.Count & 3) != 0)
        {
            tags.Add(0);
        }

        offset += size;
        size = tags.Count - offset;
    }

    private static void CreateMlucTag(string text, List<byte> tags)
    {
        WriteTag("mluc", tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU32(1, tags.Count, tags);
        WriteU32(12, tags.Count, tags);
        WriteTag("enUS", tags.Count, tags);
        WriteU32((uint)(text.Length * 2), tags.Count, tags);
        WriteU32(28, tags.Count, tags);
        foreach (char ch in text)
        {
            tags.Add(0);
            tags.Add((byte)ch);
        }
    }

    private static bool CreateXyzTag(float[] xyz, List<byte> tags)
    {
        WriteTag("XYZ ", tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        for (int i = 0; i < 3; i++)
        {
            if (!WriteS15Fixed16(xyz[i], tags.Count, tags))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CreateParaTag(float[] parameters, int curveType, List<byte> tags)
    {
        WriteTag("para", tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU16((uint)curveType, tags.Count, tags);
        WriteU16(0, tags.Count, tags);
        foreach (float p in parameters)
        {
            if (!WriteS15Fixed16(p, tags.Count, tags))
            {
                return false;
            }
        }

        return true;
    }

    private static void CreateCurvTag(ushort[] curve, List<byte> tags)
    {
        int pos = tags.Count;
        for (int i = 0; i < 12 + (curve.Length * 2); i++)
        {
            tags.Add(0);
        }

        WriteTag("curv", pos, tags);
        WriteU32(0, pos + 4, tags);
        WriteU32((uint)curve.Length, pos + 8, tags);
        for (int i = 0; i < curve.Length; i++)
        {
            WriteU16(curve[i], pos + 12 + (i * 2), tags);
        }
    }

    // opsin_params.h
    private const float ScaledXybOffset0 = 0.015386134f, ScaledXybOffset1 = 0.0f, ScaledXybOffset2 = 0.27770459f;
    private const float ScaledXybScale0 = 22.995788804f, ScaledXybScale1 = 1.183000077f, ScaledXybScale2 = 1.502141333f;
    private const float OpsinAbsorbanceBias0 = 0.0037930732552754493f;

    private static float ReciprocialSum(float r1, float r2) => r1 * r2 / (r1 + r2);

    private static float[] XybOffset => [ScaledXybOffset0 + ScaledXybOffset1, ScaledXybOffset1 - ScaledXybOffset0 + (1.0f / ScaledXybScale0),
        ScaledXybOffset1 + ScaledXybOffset2];

    private static float[] XybScale => [ReciprocialSum(ScaledXybScale0, ScaledXybScale1), ReciprocialSum(ScaledXybScale0, ScaledXybScale1),
        ReciprocialSum(ScaledXybScale1, ScaledXybScale2)];

    private static float XybCorner(int x, int y, int b, int idx)
    {
        float[] scale = [ScaledXybScale0, ScaledXybScale1, ScaledXybScale2];
        float[] offset = [ScaledXybOffset0, ScaledXybOffset1, ScaledXybOffset2];
        int v = idx == 0 ? x : idx == 1 ? y : b;
        return (v / scale[idx]) - offset[idx];
    }

    private static float ScaledA2BCorner(int x, int y, int b, int idx) =>
        idx == 0 ? XybCorner(x, y, b, 1) + XybCorner(x, y, b, 0)
        : idx == 1 ? XybCorner(x, y, b, 1) - XybCorner(x, y, b, 0)
        : XybCorner(x, y, b, 2) + XybCorner(x, y, b, 1);

    private static bool CreateLutAtoBTagForXyb(List<byte> tags)
    {
        WriteTag("mAB ", tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU8(3, tags.Count, tags);
        WriteU8(3, tags.Count, tags);
        WriteU16(0, tags.Count, tags);
        WriteU32(32, tags.Count, tags);
        WriteU32(244, tags.Count, tags);
        WriteU32(148, tags.Count, tags);
        WriteU32(80, tags.Count, tags);
        WriteU32(32, tags.Count, tags);
        for (int i = 0; i < 3; i++)
        {
            if (!CreateParaTag([1.0f], 0, tags))
            {
                return false;
            }
        }

        for (int i = 0; i < 16; i++)
        {
            WriteU8(i < 3 ? 2u : 0u, tags.Count, tags);
        }

        WriteU8(2, tags.Count, tags);
        WriteU8(0, tags.Count, tags);
        WriteU16(0, tags.Count, tags);
        float[] off = XybOffset, scl = XybScale;
        for (int ix = 0; ix < 2; ix++)
        {
            for (int iy = 0; iy < 2; iy++)
            {
                for (int ib = 0; ib < 2; ib++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float outF = (ScaledA2BCorner(ix, iy, ib, i) + off[i]) * scl[i];
                        int val = (int)MathF.Round(65535 * outF, MidpointRounding.AwayFromZero);
                        WriteU16((uint)val, tags.Count, tags);
                    }
                }
            }
        }

        for (int i = 0; i < 3; i++)
        {
            float b = -off[i] - LjDec.CbrtF(-OpsinAbsorbanceBias0);
            if (!CreateParaTag([3, 1.0f / scl[i], b, 0, Math.Max(0.0f, -b * scl[i])], 3, tags))
            {
                return false;
            }
        }

        double[] matrix = [1.5170095, -1.1065225, 0.071623, -0.050022, 0.5683655, -0.018344, -1.387676, 1.1145555, 0.6857255];
        foreach (double v in matrix)
        {
            if (!WriteS15Fixed16((float)v, tags.Count, tags))
            {
                return false;
            }
        }

        for (int i = 0; i < 3; i++)
        {
            float intercept = 0;
            for (int j = 0; j < 3; j++)
            {
                intercept = (float)(intercept + (matrix[(i * 3) + j] * -OpsinAbsorbanceBias0));
            }

            if (!WriteS15Fixed16(intercept, tags.Count, tags))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CreateLutAtoBTagForHdr(LjExtEncoding c, List<byte> tags)
    {
        const int k3DLutDim = 9;
        WriteTag("mft1", tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU8(3, tags.Count, tags);
        WriteU8(3, tags.Count, tags);
        WriteU8(k3DLutDim, tags.Count, tags);
        WriteU8(0, tags.Count, tags);
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (!WriteS15Fixed16(i == j ? 1.0f : 0.0f, tags.Count, tags))
                {
                    return false;
                }
            }
        }

        for (int c2 = 0; c2 < 3; c2++)
        {
            for (int i = 0; i < 256; i++)
            {
                WriteU8((uint)i, tags.Count, tags);
            }
        }

        var pcslab = new byte[3];
        for (int ix = 0; ix < k3DLutDim; ix++)
        {
            for (int iy = 0; iy < k3DLutDim; iy++)
            {
                for (int ib = 0; ib < k3DLutDim; ib++)
                {
                    float[] f = [ix * (1.0f / (k3DLutDim - 1)), iy * (1.0f / (k3DLutDim - 1)), ib * (1.0f / (k3DLutDim - 1))];
                    if (!ToneMapPixel(c, f, pcslab))
                    {
                        return false;
                    }

                    foreach (byte v in pcslab)
                    {
                        WriteU8(v, tags.Count, tags);
                    }
                }
            }
        }

        for (int c2 = 0; c2 < 3; c2++)
        {
            for (int i = 0; i < 256; i++)
            {
                WriteU8((uint)i, tags.Count, tags);
            }
        }

        return true;
    }

    private static bool CreateNoOpBToATag(List<byte> tags)
    {
        WriteTag("mBA ", tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU8(3, tags.Count, tags);
        WriteU8(3, tags.Count, tags);
        WriteU16(0, tags.Count, tags);
        WriteU32(32, tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        WriteU32(0, tags.Count, tags);
        for (int i = 0; i < 3; i++)
        {
            if (!CreateParaTag([1.0f], 0, tags))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>printf("%g").</summary>
    private static string FormatG(double v)
    {
        if (v == 0)
        {
            return double.IsNegative(v) ? "-0" : "0";
        }

        string s = v.ToString("E5", CultureInfo.InvariantCulture);   // d.dddddE+xxx, rounded to 6 significant digits
        int ePos = s.IndexOf('E');
        int exp = int.Parse(s[(ePos + 1)..], CultureInfo.InvariantCulture);
        if (exp < -4 || exp >= 6)
        {
            string mant = s[..ePos].TrimEnd('0').TrimEnd('.');
            return mant + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }

        string f = v.ToString("F" + Math.Max(0, 5 - exp), CultureInfo.InvariantCulture);
        if (f.Contains('.'))
        {
            f = f.TrimEnd('0').TrimEnd('.');
        }

        return f;
    }

    private static string CsName(uint cs) => cs switch
    {
        LjColorEncoding.CsRgb => "RGB",
        LjColorEncoding.CsGray => "Gra",
        LjColorEncoding.CsXyb => "XYB",
        _ => "CS?",
    };

    private static string WpName(uint wp) => wp switch
    {
        LjColorEncoding.WpD65 => "D65",
        LjColorEncoding.WpCustom => "Cst",
        LjColorEncoding.WpE => "EER",
        _ => "DCI",
    };

    private static string PrName(uint p) => p switch
    {
        LjColorEncoding.PrSrgb => "SRG",
        LjColorEncoding.Pr2100 => "202",
        LjColorEncoding.PrP3 => "DCI",
        _ => "Cst",
    };

    private static string TfName(uint tf) => tf switch
    {
        LjCustomTransferFunction.TfSrgb => "SRG",
        LjCustomTransferFunction.TfLinear => "Lin",
        LjCustomTransferFunction.Tf709 => "709",
        LjCustomTransferFunction.TfPq => "PeQ",
        LjCustomTransferFunction.TfHlg => "HLG",
        LjCustomTransferFunction.TfDci => "DCI",
        _ => "TF?",
    };

    private static string RiName(uint ri) => ri switch
    {
        LjColorEncoding.RiPerceptual => "Per",
        LjColorEncoding.RiRelative => "Rel",
        LjColorEncoding.RiSaturation => "Sat",
        _ => "Abs",
    };

    private static bool CloseEnough(double a, double b) => Math.Abs(a - b) < 3e-5;

    public static string Description(LjExtEncoding c, bool uniqueName)
    {
        if (c.ColorSpace == LjColorEncoding.CsRgb && !uniqueName)
        {
            if (c.WhitePoint == LjColorEncoding.WpD65)
            {
                if (c.TransferFunction == LjCustomTransferFunction.TfSrgb)
                {
                    if (c.Primaries == LjColorEncoding.PrSrgb)
                    {
                        return "sRGB";
                    }

                    if (c.Primaries == LjColorEncoding.PrP3)
                    {
                        return "DisplayP3";
                    }
                }

                if (c.Primaries == LjColorEncoding.Pr2100)
                {
                    if (c.TransferFunction == LjCustomTransferFunction.TfPq)
                    {
                        return "Rec2100PQ";
                    }

                    if (c.TransferFunction == LjCustomTransferFunction.TfHlg)
                    {
                        return "Rec2100HLG";
                    }
                }

                if (c.Primaries == LjColorEncoding.PrCustom && CloseEnough(c.Rx, 0.6400) && CloseEnough(c.Ry, 0.3300) && CloseEnough(c.Gx, 0.2100) &&
                    CloseEnough(c.Gy, 0.7100) && CloseEnough(c.Bx, 0.1500) && CloseEnough(c.By, 0.0600) && c.TransferFunction == LjExtEncoding.TfGamma &&
                    CloseEnough(c.Gamma, 256.0 / 563.0))
                {
                    return "Adobe98";
                }
            }

            if (c.WhitePoint == LjColorEncoding.WpCustom && CloseEnough(c.Wx, 0.345669) && CloseEnough(c.Wy, 0.358496) &&
                c.Primaries == LjColorEncoding.PrCustom && CloseEnough(c.Rx, 0.734699) && CloseEnough(c.Ry, 0.265301) &&
                CloseEnough(c.Gx, 0.159597) && CloseEnough(c.Gy, 0.840403) && CloseEnough(c.Bx, 0.036598) && CloseEnough(c.By, 0.000105) &&
                c.TransferFunction == LjExtEncoding.TfGamma && CloseEnough(c.Gamma, 1.0 / 1.8))
            {
                return "ProPhoto";
            }
        }

        string d = CsName(c.ColorSpace);
        bool explicitWpTf = c.ColorSpace != LjColorEncoding.CsXyb;
        if (explicitWpTf)
        {
            d += '_';
            d += c.WhitePoint == LjColorEncoding.WpCustom ? FormatG(c.Wx) + ';' + FormatG(c.Wy) : WpName(c.WhitePoint);
        }

        if (c.ColorSpace != LjColorEncoding.CsGray && c.ColorSpace != LjColorEncoding.CsXyb)
        {
            d += '_';
            d += c.Primaries == LjColorEncoding.PrCustom
                ? FormatG(c.Rx) + ';' + FormatG(c.Ry) + ';' + FormatG(c.Gx) + ';' + FormatG(c.Gy) + ';' + FormatG(c.Bx) + ';' + FormatG(c.By)
                : PrName(c.Primaries);
        }

        d += '_';
        d += RiName(c.RenderingIntent);
        if (explicitWpTf)
        {
            d += '_';
            d += c.TransferFunction == LjExtEncoding.TfGamma ? "g" + FormatG(c.Gamma) : TfName(c.TransferFunction);
        }

        return d;
    }

    /// <summary>MaybeCreateProfile: the ICC profile libjxl writes for an encoding, or null when there is none.</summary>
    public static byte[]? MaybeCreateProfile(LjExtEncoding c)
    {
        uint tf = c.TransferFunction;
        if (c.ColorSpace == LjColorEncoding.CsUnknown || tf == LjCustomTransferFunction.TfUnknown)
        {
            return null;
        }

        if (c.ColorSpace is not (LjColorEncoding.CsRgb or LjColorEncoding.CsGray or LjColorEncoding.CsXyb))
        {
            return null;
        }

        if (c.ColorSpace == LjColorEncoding.CsXyb && c.RenderingIntent != LjColorEncoding.RiPerceptual)
        {
            return null;
        }

        bool toneMap = CanToneMap(c);
        var header = new List<byte>(new byte[128]);
        WriteU32(0, 0, header);
        WriteTag("jxl ", 4, header);
        WriteU32(0x04400000u, 8, header);
        WriteTag(c.ColorSpace == LjColorEncoding.CsXyb ? "scnr" : "mntr", 12, header);
        WriteTag(c.ColorSpace == LjColorEncoding.CsGray ? "GRAY" : "RGB ", 16, header);
        WriteTag(toneMap ? "Lab " : "XYZ ", 20, header);
        WriteU16(2019, 24, header);
        WriteU16(12, 26, header);
        WriteU16(1, 28, header);
        WriteU16(0, 30, header);
        WriteU16(0, 32, header);
        WriteU16(0, 34, header);
        WriteTag("acsp", 36, header);
        WriteTag("APPL", 40, header);
        WriteU32(0, 44, header);
        WriteU32(0, 48, header);
        WriteU32(0, 52, header);
        WriteU32(0, 56, header);
        WriteU32(0, 60, header);
        WriteU32(c.RenderingIntent, 64, header);
        WriteU32(0x0000f6d6, 68, header);
        WriteU32(0x00010000, 72, header);
        WriteU32(0x0000d32d, 76, header);
        WriteTag("jxl ", 80, header);

        var tagtable = new List<byte>();
        var tags = new List<byte>();
        var offsets = new List<int>();
        WriteU32(0, tagtable.Count, tagtable);
        int tagOffset = 0, tagSize = 0;
        CreateMlucTag(Description(c, false), tags);
        FinalizeTag(tags, ref tagOffset, ref tagSize);
        AddToTagTable("desc", tagOffset, tagSize, tagtable, offsets);
        CreateMlucTag("CC0", tags);
        FinalizeTag(tags, ref tagOffset, ref tagSize);
        AddToTagTable("cprt", tagOffset, tagSize, tagtable, offsets);
        if (c.ColorSpace == LjColorEncoding.CsGray)
        {
            // CIEXYZFromWhiteCIExy
            if (Math.Abs(c.Wy) < 1e-12)
            {
                return null;
            }

            float factor = (float)(1 / c.Wy);
            float[] wtpt = [(float)(c.Wx * factor), 1, (float)((1 - c.Wx - c.Wy) * factor)];
            if (!CreateXyzTag(wtpt, tags))
            {
                return null;
            }
        }
        else if (!CreateXyzTag([0.964203f, 1.0f, 0.824905f], tags))
        {
            return null;
        }

        FinalizeTag(tags, ref tagOffset, ref tagSize);
        AddToTagTable("wtpt", tagOffset, tagSize, tagtable, offsets);
        if (c.ColorSpace != LjColorEncoding.CsGray)
        {
            if (c.Wy == 0 || !AdaptToXyzD50((float)c.Wx, (float)c.Wy, out float[,] chad))
            {
                return null;
            }

            WriteTag("sf32", tags.Count, tags);
            WriteU32(0, tags.Count, tags);
            for (int j = 0; j < 3; j++)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (!WriteS15Fixed16(chad[j, i], tags.Count, tags))
                    {
                        return null;
                    }
                }
            }

            FinalizeTag(tags, ref tagOffset, ref tagSize);
            AddToTagTable("chad", tagOffset, tagSize, tagtable, offsets);
        }

        if (c.ColorSpace == LjColorEncoding.CsRgb)
        {
            // MaybeCreateICCCICPTag
            int cicpPrimaries = -1;
            if (c.Primaries == LjColorEncoding.PrP3)
            {
                cicpPrimaries = c.WhitePoint == LjColorEncoding.WpD65 ? 12 : c.WhitePoint == LjColorEncoding.WpDci ? 11 : -1;
            }
            else if (c.Primaries != LjColorEncoding.PrCustom && c.WhitePoint == LjColorEncoding.WpD65)
            {
                cicpPrimaries = (int)c.Primaries;
            }

            if (cicpPrimaries >= 0 && tf != LjCustomTransferFunction.TfUnknown && tf != LjExtEncoding.TfGamma)
            {
                WriteTag("cicp", tags.Count, tags);
                WriteU32(0, tags.Count, tags);
                WriteU8((uint)cicpPrimaries, tags.Count, tags);
                WriteU8(tf, tags.Count, tags);
                WriteU8(0, tags.Count, tags);
                WriteU8(1, tags.Count, tags);
                FinalizeTag(tags, ref tagOffset, ref tagSize);
                AddToTagTable("cicp", tagOffset, tagSize, tagtable, offsets);
            }

            if (!PrimariesToXyzD50((float)c.Rx, (float)c.Ry, (float)c.Gx, (float)c.Gy, (float)c.Bx, (float)c.By, (float)c.Wx, (float)c.Wy,
                    out float[,] m))
            {
                return null;
            }

            string[] names = ["rXYZ", "gXYZ", "bXYZ"];
            for (int k = 0; k < 3; k++)
            {
                if (!CreateXyzTag([m[0, k], m[1, k], m[2, k]], tags))
                {
                    return null;
                }

                FinalizeTag(tags, ref tagOffset, ref tagSize);
                AddToTagTable(names[k], tagOffset, tagSize, tagtable, offsets);
            }
        }

        if (c.ColorSpace == LjColorEncoding.CsXyb)
        {
            if (!CreateLutAtoBTagForXyb(tags))
            {
                return null;
            }

            FinalizeTag(tags, ref tagOffset, ref tagSize);
            AddToTagTable("A2B0", tagOffset, tagSize, tagtable, offsets);
            if (!CreateNoOpBToATag(tags))
            {
                return null;
            }

            FinalizeTag(tags, ref tagOffset, ref tagSize);
            AddToTagTable("B2A0", tagOffset, tagSize, tagtable, offsets);
        }
        else if (toneMap)
        {
            if (!CreateLutAtoBTagForHdr(c, tags))
            {
                return null;
            }

            FinalizeTag(tags, ref tagOffset, ref tagSize);
            AddToTagTable("A2B0", tagOffset, tagSize, tagtable, offsets);
            if (!CreateNoOpBToATag(tags))
            {
                return null;
            }

            FinalizeTag(tags, ref tagOffset, ref tagSize);
            AddToTagTable("B2A0", tagOffset, tagSize, tagtable, offsets);
        }
        else
        {
            bool ok = true;
            if (tf == LjExtEncoding.TfGamma)
            {
                ok = CreateParaTag([(float)(1.0 / c.Gamma)], 0, tags);
            }
            else
            {
                switch (tf)
                {
                    case LjCustomTransferFunction.TfHlg:
                        CreateCurvTag(CreateTableCurve(64, true, CanToneMap(c)), tags);
                        break;
                    case LjCustomTransferFunction.TfPq:
                        CreateCurvTag(CreateTableCurve(64, false, CanToneMap(c)), tags);
                        break;
                    case LjCustomTransferFunction.TfSrgb:
                        ok = CreateParaTag([2.4f, (float)(1.0 / 1.055), (float)(0.055 / 1.055), (float)(1.0 / 12.92), 0.04045f], 3, tags);
                        break;
                    case LjCustomTransferFunction.Tf709:
                        ok = CreateParaTag([(float)(1.0 / 0.45), (float)(1.0 / 1.099), (float)(0.099 / 1.099), (float)(1.0 / 4.5), 0.081f], 3, tags);
                        break;
                    case LjCustomTransferFunction.TfLinear:
                        ok = CreateParaTag([1.0f, 1.0f, 0.0f, 1.0f, 0.0f], 3, tags);
                        break;
                    case LjCustomTransferFunction.TfDci:
                        ok = CreateParaTag([2.6f, 1.0f, 0.0f, 1.0f, 0.0f], 3, tags);
                        break;
                    default:
                        return null;
                }
            }

            if (!ok)
            {
                return null;
            }

            FinalizeTag(tags, ref tagOffset, ref tagSize);
            if (c.ColorSpace == LjColorEncoding.CsGray)
            {
                AddToTagTable("kTRC", tagOffset, tagSize, tagtable, offsets);
            }
            else
            {
                AddToTagTable("rTRC", tagOffset, tagSize, tagtable, offsets);
                AddToTagTable("gTRC", tagOffset, tagSize, tagtable, offsets);
                AddToTagTable("bTRC", tagOffset, tagSize, tagtable, offsets);
            }
        }

        WriteU32((uint)offsets.Count, 0, tagtable);
        for (int i = 0; i < offsets.Count; i++)
        {
            WriteU32((uint)(offsets[i] + header.Count + tagtable.Count), 4 + (12 * i) + 4, tagtable);
        }

        WriteU32((uint)(header.Count + tagtable.Count + tags.Count), 0, header);
        var icc = new List<byte>(header.Count + tagtable.Count + tags.Count);
        icc.AddRange(header);
        icc.AddRange(tagtable);
        icc.AddRange(tags);
        var iccSum = new List<byte>(icc);
        if (iccSum.Count >= 64 + 4)
        {
            for (int i = 0; i < 4; i++)
            {
                iccSum[44 + i] = 0;
                iccSum[64 + i] = 0;
            }
        }

        byte[] checksum = IccComputeMd5(iccSum);
        byte[] result = icc.ToArray();
        Array.Copy(checksum, 0, result, 84, 16);
        return result;
    }

    // ---- JxlCmsSetFieldsFromICC ----

    private static float[] MatrixProduct(float[] m, float[] v)
    {
        var o = new float[3];
        for (int i = 0; i < 3; i++)
        {
            o[i] = 0;
            for (int j = 0; j < 3; j++)
            {
                o[i] += m[(i * 3) + j] * v[j];
            }
        }

        return o;
    }

    private static (double X, double Y) CieXyFromXyz(float[] xyz)
    {
        double factor = 1.0 / (double)(xyz[0] + xyz[1] + xyz[2]);
        return (xyz[0] * factor, xyz[1] * factor);
    }

    private static bool UnadaptedWhitePoint(SkProfile profile, out (double X, double Y) xy)
    {
        xy = default;
        var wp = new float[3];
        if (!LjSkcms.GetWtpt(profile, wp))
        {
            return false;
        }

        if (!LjSkcms.GetChad(profile, out float[] chad))
        {
            xy = CieXyFromXyz(wp);
            return true;
        }

        var inverseChad = new float[9];
        if (!LjSkcms.Matrix3x3Invert(chad, inverseChad))
        {
            return false;
        }

        xy = CieXyFromXyz(MatrixProduct(inverseChad, wp));
        return true;
    }

    private static bool IdentifyPrimaries(SkProfile profile, (double X, double Y) wpUnadapted, LjColorEncoding c)
    {
        if (!c.HasPrimaries)
        {
            return true;
        }

        var inverseChad = new float[9];
        if (LjSkcms.GetChad(profile, out float[] chad))
        {
            if (!LjSkcms.Matrix3x3Invert(chad, inverseChad))
            {
                return false;
            }
        }
        else
        {
            float[] kLmsFromXyz = [0.8951f, 0.2664f, -0.1614f, -0.7502f, 1.7135f, 0.0367f, 0.0389f, -0.0685f, 1.0296f];
            float[] kXyzFromLms = [0.9869929f, -0.1470543f, 0.1599627f, 0.4323053f, 0.5183603f, 0.0492912f, -0.0085287f, 0.0400428f, 0.9684867f];
            float[] kWpD50Xyz = [0.96420288f, 1.0f, 0.82490540f];
            if (Math.Abs(wpUnadapted.Y) < 1e-12)
            {
                return false;
            }

            float factor = (float)(1 / wpUnadapted.Y);
            float[] wpUnadaptedXyz = [(float)(wpUnadapted.X * factor), 1, (float)((1 - wpUnadapted.X - wpUnadapted.Y) * factor)];
            float[] wpD50Lms = MatrixProduct(kLmsFromXyz, kWpD50Xyz), wpUnadaptedLms = MatrixProduct(kLmsFromXyz, wpUnadaptedXyz);
            inverseChad = [wpUnadaptedLms[0] / wpD50Lms[0], 0, 0, 0, wpUnadaptedLms[1] / wpD50Lms[1], 0, 0, 0, wpUnadaptedLms[2] / wpD50Lms[2]];
            inverseChad = LjSkcms.Matrix3x3Concat(kXyzFromLms, inverseChad);
            inverseChad = LjSkcms.Matrix3x3Concat(inverseChad, kLmsFromXyz);
        }

        var xy = new (double X, double Y)[3];
        for (int i = 0; i < 3; i++)
        {
            var rgb = new float[3];
            rgb[i] = 1;
            var xyz = new float[3];
            LjSkcms.Transform(rgb, SkFmt.RgbFff, SkAlpha.Opaque, profile, xyz, SkFmt.RgbFff, SkAlpha.Opaque, LjSkcms.XyzD50Profile, 1);
            xy[i] = CieXyFromXyz(MatrixProduct(inverseChad, xyz));
        }

        return SetPrimaries(c, xy[0].X, xy[0].Y, xy[1].X, xy[1].Y, xy[2].X, xy[2].Y);
    }

    private static bool IsApproximatelyEqual(SkProfile profile, LjColorEncoding c)
    {
        byte[]? bytes = MaybeCreateProfile(ToExternal(c));
        if (bytes == null)
        {
            return false;
        }

        if (!LjSkcms.Parse(bytes, out SkProfile profileTest))
        {
            return false;
        }

        return LjSkcms.ApproximatelyEqualProfiles(profileTest, profile);
    }

    private static readonly uint[] TfValues =
    [
        LjCustomTransferFunction.Tf709, LjCustomTransferFunction.TfLinear, LjCustomTransferFunction.TfSrgb, LjCustomTransferFunction.TfPq,
        LjCustomTransferFunction.TfDci, LjCustomTransferFunction.TfHlg,
    ];

    private static void DetectTransferFunction(SkProfile profile, LjColorEncoding c)
    {
        static bool IsGamma(in SkTf tf) => tf.A == 1 && tf.B == 0 && tf.D == 0 && tf.E == 0 && tf.F == 0;
        var gamma = new float[3];
        if (profile.HasTrc)
        {
            for (int i = 0; i < 3; i++)
            {
                // libjxl tests profile.trc->parametric, i.e. the first curve, for every channel.
                if (profile.Trc[i].TableEntries == 0 && IsGamma(profile.Trc[0].Parametric))
                {
                    gamma[i] = 1.0f / profile.Trc[0].Parametric.G;
                }
                else if (LjSkcms.ApproximateCurve(profile.Trc[i], out SkTf approx, out _) && IsGamma(approx))
                {
                    gamma[i] = 1.0f / approx.G;
                }
            }
        }

        if (gamma[0] != 0 && Math.Abs(gamma[0] - gamma[1]) < 1e-4f && Math.Abs(gamma[1] - gamma[2]) < 1e-4f)
        {
            if (SetGamma(c.Tf, gamma[0]) && IsApproximatelyEqual(profile, c))
            {
                return;
            }
        }

        foreach (uint tf in TfValues)
        {
            SetTransferFunction(c.Tf, tf);
            if (IsApproximatelyEqual(profile, c))
            {
                return;
            }
        }

        SetTransferFunction(c.Tf, LjCustomTransferFunction.TfUnknown);
    }

    private static bool ApplyCicp(byte colorPrimaries, byte transferCharacteristics, byte matrixCoefficients, byte fullRange, LjColorEncoding c)
    {
        if (matrixCoefficients != 0 || fullRange != 1)
        {
            return false;
        }

        uint tf = transferCharacteristics;
        if (tf is not (LjCustomTransferFunction.Tf709 or LjCustomTransferFunction.TfLinear or LjCustomTransferFunction.TfSrgb or
            LjCustomTransferFunction.TfPq or LjCustomTransferFunction.TfDci or LjCustomTransferFunction.TfHlg))
        {
            return false;
        }

        if (colorPrimaries is not (12 or (byte)LjColorEncoding.PrSrgb or (byte)LjColorEncoding.Pr2100 or (byte)LjColorEncoding.PrP3))
        {
            return false;
        }

        c.ColorSpace = LjColorEncoding.CsRgb;
        SetTransferFunction(c.Tf, tf);
        if (colorPrimaries == LjColorEncoding.PrP3)
        {
            c.WhitePoint = LjColorEncoding.WpDci;
            c.Primaries = LjColorEncoding.PrP3;
        }
        else if (colorPrimaries == 12)
        {
            c.WhitePoint = LjColorEncoding.WpD65;
            c.Primaries = LjColorEncoding.PrP3;
        }
        else
        {
            c.WhitePoint = LjColorEncoding.WpD65;
            c.Primaries = colorPrimaries;
        }

        return true;
    }

    /// <summary>JxlCmsSetFieldsFromICC: the encoding an ICC profile describes (fields kUnknown where it has none).</summary>
    public static bool SetFieldsFromIccExternal(byte[] icc, out LjExtEncoding ext, out bool cmyk)
    {
        ext = new LjExtEncoding { ColorSpace = LjColorEncoding.CsUnknown, TransferFunction = LjCustomTransferFunction.TfUnknown };
        cmyk = false;
        if (icc.Length < 128 || !LjSkcms.Parse(icc, out SkProfile profile))
        {
            return false;
        }

        uint big = icc[67] + ((uint)icc[66] << 8) + ((uint)icc[65] << 16) + ((uint)icc[64] << 24);
        uint little = ((uint)icc[67] << 24) + ((uint)icc[66] << 16) + ((uint)icc[65] << 8) + icc[64];
        uint intent = Math.Min(big, little);
        if (intent > 3)
        {
            return false;
        }

        var c = new LjColorEncoding { AllDefaultField = false, RenderingIntent = intent };
        if (profile.HasCicp && ApplyCicp(profile.CicpPrimaries, profile.CicpTransfer, profile.CicpMatrix, profile.CicpFullRange, c))
        {
            ext = ToExternal(c);
            return true;
        }

        c.ColorSpace = profile.DataColorSpace switch
        {
            LjSkcms.SigRgb or LjSkcms.SigCmyk => LjColorEncoding.CsRgb,
            LjSkcms.SigGray => LjColorEncoding.CsGray,
            _ => LjColorEncoding.CsUnknown,
        };
        cmyk = profile.DataColorSpace == LjSkcms.SigCmyk;
        if (!UnadaptedWhitePoint(profile, out (double X, double Y) wp) || !SetWhitePoint(c, wp.X, wp.Y))
        {
            return false;
        }

        if (!IdentifyPrimaries(profile, wp, c))
        {
            return false;
        }

        DetectTransferFunction(profile, c);
        ext = ToExternal(c);
        return true;
    }

    /// <summary>cms::ColorEncoding::SetFieldsFromICC: a new encoding holding <paramref name="icc"/> with its fields, or null.</summary>
    public static LjColorEncoding? FromIcc(byte[] icc)
    {
        if (!SetFieldsFromIccExternal(icc, out LjExtEncoding ext, out bool cmyk))
        {
            return null;
        }

        var c = new LjColorEncoding { AllDefaultField = false };
        if (!FromExternal(ext, c))
        {
            return null;
        }

        c.Cmyk = cmyk;
        c.WantIcc = true;
        c.Icc = icc;
        return c;
    }

    // ---- JxlCmsInit / DoColorSpaceTransform ----

    private enum ExtraTf
    {
        None,
        Pq,
        Hlg,
        Srgb,
    }

    /// <summary>One colour transform between two encodings (a JxlCms instance).</summary>
    internal sealed class Transform
    {
        private SkProfile profileSrc = null!, profileDst = null!;
        private bool skip, applyHlgOotf;
        private int hlgOotfNumChannels;
        private readonly float[] hlgOotfLuminances = new float[3];
        private ExtraTf preprocess, postprocess;
        private float intensityTarget;

        public int ChannelsSrc { get; private set; }

        public int ChannelsDst { get; private set; }

        /// <summary>ColorSpaceTransform::Init + JxlCmsInit; null when libjxl's init fails.</summary>
        public static Transform? Create(LjColorEncoding src, LjColorEncoding dst, float intensityTarget)
        {
            if (dst.Cmyk)
            {
                return null;
            }

            byte[]? iccSrc = Icc(src), iccDst = Icc(dst);
            if (iccSrc == null || iccSrc.Length == 0 || iccDst == null || iccDst.Length == 0)
            {
                return null;
            }

            LjColorEncoding? cSrc = FromIcc(iccSrc), cDst = FromIcc(iccDst);
            if (cSrc == null || cDst == null)
            {
                return null;
            }

            var t = new Transform { intensityTarget = intensityTarget };
            if (!LjSkcms.Parse(iccSrc, out t.profileSrc) || !LjSkcms.Parse(iccDst, out t.profileDst))
            {
                return null;
            }

            t.skip = SameColorEncoding(cSrc, cDst);
            bool srcHlg = LjCe.TfIsHlg(cSrc), dstHlg = LjCe.TfIsHlg(cDst);
            t.applyHlgOotf = srcHlg != dstHlg;
            if (t.applyHlgOotf)
            {
                LjColorEncoding cHlg = srcHlg ? cSrc : cDst;
                t.hlgOotfNumChannels = LjCe.IsGray(cHlg) ? 1 : 3;
                if (t.hlgOotfNumChannels == 3 && !GetPrimariesLuminances(cHlg, t.hlgOotfLuminances))
                {
                    return null;
                }
            }

            bool srcLinear = LjCe.TfIsLinear(cSrc);
            bool dstLinear = LjCe.TfIsLinear(cDst);
            if (LjCe.TfIsPq(cSrc) || srcHlg || (LjCe.TfIsSrgb(cSrc) && dstLinear && SameColorSpace(cSrc, cDst)))
            {
                LjColorEncoding cLinearSrc = cSrc.Clone();
                SetTransferFunction(cLinearSrc.Tf, LjCustomTransferFunction.TfLinear);
                byte[]? icc = MaybeCreateProfile(ToExternal(cLinearSrc));
                if (icc != null && LjSkcms.Parse(icc, out SkProfile newSrc))
                {
                    t.profileSrc = newSrc;
                    t.preprocess = LjCe.TfIsSrgb(cSrc) ? ExtraTf.Srgb : LjCe.TfIsPq(cSrc) ? ExtraTf.Pq : ExtraTf.Hlg;
                    cSrc = cLinearSrc;
                    srcLinear = true;
                }
                else if (t.applyHlgOotf)
                {
                    return null;
                }
            }

            if (LjCe.TfIsPq(cDst) || dstHlg || (LjCe.TfIsSrgb(cDst) && srcLinear && SameColorSpace(cSrc, cDst)))
            {
                LjColorEncoding cLinearDst = cDst.Clone();
                SetTransferFunction(cLinearDst.Tf, LjCustomTransferFunction.TfLinear);
                byte[]? icc = MaybeCreateProfile(ToExternal(cLinearDst));
                if (icc != null && LjSkcms.Parse(icc, out SkProfile newDst))
                {
                    t.profileDst = newDst;
                    t.postprocess = LjCe.TfIsSrgb(cDst) ? ExtraTf.Srgb : LjCe.TfIsPq(cDst) ? ExtraTf.Pq : ExtraTf.Hlg;
                    cDst = cLinearDst;
                }
                else if (t.applyHlgOotf)
                {
                    return null;
                }
            }

            if (SameColorEncoding(cSrc, cDst))
            {
                t.skip = true;
            }

            if (!LjSkcms.MakeUsableAsDestination(t.profileDst))
            {
                return null;
            }

            t.ChannelsSrc = cSrc.Cmyk ? 4 : LjCe.IsGray(cSrc) ? 1 : 3;
            t.ChannelsDst = LjCe.IsGray(cDst) ? 1 : 3;
            return t;
        }

        private static bool GetPrimariesLuminances(LjColorEncoding encoding, float[] luminances)
        {
            (double wx, double wy) = LjCe.WhitePointXy(encoding);
            if (Math.Abs(wy) < 1e-12)
            {
                return false;
            }

            float factor = (float)(1 / wy);
            float[] whiteXyz = [(float)(wx * factor), 1, (float)((1 - wx - wy) * factor)];
            double[] p = LjCe.PrimariesXy(encoding);
            var ch = new double[3, 3]
            {
                { p[0], p[2], p[4] }, { p[1], p[3], p[5] }, { 1 - p[0] - p[1], 1 - p[2] - p[3], 1 - p[4] - p[5] },
            };
            var t = new double[3, 3];
            t[0, 0] = (ch[1, 1] * ch[2, 2]) - (ch[1, 2] * ch[2, 1]);
            t[0, 1] = (ch[0, 2] * ch[2, 1]) - (ch[0, 1] * ch[2, 2]);
            t[0, 2] = (ch[0, 1] * ch[1, 2]) - (ch[0, 2] * ch[1, 1]);
            t[1, 0] = (ch[1, 2] * ch[2, 0]) - (ch[1, 0] * ch[2, 2]);
            t[1, 1] = (ch[0, 0] * ch[2, 2]) - (ch[0, 2] * ch[2, 0]);
            t[1, 2] = (ch[0, 2] * ch[1, 0]) - (ch[0, 0] * ch[1, 2]);
            t[2, 0] = (ch[1, 0] * ch[2, 1]) - (ch[1, 1] * ch[2, 0]);
            t[2, 1] = (ch[0, 1] * ch[2, 0]) - (ch[0, 0] * ch[2, 1]);
            t[2, 2] = (ch[0, 0] * ch[1, 1]) - (ch[0, 1] * ch[1, 0]);
            double det = (ch[0, 0] * t[0, 0]) + (ch[0, 1] * t[1, 0]) + (ch[0, 2] * t[2, 0]);
            if (Math.Abs(det) < 1e-10)
            {
                return false;
            }

            double idet = 1.0 / det;
            double[] ys = [p[1], p[3], p[5]];
            for (int i = 0; i < 3; i++)
            {
                luminances[i] = (float)(ys[i] * ((t[i, 0] * idet * whiteXyz[0]) + (t[i, 1] * idet * whiteXyz[1]) + (t[i, 2] * idet * whiteXyz[2])));
            }

            return true;
        }

        private void ApplyHlgOotf(float[] buf, int size, bool forward)
        {
            if (295 <= intensityTarget && intensityTarget <= 305)
            {
                return;
            }

            float gamma = 1.2f * LjDec.PowF(1.111f, LjDec.Log2F(intensityTarget * 1e-3f));
            if (!forward)
            {
                gamma = 1.0f / gamma;
            }

            if (hlgOotfNumChannels == 1)
            {
                for (int x = 0; x < size; x++)
                {
                    buf[x] = LjDec.PowF(buf[x], gamma);
                }

                return;
            }

            for (int x = 0; x < size; x += 3)
            {
                // djxl inlines the inverse OOTF (AfterTransform) into the Highway AVX2 transform, where clang fuses the
                // multiply-adds; BeforeTransform calls the out-of-line baseline copy.
                float luminance = forward
                    ? (buf[x] * hlgOotfLuminances[0]) + (buf[x + 1] * hlgOotfLuminances[1]) + (buf[x + 2] * hlgOotfLuminances[2])
                    : MathF.FusedMultiplyAdd(buf[x + 2], hlgOotfLuminances[2],
                        MathF.FusedMultiplyAdd(buf[x], hlgOotfLuminances[0], buf[x + 1] * hlgOotfLuminances[1]));
                float ratio = LjDec.PowF(luminance, gamma - 1);
                if (float.IsFinite(ratio))
                {
                    buf[x] *= ratio;
                    buf[x + 1] *= ratio;
                    buf[x + 2] *= ratio;
                    if (forward && gamma < 1)
                    {
                        float maximum = Math.Max(buf[x], Math.Max(buf[x + 1], buf[x + 2]));
                        if (maximum > 1)
                        {
                            float normalizer = 1.0f / maximum;
                            buf[x] *= normalizer;
                            buf[x + 1] *= normalizer;
                            buf[x + 2] *= normalizer;
                        }
                    }
                }
            }
        }

        private void BeforeTransform(float[] buf, int size)
        {
            switch (preprocess)
            {
                case ExtraTf.Pq:
                {
                    float fromNits = 10000.0f / intensityTarget;
                    for (int i = 0; i < size; i++)
                    {
                        buf[i] = PqDisplayFromEncodedFast(buf[i], fromNits);
                    }

                    break;
                }

                case ExtraTf.Hlg:
                    for (int i = 0; i < size; i++)
                    {
                        buf[i] = (float)HlgDisplayFromEncoded(buf[i]);
                    }

                    if (applyHlgOotf)
                    {
                        ApplyHlgOotf(buf, size, true);
                    }

                    break;
                case ExtraTf.Srgb:
                    for (int i = 0; i < size; i++)
                    {
                        buf[i] = SrgbDisplayFromEncoded(buf[i]);
                    }

                    break;
            }
        }

        private void AfterTransform(float[] buf, int size)
        {
            switch (postprocess)
            {
                case ExtraTf.Pq:
                {
                    Func<float, float> pq = LjTf.Pq(intensityTarget);
                    for (int i = 0; i < size; i++)
                    {
                        buf[i] = pq(buf[i]);
                    }

                    break;
                }

                case ExtraTf.Hlg:
                    if (applyHlgOotf)
                    {
                        ApplyHlgOotf(buf, size, false);
                    }

                    for (int i = 0; i < size; i++)
                    {
                        buf[i] = (float)HlgEncodedFromDisplay(buf[i], true);
                    }

                    break;
                case ExtraTf.Srgb:
                    for (int i = 0; i < size; i++)
                    {
                        buf[i] = LjTf.SrgbFromLinear(buf[i]);
                    }

                    break;
            }
        }

        /// <summary>DoColorSpaceTransform: <paramref name="src"/> holds <c>xsize * ChannelsSrc</c> interleaved samples and is
        /// overwritten; <paramref name="dst"/> receives <c>xsize * ChannelsDst</c> samples (it needs room for 3 per pixel).</summary>
        public void Run(float[] src, float[] dst, int xsize)
        {
            if (preprocess != ExtraTf.None)
            {
                BeforeTransform(src, xsize * ChannelsSrc);
            }

            if (ChannelsSrc == 1 && !skip)
            {
                for (int i = 0; i < xsize; i++)
                {
                    int x = xsize - i - 1;
                    src[(x * 3) + 2] = src[(x * 3) + 1] = src[x * 3] = src[x];
                }
            }

            if (skip)
            {
                Array.Copy(src, dst, xsize * ChannelsSrc);
            }
            else
            {
                float[] xformDst = ChannelsDst == 1 ? new float[xsize * 3] : dst;
                LjSkcms.Transform(src, ChannelsSrc == 4 ? SkFmt.RgbaFfff : SkFmt.RgbFff, SkAlpha.Opaque, profileSrc, xformDst, SkFmt.RgbFff,
                    SkAlpha.Opaque, profileDst, xsize);
                if (ChannelsDst == 1)
                {
                    for (int x = 0; x < xsize; x++)
                    {
                        dst[x] = xformDst[x * 3];
                    }
                }
            }

            if (postprocess != ExtraTf.None)
            {
                AfterTransform(dst, xsize * ChannelsDst);
            }
        }
    }

    // TF_SRGB / TF_PQ DisplayFromEncoded (transfer_functions-inl.h, Highway AVX2: fused multiply-adds)
    private static readonly float[] SrgbToLinP = [2.200248328e-04f, 1.043637593e-02f, 1.624820318e-01f, 7.961564959e-01f, 8.210152774e-01f];
    private static readonly float[] SrgbToLinQ = [2.631846970e-01f, 1.076976492e+00f, 4.987528350e-01f, -5.512498495e-02f, 6.521209011e-03f];
    private static readonly float[] PqToLinP = [2.62975656e-04f, -6.23553089e-03f, 7.38602301e-01f, 2.64553172e+00f, 5.50034862e-01f];
    private static readonly float[] PqToLinQ = [4.21350107e+02f, -4.28736818e+02f, 1.74364667e+02f, -3.39078883e+01f, 2.67718770e+00f];

    private static float EvalRational(float x, float[] p, float[] q)
    {
        float yp = p[4], yq = q[4];
        for (int k = 3; k >= 0; k--)
        {
            yp = MathF.FusedMultiplyAdd(yp, x, p[k]);
            yq = MathF.FusedMultiplyAdd(yq, x, q[k]);
        }

        return yp / yq;
    }

    public static float SrgbDisplayFromEncoded(float v)
    {
        float x = MathF.Abs(v);
        float linear = x * (1.0f / 12.92f);
        float poly = EvalRational(x, SrgbToLinP, SrgbToLinQ);
        float magnitude = x > 0.04045f ? poly : linear;
        return BitConverter.SingleToInt32Bits(v) < 0 ? -magnitude : magnitude;
    }

    private static float PqDisplayFromEncodedFast(float v, float fromNits)
    {
        float x = MathF.Abs(v);
        float xpxx = MathF.FusedMultiplyAdd(x, x, x);
        float magnitude = EvalRational(xpxx, PqToLinP, PqToLinQ);
        float r = magnitude * fromNits;
        return BitConverter.SingleToInt32Bits(v) < 0 ? -r : r;
    }
}
