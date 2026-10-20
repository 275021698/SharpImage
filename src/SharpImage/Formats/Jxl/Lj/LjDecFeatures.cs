// libjxl 0.12 image features: patches (dec_patch_dictionary.cc, stage_patches.cc), splines (splines.cc,
// stage_splines.cc) and noise (dec_noise.cc, stage_noise.cc, xorshift128plus-inl.h). SIMD code is evaluated per
// pixel with fused multiply-adds where libjxl's AVX2 build fuses; scalar libjxl code stays unfused.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjPatchDictionary
{
    private const int NumRefPatchContext = 0, ReferenceFrameContext = 1, PatchSizeContext = 2, PatchReferencePositionContext = 3,
        PatchPositionContext = 4, PatchBlendModeContext = 5, PatchOffsetContext = 6, PatchCountContext = 7, PatchAlphaChannelContext = 8,
        PatchClampContext = 9, NumPatchDictionaryContexts = 10;

    private struct RefPos
    {
        public int Ref, X0, Y0, XSize, YSize;
    }

    private struct Pos
    {
        public int X, Y, RefPosIdx;
    }

    private readonly List<RefPos> refPositions = new();
    private readonly List<Pos> positions = new();
    private readonly List<LjAlpha.Blending> blendings = new();
    private int blendingsStride;
    private LjReferenceFrame[] refs = [];
    private List<int>[] rowPatches = [];

    private static bool UsesAlpha(int m) => m is LjAlpha.PbBlendAbove or LjAlpha.PbBlendBelow or LjAlpha.PbAlphaWeightedAddAbove or LjAlpha.PbAlphaWeightedAddBelow;

    public static LjPatchDictionary Decode(LjBitReader br, int xsize, int ysize, int numExtraChannels, LjReferenceFrame[] refs)
    {
        var pd = new LjPatchDictionary { refs = refs, blendingsStride = numExtraChannels + 1 };
        LjAnsCode code = LjDecAns.DecodeHistograms(br, NumPatchDictionaryContexts, out byte[] ctxMap);
        var decoder = new LjAnsSymbolReader(code, br);
        int ReadNum(int ctx) => (int)decoder.ReadHybridUint(ctx, br, ctxMap);
        int numRefPatch = ReadNum(NumRefPatchContext);
        long numPixels = (long)xsize * ysize;
        long maxRefPatches = 1024 + (numPixels / 4);
        long maxPatches = maxRefPatches * 4;
        if (numRefPatch > maxRefPatches)
        {
            throw LjDec.Fail("Too many patches in dictionary");
        }

        long totalPatches = 0;
        for (int id = 0; id < numRefPatch; id++)
        {
            var rp = new RefPos { Ref = ReadNum(ReferenceFrameContext) };
            if (rp.Ref >= 4 || refs[rp.Ref].Empty)
            {
                throw LjDec.Fail("Invalid reference frame ID");
            }

            if (!refs[rp.Ref].IbIsInXyb)
            {
                throw LjDec.Fail("Patches cannot use frames saved post color transforms");
            }

            LjImageF ib = refs[rp.Ref].Planes![0];
            rp.X0 = ReadNum(PatchReferencePositionContext);
            rp.Y0 = ReadNum(PatchReferencePositionContext);
            rp.XSize = ReadNum(PatchSizeContext) + 1;
            rp.YSize = ReadNum(PatchSizeContext) + 1;
            if ((long)rp.X0 + rp.XSize > ib.XSize || (long)rp.Y0 + rp.YSize > ib.YSize)
            {
                throw LjDec.Fail("Invalid position specified in reference frame");
            }

            long idCount = ReadNum(PatchCountContext);
            if (idCount > maxPatches)
            {
                throw LjDec.Fail("Too many patches in dictionary");
            }

            idCount++;
            totalPatches += idCount;
            if (totalPatches > maxPatches)
            {
                throw LjDec.Fail("Too many patches in dictionary");
            }

            bool chooseAlpha = numExtraChannels > 1;
            for (long i = 0; i < idCount; i++)
            {
                var pos = new Pos { RefPosIdx = pd.refPositions.Count };
                if (i == 0)
                {
                    pos.X = ReadNum(PatchPositionContext);
                    pos.Y = ReadNum(PatchPositionContext);
                }
                else
                {
                    int deltax = LjHeaderUtil.UnpackSigned((uint)ReadNum(PatchOffsetContext));
                    if (deltax < 0 && -deltax > pd.positions[^1].X)
                    {
                        throw LjDec.Fail("Invalid patch: negative x coordinate");
                    }

                    pos.X = pd.positions[^1].X + deltax;
                    int deltay = LjHeaderUtil.UnpackSigned((uint)ReadNum(PatchOffsetContext));
                    if (deltay < 0 && -deltay > pd.positions[^1].Y)
                    {
                        throw LjDec.Fail("Invalid patch: negative y coordinate");
                    }

                    pos.Y = pd.positions[^1].Y + deltay;
                }

                if ((long)pos.X + rp.XSize > xsize || (long)pos.Y + rp.YSize > ysize)
                {
                    throw LjDec.Fail("Invalid patch position");
                }

                for (int j = 0; j < pd.blendingsStride; j++)
                {
                    int blendMode = ReadNum(PatchBlendModeContext);
                    if (blendMode >= 8)
                    {
                        throw LjDec.Fail("Invalid patch blend mode");
                    }

                    var info = new LjAlpha.Blending { Mode = blendMode };
                    if (UsesAlpha(blendMode) && chooseAlpha)
                    {
                        info.AlphaChannel = ReadNum(PatchAlphaChannelContext);
                        if (info.AlphaChannel >= numExtraChannels)
                        {
                            throw LjDec.Fail("Invalid alpha channel for blending");
                        }
                    }

                    if (UsesAlpha(blendMode) || blendMode == LjAlpha.PbMul)
                    {
                        info.Clamp = ReadNum(PatchClampContext) != 0;
                    }

                    pd.blendings.Add(info);
                }

                pd.positions.Add(pos);
            }

            pd.refPositions.Add(rp);
        }

        if (!decoder.CheckAnsFinalState())
        {
            throw LjDec.Fail("ANS checksum failure.");
        }

        // GetPatchesForRow returns the patches covering a row in increasing index order.
        int maxY = 0;
        foreach (Pos p in pd.positions)
        {
            maxY = Math.Max(maxY, p.Y + pd.refPositions[p.RefPosIdx].YSize);
        }

        pd.rowPatches = new List<int>[maxY];
        for (int i = 0; i < pd.positions.Count; i++)
        {
            Pos p = pd.positions[i];
            for (int y = p.Y; y < p.Y + pd.refPositions[p.RefPosIdx].YSize; y++)
            {
                (pd.rowPatches[y] ??= new List<int>()).Add(i);
            }
        }

        return pd;
    }

    public int GetReferences()
    {
        int result = 0;
        foreach (RefPos rp in refPositions)
        {
            result |= 1 << rp.Ref;
        }

        return result;
    }

    /// <summary>AddOneRow: blends the patches covering row <paramref name="y"/> into <paramref name="inout"/>.</summary>
    public void AddOneRow((float[] A, int O)[] inout, int y, int x0, int xsize, List<LjExtraChannelInfo> eci)
    {
        if (y >= rowPatches.Length || rowPatches[y] == null)
        {
            return;
        }

        int numEc = eci.Count;
        var fg = new (float[] A, int O)[3 + numEc];
        var bgo = new (float[] A, int O)[3 + numEc];
        var ecBlend = new LjAlpha.Blending[numEc];
        foreach (int posIdx in rowPatches[y])
        {
            int blendingIdx = posIdx * blendingsStride;
            Pos pos = positions[posIdx];
            RefPos rp = refPositions[pos.RefPosIdx];
            int by = pos.Y, bx = pos.X;
            int iy = y - by;
            if (bx >= x0 + xsize || bx + rp.XSize < x0)
            {
                continue;
            }

            int patchX0 = Math.Max(bx, x0);
            int patchX1 = Math.Min(bx + rp.XSize, x0 + xsize);
            LjImageF[] planes = refs[rp.Ref].Planes!;
            int shift = patchX0 - x0;
            for (int c = 0; c < 3 + numEc; c++)
            {
                LjImageF p = planes[c];
                fg[c] = (p.Data, p.Row(rp.Y0 + iy) + rp.X0 + x0 - bx + shift);
                bgo[c] = (inout[c].A, inout[c].O + shift);
            }

            for (int i = 0; i < numEc; i++)
            {
                ecBlend[i] = blendings[blendingIdx + 1 + i];
            }

            LjAlpha.PerformBlending(bgo, fg, bgo, patchX1 - patchX0, blendings[blendingIdx], ecBlend, eci);
        }
    }
}

