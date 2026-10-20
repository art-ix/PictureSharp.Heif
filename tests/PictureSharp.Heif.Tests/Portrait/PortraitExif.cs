using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Tests;

internal static class PortraitExif
{
    // The public PictureSharp 0.7.6 EXIF API exposes physical focal length, but not tag 0xa405.
    // Read only this SHORT from the EXIF sub-IFD; never enumerate GPS, MakerNotes or other personal metadata.
    public static int? EquivalentFocalLength(byte[]? tiff)
    {
        if (tiff is null || tiff.Length < 8) { return null; }
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) { return null; }
        if (U16(2) != 42) { return null; }
        uint? exif = Find(U32(4), 0x8769, 4);
        uint? equivalent = exif is null ? null : Find(exif.Value, 0xa405, 3);
        return equivalent is > 0 and <= ushort.MaxValue ? (int)equivalent.Value : null;

        ushort U16(int position) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(position, 2)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(position, 2));
        uint U32(int position) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(position, 4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(position, 4));
        uint? Find(uint offset, ushort tag, ushort type)
        {
            if (offset < 8 || offset > tiff.Length - 2) { return null; }
            int count = U16((int)offset);
            if ((long)offset + 2 + 12L * count > tiff.Length) { return null; }
            for (int i = 0; i < count; i++)
            {
                int entry = (int)offset + 2 + 12 * i;
                if (U16(entry) == tag && U16(entry + 2) == type && U32(entry + 4) == 1)
                {
                    return type == 3 ? U16(entry + 8) : U32(entry + 8);
                }
            }

            return null;
        }
    }
}
