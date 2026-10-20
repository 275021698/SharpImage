// JPEG XL header bundles (headers.cc, image_metadata.cc, color_encoding_internal.cc, frame_header.cc,
// loop_filter.cc), each a line-by-line port of libjxl's VisitFields so writing, reading and all-default
// detection behave exactly as in libjxl.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjSizeHeader : ILjBundle
{
    public bool Small;
    public uint YsizeDiv8Minus1;
    public uint Ysize = 1;
    public uint Ratio;
    public uint XsizeDiv8Minus1;
    public uint Xsize = 1;

    private static readonly LjU32Enc SizeEnc = new(LjU32.BitsOffset(9, 1), LjU32.BitsOffset(13, 1), LjU32.BitsOffset(18, 1), LjU32.BitsOffset(30, 1));

    public void VisitFields(LjVisitor v)
    {
        v.Bool(false, ref Small);
        if (v.Conditional(Small))
        {
            v.Bits(5, 0, ref YsizeDiv8Minus1);
        }

        if (v.Conditional(!Small))
        {
            v.U32(SizeEnc, 1, ref Ysize);
        }

        v.Bits(3, 0, ref Ratio);
        if (v.Conditional(Ratio == 0 && Small))
        {
            v.Bits(5, 0, ref XsizeDiv8Minus1);
        }

        if (v.Conditional(Ratio == 0 && !Small))
        {
            v.U32(SizeEnc, 1, ref Xsize);
        }
    }

    private static readonly (uint Num, uint Den)[] Ratios = [(1, 1), (12, 10), (4, 3), (3, 2), (16, 9), (5, 4), (2, 1)];

    private static uint MulTruncate(uint ratio, uint m) => (uint)((ulong)m * Ratios[ratio - 1].Num / Ratios[ratio - 1].Den);

    public static uint FindAspectRatio(uint xsize, uint ysize)
    {
        for (uint r = 1; r < 8; r++)
        {
            if (xsize == MulTruncate(r, ysize))
            {
                return r;
            }
        }

        return 0;
    }

    public uint YsizeValue => Small ? (YsizeDiv8Minus1 + 1) * 8 : Ysize;

    public uint XsizeValue => Ratio != 0 ? MulTruncate(Ratio, YsizeValue) : Small ? (XsizeDiv8Minus1 + 1) * 8 : Xsize;

    public void Set(uint xsize, uint ysize)
    {
        Ratio = FindAspectRatio(xsize, ysize);
        Small = ysize <= 256 && ysize % 8 == 0 && (Ratio != 0 || (xsize <= 256 && xsize % 8 == 0));
        if (Small)
        {
            YsizeDiv8Minus1 = (ysize / 8) - 1;
        }
        else
        {
            Ysize = ysize;
        }

        if (Ratio == 0)
        {
            if (Small)
            {
                XsizeDiv8Minus1 = (xsize / 8) - 1;
            }
            else
            {
                Xsize = xsize;
            }
        }
    }
}

internal sealed class LjPreviewHeader : ILjBundle
{
    public bool Div8;
    public uint YsizeDiv8 = 1, Ysize = 1, Ratio, XsizeDiv8 = 1, Xsize = 1;

    private static readonly LjU32Enc Div8Enc = new(LjU32.Val(16), LjU32.Val(32), LjU32.BitsOffset(5, 1), LjU32.BitsOffset(9, 33));
    private static readonly LjU32Enc PlainEnc = new(LjU32.BitsOffset(6, 1), LjU32.BitsOffset(8, 65), LjU32.BitsOffset(10, 321), LjU32.BitsOffset(12, 1345));

    public void VisitFields(LjVisitor v)
    {
        v.Bool(false, ref Div8);
        if (v.Conditional(Div8))
        {
            v.U32(Div8Enc, 1, ref YsizeDiv8);
        }

        if (v.Conditional(!Div8))
        {
            v.U32(PlainEnc, 1, ref Ysize);
        }

        v.Bits(3, 0, ref Ratio);
        if (v.Conditional(Ratio == 0 && Div8))
        {
            v.U32(Div8Enc, 1, ref XsizeDiv8);
        }

        if (v.Conditional(Ratio == 0 && !Div8))
        {
            v.U32(PlainEnc, 1, ref Xsize);
        }
    }
}

internal sealed class LjAnimationHeader : ILjBundle
{
    public uint TpsNumerator = 100, TpsDenominator = 1, NumLoops;
    public bool HaveTimecodes;

    public void VisitFields(LjVisitor v)
    {
        v.U32(LjU32.Val(100), LjU32.Val(1000), LjU32.BitsOffset(10, 1), LjU32.BitsOffset(30, 1), 1, ref TpsNumerator);
        v.U32(LjU32.Val(1), LjU32.Val(1001), LjU32.BitsOffset(8, 1), LjU32.BitsOffset(10, 1), 1, ref TpsDenominator);
        v.U32(LjU32.Val(0), LjU32.BitsOnly(3), LjU32.BitsOnly(16), LjU32.BitsOnly(32), 0, ref NumLoops);
        v.Bool(false, ref HaveTimecodes);
    }
}

internal sealed class LjBitDepth : ILjBundle
{
    public bool FloatingPointSample;
    public uint BitsPerSample = 8;
    public uint ExponentBitsPerSample;

