namespace PictureSharp.Formats.Heif.Hevc;

// H.265 7.3.2.3 pic_parameter_set_rbsp. Tile geometry is resolved against the SPS in HevcPictureLayout.
internal sealed class HevcPictureParameterSet
{
    public int Id { get; private init; }
    public int SpsId { get; private init; }
    public bool DependentSliceSegmentsEnabled { get; private init; }
    public bool OutputFlagPresent { get; private init; }
    public int NumExtraSliceHeaderBits { get; private init; }
    public bool SignDataHidingEnabled { get; private init; }
    public bool CabacInitPresent { get; private init; }
    public int InitQp { get; private init; }
    public bool ConstrainedIntraPred { get; private init; }
    public bool TransformSkipEnabled { get; private init; }
    public bool CuQpDeltaEnabled { get; private init; }
    public int DiffCuQpDeltaDepth { get; private init; }
    public int CbQpOffset { get; private init; }
    public int CrQpOffset { get; private init; }
    public bool SliceChromaQpOffsetsPresent { get; private init; }
    public bool TransquantBypassEnabled { get; private init; }
    public bool TilesEnabled { get; private init; }
    public bool EntropyCodingSyncEnabled { get; private init; }
    public int NumTileColumns { get; private init; } = 1;
    public int NumTileRows { get; private init; } = 1;
    public bool UniformSpacing { get; private init; } = true;
    public int[] ColumnWidths { get; private init; } = [];
    public int[] RowHeights { get; private init; } = [];
    public bool LoopFilterAcrossTilesEnabled { get; private init; } = true;
    public bool LoopFilterAcrossSlicesEnabled { get; private init; }
    public bool DeblockingFilterOverrideEnabled { get; private init; }
    public bool DeblockingFilterDisabled { get; private init; }
    public int BetaOffsetDiv2 { get; private init; }
    public int TcOffsetDiv2 { get; private init; }
    public HevcScalingList? ScalingList { get; private init; }
    public bool ListsModificationPresent { get; private init; }
    public bool SliceSegmentHeaderExtensionPresent { get; private init; }

