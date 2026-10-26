using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

internal sealed class HeifDecoder : IImageFormatDecoder
{
    public static HeifDecoder Instance { get; } = new();

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        HeifContainer container = HeifContainer.Open(data, options);
        return options.LoadGainMap && HeifGainMap.Find(container) is { } gainMap
            ? container.Info with { GainMap = gainMap.Describe() }
            : container.Info;
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) =>
        new HeifImageDecoder(HeifContainer.Open(data, context.Options, context), context).Decode();
}
