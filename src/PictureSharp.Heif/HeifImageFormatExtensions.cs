namespace PictureSharp.Formats.Heif;

/// <summary>HEIF members of <see cref="ImageFormat"/>.</summary>
public static class HeifImageFormatExtensions
{
    extension(ImageFormat)
    {
        /// <summary>HEIC/HEIF (<c>image/heic</c>), available for decoding and encoding once <see cref="HeifFormat"/> is registered.</summary>
        public static ImageFormat Heif => HeifFormat.Descriptor;
    }
}
