using System.Buffers;

namespace PictureSharp.Formats.Heif.Hevc;

// Decodes the first coded picture of a NAL unit sequence (parameter sets followed by slice segments), as stored in a
// HEIF image item. NAL units are passed without start codes or length prefixes.
internal static class HevcDecoder
{
    // reservePixels is invoked with the coded width and height before any picture buffer is allocated.
    public static HevcFrame Decode(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits, Action<int, int>? reservePixels,
        CancellationToken cancellationToken)
    {
        int largest = 0;
        foreach (ReadOnlyMemory<byte> nal in nalUnits)
        {
            largest = Math.Max(largest, nal.Length);
        }

        byte[] rbsp = ArrayPool<byte>.Shared.Rent(Math.Max(largest, 16));
        try
        {
            var spsById = new HevcSequenceParameterSet?[16];
            var ppsById = new HevcPictureParameterSet?[64];
            HevcPictureDecoder? picture = null;
            foreach (ReadOnlyMemory<byte> nalUnit in nalUnits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> nal = nalUnit.Span;
                HevcNalHeader header = HevcNalHeader.Parse(nal);
                if (header.LayerId != 0)
                {
                    continue;
                }

                int length = HevcNalHeader.ExtractRbsp(nal, rbsp);
                switch (header.Type)
                {
                    case HevcNalHeader.SpsType:
                        HevcSequenceParameterSet sps = HevcSequenceParameterSet.Parse(rbsp.AsSpan(0, length));
                        spsById[sps.Id] = sps;
                        break;
                    case HevcNalHeader.PpsType:
                        HevcPictureParameterSet pps = HevcPictureParameterSet.Parse(rbsp.AsSpan(0, length));
                        ppsById[pps.Id] = pps;
                        break;
                    case <= 9 or (>= 16 and <= 21):
                        if (picture is not null && (rbsp[0] & 0x80) != 0)
                        {
                            // first_slice_segment_in_pic_flag: an image item holds one picture; later ones are ignored.
                            return picture.Finish();
                        }

                        picture ??= CreatePicture(rbsp.AsSpan(0, length), header, spsById, ppsById, reservePixels, cancellationToken);
                        if (HevcSliceHeader.ReadPpsId(rbsp.AsSpan(0, length), header) != picture.Pps.Id)
                        {
                            throw HevcError.Invalid("slice segments of one picture refer to different PPSs.");
                        }

                        picture.DecodeSliceSegment(rbsp, length, header);
                        break;
                }
            }

            return picture is null ? throw HevcError.Invalid("no coded picture.") : picture.Finish();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rbsp);
        }
    }

    private static HevcPictureDecoder CreatePicture(ReadOnlySpan<byte> rbsp, HevcNalHeader nal, HevcSequenceParameterSet?[] spsById,
        HevcPictureParameterSet?[] ppsById, Action<int, int>? reservePixels, CancellationToken cancellationToken)
    {
        HevcPictureParameterSet pps = ppsById[HevcSliceHeader.ReadPpsId(rbsp, nal)] ?? throw HevcError.Invalid("missing PPS.");
        HevcSequenceParameterSet sps = spsById[pps.SpsId] ?? throw HevcError.Invalid("missing SPS.");
        reservePixels?.Invoke(sps.Width, sps.Height);
        return new HevcPictureDecoder(sps, pps, cancellationToken);
    }
}
