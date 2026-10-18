using System.Diagnostics;
using System.Text;

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
                    await Assert.That(image.Hdr).IsNull().Because(name);
                    Console.WriteLine($"Local HDR: {name} has no gain map.");
                    continue;
                }

                if (new FileInfo(mapPng).Length == 0)
                {
                    // heif-dec leaves an empty file when libpng rejects the map's ICC profile (a gray profile on RGB
                    // output); there is no oracle for this photo, which is reported, not counted as a comparison.
                    await Assert.That(image.Hdr).IsNotNull().Because(name);
                    Console.WriteLine($"Local HDR: {name} not compared: heif-dec could not write the gain map.");
                    continue;
                }

                GainMapMetadata metadata = items.FirstOrDefault(item => item.Type == "tmap") is { Data: not null } tmap
                    ? GainMapMetadata.ParseIso(tmap.Data.AsSpan(1), 1)
                    : GainMapMetadata.ParseApple(Tiff(items.First(item => item.Type == "Exif").Data),
                        items.First(item => item.Type == "mime" && Encoding.UTF8.GetString(item.Data).Contains("HDRGainMapVersion")).Data);
                await Assert.That(image.Hdr!.Selected).IsEqualTo(metadata.Kind).Because(name);
                // heif-dec found the Apple map; iOS 18 adds the ISO description of the same map.
                await Assert.That(image.Hdr.Detected).IsEqualTo(HdrGainMapKinds.Apple | (metadata.Kind & HdrGainMapKinds.Iso21496)).Because(name);
                await Assert.That(image.Hdr.CanDecode).IsTrue().Because(name);

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

    private static byte[] Tiff(byte[] exif) => exif[(4 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(exif))..];
}
