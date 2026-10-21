// libjxl 0.12 render pipeline (render_pipeline.cc + simple_render_pipeline.cc): a frame's channels pass through a
// list of stages; each stage reads rows with mirrored borders and writes in place or into new (possibly upsampled)
// buffers. This is the full-frame "simple" pipeline libjxl uses as its reference; its output equals the low-memory
// pipeline's.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal enum LjChannelMode
{
    Ignored = 0,
    InPlace = 1,
    InOut = 2,
    Input = 3,
}

/// <summary>Row pointers handed to a stage: per channel, per row offset (-border..border or output row index).</summary>
internal sealed class LjStageRows
{
    public float[][][] Arr;
    public int[][] Off;

    public LjStageRows(int numC)
    {
        Arr = new float[numC][][];
        Off = new int[numC][];
    }
}

internal abstract class LjStage
{
    public const int XOffset = 32;   // kRenderPipelineXOffset
    public int BorderX, BorderY, ShiftX, ShiftY;

    public abstract LjChannelMode GetChannelMode(int c);

    public virtual void SetInputSizes(int[] xs, int[] ys)
    {
    }

    /// <summary>Processes row <paramref name="ypos"/>; <paramref name="xsize"/> pixels starting at x = 0.</summary>
    public abstract void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos);

    public virtual bool SwitchToImageDimensions => false;

    public virtual void GetImageDimensions(out int xsize, out int ysize, out int x0, out int y0) => throw new NotSupportedException();

    public virtual void ProcessPaddingRow(LjStageRows output, int xsize, int xpos, int ypos)
    {
    }

    /// <summary>Input row of channel <paramref name="c"/> at vertical offset <paramref name="offset"/>: (array, index of x = 0).</summary>
    protected (float[] A, int O) In(LjStageRows rows, int c, int offset) => (rows.Arr[c][BorderY + offset], rows.Off[c][BorderY + offset]);

    protected (float[] A, int O) Out(LjStageRows rows, int c, int offset) => (rows.Arr[c][offset], rows.Off[c][offset]);
}

internal sealed class LjRenderPipeline
{
    private readonly List<LjStage> stages = new();
    private readonly int numC;
    private (int X, int Y)[][] channelShifts = Array.Empty<(int, int)[]>();
    private LjFrameDims fd = new();
    private LjImageF[] channelData = Array.Empty<LjImageF>();
    private const int Xo = LjStage.XOffset;

    public LjRenderPipeline(int numC) => this.numC = numC;

    public int NumChannels => numC;

    public void AddStage(LjStage s) => stages.Add(s);

    public IReadOnlyList<LjStage> Stages => stages;

    public void Finalize(LjFrameDims frameDims)
    {
        if (stages.Count == 0)
        {
            throw LjDec.Fail("empty render pipeline");
        }

        fd = frameDims;
        channelShifts = new (int, int)[stages.Count][];
        channelShifts[0] = new (int, int)[numC];
        for (int i = 1; i < stages.Count; i++)
        {
            LjStage st = stages[i - 1];
            for (int c = 0; c < numC; c++)
            {
                if (st.GetChannelMode(c) == LjChannelMode.InOut)
                {
                    channelShifts[0][c].X += st.ShiftX;
                    channelShifts[0][c].Y += st.ShiftY;
                }
            }
        }

        for (int i = 1; i < stages.Count; i++)
        {
            LjStage st = stages[i - 1];
            channelShifts[i] = new (int, int)[numC];
            for (int c = 0; c < numC; c++)
            {
                channelShifts[i][c] = st.GetChannelMode(c) == LjChannelMode.InOut
                    ? (channelShifts[i - 1][c].X - st.ShiftX, channelShifts[i - 1][c].Y - st.ShiftY)
                    : channelShifts[i - 1][c];
            }
        }

        channelData = new LjImageF[numC];
        for (int c = 0; c < numC; c++)
        {
            int w = LjDec.DivCeil(fd.XsizeUpsampled, 1 << channelShifts[0][c].X) + (Xo * 2);
            int h = LjDec.DivCeil(fd.YsizeUpsampled, 1 << channelShifts[0][c].Y) + (Xo * 2);
            channelData[c] = new LjImageF(w, h);
        }
    }

    /// <summary>SimpleRenderPipeline::MakeChannelRect: where group <paramref name="group"/> of channel <paramref name="c"/> goes.</summary>
    public (LjImageF Img, LjRect Rect) GetInputBuffer(int group, int c)
    {
        int baseColorShift = LjDec.CeilLog2Nonzero(fd.XsizeUpsampledPadded / fd.XsizePadded);
        int gx = group % fd.XsizeGroups, gy = group / fd.XsizeGroups;
        int xgroupdim = (fd.GroupDim << baseColorShift) >> channelShifts[0][c].X;
        int ygroupdim = (fd.GroupDim << baseColorShift) >> channelShifts[0][c].Y;
        var r = new LjRect(Xo + (gx * xgroupdim), Xo + (gy * ygroupdim), xgroupdim, ygroupdim,
            Xo + LjDec.DivCeil(fd.XsizeUpsampled, 1 << channelShifts[0][c].X), Xo + LjDec.DivCeil(fd.YsizeUpsampled, 1 << channelShifts[0][c].Y));
        return (channelData[c], r);
    }

