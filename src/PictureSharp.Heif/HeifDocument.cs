using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using PictureSharp.Formats.Heif.Codecs;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif;

/// <summary>
/// An immutable catalogue of a HEIF still-image container. Opening copies the input and validates its structure
/// without decoding HEVC pixels or decompressing XMP. The document retains no file handle.
/// </summary>
/// <remarks>
/// Read methods take a <see cref="HeifItemInfo"/> of this document (from <see cref="PrimaryImage"/>,
/// <see cref="TopLevelImages"/>, <see cref="GetItem"/> or an item's relationships). Each read has independent
/// <see cref="DecoderOptions"/>; <see cref="DecoderOptions.MaxInputBytes"/> is not checked again because
/// <see cref="HeifDocumentOptions.MaxInputBytes"/> already limited the input. HeifFormat must be registered in the
/// supplied or default configuration. Returned images belong to the caller.
/// </remarks>
public sealed class HeifDocument
{
    private const int ChunkSize = 16384;
    private readonly ReadOnlyMemory<byte> data;
    private readonly Dictionary<uint, HeifItemInfo> byId;

    private HeifDocument(byte[] data, HeifDocumentOptions options, CancellationToken cancellationToken)
    {
        this.data = data;
        HeifContainer container = HeifContainer.OpenCatalog(data, new DecoderOptions { MaxInputBytes = options.MaxInputBytes }, cancellationToken);
        (HeifItemInfo[] items, HashSet<uint> topLevel) = container.Catalog();
        byId = items.ToDictionary(item => item.Id);
        foreach (HeifItemInfo item in items)
        {
            item.Link(byId);
        }

        Items = Array.AsReadOnly(items);
        TopLevelImages = Array.AsReadOnly(items.Where(item => topLevel.Contains(item.Id)).ToArray());
        PrimaryImage = byId[container.PrimaryItemId];
    }

    /// <summary>The primary item declared by pitm, including when it is excluded from TopLevelImages.</summary>
    public HeifItemInfo PrimaryImage { get; }
    /// <summary>All items, including metadata, tiles and unsupported types, in identifier order.</summary>
    public IReadOnlyList<HeifItemInfo> Items { get; }
    /// <summary>Visible photographs, excluding tiles, thumbnails, auxiliary images and ISO tmap descriptors.</summary>
    public IReadOnlyList<HeifItemInfo> TopLevelImages { get; }

    /// <summary>Opens a file with the input byte limit and cancellation checked while reading it.</summary>
    public static HeifDocument Open(string path, HeifDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        return Open(input, options, cancellationToken);
    }

    /// <summary>Reads from the current stream position to its end, without seeking or closing the caller's stream.</summary>
    /// <exception cref="ImageFormatException">The input exceeds MaxInputBytes or has a malformed container structure.</exception>
    public static HeifDocument Open(Stream stream, HeifDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new HeifDocumentOptions();
        // Without asynchronous I/O the read completes synchronously.
        byte[] input = ReadAll(stream, options, asynchronous: false, cancellationToken).GetAwaiter().GetResult();
        return new HeifDocument(input, options, cancellationToken);
    }

    /// <summary>Copies and opens encoded bytes; the caller may subsequently change or release the input buffer.</summary>
    public static HeifDocument Open(ReadOnlyMemory<byte> data, HeifDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new HeifDocumentOptions();
        cancellationToken.ThrowIfCancellationRequested();
        CheckLength(data.Length, options);
        return new HeifDocument(data.ToArray(), options, cancellationToken);
    }

