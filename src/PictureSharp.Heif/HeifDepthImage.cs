namespace PictureSharp.Formats.Heif;

/// <summary>
/// The raw samples of a depth or disparity auxiliary image, as <see cref="HeifDocument.LoadDepth"/> reads them. Values
/// are the decoded luma samples at the coded bit depth: no colour conversion, gamma, bit-depth reduction or scaling to
/// the photograph's size. Units, calibration and conversion to distance or disparity are not interpreted; the original
/// auxiliary type, subtype and metadata are kept for that. The object is immutable and owns its buffers.
/// </summary>
public sealed class HeifDepthImage
{
    internal HeifDepthImage(uint itemId, uint? photoItemId, HeifAuxiliaryInfo auxiliary, int width, int height, int bitDepth,
        ushort[] samples, ReadOnlyMemory<byte> exif, ReadOnlyMemory<byte> xmp)
    {
        ItemId = itemId;
        PhotoItemId = photoItemId;
        Auxiliary = auxiliary;
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Samples = samples;
        Exif = exif;
        Xmp = xmp;
        AppleDepth = HeifAppleDepthInfo.FromXmp(xmp.Span);
    }

    /// <summary>The identifier of the depth or disparity item.</summary>
    public uint ItemId { get; }

    /// <summary>The first item the depth image's auxl reference points to (the photograph), or null without one.</summary>
    public uint? PhotoItemId { get; }

    /// <summary>The declared auxiliary role with the original aux_type and uninterpreted aux_subtype bytes.</summary>
    public HeifAuxiliaryInfo Auxiliary { get; }

    /// <summary>Width after the item's container transforms (grid assembly, clap, irot, imir).</summary>
    public int Width { get; }

    /// <summary>Height after the item's container transforms.</summary>
    public int Height { get; }

    /// <summary>The coded luma bit depth (8 or 10 for the supported HEVC profiles); every sample is below <c>1 &lt;&lt; BitDepth</c>.</summary>
    public int BitDepth { get; }

    /// <summary><see cref="Width"/> × <see cref="Height"/> samples in top-down rows, without padding.</summary>
    public ReadOnlyMemory<ushort> Samples { get; }

    /// <summary>
    /// The TIFF data of an EXIF item describing the depth item (cdsc), or empty without one or with
    /// <see cref="DecoderOptions.IgnoreMetadata"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Exif { get; }

    /// <summary>
    /// The XMP packet describing the depth item (cdsc), decompressed when stored with deflate, or empty without one or
    /// with <see cref="DecoderOptions.IgnoreMetadata"/>. Apple stores its depth description here.
    /// </summary>
    public ReadOnlyMemory<byte> Xmp { get; }

    /// <summary>
    /// Apple's description of the samples read from <see cref="Xmp"/> (declared kind, value range, simulated aperture),
    /// or null when the XMP is absent, malformed or not Apple's. Required by <see cref="HeifEdits.Portrait"/>.
    /// </summary>
    public HeifAppleDepthInfo? AppleDepth { get; }

    /// <summary>
    /// Creates an opaque greyscale image of the samples at their own size for viewing. Samples are stretched linearly
    /// from the described integer range (<see cref="AppleDepth"/>), or from 0 to the largest code of
    /// <see cref="BitDepth"/>, to 0–255 with rounding; values outside the range are clipped. Apple disparity is bright
    /// for near objects. Resize the result to overlay it on the photograph.
    /// </summary>
    public Image ToImage()
    {
        int low = AppleDepth?.IntMinValue ?? 0, high = AppleDepth?.IntMaxValue ?? (1 << BitDepth) - 1;
        var image = new Image(Width, Height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        ReadOnlySpan<ushort> samples = Samples.Span;
        for (int i = 0; i < samples.Length; i++)
        {
            byte value = (byte)Math.Clamp(((samples[i] - low) * 510 + (high - low)) / (2 * (high - low)), 0, 255);
            pixels[i] = new Rgba32(value, value, value);
        }

        return image;
    }
}
