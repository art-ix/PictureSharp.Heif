using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Vector inverse DCT: both stages as matrix products over four or eight columns at a time, which give the same integer
// sums as the scalar butterflies (no overflow: 32 products of 16-bit values and 8-bit weights). Rows and columns past
// the last nonzero coefficient are skipped. Vector dequantization follows.
internal static partial class HevcTransform
{
    // DctMatrix widened to int, so vector loads need no conversion.
    private static readonly int[] DctMatrix32 = WidenDctMatrix();

    // wide selects 256-bit vectors for blocks of 8x8 and larger.
    internal static void InverseDctVector(ReadOnlySpan<int> coefficients, Span<int> residual, Span<int> temporary, int log2Size,
        int shift2, bool wide)
    {
        int size = 1 << log2Size;
        int count = size * size;
        coefficients = coefficients[..count];
        residual = residual[..count];
        temporary = temporary[..count];
        int step = 32 >> log2Size;

        // Nonzero extent: rows (vertical frequencies) and columns (horizontal frequencies).
        int rows = 0;
        int columns = 0;
        for (int k = 0; k < size; k++)
        {
            int last = coefficients.Slice(k * size, size).LastIndexOfAnyExcept(0);
            if (last >= 0)
            {
                rows = k + 1;
                columns = Math.Max(columns, last + 1);
            }
        }

        if (rows == 0)
        {
            residual.Clear();
            return;
        }

        if (wide && size >= 8)
        {
            InverseDctStages256(coefficients, residual, temporary, size, step, rows, columns, shift2);
        }
        else
        {
            InverseDctStages128(coefficients, residual, temporary, size, step, rows, columns, shift2);
        }
    }

    // First stage: temporary[y][x] = sum over k < rows of T[k][y] * c[k][x], for the columns that may be nonzero.
    // Second stage: residual[y][x] = sum over k < columns of temporary[y][k] * T[k][x] (later columns are zero).
    private static void InverseDctStages128(ReadOnlySpan<int> coefficients, Span<int> residual, Span<int> temporary, int size,
        int step, int rows, int columns, int shift2)
    {
        ref int matrix = ref MemoryMarshal.GetArrayDataReference(DctMatrix32);
        ref int input = ref MemoryMarshal.GetReference(coefficients);
        ref int middle = ref MemoryMarshal.GetReference(temporary);
        ref int output = ref MemoryMarshal.GetReference(residual);
        Vector128<int> minimum = Vector128.Create((int)short.MinValue);
        Vector128<int> maximum = Vector128.Create((int)short.MaxValue);
        int vectorColumns = (columns + 3) & ~3;
        Vector128<int> add1 = Vector128.Create(64);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < vectorColumns; x += 4)
            {
                Vector128<int> sum = Vector128<int>.Zero;
                for (int k = 0; k < rows; k++)
                {
                    Vector128<int> weight = Vector128.Create(Unsafe.Add(ref matrix, (k * step * 32) + y));
                    sum += weight * Vector128.LoadUnsafe(ref input, (nuint)((k * size) + x));
                }

                Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(sum + add1, 7), minimum), maximum)
                    .StoreUnsafe(ref middle, (nuint)((y * size) + x));
            }
        }

        Vector128<int> add2 = Vector128.Create(1 << (shift2 - 1));
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x += 4)
            {
                Vector128<int> sum = Vector128<int>.Zero;
                for (int k = 0; k < columns; k++)
                {
                    Vector128<int> value = Vector128.Create(Unsafe.Add(ref middle, (y * size) + k));
                    sum += value * Vector128.LoadUnsafe(ref matrix, (nuint)((k * step * 32) + x));
                }

                Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(sum + add2, shift2), minimum), maximum)
                    .StoreUnsafe(ref output, (nuint)((y * size) + x));
            }
        }
    }

    // The same stages eight columns at a time, for blocks of 8x8 and larger.
    private static void InverseDctStages256(ReadOnlySpan<int> coefficients, Span<int> residual, Span<int> temporary, int size,
        int step, int rows, int columns, int shift2)
    {
        ref int matrix = ref MemoryMarshal.GetArrayDataReference(DctMatrix32);
        ref int input = ref MemoryMarshal.GetReference(coefficients);
        ref int middle = ref MemoryMarshal.GetReference(temporary);
        ref int output = ref MemoryMarshal.GetReference(residual);
        Vector256<int> minimum = Vector256.Create((int)short.MinValue);
        Vector256<int> maximum = Vector256.Create((int)short.MaxValue);
        int vectorColumns = (columns + 7) & ~7;
        Vector256<int> add1 = Vector256.Create(64);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < vectorColumns; x += 8)
            {
                Vector256<int> sum = Vector256<int>.Zero;
                for (int k = 0; k < rows; k++)
                {
                    Vector256<int> weight = Vector256.Create(Unsafe.Add(ref matrix, (k * step * 32) + y));
                    sum += weight * Vector256.LoadUnsafe(ref input, (nuint)((k * size) + x));
                }

                Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sum + add1, 7), minimum), maximum)
                    .StoreUnsafe(ref middle, (nuint)((y * size) + x));
            }
        }

        Vector256<int> add2 = Vector256.Create(1 << (shift2 - 1));
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x += 8)
            {
                Vector256<int> sum = Vector256<int>.Zero;
                for (int k = 0; k < columns; k++)
                {
                    Vector256<int> value = Vector256.Create(Unsafe.Add(ref middle, (y * size) + k));
                    sum += value * Vector256.LoadUnsafe(ref matrix, (nuint)((k * step * 32) + x));
                }

                Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sum + add2, shift2), minimum), maximum)
                    .StoreUnsafe(ref output, (nuint)((y * size) + x));
            }
        }
    }

    // xDeQuant over every coefficient: a zero level gives zero in both branches, so no test per coefficient is needed.
    private static void DequantizeVector(Span<int> coefficients, int scale, ReadOnlySpan<int> factors, int rightShift,
        int inputMinimum, int inputMaximum)
    {
        ref int level = ref MemoryMarshal.GetReference(coefficients);
        ref int factor = ref MemoryMarshal.GetReference(factors);
        bool scaling = !factors.IsEmpty;
        Vector128<int> scales = Vector128.Create(scale);
        Vector128<int> inputLow = Vector128.Create(inputMinimum);
        Vector128<int> inputHigh = Vector128.Create(inputMaximum);
        Vector128<int> transformLow = Vector128.Create(-(1 << MaxLog2TrDynamicRange));
        Vector128<int> transformHigh = Vector128.Create((1 << MaxLog2TrDynamicRange) - 1);
        Vector128<int> add = Vector128.Create(rightShift > 0 ? 1 << (rightShift - 1) : 0);
        for (int n = 0; n < coefficients.Length; n += 4)
        {
            Vector128<int> multiplier = scaling ? scales * Vector128.LoadUnsafe(ref factor, (nuint)n) : scales;
            Vector128<int> value = Vector128.Min(Vector128.Max(Vector128.LoadUnsafe(ref level, (nuint)n), inputLow), inputHigh) * multiplier;
            value = rightShift > 0 ? Vector128.ShiftRightArithmetic(value + add, rightShift) : value << -rightShift;
            Vector128.Min(Vector128.Max(value, transformLow), transformHigh).StoreUnsafe(ref level, (nuint)n);
        }
    }

    private static int[] WidenDctMatrix()
    {
        int[] matrix = new int[32 * 32];
        for (int i = 0; i < matrix.Length; i++)
        {
            matrix[i] = DctMatrix[i];
        }

        return matrix;
    }
}
