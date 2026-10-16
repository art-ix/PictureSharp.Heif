namespace PictureSharp.Formats.Heif.Codecs;

// Maps CICP colour primaries and transfer characteristics (ITU-T H.273) without an ICC profile onto PictureSharp's
// PNG-style colour description. sRGB/BT.709 content gets none (it is the default); other primaries get their
// chromaticities. Power-law approximations: sRGB/BT.709-family curves 1/2.2, BT.470 M 1/2.2, BT.470 BG 1/2.8.
internal static class HeifColorDescription
{
    public static void Apply(ImageMetadata metadata, int primaries, int transfer, DecoderOptions options)
    {
        if (transfer is 16 or 18)
        {
            if (options.ColorConversion != ColorConversionMode.None)
            {
                throw new NotSupportedException("HEIF images with PQ or HLG transfer characteristics cannot be converted to sRGB.");
            }

            return;
        }

        CieChromaticities? chromaticities = primaries switch
        {
            4 => new CieChromaticities(0.310f, 0.316f, 0.67f, 0.33f, 0.21f, 0.71f, 0.14f, 0.08f),
            5 => new CieChromaticities(0.3127f, 0.3290f, 0.64f, 0.33f, 0.29f, 0.60f, 0.15f, 0.06f),
            6 or 7 => new CieChromaticities(0.3127f, 0.3290f, 0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f),
            9 => new CieChromaticities(0.3127f, 0.3290f, 0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f),
            11 => new CieChromaticities(0.314f, 0.351f, 0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f),
            12 => new CieChromaticities(0.3127f, 0.3290f, 0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f),
            22 => new CieChromaticities(0.3127f, 0.3290f, 0.630f, 0.340f, 0.295f, 0.605f, 0.155f, 0.077f),
            _ => null,
        };
        float? gamma = transfer switch
        {
            1 or 2 or 4 or 6 or 13 or 14 or 15 => 0.45455f,
            5 => 0.35714f,
            8 => 1f,
            _ => null,
        };
        if (chromaticities is null && transfer is 1 or 2 or 6 or 13 or 14 or 15)
        {
            return;
        }

        if (chromaticities is not null)
        {
            metadata.Chromaticities = chromaticities;
        }

        if (gamma is not null)
        {
            metadata.Gamma = gamma;
        }
    }
}
