using System.Drawing;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// A gain map item detached from the input: decoded on demand (ToHdr or the first geometric edit), possibly on another
// thread after the load returned. A monochrome map becomes gray RGB, as heif-dec writes it. Stored keeps the coded
// item for the encoder to copy, when it has a layout the writer reproduces.
// A map coded with more than 8 bits decodes to Rgba64 with PreserveBitDepth (pixelType).
internal sealed class HeifGainMapImage(HeifImageNode node, HeifCodedImage? stored, Size size, PixelType pixelType = PixelType.Rgba32)
    : GainMapImage(size, pixelType)
{
    public HeifImageNode Node => node;

    public HeifCodedImage? Stored => stored;

    public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var renderer = new HeifRenderer(maxDegreeOfParallelism, cancellationToken);
        return PixelType == PixelType.Rgba64
            ? renderer.Render<Rgba64>(node, alpha: false).ToImage(alpha: false)
            : renderer.Render<Rgba32>(node, alpha: false).ToImage(alpha: false);
    }
}
