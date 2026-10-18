namespace PictureSharp.Formats.Heif;

/// <summary>Common values of <see cref="HeifItemReference.Type"/>.</summary>
public static class HeifReferenceTypes
{
    /// <summary>dimg: a derived image refers to its ordered sources.</summary>
    public const string DerivedImage = "dimg";
    /// <summary>thmb: a thumbnail refers to its photograph.</summary>
    public const string Thumbnail = "thmb";
    /// <summary>auxl: an auxiliary image refers to the image it accompanies.</summary>
    public const string Auxiliary = "auxl";
    /// <summary>cdsc: a metadata item refers to the image it describes.</summary>
    public const string ContentDescription = "cdsc";
    /// <summary>prem: an image refers to the alpha image its colour samples are premultiplied by.</summary>
    public const string PremultipliedAlpha = "prem";
}
