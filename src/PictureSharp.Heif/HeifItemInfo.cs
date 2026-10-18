using System.Drawing;

namespace PictureSharp.Formats.Heif;

/// <summary>An immutable container item description. Cataloguing does not establish that its pixels can be decoded.</summary>
public sealed class HeifItemInfo
{
    internal HeifItemInfo(uint id, string type, string name, bool hidden, bool image, Size? size, int? bits,
        HeifAuxiliaryInfo? auxiliary, IEnumerable<HeifItemReference> references,
        IEnumerable<uint> thumbnails, IEnumerable<uint> auxiliaries, IEnumerable<uint> metadata)
    {
        Id = id;
        Type = type;
        Name = name;
        IsHidden = hidden;
        IsImage = image;
        DisplaySize = size;
        BitsPerChannel = bits;
        Auxiliary = auxiliary;
        References = Array.AsReadOnly(references.ToArray());
        ThumbnailItemIds = Array.AsReadOnly(thumbnails.ToArray());
        AuxiliaryItemIds = Array.AsReadOnly(auxiliaries.ToArray());
        MetadataItemIds = Array.AsReadOnly(metadata.ToArray());
        SourceItemIds = References.FirstOrDefault(reference => reference.Type == "dimg")?.TargetItemIds ?? Array.AsReadOnly(Array.Empty<uint>());
    }

    /// <summary>The original item identifier.</summary>
    public uint Id { get; }
    /// <summary>The original four-character item type.</summary>
    public string Type { get; }
    /// <summary>The UTF-8 item name declared in infe.</summary>
    public string Name { get; }
    /// <summary>Whether infe marks the item as hidden.</summary>
    public bool IsHidden { get; }
    /// <summary>Whether the item has a recognized image type, including image types not supported by the decoder.</summary>
    public bool IsImage { get; }
    /// <summary>Declared size after container transforms, or null when it cannot be determined without unsupported processing.</summary>
    public Size? DisplaySize { get; }
    /// <summary>Declared sample depth, or null when the container does not provide it.</summary>
    public int? BitsPerChannel { get; }
    /// <summary>The auxiliary role, type and subtype, or null when no auxiliary role is declared.</summary>
    public HeifAuxiliaryInfo? Auxiliary { get; }
    /// <summary>All outgoing references with their original direction and ordered targets.</summary>
    public IReadOnlyList<HeifItemReference> References { get; }
    /// <summary>Items whose thmb reference points to this item, in identifier order.</summary>
    public IReadOnlyList<uint> ThumbnailItemIds { get; }
    /// <summary>Items whose auxl reference points to this item, plus ISO tmap gain maps, in identifier order.</summary>
    public IReadOnlyList<uint> AuxiliaryItemIds { get; }
    /// <summary>The ordered sources of this derived image (dimg).</summary>
    public IReadOnlyList<uint> SourceItemIds { get; }
    /// <summary>Items whose cdsc reference points to this item, in identifier order.</summary>
    public IReadOnlyList<uint> MetadataItemIds { get; }
}
