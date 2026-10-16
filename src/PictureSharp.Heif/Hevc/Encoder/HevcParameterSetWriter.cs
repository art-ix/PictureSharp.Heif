namespace PictureSharp.Formats.Heif.Hevc;

// Coding configuration of one encoded picture.
internal sealed record HevcEncoderSettings
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int ChromaFormatIdc { get; init; } = 1;
    public int Qp { get; init; } = 26;
    public int Log2CtbSize { get; init; } = 5;
    public bool SaoEnabled { get; init; }
    public bool SignDataHiding { get; init; }
    public bool TransformSkip { get; init; }

    // Disables the deblocking filter in the PPS (used by tests to compare reconstructions directly).
    public bool DeblockingDisabled { get; init; }
    public bool FullRange { get; init; } = true;
    public int MatrixCoefficients { get; init; } = 6;
    public int ColourPrimaries { get; init; } = 1;
    public int TransferCharacteristics { get; init; } = 13;

    // Coded size: the picture rounded up to the minimum coding block (8); the conformance window removes the padding.
    public int CodedWidth => (Width + 7) & ~7;
    public int CodedHeight => (Height + 7) & ~7;
}

// VPS, SPS, PPS and the slice segment header of a single intra (IDR) picture (H.265 7.3.2, 7.3.6).
internal static class HevcParameterSetWriter
{
    public const int IdrNalType = 20; // IDR_N_LP

    public static byte[] Vps(HevcEncoderSettings settings)
    {
        var w = new HevcBitWriter();
        w.Write(0, 4); // vps_video_parameter_set_id
        w.Write(1, 1); // vps_base_layer_internal_flag
        w.Write(1, 1); // vps_base_layer_available_flag
        w.Write(0, 6); // vps_max_layers_minus1
        w.Write(0, 3); // vps_max_sub_layers_minus1
        w.Write(1, 1); // vps_temporal_id_nesting_flag
        w.Write(0xffff, 16);
        ProfileTierLevel(w, settings);
        w.Write(1, 1); // vps_sub_layer_ordering_info_present_flag
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteUe(0);
        w.Write(0, 6); // vps_max_layer_id
        w.WriteUe(0); // vps_num_layer_sets_minus1
        w.Write(0, 1); // vps_timing_info_present_flag
        w.Write(0, 1); // vps_extension_flag
        w.WriteTrailingBits();
        return w.ToNalUnit(HevcNalHeader.VpsType);
    }

    public static byte[] Sps(HevcEncoderSettings settings)
    {
        var w = new HevcBitWriter();
        w.Write(0, 4); // sps_video_parameter_set_id
        w.Write(0, 3); // sps_max_sub_layers_minus1
        w.Write(1, 1); // sps_temporal_id_nesting_flag
        ProfileTierLevel(w, settings);
        w.WriteUe(0); // sps_seq_parameter_set_id
        w.WriteUe((uint)settings.ChromaFormatIdc);
        w.WriteUe((uint)settings.CodedWidth);
        w.WriteUe((uint)settings.CodedHeight);
        int sub = settings.ChromaFormatIdc == 1 ? 2 : 1;
        int padRight = settings.CodedWidth - settings.Width;
        int padBottom = settings.CodedHeight - settings.Height;
        bool window = padRight != 0 || padBottom != 0;
        w.WriteFlag(window);
        if (window)
        {
            w.WriteUe(0);
            w.WriteUe((uint)(padRight / sub));
            w.WriteUe(0);
            w.WriteUe((uint)(padBottom / sub));
        }

        w.WriteUe(0); // bit_depth_luma_minus8
        w.WriteUe(0); // bit_depth_chroma_minus8
        w.WriteUe(4); // log2_max_pic_order_cnt_lsb_minus4
        w.Write(1, 1); // sps_sub_layer_ordering_info_present_flag
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteUe(0); // log2_min_luma_coding_block_size_minus3
        w.WriteUe((uint)(settings.Log2CtbSize - 3));
        w.WriteUe(0); // log2_min_luma_transform_block_size_minus2
        w.WriteUe(3); // log2_diff_max_min_luma_transform_block_size (4..32)
        w.WriteUe(0); // max_transform_hierarchy_depth_inter
        w.WriteUe(0); // max_transform_hierarchy_depth_intra
        w.Write(0, 1); // scaling_list_enabled_flag
        w.Write(0, 1); // amp_enabled_flag
        w.WriteFlag(settings.SaoEnabled);
        w.Write(0, 1); // pcm_enabled_flag
        w.WriteUe(0); // num_short_term_ref_pic_sets
        w.Write(0, 1); // long_term_ref_pics_present_flag
        w.Write(0, 1); // sps_temporal_mvp_enabled_flag
        w.Write(1, 1); // strong_intra_smoothing_enabled_flag
        w.Write(1, 1); // vui_parameters_present_flag
        w.Write(0, 1); // aspect_ratio_info_present_flag
        w.Write(0, 1); // overscan_info_present_flag
        w.Write(1, 1); // video_signal_type_present_flag
        w.Write(5, 3); // video_format: unspecified
        w.WriteFlag(settings.FullRange);
        w.Write(1, 1); // colour_description_present_flag
        w.Write((uint)settings.ColourPrimaries, 8);
        w.Write((uint)settings.TransferCharacteristics, 8);
        w.Write((uint)settings.MatrixCoefficients, 8);
        w.Write(0, 1); // chroma_loc_info_present_flag
        w.Write(0, 3); // neutral_chroma_indication_flag, field_seq_flag, frame_field_info_present_flag
        w.Write(0, 1); // default_display_window_flag
        w.Write(0, 1); // vui_timing_info_present_flag
        w.Write(0, 1); // bitstream_restriction_flag
        w.Write(0, 1); // sps_extension_present_flag
        w.WriteTrailingBits();
        return w.ToNalUnit(HevcNalHeader.SpsType);
    }

