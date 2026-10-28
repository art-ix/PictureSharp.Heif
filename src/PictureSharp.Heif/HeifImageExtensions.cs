using PictureSharp.Formats.Heif.Codecs;

namespace PictureSharp.Formats.Heif;

/// <summary>HEIC shortcuts for <see cref="Image"/>; saving requires <see cref="HeifFormat"/> to be registered.</summary>
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

        /// <summary>
        /// Renders Photos sliders and the Portrait blur into a copy of the image (its first and only frame), in the
        /// order of <see cref="HeifEdits"/>: sliders first, then the blur. Metadata and alpha are kept; the image is not
        /// changed. An attached HDR gain map is not edited and is removed from the result. Empty edits return an
        /// unchanged copy.
        /// </summary>
        /// <param name="edits">The stages to apply, their limits and parallelism.</param>
        /// <param name="maps">
        /// The photograph's depth and matte, from <see cref="HeifDocument.LoadPortraitMaps"/> or built by the caller;
        /// required when <see cref="HeifEdits.Portrait"/> is set, ignored otherwise.
        /// </param>
        /// <param name="cancellationToken">Checked between row bands of every stage.</param>
        /// <remarks>The image must be in display orientation (as loaded, after its container transforms).</remarks>
        /// <exception cref="ArgumentException">
        /// The image has several frames; a portrait is requested without maps; the depth has no Apple description;
        /// or the depth or matte proportions differ from the image's.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="HeifPortraitOptions.FocusPoint"/> lies outside the image.</exception>
        /// <exception cref="NotSupportedException">
        /// The image has 16-bit samples (<see cref="PixelType.Rgba64"/>); the edits render 8-bit images, so convert it with
        /// <c>Clone(x => x.ConvertPixelType(PixelType.Rgba32))</c> first.
        /// </exception>
        /// <exception cref="ImageFormatException">
        /// Depth samples lie outside the described range, or the working pixels exceed <see cref="HeifEdits.MaxPixels"/>.
        /// </exception>
        public Image ApplyHeifEdits(HeifEdits edits, HeifPortraitMaps? maps = null, CancellationToken cancellationToken = default) =>
            HeifEditPipeline.Apply(image, edits, maps, fromFile: false, cancellationToken);
    }
}
