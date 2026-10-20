namespace SharpImage.Formats.WebP;

// frame_enc.c, token_enc.c, tree_enc.c, filter_enc.c and syntax_enc.c
internal sealed partial class Vp8Encoder
{
    private const int RiffHeaderSize = 12, ChunkHeaderSize = 8, Vp8FrameHeaderSize = 10, TagSize = 4;
    private const int Vp8XChunkSize = 10;
    private const int HeaderSizeEstimate = RiffHeaderSize + ChunkHeaderSize + Vp8FrameHeaderSize;
    private const double DqLimit = 0.4;  // convergence is considered reached if dq < DQ_LIMIT
    private const int Vp8MaxPartition0Size = 1 << 19;   // max size of mode partition
    private const int Vp8MaxPartitionSize = 1 << 24;    // max size for token partition
    // we allow 2k of extra head-room in PARTITION0 limit.
    private const ulong Partition0SizeLimit = (Vp8MaxPartition0Size - 2048UL) << 11;

    private static float ClampF(float v, float min, float max) => v < min ? min : v > max ? max : v;

    /// <summary>PassStats: struct for organizing convergence in either size or PSNR.</summary>
    private sealed class PassStats
    {
        public bool IsFirst;
        public float Dq;
        public float Q, LastQ;
        public float QMin, QMax;
        public double Value, LastValue;   // PSNR or size
        public double Target;
        public bool DoSizeSearch;
    }

    private PassStats InitPassStats()
    {
        ulong targetSize = (ulong)_config.TargetSize;
        bool doSizeSearch = targetSize != 0;
        float targetPsnr = _config.TargetPsnr;
        var s = new PassStats
        {
            IsFirst = true,
            Dq = 10f,
            QMin = 1f * _config.QMin,
            QMax = 1f * _config.QMax,
        };
        s.Q = s.LastQ = ClampF(_config.Quality, s.QMin, s.QMax);
        s.Target = doSizeSearch ? targetSize
                 : targetPsnr > 0.0 ? targetPsnr
                 : 40.0;   // default, just in case
        s.Value = s.LastValue = 0.0;
        s.DoSizeSearch = doSizeSearch;
        return s;
    }

    private static float ComputeNextQ(PassStats s)
    {
        float dq;
        if (s.IsFirst)
        {
            dq = s.Value > s.Target ? -s.Dq : s.Dq;
            s.IsFirst = false;
        }
        else if (s.Value != s.LastValue)
        {
            double slope = (s.Target - s.Value) / (s.LastValue - s.Value);
            dq = (float)(slope * (s.LastQ - s.Q));
        }
        else
        {
            dq = 0f;  // we're done?!
        }
        // Limit variable to avoid large swings.
        s.Dq = ClampF(dq, -30f, 30f);
        s.LastQ = s.Q;
        s.LastValue = s.Value;
        s.Q = ClampF(s.Q + s.Dq, s.QMin, s.QMax);
        return s.Q;
    }

