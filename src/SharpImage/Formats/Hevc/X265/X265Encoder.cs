// Port of the single-picture path of x265's encoder (encoder/api.cpp x265_encoder_open / encode, encoder.cpp
// configure / initVPS / initSPS / initPPS / getStreamHeaders, level.cpp, ratecontrol.cpp for the first CRF / CQP
// I frame, slicetype.cpp calcAdaptiveQuantFrame (variance AQ), frameencoder.cpp compressFrame / processRowEncoder /
// encodeSlice and framefilter.cpp): one IDR picture, all-intra, WPP substreams, deblocking and SAO. The frame is
// processed in the order x265's single-threaded wavefront produces (its output does not depend on threading).
using System;
using System.Collections.Generic;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;
#if HIGH_BIT_DEPTH
using pixel = ushort;
#else
using pixel = byte;
#endif

/// <summary>Settings of one x265 encode as libheif's x265 plugin makes them.</summary>
internal sealed class X265EncodeSettings
{
    /// <summary>libheif quality 0..100 (CRF = (100 - quality) / 2).</summary>
    public int Quality = 50;

    /// <summary>Constant QP instead of CRF (x265 --qp), for testing.</summary>
    public int? ConstantQp;

    /// <summary>CTU size (libheif halves 64 while the image is smaller than it, down to 16).</summary>
    public int CtuSize = 64;

    public bool Deblock = true;
    public bool Sao = true;
    public int AqMode = 1;
    public double PsyRd = 1.0;
    public double PsyRdoq = 1.0;
    public int RdoqLevel = 2;
    public bool Monochrome;
    public bool FullRange = true;
    public int ColorPrimaries = 1, TransferCharacteristics = 13, MatrixCoefficients = 6;
    public bool WriteColorDescription = true;
}

[System.Runtime.CompilerServices.SkipLocalsInit]
internal static unsafe class X265Encoder
{
    private const int NAL_UNIT_VPS = 32, NAL_UNIT_SPS = 33, NAL_UNIT_PPS = 34;

    /// <summary>Worker threads for the WPP rows (0: one per processor); the output does not depend on it.</summary>
    internal static int MaxWorkers;

