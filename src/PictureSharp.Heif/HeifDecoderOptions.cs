namespace PictureSharp.Formats.Heif;

/// <summary>
/// Selects the HEIF image item to decode, passed in <see cref="DecoderOptions.FormatOptions"/>. Without it the decoder
/// reads the primary item. Load, <c>Image.Identify</c> and <c>Image.ReadMetadata</c> all describe the selected item.
/// </summary>
/// <example>
/// <code>
/// using var second = Image.Load("burst.heic", new DecoderOptions { FormatOptions = [new HeifDecoderOptions { ItemId = 2 }] });
/// </code>
/// </example>
public sealed record HeifDecoderOptions : FormatDecoderOptions
{
    /// <summary>
    /// The identifier of the image item (its <c>infe</c> item ID, as <c>heif-info</c> prints it), or null for the
    /// primary item. Any image item can be selected: another top-level image, a thumbnail, a grid tile or an
    /// auxiliary image such as a depth or gain map. The item is decoded with its own transforms, colour properties,
    /// alpha (an auxiliary alpha image that refers to it), EXIF and XMP (items that describe it) and, with
    /// <see cref="DecoderOptions.LoadGainMap"/>, the gain map derived from it. An identifier that is not in the file, or
    /// an item that is not an image (EXIF, XMP), throws <see cref="ImageFormatException"/>. Default: null.
    /// </summary>
    public uint? ItemId
    {
        get;
        init => field = value is not 0 ? value : throw new ArgumentOutOfRangeException(nameof(ItemId), value, "HEIF item identifiers start at 1.");
    }
}
