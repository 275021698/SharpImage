// Port of x265 encoder/entropy.cpp parameter-set and slice-header writers (single layer, no scaling lists, no
// SCC / multiview / alpha extensions).
using static SharpImage.Formats.Hevc.X265.X265Const;

namespace SharpImage.Formats.Hevc.X265;

internal sealed unsafe partial class X265Entropy
{
    private X265Bitstream Bs => m_bitIf!;

    public void CodeVPS(X265Vps vps)
    {
        X265Bitstream bs = Bs;
        bs.WriteCode(0, 4);                         // vps_video_parameter_set_id
        bs.WriteCode(3, 2);                         // vps_reserved_three_2bits
        bs.WriteCode(0, 6);                         // vps_reserved_zero_6bits (maxLayers - 1)
        bs.WriteCode(vps.maxTempSubLayers - 1, 3);  // vps_max_sub_layers_minus1
        bs.WriteFlag(vps.maxTempSubLayers == 1);    // vps_temporal_id_nesting_flag
        bs.WriteCode(0xffff, 16);                   // vps_reserved_ffff_16bits
        CodeProfileTier(vps.ptl, (int)vps.maxTempSubLayers);
        bs.WriteFlag(true);                         // vps_sub_layer_ordering_info_present_flag
        for (uint i = 0; i < vps.maxTempSubLayers; i++)
        {
            bs.WriteUvlc(vps.maxDecPicBuffering[i] - 1);
            bs.WriteUvlc(vps.numReorderPics[i]);
            bs.WriteUvlc(vps.maxLatencyIncrease[i] + 1);
        }

        bs.WriteCode(0, 6);                         // vps_max_nuh_reserved_zero_layer_id
        bs.WriteUvlc(0);                            // vps_max_op_sets_minus1
        bs.WriteFlag(false);                        // vps_timing_info_present_flag
        bs.WriteFlag(false);                        // vps_extension_flag
    }

    public void CodeSPS(X265Sps sps, X265ProfileTierLevel ptl)
    {
        X265Bitstream bs = Bs;
        bs.WriteCode(0, 4);                         // sps_video_parameter_set_id
        bs.WriteCode(sps.maxTempSubLayers - 1, 3);  // sps_max_sub_layers_minus1
        bs.WriteFlag(sps.maxTempSubLayers == 1);    // sps_temporal_id_nesting_flag
        CodeProfileTier(ptl, (int)sps.maxTempSubLayers);
        bs.WriteUvlc(0);                            // sps_seq_parameter_set_id
        bs.WriteUvlc((uint)sps.chromaFormatIdc);
        if (sps.chromaFormatIdc == X265_CSP_I444)
        {
            bs.WriteFlag(false);                    // separate_colour_plane_flag
        }

        bs.WriteUvlc(sps.picWidthInLumaSamples);
        bs.WriteUvlc(sps.picHeightInLumaSamples);
        X265Window conf = sps.conformanceWindow;
        bs.WriteFlag(conf.bEnabled);
        if (conf.bEnabled)
        {
            int hShift = sps.chromaFormatIdc == X265_CSP_I420 || sps.chromaFormatIdc == X265_CSP_I422 ? 1 : 0;
            int vShift = sps.chromaFormatIdc == X265_CSP_I420 ? 1 : 0;
            bs.WriteUvlc((uint)(conf.leftOffset >> hShift));
            bs.WriteUvlc((uint)(conf.rightOffset >> hShift));
            bs.WriteUvlc((uint)(conf.topOffset >> vShift));
            bs.WriteUvlc((uint)(conf.bottomOffset >> vShift));
        }

        bs.WriteUvlc(X265_DEPTH - 8);               // bit_depth_luma_minus8
        bs.WriteUvlc(X265_DEPTH - 8);               // bit_depth_chroma_minus8
        bs.WriteUvlc((uint)(sps.log2MaxPocLsb - 4));
        bs.WriteFlag(true);                         // sps_sub_layer_ordering_info_present_flag
        for (uint i = 0; i < sps.maxTempSubLayers; i++)
        {
            bs.WriteUvlc(sps.maxDecPicBuffering[i] - 1);
            bs.WriteUvlc((uint)sps.numReorderPics[i]);
            bs.WriteUvlc(sps.maxLatencyIncrease[i] + 1);
        }

        bs.WriteUvlc((uint)(sps.log2MinCodingBlockSize - 3));
        bs.WriteUvlc((uint)sps.log2DiffMaxMinCodingBlockSize);
        bs.WriteUvlc(sps.quadtreeTULog2MinSize - 2);
        bs.WriteUvlc(sps.quadtreeTULog2MaxSize - sps.quadtreeTULog2MinSize);
        bs.WriteUvlc(sps.quadtreeTUMaxDepthInter - 1);
        bs.WriteUvlc(sps.quadtreeTUMaxDepthIntra - 1);
        bs.WriteFlag(false);                        // scaling_list_enabled_flag
        bs.WriteFlag(sps.bUseAMP);
        bs.WriteFlag(sps.bUseSAO);
        bs.WriteFlag(false);                        // pcm_enabled_flag
        bs.WriteUvlc((uint)sps.spsrpsNum);          // num_short_term_ref_pic_sets
        bs.WriteFlag(false);                        // long_term_ref_pics_present_flag
        bs.WriteFlag(sps.bTemporalMVPEnabled);
        bs.WriteFlag(sps.bUseStrongIntraSmoothing);
        bs.WriteFlag(true);                         // vui_parameters_present_flag
        CodeVUI(sps, (int)sps.maxTempSubLayers);
        bs.WriteFlag(false);                        // sps_extension_flag
    }

