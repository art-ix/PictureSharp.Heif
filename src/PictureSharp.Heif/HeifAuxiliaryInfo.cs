namespace PictureSharp.Formats.Heif;

/// <summary>The recognized role of an auxiliary image; its original type is preserved separately.</summary>
public enum HeifAuxiliaryKind
{
    /// <summary>An auxiliary type the plugin does not recognize.</summary>
    Other,
    /// <summary>A straight or premultiplied alpha plane.</summary>
    Alpha,
    /// <summary>Depth samples; units and calibration are not interpreted.</summary>
    Depth,
    /// <summary>Disparity samples; units and calibration are not interpreted.</summary>
    Disparity,
    /// <summary>An HDR gain map declared by Apple auxC or ISO tmap relationships.</summary>
    GainMap
}

/// <summary>The declared auxiliary role and uninterpreted subtype bytes of an image item.</summary>
public sealed class HeifAuxiliaryInfo
{
    internal HeifAuxiliaryInfo(string? type, ReadOnlySpan<byte> subtype, HeifAuxiliaryKind kind)
    {
        Type = type;
        Subtype = subtype.ToArray();
        Kind = kind;
    }

    /// <summary>The original aux_type, or null for an ISO gain map identified only through tmap.</summary>
    public string? Type { get; }

    /// <summary>The original aux_subtype bytes, without interpretation.</summary>
    public ReadOnlyMemory<byte> Subtype { get; }

    /// <summary>The recognized role. Unknown manufacturer types have the role <see cref="HeifAuxiliaryKind.Other"/>.</summary>
    public HeifAuxiliaryKind Kind { get; }
}
