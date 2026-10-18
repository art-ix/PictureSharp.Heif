using System.Buffers.Binary;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;

namespace PictureSharp.Formats.Heif.Tests;

// Test-only construction with idat payloads, independent of the production writer and parser.
internal static class HeifDocumentFixture
{
    internal sealed record Item(int Id, string Type = "hvc1", byte[][]? Properties = null, byte[]? Payload = null,
        bool Hidden = false, string Name = "", (string Type, int[] Targets)[]? References = null,
        int Protection = 0, int Method = 1, int DataReference = 0);

    public static byte[] Build(params Item[] items)
    {
        var properties = new List<byte[]>();
        var associations = new List<byte[]>();
        var locations = new List<byte[]>();
        var definitions = new List<byte[]>();
        var references = new List<byte[]>();
        var payloads = new List<byte[]>();
        int offset = 0;
        foreach (Item item in items)
        {
            byte[] payload = item.Payload ?? [0, 0, 0, 0];
            var indexes = new List<byte>();
            foreach (byte[] property in item.Properties ?? [Ispe(80, 60), Hvcc()])
            {
                properties.Add(property);
                indexes.Add((byte)properties.Count);
            }

            associations.Add(Association(item.Id, indexes.ToArray()));
            locations.Add(Join(U16(item.Id), U16(item.Method), U16(item.DataReference), U16(1), U32((uint)offset), U32((uint)payload.Length)));
            definitions.Add(Full("infe", 2, item.Hidden ? 1u : 0u, U16(item.Id), U16(item.Protection),
                System.Text.Encoding.ASCII.GetBytes(item.Type), Text(item.Name),
                item.Type == "mime" ? Join(Text("application/rdf+xml"), Text("")) : []));
            foreach ((string type, int[] targets) in item.References ?? [])
            {
                references.Add(Reference(type, item.Id, targets));
            }

            payloads.Add(payload);
            offset += payload.Length;
        }

        return Join(Ftyp(), Full("meta", 0, 0, Handler(), Full("pitm", 0, 0, U16(items[0].Id)), Iinf(definitions.ToArray()),
            Full("iloc", 1, 0, [0x44, 0], U16(items.Length), Join(locations.ToArray())),
            Box("iprp", Box("ipco", properties.ToArray()), Ipma(associations.ToArray())),
            Full("iref", 0, 0, references.ToArray()), Box("idat", payloads.ToArray())));
    }

    public static Item Encoded(int id, int width, int height, (string Type, int[] Targets)[]? references = null, bool alpha = false, bool solid = false)
    {
        var config = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
        using Image image = HeifEncodeTests.Sample(width, height, alpha);
        if (solid)
        {
            image.GetPixelSpan().Fill(new Rgba32(230, 10, 30));
        }

        byte[] file = image.ToHeic(new HeifEncoderOptions { Configuration = config, TileSize = 0 });
        byte[][] props = Boxes(Child(Child(Child(file, "meta")[4..], "iprp"), "ipco")).Select(box => Box(box.Type, box.Data.ToArray())).ToArray();
        byte[] payload = HeifTestItems.Payloads(file).Single(item => item.Id == 1).Data;
        return new Item(id, Properties: props, Payload: payload, References: references);
    }

    public static byte[] Auxiliary(string type, params byte[] subtype) => Full("auxC", 0, 0, Text(type), subtype);

    public static ReadOnlyMemory<byte> Child(ReadOnlyMemory<byte> data, string type) => Boxes(data).Single(box => box.Type == type).Data;

    public static IEnumerable<(string Type, ReadOnlyMemory<byte> Data)> Boxes(ReadOnlyMemory<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Span[offset..]);
            yield return (System.Text.Encoding.ASCII.GetString(data.Span.Slice(offset + 4, 4)), data.Slice(offset + 8, length - 8));
            offset += length;
        }
    }
}
