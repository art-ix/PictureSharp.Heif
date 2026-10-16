using PictureSharp.Formats.Heif.Codecs;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif;

/// <summary>
/// The HEIC/HEIF format plugin. Register it once to read and write <c>.heic</c>/<c>.heif</c> files through the usual
/// <see cref="Image"/> API.
/// </summary>
/// <example>
/// <code>
/// ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
/// </code>
/// </example>
public sealed class HeifFormat : IImageFormatModule
{
    // One instance per process: ImageFormat compares by reference, so every module shares it.
    internal static readonly ImageFormat Descriptor = ImageFormat.Create("HEIF", "image/heic", ".heic", ".heif", ".hif");

    /// <inheritdoc/>
    public ImageFormat Format => Descriptor;

    /// <inheritdoc/>
    public int HeaderSize => HeifSignature.HeaderSize;

    /// <inheritdoc/>
    public bool IsSupported(ReadOnlySpan<byte> header) => HeifSignature.IsHevcImage(header);

    /// <inheritdoc/>
    public IImageFormatDecoder Decoder => HeifDecoder.Instance;

    /// <inheritdoc/>
    public IImageFormatEncoder Encoder => HeifEncoder.Instance;
}
