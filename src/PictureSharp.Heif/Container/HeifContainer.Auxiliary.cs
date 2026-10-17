namespace PictureSharp.Formats.Heif.Container;

// Auxiliary and derived items around the primary image, used to find HDR gain maps.
internal sealed partial class HeifContainer
{
    // The auxC aux_type of an item, or empty when it has none.
    public ReadOnlySpan<byte> AuxiliaryType(HeifItem item)
    {
        if (PropertyOf(item, 0x61757843) is not { } property)
        {
            return default;
        }

        var reader = new HeifReader(property.Data);
        reader.FullBox();
        return reader.String().Span;
    }

    // Items whose reference of the given type points to the target, in item identifier order.
    public IEnumerable<HeifItem> Referencing(uint referenceType, HeifItem target) =>
        items.Values.Where(item => item.References.TryGetValue(referenceType, out List<uint>? targets) && targets.Contains(target.Id))
            .OrderBy(item => item.Id);

    // The items an item derives from (dimg), in reference order.
    public IReadOnlyList<HeifItem> Sources(HeifItem item) =>
        item.References.TryGetValue(0x64696d67, out List<uint>? sources) ? [.. sources.Select(id => items[id])] : [];

    // 1 for a monochrome (4:0:0) HEVC image, 3 otherwise, from the hvcC of its first coded picture.
    public int ChannelCount(HeifItem item)
    {
        for (int depth = 0; !IsCoded(item); depth++)
        {
            Require(depth < MaxReferenceDepth && SourceCount(item) > 0, "Derived image has no source images.");
            item = Source(item, 0);
        }

        // HEVCDecoderConfigurationRecord: chromaFormat in the low two bits of byte 16.
        HeifBox configuration = PropertyOf(item, 0x68766343) ?? throw HeifReader.Error("HEVC image is missing its configuration.");
        return (configuration.Data.Span[16] & 3) == 0 ? 1 : 3;
    }
}
