// skcms (libjxl third_party/skcms @ 96d9171c, BSD licence) as libjxl's default CMS uses it: ICC parsing, transfer
// function maths, curve approximation and skcms_Transform. djxl builds skcms for its baseline target on Windows
// (SKCMS_DISABLE_HSW/SKX: CMake's processor name is AMD64, not x86_64), i.e. 4-lane SSE2 without FMA; every lane
// is independent, so the transform here runs the same stages one op at a time over whole rows.
using System;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Jxl.Lj;

/// <summary>skcms_TransferFunction: g, a, b, c, d, e, f.</summary>
internal struct SkTf
{
    public float G, A, B, C, D, E, F;

    public SkTf(float g, float a, float b, float c, float d, float e, float f)
    {
        G = g;
        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
    }

    public readonly bool BitEquals(in SkTf o) =>
        Bits(G) == Bits(o.G) && Bits(A) == Bits(o.A) && Bits(B) == Bits(o.B) && Bits(C) == Bits(o.C) &&
        Bits(D) == Bits(o.D) && Bits(E) == Bits(o.E) && Bits(F) == Bits(o.F);

    private static int Bits(float v) => BitConverter.SingleToInt32Bits(v);
}

/// <summary>skcms_Curve: a parametric curve (TableEntries == 0) or an 8/16-bit table inside the profile buffer.</summary>
internal struct SkCurve
{
    public uint TableEntries;
    public SkTf Parametric;
    public byte[]? Buf;
    public int Table8, Table16;   // offsets into Buf, or -1
}

internal sealed class SkA2B
{
    public SkCurve[] InputCurves = new SkCurve[4];
    public byte[]? Buf;
    public int Grid8 = -1, Grid16 = -1;
    public uint InputChannels;
    public byte[] GridPoints = new byte[4];
    public SkCurve[] MatrixCurves = new SkCurve[3];
    public float[] Matrix = new float[12];   // 3x4, row-major
    public uint MatrixChannels;
    public uint OutputChannels;
    public SkCurve[] OutputCurves = new SkCurve[3];
}

internal sealed class SkB2A
{
    public SkCurve[] InputCurves = new SkCurve[3];
    public uint InputChannels;
    public uint MatrixChannels;
    public SkCurve[] MatrixCurves = new SkCurve[3];
    public float[] Matrix = new float[12];
    public SkCurve[] OutputCurves = new SkCurve[4];
    public byte[]? Buf;
    public int Grid8 = -1, Grid16 = -1;
    public byte[] GridPoints = new byte[4];
    public uint OutputChannels;
}

/// <summary>skcms_ICCProfile.</summary>
internal sealed class SkProfile
{
    public byte[] Buffer = [];
    public uint Size, DataColorSpace, Pcs, TagCount;
    public SkCurve[] Trc = new SkCurve[3];
    public float[] ToXyzD50 = new float[9];   // 3x3, row-major
    public SkA2B A2B = new();
    public SkB2A B2A = new();
    public byte CicpPrimaries, CicpTransfer, CicpMatrix, CicpFullRange;
    public bool HasTrc, HasToXyzD50, HasA2B, HasB2A, HasCicp;

    /// <summary>A struct copy (the A2B/B2A tables are never modified after parsing, so they are shared).</summary>
    public SkProfile Clone()
    {
        var p = (SkProfile)MemberwiseClone();
        p.Trc = (SkCurve[])Trc.Clone();
        p.ToXyzD50 = (float[])ToXyzD50.Clone();
        return p;
    }
}

internal enum SkFmt
{
    Rgb888 = 8,
    Rgba8888 = 10,
    RgbHhh = 28,
    RgbFff = 32,
    RgbaFfff = 34,
}

internal enum SkAlpha
{
    Opaque,
    Unpremul,
    PremulAsEncoded,
}

internal static class LjSkcms
{
    public const uint SigAcsp = 0x61637370, SigRTrc = 0x72545243, SigGTrc = 0x67545243, SigBTrc = 0x62545243, SigKTrc = 0x6B545243;
    public const uint SigRXyz = 0x7258595A, SigGXyz = 0x6758595A, SigBXyz = 0x6258595A, SigA2B0 = 0x41324230, SigB2A0 = 0x42324130;
    public const uint SigChad = 0x63686164, SigWtpt = 0x77747074, SigCicp = 0x63696370, SigCurv = 0x63757276, SigMft1 = 0x6D667431;
    public const uint SigMft2 = 0x6D667432, SigMab = 0x6D414220, SigMba = 0x6D424120, SigPara = 0x70617261, SigSf32 = 0x73663332;
    public const uint SigCmyk = 0x434D594B, SigGray = 0x47524159, SigRgb = 0x52474220, SigLab = 0x4C616220, SigXyz = 0x58595A20;

    public const int TfInvalid = 0, TfSrgbish = 1, TfPqish = 2, TfHlgish = 3, TfHlginvish = 4, TfPq = 5, TfHlg = 6;

    // ---- scalar maths (skcms.cc) ----

    private static int F2I(float v) => BitConverter.SingleToInt32Bits(v);

    private static float I2F(int v) => BitConverter.Int32BitsToSingle(v);

    /// <summary>cvttss2si / cvttps2dq: truncation, 0x80000000 for NaN and out-of-range values.</summary>
    public static int Cvtt(float x) => x >= -2147483648f && x < 2147483648f ? (int)x : int.MinValue;

    private static float Log2f(float x)
    {
        int bits = F2I(x);
        float e = bits * (1.0f / (1 << 23));
        float m = I2F((bits & 0x007fffff) | 0x3f000000);
        return e - 124.225514990f - (1.498030302f * m) - (1.725879990f / (0.3520887068f + m));
    }

    private static float Logf(float x) => 0.69314718f * Log2f(x);

    public static float Floorf(float x)
    {
        float roundtrip = Cvtt(x);
        return roundtrip > x ? roundtrip - 1 : roundtrip;
    }

    private static float Exp2f(float x)
    {
        if (x > 128.0f)
        {
            return float.PositiveInfinity;
        }
        else if (x < -127.0f)
        {
            return 0.0f;
        }

        float fract = x - Floorf(x);
        float fbits = (1.0f * (1 << 23)) * (x + 121.274057500f - (1.490129070f * fract) + (27.728023300f / (4.84252568f - fract)));
        if (fbits >= (float)int.MaxValue)
        {
            return float.PositiveInfinity;
        }
        else if (fbits < 0)
        {
            return 0;
        }

        return I2F(Cvtt(fbits));
    }

    public static float Powf(float x, float y)
    {
        if (x <= 0.0f)
        {
            return 0.0f;
        }

        if (x == 1.0f)
        {
            return 1.0f;
        }

        return Exp2f(Log2f(x) * y);
    }

    private static float Expf(float x) => Exp2f(1.4426950408889634074f * x);

    private static float Fmaxf(float x, float y) => x > y ? x : y;

    private static float Fminf(float x, float y) => x < y ? x : y;

    private static float Fabsf(float x) => x < 0 ? -x : x;

    private static bool IsFinite(float x) => 0 == x * 0;

    private static float Minus1Ulp(float x) => I2F(F2I(x) - 1);

    private static float TfKindMarker(int kind) => -(float)kind;

    public static int Classify(in SkTf tf) => Classify(tf, out _);

    /// <summary>classify(); for the PQish/HLG(inv)ish kinds the six parameters are a..f.</summary>
    public static int Classify(in SkTf tf, out SkTf parts)
    {
        parts = tf;
        if (tf.G < 0)
        {
            if (tf.G < -128)
            {
                return TfInvalid;
            }

            int enumG = -(int)tf.G;
            if (-enumG != tf.G)
            {
                return TfInvalid;
            }

            switch (enumG)
            {
                case TfPqish:
                    return TfPqish;
                case TfHlgish:
                    return TfHlgish;
                case TfHlginvish:
                    return TfHlginvish;
                case TfPq:
                    if (tf.B != 0.0f || tf.C != 0.0f || tf.D != 0.0f || tf.E != 0.0f || tf.F != 0.0f)
                    {
                        return TfInvalid;
                    }

                    return TfPq;
                case TfHlg:
                    if (tf.D != 0.0f || tf.E != 0.0f || tf.F != 0.0f)
                    {
                        return TfInvalid;
                    }

                    return TfHlg;
            }

            return TfInvalid;
        }

        if (IsFinite(tf.A + tf.B + tf.C + tf.D + tf.E + tf.F + tf.G) && tf.A >= 0 && tf.C >= 0 && tf.D >= 0 && tf.G >= 0 &&
            (tf.A * tf.D) + tf.B >= 0)
        {
            return TfSrgbish;
        }

        return TfInvalid;
    }

    public static SkTf MakePqish(float a, float b, float c, float d, float e, float f) => new(TfKindMarker(TfPqish), a, b, c, d, e, f);

    public static SkTf MakeScaledHlgish(float k, float r, float g, float a, float b, float c) =>
        new(TfKindMarker(TfHlgish), r, g, a, b, c, k - 1.0f);

    public static SkTf MakeHlgish(float r, float g, float a, float b, float c) => MakeScaledHlgish(1.0f, r, g, a, b, c);

    public static float TfEval(in SkTf tf, float x)
    {
        float sign = x < 0 ? -1.0f : 1.0f;
        x *= sign;
        switch (Classify(tf))
        {
            case TfInvalid:
                break;
            case TfHlg:
            {
                const float a = 0.17883277f, b = 0.28466892f, c = 0.55991073f;
                return sign * (x <= 0.5f ? x * x / 3.0f : (Expf((x - c) / a) + b) / 12.0f);
            }

            case TfHlgish:
            {
                // TF_HLGish {R, G, a, b, c, K_minus_1} = tf.{A, B, C, D, E, F}
                float k = tf.F + 1.0f;
                return k * sign * (x * tf.A <= 1 ? Powf(x * tf.A, tf.B) : Expf((x - tf.E) * tf.C) + tf.D);
            }

            case TfHlginvish:
            {
                float k = tf.F + 1.0f;
                x /= k;
                return sign * (x <= 1 ? tf.A * Powf(x, tf.B) : (tf.C * Logf(x - tf.D)) + tf.E);
            }

            case TfSrgbish:
                return sign * (x < tf.D ? (tf.C * x) + tf.F : Powf((tf.A * x) + tf.B, tf.G) + tf.E);
            case TfPq:
            {
                const float c1 = 107 / 128.0f, c2 = 2413 / 128.0f, c3 = 2392 / 128.0f, m1 = 1305 / 8192.0f, m2 = 2523 / 32.0f;
                float p = Powf(x, 1.0f / m2);
                return Powf((p - c1) / (c2 - (c3 * p)), 1.0f / m1);
            }

            case TfPqish:
                // TF_PQish {A, B, C, D, E, F} = tf.{A..F}
                return sign * Powf((tf.A + (tf.B * Powf(x, tf.C))) / (tf.D + (tf.E * Powf(x, tf.C))), tf.F);
        }

        return 0;
    }

