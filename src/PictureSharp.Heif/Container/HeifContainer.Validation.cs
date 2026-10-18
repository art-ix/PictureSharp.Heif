namespace PictureSharp.Formats.Heif.Container;

internal sealed partial class HeifContainer
{
    private void ValidateItems()
    {
        // Longest reference chain below each item whose references were all checked; an item still being checked is
        // absent from it but in the path, which makes a reference to it a cycle.
        var depths = new Dictionary<HeifItem, int>();
        var path = new HashSet<HeifItem>();
        foreach (HeifItem item in items.Values)
        {
            CheckCancellation();
            Require(item.Defined, "Reference to an undefined item.");
            foreach (HeifAssociation association in item.Properties)
            {
                Property(association);
            }

            if (item.Located && item.ConstructionMethod <= 1 && item.DataReferenceIndex == 0)
            {
                Require(item.ConstructionMethod != 1 || hasItemData, "Item refers to a missing idat box.");
                ReadOnlyMemory<byte> source = item.ConstructionMethod == 1 ? itemData : data;
                long total = 0;
                foreach (HeifExtent extent in item.Extents)
                {
                    Require(extent.Offset <= (ulong)source.Length && extent.Length <= (ulong)source.Length - extent.Offset,
                        "Item extent is outside its data source.");
                    total += (long)extent.Length;
                    Require(total <= data.Length, "Item extents exceed the input size.");
                }
            }

            ValidateGraph(item, 0, depths, path);
        }
    }

    private int ValidateGraph(HeifItem item, int depth, Dictionary<HeifItem, int> depths, HashSet<HeifItem> path)
    {
        Require(depth <= MaxReferenceDepth, "Item reference nesting exceeds the limit.");
        Require(!path.Contains(item), "Cyclic item references.");
        if (depths.TryGetValue(item, out int known))
        {
            Require(depth + known <= MaxReferenceDepth, "Item reference nesting exceeds the limit.");
            return known;
        }

        path.Add(item);
        int longest = 0;
        foreach ((uint type, List<uint> targets) in item.References)
        {
            // prem is a reverse relationship to auxl, not a decoding dependency.
            if (type is not (HeifFourCc.Dimg or HeifFourCc.Auxl or HeifFourCc.Thmb or HeifFourCc.Cdsc))
            {
                continue;
            }

            foreach (uint target in targets)
            {
                longest = Math.Max(longest, 1 + ValidateGraph(items[target], depth + 1, depths, path));
            }
        }

        path.Remove(item);
        depths.Add(item, longest);
        return longest;
    }

    private static void EnsureReadable(HeifItem item)
    {
        if (item.ProtectionIndex != 0 || item.DataReferenceIndex != 0 || item.ConstructionMethod > 1)
        {
            throw new NotSupportedException("Protected, external or item-relative HEIF data is not supported.");
        }

        if (item.Extents.Any(extent => extent.Length == 0))
        {
            throw new NotSupportedException("Implicit HEIF item extent lengths are not supported.");
        }
    }

    private ReadOnlyMemory<byte> ItemSource(HeifItem item)
    {
        EnsureReadable(item);
        Require(item.ConstructionMethod != 1 || hasItemData, "Item refers to a missing idat box.");
        return item.ConstructionMethod == 1 ? itemData : data;
    }

    private void ReadItemPrefix(HeifItem item, Span<byte> destination)
    {
        Require(item.Located, "Missing item location.");
        ReadOnlySpan<byte> source = ItemSource(item).Span;
        int copied = 0;
        foreach (HeifExtent extent in item.Extents)
        {
            int length = (int)Math.Min(extent.Length, (ulong)(destination.Length - copied));
            source.Slice((int)extent.Offset, length).CopyTo(destination[copied..]);
            copied += length;
            if (copied == destination.Length)
            {
                return;
            }
        }

        throw HeifReader.Error("Truncated derived image data.");
    }

    private HeifImageDescription Describe(HeifItem item, int depth)
    {
        Require(depth <= MaxReferenceDepth, "Derived image nesting exceeds the limit.");
        if (descriptions.TryGetValue(item, out HeifImageDescription cached))
        {
            return cached;
        }

        CheckCancellation();
        if (item.Type is not (HeifFourCc.Hvc1 or HeifFourCc.Grid or HeifFourCc.Iden))
        {
            throw new NotSupportedException("Unsupported HEIF image item type.");
        }

        EnsureReadable(item);
        int width = 0, height = 0, bits = 0, configurationBits = 0;
        bool configuration = false;
        var unique = new HashSet<uint>();
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (unsupportedProperties.Contains(association.Index) || association.Essential && !KnownProperty(property.Type))
            {
                throw new NotSupportedException("Unknown essential HEIF item property.");
            }

            if (KnownProperty(property.Type) && property.Type != HeifFourCc.Colr)
            {
                Require(unique.Add(property.Type), "Duplicate image property.");
            }

            var reader = new HeifReader(property.Data);
            switch (property.Type)
            {
                case HeifFourCc.Ispe:
                    reader.FullBox();
                    width = Dimension(reader.UInt32());
                    height = Dimension(reader.UInt32());
                    break;
                case HeifFourCc.Pixi:
                    reader.FullBox();
                    int channels = reader.Byte();
                    for (int i = 0; i < channels; i++)
                    {
                        int channelBits = reader.Byte();
                        Require(bits == 0 || bits == channelBits, "Inconsistent pixel bit depths.");
                        bits = channelBits;
                    }

                    break;
                case HeifFourCc.HvcC:
                    configuration = true;
                    ReadOnlySpan<byte> config = property.Data.Span;
                    // Identification needs the declared depth, not a pixel decoder for the signalled profile.
                    configurationBits = 8 + (config[17] & 7);

                    break;
            }
        }

