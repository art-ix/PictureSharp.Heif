namespace PictureSharp.Formats.Heif.Hevc;

// CABAC syntax elements of intra coding units, the mirror of HevcPictureDecoder's parser (H.265 7.3.8, 9.3.4.2).
// port: HM source/Lib/TLibEncoder/TEncSbac.cpp codeSplitFlag, codePartSize, codeIntraDirLumaAng, codeIntraDirChroma,
//       codeQtCbf, codeLastSignificantXY, codeCoeffNxN, xWriteCoefRemainExGolomb
internal static class HevcSyntaxWriter
{
    private static ReadOnlySpan<byte> GroupIndex =>
        [0, 1, 2, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9];

    private static ReadOnlySpan<byte> MinInGroup => [0, 1, 2, 3, 4, 6, 8, 12, 16, 24];

    private static ReadOnlySpan<byte> ContextIndexMap4x4 => [0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8];

    // 7.3.8.3 sao (port: HM TEncSbac::codeSAOBlkParam, codeSaoMaxUvlc, codeSaoTypeIdx) for 8-bit samples.
    public static void Sao(HevcCabacWriter writer, Span<byte> contexts, in HevcSaoParameters sao, bool leftAvailable,
        bool upAvailable, int components)
    {
        if (leftAvailable)
        {
            writer.EncodeBin(ref contexts[HevcContext.SaoMerge], sao.MergeLeft ? 1 : 0);
            if (sao.MergeLeft)
            {
                return;
            }
        }

        if (upAvailable)
        {
            writer.EncodeBin(ref contexts[HevcContext.SaoMerge], sao.MergeUp ? 1 : 0);
            if (sao.MergeUp)
            {
                return;
            }
        }

        for (int c = 0; c < components; c++)
        {
            int type = sao.Type(c);
            if (c != 2)
            {
                writer.EncodeBin(ref contexts[HevcContext.SaoTypeIdx], type != 0 ? 1 : 0);
                if (type != 0)
                {
                    writer.EncodeBypass(type == 1 ? 0 : 1);
                }
            }

            if (type == 0)
            {
                continue;
            }

            const int maximum = 7;
            for (int i = 0; i < 4; i++)
            {
                int magnitude = Math.Abs(sao.Offset(c, i));
                for (int k = 0; k < magnitude; k++)
                {
                    writer.EncodeBypass(1);
                }

                if (magnitude < maximum)
                {
                    writer.EncodeBypass(0);
                }
            }

            if (type == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    int offset = sao.Offset(c, i);
                    if (offset != 0)
                    {
                        writer.EncodeBypass(offset < 0 ? 1 : 0);
                    }
                }

                writer.EncodeBypassBits((uint)sao.Class(c), 5);
            }
            else if (c != 2)
            {
                writer.EncodeBypassBits((uint)sao.Class(c), 2);
            }
        }
    }

    public static void SplitCuFlag(HevcCabacWriter writer, Span<byte> contexts, bool split, int ctxInc) =>
        writer.EncodeBin(ref contexts[HevcContext.SplitCuFlag + ctxInc], split ? 1 : 0);

    public static void PartModeIntra(HevcCabacWriter writer, Span<byte> contexts, bool partNxN) =>
        writer.EncodeBin(ref contexts[HevcContext.PartMode], partNxN ? 0 : 1);

    // prev_intra_luma_pred_flag for every prediction block, then mpm_idx or rem_intra_luma_pred_mode for each.
    public static void IntraLumaModes(HevcCabacWriter writer, Span<byte> contexts, ReadOnlySpan<int> modes,
        ReadOnlySpan<int> candidates)
    {
        int parts = modes.Length;
        Span<int> mpm = stackalloc int[4];
        for (int j = 0; j < parts; j++)
        {
            ReadOnlySpan<int> c = candidates.Slice(j * 3, 3);
            mpm[j] = c[0] == modes[j] ? 0 : c[1] == modes[j] ? 1 : c[2] == modes[j] ? 2 : -1;
            writer.EncodeBin(ref contexts[HevcContext.PrevIntraLumaPred], mpm[j] >= 0 ? 1 : 0);
        }

        Span<int> sorted = stackalloc int[3];
        for (int j = 0; j < parts; j++)
        {
            if (mpm[j] >= 0)
            {
                writer.EncodeBypass(mpm[j] == 0 ? 0 : 1);
                if (mpm[j] > 0)
                {
                    writer.EncodeBypass(mpm[j] == 1 ? 0 : 1);
                }
            }
            else
            {
                candidates.Slice(j * 3, 3).CopyTo(sorted);
                sorted.Sort();
                int remaining = modes[j];
                for (int i = 2; i >= 0; i--)
                {
                    if (remaining > sorted[i])
                    {
                        remaining--;
                    }
                }

                writer.EncodeBypassBits((uint)remaining, 5);
            }
        }
    }

    // intra_chroma_pred_mode: 4 (derived from luma) or an index into {planar, vertical, horizontal, DC}.
    public static void IntraChromaMode(HevcCabacWriter writer, Span<byte> contexts, int syntaxValue)
    {
        writer.EncodeBin(ref contexts[HevcContext.IntraChromaPredMode], syntaxValue == 4 ? 0 : 1);
        if (syntaxValue != 4)
        {
            writer.EncodeBypassBits((uint)syntaxValue, 2);
        }
    }

    // 7.3.8.14 cu_qp_delta_abs (TU prefix, cMax 5, then EG0) and cu_qp_delta_sign_flag (port: TEncSbac::codeDeltaQP).
    public static void CuQpDelta(HevcCabacWriter writer, Span<byte> contexts, int delta)
    {
        int magnitude = Math.Abs(delta);
        int prefix = Math.Min(magnitude, 5);
        writer.EncodeBin(ref contexts[HevcContext.CuQpDeltaAbs], prefix > 0 ? 1 : 0);
        for (int i = 1; i < 5 && i <= prefix; i++)
        {
            writer.EncodeBin(ref contexts[HevcContext.CuQpDeltaAbs + 1], i < prefix ? 1 : 0);
        }

        if (prefix == 5)
        {
            int suffix = magnitude - 5;
            int k = 0;
            while (suffix >= (1 << k))
            {
                writer.EncodeBypass(1);
                suffix -= 1 << k;
                k++;
            }

            writer.EncodeBypass(0);
            if (k > 0)
            {
                writer.EncodeBypassBits((uint)suffix, k);
            }
        }

        if (magnitude > 0)
        {
            writer.EncodeBypass(delta < 0 ? 1 : 0);
        }
    }

    public static void CbfLuma(HevcCabacWriter writer, Span<byte> contexts, bool cbf, int trafoDepth) =>
        writer.EncodeBin(ref contexts[HevcContext.CbfLuma + (trafoDepth == 0 ? 1 : 0)], cbf ? 1 : 0);

    public static void CbfChroma(HevcCabacWriter writer, Span<byte> contexts, bool cbf, int trafoDepth) =>
        writer.EncodeBin(ref contexts[HevcContext.CbfChroma + trafoDepth], cbf ? 1 : 0);

    // residual_coding for a block with at least one nonzero level (raster order).
    public static void Residual(HevcCabacWriter writer, Span<byte> contexts, ReadOnlySpan<int> levels, int log2Size,
        bool luma, int scanIdx, bool transformSkipEnabled, bool transformSkip, bool signHiding)
    {
        int size = 1 << log2Size;
        if (transformSkipEnabled && log2Size == 2)
        {
            writer.EncodeBin(ref contexts[luma ? HevcContext.TransformSkipLuma : HevcContext.TransformSkipChroma], transformSkip ? 1 : 0);
        }

        int log2Groups = log2Size - 2;
        int groupsWide = 1 << log2Groups;
        ReadOnlySpan<byte> subBlockScan = HevcScan.Get(log2Groups, scanIdx);
        ReadOnlySpan<byte> positionScan = HevcScan.Get(2, scanIdx);
        Span<byte> codedSubBlocks = stackalloc byte[64];
        codedSubBlocks.Clear();

        // Last significant coefficient in scan order and the coded sub-block map.
        int lastSubBlock = -1;
        int lastPosition = -1;
        for (int s = (1 << (log2Groups * 2)) - 1; s >= 0; s--)
        {
            int xS = subBlockScan[s] & 15;
            int yS = subBlockScan[s] >> 4;
            for (int p = 15; p >= 0; p--)
            {
                int x = (xS << 2) + (positionScan[p] & 15);
                int y = (yS << 2) + (positionScan[p] >> 4);
                if (levels[(y * size) + x] != 0)
                {
                    codedSubBlocks[(yS * groupsWide) + xS] = 1;
                    if (lastSubBlock < 0)
                    {
                        lastSubBlock = s;
                        lastPosition = p;
                    }
                }
            }
        }

        int lastX = ((subBlockScan[lastSubBlock] & 15) << 2) + (positionScan[lastPosition] & 15);
        int lastY = ((subBlockScan[lastSubBlock] >> 4) << 2) + (positionScan[lastPosition] >> 4);
        LastSignificant(writer, contexts, scanIdx == HevcScan.Vertical ? lastY : lastX,
            scanIdx == HevcScan.Vertical ? lastX : lastY, log2Size, luma);

        int sigBase = luma ? HevcContext.SigCoeffLuma : HevcContext.SigCoeffChroma;
        int firstSigContext = log2Size == 2 ? 0 : log2Size == 3 ? (luma ? (scanIdx == HevcScan.Diagonal ? 9 : 15) : 9) : (luma ? 21 : 12);
        Span<int> absolute = stackalloc int[16];
        int c1 = 1;
        for (int s = lastSubBlock; s >= 0; s--)
        {
            int xS = subBlockScan[s] & 15;
            int yS = subBlockScan[s] >> 4;
            int count = 0;
            uint signs = 0;
            int lastNonZero = -1;
            int firstNonZero = 16;
            int p = 15;
            if (s == lastSubBlock)
            {
                int value = levels[(lastY * size) + lastX];
                absolute[0] = Math.Abs(value);
                signs = value < 0 ? 1u : 0u;
                count = 1;
                lastNonZero = lastPosition;
                firstNonZero = lastPosition;
                p = lastPosition - 1;
            }

            bool coded = codedSubBlocks[(yS * groupsWide) + xS] != 0;
            if (s != lastSubBlock && s != 0)
            {
                int right = xS < groupsWide - 1 ? codedSubBlocks[(yS * groupsWide) + xS + 1] : 0;
                int below = yS < groupsWide - 1 ? codedSubBlocks[((yS + 1) * groupsWide) + xS] : 0;
                writer.EncodeBin(ref contexts[HevcContext.CodedSubBlock + (right | below) + (luma ? 0 : 2)], coded ? 1 : 0);
            }
            else
            {
                coded = true;
            }

            if (coded)
            {
                int pattern = (xS < groupsWide - 1 ? codedSubBlocks[(yS * groupsWide) + xS + 1] : 0)
                    | ((yS < groupsWide - 1 ? codedSubBlocks[((yS + 1) * groupsWide) + xS] : 0) << 1);
                for (; p >= 0; p--)
                {
                    int xC = (xS << 2) + (positionScan[p] & 15);
                    int yC = (yS << 2) + (positionScan[p] >> 4);
                    int value = levels[(yC * size) + xC];
                    if (p > 0 || s == 0 || s == lastSubBlock || count > 0)
                    {
                        int ctx = SignificanceContext(xC, yC, log2Size, pattern, firstSigContext, luma);
                        writer.EncodeBin(ref contexts[sigBase + ctx], value != 0 ? 1 : 0);
                    }

                    if (value != 0)
                    {
                        absolute[count++] = Math.Abs(value);
                        signs = (2 * signs) + (value < 0 ? 1u : 0u);
                        if (lastNonZero < 0)
                        {
                            lastNonZero = p;
                        }

                        firstNonZero = p;
                    }
                }
            }

            if (count == 0)
            {
                continue;
            }

            bool signHidden = lastNonZero - firstNonZero >= 4;
            int ctxSet = (s == 0 || !luma ? 0 : 2) + (c1 == 0 ? 1 : 0);
            c1 = 1;
            int greater1Base = HevcContext.Greater1 + (luma ? 0 : 16) + (ctxSet * 4);
            int firstGreater1 = -1;
            bool escape = count > 8;
            for (int i = 0; i < Math.Min(count, 8); i++)
            {
                int bin = absolute[i] > 1 ? 1 : 0;
                writer.EncodeBin(ref contexts[greater1Base + c1], bin);
                if (bin != 0)
                {
                    c1 = 0;
                    if (firstGreater1 < 0)
                    {
                        firstGreater1 = i;
                    }
                    else
                    {
                        escape = true;
                    }
                }
                else if (c1 is > 0 and < 3)
                {
                    c1++;
                }
            }

            if (c1 == 0 && firstGreater1 >= 0)
            {
                int bin = absolute[firstGreater1] > 2 ? 1 : 0;
                writer.EncodeBin(ref contexts[HevcContext.Greater2 + (luma ? 0 : 4) + ctxSet], bin);
                escape |= bin != 0;
            }

            if (signHiding && signHidden)
            {
                writer.EncodeBypassBits(signs >> 1, count - 1);
            }
            else
            {
                writer.EncodeBypassBits(signs, count);
            }

            if (escape)
            {
                int rice = 0;
                int firstCoefficient2 = 1;
                for (int i = 0; i < count; i++)
                {
                    int baseLevel = i < 8 ? 2 + firstCoefficient2 : 1;
                    if (absolute[i] >= baseLevel)
                    {
                        CoeffAbsLevelRemaining(writer, absolute[i] - baseLevel, rice);
                        if (absolute[i] > 3 << rice)
                        {
                            rice = Math.Min(rice + 1, 4);
                        }
                    }

                    if (absolute[i] >= 2)
                    {
                        firstCoefficient2 = 0;
                    }
                }
            }
        }
    }

    // port: TEncSbac::codeLastSignificantXY (coordinates already swapped for vertical scan).
    private static void LastSignificant(HevcCabacWriter writer, Span<byte> contexts, int x, int y, int log2Size, bool luma)
    {
        int ctxOffset = luma ? (3 * (log2Size - 2)) + ((log2Size - 1) >> 2) : 15;
        int ctxShift = luma ? (log2Size + 1) >> 2 : log2Size - 2;
        int groups = GroupIndex[(1 << log2Size) - 1];
        int groupX = GroupIndex[x];
        int groupY = GroupIndex[y];
        for (int i = 0; i < groupX; i++)
        {
            writer.EncodeBin(ref contexts[HevcContext.LastXPrefix + ctxOffset + (i >> ctxShift)], 1);
        }

        if (groupX < groups)
        {
            writer.EncodeBin(ref contexts[HevcContext.LastXPrefix + ctxOffset + (groupX >> ctxShift)], 0);
        }

        for (int i = 0; i < groupY; i++)
        {
            writer.EncodeBin(ref contexts[HevcContext.LastYPrefix + ctxOffset + (i >> ctxShift)], 1);
        }

        if (groupY < groups)
        {
            writer.EncodeBin(ref contexts[HevcContext.LastYPrefix + ctxOffset + (groupY >> ctxShift)], 0);
        }

        if (groupX > 3)
        {
            writer.EncodeBypassBits((uint)(x - MinInGroup[groupX]), (groupX - 2) >> 1);
        }

        if (groupY > 3)
        {
            writer.EncodeBypassBits((uint)(y - MinInGroup[groupY]), (groupY - 2) >> 1);
        }
    }

    // port: TEncSbac::xWriteCoefRemainExGolomb (useLimitedPrefixLength false)
    private static void CoeffAbsLevelRemaining(HevcCabacWriter writer, int value, int rice)
    {
        if (value < (3 << rice))
        {
            int length = value >> rice;
            writer.EncodeBypassBits((1u << (length + 1)) - 2, length + 1);
            writer.EncodeBypassBits((uint)(value % (1 << rice)), rice);
            return;
        }

        int length2 = rice;
        int code = value - (3 << rice);
        while (code >= (1 << length2))
        {
            code -= 1 << length2++;
        }

        int prefix = 3 + length2 + 1 - rice;
        writer.EncodeBypassBits((uint)((1L << prefix) - 2), prefix);
        writer.EncodeBypassBits((uint)code, length2);
    }

    // Same derivation as the decoder (port: TComTrQuant::getSigCtxInc).
    internal static int SignificanceContext(int xC, int yC, int log2Size, int pattern, int firstContext, bool luma)
    {
        if (xC + yC == 0)
        {
            return 0;
        }

        if (log2Size == 2)
        {
            return ContextIndexMap4x4[(yC << 2) + xC];
        }

        int xP = xC & 3;
        int yP = yC & 3;
        int count = pattern switch
        {
            0 => xP + yP >= 3 ? 0 : xP + yP >= 1 ? 1 : 2,
            1 => yP >= 2 ? 0 : yP >= 1 ? 1 : 2,
            2 => xP >= 2 ? 0 : xP >= 1 ? 1 : 2,
            _ => 2,
        };
        bool notFirstGroup = (xC >> 2) + (yC >> 2) > 0;
        return firstContext + (notFirstGroup && luma ? 3 : 0) + count;
    }
}