    /// <summary>
    /// Encodes one 8-bit picture (planes already padded by the caller to the size it passes, as libheif pads to
    /// even dimensions of at least 64) and returns the NAL units (VPS, SPS, PPS, slice) without start codes.
    /// </summary>
    public static List<(int Type, byte[] Payload)> EncodePicture(ReadOnlySpan<pixel> y, ReadOnlySpan<pixel> u, ReadOnlySpan<pixel> v,
        int width, int height, int strideY, int strideC, X265EncodeSettings s)
    {
        using var arena = new X265Arena();
        var p = new X265Param
        {
            internalCsp = s.Monochrome ? X265_CSP_I400 : X265_CSP_I420,
            psyRd = s.PsyRd,
            psyRdoq = s.PsyRdoq,
            rdoqLevel = s.RdoqLevel,
            bEnableLoopFilter = s.Deblock,
            bEnableSAO = s.Sao,
            selectiveSAO = s.Sao ? 4 : 0,
            aqMode = s.AqMode,
            vuiFullRange = s.FullRange,
            vuiColorDescriptionPresent = s.WriteColorDescription,
            vuiColorPrimaries = s.ColorPrimaries,
            vuiTransferCharacteristics = s.TransferCharacteristics,
            vuiMatrixCoeffs = s.MatrixCoefficients,
        };

        // CTU size (x265_param_parse "ctu")
        p.maxCUSize = s.CtuSize;
        p.maxLog2CUSize = X265Tables.Log2Size[s.CtuSize];
        p.maxCUDepth = p.maxLog2CUSize - X265Tables.Log2Size[p.minCUSize];
        p.unitSizeDepth = p.maxLog2CUSize - LOG2_UNIT_SIZE;
        p.num4x4Partitions = 1 << (p.unitSizeDepth << 1);
        if (p.maxTUSize > p.maxCUSize)
        {
            p.maxTUSize = p.maxCUSize;
        }

        if (p.qgSize > p.maxCUSize)
        {
            p.qgSize = p.maxCUSize;
        }

        if (s.ConstantQp is int cqp)
        {
            p.rateControlMode = 1;   // X265_RC_CQP
            p.qp = cqp;
            p.aqMode = 0;            // x265 has no AQ with a constant QP
        }
        else
        {
            p.rfConstant = (100 - s.Quality) / 2.0;
        }

        // Encoder::configure: pad to the minimum CU size, the conformance window crops it
        var conf = new X265Window();
        p.sourceWidth = width;
        p.sourceHeight = height;
        if ((p.sourceWidth & (p.minCUSize - 1)) != 0)
        {
            int pad = p.minCUSize - (p.sourceWidth & (p.minCUSize - 1));
            p.sourceWidth += pad;
            conf.bEnabled = true;
            conf.rightOffset = pad;
        }

        if ((p.sourceHeight & (p.minCUSize - 1)) != 0)
        {
            int pad = p.minCUSize - (p.sourceHeight & (p.minCUSize - 1));
            p.sourceHeight += pad;
            conf.bEnabled = true;
            conf.bottomOffset = pad;
        }

        // Encoder::create: no WPP with a single CTU row or fewer than 3 columns (x265 also drops it without a
        // thread pool, i.e. on a single-core machine; libheif's output assumes a pool)
        int ctuRows = (p.sourceHeight + p.maxCUSize - 1) >> p.maxLog2CUSize;
        int ctuCols = (p.sourceWidth + p.maxCUSize - 1) >> p.maxLog2CUSize;
        if (ctuRows == 1 || ctuCols < 3)
        {
            p.bEnableWavefront = false;
        }

        // VPS / SPS / PPS
        var vps = new X265Vps();
        EnforceLevel(p, vps);
        DetermineLevel(p, vps);
        vps.ptl.progressiveSourceFlag = true;
        vps.ptl.interlacedSourceFlag = false;
        vps.ptl.nonPackedConstraintFlag = false;
        vps.ptl.frameOnlyConstraintFlag = true;

        var sps = new X265Sps
        {
            conformanceWindow = conf,
            chromaFormatIdc = p.internalCsp,
            picWidthInLumaSamples = (uint)p.sourceWidth,
            picHeightInLumaSamples = (uint)p.sourceHeight,
            numCuInWidth = (uint)((p.sourceWidth + p.maxCUSize - 1) / p.maxCUSize),
            numCuInHeight = (uint)((p.sourceHeight + p.maxCUSize - 1) / p.maxCUSize),
            numPartitions = (uint)p.num4x4Partitions,
            numPartInCUSize = 1u << p.unitSizeDepth,
            log2MinCodingBlockSize = p.maxLog2CUSize - p.maxCUDepth,
            log2DiffMaxMinCodingBlockSize = p.maxCUDepth,
            quadtreeTULog2MaxSize = (uint)Math.Min(p.maxLog2CUSize, X265Tables.Log2Size[p.maxTUSize]),
            quadtreeTULog2MinSize = 2,
            quadtreeTUMaxDepthInter = (uint)p.tuQTMaxInterDepth,
            quadtreeTUMaxDepthIntra = (uint)p.tuQTMaxIntraDepth,
            bUseSAO = p.bEnableSAO,
            bUseAMP = p.bEnableAMP,
            maxAMPDepth = p.bEnableAMP ? (uint)p.maxCUDepth : 0,
            maxTempSubLayers = vps.maxTempSubLayers,
            bUseStrongIntraSmoothing = p.bEnableStrongIntraSmoothing,
            bTemporalMVPEnabled = p.bEnableTemporalMvp,
            bEmitVUITimingInfo = p.bEmitVUITimingInfo,
            bEmitVUIHRDInfo = p.bEmitVUIHRDInfo,
            log2MaxPocLsb = p.log2MaxPocLsb,
            aspectRatioInfoPresentFlag = p.vuiAspectRatioIdc != 0,
            aspectRatioIdc = p.vuiAspectRatioIdc,
            sarWidth = p.vuiSarWidth,
            sarHeight = p.vuiSarHeight,
            videoSignalTypePresentFlag = p.vuiVideoSignalTypePresent,
            videoFormat = p.vuiVideoFormat,
            videoFullRangeFlag = p.vuiFullRange,
            colourDescriptionPresentFlag = p.vuiColorDescriptionPresent,
            colourPrimaries = p.vuiColorPrimaries,
            transferCharacteristics = p.vuiTransferCharacteristics,
            matrixCoefficients = p.vuiMatrixCoeffs,
            numUnitsInTick = (uint)p.fpsDenom,
            timeScale = (uint)p.fpsNum,
        };
        sps.numCUsInFrame = sps.numCuInWidth * sps.numCuInHeight;
        for (int i = 0; i < sps.maxTempSubLayers; i++)
        {
            sps.maxDecPicBuffering[i] = vps.maxDecPicBuffering[i];
            sps.numReorderPics[i] = (int)vps.numReorderPics[i];
            sps.maxLatencyIncrease[i] = vps.maxLatencyIncrease[i] = (uint)p.bframes;
        }

        int maxDeltaPOC = (p.bframes + 2) * 1 * 2;
        while ((1 << sps.log2MaxPocLsb) <= maxDeltaPOC * 2)
        {
            sps.log2MaxPocLsb++;
        }

        var pps = new X265Pps();
        if (!p.bLossless && p.aqMode != 0)
        {
            pps.bUseDQP = true;
            pps.maxCuDQPDepth = (uint)(X265Tables.Log2Size[p.maxCUSize] - X265Tables.Log2Size[p.qgSize]);
        }

        pps.chromaQpOffset[0] = p.cbQpOffset;
        pps.chromaQpOffset[1] = p.crQpOffset;
        pps.bTransformSkipEnabled = p.bEnableTransformSkip;
        pps.bSignHideEnabled = p.bEnableSignHiding;
        pps.bDeblockingFilterControlPresent = !p.bEnableLoopFilter || p.deblockingFilterBetaOffset != 0 || p.deblockingFilterTCOffset != 0;
        pps.bPicDisableDeblockingFilter = !p.bEnableLoopFilter;
        pps.deblockingFilterBetaOffsetDiv2 = p.deblockingFilterBetaOffset;
        pps.deblockingFilterTcOffsetDiv2 = p.deblockingFilterTCOffset;
        pps.bEntropyCodingSyncEnabled = p.bEnableWavefront;
        pps.numRefIdxDefault[0] = 1;
        pps.numRefIdxDefault[1] = 1;

        // the frame
        var slice = new X265Slice { m_sps = sps, m_pps = pps, m_param = p, m_bUseSao = p.selectiveSAO != 0 };
        slice.m_endCUAddr = sps.numCUsInFrame * (uint)p.num4x4Partitions;   // realEndAddress for a full picture
        var frame = new X265FrameData { m_param = p, m_slice = slice };
        frame.m_fencPic = new X265PicYuv(arena, p.sourceWidth, p.sourceHeight, p.internalCsp, p.maxCUSize);
        frame.m_fencPic.CreateOffsets(arena, (int)sps.numCuInWidth, (int)sps.numCuInHeight, p.maxCUSize, p.unitSizeDepth);
        frame.m_reconPic = new X265PicYuv(arena, p.sourceWidth, p.sourceHeight, p.internalCsp, p.maxCUSize);
        frame.m_reconPic.ShareOffsets(frame.m_fencPic);
        frame.m_fencPic.CopyFromPicture(y, u, v, strideY, strideC, conf.rightOffset, conf.bottomOffset);
        frame.m_picCTU = new X265CuData[sps.numCUsInFrame];
        for (uint i = 0; i < sps.numCUsInFrame; i++)
        {
            frame.m_picCTU[i] = new X265CuData();
            frame.m_picCTU[i].Initialize(arena, 0, p.internalCsp, (uint)p.num4x4Partitions, (uint)p.maxCUSize, (uint)p.unitSizeDepth, p.maxLog2CUSize);
        }

        frame.m_cuStatBaseQp = new double[sps.numCUsInFrame];
        if (slice.m_bUseSao)
        {
            frame.m_saoParam = new X265SaoParam((int)sps.numCUsInFrame, (int)sps.numCuInWidth);
        }

        // adaptive quantization offsets (lookahead)
        double[]? qpAqOffset = p.aqMode != 0 ? CalcAdaptiveQuantFrame(frame.m_fencPic, p) : null;

        // rate control: the QP of the first (I) frame
        double avgQp;
        int frameQp;
        if (p.rateControlMode == 1)
        {
            double ipOffset = 6.0 * Math.Log2(p.ipFactor);
            frameQp = p.qp != 0 && !p.bLossless ? Clip3(QP_MIN, QP_MAX_MAX, (int)(p.qp - ipOffset + 0.5)) : p.qp;
            avgQp = frameQp;
        }
        else
        {
            // RateControl::rateEstimateQscale, first CRF frame: qScale(CRF_INIT_QP) / ipFactor, clipped
            double q = QpToQScale((int)p.rfConstant) / Math.Abs(p.ipFactor);
            q = Clip3(QpToQScale(p.qpMin), QpToQScale(p.qpMax), q);
            double qp = QScaleToQp(q);
            qp = Clip3(p.qpMin, p.qpMax, qp);
            frameQp = (int)(qp + 0.5);
            avgQp = qp;
        }

        frame.m_avgQpRc = frame.m_avgQpAq = avgQp;
        slice.m_sliceQp = Clip3(-QP_BD_OFFSET, QP_MAX_SPEC, frameQp);

        var nalList = new X265NalList();
        var bs = new X265Bitstream();
        var entropy = new X265Entropy(arena);
        GetStreamHeaders(nalList, entropy, bs, vps, sps, pps, p);
        CompressFrame(arena, frame, qpAqOffset, nalList, bs, entropy);

        var result = new List<(int, byte[])>();
        foreach ((int type, byte[] data) in nalList.Nals)
        {
            int sc = data[2] == 1 ? 3 : 4;
            result.Add((type, data.AsSpan(sc).ToArray()));
        }

        return result;
    }

