namespace PictureSharp.Formats.Heif.Hevc;

// H.265 7.3.1: NAL unit header and RBSP extraction.
internal readonly record struct HevcNalHeader(int Type, int LayerId, int TemporalId)
{
    public const int VpsType = 32;
    public const int SpsType = 33;
    public const int PpsType = 34;

    public bool IsVcl => Type < 32;

    // IDR_W_RADL and IDR_N_LP (7.4.2.2).
    public bool IsIdr => Type is 19 or 20;

    public bool IsIrap => Type is >= 16 and <= 23;

    public static HevcNalHeader Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2)
        {
            throw HevcError.Invalid("NAL unit shorter than its header.");
        }

        if ((nal[0] & 0x80) != 0)
        {
            throw HevcError.Invalid("forbidden_zero_bit is set.");
        }

        int temporalIdPlus1 = nal[1] & 7;
        if (temporalIdPlus1 == 0)
        {
            throw HevcError.Invalid("nuh_temporal_id_plus1 is zero.");
        }

        return new((nal[0] >> 1) & 0x3f, ((nal[0] & 1) << 5) | (nal[1] >> 3), temporalIdPlus1 - 1);
    }

    // 7.4.2: removes emulation_prevention_three_byte from the payload after the two-byte header. With removed, records
    // the RBSP length at each removal, so payload positions (entry points count those bytes) map back to the RBSP.
    public static int ExtractRbsp(ReadOnlySpan<byte> nal, Span<byte> destination, List<int>? removed = null)
    {
        int length = 0;
        int zeros = 0;
        for (int i = 2; i < nal.Length; i++)
        {
            byte value = nal[i];
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                removed?.Add(length);
                continue;
            }

            destination[length++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return length;
    }
}
