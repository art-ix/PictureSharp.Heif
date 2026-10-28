namespace PictureSharp.Formats.Heif;

/// <summary>The sample depth the HEIC encoder writes (<see cref="HeifEncoderOptions.BitDepth"/>).</summary>
public enum HeifBitDepth
{
    /// <summary>
    /// 8-bit images are coded with 8 bits (HEVC Main). A 16-bit image is coded with 10 bits (HEVC Main 10), unless every
    /// sample is an exact 8-bit value (a multiple of 257, e.g. a widened 8-bit image), which is coded with 8 bits. Alpha
    /// and a re-encoded gain map follow the same rule for their own pixels.
    /// </summary>
    Auto,

    /// <summary>8 bits: 16-bit images are rounded to 8 bits first.</summary>
    UpTo8,

    /// <summary>10 bits (HEVC Main 10 and Monochrome 10) for colour, alpha and a re-encoded gain map; 8-bit images are widened exactly.</summary>
    Ten,
}
