namespace PictureSharp.Formats.Heif.Container;

// Auxiliary and derived items around the primary image, used to find HDR gain maps.
internal sealed partial class HeifContainer
{
    // The auxC aux_type of an item, or empty when it has none.
    public ReadOnlySpan<byte> AuxiliaryType(HeifItem item)
    {
        if (PropertyOf(item, HeifFourCc.AuxC) is not { } property)
        {
            return default;
        }

        var reader = new HeifReader(property.Data);
        reader.FullBox();
        return reader.String().Span;
    }

    // Items whose reference of the given type points to the target, in item identifier order.
    public IEnumerable<HeifItem> Referencing(uint referenceType, HeifItem target) =>
        items.Values.Where(item => item.RefersTo(referenceType, target)).OrderBy(item => item.Id);

    // The items an item derives from (dimg), in reference order.
    public IReadOnlyList<HeifItem> Sources(HeifItem item) => [.. item.Targets(HeifFourCc.Dimg).Select(id => items[id])];

    // 1 for a monochrome (4:0:0) HEVC image, 3 otherwise, from the hvcC of its first coded picture.
    public int ChannelCount(HeifItem item)
    {
        for (int depth = 0; item.Type != HeifFourCc.Hvc1; depth++)
        {
            Require(depth < MaxReferenceDepth && Sources(item) is [_, ..], "Derived image has no source images.");
            item = Sources(item)[0];
        }

        // HEVCDecoderConfigurationRecord: chromaFormat in the low two bits of byte 16.
        HeifBox configuration = PropertyOf(item, HeifFourCc.HvcC) ?? throw HeifReader.Error("HEVC image is missing its configuration.");
        return (configuration.Data.Span[16] & 3) == 0 ? 1 : 3;
    }
}
