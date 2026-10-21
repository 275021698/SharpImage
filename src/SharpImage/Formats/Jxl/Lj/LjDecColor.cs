// libjxl 0.12 decoder colour output: OpsinParams / OutputEncodingInfo (dec_xyb.cc), XybToRgb (dec_xyb-inl.h),
// the exact transfer functions of the high-precision build (cms/transfer_functions-inl.h) and the 3x3 colour matrix
// helpers (base/matrix_ops.h, cms/jxl_cms_internal.h).
using System;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjOpsinParams
{
    /// <summary>inverse_opsin_matrix: 9 entries (libjxl stores each broadcast to 4 lanes).</summary>
    public float[] InverseOpsinMatrix = new float[9];
    public float[] OpsinBiases = new float[4];
    public float[] OpsinBiasesCbrt = new float[4];
    public float[] QuantBiases = new float[4];

    public static void XybToRgb(LjOpsinParams p, float opsinX, float opsinY, float opsinB, out float r, out float g, out float b)
    {
        float gammaR = opsinY + opsinX;
        float gammaG = opsinY - opsinX;
        float gammaB = opsinB;
        gammaR -= p.OpsinBiasesCbrt[0];
        gammaG -= p.OpsinBiasesCbrt[1];
        gammaB -= p.OpsinBiasesCbrt[2];
        float gammaR2 = gammaR * gammaR, gammaG2 = gammaG * gammaG, gammaB2 = gammaB * gammaB;
        float mixedR = MathF.FusedMultiplyAdd(gammaR2, gammaR, p.OpsinBiases[0]);
        float mixedG = MathF.FusedMultiplyAdd(gammaG2, gammaG, p.OpsinBiases[1]);
        float mixedB = MathF.FusedMultiplyAdd(gammaB2, gammaB, p.OpsinBiases[2]);
        float[] m = p.InverseOpsinMatrix;
        r = m[0] * mixedR;
        g = m[3] * mixedR;
        b = m[6] * mixedR;
        r = MathF.FusedMultiplyAdd(m[1], mixedG, r);
        g = MathF.FusedMultiplyAdd(m[4], mixedG, g);
        b = MathF.FusedMultiplyAdd(m[7], mixedG, b);
        r = MathF.FusedMultiplyAdd(m[2], mixedB, r);
        g = MathF.FusedMultiplyAdd(m[5], mixedB, g);
        b = MathF.FusedMultiplyAdd(m[8], mixedB, b);
    }
}

internal static class LjMatrix
{
    /// <summary>Mul3x3Matrix: C = A * B with double accumulation, float storage.</summary>
    public static float[,] Mul(float[,] a, float[,] b)
    {
        var c = new float[3, 3];
        for (int x = 0; x < 3; x++)
        {
            double t0 = b[0, x], t1 = b[1, x], t2 = b[2, x];
            for (int y = 0; y < 3; y++)
            {
                c[y, x] = (float)((a[y, 0] * t0) + (a[y, 1] * t1) + (a[y, 2] * t2));
            }
        }

        return c;
    }

    public static float[] MulVec(float[,] a, float[] b)
    {
        var c = new float[3];
        for (int y = 0; y < 3; y++)
        {
            double e = 0;
            for (int x = 0; x < 3; x++)
            {
                e += (double)a[y, x] * b[x];
            }

            c[y] = (float)e;
        }

        return c;
    }

