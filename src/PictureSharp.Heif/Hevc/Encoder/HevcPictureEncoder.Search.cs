using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

internal sealed partial class HevcPictureEncoder
{
    private const double Fraction = 1.0 / 32768;

    private readonly HevcCabacCounter counter = new();
    private readonly int[] residual = new int[32 * 32];
    private readonly int[] coefficients = new int[32 * 32];
    private readonly int[] quantized = new int[32 * 32];
    private readonly int[] temporary = new int[32 * 32];
    private readonly int[] reconstructed = new int[32 * 32];
    private readonly HevcRdoq rdoq = new();
    private readonly RegionState[] savedLeaf = [new(), new(), new(), new()];
    private readonly ushort[][] savedTreeRecon = [new ushort[32 * 32], new ushort[16 * 16], new ushort[8 * 8], new ushort[4 * 4]];
    private readonly int[][] savedTreeLevels = [new int[32 * 32], new int[16 * 16], new int[8 * 8], new int[4 * 4]];
    private readonly ushort[] bestModeRecon = new ushort[32 * 32];
    private readonly int[] bestModeLevels = new int[32 * 32];

    // Rate-distortion search for the coding quadtree rooted at (x0, y0); leaves the chosen reconstruction, levels and
    // modes in place and ctx in the state after coding them. Returns the cost.
    private double SearchCodingQuadtree(int x0, int y0, int log2Size, int depth, byte[] ctx)
    {
        int size = 1 << log2Size;
        bool inside = x0 + size <= width && y0 + size <= height;
        if (!inside || log2Size > 5)
        {
            double total = 0;
            int half = size >> 1;
            for (int i = 0; i < 4; i++)
            {
                int x = x0 + ((i & 1) * half);
                int y = y0 + ((i >> 1) * half);
                if (x < width && y < height)
                {
                    total += SearchCodingQuadtree(x, y, log2Size - 1, depth + 1, ctx);
                }
            }

            return total;
        }

        byte[] start = (byte[])ctx.Clone();
        Fill(ctDepth, x0, y0, size, (byte)depth);
        int splitContext = SplitContext(x0, y0, depth);
        if (log2Size == MinCbLog2)
        {
            return SearchLeaf(x0, y0, log2Size, ctx);
        }

        HevcSyntaxWriter.SplitCuFlag(counter, ctx, false, splitContext);
        double leafCost = SearchLeaf(x0, y0, log2Size, ctx) + (lambda * SplitFlagBits(start, splitContext, false));
        RegionState saved = savedLeaf[depth];
        saved.Save(this, x0, y0, size, ctx);

        // A leaf predicted without any residual is rarely improved by smaller coding units (early termination).
        if (!HasLevels(0, x0, y0, log2Size) && (chromaFormat == 0
            || (!HasLevels(1, x0 >> 1, y0 >> 1, log2Size - 1) && !HasLevels(2, x0 >> 1, y0 >> 1, log2Size - 1))))
        {
            return leafCost;
        }

        // Split: start again from the coding unit's initial context state.
        start.CopyTo(ctx, 0);
        double splitCost = lambda * SplitFlagBits(start, splitContext, true);
        HevcSyntaxWriter.SplitCuFlag(counter, ctx, true, splitContext);
        int halfSize = size >> 1;
        for (int i = 0; i < 4 && splitCost < leafCost; i++)
        {
            splitCost += SearchCodingQuadtree(x0 + ((i & 1) * halfSize), y0 + ((i >> 1) * halfSize), log2Size - 1, depth + 1, ctx);
        }

        if (splitCost < leafCost)
        {
            return splitCost;
        }

        saved.Restore(this, ctx);
        return leafCost;
    }

    private double SplitFlagBits(byte[] state, int splitContext, bool split) =>
        HevcCabacCounter.Cost(state[HevcContext.SplitCuFlag + splitContext], split ? 1 : 0) * Fraction;

