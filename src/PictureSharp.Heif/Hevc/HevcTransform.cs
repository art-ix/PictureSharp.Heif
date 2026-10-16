using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Heif.Hevc;

// H.265 8.6.2–8.6.4: scaling (dequantization), inverse DCT/DST and transform skip.
// port: HM source/Lib/TLibCommon/TComTrQuant.cpp xDeQuant, xITrMxN, partialButterflyInverseN, fastInverseDst,
//       xITransformSkip. The butterflies are evaluated as plain matrix products: integer sums are identical.
internal static class HevcTransform
{
    private const int MaxLog2TrDynamicRange = 15;
    private const int IQuantShift = 6;

    // levelScale (8-309), HM g_invQuantScales
    private static ReadOnlySpan<int> LevelScale => [40, 45, 51, 57, 64, 72];

    // port: TComTrQuant::xDeQuant. coefficients holds TransCoeffLevel on input and d[x][y] on output.
    public static void Dequantize(Span<int> coefficients, int log2Size, int qp, int bitDepth, ReadOnlySpan<int> factors)
    {
        int count = 1 << (log2Size * 2);
        int transformShift = MaxLog2TrDynamicRange - bitDepth - log2Size;
        int per = qp / 6;
        int rem = qp % 6;
        bool scaling = !factors.IsEmpty;
        int rightShift = IQuantShift - (transformShift + per) + (scaling ? 4 : 0);
        const int transformMinimum = -(1 << MaxLog2TrDynamicRange);
        const int transformMaximum = (1 << MaxLog2TrDynamicRange) - 1;
        int scaleBits = scaling ? 1 + IQuantShift + 8 : IQuantShift + 1;
        int inputBits = Math.Min(MaxLog2TrDynamicRange + 1, 32 + rightShift - scaleBits);
        int inputMinimum = -(1 << (inputBits - 1));
        int inputMaximum = (1 << (inputBits - 1)) - 1;
        int scale = LevelScale[rem];
        if (rightShift > 0)
        {
            int add = 1 << (rightShift - 1);
            for (int n = 0; n < count; n++)
            {
                int level = coefficients[n];
                if (level != 0)
                {
                    int multiplier = scaling ? scale * factors[n] : scale;
                    int value = ((Math.Clamp(level, inputMinimum, inputMaximum) * multiplier) + add) >> rightShift;
                    coefficients[n] = Math.Clamp(value, transformMinimum, transformMaximum);
                }
            }
        }
        else
        {
            int leftShift = -rightShift;
            for (int n = 0; n < count; n++)
            {
                int level = coefficients[n];
                if (level != 0)
                {
                    int multiplier = scaling ? scale * factors[n] : scale;
                    int value = (Math.Clamp(level, inputMinimum, inputMaximum) * multiplier) << leftShift;
                    coefficients[n] = Math.Clamp(value, transformMinimum, transformMaximum);
                }
            }
        }
    }

