using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

// Auxiliary and derived items around the primary image, used to find HDR gain maps.
internal sealed partial class HeifContainer
{
    // The auxC aux_type of an item, or empty when it has none.
    public ReadOnlySpan<byte> AuxiliaryType(HeifItem item)
    {
        if (PropertyOf(item, HeifFourCc.AuxC, supportedOnly: true) is not { } property)
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

    // A copy of an image item's coded data for writing it again: an hvc1 item, or a grid whose hvc1 tiles share one
    // configuration and size and have no properties of their own beyond them and an ICC profile (Photos exports tag
    // every map tile with one; it does not change how samples decode). Null for anything else (iden, tiles with
    // transforms or nclx, mixed configurations), which is decoded and encoded again instead.
    public HeifCodedImage? StoredImage(HeifItem item)
    {
        HeifItem[] tiles = item.Type switch
        {
            HeifFourCc.Hvc1 => [item],
            HeifFourCc.Grid => [.. Sources(item)],
            _ => [],
        };
        static bool Copied(HeifBox property) => property.Type is HeifFourCc.HvcC or HeifFourCc.Ispe or HeifFourCc.Pixi
            || (property.Type == HeifFourCc.Colr && BinaryPrimitives.ReadUInt32BigEndian(property.Data.Span) is HeifFourCc.Prof or HeifFourCc.RIcc);
        bool OwnProperties(HeifItem tile) => tile != item && !tile.Properties.Select(Property).All(Copied);
        if (tiles.Length == 0 || tiles.Any(tile => tile.Type != HeifFourCc.Hvc1 || OwnProperties(tile)))
        {
            return null;
        }

        byte[] configuration = PropertyOf(tiles[0], HeifFourCc.HvcC)!.Value.Data.ToArray();
        (int tileWidth, int tileHeight) = StoredSize(tiles[0]);
        if (tiles.Any(tile => !PropertyOf(tile, HeifFourCc.HvcC)!.Value.Data.Span.SequenceEqual(configuration) || StoredSize(tile) != (tileWidth, tileHeight)))
        {
            return null;
        }

        CheckCancellation();
        (int width, int height, int columns, int rows) = item.Type == HeifFourCc.Grid && GridLayout(item) is var layout
            ? (layout.Width, layout.Height, layout.Columns, layout.Rows)
            : (tileWidth, tileHeight, 1, 1);
        // HEVCDecoderConfigurationRecord: chromaFormat in byte 16, bitDepthLumaMinus8 in byte 17.
        return new HeifCodedImage(width, height, tileWidth, tileHeight, columns, rows, configuration,
            [.. tiles.Select(tile => ItemData(tile).ToArray())], (configuration[16] & 3) == 0 ? 1 : 3)
        {
            BitDepth = (configuration[17] & 7) + 8,
        };
    }
}
