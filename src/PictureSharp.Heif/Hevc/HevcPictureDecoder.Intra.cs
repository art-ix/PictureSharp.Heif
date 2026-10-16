namespace PictureSharp.Formats.Heif.Hevc;

internal sealed partial class HevcPictureDecoder
{
    // 8.4.4.2.6 intraPredAngle and invAngle (port: HM TComPrediction::xPredIntraAng angTable/invAngTable).
    private static ReadOnlySpan<int> AngleTable => [0, 2, 5, 9, 13, 17, 21, 26, 32];
    private static ReadOnlySpan<int> InverseAngleTable => [0, 4096, 1638, 910, 630, 482, 390, 315, 256];

    // Fills prediction (raster, size x size) for the block at (xTb, yTb) in component coordinates.
    // port: HM TComPrediction::initIntraPatternChType, fillReferenceSamples, predIntraAng, xPredIntraPlanar,
    //       xPredIntraAng, xDCPredFiltering
    private void PredictIntra(int cIdx, int xTb, int yTb, int log2Size, int mode, int xTbY, int yTbY)
    {
        int size = 1 << log2Size;
        int bitDepth = cIdx == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        BuildReferenceSamples(cIdx, xTb, yTb, size, bitDepth, xTbY, yTbY);

        // 8.4.4.2.3 filtering of neighbouring samples (luma only for 4:2:0 and 4:0:0).
        int[] top = referenceTop;
        int[] left = referenceLeft;
        if (cIdx == 0 && mode != 1 && size != 4)
        {
            int minDistVerHor = Math.Min(Math.Abs(mode - 26), Math.Abs(mode - 10));
            int threshold = size == 8 ? 7 : size == 16 ? 1 : 0;
            if (minDistVerHor > threshold)
            {
                FilterReferenceSamples(size, bitDepth);
                top = filteredTop;
                left = filteredLeft;
            }
        }

        int maximum = (1 << bitDepth) - 1;
        bool edgeFilters = cIdx == 0 && size < 32;
        if (mode == 0)
        {
            // 8.4.4.2.5 planar
            int shift = log2Size + 1;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    prediction[(y * size) + x] = (((size - 1 - x) * left[1 + y]) + ((x + 1) * top[1 + size])
                        + ((size - 1 - y) * top[1 + x]) + ((y + 1) * left[1 + size]) + size) >> shift;
                }
            }

