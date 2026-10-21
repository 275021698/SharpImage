// libjxl 0.12 frame decoding: FrameDecoder (dec_frame.cc), ModularFrameDecoder (dec_modular.cc), the AC group decoder
// (dec_group.cc) and pipeline assembly (dec_cache.cc PreparePipeline). Single-threaded and whole-frame: every
// section of the frame is present, so groups are decoded in order and the pipeline then runs over the full frame.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl.Lj;

/// <summary>A saved frame (ImageBundle in PassesSharedState::reference_frames): colour planes then extra channels.</summary>
internal sealed class LjReferenceFrame
{
    public LjImageF[]? Planes;
    public bool IbIsInXyb;

    public bool Empty => Planes == null || Planes[0].XSize == 0 || Planes[0].YSize == 0;
}

/// <summary>State shared by the frames of one codestream.</summary>
internal sealed class LjDecoderState
{
    public LjSizeHeader Size = new();
    public LjImageMetadata Meta = new();
    public LjCustomTransformData Transform = new();
    public LjOutputEncoding Output = new();
    public LjReferenceFrame[] RefFrames = [new(), new(), new(), new()];
    public LjImage3F?[] DcFrames = new LjImage3F?[4];
    public bool Coalescing = true;
    public bool RenderSpotColors = true;
    public int VisibleFrameIndex, NonvisibleFrameIndex;

    public int ImageXsize => (int)Size.XsizeValue;

    public int ImageYsize => (int)Size.YsizeValue;
}

internal sealed partial class LjFrameDecoder
{
    private readonly LjDecoderState st;
    public LjFrameHeader Fh = new();
    public LjFrameDims Fd = new();
    private uint[] tocSizes = [];
    private int[] tocIds = [];
    private int headerBytes;

    // PassesSharedState
    private LjDequantMatrices matrices = new();
    private LjQuantizer quantizer = null!;
    private LjBlockCtxMap blockCtxMap = new();
    private LjColorCorrelation cmap = null!;
    private LjImageB acStrategy = null!;
    private LjImageI rawQuantField = null!;
    private LjImageB epfSharpness = null!;
    private LjImageB quantDc = null!;
    private LjImage3F dc = null!;
    private uint usedAcs;
    private LjImageF? sigma;
    private float xDmMultiplier, bDmMultiplier;
    private uint[] coeffOrders = [];
    private int coeffOrderSize;
    private int numHistograms;
    private LjAnsCode[] acCode = [];
    private byte[][] acContextMap = [];

    // ModularFrameDecoder
    private LjModularImage fullImage = new();
    private readonly LjDecModular.GlobalTree tree = new();
    private LjGroupHeader globalHeader = new();
    private bool doColor;

    // Pipeline
    private LjRenderPipeline pipeline = null!;
    private LjWriteStage outputStage = null!;
    private LjWriteStage? refStage;
    private LjWriteStage? dcFrameStage;

    // Features
    private LjPatchDictionary? patches;
    private LjSplines? splines;
    private LjNoiseParams? noise;

    /// <summary>JPEG reconstruction target: coefficients are written here instead of pixels.</summary>
    public LjJpegData? JpegData;

    public LjFrameDecoder(LjDecoderState state) => st = state;

    public bool IsLast => Fh.IsLast;

    public uint Duration => Fh.AnimationFrame.Duration;

    /// <summary>Reads the frame header and TOC. Returns the total frame size in bytes (header + sections).</summary>
    public long InitFrame(byte[] data, int offset, bool isPreview)
    {
        var br = new LjBitReader(data, offset, data.Length - offset);
        Fh = new LjFrameHeader
        {
            NonserializedMetadata = st.Meta,
            NonserializedIsPreview = isPreview,
            NonserializedXsize = isPreview ? PreviewXsize() : (uint)st.ImageXsize,
            NonserializedYsize = isPreview ? PreviewYsize() : (uint)st.ImageYsize,
        };
        LjFields.ReadInto(Fh, br);
        Fd = LjFrameDims.FromHeader(Fh);
        if (!isPreview && (Fh.IsLast || Fh.AnimationFrame.Duration > 0) &&
            (Fh.FrameType == LjFrameHeader.RegularFrame || Fh.FrameType == LjFrameHeader.SkipProgressive))
        {
            ++st.VisibleFrameIndex;
            st.NonvisibleFrameIndex = 0;
        }
        else
        {
            ++st.NonvisibleFrameIndex;
        }

        int numPasses = (int)Fh.Passes.NumPasses;
        int tocEntries = LjToc.NumTocEntries(Fd.NumGroups, Fd.NumDcGroups, numPasses);
        LjToc.Read(tocEntries, br, out uint[] sizes, out uint[]? permutation);
        tocSizes = sizes;
        tocIds = new int[tocEntries];
        for (int i = 0; i < tocEntries; ++i)
        {
            int index = permutation != null ? (int)permutation[i] : i;
            tocIds[index] = i;
        }

        if (!br.AllReadsWithinBounds)
        {
            throw LjDec.Fail("Truncated frame header");
        }

        headerBytes = (int)(br.TotalBitsConsumed / 8);
        if (!Fh.ChromaSubsampling.Is444 && (Fh.Flags & LjFrameHeader.FlagSkipAdaptiveDcSmoothing) == 0 && Fh.Encoding == LjFrameHeader.EncVarDct)
        {
            throw LjDec.Fail("Non-444 chroma subsampling is not allowed when adaptive DC smoothing is enabled");
        }

        long total = headerBytes;
        foreach (uint s in tocSizes)
        {
            total += s;
        }

        return total;
    }

    private uint PreviewXsize()
    {
        LjPreviewHeader p = st.Meta.PreviewSize;
        uint ys = p.Div8 ? p.YsizeDiv8 * 8 : p.Ysize;
        if (p.Ratio != 0)
        {
            var s = new LjSizeHeader { Small = false, Ysize = ys, Ratio = p.Ratio };
            return s.XsizeValue;
        }

        return p.Div8 ? p.XsizeDiv8 * 8 : p.Xsize;
    }

    private uint PreviewYsize()
    {
        LjPreviewHeader p = st.Meta.PreviewSize;
        return p.Div8 ? p.YsizeDiv8 * 8 : p.Ysize;
    }

    /// <summary>FrameDecoder::SavedAs.</summary>
    public static int SavedAs(LjFrameHeader h)
    {
        if (h.FrameType == LjFrameHeader.DcFrame)
        {
            return 16 << ((int)h.DcLevel - 1);
        }

        return h.CanBeReferenced ? 1 << (int)h.SaveAsReference : 0;
    }

    // ================================================================== section processing