internal sealed class LjPatchesStage : LjStage
{
    private readonly LjPatchDictionary patches;
    private readonly List<LjExtraChannelInfo> eci;

    public LjPatchesStage(LjPatchDictionary p, List<LjExtraChannelInfo> eci, int numC)
    {
        patches = p;
        this.eci = eci;
    }

    public override LjChannelMode GetChannelMode(int c) => c < 3 + eci.Count ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        var rows = new (float[] A, int O)[3 + eci.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = In(input, i, 0);
        }

        patches.AddOneRow(rows, ypos, xpos, xsize, eci);
    }
}

// ====================================================================== splines

internal sealed class LjSplines
{
    private const int QuantizationAdjustmentContext = 0, StartingPositionContext = 1, NumSplinesContext = 2, NumControlPointsContext = 3,
        ControlPointsContext = 4, DctContext = 5, NumSplineContexts = 6;

    private const float Sqrt2 = 1.41421356237f;
    private const float Sqrt0_5 = 0.70710678118f;
    private static readonly float[] ChannelWeight = [0.0042f, 0.075f, 0.07f, .3333f];

    private sealed class Quantized
    {
        public (long X, long Y)[] ControlPoints = [];
        public int[][] ColorDct = [new int[32], new int[32], new int[32]];
        public int[] SigmaDct = new int[32];
    }

