using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

// ISO/IEC 14496-12: bounded box and field reader. Unknown boxes are skipped, never recursively descended into.
internal ref struct HeifReader(ReadOnlyMemory<byte> data)
{
    private ReadOnlyMemory<byte> remaining = data;

    public readonly int Remaining => remaining.Length;

    public ReadOnlyMemory<byte> Read(int count)
    {
        if (count < 0 || count > remaining.Length)
        {
            throw Error("Truncated container field.");
        }

        ReadOnlyMemory<byte> result = remaining[..count];
        remaining = remaining[count..];
        return result;
    }

    public byte Byte() => Read(1).Span[0];
    public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2).Span);
    public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Read(4).Span);
    public ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Read(8).Span);

    public ulong Unsigned(int size) => size switch
    {
        0 => 0,
        4 => UInt32(),
        8 => UInt64(),
        _ => throw Error("Invalid item location field size.")
    };

    public ReadOnlyMemory<byte> String()
    {
        int length = remaining.Span.IndexOf((byte)0);
        if (length < 0)
        {
            throw Error("Unterminated container string.");
        }

        ReadOnlyMemory<byte> value = Read(length);
        Read(1);
        return value;
    }

    public byte FullBox(out uint flags)
    {
        uint value = UInt32();
        flags = value & 0xffffff;
        return (byte)(value >> 24);
    }

    public void FullBox(byte version = 0)
    {
        byte actual = FullBox(out uint flags);
        if (actual != version)
        {
            throw new NotSupportedException("Unsupported HEIF box version.");
        }

        if (flags != 0)
        {
            throw Error("Invalid box flags.");
        }
    }

    public HeifBox Box()
    {
        int available = remaining.Length;
        ulong size = UInt32();
        uint type = UInt32();
        int header = 8;
        if (size == 1)
        {
            size = UInt64();
            header = 16;
        }
        else if (size == 0)
        {
            size = (ulong)available;
        }

        if (size < (ulong)header || size > (ulong)available)
        {
            throw Error("Invalid or truncated box size.");
        }

        ReadOnlyMemory<byte> payload = Read((int)size - header);
        // Extended-type boxes have a 16-byte user type following the ordinary header.
        if (type == 0x75756964 && payload.Length < 16)
        {
            throw Error("Truncated uuid box.");
        }

        return new(type, payload);
    }

    public readonly void End()
    {
        if (remaining.Length != 0)
        {
            throw Error("Unexpected trailing box data.");
        }
    }

    public static ImageFormatException Error(string message) => new(HeifFormat.Descriptor, message);
}

internal readonly record struct HeifBox(uint Type, ReadOnlyMemory<byte> Data);
