namespace PictureSharp.Formats.Heif.Hevc;

// H.265 7.3.2.2 seq_parameter_set_rbsp, restricted to what intra decoding needs.
internal sealed class HevcSequenceParameterSet
{
    public const int MaxDimension = 16888; // level 6.2 maximum; also bounds every derived array.

    public int Id { get; private init; }
    public int ChromaFormatIdc { get; private init; }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int ConformanceLeft { get; private init; }
    public int ConformanceRight { get; private init; }
    public int ConformanceTop { get; private init; }
    public int ConformanceBottom { get; private init; }
    public int BitDepthLuma { get; private init; }
    public int BitDepthChroma { get; private init; }
    public int Log2MaxPicOrderCntLsb { get; private init; }
    public int Log2MinCbSize { get; private init; }
    public int Log2CtbSize { get; private init; }
    public int Log2MinTbSize { get; private init; }
    public int Log2MaxTbSize { get; private init; }
    public int MaxTransformHierarchyDepthIntra { get; private init; }
    public bool ScalingListEnabled { get; private init; }
    public HevcScalingList? ScalingList { get; private init; }
    public bool AmpEnabled { get; private init; }
    public bool SaoEnabled { get; private init; }
    public bool PcmEnabled { get; private init; }
    public int PcmBitDepthLuma { get; private init; }
    public int PcmBitDepthChroma { get; private init; }
    public int Log2MinPcmCbSize { get; private init; }
    public int Log2MaxPcmCbSize { get; private init; }
    public bool PcmLoopFilterDisabled { get; private init; }
    public int NumShortTermRefPicSets { get; private init; }
    public int[] NumDeltaPocs { get; private init; } = [];
    public bool LongTermRefPicsPresent { get; private init; }
    public int NumLongTermRefPicsSps { get; private init; }
    public bool TemporalMvpEnabled { get; private init; }
    public bool StrongIntraSmoothingEnabled { get; private init; }
    public HevcColourDescription? Colour { get; private init; }

    public int SubWidthC => ChromaFormatIdc == 1 ? 2 : 1;
    public int SubHeightC => ChromaFormatIdc == 1 ? 2 : 1;
    public int CtbSize => 1 << Log2CtbSize;
    public int PicWidthInCtbs => (Width + CtbSize - 1) >> Log2CtbSize;
    public int PicHeightInCtbs => (Height + CtbSize - 1) >> Log2CtbSize;
    public int QpBdOffsetY => 6 * (BitDepthLuma - 8);
    public int QpBdOffsetC => 6 * (BitDepthChroma - 8);