    public static int Mirror(int x, int xsize)
    {
        while (x < 0 || x >= xsize)
        {
            x = x < 0 ? -x - 1 : (2 * xsize) - 1 - x;
        }

        return x;
    }

    /// <summary>SimpleRenderPipeline::ProcessBuffers once all input is present: runs every stage over the frame.</summary>
    public void Run()
    {
        for (int stageId = 0; stageId < stages.Count; stageId++)
        {
            LjStage stage = stages[stageId];
            var newChannels = new LjImageF?[numC];
            var inX = new int[numC];
            var inY = new int[numC];
            for (int c = 0; c < numC; c++)
            {
                inX[c] = channelData[c].XSize - (Xo * 2);
                inY[c] = channelData[c].YSize - (Xo * 2);
            }

            for (int c = 0; c < numC; c++)
            {
                if (stage.GetChannelMode(c) != LjChannelMode.InOut)
                {
                    continue;
                }

                int w = (inX[c] << stage.ShiftX) + (Xo * 2);
                int h = (inY[c] << stage.ShiftY) + (Xo * 2);
                var img = new LjImageF(w + 64, h);
                img.ShrinkTo(w, h);
                newChannels[c] = img;
            }

            // Mirrored borders for kInOut channels.
            for (int c = 0; c < numC; c++)
            {
                if (stage.GetChannelMode(c) != LjChannelMode.InOut)
                {
                    continue;
                }

                LjImageF img = channelData[c];
                int xs = inX[c], ys = inY[c];
                for (int y = 0; y < ys; y++)
                {
                    int row = img.Row(Xo + y) + Xo;
                    for (int ix = 0; ix < stage.BorderX; ix++)
                    {
                        img.Data[row - ix - 1] = img.Data[row + Mirror(-ix - 1, xs)];
                    }

                    for (int ix = 0; ix < stage.BorderX; ix++)
                    {
                        img.Data[row + ix + xs] = img.Data[row + Mirror(ix + xs, xs)];
                    }
                }

                for (int y = 0; y < stage.BorderY; y++)
                {
                    int dst = img.Row(Xo - y - 1) + Xo - stage.BorderX;
                    int src = img.Row(Xo + Mirror(-y - 1, ys)) + Xo - stage.BorderX;
                    Array.Copy(img.Data, src, img.Data, dst, xs + (2 * stage.BorderX));
                }

                for (int y = 0; y < stage.BorderY; y++)
                {
                    int dst = img.Row(Xo + ys + y) + Xo - stage.BorderX;
                    int src = img.Row(Xo + Mirror(ys + y, ys)) + Xo - stage.BorderX;
                    Array.Copy(img.Data, src, img.Data, dst, xs + (2 * stage.BorderX));
                }
            }

            int ysize = 0, xsize = 0;
            for (int c = 0; c < numC; c++)
            {
                if (stage.GetChannelMode(c) == LjChannelMode.Ignored)
                {
                    continue;
                }

                ysize = Math.Max(inY[c], ysize);
                xsize = Math.Max(inX[c], xsize);
            }

            if (ysize == 0 || xsize == 0)
            {
                throw LjDec.Fail("empty stage input");
            }

            stage.SetInputSizes(inX, inY);
            int borderY = stage.BorderY;
            const int rowsPerTask = 16;
            LjFrameDecoder.RunParallel((ysize + rowsPerTask - 1) / rowsPerTask, task =>
            {
            var inputRows = new LjStageRows(numC);
            var outputRows = new LjStageRows(numC);
            for (int c = 0; c < numC; c++)
            {
                inputRows.Arr[c] = new float[(2 * stage.BorderY) + 1][];
                inputRows.Off[c] = new int[(2 * stage.BorderY) + 1];
                outputRows.Arr[c] = new float[1 << stage.ShiftY][];
                outputRows.Off[c] = new int[1 << stage.ShiftY];
            }

            for (int y = task * rowsPerTask; y < Math.Min(ysize, (task + 1) * rowsPerTask); y++)
            {
                for (int c = 0; c < numC; c++)
                {
                    if (stage.GetChannelMode(c) == LjChannelMode.Ignored)
                    {
                        continue;
                    }

                    LjImageF img = channelData[c];
                    for (int iy = -borderY; iy <= borderY; iy++)
                    {
                        inputRows.Arr[c][iy + borderY] = img.Data;
                        inputRows.Off[c][iy + borderY] = img.Row(y + Xo + iy) + Xo;
                    }
                }

                for (int c = 0; c < numC; c++)
                {
                    LjImageF? o = newChannels[c];
                    if (o == null)
                    {
                        continue;
                    }

                    for (int iy = 0; iy < (1 << stage.ShiftY); iy++)
                    {
                        outputRows.Arr[c][iy] = o.Data;
                        outputRows.Off[c][iy] = o.Row((y << stage.ShiftY) + iy + Xo) + Xo;
                    }
                }

                stage.ProcessRow(inputRows, outputRows, xsize, 0, y);
            }
            });

            for (int c = 0; c < numC; c++)
            {
                if (newChannels[c] != null)
                {
                    channelData[c] = newChannels[c]!;
                }
            }

            for (int c = 0; c < numC; c++)
            {
                int nextStage = Math.Min(stageId + 1, channelShifts.Length - 1);
                int cx = LjDec.DivCeil(fd.XsizeUpsampled, 1 << channelShifts[nextStage][c].X);
                int cy = LjDec.DivCeil(fd.YsizeUpsampled, 1 << channelShifts[nextStage][c].Y);
                channelData[c].ShrinkTo(cx + (2 * Xo), cy + (2 * Xo));
            }

            LjProf.Mark("  stage " + stage.GetType().Name);
            if (stage.SwitchToImageDimensions)
            {
                stage.GetImageDimensions(out int imageXsize, out int imageYsize, out int x0, out int y0);
                var nfd = new LjFrameDims();
                nfd.Set(imageXsize, imageYsize, 0, 0, 0, false, 1);
                fd = nfd;
                LjImageF[] old = channelData;
                channelData = new LjImageF[numC];
                for (int c = 0; c < numC; c++)
                {
                    channelData[c] = new LjImageF((2 * Xo) + imageXsize, (2 * Xo) + imageYsize);
                }

                var padRows = new LjStageRows(numC);
                for (int c = 0; c < numC; c++)
                {
                    padRows.Arr[c] = new float[1][];
                    padRows.Off[c] = new int[1];
                }

                for (int y = 0; y < imageYsize; ++y)
                {
                    for (int c = 0; c < numC; c++)
                    {
                        padRows.Arr[c][0] = channelData[c].Data;
                        padRows.Off[c][0] = channelData[c].Row(Xo + y) + Xo;
                    }

                    stage.ProcessPaddingRow(padRows, imageXsize, 0, y);
                }

                int x0Fg = 0, y0Fg = 0;
                if (x0 < 0)
                {
                    xsize += x0;
                    x0Fg -= x0;
                    x0 = 0;
                }

                if (x0 + xsize > imageXsize)
                {
                    xsize = imageXsize - x0;
                }

                if (y0 < 0)
                {
                    ysize += y0;
                    y0Fg -= y0;
                    y0 = 0;
                }

                if (y0 + ysize > imageYsize)
                {
                    ysize = imageYsize - y0;
                }

                for (int c = 0; c < numC; c++)
                {
                    for (int y = 0; y < ysize; y++)
                    {
                        Array.Copy(old[c].Data, old[c].Row(Xo + y0Fg + y) + Xo + x0Fg, channelData[c].Data, channelData[c].Row(Xo + y0 + y) + Xo + x0, Math.Max(0, xsize));
                    }
                }
            }
        }
    }
}