    /// <summary>Decodes every section of the frame (all present) and runs the render pipeline.</summary>
    public void DecodeSections(byte[] data, int frameOffset)
    {
        int numPasses = (int)Fh.Passes.NumPasses;
        int n = tocSizes.Length;
        var readers = new LjBitReader[n];   // by section id
        long pos = frameOffset + headerBytes;
        for (int i = 0; i < n; i++)
        {
            if (pos + tocSizes[i] > data.Length)
            {
                throw LjDec.Fail("Truncated frame sections");
            }

            readers[tocIds[i]] = new LjBitReader(data, (int)pos, (int)tocSizes[i]);
            pos += tocSizes[i];
        }

        InitFrameOutput();
        bool single = Fd.NumGroups == 1 && numPasses == 1;
        LjBitReader DcGlobal() => readers[0];
        LjBitReader DcGroup(int g) => single ? readers[0] : readers[1 + g];
        LjBitReader AcGlobal() => single ? readers[0] : readers[1 + Fd.NumDcGroups];
        LjBitReader AcGroup(int g, int pass) => single ? readers[0] : readers[2 + Fd.NumDcGroups + (pass * Fd.NumGroups) + g];

        LjProf.Mark("frame start");
        ProcessDcGlobal(DcGlobal());
        LjProf.Mark("dc global");
        RunParallel(Fd.NumDcGroups, g => ProcessDcGroup(g, DcGroup(g)));

        LjProf.Mark("dc groups");
        PreparePipeline();
        FinalizeDc();
        LjProf.Mark("finalize dc");
        ProcessAcGlobal(AcGlobal());
        LjProf.Mark("ac global");
        RunParallel(Fd.NumGroups, g =>
        {
            var passReaders = new LjBitReader[numPasses];
            for (int p = 0; p < numPasses; p++)
            {
                passReaders[p] = AcGroup(g, p);
            }

            ProcessAcGroup(g, passReaders);
        });

        foreach (LjBitReader r in readers)
        {
            if (r != null && !r.AllReadsWithinBounds)
            {
                throw LjDec.Fail("frame out of bounds");
            }
        }

        if (JpegData != null)
        {
            return;
        }

        LjProf.Mark("ac groups");
        FinalizeModular();
        LjProf.Mark("finalize modular");
        pipeline.Run();
        LjProf.Mark("pipeline");
        if (Fh.CanBeReferenced)
        {
            LjReferenceFrame rf = st.RefFrames[Fh.SaveAsReference];
            rf.Planes = refStage?.Planes;
            rf.IbIsInXyb = Fh.SaveBeforeColorTransform;
        }

        if (dcFrameStage != null)
        {
            st.DcFrames[Fh.DcLevel - 1] = new LjImage3F(dcFrameStage.Planes[0], dcFrameStage.Planes[1], dcFrameStage.Planes[2]);
        }
    }

