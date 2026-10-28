namespace PictureSharp.Formats.Heif;

/// <summary>
/// Options for the HEIC encoder: HEVC 4:2:0 in the HEIF container, Main (8-bit) or, for 16-bit images, Main 10
/// (<see cref="BitDepth"/>).
/// </summary>
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

    /// <summary>
    /// Maximum displayed dimension of an embedded thumbnail; 0 disables it (the default). Keeps the aspect ratio,
    /// never enlarges, preserves color and alpha, and excludes EXIF/XMP. Uses the image's quality settings.
    /// </summary>
    public int ThumbnailMaxDimension
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(ThumbnailMaxDimension), value, "Use 0 or a positive dimension.");
    }

    /// <summary>
    /// Whether the HDR gain map attached by <c>DecoderOptions.LoadGainMap</c> (and kept through geometric edits) is written
    /// as an ISO 21496-1 <c>tmap</c> item, as iPhones store it. Default: <see cref="HeifGainMapMode.None"/>, an SDR file.
    /// Maps from HEIC and JPEG (Ultra HDR) are written, an Apple-only map converted to ISO 21496-1 by the core; a map the
    /// core cannot convert is skipped by <see cref="HeifGainMapMode.Preserve"/> and rejected by
    /// <see cref="HeifGainMapMode.Require"/> with <see cref="NotSupportedException"/>;
    /// <see cref="HeifGainMapMode.Require"/> without a map throws
    /// <see cref="InvalidOperationException"/>. A map loaded from HEIC is copied without re-encoding when neither the image
    /// was rotated or edited nor its EXIF orientation set; the embedded thumbnail never gets a map.
    /// </summary>
    public HeifGainMapMode GainMap
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(GainMap), value, "Unknown gain map mode.");
    }

    /// <summary>Quality 0–100 of a gain map that has to be encoded again, mapped to QP as <see cref="Quality"/>. Default: 90.</summary>
    public int GainMapQuality { get; init => field = InRange(value, 0, 100, nameof(GainMapQuality)); } = 90;

    /// <summary>
    /// The coded sample depth: 8 bits, or 10 bits for 16-bit images by default. Samples are scaled to 10 bits with
    /// rounding (<c>round(v × 1023 / 65535)</c>). Default: <see cref="HeifBitDepth.Auto"/>.
    /// </summary>
    public HeifBitDepth BitDepth
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(BitDepth), value, "Unknown HEIF bit depth.");
    }

    private static int InRange(int value, int minimum, int maximum, string name) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(name, value, $"Use a value from {minimum} to {maximum}.");
}
