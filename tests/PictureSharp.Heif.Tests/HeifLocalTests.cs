using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PictureSharp.Formats.Heif.Tests;

public sealed partial class HeifLocalTests
{
    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_IdentifyMatchesHeifInfo()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? AppContext.BaseDirectory;
        string sampleDirectory = Path.Combine(root, "samples");
        string[] samples = Directory.Exists(sampleDirectory)
            ? Directory.GetFiles(sampleDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".heif", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        string tools = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools");
        string oracle = Path.Combine(tools, "bin", "heif-info");
        Skip.Unless(samples.Length > 0, "No private HEIC samples in samples/.");
        Skip.Unless(File.Exists(oracle), "The pinned heif-info oracle is not available.");
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
        var descriptions = new Dictionary<string, ImageInfo>();
        foreach (string path in samples)
        {
            ImageInfo info = Image.Identify(File.ReadAllBytes(path).AsMemory(), new DecoderOptions { Configuration = configuration });
            descriptions.Add(path, info);
            Console.WriteLine($"Local HEIC: {Path.GetFileName(path)}: {info.Width}x{info.Height}, {info.BitsPerChannel}-bit, alpha={info.HasAlpha}");
        }

        foreach (string path in samples)
        {
            ImageInfo info = descriptions[path];
            var start = new ProcessStartInfo(oracle) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(await errorTask);
            Match dimensions = PrimaryDimensions().Match(output);
            await Assert.That(dimensions.Success).IsTrue().Because(Path.GetFileName(path));
            await Assert.That((info.Width, info.Height)).IsEqualTo((int.Parse(dimensions.Groups[1].Value), int.Parse(dimensions.Groups[2].Value)))
                .Because(Path.GetFileName(path));
            // iPhone photos are grids: the primary item plus its tiles at least.
            await Assert.That(info.ContainerItemCount ?? 0).IsGreaterThan(2).Because(Path.GetFileName(path));
        }
    }

    [GeneratedRegex(@"image:\s*(\d+)x(\d+)[^\r\n]*primary")]
    private static partial Regex PrimaryDimensions();
}