    public static HevcSequenceParameterSet Parse(ReadOnlySpan<byte> rbsp)
    {
        var reader = new HevcBitReader(rbsp);
        reader.Skip(4); // sps_video_parameter_set_id
        int maxSubLayersMinus1 = (int)reader.ReadBits(3);
        if (maxSubLayersMinus1 > 6)
        {
            throw HevcError.Invalid("sps_max_sub_layers_minus1 out of range.");
        }

        reader.Skip(1); // sps_temporal_id_nesting_flag
        SkipProfileTierLevel(ref reader, maxSubLayersMinus1);
        int id = reader.ReadUe(15, "sps_seq_parameter_set_id");
        int chromaFormatIdc = reader.ReadUe(3, "chroma_format_idc");
        if (chromaFormatIdc == 3 && reader.ReadFlag())
        {
            throw HevcError.Unsupported("separate colour planes.");
        }

        if (chromaFormatIdc > 1)
        {
            throw HevcError.Unsupported("4:2:2 and 4:4:4 chroma (range extensions).");
        }

        int width = reader.ReadUe(MaxDimension, "pic_width_in_luma_samples");
        int height = reader.ReadUe(MaxDimension, "pic_height_in_luma_samples");
        if (width == 0 || height == 0)
        {
            throw HevcError.Invalid("zero picture dimension.");
        }

        int left = 0;
        int right = 0;
        int top = 0;
        int bottom = 0;
        if (reader.ReadFlag())
        {
            left = reader.ReadUe(MaxDimension, "conf_win_left_offset");
            right = reader.ReadUe(MaxDimension, "conf_win_right_offset");
            top = reader.ReadUe(MaxDimension, "conf_win_top_offset");
            bottom = reader.ReadUe(MaxDimension, "conf_win_bottom_offset");
        }

        int subWidth = chromaFormatIdc == 1 ? 2 : 1;
        int subHeight = chromaFormatIdc == 1 ? 2 : 1;
        if (subWidth * (left + right) >= width || subHeight * (top + bottom) >= height)
        {
            throw HevcError.Invalid("conformance window is empty.");
        }

        int bitDepthLuma = reader.ReadUe(8, "bit_depth_luma_minus8") + 8;
        int bitDepthChroma = reader.ReadUe(8, "bit_depth_chroma_minus8") + 8;
        if (bitDepthLuma > 10 || (chromaFormatIdc != 0 && bitDepthChroma > 10))
        {
            throw HevcError.Unsupported("bit depths above 10.");
        }

        int log2MaxPocLsb = reader.ReadUe(12, "log2_max_pic_order_cnt_lsb_minus4") + 4;
        bool subLayerOrdering = reader.ReadFlag();
        for (int i = subLayerOrdering ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
        {
            reader.ReadUe(); // sps_max_dec_pic_buffering_minus1
            reader.ReadUe(); // sps_max_num_reorder_pics
            reader.ReadUe(); // sps_max_latency_increase_plus1
        }

        int log2MinCb = reader.ReadUe(3, "log2_min_luma_coding_block_size_minus3") + 3;
        int log2Ctb = log2MinCb + reader.ReadUe(3, "log2_diff_max_min_luma_coding_block_size");
        int log2MinTb = reader.ReadUe(3, "log2_min_luma_transform_block_size_minus2") + 2;
        int log2MaxTb = log2MinTb + reader.ReadUe(3, "log2_diff_max_min_luma_transform_block_size");
        if (log2Ctb is < 4 or > 6 || log2MinTb >= log2MinCb || log2MaxTb > Math.Min(log2Ctb, 5))
        {
            throw HevcError.Invalid("invalid coding or transform block sizes.");
        }

        if ((width & ((1 << log2MinCb) - 1)) != 0 || (height & ((1 << log2MinCb) - 1)) != 0)
        {
            throw HevcError.Invalid("picture size is not a multiple of the minimum coding block size.");
        }

        reader.ReadUe(log2Ctb - log2MinTb, "max_transform_hierarchy_depth_inter");
        int maxDepthIntra = reader.ReadUe(log2Ctb - log2MinTb, "max_transform_hierarchy_depth_intra");
        bool scalingListEnabled = reader.ReadFlag();
        HevcScalingList? scalingList = null;
        if (scalingListEnabled)
        {
            scalingList = reader.ReadFlag() ? HevcScalingList.Parse(ref reader) : HevcScalingList.CreateDefault();
        }

        bool amp = reader.ReadFlag();
        bool sao = reader.ReadFlag();
        bool pcm = reader.ReadFlag();
        int pcmBitDepthLuma = 0;
        int pcmBitDepthChroma = 0;
        int log2MinPcm = 0;
        int log2MaxPcm = 0;
        bool pcmLoopFilterDisabled = false;
        if (pcm)
        {
            pcmBitDepthLuma = (int)reader.ReadBits(4) + 1;
            pcmBitDepthChroma = (int)reader.ReadBits(4) + 1;
            log2MinPcm = reader.ReadUe(2, "log2_min_pcm_luma_coding_block_size_minus3") + 3;
            log2MaxPcm = log2MinPcm + reader.ReadUe(2, "log2_diff_max_min_pcm_luma_coding_block_size");
            pcmLoopFilterDisabled = reader.ReadFlag();
            if (pcmBitDepthLuma > bitDepthLuma || pcmBitDepthChroma > bitDepthChroma || log2MaxPcm > Math.Min(log2Ctb, 5)
                || log2MinPcm < log2MinCb)
            {
                throw HevcError.Invalid("invalid PCM parameters.");
            }
        }

        int numShortTermRefPicSets = reader.ReadUe(64, "num_short_term_ref_pic_sets");
        int[] numDeltaPocs = new int[numShortTermRefPicSets];
        for (int i = 0; i < numShortTermRefPicSets; i++)
        {
            numDeltaPocs[i] = ParseShortTermRefPicSet(ref reader, i, numShortTermRefPicSets, numDeltaPocs);
        }

        bool longTerm = reader.ReadFlag();
        int numLongTerm = 0;
        if (longTerm)
        {
            numLongTerm = reader.ReadUe(32, "num_long_term_ref_pics_sps");
            for (int i = 0; i < numLongTerm; i++)
            {
                reader.Skip(log2MaxPocLsb + 1); // lt_ref_pic_poc_lsb_sps, used_by_curr_pic_lt_sps_flag
            }
        }

        bool temporalMvp = reader.ReadFlag();
        bool strongIntraSmoothing = reader.ReadFlag();
        HevcColourDescription? colour = null;
        if (reader.ReadFlag())
        {
            colour = ParseVui(ref reader, maxSubLayersMinus1);
        }

        if (reader.ReadFlag()) // sps_extension_present_flag
        {
            bool range = reader.ReadFlag();
            bool multilayer = reader.ReadFlag();
            bool threeD = reader.ReadFlag();
            bool scc = reader.ReadFlag();
            reader.Skip(4); // sps_extension_4bits: extension data is ignored by decoders of this version
            if (range && reader.ReadBits(9) != 0)
            {
                throw HevcError.Unsupported("range extension coding tools.");
            }

            if (multilayer)
            {
                reader.Skip(1); // inter_view_mv_vert_constraint_flag
            }

            if (threeD || scc)
            {
                throw HevcError.Unsupported("3D or screen content coding extensions.");
            }
        }

        return new HevcSequenceParameterSet
        {
            Id = id,
            ChromaFormatIdc = chromaFormatIdc,
            Width = width,
            Height = height,
            ConformanceLeft = left,
            ConformanceRight = right,
            ConformanceTop = top,
            ConformanceBottom = bottom,
            BitDepthLuma = bitDepthLuma,
            BitDepthChroma = chromaFormatIdc == 0 ? bitDepthLuma : bitDepthChroma,
            Log2MaxPicOrderCntLsb = log2MaxPocLsb,
            Log2MinCbSize = log2MinCb,
            Log2CtbSize = log2Ctb,
            Log2MinTbSize = log2MinTb,
            Log2MaxTbSize = log2MaxTb,
            MaxTransformHierarchyDepthIntra = maxDepthIntra,
            ScalingListEnabled = scalingListEnabled,
            ScalingList = scalingList,
            AmpEnabled = amp,
            SaoEnabled = sao,
            PcmEnabled = pcm,
            PcmBitDepthLuma = pcmBitDepthLuma,
            PcmBitDepthChroma = pcmBitDepthChroma,
            Log2MinPcmCbSize = log2MinPcm,
            Log2MaxPcmCbSize = log2MaxPcm,
            PcmLoopFilterDisabled = pcmLoopFilterDisabled,
            NumShortTermRefPicSets = numShortTermRefPicSets,
            NumDeltaPocs = numDeltaPocs,
            LongTermRefPicsPresent = longTerm,
            NumLongTermRefPicsSps = numLongTerm,
            TemporalMvpEnabled = temporalMvp,
            StrongIntraSmoothingEnabled = strongIntraSmoothing,
            Colour = colour,
        };
    }

    // 7.3.7 st_ref_pic_set; returns NumDeltaPocs. Only the counts matter for parsing (7.4.8).
    public static int ParseShortTermRefPicSet(ref HevcBitReader reader, int index, int count, ReadOnlySpan<int> numDeltaPocs)
    {
        if (index != 0 && reader.ReadFlag())
        {
            int delta = index == count ? reader.ReadUe(index - 1, "delta_idx_minus1") + 1 : 1;
            int reference = index - delta;
            reader.Skip(1); // delta_rps_sign
            reader.ReadUe(32767, "abs_delta_rps_minus1");
            int total = 0;
            for (int j = 0; j <= numDeltaPocs[reference]; j++)
            {
                bool used = reader.ReadFlag();
                if (used || reader.ReadFlag())
                {
                    total++;
                }
            }

            return CheckDeltaPocs(total);
        }

        int negative = reader.ReadUe(16, "num_negative_pics");
        int positive = reader.ReadUe(16, "num_positive_pics");
        for (int i = 0; i < negative + positive; i++)
        {
            reader.ReadUe(32767, "delta_poc_minus1");
            reader.Skip(1);
        }

        return CheckDeltaPocs(negative + positive);
    }

    private static int CheckDeltaPocs(int total) =>
        total <= 16 ? total : throw HevcError.Invalid("too many pictures in a reference picture set.");

    // 7.3.3 profile_tier_level with profilePresentFlag equal to 1.
    public static void SkipProfileTierLevel(ref HevcBitReader reader, int maxSubLayersMinus1)
    {
        reader.Skip(96);
        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = reader.ReadFlag();
            levelPresent[i] = reader.ReadFlag();
        }

        if (maxSubLayersMinus1 > 0)
        {
            reader.Skip(2 * (8 - maxSubLayersMinus1));
        }

        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            reader.Skip((profilePresent[i] ? 88 : 0) + (levelPresent[i] ? 8 : 0));
        }
    }

    // E.2.1 vui_parameters; only the colour description is kept.
    private static HevcColourDescription? ParseVui(ref HevcBitReader reader, int maxSubLayersMinus1)
    {
        if (reader.ReadFlag() && reader.ReadBits(8) == 255)
        {
            reader.Skip(32); // sar_width, sar_height
        }

        if (reader.ReadFlag())
        {
            reader.Skip(1); // overscan_appropriate_flag
        }

        HevcColourDescription? colour = null;
        if (reader.ReadFlag())
        {
            reader.Skip(3); // video_format
            bool fullRange = reader.ReadFlag();
            colour = reader.ReadFlag()
                ? new HevcColourDescription((int)reader.ReadBits(8), (int)reader.ReadBits(8), (int)reader.ReadBits(8), fullRange)
                : new HevcColourDescription(2, 2, 2, fullRange);
        }

        if (reader.ReadFlag())
        {
            reader.ReadUe();
            reader.ReadUe();
        }

        reader.Skip(3); // neutral_chroma_indication_flag, field_seq_flag, frame_field_info_present_flag
        if (reader.ReadFlag())
        {
            for (int i = 0; i < 4; i++)
            {
                reader.ReadUe(); // default display window offsets
            }
        }

        if (reader.ReadFlag())
        {
            reader.Skip(64); // num_units_in_tick, time_scale
            if (reader.ReadFlag())
            {
                reader.ReadUe(); // num_ticks_poc_diff_one_minus1
            }

            if (reader.ReadFlag())
            {
                SkipHrdParameters(ref reader, true, maxSubLayersMinus1);
            }
        }

        if (reader.ReadFlag())
        {
            reader.Skip(3);
            for (int i = 0; i < 5; i++)
            {
                reader.ReadUe();
            }
        }

        return colour;
    }

    // E.2.2 hrd_parameters
    public static void SkipHrdParameters(ref HevcBitReader reader, bool commonInfPresent, int maxSubLayersMinus1)
    {
        bool nal = false;
        bool vcl = false;
        bool subPic = false;
        if (commonInfPresent)
        {
            nal = reader.ReadFlag();
            vcl = reader.ReadFlag();
            if (nal || vcl)
            {
                subPic = reader.ReadFlag();
                if (subPic)
                {
                    reader.Skip(8 + 5 + 1 + 5);
                }

                reader.Skip(8); // bit_rate_scale, cpb_size_scale
                if (subPic)
                {
                    reader.Skip(4);
                }

                reader.Skip(15);
            }
        }

        for (int i = 0; i <= maxSubLayersMinus1; i++)
        {
            bool fixedGeneral = reader.ReadFlag();
            bool fixedWithinCvs = fixedGeneral || reader.ReadFlag();
            bool lowDelay = false;
            if (fixedWithinCvs)
            {
                reader.ReadUe(); // elemental_duration_in_tc_minus1
            }
            else
            {
                lowDelay = reader.ReadFlag();
            }

            int cpbCount = lowDelay ? 1 : reader.ReadUe(31, "cpb_cnt_minus1") + 1;
            for (int k = (nal ? 1 : 0) + (vcl ? 1 : 0); k > 0; k--)
            {
                for (int j = 0; j < cpbCount; j++)
                {
                    reader.ReadUe();
                    reader.ReadUe();
                    if (subPic)
                    {
                        reader.ReadUe();
                        reader.ReadUe();
                    }

                    reader.Skip(1);
                }
            }
        }
    }
}

// E.3.1 colour description from the VUI (2 means unspecified).
internal readonly record struct HevcColourDescription(int Primaries, int Transfer, int Matrix, bool FullRange);
