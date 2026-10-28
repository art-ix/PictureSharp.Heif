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
        (HeifCodedImage coded, List<HeifTransform> transforms) = HeifImageEncoder.TenBit(map.Frames.RootFrame, options.BitDepth)
            ? Code(HeifImageEncoder.Wide(map.Frames.RootFrame), map.Width, map.Height, options, cancellationToken)
            : Code(HeifImageEncoder.Narrow(map.Frames.RootFrame), map.Width, map.Height, options, cancellationToken);
        transforms.AddRange(orientation);
        return new HeifGainMapContent(coded, HeifImageEncoder.SignalledColour, transforms, metadata, alternateIcc, width, height);
    }

    // A map coded with 8 bits, or 10 for a 16-bit map (HeifEncoderOptions.BitDepth, as for the image).
    private static (HeifCodedImage Image, List<HeifTransform> Transforms) Code<TPixel>(TPixel[] pixels, int width, int height,
        HeifEncoderOptions options, CancellationToken cancellationToken) where TPixel : unmanaged
    {
        HeifImageEncoder.PlaneSource source = IsGray(pixels) ? HeifImageEncoder.PlaneSource.Gray : HeifImageEncoder.PlaneSource.YCbCr;
        return HeifImageEncoder.Code(pixels, width, height, source, options.GainMapQuality, aqStrength: 0, options, cancellationToken);
    }

    private static bool IsGray<TPixel>(ReadOnlySpan<TPixel> pixels) where TPixel : unmanaged
    {
        foreach (TPixel pixel in pixels)
        {
            bool gray = pixel switch
            {
                Rgba32 p => p.R == p.G && p.G == p.B,
                Rgba64 p => p.R == p.G && p.G == p.B,
                _ => false,
            };
            if (!gray)
            {
                return false;
            }
        }

        return true;
    }
}
