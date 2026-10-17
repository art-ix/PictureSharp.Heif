namespace PictureSharp.Formats.Heif.Container;

internal sealed partial class HeifContainer
{
    private void ParseProperties(ReadOnlyMemory<byte> bytes)
    {
        var reader = new HeifReader(bytes);
        bool ipco = false;
        var associationVersions = new HashSet<uint>();
        while (reader.Remaining > 0)
        {
            HeifBox box = NextBox(ref reader);
            if (box.Type == 0x6970636f) // ipco
            {
                Require(!ipco, "Duplicate property container.");
                ipco = true;
                var propertyReader = new HeifReader(box.Data);
                while (propertyReader.Remaining > 0)
                {
                    Require(properties.Count < 32767, "Too many item properties.");
                    HeifBox property = NextBox(ref propertyReader);
                    ValidateProperty(property);
                    properties.Add(property);
                }
            }
            else if (box.Type == 0x69706d61) // ipma
            {
                var associations = new HeifReader(box.Data);
                byte version = associations.FullBox(out uint flags);
                if (version > 1)
                {
                    throw new NotSupportedException("Unsupported property association version.");
                }

                Require(flags <= 1 && associationVersions.Add(((uint)version << 24) | flags), "Invalid or duplicate ipma.");
                uint count = associations.UInt32();
                Require(count <= MaxItems, "Too many property association entries.");
                for (uint i = 0; i < count; i++)
                {
                    HeifItem item = Item(version == 0 ? associations.UInt16() : associations.UInt32());
                    Require(!item.Associated, "Duplicate property associations for an item.");
                    item.Associated = true;
                    int number = associations.Byte();
                    Entries(number);
                    for (int j = 0; j < number; j++)
                    {
                        int value = flags == 0 ? associations.Byte() : associations.UInt16();
                        int essentialBit = flags == 0 ? 0x80 : 0x8000;
                        int index = value & (essentialBit - 1);
                        bool essential = (value & essentialBit) != 0;
                        Require(index != 0 || !essential, "An absent property cannot be essential.");
                        if (index != 0)
                        {
                            item.Properties.Add(new(index, essential));
                        }
                    }
                }

                associations.End();
            }
        }

        Require(ipco, "Missing item property container.");
    }

    private void ValidateProperty(HeifBox property)
    {
        var reader = new HeifReader(property.Data);
        switch (property.Type)
        {
            case 0x69737065: // ispe
                reader.FullBox();
                Dimension(reader.UInt32());
                Dimension(reader.UInt32());
                reader.End();
                break;
            case 0x70697869: // pixi
                reader.FullBox();
                int channels = reader.Byte();
                Require(channels > 0, "Empty pixel information.");
                for (int i = 0; i < channels; i++)
                {
                    Require(reader.Byte() > 0, "Invalid channel bit depth.");
                }

                reader.End();
                break;
            case 0x69726f74: // irot
                Require(reader.Byte() <= 3, "Invalid rotation bits.");
                reader.End();
                break;
            case 0x696d6972: // imir
                Require(reader.Byte() <= 1, "Invalid mirror bits.");
                reader.End();
                break;
            case 0x636c6170: // clap
                for (int i = 0; i < 4; i++)
                {
                    uint numerator = reader.UInt32();
                    Require(i >= 2 || numerator > 0, "Empty clean aperture.");
                    Require(reader.UInt32() != 0, "Zero clean aperture denominator.");
                }

                reader.End();
                break;
            case 0x61757843: // auxC
                reader.FullBox();
                reader.String(); // aux_subtype occupies the remaining bytes.
                break;
            case 0x636f6c72: // colr
                uint colour = reader.UInt32();
                if (colour == 0x6e636c78) // nclx
                {
                    reader.Read(6);
                    Require((reader.Byte() & 0x7f) == 0, "Invalid nclx reserved bits.");
                    reader.End();
                }
                else if (colour is 0x70726f66 or 0x72494343) // prof, rICC
                {
                    ChargeMetadata(reader.Remaining);
                }

                break;
            case 0x68766343: // hvcC, ISO/IEC 14496-15 HEVCDecoderConfigurationRecord
                Require(reader.Byte() == 1, "Invalid HEVC configuration version.");
                reader.Read(12);
                Require((reader.UInt16() & 0xf000) == 0xf000, "Invalid HEVC segmentation reserved bits.");
                Require((reader.Byte() & 0xfc) == 0xfc, "Invalid HEVC parallelism reserved bits.");
                Require((reader.Byte() & 0xfc) == 0xfc, "Invalid HEVC chroma reserved bits.");
                Require((reader.Byte() & 0xf8) == 0xf8, "Invalid HEVC depth reserved bits.");
                Require((reader.Byte() & 0xf8) == 0xf8, "Invalid HEVC depth reserved bits.");
                reader.Read(2);
                reader.Byte();
                int arrays = reader.Byte();
                for (int i = 0; i < arrays; i++)
                {
                    // libheif writers differ in the reserved array-header bit; it does not change NAL framing.
                    reader.Byte();
                    int count = reader.UInt16();
                    Entries(count);
                    for (int j = 0; j < count; j++)
                    {
                        int length = reader.UInt16();
                        Require(length >= 2, "Invalid HEVC parameter set length.");
                        reader.Read(length);
                    }
                }

                reader.End();
                break;
        }
    }

    private HeifBox Property(HeifAssociation association)
    {
        Require(association.Index > 0 && association.Index <= properties.Count, "Property association outside ipco.");
        return properties[association.Index - 1];
    }

    private static bool KnownProperty(uint type) => type is
        0x69737065 or 0x70697869 or 0x69726f74 or 0x696d6972 or 0x636c6170 or 0x61757843 or 0x636f6c72 or 0x68766343;

    private bool IsAlpha(HeifItem item)
    {
        ReadOnlySpan<byte> name = AuxiliaryType(item);
        return name.SequenceEqual("urn:mpeg:hevc:2015:auxid:1"u8) || name.SequenceEqual("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"u8);
    }

    private void ChargeMetadata(long bytes)
    {
        if (options.IgnoreMetadata)
        {
            return;
        }

        Require(bytes <= options.MaxMetadataBytes - metadataBytes, "Metadata exceeds MaxMetadataBytes.");
        context?.ReserveMetadata(bytes);
        metadataBytes += bytes;
    }

    private static int Dimension(uint value)
    {
        Require(value is > 0 and <= int.MaxValue, "Invalid image dimension.");
        return (int)value;
    }
}