    public static double QpToQScale(double qp) => 0.85 * Math.Pow(2.0, (qp - 12.0) / 6.0);

    public static double QScaleToQp(double qScale) => 12.0 + (6.0 * Math.Log2(qScale / 0.85));

    private static void GetStreamHeaders(X265NalList list, X265Entropy sbacCoder, X265Bitstream bs, X265Vps vps, X265Sps sps, X265Pps pps, X265Param p)
    {
        sbacCoder.SetBitstream(bs);
        bs.ResetBits();
        sbacCoder.CodeVPS(vps);
        bs.WriteByteAlignment();
        list.Serialize(NAL_UNIT_VPS, bs);

        bs.ResetBits();
        sbacCoder.CodeSPS(sps, vps.ptl);
        bs.WriteByteAlignment();
        list.Serialize(NAL_UNIT_SPS, bs);

        bs.ResetBits();
        sbacCoder.CodePPS(pps, p.maxSlices <= 1, 0);
        bs.WriteByteAlignment();
        list.Serialize(NAL_UNIT_PPS, bs);
    }

    // ---- level (encoder/level.cpp) ----

    private readonly struct LevelSpec
    {
        public readonly uint maxLumaSamples;
        public readonly ulong maxLumaSamplesPerSecond;
        public readonly uint maxBitrateMain, maxBitrateHigh, maxCpbSizeMain, maxCpbSizeHigh, minCompressionRatio;
        public readonly int levelEnum;

        public LevelSpec(uint a, ulong b, uint c, uint d, uint e, uint f, uint g, int h)
        {
            maxLumaSamples = a;
            maxLumaSamplesPerSecond = b;
            maxBitrateMain = c;
            maxBitrateHigh = d;
            maxCpbSizeMain = e;
            maxCpbSizeHigh = f;
            minCompressionRatio = g;
            levelEnum = h;
        }
    }