    // port: xITrMxN. residual receives the clipped 16-bit residual in raster order.
    public static void Inverse(ReadOnlySpan<int> coefficients, Span<int> residual, Span<int> temporary, int log2Size,
        bool dst, int bitDepth)
    {
        int size = 1 << log2Size;
        int shift2 = 20 - bitDepth;
        if (dst)
        {
            InverseDst(coefficients, residual, temporary, shift2);
            return;
        }

        // Vertical (first stage, shift 7, clipped to the transform dynamic range): find the last nonzero row once.
        int rows = size;
        while (rows > 0 && coefficients.Slice((rows - 1) * size, size).IndexOfAnyExcept(0) < 0)
        {
            rows--;
        }

        Span<int> input = stackalloc int[32];
        Span<int> output = stackalloc int[32];
        Span<int> scratch = stackalloc int[64];
        for (int x = 0; x < size; x++)
        {
            bool any = false;
            for (int k = 0; k < rows; k++)
            {
                input[k] = coefficients[(k * size) + x];
                any |= input[k] != 0;
            }

            if (!any)
            {
                for (int y = 0; y < size; y++)
                {
                    temporary[(y * size) + x] = 0;
                }

                continue;
            }

            input[rows..size].Clear();
            InverseDct(input, output, size, rows, scratch);
            for (int y = 0; y < size; y++)
            {
                temporary[(y * size) + x] = Math.Clamp((output[y] + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Horizontal (second stage), clipped to the Pel range as in HM.
        int add2 = 1 << (shift2 - 1);
        for (int y = 0; y < size; y++)
        {
            ReadOnlySpan<int> row = temporary.Slice(y * size, size);
            int columns = size;
            while (columns > 0 && row[columns - 1] == 0)
            {
                columns--;
            }

            Span<int> destination = residual.Slice(y * size, size);
            if (columns == 0)
            {
                destination.Fill(Math.Clamp(add2 >> shift2, short.MinValue, short.MaxValue));
                continue;
            }

            InverseDct(row, output, size, columns, scratch);
            for (int x = 0; x < size; x++)
            {
                destination[x] = Math.Clamp((output[x] + add2) >> shift2, short.MinValue, short.MaxValue);
            }
        }
    }

    // One-dimensional inverse DCT of n points by even-odd decomposition, exactly equal to the matrix product
    // (port: HM partialButterflyInverse4/8/16/32). Coefficients from nonZero on are zero; scratch holds 2n values.
    private static void InverseDct(ReadOnlySpan<int> coefficients, Span<int> output, int n, int nonZero, Span<int> scratch)
    {
        if (n == 2)
        {
            output[0] = 64 * (coefficients[0] + coefficients[1]);
            output[1] = 64 * (coefficients[0] - coefficients[1]);
            return;
        }

        int half = n >> 1;
        int step = 32 / n;
        Span<int> even = scratch[..half];
        Span<int> evenOutput = scratch.Slice(half, half);
        for (int j = 0; j < half; j++)
        {
            even[j] = coefficients[2 * j];
        }

        InverseDct(even, evenOutput, half, (nonZero + 1) >> 1, scratch[n..]);
        ReadOnlySpan<sbyte> matrix = DctMatrix;
        int oddCount = nonZero >> 1;
        for (int i = 0; i < half; i++)
        {
            int odd = 0;
            for (int j = 0; j < oddCount; j++)
            {
                odd += matrix[((((2 * j) + 1) * step) * 32) + i] * coefficients[(2 * j) + 1];
            }

            output[i] = evenOutput[i] + odd;
            output[n - 1 - i] = evenOutput[i] - odd;
        }
    }

    // 4x4 inverse DST-VII as matrix products.
    private static void InverseDst(ReadOnlySpan<int> coefficients, Span<int> residual, Span<int> temporary, int shift2)
    {
        ReadOnlySpan<sbyte> matrix = DstMatrix;
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 4; y++)
            {
                int sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += matrix[(k * 4) + y] * coefficients[(k * 4) + x];
                }

                temporary[(y * 4) + x] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        int add2 = 1 << (shift2 - 1);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += matrix[(k * 4) + x] * temporary[(y * 4) + k];
                }

                residual[(y * 4) + x] = Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
            }
        }
    }

    // port: xITransformSkip (no rotation, no extended precision)
    public static void TransformSkip(ReadOnlySpan<int> coefficients, Span<int> residual, int log2Size, int bitDepth)
    {
        int shift = MaxLog2TrDynamicRange - bitDepth - log2Size;
        int count = 1 << (log2Size * 2);
        if (shift > 0)
        {
            int offset = 1 << (shift - 1);
            for (int n = 0; n < count; n++)
            {
                residual[n] = (short)((coefficients[n] + offset) >> shift);
            }
        }
        else
        {
            for (int n = 0; n < count; n++)
            {
                residual[n] = (short)(coefficients[n] << -shift);
            }
        }
    }

