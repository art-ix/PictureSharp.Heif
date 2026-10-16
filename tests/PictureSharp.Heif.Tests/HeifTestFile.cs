using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Heif.Tests;

// Deliberately constructed container fixtures; the payload is not a decodable HEVC picture.
internal static class HeifTestFile
{
    public static byte[] Box(string type, params byte[][] parts)
    {
        byte[] payload = Join(parts);
        return Join(U32((uint)payload.Length + 8), Encoding.ASCII.GetBytes(type), payload);
    }

    public static byte[] Full(string type, byte version, uint flags, params byte[][] parts) =>
        Box(type, [version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], Join(parts));

    public static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    public static byte[] U16(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    public static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] Text(string value) => Encoding.UTF8.GetBytes(value + '\0');
    public static byte[] Ftyp() => Box("ftyp", "heic"u8.ToArray(), U32(0), "mif1heic"u8.ToArray());
    public static byte[] Handler() => Full("hdlr", 0, 0, U32(0), "pict"u8.ToArray(), new byte[12], Text(""));
    public static byte[] Ispe(int width, int height) => Full("ispe", 0, 0, U32((uint)width), U32((uint)height));
    public static byte[] Pixi(int bits) => Full("pixi", 0, 0, [3, (byte)bits, (byte)bits, (byte)bits]);
    public static byte[] Hvcc(int bits = 8)
    {
        byte[] record = new byte[23];
        record[0] = 1;
        record[1] = (byte)(bits == 10 ? 2 : 1);
        record[12] = 30;
        record[13] = 0xf0;
        record[15] = 0xfc;
        record[16] = 0xfd;
        record[17] = record[18] = (byte)(0xf8 + bits - 8);
        record[21] = 3;
        return Box("hvcC", record);
    }

    public static byte[] Infe(int id, string type = "hvc1", bool wide = false) =>
        Full("infe", wide ? (byte)3 : (byte)2, 0, wide ? U32((uint)id) : U16(id), U16(0), Encoding.ASCII.GetBytes(type), Text(""));

    public static byte[] Iinf(params byte[][] items) => Full("iinf", 0, 0, U16(items.Length), Join(items));
    public static byte[] Iloc(int count = 1, int length = 4) => Full("iloc", 1, 0, [0x44, 0], U16(count),
        Join(Enumerable.Range(1, count).Select(id => Join(U16(id), U16(1), U16(0), U16(1), U32(0), U32((uint)length))).ToArray()));

    public static byte[] Ipma(params byte[][] associations) => Full("ipma", 0, 0, U32((uint)associations.Length), Join(associations));
    public static byte[] Association(int id, params byte[] indexes) => Join(U16(id), [(byte)indexes.Length], indexes);
    public static byte[] Reference(string type, int from, params int[] to) => Box(type, U16(from), U16(to.Length), Join(to.Select(U16).ToArray()));

    public static byte[] File(byte[]? properties = null, byte[]? association = null, byte[]? locations = null,
        byte[]? info = null, byte[]? references = null, byte[]? payload = null, byte[]? extra = null, int bits = 8) =>
        Join(Ftyp(), Full("meta", 0, 0, Handler(), Full("pitm", 0, 0, U16(1)), info ?? Iinf(Infe(1)),
            locations ?? Iloc(), Box("iprp", properties ?? Box("ipco", Ispe(80, 60), Hvcc(bits), Pixi(bits)),
                association ?? Ipma(Association(1, 1, 0x82, 3))), references ?? [],
            Box("idat", payload ?? [0, 0, 0, 0]), extra ?? []));
}