    /// <summary>Runs independent group jobs on the thread pool; a decode error surfaces as itself, not as an aggregate.</summary>
    internal static void RunParallel(int count, Action<int> body)
    {
        if (count <= 1)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        try
        {
            System.Threading.Tasks.Parallel.For(0, count, body);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>The rendered frame (colour planes then extra channels): image-sized when blended/coalesced.</summary>
    public LjImageF[] OutputPlanes => outputStage.Planes;

    private void InitFrameOutput()
    {
        // InitializePassesSharedState + PassesDecoderState::Init.
        matrices = new LjDequantMatrices();
        quantizer = new LjQuantizer(matrices);
        cmap = new LjColorCorrelation(Fd.Xsize, Fd.Ysize);
        acStrategy = new LjImageB(Fd.XsizeBlocks, Fd.YsizeBlocks);
        rawQuantField = new LjImageI(Fd.XsizeBlocks, Fd.YsizeBlocks);
        epfSharpness = new LjImageB(Fd.XsizeBlocks, Fd.YsizeBlocks);
        quantDc = new LjImageB(Fd.XsizeBlocks, Fd.YsizeBlocks);
        if ((Fh.Flags & LjFrameHeader.FlagUseDcFrame) != 0)
        {
            if (Fh.DcLevel == 4)
            {
                throw LjDec.Fail("Invalid DC level for kUseDcFrame");
            }

            dc = st.DcFrames[Fh.DcLevel] ?? throw LjDec.Fail("kUseDcFrame without a decoded DC frame");
        }
        else
        {
            dc = new LjImage3F(Fd.XsizeBlocks, Fd.YsizeBlocks);
        }

        xDmMultiplier = LjDec.PowF(1 / 1.25f, Fh.XQmScale - 2.0f);
        bDmMultiplier = LjDec.PowF(1 / 1.25f, Fh.BQmScale - 2.0f);
        usedAcs = 0;
        if (Fh.LoopFilter.EpfIters > 0)
        {
            sigma = new LjImageF(Fd.XsizeBlocks + 4, Fd.YsizeBlocks + 4);
        }

        if (JpegData != null)
        {
            InitJpegOutput();
        }
    }

    private void ProcessDcGlobal(LjBitReader br)
    {
        patches = null;
        splines = null;
        noise = null;
        if ((Fh.Flags & LjFrameHeader.FlagPatches) != 0)
        {
            patches = LjPatchDictionary.Decode(br, Fd.XsizePadded, Fd.YsizePadded, st.Meta.ExtraChannelInfo.Count, st.RefFrames);
            if (Fh.Upsampling != 1)
            {
                foreach (uint ecups in Fh.ExtraChannelUpsampling)
                {
                    if (ecups != Fh.Upsampling)
                    {
                        throw LjDec.Fail("Cannot use extra channels in patches if color channels are subsampled differently from extra channels");
                    }
                }
            }
        }

        if ((Fh.Flags & LjFrameHeader.FlagSplines) != 0)
        {
            splines = LjSplines.Decode(br, (long)Fd.Xsize * Fd.Ysize);
        }

        if ((Fh.Flags & LjFrameHeader.FlagNoise) != 0)
        {
            noise = LjNoiseParams.Decode(br);
        }

        matrices.DecodeDc(br);
        if (Fh.Encoding == LjFrameHeader.EncVarDct)
        {
            quantizer.Decode(br);
            blockCtxMap = LjBlockCtxMap.Decode(br);
            cmap.DecodeDc(br);
            if (JpegData != null)
            {
                quantizer.ClearDcMul();
            }

            Array.Fill(acStrategy.Data, (byte)0xFF);
        }

        if (splines != null)
        {
            splines.InitializeDrawCache(Fd.XsizeUpsampled, Fd.YsizeUpsampled, cmap);
        }

        DecodeModularGlobalInfo(br);
    }

    private void ProcessDcGroup(int dcGroupId, LjBitReader br)
    {
        int gx = dcGroupId % Fd.XsizeDcGroups;
        int gy = dcGroupId / Fd.XsizeDcGroups;
        if (Fh.Encoding == LjFrameHeader.EncVarDct && (Fh.Flags & LjFrameHeader.FlagUseDcFrame) == 0)
        {
            DecodeVarDctDc(dcGroupId, br);
        }

        var mrect = new LjRect(gx * Fd.DcGroupDim, gy * Fd.DcGroupDim, Fd.DcGroupDim, Fd.DcGroupDim);
        DecodeModularGroup(mrect, br, 3, 1000, 1 + Fd.NumDcGroups + dcGroupId, false);
        if (Fh.Encoding == LjFrameHeader.EncVarDct)
        {
            DecodeAcMetadata(dcGroupId, br);
        }
        else if (Fh.LoopFilter.EpfIters > 0)
        {
            Array.Fill(sigma!.Data, -1.1715728752538099024f / Fh.LoopFilter.EpfSigmaForModular);
        }
    }

    private void FinalizeDc()
    {
        if (Fh.Encoding == LjFrameHeader.EncVarDct && (Fh.Flags & LjFrameHeader.FlagSkipAdaptiveDcSmoothing) == 0 &&
            (Fh.Flags & LjFrameHeader.FlagUseDcFrame) == 0)
        {
            LjDecVarDct.AdaptiveDcSmoothing(quantizer.MulDc, dc);
        }
    }

    private void ProcessAcGlobal(LjBitReader br)
    {
        if (Fh.Encoding == LjFrameHeader.EncVarDct)
        {
            matrices.Decode(br, (w, h, idx, r) => DecodeQuantTableModular(w, h, idx, r));
            matrices.EnsureComputed(usedAcs);
            int numHistoBits = LjDec.CeilLog2Nonzero(Fd.NumGroups);
            numHistograms = 1 + (int)br.ReadBits(numHistoBits);

            // PassesDecoderState::InitForAC.
            coeffOrderSize = 0;
            for (int o = 0; o < LjDecTransforms.NumValidStrategies; ++o)
            {
                if (((1u << o) & usedAcs) == 0)
                {
                    continue;
                }

                int ord = LjDecTransforms.StrategyOrder[o];
                coeffOrderSize = Math.Max(LjDecTransforms.CoeffOrderOffset[3 * (ord + 1)] * 64, coeffOrderSize);
            }

            int numPasses = (int)Fh.Passes.NumPasses;
            coeffOrders = new uint[Math.Max(1, numPasses * coeffOrderSize)];
            acCode = new LjAnsCode[numPasses];
            acContextMap = new byte[numPasses][];
            for (int i = 0; i < numPasses; i++)
            {
                uint usedOrders = LjDecVarDct.ReadUsedOrders(br);
                LjDecVarDct.DecodeCoeffOrders(usedOrders, usedAcs, coeffOrders, i * coeffOrderSize, br);
                int numContexts = numHistograms * blockCtxMap.NumAcContexts;
                acCode[i] = LjDecAns.DecodeHistograms(br, numContexts, out byte[] ctxMap);
                var extended = new byte[numContexts + LjBlockCtxMap.ZeroDensityContextLimit - LjBlockCtxMap.ZeroDensityContextCount];
                Array.Copy(ctxMap, extended, Math.Min(ctxMap.Length, extended.Length));
                acContextMap[i] = extended;
            }
        }

        if (JpegData != null)
        {
            SetJpegQuantTables();
        }
    }

    private void ProcessAcGroup(int g, LjBitReader[] passReaders)
    {
        if (Fh.Encoding == LjFrameHeader.EncVarDct)
        {
            DecodeVarDctGroup(g, passReaders);
        }

        int numPasses = (int)Fh.Passes.NumPasses;
        int gx = g % Fd.XsizeGroups, gy = g / Fd.XsizeGroups;
        var mrect = new LjRect(gx * Fd.GroupDim, gy * Fd.GroupDim, Fd.GroupDim, Fd.GroupDim);
        for (int i = 0; i < numPasses; ++i)
        {
            GetDownsamplingBracket(i, out int minShift, out int maxShift);
            int streamId = 1 + (3 * Fd.NumDcGroups) + LjDequantMatrices.NumQuantTables + (Fd.NumGroups * i) + g;
            DecodeModularGroup(mrect, passReaders[i], minShift, maxShift, streamId, false);
        }

        if (noise != null && JpegData == null)
        {
            PrepareNoiseInput(g);
        }
    }

    private void GetDownsamplingBracket(int pass, out int minShift, out int maxShift)
    {
        LjPasses p = Fh.Passes;
        maxShift = 2;
        minShift = 3;
        for (int i = 0; ; i++)
        {
            for (int j = 0; j < p.NumDownsample; ++j)
            {
                if (i == p.LastPass[j])
                {
                    minShift = p.Downsample[j] switch { 8 => 3, 4 => 2, 2 => 1, 1 => 0, _ => minShift };
                }
            }

            if (i == p.NumPasses - 1)
            {
                minShift = 0;
            }

            if (i == pass)
            {
                return;
            }

            maxShift = minShift - 1;
        }
    }

    // ================================================================== modular

    private void DecodeModularGlobalInfo(LjBitReader br)
    {
        LjImageMetadata m = st.Meta;
        bool decodeColor = Fh.Encoding == LjFrameHeader.EncModular;
        bool isGray = m.ColorEncoding.ColorSpace == LjColorEncoding.CsGray;
        int nbChans = 3;
        if (isGray && Fh.ColorTransform == LjFrameHeader.CtNone)
        {
            nbChans = 1;
        }

        doColor = decodeColor;
        int nbExtra = m.ExtraChannelInfo.Count;
        bool hasTree = br.ReadBits(1) != 0;
        tree.Tree = new List<LjTreeNode>();
        tree.Code = null;
        if (hasTree)
        {
            long treeSizeLimit = Math.Min(1L << 22, 1024 + ((long)Fd.Xsize * Fd.Ysize * (nbChans + nbExtra) / 16));
            tree.Tree = LjDecModular.DecodeTree(br, treeSizeLimit);
            tree.Code = LjDecAns.DecodeHistograms(br, (tree.Tree.Count + 1) / 2, out tree.ContextMap);
        }

        if (!doColor)
        {
            nbChans = 0;
        }

        bool fp = m.BitDepth.FloatingPointSample;
        if (m.BitDepth.BitsPerSample >= 32 && doColor && Fh.ColorTransform != LjFrameHeader.CtXyb)
        {
            if (m.BitDepth.BitsPerSample == 32 && !fp)
            {
                throw LjDec.Fail("uint32_t not supported in dec_modular");
            }

            if (m.BitDepth.BitsPerSample > 32)
            {
                throw LjDec.Fail("bits_per_sample > 32 not supported");
            }
        }

        LjModularImage gi = LjModularImage.Create(Fd.Xsize, Fd.Ysize, (int)m.BitDepth.BitsPerSample, nbChans + nbExtra);
        if (Fh.ColorTransform == LjFrameHeader.CtYCbCr)
        {
            for (int c = 0; c < nbChans; c++)
            {
                LjChannel ch = gi.Channel[c];
                ch.HShift = Fh.ChromaSubsampling.HShift(c);
                ch.VShift = Fh.ChromaSubsampling.VShift(c);
                gi.Channel[c] = new LjChannel(LjDec.DivCeil(Fd.Xsize, 1 << ch.HShift), LjDec.DivCeil(Fd.Ysize, 1 << ch.VShift), ch.HShift, ch.VShift);
            }
        }

        for (int ec = 0, c = nbChans; ec < nbExtra; ec++, c++)
        {
            int ecups = (int)Fh.ExtraChannelUpsampling[ec];
            int shift = LjDec.CeilLog2Nonzero(ecups) - LjDec.CeilLog2Nonzero(Fh.Upsampling);
            gi.Channel[c] = new LjChannel(LjDec.DivCeil(Fd.XsizeUpsampled, ecups), LjDec.DivCeil(Fd.YsizeUpsampled, ecups), shift, shift);
        }

        globalHeader = new LjGroupHeader();
        LjDecModular.GenericDecompress(br, gi, globalHeader, 0, Fd.GroupDim, Fd.GroupDim, false, tree);
        fullImage = gi;
    }

    /// <summary>ModularFrameDecoder::DecodeGroup (full-image mode).</summary>
    private void DecodeModularGroup(LjRect rect, LjBitReader br, int minShift, int maxShift, int streamId, bool zerofill)
    {
        var gi = new LjModularImage { W = rect.XSize, H = rect.YSize, Bitdepth = fullImage.Bitdepth };
        int c = fullImage.NbMetaChannels;
        for (; c < fullImage.Channel.Count; c++)
        {
            LjChannel fc = fullImage.Channel[c];
            if (fc.W > Fd.GroupDim || fc.H > Fd.GroupDim)
            {
                break;
            }
        }

        int beginc = c;
        var targets = new List<(LjChannel Fc, LjRect R)>();
        for (; c < fullImage.Channel.Count; c++)
        {
            LjChannel fc = fullImage.Channel[c];
            int shift = Math.Min(fc.HShift, fc.VShift);
            if (shift > maxShift || shift < minShift)
            {
                continue;
            }

            var r = new LjRect(rect.X0 >> fc.HShift, rect.Y0 >> fc.VShift, rect.XSize >> fc.HShift, rect.YSize >> fc.VShift, fc.W, fc.H);
            if (r.XSize == 0 || r.YSize == 0)
            {
                continue;
            }

            gi.Channel.Add(new LjChannel(r.XSize, r.YSize, fc.HShift, fc.VShift));
            targets.Add((fc, r));
        }

        if (gi.Channel.Count == 0)
        {
            return;
        }

        if (!zerofill)
        {
            LjDecModular.GenericDecompress(br, gi, null, streamId, 0xFFFFFF, 256, true, tree);
        }

        for (int i = 0; i < targets.Count; i++)
        {
            (LjChannel fc, LjRect r) = targets[i];
            LjChannel src = gi.Channel[i];
            for (int y = 0; y < r.YSize; y++)
            {
                Array.Copy(src.Plane, y * src.Stride, fc.Plane, ((r.Y0 + y) * fc.Stride) + r.X0, r.XSize);
            }
        }
    }

    private void DecodeVarDctDc(int groupId, LjBitReader br)
    {
        LjRect r = Fd.DcGroupRect(groupId);
        LjModularImage image = LjModularImage.Create(r.XSize, r.YSize, fullImage.Bitdepth, 3);
        int streamId = 1 + groupId;
        int extraPrecision = (int)br.ReadBits(2);
        float mul = 1.0f / (1 << extraPrecision);
        for (int c = 0; c < 3; c++)
        {
            int ci = c < 2 ? c ^ 1 : c;
            LjChannel ch = image.Channel[ci];
            image.Channel[ci] = new LjChannel(ch.W >> Fh.ChromaSubsampling.HShift(c), ch.H >> Fh.ChromaSubsampling.VShift(c));
        }

        LjDecModular.GenericDecompress(br, image, null, streamId, 0xFFFFFF, 256, true, tree);
        LjDecVarDct.DequantDc(r, dc, quantDc, image, quantizer.MulDc, mul, cmap.DcFactors, Fh.ChromaSubsampling, blockCtxMap);
    }

    private void DecodeAcMetadata(int groupId, LjBitReader br)
    {
        LjRect r = Fd.DcGroupRect(groupId);
        int upperBound = r.XSize * r.YSize;
        int count = (int)br.ReadBits(LjDec.CeilLog2Nonzero(upperBound)) + 1;
        int streamId = 1 + (2 * Fd.NumDcGroups) + groupId;
        var cr = new LjRect(r.X0 >> 3, r.Y0 >> 3, (r.XSize + 7) >> 3, (r.YSize + 7) >> 3);
        var image = new LjModularImage { W = r.XSize, H = r.YSize, Bitdepth = fullImage.Bitdepth };
        image.Channel.Add(new LjChannel(cr.XSize, cr.YSize, 3, 3));
        image.Channel.Add(new LjChannel(cr.XSize, cr.YSize, 3, 3));
        image.Channel.Add(new LjChannel(count, 2, 0, 0));
        image.Channel.Add(new LjChannel(r.XSize, r.YSize));
        LjDecModular.GenericDecompress(br, image, null, streamId, 0xFFFFFF, 256, true, tree);
        for (int y = 0; y < cr.YSize; y++)
        {
            for (int x = 0; x < cr.XSize; x++)
            {
                cmap.YtoxMap.Data[cmap.YtoxMap.Row(cr.Y0 + y) + cr.X0 + x] = (sbyte)Math.Clamp(image.Channel[0][x, y], -128, 127);
                cmap.YtobMap.Data[cmap.YtobMap.Row(cr.Y0 + y) + cr.X0 + x] = (sbyte)Math.Clamp(image.Channel[1][x, y], -128, 127);
            }
        }

        int num = 0;
        bool is444 = Fh.ChromaSubsampling.Is444;
        int xlim = Math.Min(acStrategy.XSize, r.X0 + r.XSize);
        int ylim = Math.Min(acStrategy.YSize, r.Y0 + r.YSize);
        uint localUsedAcs = 0;
        LjChannel ch2 = image.Channel[2], ch3 = image.Channel[3];
        for (int iy = 0; iy < r.YSize; iy++)
        {
            int y = r.Y0 + iy;
            for (int ix = 0; ix < r.XSize; ix++)
            {
                int x = r.X0 + ix;
                int sharpness = ch3[ix, iy];
                if (sharpness < 0 || sharpness >= 8)
                {
                    throw LjDec.Fail("Corrupted sharpness field");
                }

                epfSharpness.Data[epfSharpness.Row(y) + x] = (byte)sharpness;
                if (acStrategy.Data[acStrategy.Row(y) + x] != 0xFF)
                {
                    continue;
                }

                if (num >= count)
                {
                    throw LjDec.Fail("Corrupted stream");
                }

                int raw = ch2[num, 0];
                if (raw < 0 || raw >= LjDecTransforms.NumValidStrategies)
                {
                    throw LjDec.Fail("Invalid AC strategy");
                }

                localUsedAcs |= 1u << raw;
                int cbx = LjDecTransforms.CoveredBlocksX[raw], cby = LjDecTransforms.CoveredBlocksY[raw];
                if ((cbx > 1 || cby > 1) && !is444)
                {
                    throw LjDec.Fail("AC strategy not compatible with chroma subsampling");
                }

                int nextXAcBlock = ((x / 32) + 1) * 32;
                int nextYAcBlock = ((y / 32) + 1) * 32;
                if (x + cbx > nextXAcBlock || x + cbx > xlim || y + cby > nextYAcBlock || y + cby > ylim)
                {
                    throw LjDec.Fail("Invalid AC strategy, overflow");
                }

                for (int by = 0; by < cby; by++)
                {
                    for (int bx = 0; bx < cbx; bx++)
                    {
                        int p = acStrategy.Row(y + by) + x + bx;
                        if (acStrategy.Data[p] != 0xFF)
                        {
                            throw LjDec.Fail("Invalid AC strategy: block overlap");
                        }

                        acStrategy.Data[p] = (byte)((raw << 1) | ((by | bx) == 0 ? 1 : 0));
                    }
                }

                rawQuantField.Data[rawQuantField.Row(y) + x] = 1 + Math.Max(0, Math.Min(LjQuantizer.QuantMax - 1, ch2[num, 1]));
                num++;
            }
        }

        System.Threading.Interlocked.Or(ref usedAcs, localUsedAcs);
        if (Fh.LoopFilter.EpfIters > 0)
        {
            ComputeSigma(r);
        }
    }

    /// <summary>ComputeSigma (epf.cc).</summary>
    private void ComputeSigma(LjRect blockRect)
    {
        const int sigmaPadding = 2;
        LjLoopFilter lf = Fh.LoopFilter;
        float quantScale = quantizer.Scale;
        LjImageF s = sigma!;
        int ss = s.Stride;
        for (int by = 0; by < blockRect.YSize; ++by)
        {
            int sigmaRow = s.Row(blockRect.Y0 + by) + blockRect.X0;
            int sharpRow = epfSharpness.Row(blockRect.Y0 + by) + blockRect.X0;
            int acsRow = acStrategy.Row(blockRect.Y0 + by) + blockRect.X0;
            int qRow = rawQuantField.Row(blockRect.Y0 + by) + blockRect.X0;
            for (int bx = 0; bx < blockRect.XSize; bx++)
            {
                byte acs = acStrategy.Data[acsRow + bx];
                int raw = acs >> 1;
                if ((acs & 1) == 0)
                {
                    continue;
                }

                int llfX = LjDecTransforms.CoveredBlocksX[raw], llfY = LjDecTransforms.CoveredBlocksY[raw];
                float sigmaQuant = lf.EpfQuantMul / (quantScale * rawQuantField.Data[qRow + bx] * -1.1715728752538099024f);
                for (int iy = 0; iy < llfY; iy++)
                {
                    for (int ix = 0; ix < llfX; ix++)
                    {
                        float sg = sigmaQuant * lf.EpfSharpLut[epfSharpness.Data[sharpRow + bx + ix + (iy * epfSharpness.Stride)]];
                        sg = Math.Min(-1e-4f, sg);
                        s.Data[sigmaRow + bx + ix + sigmaPadding + ((iy + sigmaPadding) * ss)] = 1.0f / sg;
                    }
                }

                if (bx + blockRect.X0 == 0)
                {
                    for (int iy = 0; iy < llfY; iy++)
                    {
                        int p = sigmaRow + sigmaPadding + ((iy + sigmaPadding) * ss);
                        s.Data[p - 1] = s.Data[p];
                    }
                }

                if (bx + blockRect.X0 + llfX == Fd.XsizeBlocks)
                {
                    for (int iy = 0; iy < llfY; iy++)
                    {
                        int p = sigmaRow + sigmaPadding + bx + llfX + ((iy + sigmaPadding) * ss);
                        s.Data[p] = s.Data[p - 1];
                    }
                }

                int offsetBefore = bx + blockRect.X0 == 0 ? 1 : bx + sigmaPadding;
                int offsetAfter = bx + blockRect.X0 + llfX == Fd.XsizeBlocks ? sigmaPadding + llfX + bx + 1 : sigmaPadding + llfX + bx;
                int num = offsetAfter - offsetBefore;
                if (by + blockRect.Y0 == 0)
                {
                    Array.Copy(s.Data, sigmaRow + offsetBefore + (sigmaPadding * ss), s.Data, sigmaRow + offsetBefore + ((sigmaPadding - 1) * ss), num);
                }

                if (by + blockRect.Y0 + llfY == Fd.YsizeBlocks)
                {
                    Array.Copy(s.Data, sigmaRow + offsetBefore + (ss * (llfY + sigmaPadding - 1)), s.Data, sigmaRow + offsetBefore + (ss * (llfY + sigmaPadding)), num);
                }
            }
        }
    }

    private int[] DecodeQuantTableModular(int w, int h, int idx, LjBitReader br)
    {
        LjModularImage image = LjModularImage.Create(w, h, 8, 3);
        int streamId = 1 + (3 * Fd.NumDcGroups) + idx;
        LjDecModular.GenericDecompress(br, image, null, streamId, 0xFFFFFF, 256, true, tree);
        var q = new int[w * h * 3];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    q[(c * w * h) + (y * w) + x] = image.Channel[c][x, y];
                }
            }
        }

