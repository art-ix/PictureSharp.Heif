namespace PictureSharp.Formats.Heif;

/// <summary>An outgoing item reference, with its original FourCC and ordered target identifiers.</summary>
public sealed class HeifItemReference
{
    internal HeifItemReference(string type, IEnumerable<uint> targetItemIds)
    {
        Type = type;
        TargetItemIds = Array.AsReadOnly(targetItemIds.ToArray());
    }

    /// <summary>The original four-character reference type, e.g. dimg, auxl, thmb or cdsc.</summary>
    public string Type { get; }

    /// <summary>The target identifiers in the order recorded in iref.</summary>
    public IReadOnlyList<uint> TargetItemIds { get; }
}
