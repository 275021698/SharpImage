namespace SharpImage.Formats.WebP;

// quant_enc.c and cost_enc.c
internal sealed partial class Vp8Encoder
{
    private const bool DoTrellisI4 = true;
    private const bool DoTrellisI16 = true;   // not a huge gain, but ok at low bitrate.
    private const bool DoTrellisUv = false;   // disable trellis for UV. Risky. Not worth.

    private const int MidAlpha = 64;       // neutral value for susceptibility
    private const int MinAlpha = 30;       // lowest usable value for susceptibility
    private const int MaxAlphaQ = 100;     // higher meaningful value for susceptibility
    private const double SnsToDq = 0.9;    // Scaling constant between the sns value and the QP

    private const int FlatnessLimitI16 = 0;       // I16 mode (special case)
    private const int FlatnessLimitI4 = 3;        // I4 mode
    private const int FlatnessLimitUv = 2;        // UV mode
    private const int FlatnessPenalty = 140;      // roughly ~1bit per block

    private const int RdDistoMult = 256;  // distortion multiplier (equivalent of lambda)

    private static int Mult8b(int a, int b) => (a * b + 128) >> 8;

    private static readonly byte[][] BiasMatrices =   // [luma-ac,luma-dc,chroma][dc,ac]
    [
        [96, 110], [96, 108], [110, 115],
    ];

