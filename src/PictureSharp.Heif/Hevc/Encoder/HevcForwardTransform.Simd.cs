using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Vector forward transform as two matrix products, with the integer sums of the scalar butterflies:
// rows: A[y][k] = sum over i of X[y][i] * T[k][i] (vectors over k, from the transposed matrix),
// columns: C[k2][k1] = sum over y of T[k2][y] * A[y][k1] (vectors over k1).
internal static partial class HevcForwardTransform
{
    // Per size (4, 8, 16, 32, then the 4x4 DST): the matrix T[k][i] and its transpose, as int.
    private static readonly int[][] Matrices = BuildMatrices(transposed: false);
    private static readonly int[][] TransposedMatrices = BuildMatrices(transposed: true);

    // wide selects 256-bit vectors for blocks of 8x8 and larger.
    internal static void ForwardVector(ReadOnlySpan<int> residual, Span<int> coefficients, Span<int> temporary, int log2Size,
        bool dst, int bitDepth, bool wide)
    {
        int size = 1 << log2Size;
        int count = size * size;
        residual = residual[..count];
        coefficients = coefficients[..count];
        temporary = temporary[..count];
        int table = dst ? 4 : log2Size - 2;
        int shift1 = log2Size + bitDepth - 9;
        int shift2 = log2Size + 6;
        if (wide && size >= 8)
        {
            Stage256(residual, temporary, TransposedMatrices[table], size, shift1, broadcastInput: true);
            Stage256(temporary, coefficients, Matrices[table], size, shift2, broadcastInput: false);
        }
        else
        {
            Stage128(residual, temporary, TransposedMatrices[table], size, shift1, broadcastInput: true);
            Stage128(temporary, coefficients, Matrices[table], size, shift2, broadcastInput: false);
        }
    }

    // output[r][c] = (sum over j of a[r][j] * b[j][c] + rounding) >> shift, vectors over c. With broadcastInput, a is the
    // input and b the matrix; otherwise a is the matrix and b the input.
    private static void Stage128(ReadOnlySpan<int> input, Span<int> output, int[] matrix, int size, int shift, bool broadcastInput)
    {
        ref int a = ref broadcastInput ? ref MemoryMarshal.GetReference(input) : ref MemoryMarshal.GetArrayDataReference(matrix);
        ref int b = ref broadcastInput ? ref MemoryMarshal.GetArrayDataReference(matrix) : ref MemoryMarshal.GetReference(input);
        ref int result = ref MemoryMarshal.GetReference(output);
        Vector128<int> add = Vector128.Create(1 << (shift - 1));
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c += 4)
            {
                Vector128<int> sum = Vector128<int>.Zero;
                for (int j = 0; j < size; j++)
                {
                    sum += Vector128.Create(Unsafe.Add(ref a, (r * size) + j)) * Vector128.LoadUnsafe(ref b, (nuint)((j * size) + c));
                }

                Vector128.ShiftRightArithmetic(sum + add, shift).StoreUnsafe(ref result, (nuint)((r * size) + c));
            }
        }
    }

    private static void Stage256(ReadOnlySpan<int> input, Span<int> output, int[] matrix, int size, int shift, bool broadcastInput)
    {
        ref int a = ref broadcastInput ? ref MemoryMarshal.GetReference(input) : ref MemoryMarshal.GetArrayDataReference(matrix);
        ref int b = ref broadcastInput ? ref MemoryMarshal.GetArrayDataReference(matrix) : ref MemoryMarshal.GetReference(input);
        ref int result = ref MemoryMarshal.GetReference(output);
        Vector256<int> add = Vector256.Create(1 << (shift - 1));
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c += 8)
            {
                Vector256<int> sum = Vector256<int>.Zero;
                for (int j = 0; j < size; j++)
                {
                    sum += Vector256.Create(Unsafe.Add(ref a, (r * size) + j)) * Vector256.LoadUnsafe(ref b, (nuint)((j * size) + c));
                }

                Vector256.ShiftRightArithmetic(sum + add, shift).StoreUnsafe(ref result, (nuint)((r * size) + c));
            }
        }
    }

    private static int[][] BuildMatrices(bool transposed)
    {
        var matrices = new int[5][];
        for (int table = 0; table < 5; table++)
        {
            int size = table == 4 ? 4 : 4 << table;
            int[] matrix = new int[size * size];
            for (int k = 0; k < size; k++)
            {
                for (int i = 0; i < size; i++)
                {
                    int value = table == 4 ? DstMatrix[(k * 4) + i] : HevcTransform.DctCoefficient(k * (32 / size), i);
                    matrix[transposed ? (i * size) + k : (k * size) + i] = value;
                }
            }

            matrices[table] = matrix;
        }

        return matrices;
    }
}