    // Tables for level coding
    private static readonly byte[] Cat3 = [173, 148, 140];
    private static readonly byte[] Cat4 = [176, 155, 140, 135];
    private static readonly byte[] Cat5 = [180, 157, 141, 134, 130];
    private static readonly byte[] Cat6 = [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    // Reset the statistics about: number of skips, token proba, level cost,...
    private void ResetStats()
    {
        CalculateLevelCosts();
        _proba.NbSkip = 0;
    }

    // Skip decision probability
    private const int SkipProbaThreshold = 250;  // value below which using skip_proba is OK.

    private static int CalcSkipProba(ulong nb, ulong total) => (int)(total != 0 ? (total - nb) * 255 / total : 255);

    // Returns the bit-cost for coding the skip probability.
    private int FinalizeSkipProba()
    {
        var proba = _proba;
        int nbMbs = _mbW * _mbH;
        int nbEvents = proba.NbSkip;
        proba.SkipProba = CalcSkipProba((ulong)nbEvents, (ulong)nbMbs);
        proba.UseSkipProba = proba.SkipProba < SkipProbaThreshold;
        int size = 256;   // 'use_skip_proba' bit
        if (proba.UseSkipProba)
        {
            size += nbEvents * BitCost(1, proba.SkipProba) + (nbMbs - nbEvents) * BitCost(0, proba.SkipProba);
            size += 8 * 256;   // cost of signaling the 'skip_proba' itself.
        }
        return size;
    }

    // Collect statistics and deduce probabilities for next coding pass.
    // Return the total bit-cost for coding the probability updates.
    private static int CalcTokenProba(int nb, int total) => nb != 0 ? 255 - nb * 255 / total : 255;

    // Cost of coding 'nb' 1's and 'total-nb' 0's using 'proba' probability.
    private static int BranchCost(int nb, int total, int proba) => nb * BitCost(1, proba) + (total - nb) * BitCost(0, proba);

    private void ResetTokenStats() => Array.Clear(_proba.Stats);

    private int FinalizeTokenProbas()
    {
        var proba = _proba;
        bool hasChanged = false;
        int size = 0;
        for (int i = 0; i < proba.Coeffs.Length; ++i)
        {
            uint stats = proba.Stats[i];
            int nb = (int)(stats >> 0) & 0xffff;
            int total = (int)(stats >> 16) & 0xffff;
            int updateProba = Vp8EncTables.CoeffsUpdateProba[i];
            int oldP = Vp8EncTables.CoeffsProba0[i];
            int newP = CalcTokenProba(nb, total);
            int oldCost = BranchCost(nb, total, oldP) + BitCost(0, updateProba);
            int newCost = BranchCost(nb, total, newP) + BitCost(1, updateProba) + 8 * 256;
            bool useNewP = oldCost > newCost;
            size += BitCost(useNewP ? 1 : 0, updateProba);
            if (useNewP)
            {
                // only use proba that seem meaningful enough.
                proba.Coeffs[i] = (byte)newP;
                hasChanged |= newP != oldP;
                size += 8 * 256;
            }
            else
            {
                proba.Coeffs[i] = (byte)oldP;
            }
        }
        proba.Dirty = hasChanged;
        return size;
    }

    // Finalize Segment probability based on the coding tree
    private static int GetProba(int a, int b)
    {
        int total = a + b;
        return total == 0 ? 255     // that's the default probability.
                          : (255 * a + total / 2) / total;  // rounded proba
    }

    private void ResetSegments()
    {
        for (int n = 0; n < _mbW * _mbH; ++n) _mbInfo[n].Segment = 0;
    }

    private void SetSegmentProbas()
    {
        Span<int> p = stackalloc int[NumMbSegments];
        p.Clear();
        for (int n = 0; n < _mbW * _mbH; ++n) ++p[_mbInfo[n].Segment];
        if (_numSegments > 1)
        {
            var probas = _proba.Segments;
            probas[0] = (byte)GetProba(p[0] + p[1], p[2] + p[3]);
            probas[1] = (byte)GetProba(p[0], p[1]);
            probas[2] = (byte)GetProba(p[2], p[3]);

            _updateMap = probas[0] != 255 || probas[1] != 255 || probas[2] != 255;
            if (!_updateMap) ResetSegments();
            _segmentHdrSize =
                p[0] * (BitCost(0, probas[0]) + BitCost(0, probas[1])) +
                p[1] * (BitCost(0, probas[0]) + BitCost(1, probas[1])) +
                p[2] * (BitCost(1, probas[0]) + BitCost(0, probas[2])) +
                p[3] * (BitCost(1, probas[0]) + BitCost(1, probas[2]));
        }
        else
        {
            _updateMap = false;
            _segmentHdrSize = 0;
        }
    }

    // ---- Coefficient coding -------------------------------------------------------------------------------------------

    private int PutCoeffs(Vp8BitWriter bw, int ctx, in Residual res)
    {
        int n = res.First;
        var coeffs = _proba.Coeffs;
        // should be prob[VP8EncBands[n]], but it's equivalent for n=0 or 1
        int p = Vp8EncProba.ProbaIdx(res.CoeffType, n, ctx);
        if (bw.PutBit(res.Last >= 0 ? 1 : 0, coeffs[p + 0]) == 0) return 0;

        var c16 = res.Coeffs;
        int o = res.CoeffsOff;
        while (n < 16)
        {
            int c = c16[o + n++];
            bool sign = c < 0;
            int v = sign ? -c : c;
            if (bw.PutBit(v != 0 ? 1 : 0, coeffs[p + 1]) == 0)
            {
                p = Vp8EncProba.ProbaIdx(res.CoeffType, Vp8EncBands[n], 0);
                continue;
            }
            if (bw.PutBit(v > 1 ? 1 : 0, coeffs[p + 2]) == 0)
            {
                p = Vp8EncProba.ProbaIdx(res.CoeffType, Vp8EncBands[n], 1);
            }
            else
            {
                if (bw.PutBit(v > 4 ? 1 : 0, coeffs[p + 3]) == 0)
                {
                    if (bw.PutBit(v != 2 ? 1 : 0, coeffs[p + 4]) != 0) bw.PutBit(v == 4 ? 1 : 0, coeffs[p + 5]);
                }
                else if (bw.PutBit(v > 10 ? 1 : 0, coeffs[p + 6]) == 0)
                {
                    if (bw.PutBit(v > 6 ? 1 : 0, coeffs[p + 7]) == 0)
                    {
                        bw.PutBit(v == 6 ? 1 : 0, 159);
                    }
                    else
                    {
                        bw.PutBit(v >= 9 ? 1 : 0, 165);
                        bw.PutBit((v & 1) == 0 ? 1 : 0, 145);
                    }
                }
                else
                {
                    int mask;
                    byte[] tab;
                    if (v < 3 + (8 << 1))
                    {
                        // VP8Cat3  (3b)
                        bw.PutBit(0, coeffs[p + 8]);
                        bw.PutBit(0, coeffs[p + 9]);
                        v -= 3 + (8 << 0);
                        mask = 1 << 2;
                        tab = Cat3;
                    }
                    else if (v < 3 + (8 << 2))
                    {
                        // VP8Cat4  (4b)
                        bw.PutBit(0, coeffs[p + 8]);
                        bw.PutBit(1, coeffs[p + 9]);
                        v -= 3 + (8 << 1);
                        mask = 1 << 3;
                        tab = Cat4;
                    }
                    else if (v < 3 + (8 << 3))
                    {
                        // VP8Cat5  (5b)
                        bw.PutBit(1, coeffs[p + 8]);
                        bw.PutBit(0, coeffs[p + 10]);
                        v -= 3 + (8 << 2);
                        mask = 1 << 4;
                        tab = Cat5;
                    }
                    else
                    {
                        // VP8Cat6 (11b)
                        bw.PutBit(1, coeffs[p + 8]);
                        bw.PutBit(1, coeffs[p + 10]);
                        v -= 3 + (8 << 3);
                        mask = 1 << 10;
                        tab = Cat6;
                    }
                    int ti = 0;
                    while (mask != 0)
                    {
                        bw.PutBit((v & mask) != 0 ? 1 : 0, tab[ti++]);
                        mask >>= 1;
                    }
                }
                p = Vp8EncProba.ProbaIdx(res.CoeffType, Vp8EncBands[n], 2);
            }
            bw.PutBitUniform(sign ? 1 : 0);
            if (n == 16 || bw.PutBit(n <= res.Last ? 1 : 0, coeffs[p + 0]) == 0) return 1;   // EOB
        }
        return 1;
    }

    private void CodeResiduals(Vp8BitWriter bw, Iterator it, Vp8ModeScore rd)
    {
        var res = new Residual();
        bool i16 = _mbInfo[it.Mb].Type == 1;
        int segment = _mbInfo[it.Mb].Segment;

        it.NzToBytes();

        ulong pos1 = bw.Pos;
        if (i16)
        {
            InitResidual(0, 1, ref res);
            SetResidualCoeffs(rd.YDcLevels, 0, ref res);
            it.TopNz[8] = it.LeftNz[8] = PutCoeffs(bw, it.TopNz[8] + it.LeftNz[8], res);
            InitResidual(1, 0, ref res);
        }
        else
        {
            InitResidual(0, 3, ref res);
        }

        // luma-AC
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                int ctx = it.TopNz[x] + it.LeftNz[y];
                SetResidualCoeffs(rd.YAcLevels, (x + y * 4) * 16, ref res);
                it.TopNz[x] = it.LeftNz[y] = PutCoeffs(bw, ctx, res);
            }
        }
        ulong pos2 = bw.Pos;

