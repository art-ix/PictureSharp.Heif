using System.Buffers.Binary;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HeifContainerTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static ImageInfo Identify(byte[] bytes, DecoderOptions? options = null) =>
        Image.Identify(bytes.AsMemory(), (options ?? new DecoderOptions()) with { Configuration = Configuration });

    [Test]
    public async Task Identify_ItemIdSelectsAnotherImageItem()
    {
        // Item 1 (80x60) with an alpha item 2 (40x30 would not match, so both are 80x60) and an Exif item 3.
        byte[] props = Box("ipco", Ispe(80, 60), Hvcc(), Full("auxC", 0, 0, Text("urn:mpeg:hevc:2015:auxid:1")), Ispe(40, 30));
        byte[] bytes = File(properties: props,
            association: Ipma(Association(1, 1, 0x82), Association(2, 1, 0x82, 3), Association(4, 4, 0x82)),
            info: Iinf(Infe(1), Infe(2), Infe(3, "Exif"), Infe(4)), locations: Iloc(4),
            references: Full("iref", 0, 0, Reference("auxl", 2, 1), Reference("thmb", 4, 1)));
        await Assert.That(Identify(bytes).HasAlpha).IsTrue();

        ImageInfo alpha = Identify(bytes, Select(2));
        await Assert.That((alpha.Width, alpha.Height, alpha.HasAlpha)).IsEqualTo((80, 60, false));
        ImageInfo thumbnail = Identify(bytes, Select(4));
        await Assert.That((thumbnail.Width, thumbnail.Height)).IsEqualTo((40, 30));
        // The item count still covers every item; selecting the primary item explicitly changes nothing.
        await Assert.That(thumbnail.ContainerItemCount).IsEqualTo(4);
        await Assert.That(Identify(bytes, Select(1))).IsEqualTo(Identify(bytes));

        await Assert.That(() => Identify(bytes, Select(3))).Throws<ImageFormatException>();
        await Assert.That(() => Identify(bytes, Select(9))).Throws<ImageFormatException>();
        await Assert.That(() => new HeifDecoderOptions { ItemId = 0 }).Throws<ArgumentOutOfRangeException>();
    }

    private static DecoderOptions Select(uint id) => new() { FormatOptions = [new HeifDecoderOptions { ItemId = id }] };

    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task Identify_ReportsStoredProperties(int bits)
    {
        ImageInfo info = Identify(File(bits: bits));
        await Assert.That(info.Width).IsEqualTo(80);
        await Assert.That(info.Height).IsEqualTo(60);
        await Assert.That(info.BitsPerChannel).IsEqualTo(bits);
        await Assert.That(info.FrameCount).IsEqualTo(1);
        await Assert.That(info.ContainerItemCount).IsEqualTo(1);
        await Assert.That(info.HasAlpha).IsFalse();
        await Assert.That(info.Format).IsSameReferenceAs(ImageFormat.Heif);
    }

    [Test]
    public async Task Identify_UsesAssociationOrderForTransforms()
    {
        byte[] props = Box("ipco", Ispe(80, 60), Hvcc(), Box("irot", [1]),
            Box("clap", U32(40), U32(1), U32(20), U32(1), U32(0), U32(1), U32(0), U32(1)), Box("imir", [1]));
        ImageInfo cropFirst = Identify(File(properties: props, association: Ipma(Association(1, 1, 0x82, 4, 3, 5))));
        ImageInfo rotateFirst = Identify(File(properties: props, association: Ipma(Association(1, 1, 0x82, 3, 4, 5))),
            new DecoderOptions { ApplyExifOrientation = true });
        await Assert.That((cropFirst.Width, cropFirst.Height)).IsEqualTo((20, 40));
        await Assert.That((rotateFirst.Width, rotateFirst.Height)).IsEqualTo((40, 20));
        await Assert.That((int)rotateFirst.Orientation).IsEqualTo(1);
    }

    [Test]
    public async Task Identify_AlphaRequiresAuxiliaryTypeAndReference()
    {
        byte[] props = Box("ipco", Ispe(80, 60), Hvcc(), Full("auxC", 0, 0, Text("urn:mpeg:hevc:2015:auxid:1")));
        byte[] bytes = File(properties: props, association: Ipma(Association(1, 1, 0x82), Association(2, 1, 0x82, 3)),
            info: Iinf(Infe(1), Infe(2)), locations: Iloc(2), references: Full("iref", 0, 0, Reference("auxl", 2, 1), Reference("prem", 1, 2)));
        await Assert.That(Identify(bytes).HasAlpha).IsTrue();
        await Assert.That(Identify(bytes).ContainerItemCount).IsEqualTo(2);
        await Assert.That(() => Identify(bytes, new DecoderOptions { MaxPixels = 9599 })).Throws<ImageFormatException>();
        await Assert.That(Identify(bytes, new DecoderOptions { MaxPixels = 9600 }).HasAlpha).IsTrue();
    }

    [Test]
    public async Task Identify_GridChecksTilesAndOutputDimensions()
    {
        byte[] bytes = Grid();
        ImageInfo info = Identify(bytes);
        await Assert.That((info.Width, info.Height)).IsEqualTo((120, 60));
        // Tiles are container items, not frames.
        await Assert.That((info.ContainerItemCount, info.FrameCount)).IsEqualTo((3, 1));
        await Assert.That(() => Identify(bytes, new DecoderOptions { MaxPixels = 16799 })).Throws<ImageFormatException>();
        await Assert.That(Identify(bytes, new DecoderOptions { MaxPixels = 16800 }).BitsPerChannel).IsEqualTo(8);
    }

    private static byte[] Grid() => File(info: Iinf(Infe(1, "grid"), Infe(2), Infe(3)), locations: Iloc(3, 8),
        properties: Box("ipco", Ispe(120, 60), Ispe(80, 60), Hvcc()),
        association: Ipma(Association(1, 1), Association(2, 2, 0x83), Association(3, 2, 0x83)),
        references: Full("iref", 0, 0, Reference("dimg", 1, 2, 3)), payload: [0, 0, 0, 1, 0, 120, 0, 60]);

    [Test]
    public async Task Identify_IdentityUsesSourceDepth()
    {
        byte[] bytes = File(info: Iinf(Infe(1, "iden"), Infe(2)), locations: Iloc(2),
            properties: Box("ipco", Ispe(80, 60), Hvcc(10)),
            association: Ipma(Association(1, 1), Association(2, 1, 0x82)),
            references: Full("iref", 0, 0, Reference("dimg", 1, 2)));
        await Assert.That(Identify(bytes).BitsPerChannel).IsEqualTo(10);
    }

    [Test]
    public async Task Identify_UnknownOptionalPropertyIsIgnored_EssentialIsRejected()
    {
        byte[] props = Box("ipco", Ispe(80, 60), Hvcc(), Box("zzzz", [1, 2, 3]));
        await Assert.That(Identify(File(properties: props)).Width).IsEqualTo(80);
        await Assert.That(() => Identify(File(properties: props, association: Ipma(Association(1, 1, 0x82, 0x83)))))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task Identify_InputAndStoredPixelLimitsApplyBeforeCrop()
    {
        byte[] bytes = File(properties: Box("ipco", Ispe(80, 60), Hvcc(),
            Box("clap", U32(2), U32(1), U32(2), U32(1), U32(0), U32(1), U32(0), U32(1))));
        await Assert.That(() => Identify(bytes, new DecoderOptions { MaxPixels = 4799 })).Throws<ImageFormatException>();
        await Assert.That(() => new HeifFormat().Decoder.Identify(bytes, new DecoderOptions { MaxInputBytes = bytes.Length - 1 }))
            .Throws<ImageFormatException>();
        await Assert.That(Identify(bytes, new DecoderOptions { MaxPixels = 4800 }).Width).IsEqualTo(2);
    }

    [Test]
    public async Task Identify_MetadataBudgetHonoursIgnoreMetadata()
    {
        byte[] bytes = File(properties: Box("ipco", Ispe(80, 60), Hvcc(), Box("colr", "prof"u8.ToArray(), new byte[12])));
        await Assert.That(() => Identify(bytes, new DecoderOptions { MaxMetadataBytes = 11 })).Throws<ImageFormatException>();
        await Assert.That(Identify(bytes, new DecoderOptions { MaxMetadataBytes = 0, IgnoreMetadata = true }).Width).IsEqualTo(80);
    }

    [Test]
    public async Task Identify_ExtendedAndToEndBoxesAreBounded()
    {
        byte[] extended = Join(U32(1), "free"u8.ToArray(), U64(20), new byte[4]);
        await Assert.That(Identify(Join(File(), extended)).Width).IsEqualTo(80);
        await Assert.That(Identify(Join(File(), U32(0), "mdat"u8.ToArray(), new byte[8])).Width).IsEqualTo(80);
        await Assert.That(() => Identify(Join(File(), U32(1), "free"u8.ToArray(), U64(ulong.MaxValue))))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task Identify_CyclesDanglingReferencesAndInvalidAssociationsFail()
    {
        await Assert.That(() => Identify(File(references: Full("iref", 0, 0, Reference("dimg", 1, 1)))))
            .Throws<ImageFormatException>();
        await Assert.That(() => Identify(File(references: Full("iref", 0, 0, Reference("dimg", 1, 7)))))
            .Throws<ImageFormatException>();
        await Assert.That(() => Identify(File(association: Ipma(Association(1, 1, 0x82, 127)))))
            .Throws<ImageFormatException>();
        await Assert.That(() => Identify(File(info: Iinf(Infe(1), Infe(1)))))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task Identify_OffsetOverflowAndOutOfBoundsExtentsFail()
    {
        byte[] overflow = Full("iloc", 1, 0, [0x88, 0x80], U16(1), U16(1), U16(1), U16(0), U64(ulong.MaxValue),
            U16(1), U64(1), U64(4));
        await Assert.That(() => Identify(File(locations: overflow))).Throws<ImageFormatException>();
        await Assert.That(() => Identify(File(locations: Iloc(1, 5)))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Identify_InvalidCleanAperturesFail()
    {
        byte[] zero = Box("clap", U32(40), U32(0), U32(20), U32(1), U32(0), U32(1), U32(0), U32(1));
        byte[] outside = Box("clap", U32(40), U32(1), U32(20), U32(1), U32(80), U32(1), U32(0), U32(1));
        foreach (byte[] crop in new[] { zero, outside })
        {
            await Assert.That(() => Identify(File(properties: Box("ipco", Ispe(80, 60), Hvcc(), crop))))
                .Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task Identify_EveryTruncatedPrefixFailsWithFormatException()
    {
        byte[] bytes = Grid();
        for (int length = 0; length < bytes.Length; length++)
        {
            int size = length;
            await Assert.That(() => new HeifFormat().Decoder.Identify(bytes.AsMemory(0, size), new DecoderOptions()))
                .Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task Load_ContainerWithoutHevcDataFails()
    {
        // The synthetic container carries an empty payload instead of coded HEVC data.
        await Assert.That(() => Image.Load(File().AsSpan(), new DecoderOptions { Configuration = Configuration }))
            .Throws<ImageFormatException>();
    }

    [Test]
    public async Task Identify_SequencesRemainUnsupported()
    {
        await Assert.That(() => Identify(Join(File(), Box("moov")))).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Identify_LocationVersionsAnd64BitFields(int version)
    {
        byte[] fields = Join(version == 2 ? U32(1) : U16(1), version == 0 ? [] : U16(0), U16(0), U64(0), U16(1), U64(0), U64(4));
        byte[] location = Full("iloc", (byte)version, 0, [0x88, 0x80], version == 2 ? U32(1) : U16(1), fields);
        // File-relative extent points at the ftyp bytes. Identify never decodes the HEVC payload.
        ImageInfo info = Identify(File(locations: location));
        await Assert.That(info.Width).IsEqualTo(80);
    }

    [Test]
    public async Task Identify_WideItemIdsAndPropertyIndexes()
    {
        const uint id = 70000;
        byte[] file = Join(Ftyp(), Full("meta", 0, 0, Handler(), Full("pitm", 1, 0, U32(id)),
            Full("iinf", 1, 0, U32(1), Infe((int)id, wide: true)),
            Full("iloc", 2, 0, [0x44, 0], U32(1), U32(id), U16(1), U16(0), U16(1), U32(0), U32(4)),
            Box("iprp", Box("ipco", Ispe(80, 60), Hvcc()), Full("ipma", 1, 1, U32(1), U32(id), [2], U16(1), U16(0x8002))),
            Box("idat", new byte[4])));
        await Assert.That(Identify(file).Height).IsEqualTo(60);
    }

    [Test]
    public async Task Identify_GridHeaderCanSpanMultipleExtents()
    {
        byte[] bytes = Grid();
        // Replace the iloc box with three locations, each split after four bytes.
        byte[] entries = Join(Enumerable.Range(1, 3).Select(id => Join(U16(id), U16(1), U16(0), U16(2),
            U32(0), U32(4), U32(4), U32(4))).ToArray());
        byte[] location = Full("iloc", 1, 0, [0x44, 0], U16(3), entries);
        byte[] replacement = File(info: Iinf(Infe(1, "grid"), Infe(2), Infe(3)), locations: location,
            properties: Box("ipco", Ispe(120, 60), Ispe(80, 60), Hvcc()),
            association: Ipma(Association(1, 1), Association(2, 2, 0x83), Association(3, 2, 0x83)),
            references: Full("iref", 0, 0, Reference("dimg", 1, 2, 3)), payload: [0, 0, 0, 1, 0, 120, 0, 60]);
        await Assert.That(Identify(replacement).Width).IsEqualTo(Identify(bytes).Width);
    }

    [Test]
    public async Task Identify_ReferenceDepthIsBounded()
    {
        const int count = 40;
        byte[][] definitions = Enumerable.Range(1, count).Select(id => Infe(id, id == count ? "hvc1" : "iden")).ToArray();
        byte[][] associations = Enumerable.Range(1, count).Select(id => Association(id, 1, 0x82)).ToArray();
        byte[][] references = Enumerable.Range(1, count - 1).Select(id => Reference("dimg", id, id + 1)).ToArray();
        byte[] bytes = File(info: Iinf(definitions), locations: Iloc(count), association: Ipma(associations),
            properties: Box("ipco", Ispe(80, 60), Hvcc()), references: Full("iref", 0, 0, references));
        await Assert.That(() => Identify(bytes)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Identify_ExifAndXmpShareTheMetadataBudget()
    {
        byte[] xmp = Full("infe", 2, 0, U16(3), U16(0), "mime"u8.ToArray(), Text(""), Text("application/rdf+xml"), Text(""));
        byte[] bytes = File(info: Iinf(Infe(1), Infe(2, "Exif"), xmp), locations: Iloc(3));
        await Assert.That(() => Identify(bytes, new DecoderOptions { MaxMetadataBytes = 7 })).Throws<ImageFormatException>();
        await Assert.That(Identify(bytes, new DecoderOptions { MaxMetadataBytes = 8 }).Width).IsEqualTo(80);
    }

    [Test]
    public async Task Identify_ExtendedFtypIsRecognized()
    {
        byte[] bytes = File();
        uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        byte[] extended = Join(U32(1), "ftyp"u8.ToArray(), U64(length + 8), bytes[8..(int)length], bytes[(int)length..]);
        await Assert.That(new HeifFormat().IsSupported(extended)).IsTrue();
        await Assert.That(Identify(extended).Width).IsEqualTo(80);
    }

    [Test]
    public async Task LoadAsync_ObservesCancellation()
    {
        using var input = new MemoryStream(File());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await Image.LoadAsync(input, new DecoderOptions { Configuration = Configuration }, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task MutatedAndRandomInputsNeverLeakUncheckedExceptions()
    {
        var random = new Random(0x48454946);
        byte[][] seeds = [File(), Grid(), .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "corpus"), "*.heic").Select(System.IO.File.ReadAllBytes)];
        for (int iteration = 0; iteration < 12000; iteration++)
        {
            byte[] bytes = iteration % 3 == 0 ? new byte[random.Next(1024)] : seeds[iteration % seeds.Length].ToArray();
            if (iteration % 3 == 0)
            {
                random.NextBytes(bytes);
            }
            else
            {
                for (int j = 0; j < 1 + iteration % 8; j++)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }
            }

            try
            {
                new HeifFormat().Decoder.Identify(bytes, new DecoderOptions { MaxPixels = 100000, MaxMetadataBytes = 4096 });
            }
            catch (Exception error) when (error is ImageFormatException or NotSupportedException)
            {
                continue;
            }
        }

        await Assert.That(seeds.Length).IsGreaterThan(2);
    }
}
