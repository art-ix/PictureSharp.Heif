using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Codecs;

// Eight pixels per step with the scalar fixed-point formulas; the row tail goes through the scalar code.
internal sealed partial class HeifColorConverter
{
    internal void ConvertRow8Vector(HeifPlanes planes, Span<Rgba32> row, int y)
    {
        int width = row.Length;
        int vectorWidth = width & ~7;
        if (vectorWidth > 0)
        {
            int cy = y >> 1;
            ReadOnlySpan<ushort> luma = planes.Planes[0].AsSpan(((planes.Top + y) * planes.Strides[0]) + planes.Left, vectorWidth);
            int chromaStart = planes.Left >> 1;
            ReadOnlySpan<ushort> cbRow = planes.Planes[1].AsSpan((((planes.Top >> 1) + cy) * planes.Strides[1]) + chromaStart, vectorWidth >> 1);
            ReadOnlySpan<ushort> crRow = planes.Planes[2].AsSpan((((planes.Top >> 1) + cy) * planes.Strides[2]) + chromaStart, vectorWidth >> 1);
            Span<uint> destination = MemoryMarshal.Cast<Rgba32, uint>(row)[..vectorWidth];
            ref ushort lumaRef = ref MemoryMarshal.GetReference(luma);
            ref ushort cbRef = ref MemoryMarshal.GetReference(cbRow);
            ref ushort crRef = ref MemoryMarshal.GetReference(crRow);
            ref uint output = ref MemoryMarshal.GetReference(destination);
            for (int x = 0; x < vectorWidth; x += 8)
            {
                (Vector128<uint> lumaLow, Vector128<uint> lumaHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref lumaRef, (nuint)x));
                Vector128<int> cb = LoadChroma(ref Unsafe.Add(ref cbRef, x >> 1));
                Vector128<int> cr = LoadChroma(ref Unsafe.Add(ref crRef, x >> 1));
                Convert8(lumaLow.AsInt32(), Duplicate(cb, 0), Duplicate(cr, 0)).StoreUnsafe(ref output, (nuint)x);
                Convert8(lumaHigh.AsInt32(), Duplicate(cb, 2), Duplicate(cr, 2)).StoreUnsafe(ref output, (nuint)(x + 4));
            }
        }

        ConvertRow8(planes, row, y, vectorWidth);
    }

    // Four chroma samples, minus 128.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LoadChroma(ref ushort source) =>
        Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref source))).AsUInt16())
            .AsInt32() - Vector128.Create(128);

    // Chroma samples first and first + 1, each repeated for two pixels (nearest neighbour).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Duplicate(Vector128<int> chroma, int first) => first == 0
        ? Vector128.Shuffle(chroma, Vector128.Create(0, 0, 1, 1))
        : Vector128.Shuffle(chroma, Vector128.Create(2, 2, 3, 3));

    // Four RGBA pixels packed as R | G << 8 | B << 16 | A << 24, the Rgba32 byte order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector128<uint> Convert8(Vector128<int> luma, Vector128<int> cb, Vector128<int> cr)
    {
        Vector128<int> yValue = (luma << 8) + Vector128.Create(128);
        Vector128<int> r = Clip8(Vector128.ShiftRightArithmetic(yValue + (Vector128.Create(crR8) * cr), 8));
        Vector128<int> g = Clip8(Vector128.ShiftRightArithmetic(yValue - (Vector128.Create(cbG8) * cb) - (Vector128.Create(crG8) * cr), 8));
        Vector128<int> b = Clip8(Vector128.ShiftRightArithmetic(yValue + (Vector128.Create(cbB8) * cb), 8));
        return (r | (g << 8) | (b << 16)).AsUInt32() | Vector128.Create(0xFF000000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clip8(Vector128<int> value) =>
        Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), Vector128.Create(255));
}
