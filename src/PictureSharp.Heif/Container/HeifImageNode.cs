namespace PictureSharp.Formats.Heif.Container;

// An image item prepared for rendering, independent of the container that described it: its transforms in association
// order and its colr nclx. A detached node owns copies of every byte it refers to, so it outlives the input buffer (a
// gain map is decoded on demand, after the load returned).
internal abstract class HeifImageNode(IReadOnlyList<HeifTransform> transforms, HeifNclx? nclx)
{
    public IReadOnlyList<HeifTransform> Transforms { get; } = transforms;

    public HeifNclx? Nclx { get; } = nclx;
}

// hvc1: the HEVC NAL units and the ispe size the decoded picture must match.
internal sealed class HeifCodedNode(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits, (int Width, int Height) storedSize,
    IReadOnlyList<HeifTransform> transforms, HeifNclx? nclx) : HeifImageNode(transforms, nclx)
{
    public IReadOnlyList<ReadOnlyMemory<byte>> NalUnits { get; } = nalUnits;

    public (int Width, int Height) StoredSize { get; } = storedSize;
}

// grid: tiles in raster order.
internal sealed class HeifGridNode(HeifGridLayout layout, IReadOnlyList<HeifImageNode> tiles,
    IReadOnlyList<HeifTransform> transforms, HeifNclx? nclx) : HeifImageNode(transforms, nclx)
{
    public HeifGridLayout Layout { get; } = layout;

    public IReadOnlyList<HeifImageNode> Tiles { get; } = tiles;
}

// iden: its source with additional transforms.
internal sealed class HeifIdentityNode(HeifImageNode source, IReadOnlyList<HeifTransform> transforms, HeifNclx? nclx)
    : HeifImageNode(transforms, nclx)
{
    public HeifImageNode Source { get; } = source;
}