    public void VisitFields(LjVisitor v)
    {
        v.Bool(false, ref FloatingPointSample);
        if (!FloatingPointSample)
        {
            v.U32(LjU32.Val(8), LjU32.Val(10), LjU32.Val(12), LjU32.BitsOffset(6, 1), 8, ref BitsPerSample);
            ExponentBitsPerSample = 0;
        }
        else
        {
            v.U32(LjU32.Val(32), LjU32.Val(16), LjU32.Val(24), LjU32.BitsOffset(6, 1), 32, ref BitsPerSample);
            ExponentBitsPerSample -= 1;
            v.Bits(4, 8 - 1, ref ExponentBitsPerSample);
            ExponentBitsPerSample += 1;
        }
    }
}

internal sealed class LjExtraChannelInfo : ILjBundle
{
    public const uint TypeAlpha = 0, TypeDepth = 1, TypeSpotColor = 2, TypeSelectionMask = 3, TypeBlack = 4, TypeCfa = 5, TypeThermal = 6,
        TypeUnknown = 15;

    public bool AllDefaultField = true;
    public uint Type = TypeAlpha;
    public LjBitDepth BitDepth = new();
    public uint DimShift;
    public string Name = string.Empty;
    public bool AlphaAssociated;
    public float[] SpotColor = new float[4];
    public uint CfaChannel = 1;

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        v.Enum(TypeAlpha, ref Type);
        v.VisitNested(BitDepth);
        v.U32(LjU32.Val(0), LjU32.Val(3), LjU32.Val(4), LjU32.BitsOffset(3, 1), 0, ref DimShift);
        LjHeaderUtil.VisitNameString(v, ref Name);
        if (v.Conditional(Type == TypeAlpha))
        {
            v.Bool(false, ref AlphaAssociated);
        }

        if (v.Conditional(Type == TypeSpotColor))
        {
            for (int i = 0; i < 4; i++)
            {
                v.F16(0, ref SpotColor[i]);
            }
        }

        if (v.Conditional(Type == TypeCfa))
        {
            v.U32(LjU32.Val(1), LjU32.BitsOnly(2), LjU32.BitsOffset(4, 3), LjU32.BitsOffset(8, 19), 1, ref CfaChannel);
        }
    }
}

internal sealed class LjToneMapping : ILjBundle
{
    public bool AllDefaultField = true;
    public float IntensityTarget = 255f, MinNits, LinearBelow;
    public bool RelativeToMaxDisplay;

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        v.F16(255f, ref IntensityTarget);
        v.F16(0.0f, ref MinNits);
        v.Bool(false, ref RelativeToMaxDisplay);
        v.F16(0.0f, ref LinearBelow);
    }
}

internal sealed class LjCustomxy : ILjBundle
{
    public int X, Y;

    private static readonly LjU32Enc Enc = new(LjU32.BitsOnly(19), LjU32.BitsOffset(19, 524288), LjU32.BitsOffset(20, 1048576), LjU32.BitsOffset(21, 2097152));

    public void VisitFields(LjVisitor v)
    {
        uint ux = LjMath.PackSigned(X);
        v.U32(Enc, 0, ref ux);
        X = LjHeaderUtil.UnpackSigned(ux);
        uint uy = LjMath.PackSigned(Y);
        v.U32(Enc, 0, ref uy);
        Y = LjHeaderUtil.UnpackSigned(uy);
    }
}

internal sealed class LjCustomTransferFunction : ILjBundle
{
    public const uint Tf709 = 1, TfUnknown = 2, TfLinear = 8, TfSrgb = 13, TfPq = 16, TfDci = 17, TfHlg = 18;
    public uint NonserializedColorSpace;
    public bool HaveGamma;
    public uint Gamma = 10000000;
    public uint TransferFunction = TfSrgb;

    private bool SetImplicit()
    {
        if (NonserializedColorSpace == LjColorEncoding.CsXyb)
        {
            HaveGamma = true;
            Gamma = 3333333;
            return true;
        }

        return false;
    }

    public void VisitFields(LjVisitor v)
    {
        if (v.Conditional(!SetImplicit()))
        {
            v.Bool(false, ref HaveGamma);
            if (v.Conditional(HaveGamma))
            {
                v.Bits(24, 10000000, ref Gamma);
            }

            if (v.Conditional(!HaveGamma))
            {
                v.Enum(TfSrgb, ref TransferFunction);
            }
        }
    }
}

internal sealed class LjColorEncoding : ILjBundle
{
    public const uint CsRgb = 0, CsGray = 1, CsXyb = 2, CsUnknown = 3;
    public const uint WpD65 = 1, WpCustom = 2, WpE = 10, WpDci = 11;
    public const uint PrSrgb = 1, PrCustom = 2, Pr2100 = 9, PrP3 = 11;
    public const uint RiPerceptual = 0, RiRelative = 1, RiSaturation = 2, RiAbsolute = 3;

    public bool AllDefaultField = true;
    public bool WantIcc;
    public uint ColorSpace = CsRgb;
    public uint WhitePoint = WpD65;
    public LjCustomxy White = new();
    public uint Primaries = PrSrgb;
    public LjCustomxy Red = new(), Green = new(), Blue = new();
    public LjCustomTransferFunction Tf = new();
    public uint RenderingIntent = RiRelative;

