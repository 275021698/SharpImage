// libjxl 0.12 frame blending (blending.cc, alpha.cc, render_pipeline/stage_blending.cc) and spot colours
// (render_pipeline/stage_spot.cc). Scalar libjxl code: plain float arithmetic, no fused multiply-adds.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjAlpha
{
    public const int PbNone = 0, PbReplace = 1, PbAdd = 2, PbMul = 3, PbBlendAbove = 4, PbBlendBelow = 5, PbAlphaWeightedAddAbove = 6,
        PbAlphaWeightedAddBelow = 7;

    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <summary>PerformAlphaBlending for a single channel (bg/fg channel, their alphas). Same arrays = the alpha itself.</summary>
    public static void AlphaBlend1(float[] bg, int bo, float[] bga, int bao, float[] fg, int fo, float[] fga, int fao, float[] o, int oo, int n,
        bool isAlphaItself, bool premultiplied, bool clamp)
    {
        if (isAlphaItself)
        {
            for (int x = 0; x < n; ++x)
            {
                float fa = clamp ? Clamp01(fga[fao + x]) : fga[fao + x];
                o[oo + x] = 1.0f - ((1.0f - fa) * (1.0f - bga[bao + x]));
            }
        }
        else if (premultiplied)
        {
            for (int x = 0; x < n; ++x)
            {
                float fa = clamp ? Clamp01(fga[fao + x]) : fga[fao + x];
                o[oo + x] = fg[fo + x] + (bg[bo + x] * (1.0f - fa));
            }
        }
        else
        {
            for (int x = 0; x < n; ++x)
            {
                float fa = clamp ? Clamp01(fga[fao + x]) : fga[fao + x];
                float newA = 1.0f - ((1.0f - fa) * (1.0f - bga[bao + x]));
                float rnewA = newA > 0 ? 1.0f / newA : 0.0f;
                o[oo + x] = ((fg[fo + x] * fa) + (bg[bo + x] * bga[bao + x] * (1.0f - fa))) * rnewA;
            }
        }
    }

    public static void AlphaWeightedAdd(float[] bg, int bo, float[] fg, int fo, float[] fga, int fao, float[] o, int oo, int n, bool fgIsAlpha, bool clamp)
    {
        if (fgIsAlpha)
        {
            Array.Copy(bg, bo, o, oo, n);
        }
        else if (clamp)
        {
            for (int x = 0; x < n; ++x)
            {
                o[oo + x] = bg[bo + x] + (fg[fo + x] * Clamp01(fga[fao + x]));
            }
        }
        else
        {
            for (int x = 0; x < n; ++x)
            {
                o[oo + x] = bg[bo + x] + (fg[fo + x] * fga[fao + x]);
            }
        }
    }

    public static void MulBlend(float[] bg, int bo, float[] fg, int fo, float[] o, int oo, int n, bool clamp)
    {
        for (int x = 0; x < n; ++x)
        {
            o[oo + x] = bg[bo + x] * (clamp ? Clamp01(fg[fo + x]) : fg[fo + x]);
        }
    }

    public struct Blending
    {
        public int Mode;
        public int AlphaChannel;
        public bool Clamp;
    }

    /// <summary>PerformBlending: bg/fg rows per channel (3 + num_ec), written to out.</summary>
    public static void PerformBlending((float[] A, int O)[] bg, (float[] A, int O)[] fg, (float[] A, int O)[] output, int xsize, Blending color,
        Blending[] ec, List<LjExtraChannelInfo> eci)
    {
        bool hasAlpha = false;
        int numEc = eci.Count;
        for (int i = 0; i < numEc; i++)
        {
            if (eci[i].Type == LjExtraChannelInfo.TypeAlpha)
            {
                hasAlpha = true;
                break;
            }
        }

        var tmp = new float[3 + numEc][];
        for (int i = 0; i < tmp.Length; i++)
        {
            tmp[i] = new float[Math.Max(1, xsize)];
        }

        for (int i = 0; i < numEc; i++)
        {
            int a = ec[i].AlphaChannel;
            switch (ec[i].Mode)
            {
                case PbAdd:
                    for (int x = 0; x < xsize; x++)
                    {
                        tmp[3 + i][x] = bg[3 + i].A[bg[3 + i].O + x] + fg[3 + i].A[fg[3 + i].O + x];
                    }

                    break;
                case PbBlendAbove:
                    AlphaBlend1(bg[3 + i].A, bg[3 + i].O, bg[3 + a].A, bg[3 + a].O, fg[3 + i].A, fg[3 + i].O, fg[3 + a].A, fg[3 + a].O, tmp[3 + i], 0,
                        xsize, a == i, eci[a].AlphaAssociated, ec[i].Clamp);
                    break;
                case PbBlendBelow:
                    AlphaBlend1(fg[3 + i].A, fg[3 + i].O, fg[3 + a].A, fg[3 + a].O, bg[3 + i].A, bg[3 + i].O, bg[3 + a].A, bg[3 + a].O, tmp[3 + i], 0,
                        xsize, a == i, eci[a].AlphaAssociated, ec[i].Clamp);
                    break;
                case PbAlphaWeightedAddAbove:
                    AlphaWeightedAdd(bg[3 + i].A, bg[3 + i].O, fg[3 + i].A, fg[3 + i].O, fg[3 + a].A, fg[3 + a].O, tmp[3 + i], 0, xsize, a == i, ec[i].Clamp);
                    break;
                case PbAlphaWeightedAddBelow:
                    AlphaWeightedAdd(fg[3 + i].A, fg[3 + i].O, bg[3 + i].A, bg[3 + i].O, bg[3 + a].A, bg[3 + a].O, tmp[3 + i], 0, xsize, a == i, ec[i].Clamp);
                    break;
                case PbMul:
                    MulBlend(bg[3 + i].A, bg[3 + i].O, fg[3 + i].A, fg[3 + i].O, tmp[3 + i], 0, xsize, ec[i].Clamp);
                    break;
                case PbReplace:
                    Array.Copy(fg[3 + i].A, fg[3 + i].O, tmp[3 + i], 0, xsize);
                    break;
                default:
                    Array.Copy(bg[3 + i].A, bg[3 + i].O, tmp[3 + i], 0, xsize);
                    break;
            }
        }

        int alpha = color.AlphaChannel;
        void Add()
        {
            for (int p = 0; p < 3; p++)
            {
                for (int x = 0; x < xsize; x++)
                {
                    tmp[p][x] = bg[p].A[bg[p].O + x] + fg[p].A[fg[p].O + x];
                }
            }
        }

        void BlendWeighted((float[] A, int O)[] bottom, (float[] A, int O)[] top)
        {
            bool pre = eci[alpha].AlphaAssociated;
            float[] ba = bottom[3 + alpha].A, ta = top[3 + alpha].A;
            int bao = bottom[3 + alpha].O, tao = top[3 + alpha].O;
            for (int x = 0; x < xsize; ++x)
            {
                float fga = color.Clamp ? Clamp01(ta[tao + x]) : ta[tao + x];
                float bgA = ba[bao + x];
                if (pre)
                {
                    for (int p = 0; p < 3; p++)
                    {
                        tmp[p][x] = top[p].A[top[p].O + x] + (bottom[p].A[bottom[p].O + x] * (1.0f - fga));
                    }

                    tmp[3 + alpha][x] = 1.0f - ((1.0f - fga) * (1.0f - bgA));
                }
                else
                {
                    float newA = 1.0f - ((1.0f - fga) * (1.0f - bgA));
                    float rnewA = newA > 0 ? 1.0f / newA : 0.0f;
                    for (int p = 0; p < 3; p++)
                    {
                        tmp[p][x] = ((top[p].A[top[p].O + x] * fga) + (bottom[p].A[bottom[p].O + x] * bgA * (1.0f - fga))) * rnewA;
                    }

                    tmp[3 + alpha][x] = newA;
                }
            }
        }

        void AddWeighted((float[] A, int O)[] bottom, (float[] A, int O)[] top)
        {
            for (int c = 0; c < 3; c++)
            {
                AlphaWeightedAdd(bottom[c].A, bottom[c].O, top[c].A, top[c].O, top[3 + alpha].A, top[3 + alpha].O, tmp[c], 0, xsize, false, color.Clamp);
            }
        }

        void Copy((float[] A, int O)[] src)
        {
            for (int p = 0; p < 3; p++)
            {
                Array.Copy(src[p].A, src[p].O, tmp[p], 0, xsize);
            }
        }

        switch (color.Mode)
        {
            case PbAdd:
                Add();
                break;
            case PbAlphaWeightedAddAbove:
                if (hasAlpha)
                {
                    AddWeighted(bg, fg);
                }
                else
                {
                    Add();
                }

                break;
            case PbAlphaWeightedAddBelow:
                if (hasAlpha)
                {
                    AddWeighted(fg, bg);
                }
                else
                {
                    Add();
                }

                break;
            case PbBlendAbove:
                if (hasAlpha)
                {
                    BlendWeighted(bg, fg);
                }
                else
                {
                    Copy(fg);
                }

                break;
            case PbBlendBelow:
                if (hasAlpha)
                {
                    BlendWeighted(fg, bg);
                }
                else
                {
                    Copy(fg);
                }

                break;
            case PbMul:
                for (int p = 0; p < 3; p++)
                {
                    MulBlend(bg[p].A, bg[p].O, fg[p].A, fg[p].O, tmp[p], 0, xsize, color.Clamp);
                }

                break;
            case PbReplace:
                Copy(fg);
                break;
            default:
                Copy(bg);
                break;
        }

        for (int i = 0; i < 3 + numEc; i++)
        {
            Array.Copy(tmp[i], 0, output[i].A, output[i].O, xsize);
        }
    }
}

