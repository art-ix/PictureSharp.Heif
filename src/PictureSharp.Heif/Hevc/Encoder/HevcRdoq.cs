namespace PictureSharp.Formats.Heif.Hevc;

// Rate-distortion optimized quantization with sign data hiding.
// port: HM source/Lib/TLibCommon/TComTrQuant.cpp xRateDistOptQuant, xGetCodedLevel, xGetICRate, xGetRateLast,
//       signBitHidingHDQ (the RDOQ variant). Costs use HM's units: squared error times errScale (2^15 scaled) plus
//       lambda times rate in 1/32768 bits; rates come from the current context states.
internal sealed class HevcRdoq
{
    private const int MaxCoefficients = 32 * 32;
    private const int SignBit = 1 << 15;

    private static ReadOnlySpan<int> QuantScales => [26214, 23302, 20560, 18396, 16384, 14564];
    private static ReadOnlySpan<int> InverseQuantScales => [40, 45, 51, 57, 64, 72];
    private static ReadOnlySpan<byte> GroupIndex =>
        [0, 1, 2, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9];

    private readonly double[] costCoefficient = new double[MaxCoefficients];
    private readonly double[] costSig = new double[MaxCoefficients];
    private readonly double[] costZero = new double[MaxCoefficients];
    private readonly int[] rateIncUp = new int[MaxCoefficients];
    private readonly int[] rateIncDown = new int[MaxCoefficients];
    private readonly int[] sigRateDelta = new int[MaxCoefficients];
    private readonly int[] deltaU = new int[MaxCoefficients];
    private int[] scan = [];

    // Scan position to raster position for log2 sizes 2..5 and the three scan orders, built once.
    private static readonly int[][] ScanTables = BuildScanTables();
    private readonly double[] costGroupSig = new double[64];
    private readonly byte[] groupFlags = new byte[64];

