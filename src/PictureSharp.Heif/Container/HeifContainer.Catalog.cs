using System.Drawing;
using System.Text;

namespace PictureSharp.Formats.Heif.Container;

internal sealed partial class HeifContainer
{
    public (HeifItemInfo[] Items, HashSet<uint> TopLevelIds) Catalog()
    {
        var incoming = new Dictionary<(uint Type, uint Target), List<uint>>();
        var subordinate = new HashSet<uint>();
        var isoMaps = new HashSet<uint>();
        foreach (HeifItem item in items.Values.OrderBy(item => item.Id))
        {
            CheckCancellation();
            foreach ((uint type, List<uint> targets) in item.References)
            {
                foreach (uint target in targets)
                {
                    if (!incoming.TryGetValue((type, target), out List<uint>? sources))
                    {
                        incoming.Add((type, target), sources = []);
                    }

                    sources.Add(item.Id);
                    if (type == HeifFourCc.Dimg && item.Type != HeifFourCc.Tmap)
                    {
                        subordinate.Add(target);
                    }
                }

                if (type is HeifFourCc.Thmb or HeifFourCc.Auxl)
                {
                    subordinate.Add(item.Id);
                }
            }

            if (item.Type == HeifFourCc.Tmap && item.Targets(HeifFourCc.Dimg) is [var image, var map])
            {
                isoMaps.Add(map);
                subordinate.Add(map);
                if (!incoming.TryGetValue((HeifFourCc.Auxl, image), out List<uint>? auxiliaries))
                {
                    incoming.Add((HeifFourCc.Auxl, image), auxiliaries = []);
                }

                auxiliaries.Add(map);
            }
        }

        IEnumerable<uint> Incoming(uint type, uint id) => incoming.TryGetValue((type, id), out List<uint>? ids)
            ? ids.Distinct().Order() : [];
        var bitDepths = new Dictionary<uint, int?>();
        var auxiliaryDescriptions = new Dictionary<(int Index, bool IsoMap), HeifAuxiliaryInfo>();
        var topLevel = new HashSet<uint>();
        var result = new List<HeifItemInfo>();
        foreach (HeifItem item in items.Values.OrderBy(item => item.Id))
        {
            CheckCancellation();
            bool image = IsImageType(item.Type);
            HeifAuxiliaryInfo? auxiliary = CatalogAuxiliary(item, isoMaps.Contains(item.Id), auxiliaryDescriptions);
            if (image && item.Type != HeifFourCc.Tmap && !item.IsHidden && !subordinate.Contains(item.Id) && auxiliary is null)
            {
                topLevel.Add(item.Id);
            }

            result.Add(new HeifItemInfo(item.Id, FourCc(item.Type), item.Name, item.IsHidden, image,
                CatalogSize(item), CatalogBits(item, bitDepths), auxiliary,
                item.References.Select(pair => new HeifItemReference(FourCc(pair.Key), pair.Value)),
                Incoming(HeifFourCc.Thmb, item.Id), Incoming(HeifFourCc.Auxl, item.Id), Incoming(HeifFourCc.Cdsc, item.Id)));
        }

        return (result.ToArray(), topLevel);
    }

    private Size? CatalogSize(HeifItem item)
    {
        if (PropertyOf(item, HeifFourCc.Ispe, supportedOnly: true) is not { } property)
        {
            return null;
        }

        var reader = new HeifReader(property.Data);
        reader.FullBox();
        int width = (int)reader.UInt32(), height = (int)reader.UInt32();
        try
        {
            foreach (HeifTransform transform in Transforms(item))
            {
                switch (transform)
                {
                    case HeifRotation { SwapsDimensions: true }:
                        (width, height) = (height, width);
                        break;
                    case HeifCleanAperture clap:
                        ApplyCleanAperture(clap, ref width, ref height);
                        break;
                }
            }
        }
        catch (NotSupportedException)
        {
            return null;
        }

        return new Size(width, height);
    }

    private int? CatalogBits(HeifItem item, Dictionary<uint, int?> cache)
    {
        if (cache.TryGetValue(item.Id, out int? cached))
        {
            return cached;
        }

        int? bits = null;
        if (PropertyOf(item, HeifFourCc.Pixi, supportedOnly: true) is { } pixi)
        {
            ReadOnlySpan<byte> values = pixi.Data.Span[5..];
            if (values.IndexOfAnyExcept(values[0]) < 0)
            {
                bits = values[0];
            }
        }
        else if (PropertyOf(item, HeifFourCc.HvcC) is { } config)
        {
            bits = 8 + (config.Data.Span[17] & 7);
        }
        else if (item.Type is HeifFourCc.Grid or HeifFourCc.Iden && item.Targets(HeifFourCc.Dimg) is [var first, ..])
        {
            bits = CatalogBits(items[first], cache);
        }

        cache.Add(item.Id, bits);
        return bits;
    }

    private HeifAuxiliaryInfo? CatalogAuxiliary(HeifItem item, bool isoMap,
        Dictionary<(int Index, bool IsoMap), HeifAuxiliaryInfo> cache)
    {
        if (PropertyOf(item, HeifFourCc.AuxC, supportedOnly: true) is not { } property)
        {
            return isoMap ? new HeifAuxiliaryInfo(null, [], HeifAuxiliaryKind.GainMap) : null;
        }

        int index = item.Properties.First(association => Property(association).Type == HeifFourCc.AuxC
            && !unsupportedProperties.Contains(association.Index)).Index;
        if (cache.TryGetValue((index, isoMap), out HeifAuxiliaryInfo? cached))
        {
            return cached;
        }

        var reader = new HeifReader(property.Data);
        reader.FullBox();
        string type = Encoding.UTF8.GetString(reader.String().Span);
        HeifAuxiliaryKind kind = type switch
        {
            "urn:mpeg:hevc:2015:auxid:1" or "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha" => HeifAuxiliaryKind.Alpha,
            "urn:mpeg:hevc:2015:auxid:2" or "urn:mpeg:mpegB:cicp:systems:auxiliary:depth" or "urn:com:apple:photo:2018:aux:depth" => HeifAuxiliaryKind.Depth,
            "urn:com:apple:photo:2018:aux:disparity" => HeifAuxiliaryKind.Disparity,
            "urn:com:apple:photo:2020:aux:hdrgainmap" => HeifAuxiliaryKind.GainMap,
            _ => isoMap ? HeifAuxiliaryKind.GainMap : HeifAuxiliaryKind.Other
        };
        var result = new HeifAuxiliaryInfo(type, reader.Read(reader.Remaining).Span, kind);
        cache.Add((index, isoMap), result);
        return result;
    }

    private static bool IsImageType(uint type) => type is HeifFourCc.Hvc1 or HeifFourCc.Grid or HeifFourCc.Iden
        or HeifFourCc.Tmap or 0x61763031 /* av01 */ or 0x6a706567 /* jpeg */ or 0x6a326b31 /* j2k1 */
        or 0x756e6369 /* unci */ or 0x696f766c /* iovl */;

    private static string FourCc(uint value) => string.Create(4, value, static (span, code) =>
    {
        for (int i = 0; i < 4; i++)
        {
            span[i] = (char)((code >> (24 - i * 8)) & 255);
        }
    });
}
