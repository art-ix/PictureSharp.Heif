namespace PictureSharp.Formats.Heif.Container;

internal sealed partial class HeifContainer
{
    private void ValidateItems()
    {
        foreach (HeifItem item in items.Values)
        {
            context?.ThrowIfCancellationRequested();
            Require(item.Defined, "Reference to an undefined item.");
            foreach (HeifAssociation association in item.Properties)
            {
                Property(association);
            }

            if (item.Located)
            {
                ReadOnlyMemory<byte> source = ItemSource(item);
                long total = 0;
                foreach (HeifExtent extent in item.Extents)
                {
                    Require(extent.Offset <= (ulong)source.Length && extent.Length <= (ulong)source.Length - extent.Offset,
                        "Item extent is outside its data source.");
                    if (extent.Length == 0)
                    {
                        throw new NotSupportedException("Implicit HEIF item extent lengths are not supported.");
                    }
                    total += (long)extent.Length;
                    Require(total <= data.Length, "Item extents exceed the input size.");
                }

                if (item.Type == 0x45786966 || item.IsXmp)
                {
                    ChargeMetadata(total);
                }
            }

            ValidateGraph(item, 0);
        }
    }

    private int ValidateGraph(HeifItem item, int depth)
    {
        Require(depth <= MaxReferenceDepth, "Item reference nesting exceeds the limit.");
        Require(item.VisitState != 1, "Cyclic item references.");
        if (item.VisitState == 2)
        {
            Require(depth + item.ReferenceDepth <= MaxReferenceDepth, "Item reference nesting exceeds the limit.");
            return item.ReferenceDepth;
        }

        item.VisitState = 1;
        int longest = 0;
        foreach ((uint type, List<uint> targets) in item.References)
        {
            // prem is a reverse relationship to auxl, not a decoding dependency.
            if (type is not (0x64696d67 or 0x6175786c or 0x74686d62 or 0x63647363))
            {
                continue;
            }

            foreach (uint target in targets)
            {
                longest = Math.Max(longest, 1 + ValidateGraph(items[target], depth + 1));
            }
        }

        item.VisitState = 2;
        item.ReferenceDepth = longest;
        return longest;
    }

    private ReadOnlyMemory<byte> ItemSource(HeifItem item)
    {
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
        if (item.Description is { } cached)
        {
            return cached;
        }

        context?.ThrowIfCancellationRequested();
        if (item.Type is not (0x68766331 or 0x67726964 or 0x6964656e)) // hvc1, grid, iden
        {
            throw new NotSupportedException("Unsupported HEIF image item type.");
        }

        int width = 0, height = 0, bits = 0, configurationBits = 0;
        bool configuration = false;
        var unique = new HashSet<uint>();
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (association.Essential && !KnownProperty(property.Type))
            {
                throw new NotSupportedException("Unknown essential HEIF item property.");
            }

            if (KnownProperty(property.Type) && property.Type != 0x636f6c72)
            {
                Require(unique.Add(property.Type), "Duplicate image property.");
            }

            var reader = new HeifReader(property.Data);
            switch (property.Type)
            {
                case 0x69737065:
                    reader.FullBox();
                    width = Dimension(reader.UInt32());
                    height = Dimension(reader.UInt32());
                    break;
                case 0x70697869:
                    reader.FullBox();
                    int channels = reader.Byte();
                    for (int i = 0; i < channels; i++)
                    {
                        int channelBits = reader.Byte();
                        Require(bits == 0 || bits == channelBits, "Inconsistent pixel bit depths.");
                        bits = channelBits;
                    }

                    break;
                case 0x68766343:
                    configuration = true;
                    ReadOnlySpan<byte> config = property.Data.Span;
                    // Identification needs the declared depth, not a pixel decoder for the signalled profile.
                    configurationBits = 8 + (config[17] & 7);

                    break;
            }
        }

