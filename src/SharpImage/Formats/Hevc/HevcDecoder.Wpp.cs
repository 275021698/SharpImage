using System.Threading;
using System.Threading.Tasks;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Wavefront (WPP) decoding: with entropy_coding_sync_enabled_flag every CTU row has its own entry point and
/// starts from the contexts saved after the second CTU of the row above, so the rows of a slice decode in
/// parallel, each two CTUs behind the row above (which covers the above-right samples, SAO merge-up and the
/// saved contexts). The result is identical to decoding the rows one after another.
/// </summary>
internal sealed partial class HevcDecoder
{
    /// <summary>Threads used for the rows of a WPP slice (1 decodes them one after another).</summary>
    public int MaxWppThreads { get; set; } = t_maxWppThreads > 0 ? t_maxWppThreads : Environment.ProcessorCount;

    // the default for the decoders created on this thread (tests compare against the rows decoded in order)
    [ThreadStatic] internal static int t_maxWppThreads;

    // per-worker scratch, kept across slices and pictures
    private WppScratch[]? wppScratch;

    private sealed class WppScratch
    {
        public readonly short[] Residual = new short[64 * 64];
        public readonly int[] Coefficients = new int[64 * 64];
        public readonly byte[] Contexts = new byte[CabacContextStorageSize];
        public readonly short[] CcpLumaResidual = new short[32 * 32];
        public readonly int[] IntraPredModes = new int[4];
        public readonly int[] IntraPredModesC = new int[4];
        public readonly int[] RawChromaModes = new int[4];
        public readonly int[] StatCoeff = new int[4];
    }

    private sealed class WppSlice
    {
        public required byte[] Rbsp;
        public required int DataOffset;
        public required int[] RowOffsets;
        public required HevcSliceSegmentHeader Header;
        public required HevcSequenceParameterSet Sps;
        public required HevcPictureParameterSet Pps;
        public required int FirstRow;
        public required int CtbWidth;
        public required int SliceQp;
        public required int InitType;
        public required byte[] SavedContexts;  // per row: the contexts after its second CTU
        public required int[] Progress;        // per row: CTUs done; int.MaxValue once the row has stopped
        public required int[] Decoded;         // per row: CTUs decoded
        public required int[] Worker;          // per row: the worker that decoded it
        public int StopRow = int.MaxValue;     // the first row that ended the slice (error or end_of_slice_segment)
        public int NextRow = -1;
    }

    private bool CanDecodeWppRowsInParallel(HevcSliceSegmentHeader sliceHeader, HevcSequenceParameterSet sps,
        HevcPictureParameterSet pps, int startCtb, int ctbWidth, int rows)
    {
        return MaxWppThreads > 1 && rows >= 2 && ctbWidth >= 3
            && pps.EntropyCodingSyncEnabled && !pps.TilesEnabled
            && !sliceHeader.DependentSliceSegmentFlag && startCtb % ctbWidth == 0
            && sliceHeader.SliceType == HevcSliceType.ISlice
            && !sps.PersistentRiceAdaptationEnabled && !sliceHeader.CuChromaQpOffsetEnabled
            && startCtb / ctbWidth + rows <= context.PicHeightInCtbsY
            && currentFrameBuffer != null
            && !DiagSkipWppReinit && !DiagForceTableContextsForWpp && DiagCaptureSliceStartCtb < 0
            && DiagIntraPredLog == null && DiagCbfLog == null && DiagQpDeltaLog == null && DiagCtuStartLog == null
            && DiagCtuPhaseLog == null && DiagTransformTreeLog == null && DiagTraceCtuAddrs == null
            && DiagCtuBinLogs == null && DiagWppOffsetLog == null && DiagWppContextLog == null
            && DiagWppEntryBytesLog == null && DiagSliceLog == null && DiagResidualLog == null
            && DiagDecodeCallLog == null && DiagCtuMoreDataLog == null;
    }

    private void DecodeWppRowsInParallel(byte[] rbsp, int dataOffset, int[] rowOffsets, HevcSliceSegmentHeader sliceHeader,
        HevcSequenceParameterSet sps, HevcPictureParameterSet pps, int startCtb, int ctbWidth, int sliceQp, int initType)
    {
        if (tabCtDepth == null)
        {
            AllocatePerPictureArrays(sps);
            lastAllocatedSps = sps;
        }

        int rows = rowOffsets.Length;
        int ctxBytes = HevcCabacContextIndex.TotalContexts;
        var job = new WppSlice
        {
            Rbsp = rbsp,
            DataOffset = dataOffset,
            RowOffsets = rowOffsets,
            Header = sliceHeader,
            Sps = sps,
            Pps = pps,
            FirstRow = startCtb / ctbWidth,
            CtbWidth = ctbWidth,
            SliceQp = sliceQp,
            InitType = initType,
            SavedContexts = new byte[rows * ctxBytes],
            Progress = new int[rows],
            Decoded = new int[rows],
            Worker = new int[rows],
        };

        // more rows than about half the columns cannot be in flight at once
        int threads = Math.Min(Math.Min(MaxWppThreads, rows), Math.Max(2, (ctbWidth + 1) / 2));
        if (wppScratch == null || wppScratch.Length < threads)
        {
            var grown = new WppScratch[threads];
            wppScratch?.CopyTo(grown, 0);
            for (int i = wppScratch?.Length ?? 0; i < threads; i++)
                grown[i] = new WppScratch();
            wppScratch = grown;
        }

        var workers = new HevcDecoder[threads];
        for (int i = 0; i < threads; i++)
            workers[i] = CloneForWppWorker(wppScratch[i]);

        // rows are taken in order, so a row only ever waits for rows that running workers hold
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, w =>
        {
            int r;
            while ((r = Interlocked.Increment(ref job.NextRow)) < rows)
            {
                job.Worker[r] = w;
                workers[w].DecodeWppRow(job, r);
            }
        });