    public void CodePPS(X265Pps pps, bool filerAcross, int iPPSInitQpMinus26)
    {
        X265Bitstream bs = Bs;
        bs.WriteUvlc(0);                            // pps_pic_parameter_set_id
        bs.WriteUvlc(0);                            // pps_seq_parameter_set_id
        bs.WriteFlag(false);                        // dependent_slice_segments_enabled_flag
        bs.WriteFlag(false);                        // output_flag_present_flag
        bs.WriteCode(0, 3);                         // num_extra_slice_header_bits
        bs.WriteFlag(pps.bSignHideEnabled);
        bs.WriteFlag(false);                        // cabac_init_present_flag
        bs.WriteUvlc((uint)(pps.numRefIdxDefault[0] - 1));
        bs.WriteUvlc((uint)(pps.numRefIdxDefault[1] - 1));
        bs.WriteSvlc(iPPSInitQpMinus26);
        bs.WriteFlag(pps.bConstrainedIntraPred);
        bs.WriteFlag(pps.bTransformSkipEnabled);
        bs.WriteFlag(pps.bUseDQP);
        if (pps.bUseDQP)
        {
            bs.WriteUvlc(pps.maxCuDQPDepth);
        }

        bs.WriteSvlc(pps.chromaQpOffset[0]);
        bs.WriteSvlc(pps.chromaQpOffset[1]);
        bs.WriteFlag(pps.pps_slice_chroma_qp_offsets_present_flag);
        bs.WriteFlag(pps.bUseWeightPred);
        bs.WriteFlag(pps.bUseWeightedBiPred);
        bs.WriteFlag(pps.bTransquantBypassEnabled);
        bs.WriteFlag(false);                        // tiles_enabled_flag
        bs.WriteFlag(pps.bEntropyCodingSyncEnabled);
        bs.WriteFlag(filerAcross);                  // loop_filter_across_slices_enabled_flag
        bs.WriteFlag(pps.bDeblockingFilterControlPresent);
        if (pps.bDeblockingFilterControlPresent)
        {
            bs.WriteFlag(false);                    // deblocking_filter_override_enabled_flag
            bs.WriteFlag(pps.bPicDisableDeblockingFilter);
            if (!pps.bPicDisableDeblockingFilter)
            {
                bs.WriteSvlc(pps.deblockingFilterBetaOffsetDiv2);
                bs.WriteSvlc(pps.deblockingFilterTcOffsetDiv2);
            }
        }

        bs.WriteFlag(false);                        // pps_scaling_list_data_present_flag
        bs.WriteFlag(false);                        // lists_modification_present_flag
        bs.WriteUvlc(0);                            // log2_parallel_merge_level_minus2
        bs.WriteFlag(false);                        // slice_segment_header_extension_present_flag
        bs.WriteFlag(false);                        // pps_extension_flag
    }

