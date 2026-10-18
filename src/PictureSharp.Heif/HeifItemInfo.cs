using System.Drawing;

namespace PictureSharp.Formats.Heif;

/// <summary>An immutable container item description of one <see cref="HeifDocument"/>.</summary>
public sealed class HeifItemInfo
{
    private readonly uint[] thumbnailIds, auxiliaryIds, metadataIds;

    internal HeifItemInfo(uint id, string type, string name, bool hidden, bool image, bool supported, Size? size, int? bits,
        HeifAuxiliaryInfo? auxiliary, IEnumerable<HeifItemReference> references,
        IEnumerable<uint> thumbnails, IEnumerable<uint> auxiliaries, IEnumerable<uint> metadata)
    {
        Id = id;
        Type = type;
        Name = name;
        IsHidden = hidden;
        IsImage = image;
        IsSupported = supported;
        DisplaySize = size;
        BitsPerChannel = bits;
        Auxiliary = auxiliary;
        References = Array.AsReadOnly(references.ToArray());
        thumbnailIds = thumbnails.ToArray();
        auxiliaryIds = auxiliaries.ToArray();
        metadataIds = metadata.ToArray();
    }

    /// <summary>The original item identifier, as <see cref="HeifDecoderOptions.ItemId"/> and <c>heif-info</c> use it.</summary>
    public uint Id { get; }
    /// <summary>The original four-character item type; <see cref="HeifItemTypes"/> names the common ones.</summary>
    public string Type { get; }
    /// <summary>The UTF-8 item name declared in infe.</summary>
    public string Name { get; }
    /// <summary>Whether infe marks the item as hidden.</summary>
    public bool IsHidden { get; }
    /// <summary>Whether the item has a recognized image type, including image types not supported by the decoder.</summary>
    public bool IsImage { get; }
    /// <summary>
    /// Whether the decoder supports the item's type, storage and properties as the container declares them. True does
    /// not guarantee decoding: the HEVC bitstream may use unsupported features or be corrupt. False for non-images.
    /// </summary>
    public bool IsSupported { get; }
    /// <summary>Declared size after container transforms, or null when it cannot be determined without unsupported processing.</summary>
    public Size? DisplaySize { get; }
    /// <summary>Declared sample depth, or null when the container does not provide it.</summary>
    public int? BitsPerChannel { get; }
    /// <summary>The auxiliary role, type and subtype, or null when no auxiliary role is declared.</summary>
    public HeifAuxiliaryInfo? Auxiliary { get; }
    /// <summary>All outgoing references with their original direction and ordered target identifiers.</summary>
    public IReadOnlyList<HeifItemReference> References { get; }
    /// <summary>The ordered sources of this derived image (outgoing dimg), such as grid tiles.</summary>
    public IReadOnlyList<HeifItemInfo> SourceImages { get; private set; } = [];
    /// <summary>Items whose thmb reference points to this item, in identifier order.</summary>
    public IReadOnlyList<HeifItemInfo> Thumbnails { get; private set; } = [];
    /// <summary>Items whose auxl reference points to this item, plus ISO tmap gain maps, in identifier order.</summary>
    public IReadOnlyList<HeifItemInfo> AuxiliaryImages { get; private set; } = [];
    /// <summary>Items whose cdsc reference points to this item (EXIF, XMP), in identifier order.</summary>
    public IReadOnlyList<HeifItemInfo> MetadataItems { get; private set; } = [];

    /// <summary>
    /// The first auxiliary image of this item (in identifier order) whose <see cref="HeifAuxiliaryInfo.Type"/> equals
    /// <paramref name="auxiliaryType"/>, such as <see cref="HeifAppleAuxiliaryTypes.PortraitEffectsMatte"/>; null when none.
    /// </summary>
    public HeifItemInfo? FindAuxiliaryImage(string auxiliaryType)
    {
        ArgumentNullException.ThrowIfNull(auxiliaryType);
        return AuxiliaryImages.FirstOrDefault(item => item.Auxiliary?.Type == auxiliaryType);
    }

    /// <summary>
    /// The first depth or disparity auxiliary image of this item (<see cref="HeifAuxiliaryKind.Depth"/> or
    /// <see cref="HeifAuxiliaryKind.Disparity"/>), as <see cref="HeifDocument.LoadDepth"/> reads it; null when none.
    /// </summary>
    public HeifItemInfo? FindDepthImage() =>
        AuxiliaryImages.FirstOrDefault(item => item.Auxiliary?.Kind is HeifAuxiliaryKind.Depth or HeifAuxiliaryKind.Disparity);

    // Resolves relationships once every item of the document exists; references were validated on opening.
    internal void Link(IReadOnlyDictionary<uint, HeifItemInfo> items)
    {
        IReadOnlyList<HeifItemInfo> Resolve(IEnumerable<uint> ids) => Array.AsReadOnly(ids.Select(id => items[id]).ToArray());
        SourceImages = Resolve(References.FirstOrDefault(reference => reference.Type == HeifReferenceTypes.DerivedImage)?.TargetItemIds ?? []);
        Thumbnails = Resolve(thumbnailIds);
        AuxiliaryImages = Resolve(auxiliaryIds);
        MetadataItems = Resolve(metadataIds);
    }
}