// ====================================================================== stages

internal sealed class LjChromaUpsamplingStage : LjStage
{
    private readonly int c;
    private readonly bool horizontal;

    public LjChromaUpsamplingStage(int channel, bool horizontal)
    {
        c = channel;
        this.horizontal = horizontal;
        if (horizontal)
        {
            ShiftX = 1;
            BorderX = 1;
        }
        else
        {
            ShiftY = 1;
            BorderY = 1;
        }
    }

    public override LjChannelMode GetChannelMode(int ch) => ch == c ? LjChannelMode.InOut : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        if (horizontal)
        {
            (float[] ia, int io) = In(input, c, 0);
            (float[] oa, int oo) = Out(output, c, 0);
            for (int x = 0; x < xsize; x++)
            {
                float current = ia[io + x] * 0.75f;
                float prev = ia[io + x - 1];
                float next = ia[io + x + 1];
                oa[oo + (2 * x)] = MathF.FusedMultiplyAdd(0.25f, prev, current);
                oa[oo + (2 * x) + 1] = MathF.FusedMultiplyAdd(0.25f, next, current);
            }
        }
        else
        {
            (float[] ta, int to) = In(input, c, -1);
            (float[] ma, int mo) = In(input, c, 0);
            (float[] ba, int bo) = In(input, c, 1);
            (float[] o0, int oo0) = Out(output, c, 0);
            (float[] o1, int oo1) = Out(output, c, 1);
            for (int x = 0; x < xsize; x++)
            {
                float imScaled = ma[mo + x] * 0.75f;
                o0[oo0 + x] = MathF.FusedMultiplyAdd(ta[to + x], 0.25f, imScaled);
                o1[oo1 + x] = MathF.FusedMultiplyAdd(ba[bo + x], 0.25f, imScaled);
            }
        }
    }
}

internal sealed class LjGaborishStage : LjStage
{
    private readonly float[] weights = new float[9];

