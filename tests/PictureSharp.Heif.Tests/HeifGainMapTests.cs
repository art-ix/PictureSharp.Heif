using System.Drawing;
using System.Text;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

// HDR gain maps in the synthetic corpus: ToHdr must equal the reference format fed with the map heif-dec decodes and
// the metadata the generator wrote, bit for bit.
public sealed class HeifGainMapTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");

    private static readonly Lazy<Entry[]> Manifest = new(() => [.. JsonSerializer.Deserialize<Entry[]>(
        File.ReadAllText(Path.Combine(Corpus, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
        .Where(entry => entry.GainMap is not null)]);

    public static IEnumerable<string> Decodable() => Manifest.Value.Where(entry => entry.GainMap!.MapPng is not null).Select(entry => entry.File);

    private static Entry Find(string file) => Manifest.Value.Single(entry => entry.File == file);

    private static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Corpus, file));

    private static DecoderOptions Options(DecoderOptions? options = null, bool gainMap = true) =>
        (options ?? new DecoderOptions()) with { Configuration = Configuration, LoadGainMap = gainMap };

    private static GainMapMetadata ExpectedMetadata(GainMapEntry gainMap) => gainMap.IsoPayload is { } iso
        ? GainMapMetadata.ParseIso(Convert.FromHexString(iso), gainMap.Channels)
        : GainMapMetadata.ParseApple(Convert.FromHexString(gainMap.AppleExif!), Encoding.UTF8.GetBytes(gainMap.AppleXmp!));

    // The reference for a corpus file: our base pixels (equal to heif-dec, see HeifDecodeTests) and heif-dec's map.
    private static ReferenceGainMapFormat Reference(string file, DecoderOptions? options = null)
    {
        GainMapEntry gainMap = Find(file).GainMap!;
        Image baseImage = Image.Load(Read(file).AsSpan(), Options(options, gainMap: false));
        Image map = Image.Load(Path.Combine(Corpus, gainMap.MapPng!));
        return new ReferenceGainMapFormat(baseImage, ExpectedMetadata(gainMap), map);
    }

    [Test]
    [MethodDataSource(nameof(Decodable))]
    public async Task Identify_DescribesTheGainMap(string file)
    {
        GainMapEntry gainMap = Find(file).GainMap!;
        GainMapInfo? hdr = Image.Identify(Read(file).AsMemory(), Options()).GainMap;
        await Assert.That(hdr).IsNotNull();
        await Assert.That(hdr!.Selected).IsEqualTo(Enum.Parse<HdrGainMapKinds>(gainMap.Kind));
        await Assert.That(hdr.Detected).IsEqualTo(Enum.Parse<HdrGainMapKinds>(gainMap.Detected));
        await Assert.That(hdr.GainMapSize).IsEqualTo(new Size(gainMap.Width, gainMap.Height));
        await Assert.That(hdr.CanDecode).IsTrue();
        await Assert.That(hdr.Headroom).IsEqualTo(ExpectedMetadata(gainMap).Headroom);
        await Assert.That(Image.Identify(Read(file).AsMemory(), Options(gainMap: false)).GainMap).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(Decodable))]
    public async Task ToHdr_EqualsTheReference(string file)
    {
        using Image image = Image.Load(Read(file).AsSpan(), Options());
        await Assert.That(image.GainMap).IsEqualTo(Image.Identify(Read(file).AsMemory(), Options()).GainMap);
        using HdrImage actual = image.ToHdr();
        using HdrImage expected = Reference(file).ToHdr();
        await Assert.That(actual.GainMapKind).IsEqualTo(Enum.Parse<HdrGainMapKinds>(Find(file).GainMap!.Kind));
        await Assert.That(ReferenceGainMapFormat.BitwiseEqual(actual, expected)).IsTrue();
    }

    [Test]
    public async Task Apple_HeadroomComesFromTheMakerNote()
    {
        // MakerNote tags 33 = 1.2 and 48 = 0.5 give 2^(2.303 - 0.303 * 0.5); the map's XMP says 3.5.
        GainMapInfo hdr = Image.Identify(Read("gainmap-apple.heic").AsMemory(), Options()).GainMap!;
        await Assert.That(hdr.Headroom!.Value).IsEqualTo((float)Math.Pow(2, 2.303 - (0.303 * 0.5))).Within(1e-5f);
    }

    [Test]
    public async Task IsoAndApple_IsoIsSelected_AndTheRotatedMapFollowsTheBase()
    {
        using Image image = Image.Load(Read("gainmap-both-rotated.heic").AsSpan(), Options());
        await Assert.That((image.Width, image.Height)).IsEqualTo((48, 64));
        await Assert.That(image.GainMap!.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await Assert.That(image.GainMap.Detected).IsEqualTo(HdrGainMapKinds.Iso21496 | HdrGainMapKinds.Apple);
        await Assert.That(image.GainMap.GainMapSize).IsEqualTo(new Size(24, 32));
    }

    [Test]
    [Arguments("gainmap-iso-version1.heic", false)]
    [Arguments("gainmap-iso-truncated.heic", true)]
    public async Task InvalidMetadata_IsReported_AndTheBaseStillLoads(string file, bool corrupt)
    {
        GainMapInfo hdr = Image.Identify(Read(file).AsMemory(), Options()).GainMap!;
        await Assert.That(hdr.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await Assert.That(hdr.CanDecode).IsFalse();
        await Assert.That(hdr.UnsupportedReason).IsNotNull();
        using Image image = Image.Load(Read(file).AsSpan(), Options());
        using Image plain = Image.Load(Read(file).AsSpan(), Options(gainMap: false));
        await Assert.That(HeifDecodeTests.RgbaHash(image)).IsEqualTo(HeifDecodeTests.RgbaHash(plain));
        await Assert.That(image.GainMap!.CanDecode).IsFalse();
        if (corrupt)
        {
            await Assert.That(() => image.ToHdr()).Throws<ImageFormatException>();
        }
        else
        {
            await Assert.That(() => image.ToHdr()).Throws<NotSupportedException>();
        }
    }

    [Test]
    public async Task WithoutLoadGainMap_TheImageHasNoHdr()
    {
        using Image image = Image.Load(Read("gainmap-iso.heic").AsSpan(), Options(gainMap: false));
        await Assert.That(image.GainMap).IsNull();
        await Assert.That(() => image.ToHdr()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheMapOutlivesTheInputBuffer()
    {
        byte[] bytes = Read("gainmap-iso-rgb.heic");
        using Image image = Image.Load(bytes.AsSpan(), Options());
        Array.Clear(bytes);
        using HdrImage actual = image.ToHdr();
        using HdrImage expected = Reference("gainmap-iso-rgb.heic").ToHdr();
        await Assert.That(ReferenceGainMapFormat.BitwiseEqual(actual, expected)).IsTrue();
    }

    [Test]
    public async Task TargetSizeAndEdits_KeepTheMap()
    {
        var options = new DecoderOptions { TargetSize = new Size(32, 0) };
        using Image reduced = Image.Load(Read("gainmap-both-rotated.heic").AsSpan(), Options(options));
        using HdrImage actual = reduced.ToHdr();
        using HdrImage expected = Reference("gainmap-both-rotated.heic").ToHdr(options);
        await Assert.That((actual.Width, actual.Height)).IsEqualTo((32, 43));
        await Assert.That(ReferenceGainMapFormat.BitwiseEqual(actual, expected)).IsTrue();

        using Image edited = Image.Load(Read("gainmap-iso.heic").AsSpan(), Options());
        edited.Mutate(x => x.Resize(32, 24));
        await Assert.That(edited.GainMap!.CanDecode).IsTrue();
        using HdrImage resized = edited.ToHdr();
        await Assert.That((resized.Width, resized.Height)).IsEqualTo((32, 24));
    }

    [Test]
    public async Task IgnoreMetadata_KeepsTheBaseColourForTheMap()
    {
        var options = new DecoderOptions { IgnoreMetadata = true };
        using Image image = Image.Load(Read("gainmap-apple.heic").AsSpan(), Options(options));
        await Assert.That(image.Metadata.GetBlockLength(MetadataKinds.Exif)).IsEqualTo(0);
        using HdrImage actual = image.ToHdr();
        using HdrImage expected = Reference("gainmap-apple.heic").ToHdr();
        await Assert.That(ReferenceGainMapFormat.BitwiseEqual(actual, expected)).IsTrue();
    }

    [Test]
    public async Task MapDecoding_HonorsCancellation()
    {
        using Image image = Image.Load(Read("gainmap-iso.heic").AsSpan(), Options());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => image.ToHdr(null, cancelled.Token)).Throws<OperationCanceledException>();
    }

    private sealed record Entry(string File, GainMapEntry? GainMap);

    private sealed record GainMapEntry(string Kind, string Detected, int Width, int Height, int Channels, string? MapPng, string? IsoPayload,
        string? AppleXmp, string? AppleExif);
}
