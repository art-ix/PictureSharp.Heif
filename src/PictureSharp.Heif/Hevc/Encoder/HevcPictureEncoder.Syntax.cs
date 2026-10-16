namespace PictureSharp.Formats.Heif.Hevc;

internal sealed partial class HevcPictureEncoder
{
    private readonly int[] residualBlock = new int[32 * 32];

    // 7.3.8.4 coding_quadtree from the decision maps.
    private void WriteCodingQuadtree(HevcCabacWriter writer, Span<byte> ctx, int x0, int y0, int log2Size, int depth)
    {
        int size = 1 << log2Size;
        bool split;
        if (x0 + size <= width && y0 + size <= height && log2Size > MinCbLog2)
        {
            split = ctDepth[BlockIndex(x0, y0)] > depth;
            HevcSyntaxWriter.SplitCuFlag(writer, ctx, split, SplitContext(x0, y0, depth));
        }
        else
        {
            split = log2Size > MinCbLog2;
        }

        if (!split)
        {
            WriteCodingUnit(writer, ctx, x0, y0, log2Size);
            return;
        }

        int half = size >> 1;
        for (int i = 0; i < 4; i++)
        {
            int x = x0 + ((i & 1) * half);
            int y = y0 + ((i >> 1) * half);
            if (x < width && y < height)
            {
                WriteCodingQuadtree(writer, ctx, x, y, log2Size - 1, depth + 1);
            }
        }
    }

    // 9.3.4.2.2 split_cu_flag context from the depths of the available left and above blocks.
    private int SplitContext(int x0, int y0, int depth) =>
        (IsAvailable(x0, y0, x0 - 1, y0) && ctDepth[BlockIndex(x0 - 1, y0)] > depth ? 1 : 0)
        + (IsAvailable(x0, y0, x0, y0 - 1) && ctDepth[BlockIndex(x0, y0 - 1)] > depth ? 1 : 0);

    // 7.3.8.5 coding_unit and its transform tree (TU = CU, or four 4x4 luma TUs for NxN).
    private void WriteCodingUnit(HevcCabacWriter writer, Span<byte> ctx, int x0, int y0, int log2Size)
    {
        bool nxn = partNxN[BlockIndex(x0, y0)] != 0;
        if (log2Size == MinCbLog2)
        {
            HevcSyntaxWriter.PartModeIntra(writer, ctx, nxn);
        }

        WriteLumaModes(writer, ctx, x0, y0, log2Size, nxn);
        int lumaMode = intraModes[BlockIndex(x0, y0)];
        int chromaMode = 0;
        if (chromaFormat != 0)
        {
            int syntax = chromaSyntax[BlockIndex(x0, y0)];
            HevcSyntaxWriter.IntraChromaMode(writer, ctx, syntax);
            chromaMode = ChromaModeFromSyntax(syntax, lumaMode);
        }

        WriteTransformTree(writer, ctx, x0, y0, log2Size, nxn, chromaMode);
    }

    private void WriteLumaModes(HevcCabacWriter writer, Span<byte> ctx, int x0, int y0, int log2Size, bool nxn)
    {
        int parts = nxn ? 4 : 1;
        int pbSize = nxn ? 1 << (log2Size - 1) : 1 << log2Size;
        Span<int> modes = stackalloc int[parts];
        Span<int> candidates = stackalloc int[parts * 3];
        for (int j = 0; j < parts; j++)
        {
            int xPb = x0 + ((j & 1) * pbSize);
            int yPb = y0 + ((j >> 1) * pbSize);
            modes[j] = intraModes[BlockIndex(xPb, yPb)];
            MpmCandidates(xPb, yPb, candidates.Slice(j * 3, 3));
        }

        HevcSyntaxWriter.IntraLumaModes(writer, ctx, modes, candidates);
    }

    private void WriteTransformTree(HevcCabacWriter writer, Span<byte> ctx, int x0, int y0, int log2Size, bool nxn, int chromaMode) =>
        WriteTransformNode(writer, ctx, x0, y0, x0, y0, log2Size, 0, 0, nxn, chromaMode, false, false);

