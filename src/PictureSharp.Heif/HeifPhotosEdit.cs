using PictureSharp.Formats.Heif.Codecs;

namespace PictureSharp.Formats.Heif;

/// <summary>
/// Applies Apple Photos "Adjust" sliders (<see cref="HeifPhotosAdjustments"/>) to a decoded photograph, approximating
/// what Photos exports for the same settings.
/// </summary>
/// <remarks>
/// Each slider was fitted to Photos exports of three iPhone photographs edited with that slider alone (0.6.8
/// measurements); Apple's algorithm is private and adapts several sliders to the picture, so the result does not
/// match Photos exactly. Warmth, tint, vignette, brightness and contrast are the closest; saturation, vibrance, black
/// point, shadows, highlights, brilliance and definition are approximate; exposure, sharpness and noise reduction are
/// rough and will be refined. Values between the measured slider positions are interpolated linearly. Sliders combine in a fixed
/// order: tone curves per channel (warmth, tint, exposure, brightness, highlights, shadows, black point, brilliance,
/// definition, sharpness, noise reduction), band filters (sharpness, definition, noise reduction, brilliance,
/// shadows), contrast, saturation, vibrance and vignette last. The result does not depend on the thread count.
/// </remarks>
public static class HeifPhotosEdit
{
    /// <summary>
    /// Renders the adjustments into a copy of <paramref name="photo"/> (its first and only frame), keeping its
    /// metadata and alpha; the photograph is not changed. An attached HDR gain map is not adjusted and is removed from
    /// the result.
    /// </summary>
    /// <param name="photo">The photograph in display orientation (as loaded, after its container transforms).</param>
    /// <param name="adjustments">Slider positions; all zero returns an unchanged copy.</param>
    /// <param name="cancellationToken">Checked between row bands of every stage.</param>
    /// <exception cref="ArgumentException">The photograph has several frames.</exception>
    public static Image Render(Image photo, HeifPhotosAdjustments adjustments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(adjustments);
        if (photo.Frames.Count != 1)
        {
            throw new ArgumentException("The photograph must have exactly one frame.", nameof(photo));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var renderer = new HeifPhotosRenderer(photo.Width, photo.Height, adjustments, cancellationToken);
        return photo.Clone(x => x.Apply(frame => renderer.Render(photo.GetPixelMemory(), frame.GetPixelMemory())), cancellationToken);
    }
}
