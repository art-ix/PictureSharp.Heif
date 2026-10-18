using System.Buffers;
using System.Drawing;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif;

/// <summary>
/// An immutable catalogue of a HEIF still-image container. Opening copies the input and validates its structure
/// without decoding HEVC pixels or decompressing XMP. The document retains no file handle.
/// </summary>
public sealed class HeifDocument
{
    private readonly ReadOnlyMemory<byte> data;
    private readonly Dictionary<uint, HeifItemInfo> byId;

    private HeifDocument(byte[] data, HeifDocumentOptions options, CancellationToken cancellationToken)
    {
        this.data = data;
        HeifContainer container = HeifContainer.OpenCatalog(data, new DecoderOptions { MaxInputBytes = options.MaxInputBytes }, cancellationToken);
        PrimaryItemId = container.PrimaryItemId;
        (HeifItemInfo[] items, HashSet<uint> topLevel) = container.Catalog();
        Items = Array.AsReadOnly(items);
        TopLevelImages = Array.AsReadOnly(items.Where(item => topLevel.Contains(item.Id)).ToArray());
        byId = items.ToDictionary(item => item.Id);
    }

    /// <summary>The primary identifier declared by pitm, independent of decoder ItemId options.</summary>
    public uint PrimaryItemId { get; }
    /// <summary>The primary item description, including when it is excluded from TopLevelImages. Does not decode pixels.</summary>
    public HeifItemInfo PrimaryImage => byId[PrimaryItemId];
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
        long limit = Math.Min(options.MaxInputBytes, Array.MaxLength);
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(chunk.Length, limit - buffer.Length + 1);
                int read = stream.Read(chunk, 0, count);
                if (read == 0)
                {
                    break;
                }

                AppendInput(buffer, chunk.AsSpan(0, read), options);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new HeifDocument(buffer.ToArray(), options, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Copies and opens encoded bytes; the caller may subsequently change or release the input buffer.</summary>
    /// <remarks>Pixel and metadata budgets are supplied separately to each LoadImage or LoadPreview call.</remarks>
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
        long limit = Math.Min(options.MaxInputBytes, Array.MaxLength);
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(chunk.Length, limit - buffer.Length + 1);
                int read = await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                AppendInput(buffer, chunk.AsSpan(0, read), options);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new HeifDocument(buffer.ToArray(), options, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Finds an item by identifier.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The identifier is absent from the document.</exception>
    public HeifItemInfo GetItem(uint itemId) => byId.TryGetValue(itemId, out HeifItemInfo? item)
        ? item : throw new ArgumentOutOfRangeException(nameof(itemId), itemId, "The item is not in this HEIF document.");

    /// <summary>
    /// Decodes the specified item from the document's owned input. The caller owns the returned image.
    /// Null options use the core defaults; explicit itemId overrides HeifDecoderOptions.ItemId while TargetSize is respected.
    /// HeifFormat must be registered in the supplied or default configuration.
    /// </summary>
    /// <remarks>
    /// Each call has independent pixel and metadata budgets. A MaxInputBytes smaller than the document's buffer rejects
    /// the read. Opening limits and options from earlier reads are never reused or modified.
    /// </remarks>
    public Image LoadImage(uint itemId, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetItem(itemId).IsImage)
        {
            throw HeifReader.Error("The selected HEIF item is not an image.");
        }

        options ??= DecoderOptions.Default;
        return Image.Load(data, options with
        {
            FormatOptions = [.. options.FormatOptions.Where(option => option is not HeifDecoderOptions), new HeifDecoderOptions { ItemId = itemId }]
        }, cancellationToken);
    }

    /// <summary>Loads a preview of the primary image; equivalent to LoadPreview(PrimaryItemId, targetSize, options, cancellationToken).</summary>
    public Image LoadPreview(Size targetSize, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
        LoadPreview(PrimaryItemId, targetSize, options, cancellationToken);

    /// <summary>
    /// Loads the smallest supported thumbnail with matching displayed proportions covering the requested size,
    /// or resizes the photograph when none qualifies. Fits inside the positive target dimensions without enlarging.
    /// Explicit itemId and targetSize replace ItemId and TargetSize in this call's options. The caller owns the image.
    /// </summary>
    /// <remarks>
    /// Proportions tolerate one pixel of shorter-side rounding; unknown dimensions cannot select a thumbnail.
    /// Embedded previews may differ in framing, compression and color from the photograph even with matching proportions.
    /// Use LoadImage with TargetSize when the photograph itself is required. Each item retains its own color and transforms.
    /// Unsupported thumbnails are skipped; corrupt data, limit violations and cancellation are reported.
    /// HeifFormat must be registered in the supplied or default configuration. Null options use the core defaults.
    /// </remarks>
    public Image LoadPreview(uint itemId, Size targetSize, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSize.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSize.Height);
        cancellationToken.ThrowIfCancellationRequested();
        HeifItemInfo image = GetItem(itemId);
        if (!image.IsImage)
        {
            throw HeifReader.Error("The selected HEIF item is not an image.");
        }

        options = (options ?? DecoderOptions.Default) with { TargetSize = targetSize };
        Size needed = image.DisplaySize is { } size ? Fit(size, targetSize) : targetSize;
        IEnumerable<HeifItemInfo> candidates = image.ThumbnailItemIds.Select(GetItem)
            .Where(item => item.IsImage && image.DisplaySize is { } photoSize && item.DisplaySize is { } dimensions
                && MatchingProportions(photoSize, dimensions) && dimensions.Width >= needed.Width && dimensions.Height >= needed.Height)
            .OrderBy(item => (long)item.DisplaySize!.Value.Width * item.DisplaySize.Value.Height).ThenBy(item => item.Id);
        foreach (HeifItemInfo candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return LoadImage(candidate.Id, options, cancellationToken);
            }
            catch (NotSupportedException)
            {
                // Only a valid unsupported thumbnail permits another candidate or the full photograph.
            }
        }

        return LoadImage(itemId, options, cancellationToken);
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

    private static void AppendInput(MemoryStream buffer, ReadOnlySpan<byte> chunk, HeifDocumentOptions options)
    {
        CheckLength(buffer.Length + chunk.Length, options);
        if (buffer.Length + chunk.Length > buffer.Capacity)
        {
            long limit = Math.Min(options.MaxInputBytes, Array.MaxLength);
            buffer.Capacity = (int)Math.Min(limit, Math.Max(buffer.Length + chunk.Length, (long)buffer.Capacity * 2));
        }

        buffer.Write(chunk);
    }

    private static void CheckLength(long length, HeifDocumentOptions options)
    {
        if (length > options.MaxInputBytes || length > Array.MaxLength)
        {
            throw HeifReader.Error("Input exceeds MaxInputBytes or the supported buffer size.");
        }
    }
}