/// <summary>BlendingStage: composites the frame onto its reference frame and switches to image dimensions.</summary>
internal sealed class LjBlendingStage : LjStage
{
    private readonly LjFrameHeader fh;
    private readonly LjDecoderState st;
    private readonly LjReferenceFrame bg;
    private readonly int imageXsize, imageYsize;
    private readonly LjAlpha.Blending[] blending;
    private readonly float[] zeroes;

    public LjBlendingStage(LjFrameHeader fh, LjDecoderState st, LjOutputEncoding oe)
    {
        this.fh = fh;
        this.st = st;
        imageXsize = st.ImageXsize;
        imageYsize = st.ImageYsize;
        bg = st.RefFrames[fh.BlendingInfo.Source];
        zeroes = new float[imageXsize];
        if (!bg.Empty && bg.IbIsInXyb)
        {
            throw LjDec.Fail("Trying to blend XYB reference frame and non-XYB frame");
        }

        void Verify(LjReferenceFrame r)
        {
            if (!r.Empty && (r.Planes![0].XSize < imageXsize || r.Planes[0].YSize < imageYsize))
            {
                throw LjDec.Fail("Trying to use a crop as a background");
            }
        }

        Verify(bg);
        foreach (LjBlendingInfo info in fh.ExtraChannelBlendingInfo)
        {
            Verify(st.RefFrames[info.Source]);
        }

        if (st.Meta.XybEncoded && !oe.ColorEncodingIsOriginal)
        {
            throw LjDec.Fail("Blending in unsupported color space");
        }

        blending = new LjAlpha.Blending[fh.ExtraChannelBlendingInfo.Length + 1];
        blending[0] = Make(fh.BlendingInfo);
        for (int i = 0; i < fh.ExtraChannelBlendingInfo.Length; i++)
        {
            blending[1 + i] = Make(fh.ExtraChannelBlendingInfo[i]);
        }
    }

