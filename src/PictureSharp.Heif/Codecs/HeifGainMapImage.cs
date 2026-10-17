using System.Drawing;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// A gain map item detached from the input: decoded on demand (ToHdr or the first geometric edit), possibly on another
// thread after the load returned. A monochrome map becomes gray RGB, as heif-dec writes it.
internal sealed class HeifGainMapImage(HeifImageNode node, Size size) : GainMapImage(size)
{
    public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
        new HeifRenderer(cancellationToken).Render(node, alpha: false).ToImage(alpha: false);
}