    /// <summary>Reads a file with asynchronous I/O, then validates the container without decoding pixels.</summary>
    public static async Task<HeifDocument> OpenAsync(string path, HeifDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return await OpenAsync(input, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads asynchronously from the current position to the end and validates the container, observing cancellation
    /// during I/O and parsing. The caller's stream remains open on success, error and cancellation; seeking is not required.
    /// </summary>
    /// <exception cref="ImageFormatException">The input exceeds MaxInputBytes or has a malformed container structure.</exception>
    public static async Task<HeifDocument> OpenAsync(Stream stream, HeifDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new HeifDocumentOptions();
        byte[] input = await ReadAll(stream, options, asynchronous: true, cancellationToken).ConfigureAwait(false);
        return new HeifDocument(input, options, cancellationToken);
    }

    /// <summary>Finds an item by identifier.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The identifier is absent from the document.</exception>
    public HeifItemInfo GetItem(uint itemId) => TryGetItem(itemId, out HeifItemInfo? item)
        ? item : throw new ArgumentOutOfRangeException(nameof(itemId), itemId, "The item is not in this HEIF document.");

    /// <summary>Finds an item by identifier; false when the document has no such item.</summary>
    public bool TryGetItem(uint itemId, [NotNullWhen(true)] out HeifItemInfo? item) => byId.TryGetValue(itemId, out item);

    /// <summary>
    /// Decodes the image item with its own transforms, colour, alpha, EXIF and XMP. Null options use the core defaults;
    /// the item replaces HeifDecoderOptions.ItemId in options, and TargetSize is respected.
    /// </summary>
    /// <exception cref="ArgumentException">The item is not an image or belongs to another document.</exception>
    /// <exception cref="NotSupportedException">The item uses coding or features the decoder does not support.</exception>
    public Image LoadImage(HeifItemInfo image, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        DecoderOptions read = ReadOptions(image, options);
        cancellationToken.ThrowIfCancellationRequested();
        return Image.Load(data, read, cancellationToken);
    }

    /// <summary>Describes the image item as <c>Image.Identify</c> does, without decoding HEVC pixels.</summary>
    /// <exception cref="ArgumentException">The item is not an image or belongs to another document.</exception>
    public ImageInfo Identify(HeifItemInfo image, DecoderOptions? options = null) => Image.Identify(data, ReadOptions(image, options));

    /// <summary>
    /// Reads the image item's metadata (EXIF, XMP, ICC and colour description) as <c>Image.ReadMetadata</c> does,
    /// without decoding HEVC pixels.
    /// </summary>
    /// <exception cref="ArgumentException">The item is not an image or belongs to another document.</exception>
    public ImageMetadata ReadMetadata(HeifItemInfo image, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        DecoderOptions read = ReadOptions(image, options);
        cancellationToken.ThrowIfCancellationRequested();
        return Image.ReadMetadata(data, read, cancellationToken);
    }

    /// <summary>
    /// Selects the embedded thumbnail that <see cref="LoadPreview"/> decodes: the smallest supported thumbnail of the
    /// image with matching displayed proportions that covers <paramref name="targetSize"/> after fitting the photograph
    /// inside it; null when none qualifies. Does not decode pixels.
    /// </summary>
    /// <remarks>
    /// Proportions tolerate one pixel of shorter-side rounding; unknown dimensions cannot select a thumbnail. Ties
    /// choose the lower identifier. Embedded previews may differ in framing, compression and colour from the photograph.
    /// </remarks>
    /// <exception cref="ArgumentException">The item is not an image or belongs to another document.</exception>
    public HeifItemInfo? FindThumbnail(HeifItemInfo image, Size targetSize)
    {
        CheckImage(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSize.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSize.Height);
        if (image.DisplaySize is not { } photo)
        {
            return null;
        }

        Size needed = Fit(photo, targetSize);
        return image.Thumbnails
            .Where(item => item.IsSupported && item.DisplaySize is { } size && MatchingProportions(photo, size)
                && size.Width >= needed.Width && size.Height >= needed.Height)
            .OrderBy(item => (long)item.DisplaySize!.Value.Width * item.DisplaySize.Value.Height).ThenBy(item => item.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Loads the thumbnail <see cref="FindThumbnail"/> selects, or the photograph when none qualifies, resized to fit
    /// inside the positive target dimensions without enlarging. The selected item replaces ItemId and targetSize
    /// replaces TargetSize in this call's options. Each item retains its own colour and transforms.
    /// </summary>
    /// <remarks>
    /// When the selected thumbnail's bitstream turns out unsupported, the photograph is loaded instead; corrupt data,
    /// limit violations and cancellation are reported. Use <see cref="LoadImage"/> with TargetSize when the photograph
    /// itself is required.
    /// </remarks>
    /// <exception cref="ArgumentException">The item is not an image or belongs to another document.</exception>
    public Image LoadPreview(HeifItemInfo image, Size targetSize, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        HeifItemInfo? thumbnail = FindThumbnail(image, targetSize);
        options = (options ?? DecoderOptions.Default) with { TargetSize = targetSize };
        if (thumbnail is not null)
        {
            try
            {
                return LoadImage(thumbnail, options, cancellationToken);
            }
            catch (NotSupportedException)
            {
                // Only a valid unsupported thumbnail permits falling back to the photograph.
            }
        }

        return LoadImage(image, options, cancellationToken);
    }

    /// <summary>
    /// Reads the raw samples of a depth or disparity auxiliary image (<see cref="HeifAuxiliaryKind.Depth"/> or
    /// <see cref="HeifAuxiliaryKind.Disparity"/>), with its auxiliary type, subtype and metadata. Samples keep the coded
    /// bit depth; grids and the item's own transforms are applied without changing values. Nothing is scaled or
    /// interpreted.
    /// </summary>
    /// <remarks>
    /// Before allocating, the item's stored size and its dependencies are checked against
    /// <see cref="DecoderOptions.MaxPixels"/> and its metadata against <see cref="DecoderOptions.MaxMetadataBytes"/>;
    /// <see cref="DecoderOptions.MaxDegreeOfParallelism"/>, <see cref="DecoderOptions.IgnoreMetadata"/> and cancellation
    /// are observed. Other options, including TargetSize, have no effect.
    /// </remarks>
    /// <exception cref="ArgumentException">The item is not a depth or disparity image, or belongs to another document.</exception>
    /// <exception cref="ImageFormatException">The item is corrupt or exceeds a limit.</exception>
    /// <exception cref="NotSupportedException">The item uses coding or features the decoder does not support.</exception>
    public HeifDepthImage LoadDepth(HeifItemInfo depth, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        DecoderOptions read = ReadOptions(depth, options);
        if (depth.Auxiliary is not { Kind: HeifAuxiliaryKind.Depth or HeifAuxiliaryKind.Disparity } auxiliary)
        {
            throw new ArgumentException($"HEIF item {depth.Id} is not a depth or disparity image.", nameof(depth));
        }

        cancellationToken.ThrowIfCancellationRequested();
        HeifContainer container = HeifContainer.Open(data, read, null, cancellationToken);
        var renderer = new HeifRenderer(read.MaxDegreeOfParallelism, cancellationToken);
        (HeifCanvas<ushort> samples, int bitDepth) = renderer.RenderSamples(container.Node(container.ImageItem, detach: false));
        ReadOnlyMemory<byte> exif = default, xmp = default;
        if (!read.IgnoreMetadata)
        {
            // As Load does: a later block of the same kind replaces an earlier one; malformed EXIF is skipped.
            foreach ((bool isExif, ReadOnlyMemory<byte> block) in container.DescriptiveMetadata(container.ImageItem))
            {
                if (!isExif)
                {
                    xmp = block.ToArray();
                }
                else if (HeifMetadataReader.TryGetTiff(block.Span, out ReadOnlySpan<byte> tiff))
                {
                    exif = tiff.ToArray();
                }
            }
        }

        uint? photo = depth.References.FirstOrDefault(reference => reference.Type == HeifReferenceTypes.Auxiliary)?.TargetItemIds
            is [var target, ..] ? target : null;
        return new HeifDepthImage(depth.Id, photo, auxiliary, samples.Width, samples.Height, bitDepth, samples.Pixels, exif, xmp);
    }

    private DecoderOptions ReadOptions(HeifItemInfo image, DecoderOptions? options)
    {
        CheckImage(image);
        options ??= DecoderOptions.Default;
        return options with
        {
            MaxInputBytes = data.Length,
            FormatOptions = [.. options.FormatOptions.Where(option => option is not HeifDecoderOptions), new HeifDecoderOptions { ItemId = image.Id }]
        };
    }

    private void CheckImage(HeifItemInfo image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!byId.TryGetValue(image.Id, out HeifItemInfo? own) || !ReferenceEquals(own, image))
        {
            throw new ArgumentException("The item belongs to another HEIF document.", nameof(image));
        }

        if (!image.IsImage)
        {
            throw new ArgumentException($"HEIF item {image.Id} ({image.Type}) is not an image.", nameof(image));
        }
    }

    // Compare at the thumbnail's longer side using integer products, so a one-pixel tolerance does not grow with size.
    private static bool MatchingProportions(Size photo, Size thumbnail)
    {
        long photoLong = Math.Max(photo.Width, photo.Height), thumbnailLong = Math.Max(thumbnail.Width, thumbnail.Height);
        return Math.Abs(thumbnail.Width * photoLong - photo.Width * thumbnailLong) <= photoLong
            && Math.Abs(thumbnail.Height * photoLong - photo.Height * thumbnailLong) <= photoLong;
    }

    private static Size Fit(Size source, Size target)
    {
        double scale = Math.Min(1, Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height));
        return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    // Reads up to one byte past the limit, so an oversized stream fails without buffering it whole.
    private static async ValueTask<byte[]> ReadAll(Stream stream, HeifDocumentOptions options, bool asynchronous, CancellationToken cancellationToken)
    {
        long limit = Math.Min(options.MaxInputBytes, Array.MaxLength);
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(chunk.Length, limit - buffer.Length + 1);
                int read = asynchronous
                    ? await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false)
                    : stream.Read(chunk, 0, count);
                if (read == 0)
                {
                    break;
                }

                CheckLength(buffer.Length + read, options);
                if (buffer.Length + read > buffer.Capacity)
                {
                    buffer.Capacity = (int)Math.Min(limit, Math.Max(buffer.Length + read, (long)buffer.Capacity * 2));
                }

                buffer.Write(chunk, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static void CheckLength(long length, HeifDocumentOptions options)
    {
        if (length > options.MaxInputBytes || length > Array.MaxLength)
        {
            throw HeifReader.Error("Input exceeds MaxInputBytes or the supported buffer size.");
        }
    }
}
