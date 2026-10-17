using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Rows are transform block widths (4 to 32 samples): four or eight samples per step. Squared errors of a row fit in int
// (32 * 1023^2), so the encoder's double sums are unchanged.
internal static partial class HevcSamples
{
    internal static void StoreClippedVector(Span<ushort> row, ReadOnlySpan<int> prediction, ReadOnlySpan<int> residual, int maximum)
    {
        int length = row.Length & ~3;
        prediction = prediction[..length];
        residual = residual[..length];
        ref ushort destination = ref MemoryMarshal.GetReference(row);
        ref int predicted = ref MemoryMarshal.GetReference(prediction);
        ref int added = ref MemoryMarshal.GetReference(residual);
        Vector128<int> upper = Vector128.Create(maximum);
        int x = 0;
        for (; x + 8 <= length; x += 8)
        {
            Vector128<int> low = Clip(Vector128.LoadUnsafe(ref predicted, (nuint)x) + Vector128.LoadUnsafe(ref added, (nuint)x), upper);
            Vector128<int> high = Clip(Vector128.LoadUnsafe(ref predicted, (nuint)(x + 4)) + Vector128.LoadUnsafe(ref added, (nuint)(x + 4)), upper);
            Vector128.Narrow(low.AsUInt32(), high.AsUInt32()).StoreUnsafe(ref destination, (nuint)x);
        }

        if (x < length)
        {
            Vector128<int> last = Clip(Vector128.LoadUnsafe(ref predicted, (nuint)x) + Vector128.LoadUnsafe(ref added, (nuint)x), upper);
            StoreLower(ref Unsafe.Add(ref destination, x), last);
        }

        StoreClippedScalar(row[length..], prediction[length..], residual[length..], maximum);
    }

    internal static void StoreVector(Span<ushort> row, ReadOnlySpan<int> prediction)
    {
        int length = row.Length & ~3;
        prediction = prediction[..length];
        ref ushort destination = ref MemoryMarshal.GetReference(row);
        ref int predicted = ref MemoryMarshal.GetReference(prediction);
        int x = 0;
        for (; x + 8 <= length; x += 8)
        {
            Vector128.Narrow(Vector128.LoadUnsafe(ref predicted, (nuint)x).AsUInt32(), Vector128.LoadUnsafe(ref predicted, (nuint)(x + 4)).AsUInt32())
                .StoreUnsafe(ref destination, (nuint)x);
        }

        if (x < length)
        {
            StoreLower(ref Unsafe.Add(ref destination, x), Vector128.LoadUnsafe(ref predicted, (nuint)x));
        }

        StoreScalar(row[length..], prediction[length..]);
    }

    internal static void SubtractVector(ReadOnlySpan<ushort> original, ReadOnlySpan<int> prediction, Span<int> residual)
    {
        int length = residual.Length & ~3;
        original = original[..length];
        prediction = prediction[..length];
        ref ushort source = ref MemoryMarshal.GetReference(original);
        ref int predicted = ref MemoryMarshal.GetReference(prediction);
        ref int destination = ref MemoryMarshal.GetReference(residual);
        for (int x = 0; x < length; x += 4)
        {
            (LoadWiden(ref Unsafe.Add(ref source, x)) - Vector128.LoadUnsafe(ref predicted, (nuint)x)).StoreUnsafe(ref destination, (nuint)x);
        }

        SubtractScalar(original[length..], prediction[length..], residual[length..]);
    }

    internal static int StoreClippedErrorVector(Span<ushort> row, ReadOnlySpan<int> prediction, ReadOnlySpan<int> residual,
        int maximum, ReadOnlySpan<ushort> original)
    {
        int length = row.Length & ~3;
        prediction = prediction[..length];
        residual = residual[..length];
        original = original[..length];
        ref ushort destination = ref MemoryMarshal.GetReference(row);
        ref int predicted = ref MemoryMarshal.GetReference(prediction);
        ref int added = ref MemoryMarshal.GetReference(residual);
        ref ushort source = ref MemoryMarshal.GetReference(original);
        Vector128<int> upper = Vector128.Create(maximum);
        Vector128<int> error = Vector128<int>.Zero;
        for (int x = 0; x < length; x += 4)
        {
            Vector128<int> value = Clip(Vector128.LoadUnsafe(ref predicted, (nuint)x) + Vector128.LoadUnsafe(ref added, (nuint)x), upper);
            StoreLower(ref Unsafe.Add(ref destination, x), value);
            Vector128<int> difference = LoadWiden(ref Unsafe.Add(ref source, x)) - value;
            error += difference * difference;
        }

        return Vector128.Sum(error) + StoreClippedErrorScalar(row[length..], prediction[length..], residual[length..], maximum,
            original[length..]);
    }

    // Four 16-bit samples widened to int.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> LoadWiden(ref ushort source) =>
        Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref source))).AsUInt16())
            .AsInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clip(Vector128<int> value, Vector128<int> maximum) =>
        Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), maximum);

    // Four samples (already in range) narrowed to 16 bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreLower(ref ushort destination, Vector128<int> samples) =>
        Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref destination),
            Vector128.Narrow(samples.AsUInt32(), samples.AsUInt32()).AsUInt64().ToScalar());
}