    private struct Segment
    {
        public float CenterX, CenterY, MaximumDistance, InvSigma, SigmaOver4TimesIntensity;
        public float C0, C1, C2;
    }

    private readonly List<Quantized> splines = new();
    private readonly List<(float X, float Y)> startingPoints = new();
    private int quantizationAdjustment;
    private readonly List<Segment> segments = new();
    private int[] segmentIndices = [];
    private long[] segmentYStart = [];

    public static LjSplines Decode(LjBitReader br, long numPixels)
    {
        var s = new LjSplines();
        LjAnsCode code = LjDecAns.DecodeHistograms(br, NumSplineContexts, out byte[] ctxMap);
        var decoder = new LjAnsSymbolReader(code, br);
        long numSplines = decoder.ReadHybridUint(NumSplinesContext, br, ctxMap);
        long maxControlPoints = Math.Min(1L << 20, numPixels / 2);
        if (numSplines > maxControlPoints || numSplines + 1 > maxControlPoints)
        {
            throw LjDec.Fail("Too many splines");
        }

        numSplines++;
        long lastX = 0, lastY = 0;
        for (long i = 0; i < numSplines; i++)
        {
            uint dx = decoder.ReadHybridUint(StartingPositionContext, br, ctxMap);
            uint dy = decoder.ReadHybridUint(StartingPositionContext, br, ctxMap);
            long x, y;
            if (i != 0)
            {
                x = LjHeaderUtil.UnpackSigned(dx) + lastX;
                y = LjHeaderUtil.UnpackSigned(dy) + lastY;
            }
            else
            {
                x = dx;
                y = dy;
            }

            ValidatePos(x, y);
            s.startingPoints.Add((x, y));
            lastX = x;
            lastY = y;
        }

        s.quantizationAdjustment = LjHeaderUtil.UnpackSigned(decoder.ReadHybridUint(QuantizationAdjustmentContext, br, ctxMap));
        long totalControlPoints = numSplines;
        for (long i = 0; i < numSplines; ++i)
        {
            var q = new Quantized();
            long ncp = decoder.ReadHybridUint(NumControlPointsContext, br, ctxMap);
            if (ncp > maxControlPoints)
            {
                throw LjDec.Fail("Too many control points");
            }

            totalControlPoints += ncp;
            if (totalControlPoints > maxControlPoints)
            {
                throw LjDec.Fail("Too many control points");
            }

            q.ControlPoints = new (long, long)[ncp];
            const long deltaLimit = 1L << 30;
            for (long k = 0; k < ncp; k++)
            {
                long a = LjHeaderUtil.UnpackSigned(decoder.ReadHybridUint(ControlPointsContext, br, ctxMap));
                long b = LjHeaderUtil.UnpackSigned(decoder.ReadHybridUint(ControlPointsContext, br, ctxMap));
                if (a >= deltaLimit || a <= -deltaLimit || b >= deltaLimit || b <= -deltaLimit)
                {
                    throw LjDec.Fail("Spline delta-delta is out of bounds");
                }

                q.ControlPoints[k] = (a, b);
            }

            void DecodeDct(int[] dct)
            {
                for (int j = 0; j < 32; ++j)
                {
                    dct[j] = LjHeaderUtil.UnpackSigned(decoder.ReadHybridUint(DctContext, br, ctxMap));
                    if (dct[j] == int.MinValue)
                    {
                        throw LjDec.Fail("The weird number in spline DCT");
                    }
                }
            }

            foreach (int[] d in q.ColorDct)
            {
                DecodeDct(d);
            }

            DecodeDct(q.SigmaDct);
            s.splines.Add(q);
        }

        if (!decoder.CheckAnsFinalState())
        {
            throw LjDec.Fail("ANS checksum failure.");
        }

        return s;
    }

    private static void ValidatePos(double x, double y)
    {
        const double limit = 1u << 23;
        if (x >= limit || x <= -limit || y >= limit || y <= -limit)
        {
            throw LjDec.Fail("Spline coordinates out of bounds");
        }
    }