    private static LjAlpha.Blending Make(LjBlendingInfo info) => new()
    {
        AlphaChannel = (int)info.AlphaChannel,
        Clamp = info.Clamp,
        Mode = info.Mode switch
        {
            LjBlendingInfo.Replace => LjAlpha.PbReplace,
            LjBlendingInfo.Add => LjAlpha.PbAdd,
            LjBlendingInfo.Mul => LjAlpha.PbMul,
            LjBlendingInfo.Blend => LjAlpha.PbBlendAbove,
            _ => LjAlpha.PbAlphaWeightedAddAbove,
        },
    };

    public override LjChannelMode GetChannelMode(int c) => LjChannelMode.InPlace;

    public override bool SwitchToImageDimensions => true;

    public override void GetImageDimensions(out int xsize, out int ysize, out int x0, out int y0)
    {
        xsize = imageXsize;
        ysize = imageYsize;
        x0 = fh.FrameOriginX0;
        y0 = fh.FrameOriginY0;
    }

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        int bgXpos = fh.FrameOriginX0 + xpos;
        int bgYpos = fh.FrameOriginY0 + ypos;
        int offset = 0;
        if (bgXpos + xsize <= 0 || fh.FrameOriginX0 >= imageXsize || bgYpos < 0 || bgYpos >= imageYsize)
        {
            return;
        }