    // Best leaf coding unit: 2Nx2N, and NxN at the minimum size. Updates ctx with the coded syntax.
    private double SearchLeaf(int x0, int y0, int log2Size, byte[] ctx)
    {
        int size = 1 << log2Size;
        Fill(partNxN, x0, y0, size, 0);
        double distortion = SearchLumaBlock(x0, y0, log2Size, ctx, 0);
        distortion += SearchChroma(x0, y0, log2Size, ctx);
        double cost = distortion + (lambda * CodingUnitBits(x0, y0, log2Size, ctx, update: false));
        if (log2Size == MinCbLog2)
        {
            RegionState saved = savedLeaf[3];
            saved.Save(this, x0, y0, size, ctx);
            Fill(partNxN, x0, y0, size, 1);
            double nxnDistortion = 0;
            for (int i = 0; i < 4; i++)
            {
                nxnDistortion += SearchLumaBlock(x0 + ((i & 1) * 4), y0 + ((i >> 1) * 4), 2, ctx, 1);
            }

            nxnDistortion += SearchChroma(x0, y0, log2Size, ctx);
            double nxnCost = nxnDistortion + (lambda * CodingUnitBits(x0, y0, log2Size, ctx, update: false));
            if (nxnCost < cost)
            {
                cost = nxnCost;
            }
            else
            {
                saved.Restore(this, ctx);
            }
        }

        CodingUnitBits(x0, y0, log2Size, ctx, update: true);
        return cost;
    }

    // Estimated bits of the coding unit's syntax from the current maps; update leaves ctx in the coded state.
    private double CodingUnitBits(int x0, int y0, int log2Size, byte[] ctx, bool update)
    {
        Span<byte> scratch = stackalloc byte[HevcContext.Count];
        Span<byte> state = update ? ctx : scratch;
        if (!update)
        {
            ctx.CopyTo(state);
        }

        counter.FractionalBits = 0;
        WriteCodingUnit(counter, state, x0, y0, log2Size);
        return counter.FractionalBits * Fraction;
    }

    // Chooses the luma mode of one prediction block (= transform block) and codes it. Returns its distortion.
    private double SearchLumaBlock(int x, int y, int log2Size, byte[] ctx, int trafoDepth)
    {
        int size = 1 << log2Size;
        Span<int> candidates = stackalloc int[3];
        MpmCandidates(x, y, candidates);

        // SATD pre-selection, coarse to fine: planar, DC and every fourth angular mode, then the neighbours at distance
        // two and one of the best angular modes. Refine the winner and competitive MPMs with ordinary
        // quantization; only the selected mode needs the full RDOQ pass.
        const int keep = 1;
        Span<double> satd = stackalloc double[35];
        satd.Fill(double.NaN);
        intra.PrepareReferences(recon[0], strides[0], 0, x, y, log2Size, bitDepth, x, y, this);
        for (int mode = 0; mode < 35; mode = mode < 2 ? mode + 1 : mode + 4)
        {
            SatdCost(mode, x, y, log2Size, candidates, satd);
        }

        Span<int> centres = stackalloc int[2];
        for (int distance = 2; distance >= 1; distance--)
        {
            BestModes(satd, centres, angularOnly: true);
            foreach (int centre in centres)
            {
                SatdCost(centre - distance, x, y, log2Size, candidates, satd);
                SatdCost(centre + distance, x, y, log2Size, candidates, satd);
            }
        }

        Span<int> bestModes = stackalloc int[keep];
        BestModes(satd, bestModes, angularOnly: false);

        Span<int> trial = stackalloc int[keep + 3];
        int trials = 0;
        for (int i = 0; i < keep; i++)
        {
            trial[trials++] = bestModes[i];
        }

        // MPMs have cheaper signalling, but a poor prediction rarely recovers that difference during RDO.
        foreach (int candidate in candidates)
        {
            SatdCost(candidate, x, y, log2Size, candidates, satd);
            if (!trial[..trials].Contains(candidate) && satd[candidate] <= satd[bestModes[0]] * 1.5)
            {
                trial[trials++] = candidate;
            }
        }

        double best = double.MaxValue;
        int bestMode = 0;
        double bestDistortion = 0;
        for (int i = 0; i < trials; i++)
        {
            int mode = trial[i];
            // The SATD pass prepared this block's references; trials write only the block itself, so they stay valid.
            double distortion = CodeBlock(0, x, y, log2Size, mode, x, y, ctx, HevcContext.CbfLuma + (trafoDepth == 0 ? 1 : 0),
                referencesPrepared: true, useRdoq: false);
            double bits = LumaModeBits(mode, candidates, ctx) + ResidualBits(0, x, y, log2Size, ScanIndex(mode, log2Size, true), ctx, trafoDepth);
            double cost = distortion + (lambda * bits);
            if (cost < best)
            {
                best = cost;
                bestMode = mode;
                bestDistortion = distortion;
                if (!settings.Rdoq && i < trials - 1)
                {
                    CopyLuma(x, y, size, bestModeRecon, bestModeLevels, save: true);
                }
            }
        }

        // Refine the selected mode with RDOQ after the cheaper mode search.
        if (settings.Rdoq)
        {
            bestDistortion = CodeBlock(0, x, y, log2Size, bestMode, x, y, ctx,
                HevcContext.CbfLuma + (trafoDepth == 0 ? 1 : 0), referencesPrepared: true);
        }

        // With RDOQ disabled, keep the best trial's reconstruction instead of coding it again.
        if (!settings.Rdoq && bestMode != trial[trials - 1])
        {
            CopyLuma(x, y, size, bestModeRecon, bestModeLevels, save: false);
        }

        Fill(intraModes, x, y, size, (byte)bestMode);
        if (trafoDepth == 0)
        {
            SearchLumaTree(x, y, log2Size, 0, bestMode, ctx, out double distortion, bestDistortion);
            return distortion;
        }

        Fill(tuDepth, x, y, size, (byte)trafoDepth);
        return bestDistortion;
    }