    private static float EvalCurve(in SkCurve curve, float x)
    {
        if (curve.TableEntries == 0)
        {
            return TfEval(curve.Parametric, x);
        }

        float ix = Fmaxf(0, Fminf(x, 1)) * (float)(curve.TableEntries - 1);
        int lo = Cvtt(ix), hi = Cvtt(Minus1Ulp(ix + 1.0f));
        float t = ix - lo;
        float l, h;
        byte[] buf = curve.Buf!;
        if (curve.Table8 >= 0)
        {
            l = buf[curve.Table8 + lo] * (1 / 255.0f);
            h = buf[curve.Table8 + hi] * (1 / 255.0f);
        }
        else
        {
            l = ((buf[curve.Table16 + (2 * lo)] << 8) | buf[curve.Table16 + (2 * lo) + 1]) * (1 / 65535.0f);
            h = ((buf[curve.Table16 + (2 * hi)] << 8) | buf[curve.Table16 + (2 * hi) + 1]) * (1 / 65535.0f);
        }

        return l + ((h - l) * t);
    }

    public static float MaxRoundtripError(in SkCurve curve, in SkTf invTf)
    {
        uint n = curve.TableEntries > 256 ? curve.TableEntries : 256;
        float dx = 1.0f / (float)(n - 1);
        float err = 0;
        for (uint i = 0; i < n; i++)
        {
            float x = (float)i * dx, y = EvalCurve(curve, x);
            err = Fmaxf(err, Fabsf(x - TfEval(invTf, y)));
        }

        return err;
    }

    public static bool AreApproximateInverses(in SkCurve curve, in SkTf invTf) => MaxRoundtripError(curve, invTf) < (1 / 512.0f);

    // ---- parsing ----

    private static ushort ReadBigU16(byte[] b, long p) => (ushort)((b[p] << 8) | b[p + 1]);

