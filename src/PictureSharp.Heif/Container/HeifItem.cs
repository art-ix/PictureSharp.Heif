namespace PictureSharp.Formats.Heif.Container;

internal sealed class HeifItem(uint id)
{
    public uint Id { get; } = id;
    public uint Type { get; set; }
    public bool Defined { get; set; }
    public bool Located { get; set; }
    public bool Associated { get; set; }
    public bool IsXmp { get; set; }
    public ushort ConstructionMethod { get; set; }
    public List<HeifExtent> Extents { get; } = [];
    public List<HeifAssociation> Properties { get; } = [];
    public Dictionary<uint, List<uint>> References { get; } = [];
    public HeifImageDescription? Description { get; set; }
    public int ReferenceDepth { get; set; }
    public byte VisitState { get; set; }
}

internal readonly record struct HeifExtent(ulong Offset, ulong Length);
internal readonly record struct HeifAssociation(int Index, bool Essential);
internal readonly record struct HeifImageDescription(int Width, int Height, int BitsPerChannel);