    /// <summary>The ICC profile (when <see cref="WantIcc"/>); not serialized here.</summary>
    public byte[]? Icc;

    public bool HasPrimaries => ColorSpace != CsGray && ColorSpace != CsXyb;

    private bool ImplicitWhitePoint()
    {
        if (ColorSpace == CsXyb)
        {
            WhitePoint = WpD65;
            return true;
        }

        return false;
    }

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        v.Bool(false, ref WantIcc);
        v.Enum(CsRgb, ref ColorSpace);
        if (v.Conditional(!WantIcc))
        {
            if (v.Conditional(!ImplicitWhitePoint()))
            {
                v.Enum(WpD65, ref WhitePoint);
                if (v.Conditional(WhitePoint == WpCustom))
                {
                    v.VisitNested(White);
                }
            }

            if (v.Conditional(HasPrimaries))
            {
                v.Enum(PrSrgb, ref Primaries);
                if (v.Conditional(Primaries == PrCustom))
                {
                    v.VisitNested(Red);
                    v.VisitNested(Green);
                    v.VisitNested(Blue);
                }
            }

            Tf.NonserializedColorSpace = ColorSpace;
            v.VisitNested(Tf);
            v.Enum(RiRelative, ref RenderingIntent);
        }
    }
}

internal sealed class LjOpsinInverseMatrix : ILjBundle
{
    public bool AllDefaultField = true;
    public float[] InverseMatrix = (float[])DefaultInverse.Clone();
    public float[] OpsinBiases = (float[])DefaultBiases.Clone();
    public float[] QuantBiases = (float[])DefaultQuantBias.Clone();

    public static readonly float[] DefaultInverse =
    [
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
    ];

    public static readonly float[] DefaultBiases = [-0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f];
    public static readonly float[] DefaultQuantBias = [1.0f - 0.05465007330715401f, 1.0f - 0.07005449891748593f, 1.0f - 0.049935103337343655f, 0.145f];

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        for (int i = 0; i < 9; i++)
        {
            v.F16(DefaultInverse[i], ref InverseMatrix[i]);
        }

        for (int i = 0; i < 3; i++)
        {
            v.F16(DefaultBiases[i], ref OpsinBiases[i]);
        }

        for (int i = 0; i < 4; i++)
        {
            v.F16(DefaultQuantBias[i], ref QuantBiases[i]);
        }
    }
}

internal sealed class LjCustomTransformData : ILjBundle
{
    public bool AllDefaultField = true;
    public bool NonserializedXybEncoded;
    public LjOpsinInverseMatrix OpsinInverseMatrix = new();
    public uint CustomWeightsMask;
    public float[] Upsampling2Weights = (float[])LjUpsamplingWeights.K2.Clone();
    public float[] Upsampling4Weights = (float[])LjUpsamplingWeights.K4.Clone();
    public float[] Upsampling8Weights = (float[])LjUpsamplingWeights.K8.Clone();

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        if (v.Conditional(NonserializedXybEncoded))
        {
            v.VisitNested(OpsinInverseMatrix);
        }

        v.Bits(3, 0, ref CustomWeightsMask);
        if (v.Conditional((CustomWeightsMask & 1) != 0))
        {
            for (int i = 0; i < 15; i++)
            {
                v.F16(LjUpsamplingWeights.K2[i], ref Upsampling2Weights[i]);
            }
        }

        if (v.Conditional((CustomWeightsMask & 2) != 0))
        {
            for (int i = 0; i < 55; i++)
            {
                v.F16(LjUpsamplingWeights.K4[i], ref Upsampling4Weights[i]);
            }
        }

        if (v.Conditional((CustomWeightsMask & 4) != 0))
        {
            for (int i = 0; i < 210; i++)
            {
                v.F16(LjUpsamplingWeights.K8[i], ref Upsampling8Weights[i]);
            }
        }
    }
}

internal sealed class LjImageMetadata : ILjBundle
{
    public bool AllDefaultField = true;
    public uint Orientation = 1;
    public bool HaveIntrinsicSize;
    public LjSizeHeader IntrinsicSize = new();
    public bool HavePreview;
    public LjPreviewHeader PreviewSize = new();
    public bool HaveAnimation;
    public LjAnimationHeader Animation = new();
    public LjBitDepth BitDepth = new();
    public bool Modular16BitBufferSufficient = true;
    public List<LjExtraChannelInfo> ExtraChannelInfo = new();
    public bool XybEncoded = true;
    public LjColorEncoding ColorEncoding = new();
    public LjToneMapping ToneMapping = new();
    public ulong Extensions;

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        bool toneMappingDefault = !v.IsReading && LjFields.AllDefault(ToneMapping);
        bool extraFields = Orientation != 1 || HavePreview || HaveAnimation || HaveIntrinsicSize || !toneMappingDefault;
        v.Bool(false, ref extraFields);
        if (v.Conditional(extraFields))
        {
            Orientation--;
            v.Bits(3, 0, ref Orientation);
            Orientation++;
            v.Bool(false, ref HaveIntrinsicSize);
            if (v.Conditional(HaveIntrinsicSize))
            {
                v.VisitNested(IntrinsicSize);
            }

            v.Bool(false, ref HavePreview);
            if (v.Conditional(HavePreview))
            {
                v.VisitNested(PreviewSize);
            }

            v.Bool(false, ref HaveAnimation);
            if (v.Conditional(HaveAnimation))
            {
                v.VisitNested(Animation);
            }
        }
        else
        {
            Orientation = 1;
            HaveIntrinsicSize = false;
            HavePreview = false;
            HaveAnimation = false;
        }

