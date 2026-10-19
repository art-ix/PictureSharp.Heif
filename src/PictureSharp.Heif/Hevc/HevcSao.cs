using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Hevc;

// H.265 8.7.3.2 sample adaptive offset for runs of samples whose neighbours are all usable and filtered (the CTB
// interior); the picture decoder handles CTB borders and PCM/lossless samples sample by sample.
// port: HM TComSampleAdaptiveOffset::offsetBlock
internal static partial class HevcSao
{
    // Edge offset: offsets[0..3] apply to edgeIdx categories 1, 2, 3 and 4 (sign sums -2, -1, +1 and +2).
    public static void EdgeRow(ReadOnlySpan<ushort> current, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> destination,
        ReadOnlySpan<short> offsets, int maximum)
    {
        int done = Vector128.IsHardwareAccelerated && maximum <= short.MaxValue ? EdgeRowVector(current, a, b, destination, offsets, maximum) : 0;
        EdgeRowScalar(current[done..], a[done..], b[done..], destination[done..], offsets, maximum);
    }

    public static void EdgeRowScalar(ReadOnlySpan<ushort> current, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> destination,
        ReadOnlySpan<short> offsets, int maximum)
    {
        a = a[..current.Length];
        b = b[..current.Length];
        destination = destination[..current.Length];
        for (int x = 0; x < current.Length; x++)
        {
            destination[x] = (ushort)HevcTransform.Clip(current[x] + EdgeOffset(current[x], a[x], b[x], offsets), maximum);
        }
    }

    public static int EdgeOffset(int current, int a, int b, ReadOnlySpan<short> offsets) =>
        (Math.Sign(current - a) + Math.Sign(current - b)) switch
        {
            -2 => offsets[0],
            -1 => offsets[1],
            1 => offsets[2],
            2 => offsets[3],
            _ => 0,
        };

    // Band offset: bandTable holds the offset of each of the 32 bands.
    public static void BandRow(ReadOnlySpan<ushort> current, Span<ushort> destination, ReadOnlySpan<int> bandTable, int bandShift, int maximum)
    {
        destination = destination[..current.Length];
        for (int x = 0; x < current.Length; x++)
        {
            int sample = current[x];
            destination[x] = (ushort)HevcTransform.Clip(sample + bandTable[(sample >> bandShift) & 31], maximum);
        }
    }
}