    public LjGaborishStage(LjLoopFilter lf)
    {
        BorderX = BorderY = 1;
        weights[0] = 1;
        weights[1] = lf.GabXWeight1;
        weights[2] = lf.GabXWeight2;
        weights[3] = 1;
        weights[4] = lf.GabYWeight1;
        weights[5] = lf.GabYWeight2;
        weights[6] = 1;
        weights[7] = lf.GabBWeight1;
        weights[8] = lf.GabBWeight2;
        for (int c = 0; c < 3; c++)
        {
            float div = weights[3 * c] + (4 * (weights[(3 * c) + 1] + weights[(3 * c) + 2]));
            float mul = 1.0f / div;
            weights[3 * c] *= mul;
            weights[(3 * c) + 1] *= mul;
            weights[(3 * c) + 2] *= mul;
        }
    }

    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InOut : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        for (int c = 0; c < 3; c++)
        {
            (float[] t, int to) = In(input, c, -1);
            (float[] m, int mo) = In(input, c, 0);
            (float[] b, int bo) = In(input, c, 1);
            (float[] o, int oo) = Out(output, c, 0);
            float w0 = weights[3 * c], w1 = weights[(3 * c) + 1], w2 = weights[(3 * c) + 2];
            if (LjSimd.Enabled)
            {
                LjSimd.GaborishRow(t, to, m, mo, b, bo, o, oo, xsize, w0, w1, w2);
                continue;
            }

            for (int x = 0; x < xsize; x++)
            {
                float sum0 = m[mo + x];
                float sum1 = (m[mo + x - 1] + m[mo + x + 1]) + (t[to + x] + b[bo + x]);
                float sum2 = (t[to + x - 1] + t[to + x + 1]) + (b[bo + x - 1] + b[bo + x + 1]);
                o[oo + x] = MathF.FusedMultiplyAdd(sum2, w2, MathF.FusedMultiplyAdd(sum1, w1, sum0 * w0));
            }
        }
    }
}

/// <summary>EPF0/1/2 stages (stage_epf.cc).</summary>
internal sealed class LjEpfStage : LjStage
{
    private const int SigmaPadding = 2;
    private const float MinSigma = -3.90524291751269967465540850526868f;
    private readonly int pass;
    private readonly LjLoopFilter lf;
    private readonly LjImageF sigma;

    private static readonly (int Dy, int Dx)[] SadsOff = [(-2, 0), (-1, -1), (-1, 0), (-1, 1), (0, -2), (0, -1), (0, 1), (0, 2), (1, -1), (1, 0), (1, 1), (2, 0)];
    private static readonly (int Dy, int Dx)[] PlusOff = [(0, 0), (-1, 0), (0, -1), (1, 0), (0, 1)];

    public LjEpfStage(int pass, LjLoopFilter lf, LjImageF sigma)
    {
        this.pass = pass;
        this.lf = lf;
        this.sigma = sigma;
        BorderX = BorderY = pass == 0 ? 3 : pass == 1 ? 2 : 1;
    }

    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InOut : LjChannelMode.Ignored;

