namespace PictureSharp.Formats.Heif.Hevc;

internal sealed partial class HevcPictureDecoder
{
    // 8.6.1 chroma QP mapping for ChromaArrayType 1 (port: HM g_aucChromaScale[CHROMA_420], QpParam).
    private static ReadOnlySpan<byte> ChromaScale420 =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51,
    ];

    // Group index of the last significant position (port: HM g_uiGroupIdx, g_uiMinInGroup).
    private static ReadOnlySpan<byte> GroupIndex =>
        [0, 1, 2, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9];

    private static ReadOnlySpan<byte> MinInGroup => [0, 1, 2, 3, 4, 6, 8, 12, 16, 24];

    private static ReadOnlySpan<byte> ContextIndexMap4x4 => [0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8];

    // Predicts one transform block, decodes its residual when coded and stores the reconstruction.
    // cIdx 0 uses luma coordinates; chroma blocks use chroma coordinates. (xTbY, yTbY) is the luma location used for
    // neighbour availability (8.4.4.2.2).
    private void ReconstructBlock(int cIdx, int xTb, int yTb, int log2Size, int mode, bool cbf, int xTbY, int yTbY)
    {
        int size = 1 << log2Size;
        int bitDepth = cIdx == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        ushort[] plane = frame.Planes[cIdx];
        int stride = frame.Strides[cIdx];
        intra.Predict(plane, stride, cIdx, xTb, yTb, log2Size, mode, bitDepth, xTbY, yTbY, this);
        int[] prediction = intra.Prediction;
        int maximum = (1 << bitDepth) - 1;
        if (!cbf)
        {
            for (int y = 0; y < size; y++)
            {
                HevcSamples.Store(plane.AsSpan(((yTb + y) * stride) + xTb, size), prediction.AsSpan(y * size, size));
            }

            return;
        }

        bool transformSkip = ParseResidualCoding(cIdx, log2Size, mode);
        Span<int> coefficientBlock = coefficients.AsSpan(0, size * size);
        Span<int> residualBlock = residual.AsSpan(0, size * size);
        if (cuTransquantBypass)
        {
            coefficientBlock.CopyTo(residualBlock);
        }
        else
        {
            int qp = cIdx == 0 ? qpY + sps.QpBdOffsetY : ChromaQp(cIdx);
            ReadOnlySpan<int> factors = scalingList is null || (transformSkip && log2Size > 2)
                ? default
                : scalingList.GetFactors(log2Size, cIdx);
            HevcTransform.Dequantize(coefficientBlock, log2Size, qp, bitDepth, factors);
            if (transformSkip)
            {
                HevcTransform.TransformSkip(coefficientBlock, residualBlock, log2Size, bitDepth);
            }
            else
            {
                HevcTransform.Inverse(coefficientBlock, residualBlock, temporary, log2Size, cIdx == 0 && log2Size == 2, bitDepth);
            }
        }

        for (int y = 0; y < size; y++)
        {
            HevcSamples.StoreClipped(plane.AsSpan(((yTb + y) * stride) + xTb, size), prediction.AsSpan(y * size, size),
                residualBlock.Slice(y * size, size), maximum);
        }
    }

    // port: HM QpParam::QpParam (chroma)
    private int ChromaQp(int cIdx)
    {
        int offset = cIdx == 1 ? pps.CbQpOffset + slice.CbQpOffset : pps.CrQpOffset + slice.CrQpOffset;
        int qpBdOffset = sps.QpBdOffsetC;
        int baseQp = Math.Clamp(qpY + offset, -qpBdOffset, 57);
        return baseQp < 0 ? baseQp + qpBdOffset : ChromaScale420[baseQp] + qpBdOffset;
    }

    // 7.3.8.11 residual_coding (port: HM TDecSbac::parseCoeffNxN, parseLastSignificantXY, xReadCoefRemainExGolomb).
    // Leaves TransCoeffLevel in raster order in coefficients and returns transform_skip_flag.
    private bool ParseResidualCoding(int cIdx, int log2Size, int predMode)
    {
        int size = 1 << log2Size;
        Span<int> block = coefficients.AsSpan(0, size * size);
        ClearWrittenCoefficients();
        bool luma = cIdx == 0;
        bool transformSkip = false;
        if (pps.TransformSkipEnabled && !cuTransquantBypass && log2Size == 2)
        {
            transformSkip = cabac.DecodeBin(ref contexts[luma ? HevcContext.TransformSkipLuma : HevcContext.TransformSkipChroma]) != 0;
        }

        // 7.4.9.11 scanIdx: mode-dependent scan for 4x4 and luma 8x8 blocks.
        int scanIdx = HevcScan.Diagonal;
        if (log2Size == 2 || (log2Size == 3 && luma))
        {
            if (predMode is >= 6 and <= 14)
            {
                scanIdx = HevcScan.Vertical;
            }
            else if (predMode is >= 22 and <= 30)
            {
                scanIdx = HevcScan.Horizontal;
            }
        }

        // last_sig_coeff_x/y_prefix and suffix.
        int ctxOffset = luma ? (3 * (log2Size - 2)) + ((log2Size - 1) >> 2) : 15;
        int ctxShift = luma ? (log2Size + 1) >> 2 : log2Size - 2;
        int groups = GroupIndex[size - 1];
        int lastX = 0;
        while (lastX < groups && cabac.DecodeBin(ref contexts[HevcContext.LastXPrefix + ctxOffset + (lastX >> ctxShift)]) != 0)
        {
            lastX++;
        }

        int lastY = 0;
        while (lastY < groups && cabac.DecodeBin(ref contexts[HevcContext.LastYPrefix + ctxOffset + (lastY >> ctxShift)]) != 0)
        {
            lastY++;
        }

        if (lastX > 3)
        {
            lastX = MinInGroup[lastX] + (int)cabac.DecodeBypassBits((lastX - 2) >> 1);
        }

        if (lastY > 3)
        {
            lastY = MinInGroup[lastY] + (int)cabac.DecodeBypassBits((lastY - 2) >> 1);
        }

        if (scanIdx == HevcScan.Vertical)
        {
            (lastX, lastY) = (lastY, lastX);
        }

        int log2Groups = log2Size - 2;
        int groupsWide = 1 << log2Groups;
        ReadOnlySpan<byte> subBlockScan = HevcScan.Get(log2Groups, scanIdx);
        ReadOnlySpan<byte> positionScan = HevcScan.Get(2, scanIdx);

        // Scan position of the last significant coefficient: its sub-block in the sub-block scan, then its position in
        // the 4x4 scan (scan entries are x | y << 4).
        int lastSubBlock = subBlockScan.IndexOf((byte)(((lastY >> 2) << 4) | (lastX >> 2)));
        int lastPosition = positionScan.IndexOf((byte)(((lastY & 3) << 4) | (lastX & 3)));
        if (lastSubBlock < 0 || lastPosition < 0)
        {
            throw HevcError.Invalid("last significant coefficient outside the transform block.");
        }

        int sigBase = luma ? HevcContext.SigCoeffLuma : HevcContext.SigCoeffChroma;
        int firstSigContext;
        if (log2Size == 2)
        {
            firstSigContext = 0;
        }
        else if (log2Size == 3)
        {
            firstSigContext = luma ? (scanIdx == HevcScan.Diagonal ? 9 : 15) : 9;
        }
        else
        {
            firstSigContext = luma ? 21 : 12;
        }

        bool signHidingAllowed = pps.SignDataHidingEnabled && !cuTransquantBypass;
        Span<byte> codedSubBlocks = stackalloc byte[64];
        codedSubBlocks.Clear();
        Span<int> positions = stackalloc int[16];
        Span<int> absolute = stackalloc int[16];
        int c1 = 1;
        for (int s = lastSubBlock; s >= 0; s--)
        {
            int xS = subBlockScan[s] & 15;
            int yS = subBlockScan[s] >> 4;
            int count = 0;
            int lastNonZero = -1;
            int firstNonZero = 16;
            int p = 15;
            if (s == lastSubBlock)
            {
                p = lastPosition - 1;
                positions[0] = (lastY * size) + lastX;
                lastNonZero = lastPosition;
                firstNonZero = lastPosition;
                count = 1;
            }

            // coded_sub_block_flag (ctx 9.3.4.2.4)
            bool inferredSigned = false;
            if (s == lastSubBlock || s == 0)
            {
                codedSubBlocks[(yS * groupsWide) + xS] = 1;
            }
            else
            {
                int right = xS < groupsWide - 1 ? codedSubBlocks[(yS * groupsWide) + xS + 1] : 0;
                int below = yS < groupsWide - 1 ? codedSubBlocks[((yS + 1) * groupsWide) + xS] : 0;
                int ctx = (right | below) + (luma ? 0 : 2);
                codedSubBlocks[(yS * groupsWide) + xS] = (byte)cabac.DecodeBin(ref contexts[HevcContext.CodedSubBlock + ctx]);
                inferredSigned = true;
            }

            bool coded = codedSubBlocks[(yS * groupsWide) + xS] != 0;

            // port: HM TComTrQuant::calcPatternSigCtx
            int pattern = 0;
            if (xS < groupsWide - 1)
            {
                pattern |= codedSubBlocks[(yS * groupsWide) + xS + 1];
            }

            if (yS < groupsWide - 1)
            {
                pattern |= codedSubBlocks[((yS + 1) * groupsWide) + xS] << 1;
            }

            for (; p >= 0; p--)
            {
                int xC = (xS << 2) + (positionScan[p] & 15);
                int yC = (yS << 2) + (positionScan[p] >> 4);
                int significant = 0;
                if (coded)
                {
                    if (p > 0 || !inferredSigned || count > 0)
                    {
                        int ctx = SignificanceContext(xC, yC, log2Size, pattern, firstSigContext, luma);
                        significant = cabac.DecodeBin(ref contexts[sigBase + ctx]);
                    }
                    else
                    {
                        significant = 1;
                    }
                }

                if (significant != 0)
                {
                    positions[count++] = (yC * size) + xC;
                    if (lastNonZero < 0)
                    {
                        lastNonZero = p;
                    }

                    firstNonZero = p;
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
            for (int i = 0; i < count; i++)
            {
                absolute[i] = 1;
            }

            int firstGreater1 = -1;
            bool escape = count > 8;
            for (int i = 0; i < Math.Min(count, 8); i++)
            {
                int bin = cabac.DecodeBin(ref contexts[greater1Base + c1]);
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

                absolute[i] = bin + 1;
            }

            if (c1 == 0 && firstGreater1 >= 0)
            {
                int bin = cabac.DecodeBin(ref contexts[HevcContext.Greater2 + (luma ? 0 : 4) + ctxSet]);
                absolute[firstGreater1] = bin + 2;
                if (bin != 0)
                {
                    escape = true;
                }
            }

            bool hideSign = signHidden && signHidingAllowed;
            int signCount = hideSign ? count - 1 : count;
            uint signs = signCount > 0 ? cabac.DecodeBypassBits(signCount) << (32 - signCount) : 0;
            if (escape)
            {
                int rice = 0;
                int firstCoefficient2 = 1;
                for (int i = 0; i < count; i++)
                {
                    int baseLevel = i < 8 ? 2 + firstCoefficient2 : 1;
                    if (absolute[i] == baseLevel)
                    {
                        absolute[i] = baseLevel + ReadCoeffAbsLevelRemaining(rice);
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

            int sum = 0;
            for (int i = 0; i < count; i++)
            {
                int value = absolute[i];
                sum += value;
                if (i == count - 1 && hideSign)
                {
                    if ((sum & 1) != 0)
                    {
                        value = -value;
                    }
                }
                else
                {
                    if ((int)signs < 0)
                    {
                        value = -value;
                    }

                    signs <<= 1;
                }

                block[positions[i]] = value;
                writtenCoefficients[writtenCoefficientCount++] = positions[i];
            }
        }

        return transformSkip;
    }

    // Zeroes the previous block's coefficients (dequantization changes only those), cheaper than clearing 32x32.
    private void ClearWrittenCoefficients()
    {
        for (int i = 0; i < writtenCoefficientCount; i++)
        {
            coefficients[writtenCoefficients[i]] = 0;
        }

        writtenCoefficientCount = 0;
    }

    // port: HM TComTrQuant::getSigCtxInc (without transform_skip_context)
    private static int SignificanceContext(int xC, int yC, int log2Size, int pattern, int firstContext, bool luma)
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

    // port: HM TDecSbac::xReadCoefRemainExGolomb (useLimitedPrefixLength false)
    private int ReadCoeffAbsLevelRemaining(int rice)
    {
        int prefix = 0;
        while (cabac.DecodeBypass() != 0)
        {
            if (++prefix > 32)
            {
                throw HevcError.Invalid("coeff_abs_level_remaining prefix too long.");
            }
        }

        if (prefix < 3)
        {
            return (prefix << rice) + (rice > 0 ? (int)cabac.DecodeBypassBits(rice) : 0);
        }

        int suffixLength = prefix - 3 + rice;
        if (suffixLength > 30)
        {
            throw HevcError.Invalid("coeff_abs_level_remaining too large.");
        }

        int suffix = (int)cabac.DecodeBypassBits(suffixLength);
        return (((1 << (prefix - 3)) + 3 - 1) << rice) + suffix;
    }

    // 7.3.8.7 pcm_sample and 8.4.4.1 reconstruction (port: HM TDecSbac::parseIPCMInfo, TDecCu::xDecodePCMTexture)
    private void DecodePcm(int x0, int y0, int log2CbSize)
    {
        int bit = 0;
        int size = 1 << log2CbSize;
        WritePcm(0, x0, y0, size, sps.PcmBitDepthLuma, sps.BitDepthLuma, ref bit);
        if (sps.ChromaFormatIdc != 0)
        {
            WritePcm(1, x0 >> 1, y0 >> 1, size >> 1, sps.PcmBitDepthChroma, sps.BitDepthChroma, ref bit);
            WritePcm(2, x0 >> 1, y0 >> 1, size >> 1, sps.PcmBitDepthChroma, sps.BitDepthChroma, ref bit);
        }

        cabac.SkipPcmBytes((bit + 7) >> 3);
        cabac.Start();
    }

    private void WritePcm(int cIdx, int x0, int y0, int size, int pcmBitDepth, int bitDepth, ref int bit)
    {
        ushort[] plane = frame.Planes[cIdx];
        int stride = frame.Strides[cIdx];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                plane[((y0 + y) * stride) + x0 + x] = (ushort)(cabac.ReadPcmBits(ref bit, pcmBitDepth) << (bitDepth - pcmBitDepth));
            }
        }
    }
}