    private const uint MAX_UINT = 0xFFFFFFFF;

    private static readonly LevelSpec[] Levels =
    {
        new(36864, 552960, 128, MAX_UINT, 350, MAX_UINT, 2, 30),
        new(122880, 3686400, 1500, MAX_UINT, 1500, MAX_UINT, 2, 60),
        new(245760, 7372800, 3000, MAX_UINT, 3000, MAX_UINT, 2, 63),
        new(552960, 16588800, 6000, MAX_UINT, 6000, MAX_UINT, 2, 90),
        new(983040, 33177600, 10000, MAX_UINT, 10000, MAX_UINT, 2, 93),
        new(2228224, 66846720, 12000, 30000, 12000, 30000, 4, 120),
        new(2228224, 133693440, 20000, 50000, 20000, 50000, 4, 123),
        new(8912896, 267386880, 25000, 100000, 25000, 100000, 6, 150),
        new(8912896, 534773760, 40000, 160000, 40000, 160000, 8, 153),
        new(8912896, 1069547520, 60000, 240000, 60000, 240000, 8, 156),
        new(35651584, 1069547520, 60000, 240000, 60000, 240000, 8, 180),
        new(35651584, 2139095040, 120000, 480000, 120000, 480000, 8, 183),
        new(35651584, 4278190080U, 240000, 800000, 240000, 800000, 6, 186),
        new(80216064, 4812963840UL, 320000, 1600000, 240000, 1600000, 6, 189),
        new(142606336, 4812963840UL, 320000, 1600000, 240000, 1600000, 6, 210),
        new(142606336, 8556380160UL, 480000, 3200000, 480000, 3200000, 6, 213),
        new(142606336, 17112760320UL, 960000, 6400000, 960000, 6400000, 6, 216),
        new(MAX_UINT, ulong.MaxValue, MAX_UINT, MAX_UINT, MAX_UINT, MAX_UINT, 1, 255),
    };

    private static void EnforceLevel(X265Param p, X265Vps vps)
    {
        vps.maxTempSubLayers = 1;
        vps.numReorderPics[0] = 0;   // no B frames
        vps.maxDecPicBuffering[0] = (uint)(Math.Min(16, Math.Max((int)vps.numReorderPics[0] + 2, p.maxNumReferences) + 1));
    }

    private static void DetermineLevel(X265Param p, X265Vps vps)
    {
        X265ProfileTierLevel ptl = vps.ptl;
        ptl.onePictureOnlyConstraintFlag = p.totalFrames == 1;
        ptl.intraConstraintFlag = p.keyframeMax <= 1 || ptl.onePictureOnlyConstraintFlag;
        ptl.bitDepthConstraint = (uint)X265_DEPTH;
        ptl.chromaFormatConstraint = p.internalCsp;
        ptl.lowerBitRateConstraintFlag = true;
        vps.maxTempSubLayers = 1;

        if (p.internalCsp == X265_CSP_I420 && X265_DEPTH <= 8)
        {
            ptl.profileIdc = ptl.onePictureOnlyConstraintFlag ? 3 : ptl.intraConstraintFlag ? 4 : 1;   // MAINSTILLPICTURE / MAINREXT / MAIN
        }
        else if (p.internalCsp == X265_CSP_I420 && X265_DEPTH <= 10)
        {
            ptl.profileIdc = ptl.intraConstraintFlag ? 4 : 2;   // MAINREXT (Main10 Intra; no 10-bit still picture profile) / MAIN10
        }
        else
        {
            ptl.profileIdc = 4;   // MAINREXT
        }

        Array.Clear(ptl.profileCompatibilityFlag);
        ptl.profileCompatibilityFlag[ptl.profileIdc] = true;
        if (ptl.profileIdc == 2 && X265_DEPTH == 8)
        {
            ptl.profileCompatibilityFlag[1] = true;
        }
        else if (ptl.profileIdc == 1)
        {
            ptl.profileCompatibilityFlag[2] = true;
        }
        else if (ptl.profileIdc == 3)
        {
            ptl.profileCompatibilityFlag[1] = true;
            ptl.profileCompatibilityFlag[2] = true;
        }
        else if (ptl.profileIdc == 4)
        {
            ptl.profileCompatibilityFlag[4] = true;
        }

        uint lumaSamples = (uint)(p.sourceWidth * p.sourceHeight);
        ulong samplesPerSec = (ulong)(lumaSamples * ((double)p.fpsNum / p.fpsDenom));
        const uint bitrate = 0;
        const uint maxDpbPicBuf = 6;
        ptl.levelIdc = 0;
        ptl.tierFlag = false;
        for (int i = 0; i < Levels.Length; i++)
        {
            LevelSpec l = Levels[i];
            if (lumaSamples > l.maxLumaSamples)
            {
                continue;
            }

            if (samplesPerSec > l.maxLumaSamplesPerSecond)
            {
                continue;
            }

            if (bitrate > l.maxBitrateMain && l.maxBitrateHigh == MAX_UINT)
            {
                continue;
            }

            if (bitrate > l.maxBitrateHigh)
            {
                continue;
            }

            if (p.sourceWidth > Math.Sqrt(l.maxLumaSamples * 8.0f))
            {
                continue;
            }

            if (p.sourceHeight > Math.Sqrt(l.maxLumaSamples * 8.0f))
            {
                continue;
            }

            uint maxDpbSize = maxDpbPicBuf;
            if (lumaSamples <= (l.maxLumaSamples >> 2))
            {
                maxDpbSize = Math.Min(4 * maxDpbPicBuf, 16);
            }
            else if (lumaSamples <= (l.maxLumaSamples >> 1))
            {
                maxDpbSize = Math.Min(2 * maxDpbPicBuf, 16);
            }
            else if (lumaSamples <= ((3 * l.maxLumaSamples) >> 2))
            {
                maxDpbSize = Math.Min((4 * maxDpbPicBuf) / 3, 16);
            }

            if (vps.maxDecPicBuffering[vps.maxTempSubLayers - 1] > maxDpbSize)
            {
                continue;
            }

            if (l.levelEnum >= 150 && p.maxCUSize < 32)
            {
                // level 5 and higher need CTU 32 or 64: x265 then signals profile and level NONE
                ptl.profileIdc = 0;
                ptl.levelIdc = 0;
                ptl.tierFlag = false;
                return;
            }

            ptl.tierFlag = false;
            ptl.levelIdc = l.levelEnum;
            ptl.minCrForLevel = l.minCompressionRatio;
            ptl.maxLumaSrForLevel = l.maxLumaSamplesPerSecond;
            break;
        }
    }

