namespace PictureSharp.Formats.Heif.Container;

// An item as the meta box declares it: infe (Defined), iloc (Located) and ipma (Associated) entries and its iref
// references by type.
internal sealed class HeifItem(uint id)
{
    public uint Id { get; } = id;
    public uint Type { get; set; }
    public bool Defined { get; set; }
    public bool Located { get; set; }
    public bool Associated { get; set; }
    public bool IsXmp { get; set; }
    public HeifContentEncoding ContentEncoding { get; set; }
    public ushort ConstructionMethod { get; set; }
    public List<HeifExtent> Extents { get; } = [];
    public List<HeifAssociation> Properties { get; } = [];
    public Dictionary<uint, List<uint>> References { get; } = [];

    public bool IsExif => Type == HeifFourCc.Exif;

    // The item identifiers this item refers to with the given reference type, in reference order.
    public IReadOnlyList<uint> Targets(uint referenceType) =>
        References.TryGetValue(referenceType, out List<uint>? targets) ? targets : [];

    public bool RefersTo(uint referenceType, HeifItem target) => Targets(referenceType).Contains(target.Id);
}

// content_encoding of a mime item (ISO/IEC 23008-12 A.2.2), HTTP content codings.
internal enum HeifContentEncoding
{
    None,
    Deflate,
    Unknown
}

internal readonly record struct HeifExtent(ulong Offset, ulong Length);
internal readonly record struct HeifAssociation(int Index, bool Essential);
internal readonly record struct HeifImageDescription(int Width, int Height, int BitsPerChannel);
