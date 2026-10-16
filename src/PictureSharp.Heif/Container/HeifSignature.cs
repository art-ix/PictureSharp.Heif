using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

/// <summary>Recognizes HEVC-coded HEIF files by the brands of their leading <c>ftyp</c> box (ISO/IEC 14496-12 4.3).</summary>
internal static class HeifSignature
{
    /// <summary>Enough for the <c>ftyp</c> boxes written by cameras and encoders, which list a handful of brands.</summary>
    public const int HeaderSize = 256;

    private const uint Ftyp = 0x66747970;   // 'ftyp'

    /// <summary>Brands that declare HEVC-coded images or sequences (ISO/IEC 23008-12 Annex B).</summary>
    private static ReadOnlySpan<uint> HevcBrands => [0x68656963, 0x68656978, 0x6865696d, 0x68656973, 0x68657663, 0x68657678];   // heic heix heim heis hevc hevx

    /// <summary>AV1 brands: AVIF shares the container but not the codec.</summary>
    private static ReadOnlySpan<uint> AvifBrands => [0x61766966, 0x61766973];   // avif avis

    /// <summary>Generic HEIF brands that do not name the codec.</summary>
    private static ReadOnlySpan<uint> GenericBrands => [0x6d696631, 0x6d736631];   // mif1 msf1

    /// <summary>
    /// Whether <paramref name="header"/> starts with an <c>ftyp</c> box of an HEVC-coded HEIF file: an HEVC brand, or a
    /// generic HEIF brand without an AVIF brand. Malformed boxes return false.
    /// </summary>
    public static bool IsHevcImage(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != Ftyp)
        {
            return false;
        }

        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        int payloadOffset = 8;
        if (size == 1)
        {
            if (header.Length < 24)
            {
                return false;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            payloadOffset = 16;
        }
        else if (size == 0)
        {
            size = (ulong)header.Length;
        }

        if (size < (ulong)(payloadOffset + 8) || (size - (ulong)payloadOffset) % 4 != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> box = header[..(int)Math.Min(size, (ulong)header.Length)];
        bool generic = false, avif = false;
        // The major brand, then the compatible brands after the 4-byte minor version.
        for (int offset = payloadOffset; offset + 4 <= box.Length; offset += offset == payloadOffset ? 8 : 4)
        {
            uint brand = BinaryPrimitives.ReadUInt32BigEndian(box[offset..]);
            if (HevcBrands.Contains(brand))
            {
                return true;
            }

            avif |= AvifBrands.Contains(brand);
            generic |= GenericBrands.Contains(brand);
        }

        return generic && !avif;
    }
}
