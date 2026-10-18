using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

// Item data access for decoding, valid after Open(): all references, extents and properties were checked there.
internal sealed partial class HeifContainer
{
    // The decoded image: the primary item, or the item HeifDecoderOptions selects.
    public HeifItem ImageItem { get; private set; } = null!;

    public HeifItem? AlphaItem { get; private set; }

    // ISO/IEC 23008-12 prem: the master image's colour samples are premultiplied by its alpha.
    public bool PremultipliedAlpha => AlphaItem is not null && ImageItem.RefersTo(HeifFourCc.Prem, AlphaItem);

    public IEnumerable<HeifItem> Items => items.Values;

    // Displayed size after the item's transforms; throws NotSupportedException for items that are not images.
    public (int Width, int Height) DisplaySize(HeifItem item)
    {
        HeifImageDescription description = Describe(item, 0);
        return (description.Width, description.Height);
    }

    // The rendering description of an image item; detach copies the bytes it refers to.
    public HeifImageNode Node(HeifItem item, bool detach, int depth = 0)
    {
        Require(depth <= MaxReferenceDepth, "Derived image nesting exceeds the limit.");
        context?.ThrowIfCancellationRequested();
        IReadOnlyList<HeifTransform> transforms = [.. Transforms(item)];
        HeifNclx? nclx = Nclx(item);
        switch (item.Type)
        {
            case HeifFourCc.Hvc1:
                List<ReadOnlyMemory<byte>> nals = HevcNalUnits(item);
                if (detach)
                {
                    for (int i = 0; i < nals.Count; i++)
                    {
                        nals[i] = nals[i].ToArray();
                    }
                }

                return new HeifCodedNode(nals, StoredSize(item), transforms, nclx);
            case HeifFourCc.Grid:
                return new HeifGridNode(GridLayout(item), [.. Sources(item).Select(tile => Node(tile, detach, depth + 1))],
                    transforms, nclx);
            case HeifFourCc.Iden:
                return new HeifIdentityNode(Node(Sources(item)[0], detach, depth + 1), transforms, nclx);
            default:
                throw new NotSupportedException("Unsupported HEIF image item type.");
        }
    }

    // Concatenated item payload; a single extent is returned without copying.
    public ReadOnlyMemory<byte> ItemData(HeifItem item)
    {
        ReadOnlyMemory<byte> source = ItemSource(item);
        if (item.Extents.Count == 1)
        {
            HeifExtent extent = item.Extents[0];
            return source.Slice((int)extent.Offset, (int)extent.Length);
        }

        long total = 0;
        foreach (HeifExtent extent in item.Extents)
        {
            total += (long)extent.Length;
        }

        byte[] buffer = new byte[total];
        int position = 0;
        foreach (HeifExtent extent in item.Extents)
        {
            source.Span.Slice((int)extent.Offset, (int)extent.Length).CopyTo(buffer.AsSpan(position));
            position += (int)extent.Length;
        }

        return buffer;
    }

    // ISO/IEC 14496-15 8.3.3: parameter set NAL units from hvcC followed by the length-prefixed NAL units of the item.
    private List<ReadOnlyMemory<byte>> HevcNalUnits(HeifItem item)
    {
        ReadOnlyMemory<byte> configuration = PropertyOf(item, HeifFourCc.HvcC)!.Value.Data;
        var nals = new List<ReadOnlyMemory<byte>>();
        var reader = new HeifReader(configuration);
        reader.Read(21);
        int lengthSize = (reader.Byte() & 3) + 1;
        Require(lengthSize != 3, "Invalid NAL unit length size.");
        int arrays = reader.Byte();
        for (int i = 0; i < arrays; i++)
        {
            reader.Byte();
            int count = reader.UInt16();
            for (int j = 0; j < count; j++)
            {
                nals.Add(reader.Read(reader.UInt16()));
            }
        }

        ReadOnlyMemory<byte> payload = ItemData(item);
        int position = 0;
        while (position < payload.Length)
        {
            context?.ThrowIfCancellationRequested();
            Require(payload.Length - position >= lengthSize, "Truncated NAL unit length.");
            ReadOnlySpan<byte> prefix = payload.Span.Slice(position, lengthSize);
            uint length = lengthSize switch
            {
                1 => prefix[0],
                2 => BinaryPrimitives.ReadUInt16BigEndian(prefix),
                _ => BinaryPrimitives.ReadUInt32BigEndian(prefix),
            };
            position += lengthSize;
            Require(length >= 2 && length <= (uint)(payload.Length - position), "Invalid NAL unit length.");
            Entries(1);
            nals.Add(payload.Slice(position, (int)length));
            position += (int)length;
        }

        return nals;
    }

    // ISO/IEC 23008-12 6.6.2.3 ImageGrid with 16- or 32-bit output dimensions.
    private HeifGridLayout GridLayout(HeifItem item)
    {
        Span<byte> header = stackalloc byte[12];
        ReadItemPrefix(item, header[..8]);
        Require(header[0] == 0 && (header[1] & ~1) == 0, "Invalid grid header.");
        int rows = header[2] + 1;
        int columns = header[3] + 1;
        if ((header[1] & 1) == 0)
        {
            return new(rows, columns, BinaryPrimitives.ReadUInt16BigEndian(header[4..]), BinaryPrimitives.ReadUInt16BigEndian(header[6..]));
        }

        ReadItemPrefix(item, header);
        return new(rows, columns, Dimension(BinaryPrimitives.ReadUInt32BigEndian(header[4..])),
            Dimension(BinaryPrimitives.ReadUInt32BigEndian(header[8..])));
    }

    // Stored (pre-transform) size from ispe.
    private (int Width, int Height) StoredSize(HeifItem item)
    {
        var reader = new HeifReader(PropertyOf(item, HeifFourCc.Ispe)!.Value.Data);
        reader.FullBox();
        return ((int)reader.UInt32(), (int)reader.UInt32());
    }

    // irot, imir and clap properties in association order.
    private IEnumerable<HeifTransform> Transforms(HeifItem item) =>
        item.Properties.Select(Property).Where(property => HeifTransform.IsTransform(property.Type)).Select(HeifTransform.Read);

    // colr nclx, when present.
    private HeifNclx? Nclx(HeifItem item)
    {
        foreach (HeifBox property in item.Properties.Select(Property))
        {
            ReadOnlySpan<byte> data = property.Data.Span;
            if (property.Type == HeifFourCc.Colr && BinaryPrimitives.ReadUInt32BigEndian(data) == HeifFourCc.Nclx)
            {
                return new HeifNclx(BinaryPrimitives.ReadUInt16BigEndian(data[4..]), BinaryPrimitives.ReadUInt16BigEndian(data[6..]),
                    BinaryPrimitives.ReadUInt16BigEndian(data[8..]), (data[10] & 0x80) != 0);
            }
        }

        return null;
    }

    // colr prof or rICC (an ICC profile either way).
    public ReadOnlyMemory<byte>? Icc(HeifItem item)
    {
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type == HeifFourCc.Colr
                && BinaryPrimitives.ReadUInt32BigEndian(property.Data.Span) is HeifFourCc.Prof or HeifFourCc.RIcc)
            {
                ChargeIcc(association, property);
                return property.Data[4..];
            }
        }

        return null;
    }

    private HeifBox? PropertyOf(HeifItem item, uint type, bool supportedOnly = false)
    {
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type == type && (!supportedOnly || !unsupportedProperties.Contains(association.Index)))
            {
                return property;
            }
        }

        return null;
    }
}
