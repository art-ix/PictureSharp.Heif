using System.Drawing;

namespace PictureSharp.Formats.Heif.Codecs;

// Prepares the portrait renderer: disparity from the depth samples, the focus range and the photograph's exposure.
internal static class HeifPortraitSetup
{
    // The exposure's ISO sets the grain level in blurred areas; photographs without it get the level of ISO 64.
    private static float Iso(Image photo) =>
        photo.Metadata.ExifProfile.TryGetValue(ExifTag.IsoSpeed, out var iso) && iso > 0 ? iso : 64;

    // Exposure time in seconds; 0 when unknown. ISO × time tells night shots, where lamps are clipped, from daylight.
    private static float ExposureTime(Image photo) =>
        photo.Metadata.ExifProfile.TryGetValue(ExifTag.ExposureTime, out ExifRational time) && time.Denominator != 0 ? (float)time.ToDouble() : 0;

    // Validates the photograph, depth, matte and options and prepares the renderer. Inconsistent depth or matte from a
    // file is corrupt data; from a caller it is an argument error.
    public static HeifPortraitRenderer Create(Image photo, HeifPortraitMaps maps, HeifPortraitOptions options, int maxDegreeOfParallelism,
        bool fromFile, CancellationToken cancellationToken)
    {
        HeifDepthImage depth = maps.Depth;
        Image? matte = maps.Matte;
        HeifAppleDepthInfo description = depth.AppleDepth
            ?? throw (fromFile
                ? new NotSupportedException($"HEIF depth item {depth.ItemId} has no Apple depth description.")
                : new ArgumentException("The depth image has no Apple depth description.", "maps"));
        int width = photo.Width, height = photo.Height;
        CheckProportions(width, height, depth.Width, depth.Height, "depth image", "maps", fromFile);
        if (matte is not null)
        {
            CheckProportions(width, height, matte.Width, matte.Height, "matte", "maps", fromFile);
        }

        if (options.FocusPoint is { } point && (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height))
        {
            throw new ArgumentOutOfRangeException("edits", point, "FocusPoint lies outside the photograph.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        float[] disparity = Disparity(depth, description);
        byte[]? matteSamples = matte is null ? null : Red(matte);
        float focus, nearFocus;
        if (options.FocusPoint is { } focusPoint)
        {
            focus = nearFocus = FocusAt(disparity, depth.Width, depth.Height, focusPoint, width, height);
        }
        else
        {
            (focus, nearFocus) = SubjectFocus(disparity, depth.Width, depth.Height, matteSamples, matte?.Width ?? 0, matte?.Height ?? 0);
        }
        float aperture = options.Aperture
            ?? (description.SimulatedAperture is { } stored && stored >= HeifPortraitOptions.MinimumAperture && stored <= HeifPortraitOptions.MaximumAperture
                ? stored : HeifPortraitRenderer.ReferenceAperture);
        return new HeifPortraitRenderer(width, height, disparity, depth.Width, depth.Height, matteSamples,
            matte?.Width ?? 0, matte?.Height ?? 0, focus, nearFocus, aperture, FocalRatio(description), maxDegreeOfParallelism,
            cancellationToken, Iso(photo), Iso(photo) * ExposureTime(photo));
    }

    // Matching proportions, as for thumbnails: one pixel of rounding at the photograph's longer side.
    private static void CheckProportions(int width, int height, int mapWidth, int mapHeight, string what, string parameter, bool fromFile)
    {
        long longer = Math.Max(width, height), mapLonger = Math.Max(mapWidth, mapHeight);
        if (Math.Abs(mapWidth * longer - width * mapLonger) <= longer && Math.Abs(mapHeight * longer - height * mapLonger) <= longer)
        {
            return;
        }

        string message = $"The {what} ({mapWidth}×{mapHeight}) does not have the photograph's proportions ({width}×{height}).";
        throw fromFile ? new ImageFormatException(message) : new ArgumentException(message, parameter);
    }

    // Disparity in the description's units (1/m for Apple): native values for disparity maps, reciprocals of depth.
    private static float[] Disparity(HeifDepthImage depth, HeifAppleDepthInfo description)
    {
        ReadOnlySpan<ushort> samples = depth.Samples.Span;
        var result = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            int sample = samples[i];
            if (sample < description.IntMinValue || sample > description.IntMaxValue)
            {
                throw new ImageFormatException($"HEIF depth item {depth.ItemId} has sample {sample} outside the described range " +
                    $"{description.IntMinValue}–{description.IntMaxValue}.");
            }

            float value = description.ToFloat(sample);
            result[i] = description.Kind == HeifAuxiliaryKind.Depth ? (value > 0 ? 1 / value : 0) : value;
        }

        return result;
    }

    private static byte[] Red(Image matte)
    {
        var result = new byte[matte.Width * matte.Height];
        if (matte.PixelType == PixelType.Rgba64)
        {
            ReadOnlySpan<Rgba64> wide = matte.GetPixelSpan<Rgba64>();
            for (int i = 0; i < wide.Length; i++)
            {
                result[i] = wide[i].ToRgba32().R;
            }

            return result;
        }

        ReadOnlySpan<Rgba32> pixels = matte.GetPixelSpan<Rgba32>();
        for (int i = 0; i < pixels.Length; i++)
        {
            result[i] = pixels[i].R;
        }

        return result;
    }

    // The median disparity in a 5×5 neighbourhood of the depth sample under the point.
    private static float FocusAt(float[] disparity, int depthWidth, int depthHeight, Point point, int width, int height)
    {
        int cx = Math.Min(depthWidth - 1, (int)((point.X + 0.5) * depthWidth / width));
        int cy = Math.Min(depthHeight - 1, (int)((point.Y + 0.5) * depthHeight / height));
        var values = new List<float>(25);
        for (int y = Math.Max(0, cy - 2); y <= Math.Min(depthHeight - 1, cy + 2); y++)
        {
            for (int x = Math.Max(0, cx - 2); x <= Math.Min(depthWidth - 1, cx + 2); x++)
            {
                values.Add(disparity[y * depthWidth + x]);
            }
        }

        return Quantile(values, 0.5);
    }

    // The focus range from its far end to its near end: the median disparity under the matte to 95 % of the way from
    // there to the nearest sample (the 97.5th percentile), or the scene's 90th to 99.5th percentile without a matte.
    // The maximum is never used, so a few outlying samples cannot widen the range.
    private static (float Far, float Near) SubjectFocus(float[] disparity, int depthWidth, int depthHeight, byte[]? matte, int matteWidth, int matteHeight)
    {
        if (matte is not null)
        {
            var subject = new List<float>();
            for (int y = 0; y < depthHeight; y++)
            {
                int my = Math.Min(matteHeight - 1, (int)((y + 0.5) * matteHeight / depthHeight));
                for (int x = 0; x < depthWidth; x++)
                {
                    int mx = Math.Min(matteWidth - 1, (int)((x + 0.5) * matteWidth / depthWidth));
                    if (matte[my * matteWidth + mx] >= 128)
                    {
                        subject.Add(disparity[y * depthWidth + x]);
                    }
                }
            }

            if (subject.Count >= Math.Max(16, disparity.Length / 1000))
            {
                return Range(subject, 0.5);
            }
        }

        return Range([.. disparity], 0.9);

        static (float Far, float Near) Range(List<float> values, double start)
        {
            values.Sort();
            float At(double q) => values[(int)Math.Min(values.Count - 1, Math.Round(q * (values.Count - 1)))];
            return (At(start), At(start + 0.95 * (1 - start)));
        }
    }

    private static float Quantile(List<float> values, double quantile)
    {
        values.Sort();
        return values[(int)Math.Min(values.Count - 1, Math.Round(quantile * (values.Count - 1)))];
    }

    // The focal length in units of the sensor's longer side, from the intrinsic matrix (fx is its first element).
    private static float FocalRatio(HeifAppleDepthInfo description)
    {
        if (description.IntrinsicMatrix is [var fx, ..] && description.IntrinsicMatrixReferenceSize is { } size)
        {
            float ratio = fx / Math.Max(size.Width, size.Height);
            if (ratio is >= 0.2f and <= 20f)
            {
                return ratio;
            }
        }

        return HeifPortraitRenderer.DefaultFocalRatio;
    }
}