        Require(width > 0 && height > 0, "Image item has no spatial extents.");
        if (item.Type == HeifFourCc.Hvc1)
        {
            Require(item.Located && configuration, "HEVC image is missing its data or configuration.");
            Require(bits == 0 || bits == configurationBits, "Pixel information disagrees with HEVC configuration.");
            bits = configurationBits;
        }
        else
        {
            IReadOnlyList<uint> sources = item.Targets(HeifFourCc.Dimg);
            Require(sources.Count > 0, "Derived image has no source images.");
            if (item.Type == HeifFourCc.Iden)
            {
                Require(sources.Count == 1, "Identity image requires exactly one source.");
                HeifImageDescription source = Describe(items[sources[0]], depth + 1);
                Require(width == source.Width && height == source.Height, "Identity image dimensions disagree with its source.");
                Require(bits == 0 || bits == source.BitsPerChannel, "Identity image bit depth disagrees with its source.");
                bits = source.BitsPerChannel;
            }
            else
            {
                bits = DescribeGrid(item, sources, width, height, bits, depth);
            }
        }

        // Transform properties are applied in association order, not property-container order.
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

        var result = new HeifImageDescription(width, height, bits);
        descriptions.Add(item, result);
        return result;
    }

    private int DescribeGrid(HeifItem item, IReadOnlyList<uint> sources, int width, int height, int bits, int depth)
    {
        HeifGridLayout grid = GridLayout(item);
        Require(sources.Count == grid.Rows * grid.Columns, "Grid reference count does not match its dimensions.");
        Require(width == grid.Width && height == grid.Height, "Grid output dimensions disagree with ispe.");
        HeifImageDescription first = Describe(items[sources[0]], depth + 1);
        Require(bits == 0 || bits == first.BitsPerChannel, "Grid bit depth disagrees with its tiles.");
        foreach (uint id in sources)
        {
            HeifImageDescription tile = Describe(items[id], depth + 1);
            Require(tile == first, "Grid tiles have inconsistent dimensions or bit depths.");
        }

        Require((long)first.Width * grid.Columns >= width && (long)first.Width * (grid.Columns - 1) < width
            && (long)first.Height * grid.Rows >= height && (long)first.Height * (grid.Rows - 1) < height, "Invalid grid tile coverage.");
        return first.BitsPerChannel;
    }

    private static void ApplyCleanAperture(HeifCleanAperture clap, ref int width, ref int height)
    {
        if (clap.WidthN % clap.WidthD != 0 || clap.HeightN % clap.HeightD != 0)
        {
            throw new NotSupportedException("Fractional-pixel clean apertures are not supported.");
        }

        int croppedWidth = Dimension(clap.WidthN / clap.WidthD), croppedHeight = Dimension(clap.HeightN / clap.HeightD);
        Int128 leftN = (Int128)(width - croppedWidth) * clap.HorizontalOffsetD + 2 * (Int128)clap.HorizontalOffsetN;
        Int128 topN = (Int128)(height - croppedHeight) * clap.VerticalOffsetD + 2 * (Int128)clap.VerticalOffsetN;
        Int128 leftD = 2 * (Int128)clap.HorizontalOffsetD, topD = 2 * (Int128)clap.VerticalOffsetD;
        Require(leftN >= 0 && topN >= 0 && leftN + croppedWidth * leftD <= width * leftD
            && topN + croppedHeight * topD <= height * topD, "Clean aperture lies outside the image.");
        if (leftN % leftD != 0 || topN % topD != 0)
        {
            throw new NotSupportedException("Fractional-pixel clean apertures are not supported.");
        }

        width = croppedWidth;
        height = croppedHeight;
    }

    private void ChargePixels(HeifItem item, HashSet<uint> charged, ref long total, int depth = 0)
    {
        Require(depth <= MaxReferenceDepth, "Pixel dependency nesting exceeds the limit.");
        if (!charged.Add(item.Id))
        {
            return;
        }

        // Charge stored dimensions: a crop must not bypass the pixel budget.
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type == HeifFourCc.Ispe)
            {
                var reader = new HeifReader(property.Data);
                reader.FullBox();
                long pixels = (long)reader.UInt32() * reader.UInt32();
                Require(pixels <= options.MaxPixels - total, "Image and its dependencies exceed MaxPixels.");
                total += pixels;
            }
        }

        foreach (uint id in item.Targets(HeifFourCc.Dimg))
        {
            ChargePixels(items[id], charged, ref total, depth + 1);
        }
    }
}