    private void CodeProfileTier(X265ProfileTierLevel ptl, int maxTempSubLayers)
    {
        X265Bitstream bs = Bs;
        bs.WriteCode(0, 2);                         // profile_space
        bs.WriteFlag(ptl.tierFlag);
        bs.WriteCode((uint)ptl.profileIdc, 5);
        for (int j = 0; j < 32; j++)
        {
            bs.WriteFlag(ptl.profileCompatibilityFlag[j]);
        }

        bs.WriteFlag(ptl.progressiveSourceFlag);
        bs.WriteFlag(ptl.interlacedSourceFlag);
        bs.WriteFlag(ptl.nonPackedConstraintFlag);
        bs.WriteFlag(ptl.frameOnlyConstraintFlag);
        if (ptl.profileIdc == 4 || ptl.profileIdc == 5)   // MAINREXT, HIGHTHROUGHPUTREXT
        {
            uint bitDepthConstraint = ptl.bitDepthConstraint;
            int csp = ptl.chromaFormatConstraint;
            bs.WriteFlag(bitDepthConstraint <= 12);
            bs.WriteFlag(bitDepthConstraint <= 10);
            bs.WriteFlag(bitDepthConstraint <= 8 && csp != X265_CSP_I422);
            bs.WriteFlag(csp == X265_CSP_I422 || csp == X265_CSP_I420 || csp == X265_CSP_I400);
            bs.WriteFlag(csp == X265_CSP_I420 || csp == X265_CSP_I400);
            bs.WriteFlag(csp == X265_CSP_I400);
            bs.WriteFlag(ptl.intraConstraintFlag);
            bs.WriteFlag(ptl.onePictureOnlyConstraintFlag);
            bs.WriteFlag(ptl.lowerBitRateConstraintFlag);
            bs.WriteCode(0, 16);
            bs.WriteCode(0, 16);
            bs.WriteCode(0, 3);
        }
        else
        {
            bs.WriteCode(0, 16);
            bs.WriteCode(0, 16);
            bs.WriteCode(0, 12);
        }

        bs.WriteCode((uint)ptl.levelIdc, 8);
        if (maxTempSubLayers > 1)
        {
            for (int i = 0; i < maxTempSubLayers - 1; i++)
            {
                bs.WriteFlag(false);
                bs.WriteFlag(false);
            }

            for (int i = maxTempSubLayers - 1; i < 8; i++)
            {
                bs.WriteCode(0, 2);
            }
        }
    }

    private void CodeVUI(X265Sps vui, int maxSubTLayers)
    {
        X265Bitstream bs = Bs;
        bs.WriteFlag(vui.aspectRatioInfoPresentFlag);
        if (vui.aspectRatioInfoPresentFlag)
        {
            bs.WriteCode((uint)vui.aspectRatioIdc, 8);
            if (vui.aspectRatioIdc == 255)
            {
                bs.WriteCode((uint)vui.sarWidth, 16);
                bs.WriteCode((uint)vui.sarHeight, 16);
            }
        }

        bs.WriteFlag(vui.overscanInfoPresentFlag);
        if (vui.overscanInfoPresentFlag)
        {
            bs.WriteFlag(vui.overscanAppropriateFlag);
        }

        bs.WriteFlag(vui.videoSignalTypePresentFlag);
        if (vui.videoSignalTypePresentFlag)
        {
            bs.WriteCode((uint)vui.videoFormat, 3);
            bs.WriteFlag(vui.videoFullRangeFlag);
            bs.WriteFlag(vui.colourDescriptionPresentFlag);
            if (vui.colourDescriptionPresentFlag)
            {
                bs.WriteCode((uint)vui.colourPrimaries, 8);
                bs.WriteCode((uint)vui.transferCharacteristics, 8);
                bs.WriteCode((uint)vui.matrixCoefficients, 8);
            }
        }

        bs.WriteFlag(vui.chromaLocInfoPresentFlag);
        bs.WriteFlag(false);                        // neutral_chroma_indication_flag
        bs.WriteFlag(vui.fieldSeqFlag);
        bs.WriteFlag(vui.frameFieldInfoPresentFlag);
        bs.WriteFlag(vui.defaultDisplayWindow.bEnabled);
        if (vui.defaultDisplayWindow.bEnabled)
        {
            bs.WriteUvlc((uint)vui.defaultDisplayWindow.leftOffset);
            bs.WriteUvlc((uint)vui.defaultDisplayWindow.rightOffset);
            bs.WriteUvlc((uint)vui.defaultDisplayWindow.topOffset);
            bs.WriteUvlc((uint)vui.defaultDisplayWindow.bottomOffset);
        }

        if (!vui.bEmitVUITimingInfo)
        {
            bs.WriteFlag(false);                    // vui_timing_info_present_flag
        }
        else
        {
            bs.WriteFlag(true);
            bs.WriteCode(vui.numUnitsInTick, 32);
            bs.WriteCode(vui.timeScale, 32);
            bs.WriteFlag(false);                    // vui_poc_proportional_to_timing_flag
            if (!vui.bEmitVUIHRDInfo)
            {
                bs.WriteFlag(false);
            }
            else
            {
                bs.WriteFlag(vui.hrdParametersPresentFlag);   // HRD parameters are only emitted with VBV
            }
        }

        bs.WriteFlag(false);                        // bitstream_restriction_flag
    }

