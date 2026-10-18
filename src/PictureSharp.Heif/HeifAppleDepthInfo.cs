using System.Buffers.Binary;
using System.Drawing;
using PictureSharp.Formats.Heif.Codecs;

namespace PictureSharp.Formats.Heif;

/// <summary>
/// Apple's description of a depth or disparity auxiliary image, read from the item's XMP (<c>apdi</c>,
/// <c>depthData</c> and <c>depthBlurEffect</c> properties). iPhone Portrait photographs store disparity under the
/// generic depth type <c>urn:mpeg:hevc:2015:auxid:2</c>; <see cref="Kind"/> reports what the XMP declares. The object
/// is immutable.
/// </summary>
/// <remarks>
/// Values are reported as stored. Calibration fields (intrinsics, extrinsics, lens distortion) are raw and not used to
/// compute metric depth. <see cref="RenderingParameters"/> is Apple's private rendering blob and is not interpreted.
/// </remarks>
public sealed class HeifAppleDepthInfo
{
    private const string Apdi = "http://ns.apple.com/pixeldatainfo/1.0/";
    private const string DepthData = "http://ns.apple.com/depthData/1.0/";
    private const string DepthBlur = "http://ns.apple.com/depthBlurEffect/1.0/";

    private HeifAppleDepthInfo(HeifAuxiliaryKind kind, int intMin, int intMax, float floatMin, float floatMax)
    {
        Kind = kind;
        IntMinValue = intMin;
        IntMaxValue = intMax;
        FloatMinValue = floatMin;
        FloatMaxValue = floatMax;
    }

    /// <summary><see cref="HeifAuxiliaryKind.Depth"/> or <see cref="HeifAuxiliaryKind.Disparity"/>, from <c>apdi:AuxiliaryImageType</c>.</summary>
    public HeifAuxiliaryKind Kind { get; }

    /// <summary>The stored pixel format as a four-character code (for example <c>L008</c>), or null when absent.</summary>
    public string? StoredFormat { get; private init; }

    /// <summary>The capture's native pixel format as a four-character code (for example <c>hdis</c>, Float16 disparity), or null.</summary>
    public string? NativeFormat { get; private init; }

    /// <summary>The sample value that maps to <see cref="FloatMinValue"/> (<c>apdi:IntMinValue</c>).</summary>
    public int IntMinValue { get; }

    /// <summary>The sample value that maps to <see cref="FloatMaxValue"/> (<c>apdi:IntMaxValue</c>); greater than <see cref="IntMinValue"/>.</summary>
    public int IntMaxValue { get; }

    /// <summary>The smallest native value (<c>apdi:FloatMinValue</c>): disparity in 1/m or depth in metres, as Apple declares.</summary>
    public float FloatMinValue { get; }

    /// <summary>The largest native value (<c>apdi:FloatMaxValue</c>); not below <see cref="FloatMinValue"/>.</summary>
    public float FloatMaxValue { get; }

    /// <summary><c>depthData:Accuracy</c> (<c>relative</c> or <c>absolute</c>), or null.</summary>
    public string? Accuracy { get; private init; }

    /// <summary><c>depthData:Quality</c> (<c>high</c> or <c>low</c>), or null.</summary>
    public string? Quality { get; private init; }

    /// <summary>Whether the map was filtered (holes filled, smoothed) according to <c>depthData:Filtered</c>, or null.</summary>
    public bool? IsFiltered { get; private init; }

    /// <summary><c>depthData:DepthDataVersion</c>, or null.</summary>
    public int? DepthDataVersion { get; private init; }

    /// <summary>The sensor pixel size in millimetres (<c>depthData:PixelSize</c>), or null.</summary>
    public float? PixelSize { get; private init; }

    /// <summary>The 3×3 camera intrinsic matrix in column-major order, as stored; empty when absent or malformed.</summary>
    public IReadOnlyList<float> IntrinsicMatrix { get; private init; } = [];

    /// <summary>The image size the intrinsic matrix refers to, or null.</summary>
    public Size? IntrinsicMatrixReferenceSize { get; private init; }

    /// <summary>The 4×3 extrinsic matrix, as stored; empty when absent or malformed.</summary>
    public IReadOnlyList<float> ExtrinsicMatrix { get; private init; } = [];

    /// <summary>The lens distortion lookup coefficients, as stored; empty when absent or malformed.</summary>
    public IReadOnlyList<float> LensDistortionCoefficients { get; private init; } = [];

    /// <summary>The inverse lens distortion lookup coefficients, as stored; empty when absent or malformed.</summary>
    public IReadOnlyList<float> InverseLensDistortionCoefficients { get; private init; } = [];

    /// <summary>The lens distortion centre in the reference image's pixels, or null.</summary>
    public PointF? LensDistortionCenter { get; private init; }

    /// <summary>The f-number of the simulated aperture (<c>depthBlurEffect:SimulatedAperture</c>), or null.</summary>
    public float? SimulatedAperture { get; private init; }

