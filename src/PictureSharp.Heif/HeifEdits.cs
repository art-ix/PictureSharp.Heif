namespace PictureSharp.Formats.Heif;

/// <summary>
/// What <see cref="HeifDocument.Render"/> and <see cref="HeifImageExtensions.ApplyHeifEdits"/> apply to a photograph:
/// Apple Photos sliders and the Portrait background blur, with shared limits and parallelism.
/// </summary>
/// <remarks>
/// Stages run in a fixed order: <see cref="Adjustments"/> first, then <see cref="Portrait"/> on the adjusted pixels, so
/// the blur mixes the edited colours as a camera would. For another order, apply the stages in separate calls. Both
/// stages are approximations fitted to Photos exports and do not match Photos exactly. The result does not depend on
/// the thread count.
/// </remarks>
public sealed record HeifEdits
{
    /// <summary>Photos "Adjust" sliders; null or all zero leaves the colours unchanged.</summary>
    public HeifPhotosAdjustments? Adjustments { get; init; }

    /// <summary>The Portrait background blur; null renders none. Rendering it needs the photograph's depth.</summary>
    public HeifPortraitOptions? Portrait { get; init; }

    /// <summary>
    /// The largest number of working pixels: the result, an intermediate image when both stages run, and the blur
    /// pyramid and layers (at most about twice the photograph's pixels). Exceeding it throws
    /// <see cref="ImageFormatException"/> before they are allocated. Defaults to <see cref="DecoderOptions.MaxPixels"/>
    /// of the default options.
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

    /// <summary>Whether no stage changes the photograph, so rendering returns an unchanged copy.</summary>
    public bool IsEmpty => (Adjustments?.IsEmpty ?? true) && Portrait is null;
}
