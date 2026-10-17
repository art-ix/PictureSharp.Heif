using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// Stores reconstructed sample rows (8.6.7 picture construction): prediction plus residual, clipped to the bit depth.
internal static partial class HevcSamples
{
    public static void StoreClipped(Span<ushort> row, ReadOnlySpan<int> prediction, ReadOnlySpan<int> residual, int maximum)
    {
        if (Vector128.IsHardwareAccelerated && row.Length >= 4)
        {
            StoreClippedVector(row, prediction, residual, maximum);
        }
        else
        {
            StoreClippedScalar(row, prediction, residual, maximum);
        }
    }

    public static void Store(Span<ushort> row, ReadOnlySpan<int> prediction)
    {
        if (Vector128.IsHardwareAccelerated && row.Length >= 4)
        {
            StoreVector(row, prediction);
        }
        else
        {
            StoreScalar(row, prediction);
        }
    }

    // residual = original - prediction (encoder).
    public static void Subtract(ReadOnlySpan<ushort> original, ReadOnlySpan<int> prediction, Span<int> residual)
    {
        if (Vector128.IsHardwareAccelerated && residual.Length >= 4)
        {
            SubtractVector(original, prediction, residual);
        }
        else
        {
            SubtractScalar(original, prediction, residual);
        }
    }

    // StoreClipped that also returns the squared error against the original row (encoder).
    public static int StoreClippedError(Span<ushort> row, ReadOnlySpan<int> prediction, ReadOnlySpan<int> residual, int maximum,
        ReadOnlySpan<ushort> original)
    {
        return Vector128.IsHardwareAccelerated && row.Length >= 4
            ? StoreClippedErrorVector(row, prediction, residual, maximum, original)
            : StoreClippedErrorScalar(row, prediction, residual, maximum, original);
    }

    internal static void SubtractScalar(ReadOnlySpan<ushort> original, ReadOnlySpan<int> prediction, Span<int> residual)
    {
        for (int x = 0; x < residual.Length; x++)
        {
            residual[x] = original[x] - prediction[x];
        }
    }

    internal static int StoreClippedErrorScalar(Span<ushort> row, ReadOnlySpan<int> prediction, ReadOnlySpan<int> residual,
        int maximum, ReadOnlySpan<ushort> original)
    {
        int error = 0;
        for (int x = 0; x < row.Length; x++)
        {
            int value = HevcTransform.Clip(prediction[x] + residual[x], maximum);
            row[x] = (ushort)value;
            int difference = original[x] - value;
            error += difference * difference;
        }

        return error;
    }

    internal static void StoreClippedScalar(Span<ushort> row, ReadOnlySpan<int> prediction, ReadOnlySpan<int> residual, int maximum)
    {
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (ushort)HevcTransform.Clip(prediction[x] + residual[x], maximum);
        }
    }

    internal static void StoreScalar(Span<ushort> row, ReadOnlySpan<int> prediction)
    {
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (ushort)prediction[x];
        }
    }
}