    // ---- adaptive quantization (slicetype.cpp calcAdaptiveQuantFrame, X265_AQ_VARIANCE) ----

    private static uint AcEnergyVar(ulong sumSsd, int shift)
    {
        uint sum = (uint)sumSsd;
        uint ssd = (uint)(sumSsd >> 32);
        return (uint)(ssd - (((ulong)sum * sum) >> shift));
    }

    private static uint AcEnergyCu(X265PicYuv pic, int blockX, int blockY, int csp, int qgSize)
    {
        long stride = pic.m_stride;
        long cStride = pic.m_strideC;
        long blockOffsetLuma = blockX + (blockY * stride);
        int hShift = csp == X265_CSP_I420 ? 1 : 0, vShift = hShift;
        long blockOffsetChroma = (blockX >> hShift) + ((blockY >> vShift) * cStride);
        uint var;
        if (qgSize == 8)
        {
            var = AcEnergyVar(X265Primitives.PixelVar(pic.m_picOrg[0] + blockOffsetLuma, stride, 8), 6);
        }
        else
        {
            var = AcEnergyVar(X265Primitives.PixelVar(pic.m_picOrg[0] + blockOffsetLuma, stride, 16), 8);
        }

        if (csp != X265_CSP_I400)
        {
            for (int plane = 1; plane <= 2; plane++)
            {
                pixel* src = pic.m_picOrg[plane] + blockOffsetChroma;
                if (qgSize == 8)
                {
                    var += AcEnergyVar(X265Primitives.PixelVar(src, cStride, 4), 4);
                }
                else
                {
                    var += AcEnergyVar(X265Primitives.PixelVar(src, cStride, 8), 6);
                }
            }
        }

        return var;
    }

    private static double[] CalcAdaptiveQuantFrame(X265PicYuv fenc, X265Param p)
    {
        int maxCol = fenc.m_picWidth;
        int maxRow = fenc.m_picHeight;
        int loopIncr = p.qgSize == 8 ? 8 : 16;
        float modeOneConst = p.qgSize == 8 ? 11.427f : 14.427f;
        int blocksX = (maxCol + loopIncr - 1) / loopIncr;
        int blocksY = (maxRow + loopIncr - 1) / loopIncr;
        var qpAqOffset = new double[blocksX * blocksY];
        double strength = p.aqStrength * 1.0397f;
        int blockXY = 0;
        for (int blockY = 0; blockY < maxRow; blockY += loopIncr)
        {
            for (int blockX = 0; blockX < maxCol; blockX += loopIncr)
            {
                uint energy = AcEnergyCu(fenc, blockX, blockY, p.internalCsp, p.qgSize);
                double qpAdj = strength * (Math.Log2(Math.Max(energy, 1)) - (double)(modeOneConst + (2 * (X265_DEPTH - 8))));
                qpAqOffset[blockXY++] = qpAdj;
            }
        }

        return qpAqOffset;
    }

    // ---- frame encoder ----