    // 8.6.1 QpC for ChromaArrayType 1 from qPi (port: HM g_aucChromaScale[CHROMA_420]).
    public static int ChromaQp420(int qpi) => qpi < 30 ? qpi : qpi >= 43 ? qpi - 6 : ChromaScale[qpi - 30];

    private static ReadOnlySpan<byte> ChromaScale => [29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37];

    // Row k (basis function), column n of the 32x32 DCT matrix.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DctCoefficient(int k, int n) => DctMatrix[(k * 32) + n];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clip(int value, int maximum) => value < 0 ? 0 : value > maximum ? maximum : value;

    // DST-VII 4x4, row k = basis function k (port: HM g_as_DST_MAT_4).
    private static ReadOnlySpan<sbyte> DstMatrix =>
    [
        29, 55, 74, 84,
        74, 74, 0, -74,
        84, -29, -74, 55,
        55, -84, 74, -29,
    ];

    // DCT 32x32, row k = basis function k; smaller sizes use every (32 >> log2Size)-th row (port: HM g_aiT32).
    private static ReadOnlySpan<sbyte> DctMatrix =>
    [
        64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
        90, 90, 88, 85, 82, 78, 73, 67, 61, 54, 46, 38, 31, 22, 13, 4, -4, -13, -22, -31, -38, -46, -54, -61, -67, -73, -78, -82, -85, -88, -90, -90,
        90, 87, 80, 70, 57, 43, 25, 9, -9, -25, -43, -57, -70, -80, -87, -90, -90, -87, -80, -70, -57, -43, -25, -9, 9, 25, 43, 57, 70, 80, 87, 90,
        90, 82, 67, 46, 22, -4, -31, -54, -73, -85, -90, -88, -78, -61, -38, -13, 13, 38, 61, 78, 88, 90, 85, 73, 54, 31, 4, -22, -46, -67, -82, -90,
        89, 75, 50, 18, -18, -50, -75, -89, -89, -75, -50, -18, 18, 50, 75, 89, 89, 75, 50, 18, -18, -50, -75, -89, -89, -75, -50, -18, 18, 50, 75, 89,
        88, 67, 31, -13, -54, -82, -90, -78, -46, -4, 38, 73, 90, 85, 61, 22, -22, -61, -85, -90, -73, -38, 4, 46, 78, 90, 82, 54, 13, -31, -67, -88,
        87, 57, 9, -43, -80, -90, -70, -25, 25, 70, 90, 80, 43, -9, -57, -87, -87, -57, -9, 43, 80, 90, 70, 25, -25, -70, -90, -80, -43, 9, 57, 87,
        85, 46, -13, -67, -90, -73, -22, 38, 82, 88, 54, -4, -61, -90, -78, -31, 31, 78, 90, 61, 4, -54, -88, -82, -38, 22, 73, 90, 67, 13, -46, -85,
        83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83,
        82, 22, -54, -90, -61, 13, 78, 85, 31, -46, -90, -67, 4, 73, 88, 38, -38, -88, -73, -4, 67, 90, 46, -31, -85, -78, -13, 61, 90, 54, -22, -82,
        80, 9, -70, -87, -25, 57, 90, 43, -43, -90, -57, 25, 87, 70, -9, -80, -80, -9, 70, 87, 25, -57, -90, -43, 43, 90, 57, -25, -87, -70, 9, 80,
        78, -4, -82, -73, 13, 85, 67, -22, -88, -61, 31, 90, 54, -38, -90, -46, 46, 90, 38, -54, -90, -31, 61, 88, 22, -67, -85, -13, 73, 82, 4, -78,
        75, -18, -89, -50, 50, 89, 18, -75, -75, 18, 89, 50, -50, -89, -18, 75, 75, -18, -89, -50, 50, 89, 18, -75, -75, 18, 89, 50, -50, -89, -18, 75,
        73, -31, -90, -22, 78, 67, -38, -90, -13, 82, 61, -46, -88, -4, 85, 54, -54, -85, 4, 88, 46, -61, -82, 13, 90, 38, -67, -78, 22, 90, 31, -73,
        70, -43, -87, 9, 90, 25, -80, -57, 57, 80, -25, -90, -9, 87, 43, -70, -70, 43, 87, -9, -90, -25, 80, 57, -57, -80, 25, 90, 9, -87, -43, 70,
        67, -54, -78, 38, 85, -22, -90, 4, 90, 13, -88, -31, 82, 46, -73, -61, 61, 73, -46, -82, 31, 88, -13, -90, -4, 90, 22, -85, -38, 78, 54, -67,
        64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64,
        61, -73, -46, 82, 31, -88, -13, 90, -4, -90, 22, 85, -38, -78, 54, 67, -67, -54, 78, 38, -85, -22, 90, 4, -90, 13, 88, -31, -82, 46, 73, -61,
        57, -80, -25, 90, -9, -87, 43, 70, -70, -43, 87, 9, -90, 25, 80, -57, -57, 80, 25, -90, 9, 87, -43, -70, 70, 43, -87, -9, 90, -25, -80, 57,
        54, -85, -4, 88, -46, -61, 82, 13, -90, 38, 67, -78, -22, 90, -31, -73, 73, 31, -90, 22, 78, -67, -38, 90, -13, -82, 61, 46, -88, 4, 85, -54,
        50, -89, 18, 75, -75, -18, 89, -50, -50, 89, -18, -75, 75, 18, -89, 50, 50, -89, 18, 75, -75, -18, 89, -50, -50, 89, -18, -75, 75, 18, -89, 50,
        46, -90, 38, 54, -90, 31, 61, -88, 22, 67, -85, 13, 73, -82, 4, 78, -78, -4, 82, -73, -13, 85, -67, -22, 88, -61, -31, 90, -54, -38, 90, -46,
        43, -90, 57, 25, -87, 70, 9, -80, 80, -9, -70, 87, -25, -57, 90, -43, -43, 90, -57, -25, 87, -70, -9, 80, -80, 9, 70, -87, 25, 57, -90, 43,
        38, -88, 73, -4, -67, 90, -46, -31, 85, -78, 13, 61, -90, 54, 22, -82, 82, -22, -54, 90, -61, -13, 78, -85, 31, 46, -90, 67, 4, -73, 88, -38,
        36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36,
        31, -78, 90, -61, 4, 54, -88, 82, -38, -22, 73, -90, 67, -13, -46, 85, -85, 46, 13, -67, 90, -73, 22, 38, -82, 88, -54, -4, 61, -90, 78, -31,
        25, -70, 90, -80, 43, 9, -57, 87, -87, 57, -9, -43, 80, -90, 70, -25, -25, 70, -90, 80, -43, -9, 57, -87, 87, -57, 9, 43, -80, 90, -70, 25,
        22, -61, 85, -90, 73, -38, -4, 46, -78, 90, -82, 54, -13, -31, 67, -88, 88, -67, 31, 13, -54, 82, -90, 78, -46, 4, 38, -73, 90, -85, 61, -22,
        18, -50, 75, -89, 89, -75, 50, -18, -18, 50, -75, 89, -89, 75, -50, 18, 18, -50, 75, -89, 89, -75, 50, -18, -18, 50, -75, 89, -89, 75, -50, 18,
        13, -38, 61, -78, 88, -90, 85, -73, 54, -31, 4, 22, -46, 67, -82, 90, -90, 82, -67, 46, -22, -4, 31, -54, 73, -85, 90, -88, 78, -61, 38, -13,
        9, -25, 43, -57, 70, -80, 87, -90, 90, -87, 80, -70, 57, -43, 25, -9, -9, 25, -43, 57, -70, 80, -87, 90, -90, 87, -80, 70, -57, 43, -25, 9,
        4, -13, 22, -31, 38, -46, 54, -61, 67, -73, 78, -82, 85, -88, 90, -90, 90, -90, 88, -85, 82, -78, 73, -67, 61, -54, 46, -38, 31, -22, 13, -4,
    ];
}
