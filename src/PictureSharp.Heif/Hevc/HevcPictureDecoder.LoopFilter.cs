namespace PictureSharp.Formats.Heif.Hevc;

internal sealed partial class HevcPictureDecoder
{
    // port: HM source/Lib/TLibCommon/TComLoopFilter.cpp sm_tcTable, sm_betaTable (Table 8-12)
    private static ReadOnlySpan<byte> TcTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4,
        4, 5, 5, 6, 6, 7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 24,
    ];

    private static ReadOnlySpan<byte> BetaTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24, 26,
        28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    ];

    // H.265 8.7.2: all vertical edges of the picture, then all horizontal edges. Every edge of an intra picture has
    // bS 2. port: HM TComLoopFilter::loopFilterPic, xEdgeFilterLuma, xEdgeFilterChroma
    private void Deblock()
    {
        for (int direction = 0; direction < 2; direction++)
        {
            bool vertical = direction == 0;
            byte edgeFlag = vertical ? EdgeVertical : EdgeHorizontal;
            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    int edge = vertical ? bx : by;
                    if ((edge & 1) != 0 || edge == 0 || (blockFlags[(by * blocksWide) + bx] & edgeFlag) == 0)
                    {
                        continue;
                    }

                    int q = (by * blocksWide) + bx;
                    int p = vertical ? q - 1 : q - blocksWide;
                    FilterLumaSegment(bx << 2, by << 2, vertical, p, q);
                    if (sps.ChromaFormatIdc != 0 && (edge & 3) == 0)
                    {
                        FilterChromaSegment(bx << 1, by << 1, vertical, p, q);
                    }
                }
            }
        }
    }

    private HevcSliceHeader SliceOfBlock(int blockIndex)
    {
        int x = (blockIndex % blocksWide) << 2;
        int y = (blockIndex / blocksWide) << 2;
        return slices[ctbSliceIndex[((y >> sps.Log2CtbSize) * layout.WidthInCtbs) + (x >> sps.Log2CtbSize)]];
    }

    private void FilterLumaSegment(int x0, int y0, bool vertical, int p, int q)
    {
        HevcSliceHeader qSlice = SliceOfBlock(q);
        int qp = (qpMap[p] + qpMap[q] + 1) >> 1;
        int bitDepth = sps.BitDepthLuma;
        int scale = 1 << (bitDepth - 8);
        int tc = TcTable[Math.Clamp(qp + 2 + (qSlice.TcOffsetDiv2 << 1), 0, 53)] * scale;
        int beta = BetaTable[Math.Clamp(qp + (qSlice.BetaOffsetDiv2 << 1), 0, 51)] * scale;
        ushort[] plane = frame.Planes[0];
        int stride = frame.Strides[0];
        int offset = vertical ? 1 : stride;
        int step = vertical ? stride : 1;
        int origin = (y0 * stride) + x0;
        bool noFilterP = (blockFlags[p] & NoLoopFilter) != 0;
        bool noFilterQ = (blockFlags[q] & NoLoopFilter) != 0;

        int dp0 = SecondDifference(plane, origin, -offset);
        int dq0 = SecondDifference(plane, origin, offset);
        int dp3 = SecondDifference(plane, origin + (3 * step), -offset);
        int dq3 = SecondDifference(plane, origin + (3 * step), offset);
        int d0 = dp0 + dq0;
        int d3 = dp3 + dq3;
        int d = d0 + d3;
        if (d >= beta)
        {
            return;
        }

        int sideThreshold = (beta + (beta >> 1)) >> 3;
        bool filterP = dp0 + dp3 < sideThreshold;
        bool filterQ = dq0 + dq3 < sideThreshold;
        bool strong = UseStrongFilter(plane, origin, offset, 2 * d0, beta, tc)
            && UseStrongFilter(plane, origin + (3 * step), offset, 2 * d3, beta, tc);
        int maximum = (1 << bitDepth) - 1;
        for (int i = 0; i < 4; i++)
        {
            FilterLumaLine(plane, origin + (i * step), offset, tc, strong, noFilterP, noFilterQ, tc * 10, filterP, filterQ, maximum);
        }
    }

    private static int SecondDifference(ushort[] plane, int position, int offset) =>
        Math.Abs(plane[position + (offset < 0 ? 3 * offset : 0)] - (2 * plane[position + (offset < 0 ? 2 * offset : offset)])
            + plane[position + (offset < 0 ? offset : 2 * offset)]);

    // port: TComLoopFilter::xUseStrongFiltering
    private static bool UseStrongFilter(ushort[] plane, int position, int offset, int d, int beta, int tc)
    {
        int p0 = plane[position - offset];
        int q0 = plane[position];
        int p3 = plane[position - (4 * offset)];
        int q3 = plane[position + (3 * offset)];
        return Math.Abs(p3 - p0) + Math.Abs(q3 - q0) < (beta >> 3) && d < (beta >> 2) && Math.Abs(p0 - q0) < ((tc * 5) + 1) >> 1;
    }

    // port: TComLoopFilter::xPelFilterLuma
    private static void FilterLumaLine(ushort[] plane, int position, int offset, int tc, bool strong, bool noFilterP,
        bool noFilterQ, int thresholdCut, bool filterSecondP, bool filterSecondQ, int maximum)
    {
        int m4 = plane[position];
        int m3 = plane[position - offset];
        int m5 = plane[position + offset];
        int m2 = plane[position - (2 * offset)];
        int m6 = plane[position + (2 * offset)];
        int m1 = plane[position - (3 * offset)];
        int m7 = plane[position + (3 * offset)];
        int m0 = plane[position - (4 * offset)];
        int tc2 = 2 * tc;
        if (strong)
        {
            if (!noFilterP)
            {
                plane[position - offset] = (ushort)Math.Clamp((m1 + (2 * m2) + (2 * m3) + (2 * m4) + m5 + 4) >> 3, m3 - tc2, m3 + tc2);
                plane[position - (2 * offset)] = (ushort)Math.Clamp((m1 + m2 + m3 + m4 + 2) >> 2, m2 - tc2, m2 + tc2);
                plane[position - (3 * offset)] = (ushort)Math.Clamp(((2 * m0) + (3 * m1) + m2 + m3 + m4 + 4) >> 3, m1 - tc2, m1 + tc2);
            }

            if (!noFilterQ)
            {
                plane[position] = (ushort)Math.Clamp((m2 + (2 * m3) + (2 * m4) + (2 * m5) + m6 + 4) >> 3, m4 - tc2, m4 + tc2);
                plane[position + offset] = (ushort)Math.Clamp((m3 + m4 + m5 + m6 + 2) >> 2, m5 - tc2, m5 + tc2);
                plane[position + (2 * offset)] = (ushort)Math.Clamp((m3 + m4 + m5 + (3 * m6) + (2 * m7) + 4) >> 3, m6 - tc2, m6 + tc2);
            }

            return;
        }

        int delta = ((9 * (m4 - m3)) - (3 * (m5 - m2)) + 8) >> 4;
        if (Math.Abs(delta) >= thresholdCut)
        {
            return;
        }

        delta = Math.Clamp(delta, -tc, tc);
        int halfTc = tc >> 1;
        if (!noFilterP)
        {
            plane[position - offset] = (ushort)HevcTransform.Clip(m3 + delta, maximum);
            if (filterSecondP)
            {
                int delta1 = Math.Clamp((((m1 + m3 + 1) >> 1) - m2 + delta) >> 1, -halfTc, halfTc);
                plane[position - (2 * offset)] = (ushort)HevcTransform.Clip(m2 + delta1, maximum);
            }
        }

        if (!noFilterQ)
        {
            plane[position] = (ushort)HevcTransform.Clip(m4 - delta, maximum);
            if (filterSecondQ)
            {
                int delta2 = Math.Clamp((((m6 + m4 + 1) >> 1) - m5 - delta) >> 1, -halfTc, halfTc);
                plane[position + offset] = (ushort)HevcTransform.Clip(m5 + delta2, maximum);
            }
        }
    }

    // Two chroma lines per 4x4 luma block (4:2:0); port: TComLoopFilter::xEdgeFilterChroma, xPelFilterChroma
    private void FilterChromaSegment(int xc, int yc, bool vertical, int p, int q)
    {
        HevcSliceHeader qSlice = SliceOfBlock(q);
        bool noFilterP = (blockFlags[p] & NoLoopFilter) != 0;
        bool noFilterQ = (blockFlags[q] & NoLoopFilter) != 0;
        int scale = 1 << (sps.BitDepthChroma - 8);
        int maximum = (1 << sps.BitDepthChroma) - 1;
        for (int cIdx = 1; cIdx <= 2; cIdx++)
        {
            int qpi = ((qpMap[p] + qpMap[q] + 1) >> 1) + (cIdx == 1 ? pps.CbQpOffset : pps.CrQpOffset);
            int qpc = qpi >= 58 ? qpi - 6 : qpi >= 0 ? ChromaScale420[qpi] : qpi;
            int tc = TcTable[Math.Clamp(qpc + 2 + (qSlice.TcOffsetDiv2 << 1), 0, 53)] * scale;
            ushort[] plane = frame.Planes[cIdx];
            int stride = frame.Strides[cIdx];
            int offset = vertical ? 1 : stride;
            int step = vertical ? stride : 1;
            for (int i = 0; i < 2; i++)
            {
                int position = (yc * stride) + xc + (i * step);
                int m4 = plane[position];
                int m3 = plane[position - offset];
                int m5 = plane[position + offset];
                int m2 = plane[position - (2 * offset)];
                int delta = Math.Clamp(((((m4 - m3) << 2) + m2 - m5 + 4) >> 3), -tc, tc);
                if (!noFilterP)
                {
                    plane[position - offset] = (ushort)HevcTransform.Clip(m3 + delta, maximum);
                }

                if (!noFilterQ)
                {
                    plane[position] = (ushort)HevcTransform.Clip(m4 - delta, maximum);
                }
            }
        }
    }

    // H.265 8.7.3 sample adaptive offset on a copy of the deblocked picture.
    // port: HM TComSampleAdaptiveOffset::offsetCTU, offsetBlock, TComPicSym::deriveLoopFilterBoundaryAvailibility;
    // PCM/lossless samples keep their reconstruction as after HM xPCMRestoration.
    private void ApplySao()
    {
        if (!slices.Exists(s => s.SaoLuma || s.SaoChroma))
        {
            return;
        }

        int components = sps.ChromaFormatIdc == 0 ? 1 : 3;
        for (int cIdx = 0; cIdx < components; cIdx++)
        {
            ushort[] destination = frame.Planes[cIdx];
            ushort[] source = (ushort[])destination.Clone();
            for (int ctb = 0; ctb < layout.CtbCount; ctb++)
            {
                if (saoTypes[(ctb * 3) + cIdx] != 0)
                {
                    SaoCtb(ctb, cIdx, source, destination);
                }
            }
        }
    }

    private void SaoCtb(int ctb, int cIdx, ushort[] source, ushort[] destination)
    {
        int index = (ctb * 3) + cIdx;
        int shift = cIdx == 0 ? 0 : 1;
        int stride = frame.Strides[cIdx];
        int planeWidth = sps.Width >> shift;
        int planeHeight = sps.Height >> shift;
        int ctbSize = sps.CtbSize >> shift;
        int x0 = (ctb % layout.WidthInCtbs) * ctbSize;
        int y0 = (ctb / layout.WidthInCtbs) * ctbSize;
        int width = Math.Min(ctbSize, planeWidth - x0);
        int height = Math.Min(ctbSize, planeHeight - y0);
        int bitDepth = cIdx == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        int maximum = (1 << bitDepth) - 1;
        ReadOnlySpan<short> offsets = saoOffsets.AsSpan(index * 4, 4);
        bool checkNoFilter = (sps.PcmEnabled && sps.PcmLoopFilterDisabled) || pps.TransquantBypassEnabled;

        if (saoTypes[index] == 1)
        {
            Span<int> bandTable = stackalloc int[32];
            int position = saoClasses[index];
            for (int k = 0; k < 4; k++)
            {
                bandTable[(position + k) & 31] = offsets[k];
            }

            int bandShift = bitDepth - 5;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (checkNoFilter && IsNoFilterSample(x0 + x, y0 + y, shift))
                    {
                        continue;
                    }

                    int i = ((y0 + y) * stride) + x0 + x;
                    destination[i] = (ushort)HevcTransform.Clip(source[i] + bandTable[source[i] >> bandShift], maximum);
                }
            }

            return;
        }

        Span<bool> neighbours = stackalloc bool[9];
        NeighbourAvailability(ctb, neighbours);
        (int ax, int ay, int bx, int by) = saoClasses[index] switch
        {
            0 => (-1, 0, 1, 0),
            1 => (0, -1, 0, 1),
            2 => (-1, -1, 1, 1),
            _ => (1, -1, -1, 1),
        };
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!NeighbourUsable(x + ax, y + ay, width, height, neighbours)
                    || !NeighbourUsable(x + bx, y + by, width, height, neighbours))
                {
                    continue;
                }

                if (checkNoFilter && IsNoFilterSample(x0 + x, y0 + y, shift))
                {
                    continue;
                }

                int i = ((y0 + y) * stride) + x0 + x;
                int current = source[i];
                int edge = Math.Sign(current - source[i + (ay * stride) + ax]) + Math.Sign(current - source[i + (by * stride) + bx]);
                int offset = edge switch { -2 => offsets[0], -1 => offsets[1], 1 => offsets[2], 2 => offsets[3], _ => 0 };
                destination[i] = (ushort)HevcTransform.Clip(current + offset, maximum);
            }
        }
    }

    private bool IsNoFilterSample(int x, int y, int shift) =>
        (blockFlags[BlockIndex(x << shift, y << shift)] & NoLoopFilter) != 0;

    // Neighbour CTB availability in a 3x3 grid (index (dy + 1) * 3 + dx + 1), including picture, slice and tile limits.
    private void NeighbourAvailability(int ctb, Span<bool> neighbours)
    {
        int cx = ctb % layout.WidthInCtbs;
        int cy = ctb / layout.WidthInCtbs;
        HevcSliceHeader current = slices[ctbSliceIndex[ctb]];
        int currentTile = layout.TileIdOfRs(ctb);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                bool available = nx >= 0 && ny >= 0 && nx < layout.WidthInCtbs && ny < layout.HeightInCtbs;
                if (available && (dx != 0 || dy != 0))
                {
                    int neighbour = (ny * layout.WidthInCtbs) + nx;
                    HevcSliceHeader other = slices[ctbSliceIndex[neighbour]];
                    if (other.SliceAddress != current.SliceAddress)
                    {
                        // HM: left/above/above-left use the current slice's flag, right/below/below-right the
                        // neighbour's; above-right and below-left use the later slice's flag.
                        bool useCurrent = (dy, dx) switch
                        {
                            (-1, 1) or (1, -1) => layout.RsToTs[current.SliceAddress] > layout.RsToTs[other.SliceAddress],
                            _ => dy < 0 || (dy == 0 && dx < 0),
                        };
                        available = useCurrent ? current.LoopFilterAcrossSlices : other.LoopFilterAcrossSlices;
                    }

                    if (available && !pps.LoopFilterAcrossTilesEnabled && layout.TileIdOfRs(neighbour) != currentTile)
                    {
                        available = false;
                    }
                }

                neighbours[((dy + 1) * 3) + dx + 1] = available;
            }
        }
    }

    private static bool NeighbourUsable(int x, int y, int width, int height, ReadOnlySpan<bool> neighbours)
    {
        int dx = x < 0 ? -1 : x >= width ? 1 : 0;
        int dy = y < 0 ? -1 : y >= height ? 1 : 0;
        return neighbours[((dy + 1) * 3) + dx + 1];
    }
}
