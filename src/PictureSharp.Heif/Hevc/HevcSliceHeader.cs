namespace PictureSharp.Formats.Heif.Hevc;

// H.265 7.3.6.1 slice_segment_header for intra slices.
internal sealed class HevcSliceHeader
{
    public bool FirstSliceSegmentInPic { get; private init; }
    public int PpsId { get; private init; }
    public bool Dependent { get; private init; }
    public int SegmentAddress { get; private init; }
    public bool SaoLuma { get; private init; }
    public bool SaoChroma { get; private init; }
    public int SliceQpY { get; private init; }
    public int CbQpOffset { get; private init; }
    public int CrQpOffset { get; private init; }
    public bool DeblockingFilterDisabled { get; private init; }
    public int BetaOffsetDiv2 { get; private init; }
    public int TcOffsetDiv2 { get; private init; }
    public bool LoopFilterAcrossSlices { get; private init; }

    // Raster address of the first CTB of the independent slice this segment belongs to (SliceAddrRs).
    public int SliceAddress { get; private init; }

    // Byte offset of slice_segment_data within the RBSP.
    public int DataOffset { get; private init; }

    public static int ReadPpsId(ReadOnlySpan<byte> rbsp, HevcNalHeader nal)
    {
        var reader = new HevcBitReader(rbsp);
        reader.Skip(nal.IsIrap ? 2 : 1);
        return reader.ReadUe(63, "slice_pic_parameter_set_id");
    }

    public static HevcSliceHeader Parse(ReadOnlySpan<byte> rbsp, HevcNalHeader nal, HevcSequenceParameterSet sps,
        HevcPictureParameterSet pps, HevcPictureLayout layout, HevcSliceHeader? previous)
    {
        var reader = new HevcBitReader(rbsp);
        bool first = reader.ReadFlag();
        if (nal.IsIrap)
        {
            reader.Skip(1); // no_output_of_prior_pics_flag
        }

        int ppsId = reader.ReadUe(63, "slice_pic_parameter_set_id");
        bool dependent = false;
        int address = 0;
        if (!first)
        {
            if (pps.DependentSliceSegmentsEnabled)
            {
                dependent = reader.ReadFlag();
            }

            int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(layout.CtbCount - 1));
            address = (int)reader.ReadBits(bits);
            if (address == 0 || address >= layout.CtbCount)
            {
                throw HevcError.Invalid("slice_segment_address out of range.");
            }
        }

        if (dependent)
        {
            if (previous is null)
            {
                throw HevcError.Invalid("dependent slice segment without a preceding slice.");
            }

            return new HevcSliceHeader
            {
                FirstSliceSegmentInPic = first,
                PpsId = ppsId,
                Dependent = true,
                SegmentAddress = address,
                SliceAddress = previous.SliceAddress,
                SaoLuma = previous.SaoLuma,
                SaoChroma = previous.SaoChroma,
                SliceQpY = previous.SliceQpY,
                CbQpOffset = previous.CbQpOffset,
                CrQpOffset = previous.CrQpOffset,
                DeblockingFilterDisabled = previous.DeblockingFilterDisabled,
                BetaOffsetDiv2 = previous.BetaOffsetDiv2,
                TcOffsetDiv2 = previous.TcOffsetDiv2,
                LoopFilterAcrossSlices = previous.LoopFilterAcrossSlices,
                DataOffset = ReadTail(ref reader, pps, layout),
            };
        }

        reader.Skip(pps.NumExtraSliceHeaderBits);
        int sliceType = reader.ReadUe(2, "slice_type");
        if (sliceType != 2)
        {
            throw HevcError.Unsupported("inter-predicted (P or B) slices.");
        }

        if (pps.OutputFlagPresent)
        {
            reader.Skip(1);
        }

        if (!nal.IsIdr)
        {
            reader.Skip(sps.Log2MaxPicOrderCntLsb);
            if (!reader.ReadFlag())
            {
                HevcSequenceParameterSet.ParseShortTermRefPicSet(ref reader, sps.NumShortTermRefPicSets,
                    sps.NumShortTermRefPicSets, sps.NumDeltaPocs);
            }
            else if (sps.NumShortTermRefPicSets > 1)
            {
                reader.Skip(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(sps.NumShortTermRefPicSets - 1)));
            }
            else if (sps.NumShortTermRefPicSets == 0)
            {
                throw HevcError.Invalid("short_term_ref_pic_set_sps_flag without SPS sets.");
            }