        // leave the state as decoding the rows in order would: the last row's contexts and QP for a following
        // dependent slice, the last saved WPP contexts for a following slice that starts a row
        int stop = Math.Min(job.StopRow, rows - 1);
        var last = workers[job.Worker[stop]];
        last.cabacContextStorage.AsSpan().CopyTo(cabacContextStorage);
        qpY = last.qpY;
        qpYPred = last.qpYPred;
        firstQpGroup = last.firstQpGroup;
        currentSliceQp = last.currentSliceQp;
        isCuQpDeltaCoded = last.isCuQpDeltaCoded;
        cuQpDelta = last.cuQpDelta;

        int ctus = 0, savedRow = -1;
        for (int r = 0; r <= stop; r++)
        {
            ctus += job.Decoded[r];
            if (job.Decoded[r] >= 2)
                savedRow = r;
        }
        if (savedRow >= 0)
        {
            wppSavedContexts ??= new byte[ctxBytes];
            job.SavedContexts.AsSpan(savedRow * ctxBytes, ctxBytes).CopyTo(wppSavedContexts);
        }

        // the diagnostic counters the workers kept
        int cbfLuma = DiagCbfLumaCount, cbfChroma = DiagCbfChromaCount, nonZero = DiagNonZeroCoeffCount, tus = DiagTotalTuCount;
        foreach (var wk in workers)
        {
            DiagCbfLumaCount += wk.DiagCbfLumaCount - cbfLuma;
            DiagCbfChromaCount += wk.DiagCbfChromaCount - cbfChroma;
            DiagNonZeroCoeffCount += wk.DiagNonZeroCoeffCount - nonZero;
            DiagTotalTuCount += wk.DiagTotalTuCount - tus;
        }

        LastCtusDecoded = ctus;
        LastTotalCtus = context.PicWidthInCtbsY * context.PicHeightInCtbsY;
    }

    private HevcDecoder CloneForWppWorker(WppScratch s)
    {
        var c = (HevcDecoder)MemberwiseClone();
        c.residualBuffer = s.Residual;
        c.coefficientBuffer = s.Coefficients;
        c.cabacContextStorage = s.Contexts;
        c.ccpLumaResidual = s.CcpLumaResidual;
        c.intraPredModeForPu = s.IntraPredModes;
        c.intraPredModeCForPu = s.IntraPredModesC;
        c.rawChromaModeForPu = s.RawChromaModes;
        c.statCoeff = s.StatCoeff;
        c.wppSavedContexts = null;
        c.wppSavedStatCoeff = null;
        c.wppScratch = null;
        return c;
    }

    private void DecodeWppRow(WppSlice job, int r)
    {
        int[] progress = job.Progress;
        int ctbWidth = job.CtbWidth;
        int ctxBytes = HevcCabacContextIndex.TotalContexts;
        try
        {
            var cabac = new HevcCabacDecoder(job.Rbsp.AsSpan(job.DataOffset), cabacContextStorage);
            if (r == 0)
            {
                cabac.InitializeContexts(job.SliceQp, job.InitType);
            }
            else
            {
                if (!WaitForRowAbove(job, r, 2))
                    return;
                cabac.ReinitAtOffset(job.RowOffsets[r]);
                cabac.LoadContextStates(job.SavedContexts.AsSpan((r - 1) * ctxBytes, ctxBytes));
            }

            int ctbY = job.FirstRow + r;
            Span<short> residual = residualBuffer.AsSpan();
            for (int ctbX = 0; ctbX < ctbWidth; ctbX++)
            {
                if (r > 0 && !WaitForRowAbove(job, r, Math.Min(ctbX + 2, ctbWidth)))
                    return;

                bool moreData = DecodeCtu(ref cabac, job.Header, job.Sps, job.Pps, ctbX, ctbY, residual);
                if (ctbX == 1)
                    cabac.SaveContextStates(job.SavedContexts.AsSpan(r * ctxBytes, ctxBytes));
                job.Decoded[r] = ctbX + 1;
                Volatile.Write(ref progress[r], ctbX + 1);

                // the slice ends here: anywhere but the end of its last row, the rows after it are not decoded
                if (!moreData)
                {
                    if (ctbX != ctbWidth - 1 || r != progress.Length - 1)
                        Stop(job, r);
                    return;
                }
            }
        }
        catch (Exception)
        {
            Stop(job, r);
        }
        finally
        {
            Volatile.Write(ref progress[r], int.MaxValue);
        }
    }

    private static void Stop(WppSlice job, int r)
    {
        int seen;
        while (r < (seen = Volatile.Read(ref job.StopRow)) && Interlocked.CompareExchange(ref job.StopRow, r, seen) != seen)
        {
        }
    }

    // Waits until the row above has decoded 'needed' CTUs; false when an earlier row ended the slice.
    private static bool WaitForRowAbove(WppSlice job, int r, int needed)
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref job.Progress[r - 1]) < needed)
            spin.SpinOnce(sleep1Threshold: -1);
        return Volatile.Read(ref job.StopRow) >= r;
    }
}
