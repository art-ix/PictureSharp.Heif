namespace PictureSharp.Formats.Heif.Hevc;

// The HEVC layer does not depend on the container or the format descriptor, so tests can link it directly.
internal static class HevcError
{
    public static ImageFormatException Invalid(string message) => new("Invalid HEVC bitstream: " + message);

    public static NotSupportedException Unsupported(string message) => new("Unsupported HEVC feature: " + message);
}
