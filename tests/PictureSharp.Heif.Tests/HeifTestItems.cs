using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Tests;

// Independent, test-only extraction of hvc1 items (hvcC parameter sets followed by the item's NAL units), so HEVC output
// can be checked per item without the plugin's internal container parser.
internal static class HeifTestItems
{
    public static List<(uint Id, List<ReadOnlyMemory<byte>> Nals)> Extract(byte[] file)
    {
        ReadOnlyMemory<byte> meta = Child(file, "meta")[4..];
        ReadOnlyMemory<byte> iinf = Child(meta, "iinf");
        ReadOnlyMemory<byte> iloc = Child(meta, "iloc");
        ReadOnlyMemory<byte> iprp = Child(meta, "iprp");
        ReadOnlyMemory<byte> idat = TryChild(meta, "idat");
        Dictionary<uint, string> types = ItemTypes(iinf);
        Dictionary<uint, (int Method, List<(long Offset, long Length)> Extents)> locations = Locations(iloc.Span);
        List<(string Type, ReadOnlyMemory<byte> Data)> properties = [.. Boxes(Child(iprp, "ipco"))];
        Dictionary<uint, List<int>> associations = Associations(Child(iprp, "ipma").Span);
        var result = new List<(uint, List<ReadOnlyMemory<byte>>)>();
        foreach ((uint id, string type) in types.OrderBy(pair => pair.Key))
        {
            if (type != "hvc1")
            {
                continue;
            }

            ReadOnlySpan<byte> hvcc = properties[associations[id].First(i => properties[i - 1].Type == "hvcC") - 1].Data.Span;
            var nals = new List<ReadOnlyMemory<byte>>();
            int position = 23;
            for (int array = 0; array < hvcc[22]; array++)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(hvcc[(position + 1)..]);
                position += 3;
                for (int n = 0; n < count; n++)
                {
                    int length = BinaryPrimitives.ReadUInt16BigEndian(hvcc[position..]);
                    nals.Add(hvcc.Slice(position + 2, length).ToArray());
                    position += 2 + length;
                }
            }

            (int method, List<(long Offset, long Length)> extents) = locations[id];
            byte[] payload = [.. extents.SelectMany(e => (method == 1 ? idat : file).Slice((int)e.Offset, (int)e.Length).ToArray())];
            int lengthSize = (hvcc[21] & 3) + 1;
            for (int p = 0; p < payload.Length;)
            {
                int length = 0;
                for (int k = 0; k < lengthSize; k++)
                {
                    length = (length << 8) | payload[p + k];
                }

                nals.Add(payload.AsMemory(p + lengthSize, length));
                p += lengthSize + length;
            }

            result.Add((id, nals));
        }

