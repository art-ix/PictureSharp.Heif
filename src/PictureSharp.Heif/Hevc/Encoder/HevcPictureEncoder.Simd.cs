using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Vector SATD: each 4x4 Hadamard runs down the four row vectors, then within each vector (two butterfly stages by
// shuffle and select). The 16 outputs are those of the scalar transform in another order, so the sums are equal.
internal sealed partial class HevcPictureEncoder
{
    internal static double SatdVector(ushort[] original, int stride, int x0, int y0, int[] prediction, int size)
    {
        long total = 0;
        for (int by = 0; by < size; by += 4)
        {
            for (int bx = 0; bx < size; bx += 4)
            {
                Vector128<int> d0 = Difference(original, ((y0 + by) * stride) + x0 + bx, prediction, (by * size) + bx);
                Vector128<int> d1 = Difference(original, ((y0 + by + 1) * stride) + x0 + bx, prediction, ((by + 1) * size) + bx);
                Vector128<int> d2 = Difference(original, ((y0 + by + 2) * stride) + x0 + bx, prediction, ((by + 2) * size) + bx);
                Vector128<int> d3 = Difference(original, ((y0 + by + 3) * stride) + x0 + bx, prediction, ((by + 3) * size) + bx);
                Vector128<int> a0 = d0 + d3;
                Vector128<int> a1 = d1 + d2;
                Vector128<int> a2 = d1 - d2;
                Vector128<int> a3 = d0 - d3;
                Vector128<int> sum = Vector128.Abs(Hadamard4(a0 + a1)) + Vector128.Abs(Hadamard4(a0 - a1))
                    + Vector128.Abs(Hadamard4(a2 + a3)) + Vector128.Abs(Hadamard4(a3 - a2));
                total += (Vector128.Sum(sum) + 1) >> 1;
            }
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Difference(ushort[] original, int offset, int[] prediction, int predictionOffset) =>
        HevcSamples.LoadWiden(ref original.AsSpan(offset, 4)[0]) - Vector128.Create(prediction.AsSpan(predictionOffset, 4));

    // [a, b, c, d] to [a+b+c+d, a-b+c-d, a+b-c-d, a-b-c+d].
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Hadamard4(Vector128<int> v)
    {
        Vector128<int> p = Vector128.Shuffle(v, Vector128.Create(2, 3, 0, 1));
        v = Vector128.ConditionalSelect(Vector128.Create(-1, -1, 0, 0), v + p, p - v);
        Vector128<int> q = Vector128.Shuffle(v, Vector128.Create(1, 0, 3, 2));
        return Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0), v + q, q - v);
    }
}
