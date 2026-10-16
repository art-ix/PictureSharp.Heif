namespace PictureSharp.Formats.Heif;

/// <summary>Options for the HEIC encoder (HEVC Main 8-bit 4:2:0 in the HEIF container).</summary>
public sealed record HeifEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => HeifFormat.Descriptor;

    /// <summary>
    /// Quality 0–100; higher is larger and closer to the original. It maps linearly to the HEVC quantization parameter
    /// (QP = round(0.51 × (100 − Quality))). Default: 50.
    /// </summary>
    public int Quality { get; init => field = InRange(value, 0, 100, nameof(Quality)); } = 50;

    /// <summary>
    /// Strength 0–3 of variance adaptive quantization: textured regions get a coarser and smooth regions a finer
    /// quantizer, which keeps gradients and skin free of banding at the same file size. 0 disables it.
    /// Default: <see cref="DefaultAdaptiveQuantizationStrength"/>.
    /// </summary>
    public float AdaptiveQuantizationStrength
    {
        get;
        init => field = value is >= 0 and <= 3 ? value : throw new ArgumentOutOfRangeException(nameof(AdaptiveQuantizationStrength), value, "Use a value from 0 to 3.");
    } = DefaultAdaptiveQuantizationStrength;

    /// <summary>The default <see cref="AdaptiveQuantizationStrength"/>.</summary>
    public const float DefaultAdaptiveQuantizationStrength = 1;

    /// <summary>Quality of the alpha plane 0–100, or null to use <see cref="Quality"/>. Default: null.</summary>
    public int? AlphaQuality { get; init => field = value is null ? null : InRange(value.Value, 0, 100, nameof(AlphaQuality)); }

    /// <summary>
    /// Images wider or taller than this are stored as a grid of tiles of this size (a multiple of 32, 64–4096), as
    /// cameras do; 0 always stores a single image. Default: 512.
    /// </summary>
    public int TileSize
    {
        get;
        init => field = value == 0 || (value is >= 64 and <= 4096 && value % 32 == 0)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TileSize), value, "Use 0 or a multiple of 32 from 64 to 4096.");
    } = 512;

    /// <summary>Maximum number of tiles encoded in parallel; -1 uses all processors. The output does not depend on it. Default: -1.</summary>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value is -1 or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), value, "Use -1 or a positive value.");
    } = -1;

    private static int InRange(int value, int minimum, int maximum, string name) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(name, value, $"Use a value from {minimum} to {maximum}.");
}