    /// <summary>
    /// The decoded bytes of <c>depthBlurEffect:RenderingParameters</c>, Apple's private rendering parameters, without
    /// interpretation; empty when absent or not valid base64.
    /// </summary>
    public ReadOnlyMemory<byte> RenderingParameters { get; private init; }

    /// <summary>
    /// Reads the description from an XMP packet. Returns null when the packet is malformed or lacks the declared kind
    /// (<c>depth</c> or <c>disparity</c>) or a valid integer and float range; malformed optional fields are reported as
    /// absent.
    /// </summary>
    public static HeifAppleDepthInfo? FromXmp(ReadOnlySpan<byte> xmp)
    {
        if (XmpProperties.Parse(xmp) is not { } xml)
        {
            return null;
        }

        HeifAuxiliaryKind? kind = xml.Get(Apdi, "AuxiliaryImageType")?.Trim() switch
        {
            "disparity" => HeifAuxiliaryKind.Disparity,
            "depth" => HeifAuxiliaryKind.Depth,
            _ => null
        };
        if (kind is null
            || xml.GetInt(Apdi, "IntMinValue") is not { } intMin || xml.GetInt(Apdi, "IntMaxValue") is not { } intMax
            || xml.GetFloat(Apdi, "FloatMinValue") is not { } floatMin || xml.GetFloat(Apdi, "FloatMaxValue") is not { } floatMax
            || intMin < 0 || intMax <= intMin || floatMax < floatMin)
        {
            return null;
        }

        int? width = xml.GetInt(DepthData, "IntrinsicMatrixReferenceWidth"), height = xml.GetInt(DepthData, "IntrinsicMatrixReferenceHeight");
        float? centerX = xml.GetFloat(DepthData, "LensDistortionCenterOffsetX"), centerY = xml.GetFloat(DepthData, "LensDistortionCenterOffsetY");
        float? aperture = xml.GetFloat(DepthBlur, "SimulatedAperture");
        return new HeifAppleDepthInfo(kind.Value, intMin, intMax, floatMin, floatMax)
        {
            StoredFormat = FourCc(xml.GetInt(Apdi, "StoredFormat")),
            NativeFormat = FourCc(xml.GetInt(Apdi, "NativeFormat")),
            Accuracy = xml.Get(DepthData, "Accuracy")?.Trim(),
            Quality = xml.Get(DepthData, "Quality")?.Trim(),
            IsFiltered = xml.Get(DepthData, "Filtered")?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => null
            },
            DepthDataVersion = xml.GetInt(DepthData, "DepthDataVersion"),
            PixelSize = xml.GetFloat(DepthData, "PixelSize"),
            IntrinsicMatrix = Matrix(xml.GetFloats(DepthData, "IntrinsicMatrix"), 9),
            IntrinsicMatrixReferenceSize = width > 0 && height > 0 ? new Size(width.Value, height.Value) : null,
            ExtrinsicMatrix = Matrix(xml.GetFloats(DepthData, "ExtrinsicMatrix"), 12),
            LensDistortionCoefficients = Array.AsReadOnly(xml.GetFloats(DepthData, "LensDistortionCoefficients") ?? []),
            InverseLensDistortionCoefficients = Array.AsReadOnly(xml.GetFloats(DepthData, "InverseLensDistortionCoefficients") ?? []),
            LensDistortionCenter = centerX is { } x && centerY is { } y ? new PointF(x, y) : null,
            SimulatedAperture = aperture > 0 ? aperture : null,
            RenderingParameters = Base64(xml.Get(DepthBlur, "RenderingParameters")),
        };
    }

    /// <summary>
    /// Maps a stored sample to its native value: <c>FloatMinValue + (sample − IntMinValue) / (IntMaxValue −
    /// IntMinValue) × (FloatMaxValue − FloatMinValue)</c>. Samples outside the integer range extrapolate linearly.
    /// </summary>
    public float ToFloat(int sample) =>
        (float)(FloatMinValue + (double)(sample - IntMinValue) / (IntMaxValue - IntMinValue) * ((double)FloatMaxValue - FloatMinValue));

    /// <summary>Maps stored samples, such as <see cref="HeifDepthImage.Samples"/>, to native values with <see cref="ToFloat(int)"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="samples"/>.</exception>
    public void ToFloat(ReadOnlySpan<ushort> samples, Span<float> destination)
    {
        if (destination.Length < samples.Length)
        {
            throw new ArgumentException("The destination is shorter than the samples.", nameof(destination));
        }

        for (int i = 0; i < samples.Length; i++)
        {
            destination[i] = ToFloat(samples[i]);
        }
    }

    private static string? FourCc(int? code)
    {
        if (code is not { } value)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        foreach (byte b in bytes)
        {
            if (b is < 0x20 or > 0x7e)
            {
                return null;
            }
        }

        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static IReadOnlyList<float> Matrix(float[]? values, int length) =>
        values is { } matrix && matrix.Length == length ? Array.AsReadOnly(matrix) : [];

    private static ReadOnlyMemory<byte> Base64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            return Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return default;
        }
    }
}
