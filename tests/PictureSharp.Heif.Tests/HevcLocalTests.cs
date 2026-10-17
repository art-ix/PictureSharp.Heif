using System.Diagnostics;
using System.Security.Cryptography;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

// Private photos (samples/, never committed): every HEVC item (grid tiles, thumbnails, auxiliary images) must decode to
// exactly the YCbCr produced by both libde265 (dec265) and HM (TAppDecoder).
public sealed class HevcLocalTests
{
    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_EveryHevcItemMatchesDec265AndHm()
    {
        string root = FindRoot();
        string sampleDirectory = Path.Combine(root, "samples");
        string[] samples = Directory.Exists(sampleDirectory)
            ? [.. Directory.GetFiles(sampleDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".heif", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];
        string tools = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools"), "bin");
        string dec265 = Path.Combine(tools, "dec265");
        string hm = Path.Combine(tools, "TAppDecoder");
        Skip.Unless(samples.Length > 0, "No private HEIC samples in samples/.");
        Skip.Unless(File.Exists(dec265) && File.Exists(hm), "Build dec265 and TAppDecoder with scripts/build-heif-reference.sh.");

        string work = Directory.CreateTempSubdirectory("picturesharp-heif-local-").FullName;
        try
        {
            int items = 0;
            foreach (string path in samples)
            {
                foreach ((uint id, List<ReadOnlyMemory<byte>> nals) in HeifTestItems.Extract(File.ReadAllBytes(path)))
                {
                    string label = $"{Path.GetFileName(path)} item {id}";
                    HevcFrame frame = HevcDecoder.Decode(nals, null, CancellationToken.None);
                    string[] actual = HevcConformanceTests.PlaneHashes(frame);
                    string stream = Path.Combine(work, "item.hevc");
                    File.WriteAllBytes(stream, HeifTestItems.ToAnnexB(nals));
                    string libde265Output = Path.Combine(work, "de265.yuv");
                    string hmOutput = Path.Combine(work, "hm.yuv");
                    await Run(dec265, ["-q", "-t", "0", "-0", "-o", libde265Output, stream]);
                    await Run(hm, ["-b", stream, "-o", hmOutput, "-d", "0"]);
                    string[] libde265 = YuvHashes(File.ReadAllBytes(libde265Output), frame);
                    string[] reference = YuvHashes(File.ReadAllBytes(hmOutput), frame);
                    await Assert.That(reference).IsEquivalentTo(libde265, CollectionOrdering.Matching).Because(label + ": HM vs libde265");
                    await Assert.That(actual).IsEquivalentTo(reference, CollectionOrdering.Matching).Because(label);
                    items++;
                }
            }

            Console.WriteLine($"Local HEVC: {items} items from {samples.Length} photos match HM and libde265.");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_DecodeMatchesHeifDec()
    {
        string root = FindRoot();
        string sampleDirectory = Path.Combine(root, "samples");
        string[] samples = Directory.Exists(sampleDirectory)
            ? [.. Directory.GetFiles(sampleDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".heif", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];
        string heifDec = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools"), "bin", "heif-dec");
        Skip.Unless(samples.Length > 0, "No private HEIC samples in samples/.");
        Skip.Unless(File.Exists(heifDec), "Build heif-dec with scripts/build-heif-reference.sh.");
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-rgb-").FullName;
        try
        {
            foreach (string path in samples)
            {
                byte[] bytes = File.ReadAllBytes(path);
                var stopwatch = Stopwatch.StartNew();
                using Image image = Image.Load(bytes.AsSpan(), new DecoderOptions { Configuration = configuration });
                stopwatch.Stop();
                string png = Path.Combine(work, "reference.png");
                await Run(heifDec, ["--quiet", path, png]);
                using Image reference = Image.Load(png);
                await Assert.That((image.Width, image.Height)).IsEqualTo((reference.Width, reference.Height)).Because(path);
                await Assert.That(HeifDecodeTests.RgbaHash(image)).IsEqualTo(HeifDecodeTests.RgbaHash(reference)).Because(path);
                await Assert.That(image.Metadata.IccProfile).IsNotNull();
                Console.WriteLine($"Local decode: {Path.GetFileName(path)} {image.Width}x{image.Height} in {stopwatch.ElapsedMilliseconds} ms, identical to heif-dec.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Splits an 8-bit or little-endian 16-bit YUV file into planes of the frame's cropped geometry.
    private static string[] YuvHashes(byte[] yuv, HevcFrame frame)
    {
        int sample = frame.BitDepthLuma > 8 ? 2 : 1;
        int luma = frame.CroppedWidth * frame.CroppedHeight * sample;
        if (frame.ChromaFormatIdc == 0)
        {
            return [Convert.ToHexStringLower(SHA256.HashData(yuv.AsSpan(0, luma)))];
        }

        int chroma = (frame.CroppedWidth / 2) * (frame.CroppedHeight / 2) * sample;
        return
        [
            Convert.ToHexStringLower(SHA256.HashData(yuv.AsSpan(0, luma))),
            Convert.ToHexStringLower(SHA256.HashData(yuv.AsSpan(luma, chroma))),
            Convert.ToHexStringLower(SHA256.HashData(yuv.AsSpan(luma + chroma, chroma))),
        ];
    }

    internal static async Task Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await Assert.That(process.ExitCode).IsEqualTo(0).Because(await output + await error);
    }

    internal static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
