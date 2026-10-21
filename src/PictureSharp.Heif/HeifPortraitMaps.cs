namespace PictureSharp.Formats.Heif;

/// <summary>
/// The depth and portrait matte of a photograph, needed by <see cref="HeifEdits.Portrait"/>. Load them once with
/// <see cref="HeifDocument.LoadPortraitMaps"/> to render several variants of the same photograph.
/// </summary>
public sealed class HeifPortraitMaps : IDisposable
{
    /// <summary>Wraps a depth image and an optional matte; the maps take ownership of the matte and dispose it.</summary>
    /// <param name="depth">
    /// The photograph's depth or disparity image with an Apple description (<see cref="HeifDepthImage.AppleDepth"/>);
    /// its proportions must match the photograph's within a pixel of rounding.
    /// </param>
    /// <param name="matte">
    /// Optional portrait matte (<see cref="HeifAppleAuxiliaryTypes.PortraitEffectsMatte"/>) with the photograph's
    /// proportions; its red channel is read. Without it, focused subjects are kept sharp by disparity alone.
    /// </param>
    public HeifPortraitMaps(HeifDepthImage depth, Image? matte = null)
    {
        ArgumentNullException.ThrowIfNull(depth);
        Depth = depth;
        Matte = matte;
    }

    /// <summary>The depth or disparity samples.</summary>
    public HeifDepthImage Depth { get; }

    /// <summary>The portrait matte, or null when the photograph has none.</summary>
    public Image? Matte { get; }

    /// <summary>Disposes the matte.</summary>
    public void Dispose() => Matte?.Dispose();
}
