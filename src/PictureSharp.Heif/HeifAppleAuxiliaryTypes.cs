namespace PictureSharp.Formats.Heif;

/// <summary>
/// Apple's auxiliary image types (<see cref="HeifAuxiliaryInfo.Type"/>) found in iPhone photographs. Mattes are 8-bit
/// greyscale images, usually at half the photograph's size, where 255 marks the region; they load with
/// <see cref="HeifDocument.LoadImage"/>.
/// </summary>
public static class HeifAppleAuxiliaryTypes
{
    /// <summary>The Portrait mode subject matte (people in the foreground).</summary>
    public const string PortraitEffectsMatte = "urn:com:apple:photo:2018:aux:portraiteffectsmatte";
    /// <summary>The semantic matte of skin.</summary>
    public const string SkinMatte = "urn:com:apple:photo:2019:aux:semanticskinmatte";
    /// <summary>The semantic matte of hair.</summary>
    public const string HairMatte = "urn:com:apple:photo:2019:aux:semantichairmatte";
    /// <summary>The semantic matte of teeth.</summary>
    public const string TeethMatte = "urn:com:apple:photo:2019:aux:semanticteethmatte";
    /// <summary>The semantic matte of glasses.</summary>
    public const string GlassesMatte = "urn:com:apple:photo:2020:aux:semanticglassesmatte";
    /// <summary>The semantic matte of the sky.</summary>
    public const string SkyMatte = "urn:com:apple:photo:2020:aux:semanticskymatte";
    /// <summary>Apple's HDR gain map (see <see cref="HeifAuxiliaryKind.GainMap"/>).</summary>
    public const string HdrGainMap = "urn:com:apple:photo:2020:aux:hdrgainmap";
}
