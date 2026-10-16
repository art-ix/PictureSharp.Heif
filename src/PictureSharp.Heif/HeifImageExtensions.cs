namespace PictureSharp.Formats.Heif;

/// <summary>HEIC shortcuts for <see cref="Image"/>; require <see cref="HeifFormat"/> to be registered.</summary>
public static class HeifImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as HEIC; null options mean the defaults. Animations keep the first frame only.</summary>
        public void SaveAsHeic(string path, HeifEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as HEIC; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsHeic(Stream stream, HeifEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as HEIC asynchronously; null options mean the defaults.</summary>
        public Task SaveAsHeicAsync(string path, HeifEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as HEIC asynchronously; null options mean the defaults.</summary>
        public Task SaveAsHeicAsync(Stream stream, HeifEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as HEIC; null options mean the defaults.</summary>
        public byte[] ToHeic(HeifEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
