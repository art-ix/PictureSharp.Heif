namespace PictureSharp.Formats.Heif.Tests;

// Image.ReadMetadata reads the container only and must report what Load reports.
public sealed class HeifMetadataTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static readonly DecoderOptions Options = new() { Configuration = Configuration };
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static IEnumerable<string> Files() => Directory.GetFiles(Corpus, "*.heic").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task ReadMetadata_MatchesLoad(string file)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Corpus, file));
        ImageMetadata read = Image.ReadMetadata(bytes.AsMemory(), Options);
        using Image image = Image.Load(bytes.AsSpan(), Options);
        ImageMetadata loaded = image.Metadata;
        foreach (MetadataKinds kind in (MetadataKinds[])[MetadataKinds.Icc, MetadataKinds.Exif, MetadataKinds.Xmp])
        {
            await Assert.That(Block(read, kind)).IsEquivalentTo(Block(loaded, kind), CollectionOrdering.Matching).Because(kind.ToString());
        }

        await Assert.That(read.Chromaticities).IsEqualTo(loaded.Chromaticities);
        await Assert.That(read.Gamma).IsEqualTo(loaded.Gamma);
        await Assert.That(read.Orientation).IsEqualTo(loaded.Orientation);
    }

    [Test]
    public async Task ReadMetadata_ReportsTheBlocks()
    {
        ImageMetadata metadata = Image.ReadMetadata(File.ReadAllBytes(Path.Combine(Corpus, "wrap-icc-exif-xmp.heic")).AsMemory(), Options);
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Icc)).IsGreaterThan(0);
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Exif)).IsGreaterThan(0);
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Xmp)).IsGreaterThan(0);
        // irot/imir orient the pixels, so the EXIF orientation 6 is reported as already applied.
        await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
    }

    [Test]
    public async Task ReadMetadata_CorruptContainer_ThrowsFormatException()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Corpus, "wrap-icc-exif-xmp.heic"));
        await Assert.That(() => Image.ReadMetadata(bytes.AsMemory(0, 200), Options)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task EditMetadata_IsNotSupportedYet()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Corpus, "wrap-icc-exif-xmp.heic"));
        await Assert.That(() => Image.EditMetadata(bytes.AsMemory(), metadata => metadata.Xmp = null, Options))
            .Throws<NotSupportedException>();
    }

    private static byte[] Block(ImageMetadata metadata, MetadataKinds kind)
    {
        var block = new byte[metadata.GetBlockLength(kind)];
        metadata.CopyBlockTo(kind, block);
        return block;
    }
}