    private const int SharpenBits = 11;  // number of descaling bits for sharpening bias
    private static readonly byte[] FreqSharpening = [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

    private static int ExpandMatrix(Vp8Matrix m, int type)
    {
        int i, sum;
        for (i = 0; i < 2; ++i)
        {
            bool isAcCoeff = i > 0;
            int bias = BiasMatrices[type][isAcCoeff ? 1 : 0];
            m.IQ[i] = (ushort)((1 << Vp8EncDsp.QFix) / m.Q[i]);
            m.Bias[i] = (uint)Vp8EncDsp.Bias(bias);
            // zthresh is the exact value such that QUANTDIV(coeff, iQ, B) is:
            //   * zero if coeff <= zthresh
            //   * non-zero if coeff > zthresh
            m.ZThresh[i] = ((1u << Vp8EncDsp.QFix) - 1 - m.Bias[i]) / m.IQ[i];
        }
        for (i = 2; i < 16; ++i)
        {
            m.Q[i] = m.Q[1];
            m.IQ[i] = m.IQ[1];
            m.Bias[i] = m.Bias[1];
            m.ZThresh[i] = m.ZThresh[1];
        }
        for (sum = 0, i = 0; i < 16; ++i)
        {
            if (type == 0)
            {
                // we only use sharpening for AC luma coeffs
                m.Sharpen[i] = (ushort)((FreqSharpening[i] * m.Q[i]) >> SharpenBits);
            }
            else
            {
                m.Sharpen[i] = 0;
            }
            sum += m.Q[i];
        }
        return (sum + 8) >> 4;
    }

    private static void CheckLambdaValue(ref int v)
    {
        if (v < 1) v = 1;
    }

    private void SetupMatrices()
    {
        int tlambdaScale = _method >= 4 ? _config.SnsStrength : 0;
        int numSegments = _numSegments;
        for (int i = 0; i < numSegments; ++i)
        {
            var m = _dqm[i];
            int q = m.Quant;
            m.Y1.Q[0] = Vp8EncTables.DcTable[Clip(q + _dqY1Dc, 0, 127)];
            m.Y1.Q[1] = Vp8EncTables.AcTable[Clip(q, 0, 127)];

            m.Y2.Q[0] = (ushort)(Vp8EncTables.DcTable[Clip(q + _dqY2Dc, 0, 127)] * 2);
            // For all x in [0..284], x*155/100 is bitwise equal to (x*101581) >> 16.
            // The smallest precision for that is '(x*6349) >> 12' but 16 is a good word size.
            m.Y2.Q[1] = Vp8EncTables.AcTable2[Clip(q + _dqY2Ac, 0, 127)];

            m.Uv.Q[0] = Vp8EncTables.DcTable[Clip(q + _dqUvDc, 0, 117)];
            m.Uv.Q[1] = Vp8EncTables.AcTable[Clip(q + _dqUvAc, 0, 127)];

            int qI4 = ExpandMatrix(m.Y1, 0);
            int qI16 = ExpandMatrix(m.Y2, 1);
            int qUv = ExpandMatrix(m.Uv, 2);

            m.LambdaI4 = (3 * qI4 * qI4) >> 7;
            m.LambdaI16 = 3 * qI16 * qI16;
            m.LambdaUv = (3 * qUv * qUv) >> 6;
            m.LambdaMode = (1 * qI4 * qI4) >> 7;
            m.LambdaTrellisI4 = (7 * qI4 * qI4) >> 3;
            m.LambdaTrellisI16 = (qI16 * qI16) >> 2;
            m.LambdaTrellisUv = (qUv * qUv) << 1;
            m.TLambda = (tlambdaScale * qI4) >> 5;

            // none of these constants should be < 1
            CheckLambdaValue(ref m.LambdaI4);
            CheckLambdaValue(ref m.LambdaI16);
            CheckLambdaValue(ref m.LambdaUv);
            CheckLambdaValue(ref m.LambdaMode);
            CheckLambdaValue(ref m.LambdaTrellisI4);
            CheckLambdaValue(ref m.LambdaTrellisI16);
            CheckLambdaValue(ref m.LambdaTrellisUv);
            CheckLambdaValue(ref m.TLambda);

            m.MinDisto = 20 * m.Y1.Q[0];   // quantization-aware min disto
            m.MaxEdge = 0;

            m.I4Penalty = 1000 * qI4 * qI4;
        }
    }

    private const int FStrengthCutoff = 2;

    private void SetupFilterStrength()
    {
        int level0 = 5 * _config.FilterStrength;
        for (int i = 0; i < NumMbSegments; ++i)
        {
            var m = _dqm[i];
            // We focus on the quantization of AC coeffs.
            int qstep = Vp8EncTables.AcTable[Clip(m.Quant, 0, 127)] >> 2;
            int baseStrength = FilterStrengthFromDelta(_filterSharpness, qstep);
            // Segments with lower complexity ('beta') will be less filtered.
            int f = baseStrength * level0 / (256 + m.Beta);
            m.FStrength = f < FStrengthCutoff ? 0 : f > 63 ? 63 : f;
        }
        // We record the initial strength (mainly for the case of 1-segment only).
        _filterLevel = _dqm[0].FStrength;
        _filterSimple = _config.FilterType == 0;
        _filterSharpness = _config.FilterSharpness;
    }

    private const int MaxDqUv = 6, MinDqUv = -4;

    private static double QualityToCompression(double c)
    {
        double linearC = c < 0.75 ? c * (2.0 / 3.0) : 2.0 * c - 1.0;
        return Math.Pow(linearC, 1 / 3.0);
    }

    private static double QualityToJpegCompression(double c, double alpha)
    {
        // We map the complexity 'alpha' and quality setting 'c' to a compression
        // exponent empirically matched to the compression curve of libjpeg6b.
        const double amin = 0.30;
        const double amax = 0.85;
        const double expMin = 0.4;
        const double expMax = 0.9;
        const double slope = (expMin - expMax) / (amax - amin);
        // Linearly interpolate 'expn' from exp_min to exp_max
        // in the [amin, amax] range.
        double expn = alpha > amax ? expMin
                    : alpha < amin ? expMax
                    : expMax + slope * (alpha - amin);
        return Math.Pow(c, expn);
    }

    private static bool SegmentsAreEquivalent(Vp8SegmentInfo s1, Vp8SegmentInfo s2) =>
        s1.Quant == s2.Quant && s1.FStrength == s2.FStrength;

    private void SimplifySegments()
    {
        Span<int> map = [0, 1, 2, 3];
        int numSegments = _numSegments < NumMbSegments ? _numSegments : NumMbSegments;
        int numFinalSegments = 1;
        int s1, s2;
        for (s1 = 1; s1 < numSegments; ++s1)
        {
            // find similar segments
            var S1 = _dqm[s1];
            bool found = false;
            // check if we already have similar segment
            for (s2 = 0; s2 < numFinalSegments; ++s2)
            {
                var S2 = _dqm[s2];
                if (SegmentsAreEquivalent(S1, S2))
                {
                    found = true;
                    break;
                }
            }
            map[s1] = s2;
            if (!found)
            {
                if (numFinalSegments != s1) _dqm[numFinalSegments].CopyFrom(_dqm[s1]);
                ++numFinalSegments;
            }
        }
        if (numFinalSegments < numSegments)
        {
            // Remap
            int i = _mbW * _mbH;
            while (i-- > 0) _mbInfo[i].Segment = (byte)map[_mbInfo[i].Segment];
            _numSegments = numFinalSegments;
            // Replicate the trailing segment infos (it's mostly cosmetics)
            for (i = numFinalSegments; i < numSegments; ++i) _dqm[i].CopyFrom(_dqm[numFinalSegments - 1]);
        }
    }

    /// <summary>VP8SetSegmentParams.</summary>
    private void SetSegmentParams(float quality)
    {
        int numSegments = _numSegments;
        double amp = SnsToDq * _config.SnsStrength / 100.0 / 128.0;
        double Q = quality / 100.0;
        double cBase = _config.EmulateJpegSize
            ? QualityToJpegCompression(Q, _alpha / 255.0)
            : QualityToCompression(Q);
        for (int i = 0; i < numSegments; ++i)
        {
            // We modulate the base coefficient to accommodate for the quantization
            // susceptibility and allow denser segments to be quantized more.
            double expn = 1.0 - amp * _dqm[i].Alpha;
            double c = Math.Pow(cBase, expn);
            int q = (int)(127.0 * (1.0 - c));
            _dqm[i].Quant = Clip(q, 0, 127);
        }

        // purely indicative in the bitstream (except for the 1-segment case)
        _baseQuant = _dqm[0].Quant;

        // fill-in values for the unused segments (required by the syntax)
        for (int i = numSegments; i < NumMbSegments; ++i) _dqm[i].Quant = _baseQuant;

        // uv_alpha is normally spread around ~60. The useful range is
        // typically ~30 (quite bad) to ~100 (ok to decimate UV more).
        // We map it to the safe maximal range of MAX/MIN_DQ_UV for dq_uv.
        int dqUvAc = (_uvAlpha - MidAlpha) * (MaxDqUv - MinDqUv) / (MaxAlphaQ - MinAlpha);
        // we rescale by the user-defined strength of adaptation
        dqUvAc = dqUvAc * _config.SnsStrength / 100;
        // and make it safe.
        dqUvAc = Clip(dqUvAc, MinDqUv, MaxDqUv);
        // We also boost the dc-uv-quant a little, based on sns-strength, since
        // U/V channels are quite more reactive to high quants (flat DC-blocks
        // tend to appear, and are unpleasant).
        int dqUvDc = -4 * _config.SnsStrength / 100;
        dqUvDc = Clip(dqUvDc, -15, 15);   // 4bit-signed max allowed

        _dqY1Dc = 0;       // TODO(skal): dq-lum
        _dqY2Dc = 0;
        _dqY2Ac = 0;
        _dqUvDc = dqUvDc;
        _dqUvAc = dqUvAc;

        SetupFilterStrength();   // initialize segments' filtering, eventually

        if (numSegments > 1) SimplifySegments();

        SetupMatrices();         // finalize quantization matrices
    }

    // ---- Form the predictions in cache --------------------------------------------------------------------------------

    private static readonly int[] I16ModeOffsets =
        [Vp8EncDsp.I16DC16, Vp8EncDsp.I16TM16, Vp8EncDsp.I16VE16, Vp8EncDsp.I16HE16];

    private static readonly int[] UvModeOffsets = [Vp8EncDsp.C8DC8, Vp8EncDsp.C8TM8, Vp8EncDsp.C8VE8, Vp8EncDsp.C8HE8];

    private static readonly int[] I4ModeOffsets =
    [
        Vp8EncDsp.I4DC4, Vp8EncDsp.I4TM4, Vp8EncDsp.I4VE4, Vp8EncDsp.I4HE4, Vp8EncDsp.I4RD4,
        Vp8EncDsp.I4VR4, Vp8EncDsp.I4LD4, Vp8EncDsp.I4VL4, Vp8EncDsp.I4HD4, Vp8EncDsp.I4HU4,
    ];

    private static void MakeLuma16Preds(Iterator it)
    {
        byte[]? left = it.X != 0 ? it.Left : null;
        byte[]? top = it.Y != 0 ? it.TopBuf : null;
        Vp8EncDsp.Intra16Preds(it.Yuv, Iterator.YuvP, left, Iterator.YLeft, top, it.YTop);
    }

    private static void MakeChroma8Preds(Iterator it)
    {
        byte[]? left = it.X != 0 ? it.Left : null;
        byte[]? top = it.Y != 0 ? it.TopBuf : null;
        Vp8EncDsp.IntraChromaPreds(it.Yuv, Iterator.YuvP, left, Iterator.ULeft, top, it.UvTop);
    }

    private static void MakeIntra4Preds(Iterator it) =>
        Vp8EncDsp.Intra4Preds(it.Yuv, Iterator.YuvP, it.I4Boundary, it.I4Top);

    // VP8Scan / VP8ScanUV
    private static int Scan(int n) => Vp8EncDsp.DspScan[n];
    private static int ScanUv(int n) => Vp8EncDsp.DspScan[16 + n];

    // Distortion measurement
    private static readonly ushort[] WeightY = [38, 32, 20, 9, 32, 28, 17, 7, 20, 17, 10, 4, 9, 7, 4, 2];
    private static readonly ushort[] WeightTrellis = [30, 27, 19, 11, 27, 24, 17, 10, 19, 17, 12, 8, 11, 10, 8, 6];

    private static void InitScore(Vp8ModeScore rd)
    {
        rd.D = 0;
        rd.SD = 0;
        rd.R = 0;
        rd.H = 0;
        rd.Nz = 0;
        rd.Score = MaxCost;
    }

    private static void SetRDScore(int lambda, Vp8ModeScore rd) =>
        rd.Score = (rd.R + rd.H) * lambda + RdDistoMult * (rd.D + rd.SD);

    private static long RDScoreTrellis(int lambda, long rate, long distortion) => rate * lambda + RdDistoMult * distortion;

    // Coefficient type.
    private const int TypeI16Ac = 0, TypeI16Dc = 1, TypeChromaA = 2, TypeI4Ac = 3;

    // Trellis node
    private struct Node
    {
        public sbyte Prev;   // best previous node
        public sbyte Sign;   // sign of coeff_i
        public short Level;  // level
    }

    private const int MinDelta = 0;   // how much lower level to try
    private const int MaxDelta = 1;   // how much higher
    private const int NumNodes = MinDelta + 1 + MaxDelta;

    private readonly Node[] _trellisNodes = new Node[16 * NumNodes];
    private readonly long[] _ssScore = new long[2 * NumNodes];
    private readonly int[] _ssCosts = new int[2 * NumNodes];

    private int LevelCost(int table, int level) =>
        Vp8EncTables.LevelFixedCosts[level] +
        _proba.LevelCost[table + (level > Vp8EncProba.MaxVariableLevel ? Vp8EncProba.MaxVariableLevel : level)];

    private static int BitCost(int bit, int proba) =>
        bit == 0 ? Vp8EncTables.EntropyCost[proba] : Vp8EncTables.EntropyCost[255 - proba];

    private int TrellisQuantizeBlock(Span<short> input, Span<short> output, int ctx0, int coeffType, Vp8Matrix mtx,
        int lambda)
    {
        int probas = Vp8EncProba.ProbaIdx(coeffType, 0, 0);   // probas[band][ctx][p] = Coeffs[probas + (band*3+ctx)*11 + p]
        var coeffs = _proba.Coeffs;
        int first = coeffType == TypeI16Ac ? 1 : 0;
        var nodes = _trellisNodes;
        var ssScore = _ssScore;
        var ssCosts = _ssCosts;
        int ssCur = 0;            // SCORE_STATE(0, MIN_DELTA): index into the score states
        int ssPrev = NumNodes;    // SCORE_STATE(1, MIN_DELTA)
        int bestPath0 = -1, bestPath1 = -1, bestPath2 = -1;   // store best-last/best-level/best-previous
        long bestScore;
        int n, m, p, last;

        {
            // compute the position of the last interesting coefficient
            int thresh = mtx.Q[1] * mtx.Q[1] / 4;
            int lastProba = coeffs[probas + (Vp8EncBands[first] * 3 + ctx0) * 11 + 0];
            last = first - 1;
            for (n = 15; n >= first; --n)
            {
                int j = Vp8EncDsp.Zigzag[n];
                int err = input[j] * input[j];
                if (err > thresh)
                {
                    last = n;
                    break;
                }
            }
            // we don't need to go inspect up to n = 16 coeffs. We can just go up
            // to last + 1 (inclusive) without losing much.
            if (last < 15) ++last;

            // compute 'skip' score. This is the max score one can do.
            long cost = BitCost(0, lastProba);
            bestScore = RDScoreTrellis(lambda, cost, 0);

            // initialize source node.
            for (m = -MinDelta; m <= MaxDelta; ++m)
            {
                long rate = ctx0 == 0 ? BitCost(1, lastProba) : 0;
                ssScore[ssCur + m] = RDScoreTrellis(lambda, rate, 0);
                ssCosts[ssCur + m] = _proba.Costs(coeffType, first, ctx0);
            }
        }

        // traverse trellis.
        for (n = first; n <= last; ++n)
        {
            int j = Vp8EncDsp.Zigzag[n];
            uint Q = mtx.Q[j];
            uint iQ = mtx.IQ[j];
            uint B = (uint)Vp8EncDsp.Bias(0x00);     // neutral bias
            // note: it's important to take sign of the _original_ coeff,
            // so we don't have to consider level < 0 afterward.
            bool sign = input[j] < 0;
            uint coeff0 = (uint)(sign ? -input[j] : input[j]) + mtx.Sharpen[j];
            int level0 = Vp8EncDsp.QuantDiv(coeff0, iQ, B);
            int threshLevel = Vp8EncDsp.QuantDiv(coeff0, iQ, (uint)Vp8EncDsp.Bias(0x80));
            if (threshLevel > Vp8EncDsp.MaxLevel) threshLevel = Vp8EncDsp.MaxLevel;
            if (level0 > Vp8EncDsp.MaxLevel) level0 = Vp8EncDsp.MaxLevel;

            {
                // Swap current and previous score states
                (ssCur, ssPrev) = (ssPrev, ssCur);
            }

            // test all alternate level values around level0.
            for (m = -MinDelta; m <= MaxDelta; ++m)
            {
                int cur = n * NumNodes + m + MinDelta;
                int level = level0 + m;
                int ctx = level > 2 ? 2 : level;
                int band = Vp8EncBands[n + 1];
                long baseScore;
                long bestCurScore;
                int bestPrev;
                long cost, score;

                ssCosts[ssCur + m] = n + 1 < 16 ? _proba.Costs(coeffType, n + 1, ctx) : 0;
                if (level < 0 || level > threshLevel)
                {
                    ssScore[ssCur + m] = MaxCost;
                    // Node is dead.
                    continue;
                }

                {
                    // Compute delta_error = how much coding this level will
                    // subtract to max_error as distortion.
                    // Here, distortion = sum of (|coeff_i| - level_i * Q_i)^2
                    int newError = (int)(coeff0 - (uint)level * Q);
                    int deltaError = WeightTrellis[j] * (newError * newError - (int)coeff0 * (int)coeff0);
                    baseScore = RDScoreTrellis(lambda, 0, deltaError);
                }

                // Inspect all possible non-dead predecessors. Retain only the best one.
                // The base_score is added to all scores so it is only added for the final
                // value after the loop.
                cost = LevelCost(ssCosts[ssPrev - MinDelta], level);
                bestCurScore = ssScore[ssPrev - MinDelta] + RDScoreTrellis(lambda, cost, 0);
                bestPrev = -MinDelta;
                for (p = -MinDelta + 1; p <= MaxDelta; ++p)
                {
                    // Dead nodes (with ss_prev[p].score >= MAX_COST) are automatically
                    // eliminated since their score can't be better than the current best.
                    cost = LevelCost(ssCosts[ssPrev + p], level);
                    // Examine node assuming it's a non-terminal one.
                    score = ssScore[ssPrev + p] + RDScoreTrellis(lambda, cost, 0);
                    if (score < bestCurScore)
                    {
                        bestCurScore = score;
                        bestPrev = p;
                    }
                }
                bestCurScore += baseScore;
                // Store best finding in current node.
                nodes[cur].Sign = (sbyte)(sign ? 1 : 0);
                nodes[cur].Level = (short)level;
                nodes[cur].Prev = (sbyte)bestPrev;
                ssScore[ssCur + m] = bestCurScore;

                // Now, record best terminal node (and thus best entry in the graph).
                if (level != 0 && bestCurScore < bestScore)
                {
                    long lastPosCost = n < 15 ? BitCost(0, coeffs[probas + (band * 3 + ctx) * 11 + 0]) : 0;
                    long lastPosScore = RDScoreTrellis(lambda, lastPosCost, 0);
                    score = bestCurScore + lastPosScore;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPath0 = n;                     // best eob position
                        bestPath1 = m;                     // best node index
                        bestPath2 = bestPrev;              // best predecessor
                    }
                }
            }
        }

        // Fresh start
        // Beware! We must preserve in[0]/out[0] value for TYPE_I16_AC case.
        if (coeffType == TypeI16Ac)
        {
            input.Slice(1, 15).Clear();
            output.Slice(1, 15).Clear();
        }
        else
        {
            input.Slice(0, 16).Clear();
            output.Slice(0, 16).Clear();
        }
        if (bestPath0 == -1) return 0;  // skip!

        {
            // Unwind the best path.
            // Note: best-prev on terminal node is not necessarily equal to the
            // best_prev for non-terminal. So we patch best_path[2] in.
            int nz = 0;
            int bestNode = bestPath1;
            n = bestPath0;
            nodes[n * NumNodes + bestNode + MinDelta].Prev = (sbyte)bestPath2;   // force best-prev for terminal

            for (; n >= first; --n)
            {
                var node = nodes[n * NumNodes + bestNode + MinDelta];
                int j = Vp8EncDsp.Zigzag[n];
                output[n] = (short)(node.Sign != 0 ? -node.Level : node.Level);
                nz |= node.Level;
                input[j] = (short)(output[n] * mtx.Q[j]);
                bestNode = node.Prev;
            }
            return nz != 0 ? 1 : 0;
        }
    }