    public static void Inv(float[,] m)
    {
        var t = new double[3, 3];
        t[0, 0] = ((double)m[1, 1] * m[2, 2]) - ((double)m[1, 2] * m[2, 1]);
        t[0, 1] = ((double)m[0, 2] * m[2, 1]) - ((double)m[0, 1] * m[2, 2]);
        t[0, 2] = ((double)m[0, 1] * m[1, 2]) - ((double)m[0, 2] * m[1, 1]);
        t[1, 0] = ((double)m[1, 2] * m[2, 0]) - ((double)m[1, 0] * m[2, 2]);
        t[1, 1] = ((double)m[0, 0] * m[2, 2]) - ((double)m[0, 2] * m[2, 0]);
        t[1, 2] = ((double)m[0, 2] * m[1, 0]) - ((double)m[0, 0] * m[1, 2]);
        t[2, 0] = ((double)m[1, 0] * m[2, 1]) - ((double)m[1, 1] * m[2, 0]);
        t[2, 1] = ((double)m[0, 1] * m[2, 0]) - ((double)m[0, 0] * m[2, 1]);
        t[2, 2] = ((double)m[0, 0] * m[1, 1]) - ((double)m[0, 1] * m[1, 0]);
        double det = (m[0, 0] * t[0, 0]) + (m[0, 1] * t[1, 0]) + (m[0, 2] * t[2, 0]);
        if (Math.Abs(det) < 1e-10)
        {
            throw LjDec.Fail("Matrix determinant is too close to 0");
        }

        double idet = 1.0 / det;
        for (int j = 0; j < 3; j++)
        {
            for (int i = 0; i < 3; i++)
            {
                m[j, i] = (float)(t[j, i] * idet);
            }
        }
    }

    public static float[,] PrimariesToXyz(float rx, float ry, float gx, float gy, float bx, float by, float wx, float wy)
    {
        if (!(wx >= 0 && wx <= 1 && wy > 0 && wy <= 1))
        {
            throw LjDec.Fail("Invalid white point");
        }

        var primaries = new float[,] { { rx, gx, bx }, { ry, gy, by }, { 1.0f - rx - ry, 1.0f - gx - gy, 1.0f - bx - by } };
        var inv = (float[,])primaries.Clone();
        Inv(inv);
        float[] w = [wx / wy, 1.0f, (1.0f - wx - wy) / wy];
        float[] xyz = MulVec(inv, w);
        var a = new float[,] { { xyz[0], 0, 0 }, { 0, xyz[1], 0 }, { 0, 0, xyz[2] } };
        return Mul(primaries, a);
    }

    private static readonly float[,] Bradford = { { 0.8951f, 0.2664f, -0.1614f }, { -0.7502f, 1.7135f, 0.0367f }, { 0.0389f, -0.0685f, 1.0296f } };
    private static readonly float[,] BradfordInv = { { 0.9869929f, -0.1470543f, 0.1599627f }, { 0.4323053f, 0.5183603f, 0.0492912f }, { -0.0085287f, 0.0400428f, 0.9684867f } };

    public static float[,] AdaptToXyzD50(float wx, float wy)
    {
        if (!(wx >= 0 && wx <= 1 && wy > 0 && wy <= 1))
        {
            throw LjDec.Fail("Invalid white point");
        }

        float[] w = [wx / wy, 1.0f, (1.0f - wx - wy) / wy];
        float[] w50 = [0.96422f, 1.0f, 0.82521f];
        float[] lms = MulVec(Bradford, w);
        float[] lms50 = MulVec(Bradford, w50);
        var a = new float[,] { { lms50[0] / lms[0], 0, 0 }, { 0, lms50[1] / lms[1], 0 }, { 0, 0, lms50[2] / lms[2] } };
        float[,] b = Mul(a, Bradford);
        return Mul(BradfordInv, b);
    }

    public static float[,] PrimariesToXyzD50(float rx, float ry, float gx, float gy, float bx, float by, float wx, float wy)
    {
        float[,] toXyz = PrimariesToXyz(rx, ry, gx, gy, bx, by, wx, wy);
        float[,] d50 = AdaptToXyzD50(wx, wy);
        return Mul(d50, toXyz);
    }
}

/// <summary>Colour-encoding queries libjxl's ColorEncoding provides.</summary>
internal static class LjCe
{
    public static bool IsGray(LjColorEncoding c) => c.ColorSpace == LjColorEncoding.CsGray;

