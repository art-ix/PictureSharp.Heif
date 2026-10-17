using System.Drawing;
using System.Runtime.InteropServices;

namespace PictureSharp.Formats.Heif.Tests;

// Expected HDR results: a test format whose decoder returns a copy of a given base image (pixels and colour description)
// and attaches a given map and metadata. ToHdr through it is what a HEIC file must produce when its map decodes like
// heif-dec and its metadata parses like the given bytes; the reconstruction itself is PictureSharp's.
internal sealed class ReferenceGainMapFormat(Image baseImage, GainMapMetadata metadata, Image map) : IImageFormatModule, IImageFormatDecoder
{
    private static readonly ImageFormat Descriptor =
        ImageFormat.Create("HEIF-GAIN-MAP-REFERENCE", "image/x-heif-gain-map-reference", ".hgmref");

    public static ReadOnlySpan<byte> Signature => "HGMREF"u8;

    ImageFormat IImageFormatModule.Format => Descriptor;

    public int HeaderSize => Signature.Length;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith(Signature);

    public IImageFormatDecoder Decoder => this;

    public IImageFormatEncoder? Encoder => null;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        new() { Format = Descriptor, Width = baseImage.Width, Height = baseImage.Height };

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        var copy = new Image(baseImage.Width, baseImage.Height, new Rgba32(0, 0, 0, 255));
        baseImage.CopyPixelDataTo(copy.GetPixelSpan());
        copy.Metadata.IccProfile = baseImage.Metadata.IccProfile;
        copy.Metadata.Chromaticities = baseImage.Metadata.Chromaticities;
        copy.Metadata.Gamma = baseImage.Metadata.Gamma;
        context.AttachGainMap(metadata, new MapImage(map));
        return copy;
    }

    // ToHdr of the reference for the same options.
    public HdrImage ToHdr(DecoderOptions? options = null)
    {
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(this).Build();
        using Image image = Image.Load(Signature, (options ?? new DecoderOptions()) with { Configuration = configuration, LoadGainMap = true });
        return image.ToHdr();
    }

    public static bool BitwiseEqual(HdrImage actual, HdrImage expected) =>
        actual.Width == expected.Width && actual.Height == expected.Height && actual.ColorSpace == expected.ColorSpace
        && actual.Headroom == expected.Headroom
        && MemoryMarshal.AsBytes(actual.GetPixelSpan()).SequenceEqual(MemoryMarshal.AsBytes(expected.GetPixelSpan()));

    private sealed class MapImage(Image map) : GainMapImage(new Size(map.Width, map.Height))
    {
        public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken)
        {
            var copy = new Image(map.Width, map.Height, new Rgba32(0, 0, 0, 255));
            map.CopyPixelDataTo(copy.GetPixelSpan());
            return copy;
        }
    }
}