    // ---- Performs: difference, transform, quantize, back-transform, add -----------------------------------------------
    // all at once. Output is the reconstructed block in *yuv_out, and the
    // quantized levels in *levels.

    private readonly short[] _tmp16x16 = new short[16 * 16];
    private readonly short[] _dcTmp = new short[16];

    private int ReconstructIntra16(Iterator it, Vp8ModeScore rd, int yuvOut, int mode)
    {
        int refOff = Iterator.YuvP + I16ModeOffsets[mode];
        int src = Iterator.YuvIn + YOffEnc;
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        uint nz = 0;
        int n;
        var tmp = _tmp16x16;
        var dcTmp = _dcTmp;
        var yuv = it.Yuv;

        for (n = 0; n < 16; n += 2)
            Vp8EncDsp.FTransform2(yuv, src + Scan(n), yuv, refOff + Scan(n), tmp.AsSpan(n * 16, 32));
        Vp8EncDsp.FTransformWHT(tmp, dcTmp);
        nz |= (uint)Vp8EncDsp.QuantizeBlock(dcTmp, rd.YDcLevels, dqm.Y2) << 24;

        if (DoTrellisI16 && it.DoTrellis)
        {
            int x, y;
            it.NzToBytes();
            for (y = 0, n = 0; y < 4; ++y)
            {
                for (x = 0; x < 4; ++x, ++n)
                {
                    int ctx = it.TopNz[x] + it.LeftNz[y];
                    int nonZero = TrellisQuantizeBlock(tmp.AsSpan(n * 16, 16), rd.YAcLevels.AsSpan(n * 16, 16), ctx,
                        TypeI16Ac, dqm.Y1, dqm.LambdaTrellisI16);
                    it.TopNz[x] = it.LeftNz[y] = nonZero;
                    rd.YAcLevels[n * 16] = 0;
                    nz |= (uint)nonZero << n;
                }
            }
        }
        else
        {
            for (n = 0; n < 16; n += 2)
            {
                // Zero-out the first coeff, so that: a) nz is correct below, and
                // b) finding 'last' non-zero coeffs in SetResidualCoeffs() is simplified.
                tmp[n * 16] = tmp[(n + 1) * 16] = 0;
                nz |= (uint)Vp8EncDsp.Quantize2Blocks(tmp.AsSpan(n * 16, 32), rd.YAcLevels.AsSpan(n * 16, 32), dqm.Y1) << n;
            }
        }

        // Transform back
        Vp8EncDsp.TransformWHT(dcTmp, tmp);
        for (n = 0; n < 16; n += 2)
            Vp8EncDsp.ITransform(yuv, refOff + Scan(n), tmp.AsSpan(n * 16, 32), yuv, yuvOut + Scan(n), true);

        return (int)nz;
    }