        return q;
    }

    /// <summary>FinalizeDecoding: undo the global transforms and write every group into the pipeline.</summary>
    private void FinalizeModular()
    {
        if (fullImage.Channel.Count == 0)
        {
            return;
        }

        LjDecModular.UndoTransforms(fullImage, globalHeader.WpHeader);
        for (int g = 0; g < Fd.NumGroups; g++)
        {
            ModularImageToDecodedRect(fullImage, g, Fd.GroupRect(g));
        }
    }

    private void ModularImageToDecodedRect(LjModularImage gi, int group, LjRect modularRect)
    {
        LjImageMetadata m = st.Meta;
        int c = 0;
        if (doColor)
        {
            bool rgbFromGray = m.ColorEncoding.ColorSpace == LjColorEncoding.CsGray && Fh.ColorTransform == LjFrameHeader.CtNone;
            bool fp = m.BitDepth.FloatingPointSample && Fh.ColorTransform != LjFrameHeader.CtXyb;
            for (; c < 3; c++)
            {
                double factor = gi.Bitdepth < 32 ? 1.0 / ((1u << gi.Bitdepth) - 1) : 0;
                int cIn = c;
                if (Fh.ColorTransform == LjFrameHeader.CtXyb)
                {
                    factor = matrices.DcQuant[c];
                    if (c < 2)
                    {
                        cIn = 1 - c;
                    }
                }
                else if (rgbFromGray)
                {
                    cIn = 0;
                }

                LjChannel chIn = gi.Channel[cIn];
                (LjImageF buf, LjRect r) = pipeline.GetInputBuffer(group, c);
                var mr = new LjRect(modularRect.X0 >> chIn.HShift, modularRect.Y0 >> chIn.VShift,
                    LjDec.DivCeil(modularRect.XSize, 1 << chIn.HShift), LjDec.DivCeil(modularRect.YSize, 1 << chIn.VShift)).Crop(chIn.W, chIn.H);
                if (r.YSize != mr.YSize || r.XSize != mr.XSize)
                {
                    throw LjDec.Fail("Dimension mismatch fitting a modular channel into the frame");
                }

                float ffactor = (float)factor;
                for (int y = 0; y < r.YSize; y++)
                {
                    int rowIn = ((mr.Y0 + y) * chIn.Stride) + mr.X0;
                    if (Fh.ColorTransform == LjFrameHeader.CtXyb && c == 2)
                    {
                        LjChannel chY = gi.Channel[0];
                        int rowY = ((mr.Y0 + y) * chY.Stride) + mr.X0;
                        int o = buf.Row(r.Y0 + y) + r.X0;
                        for (int x = 0; x < r.XSize; x++)
                        {
                            buf.Data[o + x] = (float)(chIn.Plane[rowIn + x] + chY.Plane[rowY + x]) * ffactor;
                        }
                    }
                    else if (fp)
                    {
                        int bits = (int)m.BitDepth.BitsPerSample, expBits = (int)m.BitDepth.ExponentBitsPerSample;
                        for (int cc = rgbFromGray ? 0 : c; cc < (rgbFromGray ? 3 : c + 1); cc++)
                        {
                            (LjImageF b2, LjRect r2) = pipeline.GetInputBuffer(group, cc);
                            IntToFloat(chIn.Plane, rowIn, b2.Data, b2.Row(r2.Y0 + y) + r2.X0, r.XSize, bits, expBits);
                        }
                    }
                    else
                    {
                        for (int cc = rgbFromGray ? 0 : c; cc < (rgbFromGray ? 3 : c + 1); cc++)
                        {
                            (LjImageF b2, LjRect r2) = pipeline.GetInputBuffer(group, cc);
                            int o = b2.Row(r2.Y0 + y) + r2.X0;
                            if (gi.Bitdepth < 23)
                            {
                                for (int x = 0; x < r.XSize; x++)
                                {
                                    b2.Data[o + x] = chIn.Plane[rowIn + x] * ffactor;
                                }
                            }
                            else
                            {
                                for (int x = 0; x < r.XSize; x++)
                                {
                                    b2.Data[o + x] = (float)(chIn.Plane[rowIn + x] * factor);
                                }
                            }
                        }
                    }
                }

                if (rgbFromGray)
                {
                    break;
                }
            }

            if (rgbFromGray)
            {
                c = 1;
            }
        }

        int numExtra = m.ExtraChannelInfo.Count;
        for (int ec = 0; ec < numExtra; ec++, c++)
        {
            LjExtraChannelInfo eci = m.ExtraChannelInfo[ec];
            int bits = (int)eci.BitDepth.BitsPerSample, expBits = (int)eci.BitDepth.ExponentBitsPerSample;
            bool fp = eci.BitDepth.FloatingPointSample;
            double factor = fp ? 0 : 1.0 / ((1u << bits) - 1);
            LjChannel chIn = gi.Channel[c];
            (LjImageF buf, LjRect r) = pipeline.GetInputBuffer(group, 3 + ec);
            var mr = new LjRect(modularRect.X0 >> chIn.HShift, modularRect.Y0 >> chIn.VShift,
                LjDec.DivCeil(modularRect.XSize, 1 << chIn.HShift), LjDec.DivCeil(modularRect.YSize, 1 << chIn.VShift)).Crop(chIn.W, chIn.H);
            if (r.YSize != mr.YSize || r.XSize != mr.XSize)
            {
                throw LjDec.Fail("Dimension mismatch fitting a modular extra channel into the frame");
            }

            float ffactor = (float)factor;
            for (int y = 0; y < r.YSize; ++y)
            {
                int rowIn = ((mr.Y0 + y) * chIn.Stride) + mr.X0;
                int o = buf.Row(r.Y0 + y) + r.X0;
                if (fp)
                {
                    IntToFloat(chIn.Plane, rowIn, buf.Data, o, r.XSize, bits, expBits);
                }
                else if (gi.Bitdepth < 23)
                {
                    for (int x = 0; x < r.XSize; x++)
                    {
                        buf.Data[o + x] = chIn.Plane[rowIn + x] * ffactor;
                    }
                }
                else
                {
                    for (int x = 0; x < r.XSize; x++)
                    {
                        buf.Data[o + x] = (float)(chIn.Plane[rowIn + x] * factor);
                    }
                }
            }
        }
    }

    /// <summary>int_to_float.</summary>
    private static void IntToFloat(int[] rowIn, int ri, float[] rowOut, int ro, int xsize, int bits, int expBits)
    {
        if (bits == 32)
        {
            for (int x = 0; x < xsize; x++)
            {
                rowOut[ro + x] = BitConverter.Int32BitsToSingle(rowIn[ri + x]);
            }

            return;
        }

        int expBias = (1 << (expBits - 1)) - 1;
        int signShift = bits - 1;
        int mantBits = bits - expBits - 1;
        int mantShift = 23 - mantBits;
        for (int x = 0; x < xsize; ++x)
        {
            uint f = (uint)rowIn[ri + x];
            int signbit = (int)(f >> signShift);
            f &= (1u << signShift) - 1;
            if (f == 0)
            {
                rowOut[ro + x] = signbit != 0 ? -0.0f : 0.0f;
                continue;
            }

            int exp = (int)(f >> mantBits);
            int mantissa = (int)(f & ((1u << mantBits) - 1));
            if (exp == (1 << expBits) - 1)
            {
                f = signbit != 0 ? 0x80000000u : 0;
                f |= 0b11111111u << 23;
                f |= (uint)mantissa << mantShift;
                rowOut[ro + x] = BitConverter.UInt32BitsToSingle(f);
                continue;
            }

            mantissa <<= mantShift;
            if (exp == 0 && expBits < 8)
            {
                while ((mantissa & 0x800000) == 0)
                {
                    mantissa <<= 1;
                    exp--;
                }

                exp++;
                mantissa &= 0x7fffff;
            }

            exp -= expBias;
            exp += 127;
            if (exp < 0)
            {
                throw LjDec.Fail("float exponent underflow");
            }

            f = signbit != 0 ? 0x80000000u : 0;
            f |= (uint)exp << 23;
            f |= (uint)mantissa;
            rowOut[ro + x] = BitConverter.UInt32BitsToSingle(f);
        }
    }

    // ================================================================== VarDCT AC groups

    private void DecodeVarDctGroup(int groupIdx, LjBitReader[] readers)
    {
        LjRect blockRect = Fd.BlockGroupRect(groupIdx);
        int numPasses = readers.Length;
        int histoSelectorBits = LjDec.CeilLog2Nonzero(numHistograms);
        var decoders = new LjAnsSymbolReader[numPasses];
        var ctxOffset = new int[numPasses];
        for (int pass = 0; pass < numPasses; pass++)
        {
            int curHistogram = histoSelectorBits != 0 ? (int)readers[pass].ReadBits(histoSelectorBits) : 0;
            if (curHistogram >= numHistograms)
            {
                throw LjDec.Fail("Invalid histogram selector");
            }

            ctxOffset[pass] = curHistogram * blockCtxMap.NumAcContexts;
            decoders[pass] = new LjAnsSymbolReader(acCode[pass], readers[pass]);
        }

        LjChromaSubsampling cs = Fh.ChromaSubsampling;
        int[] hshift = [cs.HShift(0), cs.HShift(1), cs.HShift(2)];
        int[] vshift = [cs.VShift(0), cs.VShift(1), cs.VShift(2)];
        var r = new LjRect[3];
        for (int i = 0; i < 3; i++)
        {
            r[i] = new LjRect(blockRect.X0 >> hshift[i], blockRect.Y0 >> vshift[i], blockRect.XSize >> hshift[i], blockRect.YSize >> vshift[i]);
        }

        // num_nzeroes per pass and channel, 32x32 blocks.
        var nzeros = new int[numPasses][][];
        for (int p = 0; p < numPasses; p++)
        {
            nzeros[p] = [new int[32 * 32], new int[32 * 32], new int[32 * 32]];
        }

        const int nzStride = 32;
        float invGlobalScale = quantizer.InvGlobalScale;
        float[] biases = st.Output.OpsinParams.QuantBiases;
        int[] qblock = qblockBuf ??= new int[3 * 65536];
        float[] block = blockBuf ??= new float[3 * 65536];
        float[] scratch = scratchBuf ??= new float[65536 + 4096];
        var pixBuf = new (LjImageF Img, LjRect R)[3];
        for (int c = 0; c < 3; c++)
        {
            if (JpegData == null)
            {
                pixBuf[c] = pipeline.GetInputBuffer(groupIdx, c);
            }
        }

        for (int by = 0; by < blockRect.YSize; ++by)
        {
            int[] sby = [by >> vshift[0], by >> vshift[1], by >> vshift[2]];
            int qfRow = rawQuantField.Row(blockRect.Y0 + by) + blockRect.X0;
            int qdcRow = quantDc.Row(blockRect.Y0 + by) + blockRect.X0;
            int acsRow = acStrategy.Row(blockRect.Y0 + by) + blockRect.X0;
            int ty = (blockRect.Y0 + by) / 8;
            int bx = 0;
            for (int tx = 0; tx < LjDec.DivCeil(blockRect.XSize, 8); tx++)
            {
                int absTx = tx + (blockRect.X0 / 8);
                sbyte ytox = cmap.YtoxMap.Data[cmap.YtoxMap.Row(ty) + absTx];
                sbyte ytob = cmap.YtobMap.Data[cmap.YtobMap.Row(ty) + absTx];

                // YtoXRatio / YtoBRatio inlined into AVX2 code: clang contracts base + factor * scale into an FMA.
                float xCcMul = MathF.FusedMultiplyAdd(ytox, cmap.ColorScale, cmap.BaseCorrelationX);
                float bCcMul = MathF.FusedMultiplyAdd(ytob, cmap.ColorScale, cmap.BaseCorrelationB);
                for (; bx < blockRect.XSize && bx < (tx + 1) * 8;)
                {
                    int[] sbx = [bx >> hshift[0], bx >> hshift[1], bx >> hshift[2]];
                    byte acsByte = acStrategy.Data[acsRow + bx];
                    int raw = acsByte >> 1;
                    int llfX = LjDecTransforms.CoveredBlocksX[raw];
                    if ((acsByte & 1) == 0)
                    {
                        bx += llfX;
                        continue;
                    }

                    int log2Covered = LjDecTransforms.Log2CoveredBlocks[raw];
                    int covered = 1 << log2Covered;
                    int size = covered * 64;
                    Array.Clear(qblock, 0, size * 3);

                    // GetBlockFromBitstream::LoadBlock.
                    foreach (int c in new[] { 1, 0, 2 })
                    {
                        int sbxc = bx >> hshift[c];
                        int sbyc = by >> vshift[c];
                        if ((sbxc << hshift[c]) != bx || (sbyc << vshift[c]) != by)
                        {
                            continue;
                        }

                        for (int pass = 0; pass < numPasses; pass++)
                        {
                            DecodeAcVarBlock(ctxOffset[pass], log2Covered, nzeros[pass][c], sbyc * nzStride, sbyc == 0 ? -1 : (sbyc - 1) * nzStride,
                                nzStride, c, sbxc, raw, coeffOrders, pass * coeffOrderSize, readers[pass], decoders[pass], acContextMap[pass],
                                quantDc.Data[qdcRow + bx], (uint)rawQuantField.Data[qfRow + bx], qblock, c * size, (int)Fh.Passes.Shift[pass]);
                        }
                    }

                    if (JpegData != null)
                    {
                        WriteJpegBlock(raw, bx, by, sbx, sby, hshift, vshift, r, ytox, ytob, qblock, size);
                    }
                    else
                    {
                        // DequantBlock.
                        int quant = rawQuantField.Data[qfRow + bx];
                        float scaledDequantS = invGlobalScale / quant;
                        float sdx = scaledDequantS * xDmMultiplier, sdy = scaledDequantS, sdb = scaledDequantS * bDmMultiplier;
                        int mOff = matrices.MatrixOffset(raw, 0);
                        float[] table = matrices.Table;
                        int cov = LjDecTransforms.CoveredBlocksX[raw] * LjDecTransforms.CoveredBlocksY[raw];
                        if (LjSimd.Enabled)
                        {
                            LjSimd.DequantBlock(table, mOff, size, cov * 64, sdx, sdy, sdb, qblock, block, biases, xCcMul, bCcMul);
                        }
                        else
                        for (int k = 0; k < cov * 64; k++)
                        {
                            float xMul = table[mOff + k] * sdx;
                            float yMul = table[mOff + size + k] * sdy;
                            float bMul = table[mOff + (2 * size) + k] * sdb;
                            float dqXcc = LjDecVarDct.AdjustQuantBias(0, qblock[k], biases) * xMul;
                            float dqY = LjDecVarDct.AdjustQuantBias(1, qblock[size + k], biases) * yMul;
                            float dqBcc = LjDecVarDct.AdjustQuantBias(2, qblock[(2 * size) + k], biases) * bMul;
                            block[k] = MathF.FusedMultiplyAdd(xCcMul, dqY, dqXcc);
                            block[size + k] = dqY;
                            block[(2 * size) + k] = MathF.FusedMultiplyAdd(bCcMul, dqY, dqBcc);
                        }

                        for (int c = 0; c < 3; c++)
                        {
                            LjImageF dcp = dc.Planes[c];
                            int dcOff = dcp.Row(r[c].Y0 + sby[c]) + r[c].X0 + sbx[c];
                            LjDecTransforms.LowestFrequenciesFromDc(raw, dcp.Data, dcOff, dcp.Stride, block, c * size, scratch);
                        }

                        foreach (int c in new[] { 1, 0, 2 })
                        {
                            if ((sbx[c] << hshift[c]) != bx || (sby[c] << vshift[c]) != by)
                            {
                                continue;
                            }

                            (LjImageF img, LjRect pr) = pixBuf[c];
                            int po = img.Row(pr.Y0 + (sby[c] * 8)) + pr.X0 + (sbx[c] * 8);
                            LjDecTransforms.TransformToPixels(raw, block, c * size, img.Data, po, img.Stride, scratch);
                        }
                    }

                    bx += llfX;
                }
            }
        }

        for (int pass = 0; pass < numPasses; pass++)
        {
            if (!decoders[pass].CheckAnsFinalState())
            {
                throw LjDec.Fail("ANS checksum failure.");
            }
        }
    }

    [ThreadStatic] private static int[]? qblockBuf;
    [ThreadStatic] private static float[]? blockBuf;
    [ThreadStatic] private static float[]? scratchBuf;

    /// <summary>DecodeACVarBlock.</summary>
    private void DecodeAcVarBlock(int ctxOffsetP, int log2CoveredBlocks, int[] rowNzeros, int rowOff, int topOff, int nzerosStride, int c, int bx,
        int raw, uint[] coeffOrder, int coeffOrderBase, LjBitReader br, LjAnsSymbolReader decoder, byte[] contextMap, byte qdc, uint qf,
        int[] block, int blockOff, int shift)
    {
        int coveredBlocks = 1 << log2CoveredBlocks;
        int size = coveredBlocks * 64;
        int predictedNzeros = LjDecVarDct.PredictFromTopAndLeft(topOff < 0 ? null : rowNzeros, topOff, rowNzeros, rowOff, bx, 32);
        int ord = LjDecTransforms.StrategyOrder[raw];
        int orderOff = coeffOrderBase + LjDecTransforms.CoeffOrderOffsetOf(ord, c);
        int blockCtx = blockCtxMap.Context(qdc, qf, ord, c);
        int nzeroCtx = blockCtxMap.NonZeroContext(predictedNzeros, blockCtx) + ctxOffsetP;
        int nzeros = (int)decoder.ReadHybridUint(nzeroCtx, br, contextMap);
        if (nzeros > size - coveredBlocks)
        {
            throw LjDec.Fail("Invalid AC: nzeros too large");
        }

        int cbx = LjDecTransforms.CoveredBlocksX[raw], cby = LjDecTransforms.CoveredBlocksY[raw];
        for (int y = 0; y < cby; y++)
        {
            for (int x = 0; x < cbx; x++)
            {
                rowNzeros[rowOff + bx + x + (y * nzerosStride)] = (nzeros + coveredBlocks - 1) >> log2CoveredBlocks;
            }
        }

        int histoOffset = ctxOffsetP + blockCtxMap.ZeroDensityContextsOffset(blockCtx);
        int prev = nzeros > size / 16 ? 0 : 1;
        for (int k = coveredBlocks; k < size && nzeros != 0; ++k)
        {
            int ctx = histoOffset + LjBlockCtxMap.ZeroDensityContext(nzeros, k, coveredBlocks, log2CoveredBlocks, prev);
            uint uCoeff = decoder.ReadHybridUint(ctx, br, contextMap);
            uint magnitude = uCoeff >> 1;
            uint negSign = ~uCoeff & 1;
            int coeff = (int)((magnitude ^ (negSign - 1)) << shift);
            block[blockOff + (int)coeffOrder[orderOff + k]] += coeff;
            prev = uCoeff != 0 ? 1 : 0;
            nzeros -= prev;
        }

        if (nzeros != 0)
        {
            throw LjDec.Fail("Invalid AC: nzeros at end of block is not 0");
        }
    }

    // ================================================================== pipeline

    private bool NeedsBlending()
    {
        if (!(Fh.FrameType == LjFrameHeader.RegularFrame || Fh.FrameType == LjFrameHeader.SkipProgressive))
        {
            return false;
        }

        bool replaceAll = Fh.BlendingInfo.Mode == LjBlendingInfo.Replace;
        foreach (LjBlendingInfo ec in Fh.ExtraChannelBlendingInfo)
        {
            if (ec.Mode != LjBlendingInfo.Replace)
            {
                replaceAll = false;
            }
        }

        return Fh.CustomSizeOrOrigin || !replaceAll;
    }

    /// <summary>PassesDecoderState::PreparePipeline.</summary>
    private void PreparePipeline()
    {
        if (JpegData != null)
        {
            return;
        }

        LjImageMetadata m = st.Meta;
        LjOutputEncoding oe = st.Output;
        int numC = 3 + m.ExtraChannelInfo.Count;
        bool renderNoise = noise != null;
        int numTmpC = renderNoise ? 3 : 0;
        pipeline = new LjRenderPipeline(numC + numTmpC);
        refStage = null;
        dcFrameStage = null;
        LjChromaSubsampling cs = Fh.ChromaSubsampling;
        if (!cs.Is444)
        {
            for (int c = 0; c < 3; c++)
            {
                if (cs.HShift(c) != 0)
                {
                    pipeline.AddStage(new LjChromaUpsamplingStage(c, true));
                }

                if (cs.VShift(c) != 0)
                {
                    pipeline.AddStage(new LjChromaUpsamplingStage(c, false));
                }
            }
        }

        LjLoopFilter lf = Fh.LoopFilter;
        if (lf.Gab)
        {
            pipeline.AddStage(new LjGaborishStage(lf));
        }

        if (lf.EpfIters >= 3)
        {
            pipeline.AddStage(new LjEpfStage(0, lf, sigma!));
        }

        if (lf.EpfIters >= 1)
        {
            pipeline.AddStage(new LjEpfStage(1, lf, sigma!));
        }

        if (lf.EpfIters >= 2)
        {
            pipeline.AddStage(new LjEpfStage(2, lf, sigma!));
        }

        bool lateEcUpsample = Fh.Upsampling != 1;
        foreach (uint ecups in Fh.ExtraChannelUpsampling)
        {
            if (ecups != Fh.Upsampling)
            {
                lateEcUpsample = false;
            }
        }

        if (!lateEcUpsample)
        {
            for (int ec = 0; ec < Fh.ExtraChannelUpsampling.Length; ec++)
            {
                if (Fh.ExtraChannelUpsampling[ec] != 1)
                {
                    pipeline.AddStage(new LjUpsamplingStage(st.Transform, 3 + ec, LjDec.CeilLog2Nonzero(Fh.ExtraChannelUpsampling[ec])));
                }
            }
        }

        if (patches != null)
        {
            pipeline.AddStage(new LjPatchesStage(patches, m.ExtraChannelInfo, numC));
        }

        if (splines != null)
        {
            pipeline.AddStage(new LjSplineStage(splines));
        }

        if (Fh.Upsampling != 1)
        {
            int nb = 3 + (lateEcUpsample ? Fh.ExtraChannelUpsampling.Length : 0);
            for (int c = 0; c < nb; c++)
            {
                pipeline.AddStage(new LjUpsamplingStage(st.Transform, c, LjDec.CeilLog2Nonzero(Fh.Upsampling)));
            }
        }

        if (renderNoise)
        {
            pipeline.AddStage(new LjConvolveNoiseStage(numC));
            pipeline.AddStage(new LjAddNoiseStage(noise!, cmap, numC));
        }

        if (Fh.DcLevel != 0)
        {
            dcFrameStage = new LjWriteStage(true);
            pipeline.AddStage(dcFrameStage);
        }

        if (Fh.CanBeReferenced && Fh.SaveBeforeColorTransform)
        {
            refStage = new LjWriteStage(false);
            pipeline.AddStage(refStage);
        }

        bool linear = false;
        if (Fh.ColorTransform == LjFrameHeader.CtYCbCr)
        {
            pipeline.AddStage(new LjYCbCrStage());
        }
        else if (Fh.ColorTransform == LjFrameHeader.CtXyb)
        {
            pipeline.AddStage(new LjXybStage(oe.OpsinParams, oe.ColorEncoding.ColorSpace == LjColorEncoding.CsXyb));
            if (oe.ColorEncoding.ColorSpace != LjColorEncoding.CsXyb)
            {
                linear = true;
            }
        }

        if (st.Coalescing && NeedsBlending())
        {
            if (linear)
            {
                pipeline.AddStage(FromLinearStage(oe));
                linear = false;
            }

            pipeline.AddStage(new LjBlendingStage(Fh, st, oe));
        }

        if (st.Coalescing && Fh.CanBeReferenced && !Fh.SaveBeforeColorTransform)
        {
            if (linear)
            {
                pipeline.AddStage(FromLinearStage(oe));
                linear = false;
            }

            refStage = new LjWriteStage(false);
            pipeline.AddStage(refStage);
        }

        if (st.RenderSpotColors)
        {
            for (int i = 0; i < m.ExtraChannelInfo.Count; i++)
            {
                if (m.ExtraChannelInfo[i].Type == LjExtraChannelInfo.TypeSpotColor)
                {
                    pipeline.AddStage(new LjSpotColorStage(3 + i, m.ExtraChannelInfo[i].SpotColor));
                }
            }
        }

        if (linear)
        {
            int channelsSrc = oe.OrigColorEncoding.Cmyk ? 4 : LjCe.IsGray(oe.OrigColorEncoding) ? 1 : 3;
            int channelsDst = LjCe.IsGray(oe.ColorEncoding) ? 1 : 3;
            if (oe.ColorEncodingIsOriginal || !oe.CmsSet || channelsDst != channelsSrc)
            {
                pipeline.AddStage(FromLinearStage(oe));
            }
            else
            {
                LjCmsStage? cms = LjCmsStage.Create(oe, true);
                if (cms != null)
                {
                    pipeline.AddStage(cms);
                }
            }
        }
        else
        {
            LjCmsStage? cms = LjCmsStage.Create(oe, false);
            if (cms != null)
            {
                pipeline.AddStage(cms);
            }
        }

        outputStage = new LjWriteStage(false);
        pipeline.AddStage(outputStage);
        pipeline.Finalize(Fd);
    }

    private static LjFromLinearStage FromLinearStage(LjOutputEncoding oe)
    {
        LjColorEncoding ce = oe.ColorEncoding;
        if (LjCe.TfIsLinear(ce))
        {
            return new LjFromLinearStage(null);
        }

        if (LjCe.TfIsSrgb(ce))
        {
            return new LjFromLinearStage(LjTf.SrgbFromLinear, null, simdKind: 1);
        }

        if (LjCe.TfIsPq(ce))
        {
            return new LjFromLinearStage(LjTf.Pq(oe.OrigIntensityTarget));
        }

        if (LjCe.TfIsHlg(ce))
        {
            return new LjFromLinearStage(LjTf.HlgFromLinear, LjHlgOotf.ToSceneLight(oe.DesiredIntensityTarget, oe.Luminances));
        }

        if (LjCe.TfIs709(ce))
        {
            return new LjFromLinearStage(LjTf.Rec709FromLinear, null, simdKind: 2);
        }

        if (ce.Tf.HaveGamma || LjCe.TfIsDci(ce))
        {
            return new LjFromLinearStage(LjTf.Gamma(oe.InverseGamma), null, simdKind: 3, inverseGamma: oe.InverseGamma);
        }

        throw LjDec.Fail("Invalid target encoding");
    }
}