    // Quantizes coefficients into levels (raster order) and returns the number of nonzero levels.
    public int Quantize(ReadOnlySpan<int> coefficients, Span<int> levels, int log2Size, int qp, bool luma, int scanIdx,
        ReadOnlySpan<byte> contexts, int cbfContext, double lambda, bool signHiding)
    {
        int size = 1 << log2Size;
        int count = size * size;
        int transformShift = 15 - 8 - log2Size;
        int qBits = 14 + (qp / 6) + transformShift;
        int quantScale = QuantScales[qp % 6];
        double errorScale = (1 << 15) * Math.Pow(2, -2.0 * transformShift) / quantScale / quantScale;
        int log2Groups = log2Size - 2;
        int groupsWide = 1 << log2Groups;
        int groupCount = 1 << (log2Groups * 2);
        ReadOnlySpan<byte> subBlockScan = HevcScan.Get(log2Groups, scanIdx);

        // No coefficient reaches level 1 even when rounded up: the result is the all-zero block.
        long threshold = 1L << (qBits - 1);
        bool anyLevel = false;
        for (int i = 0; i < count && !anyLevel; i++)
        {
            anyLevel = (long)Math.Abs(coefficients[i]) * quantScale >= threshold;
        }

        if (!anyLevel)
        {
            levels[..count].Clear();
            return 0;
        }

        scan = ScanTables[((log2Size - 2) * 3) + scanIdx];

        int sigBase = luma ? HevcContext.SigCoeffLuma : HevcContext.SigCoeffChroma;
        int firstSigContext = log2Size == 2 ? 0 : log2Size == 3 ? (luma ? (scanIdx == HevcScan.Diagonal ? 9 : 15) : 9) : (luma ? 21 : 12);
        int greater1Base = HevcContext.Greater1 + (luma ? 0 : 16);
        int greater2Base = HevcContext.Greater2 + (luma ? 0 : 4);
        int groupBase = HevcContext.CodedSubBlock + (luma ? 0 : 2);
        Array.Clear(costCoefficient, 0, count);
        Array.Clear(costSig, 0, count);
        Array.Clear(rateIncUp, 0, count);
        Array.Clear(rateIncDown, 0, count);
        Array.Clear(sigRateDelta, 0, count);
        Array.Clear(deltaU, 0, count);
        Array.Clear(costGroupSig, 0, groupCount);
        Array.Clear(groupFlags, 0, groupCount);
        levels[..count].Clear();

        double blockUncodedCost = 0;
        double baseCost = 0;
        int lastScanPos = -1;
        int lastGroup = -1;
        int ctxSet = 0;
        int c1 = 1;
        int c2 = 0;
        int c1Index = 0;
        int c2Index = 0;
        int rice = 0;
        for (int g = groupCount - 1; g >= 0; g--)
        {
            int xS = subBlockScan[g] & 15;
            int yS = subBlockScan[g] >> 4;
            int pattern = (xS < groupsWide - 1 ? groupFlags[(yS * groupsWide) + xS + 1] : 0)
                | ((yS < groupsWide - 1 ? groupFlags[((yS + 1) * groupsWide) + xS] : 0) << 1);
            double sigCostGroup = 0;
            double sigCostFirst = 0;
            double codedLevelAndDistortion = 0;
            double uncodedDistortion = 0;
            int nonZeroBeforeFirst = 0;
            for (int p = 15; p >= 0; p--)
            {
                int scanPos = (g * 16) + p;
                int blockPos = scan[scanPos];
                long scaled = Math.Min((long)Math.Abs(coefficients[blockPos]) * quantScale, int.MaxValue - (1L << (qBits - 1)));
                int maxLevel = (int)Math.Min(32767, (scaled + (1L << (qBits - 1))) >> qBits);
                double error = scaled;
                costZero[scanPos] = error * error * errorScale;
                blockUncodedCost += costZero[scanPos];
                levels[blockPos] = maxLevel;
                if (maxLevel > 0 && lastScanPos < 0)
                {
                    lastScanPos = scanPos;
                    ctxSet = (g == 0 || !luma ? 0 : 2);
                    lastGroup = g;
                }

                if (lastScanPos >= 0)
                {
                    int oneContext = greater1Base + (4 * ctxSet) + c1;
                    int absContext = greater2Base + ctxSet;
                    int level;
                    if (scanPos == lastScanPos)
                    {
                        level = CodedLevel(ref costCoefficient[scanPos], costZero[scanPos], ref costSig[scanPos], scaled, maxLevel,
                            -1, oneContext, absContext, rice, c1Index, c2Index, qBits, errorScale, true, contexts, lambda);
                    }
                    else
                    {
                        int sigContext = sigBase + HevcSyntaxWriter.SignificanceContext(blockPos % size, blockPos / size, log2Size,
                            pattern, firstSigContext, luma);
                        level = CodedLevel(ref costCoefficient[scanPos], costZero[scanPos], ref costSig[scanPos], scaled, maxLevel,
                            sigContext, oneContext, absContext, rice, c1Index, c2Index, qBits, errorScale, false, contexts, lambda);
                        sigRateDelta[blockPos] = Bits(contexts, sigContext, 1) - Bits(contexts, sigContext, 0);
                    }

                    deltaU[blockPos] = (int)((scaled - ((long)level << qBits)) >> (qBits - 8));
                    if (level > 0)
                    {
                        int rateNow = LevelRate(level, oneContext, absContext, rice, c1Index, c2Index, contexts);
                        rateIncUp[blockPos] = LevelRate(level + 1, oneContext, absContext, rice, c1Index, c2Index, contexts) - rateNow;
                        rateIncDown[blockPos] = LevelRate(level - 1, oneContext, absContext, rice, c1Index, c2Index, contexts) - rateNow;
                    }
                    else
                    {
                        rateIncUp[blockPos] = Bits(contexts, oneContext, 0);
                    }

                    levels[blockPos] = level;
                    baseCost += costCoefficient[scanPos];
                    int baseLevel = c1Index < 8 ? 2 + (c2Index < 1 ? 1 : 0) : 1;
                    if (level >= baseLevel && level > 3 << rice)
                    {
                        rice = Math.Min(rice + 1, 4);
                    }

                    if (level >= 1)
                    {
                        c1Index++;
                    }

                    if (level > 1)
                    {
                        c1 = 0;
                        c2 += c2 < 2 ? 1 : 0;
                        c2Index++;
                    }
                    else if (c1 is > 0 and < 3 && level > 0)
                    {
                        c1++;
                    }

                    if (scanPos % 16 == 0 && scanPos > 0)
                    {
                        ctxSet = ((g - 1) == 0 || !luma ? 0 : 2) + (c1 == 0 ? 1 : 0);
                        c1 = 1;
                        c2 = 0;
                        c1Index = 0;
                        c2Index = 0;
                        rice = 0;
                    }
                }
                else
                {
                    baseCost += costZero[scanPos];
                }

                sigCostGroup += costSig[scanPos];
                if (p == 0)
                {
                    sigCostFirst = costSig[scanPos];
                }

                if (levels[blockPos] != 0)
                {
                    groupFlags[(yS * groupsWide) + xS] = 1;
                    codedLevelAndDistortion += costCoefficient[scanPos] - costSig[scanPos];
                    uncodedDistortion += costZero[scanPos];
                    if (p != 0)
                    {
                        nonZeroBeforeFirst++;
                    }
                }
            }

            if (lastGroup < 0)
            {
                continue;
            }

            if (g == 0)
            {
                groupFlags[(yS * groupsWide) + xS] = 1;
                continue;
            }

            int right = xS < groupsWide - 1 ? groupFlags[(yS * groupsWide) + xS + 1] : 0;
            int below = yS < groupsWide - 1 ? groupFlags[((yS + 1) * groupsWide) + xS] : 0;
            int groupContext = groupBase + (right | below);
            if (groupFlags[(yS * groupsWide) + xS] == 0)
            {
                baseCost += Cost(contexts, groupContext, 0, lambda) - sigCostGroup;
                costGroupSig[g] = Cost(contexts, groupContext, 0, lambda);
            }
            else if (g < lastGroup)
            {
                if (nonZeroBeforeFirst == 0)
                {
                    baseCost -= sigCostFirst;
                    sigCostGroup -= sigCostFirst;
                }

                double costZeroGroup = baseCost;
                baseCost += Cost(contexts, groupContext, 1, lambda);
                costZeroGroup += Cost(contexts, groupContext, 0, lambda);
                costGroupSig[g] = Cost(contexts, groupContext, 1, lambda);
                costZeroGroup += uncodedDistortion - codedLevelAndDistortion - sigCostGroup;
                if (costZeroGroup < baseCost)
                {
                    groupFlags[(yS * groupsWide) + xS] = 0;
                    baseCost = costZeroGroup;
                    costGroupSig[g] = Cost(contexts, groupContext, 0, lambda);
                    for (int p = 15; p >= 0; p--)
                    {
                        int scanPos = (g * 16) + p;
                        int blockPos = scan[scanPos];
                        if (levels[blockPos] != 0)
                        {
                            levels[blockPos] = 0;
                            costCoefficient[scanPos] = costZero[scanPos];
                            costSig[scanPos] = 0;
                        }
                    }
                }
            }
        }

        if (lastScanPos < 0)
        {
            return 0;
        }

        // Choose the last significant position (and whether to code the block at all).
        double bestCost = blockUncodedCost + Cost(contexts, cbfContext, 0, lambda);
        baseCost += Cost(contexts, cbfContext, 1, lambda);
        int bestLastPlusOne = 0;
        bool foundLast = false;
        for (int g = lastGroup; g >= 0 && !foundLast; g--)
        {
            int xS = subBlockScan[g] & 15;
            int yS = subBlockScan[g] >> 4;
            baseCost -= costGroupSig[g];
            if (groupFlags[(yS * groupsWide) + xS] == 0)
            {
                continue;
            }

            for (int p = 15; p >= 0; p--)
            {
                int scanPos = (g * 16) + p;
                if (scanPos > lastScanPos)
                {
                    continue;
                }

                int blockPos = scan[scanPos];
                if (levels[blockPos] != 0)
                {
                    int x = blockPos % size;
                    int y = blockPos / size;
                    double lastCost = scanIdx == HevcScan.Vertical ? LastCost(y, x, log2Size, luma, contexts, lambda) : LastCost(x, y, log2Size, luma, contexts, lambda);
                    double total = baseCost + lastCost - costSig[scanPos];
                    if (total < bestCost)
                    {
                        bestLastPlusOne = scanPos + 1;
                        bestCost = total;
                    }

                    if (levels[blockPos] > 1)
                    {
                        foundLast = true;
                        break;
                    }

                    baseCost -= costCoefficient[scanPos];
                    baseCost += costZero[scanPos];
                }
                else
                {
                    baseCost -= costSig[scanPos];
                }
            }
        }

        int absoluteSum = 0;
        for (int scanPos = 0; scanPos < bestLastPlusOne; scanPos++)
        {
            int blockPos = scan[scanPos];
            int level = levels[blockPos];
            absoluteSum += level;
            levels[blockPos] = coefficients[blockPos] < 0 ? -level : level;
        }

        for (int scanPos = bestLastPlusOne; scanPos <= lastScanPos; scanPos++)
        {
            levels[scan[scanPos]] = 0;
        }

        if (signHiding && absoluteSum >= 2)
        {
            HideSigns(coefficients, levels, groupCount, qp, lambda);
        }

        int nonZero = 0;
        for (int i = 0; i < count; i++)
        {
            nonZero += levels[i] != 0 ? 1 : 0;
        }

        return nonZero;
    }

