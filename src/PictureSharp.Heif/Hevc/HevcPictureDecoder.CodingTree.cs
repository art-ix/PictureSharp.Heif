namespace PictureSharp.Formats.Heif.Hevc;

internal sealed partial class HevcPictureDecoder
{
    // H.265 7.3.8.2 coding_tree_unit
    private void DecodeCodingTreeUnit(int ctbAddrRs)
    {
        int x = (ctbAddrRs % layout.WidthInCtbs) << sps.Log2CtbSize;
        int y = (ctbAddrRs / layout.WidthInCtbs) << sps.Log2CtbSize;
        if (slice.SaoLuma || slice.SaoChroma)
        {
            ParseSao(ctbAddrRs);
        }

        CodingQuadtree(x, y, sps.Log2CtbSize, 0);
    }

    // 7.3.8.3 sao (port: HM TDecSbac::parseSAOBlkParam, parseSaoMaxUvlc, parseSaoTypeIdx)
    private void ParseSao(int ctbAddrRs)
    {
        int rx = ctbAddrRs % layout.WidthInCtbs;
        int tileId = layout.TileIdOfRs(ctbAddrRs);
        bool merge = false;
        if (rx > 0 && ctbAddrRs - 1 >= slice.SliceAddress && layout.TileIdOfRs(ctbAddrRs - 1) == tileId
            && cabac.DecodeBin(ref contexts[HevcContext.SaoMerge]) != 0)
        {
            CopySao(ctbAddrRs - 1, ctbAddrRs);
            merge = true;
        }

        int up = ctbAddrRs - layout.WidthInCtbs;
        if (!merge && up >= 0 && up >= slice.SliceAddress && layout.TileIdOfRs(up) == tileId
            && cabac.DecodeBin(ref contexts[HevcContext.SaoMerge]) != 0)
        {
            CopySao(up, ctbAddrRs);
            merge = true;
        }

        if (merge)
        {
            return;
        }

        int components = sps.ChromaFormatIdc == 0 ? 1 : 3;
        Span<int> offsets = stackalloc int[4];
        for (int c = 0; c < components; c++)
        {
            int index = (ctbAddrRs * 3) + c;
            bool enabled = c == 0 ? slice.SaoLuma : slice.SaoChroma;
            if (!enabled)
            {
                saoTypes[index] = 0;
                continue;
            }

            int type;
            if (c == 2)
            {
                type = saoTypes[index - 1];
            }
            else
            {
                type = cabac.DecodeBin(ref contexts[HevcContext.SaoTypeIdx]) == 0 ? 0 : cabac.DecodeBypass() == 0 ? 1 : 2;
            }

            saoTypes[index] = (byte)type;
            if (type == 0)
            {
                continue;
            }

            int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
            int maximum = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;
            for (int i = 0; i < 4; i++)
            {
                int value = 0;
                while (value < maximum && cabac.DecodeBypass() != 0)
                {
                    value++;
                }

                offsets[i] = value;
            }

            int scale = bitDepth - Math.Min(bitDepth, 10);
            Span<short> destination = saoOffsets.AsSpan(index * 4, 4);
            if (type == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (offsets[i] != 0 && cabac.DecodeBypass() != 0)
                    {
                        offsets[i] = -offsets[i];
                    }
                }

                saoClasses[index] = (byte)cabac.DecodeBypassBits(5);
                for (int i = 0; i < 4; i++)
                {
                    destination[i] = (short)(offsets[i] << scale);
                }
            }
            else
            {
                saoClasses[index] = c == 2 ? saoClasses[index - 1] : (byte)cabac.DecodeBypassBits(2);
                destination[0] = (short)(offsets[0] << scale);
                destination[1] = (short)(offsets[1] << scale);
                destination[2] = (short)(-(offsets[2] << scale));
                destination[3] = (short)(-(offsets[3] << scale));
            }
        }
    }

    private void CopySao(int source, int destination)
    {
        saoTypes.AsSpan(source * 3, 3).CopyTo(saoTypes.AsSpan(destination * 3));
        saoClasses.AsSpan(source * 3, 3).CopyTo(saoClasses.AsSpan(destination * 3));
        saoOffsets.AsSpan(source * 12, 12).CopyTo(saoOffsets.AsSpan(destination * 12));
    }

    // 7.3.8.4 coding_quadtree
    private void CodingQuadtree(int x0, int y0, int log2CbSize, int cqtDepth)
    {
        int size = 1 << log2CbSize;
        bool split;
        if (x0 + size <= sps.Width && y0 + size <= sps.Height && log2CbSize > sps.Log2MinCbSize)
        {
            // 9.3.4.2.2: ctxInc from the depths of the left and above coding blocks.
            int ctxInc = 0;
            if (IsAvailable(x0, y0, x0 - 1, y0) && ctDepth[BlockIndex(x0 - 1, y0)] > cqtDepth)
            {
                ctxInc++;
            }

            if (IsAvailable(x0, y0, x0, y0 - 1) && ctDepth[BlockIndex(x0, y0 - 1)] > cqtDepth)
            {
                ctxInc++;
            }

            split = cabac.DecodeBin(ref contexts[HevcContext.SplitCuFlag + ctxInc]) != 0;
        }
        else
        {
            split = log2CbSize > sps.Log2MinCbSize;
        }

        if (log2CbSize >= log2MinCuQpDeltaSize)
        {
            StartQuantizationGroup(x0, y0);
        }

        if (split)
        {
            int half = size >> 1;
            CodingQuadtree(x0, y0, log2CbSize - 1, cqtDepth + 1);
            if (x0 + half < sps.Width)
            {
                CodingQuadtree(x0 + half, y0, log2CbSize - 1, cqtDepth + 1);
            }

            if (y0 + half < sps.Height)
            {
                CodingQuadtree(x0, y0 + half, log2CbSize - 1, cqtDepth + 1);
            }

            if (x0 + half < sps.Width && y0 + half < sps.Height)
            {
                CodingQuadtree(x0 + half, y0 + half, log2CbSize - 1, cqtDepth + 1);
            }
        }
        else
        {
            CodingUnit(x0, y0, log2CbSize, cqtDepth);
        }
    }

    // 8.6.1 qPY_PRED for a quantization group (port: HM TComDataCU::getRefQP, getLastCodedQP).
    private void StartQuantizationGroup(int xQg, int yQg)
    {
        isCuQpDeltaCoded = false;
        cuQpDeltaVal = 0;
        int ctbMask = sps.CtbSize - 1;
        int qpPrev = lastQpY;
        int qpA = (xQg & ctbMask) != 0 ? qpMap[BlockIndex(xQg - 1, yQg)] : qpPrev;
        int qpB = (yQg & ctbMask) != 0 ? qpMap[BlockIndex(xQg, yQg - 1)] : qpPrev;
        qpYPred = (qpA + qpB + 1) >> 1;
    }

    private void UpdateQpY()
    {
        int offset = sps.QpBdOffsetY;
        qpY = ((qpYPred + cuQpDeltaVal + 52 + (2 * offset)) % (52 + offset)) - offset;
    }

    // 7.3.8.5 coding_unit for intra slices (port: HM TDecCu::xDecodeCU)
    private void CodingUnit(int x0, int y0, int log2CbSize, int cqtDepth)
    {
        int size = 1 << log2CbSize;
        UpdateQpY();
        cuTransquantBypass = pps.TransquantBypassEnabled && cabac.DecodeBin(ref contexts[HevcContext.CuTransquantBypass]) != 0;
        bool partNxN = false;
        if (log2CbSize == sps.Log2MinCbSize)
        {
            partNxN = cabac.DecodeBin(ref contexts[HevcContext.PartMode]) == 0;
            if (partNxN && log2CbSize - 1 < sps.Log2MinTbSize)
            {
                throw HevcError.Invalid("intra NxN partition below the minimum transform size.");
            }
        }

        FillBlocks(ctDepth, x0, y0, size, (byte)cqtDepth);
        bool pcm = !partNxN && sps.PcmEnabled && log2CbSize >= sps.Log2MinPcmCbSize && log2CbSize <= sps.Log2MaxPcmCbSize
            && cabac.DecodeTerminate() != 0;
        if (pcm)
        {
            FillBlocks(intraModes, x0, y0, size, 1);
            DecodePcm(x0, y0, log2CbSize);
            MarkTransformEdges(x0, y0, size);
        }
        else
        {
            int chromaMode = ParseIntraModes(x0, y0, log2CbSize, partNxN);
            int maxTrafoDepth = sps.MaxTransformHierarchyDepthIntra + (partNxN ? 1 : 0);
            TransformTree(x0, y0, x0, y0, log2CbSize, 0, 0, maxTrafoDepth, partNxN, chromaMode, false, false);
        }

        // HM TDecCu::xFinishDecodeCU: the CU keeps the QP current at its end.
        FillQp(x0, y0, size, (sbyte)qpY);
        lastQpY = qpY;
        if (cuTransquantBypass || (pcm && sps.PcmLoopFilterDisabled))
        {
            int blocks = size >> 2;
            for (int j = 0; j < blocks; j++)
            {
                int row = BlockIndex(x0, y0 + (j << 2));
                for (int i = 0; i < blocks && x0 + (i << 2) < sps.Width; i++)
                {
                    blockFlags[row + i] |= NoLoopFilter;
                }
            }
        }

        SetCodingUnitEdges(x0, y0, size);
    }

    private void FillQp(int x0, int y0, int size, sbyte value)
    {
        int blocks = size >> 2;
        for (int j = 0; j < blocks && y0 + (j << 2) < sps.Height; j++)
        {
            int count = Math.Min(blocks, (sps.Width - x0) >> 2);
            qpMap.AsSpan(BlockIndex(x0, y0 + (j << 2)), count).Fill(value);
        }
    }

    // 7.3.8.5 intra modes and 8.4.2 / 8.4.3 derivation (port: HM TDecSbac::parseIntraDirLumaAng, parseIntraDirChroma).
    // Returns the chroma prediction mode (8.4.3, 4:2:0).
    private int ParseIntraModes(int x0, int y0, int log2CbSize, bool partNxN)
    {
        int parts = partNxN ? 4 : 1;
        int pbSize = partNxN ? 1 << (log2CbSize - 1) : 1 << log2CbSize;
        Span<bool> prevFlags = stackalloc bool[4];
        for (int j = 0; j < parts; j++)
        {
            prevFlags[j] = cabac.DecodeBin(ref contexts[HevcContext.PrevIntraLumaPred]) != 0;
        }

        Span<int> candidates = stackalloc int[3];
        int firstMode = 0;
        for (int j = 0; j < parts; j++)
        {
            int xPb = x0 + ((j & 1) * pbSize);
            int yPb = y0 + ((j >> 1) * pbSize);
            int candA = IsAvailable(xPb, yPb, xPb - 1, yPb) ? intraModes[BlockIndex(xPb - 1, yPb)] : 1;
            int candB = 1;
            if (IsAvailable(xPb, yPb, xPb, yPb - 1) && yPb - 1 >= ((yPb >> sps.Log2CtbSize) << sps.Log2CtbSize))
            {
                candB = intraModes[BlockIndex(xPb, yPb - 1)];
            }

            if (candA == candB)
            {
                if (candA < 2)
                {
                    candidates[0] = 0;
                    candidates[1] = 1;
                    candidates[2] = 26;
                }
                else
                {
                    candidates[0] = candA;
                    candidates[1] = 2 + ((candA + 29) % 32);
                    candidates[2] = 2 + ((candA - 2 + 1) % 32);
                }
            }
            else
            {
                candidates[0] = candA;
                candidates[1] = candB;
                candidates[2] = candA != 0 && candB != 0 ? 0 : candA != 1 && candB != 1 ? 1 : 26;
            }

            int mode;
            if (prevFlags[j])
            {
                int mpmIdx = cabac.DecodeBypass() == 0 ? 0 : cabac.DecodeBypass() == 0 ? 1 : 2;
                mode = candidates[mpmIdx];
            }
            else
            {
                mode = (int)cabac.DecodeBypassBits(5);
                candidates.Sort();
                for (int i = 0; i < 3; i++)
                {
                    if (mode >= candidates[i])
                    {
                        mode++;
                    }
                }
            }

            FillBlocks(intraModes, xPb, yPb, pbSize, (byte)mode);
            if (j == 0)
            {
                firstMode = mode;
            }
        }

        if (sps.ChromaFormatIdc == 0)
        {
            return 0;
        }

        int chromaSyntax = cabac.DecodeBin(ref contexts[HevcContext.IntraChromaPredMode]) == 0 ? 4 : (int)cabac.DecodeBypassBits(2);
        if (chromaSyntax == 4)
        {
            return firstMode;
        }

        int chromaMode = chromaSyntax switch { 0 => 0, 1 => 26, 2 => 10, _ => 1 };
        return chromaMode == firstMode ? 34 : chromaMode;
    }

    // 7.3.8.8 transform_tree (port: HM TDecEntropy::xDecodeTransform)
    private void TransformTree(int x0, int y0, int xBase, int yBase, int log2TrafoSize, int trafoDepth, int blkIdx,
        int maxTrafoDepth, bool intraSplit, int chromaMode, bool parentCbfCb, bool parentCbfCr)
    {
        bool split;
        if (log2TrafoSize <= sps.Log2MaxTbSize && log2TrafoSize > sps.Log2MinTbSize && trafoDepth < maxTrafoDepth
            && !(intraSplit && trafoDepth == 0))
        {
            split = cabac.DecodeBin(ref contexts[HevcContext.SplitTransformFlag + 5 - log2TrafoSize]) != 0;
        }
        else
        {
            split = log2TrafoSize > sps.Log2MaxTbSize || (intraSplit && trafoDepth == 0);
        }

        bool cbfCb = false;
        bool cbfCr = false;
        if (sps.ChromaFormatIdc != 0)
        {
            if (log2TrafoSize > 2)
            {
                if (trafoDepth == 0 || parentCbfCb)
                {
                    cbfCb = cabac.DecodeBin(ref contexts[HevcContext.CbfChroma + trafoDepth]) != 0;
                }

                if (trafoDepth == 0 || parentCbfCr)
                {
                    cbfCr = cabac.DecodeBin(ref contexts[HevcContext.CbfChroma + trafoDepth]) != 0;
                }
            }
            else if (trafoDepth > 0)
            {
                cbfCb = parentCbfCb;
                cbfCr = parentCbfCr;
            }
        }

        if (split)
        {
            int half = 1 << (log2TrafoSize - 1);
            TransformTree(x0, y0, x0, y0, log2TrafoSize - 1, trafoDepth + 1, 0, maxTrafoDepth, intraSplit, chromaMode, cbfCb, cbfCr);
            TransformTree(x0 + half, y0, x0, y0, log2TrafoSize - 1, trafoDepth + 1, 1, maxTrafoDepth, intraSplit, chromaMode, cbfCb, cbfCr);
            TransformTree(x0, y0 + half, x0, y0, log2TrafoSize - 1, trafoDepth + 1, 2, maxTrafoDepth, intraSplit, chromaMode, cbfCb, cbfCr);
            TransformTree(x0 + half, y0 + half, x0, y0, log2TrafoSize - 1, trafoDepth + 1, 3, maxTrafoDepth, intraSplit, chromaMode, cbfCb, cbfCr);
            return;
        }

        // Intra CUs always code cbf_luma.
        bool cbfLuma = cabac.DecodeBin(ref contexts[HevcContext.CbfLuma + (trafoDepth == 0 ? 1 : 0)]) != 0;
        TransformUnit(x0, y0, xBase, yBase, log2TrafoSize, blkIdx, chromaMode, cbfLuma, cbfCb, cbfCr);
    }

    // 7.3.8.10 transform_unit with reconstruction of the luma and chroma blocks it covers.
    private void TransformUnit(int x0, int y0, int xBase, int yBase, int log2TrafoSize, int blkIdx, int chromaMode,
        bool cbfLuma, bool cbfCb, bool cbfCr)
    {
        if ((cbfLuma || cbfCb || cbfCr) && pps.CuQpDeltaEnabled && !isCuQpDeltaCoded)
        {
            ParseCuQpDelta();
        }

        int size = 1 << log2TrafoSize;
        ReconstructBlock(0, x0, y0, log2TrafoSize, intraModes[BlockIndex(x0, y0)], cbfLuma, x0, y0);
        MarkTransformEdges(x0, y0, size);
        if (sps.ChromaFormatIdc == 0)
        {
            return;
        }

        if (log2TrafoSize > 2)
        {
            ReconstructBlock(1, x0 >> 1, y0 >> 1, log2TrafoSize - 1, chromaMode, cbfCb, x0, y0);
            ReconstructBlock(2, x0 >> 1, y0 >> 1, log2TrafoSize - 1, chromaMode, cbfCr, x0, y0);
        }
        else if (blkIdx == 3)
        {
            ReconstructBlock(1, xBase >> 1, yBase >> 1, 2, chromaMode, cbfCb, xBase, yBase);
            ReconstructBlock(2, xBase >> 1, yBase >> 1, 2, chromaMode, cbfCr, xBase, yBase);
        }
    }

    // 7.3.8.14 / 9.3.3.10 cu_qp_delta_abs (port: HM TDecSbac::parseDeltaQP)
    private void ParseCuQpDelta()
    {
        int value = 0;
        if (cabac.DecodeBin(ref contexts[HevcContext.CuQpDeltaAbs]) != 0)
        {
            value = 1;
            while (value < 5 && cabac.DecodeBin(ref contexts[HevcContext.CuQpDeltaAbs + 1]) != 0)
            {
                value++;
            }

            if (value == 5)
            {
                // EG0 suffix
                int k = 0;
                while (cabac.DecodeBypass() != 0)
                {
                    if (++k > 30)
                    {
                        throw HevcError.Invalid("cu_qp_delta_abs too large.");
                    }
                }

                value += (int)(((1u << k) - 1) + (k > 0 ? cabac.DecodeBypassBits(k) : 0));
            }
        }

        if (value != 0 && cabac.DecodeBypass() != 0)
        {
            value = -value;
        }

        int limit = 26 + (sps.QpBdOffsetY / 2);
        if (value < -limit || value > limit - 1)
        {
            throw HevcError.Invalid("CuQpDeltaVal out of range.");
        }

        isCuQpDeltaCoded = true;
        cuQpDeltaVal = value;
        UpdateQpY();
    }

    // Deblocking edge flags (port: HM TComLoopFilter::xSetEdgefilterTU, xSetEdgefilterPU, xSetLoopfilterParam).
    private void MarkTransformEdges(int x0, int y0, int size)
    {
        if (slice.DeblockingFilterDisabled)
        {
            return;
        }

        int blocks = size >> 2;
        for (int i = 0; i < blocks; i++)
        {
            blockFlags[BlockIndex(x0, y0 + (i << 2))] |= EdgeVertical;
            blockFlags[BlockIndex(x0 + (i << 2), y0)] |= EdgeHorizontal;
        }
    }

    private void SetCodingUnitEdges(int x0, int y0, int size)
    {
        bool left = x0 > 0 && !slice.DeblockingFilterDisabled && CanFilterAcross(x0 - 1, y0);
        bool top = y0 > 0 && !slice.DeblockingFilterDisabled && CanFilterAcross(x0, y0 - 1);
        int blocks = size >> 2;
        for (int i = 0; i < blocks; i++)
        {
            if (y0 + (i << 2) < sps.Height)
            {
                ref byte flags = ref blockFlags[BlockIndex(x0, y0 + (i << 2))];
                flags = left ? (byte)(flags | EdgeVertical) : (byte)(flags & ~EdgeVertical);
            }

            if (x0 + (i << 2) < sps.Width)
            {
                ref byte flags = ref blockFlags[BlockIndex(x0 + (i << 2), y0)];
                flags = top ? (byte)(flags | EdgeHorizontal) : (byte)(flags & ~EdgeHorizontal);
            }
        }
    }

    // HM TComDataCU::getPULeft/getPUAbove with slice and tile restrictions taken from the current slice and PPS.
    private bool CanFilterAcross(int xN, int yN)
    {
        int ctbN = ((yN >> sps.Log2CtbSize) * layout.WidthInCtbs) + (xN >> sps.Log2CtbSize);
        if (!slice.LoopFilterAcrossSlices && ctbSliceAddress[ctbN] != slice.SliceAddress)
        {
            return false;
        }

        int ctbCurrent = layout.TsToRs[decodedCtbs];
        return pps.LoopFilterAcrossTilesEnabled || layout.TileIdOfRs(ctbN) == layout.TileIdOfRs(ctbCurrent);
    }
}
