using System.Drawing;

namespace PictureSharp.Formats.Heif;

/// <summary>
/// The Portrait depth-of-field (background blur) of <see cref="HeifEdits.Portrait"/>, rendered from the photograph's
/// disparity map. iPhone stores the main image sharp and applies the blur only when displaying it, so a converted
/// photograph loses the effect; this stage approximates it.
/// </summary>
/// <remarks>
/// The approximation was tuned against photographs exported by Apple Photos; Apple's algorithm is private and the
/// result does not match Photos exactly. Each pixel is blurred with a disc whose radius grows with its disparity
/// difference from the focus and as the aperture opens; colours are mixed in linear light. The portrait matte keeps
/// subjects near the focus sharp and stops their colours bleeding into the blurred background. Without a
/// <see cref="FocusPoint"/> the focus spans the subject's depths up to its nearest parts, and only pixels nearer than
/// that range or farther than its start are blurred. At wide apertures, foreground people under the matte receive
/// less blur when focusing behind them. Fully protected pixels and pixels at the focus are copied unchanged. The
/// exposure's ISO and time from EXIF set the grain added to blurred areas.
/// </remarks>
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
    /// The point to focus on, in pixels of the photograph as passed or loaded (after its container transforms); only
    /// the depth plane under it is in focus. Null focuses on the subject over a range of depths: from the median
    /// disparity under the portrait matte to its nearest parts (the 97.5th percentile), or without a matte from the
    /// scene's 90th to its 99.5th disparity percentile, so a subject's nearer parts and a thin nearest object stay
    /// sharp while a few outlying samples do not move the focus.
    /// </summary>
    public Point? FocusPoint { get; init; }

    internal const float MinimumAperture = 1.4f, MaximumAperture = 16f;
}
