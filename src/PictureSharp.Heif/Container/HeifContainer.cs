namespace PictureSharp.Formats.Heif.Container;

internal sealed partial class HeifContainer(ReadOnlyMemory<byte> data, DecoderOptions options, ImageDecodeContext? context)
{
    private const int MaxItems = 65536;
    private const int MaxBoxes = 262144;
    private const int MaxEntries = 262144;
    private const int MaxReferenceDepth = 32;
    private readonly Dictionary<uint, HeifItem> items = [];
    private readonly List<HeifBox> properties = [];
    private ReadOnlyMemory<byte> itemData;
    private bool hasItemData;
    private uint primaryId;
    private int itemInfoCount;
    private int boxCount;
    private int entryCount;
    private long metadataBytes;

    public static ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options, ImageDecodeContext? context = null)
    {
        var container = new HeifContainer(data, options, context);
        return container.Parse();
    }

    private ImageInfo Parse()
    {
        context?.ThrowIfCancellationRequested();
        Require(data.Length <= options.MaxInputBytes, "Input exceeds MaxInputBytes.");
        Require(HeifSignature.IsHevcImage(data.Span), "Unrecognized HEIF signature.");
        var reader = new HeifReader(data);
        HeifBox ftyp = NextBox(ref reader);
        Require(ftyp.Type == 0x66747970, "Missing ftyp.");
        bool meta = false;
        while (reader.Remaining > 0)
        {
            HeifBox box = NextBox(ref reader);
            switch (box.Type)
            {
                case 0x6d657461: // meta
                    Require(!meta, "Duplicate meta box.");
                    meta = true;
                    ParseMeta(box.Data);
                    break;
                case 0x6d6f6f76: // moov
                    throw new NotSupportedException("HEIF image sequences are not supported.");
                case 0x66747970:
                    throw HeifReader.Error("Duplicate ftyp box.");
            }
        }

        Require(meta, "Missing meta box.");
        Require(items.TryGetValue(primaryId, out HeifItem? primary) && primary.Defined, "Missing primary image item.");
        ValidateItems();
        HeifImageDescription description = Describe(primary!, 0);
        bool alpha = false;
        var required = new HashSet<uint>();
        long pixels = 0;
        ChargePixels(primary!, required, ref pixels);
        foreach (HeifItem item in items.Values)
        {
            if (item.References.TryGetValue(0x6175786c, out List<uint>? targets) && targets.Contains(primaryId) && IsAlpha(item))
            {
                Require(!alpha, "Multiple alpha images for the primary image.");
                alpha = true;
                HeifImageDescription alphaDescription = Describe(item, 0);
                Require(alphaDescription.Width == description.Width && alphaDescription.Height == description.Height,
                    "Alpha image dimensions do not match the primary image.");
                ChargePixels(item, required, ref pixels);
            }
        }

        return new ImageInfo
        {
            Format = HeifFormat.Descriptor,
            Width = description.Width,
            Height = description.Height,
            BitsPerChannel = description.BitsPerChannel,
            HasAlpha = alpha,
            FrameCount = 1,
            // Every iinf entry (tiles, alpha, thumbnails, metadata), independent of the single-frame FrameCount.
            ContainerItemCount = itemInfoCount,
            // Container transforms are already reflected in the reported dimensions.
            Orientation = (ExifOrientation)1
        };
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
                case 0x68646c72: // hdlr
                case 0x7069746d: // pitm
                case 0x69696e66: // iinf
                case 0x696c6f63: // iloc
                case 0x69726566: // iref
                case 0x69707270: // iprp
                case 0x69646174: // idat
                    Require(seen.Add(box.Type), "Duplicate metadata box.");
                    break;
            }

            switch (box.Type)
            {
                case 0x68646c72:
                    var handler = new HeifReader(box.Data);
                    handler.FullBox();
                    handler.Read(4);
                    Require(handler.UInt32() == 0x70696374, "HEIF requires a pict handler.");
                    handler.Read(12);
                    handler.String();
                    handler.End();
                    break;
                case 0x7069746d:
                    var pitm = new HeifReader(box.Data);
                    byte version = Version(ref pitm, 1);
                    primaryId = version == 0 ? pitm.UInt16() : pitm.UInt32();
                    Require(primaryId != 0, "Invalid primary item identifier.");
                    pitm.End();
                    break;
                case 0x69696e66: ParseItemInfo(box.Data); break;
                case 0x696c6f63: ParseLocations(box.Data); break;
                case 0x69726566: ParseReferences(box.Data); break;
                case 0x69707270: ParseProperties(box.Data); break;
                case 0x69646174: itemData = box.Data; hasItemData = true; break;
            }
        }

        Require(seen.Contains(0x68646c72) && seen.Contains(0x7069746d) && seen.Contains(0x69696e66)
            && seen.Contains(0x696c6f63) && seen.Contains(0x69707270), "Missing required metadata box.");
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
            Require(box.Type == 0x696e6665, "Expected infe box.");
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
            if (info.UInt16() != 0)
            {
                throw new NotSupportedException("Protected HEIF items are not supported.");
            }

            item.Type = info.UInt32();
            info.String();
            if (item.Type == 0x6d696d65) // mime
            {
                item.IsXmp = info.String().Span.SequenceEqual("application/rdf+xml"u8);
                if (info.Remaining > 0)
                {
                    ReadOnlyMemory<byte> encoding = info.String();
                    if (item.IsXmp && encoding.Length > 0 && !options.IgnoreMetadata)
                    {
                        throw new NotSupportedException("Compressed XMP is not supported.");
                    }
                }
            }
            else if (item.Type == 0x75726920)
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
            if (method > 1 || reader.UInt16() != 0)
            {
                throw new NotSupportedException("External or item-relative HEIF data is not supported.");
            }

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
        context?.ThrowIfCancellationRequested();
        Require(++boxCount <= MaxBoxes, "Too many container boxes.");
        return reader.Box();
    }

    private void Entries(int count)
    {
        Require(count <= MaxEntries - entryCount, "Too many container entries.");
        entryCount += count;
    }

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