    private static int[][] BuildScanTables()
    {
        var tables = new int[12][];
        ReadOnlySpan<byte> positions;
        for (int log2Size = 2; log2Size <= 5; log2Size++)
        {
            int size = 1 << log2Size;
            int groupCount = 1 << ((log2Size - 2) * 2);
            for (int scanIdx = 0; scanIdx < 3; scanIdx++)
            {
                ReadOnlySpan<byte> subBlockScan = HevcScan.Get(log2Size - 2, scanIdx);
                positions = HevcScan.Get(2, scanIdx);
                int[] table = new int[size * size];
                for (int s = 0; s < groupCount; s++)
                {
                    int xS = subBlockScan[s] & 15;
                    int yS = subBlockScan[s] >> 4;
                    for (int p = 0; p < 16; p++)
                    {
                        table[(s * 16) + p] = (((yS << 2) + (positions[p] >> 4)) * size) + (xS << 2) + (positions[p] & 15);
                    }
                }

                tables[((log2Size - 2) * 3) + scanIdx] = table;
            }
        }

        return tables;
    }

    // port: TComTrQuant::signBitHidingHDQ as called after RDOQ: in each coefficient group spanning at least four scan
    // positions, adjust the cheapest level by one so the parity of the sum encodes the first coefficient's sign.
    private void HideSigns(ReadOnlySpan<int> coefficients, Span<int> levels, int groupCount, int qp, double lambda)
    {
        double inverse = InverseQuantScales[qp % 6];
        long rdFactor = (long)((inverse * inverse * (1L << (2 * (qp / 6))) / lambda / 16) + 0.5);
        int lastCg = -1;
        for (int g = groupCount - 1; g >= 0; g--)
        {
            int first = 16;
            int last = -1;
            for (int n = 15; n >= 0; n--)
            {
                if (levels[scan[(g * 16) + n]] != 0)
                {
                    last = n;
                    break;
                }
            }

            for (int n = 0; n < 16; n++)
            {
                if (levels[scan[(g * 16) + n]] != 0)
                {
                    first = n;
                    break;
                }
            }

            int sum = 0;
            for (int n = first; n <= last; n++)
            {
                sum += levels[scan[(g * 16) + n]];
            }

            if (last >= 0 && lastCg == -1)
            {
                lastCg = 1;
            }

            if (last - first >= 4)
            {
                int signBit = levels[scan[(g * 16) + first]] > 0 ? 0 : 1;
                if (signBit != (sum & 1))
                {
                    long minCost = long.MaxValue;
                    int minPosition = -1;
                    int finalChange = 0;
                    for (int n = lastCg == 1 ? last : 15; n >= 0; n--)
                    {
                        int blockPos = scan[(g * 16) + n];
                        long cost;
                        int change;
                        if (levels[blockPos] != 0)
                        {
                            long costUp = (rdFactor * -deltaU[blockPos]) + rateIncUp[blockPos];
                            long costDown = (rdFactor * deltaU[blockPos]) + rateIncDown[blockPos]
                                - (Math.Abs(levels[blockPos]) == 1 ? sigRateDelta[blockPos] : 0);
                            if (lastCg == 1 && last == n && Math.Abs(levels[blockPos]) == 1)
                            {
                                costDown -= 4 << 15;
                            }

                            if (costUp < costDown)
                            {
                                cost = costUp;
                                change = 1;
                            }
                            else
                            {
                                change = -1;
                                cost = n == first && Math.Abs(levels[blockPos]) == 1 ? long.MaxValue : costDown;
                            }
                        }
                        else
                        {
                            cost = (rdFactor * -Math.Abs(deltaU[blockPos])) + (1 << 15) + rateIncUp[blockPos] + sigRateDelta[blockPos];
                            change = 1;
                            if (n < first && (coefficients[blockPos] >= 0 ? 0 : 1) != signBit)
                            {
                                cost = long.MaxValue;
                            }
                        }

                        if (cost < minCost)
                        {
                            minCost = cost;
                            finalChange = change;
                            minPosition = blockPos;
                        }
                    }

                    if (Math.Abs(levels[minPosition]) == 32767)
                    {
                        finalChange = -1;
                    }

                    levels[minPosition] += coefficients[minPosition] >= 0 ? finalChange : -finalChange;
                }
            }

            if (lastCg == 1)
            {
                lastCg = 0;
            }
        }
    }

