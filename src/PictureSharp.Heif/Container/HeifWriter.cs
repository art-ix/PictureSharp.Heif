using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

// One HEVC-coded image (a single item or a grid of tiles sharing one configuration).
internal sealed record HeifCodedImage(int Width, int Height, int TileWidth, int TileHeight, int Columns, int Rows,
    byte[] Configuration, IReadOnlyList<byte[]> TilePayloads, int Channels)
{
    public bool IsGrid => TilePayloads.Count > 1 || TileWidth != Width || TileHeight != Height;
}

// Writes a HEIF still image file (ISO/IEC 23008-12, boxes per ISO/IEC 14496-12): ftyp, meta (hdlr, pitm, iinf, iloc,
// iprp, iref, idat) and mdat. Grids keep their tiles as hidden items, as cameras write them.
internal static class HeifWriter
{
    // Transforms apply to the colour and alpha images alike, in this order.
    public sealed record Content(HeifCodedImage Color, HeifCodedImage? Alpha, byte[]? Icc, HeifNclx Nclx,
        IReadOnlyList<HeifTransform> Transforms, byte[]? Exif, byte[]? Xmp);

    private sealed class Item(uint id, uint type, bool hidden)
    {
        public uint Id { get; } = id;
        public uint Type { get; } = type;
        public bool Hidden { get; } = hidden;
        public byte[] Data { get; set; } = [];
        public bool InItemData { get; set; }
        public List<(int Property, bool Essential)> Properties { get; } = [];
        public string? ContentType { get; set; }
    }

