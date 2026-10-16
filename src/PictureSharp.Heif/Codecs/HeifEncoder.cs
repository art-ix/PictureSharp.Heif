namespace PictureSharp.Formats.Heif.Codecs;

// The plugin's encoder adapter.
internal sealed class HeifEncoder : IImageFormatEncoder<HeifEncoderOptions>
{
    public static HeifEncoder Instance { get; } = new();

    public void Encode(Image image, Stream output, HeifEncoderOptions options, CancellationToken cancellationToken) =>
        output.Write(HeifImageEncoder.Encode(image, options, cancellationToken));
}
