using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Eight samples per step as 16-bit lanes (samples and offsets fit: at most 10-bit samples in the supported profiles).
internal static partial class HevcSao
{
    // Returns the number of samples written; the caller finishes the rest with the scalar path.
    internal static int EdgeRowVector(ReadOnlySpan<ushort> current, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> destination,
        ReadOnlySpan<short> offsets, int maximum)
    {
        int length = current.Length & ~7;
        if (length == 0)
        {
            return 0;
        }

        a = a[..length];
        b = b[..length];
        destination = destination[..length];
        ref short c = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(current));
        ref short na = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(a));
        ref short nb = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(b));
        ref short output = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(destination));
        Vector128<short> o0 = Vector128.Create(offsets[0]);
        Vector128<short> o1 = Vector128.Create(offsets[1]);
        Vector128<short> o2 = Vector128.Create(offsets[2]);
        Vector128<short> o3 = Vector128.Create(offsets[3]);
        Vector128<short> upper = Vector128.Create((short)maximum);
        Vector128<short> two = Vector128.Create((short)2);
        for (int x = 0; x < length; x += 8)
        {
            Vector128<short> sample = Vector128.LoadUnsafe(ref c, (nuint)x);
            Vector128<short> left = Vector128.LoadUnsafe(ref na, (nuint)x);
            Vector128<short> right = Vector128.LoadUnsafe(ref nb, (nuint)x);
            // Comparison masks are -1 where true, so (less - greater) is Sign(sample - neighbour).
            Vector128<short> edge = Vector128.LessThan(sample, left) - Vector128.GreaterThan(sample, left)
                + Vector128.LessThan(sample, right) - Vector128.GreaterThan(sample, right);
            Vector128<short> offset = (Vector128.Equals(edge, -two) & o0) | (Vector128.Equals(edge, -Vector128<short>.One) & o1)
                | (Vector128.Equals(edge, Vector128<short>.One) & o2) | (Vector128.Equals(edge, two) & o3);
            Vector128.Min(Vector128.Max(sample + offset, Vector128<short>.Zero), upper).StoreUnsafe(ref output, (nuint)x);
        }

        return length;
    }
}