    public static byte[] Write(Content content)
    {
        var items = new List<Item>();
        var properties = new List<byte[]>();
        var references = new List<(uint Type, uint From, List<uint> To)>();
        int Property(byte[] box)
        {
            properties.Add(box);
            return properties.Count;
        }

        uint nextId = 1;
        Item AddImage(HeifCodedImage image, int configuration, IEnumerable<(int, bool)> extra, bool alpha)
        {
            int full = Property(Ispe(image.Width, image.Height));
            int pixi = Property(Pixi(image.Channels));
            if (!image.IsGrid)
            {
                var item = new Item(nextId++, HeifFourCc.Hvc1, hidden: false) { Data = Length(image.TilePayloads[0]) };
                item.Properties.Add((configuration, true));
                item.Properties.Add((full, false));
                item.Properties.Add((pixi, false));
                item.Properties.AddRange(extra);
                items.Add(item);
                return item;
            }

            var grid = new Item(nextId++, HeifFourCc.Grid, hidden: false) { InItemData = true };
            grid.Data = GridDescriptor(image);
            grid.Properties.Add((full, false));
            grid.Properties.Add((pixi, false));
            grid.Properties.AddRange(extra);
            items.Add(grid);
            int tileExtents = Property(Ispe(image.TileWidth, image.TileHeight));
            var tiles = new List<uint>();
            foreach (byte[] payload in image.TilePayloads)
            {
                var tile = new Item(nextId++, HeifFourCc.Hvc1, hidden: true) { Data = Length(payload) };
                tile.Properties.Add((configuration, true));
                tile.Properties.Add((tileExtents, false));
                if (alpha)
                {
                    tile.Properties.Add((pixi, false));
                }

                items.Add(tile);
                tiles.Add(tile.Id);
            }

            references.Add((HeifFourCc.Dimg, grid.Id, tiles));
            return grid;
        }

        IEnumerable<(int, bool)> TransformProperties() =>
            [.. content.Transforms.Select(transform => (Property(Box(transform.Type, transform.Payload())), true))];

        int colorConfiguration = Property(Box(HeifFourCc.HvcC, content.Color.Configuration));
        int colour = Property(content.Icc is { } icc ? Box(HeifFourCc.Colr, [.. "prof"u8, .. icc]) : Nclx(content.Nclx));
        Item primary = AddImage(content.Color, colorConfiguration, [(colour, false), .. TransformProperties()], alpha: false);
        if (content.Alpha is { } alphaImage)
        {
            int alphaConfiguration = Property(Box(HeifFourCc.HvcC, alphaImage.Configuration));
            int auxiliary = Property(FullBox(HeifFourCc.AuxC, 0, 0, [.. "urn:mpeg:hevc:2015:auxid:1"u8, 0]));
            Item alpha = AddImage(alphaImage, alphaConfiguration, [(auxiliary, true), .. TransformProperties()], alpha: true);
            references.Add((HeifFourCc.Auxl, alpha.Id, [primary.Id]));
        }

        if (content.Exif is { } exif)
        {
            var item = new Item(nextId++, HeifFourCc.Exif, hidden: false) { Data = [0, 0, 0, 0, .. exif] };
            items.Add(item);
            references.Add((HeifFourCc.Cdsc, item.Id, [primary.Id]));
        }

        if (content.Xmp is { } xmp)
        {
            var item = new Item(nextId++, HeifFourCc.Mime, hidden: false) { Data = xmp, ContentType = "application/rdf+xml" };
            items.Add(item);
            references.Add((HeifFourCc.Cdsc, item.Id, [primary.Id]));
        }

        if (nextId > 0xffff)
        {
            throw new NotSupportedException("The image needs more HEIF items than a 16-bit item identifier allows.");
        }

        byte[] ftyp = Box(HeifFourCc.Ftyp, [.. "heic"u8, 0, 0, 0, 0, .. "mif1"u8, .. "heic"u8]);
        byte[] meta = Meta(items, properties, references, primary.Id, 0);
        long mdatStart = ftyp.Length + meta.Length + 8;
        meta = Meta(items, properties, references, primary.Id, mdatStart);
        long mdatLength = 8 + items.Where(i => !i.InItemData).Sum(i => (long)i.Data.Length);
        if (mdatStart + mdatLength > uint.MaxValue)
        {
            throw new NotSupportedException("HEIF files above 4 GiB are not supported.");
        }

        var output = new MemoryStream((int)(ftyp.Length + meta.Length + mdatLength));
        output.Write(ftyp);
        output.Write(meta);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)mdatLength);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], HeifFourCc.Mdat);
        output.Write(header);
        foreach (Item item in items.Where(i => !i.InItemData))
        {
            output.Write(item.Data);
        }

        return output.ToArray();
    }

    private static byte[] Meta(List<Item> items, List<byte[]> properties, List<(uint Type, uint From, List<uint> To)> references,
        uint primary, long mdatData)
    {
        var infos = new List<byte>();
        foreach (Item item in items)
        {
            var info = new List<byte>();
            info.AddRange(U16(item.Id));
            info.AddRange(U16(0)); // item_protection_index
            info.AddRange(U32(item.Type));
            info.Add(0); // item_name
            if (item.ContentType is { } contentType)
            {
                info.AddRange(System.Text.Encoding.ASCII.GetBytes(contentType));
                info.Add(0);
            }

            infos.AddRange(FullBox(HeifFourCc.Infe, 2, item.Hidden ? 1u : 0u, [.. info]));
        }

        // iloc version 1: 4-byte offsets and lengths, no base offset; construction method 1 refers into idat.
        var locations = new List<byte> { 0x44, 0x00 };
        locations.AddRange(U16((uint)items.Count));
        long fileOffset = mdatData;
        long dataOffset = 0;
        foreach (Item item in items)
        {
            locations.AddRange(U16(item.Id));
            locations.AddRange(U16(item.InItemData ? 1u : 0u));
            locations.AddRange(U16(0)); // data_reference_index
            locations.AddRange(U16(1)); // extent_count
            locations.AddRange(U32((uint)(item.InItemData ? dataOffset : fileOffset)));
            locations.AddRange(U32((uint)item.Data.Length));
            if (item.InItemData)
            {
                dataOffset += item.Data.Length;
            }
            else
            {
                fileOffset += item.Data.Length;
            }
        }

        var associations = new List<byte>();
        associations.AddRange(U32((uint)items.Count));
        foreach (Item item in items)
        {
            associations.AddRange(U16(item.Id));
            associations.Add((byte)item.Properties.Count);
            foreach ((int property, bool essential) in item.Properties)
            {
                associations.Add((byte)((essential ? 0x80 : 0) | property));
            }
        }

        if (properties.Count > 127)
        {
            throw new NotSupportedException("Too many HEIF item properties.");
        }

        var referenceBoxes = new List<byte>();
        foreach ((uint type, uint from, List<uint> to) in references)
        {
            var reference = new List<byte>();
            reference.AddRange(U16(from));
            reference.AddRange(U16((uint)to.Count));
            foreach (uint id in to)
            {
                reference.AddRange(U16(id));
            }

            referenceBoxes.AddRange(Box(type, [.. reference]));
        }

        var meta = new List<byte>();
        meta.AddRange(FullBox(HeifFourCc.Hdlr, 0, 0, [0, 0, 0, 0, .. "pict"u8, .. new byte[12], 0]));
        meta.AddRange(FullBox(HeifFourCc.Pitm, 0, 0, U16(primary)));
        meta.AddRange(FullBox(HeifFourCc.Iinf, 0, 0, [.. U16((uint)items.Count), .. infos]));
        meta.AddRange(FullBox(HeifFourCc.Iloc, 1, 0, [.. locations]));
        byte[] propertyContainer = Box(HeifFourCc.Ipco, [.. properties.SelectMany(p => p)]);
        meta.AddRange(Box(HeifFourCc.Iprp, [.. propertyContainer, .. FullBox(HeifFourCc.Ipma, 0, 0, [.. associations])]));
        if (referenceBoxes.Count > 0)
        {
            meta.AddRange(FullBox(HeifFourCc.Iref, 0, 0, [.. referenceBoxes]));
        }

        byte[] itemData = [.. items.Where(i => i.InItemData).SelectMany(i => i.Data)];
        if (itemData.Length > 0)
        {
            meta.AddRange(Box(HeifFourCc.Idat, itemData));
        }

        return FullBox(HeifFourCc.Meta, 0, 0, [.. meta]);
    }

    // ImageGrid (6.6.2.3.2) with 16- or 32-bit output dimensions.
    private static byte[] GridDescriptor(HeifCodedImage image)
    {
        bool large = image.Width > 0xffff || image.Height > 0xffff;
        return large
            ? [0, 1, (byte)(image.Rows - 1), (byte)(image.Columns - 1), .. U32((uint)image.Width), .. U32((uint)image.Height)]
            : [0, 0, (byte)(image.Rows - 1), (byte)(image.Columns - 1), .. U16((uint)image.Width), .. U16((uint)image.Height)];
    }

    private static byte[] Length(byte[] nal) => [.. U32((uint)nal.Length), .. nal];

    private static byte[] Ispe(int width, int height) => FullBox(HeifFourCc.Ispe, 0, 0, [.. U32((uint)width), .. U32((uint)height)]);

    private static byte[] Pixi(int channels) => FullBox(HeifFourCc.Pixi, 0, 0, [(byte)channels, .. Enumerable.Repeat((byte)8, channels)]);

    private static byte[] Nclx(HeifNclx nclx) =>
        Box(HeifFourCc.Colr, [.. "nclx"u8, .. U16((uint)nclx.Primaries), .. U16((uint)nclx.Transfer), .. U16((uint)nclx.Matrix),
            (byte)(nclx.FullRange ? 0x80 : 0)]);

    private static byte[] Box(uint type, byte[] payload) => [.. U32((uint)(payload.Length + 8)), .. U32(type), .. payload];

    private static byte[] FullBox(uint type, byte version, uint flags, byte[] payload) =>
        Box(type, [version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags, .. payload]);

    private static byte[] U16(uint value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
