using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Forward DCT/DST and scalar quantization (the decoder side is HevcTransform).
// port: HM source/Lib/TLibCommon/TComTrQuant.cpp xTrMxN (partialButterfly*, fastForwardDst as a matrix product), xQuant
internal static partial class HevcForwardTransform
{
    private static ReadOnlySpan<int> QuantScales => [26214, 23302, 20560, 18396, 16384, 14564];

    public static void Forward(ReadOnlySpan<int> residual, Span<int> coefficients, Span<int> temporary, int log2Size,
        bool dst, int bitDepth)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            ForwardVector(residual, coefficients, temporary, log2Size, dst, bitDepth, Vector256.IsHardwareAccelerated);
        }
        else
        {
            ForwardScalar(residual, coefficients, temporary, log2Size, dst, bitDepth);
        }
    }

    // The HM butterflies, also the reference for ForwardVector.
    internal static void ForwardScalar(ReadOnlySpan<int> residual, Span<int> coefficients, Span<int> temporary, int log2Size,
        bool dst, int bitDepth)
    {
        int size = 1 << log2Size;
        int shift1 = log2Size + bitDepth - 9;
        int shift2 = log2Size + 6;
        int add1 = 1 << (shift1 - 1);
        int add2 = 1 << (shift2 - 1);
        Span<int> output = stackalloc int[32];
        Span<int> scratch = stackalloc int[96];

        // Horizontal: temporary[k * size + y] = transform of row y.
        for (int y = 0; y < size; y++)
        {
            Transform1D(residual.Slice(y * size, size), output, size, dst, scratch);
            for (int k = 0; k < size; k++)
            {
                temporary[(k * size) + y] = (output[k] + add1) >> shift1;
            }
        }

        // Vertical: coefficients[k2 * size + k1].
        for (int k1 = 0; k1 < size; k1++)
        {
            Transform1D(temporary.Slice(k1 * size, size), output, size, dst, scratch);
            for (int k2 = 0; k2 < size; k2++)
            {
                coefficients[(k2 * size) + k1] = (output[k2] + add2) >> shift2;
            }
        }
    }

    private static void Transform1D(ReadOnlySpan<int> input, Span<int> output, int size, bool dst, Span<int> scratch)
    {
        if (!dst)
        {
            ForwardDct(input, output, size, scratch);
            return;
        }

        for (int k = 0; k < 4; k++)
        {
            output[k] = (DstMatrix[k * 4] * input[0]) + (DstMatrix[(k * 4) + 1] * input[1]) + (DstMatrix[(k * 4) + 2] * input[2])
                + (DstMatrix[(k * 4) + 3] * input[3]);
        }
    }

    // One-dimensional forward DCT of n points by even-odd decomposition, exactly equal to the matrix product
    // (port: HM partialButterfly4/8/16/32). scratch holds 3n values.
    private static void ForwardDct(ReadOnlySpan<int> input, Span<int> output, int n, Span<int> scratch)
    {
        if (n == 2)
        {
            output[0] = 64 * (input[0] + input[1]);
            output[1] = 64 * (input[0] - input[1]);
            return;
        }

        int half = n >> 1;
        int step = 32 / n;
        Span<int> even = scratch[..half];
        Span<int> odd = scratch.Slice(half, half);
        Span<int> evenOutput = scratch.Slice(n, half);
        for (int i = 0; i < half; i++)
        {
            even[i] = input[i] + input[n - 1 - i];
            odd[i] = input[i] - input[n - 1 - i];
        }

        ForwardDct(even, evenOutput, half, scratch[(n + half)..]);
        for (int j = 0; j < half; j++)
        {
            output[2 * j] = evenOutput[j];
            int row = ((2 * j) + 1) * step;
            int sum = 0;
            for (int i = 0; i < half; i++)
            {
                sum += HevcTransform.DctCoefficient(row, i) * odd[i];
            }

            output[(2 * j) + 1] = sum;
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

    private static ReadOnlySpan<sbyte> DstMatrix =>
    [
        29, 55, 74, 84,
        74, 74, 0, -74,
        84, -29, -74, 55,
        55, -84, 74, -29,
    ];
}