            if (sps.LongTermRefPicsPresent)
            {
                int fromSps = sps.NumLongTermRefPicsSps > 0 ? reader.ReadUe(sps.NumLongTermRefPicsSps, "num_long_term_sps") : 0;
                int pictures = reader.ReadUe(32, "num_long_term_pics");
                int indexBits = sps.NumLongTermRefPicsSps > 1
                    ? 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(sps.NumLongTermRefPicsSps - 1))
                    : 0;
                for (int i = 0; i < fromSps + pictures; i++)
                {
                    reader.Skip(i < fromSps ? indexBits : sps.Log2MaxPicOrderCntLsb + 1);
                    if (reader.ReadFlag())
                    {
                        reader.ReadUe(); // delta_poc_msb_cycle_lt
                    }
                }
            }

            if (sps.TemporalMvpEnabled)
            {
                reader.Skip(1); // slice_temporal_mvp_enabled_flag
            }
        }

        bool saoLuma = false;
        bool saoChroma = false;
        if (sps.SaoEnabled)
        {
            saoLuma = reader.ReadFlag();
            if (sps.ChromaFormatIdc != 0)
            {
                saoChroma = reader.ReadFlag();
            }
        }

        int qp = pps.InitQp + reader.ReadSe(-128, 128, "slice_qp_delta");
        if (qp < -sps.QpBdOffsetY || qp > 51)
        {
            throw HevcError.Invalid("SliceQpY out of range.");
        }

        int cb = 0;
        int cr = 0;
        if (pps.SliceChromaQpOffsetsPresent)
        {
            cb = reader.ReadSe(-12, 12, "slice_cb_qp_offset");
            cr = reader.ReadSe(-12, 12, "slice_cr_qp_offset");
            if (Math.Abs(cb + pps.CbQpOffset) > 12 || Math.Abs(cr + pps.CrQpOffset) > 12)
            {
                throw HevcError.Invalid("chroma QP offsets out of range.");
            }
        }

        bool deblockingDisabled = pps.DeblockingFilterDisabled;
        int beta = pps.BetaOffsetDiv2;
        int tc = pps.TcOffsetDiv2;
        if (pps.DeblockingFilterOverrideEnabled && reader.ReadFlag())
        {
            deblockingDisabled = reader.ReadFlag();
            if (!deblockingDisabled)
            {
                beta = reader.ReadSe(-6, 6, "slice_beta_offset_div2");
                tc = reader.ReadSe(-6, 6, "slice_tc_offset_div2");
            }
        }

        bool acrossSlices = pps.LoopFilterAcrossSlicesEnabled;
        if (pps.LoopFilterAcrossSlicesEnabled && (saoLuma || saoChroma || !deblockingDisabled))
        {
            acrossSlices = reader.ReadFlag();
        }

        return new HevcSliceHeader
        {
            FirstSliceSegmentInPic = first,
            PpsId = ppsId,
            SegmentAddress = address,
            SliceAddress = address,
            SaoLuma = saoLuma,
            SaoChroma = saoChroma,
            SliceQpY = qp,
            CbQpOffset = cb,
            CrQpOffset = cr,
            DeblockingFilterDisabled = deblockingDisabled,
            BetaOffsetDiv2 = beta,
            TcOffsetDiv2 = tc,
            LoopFilterAcrossSlices = acrossSlices,
            DataOffset = ReadTail(ref reader, pps, layout),
        };
    }

    // Entry points, header extension and byte_alignment(). Substreams are decoded sequentially, so the entry point
    // offsets are only skipped.
    private static int ReadTail(ref HevcBitReader reader, HevcPictureParameterSet pps, HevcPictureLayout layout)
    {
        if (pps.TilesEnabled || pps.EntropyCodingSyncEnabled)
        {
            int count = reader.ReadUe(layout.CtbCount, "num_entry_point_offsets");
            if (count > 0)
            {
                int length = reader.ReadUe(31, "offset_len_minus1") + 1;
                reader.Skip(checked(count * length));
            }
        }

        if (pps.SliceSegmentHeaderExtensionPresent)
        {
            reader.Skip(reader.ReadUe(256, "slice_segment_header_extension_length") * 8);
        }

        if (!reader.ReadFlag())
        {
            throw HevcError.Invalid("missing alignment_bit_equal_to_one.");
        }

        while (!reader.IsByteAligned)
        {
            if (reader.ReadFlag())
            {
                throw HevcError.Invalid("nonzero alignment bit.");
            }
        }

        return reader.BitPosition >> 3;
    }
}