        v.VisitNested(BitDepth);
        v.Bool(true, ref Modular16BitBufferSufficient);
        uint numExtra = (uint)ExtraChannelInfo.Count;
        v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.BitsOffset(4, 2), LjU32.BitsOffset(12, 1), 0, ref numExtra);
        if (v.Conditional(numExtra != 0))
        {
            if (v.IsReading)
            {
                ExtraChannelInfo.Clear();
                for (int i = 0; i < numExtra; i++)
                {
                    ExtraChannelInfo.Add(new LjExtraChannelInfo());
                }
            }

            foreach (LjExtraChannelInfo eci in ExtraChannelInfo)
            {
                v.VisitNested(eci);
            }
        }

        v.Bool(true, ref XybEncoded);
        v.VisitNested(ColorEncoding);
        if (v.Conditional(extraFields))
        {
            v.VisitNested(ToneMapping);
        }

        v.BeginExtensions(ref Extensions);
        v.EndExtensions();
    }
}

internal sealed class LjChromaSubsampling : ILjBundle
{
    private static readonly byte[] KHShift = [0, 1, 1, 0];
    private static readonly byte[] KVShift = [0, 1, 0, 1];
    public uint[] ChannelMode = new uint[3];
    private int maxhs, maxvs;

    public void VisitFields(LjVisitor v)
    {
        for (int i = 0; i < 3; i++)
        {
            v.Bits(2, 0, ref ChannelMode[i]);
        }

        Recompute();
    }

    private void Recompute()
    {
        maxhs = 0;
        maxvs = 0;
        foreach (uint ch in ChannelMode)
        {
            maxhs = Math.Max(maxhs, KHShift[ch]);
            maxvs = Math.Max(maxvs, KVShift[ch]);
        }
    }

    public int HShift(int c) => maxhs - KHShift[ChannelMode[c]];

    public int VShift(int c) => maxvs - KVShift[ChannelMode[c]];

    public int MaxHShift => maxhs;

    public int MaxVShift => maxvs;

    public int RawHShift(int c) => KHShift[ChannelMode[c]];

    public int RawVShift(int c) => KVShift[ChannelMode[c]];

    public bool Is444 => HShift(0) == 0 && VShift(0) == 0 && HShift(2) == 0 && VShift(2) == 0 && HShift(1) == 0 && VShift(1) == 0;

    public bool Set(ReadOnlySpan<int> hsample, ReadOnlySpan<int> vsample)
    {
        for (int c = 0; c < 3; c++)
        {
            int cjpeg = c < 2 ? c ^ 1 : c;
            int i = 0;
            for (; i < 4; i++)
            {
                if (1 << KHShift[i] == hsample[cjpeg] && 1 << KVShift[i] == vsample[cjpeg])
                {
                    ChannelMode[c] = (uint)i;
                    break;
                }
            }

            if (i == 4)
            {
                return false;
            }
        }

        Recompute();
        return true;
    }
}

internal sealed class LjPasses : ILjBundle
{
    public uint NumPasses = 1;
    public uint NumDownsample;
    public uint[] Shift = new uint[11];
    public uint[] Downsample = new uint[11];
    public uint[] LastPass = new uint[11];

    public void VisitFields(LjVisitor v)
    {
        v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), LjU32.BitsOffset(3, 4), 1, ref NumPasses);
        if (v.Conditional(NumPasses != 1))
        {
            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.Val(2), LjU32.BitsOffset(1, 3), 0, ref NumDownsample);
            for (int i = 0; i < NumPasses - 1; i++)
            {
                v.Bits(2, 0, ref Shift[i]);
            }

            Shift[NumPasses - 1] = 0;
            for (int i = 0; i < NumDownsample; i++)
            {
                v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(4), LjU32.Val(8), 1, ref Downsample[i]);
            }

            for (int i = 0; i < NumDownsample; i++)
            {
                v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.Val(2), LjU32.BitsOnly(3), 0, ref LastPass[i]);
            }
        }
    }
}

internal sealed class LjBlendingInfo : ILjBundle
{
    public const uint Replace = 0, Add = 1, Blend = 2, AlphaWeightedAdd = 3, Mul = 4;
    public uint Mode = Replace;
    public uint AlphaChannel;
    public bool Clamp;
    public uint Source;
    public int NonserializedNumExtraChannels;
    public bool NonserializedIsPartialFrame;

    public void VisitFields(LjVisitor v)
    {
        v.U32(LjU32.Val(Replace), LjU32.Val(Add), LjU32.Val(Blend), LjU32.BitsOffset(2, 3), Replace, ref Mode);
        if (v.Conditional(NonserializedNumExtraChannels > 0 && (Mode == Blend || Mode == AlphaWeightedAdd)))
        {
            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.Val(2), LjU32.BitsOffset(3, 3), 0, ref AlphaChannel);
        }