    public void CodeSliceHeader(X265Slice slice, X265FrameData encData, uint sliceAddr, uint sliceAddrBits, int sliceQp)
    {
        X265Bitstream bs = Bs;
        bs.WriteFlag(sliceAddr == 0);               // first_slice_segment_in_pic_flag
        if (slice.GetRapPicFlag)
        {
            bs.WriteFlag(false);                    // no_output_of_prior_pics_flag
        }

        bs.WriteUvlc(0);                            // slice_pic_parameter_set_id
        if (sliceAddr != 0)
        {
            bs.WriteCode(sliceAddr, sliceAddrBits);
        }

        bs.WriteUvlc((uint)slice.m_sliceType);
        // IDR: no POC / RPS / temporal MVP flag
        X265SaoParam? saoParam = encData.m_saoParam;
        if (slice.m_bUseSao)
        {
            bs.WriteFlag(saoParam!.bSaoFlag[0]);
            if (encData.m_param.internalCsp != X265_CSP_I400)
            {
                bs.WriteFlag(saoParam.bSaoFlag[1]);
            }
        }
        else if (encData.m_param.selectiveSAO != 0)
        {
            bs.WriteFlag(false);
            if (encData.m_param.internalCsp != X265_CSP_I400)
            {
                bs.WriteFlag(false);
            }
        }

        int code = sliceQp - (slice.m_iPPSQpMinus26 + 26);
        bs.WriteSvlc(code);                         // slice_qp_delta
        if (slice.m_pps.pps_slice_chroma_qp_offsets_present_flag)
        {
            bs.WriteSvlc(slice.m_chromaQpOffset[0]);
            bs.WriteSvlc(slice.m_chromaQpOffset[1]);
        }

        if (encData.m_param.maxSlices <= 1)
        {
            bool isSAOEnabled = slice.m_sps.bUseSAO && slice.m_bUseSao && (saoParam!.bSaoFlag[0] || saoParam.bSaoFlag[1]);
            bool isDBFEnabled = !slice.m_pps.bPicDisableDeblockingFilter;
            if (isSAOEnabled || isDBFEnabled)
            {
                bs.WriteFlag(slice.m_sLFaseFlag);   // slice_loop_filter_across_slices_enabled_flag
            }
        }
    }

    /// <summary>The WPP substream sizes for the slice header.</summary>
    public void CodeSliceHeaderWPPEntryPoints(uint[] substreamSizes, uint numSubStreams, uint maxOffset)
    {
        X265Bitstream bs = Bs;
        uint offsetLen = 1;
        while (maxOffset >= (1u << (int)offsetLen))
        {
            offsetLen++;
        }

        bs.WriteUvlc(numSubStreams);                // num_entry_point_offsets
        if (numSubStreams > 0)
        {
            bs.WriteUvlc(offsetLen - 1);
        }

        for (uint i = 0; i < numSubStreams; i++)
        {
            bs.WriteCode(substreamSizes[i] - 1, offsetLen);
        }
    }
}
