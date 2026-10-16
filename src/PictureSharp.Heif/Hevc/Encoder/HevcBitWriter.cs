namespace PictureSharp.Formats.Heif.Hevc;

// MSB-first RBSP writer for headers (H.265 7.2, 9.2), with NAL unit framing and emulation prevention.
internal sealed class HevcBitWriter
{
    private byte[] buffer = new byte[256];
    private int length;
    private uint pending;
    private int pendingBits;

    public int BitCount => (length * 8) + pendingBits;

    public void Write(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            pending = (pending << 1) | ((value >> i) & 1);
            if (++pendingBits == 8)
            {
                Append((byte)pending);
                pending = 0;
                pendingBits = 0;
            }
        }
    }

    public void WriteFlag(bool value) => Write(value ? 1u : 0u, 1);

    public void WriteUe(uint value)
    {
        uint code = value + 1;
        int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
        Write(0, bits - 1);
        Write(code, bits);
    }

    public void WriteSe(int value) => WriteUe(value <= 0 ? (uint)(-2L * value) : (uint)((2L * value) - 1));

    // rbsp_trailing_bits / byte_alignment: a one bit followed by zero bits up to the byte boundary.
    public void WriteTrailingBits()
    {
        Write(1, 1);
        while (pendingBits != 0)
        {
            Write(0, 1);
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (pendingBits != 0)
        {
            throw new InvalidOperationException("Raw bytes must be byte aligned.");
        }

        foreach (byte value in bytes)
        {
            Append(value);
        }
    }

    public ReadOnlySpan<byte> Rbsp => pendingBits == 0 ? buffer.AsSpan(0, length) : throw new InvalidOperationException("Unaligned RBSP.");

    // nal_unit_header (7.3.1.2) followed by the RBSP with emulation_prevention_three_byte inserted (7.4.2).
    public byte[] ToNalUnit(int nalType)
    {
        ReadOnlySpan<byte> rbsp = Rbsp;
        var output = new List<byte>(rbsp.Length + 8) { (byte)(nalType << 1), 1 };
        int zeros = 0;
        foreach (byte value in rbsp)
        {
            if (zeros >= 2 && value <= 3)
            {
                output.Add(3);
                zeros = 0;
            }

            output.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return [.. output];
    }

    private void Append(byte value)
    {
        if (length == buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }

        buffer[length++] = value;
    }
}
