using System.Text;

namespace PictureSharp.Formats.Heif.Container;

internal sealed partial class HeifContainer(ReadOnlyMemory<byte> data, DecoderOptions options, ImageDecodeContext? context,
    CancellationToken cancellationToken = default)
{
    private const int MaxItems = 65536;
    private const int MaxBoxes = 262144;
    private const int MaxEntries = 262144;
    private const int MaxReferenceDepth = 32;
    private readonly Dictionary<uint, HeifItem> items = [];
    private readonly List<HeifBox> properties = [];
    private readonly HashSet<int> unsupportedProperties = [];
    private readonly Dictionary<HeifItem, HeifImageDescription> descriptions = [];
    private ReadOnlyMemory<byte> itemData;
    private bool hasItemData;
    private uint primaryId;
    private int itemInfoCount;
    private int boxCount;
    private int entryCount;
    private long metadataBytes;

    // Parses and validates the container; the result keeps the item graph for decoding.
    public static HeifContainer Open(ReadOnlyMemory<byte> data, DecoderOptions options, ImageDecodeContext? context = null)
    {
        var container = new HeifContainer(data, options, context);
        container.Parse();
        container.Info = container.PrepareImage();
        return container;
    }

    public ImageInfo Info { get; private set; } = null!;

    public static HeifContainer OpenCatalog(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken)
    {
        var container = new HeifContainer(data, options, null, cancellationToken);
        container.Parse();
        return container;
    }

    public uint PrimaryItemId => primaryId;

    private void CheckCancellation()
    {
        cancellationToken.ThrowIfCancellationRequested();
        context?.ThrowIfCancellationRequested();
    }

    private void Parse()
    {
        CheckCancellation();
        Require(data.Length <= options.MaxInputBytes, "Input exceeds MaxInputBytes.");
        Require(HeifSignature.IsHevcImage(data.Span), "Unrecognized HEIF signature.");
        var reader = new HeifReader(data);
        HeifBox ftyp = NextBox(ref reader);
        Require(ftyp.Type == HeifFourCc.Ftyp, "Missing ftyp.");
        bool meta = false;
        while (reader.Remaining > 0)
        {
            HeifBox box = NextBox(ref reader);
            switch (box.Type)
            {
                case HeifFourCc.Meta:
                    Require(!meta, "Duplicate meta box.");
                    meta = true;
                    ParseMeta(box.Data);
                    break;
                case HeifFourCc.Moov:
                    throw new NotSupportedException("HEIF image sequences are not supported.");
                case HeifFourCc.Ftyp:
                    throw HeifReader.Error("Duplicate ftyp box.");
            }
        }

        Require(meta, "Missing meta box.");
        Require(items.TryGetValue(primaryId, out HeifItem? found) && found.Defined, "Missing primary image item.");
        ValidateItems();
    }

    private ImageInfo PrepareImage()
    {
        HeifItem image = SelectedItem(items[primaryId]);
        HeifImageDescription description = Describe(image, 0);
        ImageItem = image;
        ChargeImageMetadata(image, []);
        var charged = new HashSet<uint>();
        long pixels = 0;
        ChargePixels(image, charged, ref pixels);
        foreach (HeifItem item in items.Values.Where(item => item.RefersTo(HeifFourCc.Auxl, image) && IsAlpha(item)))
        {
            Require(AlphaItem is null, "Multiple alpha images for the image item.");
            AlphaItem = item;
            HeifImageDescription alphaDescription = Describe(item, 0);
            Require(alphaDescription.Width == description.Width && alphaDescription.Height == description.Height,
                "Alpha image dimensions do not match the image item.");
            ChargePixels(item, charged, ref pixels);
            ChargeImageMetadata(item, []);
        }

        return new ImageInfo
        {
            Format = HeifFormat.Descriptor,
            Width = description.Width,
            Height = description.Height,
            BitsPerChannel = description.BitsPerChannel,
            HasAlpha = AlphaItem is not null,
            FrameCount = 1,
            // Every iinf entry (tiles, alpha, thumbnails, metadata), independent of the single-frame FrameCount.
            ContainerItemCount = itemInfoCount,
            // Container transforms are already reflected in the reported dimensions.
            Orientation = (ExifOrientation)1
        };
    }

    // The primary item, or the image item HeifDecoderOptions.ItemId names.
    private HeifItem SelectedItem(HeifItem primary)
    {
        if (options.GetFormatOptions<HeifDecoderOptions>()?.ItemId is not { } id || id == primary.Id)
        {
            return primary;
        }

        Require(items.TryGetValue(id, out HeifItem? item) && item.Defined, $"HEIF item {id} is not in the file.");
        Require(!item!.IsExif && item.Type is not (HeifFourCc.Mime or HeifFourCc.Uri), $"HEIF item {id} is not an image.");
        return item;
    }

    private void ParseMeta(ReadOnlyMemory<byte> bytes)
    {
        var reader = new HeifReader(bytes);
        reader.FullBox();
        var seen = new HashSet<uint>();
        while (reader.Remaining > 0)
        {
            HeifBox box = NextBox(ref reader);
            switch (box.Type)
            {
                case HeifFourCc.Hdlr:
                case HeifFourCc.Pitm:
                case HeifFourCc.Iinf:
                case HeifFourCc.Iloc:
                case HeifFourCc.Iref:
                case HeifFourCc.Iprp:
                case HeifFourCc.Idat:
                    Require(seen.Add(box.Type), "Duplicate metadata box.");
                    break;
            }

            switch (box.Type)
            {
                case HeifFourCc.Hdlr:
                    var handler = new HeifReader(box.Data);
                    handler.FullBox();
                    handler.Read(4);
                    Require(handler.UInt32() == HeifFourCc.Pict, "HEIF requires a pict handler.");
                    handler.Read(12);
                    handler.String();
                    handler.End();
                    break;
                case HeifFourCc.Pitm:
                    var pitm = new HeifReader(box.Data);
                    byte version = Version(ref pitm, 1);
                    primaryId = version == 0 ? pitm.UInt16() : pitm.UInt32();
                    Require(primaryId != 0, "Invalid primary item identifier.");
                    pitm.End();
                    break;
                case HeifFourCc.Iinf: ParseItemInfo(box.Data); break;
                case HeifFourCc.Iloc: ParseLocations(box.Data); break;
                case HeifFourCc.Iref: ParseReferences(box.Data); break;
                case HeifFourCc.Iprp: ParseProperties(box.Data); break;
                case HeifFourCc.Idat: itemData = box.Data; hasItemData = true; break;
            }
        }

        Require(seen.Contains(HeifFourCc.Hdlr) && seen.Contains(HeifFourCc.Pitm) && seen.Contains(HeifFourCc.Iinf)
            && seen.Contains(HeifFourCc.Iloc) && seen.Contains(HeifFourCc.Iprp), "Missing required metadata box.");
    }

    private void ParseItemInfo(ReadOnlyMemory<byte> bytes)
    {
        var reader = new HeifReader(bytes);
        byte version = Version(ref reader, 1);
        uint count = version == 0 ? reader.UInt16() : reader.UInt32();
        Require(count <= MaxItems, "Too many HEIF items.");
        itemInfoCount = (int)count;
        for (uint i = 0; i < count; i++)
        {
            HeifBox box = NextBox(ref reader);
            Require(box.Type == HeifFourCc.Infe, "Expected infe box.");
            var info = new HeifReader(box.Data);
            byte itemVersion = info.FullBox(out uint flags);
            Require((flags & ~1u) == 0, "Invalid infe flags.");
            if (itemVersion is not (2 or 3))
            {
                throw new NotSupportedException("Only HEIF item info versions 2 and 3 are supported.");
            }

            HeifItem item = Item(itemVersion == 2 ? info.UInt16() : info.UInt32());
            Require(!item.Defined, "Duplicate item definition.");
            item.Defined = true;
            item.ProtectionIndex = info.UInt16();
            item.IsHidden = (flags & 1) != 0;

            item.Type = info.UInt32();
            item.Name = Encoding.UTF8.GetString(info.String().Span);
            if (item.Type == HeifFourCc.Mime)
            {
                item.IsXmp = info.String().Span.SequenceEqual("application/rdf+xml"u8);
                if (info.Remaining > 0)
                {
                    item.ContentEncoding = ContentEncoding(info.String().Span);
                }
            }
            else if (item.Type == HeifFourCc.Uri)
            {
                info.String();
            }

            info.End();
        }

        reader.End();
    }

    private void ParseLocations(ReadOnlyMemory<byte> bytes)
    {
        var reader = new HeifReader(bytes);
        byte version = Version(ref reader, 2);
        byte sizes = reader.Byte();
        int offsetSize = sizes >> 4, lengthSize = sizes & 15;
        sizes = reader.Byte();
        int baseSize = sizes >> 4, indexSize = sizes & 15;
        Require(IsFieldSize(offsetSize) && IsFieldSize(lengthSize) && IsFieldSize(baseSize)
            && IsFieldSize(indexSize) && (version != 0 || indexSize == 0), "Invalid iloc field sizes.");
        uint count = version < 2 ? reader.UInt16() : reader.UInt32();
        Require(count <= MaxItems, "Too many item locations.");
        for (uint i = 0; i < count; i++)
        {
            HeifItem item = Item(version < 2 ? reader.UInt16() : reader.UInt32());
            Require(!item.Located, "Duplicate item location.");
            item.Located = true;
            ushort method = version > 0 ? reader.UInt16() : (ushort)0;
            Require((method & 0xfff0) == 0, "Invalid construction method bits.");
            item.DataReferenceIndex = reader.UInt16();

            item.ConstructionMethod = method;
            ulong baseOffset = reader.Unsigned(baseSize);
            int extents = reader.UInt16();
            Entries(extents);
            Require(extents > 0, "Item has no extents.");
            for (int j = 0; j < extents; j++)
            {
                if (version > 0)
                {
                    reader.Unsigned(indexSize);
                }

                ulong offset = reader.Unsigned(offsetSize);
                ulong length = reader.Unsigned(lengthSize);
                Require(offset <= ulong.MaxValue - baseOffset, "Item offset overflow.");
                item.Extents.Add(new(baseOffset + offset, length));
            }
        }

        reader.End();
    }

    private void ParseReferences(ReadOnlyMemory<byte> bytes)
    {
        var reader = new HeifReader(bytes);
        byte version = Version(ref reader, 1);
        while (reader.Remaining > 0)
        {
            HeifBox box = NextBox(ref reader);
            var reference = new HeifReader(box.Data);
            HeifItem item = Item(version == 0 ? reference.UInt16() : reference.UInt32());
            int count = reference.UInt16();
            Entries(count);
            Require(!item.References.ContainsKey(box.Type), "Duplicate item reference type.");
            var targets = new List<uint>();
            item.References.Add(box.Type, targets);
            for (int i = 0; i < count; i++)
            {
                uint id = version == 0 ? reference.UInt16() : reference.UInt32();
                Item(id);
                targets.Add(id);
            }

            reference.End();
        }
    }

    private HeifItem Item(uint id)
    {
        Require(id != 0, "Invalid item identifier.");
        if (!items.TryGetValue(id, out HeifItem? item))
        {
            Require(items.Count < MaxItems, "Too many HEIF items.");
            item = new(id);
            items.Add(id, item);
        }

        return item;
    }

    private HeifBox NextBox(ref HeifReader reader)
    {
        CheckCancellation();
        Require(++boxCount <= MaxBoxes, "Too many container boxes.");
        return reader.Box();
    }

    private void Entries(int count)
    {
        Require(count <= MaxEntries - entryCount, "Too many container entries.");
        entryCount += count;
    }

    // HTTP content codings are case-insensitive tokens; only deflate is decoded, other XMP blocks are skipped.
    private static HeifContentEncoding ContentEncoding(ReadOnlySpan<byte> name) =>
        name.IsEmpty ? HeifContentEncoding.None
        : Ascii.EqualsIgnoreCase(name, "deflate"u8) ? HeifContentEncoding.Deflate
        : HeifContentEncoding.Unknown;

    private static bool IsFieldSize(int size) => size is 0 or 4 or 8;

    private static byte Version(ref HeifReader reader, int max)
    {
        byte version = reader.FullBox(out uint flags);
        Require(flags == 0, "Invalid full box flags.");
        if (version > max)
        {
            throw new NotSupportedException("Unsupported HEIF box version.");
        }

        return version;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw HeifReader.Error(message);
        }
    }
}