        if (v.Conditional((NonserializedNumExtraChannels > 0 && (Mode == Blend || Mode == AlphaWeightedAdd)) || Mode == Mul))
        {
            v.Bool(false, ref Clamp);
        }

        if (v.Conditional(Mode != Replace || NonserializedIsPartialFrame))
        {
            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), 0, ref Source);
        }
    }
}

internal sealed class LjAnimationFrame : ILjBundle
{
    public uint Duration, Timecode;
    public LjImageMetadata? NonserializedMetadata;

    public void VisitFields(LjVisitor v)
    {
        if (v.Conditional(NonserializedMetadata != null && NonserializedMetadata.HaveAnimation))
        {
            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.BitsOnly(8), LjU32.BitsOnly(32), 0, ref Duration);
        }

        if (v.Conditional(NonserializedMetadata != null && NonserializedMetadata.Animation.HaveTimecodes))
        {
            v.Bits(32, 0, ref Timecode);
        }
    }
}

internal sealed class LjLoopFilter : ILjBundle
{
    public bool AllDefaultField = true;
    public bool Gab = true, GabCustom;
    public float GabXWeight1 = (float)(1.1 * 0.104699568f), GabXWeight2 = (float)(1.1 * 0.055680538f);
    public float GabYWeight1 = (float)(1.1 * 0.104699568f), GabYWeight2 = (float)(1.1 * 0.055680538f);
    public float GabBWeight1 = (float)(1.1 * 0.104699568f), GabBWeight2 = (float)(1.1 * 0.055680538f);
    public uint EpfIters = 2;
    public bool EpfSharpCustom;
    public float[] EpfSharpLut = DefaultSharpLut();
    public bool EpfWeightCustom;
    public float[] EpfChannelScale = [40.0f, 5.0f, 3.5f];
    public float EpfPass1Zeroflush = 0.45f, EpfPass2Zeroflush = 0.6f;
    public bool EpfSigmaCustom;
    public float EpfQuantMul = 0.46f, EpfPass0SigmaScale = 0.9f, EpfPass2SigmaScale = 6.5f, EpfBorderSadMul = 0.6666666666666666f;
    public float EpfSigmaForModular = 1.0f;
    public ulong Extensions;
    public bool NonserializedIsModular;

    private static float[] DefaultSharpLut()
    {
        var lut = new float[8];
        for (int i = 0; i < 8; i++)
        {
            lut[i] = i / 7f;
        }

        return lut;
    }

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        v.Bool(true, ref Gab);
        if (v.Conditional(Gab))
        {
            v.Bool(false, ref GabCustom);
            if (v.Conditional(GabCustom))
            {
                v.F16((float)(1.1 * 0.104699568f), ref GabXWeight1);
                v.F16((float)(1.1 * 0.055680538f), ref GabXWeight2);
                v.F16((float)(1.1 * 0.104699568f), ref GabYWeight1);
                v.F16((float)(1.1 * 0.055680538f), ref GabYWeight2);
                v.F16((float)(1.1 * 0.104699568f), ref GabBWeight1);
                v.F16((float)(1.1 * 0.055680538f), ref GabBWeight2);
            }
        }

        v.Bits(2, 2, ref EpfIters);
        if (v.Conditional(EpfIters > 0))
        {
            if (v.Conditional(!NonserializedIsModular))
            {
                v.Bool(false, ref EpfSharpCustom);
                if (v.Conditional(EpfSharpCustom))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        v.F16(i / 7f, ref EpfSharpLut[i]);
                    }
                }
            }

            v.Bool(false, ref EpfWeightCustom);
            if (v.Conditional(EpfWeightCustom))
            {
                v.F16(40.0f, ref EpfChannelScale[0]);
                v.F16(5.0f, ref EpfChannelScale[1]);
                v.F16(3.5f, ref EpfChannelScale[2]);
                v.F16(0.45f, ref EpfPass1Zeroflush);
                v.F16(0.6f, ref EpfPass2Zeroflush);
            }

            v.Bool(false, ref EpfSigmaCustom);
            if (v.Conditional(EpfSigmaCustom))
            {
                if (v.Conditional(!NonserializedIsModular))
                {
                    v.F16(0.46f, ref EpfQuantMul);
                }

                v.F16(0.9f, ref EpfPass0SigmaScale);
                v.F16(6.5f, ref EpfPass2SigmaScale);
                v.F16(0.6666666666666666f, ref EpfBorderSadMul);
            }

            if (v.Conditional(NonserializedIsModular))
            {
                v.F16(1.0f, ref EpfSigmaForModular);
            }
        }

        v.BeginExtensions(ref Extensions);
        v.EndExtensions();
    }
}

internal sealed class LjFrameHeader : ILjBundle
{
    public const uint RegularFrame = 0, DcFrame = 1, ReferenceOnly = 2, SkipProgressive = 3;
    public const uint EncVarDct = 0, EncModular = 1;
    public const uint CtXyb = 0, CtNone = 1, CtYCbCr = 2;
    public const ulong FlagNoise = 1, FlagPatches = 2, FlagSplines = 16, FlagUseDcFrame = 32, FlagSkipAdaptiveDcSmoothing = 128;