    private static float InvAdjustedQuant(int adjustment) => adjustment >= 0 ? 1.0f / (1.0f + (.125f * adjustment)) : 1.0f - (.125f * adjustment);

    private static long RoundAway(float v) => (long)MathF.Round(v, MidpointRounding.AwayFromZero);

    private static float Hypot(float x, float y) => (float)Math.Sqrt(((double)x * x) + ((double)y * y));

    // FastCosf / FastErff (fast_math-inl.h), AVX2 build: fused multiply-adds.
    private static float FastCosf(float x)
    {
        const double kPi = 3.14159265358979323846264338327950288;
        float pi2 = (float)(kPi * 2.0f);
        float pi2Inv = (float)(0.5f / kPi);
        float npi2 = MathF.Floor(x * pi2Inv) * pi2;
        float xmodpi2 = x - npi2;
        float xPi = MathF.Min(xmodpi2, pi2 - xmodpi2);
        bool abovePihalf = xPi >= (float)(kPi / 2.0f);
        float xPihalf = abovePihalf ? (float)kPi - xPi : xPi;
        float xs = xPihalf * 0.25f;
        float x2 = xs * xs;
        float x4 = x2 * x2;
        float cosxPrescaling = MathF.FusedMultiplyAdd(x4, (float)0.06960438, MathF.FusedMultiplyAdd(x2, (float)-0.84087373, (float)1.68179268));
        float cosxScale1 = MathF.FusedMultiplyAdd(cosxPrescaling, cosxPrescaling, (float)-1.414213562);
        float cosxScale2 = MathF.FusedMultiplyAdd(cosxScale1, cosxScale1, -1);
        return abovePihalf ? -cosxScale2 : cosxScale2;
    }

    private static float FastErff(float x)
    {
        bool xle0 = x <= 0;
        float absx = MathF.Abs(x);
        float denom1 = MathF.FusedMultiplyAdd(absx, 7.77394369e-02f, 2.05260015e-04f);
        float denom2 = MathF.FusedMultiplyAdd(denom1, absx, 2.32120216e-01f);
        float denom3 = MathF.FusedMultiplyAdd(denom2, absx, 2.77820801e-01f);
        float denom4 = MathF.FusedMultiplyAdd(denom3, absx, 1.0f);
        float denom5 = denom4 * denom4;
        float invDenom5 = 1.0f / denom5;
        float result = MathF.FusedMultiplyAdd(-invDenom5, invDenom5, 1.0f);
        return xle0 ? -result : result;
    }

    private static readonly float[] CosMultipliers = BuildMultipliers();

    private static float[] BuildMultipliers()
    {
        const double kPi = 3.14159265358979323846264338327950288;
        var m = new float[32];
        for (int i = 0; i < 32; i++)
        {
            m[i] = (float)(kPi / 32 * i);
        }

        return m;
    }

    private static float ContinuousIdct(float[] dct, float t)
    {
        Span<float> lanes = stackalloc float[8];
        float tandhalf = t + 0.5f;
        for (int i = 0; i < 32; i++)
        {
            float cosArg = CosMultipliers[i] * tandhalf;
            float cos = FastCosf(cosArg);
            float localRes = dct[i] * cos;
            lanes[i & 7] = MathF.FusedMultiplyAdd(Sqrt2, localRes, lanes[i & 7]);
        }

        // Highway SumOfLanes over 8 lanes (AVX2).
        return ((lanes[0] + lanes[4]) + (lanes[3] + lanes[7])) + ((lanes[1] + lanes[5]) + (lanes[2] + lanes[6]));
    }

