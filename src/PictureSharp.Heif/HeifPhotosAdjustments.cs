namespace PictureSharp.Formats.Heif;

/// <summary>
/// Slider positions of the Apple Photos "Adjust" panel for <see cref="HeifEdits.Adjustments"/>, as shown in Photos:
/// −100 to 100, or 0 to 100 for <see cref="Sharpness"/>, <see cref="Definition"/> and <see cref="NoiseReduction"/>.
/// 0 (the default) leaves an adjustment off.
/// </summary>
/// <remarks>
/// Photos does not record these edits in a form other programs can read (iOS 27 sidecars hold only the portrait
/// effect), so the values come from the caller. Each adjustment is an approximation fitted to photographs exported
/// by Photos with that slider alone; how well it matches depends on the slider, see
/// <see href="https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/measurements/photos-edits-0.6.8-fit.md">the
/// measurements</see>. Warmth, tint, vignette, brightness and contrast are the closest; saturation, vibrance, black
/// point, shadows, highlights, brilliance and definition are approximate; exposure, sharpness and noise reduction are
/// rough and will be refined. Values between the measured slider positions are interpolated linearly. Sliders combine
/// in a fixed order: tone curves per channel (warmth, tint, exposure, brightness, highlights, shadows, black point,
/// brilliance, definition, sharpness, noise reduction), band filters (sharpness, definition, noise reduction,
/// brilliance, shadows), contrast, saturation, vibrance and vignette last.
/// </remarks>
public sealed record HeifPhotosAdjustments
{
    /// <summary>Exposure. Photos adapts it to the scene; the approximation is rough.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Exposure { get; init => field = Signed(value, nameof(Exposure)); }

    /// <summary>Brilliance: brightens shadows and mid-tones and adds local contrast. Scene-adaptive in Photos; rough.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Brilliance { get; init => field = Signed(value, nameof(Brilliance)); }

    /// <summary>Highlights: lifts or recovers bright tones. Scene-adaptive in Photos; rough.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Highlights { get; init => field = Signed(value, nameof(Highlights)); }

    /// <summary>Shadows: lifts (positive) or deepens (negative) dark tones.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Shadows { get; init => field = Signed(value, nameof(Shadows)); }

    /// <summary>Contrast: positive values steepen the tone curve, negative ones flatten it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Contrast { get; init => field = Signed(value, nameof(Contrast)); }

    /// <summary>Brightness: raises or lowers mid-tones while keeping black and white.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Brightness { get; init => field = Signed(value, nameof(Brightness)); }

    /// <summary>Black point: positive values crush dark tones to black, negative ones lift the blacks.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float BlackPoint { get; init => field = Signed(value, nameof(BlackPoint)); }

    /// <summary>Saturation: −100 gives a greyscale image.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Saturation { get; init => field = Signed(value, nameof(Saturation)); }

    /// <summary>Vibrance: changes the saturation of muted colours more than of saturated ones.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Vibrance { get; init => field = Signed(value, nameof(Vibrance)); }

    /// <summary>Warmth: positive values shift towards yellow-red, negative towards blue.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Warmth { get; init => field = Signed(value, nameof(Warmth)); }

    /// <summary>Tint: positive values shift towards magenta, negative towards green.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Tint { get; init => field = Signed(value, nameof(Tint)); }

    /// <summary>Sharpness, 0–100. Photos sharpens edge-adaptively; the approximation is a linear band filter.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside 0–100.</exception>
    public float Sharpness { get; init => field = Unsigned(value, nameof(Sharpness)); }

    /// <summary>Definition, 0–100: local contrast.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside 0–100.</exception>
    public float Definition { get; init => field = Unsigned(value, nameof(Definition)); }

    /// <summary>Noise reduction, 0–100: smooths the finest detail.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside 0–100.</exception>
    public float NoiseReduction { get; init => field = Unsigned(value, nameof(NoiseReduction)); }

    /// <summary>Vignette: positive values darken the corners, negative ones brighten them; the centre is kept.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside −100–100.</exception>
    public float Vignette { get; init => field = Signed(value, nameof(Vignette)); }

    /// <summary>Whether every slider is 0, so rendering returns an unchanged copy.</summary>
    public bool IsEmpty => Exposure == 0 && Brilliance == 0 && Highlights == 0 && Shadows == 0 && Contrast == 0 && Brightness == 0
        && BlackPoint == 0 && Saturation == 0 && Vibrance == 0 && Warmth == 0 && Tint == 0 && Sharpness == 0 && Definition == 0
        && NoiseReduction == 0 && Vignette == 0;

    private static float Signed(float value, string name) =>
        value is >= -100 and <= 100 ? value : throw new ArgumentOutOfRangeException(name, value, "Use a slider value from -100 to 100.");

    private static float Unsigned(float value, string name) =>
        value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(name, value, "Use a slider value from 0 to 100.");
}
