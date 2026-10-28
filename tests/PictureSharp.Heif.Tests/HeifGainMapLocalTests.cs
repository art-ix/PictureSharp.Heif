using System.Diagnostics;
using System.Text;
using PictureSharp.Processing;

namespace PictureSharp.Formats.Heif.Tests;

// Private iPhone photos: the gain map heif-dec writes as an auxiliary image, with metadata taken from the items by the
// test-only parser, must give the same ToHdr as the plugin; ReadMetadata must report what Load reports.
public sealed class HeifGainMapLocalTests
{
    [Test]
    [Property("Category", "Local")]
    [Property("Category", "Oracle")]
    public async Task PrivateSamples_ToHdrMatchesHeifDecMap()
    {
        string root = HevcLocalTests.FindRoot();
        string sampleDirectory = Path.Combine(root, "samples");
        string[] samples = Directory.Exists(sampleDirectory)
            ? [.. Directory.GetFiles(sampleDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];
        string heifDec = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools"), "bin", "heif-dec");
        Skip.Unless(samples.Length > 0, "No private HEIC samples in samples/.");
        Skip.Unless(File.Exists(heifDec), "Build heif-dec with scripts/build-heif-reference.sh.");
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
        var options = new DecoderOptions { Configuration = configuration, LoadGainMap = true };
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-gainmap-").FullName;
        int compared = 0;
        try
        {
            foreach (string path in samples)
            {
                // A file left by the previous photo must never stand in for this photo's output.
                foreach (string file in Directory.GetFiles(work))
                {
                    File.Delete(file);
                }

                string name = Path.GetFileName(path);
                byte[] bytes = File.ReadAllBytes(path);
                List<(uint Id, string Type, byte[] Data)> items = HeifTestItems.Payloads(bytes);
                ImageMetadata read = Image.ReadMetadata(bytes.AsMemory(), options);
                using Image image = Image.Load(bytes.AsSpan(), options);
                await Assert.That(read.IccProfile).IsEquivalentTo(image.Metadata.IccProfile!, CollectionOrdering.Matching).Because(name);
                await Assert.That(read.Exif).IsEquivalentTo(image.Metadata.Exif!, CollectionOrdering.Matching).Because(name);

                string output = Path.Combine(work, "base.png");
                await HevcLocalTests.Run(heifDec, ["--quiet", "--with-aux", "--no-colons", path, output]);
                string mapPng = Path.Combine(work, "base-urn_com_apple_photo_2020_aux_hdrgainmap.png");
                if (!File.Exists(mapPng))
                {
                    // heif-dec writes only Apple's auxiliary map; an ISO 21496-1 map without it has no oracle here.
                    await Assert.That(image.GainMap is null || (image.GainMap.Detected & HdrGainMapKinds.Apple) == 0).IsTrue().Because(name);
                    Console.WriteLine(image.GainMap is null
                        ? $"Local HDR: {name} has no gain map."
                        : $"Local HDR: {name} not compared: {image.GainMap.Detected} only, {image.GainMap.UnsupportedReason ?? "decodable"}");
                    continue;
                }

                if (new FileInfo(mapPng).Length == 0)
                {
                    // heif-dec leaves an empty file when libpng rejects the map's ICC profile (a gray profile on RGB
                    // output); there is no oracle for this photo, which is reported, not counted as a comparison.
                    await Assert.That(image.GainMap).IsNotNull().Because(name);
                    Console.WriteLine($"Local HDR: {name} not compared: heif-dec could not write the gain map.");
                    continue;
                }

                if (!image.GainMap!.CanDecode)
                {
                    // An Apple map without the MakerNote headroom (iPhone 13 Pro): the plugin reports it as unsupported,
                    // and the test-only parser must reject the same metadata.
                    await Assert.That(image.GainMap.UnsupportedReason).IsNotNull().Because(name);
                    await Assert.That(() => GainMapMetadata.ParseApple(Tiff(items.First(item => item.Type == "Exif").Data),
                        items.First(item => item.Type == "mime" && Encoding.UTF8.GetString(item.Data).Contains("HDRGainMapVersion")).Data))
                        .Throws<ImageFormatException>().Because(name);
                    Console.WriteLine($"Local HDR: {name} not compared: {image.GainMap.UnsupportedReason}");
                    continue;
                }

                GainMapMetadata metadata = items.FirstOrDefault(item => item.Type == "tmap") is { Data: not null } tmap
                    ? GainMapMetadata.ParseIso(tmap.Data.AsSpan(1), 1)
                    : GainMapMetadata.ParseApple(Tiff(items.First(item => item.Type == "Exif").Data),
                        items.First(item => item.Type == "mime" && Encoding.UTF8.GetString(item.Data).Contains("HDRGainMapVersion")).Data);
                await Assert.That(image.GainMap!.Selected).IsEqualTo(metadata.Kind).Because(name);
                // heif-dec found the Apple map; iOS 18 adds the ISO description of the same map.
                await Assert.That(image.GainMap.Detected).IsEqualTo(HdrGainMapKinds.Apple | (metadata.Kind & HdrGainMapKinds.Iso21496)).Because(name);
                await Assert.That(image.GainMap.CanDecode).IsTrue().Because(name);

                var stopwatch = Stopwatch.StartNew();
                using HdrImage actual = image.ToHdr();
                stopwatch.Stop();
                using Image baseImage = Image.Load(bytes.AsSpan(), options with { LoadGainMap = false });
                using Image map = Image.Load(mapPng);
                using HdrImage expected = new ReferenceGainMapFormat(baseImage, metadata, map).ToHdr();
                await Assert.That(ReferenceGainMapFormat.BitwiseEqual(actual, expected)).IsTrue().Because(name);
                Console.WriteLine($"Local HDR: {name} {metadata.Kind}, map {map.Width}x{map.Height}, headroom {actual.Headroom:F3}, "
                    + $"ToHdr {stopwatch.ElapsedMilliseconds} ms, identical to the heif-dec map.");
                compared++;
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        await Assert.That(compared).IsGreaterThan(0);
    }

    // Writing back (0.6.10): every private photo with a usable ISO map is saved with HeifGainMapMode.Require. The
    // unedited map is copied (identical decoded map and metadata); a resized one is encoded again within 4 codes (measured 2–4 at GainMapQuality 90).
    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_GainMapIsWrittenBack()
    {
        string root = HevcLocalTests.FindRoot();
        string sampleDirectory = Path.Combine(root, "samples");
        string[] samples = Directory.Exists(sampleDirectory)
            ? [.. Directory.GetFiles(sampleDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];
        Skip.Unless(samples.Length > 0, "No private HEIC samples in samples/.");
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
        var options = new DecoderOptions { Configuration = configuration, LoadGainMap = true };
        var encoder = new HeifEncoderOptions { Configuration = configuration, GainMap = HeifGainMapMode.Require };
        int written = 0;
        foreach (string path in samples)
        {
            string name = Path.GetFileName(path);
            using Image image = Image.Load(File.ReadAllBytes(path), options);
            if (image.GainMap is not { CanDecode: true, Selected: HdrGainMapKinds.Iso21496 })
            {
                continue;
            }

            foreach (bool resized in new[] { false, true })
            {
                using Image source = Image.Load(File.ReadAllBytes(path), options);
                if (resized)
                {
                    source.Mutate(x => x.Resize(image.Width / 4, image.Height / 4));
                }

                var stopwatch = Stopwatch.StartNew();
                byte[] heic = source.Encode(encoder);
                stopwatch.Stop();
                using Image reloaded = Image.Load(heic, options);
                EncodableGainMap expected = source.GetGainMap()!, actual = reloaded.GetGainMap()!;
                await Assert.That(actual.Metadata.ToIso()).IsEquivalentTo(expected.Metadata.ToIso(), CollectionOrdering.Matching).Because(name);
                await Assert.That(actual.Metadata.MultiplicationSpace).IsEqualTo(expected.Metadata.MultiplicationSpace).Because(name);
                using Image before = expected.Map.Decode(-1, default);
                using Image after = actual.Map.Decode(-1, default);
                await Assert.That((after.Width, after.Height)).IsEqualTo((before.Width, before.Height)).Because(name);
                int worst = 0;
                Span<Rgba32> a = before.GetPixelSpan<Rgba32>(), b = after.GetPixelSpan<Rgba32>();
                for (int i = 0; i < a.Length; i++)
                {
                    worst = Math.Max(worst, Math.Max(Math.Abs(a[i].R - b[i].R), Math.Max(Math.Abs(a[i].G - b[i].G), Math.Abs(a[i].B - b[i].B))));
                }

                await Assert.That(worst).IsLessThanOrEqualTo(resized ? 4 : 0).Because(name);
                using HdrImage hdr = reloaded.ToHdr();
                await Assert.That((hdr.Width, hdr.Height)).IsEqualTo((source.Width, source.Height)).Because(name);
                Console.WriteLine($"Local HDR write: {name} {(resized ? "resized" : "copied")}, map {after.Width}x{after.Height}, "
                    + $"worst {worst}, space {actual.Metadata.MultiplicationSpace?.ToString() ?? "base"}, {heic.Length / 1024} KiB, "
                    + $"encode {stopwatch.ElapsedMilliseconds} ms.");
                written++;
            }
        }

        await Assert.That(written).IsGreaterThan(0);
    }

    private static byte[] Tiff(byte[] exif) => exif[(4 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(exif))..];
}