    public static (double X, double Y) WhitePointXy(LjColorEncoding c) => c.WhitePoint switch
    {
        LjColorEncoding.WpCustom => (c.White.X * (1.0 / 1000000), c.White.Y * (1.0 / 1000000)),
        LjColorEncoding.WpD65 => (0.3127, 0.3290),
        LjColorEncoding.WpDci => (0.314, 0.351),
        _ => (1.0 / 3, 1.0 / 3),
    };

    public static double[] PrimariesXy(LjColorEncoding c) => c.Primaries switch
    {
        LjColorEncoding.PrCustom => [c.Red.X * (1.0 / 1000000), c.Red.Y * (1.0 / 1000000), c.Green.X * (1.0 / 1000000), c.Green.Y * (1.0 / 1000000),
            c.Blue.X * (1.0 / 1000000), c.Blue.Y * (1.0 / 1000000)],
        LjColorEncoding.PrSrgb => [0.639998686, 0.330010138, 0.300003784, 0.600003357, 0.150002046, 0.059997204],
        LjColorEncoding.Pr2100 => [0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
        _ => [0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
    };

    public static bool TfIsLinear(LjColorEncoding c) => !c.Tf.HaveGamma && c.Tf.TransferFunction == LjCustomTransferFunction.TfLinear;

    public static bool TfIsSrgb(LjColorEncoding c) => !c.Tf.HaveGamma && c.Tf.TransferFunction == LjCustomTransferFunction.TfSrgb;

    public static bool TfIsPq(LjColorEncoding c) => !c.Tf.HaveGamma && c.Tf.TransferFunction == LjCustomTransferFunction.TfPq;

    public static bool TfIsHlg(LjColorEncoding c) => !c.Tf.HaveGamma && c.Tf.TransferFunction == LjCustomTransferFunction.TfHlg;

    public static bool TfIs709(LjColorEncoding c) => !c.Tf.HaveGamma && c.Tf.TransferFunction == LjCustomTransferFunction.Tf709;

    public static bool TfIsDci(LjColorEncoding c) => !c.Tf.HaveGamma && c.Tf.TransferFunction == LjCustomTransferFunction.TfDci;

    public static double Gamma(LjColorEncoding c) => c.Tf.Gamma * (1.0 / 10000000);

    /// <summary>CanOutputToColorEncoding (dec_xyb.cc).</summary>
    public static bool CanOutputTo(LjColorEncoding c)
    {
        if (c.WantIcc || c.ColorSpace == LjColorEncoding.CsUnknown)
        {
            return false;   // !HaveFields
        }

        if (!TfIsPq(c) && !TfIsSrgb(c) && !c.Tf.HaveGamma && !TfIsLinear(c) && !TfIsHlg(c) && !TfIsDci(c) && !TfIs709(c))
        {
            return false;
        }

        if (IsGray(c) && c.WhitePoint != LjColorEncoding.WpD65)
        {
            return false;
        }

        return true;
    }

    public static LjColorEncoding LinearSrgb(bool gray) => new()
    {
        AllDefaultField = false,
        ColorSpace = gray ? LjColorEncoding.CsGray : LjColorEncoding.CsRgb,
        WhitePoint = LjColorEncoding.WpD65,
        Primaries = LjColorEncoding.PrSrgb,
        Tf = new LjCustomTransferFunction { TransferFunction = LjCustomTransferFunction.TfLinear },
        RenderingIntent = LjColorEncoding.RiRelative,
    };

    public static LjColorEncoding Srgb(bool gray) => new()
    {
        AllDefaultField = false,
        ColorSpace = gray ? LjColorEncoding.CsGray : LjColorEncoding.CsRgb,
        WhitePoint = LjColorEncoding.WpD65,
        Primaries = LjColorEncoding.PrSrgb,
        Tf = new LjCustomTransferFunction { TransferFunction = LjCustomTransferFunction.TfSrgb },
        RenderingIntent = LjColorEncoding.RiRelative,
    };

    /// <summary>ColorEncoding::SameColorEncoding.</summary>
    public static bool Same(LjColorEncoding a, LjColorEncoding b)
    {
        if (a.WantIcc || b.WantIcc)
        {
            return a.WantIcc && b.WantIcc && a.Icc != null && b.Icc != null && a.Icc.AsSpan().SequenceEqual(b.Icc);
        }

        if (a.ColorSpace != b.ColorSpace || a.WhitePoint != b.WhitePoint)
        {
            return false;
        }

        if (a.WhitePoint == LjColorEncoding.WpCustom && (a.White.X != b.White.X || a.White.Y != b.White.Y))
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

        if (a.Tf.HaveGamma != b.Tf.HaveGamma)
        {
            return false;
        }

        return a.Tf.HaveGamma ? a.Tf.Gamma == b.Tf.Gamma : a.Tf.TransferFunction == b.Tf.TransferFunction;
    }
}

/// <summary>OutputEncodingInfo (dec_xyb.cc).</summary>
internal sealed class LjOutputEncoding
{
    public LjColorEncoding OrigColorEncoding = new();
    public float OrigIntensityTarget;
    public float[,] OrigInverseMatrix = new float[3, 3];
    public bool DefaultTransform;
    public bool XybEncoded;
    public LjColorEncoding ColorEncoding = new();
    public bool ColorEncodingIsOriginal;
    public LjOpsinParams OpsinParams = new();
    public bool AllDefaultOpsin;
    public float InverseGamma;
    public float[] Luminances = [0.2126f, 0.7152f, 0.0722f];
    public float DesiredIntensityTarget;

    /// <summary>A colour management system is available (JxlDecoderSetCms).</summary>
    public bool CmsSet;

    /// <summary>ColorEncoding with a linear transfer function (linear_color_encoding).</summary>
    public LjColorEncoding LinearColorEncoding
    {
        get
        {
            LjColorEncoding c = ColorEncoding.Clone();
            c.Tf.HaveGamma = false;
            c.Tf.TransferFunction = LjCustomTransferFunction.TfLinear;
            return c;
        }
    }

    public void SetFromMetadata(LjImageMetadata m, LjCustomTransformData transform)
    {
        OrigColorEncoding = m.ColorEncoding;
        OrigIntensityTarget = m.ToneMapping.IntensityTarget;
        DesiredIntensityTarget = OrigIntensityTarget;
        LjOpsinInverseMatrix im = transform.OpsinInverseMatrix;
        for (int j = 0; j < 3; j++)
        {
            for (int i = 0; i < 3; i++)
            {
                OrigInverseMatrix[j, i] = im.InverseMatrix[(j * 3) + i];
            }
        }

        DefaultTransform = im.AllDefaultField;
        XybEncoded = m.XybEncoded;
        for (int i = 0; i < 3; i++)
        {
            OpsinParams.OpsinBiases[i] = im.OpsinBiases[i];
            OpsinParams.OpsinBiasesCbrt[i] = LjDec.CbrtF(OpsinParams.OpsinBiases[i]);
        }

        OpsinParams.OpsinBiasesCbrt[3] = OpsinParams.OpsinBiases[3] = 1;
        Array.Copy(im.QuantBiases, OpsinParams.QuantBiases, 4);
        bool origOk = LjCe.CanOutputTo(OrigColorEncoding);
        bool origGray = LjCe.IsGray(OrigColorEncoding);
        SetColorEncoding(!XybEncoded || origOk ? OrigColorEncoding : LjCe.LinearSrgb(origGray));
    }

    public void SetColorEncoding(LjColorEncoding desired)
    {
        ColorEncoding = desired;
        ColorEncodingIsOriginal = LjCe.Same(OrigColorEncoding, desired);
        bool inverseMatrixIsDefault = DefaultTransform;
        var inverseMatrix = (float[,])OrigInverseMatrix.Clone();
        Luminances = [0.2126f, 0.7152f, 0.0722f];
        if (!desired.WantIcc && (desired.Primaries != LjColorEncoding.PrSrgb || desired.WhitePoint != LjColorEncoding.WpD65) && !LjCe.IsGray(desired))
        {
            double[] sp = LjCe.PrimariesXy(LjCe.Srgb(false));
            float[,] srgbToXyzd50 = LjMatrix.PrimariesToXyzD50((float)sp[0], (float)sp[1], (float)sp[2], (float)sp[3], (float)sp[4], (float)sp[5],
                (float)0.3127, (float)0.3290);
            double[] p = LjCe.PrimariesXy(desired);
            (double wx, double wy) = LjCe.WhitePointXy(desired);
            float[,] originalToXyz = LjMatrix.PrimariesToXyz((float)p[0], (float)p[1], (float)p[2], (float)p[3], (float)p[4], (float)p[5], (float)wx, (float)wy);
            Luminances = [originalToXyz[1, 0], originalToXyz[1, 1], originalToXyz[1, 2]];
            if (XybEncoded)
            {
                float[,] adaptToD50 = LjMatrix.AdaptToXyzD50((float)wx, (float)wy);
                float[,] xyzd50ToOriginal = LjMatrix.Mul(adaptToD50, originalToXyz);
                LjMatrix.Inv(xyzd50ToOriginal);
                float[,] srgbToOriginal = LjMatrix.Mul(xyzd50ToOriginal, srgbToXyzd50);
                inverseMatrix = LjMatrix.Mul(srgbToOriginal, OrigInverseMatrix);
                inverseMatrixIsDefault = false;
            }
        }

        if (LjCe.IsGray(desired))
        {
            var srgbToLuma = new float[3, 3];
            for (int j = 0; j < 3; j++)
            {
                for (int i = 0; i < 3; i++)
                {
                    srgbToLuma[j, i] = Luminances[i];
                }
            }

            inverseMatrix = LjMatrix.Mul(srgbToLuma, inverseMatrix);
        }

        if (XybEncoded)
        {
            for (int j = 0; j < 3; ++j)
            {
                for (int i = 0; i < 3; ++i)
                {
                    OpsinParams.InverseOpsinMatrix[(j * 3) + i] = inverseMatrix[j, i] * (255.0f / OrigIntensityTarget);
                }
            }

            AllDefaultOpsin = Math.Abs(OrigIntensityTarget - 255.0) <= 0.1f && inverseMatrixIsDefault;
        }

        InverseGamma = desired.Tf.HaveGamma ? (float)LjCe.Gamma(desired) : LjCe.TfIsDci(desired) ? 1.0f / 2.6f : 1.0f;
    }
}

/// <summary>HlgOOTF (tone_mapping-inl.h).</summary>
internal sealed class LjHlgOotf
{
    private readonly float exponent, redY, greenY, blueY;
    private readonly bool apply;

    public LjHlgOotf(float gamma, float[] luminances)
    {
        exponent = gamma - 1;
        apply = exponent < -0.01f || 0.01f < exponent;
        redY = luminances[0];
        greenY = luminances[1];
        blueY = luminances[2];
    }

    public static LjHlgOotf ToSceneLight(float displayLuminance, float[] lum) =>
        new((1 / 1.2f) * LjDec.PowF(1.111f, -LjDec.Log2F(displayLuminance / 1000.0f)), lum);

    public void Apply(ref float r, ref float g, ref float b)
    {
        if (!apply)
        {
            return;
        }

        float luminance = MathF.FusedMultiplyAdd(redY, r, MathF.FusedMultiplyAdd(greenY, g, blueY * b));
        float ratio = MathF.Min(LjMath.FastPowf(luminance, exponent, true), 1e9f);
        r *= ratio;
        g *= ratio;
        b *= ratio;
    }
}

/// <summary>Transfer functions of the high-precision build (EncodedFromDisplay).</summary>
internal static class LjTf
{
    private static float EvalRational(float x, float[] p, float[] q)
    {
        int degP = p.Length - 1, degQ = q.Length - 1;
        float yp = p[degP], yq = q[degQ];
        for (int k = 1; k <= Math.Max(degP, degQ); k++)
        {
            if (degP >= k)
            {
                yp = MathF.FusedMultiplyAdd(yp, x, p[degP - k]);
            }

            if (degQ >= k)
            {
                yq = MathF.FusedMultiplyAdd(yq, x, q[degQ - k]);
            }
        }

        return yp / yq;
    }

    private static readonly float[] SrgbP = [-5.135152395e-04f, 5.287254571e-03f, 3.903842876e-01f, 1.474205315e+00f, 7.352629620e-01f];
    private static readonly float[] SrgbQ = [1.004519624e-02f, 3.036675394e-01f, 1.340816930e+00f, 9.258482155e-01f, 2.424867759e-02f];

    public static float SrgbFromLinear(float v)
    {
        float x = MathF.Abs(v);
        float linear = x * 12.92f;
        float poly = EvalRational(MathF.Sqrt(x), SrgbP, SrgbQ);
        float magnitude = x > 0.0031308f ? poly : linear;
        return BitConverter.SingleToInt32Bits(v) < 0 ? -magnitude : magnitude;
    }

    private static readonly float[] PqP = [1.351392e-02f, -1.095778e+00f, 5.522776e+01f, 1.492516e+02f, 4.838434e+01f];
    private static readonly float[] PqQ = [1.012416e+00f, 2.016708e+01f, 9.263710e+01f, 1.120607e+02f, 2.590418e+01f];
    private static readonly float[] PqPlo = [9.863406e-06f, 3.881234e-01f, 1.352821e+02f, 6.889862e+04f, -2.864824e+05f];
    private static readonly float[] PqQlo = [3.371868e+01f, 1.477719e+03f, 1.608477e+04f, -4.389884e+04f, -2.072546e+05f];

    public static Func<float, float> Pq(float displayIntensityTarget)
    {
        float toNits = displayIntensityTarget * (1.0f / 10000.0f);
        return v =>
        {
            float x = MathF.Abs(v);
            float xto025 = MathF.Sqrt(MathF.Sqrt(x * toNits));
            float magnitude = x < 1e-4f ? EvalRational(xto025, PqPlo, PqQlo) : EvalRational(xto025, PqP, PqQ);
            return BitConverter.SingleToInt32Bits(v) < 0 ? -magnitude : magnitude;
        };
    }

    public static float HlgFromLinear(float v)
    {
        const double kA = 0.17883277, kB = 1 - (4 * kA), kC = 0.5599107295, kInv12 = 1.0 / 12.0;
        float x = MathF.Abs(v);
        float lo = MathF.Sqrt((float)3.0 * x);
        float hi = MathF.FusedMultiplyAdd((float)(kA * 0.6931471805599453f), LjMath.FastLog2f(MathF.FusedMultiplyAdd(12, x, (float)-kB), true), (float)kC);
        float magnitude = x <= (float)kInv12 ? lo : hi;
        return BitConverter.SingleToInt32Bits(v) < 0 ? -magnitude : magnitude;
    }

    public static float Rec709FromLinear(float x)
    {
        float low = (float)4.5 * x;
        float hi = MathF.FusedMultiplyAdd((float)1.099, LjMath.FastPowf(x, (float)0.45, true), (float)-0.099);
        return x <= (float)0.018 ? low : hi;
    }

    public static Func<float, float> Gamma(float inverseGamma) => v => v <= 1e-5f ? 0 : LjMath.FastPowf(v, inverseGamma, true);
}