    private static float Weight(float sad, float invSigma)
    {
        float v = MathF.FusedMultiplyAdd(sad, invSigma, 1.0f);
        return v < 0 ? 0 : v;
    }

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        int rowSigma = sigma.Row((ypos / 8) + SigmaPadding);
        float sm = pass == 0 ? (float)(lf.EpfPass0SigmaScale * 1.65) : pass == 1 ? 1.65f : (float)(lf.EpfPass2SigmaScale * 1.65);
        float bsm = sm * lf.EpfBorderSadMul;
        bool borderRow = ypos % 8 == 0 || ypos % 8 == 7;
        int br = BorderY;
        var ra = new float[3][][];
        var ro = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            ra[c] = new float[(2 * br) + 1][];
            ro[c] = new int[(2 * br) + 1];
            for (int i = -br; i <= br; i++)
            {
                (ra[c][i + br], ro[c][i + br]) = In(input, c, i);
            }
        }

        (float[] ox, int oxo) = Out(output, 0, 0);
        (float[] oy, int oyo) = Out(output, 1, 0);
        (float[] ob, int obo) = Out(output, 2, 0);
        if (LjSimd.Enabled)
        {
            LjSimd.EpfRow(pass, ra, ro, ox, oxo, oy, oyo, ob, obo, xsize, xpos, sigma.Data, rowSigma, sm, bsm, lf.EpfChannelScale, borderRow);
            return;
        }

        Span<float> sads = stackalloc float[12];
        for (int x = 0; x < xsize; x++)
        {
            int bx = (x + xpos + (SigmaPadding * 8)) / 8;
            int ix = (x + xpos) % 8;
            float rs = sigma.Data[rowSigma + bx];
            if (rs < MinSigma)
            {
                ox[oxo + x] = ra[0][br][ro[0][br] + x];
                oy[oyo + x] = ra[1][br][ro[1][br] + x];
                ob[obo + x] = ra[2][br][ro[2][br] + x];
                continue;
            }

            float vsm = borderRow || ix == 0 || ix == 7 ? bsm : sm;
            float invSigma = rs * vsm;
            float X = ra[0][br][ro[0][br] + x], Y = ra[1][br][ro[1][br] + x], B = ra[2][br][ro[2][br] + x];
            float w = 1;
            if (pass == 0)
            {
                sads.Clear();
                for (int c = 0; c < 3; c++)
                {
                    float scale = lf.EpfChannelScale[c];
                    for (int i = 0; i < 12; i++)
                    {
                        float sad = 0;
                        foreach ((int dy, int dx) in PlusOff)
                        {
                            float r11 = ra[c][br + dy][ro[c][br + dy] + x + dx];
                            float c11 = ra[c][br + SadsOff[i].Dy + dy][ro[c][br + SadsOff[i].Dy + dy] + x + SadsOff[i].Dx + dx];
                            sad += MathF.Abs(r11 - c11);
                        }

                        sads[i] = MathF.FusedMultiplyAdd(sad, scale, sads[i]);
                    }
                }

                for (int i = 0; i < 12; i++)
                {
                    int row = br + SadsOff[i].Dy, xx = x + SadsOff[i].Dx;
                    float weight = Weight(sads[i], invSigma);
                    w += weight;
                    X = MathF.FusedMultiplyAdd(weight, ra[0][row][ro[0][row] + xx], X);
                    Y = MathF.FusedMultiplyAdd(weight, ra[1][row][ro[1][row] + xx], Y);
                    B = MathF.FusedMultiplyAdd(weight, ra[2][row][ro[2][row] + xx], B);
                }
            }
            else if (pass == 1)
            {
                float sad0 = 0, sad1 = 0, sad2 = 0, sad3 = 0;
                for (int c = 0; c < 3; c++)
                {
                    float[] rm2 = ra[c][0], rm1 = ra[c][1], r0 = ra[c][2], rp1 = ra[c][3], rp2 = ra[c][4];
                    int om2 = ro[c][0] + x, om1 = ro[c][1] + x, o0 = ro[c][2] + x, op1 = ro[c][3] + x, op2 = ro[c][4] + x;
                    float p20 = rm2[om2], p21 = rm1[om1];
                    float sad0c = MathF.Abs(p20 - p21);
                    float p11 = rm1[om1 - 1];
                    float sad1c = MathF.Abs(p11 - p21);
                    float p31 = rm1[om1 + 1];
                    float sad2c = MathF.Abs(p31 - p21);
                    float p02 = r0[o0 - 2], p12 = r0[o0 - 1];
                    sad1c += MathF.Abs(p02 - p12);
                    sad0c += MathF.Abs(p11 - p12);
                    float p22 = r0[o0];
                    float t = MathF.Abs(p12 - p22);
                    sad1c += t;
                    sad2c += t;
                    t = MathF.Abs(p22 - p21);
                    float sad3c = t;
                    sad0c += t;
                    float p32 = r0[o0 + 1];
                    sad0c += MathF.Abs(p31 - p32);
                    t = MathF.Abs(p22 - p32);
                    sad1c += t;
                    sad2c += t;
                    float p42 = r0[o0 + 2];
                    sad2c += MathF.Abs(p42 - p32);
                    float p13 = rp1[op1 - 1];
                    sad3c += MathF.Abs(p13 - p12);
                    float p23 = rp1[op1];
                    t = MathF.Abs(p22 - p23);
                    sad0c += t;
                    sad3c += t;
                    sad1c += MathF.Abs(p13 - p23);
                    float p33 = rp1[op1 + 1];
                    sad2c += MathF.Abs(p33 - p23);
                    sad3c += MathF.Abs(p33 - p32);
                    float p24 = rp2[op2];
                    sad3c += MathF.Abs(p24 - p23);
                    float scale = lf.EpfChannelScale[c];
                    sad0 = MathF.FusedMultiplyAdd(sad0c, scale, sad0);
                    sad1 = MathF.FusedMultiplyAdd(sad1c, scale, sad1);
                    sad2 = MathF.FusedMultiplyAdd(sad2c, scale, sad2);
                    sad3 = MathF.FusedMultiplyAdd(sad3c, scale, sad3);
                }

                AddPixel(ra, ro, br - 1, x, sad0, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel(ra, ro, br, x - 1, sad1, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel(ra, ro, br, x + 1, sad2, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel(ra, ro, br + 1, x, sad3, invSigma, ref X, ref Y, ref B, ref w);
            }
            else
            {
                float xc = X, yc = Y, bc = B;
                AddPixel2(ra, ro, br - 1, x, xc, yc, bc, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel2(ra, ro, br, x - 1, xc, yc, bc, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel2(ra, ro, br, x + 1, xc, yc, bc, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel2(ra, ro, br + 1, x, xc, yc, bc, invSigma, ref X, ref Y, ref B, ref w);
            }

            float invW = 1.0f / w;
            ox[oxo + x] = X * invW;
            oy[oyo + x] = Y * invW;
            ob[obo + x] = B * invW;
        }
    }

    private static void AddPixel(float[][][] ra, int[][] ro, int row, int x, float sad, float invSigma, ref float X, ref float Y, ref float B, ref float w)
    {
        float weight = Weight(sad, invSigma);
        w += weight;
        X = MathF.FusedMultiplyAdd(weight, ra[0][row][ro[0][row] + x], X);
        Y = MathF.FusedMultiplyAdd(weight, ra[1][row][ro[1][row] + x], Y);
        B = MathF.FusedMultiplyAdd(weight, ra[2][row][ro[2][row] + x], B);
    }

    private void AddPixel2(float[][][] ra, int[][] ro, int row, int x, float rx, float ry, float rb, float invSigma, ref float X, ref float Y, ref float B, ref float w)
    {
        float cx = ra[0][row][ro[0][row] + x], cy = ra[1][row][ro[1][row] + x], cb = ra[2][row][ro[2][row] + x];
        float sad = MathF.Abs(cx - rx) * lf.EpfChannelScale[0];
        sad = MathF.FusedMultiplyAdd(MathF.Abs(cy - ry), lf.EpfChannelScale[1], sad);
        sad = MathF.FusedMultiplyAdd(MathF.Abs(cb - rb), lf.EpfChannelScale[2], sad);
        float weight = Weight(sad, invSigma);
        w += weight;
        X = MathF.FusedMultiplyAdd(weight, cx, X);
        Y = MathF.FusedMultiplyAdd(weight, cy, Y);
        B = MathF.FusedMultiplyAdd(weight, cb, B);
    }
}

/// <summary>UpsamplingStage (2x / 4x / 8x with the custom or default kernels).</summary>
internal sealed class LjUpsamplingStage : LjStage
{
    private readonly int c;
    private readonly float[] kernel = new float[64 * 25];

    public LjUpsamplingStage(LjCustomTransformData ups, int c, int shift)
    {
        this.c = c;
        ShiftX = ShiftY = shift;
        BorderX = BorderY = 2;
        float[] weights = shift == 1 ? ups.Upsampling2Weights : shift == 2 ? ups.Upsampling4Weights : ups.Upsampling8Weights;
        int n = 1 << shift;
        int h = n / 2;
        for (int ky = 0; ky < h; ++ky)
        {
            for (int kx = 0; kx < h; ++kx)
            {
                int offset0 = ((ky * n) + kx) * 25;
                int offset1 = ((ky * n) + (n - 1 - kx)) * 25;
                int offset2 = (((n - 1 - ky) * n) + kx) * 25;
                int offset3 = (((n - 1 - ky) * n) + (n - 1 - kx)) * 25;
                for (int py = 0; py < 5; ++py)
                {
                    for (int px = 0; px < 5; ++px)
                    {
                        int j = (5 * ky) + py;
                        int i = (5 * kx) + px;
                        int my = Math.Min(i, j);
                        int mx = Math.Max(i, j);
                        float w = weights[(5 * h * my) - (my * (my - 1) / 2) + mx - my];
                        kernel[offset0 + (py * 5) + px] = w;
                        kernel[offset1 + (py * 5) + (4 - px)] = w;
                        kernel[offset2 + ((4 - py) * 5) + px] = w;
                        kernel[offset3 + ((4 - py) * 5) + (4 - px)] = w;
                    }
                }
            }
        }
    }

    public override LjChannelMode GetChannelMode(int ch) => ch == c ? LjChannelMode.InOut : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        int n = 1 << ShiftX;
        var rows = new (float[] A, int O)[5];
        for (int iy = -2; iy <= 2; iy++)
        {
            rows[iy + 2] = In(input, c, iy);
        }

        // Column min/max over the 5 rows, then over 5 columns (PreProcessRowImpl).
        var colMin = new float[xsize + 4];
        var colMax = new float[xsize + 4];
        for (int x = 0; x < xsize + 4; x++)
        {
            float v0 = rows[0].A[rows[0].O + x - 2], v1 = rows[1].A[rows[1].O + x - 2];
            float min0 = MathF.Min(v0, v1), max0 = MathF.Max(v0, v1);
            float v2 = rows[2].A[rows[2].O + x - 2], v3 = rows[3].A[rows[3].O + x - 2];
            float min1 = MathF.Min(v2, v3), max1 = MathF.Max(v2, v3);
            float v4 = rows[4].A[rows[4].O + x - 2];
            colMin[x] = MathF.Min(v4, MathF.Min(min0, min1));
            colMax[x] = MathF.Max(v4, MathF.Max(max0, max1));
        }

        var mins = new float[xsize];
        var maxs = new float[xsize];
        for (int x = 0; x < xsize; x++)
        {
            mins[x] = MathF.Min(colMin[x + 4], MathF.Min(MathF.Min(colMin[x], colMin[x + 1]), MathF.Min(colMin[x + 2], colMin[x + 3])));
            maxs[x] = MathF.Max(colMax[x + 4], MathF.Max(MathF.Max(colMax[x], colMax[x + 1]), MathF.Max(colMax[x + 2], colMax[x + 3])));
        }

        var input25 = new (float[] A, int O)[25];
        for (int iy = -2; iy <= 2; ++iy)
        {
            for (int ix = -2; ix <= 2; ++ix)
            {
                input25[(5 * (iy + 2)) + ix + 2] = (rows[iy + 2].A, rows[iy + 2].O + ix);
            }
        }

        for (int oy = 0; oy < n; oy++)
        {
            (float[] dst, int dOff) = Out(output, c, oy);
            for (int ox = 0; ox < n; ox++)
            {
                int k = (n * oy) + ox;
                int kb = k * 25;
                for (int x = 0; x < xsize; x++)
                {
                    float acc0 = input25[0].A[input25[0].O + x] * kernel[kb];
                    float acc1 = input25[1].A[input25[1].O + x] * kernel[kb + 1];
                    float acc2 = input25[2].A[input25[2].O + x] * kernel[kb + 2];
                    for (int i = 3; i < 24; i += 3)
                    {
                        acc0 = MathF.FusedMultiplyAdd(input25[i].A[input25[i].O + x], kernel[kb + i], acc0);
                        acc1 = MathF.FusedMultiplyAdd(input25[i + 1].A[input25[i + 1].O + x], kernel[kb + i + 1], acc1);
                        acc2 = MathF.FusedMultiplyAdd(input25[i + 2].A[input25[i + 2].O + x], kernel[kb + i + 2], acc2);
                    }

                    acc0 = MathF.FusedMultiplyAdd(input25[24].A[input25[24].O + x], kernel[kb + 24], acc0);
                    float result = (acc1 + acc2) + acc0;
                    dst[dOff + (x * n) + ox] = MathF.Min(MathF.Max(result, mins[x]), maxs[x]);
                }
            }
        }
    }
}

/// <summary>XYBStage: XYB to linear RGB (or scaled XYB output).</summary>
internal sealed class LjXybStage : LjStage
{
    private readonly LjOpsinParams p;
    private readonly bool outputIsXyb;

    public LjXybStage(LjOpsinParams p, bool outputIsXyb)
    {
        this.p = p;
        this.outputIsXyb = outputIsXyb;
    }

    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        (float[] a0, int o0) = In(input, 0, 0);
        (float[] a1, int o1) = In(input, 1, 0);
        (float[] a2, int o2) = In(input, 2, 0);
        if (outputIsXyb)
        {
            for (int x = 0; x < xsize; x++)
            {
                float ix = a0[o0 + x], iy = a1[o1 + x], ib = a2[o2 + x];
                a0[o0 + x] = (ix + 0.015386134f) * 22.995788804f;
                a1[o1 + x] = (iy + 0.0f) * 1.183000077f;
                a2[o2 + x] = ((ib - iy) + 0.27770459f) * 1.502141333f;
            }

            return;
        }

        if (LjSimd.Enabled)
        {
            LjSimd.XybRow(a0, o0, a1, o1, a2, o2, xsize, p);
            return;
        }

        for (int x = 0; x < xsize; x++)
        {
            LjOpsinParams.XybToRgb(p, a0[o0 + x], a1[o1 + x], a2[o2 + x], out float r, out float g, out float b);
            a0[o0 + x] = r;
            a1[o1 + x] = g;
            a2[o2 + x] = b;
        }
    }
}

internal sealed class LjYCbCrStage : LjStage
{
    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        const float c128 = 128.0f / 255;
        const float crcr = 1.402f;
        const float cgcb = -0.114f * 1.772f / 0.587f;
        const float cgcr = -0.299f * 1.402f / 0.587f;
        const float cbcb = 1.772f;
        (float[] a0, int o0) = In(input, 0, 0);
        (float[] a1, int o1) = In(input, 1, 0);
        (float[] a2, int o2) = In(input, 2, 0);
        for (int x = 0; x < xsize; x++)
        {
            float y = a1[o1 + x] + c128;
            float cb = a0[o0 + x];
            float cr = a2[o2 + x];
            a0[o0 + x] = MathF.FusedMultiplyAdd(crcr, cr, y);
            a1[o1 + x] = MathF.FusedMultiplyAdd(cgcr, cr, MathF.FusedMultiplyAdd(cgcb, cb, y));
            a2[o2 + x] = MathF.FusedMultiplyAdd(cbcb, cb, y);
        }
    }
}

/// <summary>FromLinearStage (JXL_HIGH_PRECISION build: exact transfer functions).</summary>
internal sealed class LjFromLinearStage : LjStage
{
    private readonly Func<float, float>? op;
    private readonly LjHlgOotf? hlgOotf;
    private readonly int simdKind;   // 1 sRGB, 2 Rec.709, 3 gamma
    private readonly float inverseGamma;

    public LjFromLinearStage(Func<float, float>? op, LjHlgOotf? hlgOotf = null, int simdKind = 0, float inverseGamma = 0)
    {
        this.op = op;
        this.hlgOotf = hlgOotf;
        this.simdKind = simdKind;
        this.inverseGamma = inverseGamma;
    }

    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        if (op == null)
        {
            return;
        }

        (float[] a0, int o0) = In(input, 0, 0);
        (float[] a1, int o1) = In(input, 1, 0);
        (float[] a2, int o2) = In(input, 2, 0);
        if (LjSimd.Enabled && simdKind != 0)
        {
            foreach ((float[] a, int o) in new[] { (a0, o0), (a1, o1), (a2, o2) })
            {
                if (simdKind == 1)
                {
                    LjSimd.SrgbFromLinearRow(a, o, xsize);
                }
                else if (simdKind == 2)
                {
                    LjSimd.Rec709FromLinearRow(a, o, xsize);
                }
                else
                {
                    LjSimd.GammaFromLinearRow(a, o, xsize, inverseGamma);
                }
            }

            return;
        }

        for (int x = 0; x < xsize; x++)
        {
            float r = a0[o0 + x], g = a1[o1 + x], b = a2[o2 + x];
            hlgOotf?.Apply(ref r, ref g, ref b);
            a0[o0 + x] = op(r);
            a1[o1 + x] = op(g);
            a2[o2 + x] = op(b);
        }
    }
}

