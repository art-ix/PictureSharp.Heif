using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PictureSharp.Formats.Heif.Tests;

public sealed partial class HeifCorpusTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");
    private static CorpusEntry[] ReadManifest() => JsonSerializer.Deserialize<CorpusEntry[]>(
        File.ReadAllText(Path.Combine(Corpus, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Test]
    public async Task CommittedCorpus_HashesAndIdentificationMatchManifest()
    {
        foreach (CorpusEntry entry in ReadManifest())
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Corpus, entry.File));
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(entry.Sha256);
            ImageInfo info = new HeifFormat().Decoder.Identify(bytes, new DecoderOptions());
            await Assert.That((info.Width, info.Height, info.BitsPerChannel, info.HasAlpha, info.ContainerItemCount))
                .IsEqualTo((entry.Width, entry.Height, entry.BitsPerChannel, entry.HasAlpha, entry.ItemCount));
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task Identify_MatchesHeifInfo()
    {
        string root = FindRoot();
        string directory = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools");
        string executable = Path.Combine(directory, "bin", "heif-info");
        bool available = File.Exists(executable);
        if (Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_REQUIRE_ORACLES") == "1")
        {
            await Assert.That(available).IsTrue();
        }

        Skip.Unless(available, "Build the pinned heif-info oracle with scripts/build-heif-reference.sh.");
        foreach (CorpusEntry entry in ReadManifest())
        {
            string path = Path.Combine(Corpus, entry.File);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(path);
            using Process process = Process.Start(start)!;
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            string output = await outputTask;
            string error = await errorTask;
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(error);
            Match dimensions = Dimensions().Match(output);
            await Assert.That(dimensions.Success).IsTrue().Because(output);
            ImageInfo info = new HeifFormat().Decoder.Identify(File.ReadAllBytes(path), new DecoderOptions());
            await Assert.That(info.Width).IsEqualTo(int.Parse(dimensions.Groups[1].Value));
            await Assert.That(info.Height).IsEqualTo(int.Parse(dimensions.Groups[2].Value));
            Match bitDepth = BitDepth().Match(output);
            await Assert.That(bitDepth.Success).IsTrue().Because(output);
            await Assert.That(info.BitsPerChannel).IsEqualTo(int.Parse(bitDepth.Groups[1].Value));
            await Assert.That(info.HasAlpha).IsEqualTo(output.Contains("alpha channel: yes", StringComparison.Ordinal));
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    [GeneratedRegex(@"image:\s*(\d+)x(\d+)")]
    private static partial Regex Dimensions();

    [GeneratedRegex(@"bit depth:\s*(\d+)")]
    private static partial Regex BitDepth();

    private sealed record CorpusEntry(string File, string Sha256, int Width, int Height, int BitsPerChannel, bool HasAlpha, int ItemCount);
}