    public void InitializeDrawCache(int imageXsize, int imageYsize, LjColorCorrelation cmap)
    {
        segments.Clear();
        var spans = new List<(long Start, long End)>();
        float yToX = cmap.YtoXRatio(0), yToB = cmap.YtoBRatio(0);
        float invQuant = InvAdjustedQuant(quantizationAdjustment);
        for (int si = 0; si < splines.Count; si++)
        {
            Quantized q = splines[si];
            // Dequantize.
            var points = new List<(float X, float Y)>();
            float px = MathF.Round(startingPoints[si].X, MidpointRounding.AwayFromZero);
            float py = MathF.Round(startingPoints[si].Y, MidpointRounding.AwayFromZero);
            ValidatePos(px, py);
            int currentX = (int)px, currentY = (int)py;
            points.Add((currentX, currentY));
            long cdx = 0, cdy = 0;
            foreach ((long X, long Y) p in q.ControlPoints)
            {
                cdx += p.X;
                cdy += p.Y;
                ValidatePos(cdx, cdy);
                currentX += (int)cdx;
                currentY += (int)cdy;
                ValidatePos(currentX, currentY);
                points.Add((currentX, currentY));
            }

            var colorDct = new float[3][];
            for (int c = 0; c < 3; ++c)
            {
                colorDct[c] = new float[32];
                for (int i = 0; i < 32; ++i)
                {
                    float invDctFactor = i == 0 ? Sqrt0_5 : 1.0f;
                    colorDct[c][i] = q.ColorDct[c][i] * invDctFactor * ChannelWeight[c] * invQuant;
                }
            }

            for (int i = 0; i < 32; ++i)
            {
                colorDct[0][i] += yToX * colorDct[1][i];
                colorDct[2][i] += yToB * colorDct[1][i];
            }

            var sigmaDct = new float[32];
            for (int i = 0; i < 32; ++i)
            {
                float invDctFactor = i == 0 ? Sqrt0_5 : 1.0f;
                sigmaDct[i] = q.SigmaDct[i] * invDctFactor * ChannelWeight[3] * invQuant;
            }

            for (int i = 1; i < points.Count; i++)
            {
                if (points[i] == points[i - 1])
                {
                    throw LjDec.Fail("identical successive control points in spline");
                }
            }

            List<(float X, float Y)> intermediate = CatmullRom(points);
            var toDraw = new List<((float X, float Y) P, float M)>();
            ForEachEquallySpacedPoint(intermediate, (p, m) => toDraw.Add((p, m)));
            float arcLength = ((ulong)(toDraw.Count - 2) * 1.0f) + toDraw[^1].M;
            if (arcLength <= 0.0f)
            {
                continue;
            }

            float invArcLength = 1.0f / arcLength;
            int k = 0;
            foreach (((float X, float Y) point, float multiplier) in toDraw)
            {
                float progress = MathF.Min(1.0f, (k * 1.0f) * invArcLength);
                ++k;
                var color = new float[3];
                for (int c = 0; c < 3; ++c)
                {
                    color[c] = ContinuousIdct(colorDct[c], (32 - 1) * progress);
                }

                float sigma = ContinuousIdct(sigmaDct, (32 - 1) * progress);
                ComputeSegments(imageYsize, point, multiplier, color, sigma, spans);
            }
        }

        segmentYStart = new long[imageYsize + 2];
        for (int i = 0; i < spans.Count; i++)
        {
            segmentYStart[spans[i].Start + 1]++;
            segmentYStart[spans[i].End + 1]--;
        }

        long total = 0, coverage = 0;
        for (int y = 0; y < imageYsize; y++)
        {
            coverage += segmentYStart[y + 1];
            segmentYStart[y + 1] = total;
            total += coverage;
        }

        segmentIndices = new int[total];
        for (int i = 0; i < segments.Count; ++i)
        {
            for (long y = spans[i].Start; y < spans[i].End; y++)
            {
                segmentIndices[segmentYStart[y + 1]++] = i;
            }
        }
    }

    private void ComputeSegments(int imageYsize, (float X, float Y) center, float intensity, float[] color, float sigma, List<(long, long)> spans)
    {
        if (!(float.IsFinite(sigma) && sigma != 0.0f && float.IsFinite(1.0f / sigma) && float.IsFinite(intensity)))
        {
            return;
        }

        const float distanceExp = 5;
        float maxColor = 0.01f;
        for (int c = 0; c < 3; c++)
        {
            maxColor = MathF.Max(maxColor, MathF.Abs(color[c] * intensity));
        }

        float maximumDistance = MathF.Sqrt(-2.0f * sigma * sigma * ((LjDec.LogF(0.1f) * distanceExp) - LjDec.LogF(maxColor)));
        long y0 = Math.Max(RoundAway(center.Y - maximumDistance), 0);
        long y1 = Math.Min(RoundAway(center.Y + maximumDistance) + 1, imageYsize);
        if (y1 <= y0)
        {
            return;
        }

        segments.Add(new Segment
        {
            CenterY = center.Y,
            CenterX = center.X,
            C0 = color[0],
            C1 = color[1],
            C2 = color[2],
            InvSigma = 1.0f / sigma,
            SigmaOver4TimesIntensity = .25f * sigma * intensity,
            MaximumDistance = maximumDistance,
        });
        spans.Add((y0, y1));
    }

