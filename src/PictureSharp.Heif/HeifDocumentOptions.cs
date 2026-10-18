namespace PictureSharp.Formats.Heif;

/// <summary>Immutable limits for buffering a HEIF document, independent of options for decoding its images.</summary>
public sealed record HeifDocumentOptions
{
    /// <summary>
    /// Maximum encoded input size in bytes, checked before copying memory and while reading streams.
    /// Must be positive. Default: <see cref="Array.MaxLength"/>. Pixel and metadata limits belong to each image read.
    /// </summary>
    public long MaxInputBytes
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxInputBytes), value, "Use a positive byte limit.");
    } = Array.MaxLength;
}