        return result;
    }

    // Every item's type and payload, in identifier order (e.g. tmap, Exif and XMP items).
    public static List<(uint Id, string Type, byte[] Data)> Payloads(byte[] file)
    {
        ReadOnlyMemory<byte> meta = Child(file, "meta")[4..];
        ReadOnlyMemory<byte> idat = TryChild(meta, "idat");
        Dictionary<uint, (int Method, List<(long Offset, long Length)> Extents)> locations = Locations(Child(meta, "iloc").Span);
        return [.. ItemTypes(Child(meta, "iinf")).OrderBy(pair => pair.Key).Where(pair => locations.ContainsKey(pair.Key)).Select(pair =>
        {
            (int method, List<(long Offset, long Length)> extents) = locations[pair.Key];
            byte[] data = [.. extents.SelectMany(e => (method == 1 ? idat : file).Slice((int)e.Offset, (int)e.Length).ToArray())];
            return (pair.Key, pair.Value, data);
        })];
    }

    // The item's associated properties as complete boxes, in association order (for copying an item into a fixture).
    public static byte[][] ItemProperties(byte[] file, uint id)
    {
        ReadOnlyMemory<byte> iprp = Child(Child(file, "meta")[4..], "iprp");
        List<(string Type, ReadOnlyMemory<byte> Data)> properties = [.. Boxes(Child(iprp, "ipco"))];
        return [.. Associations(Child(iprp, "ipma").Span)[id].Select(index => HeifTestFile.Box(properties[index - 1].Type,
            properties[index - 1].Data.ToArray()))];
    }

    private static Dictionary<uint, string> ItemTypes(ReadOnlyMemory<byte> iinf)
    {
        var types = new Dictionary<uint, string>();
        int countSize = iinf.Span[0] == 0 ? 2 : 4;
        foreach ((string type, ReadOnlyMemory<byte> infe) in Boxes(iinf[(4 + countSize)..]))
        {
            ReadOnlySpan<byte> span = infe.Span;
            bool wide = span[0] != 2;
            uint id = wide ? BinaryPrimitives.ReadUInt32BigEndian(span[4..]) : BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
            int offset = 4 + (wide ? 4 : 2) + 2;
            types[id] = System.Text.Encoding.ASCII.GetString(span.Slice(offset, 4));
        }

        return types;
    }

    public static byte[] ToAnnexB(IEnumerable<ReadOnlyMemory<byte>> nals) =>
        [.. nals.SelectMany(nal => new byte[] { 0, 0, 0, 1 }.Concat(nal.ToArray()))];

    private static Dictionary<uint, (int, List<(long, long)>)> Locations(ReadOnlySpan<byte> iloc)
    {
        int version = iloc[0];
        int offsetSize = iloc[4] >> 4;
        int lengthSize = iloc[4] & 15;
        int baseSize = iloc[5] >> 4;
        int indexSize = version is 1 or 2 ? iloc[5] & 15 : 0;
        int position = 6;
        long Number(ReadOnlySpan<byte> data, int size)
        {
            long value = 0;
            for (int i = 0; i < size; i++)
            {
                value = (value << 8) | data[position + i];
            }

            position += size;
            return value;
        }

        long count = Number(iloc, version < 2 ? 2 : 4);
        var result = new Dictionary<uint, (int, List<(long, long)>)>();
        for (long i = 0; i < count; i++)
        {
            uint id = (uint)Number(iloc, version < 2 ? 2 : 4);
            int method = version is 1 or 2 ? (int)(Number(iloc, 2) & 15) : 0;
            Number(iloc, 2);
            long baseOffset = Number(iloc, baseSize);
            long extents = Number(iloc, 2);
            var list = new List<(long, long)>();
            for (long e = 0; e < extents; e++)
            {
                Number(iloc, indexSize);
                long offset = baseOffset + Number(iloc, offsetSize);
                list.Add((offset, Number(iloc, lengthSize)));
            }

            result[id] = (method, list);
        }

        return result;
    }

    private static Dictionary<uint, List<int>> Associations(ReadOnlySpan<byte> ipma)
    {
        bool wideIds = ipma[0] >= 1;
        bool wideIndices = (ipma[3] & 1) != 0;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(ipma[4..]);
        int position = 8;
        var result = new Dictionary<uint, List<int>>();
        for (uint i = 0; i < count; i++)
        {
            uint id = wideIds ? BinaryPrimitives.ReadUInt32BigEndian(ipma[position..]) : BinaryPrimitives.ReadUInt16BigEndian(ipma[position..]);
            position += wideIds ? 4 : 2;
            int associations = ipma[position++];
            var list = new List<int>();
            for (int a = 0; a < associations; a++)
            {
                if (wideIndices)
                {
                    list.Add(BinaryPrimitives.ReadUInt16BigEndian(ipma[position..]) & 0x7fff);
                    position += 2;
                }
                else
                {
                    list.Add(ipma[position++] & 0x7f);
                }
            }

            result[id] = list;
        }

        return result;
    }

    private static ReadOnlyMemory<byte> Child(ReadOnlyMemory<byte> data, string type) =>
        Boxes(data).First(box => box.Type == type).Data;

    private static ReadOnlyMemory<byte> TryChild(ReadOnlyMemory<byte> data, string type) =>
        Boxes(data).FirstOrDefault(box => box.Type == type).Data;

    private static IEnumerable<(string Type, ReadOnlyMemory<byte> Data)> Boxes(ReadOnlyMemory<byte> data)
    {
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.Span[offset..]);
            string type = System.Text.Encoding.ASCII.GetString(data.Span.Slice(offset + 4, 4));
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Span[(offset + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = data.Length - offset;
            }

            yield return (type, data.Slice(offset + header, (int)size - header));
            offset += (int)size;
        }
    }
}
