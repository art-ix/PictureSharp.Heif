namespace PictureSharp.Formats.Heif.Hevc;

// Forward DCT/DST and scalar quantization (the decoder side is HevcTransform).
// port: HM source/Lib/TLibCommon/TComTrQuant.cpp xTrMxN (partialButterfly*/fastForwardDst as matrix products), xQuant
internal static class HevcForwardTransform
{
    private static ReadOnlySpan<int> QuantScales => [26214, 23302, 20560, 18396, 16384, 14564];

    public static void Forward(ReadOnlySpan<int> residual, Span<int> coefficients, Span<int> temporary, int log2Size,
        bool dst, int bitDepth)
    {
        int size = 1 << log2Size;
        int shift1 = log2Size + bitDepth - 9;
        int shift2 = log2Size + 6;
        int add1 = 1 << (shift1 - 1);
        int add2 = 1 << (shift2 - 1);
        int step = dst ? 1 : 32 >> log2Size;
        // Horizontal: temporary[k * size + y] = transform of row y.
        for (int y = 0; y < size; y++)
        {
            ReadOnlySpan<int> row = residual.Slice(y * size, size);
            for (int k = 0; k < size; k++)
            {
                int sum = 0;
                for (int n = 0; n < size; n++)
                {
                    sum += Matrix(dst, k * step, n) * row[n];
                }

                temporary[(k * size) + y] = (sum + add1) >> shift1;
            }
        }

        // Vertical: coefficients[k2 * size + k1].
        for (int k1 = 0; k1 < size; k1++)
        {
            ReadOnlySpan<int> column = temporary.Slice(k1 * size, size);
            for (int k2 = 0; k2 < size; k2++)
            {
                int sum = 0;
                for (int n = 0; n < size; n++)
                {
                    sum += Matrix(dst, k2 * step, n) * column[n];
                }

                coefficients[(k2 * size) + k1] = (sum + add2) >> shift2;
            }
        }
    }

    // Transform skip: the residual scaled up by the inverse of the decoder's transform-skip shift (4x4 only).
    public static void TransformSkip(ReadOnlySpan<int> residual, Span<int> coefficients, int log2Size, int bitDepth)
    {
        int shift = 15 - bitDepth - log2Size;
        for (int i = 0; i < 1 << (log2Size * 2); i++)
        {
            coefficients[i] = residual[i] << shift;
        }
    }

    // port: TComTrQuant::xQuant without RDOQ: dead-zone rounding offset 171/512 for intra. Returns the nonzero count.
    public static int Quantize(ReadOnlySpan<int> coefficients, Span<int> levels, int log2Size, int qp, int bitDepth)
    {
        int transformShift = 15 - bitDepth - log2Size;
        int qBits = 14 + (qp / 6) + transformShift;
        long add = 171L << (qBits - 9);
        int scale = QuantScales[qp % 6];
        int nonZero = 0;
        for (int i = 0; i < 1 << (log2Size * 2); i++)
        {
            int coefficient = coefficients[i];
            long level = ((Math.Abs((long)coefficient) * scale) + add) >> qBits;
            int value = (int)Math.Min(level, 32767);
            levels[i] = coefficient < 0 ? -value : value;
            nonZero += value != 0 ? 1 : 0;
        }

        return nonZero;
    }

    private static int Matrix(bool dst, int k, int n) => dst ? DstMatrix[(k * 4) + n] : HevcTransform.DctCoefficient(k, n);

    private static ReadOnlySpan<sbyte> DstMatrix =>
    [
        29, 55, 74, 84,
        74, 74, 0, -74,
        84, -29, -74, 55,
        55, -84, 74, -29,
    ];
}
