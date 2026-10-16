namespace PictureSharp.Formats.Heif.Hevc;

// H.265 9.3.4.3 arithmetic encoder writing slice segment data bytes.
// port: HM source/Lib/TLibEncoder/TEncBinCoderCABAC.cpp (start, encodeBin, encodeBinEP, encodeBinsEP, encodeBinTrm,
//       finish, testAndWriteOut, writeOut)
internal sealed class HevcCabacEncoder : HevcCabacWriter
{
    private byte[] output = new byte[4096];
    private int length;
    private uint low;
    private uint range;
    private int bitsLeft;
    private int bufferedBytes;
    private uint bufferedByte;

    public HevcCabacEncoder() => Start();

    public int Length => length;

    public ReadOnlySpan<byte> Output => output.AsSpan(0, length);

    public void Start()
    {
        low = 0;
        range = 510;
        bitsLeft = 23;
        bufferedBytes = 0;
        bufferedByte = 0xff;
    }

    public override void EncodeBin(ref byte state, int bin)
    {
        uint lps = LpsTable[((state >> 1) << 2) + (int)((range >> 6) & 3)];
        range -= lps;
        if (bin != (state & 1))
        {
            int numBits = RenormTable[(int)(lps >> 3)];
            low = (low + range) << numBits;
            range = lps << numBits;
            bitsLeft -= numBits;
            TestAndWriteOut();
        }
        else if (range < 256)
        {
            low <<= 1;
            range <<= 1;
            bitsLeft--;
            TestAndWriteOut();
        }

        state = Next(state, bin);
    }

    public override void EncodeBypass(int bin)
    {
        low <<= 1;
        if (bin != 0)
        {
            low += range;
        }

        bitsLeft--;
        TestAndWriteOut();
    }

    public override void EncodeBypassBits(uint value, int count)
    {
        while (count > 8)
        {
            count -= 8;
            uint pattern = value >> count;
            low <<= 8;
            low += range * pattern;
            value -= pattern << count;
            bitsLeft -= 8;
            TestAndWriteOut();
        }

        low <<= count;
        low += range * value;
        bitsLeft -= count;
        TestAndWriteOut();
    }

    public override void EncodeTerminate(int bin)
    {
        range -= 2;
        if (bin != 0)
        {
            low += range;
            low <<= 7;
            range = 2 << 7;
            bitsLeft -= 7;
        }
        else if (range >= 256)
        {
            return;
        }
        else
        {
            low <<= 1;
            range <<= 1;
            bitsLeft--;
        }

        TestAndWriteOut();
    }

    // Ends the arithmetic code after a terminating bin of 1 and appends a one bit and zero alignment, so the next
    // data (PCM samples, the next substream or nothing) starts on a byte boundary (port: TEncBinCABAC::finish plus
    // the rbsp_slice_segment_trailing_bits / byte_alignment written by HM).
    public void FinishAndAlign()
    {
        if ((low >> (32 - bitsLeft)) != 0)
        {
            WriteByte(bufferedByte + 1);
            while (bufferedBytes > 1)
            {
                WriteByte(0x00);
                bufferedBytes--;
            }

            low -= 1u << (32 - bitsLeft);
        }
        else
        {
            if (bufferedBytes > 0)
            {
                WriteByte(bufferedByte);
            }

            while (bufferedBytes > 1)
            {
                WriteByte(0xff);
                bufferedBytes--;
            }
        }

        WriteBits(low >> 8, 24 - bitsLeft);
        WriteBits(1, 1);
        while (pendingBits != 0)
        {
            WriteBits(0, 1);
        }
    }

    private uint pendingValue;
    private int pendingBits;

    private void WriteBits(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            pendingValue = (pendingValue << 1) | ((value >> i) & 1);
            if (++pendingBits == 8)
            {
                Append((byte)pendingValue);
                pendingValue = 0;
                pendingBits = 0;
            }
        }
    }

    private void WriteByte(uint value) => WriteBits(value & 0xff, 8);

    private void TestAndWriteOut()
    {
        if (bitsLeft < 12)
        {
            WriteOut();
        }
    }

    private void WriteOut()
    {
        uint leadByte = low >> (24 - bitsLeft);
        bitsLeft += 8;
        low &= 0xffffffffu >> bitsLeft;
        if (leadByte == 0xff)
        {
            bufferedBytes++;
        }
        else if (bufferedBytes > 0)
        {
            uint carry = leadByte >> 8;
            uint value = bufferedByte + carry;
            bufferedByte = leadByte & 0xff;
            WriteByte(value);
            value = (0xff + carry) & 0xff;
            while (bufferedBytes > 1)
            {
                WriteByte(value);
                bufferedBytes--;
            }
        }
        else
        {
            bufferedBytes = 1;
            bufferedByte = leadByte;
        }
    }

    private void Append(byte value)
    {
        if (length == output.Length)
        {
            Array.Resize(ref output, output.Length * 2);
        }

        output[length++] = value;
    }

    // Raw bits after pcm_flag; the arithmetic coder must be finished and aligned first, and restarted afterwards.
    public void WriteRawBits(uint value, int bits) => WriteBits(value, bits);

    private static ReadOnlySpan<byte> LpsTable =>
    [
        128, 176, 208, 240, 128, 167, 197, 227, 128, 158, 187, 216, 123, 150, 178, 205,
        116, 142, 169, 195, 111, 135, 160, 185, 105, 128, 152, 175, 100, 122, 144, 166,
        95, 116, 137, 158, 90, 110, 130, 150, 85, 104, 123, 142, 81, 99, 117, 135,
        77, 94, 111, 128, 73, 89, 105, 122, 69, 85, 100, 116, 66, 80, 95, 110,
        62, 76, 90, 104, 59, 72, 86, 99, 56, 69, 81, 94, 53, 65, 77, 89,
        51, 62, 73, 85, 48, 59, 69, 80, 46, 56, 66, 76, 43, 53, 63, 72,
        41, 50, 59, 69, 39, 48, 56, 65, 37, 45, 54, 62, 35, 43, 51, 59,
        33, 41, 48, 56, 32, 39, 46, 53, 30, 37, 43, 50, 29, 35, 41, 48,
        27, 33, 39, 45, 26, 31, 37, 43, 24, 30, 35, 41, 23, 28, 33, 39,
        22, 27, 32, 37, 21, 26, 30, 35, 20, 24, 29, 33, 19, 23, 27, 31,
        18, 22, 26, 30, 17, 21, 25, 28, 16, 20, 23, 27, 15, 19, 22, 25,
        14, 18, 21, 24, 14, 17, 20, 23, 13, 16, 19, 22, 12, 15, 18, 21,
        12, 14, 17, 20, 11, 14, 16, 19, 11, 13, 15, 18, 10, 12, 15, 17,
        10, 12, 14, 16, 9, 11, 13, 15, 9, 11, 12, 14, 8, 10, 12, 14,
        8, 9, 11, 13, 7, 9, 11, 12, 7, 9, 10, 12, 7, 8, 10, 11,
        6, 8, 9, 11, 6, 7, 9, 10, 6, 7, 8, 9, 2, 2, 2, 2,
    ];

    private static ReadOnlySpan<byte> RenormTable =>
    [
        6, 5, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];
}