    // port: TComTrQuant::xGetCodedLevel
    private static int CodedLevel(ref double codedCost, double codedCostZero, ref double codedCostSig, long scaled, int maxLevel,
        int sigContext, int oneContext, int absContext, int rice, int c1Index, int c2Index, int qBits, double errorScale,
        bool last, ReadOnlySpan<byte> contexts, double lambda)
    {
        double currentCostSig = 0;
        int best = 0;
        if (!last && maxLevel < 3)
        {
            codedCostSig = Cost(contexts, sigContext, 0, lambda);
            codedCost = codedCostZero + codedCostSig;
            if (maxLevel == 0)
            {
                return 0;
            }
        }
        else
        {
            codedCost = double.MaxValue;
        }

        if (!last)
        {
            currentCostSig = Cost(contexts, sigContext, 1, lambda);
        }

        int minLevel = maxLevel > 1 ? maxLevel - 1 : 1;
        for (int level = maxLevel; level >= minLevel; level--)
        {
            double error = scaled - ((long)level << qBits);
            double cost = (error * error * errorScale) + (lambda * LevelRate(level, oneContext, absContext, rice, c1Index, c2Index, contexts))
                + currentCostSig;
            if (cost < codedCost)
            {
                best = level;
                codedCost = cost;
                codedCostSig = currentCostSig;
            }
        }

        return best;
    }

