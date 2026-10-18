using PictureSharp.Formats.Heif.Codecs;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif;

/// <summary>How an explicitly given photograph and movie relate as an Apple Live Photo.</summary>
public enum LivePhotoPairStatus
{
    /// <summary>Both files carry the same ContentIdentifier.</summary>
    Matched,
    /// <summary>The photograph, the movie or both carry no ContentIdentifier, so the pair cannot be confirmed.</summary>
    MissingIdentifier,
    /// <summary>Both files carry a ContentIdentifier, and they differ.</summary>
    ConflictingIdentifiers
}

/// <summary>
/// Checks whether a photograph (HEIC or JPEG) and a QuickTime movie (MOV) form an Apple Live Photo, by comparing the
/// ContentIdentifier of the photograph's Apple MakerNote with the movie's
/// <c>com.apple.quicktime.content.identifier</c>. File names never confirm a pair, and no directories are searched.
/// </summary>
/// <remarks>
/// The photograph's metadata is read with <c>Image.ReadMetadata</c> and the given options (HeifFormat is added when
/// the configuration lacks it; <see cref="DecoderOptions.IgnoreMetadata"/> is ignored). Only the movie's box headers
/// are read until its moov box, which must fit in <see cref="DecoderOptions.MaxMetadataBytes"/>; media data is
/// skipped, by seeking when the stream supports it. Streams are read from their current position and left open.
/// </remarks>
/// <example>
/// <code>
/// LivePhotoPair pair = LivePhotoPair.Read("IMG_4061.HEIC", "IMG_4061.MOV");
/// bool confirmed = pair.Status == LivePhotoPairStatus.Matched;
/// </code>
/// </example>
public sealed class LivePhotoPair
{
    private LivePhotoPair(string? photo, QuickTimeLivePhoto video)
    {
        PhotoContentIdentifier = photo;
        VideoContentIdentifier = video.ContentIdentifier;
        StillImageTime = video.StillImageTime;
        Status = photo is null || video.ContentIdentifier is null ? LivePhotoPairStatus.MissingIdentifier
            : string.Equals(photo, video.ContentIdentifier, StringComparison.Ordinal) ? LivePhotoPairStatus.Matched
            : LivePhotoPairStatus.ConflictingIdentifiers;
    }

    /// <summary>The result of comparing the identifiers (ordinal, case-sensitive).</summary>
    public LivePhotoPairStatus Status { get; }

    /// <summary>The ContentIdentifier from the photograph's Apple MakerNote (tag 0x0011), or null.</summary>
    public string? PhotoContentIdentifier { get; }

    /// <summary>The movie's <c>com.apple.quicktime.content.identifier</c> metadata value, or null.</summary>
    public string? VideoContentIdentifier { get; }

    /// <summary>
    /// When the photograph was taken within the movie: the presentation time of the first sample of the timed metadata
    /// track declaring <c>com.apple.quicktime.still-image-time</c>, after its edit list; null without that track.
    /// </summary>
    public TimeSpan? StillImageTime { get; }

    /// <summary>Reads both files.</summary>
    /// <exception cref="ImageFormatException">Either file is corrupt, truncated or exceeds a limit.</exception>
    public static LivePhotoPair Read(string photoPath, string videoPath, DecoderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(photoPath);
        ArgumentException.ThrowIfNullOrEmpty(videoPath);
        string? photo = PhotoIdentifier(Image.ReadMetadata(photoPath, PhotoOptions(options), cancellationToken));
        using var video = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return new(photo, ReadVideo(video, options, asynchronous: false, cancellationToken).GetAwaiter().GetResult());
    }

    /// <summary>Reads both streams from their current positions; neither is closed.</summary>
    /// <exception cref="ImageFormatException">Either input is corrupt, truncated or exceeds a limit.</exception>
    public static LivePhotoPair Read(Stream photo, Stream video, DecoderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(video);
        string? identifier = PhotoIdentifier(Image.ReadMetadata(photo, PhotoOptions(options), cancellationToken));
        // Without asynchronous I/O the read completes synchronously.
        return new(identifier, ReadVideo(video, options, asynchronous: false, cancellationToken).GetAwaiter().GetResult());
    }

    /// <summary>Reads both files with asynchronous I/O.</summary>
    /// <exception cref="ImageFormatException">Either file is corrupt, truncated or exceeds a limit.</exception>
    public static async Task<LivePhotoPair> ReadAsync(string photoPath, string videoPath, DecoderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(photoPath);
        ArgumentException.ThrowIfNullOrEmpty(videoPath);
        string? photo = PhotoIdentifier(await Image.ReadMetadataAsync(photoPath, PhotoOptions(options), cancellationToken).ConfigureAwait(false));
        await using var video = new FileStream(videoPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return new(photo, await ReadVideo(video, options, asynchronous: true, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads both streams asynchronously from their current positions; neither is closed.</summary>
    /// <exception cref="ImageFormatException">Either input is corrupt, truncated or exceeds a limit.</exception>
    public static async Task<LivePhotoPair> ReadAsync(Stream photo, Stream video, DecoderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(video);
        string? identifier = PhotoIdentifier(await Image.ReadMetadataAsync(photo, PhotoOptions(options), cancellationToken).ConfigureAwait(false));
        return new(identifier, await ReadVideo(video, options, asynchronous: true, cancellationToken).ConfigureAwait(false));
    }

    private static DecoderOptions PhotoOptions(DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        ImageConfiguration configuration = options.Configuration ?? ImageConfiguration.Default;
        if (!configuration.CanDecode(HeifFormat.Descriptor))
        {
            configuration = configuration.ToBuilder().AddFormat(new HeifFormat()).Build();
        }

        return options with { Configuration = configuration, IgnoreMetadata = false };
    }

    private static string? PhotoIdentifier(ImageMetadata metadata) =>
        metadata.Exif is { } exif ? AppleMakerNote.ContentIdentifier(exif) : null;

    private static ValueTask<QuickTimeLivePhoto> ReadVideo(Stream video, DecoderOptions? options, bool asynchronous,
        CancellationToken cancellationToken) =>
        QuickTimeReader.Read(video, (options ?? DecoderOptions.Default).MaxMetadataBytes, asynchronous, cancellationToken);
}