    public bool AllDefaultField = true;
    public uint FrameType = RegularFrame;
    public uint Encoding = EncVarDct;
    public ulong Flags;
    public uint ColorTransform = CtXyb;
    public LjChromaSubsampling ChromaSubsampling = new();
    public uint Upsampling = 1;
    public uint[] ExtraChannelUpsampling = Array.Empty<uint>();
    public uint GroupSizeShift = 1;
    public uint XQmScale = 3, BQmScale = 2;
    public LjPasses Passes = new();
    public uint DcLevel;
    public bool CustomSizeOrOrigin;
    public int FrameOriginX0, FrameOriginY0;
    public uint FrameXsize, FrameYsize;
    public LjBlendingInfo BlendingInfo = new();
    public LjBlendingInfo[] ExtraChannelBlendingInfo = Array.Empty<LjBlendingInfo>();
    public LjAnimationFrame AnimationFrame = new();
    public bool IsLast = true;
    public uint SaveAsReference;
    public bool SaveBeforeColorTransform;
    public string Name = string.Empty;
    public LjLoopFilter LoopFilter = new();
    public ulong Extensions;

    public LjImageMetadata? NonserializedMetadata;
    public uint NonserializedXsize, NonserializedYsize;   // image size (SizeHeader)
    public bool NonserializedIsPreview;

    private static readonly LjU32Enc OriginEnc = new(LjU32.BitsOnly(8), LjU32.BitsOffset(11, 256), LjU32.BitsOffset(14, 2304), LjU32.BitsOffset(30, 18688));

    public bool CanBeReferenced => !IsLast && FrameType != DcFrame && (AnimationFrame.Duration == 0 || SaveAsReference != 0);

