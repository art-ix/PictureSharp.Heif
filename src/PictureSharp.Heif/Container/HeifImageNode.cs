namespace PictureSharp.Formats.Heif.Container;

// An image item prepared for rendering, independent of the container that described it: the HEVC NAL units of a coded
// item or the sources of a grid or identity item, with the item's transforms in association order and its colr nclx.
// A detached node owns copies of every byte it refers to, so it outlives the input buffer (a gain map is decoded on
// demand, after the load returned).
internal sealed class HeifImageNode
{
    public required HeifImageNodeKind Kind { get; init; }

    public IReadOnlyList<ReadOnlyMemory<byte>> NalUnits { get; init; } = [];

    // Coded items: the ispe size the HEVC picture must match.
    public (int Width, int Height) StoredSize { get; init; }

    // Grid items: ImageGrid rows, columns and output size.
    public (int Rows, int Columns, int Width, int Height) Grid { get; init; }

    public IReadOnlyList<HeifImageNode> Sources { get; init; } = [];

    public IReadOnlyList<HeifBox> Transforms { get; init; } = [];

    public (int Primaries, int Transfer, int Matrix, bool FullRange)? Nclx { get; init; }
}

internal enum HeifImageNodeKind
{
    Coded,
    Grid,
    Identity,
}
