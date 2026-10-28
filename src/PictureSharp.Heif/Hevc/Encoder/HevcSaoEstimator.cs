namespace PictureSharp.Formats.Heif.Hevc;

// Sample adaptive offset parameters of one CTB: per component type (0 off, 1 band, 2 edge), band position or edge
// class, and four offsets (edge offsets already signed: positive for categories 1-2, negative for 3-4).
internal struct HevcSaoParameters
{
    public bool MergeLeft;
    public bool MergeUp;
    public byte TypeLuma;
    public byte TypeChroma;
    public byte ClassLuma;
    public byte ClassChroma;
    public byte BandCb;
    public byte BandCr;
    public sbyte O0, O1, O2, O3, O4, O5, O6, O7, O8, O9, O10, O11;

    public readonly int Offset(int component, int index) => (component * 4 + index) switch
    {
        0 => O0, 1 => O1, 2 => O2, 3 => O3, 4 => O4, 5 => O5, 6 => O6, 7 => O7, 8 => O8, 9 => O9, 10 => O10, _ => O11,
    };

    public void SetOffset(int component, int index, int value)
    {
        sbyte v = (sbyte)value;
        switch (component * 4 + index)
        {
            case 0: O0 = v; break;
            case 1: O1 = v; break;
            case 2: O2 = v; break;
            case 3: O3 = v; break;
            case 4: O4 = v; break;
            case 5: O5 = v; break;
            case 6: O6 = v; break;
            case 7: O7 = v; break;
            case 8: O8 = v; break;
            case 9: O9 = v; break;
            case 10: O10 = v; break;
            default: O11 = v; break;
        }
    }

    public readonly int Type(int component) => component == 0 ? TypeLuma : TypeChroma;

    public readonly int Class(int component) => component == 0 ? ClassLuma : component == 1 ? (TypeChroma == 1 ? BandCb : ClassChroma) : (TypeChroma == 1 ? BandCr : ClassChroma);
}