    public void VisitFields(LjVisitor v)
    {
        if (v.AllDefault(this, ref AllDefaultField))
        {
            v.SetDefault(this);
            return;
        }

        v.U32(LjU32.Val(RegularFrame), LjU32.Val(DcFrame), LjU32.Val(ReferenceOnly), LjU32.Val(SkipProgressive), RegularFrame, ref FrameType);
        bool isModular = Encoding == EncModular;
        v.Bool(false, ref isModular);
        Encoding = isModular ? EncModular : EncVarDct;
        v.U64(0, ref Flags);

        bool xybEncoded = NonserializedMetadata == null || NonserializedMetadata.XybEncoded;
        if (xybEncoded)
        {
            ColorTransform = CtXyb;
        }
        else
        {
            bool alternate = ColorTransform == CtYCbCr;
            v.Bool(false, ref alternate);
            ColorTransform = alternate ? CtYCbCr : CtNone;
        }

        if (v.Conditional(ColorTransform == CtYCbCr && (Flags & FlagUseDcFrame) == 0))
        {
            v.VisitNested(ChromaSubsampling);
        }

        int numExtraChannels = NonserializedMetadata?.ExtraChannelInfo.Count ?? 0;
        if (v.Conditional((Flags & FlagUseDcFrame) == 0))
        {
            v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(4), LjU32.Val(8), 1, ref Upsampling);
            if (NonserializedMetadata != null && v.Conditional(numExtraChannels != 0))
            {
                if (ExtraChannelUpsampling.Length != numExtraChannels)
                {
                    var n = new uint[numExtraChannels];
                    for (int i = 0; i < numExtraChannels; i++)
                    {
                        n[i] = i < ExtraChannelUpsampling.Length ? ExtraChannelUpsampling[i] : 1;
                    }

                    ExtraChannelUpsampling = n;
                }

                for (int i = 0; i < numExtraChannels; i++)
                {
                    int dimShift = (int)NonserializedMetadata.ExtraChannelInfo[i].DimShift;
                    uint ec = ExtraChannelUpsampling[i] >> dimShift;
                    v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(4), LjU32.Val(8), 1, ref ec);
                    ExtraChannelUpsampling[i] = ec << dimShift;
                }
            }
            else
            {
                ExtraChannelUpsampling = Array.Empty<uint>();
            }
        }

        if (v.Conditional(Encoding == EncModular))
        {
            v.Bits(2, 1, ref GroupSizeShift);
        }

        if (v.Conditional(Encoding == EncVarDct && ColorTransform == CtXyb))
        {
            v.Bits(3, 3, ref XQmScale);
            v.Bits(3, 2, ref BQmScale);
        }
        else
        {
            XQmScale = BQmScale = 2;
        }

        if (v.Conditional(FrameType != ReferenceOnly))
        {
            v.VisitNested(Passes);
        }

        if (v.Conditional(FrameType == DcFrame))
        {
            v.U32(LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), LjU32.Val(4), 1, ref DcLevel);
        }

        if (FrameType != DcFrame)
        {
            DcLevel = 0;
        }

        bool isPartialFrame = false;
        if (v.Conditional(FrameType != DcFrame))
        {
            v.Bool(false, ref CustomSizeOrOrigin);
            if (v.Conditional(CustomSizeOrOrigin))
            {
                if (v.Conditional(FrameType == RegularFrame || FrameType == SkipProgressive))
                {
                    uint ux0 = LjMath.PackSigned(FrameOriginX0), uy0 = LjMath.PackSigned(FrameOriginY0);
                    v.U32(OriginEnc, 0, ref ux0);
                    v.U32(OriginEnc, 0, ref uy0);
                    FrameOriginX0 = LjHeaderUtil.UnpackSigned(ux0);
                    FrameOriginY0 = LjHeaderUtil.UnpackSigned(uy0);
                }

                v.U32(OriginEnc, 0, ref FrameXsize);
                v.U32(OriginEnc, 0, ref FrameYsize);
                long imageX = DefaultXsize, imageY = DefaultYsize;
                if (FrameType == RegularFrame || FrameType == SkipProgressive)
                {
                    isPartialFrame |= FrameOriginX0 > 0;
                    isPartialFrame |= FrameOriginY0 > 0;
                    isPartialFrame |= FrameXsize + (long)FrameOriginX0 < imageX;
                    isPartialFrame |= FrameYsize + (long)FrameOriginY0 < imageY;
                }
            }
        }

        if (v.Conditional(FrameType == RegularFrame || FrameType == SkipProgressive))
        {
            BlendingInfo.NonserializedNumExtraChannels = numExtraChannels;
            BlendingInfo.NonserializedIsPartialFrame = isPartialFrame;
            v.VisitNested(BlendingInfo);
            if (ExtraChannelBlendingInfo.Length != numExtraChannels)
            {
                var n = new LjBlendingInfo[numExtraChannels];
                for (int i = 0; i < numExtraChannels; i++)
                {
                    n[i] = i < ExtraChannelBlendingInfo.Length ? ExtraChannelBlendingInfo[i] : new LjBlendingInfo();
                }

                ExtraChannelBlendingInfo = n;
            }

            foreach (LjBlendingInfo ec in ExtraChannelBlendingInfo)
            {
                ec.NonserializedIsPartialFrame = isPartialFrame;
                ec.NonserializedNumExtraChannels = numExtraChannels;
                v.VisitNested(ec);
            }

            if (v.Conditional(NonserializedMetadata != null && NonserializedMetadata.HaveAnimation))
            {
                AnimationFrame.NonserializedMetadata = NonserializedMetadata;
                v.VisitNested(AnimationFrame);
            }

            v.Bool(true, ref IsLast);
        }
        else
        {
            IsLast = false;
        }

        if (v.Conditional(FrameType != DcFrame && !IsLast))
        {
            v.U32(LjU32.Val(0), LjU32.Val(1), LjU32.Val(2), LjU32.Val(3), 0, ref SaveAsReference);
        }

        if (FrameType != DcFrame)
        {
            if (v.Conditional(CanBeReferenced && BlendingInfo.Mode == LjBlendingInfo.Replace && !isPartialFrame &&
                              (FrameType == RegularFrame || FrameType == SkipProgressive)))
            {
                v.Bool(false, ref SaveBeforeColorTransform);
            }
            else if (v.Conditional(FrameType == ReferenceOnly))
            {
                v.Bool(true, ref SaveBeforeColorTransform);
            }
        }
        else
        {
            SaveBeforeColorTransform = true;
        }

        LjHeaderUtil.VisitNameString(v, ref Name);
        LoopFilter.NonserializedIsModular = isModular;
        v.VisitNested(LoopFilter);
        v.BeginExtensions(ref Extensions);
        v.EndExtensions();
    }

    // default_xsize / default_ysize: the image (or preview) size from the codestream headers.
    public long DefaultXsize => NonserializedXsize;

    public long DefaultYsize => NonserializedYsize;
}

internal static class LjHeaderUtil
{
    public static int UnpackSigned(uint v) => (int)((v >> 1) ^ (uint)-(int)(v & 1));

    public static void VisitNameString(LjVisitor v, ref string name)
    {
        uint len = (uint)name.Length;
        v.U32(LjU32.Val(0), LjU32.BitsOnly(4), LjU32.BitsOffset(5, 16), LjU32.BitsOffset(10, 48), 0, ref len);
        var chars = new char[len];
        for (int i = 0; i < len; i++)
        {
            uint c = i < name.Length ? (byte)name[i] : 0u;
            v.Bits(8, 0, ref c);
            chars[i] = (char)c;
        }

        if (v.IsReading)
        {
            name = new string(chars);
        }
    }
}

internal static class LjUpsamplingWeights
{
    public static readonly float[] K2 =
    [
        -0.01716200f, -0.03452303f, -0.04022174f, -0.02921014f, -0.00624645f,
        0.14111091f, 0.28896755f, 0.00278718f, -0.01610267f, 0.56661550f,
        0.03777607f, -0.01986694f, -0.03144731f, -0.01185068f, -0.00213539f,
    ];

