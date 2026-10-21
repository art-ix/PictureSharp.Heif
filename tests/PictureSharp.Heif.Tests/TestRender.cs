namespace PictureSharp.Formats.Heif.Tests;

// The 0.6.8 call shapes over ApplyHeifEdits, for measurement code that renders one stage with caller-owned inputs.
internal static class TestRender
{
    public static Image Portrait(Image photo, HeifDepthImage depth, Image? matte = null, HeifPortraitOptions? options = null,
        int threads = -1, CancellationToken cancellationToken = default)
    {
        using var maps = new HeifPortraitMaps(depth, matte?.Clone());
        return photo.ApplyHeifEdits(new HeifEdits { Portrait = options ?? new(), MaxDegreeOfParallelism = threads }, maps, cancellationToken);
    }

    public static Image Adjust(Image photo, HeifPhotosAdjustments adjustments, int threads = -1, CancellationToken cancellationToken = default) =>
        photo.ApplyHeifEdits(new HeifEdits { Adjustments = adjustments, MaxDegreeOfParallelism = threads }, null, cancellationToken);
}
