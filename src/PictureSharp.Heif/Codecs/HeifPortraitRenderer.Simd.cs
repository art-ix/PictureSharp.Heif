using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Codecs;

// Vector paths of the portrait renderer; each produces exactly the scalar result (IEEE square root and division per
// lane, the same operation order, no fused multiply-add).
internal sealed partial class HeifPortraitRenderer
{
    // AddDiscRowScalar for Vector<float>.Count columns at a time; returns the first dx left for the scalar tail.
    internal static int AddDiscRowVector(Span<float> red, Span<float> green, Span<float> blue, int cx, Vector3 light, float radius,
        float total, int dy, int left, int right)
    {
        int count = Vector<float>.Count;
        var lanes = Vector<int>.Indices;
        var radiusHalf = new Vector<float>(radius);
        var half = new Vector<float>(0.5f);
        var one = Vector<float>.One;
        var sum = new Vector<float>(total);
        var lightX = new Vector<float>(light.X);
        var lightY = new Vector<float>(light.Y);
        var lightZ = new Vector<float>(light.Z);
        var rowSquared = new Vector<int>(dy * dy);
        int dx = left;
        for (; dx + count - 1 <= right; dx += count)
        {
            Vector<int> column = new Vector<int>(dx) + lanes;
            Vector<float> distance = Vector.SquareRoot(Vector.ConvertToSingle(column * column + rowSquared));
            // Math.Clamp(radius − distance + 0.5, 0, 1); taps inside the disc are positive, so no signed zero arises.
            Vector<float> weight = Vector.Min(Vector.Max(radiusHalf - distance + half, Vector<float>.Zero), one);
            Vector<float> share = weight / sum;
            int x = cx + dx;
            (new Vector<float>(red[x..]) + lightX * share).CopyTo(red[x..]);
            (new Vector<float>(green[x..]) + lightY * share).CopyTo(green[x..]);
            (new Vector<float>(blue[x..]) + lightZ * share).CopyTo(blue[x..]);
        }

        return dx;
    }

    // ConvolveScalar for two texels per Vector256 (their Vector4 lanes side by side); returns the texels done. Callers
    // guarantee every tap of the output texels lies inside texels.
    internal static int ConvolveVector(ReadOnlySpan<Vector4> texels, int centre, Span<Vector4> output, int[] offsets, float[] weights, float total)
    {
        ref float source = ref Unsafe.As<Vector4, float>(ref MemoryMarshal.GetReference(texels));
        ref float target = ref Unsafe.As<Vector4, float>(ref MemoryMarshal.GetReference(output));
        Vector256<float> divisor = Vector256.Create(total);
        int j = 0;
        for (; j + 2 <= output.Length; j += 2)
        {
            Vector256<float> sum = Vector256<float>.Zero;
            for (int t = 0; t < offsets.Length; t++)
            {
                int index = centre + j + offsets[t];
                // Bounds as in the scalar path; a texel pair beyond the span would be a caller error.
                if ((uint)index > (uint)(texels.Length - 2))
                {
                    throw new ArgumentOutOfRangeException(nameof(centre));
                }

                sum += Vector256.LoadUnsafe(ref source, (nuint)index * 4) * Vector256.Create(weights[t]);
            }

            (sum / divisor).StoreUnsafe(ref target, (nuint)j * 4);
        }

        return j;
    }
}
