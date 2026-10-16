using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

internal sealed class HeifDecoder : IImageFormatDecoder
{
    public static HeifDecoder Instance { get; } = new();

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => HeifContainer.Identify(data, options);

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        HeifContainer.Identify(data, context.Options, context);
        throw new NotSupportedException("HEVC pixel decoding is planned for PictureSharp.Heif 0.2.0. Use Identify to read image information.");
    }
}
