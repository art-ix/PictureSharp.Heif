using System.Drawing;
using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// The HDR gain map of the primary image. ISO 21496-1: a tmap item deriving from the primary image and the map
// (ISO/IEC 23008-12:2024 6.6.2.4), whose payload is a version byte and the ISO metadata, with the alternate rendition's
// ICC profile in its colr box. Apple (iOS 14 and later): an auxiliary image of type
// urn:com:apple:photo:2020:aux:hdrgainmap, described by its own XMP and the primary image's EXIF MakerNote. iPhones
// since iOS 18 store both around one map. ISO wins, as in PictureSharp's JPEG reader, and invalid ISO metadata never
// falls back to the Apple model; HdrInfo.Detected reports both. The map is rendered with its own transforms, which match the primary image's.
internal sealed class HeifGainMap
{
    private const uint Tmap = 0x746d6170;
    private const uint DerivedImage = 0x64696d67;
    private const uint Auxiliary = 0x6175786c;

    private readonly HeifContainer container;
    private readonly HeifItem source;

    private HeifGainMap(HeifContainer container, HdrGainMapKinds kind, HeifItem source, HdrGainMapKinds detected)
    {
        this.container = container;
        Kind = kind;
        this.source = source;
        Detected = detected;
    }

    // The selected model.
    public HdrGainMapKinds Kind { get; }

    // Every model present, including the selected one.
    public HdrGainMapKinds Detected { get; }

    public static HeifGainMap? Find(HeifContainer container)
    {
        HeifItem primary = container.PrimaryItem;
        HeifItem? iso = container.Items.Where(item => item.Type == Tmap).OrderBy(item => item.Id)
            .FirstOrDefault(item => container.Sources(item) is [var image, ..] && image == primary);
        HeifItem? apple = container.Referencing(Auxiliary, primary).FirstOrDefault(
            item => container.AuxiliaryType(item).SequenceEqual("urn:com:apple:photo:2020:aux:hdrgainmap"u8));
        HdrGainMapKinds detected = (iso is null ? HdrGainMapKinds.None : HdrGainMapKinds.Iso21496)
            | (apple is null ? HdrGainMapKinds.None : HdrGainMapKinds.Apple);
        return (iso, apple) switch
        {
            ({ } item, _) => new HeifGainMap(container, HdrGainMapKinds.Iso21496, item, detected),
            (null, { } item) => new HeifGainMap(container, HdrGainMapKinds.Apple, item, detected),
            _ => null,
        };
    }

    // The description Identify reports; a map that cannot be used is described, never thrown.
    public HdrInfo Describe()
    {
        try
        {
            (GainMapMetadata metadata, HeifItem map) = Read();
            (int width, int height) = container.DisplaySize(map);
            return metadata.ToHdrInfo(new Size(width, height)) with { Detected = Detected };
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return new HdrInfo { Detected = Detected, UnsupportedReason = ex.Message };
        }
    }

    // Attaches the map to the load, or declares why it cannot be used.
    public void Attach(ImageDecodeContext context)
    {
        try
        {
            (GainMapMetadata metadata, HeifItem map) = Read();
            (int width, int height) = container.DisplaySize(map);
            context.AttachGainMap(metadata, new HeifGainMapImage(container.Node(map, detach: true), new Size(width, height)), Detected);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            context.DeclareGainMap(Detected, ex.Message, corrupt: ex is ImageFormatException);
        }
    }

    private (GainMapMetadata Metadata, HeifItem Map) Read()
    {
        if (Kind == HdrGainMapKinds.Iso21496)
        {
            if (container.Sources(source) is not [_, var isoMap])
            {
                throw HeifReader.Error("A tmap item must derive from a base image and a gain map.");
            }

            ReadOnlySpan<byte> payload = container.ItemData(source).Span;
            if (payload.IsEmpty)
            {
                throw HeifReader.Error("The tmap item is empty.");
            }

            if (payload[0] != 0)
            {
                throw new NotSupportedException($"HEIF tone map item version {payload[0]} is not supported.");
            }

            ReadOnlyMemory<byte>? alternateIcc = container.Icc(source);
            GainMapMetadata iso = GainMapMetadata.ParseIso(payload[1..], container.ChannelCount(isoMap),
                alternateIcc is { } icc ? icc.Span : default);
            return (iso, isoMap);
        }

        ReadOnlySpan<byte> exif = default;
        ReadOnlySpan<byte> xmp = default;
        foreach ((bool isExif, ReadOnlyMemory<byte> data) in container.DescriptiveMetadata(container.PrimaryItem))
        {
            if (isExif && exif.IsEmpty && HeifMetadataReader.TryGetTiff(data.Span, out ReadOnlySpan<byte> tiff))
            {
                exif = tiff;
            }
        }

        foreach ((bool isExif, ReadOnlyMemory<byte> data) in container.DescriptiveMetadata(source))
        {
            if (!isExif && xmp.IsEmpty)
            {
                xmp = data.Span;
            }
        }

        return (GainMapMetadata.ParseApple(exif, xmp), source);
    }
}
