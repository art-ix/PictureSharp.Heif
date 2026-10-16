using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

// Item data access for decoding, valid after Open(): all references, extents and properties were checked there.
internal sealed partial class HeifContainer
{
    private const uint Hvc1 = 0x68766331;
    private const uint Grid = 0x67726964;
    private const uint Iden = 0x6964656e;

    public HeifItem PrimaryItem { get; private set; } = null!;

    public HeifItem? AlphaItem { get; private set; }

    // ISO/IEC 23008-12 prem: the master image's colour samples are premultiplied by its alpha.
    public bool PremultipliedAlpha =>
        AlphaItem is not null && PrimaryItem.References.TryGetValue(0x7072656d, out List<uint>? targets)
        && targets.Contains(AlphaItem.Id);

    public static bool IsCoded(HeifItem item) => item.Type == Hvc1;

    public static bool IsGrid(HeifItem item) => item.Type == Grid;

    public static bool IsIdentity(HeifItem item) => item.Type == Iden;

    public HeifItem Source(HeifItem item, int index) => items[item.References[0x64696d67][index]];

    public int SourceCount(HeifItem item) =>
        item.References.TryGetValue(0x64696d67, out List<uint>? sources) ? sources.Count : 0;

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
    public List<ReadOnlyMemory<byte>> HevcNalUnits(HeifItem item)
    {
        ReadOnlyMemory<byte> configuration = PropertyOf(item, 0x68766343)!.Value.Data;
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

    // ISO/IEC 23008-12 6.6.2.3 ImageGrid; validated by DescribeGrid.
    public (int Rows, int Columns, int Width, int Height) GridLayout(HeifItem item)
    {
        Span<byte> header = stackalloc byte[12];
        ReadItemPrefix(item, header[..8]);
        int rows = header[2] + 1;
        int columns = header[3] + 1;
        if ((header[1] & 1) == 0)
        {
            return (rows, columns, BinaryPrimitives.ReadUInt16BigEndian(header[4..]), BinaryPrimitives.ReadUInt16BigEndian(header[6..]));
        }

        ReadItemPrefix(item, header);
        return (rows, columns, (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]), (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]));
    }

    // Stored (pre-transform) size from ispe.
    public (int Width, int Height) StoredSize(HeifItem item)
    {
        var reader = new HeifReader(PropertyOf(item, 0x69737065)!.Value.Data);
        reader.FullBox();
        return ((int)reader.UInt32(), (int)reader.UInt32());
    }

    // irot, imir and clap properties in association order.
    public IEnumerable<HeifBox> Transforms(HeifItem item)
    {
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type is 0x69726f74 or 0x696d6972 or 0x636c6170)
            {
                yield return property;
            }
        }
    }

    // colr nclx as (primaries, transfer, matrix, fullRange), when present.
    public (int Primaries, int Transfer, int Matrix, bool FullRange)? Nclx(HeifItem item)
    {
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type == 0x636f6c72 && property.Data.Span[..4].SequenceEqual("nclx"u8))
            {
                ReadOnlySpan<byte> data = property.Data.Span;
                return (BinaryPrimitives.ReadUInt16BigEndian(data[4..]), BinaryPrimitives.ReadUInt16BigEndian(data[6..]),
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
            if (property.Type == 0x636f6c72 && (property.Data.Span[..4].SequenceEqual("prof"u8) || property.Data.Span[..4].SequenceEqual("rICC"u8)))
            {
                return property.Data[4..];
            }
        }

        return null;
    }

    // Exif and XMP items describing the given image (cdsc references).
    public IEnumerable<(bool Exif, ReadOnlyMemory<byte> Data)> DescriptiveMetadata(HeifItem image)
    {
        foreach (HeifItem item in items.Values)
        {
            if ((item.Type == 0x45786966 || item.IsXmp) && item.References.TryGetValue(0x63647363, out List<uint>? targets)
                && targets.Contains(image.Id))
            {
                yield return (item.Type == 0x45786966, ItemData(item));
            }
        }
    }

    private HeifBox? PropertyOf(HeifItem item, uint type)
    {
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type == type)
            {
                return property;
            }
        }

        return null;
    }
}