    private readonly short[] _tmp16 = new short[16];

    private int ReconstructIntra4(Iterator it, Span<short> levels, int src, int yuvOut, int mode)
    {
        int refOff = Iterator.YuvP + I4ModeOffsets[mode];
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        int nz;
        var tmp = _tmp16;
        var yuv = it.Yuv;

        Vp8EncDsp.FTransform(yuv, src, yuv, refOff, tmp);
        if (DoTrellisI4 && it.DoTrellis)
        {
            int x = it.I4 & 3, y = it.I4 >> 2;
            int ctx = it.TopNz[x] + it.LeftNz[y];
            nz = TrellisQuantizeBlock(tmp, levels, ctx, TypeI4Ac, dqm.Y1, dqm.LambdaTrellisI4);
        }
        else
        {
            nz = Vp8EncDsp.QuantizeBlock(tmp, levels, dqm.Y1);
        }
        Vp8EncDsp.ITransform(yuv, refOff, tmp, yuv, yuvOut, false);

        return nz;
    }

    // Quantize as usual, but also compute and return the quantization error.
    // Error is already divided by DSHIFT.
    private const int C1 = 7;    // fraction of error sent to the 4x4 block below
    private const int C2 = 8;    // fraction of error sent to the 4x4 block on the right
    private const int DShift = 4;
    private const int DScale = 1;   // storage descaling, needed to make the error fit int8_t

    private static int QuantizeSingle(ref short v, Vp8Matrix mtx)
    {
        int V = v;
        bool sign = V < 0;
        if (sign) V = -V;
        if (V > (int)mtx.ZThresh[0])
        {
            int qV = Vp8EncDsp.QuantDiv((uint)V, mtx.IQ[0], mtx.Bias[0]) * mtx.Q[0];
            int err = V - qV;
            v = (short)(sign ? -qV : qV);
            return (sign ? -err : err) >> DScale;
        }
        v = 0;
        return (sign ? -V : V) >> DScale;
    }

    private void CorrectDCValues(Iterator it, Vp8Matrix mtx, short[] tmp, Vp8ModeScore rd)
    {
        //         | top[0] | top[1]
        // --------+--------+---------
        // left[0] | tmp[0]   tmp[1]  <->   err0 err1
        // left[1] | tmp[2]   tmp[3]        err2 err3
        //
        // Final errors {err1,err2,err3} are preserved and later restored
        // as top[]/left[] on the next block.
        var topDerr = _topDerr!;
        for (int ch = 0; ch <= 1; ++ch)
        {
            int top = (it.X * 2 + ch) * 2;     // it->top_derr[it->x][ch]
            int left = ch * 2;                 // it->left_derr[ch]
            int c = ch * 4 * 16;               // tmp[ch * 4]
            int err0, err1, err2, err3;
            tmp[c + 0 * 16] += (short)((C1 * topDerr[top + 0] + C2 * it.LeftDerr[left + 0]) >> (DShift - DScale));
            err0 = QuantizeSingle(ref tmp[c + 0 * 16], mtx);
            tmp[c + 1 * 16] += (short)((C1 * topDerr[top + 1] + C2 * err0) >> (DShift - DScale));
            err1 = QuantizeSingle(ref tmp[c + 1 * 16], mtx);
            tmp[c + 2 * 16] += (short)((C1 * err0 + C2 * it.LeftDerr[left + 1]) >> (DShift - DScale));
            err2 = QuantizeSingle(ref tmp[c + 2 * 16], mtx);
            tmp[c + 3 * 16] += (short)((C1 * err1 + C2 * err2) >> (DShift - DScale));
            err3 = QuantizeSingle(ref tmp[c + 3 * 16], mtx);
            rd.Derr[ch * 3 + 0] = (sbyte)err1;
            rd.Derr[ch * 3 + 1] = (sbyte)err2;
            rd.Derr[ch * 3 + 2] = (sbyte)err3;
        }
    }

    private void StoreDiffusionErrors(Iterator it, Vp8ModeScore rd)
    {
        var topDerr = _topDerr!;
        for (int ch = 0; ch <= 1; ++ch)
        {
            int top = (it.X * 2 + ch) * 2;
            int left = ch * 2;
            it.LeftDerr[left + 0] = rd.Derr[ch * 3 + 0];                         // restore err1
            it.LeftDerr[left + 1] = (sbyte)((3 * rd.Derr[ch * 3 + 2]) >> 2);     //     ... 3/4th of err3
            topDerr[top + 0] = rd.Derr[ch * 3 + 1];                              //     ... err2
            topDerr[top + 1] = (sbyte)(rd.Derr[ch * 3 + 2] - it.LeftDerr[left + 1]);   //     ... 1/4th of err3.
        }
    }

