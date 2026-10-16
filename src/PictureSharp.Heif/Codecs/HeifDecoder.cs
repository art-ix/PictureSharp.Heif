using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

internal sealed class HeifDecoder : IImageFormatDecoder
{
    public static HeifDecoder Instance { get; } = new();

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => HeifContainer.Identify(data, options);

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) =>
        new HeifImageDecoder(HeifContainer.Open(data, context.Options, context), context).Decode();
}
