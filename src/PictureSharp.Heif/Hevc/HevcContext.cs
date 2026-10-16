namespace PictureSharp.Formats.Heif.Hevc;

// Context variable layout and initialization values for I slices (initType 0).
// port: HM source/Lib/TLibCommon/ContextTables.h (I_SLICE rows)
internal static class HevcContext
{
    public const int SaoMerge = 0;
    public const int SaoTypeIdx = SaoMerge + 1;
    public const int SplitCuFlag = SaoTypeIdx + 1;
    public const int CuTransquantBypass = SplitCuFlag + 3;
    public const int PartMode = CuTransquantBypass + 1;
    public const int PrevIntraLumaPred = PartMode + 1;
    public const int IntraChromaPredMode = PrevIntraLumaPred + 1;
    public const int SplitTransformFlag = IntraChromaPredMode + 1;
    public const int CbfLuma = SplitTransformFlag + 3;
    public const int CbfChroma = CbfLuma + 2;
    public const int CuQpDeltaAbs = CbfChroma + 5;
    public const int TransformSkipLuma = CuQpDeltaAbs + 2;
    public const int TransformSkipChroma = TransformSkipLuma + 1;
    public const int LastXPrefix = TransformSkipChroma + 1;
    public const int LastYPrefix = LastXPrefix + 18;
    public const int CodedSubBlock = LastYPrefix + 18;
    public const int SigCoeffLuma = CodedSubBlock + 4;
    public const int SigCoeffChroma = SigCoeffLuma + 27;
    public const int Greater1 = SigCoeffChroma + 15;
    public const int Greater2 = Greater1 + 24;
    public const int Count = Greater2 + 6;

    public static void Initialize(Span<byte> states, int sliceQp)
    {
        ReadOnlySpan<byte> values = InitValues;
        for (int i = 0; i < Count; i++)
        {
            states[i] = HevcCabacDecoder.InitState(values[i], sliceQp);
        }
    }

    private static ReadOnlySpan<byte> InitValues =>
    [
        153, // sao_merge_left/up_flag
        200, // sao_type_idx
        139, 141, 157, // split_cu_flag
        154, // cu_transquant_bypass_flag
        184, // part_mode
        184, // prev_intra_luma_pred_flag
        63, // intra_chroma_pred_mode
        153, 138, 138, // split_transform_flag
        111, 141, // cbf_luma
        94, 138, 182, 154, 154, // cbf_cb, cbf_cr
        154, 154, // cu_qp_delta_abs
        139, 139, // transform_skip_flag luma, chroma
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_x_prefix
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_y_prefix
        91, 171, 134, 141, // coded_sub_block_flag
        111, 111, 125, 110, 110, 94, 124, 108, 124, 107, 125, 141, 179, 153, 125, 107, 125, 141, 179, 153, 125, 107,
        125, 141, 179, 153, 125, // sig_coeff_flag luma
        140, 139, 182, 182, 152, 136, 152, 136, 153, 136, 139, 111, 136, 139, 111, // sig_coeff_flag chroma
        140, 92, 137, 138, 140, 152, 138, 139, 153, 74, 149, 92, 139, 107, 122, 152, // greater1 luma
        140, 179, 166, 182, 140, 227, 122, 197, // greater1 chroma
        138, 153, 136, 167, 152, 152, // greater2 luma, chroma
    ];
}