    private readonly short[] _tmpUv = new short[8 * 16];

    private int ReconstructUV(Iterator it, Vp8ModeScore rd, int yuvOut, int mode)
    {
        int refOff = Iterator.YuvP + UvModeOffsets[mode];
        int src = Iterator.YuvIn + UOffEnc;
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        int nz = 0;
        int n;
        var tmp = _tmpUv;
        var yuv = it.Yuv;

        for (n = 0; n < 8; n += 2)
            Vp8EncDsp.FTransform2(yuv, src + ScanUv(n), yuv, refOff + ScanUv(n), tmp.AsSpan(n * 16, 32));
        if (it.HasDerr) CorrectDCValues(it, dqm.Uv, tmp, rd);

        if (DoTrellisUv && it.DoTrellis)
        {
            // (disabled)
        }
        else
        {
            for (n = 0; n < 8; n += 2)
                nz |= Vp8EncDsp.Quantize2Blocks(tmp.AsSpan(n * 16, 32), rd.UvLevels.AsSpan(n * 16, 32), dqm.Uv) << n;
        }

        for (n = 0; n < 8; n += 2)
            Vp8EncDsp.ITransform(yuv, refOff + ScanUv(n), tmp.AsSpan(n * 16, 32), yuv, yuvOut + ScanUv(n), true);
        return nz << 16;
    }

    // ---- RD-opt decision. Reconstruct each modes, evalue distortion and bit-cost. -----------------------------------
    // Pick the mode is lower RD-cost = Rate + lambda * Distortion.

    private static void StoreMaxDelta(Vp8SegmentInfo dqm, short[] dcs)
    {
        // We look at the first three AC coefficients to determine what is the average
        // delta between each sub-4x4 block.
        int v0 = Math.Abs((int)dcs[1]);
        int v1 = Math.Abs((int)dcs[2]);
        int v2 = Math.Abs((int)dcs[4]);
        int maxV = v1 > v0 ? v1 : v0;
        maxV = v2 > maxV ? v2 : maxV;
        if (maxV > dqm.MaxEdge) dqm.MaxEdge = maxV;
    }

    private static void SwapOut(Iterator it) => (it.YuvOut, it.YuvOut2) = (it.YuvOut2, it.YuvOut);

    private readonly Vp8ModeScore _rdTmp16 = new();
    private readonly Vp8ModeScore _rdBestI4 = new();
    private readonly Vp8ModeScore _rdUv = new();
    private readonly short[] _tmpLevels = new short[16];

    private void PickBestIntra16(Iterator it, Vp8ModeScore rd)
    {
        const int kNumBlocks = 16;
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        int lambda = dqm.LambdaI16;
        int tlambda = dqm.TLambda;
        int src = Iterator.YuvIn + YOffEnc;
        var rdCur = _rdTmp16;
        var rdBest = rd;
        var yuv = it.Yuv;
        bool isFlat = Vp8EncDsp.IsFlatSource16(yuv, Iterator.YuvIn + YOffEnc);

        rd.ModeI16 = -1;
        for (int mode = 0; mode < NumPredModes; ++mode)
        {
            int tmpDst = it.YuvOut2 + YOffEnc;  // scratch buffer
            rdCur.ModeI16 = mode;

            // Reconstruct
            rdCur.Nz = (uint)ReconstructIntra16(it, rdCur, tmpDst, mode);

            // Measure RD-score
            rdCur.D = Vp8EncDsp.Sse16x16(yuv, src, yuv, tmpDst);
            rdCur.SD = tlambda != 0 ? Mult8b(tlambda, Vp8EncDsp.TDisto16x16(yuv, src, yuv, tmpDst, WeightY)) : 0;
            rdCur.H = FixedCostsI16[mode];
            rdCur.R = GetCostLuma16(it, rdCur);
            if (isFlat)
            {
                // refine the first impression (which was in pixel space)
                isFlat = Vp8EncDsp.IsFlat(rdCur.YAcLevels, kNumBlocks, FlatnessLimitI16);
                if (isFlat)
                {
                    // Block is very flat. We put emphasis on the distortion being very low!
                    rdCur.D *= 2;
                    rdCur.SD *= 2;
                }
            }

            // Since we always examine Intra16 first, we can overwrite *rd directly.
            SetRDScore(lambda, rdCur);
            if (mode == 0 || rdCur.Score < rdBest.Score)
            {
                (rdCur, rdBest) = (rdBest, rdCur);
                SwapOut(it);
            }
        }
        if (rdBest != rd) rd.CopyAllFrom(rdBest);
        SetRDScore(dqm.LambdaMode, rd);   // finalize score for mode decision.
        it.SetIntra16Mode(rd.ModeI16);

        // we have a blocky macroblock (only DCs are non-zero) with fairly high
        // distortion, record max delta so we can later adjust the minimal filtering
        // strength needed to smooth these blocks out.
        if ((rd.Nz & 0x100ffff) == 0x1000000 && rd.D > dqm.MinDisto) StoreMaxDelta(dqm, rd.YDcLevels);
    }

    // return the cost array corresponding to the surrounding prediction modes.
    private int GetCostModeI4(Iterator it, byte[] modes)
    {
        int predsW = _predsW;
        int x = it.I4 & 3, y = it.I4 >> 2;
        int left = x == 0 ? _predsMem[it.Preds + y * predsW - 1] : modes[it.I4 - 1];
        int top = y == 0 ? _predsMem[it.Preds - predsW + x] : modes[it.I4 - 4];
        return (top * NumBModes + left) * NumBModes;   // VP8FixedCostsI4[top][left]
    }

