// Frozen from v0.6.6. Namespace-only adaptation; do not tune this reference.
using System.Drawing;

namespace PictureSharp.Formats.Heif.Tests.BaselineV066;

/// <summary>Settings of <see cref="HeifPortrait.Render"/> and <see cref="HeifDocument.RenderPortrait"/>.</summary>
public sealed record HeifPortraitOptions
{
    /// <summary>
    /// The simulated f-number, from 1.4 to 16; smaller values blur more. Null uses the depth item's
    /// <see cref="HeifAppleDepthInfo.SimulatedAperture"/>, or f/4.5 when it is absent or out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value lies outside 1.4–16.</exception>
    public float? Aperture
    {
        get;
        init => field = value is null || (value >= MinimumAperture && value <= MaximumAperture)
            ? value : throw new ArgumentOutOfRangeException(nameof(Aperture), value, $"Use an f-number from {MinimumAperture} to {MaximumAperture}.");
    }

    /// <summary>
    /// The point to focus on, in pixels of the photograph as passed or loaded (after its container transforms). Null
    /// focuses on the subject: the median disparity under the portrait matte, or the nearest tenth of the scene
    /// without a matte.
    /// </summary>
    public Point? FocusPoint { get; init; }

    /// <summary>
    /// The largest number of working pixels: the result plus the blur pyramid and layers (at most about twice the
    /// photograph's pixels). Exceeding it throws <see cref="ImageFormatException"/> before they are allocated.
    /// Defaults to <see cref="DecoderOptions.MaxPixels"/> of the default options.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxPixels
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxPixels), value, "Use a positive value.");
    } = DecoderOptions.Default.MaxPixels;

    /// <summary>The largest number of threads; -1 (the default) uses all processors. The result does not depend on it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is 0 or below -1.</exception>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value is -1 or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), value, "Use -1 or a positive value.");
    } = -1;

    internal const float MinimumAperture = 1.4f, MaximumAperture = 16f;
}
