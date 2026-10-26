namespace PictureSharp.Formats.Heif;

/// <summary>Whether <see cref="HeifEncoderOptions"/> writes the HDR gain map attached to the image.</summary>
public enum HeifGainMapMode
{
    /// <summary>Write an SDR file without the map (the default).</summary>
    None,

    /// <summary>Write the map when one is attached and can be written as ISO 21496-1; otherwise write an SDR file.</summary>
    Preserve,

    /// <summary>Write the map; throw when none is attached or it cannot be written.</summary>
    Require,
}