    private void SatdCost(int mode, int x, int y, int log2Size, ReadOnlySpan<int> candidates, Span<double> satd)
    {
        if (mode < 0 || mode > 34 || !double.IsNaN(satd[mode]))
        {
            return;
        }

        intra.PredictPrepared(0, log2Size, mode, bitDepth);
        satd[mode] = Satd(source[0], strides[0], x, y, intra.Prediction, 1 << log2Size)
            + (sqrtLambda * (mode == candidates[0] ? 1 : mode == candidates[1] || mode == candidates[2] ? 2 : 6));
    }

    // The modes with the lowest evaluated SATD costs, best first (ties to the lower mode).
    private static void BestModes(ReadOnlySpan<double> satd, Span<int> best, bool angularOnly)
    {
        Span<double> costs = stackalloc double[best.Length];
        costs.Fill(double.MaxValue);
        best.Fill(-1);
        for (int mode = angularOnly ? 2 : 0; mode < 35; mode++)
        {
            double cost = satd[mode];
            if (double.IsNaN(cost))
            {
                continue;
            }

            for (int i = 0; i < best.Length; i++)
            {
                if (cost < costs[i])
                {
                    for (int k = best.Length - 1; k > i; k--)
                    {
                        costs[k] = costs[k - 1];
                        best[k] = best[k - 1];
                    }

                    costs[i] = cost;
                    best[i] = mode;
                    break;
                }
            }
        }
    }

    // Residual quadtree for the chosen luma mode: the block whole, or split into four with the same mode, by
    // rate-distortion cost (port: HM TEncSearch::xRecurIntraCodingLumaQT, the final full RQT check of the best mode
    // under HHI_RQT_INTRA_SPEEDUP). Returns the cost; leaves the chosen reconstruction, levels and depth map.
    // codedDistortion: the block is already coded whole with this mode (its distortion).
    private double SearchLumaTree(int x, int y, int log2Size, int depth, int mode, byte[] ctx, out double distortion,
        double? codedDistortion = null)
    {
        int size = 1 << log2Size;
        Fill(tuDepth, x, y, size, (byte)depth);
        distortion = codedDistortion ?? CodeBlock(0, x, y, log2Size, mode, x, y, ctx, HevcContext.CbfLuma + (depth == 0 ? 1 : 0));
        bool splittable = log2Size > 2 && depth < settings.MaxTransformDepth;
        int splitContext = HevcContext.SplitTransformFlag + 5 - log2Size;
        double bits = ResidualBits(0, x, y, log2Size, ScanIndex(mode, log2Size, true), ctx, depth)
            + (splittable ? HevcCabacCounter.Cost(ctx[splitContext], 0) * Fraction : 0);
        double cost = distortion + (lambda * bits);

        // A block without residual is predicted as well as it can be at this size; splitting rarely pays off.
        if (!splittable || !HasLevels(0, x, y, log2Size))
        {
            return cost;
        }

        CopyLuma(x, y, size, savedTreeRecon[depth], savedTreeLevels[depth], save: true);
        double splitCost = lambda * HevcCabacCounter.Cost(ctx[splitContext], 1) * Fraction;
        double splitDistortion = 0;
        int half = size >> 1;
        for (int i = 0; i < 4 && splitCost < cost; i++)
        {
            splitCost += SearchLumaTree(x + ((i & 1) * half), y + ((i >> 1) * half), log2Size - 1, depth + 1, mode, ctx,
                out double childDistortion);
            splitDistortion += childDistortion;
        }

        if (splitCost < cost)
        {
            distortion = splitDistortion;
            return splitCost;
        }

        CopyLuma(x, y, size, savedTreeRecon[depth], savedTreeLevels[depth], save: false);
        Fill(tuDepth, x, y, size, (byte)depth);
        return cost;
    }