// SAO decisions from the deblocked picture: per CTB and component, statistics of the source minus the deblocked samples
// per edge category and band, offsets chosen by rate-distortion, and merges with the left or upper CTB.
// port: HM source/Lib/TLibEncoder/TEncSampleAdaptiveOffset.cpp (getBlkStats, deriveOffsets, deriveModeNewRDO,
//       deriveModeMergeRDO) in a reduced form; distortion is estimated from the statistics as HM does.
internal sealed class HevcSaoEstimator(ushort[][] source, ushort[][] deblocked, int[] strides, int width, int height,
    int log2CtbSize, int bitDepth = 8)
{
    // 7.4.9.3.2: offsets up to (1 << (Min(bitDepth, 10) - 5)) - 1; 32 bands of 1 << (bitDepth - 5) values.
    private readonly int maxOffset = HevcSyntaxWriter.SaoMaxOffset(bitDepth);
    private readonly int bandShift = bitDepth - 5;

    private double lambda;

    // Statistics: [component][type 0..3 edge classes, 4 band][category or band] sums and counts.
    private readonly long[,,] sums = new long[3, 5, 32];
    private readonly long[,,] counts = new long[3, 5, 32];

    public HevcSaoParameters Estimate(int ctbX, int ctbY, int components, double ctbLambda, HevcSaoParameters? left,
        HevcSaoParameters? up)
    {
        lambda = ctbLambda;
        Array.Clear(sums);
        Array.Clear(counts);
        for (int c = 0; c < components; c++)
        {
            Collect(c, ctbX, ctbY);
        }

        var best = default(HevcSaoParameters);
        double bestCost = lambda * 1; // type 0 for luma and chroma, about one bit each
        best.TypeLuma = 0;
        best.TypeChroma = 0;

        // New parameters: luma and chroma decided separately (chroma components share type and edge class).
        double newCost = 0;
        var parameters = default(HevcSaoParameters);
        newCost += Decide(ref parameters, 0, 0);
        if (components > 1)
        {
            newCost += Decide(ref parameters, 1, 2);
        }

        if (newCost < bestCost)
        {
            best = parameters;
            bestCost = newCost;
        }

        foreach ((HevcSaoParameters? candidate, bool isLeft) in new[] { (left, true), (up, false) })
        {
            if (candidate is not { } merge)
            {
                continue;
            }

            double cost = lambda * 1;
            for (int c = 0; c < components; c++)
            {
                cost += Distortion(merge, c);
            }

            if (cost < bestCost)
            {
                best = merge;
                best.MergeLeft = isLeft;
                best.MergeUp = !isLeft;
                bestCost = cost;
            }
        }

        return best;
    }

    // Chooses type, class and offsets for components [first, last] (one, or Cb and Cr sharing type and class).
    private double Decide(ref HevcSaoParameters parameters, int first, int last)
    {
        double offCost = lambda * 1;
        double bestCost = offCost;
        int bestType = 0;
        int bestClass = 0;
        Span<int> bestOffsets = stackalloc int[8];
        Span<int> bestBands = stackalloc int[2];
        Span<int> offsets = stackalloc int[8];
        for (int edgeClass = 0; edgeClass < 4; edgeClass++)
        {
            double cost = lambda * (2 + 2); // type bins and class
            for (int c = first; c <= last; c++)
            {
                for (int category = 0; category < 4; category++)
                {
                    long count = counts[c, edgeClass, category];
                    long sum = sums[c, edgeClass, category];
                    int estimate = count == 0 ? 0 : (int)Math.Round((double)sum / count);
                    estimate = category < 2 ? Math.Clamp(estimate, 0, maxOffset) : Math.Clamp(estimate, -maxOffset, 0);
                    (int offset, double offsetCost) = RefineOffset(estimate, sum, count);
                    offsets[((c - first) * 4) + category] = offset;
                    cost += offsetCost;
                }
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestType = 2;
                bestClass = edgeClass;
                offsets.CopyTo(bestOffsets);
            }
        }

        // Band offset: the best start band per component, four consecutive bands.
        double bandCost = lambda * 2;
        Span<int> bands = stackalloc int[2];
        Span<int> candidate = stackalloc int[4];
        for (int c = first; c <= last; c++)
        {
            double componentBest = double.MaxValue;
            for (int start = 0; start < 32; start++)
            {
                double cost = lambda * 5;
                for (int k = 0; k < 4; k++)
                {
                    int band = (start + k) & 31;
                    long count = counts[c, 4, band];
                    long sum = sums[c, 4, band];
                    int estimate = count == 0 ? 0 : Math.Clamp((int)Math.Round((double)sum / count), -maxOffset, maxOffset);
                    (int offset, double offsetCost) = RefineOffset(estimate, sum, count);
                    candidate[k] = offset;
                    cost += offsetCost + (offset != 0 ? lambda : 0);
                }

                if (cost < componentBest)
                {
                    componentBest = cost;
                    bands[c - first] = start;
                    candidate.CopyTo(offsets.Slice((c - first) * 4, 4));
                }
            }

            bandCost += componentBest;
        }

        if (bandCost < bestCost)
        {
            bestCost = bandCost;
            bestType = 1;
            offsets.CopyTo(bestOffsets);
            bands.CopyTo(bestBands);
        }

        if (first == 0)
        {
            parameters.TypeLuma = (byte)bestType;
            parameters.ClassLuma = (byte)(bestType == 1 ? bestBands[0] : bestClass);
        }
        else
        {
            parameters.TypeChroma = (byte)bestType;
            parameters.ClassChroma = (byte)bestClass;
            parameters.BandCb = (byte)bestBands[0];
            parameters.BandCr = (byte)bestBands[1];
        }

        for (int c = first; c <= last; c++)
        {
            for (int k = 0; k < 4; k++)
            {
                parameters.SetOffset(c, k, bestType == 0 ? 0 : bestOffsets[((c - first) * 4) + k]);
            }
        }

        return bestCost;
    }

    // port: HM TEncSampleAdaptiveOffset::estIterOffset — try magnitudes from the estimate down to zero.
    private (int Offset, double Cost) RefineOffset(int estimate, long sum, long count)
    {
        int bestOffset = 0;
        double bestCost = lambda * 1;
        int step = estimate > 0 ? -1 : 1;
        for (int offset = estimate; offset != 0; offset += step)
        {
            double distortion = (count * (double)offset * offset) - (2.0 * offset * sum);
            double cost = distortion + (lambda * (Math.Min(Math.Abs(offset) + 1, maxOffset)));
            if (cost < bestCost)
            {
                bestCost = cost;
                bestOffset = offset;
            }
        }

        return (bestOffset, bestCost);
    }

    // Distortion change of applying a parameter set to this CTB's statistics.
    private double Distortion(HevcSaoParameters parameters, int c)
    {
        int type = parameters.Type(c);
        if (type == 0)
        {
            return 0;
        }

        double total = 0;
        for (int k = 0; k < 4; k++)
        {
            int offset = parameters.Offset(c, k);
            int index = type == 2 ? k : (parameters.Class(c) + k) & 31;
            int statistics = type == 2 ? parameters.Class(c) : 4;
            total += (counts[c, statistics, index] * (double)offset * offset) - (2.0 * offset * sums[c, statistics, index]);
        }

        return total;
    }

    // Accumulates source minus deblocked differences per edge category (all four classes) and per band.
    private void Collect(int c, int ctbX, int ctbY)
    {
        int shift = c == 0 ? 0 : 1;
        int planeWidth = width >> shift;
        int planeHeight = height >> shift;
        int ctbSize = (1 << log2CtbSize) >> shift;
        int x0 = ctbX * ctbSize;
        int y0 = ctbY * ctbSize;
        int x1 = Math.Min(x0 + ctbSize, planeWidth);
        int y1 = Math.Min(y0 + ctbSize, planeHeight);
        ushort[] plane = deblocked[c];
        ushort[] original = source[c];
        int stride = strides[c];
        ReadOnlySpan<int> dx = [-1, 0, -1, 1];
        ReadOnlySpan<int> dy = [0, -1, -1, -1];
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * stride) + x;
                int value = plane[i];
                int difference = original[i] - value;
                int band = value >> bandShift;
                sums[c, 4, band] += difference;
                counts[c, 4, band]++;
                for (int k = 0; k < 4; k++)
                {
                    int ax = x + dx[k];
                    int ay = y + dy[k];
                    int bx = x - dx[k];
                    int by = y - dy[k];
                    if (ax < 0 || ay < 0 || bx < 0 || by < 0 || ax >= planeWidth || bx >= planeWidth || ay >= planeHeight || by >= planeHeight)
                    {
                        continue;
                    }

                    int edge = Math.Sign(value - plane[(ay * stride) + ax]) + Math.Sign(value - plane[(by * stride) + bx]);
                    int category = edge switch { -2 => 0, -1 => 1, 1 => 2, 2 => 3, _ => -1 };
                    if (category >= 0)
                    {
                        sums[c, k, category] += difference;
                        counts[c, k, category]++;
                    }
                }
            }
        }
    }
}
