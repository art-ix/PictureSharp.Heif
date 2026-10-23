using System.Drawing;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// A gain map item detached from the input: decoded on demand (ToHdr or the first geometric edit), possibly on another
// thread after the load returned. A monochrome map becomes gray RGB, as heif-dec writes it. Stored keeps the coded
// item for the encoder to copy, when it has a layout the writer reproduces.
internal sealed class HeifGainMapImage(HeifImageNode node, HeifCodedImage? stored, Size size) : GainMapImage(size)
{
    public HeifImageNode Node => node;

    public HeifCodedImage? Stored => stored;

    public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
        new HeifRenderer(maxDegreeOfParallelism, cancellationToken).Render(node, alpha: false).ToImage(alpha: false);
}
