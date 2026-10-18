using System.Drawing;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;
using static PictureSharp.Formats.Heif.Tests.HeifDocumentFixture;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HeifDocumentTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static DecoderOptions Options => new() { Configuration = Configuration };

    [Test]
    public async Task Catalog_PreservesNamesHiddenRolesAndReferenceDirections_WithoutDecoding()
    {
        byte[] bytes = Build(
            new Item(1, "grid", [Ispe(80, 60)], References: [("dimg", [2])], Name: "Zdjęcie"),
            new Item(2, Hidden: true),
            new Item(3, Properties: [Ispe(40, 30), Hvcc()], References: [("thmb", [1])]),
            new Item(4, Properties: [Ispe(80, 60), Hvcc(), Auxiliary("urn:mpeg:hevc:2015:auxid:1")], References: [("auxl", [1])]),
            new Item(5, Properties: [Ispe(8, 6), Hvcc(), Auxiliary("vendor:unknown", 1, 2, 255)], References: [("auxl", [1])]),
            new Item(6, "Exif", [], References: [("cdsc", [1])]),
            new Item(7), new Item(8, "zzzz", [], Name: "unknown"));
        HeifDocument doc = HeifDocument.Open(bytes);
        await Assert.That(doc.PrimaryImage.Id).IsEqualTo(1u);
        await Assert.That(doc.Items.Count).IsEqualTo(8);
        await Assert.That(doc.TopLevelImages.Select(item => item.Id)).IsEquivalentTo(new uint[] { 1, 7 }, CollectionOrdering.Matching);
        HeifItemInfo photo = doc.GetItem(1);
        await Assert.That(photo.Name).IsEqualTo("Zdjęcie");
        await Assert.That(photo.DisplaySize).IsEqualTo(new Size(80, 60));
        await Assert.That(photo.SourceImages.Select(item => item.Id)).IsEquivalentTo(new uint[] { 2 }, CollectionOrdering.Matching);
        await Assert.That(photo.SourceImages[0]).IsSameReferenceAs(doc.GetItem(2));
        await Assert.That(photo.Type).IsEqualTo(HeifItemTypes.Grid);
        await Assert.That(photo.References.Single().Type).IsEqualTo(HeifReferenceTypes.DerivedImage);
        await Assert.That(photo.Thumbnails.Select(item => item.Id)).IsEquivalentTo(new uint[] { 3 }, CollectionOrdering.Matching);
        await Assert.That(photo.AuxiliaryImages.Select(item => item.Id)).IsEquivalentTo(new uint[] { 4, 5 }, CollectionOrdering.Matching);
        await Assert.That(photo.MetadataItems.Select(item => item.Id)).IsEquivalentTo(new uint[] { 6 }, CollectionOrdering.Matching);
        await Assert.That(doc.GetItem(2).IsHidden).IsTrue();
        await Assert.That(doc.GetItem(2).BitsPerChannel).IsEqualTo(8);
        await Assert.That(doc.GetItem(5).Auxiliary!.Type).IsEqualTo("vendor:unknown");
        await Assert.That(doc.GetItem(5).Auxiliary!.Subtype.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 255 }, CollectionOrdering.Matching);
        await Assert.That(doc.GetItem(5).Auxiliary!.Kind).IsEqualTo(HeifAuxiliaryKind.Other);
        await Assert.That(doc.GetItem(8).DisplaySize).IsNull();
        await Assert.That(doc.GetItem(8).IsImage).IsFalse();
        await Assert.That(doc.Items is IList<HeifItemInfo> list && list.IsReadOnly).IsTrue();
        await Assert.That(() => doc.GetItem(99)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments("urn:mpeg:hevc:2015:auxid:2", HeifAuxiliaryKind.Depth)]
    [Arguments("urn:com:apple:photo:2018:aux:depth", HeifAuxiliaryKind.Depth)]
    [Arguments("urn:com:apple:photo:2018:aux:disparity", HeifAuxiliaryKind.Disparity)]
    [Arguments("urn:com:apple:photo:2020:aux:hdrgainmap", HeifAuxiliaryKind.GainMap)]
    public async Task Catalog_ClassifiesAuxiliaryWithoutInterpretingSubtype(string type, HeifAuxiliaryKind expected)
    {
        HeifDocument doc = HeifDocument.Open(Build(new Item(1), new Item(2,
            Properties: [Ispe(80, 60), Hvcc(10), Auxiliary(type, 9, 7)], References: [("auxl", [1])])));
        await Assert.That(doc.GetItem(2).Auxiliary!.Kind).IsEqualTo(expected);
        await Assert.That(doc.GetItem(2).BitsPerChannel).IsEqualTo(10);
        await Assert.That(doc.GetItem(2).Auxiliary!.Subtype.ToArray()).IsEquivalentTo(new byte[] { 9, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("gainmap-iso.heic")]
    [Arguments("gainmap-apple.heic")]
    [Arguments("gainmap-both-rotated.heic")]
    public async Task Catalog_GainMapDoesNotHideItsBaseOrBecomeAnotherPhoto(string name)
    {
        HeifDocument doc = HeifDocument.Open(System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", name)));
        await Assert.That(doc.TopLevelImages.Select(item => item.Id)).IsEquivalentTo(new[] { doc.PrimaryImage.Id }, CollectionOrdering.Matching);
        HeifItemInfo map = doc.PrimaryImage.AuxiliaryImages.Single(item => item.Auxiliary?.Kind == HeifAuxiliaryKind.GainMap);
        await Assert.That(map.Auxiliary!.Kind).IsEqualTo(HeifAuxiliaryKind.GainMap);
    }

    [Test]
    public async Task Catalog_UnsupportedAdditionalImagesDoNotPreventPrimaryDecoding()
    {
        Item primary = Encoded(1, 80, 60);
        foreach (Item unsupported in new[]
        {
            new Item(2, "jpeg"), new Item(2, Protection: 1), new Item(2, Method: 2), new Item(2, DataReference: 1),
            new Item(2, Properties: [Ispe(80, 60), Hvcc(), Full("auxC", 1, 0, Text("vendor:v2"))], References: [("auxl", [1])]),
            new Item(2, Properties: [Ispe(80, 60), Hvcc(), Box("xxxx", [1])])
        })
        {
            byte[] bytes = Build(primary, unsupported);
            // Make the last unknown property essential.
            if (unsupported.Properties?.Length == 3 && unsupported.References is null)
            {
                // This case has primary properties before it, so find the actual final association instead.
                int pos = bytes.AsSpan().IndexOf(Association(2, (byte)(primary.Properties!.Length + 1), (byte)(primary.Properties.Length + 2), (byte)(primary.Properties.Length + 3)));
                bytes[pos + 5] |= 0x80;
            }

            HeifDocument doc = HeifDocument.Open(bytes);
            await Assert.That(doc.Items.Count).IsEqualTo(2);
            using Image preview = doc.LoadPreview(doc.GetItem(1), new Size(40, 30), Options);
            await Assert.That(preview.Size).IsEqualTo(new Size(40, 30));
            await Assert.That(() => doc.LoadPreview(doc.GetItem(2), new Size(40, 30), Options)).Throws<NotSupportedException>();
        }
    }

    [Test]
    public async Task Preview_ChoosesSmallestCoveringThumbnail_WithoutDecodingThePhoto()
    {
        Item small = Encoded(2, 40, 30, [("thmb", [1])]);
        Item medium = Encoded(3, 80, 60, [("thmb", [1])]);
        Item large = Encoded(4, 120, 90, [("thmb", [1])]);
        HeifDocument doc = HeifDocument.Open(Build(new Item(1, Properties: [Ispe(160, 120), Hvcc()]), small, medium, large));
        foreach ((int width, int height, Item chosen) in new[] { (20, 15, small), (60, 45, medium), (100, 75, large) })
        {
            using Image actual = doc.LoadPreview(doc.GetItem(1), new Size(width, height), Options);
            using Image expected = Image.Load(Build(chosen with { Id = 1, References = null }), Options with { TargetSize = new Size(width, height) });
            await Assert.That(HeifDecodeTests.RgbaHash(actual)).IsEqualTo(HeifDecodeTests.RgbaHash(expected));
        }

        await Assert.That(() => doc.LoadPreview(doc.GetItem(1), new Size(150, 120), Options)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Preview_UnsupportedThumbnailFallsBack_ButCorruptThumbnailDoesNot()
    {
        Item primary = Encoded(1, 80, 60);
        Item unsupported = new(2, "jpeg", [Ispe(40, 30)], References: [("thmb", [1])]);
        HeifDocument doc = HeifDocument.Open(Build(primary, unsupported));
        using Image preview = doc.LoadPreview(doc.GetItem(1), new Size(20, 15), Options);
        await Assert.That(preview.Size).IsEqualTo(new Size(20, 15));
        HeifDocument broken = HeifDocument.Open(Build(primary, unsupported with { Type = "hvc1", Properties = [Ispe(40, 30), Hvcc()] }));
        await Assert.That(() => broken.LoadPreview(broken.GetItem(1), new Size(20, 15), Options)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Preview_TieUsesLowerId_AndOverridesReadItemIdAndTargetSize()
    {
        Item first = Encoded(2, 40, 30, [("thmb", [1])]);
        Item next = Encoded(3, 40, 30, [("thmb", [1])], solid: true);
        HeifDocument doc = HeifDocument.Open(Build(new Item(1), next, first));
        var decodeOptions = Options with { TargetSize = new Size(1, 1), FormatOptions = [new HeifDecoderOptions { ItemId = 99 }] };
        using Image preview = doc.LoadPreview(doc.GetItem(1), new Size(20, 15), decodeOptions);
        await Assert.That(preview.Size).IsEqualTo(new Size(20, 15));
        await Assert.That(doc.PrimaryImage.Id).IsEqualTo(1u);
        using Image expected = Image.Load(Build(first with { Id = 1, References = null }), Options with { TargetSize = new Size(20, 15) });
        await Assert.That(HeifDecodeTests.RgbaHash(preview)).IsEqualTo(HeifDecodeTests.RgbaHash(expected));
    }

    [Test]
    public async Task Preview_UsesNextSupportedCandidateAndOwnTransformsWithoutDoubleRotation()
    {
        Item thumbnail = Encoded(3, 41, 23, [("thmb", [1])]);
        thumbnail = thumbnail with { Properties = [.. thumbnail.Properties!, Box("irot", [3]), Box("imir", [1])] };
        byte[] bytes = Build(new Item(1, Properties: [Ispe(46, 82), Hvcc()]),
            new Item(2, "jpeg", [Ispe(23, 41)], References: [("thmb", [1])]), thumbnail);
        var options = Options with { ApplyExifOrientation = true };
        HeifDocument doc = HeifDocument.Open(bytes);
        using Image preview = doc.LoadPreview(doc.GetItem(1), new Size(23, 41), options);
        using Image direct = Image.Load(bytes, options with { FormatOptions = [new HeifDecoderOptions { ItemId = 3 }] });
        using Image withoutExif = Image.Load(bytes, options with { ApplyExifOrientation = false, FormatOptions = [new HeifDecoderOptions { ItemId = 3 }] });
        await Assert.That(preview.Size).IsEqualTo(new Size(23, 41));
        await Assert.That(HeifDecodeTests.RgbaHash(preview)).IsEqualTo(HeifDecodeTests.RgbaHash(direct));
        await Assert.That(HeifDecodeTests.RgbaHash(preview)).IsEqualTo(HeifDecodeTests.RgbaHash(withoutExif));
    }

    [Test]
    public async Task MultiplePhotosShareSourcesWithoutMixingMetadataOrBudgets()
    {
        Item source = Encoded(3, 80, 60) with { Hidden = true };
        byte[] xmp1 = "<first/>"u8.ToArray(), xmp2 = "<second-photo-with-more-metadata/>"u8.ToArray();
        byte[] bytes = Build(new Item(1, "iden", [Ispe(80, 60)], References: [("dimg", [3])]),
            new Item(2, "iden", [Ispe(80, 60)], References: [("dimg", [3])]), source,
            new Item(4, "mime", [], xmp1, References: [("cdsc", [1])]),
            new Item(5, "mime", [], xmp2, References: [("cdsc", [2])]));
        HeifDocument doc = HeifDocument.Open(bytes);
        await Assert.That(doc.TopLevelImages.Select(item => item.Id)).IsEquivalentTo(new uint[] { 1, 2 }, CollectionOrdering.Matching);
        using Image first = doc.LoadPreview(doc.GetItem(1), new Size(80, 60), Options with { MaxMetadataBytes = xmp1.Length });
        await Assert.That(first.Metadata.Xmp!).IsEquivalentTo(xmp1, CollectionOrdering.Matching);
        await Assert.That(() => doc.LoadPreview(doc.GetItem(2), new Size(80, 60), Options with { MaxMetadataBytes = xmp1.Length })).Throws<ImageFormatException>();
        HeifDocument reopened = HeifDocument.Open(bytes);
        using Image second = reopened.LoadPreview(reopened.GetItem(2), new Size(80, 60), Options);
        await Assert.That(second.Metadata.Xmp!).IsEquivalentTo(xmp2, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Open_CopiesMemory_LeavesStreamsOpen_AndChecksInputLimits()
    {
        byte[] bytes = Build(Encoded(1, 80, 60));
        HeifDocument doc = HeifDocument.Open(bytes, new HeifDocumentOptions { MaxInputBytes = bytes.Length });
        byte[] original = bytes.ToArray();
        Array.Clear(bytes);
        using Image preview = doc.LoadPreview(doc.GetItem(1), new Size(40, 30), Options);
        await Assert.That(preview.Size).IsEqualTo(new Size(40, 30));
        using var stream = new ShortStream(original);
        await Assert.That(HeifDocument.Open(stream).Items.Count).IsEqualTo(1);
        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(() => HeifDocument.Open(original, new HeifDocumentOptions { MaxInputBytes = original.Length - 1 })).Throws<ImageFormatException>();
        using var tooLong = new ShortStream(original);
        await Assert.That(() => HeifDocument.Open(tooLong, new HeifDocumentOptions { MaxInputBytes = original.Length - 1 })).Throws<ImageFormatException>();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".heic");
        try
        {
            System.IO.File.WriteAllBytes(path, original);
            HeifDocument fromFile = HeifDocument.Open(path);
            System.IO.File.Delete(path);
            using Image fromDeleted = fromFile.LoadPreview(fromFile.GetItem(1), new Size(40, 30), Options);
            await Assert.That(fromDeleted.Size).IsEqualTo(preview.Size);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public async Task Preview_StoredPixelBudgetAndCancellationAreEnforced()
    {
        byte[] bytes = Build(Encoded(1, 80, 60));
        HeifDocument doc = HeifDocument.Open(bytes);
        await Assert.That(() => doc.LoadPreview(doc.GetItem(1), new Size(1, 1), Options with { MaxPixels = 4799 })).Throws<ImageFormatException>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.That(() => HeifDocument.Open(bytes, cancellationToken: cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => doc.LoadPreview(doc.GetItem(1), new Size(1, 1), Options, cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => doc.LoadPreview(doc.GetItem(1), new Size(0, 1), Options)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Catalog_DoesNotDecompressXmp_AndPreservesIndependentMetadataRelations()
    {
        using Image image = HeifEncodeTests.Sample(16, 16);
        image.Metadata.SetBlock(MetadataKinds.Xmp, "<original/>"u8);
        byte[] bytes = image.ToHeic(new HeifEncoderOptions { Configuration = Configuration });
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            zlib.Write(new byte[1 << 20]);
        }

        byte[] file = HeifTestFile.WithEncodedXmp(bytes, compressed.ToArray(), "deflate");
        HeifDocument doc = HeifDocument.Open(file);
        await Assert.That(doc.PrimaryImage.MetadataItems.Count).IsEqualTo(1);
        await Assert.That(() => doc.LoadPreview(doc.PrimaryImage, new Size(8, 8), Options with { MaxMetadataBytes = 0 })).Throws<ImageFormatException>();
        HeifDocument reopened = HeifDocument.Open(file);
        using Image ignored = reopened.LoadPreview(reopened.PrimaryImage, new Size(8, 8), Options with { IgnoreMetadata = true, MaxMetadataBytes = 0 });
        await Assert.That(ignored.Metadata.Xmp).IsNull();
    }

    [Test]
    public async Task Catalog_SharesLargeAuxiliaryDescriptionsAndKeepsThemImmutable()
    {
        byte[] auxiliary = Auxiliary("vendor:large", new byte[65536]);
        // Shared properties in ipma must not multiply a large aux_subtype allocation per item.
        byte[] properties = Box("ipco", Ispe(80, 60), Hvcc(), auxiliary);
        byte[] bytes = HeifTestFile.File(properties: properties, info: Iinf(Infe(1), Infe(2), Infe(3)), locations: Iloc(3),
            association: Ipma(Association(1, 1, 2), Association(2, 1, 2, 3), Association(3, 1, 2, 3)),
            references: Full("iref", 0, 0, Reference("auxl", 2, 1), Reference("auxl", 3, 1)));
        HeifDocument doc = HeifDocument.Open(bytes);
        await Assert.That(doc.GetItem(2).Auxiliary).IsSameReferenceAs(doc.GetItem(3).Auxiliary);
        await Assert.That(doc.GetItem(2).Auxiliary!.Subtype.Length).IsEqualTo(65536);
    }

    [Test]
    public async Task Open_ObservesCancellationDuringStreamRead_AndReadsFromCurrentPosition()
    {
        byte[] file = Build(new Item(1));
        using var input = new MemoryStream([1, 2, 3, .. file]);
        input.Position = 3;
        await Assert.That(HeifDocument.Open(input).Items.Count).IsEqualTo(1);
        using var cts = new CancellationTokenSource();
        using var cancelInput = new CancellingStream(file, cts);
        await Assert.That(() => HeifDocument.Open(cancelInput, cancellationToken: cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(cancelInput.CanRead).IsTrue();
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, Math.Min(count, 7));
            source.Cancel();
            return read;
        }
    }

    [Test]
    public async Task Catalog_UnsupportedPropertyVersionCannotMaskTheSupportedDeclaration()
    {
        byte[] bytes = Build(new Item(1, Properties: [Ispe(80, 60), Hvcc(), Full("pixi", 1, 0), Pixi(8)]));
        HeifDocument doc = HeifDocument.Open(bytes);
        await Assert.That(doc.PrimaryImage.BitsPerChannel).IsEqualTo(8);
        await Assert.That(() => doc.LoadImage(doc.PrimaryImage, Options)).Throws<NotSupportedException>();
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task Catalog_TruncationAndMutationNeverLeakUncheckedExceptions()
    {
        byte[] bytes = Build(new Item(1), new Item(2, Properties: [Ispe(80, 60), Hvcc(), Auxiliary("vendor:type", 1, 2)], References: [("auxl", [1])]));
        for (int length = 0; length < bytes.Length; length++)
        {
            int count = length;
            await Assert.That(() => HeifDocument.Open(bytes.AsMemory(0, count))).Throws<ImageFormatException>();
        }

        var random = new Random(602);
        for (int i = 0; i < 3000; i++)
        {
            byte[] input = bytes.ToArray();
            for (int j = 0; j < 1 + i % 5; j++)
            {
                input[random.Next(input.Length)] = (byte)random.Next(256);
            }

            try
            {
                HeifDocument.Open(input, new HeifDocumentOptions { MaxInputBytes = 4096 });
            }
            catch (Exception error) when (error is ImageFormatException or NotSupportedException)
            {
            }
        }
    }

    [Test]
    public async Task IdentifyAndReadMetadata_DescribeTheSelectedPhoto()
    {
        Item source = Encoded(3, 80, 60) with { Hidden = true };
        byte[] xmp1 = "<first/>"u8.ToArray(), xmp2 = "<second-photo/>"u8.ToArray();
        byte[] bytes = Build(new Item(1, "iden", [Ispe(80, 60)], References: [("dimg", [3])]),
            new Item(2, "iden", [Ispe(80, 60), Box("irot", [1])], References: [("dimg", [3])]), source,
            new Item(4, "mime", [], xmp1, References: [("cdsc", [1])]),
            new Item(5, "mime", [], xmp2, References: [("cdsc", [2])]));
        HeifDocument doc = HeifDocument.Open(bytes);
        HeifItemInfo second = doc.GetItem(2);
        ImageInfo info = doc.Identify(second, Options with { MaxInputBytes = 1 });
        await Assert.That((info.Width, info.Height)).IsEqualTo((60, 80));
        await Assert.That(second.DisplaySize).IsEqualTo(new Size(60, 80));
        await Assert.That(doc.ReadMetadata(second, Options).Xmp!).IsEquivalentTo(xmp2, CollectionOrdering.Matching);
        await Assert.That(doc.ReadMetadata(doc.PrimaryImage, Options).Xmp!).IsEquivalentTo(xmp1, CollectionOrdering.Matching);
        await Assert.That(() => doc.ReadMetadata(second, Options with { MaxMetadataBytes = xmp1.Length })).Throws<ImageFormatException>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.That(() => doc.ReadMetadata(second, Options, cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Reads_RejectNonImagesAndItemsOfOtherDocumentsAsArguments()
    {
        byte[] bytes = Build(Encoded(1, 80, 60), new Item(2, "mime", [], "<x/>"u8.ToArray(), References: [("cdsc", [1])]));
        HeifDocument doc = HeifDocument.Open(bytes), other = HeifDocument.Open(bytes);
        await Assert.That(doc.TryGetItem(2, out HeifItemInfo? metadata)).IsTrue();
        await Assert.That(doc.TryGetItem(9, out _)).IsFalse();
        await Assert.That(metadata!.Type).IsEqualTo(HeifItemTypes.Mime);
        await Assert.That(metadata.IsSupported).IsFalse();
        await Assert.That(doc.PrimaryImage.MetadataItems.Single()).IsSameReferenceAs(metadata);
        await Assert.That(() => doc.LoadImage(metadata, Options)).Throws<ArgumentException>();
        await Assert.That(() => doc.ReadMetadata(metadata, Options)).Throws<ArgumentException>();
        await Assert.That(() => doc.LoadPreview(metadata, new Size(1, 1), Options)).Throws<ArgumentException>();
        await Assert.That(() => doc.Identify(other.PrimaryImage, Options)).Throws<ArgumentException>();
        await Assert.That(() => doc.FindThumbnail(other.PrimaryImage, new Size(1, 1))).Throws<ArgumentException>();
        await Assert.That(() => doc.LoadImage(null!, Options)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task FindThumbnail_SkipsUnsupportedThumbnails_AndNamesWhatLoadPreviewDecodes()
    {
        Item thumbnail = Encoded(3, 40, 30, [("thmb", [1])]);
        HeifDocument doc = HeifDocument.Open(Build(new Item(1, Properties: [Ispe(160, 120), Hvcc()]),
            new Item(2, "jpeg", [Ispe(40, 30)], References: [("thmb", [1])]), thumbnail));
        await Assert.That(doc.GetItem(2).IsSupported).IsFalse();
        await Assert.That(doc.GetItem(3).IsSupported).IsTrue();
        await Assert.That(doc.FindThumbnail(doc.PrimaryImage, new Size(40, 30))).IsSameReferenceAs(doc.GetItem(3));
        await Assert.That(doc.FindThumbnail(doc.PrimaryImage, new Size(41, 31))).IsNull();
        await Assert.That(() => doc.FindThumbnail(doc.PrimaryImage, new Size(0, 1))).Throws<ArgumentOutOfRangeException>();
        using Image preview = doc.LoadPreview(doc.PrimaryImage, new Size(40, 30), Options);
        using Image expected = doc.LoadImage(doc.GetItem(3), Options);
        await Assert.That(HeifDecodeTests.RgbaHash(preview)).IsEqualTo(HeifDecodeTests.RgbaHash(expected));
    }

    private sealed class ShortStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7));
    }
}
