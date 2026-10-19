using System.Buffers;

namespace PictureSharp.Formats.Heif.Hevc;

// Decodes the first coded picture of a NAL unit sequence (parameter sets followed by slice segments), as stored in a
// HEIF image item. NAL units are passed without start codes or length prefixes.
internal static class HevcDecoder
{
    // reservePixels is invoked with the coded width and height before any picture buffer is allocated. With parallel,
    // a picture coded as one slice segment with wavefront entry points decodes its CTB rows in parallel; the result
    // does not depend on it.
    public static HevcFrame Decode(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits, Action<int, int>? reservePixels,
        CancellationToken cancellationToken, ParallelOptions? parallel = null)
    {
        int largest = 0;
        foreach (ReadOnlyMemory<byte> nal in nalUnits)
        {
            largest = Math.Max(largest, nal.Length);
        }

        int sliceSegments = CountFirstPictureSliceSegments(nalUnits);

        bool wavefronts = parallel is not null && parallel.MaxDegreeOfParallelism != 1 && sliceSegments == 1
            && Environment.ProcessorCount > 1;
        List<int>? removed = wavefronts ? [] : null;

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

                removed?.Clear();
                int length = HevcNalHeader.ExtractRbsp(nal, rbsp, removed);
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
                            return picture.Finish(wavefronts ? parallel : null);
                        }

                        picture ??= CreatePicture(rbsp.AsSpan(0, length), header, spsById, ppsById, reservePixels, cancellationToken);
                        if (HevcSliceHeader.ReadPpsId(rbsp.AsSpan(0, length), header) != picture.Pps.Id)
                        {
                            throw HevcError.Invalid("slice segments of one picture refer to different PPSs.");
                        }

                        picture.DecodeSliceSegment(rbsp, length, header, removed, wavefronts ? parallel : null);
                        break;
                }
            }

            return picture is null ? throw HevcError.Invalid("no coded picture.") : picture.Finish(wavefronts ? parallel : null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rbsp);
        }
    }

    // Slice segment NAL units of the first picture: layer 0 VCL units up to the next first_slice_segment_in_pic_flag
    // (the first payload bit, never changed by emulation prevention).
    private static int CountFirstPictureSliceSegments(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits)
    {
        int count = 0;
        foreach (ReadOnlyMemory<byte> nalUnit in nalUnits)
        {
            ReadOnlySpan<byte> nal = nalUnit.Span;
            if (nal.Length < 3 || (nal[0] & 1) != 0 || nal[1] >> 3 != 0 || ((nal[0] >> 1) & 0x3f) is not (<= 9 or (>= 16 and <= 21)))
            {
                continue;
            }

            if (count > 0 && (nal[2] & 0x80) != 0)
            {
                break;
            }

            count++;
        }

        return count;
    }

    // The VUI colour description of the first sequence parameter set, without decoding a picture.
    public static HevcColourDescription? ReadColour(IReadOnlyList<ReadOnlyMemory<byte>> nalUnits)
    {
        foreach (ReadOnlyMemory<byte> nalUnit in nalUnits)
        {
            ReadOnlySpan<byte> nal = nalUnit.Span;
            HevcNalHeader header = HevcNalHeader.Parse(nal);
            if (header.LayerId == 0 && header.Type == HevcNalHeader.SpsType)
            {
                byte[] rbsp = ArrayPool<byte>.Shared.Rent(Math.Max(nal.Length, 16));
                try
                {
                    return HevcSequenceParameterSet.Parse(rbsp.AsSpan(0, HevcNalHeader.ExtractRbsp(nal, rbsp))).Colour;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rbsp);
                }
            }
        }

        return null;
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