        if (bgXpos < 0)
        {
            offset -= bgXpos;
            xsize += bgXpos;
            bgXpos = 0;
        }

        if (bgXpos + xsize > imageXsize)
        {
            xsize = Math.Max(0, imageXsize - bgXpos);
        }

        int numC = Math.Min(input.Arr.Length, st.Meta.ExtraChannelInfo.Count + 3);
        var bgRows = new (float[] A, int O)[numC];
        var fgRows = new (float[] A, int O)[numC];
        for (int c = 0; c < numC; ++c)
        {
            (float[] a, int o) = In(input, c, 0);
            fgRows[c] = (a, o + offset);
            LjReferenceFrame src = c < 3 ? bg : st.RefFrames[fh.ExtraChannelBlendingInfo[c - 3].Source];
            if (src.Empty)
            {
                bgRows[c] = (zeroes, 0);
            }
            else
            {
                LjImageF p = src.Planes![c];
                bgRows[c] = (p.Data, p.Row(bgYpos) + bgXpos);
            }
        }

        LjAlpha.PerformBlending(bgRows, fgRows, fgRows, xsize, blending[0], blending[1..], st.Meta.ExtraChannelInfo);
    }

    public override void ProcessPaddingRow(LjStageRows output, int xsize, int xpos, int ypos)
    {
        for (int c = 0; c < 3; ++c)
        {
            (float[] a, int o) = (output.Arr[c][0], output.Off[c][0]);
            if (bg.Empty)
            {
                Array.Clear(a, o, xsize);
            }
            else
            {
                LjImageF p = bg.Planes![c];
                Array.Copy(p.Data, p.Row(ypos) + xpos, a, o, xsize);
            }
        }

        for (int ec = 0; ec < st.Meta.ExtraChannelInfo.Count; ++ec)
        {
            LjReferenceFrame ecBg = st.RefFrames[fh.ExtraChannelBlendingInfo[ec].Source];
            (float[] a, int o) = (output.Arr[3 + ec][0], output.Off[3 + ec][0]);
            if (ecBg.Empty)
            {
                Array.Clear(a, o, xsize);
            }
            else
            {
                LjImageF p = ecBg.Planes![3 + ec];
                Array.Copy(p.Data, p.Row(ypos) + xpos, a, o, xsize);
            }
        }
    }
}

internal sealed class LjSpotColorStage : LjStage
{
    private readonly int spotC;
    private readonly float[] spotColor;

    public LjSpotColorStage(int spotC, float[] spotColor)
    {
        this.spotC = spotC;
        this.spotColor = spotColor;
    }

    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InPlace : c == spotC ? LjChannelMode.Input : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        float scale = spotColor[3];
        (float[] s, int so) = In(input, spotC, 0);
        for (int c = 0; c < 3; c++)
        {
            (float[] p, int po) = In(input, c, 0);
            for (int x = 0; x < xsize; x++)
            {
                float mix = scale * s[so + x];
                p[po + x] = (mix * spotColor[c]) + ((1.0f - mix) * p[po + x]);
            }
        }
    }
}