    private void CopyLuma(int x, int y, int size, ushort[] reconCopy, int[] levelCopy, bool save)
    {
        int stride = strides[0];
        for (int j = 0; j < size; j++)
        {
            int offset = ((y + j) * stride) + x;
            if (save)
            {
                recon[0].AsSpan(offset, size).CopyTo(reconCopy.AsSpan(j * size));
                levels[0].AsSpan(offset, size).CopyTo(levelCopy.AsSpan(j * size));
            }
            else
            {
                reconCopy.AsSpan(j * size, size).CopyTo(recon[0].AsSpan(offset));
                levelCopy.AsSpan(j * size, size).CopyTo(levels[0].AsSpan(offset));
            }
        }
    }

    // Chooses intra_chroma_pred_mode for the coding unit (both chroma blocks, following the luma transform tree).
    // Returns their distortion.
    private double SearchChroma(int x0, int y0, int log2Size, byte[] ctx)
    {
        if (chromaFormat == 0)
        {
            return 0;
        }

        int lumaMode = intraModes[BlockIndex(x0, y0)];
        double best = double.MaxValue;
        int bestSyntax = 4;
        for (int syntax = 4; syntax >= 0; syntax--)
        {
            int mode = ChromaModeFromSyntax(syntax, lumaMode);
            double bits = ChromaModeBits(syntax, ctx);
            double distortion = CodeChromaTree(x0, y0, log2Size, 0, mode, ctx, ref bits);
            double cost = distortion + (lambda * bits);
            if (cost < best)
            {
                best = cost;
                bestSyntax = syntax;
            }
        }

        Fill(chromaSyntax, x0, y0, 1 << log2Size, (byte)bestSyntax);
        double unused = 0;
        return CodeChromaTree(x0, y0, log2Size, 0, ChromaModeFromSyntax(bestSyntax, lumaMode), ctx, ref unused);
    }

    // Codes both chroma blocks at every chroma transform node under the luma node (x, y, log2Size); a luma node split
    // into 4x4 blocks keeps 4x4 chroma at its own level. Adds the residual bits; returns the distortion.
    private double CodeChromaTree(int x, int y, int log2Size, int depth, int mode, byte[] ctx, ref double bits)
    {
        if (log2Size > 3 && tuDepth[BlockIndex(x, y)] > depth)
        {
            int half = 1 << (log2Size - 1);
            double total = 0;
            for (int i = 0; i < 4; i++)
            {
                total += CodeChromaTree(x + ((i & 1) * half), y + ((i >> 1) * half), log2Size - 1, depth + 1, mode, ctx, ref bits);
            }

            return total;
        }

        int chromaLog2 = log2Size - 1;
        int scan = ScanIndex(mode, chromaLog2, false);
        double distortion = 0;
        for (int c = 1; c <= 2; c++)
        {
            distortion += CodeBlock(c, x >> 1, y >> 1, chromaLog2, mode, x, y, ctx, HevcContext.CbfChroma + depth);
            bits += ResidualBits(c, x >> 1, y >> 1, chromaLog2, scan, ctx, depth);
        }

        return distortion;
    }

    private double LumaModeBits(int mode, ReadOnlySpan<int> candidates, byte[] ctx)
    {
        counter.FractionalBits = 0;
        Span<byte> state = stackalloc byte[HevcContext.Count];
        ctx.CopyTo(state);
        HevcSyntaxWriter.IntraLumaModes(counter, state, [mode], candidates);
        return counter.FractionalBits * Fraction;
    }

    private double ChromaModeBits(int syntax, byte[] ctx)
    {
        counter.FractionalBits = 0;
        Span<byte> state = stackalloc byte[HevcContext.Count];
        ctx.CopyTo(state);
        HevcSyntaxWriter.IntraChromaMode(counter, state, syntax);
        return counter.FractionalBits * Fraction;
    }

