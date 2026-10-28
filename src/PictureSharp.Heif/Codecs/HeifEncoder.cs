namespace PictureSharp.Formats.Heif.Codecs;

// The plugin's encoder adapter.
internal sealed class HeifEncoder : IImageFormatEncoder<HeifEncoderOptions>
{
    public static HeifEncoder Instance { get; } = new();

    // 16-bit images are coded with 10 bits (HeifEncoderOptions.BitDepth).
    public bool SupportsPixelType(PixelType pixelType) => pixelType is PixelType.Rgba32 or PixelType.Rgba64;

    public void Encode(Image image, Stream output, HeifEncoderOptions options, CancellationToken cancellationToken) =>
        output.Write(HeifImageEncoder.Encode(image, options, cancellationToken));
}
