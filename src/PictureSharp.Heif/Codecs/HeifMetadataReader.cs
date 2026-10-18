using System.Buffers.Binary;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// The primary image's metadata, shared by Decode and ReadMetadata so both report the same blocks: the ICC profile (or
// the nclx/VUI colour as chromaticities and gamma), EXIF and XMP.
internal static class HeifMetadataReader
{
    // The ICC profile, or the nclx/VUI colour as chromaticities and gamma; a gain map needs it also with IgnoreMetadata.
    // primaryNode avoids building the node twice when the caller already has it.
    public static void ApplyColour(HeifContainer container, ImageMetadata metadata, DecoderOptions options,
        HeifImageNode? primaryNode = null)
    {
        HeifItem primary = container.ImageItem;
        ReadOnlyMemory<byte>? icc = container.Icc(primary);
        if (icc is null && primary.Type != HeifFourCc.Hvc1 && container.Sources(primary) is [var source, ..])
        {
            icc = container.Icc(source);
        }

        if (icc is { Length: > 0 } profile)
        {
            metadata.SetBlock(MetadataKinds.Icc, profile.Span);
        }
        else
        {
            HeifColorDescription.Apply(metadata, HeifRenderer.ColourOf(primaryNode ?? container.Node(primary, detach: false)), options);
        }
    }

    // EXIF and XMP.
    public static void ApplyDescriptive(HeifContainer container, ImageMetadata metadata)
    {
        foreach ((bool exif, ReadOnlyMemory<byte> data) in container.DescriptiveMetadata(container.ImageItem))
        {
            if (!exif)
            {
                metadata.SetBlock(MetadataKinds.Xmp, data.Span);
            }
            else if (TryGetTiff(data.Span, out ReadOnlySpan<byte> tiff))
            {
                metadata.SetBlock(MetadataKinds.Exif, tiff);
                // irot/imir already oriented the pixels; the EXIF orientation is informative only.
                metadata.TrySetOrientation(ExifOrientation.TopLeft);
            }
        }
    }

    // ISO/IEC 23008-12 A.2.1: exif_tiff_header_offset precedes the TIFF header. Malformed blocks are skipped.
    public static bool TryGetTiff(ReadOnlySpan<byte> exif, out ReadOnlySpan<byte> tiff)
    {
        tiff = default;
        if (exif.Length < 4)
        {
            return false;
        }

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(exif);
        if (offset > (uint)(exif.Length - 4))
        {
            return false;
        }

        tiff = exif[(4 + (int)offset)..];
        return tiff.StartsWith("II*\0"u8) || tiff.StartsWith("MM\0*"u8);
    }
}