    private bool PickBestIntra4(Iterator it, Vp8ModeScore rd)
    {
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        int lambda = dqm.LambdaI4;
        int tlambda = dqm.TLambda;
        int src0 = Iterator.YuvIn + YOffEnc;
        int bestBlocks = it.YuvOut2 + YOffEnc;
        int totalHeaderBits = 0;
        var rdBest = _rdBestI4;
        var yuv = it.Yuv;
        var tmpLevels = _tmpLevels;

        if (_maxI4HeaderBits == 0) return false;

        InitScore(rdBest);
        rdBest.H = 211;  // '211' is the value of VP8BitCost(0, 145)
        SetRDScore(dqm.LambdaMode, rdBest);
        it.StartI4();
        do
        {
            const int kNumBlocks = 1;
            // rd_i4
            long i4D = 0, i4SD = 0, i4R = 0, i4H = 0, i4Score = MaxCost;
            uint i4Nz = 0;
            int bestMode = -1;
            int src = src0 + Scan(it.I4);
            int modeCosts = GetCostModeI4(it, rd.ModesI4);
            int bestBlock = bestBlocks + Scan(it.I4);
            int tmpDst = Iterator.YuvP + Vp8EncDsp.I4TMP;    // scratch buffer.

            MakeIntra4Preds(it);
            for (int mode = 0; mode < NumBModes; ++mode)
            {
                // Reconstruct
                uint tNz = (uint)ReconstructIntra4(it, tmpLevels, src, tmpDst, mode) << it.I4;

                // Compute RD-score
                long tD = Vp8EncDsp.Sse4x4(yuv, src, yuv, tmpDst);
                long tSD = tlambda != 0 ? Mult8b(tlambda, Vp8EncDsp.TDisto4x4(yuv, src, yuv, tmpDst, WeightY)) : 0;
                long tH = Vp8EncTables.FixedCostsI4[modeCosts + mode];

                // Add flatness penalty, to avoid flat area to be mispredicted
                // by a complex mode.
                long tR;
                if (mode > 0 && Vp8EncDsp.IsFlat(tmpLevels, kNumBlocks, FlatnessLimitI4))
                    tR = FlatnessPenalty * kNumBlocks;
                else
                    tR = 0;

                // early-out check
                long tScore = (tR + tH) * lambda + RdDistoMult * (tD + tSD);
                if (bestMode >= 0 && tScore >= i4Score) continue;

                // finish computing score
                tR += GetCostLuma4(it, tmpLevels);
                tScore = (tR + tH) * lambda + RdDistoMult * (tD + tSD);

                if (bestMode < 0 || tScore < i4Score)
                {
                    i4D = tD;
                    i4SD = tSD;
                    i4R = tR;
                    i4H = tH;
                    i4Nz = tNz;
                    i4Score = tScore;
                    bestMode = mode;
                    (tmpDst, bestBlock) = (bestBlock, tmpDst);
                    tmpLevels.CopyTo(rdBest.YAcLevels, it.I4 * 16);
                }
            }
            i4Score = (i4R + i4H) * dqm.LambdaMode + RdDistoMult * (i4D + i4SD);   // SetRDScore(lambda_mode, &rd_i4)
            // AddScore(&rd_best, &rd_i4)
            rdBest.D += i4D;
            rdBest.SD += i4SD;
            rdBest.R += i4R;
            rdBest.H += i4H;
            rdBest.Nz |= i4Nz;
            rdBest.Score += i4Score;
            if (rdBest.Score >= rd.Score) return false;
            totalHeaderBits += (int)i4H;   // <- equal to mode_costs[best_mode];
            if (totalHeaderBits > _maxI4HeaderBits) return false;
            // Copy selected samples if not in the right place already.
            if (bestBlock != bestBlocks + Scan(it.I4))
                Vp8EncDsp.Copy(yuv, bestBlock, yuv, bestBlocks + Scan(it.I4), 4, 4);
            rd.ModesI4[it.I4] = (byte)bestMode;
            it.TopNz[it.I4 & 3] = it.LeftNz[it.I4 >> 2] = i4Nz != 0 ? 1 : 0;
        } while (it.RotateI4(bestBlocks));

        // finalize state
        // CopyScore(rd, &rd_best)
        rd.D = rdBest.D;
        rd.SD = rdBest.SD;
        rd.R = rdBest.R;
        rd.H = rdBest.H;
        rd.Nz = rdBest.Nz;
        rd.Score = rdBest.Score;
        it.SetIntra4Mode(rd.ModesI4);
        SwapOut(it);
        rdBest.YAcLevels.CopyTo(rd.YAcLevels, 0);
        return true;   // select intra4x4 over intra16x16
    }

    private void PickBestUV(Iterator it, Vp8ModeScore rd)
    {
        const int kNumBlocks = 8;
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        int lambda = dqm.LambdaUv;
        int src = Iterator.YuvIn + UOffEnc;
        int tmpDst = it.YuvOut2 + UOffEnc;  // scratch buffer
        int dst0 = it.YuvOut + UOffEnc;
        int dst = dst0;
        var rdUv = _rdUv;
        var yuv = it.Yuv;
        // rd_best
        long bD = 0, bSD = 0, bR = 0, bH = 0, bScore = MaxCost;
        uint bNz = 0;

        rd.ModeUv = -1;
        for (int mode = 0; mode < NumPredModes; ++mode)
        {
            // Reconstruct
            rdUv.Nz = (uint)ReconstructUV(it, rdUv, tmpDst, mode);

            // Compute RD-score
            rdUv.D = Vp8EncDsp.Sse16x8(yuv, src, yuv, tmpDst);
            rdUv.SD = 0;    // not calling TDisto here: it tends to flatten areas.
            rdUv.H = FixedCostsUv[mode];
            rdUv.R = GetCostUV(it, rdUv);
            if (mode > 0 && Vp8EncDsp.IsFlat(rdUv.UvLevels, kNumBlocks, FlatnessLimitUv))
                rdUv.R += FlatnessPenalty * kNumBlocks;

            SetRDScore(lambda, rdUv);
            if (mode == 0 || rdUv.Score < bScore)
            {
                bD = rdUv.D;
                bSD = rdUv.SD;
                bR = rdUv.R;
                bH = rdUv.H;
                bNz = rdUv.Nz;
                bScore = rdUv.Score;
                rd.ModeUv = mode;
                rdUv.UvLevels.CopyTo(rd.UvLevels, 0);
                if (it.HasDerr) rdUv.Derr.CopyTo(rd.Derr, 0);
                (dst, tmpDst) = (tmpDst, dst);
            }
        }
        it.SetIntraUVMode(rd.ModeUv);
        // AddScore(rd, &rd_best)
        rd.D += bD;
        rd.SD += bSD;
        rd.R += bR;
        rd.H += bH;
        rd.Nz |= bNz;
        rd.Score += bScore;
        if (dst != dst0)
        {
            // copy 16x8 block if needed
            Vp8EncDsp.Copy(yuv, dst, yuv, dst0, 16, 8);
        }
        if (it.HasDerr)
        {
            // store diffusion errors for next block
            StoreDiffusionErrors(it, rd);
        }
    }

    // ---- Final reconstruction and quantization. -----------------------------------------------------------------------

    private void SimpleQuantize(Iterator it, Vp8ModeScore rd)
    {
        bool isI16 = _mbInfo[it.Mb].Type == 1;
        int nz = 0;

        if (isI16)
        {
            nz = ReconstructIntra16(it, rd, it.YuvOut + YOffEnc, _predsMem[it.Preds]);
        }
        else
        {
            it.StartI4();
            do
            {
                int mode = _predsMem[it.Preds + (it.I4 & 3) + (it.I4 >> 2) * _predsW];
                int src = Iterator.YuvIn + YOffEnc + Scan(it.I4);
                int dst = it.YuvOut + YOffEnc + Scan(it.I4);
                MakeIntra4Preds(it);
                nz |= ReconstructIntra4(it, rd.YAcLevels.AsSpan(it.I4 * 16, 16), src, dst, mode) << it.I4;
            } while (it.RotateI4(it.YuvOut + YOffEnc));
        }

        nz |= ReconstructUV(it, rd, it.YuvOut + UOffEnc, _mbInfo[it.Mb].UvMode);
        rd.Nz = (uint)nz;
    }

