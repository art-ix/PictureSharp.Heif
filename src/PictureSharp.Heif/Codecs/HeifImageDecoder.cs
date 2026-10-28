using System.Diagnostics;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// Decodes the image item of a validated container (the primary item or the one HeifDecoderOptions selects): the rendered image, auxiliary alpha, descriptive metadata and,
// with DecoderOptions.LoadGainMap, the HDR gain map.
internal sealed class HeifImageDecoder(HeifContainer container, ImageDecodeContext context)
{
    public Image Decode()
    {
        ImageInfo info = container.Info;
        // With PreserveBitDepth, colour or alpha coded with more than 8 bits keeps its precision in Rgba64.
        bool wide = context.Options.PreserveBitDepth
            && (info.Color.BitsPerSample > 8 || container.AlphaItem is { } alphaSource && container.BitDepth(alphaSource) > 8);
        context.ReservePixels(info.Width, info.Height, wide ? PixelType.Rgba64 : PixelType.Rgba32, 1);
        var renderer = new HeifRenderer(context.Options.MaxDegreeOfParallelism, context.CancellationToken);
        HeifImageNode primary = container.Node(container.ImageItem, detach: false);
        Image image = wide ? Render<Rgba64>(renderer, primary) : Render<Rgba32>(renderer, primary);
        try
        {
            HeifGainMap? gainMap = context.Options.LoadGainMap ? HeifGainMap.Find(container) : null;
            // The library takes the base image's colour for HDR reconstruction from its metadata, also with IgnoreMetadata.
            if (!context.Options.IgnoreMetadata || gainMap is not null)
            {
                HeifMetadataReader.ApplyColour(container, image.Metadata, context.Options, primary);
            }

            if (!context.Options.IgnoreMetadata)
            {
                HeifMetadataReader.ApplyDescriptive(container, image.Metadata);
            }

            gainMap?.Attach(context);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    private Image Render<TPixel>(HeifRenderer renderer, HeifImageNode primary) where TPixel : unmanaged
    {
        HeifCanvas<TPixel> canvas = renderer.Render<TPixel>(primary, alpha: false);
        HeifCanvas<TPixel>? alpha = container.AlphaItem is { } alphaItem ? renderer.Render<TPixel>(container.Node(alphaItem, detach: false), alpha: true) : null;
        return (canvas, alpha) switch
        {
            (HeifCanvas<Rgba64> wide, var wideAlpha) => WithAlpha(wide, wideAlpha as HeifCanvas<Rgba64>),
            (HeifCanvas<Rgba32> narrow, var narrowAlpha) => WithAlpha(narrow, narrowAlpha as HeifCanvas<Rgba32>),
            _ => throw new UnreachableException(),
        };
    }

    private Image WithAlpha(HeifCanvas<Rgba32> canvas, HeifCanvas<Rgba32>? alpha)
    {
        if (alpha is not null)
        {
            canvas.SetAlpha(alpha, container.PremultipliedAlpha);
        }

        return canvas.ToImage(alpha is not null);
    }

    private Image WithAlpha(HeifCanvas<Rgba64> canvas, HeifCanvas<Rgba64>? alpha)
    {
        if (alpha is not null)
        {
            canvas.SetAlpha(alpha, container.PremultipliedAlpha);
        }

        return canvas.ToImage(alpha is not null);
    }
}
