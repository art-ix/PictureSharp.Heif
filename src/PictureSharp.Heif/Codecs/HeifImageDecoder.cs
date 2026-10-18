using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// Decodes the image item of a validated container (the primary item or the one HeifDecoderOptions selects): the rendered image, auxiliary alpha, descriptive metadata and,
// with DecoderOptions.LoadGainMap, the HDR gain map.
internal sealed class HeifImageDecoder(HeifContainer container, ImageDecodeContext context)
{
    public Image Decode()
    {
        ImageInfo info = container.Info;
        context.ReservePixels(info.Width, info.Height, 1);
        var renderer = new HeifRenderer(context.Options.MaxDegreeOfParallelism, context.CancellationToken);
        HeifImageNode primary = container.Node(container.ImageItem, detach: false);
        HeifCanvas canvas = renderer.Render(primary, alpha: false);
        if (container.AlphaItem is { } alphaItem)
        {
            canvas.SetAlpha(renderer.Render(container.Node(alphaItem, detach: false), alpha: true), container.PremultipliedAlpha);
        }

        Image image = canvas.ToImage(container.AlphaItem is not null);
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
}