    public static readonly float[] K4 =
    [
        -0.02419067f, -0.03491987f, -0.03693351f, -0.03094285f, -0.00529785f,
        -0.01663432f, -0.03556863f, -0.03888905f, -0.03516850f, -0.00989469f,
        0.23651958f, 0.33392945f, -0.01073543f, -0.01313181f, -0.03556694f,
        0.13048175f, 0.40103025f, 0.03951150f, -0.02077584f, 0.46914198f,
        -0.00209270f, -0.01484589f, -0.04064806f, 0.18942530f, 0.56279892f,
        0.06674400f, -0.02335494f, -0.03551682f, -0.00754830f, -0.02267919f,
        -0.02363578f, 0.00315804f, -0.03399098f, -0.01359519f, -0.00091653f,
        -0.00335467f, -0.01163294f, -0.01610294f, -0.00974088f, -0.00191622f,
        -0.01095446f, -0.03198464f, -0.04455121f, -0.02799790f, -0.00645912f,
        0.06390599f, 0.22963888f, 0.00630981f, -0.01897349f, 0.67537268f,
        0.08483369f, -0.02534994f, -0.02205197f, -0.01667999f, -0.00384443f,
    ];

    public static readonly float[] K8 =
    [
        -0.02928613f, -0.03706353f, -0.03783812f, -0.03324558f, -0.00447632f,
        -0.02519406f, -0.03752601f, -0.03901508f, -0.03663285f, -0.00646649f,
        -0.02066407f, -0.03838633f, -0.04002101f, -0.03900035f, -0.00901973f,
        -0.01626393f, -0.03954148f, -0.04046620f, -0.03979621f, -0.01224485f,
        0.29895328f, 0.35757708f, -0.02447552f, -0.01081748f, -0.04314594f,
        0.23903219f, 0.41119301f, -0.00573046f, -0.01450239f, -0.04246845f,
        0.17567618f, 0.45220643f, 0.02287757f, -0.01936783f, -0.03583255f,
        0.11572472f, 0.47416733f, 0.06284440f, -0.02685066f, 0.42720050f,
        -0.02248939f, -0.01155273f, -0.04562755f, 0.28689496f, 0.49093869f,
        -0.00007891f, -0.01545926f, -0.04562659f, 0.21238920f, 0.53980934f,
        0.03369474f, -0.02070211f, -0.03866988f, 0.14229550f, 0.56593398f,
        0.08045181f, -0.02888298f, -0.03680918f, -0.00542229f, -0.02920477f,
        -0.02788574f, -0.02118180f, -0.03942402f, -0.00775547f, -0.02433614f,
        -0.03193943f, -0.02030828f, -0.04044014f, -0.01074016f, -0.01930822f,
        -0.03620399f, -0.01974125f, -0.03919545f, -0.01456093f, -0.00045072f,
        -0.00360110f, -0.01020207f, -0.01231907f, -0.00638988f, -0.00071592f,
        -0.00279122f, -0.00957115f, -0.01288327f, -0.00730937f, -0.00107783f,
        -0.00210156f, -0.00890705f, -0.01317668f, -0.00813895f, -0.00153491f,
        -0.02128481f, -0.04173044f, -0.04831487f, -0.03293190f, -0.00525260f,
        -0.01720322f, -0.04052736f, -0.05045706f, -0.03607317f, -0.00738030f,
        -0.01341764f, -0.03965629f, -0.05151616f, -0.03814886f, -0.01005819f,
        0.18968273f, 0.33063684f, -0.01300105f, -0.01372950f, -0.04017465f,
        0.13727832f, 0.36402234f, 0.01027890f, -0.01832107f, -0.03365072f,
        0.08734506f, 0.38194295f, 0.04338228f, -0.02525993f, 0.56408126f,
        0.00458352f, -0.01648227f, -0.04887868f, 0.24585519f, 0.62026135f,
        0.04314807f, -0.02213737f, -0.04158014f, 0.16637289f, 0.65027023f,
        0.09621636f, -0.03101388f, -0.04082742f, -0.00904519f, -0.02790922f,
        -0.02117818f, 0.00798662f, -0.03995711f, -0.01243427f, -0.02231705f,
        -0.02946266f, 0.00992055f, -0.03600283f, -0.01684920f, -0.00111684f,
        -0.00411204f, -0.01297130f, -0.01723725f, -0.01022545f, -0.00165306f,
        -0.00313110f, -0.01218016f, -0.01763266f, -0.01125620f, -0.00231663f,
        -0.01374149f, -0.03797620f, -0.05142937f, -0.03117307f, -0.00581914f,
        -0.01064003f, -0.03608089f, -0.05272168f, -0.03375670f, -0.00795586f,
        0.09628104f, 0.27129991f, -0.00353779f, -0.01734151f, -0.03153981f,
        0.05686230f, 0.28500998f, 0.02230594f, -0.02374955f, 0.68214326f,
        0.05018048f, -0.02320852f, -0.04383616f, 0.18459474f, 0.71517975f,
        0.10805613f, -0.03263677f, -0.03637639f, -0.01394373f, -0.02511203f,
        -0.01728636f, 0.05407331f, -0.02867568f, -0.01893131f, -0.00240854f,
        -0.00446511f, -0.01636187f, -0.02377053f, -0.01522848f, -0.00333334f,
        -0.00819975f, -0.02964169f, -0.04499287f, -0.02745350f, -0.00612408f,
        0.02727416f, 0.19446600f, 0.00159832f, -0.02232473f, 0.74982506f,
        0.11452620f, -0.03348048f, -0.01605681f, -0.02070339f, -0.00458223f,
    ];
}