    private static void CompressFrame(X265Arena arena, X265FrameData frame, double[]? qpAqOffset, X265NalList nalList, X265Bitstream bs,
        X265Entropy entropyCoder)
    {
        X265Param p = frame.m_param;
        X265Slice slice = frame.m_slice;
        X265Sps sps = slice.m_sps;
        uint numCols = sps.numCuInWidth;
        uint numRows = sps.numCuInHeight;

        // FrameEncoder::initializeGeoms
        int maxCUSize = p.maxCUSize;
        int heightRem = p.sourceHeight & (maxCUSize - 1);
        int widthRem = p.sourceWidth & (maxCUSize - 1);
        CUGeom* cuGeoms = arena.Alloc<CUGeom>(4 * CUGeom.MAX_GEOMS);
        uint[] ctuGeomMap = new uint[numRows * numCols];
        CUGeom.CalcCTUGeoms((uint)maxCUSize, (uint)maxCUSize, (uint)maxCUSize, (uint)p.minCUSize, cuGeoms);
        int countGeoms = 1;
        if (widthRem != 0)
        {
            CUGeom.CalcCTUGeoms((uint)widthRem, (uint)maxCUSize, (uint)maxCUSize, (uint)p.minCUSize, cuGeoms + (countGeoms * CUGeom.MAX_GEOMS));
            for (uint i = 0; i < numRows; i++)
            {
                ctuGeomMap[(numCols * (i + 1)) - 1] = (uint)(countGeoms * CUGeom.MAX_GEOMS);
            }

            countGeoms++;
        }

        if (heightRem != 0)
        {
            CUGeom.CalcCTUGeoms((uint)maxCUSize, (uint)heightRem, (uint)maxCUSize, (uint)p.minCUSize, cuGeoms + (countGeoms * CUGeom.MAX_GEOMS));
            for (uint i = 0; i < numCols; i++)
            {
                ctuGeomMap[(numCols * (numRows - 1)) + i] = (uint)(countGeoms * CUGeom.MAX_GEOMS);
            }

            countGeoms++;
            if (widthRem != 0)
            {
                CUGeom.CalcCTUGeoms((uint)widthRem, (uint)heightRem, (uint)maxCUSize, (uint)p.minCUSize, cuGeoms + (countGeoms * CUGeom.MAX_GEOMS));
                ctuGeomMap[(numCols * numRows) - 1] = (uint)(countGeoms * CUGeom.MAX_GEOMS);
            }
        }

        var initSliceContext = new X265Entropy(arena);
        initSliceContext.ResetEntropy(slice);

        // one analysis (search state) per worker: WPP rows run in parallel, each CTU once the row above is two CTUs
        // ahead (x265's wavefront; the output does not depend on the thread count)
        bool parallelRows = p.bEnableWavefront && numRows > 1;
        int workers = parallelRows ? Math.Max(1, Math.Min((int)numRows, MaxWorkers > 0 ? MaxWorkers : Environment.ProcessorCount)) : 1;
        var analyses = new X265Analysis[workers];
        for (int i = 0; i < workers; i++)
        {
            analyses[i] = new X265Analysis(arena, p) { QpAqOffset = qpAqOffset };
            analyses[i].SetFrame(frame);
        }

        // per-row coders (WPP) and their buffered second-CTU contexts
        var rowCoders = new X265Entropy[numRows];
        var bufferedEntropy = new X265Entropy[numRows];
        var outStreams = new X265Bitstream[numRows];
        uint numSubstreams = p.bEnableWavefront ? numRows : 1;
        for (uint r = 0; r < numRows; r++)
        {
            // without WPP every row continues row 0's coder
            rowCoders[r] = p.bEnableWavefront || r == 0 ? new X265Entropy(arena) : rowCoders[0];
            bufferedEntropy[r] = new X265Entropy(arena);
            outStreams[r] = new X265Bitstream();
            if (r < numSubstreams)
            {
                rowCoders[r].SetBitstream(slice.m_bUseSao ? null : outStreams[r]);
            }
        }

        RowFilter? rowFilter = p.bEnableLoopFilter || slice.m_bUseSao ? new RowFilter(arena, frame, cuGeoms, ctuGeomMap, initSliceContext) : null;
        var sliceBufferedEntropy = new X265Entropy[numRows];
        for (uint r = 0; r < numRows; r++)
        {
            sliceBufferedEntropy[r] = new X265Entropy(arena);
        }

        // analysis (processRowEncoder) of every CTU row
        var completed = new int[numRows];
        if (workers == 1)
        {
            for (uint row = 0; row < numRows; row++)
            {
                AnalyseRow(row, analyses[0]);
            }
        }
        else
        {
            int nextRow = -1;
            var threads = new System.Threading.Thread[workers];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            for (int i = 0; i < workers; i++)
            {
                X265Analysis worker = analyses[i];
                threads[i] = new System.Threading.Thread(() =>
                {
                    try
                    {
                        for (int row; (row = System.Threading.Interlocked.Increment(ref nextRow)) < numRows && failure == null;)
                        {
                            AnalyseRow((uint)row, worker);
                        }
                    }
                    catch (Exception e)
                    {
                        failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                        // release the rows waiting on this one
                        for (int r = 0; r < numRows; r++) System.Threading.Volatile.Write(ref completed[r], (int)numCols);
                    }
                }, 16 * 1024 * 1024) { IsBackground = true };
                threads[i].Start();
            }

            // meanwhile, row r's filters and final bitstream once the analysis of row r + 1 is done
            for (uint row = 0; row < numRows; row++)
            {
                ref int below = ref completed[Math.Min(row + 1, numRows - 1)];
                var spin = new System.Threading.SpinWait();
                while (System.Threading.Volatile.Read(ref below) < numCols && failure == null) spin.SpinOnce(sleep1Threshold: -1);
                if (failure != null) break;
                FilterAndEncodeRow(row);
            }

            foreach (var t in threads) t.Join();
            failure?.Throw();
        }

        void AnalyseRow(uint row, X265Analysis analysis)
        {
            X265Entropy rowCoder = rowCoders[row];
            bool bFirstRowInSlice = row == 0;
            bool bLastRowInSlice = row == numRows - 1;
            if (bFirstRowInSlice)
            {
                rowCoder.Load(initSliceContext);
            }

            for (uint col = 0; col < numCols; col++)
            {
                if (row > 0)
                {
                    int need = (int)Math.Min(col + 2, numCols);
                    ref int above = ref completed[row - 1];
                    if (System.Threading.Volatile.Read(ref above) < need)
                    {
                        var spin = new System.Threading.SpinWait();
                        while (System.Threading.Volatile.Read(ref above) < need) spin.SpinOnce(sleep1Threshold: -1);
                    }
                }

                uint cuAddr = (row * numCols) + col;
                X265CuData ctu = frame.GetPicCTU(cuAddr);
                uint bLastCuInSlice = bLastRowInSlice && col == numCols - 1 ? 1u : 0u;
                ctu.InitCTU(frame, cuAddr, slice.m_sliceQp, bFirstRowInSlice ? 1u : 0u, bLastRowInSlice ? 1u : 0u, bLastCuInSlice);
                frame.m_cuStatBaseQp[cuAddr] = frame.m_avgQpRc;

                if (p.bEnableWavefront && col == 0 && !bFirstRowInSlice)
                {
                    rowCoder.CopyState(initSliceContext);
                    rowCoder.LoadContexts(bufferedEntropy[row - 1]);
                }

                CUGeom* geom = cuGeoms + ctuGeomMap[cuAddr];
                analysis.CompressCTU(ctu, geom, rowCoder);
                rowCoder.EncodeCTU(ctu, geom);
                if (p.bEnableWavefront && col == 1)
                {
                    bufferedEntropy[row].LoadContexts(rowCoder);
                }

                System.Threading.Volatile.Write(ref completed[row], (int)col + 1);
            }

            if (!slice.m_bUseSao && (p.bEnableWavefront || bLastRowInSlice))
            {
                rowCoder.FinishSlice();
            }
        }

        if (workers == 1)
        {
            for (uint row = 0; row < numRows; row++)
            {
                FilterAndEncodeRow(row);
            }
        }

        if (slice.m_bUseSao && !p.bEnableWavefront)
        {
            entropyCoder.FinishSlice();
        }

        // the loop filters of a row (FrameFilter::processRow), then its final bitstream when SAO is on (encodeSlice)
        void FilterAndEncodeRow(uint row)
        {
            rowFilter?.ProcessRow((int)row);
            if (!slice.m_bUseSao)
            {
                return;
            }

            X265SaoParam saoParam = frame.m_saoParam!;
            if (row == 0)
            {
                entropyCoder.Load(initSliceContext);
            }

            for (uint cuAddr = row * numCols; cuAddr < (row + 1) * numCols; cuAddr++)
            {
                uint col = cuAddr % numCols;
                uint subStrm = row % numSubstreams;
                X265CuData ctu = frame.GetPicCTU(cuAddr);
                entropyCoder.SetBitstream(outStreams[subStrm]);
                if (p.bEnableWavefront && col == 0 && row != 0)
                {
                    entropyCoder.CopyState(initSliceContext);
                    entropyCoder.LoadContexts(sliceBufferedEntropy[row - 1]);
                }

                if (ctu.m_bFirstRowInSlice != 0 && col == 0)
                {
                    entropyCoder.Load(initSliceContext);
                }

                if (saoParam.bSaoFlag[0] || saoParam.bSaoFlag[1])
                {
                    bool mergeLeft = col != 0 && saoParam.ctuParam[0][cuAddr].mergeMode == X265Sao.SAO_MERGE_LEFT;
                    bool mergeUp = ctu.m_bFirstRowInSlice == 0 && saoParam.ctuParam[0][cuAddr].mergeMode == X265Sao.SAO_MERGE_UP;
                    if (col != 0)
                    {
                        entropyCoder.CodeSaoMerge(mergeLeft ? 1u : 0u);
                    }

                    if (ctu.m_bFirstRowInSlice == 0 && !mergeLeft)
                    {
                        entropyCoder.CodeSaoMerge(mergeUp ? 1u : 0u);
                    }

                    if (!mergeLeft && !mergeUp)
                    {
                        if (saoParam.bSaoFlag[0])
                        {
                            entropyCoder.CodeSaoOffset(saoParam.ctuParam[0][cuAddr], 0);
                        }

                        if (saoParam.bSaoFlag[1])
                        {
                            entropyCoder.CodeSaoOffset(saoParam.ctuParam[1][cuAddr], 1);
                            entropyCoder.CodeSaoOffset(saoParam.ctuParam[2][cuAddr], 2);
                        }
                    }
                }

                entropyCoder.EncodeCTU(ctu, cuGeoms + ctuGeomMap[cuAddr]);
                if (p.bEnableWavefront)
                {
                    if (col == 1)
                    {
                        sliceBufferedEntropy[row].LoadContexts(entropyCoder);
                    }

                    if (col == numCols - 1)
                    {
                        entropyCoder.FinishSlice();
                    }
                }
            }
        }

        // slice header, substreams and WPP entry points
        bs.ResetBits();
        entropyCoder.SetBitstream(bs);
        entropyCoder.CodeSliceHeader(slice, frame, 0, 0, slice.m_sliceQp);
        var substreamSizes = new uint[numSubstreams];
        uint maxStreamSize = nalList.SerializeSubstreams(substreamSizes, numSubstreams, outStreams);
        if (slice.m_pps.bEntropyCodingSyncEnabled)
        {
            entropyCoder.CodeSliceHeaderWPPEntryPoints(substreamSizes, numRows - 1, maxStreamSize);
        }

        bs.WriteByteAlignment();
        nalList.Serialize(slice.m_nalUnitType, bs);
    }