/// <summary>CmsStage (stage_cms.cc): converts the colour (or CMYK) channels to the output encoding through the CMS.</summary>
internal sealed class LjCmsStage : LjStage
{
    private readonly LjColorEncoding cSrc;
    private readonly LjOutputEncoding oe;
    private LjCms.Transform? transform;

    private LjCmsStage(LjOutputEncoding oe, bool linear)
    {
        this.oe = oe;
        cSrc = linear ? oe.LinearColorEncoding : oe.OrigColorEncoding;
    }

    /// <summary>GetCmsStage: null when the stage is not needed.</summary>
    public static LjCmsStage? Create(LjOutputEncoding oe, bool linear)
    {
        var s = new LjCmsStage(oe, linear);
        int channelsSrc = s.cSrc.Cmyk ? 4 : LjCe.IsGray(s.cSrc) ? 1 : 3;
        int channelsDst = LjCe.IsGray(oe.ColorEncoding) ? 1 : 3;
        bool notMixingColorAndGrey = channelsSrc == channelsDst || (channelsSrc == 4 && channelsDst == 3);
        return oe.CmsSet && !LjCms.SameColorEncoding(s.cSrc, oe.ColorEncoding) && notMixingColorAndGrey ? s : null;
    }

    public override LjChannelMode GetChannelMode(int c) => c < (cSrc.Cmyk ? 4 : 3) ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    public override void SetInputSizes(int[] xs, int[] ys)
    {
        // PrepareForThreads: ColorSpaceTransform::Init.
        transform = LjCms.Transform.Create(cSrc, oe.ColorEncoding, oe.DesiredIntensityTarget) ?? throw LjDec.Fail("CMS initialization failed");
    }

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        bool graySrc = LjCe.IsGray(cSrc), grayDst = LjCe.IsGray(oe.ColorEncoding);
        var bufSrc = new float[xsize * 4];
        var bufDst = new float[xsize * 3];
        (float[] a0, int o0) = In(input, 0, 0);
        if (cSrc.Cmyk)
        {
            (float[] a1, int o1) = In(input, 1, 0);
            (float[] a2, int o2) = In(input, 2, 0);
            (float[] a3, int o3) = In(input, 3, 0);
            for (int x = 0; x < xsize; x++)
            {
                bufSrc[4 * x] = a0[o0 + x];
                bufSrc[(4 * x) + 1] = a1[o1 + x];
                bufSrc[(4 * x) + 2] = a2[o2 + x];
                bufSrc[(4 * x) + 3] = a3[o3 + x];
            }
        }
        else if (graySrc)
        {
            Array.Copy(a0, o0, bufSrc, 0, xsize);
        }
        else
        {
            (float[] a1, int o1) = In(input, 1, 0);
            (float[] a2, int o2) = In(input, 2, 0);
            for (int x = 0; x < xsize; x++)
            {
                bufSrc[3 * x] = a0[o0 + x];
                bufSrc[(3 * x) + 1] = a1[o1 + x];
                bufSrc[(3 * x) + 2] = a2[o2 + x];
            }
        }

