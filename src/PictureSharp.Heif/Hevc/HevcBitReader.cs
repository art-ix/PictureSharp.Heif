using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Heif.Hevc;

// H.265 7.2 / 9.2: MSB-first reader over an RBSP (emulation prevention bytes already removed).
internal ref struct HevcBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    private int position;

    public readonly int BitPosition => position;
    public readonly int BitsRemaining => (data.Length << 3) - position;
    public readonly bool IsByteAligned => (position & 7) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadFlag() => ReadBit() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBit()
    {
        if (position >= data.Length << 3)
        {
            throw HevcError.Invalid("truncated header.");
        }

        uint bit = (uint)(data[position >> 3] >> (7 - (position & 7))) & 1;
        position++;
        return bit;
    }

    public uint ReadBits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    public void Skip(int count)
    {
        if (count < 0 || count > BitsRemaining)
        {
            throw HevcError.Invalid("truncated header.");
        }

        position += count;
    }

    // ue(v), 9.2. Values beyond 32 bits are invalid in every syntax element we read.
    public uint ReadUe()
    {
        int zeros = 0;
        while (ReadBit() == 0)
        {
            if (++zeros > 31)
            {
                throw HevcError.Invalid("Exp-Golomb code too long.");
            }
        }

        return zeros == 0 ? 0 : (uint)((1UL << zeros) - 1 + ReadBits(zeros));
    }

    public int ReadSe()
    {
        uint code = ReadUe();
        return (code & 1) != 0 ? (int)((code + 1) >> 1) : -(int)(code >> 1);
    }

    // ue(v) checked against an inclusive maximum.
    public int ReadUe(int maximum, string name)
    {
        uint value = ReadUe();
        if (value > (uint)maximum)
        {
            throw HevcError.Invalid(name + " out of range.");
        }

        return (int)value;
    }

    public int ReadSe(int minimum, int maximum, string name)
    {
        int value = ReadSe();
        if (value < minimum || value > maximum)
        {
            throw HevcError.Invalid(name + " out of range.");
        }

        return value;
    }

    public void ByteAlign() => position = (position + 7) & ~7;

    public readonly int BytePosition => (position + 7) >> 3;

    // 7.2 more_rbsp_data(): true while data remains before the rbsp_stop_one_bit.
    public readonly bool MoreRbspData()
    {
        int last = data.Length - 1;
        while (last >= 0 && data[last] == 0)
        {
            last--;
        }

        if (last < 0)
        {
            return false;
        }

        int stopBit = (last << 3) + 7 - System.Numerics.BitOperations.TrailingZeroCount(data[last]);
        return position < stopBit;
    }
}