    private static List<(float X, float Y)> CatmullRom(List<(float X, float Y)> pts)
    {
        var result = new List<(float X, float Y)>();
        if (pts.Count == 0)
        {
            return result;
        }

        if (pts.Count == 1)
        {
            result.Add(pts[0]);
            return result;
        }

        const int numPoints = 16;
        var points = new List<(float X, float Y)>(pts);
        (float X, float Y) first = (points[0].X + (points[0].X - points[1].X), points[0].Y + (points[0].Y - points[1].Y));
        points.Insert(0, first);
        int n = points.Count;
        points.Add((points[n - 1].X + (points[n - 1].X - points[n - 2].X), points[n - 1].Y + (points[n - 1].Y - points[n - 2].Y)));
        for (int start = 0; start < points.Count - 3; ++start)
        {
            result.Add(points[start + 1]);
            var d = new float[3];
            var t = new float[4];
            t[0] = 0;
            for (int k = 0; k < 3; ++k)
            {
                d[k] = MathF.Sqrt(Hypot(points[start + k + 1].X - points[start + k].X, points[start + k + 1].Y - points[start + k].Y));
                t[k + 1] = t[k] + d[k];
            }

            for (int i = 1; i < numPoints; ++i)
            {
                float tt = d[0] + (((float)i / numPoints) * d[1]);
                var a = new (float X, float Y)[3];
                for (int k = 0; k < 3; ++k)
                {
                    (float X, float Y) p0 = points[start + k], p1 = points[start + k + 1];
                    float f = (tt - t[k]) / d[k];
                    a[k] = (p0.X + (f * (p1.X - p0.X)), p0.Y + (f * (p1.Y - p0.Y)));
                }

                var b = new (float X, float Y)[2];
                for (int k = 0; k < 2; ++k)
                {
                    float f = (tt - t[k]) / (d[k] + d[k + 1]);
                    b[k] = (a[k].X + (f * (a[k + 1].X - a[k].X)), a[k].Y + (f * (a[k + 1].Y - a[k].Y)));
                }

                float g = (tt - t[1]) / d[1];
                result.Add((b[0].X + (g * (b[1].X - b[0].X)), b[0].Y + (g * (b[1].Y - b[0].Y))));
            }
        }

        result.Add(points[^2]);
        return result;
    }

    private static void ForEachEquallySpacedPoint(List<(float X, float Y)> points, Action<(float X, float Y), float> functor)
    {
        (float X, float Y) current = points[0];
        functor(current, 1.0f);
        int next = 0;
        while (next != points.Count)
        {
            (float X, float Y) previous = current;
            bool previousIsCurrent = true;
            float arclengthFromPrevious = 0.0f;
            while (true)
            {
                if (next == points.Count)
                {
                    functor(previous, arclengthFromPrevious);
                    return;
                }

                float vx = points[next].X - previous.X, vy = points[next].Y - previous.Y;
                float arclengthToNext = MathF.Sqrt((vx * vx) + (vy * vy));
                if (arclengthFromPrevious + arclengthToNext >= 1.0f)
                {
                    float f = (1.0f - arclengthFromPrevious) / arclengthToNext;
                    current = (previous.X + (f * vx), previous.Y + (f * vy));
                    functor(current, 1.0f);
                    break;
                }

                arclengthFromPrevious += arclengthToNext;
                previous = points[next];
                previousIsCurrent = false;
                ++next;
            }

            _ = previousIsCurrent;
        }
    }

    /// <summary>AddToRow: draws the segments touching row <paramref name="y"/> over [x0, x1).</summary>
    public void AddToRow((float[] A, int O)[] rows, int y, int x0, int x1)
    {
        if (segments.Count == 0)
        {
            return;
        }

        for (long i = segmentYStart[y]; i < segmentYStart[y + 1]; i++)
        {
            Segment s = segments[segmentIndices[i]];
            long start = RoundAway(s.CenterX - s.MaximumDistance);
            long end = RoundAway(s.CenterX + s.MaximumDistance);
            if (end < x0 || start >= x1)
            {
                continue;
            }

            int spanX0 = (int)(Math.Max(x0, start) - x0);
            int spanX1 = (int)(Math.Min(x1, end + 1) - x0);
            float dy = y - s.CenterY;
            float dy2 = dy * dy;
            for (int x = spanX0; x < spanX1; x++)
            {
                float dx = (float)(x + x0) - s.CenterX;
                float sqd = MathF.FusedMultiplyAdd(dx, dx, dy2);
                float distance = MathF.Sqrt(sqd);
                float oneDimensionalFactor = FastErff(MathF.FusedMultiplyAdd(distance, 0.5f, 0.353553391f) * s.InvSigma) -
                                             FastErff(MathF.FusedMultiplyAdd(distance, 0.5f, -0.353553391f) * s.InvSigma);
                float localIntensity = s.SigmaOver4TimesIntensity * (oneDimensionalFactor * oneDimensionalFactor);
                rows[0].A[rows[0].O + x] = MathF.FusedMultiplyAdd(s.C0, localIntensity, rows[0].A[rows[0].O + x]);
                rows[1].A[rows[1].O + x] = MathF.FusedMultiplyAdd(s.C1, localIntensity, rows[1].A[rows[1].O + x]);
                rows[2].A[rows[2].O + x] = MathF.FusedMultiplyAdd(s.C2, localIntensity, rows[2].A[rows[2].O + x]);
            }
        }
    }
}

