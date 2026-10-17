using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

// Decoded RGBA must equal heif-dec (libheif 1.20.2) output, recorded in the corpus manifest after the documented
// 10-to-8-bit reduction and conversion of premultiplied alpha to straight alpha.
public sealed class HeifDecodeTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");

    private static readonly Lazy<CorpusEntry[]> Manifest = new(() => JsonSerializer.Deserialize<CorpusEntry[]>(
        File.ReadAllText(Path.Combine(Corpus, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    public static IEnumerable<string> Files() => Manifest.Value.Select(entry => entry.File);

    private static Image Load(string file, DecoderOptions? options = null) =>
        Image.Load(File.ReadAllBytes(Path.Combine(Corpus, file)).AsSpan(), (options ?? new DecoderOptions()) with { Configuration = Configuration });

    internal static string RgbaHash(Image image)
    {
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_MatchesHeifDec(string file)
    {
        CorpusEntry entry = Manifest.Value.Single(e => e.File == file);
        using Image image = Load(file);
        await Assert.That((image.Width, image.Height)).IsEqualTo((entry.Width, entry.Height));
        await Assert.That(image.HasAlpha).IsEqualTo(entry.HasAlpha);
        await Assert.That(RgbaHash(image)).IsEqualTo(entry.RgbaSha256);
    }

    [Test]
    public async Task Decode_CopiesIccExifAndXmp_AndNeutralizesExifOrientation()
    {
        using Image image = Load("wrap-icc-exif-xmp.heic");
        byte[] icc = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "p3_v4_para0.icc"));
        await Assert.That(image.Metadata.IccProfile!).IsEquivalentTo(icc, CollectionOrdering.Matching);
        await Assert.That(image.Metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
        await Assert.That(System.Text.Encoding.UTF8.GetString(image.Metadata.Xmp!)).Contains("PictureSharp.Heif corpus");

        // The stored EXIF orientation was 6; the pixels are already oriented, so applying EXIF orientation is a no-op.
        using Image oriented = Load("wrap-icc-exif-xmp.heic", new DecoderOptions { ApplyExifOrientation = true });
        await Assert.That((oriented.Width, oriented.Height)).IsEqualTo((80, 64));
        await Assert.That(RgbaHash(oriented)).IsEqualTo(RgbaHash(image));

        using Image stripped = Load("wrap-icc-exif-xmp.heic", new DecoderOptions { IgnoreMetadata = true });
        await Assert.That(stripped.Metadata.IccProfile).IsNull();
        await Assert.That(stripped.Metadata.Exif).IsNull();
    }

    [Test]
    public async Task Decode_DescribesNclxColourWithoutIcc()
    {
        using Image bt2020 = Load("x265-bt2020-limited10.heic");
        await Assert.That(bt2020.Metadata.Chromaticities).IsNotNull();
        await Assert.That(bt2020.Metadata.Chromaticities!.Value.RedX).IsEqualTo(0.708f);
        await Assert.That(bt2020.Metadata.Gamma).IsEqualTo(0.45455f);
        using Image srgb = Load("x265-rgb8.heic");
        await Assert.That(srgb.Metadata.Chromaticities).IsNull();
        await Assert.That(srgb.Metadata.IccProfile).IsNull();
    }

    [Test]
    public async Task Decode_ColorConversionUsesTheEmbeddedIccProfile()
    {
        using Image original = Load("wrap-icc-exif-xmp.heic");
        using Image converted = Load("wrap-icc-exif-xmp.heic", new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That(converted.Metadata.IccProfile).IsNull();
        await Assert.That(RgbaHash(converted)).IsNotEqualTo(RgbaHash(original));
    }

    [Test]
    public async Task Decode_TargetSizeIsAppliedByTheLibrary()
    {
        using Image image = Load("grid-2x1.heic", new DecoderOptions { TargetSize = new System.Drawing.Size(60, 0) });
        await Assert.That((image.Width, image.Height)).IsEqualTo((60, 32));
    }

    [Test]
    public async Task Decode_ChargesPixelsBeforeDecoding()
    {
        await Assert.That(() => Load("grid-2x1.heic", new DecoderOptions { MaxPixels = 120 * 64 })).Throws<ImageFormatException>();
        using Image image = Load("grid-2x1.heic", new DecoderOptions { MaxPixels = (120 * 64) + (2 * 64 * 64) });
        await Assert.That(image.Width).IsEqualTo(120);
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task MutatedFilesNeverLeakUncheckedExceptions()
    {
        var random = new Random(0x48454943);
        byte[][] seeds = [.. Directory.GetFiles(Corpus, "*.heic").Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
        int decoded = 0;
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] bytes = seeds[iteration % seeds.Length].ToArray();
            int mutations = 1 + (iteration % 6);
            for (int j = 0; j < mutations; j++)
            {
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            }

            if (iteration % 7 == 0)
            {
                Array.Resize(ref bytes, random.Next(bytes.Length));
            }

            try
            {
                var options = new DecoderOptions { Configuration = Configuration, MaxPixels = 1 << 20, LoadGainMap = iteration % 2 == 0 };
                _ = Image.Identify(bytes.AsMemory(), options);
                _ = Image.ReadMetadata(bytes.AsMemory(), options);
                using Image image = Image.Load(bytes.AsSpan(), options);
                if (image.Hdr is { CanDecode: true })
                {
                    using HdrImage hdr = image.ToHdr();
                }

                decoded++;
            }
            catch (Exception error) when (error is ImageFormatException or NotSupportedException)
            {
            }
        }

        Console.WriteLine($"HEIF decode fuzzing: {decoded} of 3000 mutated files still decoded.");
        await Assert.That(seeds.Length).IsGreaterThan(10);
    }

    private sealed record CorpusEntry(string File, int Width, int Height, bool HasAlpha, string RgbaSha256);
}
