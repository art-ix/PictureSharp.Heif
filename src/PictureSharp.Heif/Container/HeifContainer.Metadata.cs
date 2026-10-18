using System.IO.Compression;

namespace PictureSharp.Formats.Heif.Container;

// Exif and XMP items. XMP with content_encoding deflate is decompressed on first use and charged by its decompressed
// size; an unknown coding or a corrupt stream skips the block instead of failing the image.
internal sealed partial class HeifContainer
{
    private const int InflateChunk = 16384;
    private readonly Dictionary<HeifItem, ReadOnlyMemory<byte>?> inflatedXmp = [];

    // Exif and XMP items describing the given image (cdsc references), limited to the requested kinds.
    public IEnumerable<(bool Exif, ReadOnlyMemory<byte> Data)> DescriptiveMetadata(HeifItem image,
        MetadataKinds kinds = MetadataKinds.Exif | MetadataKinds.Xmp)
    {
        foreach (HeifItem item in items.Values)
        {
            bool wanted = item.IsExif ? (kinds & MetadataKinds.Exif) != 0 : item.IsXmp && (kinds & MetadataKinds.Xmp) != 0;
            if (wanted && item.RefersTo(HeifFourCc.Cdsc, image) && MetadataPayload(item) is { } data)
            {
                yield return (item.IsExif, data);
            }
        }
    }

    private ReadOnlyMemory<byte>? MetadataPayload(HeifItem item)
    {
        switch (item.ContentEncoding)
        {
            case HeifContentEncoding.None:
                return ItemData(item);
            case HeifContentEncoding.Deflate:
                if (!inflatedXmp.TryGetValue(item, out ReadOnlyMemory<byte>? inflated))
                {
                    inflated = Inflate(ItemData(item));
                    inflatedXmp.Add(item, inflated);
                }

                return inflated;
            default:
                return null;
        }
    }

    // The deflate content coding is a zlib stream (RFC 1950); a stream without a zlib header is read as raw deflate
    // (RFC 1951), which some writers store. A truncated stream counts as corrupt. The output grows only as far as the remaining MaxMetadataBytes and is charged
    // chunk by chunk, so a decompression bomb fails before it allocates past the limit. Returns null for a corrupt stream.
    private ReadOnlyMemory<byte>? Inflate(ReadOnlyMemory<byte> compressed)
    {
        using var input = new HeifInflateInput(compressed);
        using Stream inflater = IsZlibHeader(compressed.Span)
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        long limit = Math.Min(options.MaxMetadataBytes - metadataBytes, Array.MaxLength);
        byte[] output = new byte[(int)Math.Clamp(limit, 0, InflateChunk)];
        int length = 0;
        try
        {
            while (true)
            {
                context?.ThrowIfCancellationRequested();
                if (length == output.Length)
                {
                    // One byte more than the limit allows proves the stream exceeds it.
                    int capacity = (int)Math.Min((long)Math.Max(output.Length, InflateChunk) * 2, limit + 1);
                    Array.Resize(ref output, capacity);
                }

                int read = inflater.Read(output, length, output.Length - length);
                if (read == 0)
                {
                    return input.ReadPastEnd ? null : output.AsMemory(0, length);
                }

                ChargeDecompressed(read);
                length += read;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // RFC 1950: CM 8 (deflate), a window of at most 32 KiB, no preset dictionary and a valid FCHECK.
    private static bool IsZlibHeader(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && (data[0] & 0x0f) == 8 && data[0] >> 4 <= 7 && (data[1] & 0x20) == 0
        && ((data[0] << 8) | data[1]) % 31 == 0;

    // Charged also with IgnoreMetadata, when a gain map still needs its XMP, but reserved only for reported metadata.
    private void ChargeDecompressed(long bytes)
    {
        Require(bytes <= options.MaxMetadataBytes - metadataBytes, "Decompressed XMP exceeds MaxMetadataBytes.");
        if (!options.IgnoreMetadata)
        {
            context?.ReserveMetadata(bytes);
        }

        metadataBytes += bytes;
    }
}