    // Refine intra16/intra4 sub-modes based on distortion only (not rate).
    private void RefineUsingDistortion(Iterator it, bool tryBothModes, bool refineUvMode, Vp8ModeScore rd)
    {
        long bestScore = MaxCost;
        int nz = 0;
        int mode;
        bool isI16 = tryBothModes || _mbInfo[it.Mb].Type == 1;
        var dqm = _dqm[_mbInfo[it.Mb].Segment];
        // Some empiric constants, of approximate order of magnitude.
        const int lambdaDI16 = 106;
        const int lambdaDI4 = 11;
        const int lambdaDUv = 120;
        long scoreI4 = dqm.I4Penalty;
        long i4BitSum = 0;
        long bitLimit = tryBothModes ? _mbHeaderLimit : MaxCost;  // no early-out allowed
        var yuv = it.Yuv;

        if (isI16)
        {
            // First, evaluate Intra16 distortion
            int bestMode = -1;
            int src = Iterator.YuvIn + YOffEnc;
            for (mode = 0; mode < NumPredModes; ++mode)
            {
                int refOff = Iterator.YuvP + I16ModeOffsets[mode];
                long score = (long)Vp8EncDsp.Sse16x16(yuv, src, yuv, refOff) * RdDistoMult
                             + FixedCostsI16[mode] * lambdaDI16;
                if (mode > 0 && FixedCostsI16[mode] > bitLimit) continue;
                if (score < bestScore)
                {
                    bestMode = mode;
                    bestScore = score;
                }
            }
            if (it.X == 0 || it.Y == 0)
            {
                // avoid starting a checkerboard resonance from the border. See bug #432.
                if (Vp8EncDsp.IsFlatSource16(yuv, src))
                {
                    bestMode = it.X == 0 ? 0 : 2;
                    tryBothModes = false;  // stick to i16
                }
            }
            it.SetIntra16Mode(bestMode);
            // we'll reconstruct later, if i16 mode actually gets selected
        }

        // Next, evaluate Intra4
        if (tryBothModes || !isI16)
        {
            // We don't evaluate the rate here, but just account for it through a
            // constant penalty (i4 mode usually needs more bits compared to i16).
            isI16 = false;
            it.StartI4();
            do
            {
                int bestI4Mode = -1;
                long bestI4Score = MaxCost;
                int src = Iterator.YuvIn + YOffEnc + Scan(it.I4);
                int modeCosts = GetCostModeI4(it, rd.ModesI4);

                MakeIntra4Preds(it);
                for (mode = 0; mode < NumBModes; ++mode)
                {
                    int refOff = Iterator.YuvP + I4ModeOffsets[mode];
                    long score = Vp8EncDsp.Sse4x4(yuv, src, yuv, refOff) * RdDistoMult
                                 + Vp8EncTables.FixedCostsI4[modeCosts + mode] * lambdaDI4;
                    if (score < bestI4Score)
                    {
                        bestI4Mode = mode;
                        bestI4Score = score;
                    }
                }
                i4BitSum += Vp8EncTables.FixedCostsI4[modeCosts + bestI4Mode];
                rd.ModesI4[it.I4] = (byte)bestI4Mode;
                scoreI4 += bestI4Score;
                if (scoreI4 >= bestScore || i4BitSum > bitLimit)
                {
                    // Intra4 won't be better than Intra16. Bail out and pick Intra16.
                    isI16 = true;
                    break;
                }
                else
                {
                    // reconstruct partial block inside yuv_out2 buffer
                    int tmpDst = it.YuvOut2 + YOffEnc + Scan(it.I4);
                    nz |= ReconstructIntra4(it, rd.YAcLevels.AsSpan(it.I4 * 16, 16), src, tmpDst, bestI4Mode) << it.I4;
                }
            } while (it.RotateI4(it.YuvOut2 + YOffEnc));
        }

        // Final reconstruction, depending on which mode is selected.
        if (!isI16)
        {
            it.SetIntra4Mode(rd.ModesI4);
            SwapOut(it);
            bestScore = scoreI4;
        }
        else
        {
            nz = ReconstructIntra16(it, rd, it.YuvOut + YOffEnc, _predsMem[it.Preds]);
        }

        // ... and UV!
        if (refineUvMode)
        {
            int bestMode = -1;
            long bestUvScore = MaxCost;
            int src = Iterator.YuvIn + UOffEnc;
            for (mode = 0; mode < NumPredModes; ++mode)
            {
                int refOff = Iterator.YuvP + UvModeOffsets[mode];
                long score = Vp8EncDsp.Sse16x8(yuv, src, yuv, refOff) * RdDistoMult
                             + FixedCostsUv[mode] * lambdaDUv;
                if (score < bestUvScore)
                {
                    bestMode = mode;
                    bestUvScore = score;
                }
            }
            it.SetIntraUVMode(bestMode);
        }
        nz |= ReconstructUV(it, rd, it.YuvOut + UOffEnc, _mbInfo[it.Mb].UvMode);

        rd.Nz = (uint)nz;
        rd.Score = bestScore;
    }

    /// <summary>VP8Decimate: picks the modes and quantizes one macroblock; true if it has no non-zero coefficient.</summary>
    private bool Decimate(Iterator it, Vp8ModeScore rd, int rdOpt)
    {
        int method = _method;

        InitScore(rd);

        // We can perform predictions for Luma16x16 and Chroma8x8 already.
        // Luma4x4 predictions needs to be done as-we-go.
        MakeLuma16Preds(it);
        MakeChroma8Preds(it);

        if (rdOpt > RdOptNone)
        {
            it.DoTrellis = rdOpt >= RdOptTrellisAll;
            PickBestIntra16(it, rd);
            if (method >= 2) PickBestIntra4(it, rd);
            PickBestUV(it, rd);
            if (rdOpt == RdOptTrellis)
            {
                // finish off with trellis-optim now
                it.DoTrellis = true;
                SimpleQuantize(it, rd);
            }
        }
        else
        {
            // At this point we have heuristically decided intra16 / intra4.
            // For method >= 2, pick the best intra4/intra16 based on SSE (~tad slower).
            // For method <= 1, we don't re-examine the decision but just go ahead with
            // quantization/reconstruction.
            RefineUsingDistortion(it, method >= 2, method >= 1, rd);
        }
        bool isSkipped = rd.Nz == 0;
        it.SetSkip(isSkipped);
        return isSkipped;
    }

    // ---- cost_enc.c ---------------------------------------------------------------------------------------------------

