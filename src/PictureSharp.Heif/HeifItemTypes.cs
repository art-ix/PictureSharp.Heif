namespace PictureSharp.Formats.Heif;

/// <summary>Common values of <see cref="HeifItemInfo.Type"/>.</summary>
public static class HeifItemTypes
{
    /// <summary>An HEVC-coded image.</summary>
    public const string Hevc = "hvc1";
    /// <summary>A derived image assembled from tiles.</summary>
    public const string Grid = "grid";
    /// <summary>A derived image that applies its own transforms to one source.</summary>
    public const string Identity = "iden";
    /// <summary>An ISO 21496-1 tone-map descriptor that combines a photograph with its gain map.</summary>
    public const string ToneMap = "tmap";
    /// <summary>An EXIF metadata block.</summary>
    public const string Exif = "Exif";
    /// <summary>A MIME-typed block, such as XMP.</summary>
    public const string Mime = "mime";
}