        Require(width > 0 && height > 0, "Image item has no spatial extents.");
        if (item.Type == 0x68766331)
        {
            Require(item.Located && configuration, "HEVC image is missing its data or configuration.");
            Require(bits == 0 || bits == configurationBits, "Pixel information disagrees with HEVC configuration.");
            bits = configurationBits;
        }
        else
        {
            Require(item.References.TryGetValue(0x64696d67, out List<uint>? sources) && sources.Count > 0,
                "Derived image has no source images.");
            if (item.Type == 0x6964656e)
            {
                Require(sources!.Count == 1, "Identity image requires exactly one source.");
                HeifImageDescription source = Describe(items[sources[0]], depth + 1);
                Require(width == source.Width && height == source.Height, "Identity image dimensions disagree with its source.");
                Require(bits == 0 || bits == source.BitsPerChannel, "Identity image bit depth disagrees with its source.");
                bits = source.BitsPerChannel;
            }
            else
            {
                bits = DescribeGrid(item, sources!, width, height, bits, depth);
            }
        }

        // Transform properties are applied in association order, not property-container order.
        foreach (HeifAssociation association in item.Properties)
        {
            HeifBox property = Property(association);
            if (property.Type == 0x69726f74 && (property.Data.Span[0] & 1) != 0)
            {
                (width, height) = (height, width);
            }
            else if (property.Type == 0x636c6170)
            {
                ApplyCleanAperture(property.Data, ref width, ref height);
            }
        }

        var result = new HeifImageDescription(width, height, bits);
        item.Description = result;
        return result;
    }

    private int DescribeGrid(HeifItem item, List<uint> sources, int width, int height, int bits, int depth)
    {
        Span<byte> header = stackalloc byte[12];
        ReadItemPrefix(item, header[..8]);
        Require(header[0] == 0 && (header[1] & ~1) == 0, "Invalid grid header.");
        int rows = header[2] + 1, columns = header[3] + 1;
        Require(sources.Count == rows * columns, "Grid reference count does not match its dimensions.");
        int gridWidth, gridHeight;
        if ((header[1] & 1) == 0)
        {
            gridWidth = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
            gridHeight = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
        }
        else
        {
            ReadItemPrefix(item, header);
            gridWidth = Dimension(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[4..]));
            gridHeight = Dimension(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[8..]));
        }

        Require(width == gridWidth && height == gridHeight, "Grid output dimensions disagree with ispe.");
        HeifImageDescription first = Describe(items[sources[0]], depth + 1);
        Require(bits == 0 || bits == first.BitsPerChannel, "Grid bit depth disagrees with its tiles.");
        foreach (uint id in sources)
        {
            HeifImageDescription tile = Describe(items[id], depth + 1);
            Require(tile == first, "Grid tiles have inconsistent dimensions or bit depths.");
        }

        Require((long)first.Width * columns >= width && (long)first.Width * (columns - 1) < width
            && (long)first.Height * rows >= height && (long)first.Height * (rows - 1) < height, "Invalid grid tile coverage.");
        return first.BitsPerChannel;
    }

    private static void ApplyCleanAperture(ReadOnlyMemory<byte> bytes, ref int width, ref int height)
    {
        var reader = new HeifReader(bytes);
        uint widthN = reader.UInt32(), widthD = reader.UInt32();
        uint heightN = reader.UInt32(), heightD = reader.UInt32();
        int horizontalN = unchecked((int)reader.UInt32());
        uint horizontalD = reader.UInt32();
        int verticalN = unchecked((int)reader.UInt32());
        uint verticalD = reader.UInt32();
        if (widthN % widthD != 0 || heightN % heightD != 0)
        {
            throw new NotSupportedException("Fractional-pixel clean apertures are not supported.");
        }

        int croppedWidth = Dimension(widthN / widthD), croppedHeight = Dimension(heightN / heightD);
        Int128 leftN = (Int128)(width - croppedWidth) * horizontalD + 2 * (Int128)horizontalN;
        Int128 topN = (Int128)(height - croppedHeight) * verticalD + 2 * (Int128)verticalN;
        Int128 leftD = 2 * (Int128)horizontalD, topD = 2 * (Int128)verticalD;
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
            if (property.Type == 0x69737065)
            {
                var reader = new HeifReader(property.Data);
                reader.FullBox();
                long pixels = (long)reader.UInt32() * reader.UInt32();
                Require(pixels <= options.MaxPixels - total, "Image and its dependencies exceed MaxPixels.");
                total += pixels;
            }
        }

        if (item.References.TryGetValue(0x64696d67, out List<uint>? sources))
        {
            foreach (uint id in sources)
            {
                ChargePixels(items[id], charged, ref total, depth + 1);
            }
        }
    }
}