    public static byte[] Pps(HevcEncoderSettings settings)
    {
        var w = new HevcBitWriter();
        w.WriteUe(0); // pps_pic_parameter_set_id
        w.WriteUe(0); // pps_seq_parameter_set_id
        w.Write(0, 1); // dependent_slice_segments_enabled_flag
        w.Write(0, 1); // output_flag_present_flag
        w.Write(0, 3); // num_extra_slice_header_bits
        w.WriteFlag(settings.SignDataHiding);
        w.Write(0, 1); // cabac_init_present_flag
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteSe(settings.Qp - 26); // init_qp_minus26
        w.Write(0, 1); // constrained_intra_pred_flag
        w.WriteFlag(settings.TransformSkip);
        w.Write(0, 1); // cu_qp_delta_enabled_flag
        w.WriteSe(0); // pps_cb_qp_offset
        w.WriteSe(0); // pps_cr_qp_offset
        w.Write(0, 1); // pps_slice_chroma_qp_offsets_present_flag
        w.Write(0, 2); // weighted_pred_flag, weighted_bipred_flag
        w.Write(0, 1); // transquant_bypass_enabled_flag
        w.Write(0, 1); // tiles_enabled_flag
        w.Write(0, 1); // entropy_coding_sync_enabled_flag
        w.Write(0, 1); // pps_loop_filter_across_slices_enabled_flag
        w.WriteFlag(settings.DeblockingDisabled); // deblocking_filter_control_present_flag
        if (settings.DeblockingDisabled)
        {
            w.Write(0, 1); // deblocking_filter_override_enabled_flag
            w.Write(1, 1); // pps_deblocking_filter_disabled_flag
        }

        w.Write(0, 1); // pps_scaling_list_data_present_flag
        w.Write(0, 1); // lists_modification_present_flag
        w.WriteUe(0); // log2_parallel_merge_level_minus2
        w.Write(0, 1); // slice_segment_header_extension_present_flag
        w.Write(0, 1); // pps_extension_present_flag
        w.WriteTrailingBits();
        return w.ToNalUnit(HevcNalHeader.PpsType);
    }

    // Slice segment header of the single IDR slice; slice_segment_data follows from the returned writer.
    public static HevcBitWriter SliceHeader(HevcEncoderSettings settings)
    {
        var w = new HevcBitWriter();
        w.Write(1, 1); // first_slice_segment_in_pic_flag
        w.Write(0, 1); // no_output_of_prior_pics_flag
        w.WriteUe(0); // slice_pic_parameter_set_id
        w.WriteUe(2); // slice_type: I
        if (settings.SaoEnabled)
        {
            w.Write(1, 1); // slice_sao_luma_flag
            if (settings.ChromaFormatIdc != 0)
            {
                w.Write(1, 1); // slice_sao_chroma_flag
            }
        }

        w.WriteSe(0); // slice_qp_delta
        w.WriteTrailingBits(); // byte_alignment()
        return w;
    }

    // profile_tier_level(1, 0): Main for 4:2:0, the RExt Monochrome profile for 4:0:0; level from the picture size.
    private static void ProfileTierLevel(HevcBitWriter w, HevcEncoderSettings settings)
    {
        bool monochrome = settings.ChromaFormatIdc == 0;
        w.Write(0, 2); // general_profile_space
        w.Write(0, 1); // general_tier_flag
        w.Write(monochrome ? 4u : 1u, 5);
        w.Write(monochrome ? 1u << (31 - 4) : (1u << 30) | (1u << 29), 32); // compatibility flags
        w.Write(1, 1); // general_progressive_source_flag
        w.Write(0, 1); // general_interlaced_source_flag
        w.Write(0, 1); // general_non_packed_constraint_flag
        w.Write(1, 1); // general_frame_only_constraint_flag
        if (monochrome)
        {
            // max_12bit, max_10bit, max_8bit, max_422chroma, max_420chroma, max_monochrome, intra, one_picture_only,
            // lower_bit_rate: the Monochrome profile.
            w.Write(0b111111001, 9);
            w.Write(0, 32);
            w.Write(0, 2);
        }
        else
        {
            w.Write(0, 32);
            w.Write(0, 11);
        }

        w.Write(0, 1); // general_inbld_flag / reserved
        long samples = (long)settings.CodedWidth * settings.CodedHeight;
        int level = samples <= 36864 ? 30 : samples <= 122880 ? 60 : samples <= 245760 ? 63 : samples <= 552960 ? 90
            : samples <= 983040 ? 93 : samples <= 2228224 ? 120 : samples <= 8912896 ? 150 : 180;
        w.Write((uint)level, 8);
    }
}
