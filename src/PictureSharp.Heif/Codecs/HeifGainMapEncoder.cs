using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// The gain map written next to an encoded image: this plugin's own map copied with its transforms when the base image
// is written without orientation transforms, otherwise the map in the geometry of the image's pixels, coded as a
// monochrome (gray map) or 4:2:0 image with the base image's orientation transforms.
internal static class HeifGainMapEncoder
{
    public static HeifGainMapContent Prepare(Image image, EncodableGainMap gainMap, HeifEncoderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<HeifTransform> orientation = [.. HeifImageEncoder.Transforms(image.Metadata.Orientation)];
        bool swaps = orientation.OfType<HeifRotation>().Any(rotation => rotation.SwapsDimensions);
        (int width, int height) = swaps ? (image.Height, image.Width) : (image.Width, image.Height);
        byte[] metadata = [0, .. gainMap.Metadata.ToIso()];
        byte[]? alternateIcc = gainMap.Metadata.ToAlternateIcc();
        if (gainMap.Map is HeifGainMapImage { Stored: { } stored } own && orientation.Count == 0)
        {
            return new HeifGainMapContent(stored, own.Node.Nclx, own.Node.Transforms, metadata, alternateIcc, width, height);
        }

        using Image map = gainMap.Map.Decode(options.MaxDegreeOfParallelism, cancellationToken);
        var pixels = new Rgba32[map.Width * map.Height];
        map.Frames.RootFrame.CopyPixelDataTo(pixels);
        HeifImageEncoder.PlaneSource source = IsGray(pixels) ? HeifImageEncoder.PlaneSource.Gray : HeifImageEncoder.PlaneSource.YCbCr;
        (HeifCodedImage coded, List<HeifTransform> transforms) = HeifImageEncoder.Code(pixels, map.Width, map.Height, source,
            options.GainMapQuality, aqStrength: 0, options, cancellationToken);
        transforms.AddRange(orientation);
        return new HeifGainMapContent(coded, HeifImageEncoder.SignalledColour, transforms, metadata, alternateIcc, width, height);
    }

    private static bool IsGray(ReadOnlySpan<Rgba32> pixels)
    {
        foreach (Rgba32 p in pixels)
        {
            if (p.R != p.G || p.G != p.B)
            {
                return false;
            }
        }

        return true;
    }
}
