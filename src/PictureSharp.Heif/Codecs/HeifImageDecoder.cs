using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// Decodes the primary image of a validated container: the rendered image, auxiliary alpha, descriptive metadata and,
// with DecoderOptions.LoadGainMap, the HDR gain map.
internal sealed class HeifImageDecoder(HeifContainer container, ImageDecodeContext context)
{
    public Image Decode()
    {
        ImageInfo info = container.Info;
        context.ReservePixels(info.Width, info.Height, 1);
        var renderer = new HeifRenderer(context.CancellationToken);
        HeifImageNode primary = container.Node(container.PrimaryItem, detach: false);
        HeifCanvas canvas = renderer.Render(primary, alpha: false);
        if (container.AlphaItem is { } alphaItem)
        {
            HeifCanvas alpha = renderer.Render(container.Node(alphaItem, detach: false), alpha: true);
            if (alpha.Width != canvas.Width || alpha.Height != canvas.Height)
            {
                throw HeifReader.Error("Alpha image dimensions do not match the primary image.");
            }

            Span<Rgba32> pixels = canvas.Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].A = alpha.Pixels[i].R;
            }

            if (container.PremultipliedAlpha)
            {
                Unpremultiply(pixels);
            }
        }

        Image image = canvas.ToImage(container.AlphaItem is not null);
        try
        {
            HeifGainMap? gainMap = context.Options.LoadGainMap ? HeifGainMap.Find(container) : null;
            // The library takes the base image's colour for HDR reconstruction from its metadata, also with IgnoreMetadata.
            if (!context.Options.IgnoreMetadata || gainMap is not null)
            {
                HeifMetadataReader.Apply(container, image.Metadata, context.Options, colourOnly: context.Options.IgnoreMetadata, primary);
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

    // Same rounding as PictureSharp's premultiplied-to-straight conversion.
    private static void Unpremultiply(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            if (p.A is not (0 or 255))
            {
                pixels[i] = new Rgba32((byte)Math.Min(255, 255 * p.R / p.A), (byte)Math.Min(255, 255 * p.G / p.A),
                    (byte)Math.Min(255, 255 * p.B / p.A), p.A);
            }
        }
    }
}