            return;
        }

        if (mode == 1)
        {
            // 8.4.4.2.5 DC with edge filtering for luma blocks below 32x32.
            int sum = size;
            for (int i = 0; i < size; i++)
            {
                sum += top[1 + i] + left[1 + i];
            }

            int dc = sum >> (log2Size + 1);
            prediction.AsSpan(0, size * size).Fill(dc);
            if (edgeFilters)
            {
                prediction[0] = (left[1] + (2 * dc) + top[1] + 2) >> 2;
                for (int x = 1; x < size; x++)
                {
                    prediction[x] = (top[1 + x] + (3 * dc) + 2) >> 2;
                }

                for (int y = 1; y < size; y++)
                {
                    prediction[y * size] = (left[1 + y] + (3 * dc) + 2) >> 2;
                }
            }

            return;
        }

        PredictAngular(size, mode, top, left, edgeFilters, maximum);
    }

    private void PredictAngular(int size, int mode, int[] top, int[] left, bool edgeFilters, int maximum)
    {
        bool vertical = mode >= 18;
        int angleMode = vertical ? mode - 26 : -(mode - 10);
        int absoluteMode = Math.Abs(angleMode);
        int angle = (angleMode < 0 ? -1 : 1) * AngleTable[absoluteMode];
        int inverseAngle = InverseAngleTable[absoluteMode];
        int[] main = vertical ? top : left;
        int[] side = vertical ? left : top;

        // reference[k + size] holds ref[k] for k in -size..2*size.
        Span<int> reference = stackalloc int[(3 * 64) + 1];
        int origin = size;
        for (int k = 0; k <= 2 * size; k++)
        {
            reference[origin + k] = main[k];
        }

        if (angle < 0)
        {
            int last = (size * angle) >> 5;
            if (last < -1)
            {
                int inverseSum = 128;
                for (int k = -1; k >= last; k--)
                {
                    inverseSum += inverseAngle;
                    reference[origin + k] = side[inverseSum >> 8];
                }
            }
        }

        for (int y = 0; y < size; y++)
        {
            int position = (y + 1) * angle;
            int whole = position >> 5;
            int fraction = position & 31;
            for (int x = 0; x < size; x++)
            {
                int index = origin + x + whole + 1;
                int value = fraction != 0
                    ? (((32 - fraction) * reference[index]) + (fraction * reference[index + 1]) + 16) >> 5
                    : reference[index];
                if (vertical)
                {
                    prediction[(y * size) + x] = value;
                }
                else
                {
                    prediction[(x * size) + y] = value;
                }
            }
        }

        if (angle == 0 && edgeFilters)
        {
            for (int k = 0; k < size; k++)
            {
                int value = HevcTransform.Clip(main[1] + ((side[1 + k] - side[0]) >> 1), maximum);
                if (vertical)
                {
                    prediction[k * size] = value;
                }
                else
                {
                    prediction[k] = value;
                }
            }
        }
    }

    // 8.4.4.2.2 reference sample availability and substitution. top[0] = left[0] = p[-1][-1],
    // top[1 + x] = p[x][-1], left[1 + y] = p[-1][y] for x, y in 0..2*size-1.
    private void BuildReferenceSamples(int cIdx, int xTb, int yTb, int size, int bitDepth, int xTbY, int yTbY)
    {
        ushort[] plane = frame.Planes[cIdx];
        int stride = frame.Strides[cIdx];
        int scale = cIdx == 0 ? 0 : 1;
        int unit = cIdx == 0 ? 4 : 2; // samples per 4x4 luma availability unit
        int count = 2 * size;
        Span<bool> leftAvailable = stackalloc bool[128];
        Span<bool> topAvailable = stackalloc bool[128];
        bool cornerAvailable = IsAvailable(xTbY, yTbY, ((xTb - 1) << scale), ((yTb - 1) << scale));
        int available = cornerAvailable ? 1 : 0;
        for (int i = 0; i < count; i += unit)
        {
            bool leftUnit = IsAvailable(xTbY, yTbY, (xTb - 1) << scale, (yTb + i) << scale);
            bool topUnit = IsAvailable(xTbY, yTbY, (xTb + i) << scale, (yTb - 1) << scale);
            leftAvailable.Slice(i, unit).Fill(leftUnit);
            topAvailable.Slice(i, unit).Fill(topUnit);
            available += (leftUnit ? 1 : 0) + (topUnit ? 1 : 0);
        }

        if (available == 0)
        {
            int dc = 1 << (bitDepth - 1);
            referenceTop.AsSpan(0, count + 1).Fill(dc);
            referenceLeft.AsSpan(0, count + 1).Fill(dc);
            return;
        }

        // Gather available samples into one line ordered from p[-1][2N-1] up to p[-1][-1] and on to p[2N-1][-1],
        // then substitute missing ones from the nearest preceding available sample (or the first available one).
        Span<int> line = stackalloc int[(2 * 128) + 1];
        Span<bool> present = stackalloc bool[(2 * 128) + 1];
        int total = (2 * count) + 1;
        for (int i = 0; i < count; i++)
        {
            int k = count - 1 - i;
            present[k] = leftAvailable[i];
            if (present[k])
            {
                line[k] = plane[((yTb + i) * stride) + xTb - 1];
            }

            present[count + 1 + i] = topAvailable[i];
            if (topAvailable[i])
            {
                line[count + 1 + i] = plane[((yTb - 1) * stride) + xTb + i];
            }
        }

        present[count] = cornerAvailable;
        if (cornerAvailable)
        {
            line[count] = plane[((yTb - 1) * stride) + xTb - 1];
        }

        if (!present[0])
        {
            int first = 1;
            while (!present[first])
            {
                first++;
            }

            line[0] = line[first];
        }

        for (int k = 1; k < total; k++)
        {
            if (!present[k])
            {
                line[k] = line[k - 1];
            }
        }

        referenceTop[0] = line[count];
        referenceLeft[0] = line[count];
        for (int i = 0; i < count; i++)
        {
            referenceLeft[1 + i] = line[count - 1 - i];
            referenceTop[1 + i] = line[count + 1 + i];
        }
    }

    // 8.4.4.2.3 [1 2 1] smoothing or strong bilinear smoothing for 32x32 luma.
    private void FilterReferenceSamples(int size, int bitDepth)
    {
        int count = 2 * size;
        int[] top = referenceTop;
        int[] left = referenceLeft;
        int topLeft = top[0];
        int bottomLeft = left[count];
        int topRight = top[count];
        bool strong = sps.StrongIntraSmoothingEnabled && size == 32
            && Math.Abs(bottomLeft + topLeft - (2 * left[size])) < (1 << (bitDepth - 5))
            && Math.Abs(topLeft + topRight - (2 * top[size])) < (1 << (bitDepth - 5));
        filteredTop[0] = strong ? topLeft : (left[1] + (2 * topLeft) + top[1] + 2) >> 2;
        filteredLeft[0] = filteredTop[0];
        if (strong)
        {
            for (int i = 1; i < count; i++)
            {
                filteredLeft[i] = (((count - i) * topLeft) + (i * bottomLeft) + size) >> 6;
                filteredTop[i] = (((count - i) * topLeft) + (i * topRight) + size) >> 6;
            }
        }
        else
        {
            for (int i = 1; i < count; i++)
            {
                filteredLeft[i] = (left[i - 1] + (2 * left[i]) + left[i + 1] + 2) >> 2;
                filteredTop[i] = (top[i - 1] + (2 * top[i]) + top[i + 1] + 2) >> 2;
            }
        }

        filteredLeft[count] = bottomLeft;
        filteredTop[count] = topRight;
    }
}
