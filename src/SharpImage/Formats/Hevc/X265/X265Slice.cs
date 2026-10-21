// Port of the x265 structures a single intra picture needs: the effective x265_param (x265 4.x after
// x265_param_default_preset("slow", "ssim"), x265_param_apply_profile("mainstillpicture") and libheif 1.23's
// overrides), common/slice.h (VPS, SPS, PPS, Slice), common/framedata.h (FrameData) and SAO parameters.
using System;

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

/// <summary>The x265_param fields the intra path reads.</summary>
internal sealed class X265Param
{
    // geometry (sourceWidth / sourceHeight padded to a multiple of minCUSize by Encoder::configure)
    public int sourceWidth, sourceHeight;
    public int confWinRightOffset, confWinBottomOffset;
    public int internalCsp = X265_CSP_I420;
    public int maxCUSize = 64;
    public int maxLog2CUSize = 6;
    public int minCUSize = 8;
    public int maxCUDepth = 3;
    public int unitSizeDepth = 4;
    public int num4x4Partitions = 256;
    public int maxTUSize = 32;
    public int tuQTMaxIntraDepth = 2;
    public int tuQTMaxInterDepth = 1;

    // analysis
    public int rdLevel = 4;
    public int rdoqLevel = 2;
    public double psyRd = 1.0;
    public double psyRdoq = 1.0;
    public bool bEnableSignHiding = true;
    public bool bEnableTransformSkip;
    public bool bEnableStrongIntraSmoothing = true;
    public bool bLossless;
    public bool bCULossless;
    public int cbQpOffset, crQpOffset;
    public bool bEnableAMP = true;
    public bool bEnableTemporalMvp = true;

    // loop filters
    public bool bEnableLoopFilter = true;
    public int deblockingFilterBetaOffset, deblockingFilterTCOffset;
    public bool bEnableSAO = true;
    public bool bSaoNonDeblocked;
    public int selectiveSAO = 4;
    public int maxSlices = 1;
    public bool bEnableWavefront = true;

    // rate control (CRF)
    public int rateControlMode = 2;      // X265_RC_CRF (0 = ABR, 1 = CQP)
    public double rfConstant = 25;
    public int qp = 32;                  // CQP
    public double qCompress = 0.6;
    public double ipFactor = 1.4;
    public double pbFactor = 1.3;
    public int aqMode = 1;
    public double aqStrength = 1.0;
    public int qgSize = 32;
    public int qpMin = 0, qpMax = QP_MAX_MAX;

    // stream
    public int fpsNum = 1, fpsDenom = 25;
    public int log2MaxPocLsb = 8;
    public bool bEmitVUITimingInfo = true;
    public bool bEmitVUIHRDInfo = true;
    public int maxNumReferences = 1;
    public int bframes;
    public int keyframeMax = 1;
    public int totalFrames = 1;

    // VUI
    public bool vuiVideoSignalTypePresent = true;
    public int vuiVideoFormat = 5;
    public bool vuiFullRange = true;
    public bool vuiColorDescriptionPresent = true;
    public int vuiColorPrimaries = 1, vuiTransferCharacteristics = 13, vuiMatrixCoeffs = 6;
    public int vuiAspectRatioIdc, vuiSarWidth, vuiSarHeight;
}

internal struct X265Window
{
    public int leftOffset, rightOffset, topOffset, bottomOffset;
    public bool bEnabled;
}

internal sealed class X265ProfileTierLevel
{
    public int profileIdc;
    public int levelIdc;
    public uint minCrForLevel;
    public ulong maxLumaSrForLevel;
    public uint bitDepthConstraint;
    public int chromaFormatConstraint;
    public bool tierFlag;
    public bool progressiveSourceFlag, interlacedSourceFlag, nonPackedConstraintFlag, frameOnlyConstraintFlag;
    public readonly bool[] profileCompatibilityFlag = new bool[32];
    public bool intraConstraintFlag, onePictureOnlyConstraintFlag, lowerBitRateConstraintFlag;
}

internal sealed class X265Vps
{
    public readonly X265ProfileTierLevel ptl = new();
    public uint maxTempSubLayers = 1;
    public readonly uint[] numReorderPics = new uint[8];
    public readonly uint[] maxDecPicBuffering = new uint[8];
    public readonly uint[] maxLatencyIncrease = new uint[8];
}

