using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Heif.Codecs;

// The Live Photo ContentIdentifier (tag 0x0011) of the Apple MakerNote in an EXIF TIFF block: IFD0 → Exif IFD (0x8769)
// → MakerNote (0x927c) = "Apple iOS\0", u16 version, "MM", then a big-endian IFD whose offsets are relative to the
// MakerNote start. EXIF is untrusted, so any structural problem means "no identifier" rather than an error.
internal static class AppleMakerNote
{
    private const ushort ExifIfdTag = 0x8769;
    private const ushort MakerNoteTag = 0x927c;
    private const ushort ContentIdentifierTag = 0x0011;
    private const int MaxIdentifierLength = 256;

    public static string? ContentIdentifier(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8 || !(tiff.StartsWith("II*\0"u8) || tiff.StartsWith("MM\0*"u8)))
        {
            return null;
        }

        bool big = tiff[0] == 'M';
        int exif = FindEntry(tiff, Read32(tiff[4..], big), ExifIfdTag, big);
        if (exif < 0 || Read16(tiff[(exif + 2)..], big) is not (4 or 13))
        {
            return null;
        }

        int maker = FindEntry(tiff, Read32(tiff[(exif + 8)..], big), MakerNoteTag, big);
        if (maker < 0)
        {
            return null;
        }

        uint length = Read32(tiff[(maker + 4)..], big), offset = Read32(tiff[(maker + 8)..], big);
        if (length < 16 || offset > (uint)tiff.Length || length > (uint)tiff.Length - offset)
        {
            return null;
        }

        ReadOnlySpan<byte> note = tiff.Slice((int)offset, (int)length);
        if (!note.StartsWith("Apple iOS\0"u8) || note[12] != 'M' || note[13] != 'M')
        {
            return null;
        }

        int entry = FindEntry(note, 14, ContentIdentifierTag, big: true);
        if (entry < 0 || Read16(note[(entry + 2)..], true) != 2)
        {
            return null;
        }

        // ASCII: up to four bytes inline, otherwise at an offset relative to the MakerNote.
        uint count = Read32(note[(entry + 4)..], true);
        uint at = count <= 4 ? (uint)entry + 8 : Read32(note[(entry + 8)..], true);
        if (count == 0 || count > MaxIdentifierLength || at > (uint)note.Length || count > (uint)note.Length - at)
        {
            return null;
        }

        ReadOnlySpan<byte> value = note.Slice((int)at, (int)count);
        int end = value.IndexOf((byte)0);
        value = end < 0 ? value : value[..end];
        return value.IsEmpty || value.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0 ? null : Encoding.ASCII.GetString(value);
    }

    // The offset of the 12-byte entry for the tag in the directory at ifd, or -1.
    private static int FindEntry(ReadOnlySpan<byte> tiff, uint ifd, ushort tag, bool big)
    {
        if (tiff.Length < 2 || ifd > (uint)tiff.Length - 2)
        {
            return -1;
        }

        int count = Read16(tiff[(int)ifd..], big);
        for (int i = 0; i < count; i++)
        {
            long at = ifd + 2 + (12L * i);
            if (at + 12 > tiff.Length)
            {
                return -1;
            }

            if (Read16(tiff[(int)at..], big) == tag)
            {
                return (int)at;
            }
        }

        return -1;
    }

    private static ushort Read16(ReadOnlySpan<byte> data, bool big) =>
        big ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static uint Read32(ReadOnlySpan<byte> data, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
}
