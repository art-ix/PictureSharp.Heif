using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// Image.ReadMetadata without decoding HEVC: the blocks Decode sets, read from the container. Rewriting (EditMetadata)
// is not supported yet, so Rewrite keeps the interface's NotSupportedException.
internal sealed class HeifMetadataCodec : IImageFormatMetadataCodec
{
    public static HeifMetadataCodec Instance { get; } = new();

    public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        HeifContainer container = HeifContainer.Open(data, context.Options, context);
        var metadata = new ImageMetadata();
        HeifMetadataReader.Apply(container, metadata, context.Options, colourOnly: false);
        return metadata;
    }
}