    // 7.3.8.8 transform_tree from the transform depth map. For a 4x4 luma node the chroma flags are the parent's
    // (cbf_cb[xBase][yBase][trafoDepth - 1]), and chroma is coded with its last block.
    private void WriteTransformNode(HevcCabacWriter writer, Span<byte> ctx, int x, int y, int xBase, int yBase, int log2Size,
        int depth, int blkIdx, bool nxn, int chromaMode, bool parentCb, bool parentCr)
    {
        int maxDepth = settings.MaxTransformDepth + (nxn ? 1 : 0);
        bool split;
        if (log2Size <= 5 && log2Size > 2 && depth < maxDepth && !(nxn && depth == 0))
        {
            split = tuDepth[BlockIndex(x, y)] > depth;
            HevcSyntaxWriter.SplitTransformFlag(writer, ctx, split, log2Size);
        }
        else
        {
            split = log2Size > 5 || (nxn && depth == 0);
        }

        bool cbfCb = parentCb;
        bool cbfCr = parentCr;
        if (chromaFormat == 0)
        {
            cbfCb = cbfCr = false;
        }
        else if (log2Size > 2)
        {
            cbfCb = (depth == 0 || parentCb) && HasLevels(1, x >> 1, y >> 1, log2Size - 1);
            cbfCr = (depth == 0 || parentCr) && HasLevels(2, x >> 1, y >> 1, log2Size - 1);
            if (depth == 0 || parentCb)
            {
                HevcSyntaxWriter.CbfChroma(writer, ctx, cbfCb, depth);
            }

            if (depth == 0 || parentCr)
            {
                HevcSyntaxWriter.CbfChroma(writer, ctx, cbfCr, depth);
            }
        }

        if (split)
        {
            int half = 1 << (log2Size - 1);
            for (int i = 0; i < 4; i++)
            {
                WriteTransformNode(writer, ctx, x + ((i & 1) * half), y + ((i >> 1) * half), x, y, log2Size - 1, depth + 1, i,
                    nxn, chromaMode, cbfCb, cbfCr);
            }

            return;
        }

        WriteTransformUnit(writer, ctx, x, y, xBase, yBase, log2Size, depth, blkIdx, chromaMode, cbfCb, cbfCr);
    }

    // 7.3.8.10 transform_unit: cbf_luma, cu_qp_delta, the luma residual, then chroma at this node (or, for 4x4 luma
    // blocks, the parent's 4x4 chroma with the last block).
    private void WriteTransformUnit(HevcCabacWriter writer, Span<byte> ctx, int x, int y, int xBase, int yBase, int log2Size,
        int depth, int blkIdx, int chromaMode, bool cbfCb, bool cbfCr)
    {
        bool cbfLuma = HasLevels(0, x, y, log2Size);
        HevcSyntaxWriter.CbfLuma(writer, ctx, cbfLuma, depth);
        if (settings.AdaptiveQuantization && !qpDeltaCoded && (cbfLuma || cbfCb || cbfCr))
        {
            HevcSyntaxWriter.CuQpDelta(writer, ctx, qp - lastQpY);
            if (writer is HevcCabacEncoder)
            {
                qpDeltaCoded = true;
            }
        }

        if (cbfLuma)
        {
            WriteResidual(writer, ctx, 0, x, y, log2Size, ScanIndex(intraModes[BlockIndex(x, y)], log2Size, true));
        }

        if (chromaFormat == 0 || (log2Size == 2 && blkIdx != 3))
        {
            return;
        }

        int chromaLog2 = log2Size == 2 ? 2 : log2Size - 1;
        int xc = (log2Size == 2 ? xBase : x) >> 1;
        int yc = (log2Size == 2 ? yBase : y) >> 1;
        int scan = ScanIndex(chromaMode, chromaLog2, false);
        if (cbfCb)
        {
            WriteResidual(writer, ctx, 1, xc, yc, chromaLog2, scan);
        }

        if (cbfCr)
        {
            WriteResidual(writer, ctx, 2, xc, yc, chromaLog2, scan);
        }
    }

    private void WriteResidual(HevcCabacWriter writer, Span<byte> ctx, int cIdx, int x, int y, int log2Size, int scanIdx)
    {
        int size = 1 << log2Size;
        Span<int> block = residualBlock.AsSpan(0, size * size);
        int stride = strides[cIdx];
        for (int j = 0; j < size; j++)
        {
            levels[cIdx].AsSpan(((y + j) * stride) + x, size).CopyTo(block.Slice(j * size, size));
        }

        HevcSyntaxWriter.Residual(writer, ctx, block, log2Size, cIdx == 0, scanIdx, settings.TransformSkip, false,
            settings.SignHidingEnabled);
    }

    private bool HasLevels(int cIdx, int x, int y, int log2Size)
    {
        int size = 1 << log2Size;
        int stride = strides[cIdx];
        for (int j = 0; j < size; j++)
        {
            if (levels[cIdx].AsSpan(((y + j) * stride) + x, size).IndexOfAnyExcept(0) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