        // U/V
        InitResidual(0, 2, ref res);
        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; ++y)
            {
                for (int x = 0; x < 2; ++x)
                {
                    int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                    SetResidualCoeffs(rd.UvLevels, (ch * 2 + x + y * 2) * 16, ref res);
                    it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = PutCoeffs(bw, ctx, res);
                }
            }
        }
        ulong pos3 = bw.Pos;
        it.LumaBits = pos2 - pos1;
        it.UvBits = pos3 - pos2;
        it.BitCount[segment * 3 + (i16 ? 1 : 0)] += it.LumaBits;
        it.BitCount[segment * 3 + 2] += it.UvBits;
        it.BytesToNz();
    }

    // Same as CodeResiduals, but doesn't actually write anything.
    // Instead, it just records the event distribution.
    private void RecordResiduals(Iterator it, Vp8ModeScore rd)
    {
        var res = new Residual();

        it.NzToBytes();

        if (_mbInfo[it.Mb].Type == 1)
        {
            // i16x16
            InitResidual(0, 1, ref res);
            SetResidualCoeffs(rd.YDcLevels, 0, ref res);
            it.TopNz[8] = it.LeftNz[8] = RecordCoeffs(it.TopNz[8] + it.LeftNz[8], res);
            InitResidual(1, 0, ref res);
        }
        else
        {
            InitResidual(0, 3, ref res);
        }

        // luma-AC
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                int ctx = it.TopNz[x] + it.LeftNz[y];
                SetResidualCoeffs(rd.YAcLevels, (x + y * 4) * 16, ref res);
                it.TopNz[x] = it.LeftNz[y] = RecordCoeffs(ctx, res);
            }
        }

        // U/V
        InitResidual(0, 2, ref res);
        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; ++y)
            {
                for (int x = 0; x < 2; ++x)
                {
                    int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                    SetResidualCoeffs(rd.UvLevels, (ch * 2 + x + y * 2) * 16, ref res);
                    it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = RecordCoeffs(ctx, res);
                }
            }
        }

        it.BytesToNz();
    }

    // ---- ExtraInfo map / Debug function -------------------------------------------------------------------------------

    private void RecordTokens(Iterator it, Vp8ModeScore rd)
    {
        var res = new Residual();
        var tokens = _tokens;

        it.NzToBytes();
        if (_mbInfo[it.Mb].Type == 1)
        {
            // i16x16
            int ctx = it.TopNz[8] + it.LeftNz[8];
            InitResidual(0, 1, ref res);
            SetResidualCoeffs(rd.YDcLevels, 0, ref res);
            it.TopNz[8] = it.LeftNz[8] = RecordCoeffTokens(ctx, res, tokens);
            InitResidual(1, 0, ref res);
        }
        else
        {
            InitResidual(0, 3, ref res);
        }

        // luma-AC
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                int ctx = it.TopNz[x] + it.LeftNz[y];
                SetResidualCoeffs(rd.YAcLevels, (x + y * 4) * 16, ref res);
                it.TopNz[x] = it.LeftNz[y] = RecordCoeffTokens(ctx, res, tokens);
            }
        }

        // U/V
        InitResidual(0, 2, ref res);
        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; ++y)
            {
                for (int x = 0; x < 2; ++x)
                {
                    int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                    SetResidualCoeffs(rd.UvLevels, (ch * 2 + x + y * 2) * 16, ref res);
                    it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = RecordCoeffTokens(ctx, res, tokens);
                }
            }
        }
        it.BytesToNz();
    }

    private static double GetPsnr(ulong mse, ulong size) =>
        mse > 0 && size > 0 ? 10.0 * Math.Log10(255.0 * 255.0 * size / mse) : 99;

    // ---- StatLoop(): only collect statistics (number of skips, token usage, ...). --------------------------------------
    // This is used for deciding optimal probabilities. It also modifies the
    // quantizer value if some target (size, PSNR) was specified.

    private void SetLoopParams(float q)
    {
        // Make sure the quality parameter is inside valid bounds
        q = ClampF(q, 0f, 100f);

        SetSegmentParams(q);      // setup segment quantizations and filters
        SetSegmentProbas();       // compute segment probabilities

        ResetStats();
    }

    private readonly Vp8ModeScore _info = new();

    private ulong OneStatPass(int rdOpt, int nbMbs, PassStats s)
    {
        var it = new Iterator(this);
        ulong size = 0;
        ulong sizeP0 = 0;
        ulong distortion = 0;
        ulong pixelCount = (ulong)nbMbs * 384;
        var info = _info;

        SetLoopParams(s.Q);
        do
        {
            it.Import(null);
            if (Decimate(it, info, rdOpt))
            {
                // Just record the number of skips and act like skip_proba is not used.
                ++_proba.NbSkip;
            }
            RecordResiduals(it, info);
            size += (ulong)(info.R + info.H);
            sizeP0 += (ulong)info.H;
            distortion += (ulong)info.D;
            it.SaveBoundary();
        } while (it.Next() && --nbMbs > 0);

        sizeP0 += (ulong)_segmentHdrSize;
        if (s.DoSizeSearch)
        {
            size += (ulong)FinalizeSkipProba();
            size += (ulong)FinalizeTokenProbas();
            size = ((size + sizeP0 + 1024) >> 11) + HeaderSizeEstimate;
            s.Value = size;
        }
        else
        {
            s.Value = GetPsnr(distortion, pixelCount);
        }
        return sizeP0;
    }

    private void StatLoop()
    {
        int method = _method;
        bool doSearch = _doSearch;
        bool fastProbe = (method == 0 || method == 3) && !doSearch;
        int numPassLeft = _config.Pass;
        int rdOpt = method >= 3 || doSearch ? RdOptBasic : RdOptNone;
        int nbMbs = _mbW * _mbH;
        var stats = InitPassStats();
        ResetTokenStats();

        // Fast mode: quick analysis pass over few mbs. Better than nothing.
        if (fastProbe)
        {
            if (method == 3)
            {
                // we need more stats for method 3 to be reliable.
                nbMbs = nbMbs > 200 ? nbMbs >> 1 : 100;
            }
            else
            {
                nbMbs = nbMbs > 200 ? nbMbs >> 2 : 50;
            }
        }

        while (numPassLeft-- > 0)
        {
            bool isLastPass = Math.Abs(stats.Dq) <= DqLimit || numPassLeft == 0 || _maxI4HeaderBits == 0;
            ulong sizeP0 = OneStatPass(rdOpt, nbMbs, stats);
            // (also reached without error: with one segment and rd_opt NONE, no header bits are counted, and the
            // probabilities then stay the defaults)
            if (sizeP0 == 0) return;
            if (_maxI4HeaderBits > 0 && sizeP0 > Partition0SizeLimit)
            {
                ++numPassLeft;
                _maxI4HeaderBits >>= 1;   // strengthen header bit limitation...
                continue;                 // ...and start over
            }
            if (isLastPass) break;
            // If no target size: just do several pass without changing 'q'
            if (doSearch)
            {
                ComputeNextQ(stats);
                if (Math.Abs(stats.Dq) <= DqLimit) break;
            }
        }
        if (!doSearch || !stats.DoSizeSearch)
        {
            // Need to finalize probas now, since it wasn't done during the search.
            FinalizeSkipProba();
            FinalizeTokenProbas();
        }
        CalculateLevelCosts();  // finalize costs
    }

    // ---- Main loops ---------------------------------------------------------------------------------------------------

    private static readonly byte[] AverageBytesPerMb = [50, 24, 16, 9, 7, 5, 3, 2];

    private void PreLoopInitialize()
    {
        int averageBytesPerMb = AverageBytesPerMb[_baseQuant >> 4];
        int bytesPerParts = _mbW * _mbH * averageBytesPerMb / _numParts;
        // Initialize the bit-writers
        for (int p = 0; p < _numParts; ++p) _parts[p] = new Vp8BitWriter(bytesPerParts);
    }

    private void PostLoopFinalize(Iterator it)
    {
        // Finalize the partitions, check for extra errors.
        for (int p = 0; p < _numParts; ++p) _parts[p].Finish();
        AdjustFilterStrength(it);     // ...and store filter stats.
    }

    // ---- VP8EncLoop(): does the final bitstream coding. ---------------------------------------------------------------

    private void ResetAfterSkip(Iterator it)
    {
        if (_mbInfo[it.Mb].Type == 1)
        {
            _nzMem[it.Nz] = 0;  // reset all predictors
            it.LeftNz[8] = 0;
        }
        else
        {
            _nzMem[it.Nz] &= 1u << 24;  // preserve the dc_nz bit
        }
    }

    private void EncLoop()
    {
        PreLoopInitialize();
        StatLoop();  // stats-collection loop

        var it = new Iterator(this);
        InitFilter();
        var info = _info;
        do
        {
            bool dontUseSkip = !_proba.UseSkipProba;
            int rdOpt = _rdOptLevel;

            it.Import(null);
            // Warning! order is important: first call VP8Decimate() and
            // *then* decide how to code the skip decision if there's one.
            if (!Decimate(it, info, rdOpt) || dontUseSkip)
                CodeResiduals(it.Bw, it, info);
            else
                ResetAfterSkip(it);   // reset predictors after a skip
            StoreFilterStats(it);
            it.SaveBoundary();
        } while (it.Next());

        PostLoopFinalize(it);
    }

    // ---- Single pass using Token Buffer. ------------------------------------------------------------------------------

    private const int MinCount = 96;  // minimum number of macroblocks before updating stats

    private void EncTokenLoop()
    {
        // Roughly refresh the proba eight times per pass
        int maxCount = (_mbW * _mbH) >> 3;
        int numPassLeft = _config.Pass;
        bool doSearch = _doSearch;
        var proba = _proba;
        int rdOpt = _rdOptLevel;
        ulong pixelCount = (ulong)_mbW * (ulong)_mbH * 384;
        var stats = InitPassStats();
        var info = _info;
        Iterator it = null!;

        PreLoopInitialize();
        if (maxCount < MinCount) maxCount = MinCount;

        while (numPassLeft-- > 0)
        {
            bool isLastPass = Math.Abs(stats.Dq) <= DqLimit || numPassLeft == 0 || _maxI4HeaderBits == 0;
            ulong sizeP0 = 0;
            ulong distortion = 0;
            int cnt = maxCount;
            it = new Iterator(this);
            SetLoopParams(stats.Q);
            if (isLastPass)
            {
                ResetTokenStats();
                InitFilter();  // don't collect stats until last pass (too costly)
            }
            _tokens.Clear();
            do
            {
                it.Import(null);
                if (--cnt < 0)
                {
                    FinalizeTokenProbas();
                    CalculateLevelCosts();  // refresh cost tables for rd-opt
                    cnt = maxCount;
                }
                Decimate(it, info, rdOpt);
                RecordTokens(it, info);
                sizeP0 += (ulong)info.H;
                distortion += (ulong)info.D;
                if (isLastPass) StoreFilterStats(it);
                it.SaveBoundary();
            } while (it.Next());

            sizeP0 += (ulong)_segmentHdrSize;
            if (stats.DoSizeSearch)
            {
                ulong size = (ulong)FinalizeTokenProbas();
                size += _tokens.EstimateSize(proba.Coeffs);
                size = (size + sizeP0 + 1024) >> 11;  // -> size in bytes
                size += HeaderSizeEstimate;
                stats.Value = size;
            }
            else
            {
                // compute and store PSNR
                stats.Value = GetPsnr(distortion, pixelCount);
            }

            if (_maxI4HeaderBits > 0 && sizeP0 > Partition0SizeLimit)
            {
                ++numPassLeft;
                _maxI4HeaderBits >>= 1;  // strengthen header bit limitation...
                continue;                // ...and start over
            }
            if (isLastPass) break;   // done
            if (doSearch) ComputeNextQ(stats);  // Adjust q
        }
        if (!stats.DoSizeSearch) FinalizeTokenProbas();
        _tokens.Emit(_parts[0], proba.Coeffs);
        PostLoopFinalize(it);
    }

    // ---- token_enc.c --------------------------------------------------------------------------------------------------

    /// <summary>VP8TBuffer: the recorded tokens of a pass, in coding order.</summary>
    private sealed class TokenBuffer
    {
        private const ushort FixedProbaBit = 1 << 14;
        private ushort[] _tokens = [];
        private int _count;

        public void Init(int pageSize) => _tokens = new ushort[Math.Max(pageSize, 8192)];

        public void Clear() => _count = 0;

        public void Add(uint bit, uint probaIdx)
        {
            if (_count == _tokens.Length) Array.Resize(ref _tokens, _tokens.Length * 2);
            _tokens[_count++] = (ushort)((bit << 15) | probaIdx);
        }

        public void AddConstant(uint bit, uint proba)
        {
            if (_count == _tokens.Length) Array.Resize(ref _tokens, _tokens.Length * 2);
            _tokens[_count++] = (ushort)((bit << 15) | FixedProbaBit | proba);
        }

        /// <summary>VP8EmitTokens.</summary>
        public void Emit(Vp8BitWriter bw, byte[] probas)
        {
            for (int n = 0; n < _count; ++n)
            {
                ushort token = _tokens[n];
                int bit = (token >> 15) & 1;
                if ((token & FixedProbaBit) != 0)
                    bw.PutBit(bit, token & 0xff);  // constant proba
                else
                    bw.PutBit(bit, probas[token & 0x3fff]);
            }
        }

        /// <summary>VP8EstimateTokenSize.</summary>
        public ulong EstimateSize(byte[] probas)
        {
            ulong size = 0;
            for (int n = 0; n < _count; ++n)
            {
                ushort token = _tokens[n];
                int bit = token & (1 << 15);
                if ((token & FixedProbaBit) != 0)
                    size += (ulong)BitCost(bit, token & 0xff);
                else
                    size += (ulong)BitCost(bit, probas[token & 0x3fff]);
            }
            return size;
        }
    }

    private uint AddToken(TokenBuffer b, uint bit, int probaIdx, int s)
    {
        b.Add(bit, (uint)probaIdx);
        RecordStats((int)bit, _proba.Stats, s);
        return bit;
    }

    /// <summary>VP8RecordCoeffTokens.</summary>
    private int RecordCoeffTokens(int ctx, in Residual res, TokenBuffer tokens)
    {
        var coeffs = res.Coeffs;
        int o = res.CoeffsOff;
        int coeffType = res.CoeffType;
        int last = res.Last;
        int n = res.First;
        int baseId = Vp8EncProba.ProbaIdx(coeffType, n, ctx);
        // should be stats[VP8EncBands[n]], but it's equivalent for n=0 or 1
        int s = baseId;
        if (AddToken(tokens, last >= 0 ? 1u : 0u, baseId + 0, s + 0) == 0) return 0;

        while (n < 16)
        {
            int c = coeffs[o + n++];
            bool sign = c < 0;
            uint v = (uint)(sign ? -c : c);
            if (AddToken(tokens, v != 0 ? 1u : 0u, baseId + 1, s + 1) == 0)
            {
                baseId = Vp8EncProba.ProbaIdx(coeffType, Vp8EncBands[n], 0);  // ctx=0
                s = baseId;
                continue;
            }
            if (AddToken(tokens, v > 1 ? 1u : 0u, baseId + 2, s + 2) == 0)
            {
                baseId = Vp8EncProba.ProbaIdx(coeffType, Vp8EncBands[n], 1);  // ctx=1
                s = baseId;
            }
            else
            {
                if (AddToken(tokens, v > 4 ? 1u : 0u, baseId + 3, s + 3) == 0)
                {
                    if (AddToken(tokens, v != 2 ? 1u : 0u, baseId + 4, s + 4) != 0)
                        AddToken(tokens, v == 4 ? 1u : 0u, baseId + 5, s + 5);
                }
                else if (AddToken(tokens, v > 10 ? 1u : 0u, baseId + 6, s + 6) == 0)
                {
                    if (AddToken(tokens, v > 6 ? 1u : 0u, baseId + 7, s + 7) == 0)
                    {
                        tokens.AddConstant(v == 6 ? 1u : 0u, 159);
                    }
                    else
                    {
                        tokens.AddConstant(v >= 9 ? 1u : 0u, 165);
                        tokens.AddConstant((v & 1) == 0 ? 1u : 0u, 145);
                    }
                }
                else
                {
                    int mask;
                    byte[] tab;
                    uint residue = v - 3;
                    if (residue < (8 << 1))
                    {
                        // VP8Cat3  (3b)
                        AddToken(tokens, 0, baseId + 8, s + 8);
                        AddToken(tokens, 0, baseId + 9, s + 9);
                        residue -= 8 << 0;
                        mask = 1 << 2;
                        tab = Cat3;
                    }
                    else if (residue < (8 << 2))
                    {
                        // VP8Cat4  (4b)
                        AddToken(tokens, 0, baseId + 8, s + 8);
                        AddToken(tokens, 1, baseId + 9, s + 9);
                        residue -= 8 << 1;
                        mask = 1 << 3;
                        tab = Cat4;
                    }
                    else if (residue < (8 << 3))
                    {
                        // VP8Cat5  (5b)
                        AddToken(tokens, 1, baseId + 8, s + 8);
                        AddToken(tokens, 0, baseId + 10, s + 9);
                        residue -= 8 << 2;
                        mask = 1 << 4;
                        tab = Cat5;
                    }
                    else
                    {
                        // VP8Cat6 (11b)
                        AddToken(tokens, 1, baseId + 8, s + 8);
                        AddToken(tokens, 1, baseId + 10, s + 9);
                        residue -= 8 << 3;
                        mask = 1 << 10;
                        tab = Cat6;
                    }
                    int ti = 0;
                    while (mask != 0)
                    {
                        tokens.AddConstant((residue & mask) != 0 ? 1u : 0u, tab[ti++]);
                        mask >>= 1;
                    }
                }
                baseId = Vp8EncProba.ProbaIdx(coeffType, Vp8EncBands[n], 2);  // ctx=2
                s = baseId;
            }
            tokens.AddConstant(sign ? 1u : 0u, 128);
            if (n == 16 || AddToken(tokens, n <= last ? 1u : 0u, baseId + 0, s + 0) == 0) return 1;   // EOB
        }
        return 1;
    }

    // ---- tree_enc.c ---------------------------------------------------------------------------------------------------

    private void DefaultProbas()
    {
        var probas = _proba;
        probas.UseSkipProba = false;
        Array.Fill(probas.Segments, (byte)255);
        Vp8EncTables.CoeffsProba0.CopyTo(probas.Coeffs, 0);
        probas.Dirty = true;
    }

    private static int PutI4Mode(Vp8BitWriter bw, int mode, byte[] prob, int p)
    {
        // B_DC_PRED=0, B_TM_PRED, B_VE_PRED, B_HE_PRED, B_RD_PRED, B_VR_PRED, B_LD_PRED, B_VL_PRED, B_HD_PRED, B_HU_PRED
        if (bw.PutBit(mode != 0 ? 1 : 0, prob[p + 0]) != 0)
        {
            if (bw.PutBit(mode != 1 ? 1 : 0, prob[p + 1]) != 0)
            {
                if (bw.PutBit(mode != 2 ? 1 : 0, prob[p + 2]) != 0)
                {
                    if (bw.PutBit(mode >= 6 ? 1 : 0, prob[p + 3]) == 0)
                    {
                        if (bw.PutBit(mode != 3 ? 1 : 0, prob[p + 4]) != 0)
                            bw.PutBit(mode != 4 ? 1 : 0, prob[p + 5]);
                    }
                    else
                    {
                        if (bw.PutBit(mode != 6 ? 1 : 0, prob[p + 6]) != 0)
                        {
                            if (bw.PutBit(mode != 7 ? 1 : 0, prob[p + 7]) != 0)
                                bw.PutBit(mode != 8 ? 1 : 0, prob[p + 8]);
                        }
                    }
                }
            }
        }
        return mode;
    }

    private static void PutI16Mode(Vp8BitWriter bw, int mode)
    {
        // DC_PRED=0, TM_PRED=1, V_PRED=2, H_PRED=3
        if (bw.PutBit(mode == 1 || mode == 3 ? 1 : 0, 156) != 0)
            bw.PutBit(mode == 1 ? 1 : 0, 128);    // TM or HE
        else
            bw.PutBit(mode == 2 ? 1 : 0, 163);    // VE or DC
    }

    private static void PutUVMode(Vp8BitWriter bw, int uvMode)
    {
        if (bw.PutBit(uvMode != 0 ? 1 : 0, 142) != 0)
        {
            if (bw.PutBit(uvMode != 2 ? 1 : 0, 114) != 0)
                bw.PutBit(uvMode != 3 ? 1 : 0, 183);    // else: TM_PRED
        }
    }

    private static void PutSegment(Vp8BitWriter bw, int s, byte[] p)
    {
        int o = 0;
        if (bw.PutBit(s >= 2 ? 1 : 0, p[0]) != 0) o = 1;
        bw.PutBit(s & 1, p[1 + o]);
    }

    private void CodeIntraModes()
    {
        var bw = _bw;
        var it = new Iterator(this);
        do
        {
            var mb = _mbInfo[it.Mb];
            int preds = it.Preds;
            if (_updateMap) PutSegment(bw, mb.Segment, _proba.Segments);
            if (_proba.UseSkipProba) bw.PutBit(mb.Skip, _proba.SkipProba);
            if (bw.PutBit(mb.Type != 0 ? 1 : 0, 145) != 0)
            {
                // i16x16
                PutI16Mode(bw, _predsMem[preds]);
            }
            else
            {
                int predsW = _predsW;
                int topPred = preds - predsW;
                for (int y = 0; y < 4; ++y)
                {
                    int left = _predsMem[preds - 1];
                    for (int x = 0; x < 4; ++x)
                    {
                        int probas = (_predsMem[topPred + x] * NumBModes + left) * (NumBModes - 1);
                        left = PutI4Mode(bw, _predsMem[preds + x], Vp8EncTables.BModesProba, probas);
                    }
                    topPred = preds;
                    preds += predsW;
                }
            }
            PutUVMode(bw, mb.UvMode);
        } while (it.Next());
    }

    private void WriteProbas(Vp8BitWriter bw)
    {
        var probas = _proba;
        for (int i = 0; i < probas.Coeffs.Length; ++i)
        {
            byte p0 = probas.Coeffs[i];
            bool update = p0 != Vp8EncTables.CoeffsProba0[i];
            if (bw.PutBit(update ? 1 : 0, Vp8EncTables.CoeffsUpdateProba[i]) != 0) bw.PutBits(p0, 8);
        }
        if (bw.PutBitUniform(probas.UseSkipProba ? 1 : 0) != 0) bw.PutBits((uint)probas.SkipProba, 8);
    }

    // ---- filter_enc.c -------------------------------------------------------------------------------------------------

    private static int FilterStrengthFromDelta(int sharpness, int delta)
    {
        int pos = delta < 64 ? delta : 64 - 1;
        return Vp8EncTables.LevelsFromDelta[sharpness * 64 + pos];
    }

    private static int GetILevel(int sharpness, int level)
    {
        if (sharpness > 0)
        {
            if (sharpness > 4)
                level >>= 2;
            else
                level >>= 1;
            if (level > 9 - sharpness) level = 9 - sharpness;
        }
        if (level < 1) level = 1;
        return level;
    }

    private void DoFilter(Iterator it, int level)
    {
        int ilevel = GetILevel(_config.FilterSharpness, level);
        int limit = 2 * level + ilevel;

        var yuv = it.Yuv;
        int yDst = it.YuvOut2 + YOffEnc;
        int uDst = it.YuvOut2 + UOffEnc;
        int vDst = it.YuvOut2 + VOffEnc;

        // copy current block to yuv_out2
        Array.Copy(yuv, it.YuvOut, yuv, yDst, YuvSizeEnc);

        if (_filterSimple)
        {
            // simple
            Vp8EncDsp.SimpleHFilter16i(yuv, yDst, Bps, limit);
            Vp8EncDsp.SimpleVFilter16i(yuv, yDst, Bps, limit);
        }
        else
        {
            // complex
            int hevThresh = level >= 40 ? 2 : level >= 15 ? 1 : 0;
            Vp8EncDsp.HFilter16i(yuv, yDst, Bps, limit, ilevel, hevThresh);
            Vp8EncDsp.HFilter8i(yuv, uDst, vDst, Bps, limit, ilevel, hevThresh);
            Vp8EncDsp.VFilter16i(yuv, yDst, Bps, limit, ilevel, hevThresh);
            Vp8EncDsp.VFilter8i(yuv, uDst, vDst, Bps, limit, ilevel, hevThresh);
        }
    }

    private static double GetMbSsim(byte[] yuv, int yuv1, int yuv2)
    {
        double sum = 0.0;
        const int k = Vp8EncDsp.SsimKernel;
        // compute SSIM in a 10 x 10 window
        for (int y = k; y < 16 - k; y++)
            for (int x = k; x < 16 - k; x++)
                sum += Vp8EncDsp.SsimGetClipped(yuv, yuv1 + YOffEnc, Bps, yuv, yuv2 + YOffEnc, Bps, x, y, 16, 16);
        for (int x = 1; x < 7; x++)
        {
            for (int y = 1; y < 7; y++)
            {
                sum += Vp8EncDsp.SsimGetClipped(yuv, yuv1 + UOffEnc, Bps, yuv, yuv2 + UOffEnc, Bps, x, y, 8, 8);
                sum += Vp8EncDsp.SsimGetClipped(yuv, yuv1 + VOffEnc, Bps, yuv, yuv2 + VOffEnc, Bps, x, y, 8, 8);
            }
        }
        return sum;
    }

    private void InitFilter()
    {
        if (_lfStats != null) Array.Clear(_lfStats);
    }

    private void StoreFilterStats(Iterator it)
    {
        if (_lfStats == null) return;
        int s = _mbInfo[it.Mb].Segment;
        int level0 = _dqm[s].FStrength;

        // explore +/-quant range of values around level0
        int deltaMin = -_dqm[s].Quant;
        int deltaMax = _dqm[s].Quant;
        int stepSize = deltaMax - deltaMin >= 4 ? 4 : 1;

        // NOTE: Currently we are applying filter only across the sublock edges
        // There are two reasons for that.
        // 1. Applying filter on macro block edges will change the pixels in
        // the left and top macro blocks. That will be hard to restore
        // 2. Macro Blocks on the bottom and right edges of the image
        // will not get the filtering. This is fine.
        if (_mbInfo[it.Mb].Type == 1 && _mbInfo[it.Mb].Skip != 0) return;

        // Always try filter level  zero
        _lfStats[s * MaxLfLevels + 0] += GetMbSsim(it.Yuv, Iterator.YuvIn, it.YuvOut);

        for (int d = deltaMin; d <= deltaMax; d += stepSize)
        {
            int level = level0 + d;
            if (level <= 0 || level >= MaxLfLevels) continue;
            DoFilter(it, level);
            _lfStats[s * MaxLfLevels + level] += GetMbSsim(it.Yuv, Iterator.YuvIn, it.YuvOut2);
        }
    }

    private void AdjustFilterStrength(Iterator it)
    {
        if (_lfStats != null)
        {
            for (int s = 0; s < NumMbSegments; s++)
            {
                int bestLevel = 0;
                // Improvement over filter level 0 should be at least 1e-5 (relatively)
                double bestV = 1.00001 * _lfStats[s * MaxLfLevels + 0];
                for (int i = 1; i < MaxLfLevels; i++)
                {
                    double v = _lfStats[s * MaxLfLevels + i];
                    if (v > bestV)
                    {
                        bestV = v;
                        bestLevel = i;
                    }
                }
                _dqm[s].FStrength = bestLevel;
            }
            return;
        }
        if (_config.FilterStrength > 0)
        {
            int maxLevel = 0;
            for (int s = 0; s < NumMbSegments; s++)
            {
                var dqm = _dqm[s];
                // this '>> 3' accounts for some inverse WHT scaling
                int delta = (dqm.MaxEdge * dqm.Y2.Q[1]) >> 3;
                int level = FilterStrengthFromDelta(_filterSharpness, delta);
                if (level > dqm.FStrength) dqm.FStrength = level;
                if (maxLevel < dqm.FStrength) maxLevel = dqm.FStrength;
            }
            _filterLevel = maxLevel;
        }
    }

    // ---- syntax_enc.c -------------------------------------------------------------------------------------------------

    private void PutSegmentHeader(Vp8BitWriter bw)
    {
        var proba = _proba;
        if (bw.PutBitUniform(_numSegments > 1 ? 1 : 0) != 0)
        {
            // We always 'update' the quant and filter strength values
            const int updateData = 1;
            bw.PutBitUniform(_updateMap ? 1 : 0);
            if (bw.PutBitUniform(updateData) != 0)
            {
                // we always use absolute values, not relative ones
                bw.PutBitUniform(1);   // (segment_feature_mode = 1. Paragraph 9.3.)
                for (int s = 0; s < NumMbSegments; ++s) bw.PutSignedBits(_dqm[s].Quant, 7);
                for (int s = 0; s < NumMbSegments; ++s) bw.PutSignedBits(_dqm[s].FStrength, 6);
            }
            if (_updateMap)
            {
                for (int s = 0; s < 3; ++s)
                {
                    if (bw.PutBitUniform(proba.Segments[s] != 255 ? 1 : 0) != 0) bw.PutBits(proba.Segments[s], 8);
                }
            }
        }
    }

    private void PutFilterHeader(Vp8BitWriter bw)
    {
        bool useLfDelta = _i4x4LfDelta != 0;
        bw.PutBitUniform(_filterSimple ? 1 : 0);
        bw.PutBits((uint)_filterLevel, 6);
        bw.PutBits((uint)_filterSharpness, 3);
        if (bw.PutBitUniform(useLfDelta ? 1 : 0) != 0)
        {
            // '0' is the default value for i4x4_lf_delta at frame #0.
            bool needUpdate = _i4x4LfDelta != 0;
            if (bw.PutBitUniform(needUpdate ? 1 : 0) != 0)
            {
                // we don't use ref_lf_delta => emit four 0 bits
                bw.PutBits(0, 4);
                // we use mode_lf_delta for i4x4
                bw.PutSignedBits(_i4x4LfDelta, 6);
                bw.PutBits(0, 3);    // all others unused
            }
        }
    }

    // Nominal quantization parameters
    private void PutQuant(Vp8BitWriter bw)
    {
        bw.PutBits((uint)_baseQuant, 7);
        bw.PutSignedBits(_dqY1Dc, 4);
        bw.PutSignedBits(_dqY2Dc, 4);
        bw.PutSignedBits(_dqY2Ac, 4);
        bw.PutSignedBits(_dqUvDc, 4);
        bw.PutSignedBits(_dqUvAc, 4);
    }

    private void GeneratePartition0()
    {
        int mbSize = _mbW * _mbH;
        _bw = new Vp8BitWriter(mbSize * 7 / 8);   // ~7 bits per macroblock
        var bw = _bw;
        bw.PutBitUniform(0);   // colorspace
        bw.PutBitUniform(0);   // clamp type

        PutSegmentHeader(bw);
        PutFilterHeader(bw);
        bw.PutBits(_numParts == 8 ? 3u : _numParts == 4 ? 2u : _numParts == 2 ? 1u : 0u, 2);
        PutQuant(bw);
        bw.PutBitUniform(0);   // no proba update
        WriteProbas(bw);
        CodeIntraModes();
        bw.Finish();
    }

    /// <summary>VP8EncWrite: the RIFF container (VP8X + ALPH when there is alpha) around the VP8 frame.</summary>
    private byte[] EncWrite()
    {
        GeneratePartition0();
        var bw = _bw;

        // Compute VP8 size
        long vp8Size = Vp8FrameHeaderSize + bw.Size + 3 * (_numParts - 1);
        for (int p = 0; p < _numParts; ++p) vp8Size += _parts[p].Size;
        int pad = (int)(vp8Size & 1);
        vp8Size += pad;

        // Compute RIFF size
        // At the minimum it is: "WEBPVP8 nnnn" + VP8 data size.
        long riffSize = TagSize + ChunkHeaderSize + vp8Size;
        bool vp8xNeeded = _hasAlpha;   // Currently the only case when VP8X is needed.
        if (vp8xNeeded) riffSize += ChunkHeaderSize + Vp8XChunkSize;   // Add size for: VP8X header + data.
        int alphaSize = _hasAlpha ? _alphaData!.Length : 0;
        if (_hasAlpha)
        {
            // Add size for: ALPH header + data.
            int paddedAlphaSize = alphaSize + (alphaSize & 1);
            riffSize += ChunkHeaderSize + paddedAlphaSize;
        }
        // RIFF size should fit in 32-bits.
        if (riffSize > 0xfffffffeL) throw new InvalidOperationException("WebP: file too big.");
        int size0 = bw.Size;
        if (size0 >= Vp8MaxPartition0Size) throw new InvalidOperationException("WebP: partition #0 is too big.");

        var output = new byte[ChunkHeaderSize + riffSize];
        var o = output.AsSpan();
        int pos = 0;

        // RIFF header
        "RIFF"u8.CopyTo(o);
        BitConverter.TryWriteBytes(o.Slice(4), (uint)riffSize);
        "WEBP"u8.CopyTo(o.Slice(8));
        pos = RiffHeaderSize;

        if (vp8xNeeded)
        {
            "VP8X"u8.CopyTo(o.Slice(pos));
            BitConverter.TryWriteBytes(o.Slice(pos + 4), (uint)Vp8XChunkSize);
            uint flags = 0;
            if (_hasAlpha) flags |= 0x00000010;   // ALPHA_FLAG
            BitConverter.TryWriteBytes(o.Slice(pos + 8), flags);
            PutLe24(o.Slice(pos + 12), _pic.Width - 1);
            PutLe24(o.Slice(pos + 15), _pic.Height - 1);
            pos += ChunkHeaderSize + Vp8XChunkSize;
        }

        if (_hasAlpha)
        {
            "ALPH"u8.CopyTo(o.Slice(pos));
            BitConverter.TryWriteBytes(o.Slice(pos + 4), (uint)alphaSize);
            pos += ChunkHeaderSize;
            _alphaData.AsSpan().CopyTo(o.Slice(pos));
            pos += alphaSize;
            if ((alphaSize & 1) != 0) output[pos++] = 0;
        }

        // VP8 header
        "VP8 "u8.CopyTo(o.Slice(pos));
        BitConverter.TryWriteBytes(o.Slice(pos + 4), (uint)vp8Size);
        pos += ChunkHeaderSize;

        // VP8 frame header
        {
            uint bits = 0                         // keyframe (1b)
                      | ((uint)_profile << 1)     // profile (3b)
                      | (1u << 4)                 // visible (1b)
                      | ((uint)size0 << 5);       // partition length (19b)
            output[pos + 0] = (byte)(bits >> 0);
            output[pos + 1] = (byte)(bits >> 8);
            output[pos + 2] = (byte)(bits >> 16);
            // signature
            output[pos + 3] = 0x9d;
            output[pos + 4] = 0x01;
            output[pos + 5] = 0x2a;
            // dimensions
            output[pos + 6] = (byte)(_pic.Width & 0xff);
            output[pos + 7] = (byte)(_pic.Width >> 8);
            output[pos + 8] = (byte)(_pic.Height & 0xff);
            output[pos + 9] = (byte)(_pic.Height >> 8);
            pos += Vp8FrameHeaderSize;
        }

        // Partition #0 with header and partition sizes
        bw.Buffer.AsSpan(0, size0).CopyTo(o.Slice(pos));
        pos += size0;
        for (int p = 0; p < _numParts - 1; ++p)
        {
            int partSize = _parts[p].Size;
            if (partSize >= Vp8MaxPartitionSize) throw new InvalidOperationException("WebP: partition is too big.");
            output[pos++] = (byte)(partSize >> 0);
            output[pos++] = (byte)(partSize >> 8);
            output[pos++] = (byte)(partSize >> 16);
        }

        // Token partitions
        for (int p = 0; p < _numParts; ++p)
        {
            int size = _parts[p].Size;
            _parts[p].Buffer.AsSpan(0, size).CopyTo(o.Slice(pos));
            pos += size;
        }

        // Padding byte
        if (pad != 0) output[pos++] = 0;
        return output;
    }

    private static void PutLe24(Span<byte> d, int v)
    {
        d[0] = (byte)v;
        d[1] = (byte)(v >> 8);
        d[2] = (byte)(v >> 16);
    }
}