    private static uint ReadBigU32(byte[] b, long p) => ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];

    private static float ReadBigFixed(byte[] b, long p) => (float)(int)ReadBigU32(b, p) * (1.0f / 65536.0f);

    private readonly struct Tag
    {
        public readonly uint Signature, Type, Size;
        public readonly long Buf;

        public Tag(uint signature, uint type, uint size, long buf)
        {
            Signature = signature;
            Type = type;
            Size = size;
            Buf = buf;
        }
    }

    private static bool GetTagBySignature(SkProfile profile, uint sig, out Tag tag)
    {
        byte[] b = profile.Buffer;
        for (uint i = 0; i < profile.TagCount; i++)
        {
            long t = 132 + (12L * i);
            if (ReadBigU32(b, t) == sig)
            {
                uint size = ReadBigU32(b, t + 8);
                long buf = ReadBigU32(b, t + 4);
                tag = new Tag(sig, ReadBigU32(b, buf), size, buf);
                return true;
            }
        }

        tag = default;
        return false;
    }

    public static bool GetChad(SkProfile profile, out float[] m)
    {
        m = new float[9];
        if (!GetTagBySignature(profile, SigChad, out Tag tag))
        {
            return false;
        }

        if (tag.Type != SigSf32 || tag.Size < 44)
        {
            return false;
        }

        for (int i = 0; i < 9; i++)
        {
            m[i] = ReadBigFixed(profile.Buffer, tag.Buf + 8 + (4 * i));
        }

        return true;
    }

    private static bool ReadTagXyz(byte[] b, in Tag tag, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (tag.Type != SigXyz || tag.Size < 20)
        {
            return false;
        }

        x = ReadBigFixed(b, tag.Buf + 8);
        y = ReadBigFixed(b, tag.Buf + 12);
        z = ReadBigFixed(b, tag.Buf + 16);
        return true;
    }

    public static bool GetWtpt(SkProfile profile, float[] xyz) =>
        GetTagBySignature(profile, SigWtpt, out Tag tag) && ReadTagXyz(profile.Buffer, tag, out xyz[0], out xyz[1], out xyz[2]);

    private static bool ReadToXyzD50(byte[] b, in Tag r, in Tag g, in Tag bl, float[] m) =>
        ReadTagXyz(b, r, out m[0], out m[3], out m[6]) && ReadTagXyz(b, g, out m[1], out m[4], out m[7]) &&
        ReadTagXyz(b, bl, out m[2], out m[5], out m[8]);

    private static bool ReadCurvePara(byte[] b, long buf, ulong size, ref SkCurve curve, out uint curveSize)
    {
        curveSize = 0;
        if (size < 12)
        {
            return false;
        }

        ushort functionType = ReadBigU16(b, buf + 8);
        if (functionType > 4)
        {
            return false;
        }

        uint[] curveBytes = [4, 12, 16, 20, 28];
        if (size < 12 + curveBytes[functionType])
        {
            return false;
        }

        curveSize = 12 + curveBytes[functionType];
        long v = buf + 12;
        curve.TableEntries = 0;
        curve.Parametric = new SkTf(ReadBigFixed(b, v), 1.0f, 0, 0, 0, 0, 0);
        ref SkTf p = ref curve.Parametric;
        switch (functionType)
        {
            case 1:
                p.A = ReadBigFixed(b, v + 4);
                p.B = ReadBigFixed(b, v + 8);
                if (p.A == 0)
                {
                    return false;
                }

                p.D = -p.B / p.A;
                break;
            case 2:
                p.A = ReadBigFixed(b, v + 4);
                p.B = ReadBigFixed(b, v + 8);
                p.E = ReadBigFixed(b, v + 12);
                if (p.A == 0)
                {
                    return false;
                }

                p.D = -p.B / p.A;
                p.F = p.E;
                break;
            case 3:
                p.A = ReadBigFixed(b, v + 4);
                p.B = ReadBigFixed(b, v + 8);
                p.C = ReadBigFixed(b, v + 12);
                p.D = ReadBigFixed(b, v + 16);
                break;
            case 4:
                p.A = ReadBigFixed(b, v + 4);
                p.B = ReadBigFixed(b, v + 8);
                p.C = ReadBigFixed(b, v + 12);
                p.D = ReadBigFixed(b, v + 16);
                p.E = ReadBigFixed(b, v + 20);
                p.F = ReadBigFixed(b, v + 24);
                break;
        }

        return Classify(curve.Parametric) == TfSrgbish;
    }

    private static bool ReadCurveCurv(byte[] b, long buf, ulong size, ref SkCurve curve, out uint curveSize)
    {
        curveSize = 0;
        if (size < 12)
        {
            return false;
        }

        uint valueCount = ReadBigU32(b, buf + 8);
        if (size < 12 + ((ulong)valueCount * 2))
        {
            return false;
        }

        curveSize = (uint)(12 + ((ulong)valueCount * 2));
        if (valueCount < 2)
        {
            curve.TableEntries = 0;
            curve.Parametric = new SkTf(valueCount == 0 ? 1.0f : ReadBigU16(b, buf + 12) * (1.0f / 256.0f), 1.0f, 0, 0, 0, 0, 0);
        }
        else
        {
            curve.Buf = b;
            curve.Table8 = -1;
            curve.Table16 = (int)(buf + 12);
            curve.TableEntries = valueCount;
        }

        return true;
    }

    private static bool ReadCurve(byte[] b, long buf, ulong size, ref SkCurve curve, out uint curveSize)
    {
        curveSize = 0;
        if (size < 4)
        {
            return false;
        }

        uint type = ReadBigU32(b, buf);
        if (type == SigPara)
        {
            return ReadCurvePara(b, buf, size, ref curve, out curveSize);
        }
        else if (type == SigCurv)
        {
            return ReadCurveCurv(b, buf, size, ref curve, out curveSize);
        }

        return false;
    }

    private static bool ReadMftCommon(byte[] b, in Tag tag, SkA2B a2b)
    {
        a2b.MatrixChannels = 0;
        a2b.InputChannels = b[tag.Buf + 8];
        a2b.OutputChannels = b[tag.Buf + 9];
        if (a2b.OutputChannels != 3)
        {
            return false;
        }

        if (a2b.InputChannels < 1 || a2b.InputChannels > 4)
        {
            return false;
        }

        for (int i = 0; i < a2b.InputChannels; i++)
        {
            a2b.GridPoints[i] = b[tag.Buf + 10];
        }

        return a2b.GridPoints[0] >= 2;
    }

    private static bool ReadMftCommon(byte[] b, in Tag tag, SkB2A b2a)
    {
        b2a.MatrixChannels = 0;
        b2a.InputChannels = b[tag.Buf + 8];
        b2a.OutputChannels = b[tag.Buf + 9];
        if (b2a.InputChannels != 3)
        {
            return false;
        }

        if (b2a.OutputChannels < 3 || b2a.OutputChannels > 4)
        {
            return false;
        }

        for (int i = 0; i < b2a.InputChannels; i++)
        {
            b2a.GridPoints[i] = b[tag.Buf + 10];
        }

        return b2a.GridPoints[0] >= 2;
    }

    private static bool InitTables(byte[] b, long tableBase, ulong maxTablesLen, uint byteWidth, uint inputEntries, uint outputEntries,
        uint inputChannels, uint outputChannels, byte[] gridPoints, SkCurve[] inputCurves, SkCurve[] outputCurves, out int grid8, out int grid16)
    {
        grid8 = grid16 = -1;
        uint perInput = inputEntries * byteWidth, perOutput = outputEntries * byteWidth;
        uint allInput = inputChannels * perInput, allOutput = outputChannels * perOutput;
        ulong gridSize = outputChannels * byteWidth;
        for (int axis = 0; axis < inputChannels; axis++)
        {
            gridSize *= gridPoints[axis];
        }

        if (maxTablesLen < allInput + gridSize + allOutput)
        {
            return false;
        }

        for (int i = 0; i < inputChannels; i++)
        {
            inputCurves[i].TableEntries = inputEntries;
            inputCurves[i].Buf = b;
            inputCurves[i].Table8 = byteWidth == 1 ? (int)(tableBase + (i * perInput)) : -1;
            inputCurves[i].Table16 = byteWidth == 1 ? -1 : (int)(tableBase + (i * perInput));
        }

        if (byteWidth == 1)
        {
            grid8 = (int)(tableBase + allInput);
        }
        else
        {
            grid16 = (int)(tableBase + allInput);
        }

        long outputBase = tableBase + allInput + (long)gridSize;
        for (int i = 0; i < outputChannels; i++)
        {
            outputCurves[i].TableEntries = outputEntries;
            outputCurves[i].Buf = b;
            outputCurves[i].Table8 = byteWidth == 1 ? (int)(outputBase + (i * perOutput)) : -1;
            outputCurves[i].Table16 = byteWidth == 1 ? -1 : (int)(outputBase + (i * perOutput));
        }

        return true;
    }

    private static bool ReadTagMft(byte[] b, in Tag tag, SkA2B? a2b, SkB2A? b2a, bool mft2)
    {
        ulong fixedSize = mft2 ? 52UL : 48UL;
        if (tag.Size < fixedSize)
        {
            return false;
        }

        if (!(a2b != null ? ReadMftCommon(b, tag, a2b) : ReadMftCommon(b, tag, b2a!)))
        {
            return false;
        }

        uint inputEntries = 256, outputEntries = 256;
        if (mft2)
        {
            inputEntries = ReadBigU16(b, tag.Buf + 48);
            outputEntries = ReadBigU16(b, tag.Buf + 50);
            if (inputEntries < 2 || inputEntries > 4096 || outputEntries < 2 || outputEntries > 4096)
            {
                return false;
            }
        }

        bool ok;
        int g8, g16;
        if (a2b != null)
        {
            ok = InitTables(b, tag.Buf + (long)fixedSize, tag.Size - fixedSize, mft2 ? 2u : 1u, inputEntries, outputEntries, a2b.InputChannels,
                a2b.OutputChannels, a2b.GridPoints, a2b.InputCurves, a2b.OutputCurves, out g8, out g16);
            a2b.Buf = b;
            a2b.Grid8 = g8;
            a2b.Grid16 = g16;
        }
        else
        {
            ok = InitTables(b, tag.Buf + (long)fixedSize, tag.Size - fixedSize, mft2 ? 2u : 1u, inputEntries, outputEntries, b2a!.InputChannels,
                b2a.OutputChannels, b2a.GridPoints, b2a.InputCurves, b2a.OutputCurves, out g8, out g16);
            b2a.Buf = b;
            b2a.Grid8 = g8;
            b2a.Grid16 = g16;
        }

        return ok;
    }

    private static bool ReadCurves(byte[] b, long tagBuf, uint size, uint curveOffset, uint numCurves, SkCurve[] curves)
    {
        for (int i = 0; i < numCurves; i++)
        {
            if (curveOffset > size)
            {
                return false;
            }

            if (!ReadCurve(b, tagBuf + curveOffset, size - curveOffset, ref curves[i], out uint curveBytes))
            {
                return false;
            }

            if (curveBytes > uint.MaxValue - 3)
            {
                return false;
            }

            curveBytes = (curveBytes + 3) & ~3U;
            ulong newOffset = (ulong)curveOffset + curveBytes;
            curveOffset = (uint)newOffset;
            if (newOffset != curveOffset)
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadMatrix(byte[] b, long mtx, float factor, float[] m)
    {
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[(r * 4) + c] = factor * ReadBigFixed(b, mtx + (((r * 3) + c) * 4));
            }

            m[(r * 4) + 3] = factor * ReadBigFixed(b, mtx + 36 + (r * 4));
        }
    }

    private static bool ReadClut(byte[] b, in Tag tag, uint clutOffset, uint inputChannels, uint outputChannels, byte[] gridPoints,
        out int grid8, out int grid16)
    {
        grid8 = grid16 = -1;
        if (tag.Size < clutOffset + 20UL)
        {
            return false;
        }

        long clut = tag.Buf + clutOffset;
        byte width = b[clut + 16];
        if (width == 1)
        {
            grid8 = (int)(clut + 20);
        }
        else if (width == 2)
        {
            grid16 = (int)(clut + 20);
        }
        else
        {
            return false;
        }

        ulong gridSize = outputChannels * (ulong)width;
        for (int i = 0; i < inputChannels; i++)
        {
            gridPoints[i] = b[clut + i];
            if (gridPoints[i] < 2)
            {
                return false;
            }

            gridSize *= gridPoints[i];
        }

        return tag.Size >= clutOffset + 20UL + gridSize;
    }

    private static bool ReadTagMab(byte[] b, in Tag tag, SkA2B a2b, bool pcsIsXyz)
    {
        if (tag.Size < 32)
        {
            return false;
        }

        a2b.InputChannels = b[tag.Buf + 8];
        a2b.OutputChannels = b[tag.Buf + 9];
        if (a2b.OutputChannels != 3 || a2b.InputChannels > 4)
        {
            return false;
        }

        uint bCurveOffset = ReadBigU32(b, tag.Buf + 12), matrixOffset = ReadBigU32(b, tag.Buf + 16);
        uint mCurveOffset = ReadBigU32(b, tag.Buf + 20), clutOffset = ReadBigU32(b, tag.Buf + 24), aCurveOffset = ReadBigU32(b, tag.Buf + 28);
        if (bCurveOffset == 0 || !ReadCurves(b, tag.Buf, tag.Size, bCurveOffset, a2b.OutputChannels, a2b.OutputCurves))
        {
            return false;
        }

        if (mCurveOffset != 0)
        {
            if (matrixOffset == 0)
            {
                return false;
            }

            a2b.MatrixChannels = a2b.OutputChannels;
            if (!ReadCurves(b, tag.Buf, tag.Size, mCurveOffset, a2b.MatrixChannels, a2b.MatrixCurves))
            {
                return false;
            }

            if (tag.Size < matrixOffset + 48UL)
            {
                return false;
            }

            ReadMatrix(b, tag.Buf + matrixOffset, pcsIsXyz ? 65535 / 32768.0f : 1.0f, a2b.Matrix);
        }
        else
        {
            if (matrixOffset != 0)
            {
                return false;
            }

            a2b.MatrixChannels = 0;
        }

        if (aCurveOffset != 0)
        {
            if (clutOffset == 0)
            {
                return false;
            }

            if (!ReadCurves(b, tag.Buf, tag.Size, aCurveOffset, a2b.InputChannels, a2b.InputCurves))
            {
                return false;
            }

            if (!ReadClut(b, tag, clutOffset, a2b.InputChannels, a2b.OutputChannels, a2b.GridPoints, out int g8, out int g16))
            {
                return false;
            }

            a2b.Buf = b;
            a2b.Grid8 = g8;
            a2b.Grid16 = g16;
        }
        else
        {
            if (clutOffset != 0 || a2b.InputChannels != a2b.OutputChannels)
            {
                return false;
            }

            a2b.InputChannels = 0;
        }

        return true;
    }

    private static bool ReadTagMba(byte[] b, in Tag tag, SkB2A b2a, bool pcsIsXyz)
    {
        if (tag.Size < 32)
        {
            return false;
        }

        b2a.InputChannels = b[tag.Buf + 8];
        b2a.OutputChannels = b[tag.Buf + 9];
        if (b2a.InputChannels != 3 || b2a.OutputChannels < 3 || b2a.OutputChannels > 4)
        {
            return false;
        }

        uint bCurveOffset = ReadBigU32(b, tag.Buf + 12), matrixOffset = ReadBigU32(b, tag.Buf + 16);
        uint mCurveOffset = ReadBigU32(b, tag.Buf + 20), clutOffset = ReadBigU32(b, tag.Buf + 24), aCurveOffset = ReadBigU32(b, tag.Buf + 28);
        if (bCurveOffset == 0 || !ReadCurves(b, tag.Buf, tag.Size, bCurveOffset, b2a.InputChannels, b2a.InputCurves))
        {
            return false;
        }

        if (mCurveOffset != 0)
        {
            if (matrixOffset == 0)
            {
                return false;
            }

            b2a.MatrixChannels = b2a.InputChannels;
            if (!ReadCurves(b, tag.Buf, tag.Size, mCurveOffset, b2a.MatrixChannels, b2a.MatrixCurves))
            {
                return false;
            }

            if (tag.Size < matrixOffset + 48UL)
            {
                return false;
            }

            ReadMatrix(b, tag.Buf + matrixOffset, pcsIsXyz ? 32768 / 65535.0f : 1.0f, b2a.Matrix);
        }
        else
        {
            if (matrixOffset != 0)
            {
                return false;
            }

            b2a.MatrixChannels = 0;
        }

        if (aCurveOffset != 0)
        {
            if (clutOffset == 0)
            {
                return false;
            }

            if (!ReadCurves(b, tag.Buf, tag.Size, aCurveOffset, b2a.OutputChannels, b2a.OutputCurves))
            {
                return false;
            }

            if (!ReadClut(b, tag, clutOffset, b2a.InputChannels, b2a.OutputChannels, b2a.GridPoints, out int g8, out int g16))
            {
                return false;
            }

            b2a.Buf = b;
            b2a.Grid8 = g8;
            b2a.Grid16 = g16;
        }
        else
        {
            if (clutOffset != 0 || b2a.InputChannels != b2a.OutputChannels)
            {
                return false;
            }

            b2a.OutputChannels = 0;
        }

        return true;
    }

    private static int FitLinear(in SkCurve curve, int n, float tol, out float c, out float d, bool withF, out float f)
    {
        float dx = 1.0f / (float)(n - 1);
        int linPoints = 1;
        c = 0;
        f = withF ? EvalCurve(curve, 0) : 0.0f;
        float sloveMin = float.NegativeInfinity, slopeMax = float.PositiveInfinity;
        for (int i = 1; i < n; i++)
        {
            float x = (float)i * dx;
            float y = EvalCurve(curve, x);
            float slopeMaxI = (y + tol - f) / x, slopeMinI = (y - tol - f) / x;
            if (slopeMaxI < sloveMin || slopeMax < slopeMinI)
            {
                break;
            }

            slopeMax = Fminf(slopeMax, slopeMaxI);
            sloveMin = Fmaxf(sloveMin, slopeMinI);
            float curSlope = (y - f) / x;
            if (sloveMin <= curSlope && curSlope <= slopeMax)
            {
                linPoints = i + 1;
                c = curSlope;
            }
        }

        d = (float)(linPoints - 1) * dx;
        return linPoints;
    }

    private static void CanonicalizeIdentity(ref SkCurve curve)
    {
        if (curve.TableEntries != 0 && curve.TableEntries <= int.MaxValue)
        {
            int n = (int)curve.TableEntries;
            if (n == FitLinear(curve, n, 1.0f / (float)(2 * n), out float c, out _, true, out float f) && c == 1.0f && f == 0.0f)
            {
                curve.TableEntries = 0;
                curve.Table8 = curve.Table16 = -1;
                curve.Buf = null;
                curve.Parametric = new SkTf(1, 1, 0, 0, 0, 0, 0);
            }
        }
    }

    private static bool ReadA2B(byte[] b, in Tag tag, SkA2B a2b, bool pcsIsXyz)
    {
        bool ok = false;
        if (tag.Type == SigMft1)
        {
            ok = ReadTagMft(b, tag, a2b, null, false);
        }

        if (tag.Type == SigMft2)
        {
            ok = ReadTagMft(b, tag, a2b, null, true);
        }

        if (tag.Type == SigMab)
        {
            ok = ReadTagMab(b, tag, a2b, pcsIsXyz);
        }

        if (!ok)
        {
            return false;
        }

        for (int i = 0; i < Math.Min(a2b.InputChannels, 4); i++)
        {
            CanonicalizeIdentity(ref a2b.InputCurves[i]);
        }

        for (int i = 0; i < Math.Min(a2b.MatrixChannels, 3); i++)
        {
            CanonicalizeIdentity(ref a2b.MatrixCurves[i]);
        }

        for (int i = 0; i < Math.Min(a2b.OutputChannels, 3); i++)
        {
            CanonicalizeIdentity(ref a2b.OutputCurves[i]);
        }

        return true;
    }

    private static bool ReadB2A(byte[] b, in Tag tag, SkB2A b2a, bool pcsIsXyz)
    {
        bool ok = false;
        if (tag.Type == SigMft1)
        {
            ok = ReadTagMft(b, tag, null, b2a, false);
        }

        if (tag.Type == SigMft2)
        {
            ok = ReadTagMft(b, tag, null, b2a, true);
        }

        if (tag.Type == SigMba)
        {
            ok = ReadTagMba(b, tag, b2a, pcsIsXyz);
        }

        if (!ok)
        {
            return false;
        }

        for (int i = 0; i < Math.Min(b2a.InputChannels, 3); i++)
        {
            CanonicalizeIdentity(ref b2a.InputCurves[i]);
        }

        for (int i = 0; i < Math.Min(b2a.MatrixChannels, 3); i++)
        {
            CanonicalizeIdentity(ref b2a.MatrixCurves[i]);
        }

        for (int i = 0; i < Math.Min(b2a.OutputChannels, 4); i++)
        {
            CanonicalizeIdentity(ref b2a.OutputCurves[i]);
        }

        return true;
    }

    /// <summary>skcms_Parse (A2B priority {0, 1}).</summary>
    public static bool Parse(byte[] buf, out SkProfile profile) => ParseWithA2BPriority(buf, buf.Length, [0, 1], out profile);

    public static bool ParseWithA2BPriority(byte[] buf, long len, int[] priority, out SkProfile profile)
    {
        profile = new SkProfile();
        if (len < 132)
        {
            return false;
        }

        profile.Buffer = buf;
        profile.Size = ReadBigU32(buf, 0);
        uint version = ReadBigU32(buf, 8);
        profile.DataColorSpace = ReadBigU32(buf, 16);
        profile.Pcs = ReadBigU32(buf, 20);
        uint signature = ReadBigU32(buf, 36);
        float illuminantX = ReadBigFixed(buf, 68), illuminantY = ReadBigFixed(buf, 72), illuminantZ = ReadBigFixed(buf, 76);
        profile.TagCount = ReadBigU32(buf, 128);
        ulong tagTableSize = profile.TagCount * 12UL;
        if (signature != SigAcsp || profile.Size > len || profile.Size < 132 + tagTableSize || (version >> 24) > 4)
        {
            return false;
        }

        if (Fabsf(illuminantX - 0.9642f) > 0.0100f || Fabsf(illuminantY - 1.0000f) > 0.0100f || Fabsf(illuminantZ - 0.8249f) > 0.0100f)
        {
            return false;
        }

        for (uint i = 0; i < profile.TagCount; i++)
        {
            long t = 132 + (12L * i);
            uint tagOffset = ReadBigU32(buf, t + 4), tagSize = ReadBigU32(buf, t + 8);
            ulong tagEnd = (ulong)tagOffset + tagSize;
            if (tagSize < 4 || tagEnd > profile.Size)
            {
                return false;
            }
        }

        if (profile.Pcs != SigXyz && profile.Pcs != SigLab)
        {
            return false;
        }

        bool pcsIsXyz = profile.Pcs == SigXyz;
        if (profile.DataColorSpace == SigGray && GetTagBySignature(profile, SigKTrc, out Tag kTrc))
        {
            if (!ReadCurve(buf, kTrc.Buf, kTrc.Size, ref profile.Trc[0], out _))
            {
                return false;
            }

            profile.Trc[1] = profile.Trc[0];
            profile.Trc[2] = profile.Trc[0];
            profile.HasTrc = true;
            if (pcsIsXyz)
            {
                profile.ToXyzD50[0] = illuminantX;
                profile.ToXyzD50[4] = illuminantY;
                profile.ToXyzD50[8] = illuminantZ;
                profile.HasToXyzD50 = true;
            }
        }
        else
        {
            if (GetTagBySignature(profile, SigRTrc, out Tag rTrc) && GetTagBySignature(profile, SigGTrc, out Tag gTrc) &&
                GetTagBySignature(profile, SigBTrc, out Tag bTrc))
            {
                if (!ReadCurve(buf, rTrc.Buf, rTrc.Size, ref profile.Trc[0], out _) || !ReadCurve(buf, gTrc.Buf, gTrc.Size, ref profile.Trc[1], out _) ||
                    !ReadCurve(buf, bTrc.Buf, bTrc.Size, ref profile.Trc[2], out _))
                {
                    return false;
                }

                profile.HasTrc = true;
            }

            if (GetTagBySignature(profile, SigRXyz, out Tag rXyz) && GetTagBySignature(profile, SigGXyz, out Tag gXyz) &&
                GetTagBySignature(profile, SigBXyz, out Tag bXyz))
            {
                if (!ReadToXyzD50(buf, rXyz, gXyz, bXyz, profile.ToXyzD50))
                {
                    return false;
                }

                profile.HasToXyzD50 = true;
            }
        }

        foreach (int p in priority)
        {
            if (p < 0 || p > 2)
            {
                return false;
            }

            if (GetTagBySignature(profile, SigA2B0 + (uint)p, out Tag tag))
            {
                if (!ReadA2B(buf, tag, profile.A2B, pcsIsXyz))
                {
                    return false;
                }

                profile.HasA2B = true;
                break;
            }
        }

        foreach (int p in priority)
        {
            if (p < 0 || p > 2)
            {
                return false;
            }

            if (GetTagBySignature(profile, SigB2A0 + (uint)p, out Tag tag))
            {
                if (!ReadB2A(buf, tag, profile.B2A, pcsIsXyz))
                {
                    return false;
                }

                profile.HasB2A = true;
                break;
            }
        }

        if (GetTagBySignature(profile, SigCicp, out Tag cicp))
        {
            if (cicp.Type != SigCicp || cicp.Size < 12)
            {
                return false;
            }

            profile.CicpPrimaries = buf[cicp.Buf + 8];
            profile.CicpTransfer = buf[cicp.Buf + 9];
            profile.CicpMatrix = buf[cicp.Buf + 10];
            profile.CicpFullRange = buf[cicp.Buf + 11];
            profile.HasCicp = true;
        }

        return profile.HasA2B || (profile.HasTrc && profile.HasToXyzD50);
    }

    // ---- built-in profiles ----

    private static SkCurve ParamCurve(SkTf tf) => new() { Parametric = tf, Table8 = -1, Table16 = -1 };

    public static readonly SkTf SrgbTf = new(2.4f, (float)(1 / 1.055), (float)(0.055 / 1.055), (float)(1 / 12.92), 0.04045f, 0, 0);

    public static SkProfile SrgbProfile() => new()
    {
        DataColorSpace = SigRgb,
        Pcs = SigXyz,
        Trc = [ParamCurve(SrgbTf), ParamCurve(SrgbTf), ParamCurve(SrgbTf)],
        ToXyzD50 = [0.436065674f, 0.385147095f, 0.143066406f, 0.222488403f, 0.716873169f, 0.060607910f, 0.013916016f, 0.097076416f, 0.714096069f],
        HasTrc = true,
        HasToXyzD50 = true,
    };

    private static readonly SkTf IdentityTf = new(1, 1, 0, 0, 0, 0, 0);

    public static readonly SkProfile XyzD50Profile = new()
    {
        DataColorSpace = SigRgb,
        Pcs = SigXyz,
        Trc = [ParamCurve(IdentityTf), ParamCurve(IdentityTf), ParamCurve(IdentityTf)],
        ToXyzD50 = [1, 0, 0, 0, 1, 0, 0, 0, 1],
        HasTrc = true,
        HasToXyzD50 = true,
    };

    private static readonly byte[] Random252 =
    [
        8, 179, 128, 204, 253, 38, 134, 184, 68, 102, 32, 138, 99, 39, 169, 215, 119, 26, 3, 223, 95, 239, 52, 132, 114, 74, 81, 234, 97, 116, 244,
        205, 30, 154, 173, 12, 51, 159, 122, 153, 61, 226, 236, 178, 229, 55, 181, 220, 191, 194, 160, 126, 168, 82, 131, 18, 180, 245, 163, 22,
        246, 69, 235, 252, 57, 108, 14, 6, 152, 240, 255, 171, 242, 20, 227, 177, 238, 96, 85, 16, 211, 70, 200, 149, 155, 146, 127, 145, 100,
        151, 109, 19, 165, 208, 195, 164, 137, 254, 182, 248, 64, 201, 45, 209, 5, 147, 207, 210, 113, 162, 83, 225, 9, 31, 15, 231, 115, 37, 58,
        53, 24, 49, 197, 56, 120, 172, 48, 21, 214, 129, 111, 11, 50, 187, 196, 34, 60, 103, 71, 144, 47, 203, 77, 80, 232, 140, 222, 250, 206,
        166, 247, 139, 249, 221, 72, 106, 27, 199, 117, 54, 219, 135, 118, 40, 79, 41, 251, 46, 93, 212, 92, 233, 148, 28, 121, 63, 123, 158, 105,
        59, 29, 42, 143, 23, 0, 107, 176, 87, 104, 183, 156, 193, 189, 90, 188, 65, 190, 17, 198, 7, 186, 161, 1, 124, 78, 125, 170, 133, 174,
        218, 67, 157, 75, 101, 89, 217, 62, 33, 141, 228, 25, 35, 91, 230, 4, 2, 13, 73, 86, 167, 237, 84, 243, 44, 185, 66, 130, 110, 150, 142,
        216, 88, 112, 36, 224, 136, 202, 76, 94, 98, 175, 213,
    ];

    public static bool ApproximatelyEqualProfiles(SkProfile a, SkProfile b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if ((a.DataColorSpace == SigCmyk) != (b.DataColorSpace == SigCmyk))
        {
            return false;
        }

        SkFmt fmt = SkFmt.Rgb888;
        int npixels = 84;
        if (a.DataColorSpace == SigCmyk)
        {
            fmt = SkFmt.Rgba8888;
            npixels = 63;
        }

        var dstA = new byte[252];
        var dstB = new byte[252];
        if (!Transform(Random252, fmt, SkAlpha.Unpremul, a, dstA, SkFmt.Rgb888, SkAlpha.Unpremul, XyzD50Profile, npixels))
        {
            return false;
        }

        if (!Transform(Random252, fmt, SkAlpha.Unpremul, b, dstB, SkFmt.Rgb888, SkAlpha.Unpremul, XyzD50Profile, npixels))
        {
            return false;
        }

        for (int i = 0; i < 252; i++)
        {
            if (Math.Abs(dstA[i] - dstB[i]) > 1)
            {
                return false;
            }
        }

        return true;
    }

    // ---- matrices ----

    private static float[] MvMul(float[] m, float[] v)
    {
        var dst = new float[3];
        for (int row = 0; row < 3; row++)
        {
            dst[row] = (m[row * 3] * v[0]) + (m[(row * 3) + 1] * v[1]) + (m[(row * 3) + 2] * v[2]);
        }

        return dst;
    }

    public static bool Matrix3x3Invert(float[] src, float[] dst)
    {
        double a00 = src[0], a01 = src[3], a02 = src[6], a10 = src[1], a11 = src[4], a12 = src[7], a20 = src[2], a21 = src[5], a22 = src[8];
        double b0 = (a00 * a11) - (a01 * a10), b1 = (a00 * a12) - (a02 * a10), b2 = (a01 * a12) - (a02 * a11), b3 = a20, b4 = a21, b5 = a22;
        double determinant = (b0 * b5) - (b1 * b4) + (b2 * b3);
        if (determinant == 0)
        {
            return false;
        }

        double invdet = 1.0 / determinant;
        if (invdet > float.MaxValue || invdet < -float.MaxValue || !IsFinite((float)invdet))
        {
            return false;
        }

        b0 *= invdet;
        b1 *= invdet;
        b2 *= invdet;
        b3 *= invdet;
        b4 *= invdet;
        b5 *= invdet;
        dst[0] = (float)((a11 * b5) - (a12 * b4));
        dst[3] = (float)((a02 * b4) - (a01 * b5));
        dst[6] = (float)(+b2);
        dst[1] = (float)((a12 * b3) - (a10 * b5));
        dst[4] = (float)((a00 * b5) - (a02 * b3));
        dst[7] = (float)(-b1);
        dst[2] = (float)((a10 * b4) - (a11 * b3));
        dst[5] = (float)((a01 * b3) - (a00 * b4));
        dst[8] = (float)(+b0);
        foreach (float v in dst)
        {
            if (!IsFinite(v))
            {
                return false;
            }
        }

        return true;
    }

    public static float[] Matrix3x3Concat(float[] a, float[] b)
    {
        var m = new float[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[(r * 3) + c] = (a[r * 3] * b[c]) + (a[(r * 3) + 1] * b[3 + c]) + (a[(r * 3) + 2] * b[6 + c]);
            }
        }

        return m;
    }

    private static bool IsZeroToOne(float x) => 0 <= x && x <= 1;

    public static bool AdaptToXyzD50(float wx, float wy, out float[] toXyzD50)
    {
        toXyzD50 = new float[9];
        if (!IsZeroToOne(wx) || !IsZeroToOne(wy))
        {
            return false;
        }

        float[] wXyz = [wx / wy, 1, (1 - wx - wy) / wy];
        float[] wXyzD50 = [0.96422f, 1.0f, 0.82521f];
        float[] xyzToLms = [0.8951f, 0.2664f, -0.1614f, -0.7502f, 1.7135f, 0.0367f, 0.0389f, -0.0685f, 1.0296f];
        float[] lmsToXyz = [0.9869929f, -0.1470543f, 0.1599627f, 0.4323053f, 0.5183603f, 0.0492912f, -0.0085287f, 0.0400428f, 0.9684867f];
        float[] srcCone = MvMul(xyzToLms, wXyz), dstCone = MvMul(xyzToLms, wXyzD50);
        float[] m = [dstCone[0] / srcCone[0], 0, 0, 0, dstCone[1] / srcCone[1], 0, 0, 0, dstCone[2] / srcCone[2]];
        m = Matrix3x3Concat(m, xyzToLms);
        toXyzD50 = Matrix3x3Concat(lmsToXyz, m);
        return true;
    }

    public static bool PrimariesToXyzD50(float rx, float ry, float gx, float gy, float bx, float by, float wx, float wy, out float[] toXyzD50)
    {
        toXyzD50 = new float[9];
        if (!IsZeroToOne(rx) || !IsZeroToOne(ry) || !IsZeroToOne(gx) || !IsZeroToOne(gy) || !IsZeroToOne(bx) || !IsZeroToOne(by) ||
            !IsZeroToOne(wx) || !IsZeroToOne(wy))
        {
            return false;
        }

        float[] primaries = [rx, gx, bx, ry, gy, by, 1 - rx - ry, 1 - gx - gy, 1 - bx - by];
        var primariesInv = new float[9];
        if (!Matrix3x3Invert(primaries, primariesInv))
        {
            return false;
        }

        float[] wXyz = [wx / wy, 1, (1 - wx - wy) / wy];
        float[] xyz = MvMul(primariesInv, wXyz);
        float[] toXyz = [xyz[0], 0, 0, 0, xyz[1], 0, 0, 0, xyz[2]];
        toXyz = Matrix3x3Concat(primaries, toXyz);
        if (!AdaptToXyzD50(wx, wy, out float[] dxToD50))
        {
            return false;
        }

        toXyzD50 = Matrix3x3Concat(dxToD50, toXyz);
        return true;
    }

    // ---- transfer function inversion and curve fitting ----

    public static bool TfInvert(in SkTf src, out SkTf dst)
    {
        dst = default;
        int kind = Classify(src);
        switch (kind)
        {
            case TfInvalid:
            case TfPq:
            case TfHlg:
                return false;
            case TfSrgbish:
                break;
            case TfPqish:
                // {marker, -pq.A, pq.D, 1/pq.F, pq.B, -pq.E, 1/pq.C}
                dst = new SkTf(TfKindMarker(TfPqish), -src.A, src.D, 1.0f / src.F, src.B, -src.E, 1.0f / src.C);
                return true;
            case TfHlgish:
                dst = new SkTf(TfKindMarker(TfHlginvish), 1.0f / src.A, 1.0f / src.B, 1.0f / src.C, src.D, src.E, src.F);
                return true;
            case TfHlginvish:
                dst = new SkTf(TfKindMarker(TfHlgish), 1.0f / src.A, 1.0f / src.B, 1.0f / src.C, src.D, src.E, src.F);
                return true;
        }

        var inv = new SkTf(0, 0, 0, 0, 0, 0, 0);
        float dL = (src.C * src.D) + src.F, dR = Powf((src.A * src.D) + src.B, src.G) + src.E;
        if (Fabsf(dL - dR) > 1 / 512.0f)
        {
            return false;
        }

        inv.D = dL;
        if (inv.D > 0)
        {
            inv.C = 1.0f / src.C;
            inv.F = -src.F / src.C;
        }

        float k = Powf(src.A, -src.G);
        inv.G = 1.0f / src.G;
        inv.A = k;
        inv.B = -k * src.E;
        inv.E = -src.B / src.A;
        if (inv.A < 0)
        {
            return false;
        }

        if ((inv.A * inv.D) + inv.B < 0)
        {
            inv.B = -inv.A * inv.D;
        }

        if (Classify(inv) != TfSrgbish)
        {
            return false;
        }

        float s = TfEval(src, 1.0f);
        if (!IsFinite(s))
        {
            return false;
        }

        float sign = s < 0 ? -1.0f : 1.0f;
        s *= sign;
        if (s < inv.D)
        {
            inv.F = 1.0f - (sign * inv.C * s);
        }
        else
        {
            inv.E = 1.0f - (sign * Powf((inv.A * s) + inv.B, inv.G));
        }

        dst = inv;
        return Classify(dst) == TfSrgbish;
    }

    private static float RgNonlinear(float x, in SkCurve curve, in SkTf tf, float[] dfdP)
    {
        float y = EvalCurve(curve, x);
        float g = tf.G, a = tf.A, b = tf.B, c = tf.C, d = tf.D, f = tf.F;
        float yy = Fmaxf((a * y) + b, 0.0f), dd = (a * d) + b;
        dfdP[0] = (Logf(yy) * Powf(yy, g)) - (Logf(dd) * Powf(dd, g));
        dfdP[1] = (y * g * Powf(yy, g - 1)) - (d * g * Powf(dd, g - 1));
        dfdP[2] = (g * Powf(yy, g - 1)) - (g * Powf(dd, g - 1));
        float fInv = Powf(yy, g) - Powf(dd, g) + (c * d) + f;
        return x - fInv;
    }

    private static bool GaussNewtonStep(in SkCurve curve, ref SkTf tf, float x0, float dx, int n)
    {
        var lhs = new float[9];
        var rhs = new float[3];
        var dfdP = new float[3];
        for (int i = 0; i < n; i++)
        {
            float x = x0 + ((float)i * dx);
            dfdP[0] = dfdP[1] = dfdP[2] = 0;
            float resid = RgNonlinear(x, curve, tf, dfdP);
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    lhs[(r * 3) + c] += dfdP[r] * dfdP[c];
                }

                rhs[r] += dfdP[r] * resid;
            }
        }

        for (int k = 0; k < 3; k++)
        {
            if (lhs[k] == 0 && lhs[3 + k] == 0 && lhs[6 + k] == 0 && lhs[k * 3] == 0 && lhs[(k * 3) + 1] == 0 && lhs[(k * 3) + 2] == 0)
            {
                lhs[(k * 3) + k] = 1;
            }
        }

        var lhsInv = new float[9];
        if (!Matrix3x3Invert(lhs, lhsInv))
        {
            return false;
        }

        float[] dP = MvMul(lhsInv, rhs);
        tf.G += dP[0];
        tf.A += dP[1];
        tf.B += dP[2];
        return IsFinite(tf.G) && IsFinite(tf.A) && IsFinite(tf.B);
    }

    private static float MaxRoundtripErrorChecked(in SkCurve curve, in SkTf tfInv)
    {
        if (!TfInvert(tfInv, out SkTf tf) || Classify(tf) != TfSrgbish)
        {
            return float.PositiveInfinity;
        }

        if (!TfInvert(tf, out SkTf tfInvAgain))
        {
            return float.PositiveInfinity;
        }

        return MaxRoundtripError(curve, tfInvAgain);
    }

    private static bool FixupTf(ref SkTf tf)
    {
        if (tf.A < 0)
        {
            return false;
        }

        if ((tf.A * tf.D) + tf.B < 0)
        {
            tf.B = -tf.A * tf.D;
        }

        tf.E = (tf.C * tf.D) + tf.F - Powf((tf.A * tf.D) + tf.B, tf.G);
        return IsFinite(tf.E);
    }

    private static bool FitNonlinear(in SkCurve curve, int l, int n, ref SkTf tf)
    {
        if (!FixupTf(ref tf))
        {
            return false;
        }

        float dx = 1.0f / (float)(n - 1);
        SkTf bestTf = tf;
        float bestMaxError = float.PositiveInfinity;
        float initError = MaxRoundtripErrorChecked(curve, tf);
        if (initError < bestMaxError)
        {
            bestMaxError = initError;
            bestTf = tf;
        }

        for (int j = 0; j < 8; j++)
        {
            if (!GaussNewtonStep(curve, ref tf, (float)l * dx, dx, n - l) || !FixupTf(ref tf))
            {
                tf = bestTf;
                return IsFinite(bestMaxError);
            }

            float maxError = MaxRoundtripErrorChecked(curve, tf);
            if (maxError < bestMaxError)
            {
                bestMaxError = maxError;
                bestTf = tf;
            }
        }

        tf = bestTf;
        return IsFinite(bestMaxError);
    }

    public static bool ApproximateCurve(in SkCurve curve, out SkTf approx, out float maxError)
    {
        approx = default;
        maxError = 0;
        if (curve.TableEntries == 0 || curve.TableEntries == 1 || curve.TableEntries > int.MaxValue)
        {
            return false;
        }

        int n = (int)curve.TableEntries;
        float dx = 1.0f / (float)(n - 1);
        maxError = float.PositiveInfinity;
        float[] tolerances = [1.5f / 65535.0f, 1.0f / 512.0f];
        for (int t = 0; t < tolerances.Length; t++)
        {
            var tf = new SkTf { F = 0.0f };
            int l = FitLinear(curve, n, tolerances[t], out tf.C, out tf.D, false, out _);
            if (l == n)
            {
                tf.G = 1;
                tf.A = tf.C;
                tf.B = tf.F;
                tf.C = tf.D = tf.E = tf.F = 0;
            }
            else if (l == n - 1)
            {
                tf.G = 1;
                tf.A = (EvalCurve(curve, (float)(n - 1) * dx) - EvalCurve(curve, (float)(n - 2) * dx)) / dx;
                tf.B = EvalCurve(curve, (float)(n - 2) * dx) - (tf.A * (float)(n - 2) * dx);
                tf.E = 0;
            }
            else
            {
                int mid = (l + n) / 2;
                float midX = (float)mid / (float)(n - 1);
                float midY = EvalCurve(curve, midX);
                tf.G = Log2f(midY) / Log2f(midX);
                tf.A = 1;
                tf.B = 0;
                tf.E = (tf.C * tf.D) + tf.F - Powf((tf.A * tf.D) + tf.B, tf.G);
                if (!TfInvert(tf, out SkTf tfInv) || !FitNonlinear(curve, l, n, ref tfInv))
                {
                    continue;
                }

                if (!TfInvert(tfInv, out tf))
                {
                    continue;
                }
            }

            if (Classify(tf) != TfSrgbish)
            {
                continue;
            }

            if (!TfInvert(tf, out SkTf tfInv2))
            {
                continue;
            }

            float err = MaxRoundtripError(curve, tfInv2);
            if (maxError > err)
            {
                maxError = err;
                approx = tf;
            }
        }

        return IsFinite(maxError);
    }

    // ---- skcms_Transform ----

    private enum Op
    {
        LoadRgb888,
        LoadRgba8888,
        LoadFff,
        LoadFfff,
        SwapRb,
        Clamp,
        Invert,
        ForceOpaque,
        Premul,
        Unpremul,
        Matrix3x3,
        Matrix3x4,
        LabToXyz,
        XyzToLab,
        Gamma,
        Tf,
        Pq,
        Hlg,
        Hlginv,
        Table,
        HlgOotfScale,
        HlginvOotfScale,
        ClutA2B,
        ClutB2A,
        StoreRgb888,
        StoreFff,
        StoreFfff,
    }

    private readonly struct Step
    {
        public readonly Op Op;
        public readonly int Channel;   // for the per-channel curve ops
        public readonly object? Ctx;

        public Step(Op op, int channel = 0, object? ctx = null)
        {
            Op = op;
            Channel = channel;
            Ctx = ctx;
        }
    }

    private static bool TfIsGamma(in SkTf tf) => tf.G > 0 && tf.A == 1 && tf.B == 0 && tf.C == 0 && tf.D == 0 && tf.E == 0 && tf.F == 0;

    private sealed class CurveRef
    {
        public SkCurve Curve;
    }

    /// <summary>select_curve_op: null for a no-op curve.</summary>
    private static Step? SelectCurveOp(in SkCurve curve, int channel)
    {
        if (curve.TableEntries == 0)
        {
            SkTf tf = curve.Parametric;
            if (TfIsGamma(tf))
            {
                return tf.G != 1 ? new Step(Op.Gamma, channel, new CurveRef { Curve = curve }) : null;
            }

            switch (Classify(tf))
            {
                case TfInvalid:
                case TfPq:
                case TfHlg:
                    return null;
                case TfSrgbish:
                    return new Step(Op.Tf, channel, new CurveRef { Curve = curve });
                case TfPqish:
                    return new Step(Op.Pq, channel, new CurveRef { Curve = curve });
                case TfHlgish:
                    return new Step(Op.Hlg, channel, new CurveRef { Curve = curve });
                case TfHlginvish:
                    return new Step(Op.Hlginv, channel, new CurveRef { Curve = curve });
            }
        }

        return new Step(Op.Table, channel, new CurveRef { Curve = curve });
    }

    private static void AddCurveOps(System.Collections.Generic.List<Step> ops, SkCurve[] curves, int numChannels)
    {
        // Fusing r, g and b into one op (select_curve_ops) does not change any value.
        for (int index = numChannels; index-- > 0;)
        {
            Step? s = SelectCurveOp(curves[index], index);
            if (s.HasValue)
            {
                ops.Add(s.Value);
            }
        }
    }

    private static bool IsIdentity3x4(float[] m)
    {
        float[] i = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        for (int k = 0; k < 12; k++)
        {
            if (BitConverter.SingleToInt32Bits(m[k]) != BitConverter.SingleToInt32Bits(i[k]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameMatrix(float[] a, float[] b)
    {
        for (int k = 0; k < 9; k++)
        {
            if (BitConverter.SingleToInt32Bits(a[k]) != BitConverter.SingleToInt32Bits(b[k]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCicpPqTrc(SkProfile p) => p.HasCicp && p.CicpTransfer == 16;

    private static bool HasCicpHlgTrc(SkProfile p) => p.HasCicp && p.CicpTransfer == 18;

    private static SkTf ReferencePqishTrc()
    {
        SkTf tf = MakePqish(-107 / 128.0f, 1.0f, 32 / 2523.0f, 2413 / 128.0f, -2392 / 128.0f, 8192 / 1305.0f);
        float w = 10000.0f / 203.0f;
        float ws = Powf(w, 1.0f / tf.F);
        tf.A = ws * tf.A;
        tf.B = ws * tf.B;
        return tf;
    }

    private static SkTf SdrHlgishTrc()
    {
        SkTf tf = MakeHlgish(2.0f, 2.0f, 1 / 0.17883277f, 0.28466892f, 0.55991073f);
        tf.F = (1.0f / 12.0f) - 1.0f;
        return tf;
    }

    private static bool PrepForDestination(SkProfile profile, float[] fromXyzD50, SkTf[] inv, out bool dstUsingB2A, out bool dstUsingHlgOotf)
    {
        bool hasXyzD50 = profile.HasToXyzD50 && Matrix3x3Invert(profile.ToXyzD50, fromXyzD50);
        dstUsingB2A = false;
        dstUsingHlgOotf = false;
        if (HasCicpPqTrc(profile) && hasXyzD50)
        {
            SkTf trcPq = ReferencePqishTrc();
            TfInvert(trcPq, out inv[0]);
            TfInvert(trcPq, out inv[1]);
            TfInvert(trcPq, out inv[2]);
            return true;
        }

        if (HasCicpHlgTrc(profile) && hasXyzD50)
        {
            SkTf trcHlg = SdrHlgishTrc();
            TfInvert(trcHlg, out inv[0]);
            TfInvert(trcHlg, out inv[1]);
            TfInvert(trcHlg, out inv[2]);
            dstUsingHlgOotf = true;
            return true;
        }

        if (profile.HasB2A)
        {
            dstUsingB2A = true;
            return true;
        }

        return hasXyzD50 && profile.HasTrc && profile.Trc[0].TableEntries == 0 && profile.Trc[1].TableEntries == 0 &&
               profile.Trc[2].TableEntries == 0 && TfInvert(profile.Trc[0].Parametric, out inv[0]) &&
               TfInvert(profile.Trc[1].Parametric, out inv[1]) && TfInvert(profile.Trc[2].Parametric, out inv[2]);
    }

    private static int BytesPerPixel(SkFmt f) => f switch
    {
        SkFmt.Rgb888 => 3,
        SkFmt.Rgba8888 => 4,
        SkFmt.RgbFff => 12,
        SkFmt.RgbaFfff => 16,
        _ => throw new NotSupportedException(),
    };

    public static bool Transform(float[] src, SkFmt srcFmt, SkAlpha srcAlpha, SkProfile? srcProfile, float[] dst, SkFmt dstFmt, SkAlpha dstAlpha,
        SkProfile? dstProfile, int n) =>
        Transform(MemoryMarshal.AsBytes(src.AsSpan()), srcFmt, srcAlpha, srcProfile, MemoryMarshal.AsBytes(dst.AsSpan()), dstFmt, dstAlpha, dstProfile, n);

    /// <summary>skcms_Transform for the pixel formats libjxl uses (8-bit and float RGB(A)).</summary>
    public static bool Transform(ReadOnlySpan<byte> src, SkFmt srcFmt, SkAlpha srcAlpha, SkProfile? srcProfile, Span<byte> dst, SkFmt dstFmt,
        SkAlpha dstAlpha, SkProfile? dstProfile, int n)
    {
        srcProfile ??= SrgbProfile();
        dstProfile ??= SrgbProfile();
        var ops = new System.Collections.Generic.List<Step>(32);
        switch (srcFmt)
        {
            case SkFmt.Rgb888:
                ops.Add(new Step(Op.LoadRgb888));
                break;
            case SkFmt.Rgba8888:
                ops.Add(new Step(Op.LoadRgba8888));
                break;
            case SkFmt.RgbFff:
                ops.Add(new Step(Op.LoadFff));
                break;
            case SkFmt.RgbaFfff:
                ops.Add(new Step(Op.LoadFfff));
                break;
            default:
                return false;
        }

        if (srcProfile.DataColorSpace == SigCmyk)
        {
            ops.Add(new Step(Op.Invert));
            srcAlpha = SkAlpha.Unpremul;
        }

        if (srcAlpha == SkAlpha.Opaque)
        {
            ops.Add(new Step(Op.ForceOpaque));
        }
        else if (srcAlpha == SkAlpha.PremulAsEncoded)
        {
            ops.Add(new Step(Op.Unpremul));
        }

        if (!ReferenceEquals(dstProfile, srcProfile))
        {
            bool srcUsingA2B = false, srcUsingHlgOotf = false;
            var dstFromXyz = new float[9];
            var dstInv = new SkTf[3];
            if (!PrepForDestination(dstProfile, dstFromXyz, dstInv, out bool dstUsingB2A, out bool dstUsingHlgOotf))
            {
                return false;
            }

            if (HasCicpPqTrc(srcProfile) && srcProfile.HasToXyzD50)
            {
                SkTf trc = ReferencePqishTrc();
                for (int c = 2; c >= 0; c--)
                {
                    ops.Add(new Step(Op.Pq, c, new CurveRef { Curve = ParamCurve(trc) }));
                }
            }
            else if (HasCicpHlgTrc(srcProfile) && srcProfile.HasToXyzD50)
            {
                srcUsingHlgOotf = true;
                SkTf trc = SdrHlgishTrc();
                for (int c = 2; c >= 0; c--)
                {
                    ops.Add(new Step(Op.Hlg, c, new CurveRef { Curve = ParamCurve(trc) }));
                }
            }
            else if (srcProfile.HasA2B)
            {
                srcUsingA2B = true;
                SkA2B a2b = srcProfile.A2B;
                if (a2b.InputChannels != 0)
                {
                    AddCurveOps(ops, a2b.InputCurves, (int)a2b.InputChannels);
                    ops.Add(new Step(Op.Clamp));
                    ops.Add(new Step(Op.ClutA2B, 0, a2b));
                }

                if (a2b.MatrixChannels == 3)
                {
                    AddCurveOps(ops, a2b.MatrixCurves, 3);
                    if (!IsIdentity3x4(a2b.Matrix))
                    {
                        ops.Add(new Step(Op.Matrix3x4, 0, a2b.Matrix));
                    }
                }

                if (a2b.OutputChannels == 3)
                {
                    AddCurveOps(ops, a2b.OutputCurves, 3);
                }

                if (srcProfile.Pcs == SigLab)
                {
                    ops.Add(new Step(Op.LabToXyz));
                }
            }
            else if (srcProfile.HasTrc && srcProfile.HasToXyzD50)
            {
                AddCurveOps(ops, srcProfile.Trc, 3);
            }
            else
            {
                return false;
            }

            if (dstUsingB2A)
            {
                if (!srcUsingA2B)
                {
                    ops.Add(new Step(Op.Matrix3x3, 0, srcProfile.ToXyzD50));
                    if (srcUsingHlgOotf)
                    {
                        ops.Add(new Step(Op.HlgOotfScale));
                    }
                }

                if (dstProfile.Pcs == SigLab)
                {
                    ops.Add(new Step(Op.XyzToLab));
                }

                SkB2A b2a = dstProfile.B2A;
                if (b2a.InputChannels == 3)
                {
                    AddCurveOps(ops, b2a.InputCurves, 3);
                }

                if (b2a.MatrixChannels == 3)
                {
                    if (!IsIdentity3x4(b2a.Matrix))
                    {
                        ops.Add(new Step(Op.Matrix3x4, 0, b2a.Matrix));
                    }

                    AddCurveOps(ops, b2a.MatrixCurves, 3);
                }

                if (b2a.OutputChannels != 0)
                {
                    ops.Add(new Step(Op.Clamp));
                    ops.Add(new Step(Op.ClutB2A, 0, b2a));
                    AddCurveOps(ops, b2a.OutputCurves, (int)b2a.OutputChannels);
                }
            }
            else
            {
                if (srcUsingHlgOotf != dstUsingHlgOotf)
                {
                    if (!srcUsingA2B)
                    {
                        ops.Add(new Step(Op.Matrix3x3, 0, srcProfile.ToXyzD50));
                    }

                    if (srcUsingHlgOotf)
                    {
                        ops.Add(new Step(Op.HlgOotfScale));
                    }

                    if (dstUsingHlgOotf)
                    {
                        ops.Add(new Step(Op.HlginvOotfScale));
                    }

                    ops.Add(new Step(Op.Matrix3x3, 0, dstFromXyz));
                }
                else if (srcUsingA2B)
                {
                    ops.Add(new Step(Op.Matrix3x3, 0, dstFromXyz));
                }
                else if (!SameMatrix(dstProfile.ToXyzD50, srcProfile.ToXyzD50))
                {
                    ops.Add(new Step(Op.Matrix3x3, 0, Matrix3x3Concat(dstFromXyz, srcProfile.ToXyzD50)));
                }

                SkCurve[] dstCurves = [ParamCurve(dstInv[0]), ParamCurve(dstInv[1]), ParamCurve(dstInv[2])];
                AddCurveOps(ops, dstCurves, 3);
            }
        }

        if (dstFmt < SkFmt.RgbHhh)
        {
            ops.Add(new Step(Op.Clamp));
        }

        if (dstProfile.DataColorSpace == SigCmyk)
        {
            ops.Add(new Step(Op.Invert));
            dstAlpha = SkAlpha.Unpremul;
        }

        if (dstAlpha == SkAlpha.Opaque)
        {
            ops.Add(new Step(Op.ForceOpaque));
        }
        else if (dstAlpha == SkAlpha.PremulAsEncoded)
        {
            ops.Add(new Step(Op.Premul));
        }

        switch (dstFmt)
        {
            case SkFmt.Rgb888:
                ops.Add(new Step(Op.StoreRgb888));
                break;
            case SkFmt.RgbFff:
                ops.Add(new Step(Op.StoreFff));
                break;
            case SkFmt.RgbaFfff:
                ops.Add(new Step(Op.StoreFfff));
                break;
            default:
                return false;
        }

        Run(ops, src, dst, n, BytesPerPixel(srcFmt), BytesPerPixel(dstFmt));
        return true;
    }

    // ---- stages (Transform_inl.h), lane by lane ----

    private static float Min(float x, float y) => x > y ? y : x;   // min_: if_then_else(x > y, y, x)

    private static float Max(float x, float y) => x < y ? y : x;   // max_: if_then_else(x < y, y, x)

    private static float ApproxLog2(float x) => Log2f(x);

    private static float ApproxExp2(float x)
    {
        float fract = x - Floorf(x);
        float fbits = (1.0f * (1 << 23)) * (x + 121.274057500f - (1.490129070f * fract) + (27.728023300f / (4.84252568f - fract)));
        int bits = Cvtt(Min(Max(fbits, 0), 2139095040f));
        return I2F(bits);
    }

    private static float ApproxPow(float x, float y) => x == 0 || x == 1 ? x : ApproxExp2(ApproxLog2(x) * y);

    private static float ApproxExp(float x) => ApproxExp2(1.4426950408889634074f * x);

    private static float ApproxLog(float x) => 0.69314718f * ApproxLog2(x);

    private static float StripSign(float x, out int sign)
    {
        int bits = F2I(x);
        sign = bits & unchecked((int)0x80000000);
        return I2F(bits ^ sign);
    }

    private static float ApplySign(float x, int sign) => I2F(sign | F2I(x));

    private static float ApplyTf(in SkTf tf, float x)
    {
        x = StripSign(x, out int sign);
        float v = x < tf.D ? (tf.C * x) + tf.F : ApproxPow((tf.A * x) + tf.B, tf.G) + tf.E;
        return ApplySign(v, sign);
    }

    private static float ApplyGamma(in SkTf tf, float x)
    {
        x = StripSign(x, out int sign);
        return ApplySign(ApproxPow(x, tf.G), sign);
    }

    private static float ApplyPq(in SkTf tf, float x)
    {
        x = StripSign(x, out int sign);
        float v = ApproxPow(Max(tf.A + (tf.B * ApproxPow(x, tf.C)), 0) / (tf.D + (tf.E * ApproxPow(x, tf.C))), tf.F);
        return ApplySign(v, sign);
    }

    private static float ApplyHlg(in SkTf tf, float x)
    {
        float r = tf.A, g = tf.B, a = tf.C, b = tf.D, c = tf.E, k = tf.F + 1;
        x = StripSign(x, out int sign);
        float v = x * r <= 1 ? ApproxPow(x * r, g) : ApproxExp((x - c) * a) + b;
        return k * ApplySign(v, sign);
    }

    private static float ApplyHlginv(in SkTf tf, float x)
    {
        float r = tf.A, g = tf.B, a = tf.C, b = tf.D, c = tf.E, k = tf.F + 1;
        x = StripSign(x, out int sign);
        x /= k;
        float v = x <= 1 ? r * ApproxPow(x, g) : (a * ApproxLog(x - b)) + c;
        return ApplySign(v, sign);
    }

    private static float ComputeYInXyzd50(float x, float y, float z) => (-0.02831655f * x) + (1.00995452f * y) + (0.02102382f * z);

    private static float Table(in SkCurve curve, float v)
    {
        float ix = Max(0, Min(v, 1)) * (float)(curve.TableEntries - 1);
        int lo = Cvtt(ix), hi = Cvtt(Minus1Ulp(ix + 1.0f));
        float t = ix - lo;
        float l, h;
        byte[] b = curve.Buf!;
        if (curve.Table8 >= 0)
        {
            l = b[curve.Table8 + lo] * (1 / 255.0f);
            h = b[curve.Table8 + hi] * (1 / 255.0f);
        }
        else
        {
            l = ((b[curve.Table16 + (2 * lo)] << 8) | b[curve.Table16 + (2 * lo) + 1]) * (1 / 65535.0f);
            h = ((b[curve.Table16 + (2 * hi)] << 8) | b[curve.Table16 + (2 * hi) + 1]) * (1 / 65535.0f);
        }

        return l + ((h - l) * t);
    }

    private static void Clut(uint inputChannels, uint outputChannels, byte[] gridPoints, byte[] buf, int grid8, int grid16,
        ref float r, ref float g, ref float b, ref float a, Span<int> index, Span<float> weight)
    {
        int dim = (int)inputChannels;
        float i0 = r, i1 = g, i2 = b, i3 = a;
        for (int i = dim - 1, stride = 1; i >= 0; i--)
        {
            float input = i == 0 ? i0 : i == 1 ? i1 : i == 2 ? i2 : i3;
            float x = input * (float)(gridPoints[i] - 1);
            int lo = Cvtt(x), hi = Cvtt(Minus1Ulp(x + 1.0f));
            index[i] = lo * stride;
            index[i + 4] = hi * stride;
            stride *= gridPoints[i];
            float t = x - lo;
            weight[i] = 1 - t;
            weight[i + 4] = t;
        }

        r = g = b = 0;
        if (outputChannels == 4)
        {
            a = 0;
        }

        for (int combo = 0; combo < (1 << dim); combo++)
        {
            int ix = index[(combo & 1) * 4];
            float w = weight[(combo & 1) * 4];
            int sw = (dim - 1) & 3;
            if (sw >= 3)
            {
                ix += index[3 + ((combo & 8) / 2)];
                w *= weight[3 + ((combo & 8) / 2)];
            }

            if (sw >= 2)
            {
                ix += index[2 + (combo & 4)];
                w *= weight[2 + (combo & 4)];
            }

            if (sw >= 1)
            {
                ix += index[1 + ((combo & 2) * 2)];
                w *= weight[1 + ((combo & 2) * 2)];
            }

            float rr, gg, bb, aa = 0;
            if (outputChannels == 3)
            {
                if (grid8 >= 0)
                {
                    int p = grid8 + (3 * ix);
                    rr = buf[p] * (1 / 255.0f);
                    gg = buf[p + 1] * (1 / 255.0f);
                    bb = buf[p + 2] * (1 / 255.0f);
                }
                else
                {
                    int p = grid16 + (6 * ix);
                    rr = ((buf[p] << 8) | buf[p + 1]) * (1 / 65535.0f);
                    gg = ((buf[p + 2] << 8) | buf[p + 3]) * (1 / 65535.0f);
                    bb = ((buf[p + 4] << 8) | buf[p + 5]) * (1 / 65535.0f);
                }
            }
            else
            {
                if (grid8 >= 0)
                {
                    int p = grid8 + (4 * ix);
                    rr = buf[p] * (1 / 255.0f);
                    gg = buf[p + 1] * (1 / 255.0f);
                    bb = buf[p + 2] * (1 / 255.0f);
                    aa = buf[p + 3] * (1 / 255.0f);
                }
                else
                {
                    int p = grid16 + (8 * ix);
                    rr = ((buf[p] << 8) | buf[p + 1]) * (1 / 65535.0f);
                    gg = ((buf[p + 2] << 8) | buf[p + 3]) * (1 / 65535.0f);
                    bb = ((buf[p + 4] << 8) | buf[p + 5]) * (1 / 65535.0f);
                    aa = ((buf[p + 6] << 8) | buf[p + 7]) * (1 / 65535.0f);
                }
            }

            r += w * rr;
            g += w * gg;
            b += w * bb;
            a += w * aa;
        }
    }

    private static void Run(System.Collections.Generic.List<Step> ops, ReadOnlySpan<byte> src, Span<byte> dst, int n, int srcBpp, int dstBpp)
    {
        var R = new float[n];
        var G = new float[n];
        var B = new float[n];
        var A = new float[n];
        A.AsSpan().Fill(1);
        Span<int> index = stackalloc int[8];
        Span<float> weight = stackalloc float[8];
        ReadOnlySpan<float> srcF = srcBpp >= 12 ? MemoryMarshal.Cast<byte, float>(src[..(n * srcBpp)]) : default;
        foreach (Step s in ops)
        {
            switch (s.Op)
            {
                case Op.LoadRgb888:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] = src[3 * i] * (1 / 255.0f);
                        G[i] = src[(3 * i) + 1] * (1 / 255.0f);
                        B[i] = src[(3 * i) + 2] * (1 / 255.0f);
                    }

                    break;
                case Op.LoadRgba8888:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] = src[4 * i] * (1 / 255.0f);
                        G[i] = src[(4 * i) + 1] * (1 / 255.0f);
                        B[i] = src[(4 * i) + 2] * (1 / 255.0f);
                        A[i] = src[(4 * i) + 3] * (1 / 255.0f);
                    }

                    break;
                case Op.LoadFff:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] = srcF[3 * i];
                        G[i] = srcF[(3 * i) + 1];
                        B[i] = srcF[(3 * i) + 2];
                    }

                    break;
                case Op.LoadFfff:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] = srcF[4 * i];
                        G[i] = srcF[(4 * i) + 1];
                        B[i] = srcF[(4 * i) + 2];
                        A[i] = srcF[(4 * i) + 3];
                    }

                    break;
                case Op.SwapRb:
                    (R, B) = (B, R);
                    break;
                case Op.Clamp:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] = Max(0, Min(R[i], 1));
                        G[i] = Max(0, Min(G[i], 1));
                        B[i] = Max(0, Min(B[i], 1));
                        A[i] = Max(0, Min(A[i], 1));
                    }

                    break;
                case Op.Invert:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] = 1 - R[i];
                        G[i] = 1 - G[i];
                        B[i] = 1 - B[i];
                        A[i] = 1 - A[i];
                    }

                    break;
                case Op.ForceOpaque:
                    A.AsSpan().Fill(1);
                    break;
                case Op.Premul:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] *= A[i];
                        G[i] *= A[i];
                        B[i] *= A[i];
                    }

                    break;
                case Op.Unpremul:
                    for (int i = 0; i < n; i++)
                    {
                        float scale = 1 / A[i] < float.PositiveInfinity ? 1 / A[i] : 0;
                        R[i] *= scale;
                        G[i] *= scale;
                        B[i] *= scale;
                    }

                    break;
                case Op.Matrix3x3:
                {
                    float[] m = (float[])s.Ctx!;
                    for (int i = 0; i < n; i++)
                    {
                        float r = R[i], g = G[i], b = B[i];
                        R[i] = (m[0] * r) + (m[1] * g) + (m[2] * b);
                        G[i] = (m[3] * r) + (m[4] * g) + (m[5] * b);
                        B[i] = (m[6] * r) + (m[7] * g) + (m[8] * b);
                    }

                    break;
                }

                case Op.Matrix3x4:
                {
                    float[] m = (float[])s.Ctx!;
                    for (int i = 0; i < n; i++)
                    {
                        float r = R[i], g = G[i], b = B[i];
                        R[i] = (m[0] * r) + (m[1] * g) + (m[2] * b) + m[3];
                        G[i] = (m[4] * r) + (m[5] * g) + (m[6] * b) + m[7];
                        B[i] = (m[8] * r) + (m[9] * g) + (m[10] * b) + m[11];
                    }

                    break;
                }

                case Op.LabToXyz:
                    for (int i = 0; i < n; i++)
                    {
                        float l = R[i] * 100.0f, aa = (G[i] * 255.0f) - 128.0f, bb = (B[i] * 255.0f) - 128.0f;
                        float y = (l + 16.0f) * (1 / 116.0f), x = y + (aa * (1 / 500.0f)), z = y - (bb * (1 / 200.0f));
                        x = x * x * x > 0.008856f ? x * x * x : (x - (16 / 116.0f)) * (1 / 7.787f);
                        y = y * y * y > 0.008856f ? y * y * y : (y - (16 / 116.0f)) * (1 / 7.787f);
                        z = z * z * z > 0.008856f ? z * z * z : (z - (16 / 116.0f)) * (1 / 7.787f);
                        R[i] = x * 0.9642f;
                        G[i] = y;
                        B[i] = z * 0.8249f;
                    }

                    break;
                case Op.XyzToLab:
                    for (int i = 0; i < n; i++)
                    {
                        float x = R[i] * (1 / 0.9642f), y = G[i], z = B[i] * (1 / 0.8249f);
                        x = x > 0.008856f ? ApproxPow(x, 1 / 3.0f) : (x * 7.787f) + (16 / 116.0f);
                        y = y > 0.008856f ? ApproxPow(y, 1 / 3.0f) : (y * 7.787f) + (16 / 116.0f);
                        z = z > 0.008856f ? ApproxPow(z, 1 / 3.0f) : (z * 7.787f) + (16 / 116.0f);
                        float l = (y * 116.0f) - 16.0f, aa = (x - y) * 500.0f, bb = (y - z) * 200.0f;
                        R[i] = l * (1 / 100.0f);
                        G[i] = (aa + 128.0f) * (1 / 255.0f);
                        B[i] = (bb + 128.0f) * (1 / 255.0f);
                    }

                    break;
                case Op.Gamma:
                case Op.Tf:
                case Op.Pq:
                case Op.Hlg:
                case Op.Hlginv:
                case Op.Table:
                {
                    SkCurve curve = ((CurveRef)s.Ctx!).Curve;
                    float[] ch = s.Channel switch { 0 => R, 1 => G, 2 => B, _ => A };
                    SkTf tf = curve.Parametric;
                    for (int i = 0; i < n; i++)
                    {
                        ch[i] = s.Op switch
                        {
                            Op.Gamma => ApplyGamma(tf, ch[i]),
                            Op.Tf => ApplyTf(tf, ch[i]),
                            Op.Pq => ApplyPq(tf, ch[i]),
                            Op.Hlg => ApplyHlg(tf, ch[i]),
                            Op.Hlginv => ApplyHlginv(tf, ch[i]),
                            _ => Table(curve, ch[i]),
                        };
                    }

                    break;
                }

                case Op.HlgOotfScale:
                    for (int i = 0; i < n; i++)
                    {
                        float y = ComputeYInXyzd50(R[i], G[i], B[i]);
                        y = StripSign(y, out int sign);
                        float yg = ApplySign(ApproxPow(y, 0.2f), sign);
                        R[i] = R[i] * yg * (1000.0f / 203.0f);
                        G[i] = G[i] * yg * (1000.0f / 203.0f);
                        B[i] = B[i] * yg * (1000.0f / 203.0f);
                    }

                    break;
                case Op.HlginvOotfScale:
                    for (int i = 0; i < n; i++)
                    {
                        R[i] *= 203.0f / 1000.0f;
                        G[i] *= 203.0f / 1000.0f;
                        B[i] *= 203.0f / 1000.0f;
                        float y = ComputeYInXyzd50(R[i], G[i], B[i]);
                        y = StripSign(y, out int sign);
                        float yg = ApplySign(ApproxPow(y, (1.0f / 1.2f) - 1.0f), sign);
                        R[i] *= yg;
                        G[i] *= yg;
                        B[i] *= yg;
                    }

                    break;
                case Op.ClutA2B:
                {
                    var a2b = (SkA2B)s.Ctx!;
                    for (int i = 0; i < n; i++)
                    {
                        float r = R[i], g = G[i], b = B[i], a = A[i];
                        Clut(a2b.InputChannels, a2b.OutputChannels, a2b.GridPoints, a2b.Buf!, a2b.Grid8, a2b.Grid16, ref r, ref g, ref b, ref a,
                            index, weight);
                        R[i] = r;
                        G[i] = g;
                        B[i] = b;
                        if (a2b.InputChannels == 4)
                        {
                            A[i] = 1;
                        }
                    }

                    break;
                }

                case Op.ClutB2A:
                {
                    var b2a = (SkB2A)s.Ctx!;
                    for (int i = 0; i < n; i++)
                    {
                        float r = R[i], g = G[i], b = B[i], a = A[i];
                        Clut(b2a.InputChannels, b2a.OutputChannels, b2a.GridPoints, b2a.Buf!, b2a.Grid8, b2a.Grid16, ref r, ref g, ref b, ref a,
                            index, weight);
                        R[i] = r;
                        G[i] = g;
                        B[i] = b;
                        A[i] = a;
                    }

                    break;
                }

                case Op.StoreRgb888:
                    for (int i = 0; i < n; i++)
                    {
                        dst[3 * i] = (byte)Cvtt((R[i] * 255) + 0.5f);
                        dst[(3 * i) + 1] = (byte)Cvtt((G[i] * 255) + 0.5f);
                        dst[(3 * i) + 2] = (byte)Cvtt((B[i] * 255) + 0.5f);
                    }

                    break;
                case Op.StoreFff:
                {
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst[..(n * dstBpp)]);
                    for (int i = 0; i < n; i++)
                    {
                        d[3 * i] = R[i];
                        d[(3 * i) + 1] = G[i];
                        d[(3 * i) + 2] = B[i];
                    }

                    break;
                }

                case Op.StoreFfff:
                {
                    Span<float> d = MemoryMarshal.Cast<byte, float>(dst[..(n * dstBpp)]);
                    for (int i = 0; i < n; i++)
                    {
                        d[4 * i] = R[i];
                        d[(4 * i) + 1] = G[i];
                        d[(4 * i) + 2] = B[i];
                        d[(4 * i) + 3] = A[i];
                    }

                    break;
                }
            }
        }
    }

    // ---- destination preparation ----

    public static bool MakeUsableAsDestination(SkProfile profile)
    {
        if (!profile.HasB2A)
        {
            var fromXyzD50 = new float[9];
            if (!profile.HasTrc || !profile.HasToXyzD50 || !Matrix3x3Invert(profile.ToXyzD50, fromXyzD50))
            {
                return false;
            }

            var tf = new SkTf[3];
            for (int i = 0; i < 3; i++)
            {
                if (profile.Trc[i].TableEntries == 0 && TfInvert(profile.Trc[i].Parametric, out _))
                {
                    tf[i] = profile.Trc[i].Parametric;
                    continue;
                }

                if (!ApproximateCurve(profile.Trc[i], out tf[i], out _))
                {
                    return false;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                profile.Trc[i] = ParamCurve(tf[i]);
            }
        }

        return true;
    }
}