    private static readonly byte[] Vp8EncBands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];   // + sentinel

    private static readonly ushort[] FixedCostsUv = [302, 984, 439, 642];
    private static readonly ushort[] FixedCostsI16 = [663, 919, 872, 919];

    private static int VariableLevelCost(int level, byte[] probas, int p)
    {
        int pattern = Vp8EncTables.LevelCodes[(level - 1) * 2 + 0];
        int bits = Vp8EncTables.LevelCodes[(level - 1) * 2 + 1];
        int cost = 0;
        for (int i = 2; pattern != 0; ++i)
        {
            if ((pattern & 1) != 0) cost += BitCost(bits & 1, probas[p + i]);
            bits >>= 1;
            pattern >>= 1;
        }
        return cost;
    }

    /// <summary>VP8CalculateLevelCosts.</summary>
    private void CalculateLevelCosts()
    {
        var proba = _proba;
        if (!proba.Dirty) return;  // nothing to do.

        for (int ctype = 0; ctype < Vp8EncProba.NumTypes; ++ctype)
        {
            for (int band = 0; band < Vp8EncProba.NumBands; ++band)
            {
                for (int ctx = 0; ctx < Vp8EncProba.NumCtx; ++ctx)
                {
                    int p = Vp8EncProba.ProbaIdx(ctype, band, ctx);
                    int table = Vp8EncProba.CostIdx(ctype, band, ctx);
                    int cost0 = ctx > 0 ? BitCost(1, proba.Coeffs[p + 0]) : 0;
                    int costBase = BitCost(1, proba.Coeffs[p + 1]) + cost0;
                    proba.LevelCost[table + 0] = (ushort)(BitCost(0, proba.Coeffs[p + 1]) + cost0);
                    for (int v = 1; v <= Vp8EncProba.MaxVariableLevel; ++v)
                        proba.LevelCost[table + v] = (ushort)(costBase + VariableLevelCost(v, proba.Coeffs, p));
                    // Starting at level 67 and up, the variable part of the cost is
                    // actually constant.
                }
            }
            for (int n = 0; n < 16; ++n)
            {
                // replicate bands. We don't need to sentinel.
                for (int ctx = 0; ctx < Vp8EncProba.NumCtx; ++ctx)
                    proba.RemappedCosts[(ctype * 16 + n) * Vp8EncProba.NumCtx + ctx] =
                        Vp8EncProba.CostIdx(ctype, Vp8EncBands[n], ctx);
            }
        }
        proba.Dirty = false;
    }

    /// <summary>VP8Residual.</summary>
    private struct Residual
    {
        public int First, Last;
        public short[] Coeffs;
        public int CoeffsOff;
        public int CoeffType;
    }

    private static void InitResidual(int first, int coeffType, ref Residual res)
    {
        res.CoeffType = coeffType;
        res.First = first;
    }

    /// <summary>SetResidualCoeffs_C.</summary>
    private static void SetResidualCoeffs(short[] coeffs, int off, ref Residual res)
    {
        res.Last = -1;
        for (int n = 15; n >= 0; --n)
        {
            if (coeffs[off + n] != 0)
            {
                res.Last = n;
                break;
            }
        }
        res.Coeffs = coeffs;
        res.CoeffsOff = off;
    }

    /// <summary>GetResidualCost_C.</summary>
    private int GetResidualCost(int ctx0, in Residual res)
    {
        int n = res.First;
        // should be prob[VP8EncBands[n]], but it's equivalent for n=0 or 1
        int p0 = _proba.Coeffs[Vp8EncProba.ProbaIdx(res.CoeffType, n, ctx0) + 0];
        int t = _proba.Costs(res.CoeffType, n, ctx0);
        // bit_cost(1, p0) is already incorporated in t[] tables, but only if ctx != 0
        // (as required by the syntax). For ctx0 == 0, we need to add it here or it'll
        // be missing during the loop.
        int cost = ctx0 == 0 ? BitCost(1, p0) : 0;

        if (res.Last < 0) return BitCost(0, p0);
        var coeffs = res.Coeffs;
        int o = res.CoeffsOff;
        for (; n < res.Last; ++n)
        {
            int v = Math.Abs((int)coeffs[o + n]);
            int ctx = v >= 2 ? 2 : v;
            cost += LevelCost(t, v);
            t = _proba.Costs(res.CoeffType, n + 1, ctx);
        }
        // Last coefficient is always non-zero
        {
            int v = Math.Abs((int)coeffs[o + n]);
            cost += LevelCost(t, v);
            if (n < 15)
            {
                int b = Vp8EncBands[n + 1];
                int ctx = v == 1 ? 1 : 2;
                int lastP0 = _proba.Coeffs[Vp8EncProba.ProbaIdx(res.CoeffType, b, ctx) + 0];
                cost += BitCost(0, lastP0);
            }
        }
        return cost;
    }

    private int GetCostLuma4(Iterator it, short[] levels)
    {
        int x = it.I4 & 3, y = it.I4 >> 2;
        var res = new Residual();
        InitResidual(0, 3, ref res);
        int ctx = it.TopNz[x] + it.LeftNz[y];
        SetResidualCoeffs(levels, 0, ref res);
        return GetResidualCost(ctx, res);
    }

    private int GetCostLuma16(Iterator it, Vp8ModeScore rd)
    {
        var res = new Residual();
        int R = 0;

        it.NzToBytes();   // re-import the non-zero context

        // DC
        InitResidual(0, 1, ref res);
        SetResidualCoeffs(rd.YDcLevels, 0, ref res);
        R += GetResidualCost(it.TopNz[8] + it.LeftNz[8], res);

        // AC
        InitResidual(1, 0, ref res);
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                int ctx = it.TopNz[x] + it.LeftNz[y];
                SetResidualCoeffs(rd.YAcLevels, (x + y * 4) * 16, ref res);
                R += GetResidualCost(ctx, res);
                it.TopNz[x] = it.LeftNz[y] = res.Last >= 0 ? 1 : 0;
            }
        }
        return R;
    }

    private int GetCostUV(Iterator it, Vp8ModeScore rd)
    {
        var res = new Residual();
        int R = 0;

        it.NzToBytes();  // re-import the non-zero context

        InitResidual(0, 2, ref res);
        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; ++y)
            {
                for (int x = 0; x < 2; ++x)
                {
                    int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                    SetResidualCoeffs(rd.UvLevels, (ch * 2 + x + y * 2) * 16, ref res);
                    R += GetResidualCost(ctx, res);
                    it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = res.Last >= 0 ? 1 : 0;
                }
            }
        }
        return R;
    }

    // ---- Recording of token probabilities. ----------------------------------------------------------------------------

    /// <summary>VP8RecordStats.</summary>
    private static int RecordStats(int bit, uint[] stats, int s)
    {
        uint p = stats[s];
        // An overflow is inbound. Note we handle this at 0xfffe0000u instead of
        // 0xffff0000u to make sure p + 1u does not overflow.
        if (p >= 0xfffe0000u) p = ((p + 1u) >> 1) & 0x7fff7fffu;  // -> divide the stats by 2.
        // record bit count (lower 16 bits) and increment total count (upper 16 bits).
        p += 0x00010000u + (uint)bit;
        stats[s] = p;
        return bit;
    }

    /// <summary>VP8RecordCoeffs (USE_LEVEL_CODE_TABLE).</summary>
    private int RecordCoeffs(int ctx, in Residual res)
    {
        int n = res.First;
        var stats = _proba.Stats;
        // should be stats[VP8EncBands[n]], but it's equivalent for n=0 or 1
        int s = Vp8EncProba.ProbaIdx(res.CoeffType, n, ctx);
        if (res.Last < 0)
        {
            RecordStats(0, stats, s + 0);
            return 0;
        }
        var coeffs = res.Coeffs;
        int o = res.CoeffsOff;
        while (n <= res.Last)
        {
            int v;
            RecordStats(1, stats, s + 0);  // order of record doesn't matter
            while ((v = coeffs[o + n++]) == 0)
            {
                RecordStats(0, stats, s + 1);
                s = Vp8EncProba.ProbaIdx(res.CoeffType, Vp8EncBands[n], 0);
            }
            RecordStats(1, stats, s + 1);
            if (RecordStats(2u < (uint)(v + 1) ? 1 : 0, stats, s + 2) == 0)
            {
                // v = -1 or 1
                s = Vp8EncProba.ProbaIdx(res.CoeffType, Vp8EncBands[n], 1);
            }
            else
            {
                v = Math.Abs(v);
                if (v > Vp8EncProba.MaxVariableLevel) v = Vp8EncProba.MaxVariableLevel;
                {
                    int bits = Vp8EncTables.LevelCodes[(v - 1) * 2 + 1];
                    int pattern = Vp8EncTables.LevelCodes[(v - 1) * 2 + 0];
                    for (int i = 0; (pattern >>= 1) != 0; ++i)
                    {
                        int mask = 2 << i;
                        if ((pattern & 1) != 0) RecordStats((bits & mask) != 0 ? 1 : 0, stats, s + 3 + i);
                    }
                }
                s = Vp8EncProba.ProbaIdx(res.CoeffType, Vp8EncBands[n], 2);
            }
        }
        if (n < 16) RecordStats(0, stats, s + 0);
        return 1;
    }
}