internal sealed class X265Sps
{
    public int chromaFormatIdc;
    public uint picWidthInLumaSamples, picHeightInLumaSamples;
    public uint numCuInWidth, numCuInHeight, numCUsInFrame;
    public uint numPartitions, numPartInCUSize;
    public int log2MinCodingBlockSize, log2DiffMaxMinCodingBlockSize, log2MaxPocLsb;
    public uint quadtreeTULog2MaxSize, quadtreeTULog2MinSize, quadtreeTUMaxDepthInter, quadtreeTUMaxDepthIntra;
    public uint maxAMPDepth;
    public uint maxTempSubLayers;
    public readonly uint[] maxDecPicBuffering = new uint[8];
    public readonly uint[] maxLatencyIncrease = new uint[8];
    public readonly int[] numReorderPics = new int[8];
    public int spsrpsNum;
    public bool bUseSAO, bUseAMP, bUseStrongIntraSmoothing, bTemporalMVPEnabled, bEmitVUITimingInfo, bEmitVUIHRDInfo;
    public X265Window conformanceWindow;

    // VUI
    public bool aspectRatioInfoPresentFlag;
    public int aspectRatioIdc, sarWidth, sarHeight;
    public bool overscanInfoPresentFlag, overscanAppropriateFlag;
    public bool videoSignalTypePresentFlag;
    public int videoFormat;
    public bool videoFullRangeFlag, colourDescriptionPresentFlag;
    public int colourPrimaries, transferCharacteristics, matrixCoefficients;
    public bool chromaLocInfoPresentFlag;
    public bool frameFieldInfoPresentFlag, fieldSeqFlag, hrdParametersPresentFlag;
    public X265Window defaultDisplayWindow;
    public uint numUnitsInTick, timeScale;
}

internal sealed class X265Pps
{
    public uint maxCuDQPDepth;
    public readonly int[] chromaQpOffset = new int[2];
    public int deblockingFilterBetaOffsetDiv2, deblockingFilterTcOffsetDiv2;
    public bool bUseWeightPred, bUseWeightedBiPred, bUseDQP, bConstrainedIntraPred, bTransquantBypassEnabled;
    public bool bTransformSkipEnabled, bEntropyCodingSyncEnabled, bSignHideEnabled;
    public bool bDeblockingFilterControlPresent, bPicDisableDeblockingFilter;
    public readonly int[] numRefIdxDefault = new int[2];
    public bool pps_slice_chroma_qp_offsets_present_flag;
}

internal sealed class X265Slice
{
    public X265Sps m_sps = null!;
    public X265Pps m_pps = null!;
    public X265Param m_param = null!;
    public int m_nalUnitType = 20;    // NAL_UNIT_CODED_SLICE_IDR_N_LP (no leading pictures)
    public int m_sliceType = I_SLICE;
    public int m_sliceQp;
    public readonly int[] m_chromaQpOffset = new int[2];
    public int m_poc;
    public uint m_endCUAddr;
    public bool m_sLFaseFlag = true;
    public bool m_bUseSao;
    public int m_iPPSQpMinus26;

    public bool IsIntra => m_sliceType == I_SLICE;

    public bool GetRapPicFlag => m_nalUnitType >= 16 && m_nalUnitType <= 23;

    public bool GetIdrPicFlag => m_nalUnitType == 19 || m_nalUnitType == 20;
}

/// <summary>struct SaoCtuParam.</summary>
internal struct SaoCtuParam
{
    public int mergeMode;
    public int typeIdx;
    public uint bandPos;
    public int offset0, offset1, offset2, offset3;

    public int GetOffset(int i) => i switch { 0 => offset0, 1 => offset1, 2 => offset2, _ => offset3 };

    public void SetOffset(int i, int v)
    {
        switch (i)
        {
            case 0: offset0 = v; break;
            case 1: offset1 = v; break;
            case 2: offset2 = v; break;
            default: offset3 = v; break;
        }
    }

    public void Reset()
    {
        mergeMode = 0;   // SAO_MERGE_NONE
        typeIdx = -1;
        bandPos = 0;
        offset0 = offset1 = offset2 = offset3 = 0;
    }
}

/// <summary>struct SAOParam.</summary>
internal sealed class X265SaoParam
{
    public readonly SaoCtuParam[][] ctuParam = new SaoCtuParam[3][];
    public readonly bool[] bSaoFlag = new bool[2];
    public int numCuInWidth;

    public X265SaoParam(int numCtus, int numCuInWidth)
    {
        for (int i = 0; i < 3; i++) ctuParam[i] = new SaoCtuParam[numCtus];
        this.numCuInWidth = numCuInWidth;
    }
}

/// <summary>class FrameData (encoder state of one picture).</summary>
internal sealed class X265FrameData
{
    public X265Param m_param = null!;
    public X265Slice m_slice = null!;
    public X265CuData[] m_picCTU = Array.Empty<X265CuData>();
    public X265PicYuv m_reconPic = null!;
    public X265PicYuv m_fencPic = null!;
    public X265SaoParam? m_saoParam;
    public double m_avgQpRc, m_avgQpAq;
    public double[] m_cuStatBaseQp = Array.Empty<double>();

    public X265CuData GetPicCTU(uint ctuAddr) => m_picCTU[ctuAddr];
}