    // cbf plus residual_coding bits of the levels currently stored for the block.
    private double ResidualBits(int cIdx, int x, int y, int log2Size, int scanIdx, byte[] ctx, int trafoDepth)
    {
        counter.FractionalBits = 0;
        Span<byte> state = stackalloc byte[HevcContext.Count];
        ctx.CopyTo(state);
        bool coded = HasLevels(cIdx, x, y, log2Size);
        if (cIdx == 0)
        {
            HevcSyntaxWriter.CbfLuma(counter, state, coded, trafoDepth);
        }
        else
        {
            HevcSyntaxWriter.CbfChroma(counter, state, coded, trafoDepth);
        }

        if (coded)
        {
            WriteResidual(counter, state, cIdx, x, y, log2Size, scanIdx);
        }

        return counter.FractionalBits * Fraction;
    }

    // Predicts, transforms, quantizes and reconstructs one transform block; returns its squared error.
    // (xTbY, yTbY) is the luma location of the block for neighbour availability. referencesPrepared: the intra
    // predictor already holds this block's reference samples (PrepareReferences). Mode trials disable useRdoq;
    // the selected mode and its residual tree use the configured quantization.
    private double CodeBlock(int cIdx, int x, int y, int log2Size, int mode, int xTbY, int yTbY, byte[] ctx, int cbfContext,
        bool referencesPrepared = false, bool useRdoq = true)
    {
        int size = 1 << log2Size;
        int count = size * size;
        int stride = strides[cIdx];
        ushort[] plane = recon[cIdx];
        ushort[] original = source[cIdx];
        if (referencesPrepared)
        {
            intra.PredictPrepared(cIdx, log2Size, mode, bitDepth);
        }
        else
        {
            intra.Predict(plane, stride, cIdx, x, y, log2Size, mode, bitDepth, xTbY, yTbY, this);
        }

        int[] prediction = intra.Prediction;
        for (int j = 0; j < size; j++)
        {
            HevcSamples.Subtract(original.AsSpan(((y + j) * stride) + x, size), prediction.AsSpan(j * size, size),
                residual.AsSpan(j * size, size));
        }

        bool dst = cIdx == 0 && log2Size == 2;
        int blockQp = (cIdx == 0 ? qp : chromaQp) + qpOffset;
        HevcForwardTransform.Forward(residual, coefficients, temporary, log2Size, dst, bitDepth);
        int nonZero = settings.Rdoq && useRdoq
            ? rdoq.Quantize(coefficients, quantized, log2Size, blockQp, cIdx == 0, ScanIndex(mode, log2Size, cIdx == 0), ctx,
                cbfContext, lambda, settings.SignHidingEnabled, bitDepth)
            : HevcForwardTransform.Quantize(coefficients, quantized, log2Size, blockQp, bitDepth);
        Span<int> levelPlane = levels[cIdx];
        for (int j = 0; j < size; j++)
        {
            quantized.AsSpan(j * size, size).CopyTo(levelPlane.Slice(((y + j) * stride) + x, size));
        }

        Span<int> restored = reconstructed.AsSpan(0, count);
        if (nonZero > 0)
        {
            Span<int> dequantized = coefficients.AsSpan(0, count);
            quantized.AsSpan(0, count).CopyTo(dequantized);
            HevcTransform.Dequantize(dequantized, log2Size, blockQp, bitDepth, default);
            HevcTransform.Inverse(dequantized, restored, temporary, log2Size, dst, bitDepth);
        }
        else
        {
            restored.Clear();
        }

        // Integer sums of squares are exact, as were the double sums they replace.
        long error = 0;
        for (int j = 0; j < size; j++)
        {
            int offset = ((y + j) * stride) + x;
            error += HevcSamples.StoreClippedError(plane.AsSpan(offset, size), prediction.AsSpan(j * size, size),
                restored.Slice(j * size, size), maxSample, original.AsSpan(offset, size));
        }

        return error;
    }

    // Sum of absolute 4x4 Hadamard-transformed differences (port: HM TComRdCost::xCalcHADs4x4, normalized as HM).
    private static double Satd(ushort[] original, int stride, int x0, int y0, int[] prediction, int size) =>
        Vector128.IsHardwareAccelerated
            ? SatdVector(original, stride, x0, y0, prediction, size)
            : SatdScalar(original, stride, x0, y0, prediction, size);