    /// <summary>FrameFilter::ParallelFilter::processTasks for every row (all columns allowed), then the last row's SAO.</summary>
    // FrameFilter::processRow: the deblocking and SAO steps of one CTU row (rows in order; row r once the analysis of
    // row r + 1 is done, as its intra prediction reads row r unfiltered)
    private sealed class RowFilter
    {
        private readonly X265FrameData frame;
        private readonly CUGeom* cuGeoms;
        private readonly uint[] ctuGeomMap;
        private readonly X265Param p;
        private readonly int numCols, numRows;
        private readonly bool useSao;
        private readonly X265SaoParam? saoParam;
        private readonly X265PicYuv reconPic;
        private readonly X265Sao[] sao;

        public RowFilter(X265Arena arena, X265FrameData frame, CUGeom* cuGeoms, uint[] ctuGeomMap, X265Entropy initState)
        {
            this.frame = frame;
            this.cuGeoms = cuGeoms;
            this.ctuGeomMap = ctuGeomMap;
            p = frame.m_param;
            X265Slice slice = frame.m_slice;
            numCols = (int)slice.m_sps.numCuInWidth;
            numRows = (int)slice.m_sps.numCuInHeight;
            useSao = slice.m_bUseSao;
            saoParam = frame.m_saoParam;
            reconPic = frame.m_reconPic;
            sao = new X265Sao[numRows];
            if (useSao)
            {
                for (int r = 0; r < numRows; r++)
                {
                    sao[r] = new X265Sao(arena, p);
                    sao[r].StartSlice(frame, initState);
                }
            }
        }

