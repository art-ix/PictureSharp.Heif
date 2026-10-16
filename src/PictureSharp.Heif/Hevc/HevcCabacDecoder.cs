using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Heif.Hevc;

// H.265 9.3.4.3 arithmetic decoding engine.
// port: HM source/Lib/TLibDecoder/TDecBinCoderCABAC.cpp (byte-wise refill, same range/value representation)
internal sealed class HevcCabacDecoder
{
    private byte[] data = [];
    private int position;
    private int end;
    private uint value;
    private uint range;
    private int bitsNeeded;

    // Bytes requested beyond the end of the slice data; a valid stream never needs any.
    public int Overrun { get; private set; }

    public int Position => position;

    public void Reset(byte[] buffer, int start, int length)
    {
        data = buffer;
        position = start;
        end = start + length;
        Overrun = 0;
    }

    // port: TDecBinCABAC::start
    public void Start()
    {
        range = 510;
        bitsNeeded = -8;
        value = (ReadByte() << 8) | ReadByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadByte()
    {
        if (position < end)
        {
            return data[position++];
        }

        Overrun++;
        return 0;
    }

    // port: TDecBinCABAC::decodeBin; state is (pStateIdx << 1) | valMps as in HM ContextModel.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeBin(ref byte state)
    {
        int s = state;
        uint lps = LpsTable[((s >> 1) << 2) + (int)((range >> 6) - 4)];
        range -= lps;
        uint scaledRange = range << 7;
        int bin;
        if (value < scaledRange)
        {
            bin = s & 1;
            state = NextStateMps[s];
            if (scaledRange < (256 << 7))
            {
                range = scaledRange >> 6;
                value += value;
                if (++bitsNeeded == 0)
                {
                    bitsNeeded = -8;
                    value += ReadByte();
                }
            }
        }
        else
        {
            bin = (s & 1) ^ 1;
            int numBits = RenormTable[(int)(lps >> 3)];
            value = (value - scaledRange) << numBits;
            range = lps << numBits;
            state = NextStateLps[s];
            bitsNeeded += numBits;
            if (bitsNeeded >= 0)
            {
                value += ReadByte() << bitsNeeded;
                bitsNeeded -= 8;
            }
        }

        return bin;
    }

    // port: TDecBinCABAC::decodeBinEP
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeBypass()
    {
        value += value;
        if (++bitsNeeded >= 0)
        {
            bitsNeeded = -8;
            value += ReadByte();
        }

        uint scaledRange = range << 7;
        if (value >= scaledRange)
        {
            value -= scaledRange;
            return 1;
        }

        return 0;
    }

    // port: TDecBinCABAC::decodeBinsEP (the aligned fast path is equivalent and not needed without RExt alignment)
    public uint DecodeBypassBits(int count)
    {
        uint bins = 0;
        while (count > 8)
        {
            value = (value << 8) + (ReadByte() << (8 + bitsNeeded));
            uint scaled = range << 15;
            for (int i = 0; i < 8; i++)
            {
                bins += bins;
                scaled >>= 1;
                if (value >= scaled)
                {
                    bins++;
                    value -= scaled;
                }
            }

            count -= 8;
        }

        bitsNeeded += count;
        value <<= count;
        if (bitsNeeded >= 0)
        {
            value += ReadByte() << bitsNeeded;
            bitsNeeded -= 8;
        }

        uint scaledRange = range << (count + 7);
        for (int i = 0; i < count; i++)
        {
            bins += bins;
            scaledRange >>= 1;
            if (value >= scaledRange)
            {
                bins++;
                value -= scaledRange;
            }
        }

        return bins;
    }

    // port: TDecBinCABAC::decodeBinTrm
    public int DecodeTerminate()
    {
        range -= 2;
        uint scaledRange = range << 7;
        if (value >= scaledRange)
        {
            // The encoder flush ends the arithmetic code at a byte boundary: the next syntax (PCM samples or a new
            // substream) starts at the next unread byte.
            return 1;
        }

        if (scaledRange < (256 << 7))
        {
            range = scaledRange >> 6;
            value += value;
            if (++bitsNeeded == 0)
            {
                bitsNeeded = -8;
                value += ReadByte();
            }
        }

        return 0;
    }

    // Raw bits following pcm_flag, read MSB first from the next byte; returns to arithmetic decoding with Start().
    public uint ReadPcmBits(ref int bitPosition, int count)
    {
        uint result = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIndex = position + (bitPosition >> 3);
            uint bit = byteIndex < end ? (uint)(data[byteIndex] >> (7 - (bitPosition & 7))) & 1 : 0;
            if (byteIndex >= end)
            {
                Overrun++;
            }

            result = (result << 1) | bit;
            bitPosition++;
        }

        return result;
    }

    public void SkipPcmBytes(int count) => position = Math.Min(end, position + count);

    public static byte InitState(int initValue, int qp)
    {
        // port: HM source/Lib/TLibCommon/ContextModel.cpp ContextModel::init
        qp = Math.Clamp(qp, 0, 51);
        int slope = ((initValue >> 4) * 5) - 45;
        int offset = ((initValue & 15) << 3) - 16;
        int initState = Math.Min(Math.Max(1, ((slope * qp) >> 4) + offset), 126);
        int mps = initState >= 64 ? 1 : 0;
        return (byte)(((mps != 0 ? initState - 64 : 63 - initState) << 1) + mps);
    }

    // port: HM source/Lib/TLibCommon/TComCABACTables.cpp sm_aucLPSTable (rows of four)
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

    // port: TComCABACTables::sm_aucRenormTable
    private static ReadOnlySpan<byte> RenormTable =>
    [
        6, 5, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    // port: ContextModel::m_aucNextStateMPS
    private static ReadOnlySpan<byte> NextStateMps =>
    [
        2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17,
        18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33,
        34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65,
        66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81,
        82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
        98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113,
        114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 124, 125, 126, 127,
    ];

    // port: ContextModel::m_aucNextStateLPS
    private static ReadOnlySpan<byte> NextStateLps =>
    [
        1, 0, 0, 1, 2, 3, 4, 5, 4, 5, 8, 9, 8, 9, 10, 11,
        12, 13, 14, 15, 16, 17, 18, 19, 18, 19, 22, 23, 22, 23, 24, 25,
        26, 27, 26, 27, 30, 31, 30, 31, 32, 33, 32, 33, 36, 37, 36, 37,
        38, 39, 38, 39, 42, 43, 42, 43, 44, 45, 44, 45, 46, 47, 48, 49,
        48, 49, 50, 51, 52, 53, 52, 53, 54, 55, 54, 55, 56, 57, 58, 59,
        58, 59, 60, 61, 60, 61, 60, 61, 62, 63, 64, 65, 64, 65, 66, 67,
        66, 67, 66, 67, 68, 69, 68, 69, 70, 71, 70, 71, 70, 71, 72, 73,
        72, 73, 72, 73, 74, 75, 74, 75, 74, 75, 76, 77, 76, 77, 126, 127,
    ];
}