        transform!.Run(bufSrc, bufDst, xsize);
        for (int c = 0; c < 3; c++)
        {
            (float[] a, int o) = In(input, c, 0);
            for (int x = 0; x < xsize; x++)
            {
                a[o + x] = grayDst ? bufDst[x] : bufDst[(3 * x) + c];
            }
        }
    }
}

/// <summary>WriteToImageBundleStage / WriteToImage3FStage / the main output: copies the rows into float planes.</summary>
internal sealed class LjWriteStage : LjStage
{
    private readonly bool colorOnly;
    public LjImageF[] Planes = Array.Empty<LjImageF>();

    public LjWriteStage(bool colorOnly) => this.colorOnly = colorOnly;

    public override LjChannelMode GetChannelMode(int c) => colorOnly && c >= 3 ? LjChannelMode.Ignored : LjChannelMode.Input;

    public override void SetInputSizes(int[] xs, int[] ys)
    {
        int n = colorOnly ? 3 : xs.Length;
        Planes = new LjImageF[n];
        for (int c = 0; c < n; c++)
        {
            Planes[c] = new LjImageF(xs[0], ys[0]);
        }
    }

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        if (ypos >= Planes[0].YSize)
        {
            return;
        }

        for (int c = 0; c < Planes.Length; c++)
        {
            (float[] a, int o) = In(input, c, 0);
            Array.Copy(a, o, Planes[c].Data, Planes[c].Row(ypos) + xpos, Math.Min(xsize, Planes[c].XSize - xpos));
        }
    }
}