    // port: TComTrQuant::xGetICRate (sign bit plus greater1/greater2 flags and the remaining level).
    private static int LevelRate(int level, int oneContext, int absContext, int rice, int c1Index, int c2Index, ReadOnlySpan<byte> contexts)
    {
        int rate = SignBit;
        int baseLevel = c1Index < 8 ? 2 + (c2Index < 1 ? 1 : 0) : 1;
        if (level >= baseLevel)
        {
            int symbol = level - baseLevel;
            if (symbol < (3 << rice))
            {
                rate += ((symbol >> rice) + 1 + rice) << 15;
            }
            else
            {
                int length = rice;
                symbol -= 3 << rice;
                while (symbol >= (1 << length))
                {
                    symbol -= 1 << length++;
                }

                rate += (3 + length + 1 - rice + length) << 15;
            }

            if (c1Index < 8)
            {
                rate += Bits(contexts, oneContext, 1);
                if (c2Index < 1)
                {
                    rate += Bits(contexts, absContext, 1);
                }
            }
        }
        else if (level == 1)
        {
            rate += Bits(contexts, oneContext, 0);
        }
        else if (level == 2)
        {
            rate += Bits(contexts, oneContext, 1) + Bits(contexts, absContext, 0);
        }
        else
        {
            rate = 0;
        }

        return rate;
    }

    // port: TComTrQuant::xGetRateLast with TEncSbac::estLastSignificantPositionBits.
    private static double LastCost(int x, int y, int log2Size, bool luma, ReadOnlySpan<byte> contexts, double lambda)
    {
        int ctxOffset = luma ? (3 * (log2Size - 2)) + ((log2Size - 1) >> 2) : 15;
        int ctxShift = luma ? (log2Size + 1) >> 2 : log2Size - 2;
        int groups = GroupIndex[(1 << log2Size) - 1];
        long rate = PrefixRate(HevcContext.LastXPrefix + ctxOffset, ctxShift, GroupIndex[x], groups, contexts)
            + PrefixRate(HevcContext.LastYPrefix + ctxOffset, ctxShift, GroupIndex[y], groups, contexts);
        if (GroupIndex[x] > 3)
        {
            rate += (long)SignBit * ((GroupIndex[x] - 2) >> 1);
        }

        if (GroupIndex[y] > 3)
        {
            rate += (long)SignBit * ((GroupIndex[y] - 2) >> 1);
        }

        return lambda * rate;
    }

    private static long PrefixRate(int baseContext, int shift, int value, int groups, ReadOnlySpan<byte> contexts)
    {
        long rate = 0;
        for (int i = 0; i < value; i++)
        {
            rate += Bits(contexts, baseContext + (i >> shift), 1);
        }

        if (value < groups)
        {
            rate += Bits(contexts, baseContext + (value >> shift), 0);
        }

        return rate;
    }

    private static int Bits(ReadOnlySpan<byte> contexts, int context, int bin) => HevcCabacCounter.Cost(contexts[context], bin);

    private static double Cost(ReadOnlySpan<byte> contexts, int context, int bin, double lambda) => lambda * Bits(contexts, context, bin);
}