internal sealed class LjSplineStage : LjStage
{
    private readonly LjSplines splines;

    public LjSplineStage(LjSplines s) => splines = s;

    public override LjChannelMode GetChannelMode(int c) => c < 3 ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        splines.AddToRow([In(input, 0, 0), In(input, 1, 0), In(input, 2, 0)], ypos, xpos, xpos + xsize);
    }
}

// ====================================================================== noise

internal sealed class LjNoiseParams
{
    public float[] Lut = new float[8];

    public bool HasAny
    {
        get
        {
            foreach (float i in Lut)
            {
                if (MathF.Abs(i) > 1e-3f)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static LjNoiseParams Decode(LjBitReader br)
    {
        var n = new LjNoiseParams();
        for (int i = 0; i < 8; i++)
        {
            n.Lut[i] = (int)br.ReadBits(10) / 1024.0f;
        }

        return n;
    }
}

/// <summary>Xorshift128Plus (xorshift128plus-inl.h), 8 parallel generators.</summary>
internal sealed class LjXorshift128Plus
{
    private readonly ulong[] s0 = new ulong[8];
    private readonly ulong[] s1 = new ulong[8];

    public LjXorshift128Plus(uint seed1, uint seed2, uint seed3, uint seed4)
    {
        s0[0] = SplitMix64(((ulong)seed1 << 32) + seed2 + 0x9E3779B97F4A7C15UL);
        s1[0] = SplitMix64(((ulong)seed3 << 32) + seed4 + 0x9E3779B97F4A7C15UL);
        for (int i = 1; i < 8; ++i)
        {
            s0[i] = SplitMix64(s0[i - 1]);
            s1[i] = SplitMix64(s1[i - 1]);
        }
    }

    private static ulong SplitMix64(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public void Fill(ulong[] randomBits)
    {
        for (int i = 0; i < 8; ++i)
        {
            ulong a = s0[i];
            ulong b = s1[i];
            ulong bits = a + b;
            s0[i] = b;
            a ^= a << 23;
            randomBits[i] = bits;
            a ^= b ^ (a >> 18) ^ (b >> 5);
            s1[i] = a;
        }
    }
}

internal sealed class LjConvolveNoiseStage : LjStage
{
    private readonly int firstC;

    public LjConvolveNoiseStage(int numC)
    {
        firstC = numC;
        BorderX = BorderY = 2;
    }

    public override LjChannelMode GetChannelMode(int c) => c >= firstC ? LjChannelMode.InOut : LjChannelMode.Ignored;

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        for (int c = firstC; c < firstC + 3; c++)
        {
            var rows = new (float[] A, int O)[5];
            for (int i = 0; i < 5; i++)
            {
                rows[i] = In(input, c, i - 2);
            }

            (float[] o, int oo) = Out(output, c, 0);
            for (int x = 0; x < xsize; x++)
            {
                float p00 = rows[2].A[rows[2].O + x];
                float others = 0;
                for (int i = -2; i <= 2; i++)
                {
                    others += rows[0].A[rows[0].O + x + i];
                    others += rows[1].A[rows[1].O + x + i];
                    others += rows[3].A[rows[3].O + x + i];
                    others += rows[4].A[rows[4].O + x + i];
                }

                others += rows[2].A[rows[2].O + x - 2];
                others += rows[2].A[rows[2].O + x - 1];
                others += rows[2].A[rows[2].O + x + 1];
                others += rows[2].A[rows[2].O + x + 2];
                o[oo + x] = MathF.FusedMultiplyAdd(others, (float)0.16, p00 * (float)-3.84);
            }
        }
    }
}

internal sealed class LjAddNoiseStage : LjStage
{
    private readonly LjNoiseParams noise;
    private readonly LjColorCorrelation cmap;
    private readonly int firstC;

    public LjAddNoiseStage(LjNoiseParams n, LjColorCorrelation cmap, int numC)
    {
        noise = n;
        this.cmap = cmap;
        firstC = numC;
    }

    public override LjChannelMode GetChannelMode(int c) => c >= firstC ? LjChannelMode.Input : c < 3 ? LjChannelMode.InPlace : LjChannelMode.Ignored;

    private float Strength(float vx)
    {
        const int scale = 6;
        float scaledVx = MathF.Max(0, vx * scale);
        float floorX = MathF.Floor(scaledVx);
        float fracX = scaledVx - floorX;
        if (scaledVx >= scale + 1)
        {
            floorX = scale;
            fracX = 1;
        }

        int fi = (int)floorX;
        float low = noise.Lut[fi], hi = noise.Lut[fi + 1];
        float v = MathF.FusedMultiplyAdd(hi - low, fracX, low);
        v = MathF.Min(v, 1.0f);
        return v < 0 ? 0 : v;
    }

    public override void ProcessRow(LjStageRows input, LjStageRows output, int xsize, int xpos, int ypos)
    {
        if (!noise.HasAny)
        {
            return;
        }

        float ytox = cmap.YtoXRatio(0), ytob = cmap.YtoBRatio(0);
        (float[] rx, int ox) = In(input, 0, 0);
        (float[] ry, int oy) = In(input, 1, 0);
        (float[] rb, int ob) = In(input, 2, 0);
        (float[] nr, int onr) = In(input, firstC, 0);
        (float[] ng, int ong) = In(input, firstC + 1, 0);
        (float[] nc, int onc) = In(input, firstC + 2, 0);
        const float half = 0.5f, normConst = 0.22f;
        for (int x = 0; x < xsize; x++)
        {
            float vx = rx[ox + x], vy = ry[oy + x];
            float inG = vy - vx, inR = vy + vx;
            float strengthG = Strength(inG * half);
            float strengthR = Strength(inR * half);
            float rndR = nr[onr + x] * normConst;
            float rndG = ng[ong + x] * normConst;
            float rndC = nc[onc + x] * normConst;
            float redNoise = strengthR * MathF.FusedMultiplyAdd(0.0078125f, rndR, 0.9921875f * rndC);
            float greenNoise = strengthG * MathF.FusedMultiplyAdd(0.0078125f, rndG, 0.9921875f * rndC);
            float rgNoise = redNoise + greenNoise;
            rx[ox + x] = MathF.FusedMultiplyAdd(ytox, rgNoise, redNoise - greenNoise) + vx;
            ry[oy + x] = vy + rgNoise;
            rb[ob + x] = MathF.FusedMultiplyAdd(ytob, rgNoise, rb[ob + x]);
        }
    }
}

internal sealed partial class LjFrameDecoder
{
    /// <summary>PrepareNoiseInput: fills the three noise channels of a group with xorshift noise.</summary>
    private void PrepareNoiseInput(int group)
    {
        int groupDim = Fd.GroupDim;
        int gx = group % Fd.XsizeGroups, gy = group / Fd.XsizeGroups;
        int noiseCStart = 3 + st.Meta.ExtraChannelInfo.Count;
        int ups = (int)Fh.Upsampling;
        var batch64 = new ulong[8];
        var batch32 = new uint[16];
        for (int iy = 0; iy < ups; iy++)
        {
            for (int ix = 0; ix < ups; ix++)
            {
                var rng = new LjXorshift128Plus((uint)st.VisibleFrameIndex, (uint)st.NonvisibleFrameIndex,
                    (uint)(((gx * ups) + ix) * groupDim), (uint)(((gy * ups) + iy) * groupDim));
                for (int c = 0; c < 3; c++)
                {
                    (LjImageF img, LjRect r) = pipeline.GetInputBuffer(group, noiseCStart + c);
                    int x1 = r.X0 + r.XSize, y1 = r.Y0 + r.YSize;
                    var rect = new LjRect(r.X0 + (ix * groupDim), r.Y0 + (iy * groupDim), groupDim, groupDim, x1, y1);
                    for (int y = 0; y < rect.YSize; ++y)
                    {
                        int row = img.Row(rect.Y0 + y) + rect.X0;
                        int x = 0;
                        for (; x + 16 < rect.XSize; x += 16)
                        {
                            rng.Fill(batch64);
                            Unpack(batch64, batch32);
                            for (int i = 0; i < 16; i++)
                            {
                                img.Data[row + x + i] = BitsToFloat(batch32[i]);
                            }
                        }

                        rng.Fill(batch64);
                        Unpack(batch64, batch32);
                        int batchPos = 0;
                        for (; x < rect.XSize; x += 8)
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                img.Data[row + x + i] = BitsToFloat(batch32[batchPos + i]);
                            }

                            batchPos += 8;
                        }
                    }
                }
            }
        }
    }

    private static void Unpack(ulong[] b64, uint[] b32)
    {
        for (int i = 0; i < 8; i++)
        {
            b32[2 * i] = (uint)b64[i];
            b32[(2 * i) + 1] = (uint)(b64[i] >> 32);
        }
    }

    private static float BitsToFloat(uint bits) => BitConverter.UInt32BitsToSingle((bits >> 9) | 0x3F800000u);

}