    internal static double SatdScalar(ushort[] original, int stride, int x0, int y0, int[] prediction, int size)
    {
        Span<int> d = stackalloc int[16];
        Span<int> m = stackalloc int[16];
        long total = 0;
        for (int by = 0; by < size; by += 4)
        {
            for (int bx = 0; bx < size; bx += 4)
            {
                for (int j = 0; j < 4; j++)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        d[(j * 4) + i] = original[((y0 + by + j) * stride) + x0 + bx + i] - prediction[((by + j) * size) + bx + i];
                    }
                }

                for (int j = 0; j < 4; j++)
                {
                    int a0 = d[(j * 4) + 0] + d[(j * 4) + 3];
                    int a1 = d[(j * 4) + 1] + d[(j * 4) + 2];
                    int a2 = d[(j * 4) + 1] - d[(j * 4) + 2];
                    int a3 = d[(j * 4) + 0] - d[(j * 4) + 3];
                    m[(j * 4) + 0] = a0 + a1;
                    m[(j * 4) + 2] = a0 - a1;
                    m[(j * 4) + 1] = a2 + a3;
                    m[(j * 4) + 3] = a3 - a2;
                }

                int sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    int a0 = m[i] + m[12 + i];
                    int a1 = m[4 + i] + m[8 + i];
                    int a2 = m[4 + i] - m[8 + i];
                    int a3 = m[i] - m[12 + i];
                    sum += Math.Abs(a0 + a1) + Math.Abs(a0 - a1) + Math.Abs(a2 + a3) + Math.Abs(a3 - a2);
                }

                total += (sum + 1) >> 1;
            }
        }

        return total;
    }

    // Saved reconstruction, levels, maps and contexts of a square luma region (with its chroma).
    private sealed class RegionState
    {
        private readonly ushort[][] reconCopy = [new ushort[32 * 32], new ushort[16 * 16], new ushort[16 * 16]];
        private readonly int[][] levelCopy = [new int[32 * 32], new int[16 * 16], new int[16 * 16]];
        private readonly byte[][] mapCopy = [new byte[64], new byte[64], new byte[64], new byte[64], new byte[64]];
        private readonly byte[] contextCopy = new byte[HevcContext.Count];
        private int x0;
        private int y0;
        private int size;

        public void Save(HevcPictureEncoder encoder, int x, int y, int blockSize, byte[] ctx)
        {
            x0 = x;
            y0 = y;
            size = blockSize;
            ctx.CopyTo(contextCopy, 0);
            for (int c = 0; c < encoder.recon.Length; c++)
            {
                int shift = c == 0 ? 0 : 1;
                int s = size >> shift;
                for (int j = 0; j < s; j++)
                {
                    int offset = (((y0 >> shift) + j) * encoder.strides[c]) + (x0 >> shift);
                    encoder.recon[c].AsSpan(offset, s).CopyTo(reconCopy[c].AsSpan(j * s));
                    encoder.levels[c].AsSpan(offset, s).CopyTo(levelCopy[c].AsSpan(j * s));
                }
            }

            byte[][] maps = [encoder.ctDepth, encoder.intraModes, encoder.partNxN, encoder.chromaSyntax, encoder.tuDepth];
            int blocks = size >> 2;
            for (int m = 0; m < maps.Length; m++)
            {
                for (int j = 0; j < blocks; j++)
                {
                    maps[m].AsSpan(encoder.BlockIndex(x0, y0 + (j << 2)), blocks).CopyTo(mapCopy[m].AsSpan(j * blocks));
                }
            }
        }

        public void Restore(HevcPictureEncoder encoder, byte[] ctx)
        {
            contextCopy.CopyTo(ctx, 0);
            for (int c = 0; c < encoder.recon.Length; c++)
            {
                int shift = c == 0 ? 0 : 1;
                int s = size >> shift;
                for (int j = 0; j < s; j++)
                {
                    int offset = (((y0 >> shift) + j) * encoder.strides[c]) + (x0 >> shift);
                    reconCopy[c].AsSpan(j * s, s).CopyTo(encoder.recon[c].AsSpan(offset));
                    levelCopy[c].AsSpan(j * s, s).CopyTo(encoder.levels[c].AsSpan(offset));
                }
            }

            byte[][] maps = [encoder.ctDepth, encoder.intraModes, encoder.partNxN, encoder.chromaSyntax, encoder.tuDepth];
            int blocks = size >> 2;
            for (int m = 0; m < maps.Length; m++)
            {
                for (int j = 0; j < blocks; j++)
                {
                    mapCopy[m].AsSpan(j * blocks, blocks).CopyTo(maps[m].AsSpan(encoder.BlockIndex(x0, y0 + (j << 2))));
                }
            }
        }
    }
}
