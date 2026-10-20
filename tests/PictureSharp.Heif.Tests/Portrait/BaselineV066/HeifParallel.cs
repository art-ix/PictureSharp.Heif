// Frozen from v0.6.6. Namespace-only adaptation; do not tune this reference.
namespace PictureSharp.Formats.Heif.Tests.BaselineV066;

// Row bands for per-pixel passes (colour conversion, transforms). Rows are independent, so results do not depend on
// the thread count; small images stay on the calling thread.
internal static class HeifParallel
{
    private const int BandRows = 32;
    private const long MinimumParallelPixels = 1 << 18;

    public static void ForRows(int height, int width, ParallelOptions options, Action<int, int> rows)
    {
        if (options.MaxDegreeOfParallelism == 1 || (long)width * height < MinimumParallelPixels)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            rows(0, height);
            return;
        }

        int bands = (height + BandRows - 1) / BandRows;
        Parallel.For(0, bands, options, band => rows(band * BandRows, Math.Min(height, (band + 1) * BandRows)));
    }
}