    public static HevcPictureParameterSet Parse(ReadOnlySpan<byte> rbsp)
    {
        var reader = new HevcBitReader(rbsp);
        int id = reader.ReadUe(63, "pps_pic_parameter_set_id");
        int spsId = reader.ReadUe(15, "pps_seq_parameter_set_id");
        bool dependentSlices = reader.ReadFlag();
        bool outputFlagPresent = reader.ReadFlag();
        int extraBits = (int)reader.ReadBits(3);
        bool signHiding = reader.ReadFlag();
        bool cabacInitPresent = reader.ReadFlag();
        reader.ReadUe(14, "num_ref_idx_l0_default_active_minus1");
        reader.ReadUe(14, "num_ref_idx_l1_default_active_minus1");
        int initQp = 26 + reader.ReadSe(-26 - 24, 25, "init_qp_minus26");
        bool constrainedIntraPred = reader.ReadFlag();
        bool transformSkip = reader.ReadFlag();
        bool cuQpDelta = reader.ReadFlag();
        int diffCuQpDeltaDepth = cuQpDelta ? reader.ReadUe(3, "diff_cu_qp_delta_depth") : 0;
        int cbQpOffset = reader.ReadSe(-12, 12, "pps_cb_qp_offset");
        int crQpOffset = reader.ReadSe(-12, 12, "pps_cr_qp_offset");
        bool sliceChromaQpOffsets = reader.ReadFlag();
        reader.Skip(2); // weighted_pred_flag, weighted_bipred_flag
        bool transquantBypass = reader.ReadFlag();
        bool tiles = reader.ReadFlag();
        bool entropySync = reader.ReadFlag();
        int columns = 1;
        int rows = 1;
        bool uniform = true;
        int[] widths = [];
        int[] heights = [];
        bool acrossTiles = true;
        if (tiles)
        {
            columns = reader.ReadUe(HevcSequenceParameterSet.MaxDimension / 16, "num_tile_columns_minus1") + 1;
            rows = reader.ReadUe(HevcSequenceParameterSet.MaxDimension / 16, "num_tile_rows_minus1") + 1;
            uniform = reader.ReadFlag();
            if (!uniform)
            {
                widths = new int[columns - 1];
                heights = new int[rows - 1];
                for (int i = 0; i < widths.Length; i++)
                {
                    widths[i] = reader.ReadUe(HevcSequenceParameterSet.MaxDimension / 16, "column_width_minus1") + 1;
                }

                for (int i = 0; i < heights.Length; i++)
                {
                    heights[i] = reader.ReadUe(HevcSequenceParameterSet.MaxDimension / 16, "row_height_minus1") + 1;
                }
            }

            acrossTiles = reader.ReadFlag();
        }

        bool acrossSlices = reader.ReadFlag();
        bool overrideEnabled = false;
        bool deblockingDisabled = false;
        int beta = 0;
        int tc = 0;
        if (reader.ReadFlag())
        {
            overrideEnabled = reader.ReadFlag();
            deblockingDisabled = reader.ReadFlag();
            if (!deblockingDisabled)
            {
                beta = reader.ReadSe(-6, 6, "pps_beta_offset_div2");
                tc = reader.ReadSe(-6, 6, "pps_tc_offset_div2");
            }
        }

        HevcScalingList? scalingList = reader.ReadFlag() ? HevcScalingList.Parse(ref reader) : null;
        bool listsModification = reader.ReadFlag();
        reader.ReadUe(); // log2_parallel_merge_level_minus2
        bool headerExtension = reader.ReadFlag();
        if (reader.ReadFlag())
        {
            bool range = reader.ReadFlag();
            bool multilayer = reader.ReadFlag();
            bool threeD = reader.ReadFlag();
            bool scc = reader.ReadFlag();
            reader.Skip(4);
            if (range)
            {
                bool defaults = (!transformSkip || reader.ReadUe() == 0) && !reader.ReadFlag() && !reader.ReadFlag()
                    && reader.ReadUe() == 0 && reader.ReadUe() == 0;
                if (!defaults)
                {
                    throw HevcError.Unsupported("picture range extension coding tools.");
                }
            }

            if (multilayer || threeD || scc)
            {
                throw HevcError.Unsupported("multilayer, 3D or screen content picture extensions.");
            }
        }

        return new HevcPictureParameterSet
        {
            Id = id,
            SpsId = spsId,
            DependentSliceSegmentsEnabled = dependentSlices,
            OutputFlagPresent = outputFlagPresent,
            NumExtraSliceHeaderBits = extraBits,
            SignDataHidingEnabled = signHiding,
            CabacInitPresent = cabacInitPresent,
            InitQp = initQp,
            ConstrainedIntraPred = constrainedIntraPred,
            TransformSkipEnabled = transformSkip,
            CuQpDeltaEnabled = cuQpDelta,
            DiffCuQpDeltaDepth = diffCuQpDeltaDepth,
            CbQpOffset = cbQpOffset,
            CrQpOffset = crQpOffset,
            SliceChromaQpOffsetsPresent = sliceChromaQpOffsets,
            TransquantBypassEnabled = transquantBypass,
            TilesEnabled = tiles,
            EntropyCodingSyncEnabled = entropySync,
            NumTileColumns = columns,
            NumTileRows = rows,
            UniformSpacing = uniform,
            ColumnWidths = widths,
            RowHeights = heights,
            LoopFilterAcrossTilesEnabled = acrossTiles,
            LoopFilterAcrossSlicesEnabled = acrossSlices,
            DeblockingFilterOverrideEnabled = overrideEnabled,
            DeblockingFilterDisabled = deblockingDisabled,
            BetaOffsetDiv2 = beta,
            TcOffsetDiv2 = tc,
            ScalingList = scalingList,
            ListsModificationPresent = listsModification,
            SliceSegmentHeaderExtensionPresent = headerExtension,
        };
    }
}