        private void ProcessSaoCTU(int row, int col)
        {
            if (saoParam!.bSaoFlag[0])
            {
                sao[row].GenerateLumaOffsets(saoParam.ctuParam[0], row, col);
            }

            if (saoParam.bSaoFlag[1])
            {
                sao[row].GenerateChromaOffsets(saoParam.ctuParam, row, col);
            }
        }

        public void ProcessRow(int row)
        {
        uint rowAddr = (uint)(row * numCols);
            for (int col = 0; col < numCols; col++)
            {
                uint cuAddr = rowAddr + (uint)col;
                X265CuData ctu = frame.GetPicCTU(cuAddr);
                if (p.bEnableLoopFilter)
                {
                    X265Deblock.DeblockCTU(ctu, cuGeoms + ctuGeomMap[cuAddr], X265Deblock.EDGE_VER);
                }
    
                if (col >= 1)
                {
                    X265CuData ctuPrev = frame.GetPicCTU(cuAddr - 1);
                    if (p.bEnableLoopFilter)
                    {
                        X265Deblock.DeblockCTU(ctuPrev, cuGeoms + ctuGeomMap[cuAddr - 1], X265Deblock.EDGE_HOR);
                    }
    
                    if (useSao)
                    {
                        sao[row].CopySaoAboveRef(ctuPrev, reconPic, cuAddr - 1, col - 1);
                        if (col >= 2)
                        {
                            sao[row].RdoSaoUnitCu(saoParam!, ctu.m_bFirstRowInSlice != 0 ? 0 : (int)rowAddr, col - 2, (int)cuAddr - 2);
                        }
    
                        if (ctu.m_bFirstRowInSlice == 0 && col >= 3)
                        {
                            ProcessSaoCTU(row - 1, col - 3);
                        }
                    }
                }
            }
    
            {
                uint cuAddr = rowAddr + (uint)numCols - 1;
                X265CuData ctuPrev = frame.GetPicCTU(cuAddr);
                if (p.bEnableLoopFilter)
                {
                    X265Deblock.DeblockCTU(ctuPrev, cuGeoms + ctuGeomMap[cuAddr], X265Deblock.EDGE_HOR);
                }
    
                if (useSao)
                {
                    X265CuData ctu = frame.GetPicCTU(rowAddr + (uint)Math.Max(numCols - 2, 0));
                    sao[row].CopySaoAboveRef(ctuPrev, reconPic, cuAddr, numCols - 1);
                    if (numCols >= 2)
                    {
                        sao[row].RdoSaoUnitCu(saoParam!, ctu.m_bFirstRowInSlice != 0 ? 0 : (int)rowAddr, numCols - 2, (int)cuAddr - 1);
                    }
    
                    if (numCols >= 1)
                    {
                        sao[row].RdoSaoUnitCu(saoParam!, ctuPrev.m_bFirstRowInSlice != 0 ? 0 : (int)rowAddr, numCols - 1, (int)cuAddr);
                    }
    
                    if (ctuPrev.m_bFirstRowInSlice == 0 && numCols >= 3)
                    {
                        ProcessSaoCTU(row - 1, numCols - 3);
                    }
    
                    if (ctuPrev.m_bFirstRowInSlice == 0 && numCols >= 2)
                    {
                        ProcessSaoCTU(row - 1, numCols - 2);
                    }
    
                    if (ctuPrev.m_bFirstRowInSlice == 0 && numCols >= 1)
                    {
                        ProcessSaoCTU(row - 1, numCols - 1);
                    }
                }
            }
    
            // the last row of CTUs: its own SAO
            if (row == numRows - 1 && useSao)
            {
                for (int col = 0; col < numCols; col++)
                {
                    ProcessSaoCTU(row, col);
                }
            }
        }
    }
}
