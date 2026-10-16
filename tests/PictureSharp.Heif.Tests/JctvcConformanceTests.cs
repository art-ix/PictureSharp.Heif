using System.Security.Cryptography;
using System.Text.Json;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

// JCT-VC HEVC v1 conformance bitstreams (downloaded with scripts/fetch-jctvc-conformance.py --approved into the ignored
// tests/assets/external/jctvc): the first picture of each stream must match HM bit for bit, or raise
// NotSupportedException when it uses inter prediction or another feature outside the HEIF intra profile.
public sealed class JctvcConformanceTests
{
    private static readonly Lazy<Entry[]> Manifest = new(() => JsonSerializer.Deserialize<Entry[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "jctvc-reference.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    public static IEnumerable<string> Streams() => Manifest.Value.Where(e => e.Planes is not null).Select(e => e.Name);

    [Test]
    [Property("Category", "Conformance")]
    [MethodDataSource(nameof(Streams))]
    public async Task FirstPicture_MatchesHm(string name)
    {
        Entry entry = Manifest.Value.Single(e => e.Name == name);
        string path = Path.Combine(FindRoot(), "tests", "assets", "external", "jctvc", "streams", entry.File);
        Skip.Unless(File.Exists(path), "Download the conformance streams with scripts/fetch-jctvc-conformance.py --approved.");
        byte[] bytes = File.ReadAllBytes(path);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(entry.Sha256);
        HevcFrame frame;
        try
        {
            frame = HevcDecoder.Decode(HevcConformanceTests.SplitAnnexB(bytes), null, CancellationToken.None);
        }
        catch (NotSupportedException error)
        {
            Console.WriteLine($"{name}: {error.Message}");
            await Assert.That(Unsupported).Contains(name).Because(error.Message);
            return;
        }

        await Assert.That(Unsupported).DoesNotContain(name);
        await Assert.That((frame.CroppedWidth, frame.CroppedHeight, frame.BitDepthLuma, frame.ChromaFormatIdc))
            .IsEqualTo((entry.Width, entry.Height, entry.BitDepth, entry.ChromaFormat));
        await Assert.That(HevcConformanceTests.PlaneHashes(frame)).IsEquivalentTo(entry.Planes!, CollectionOrdering.Matching);
    }

    // Streams whose first picture is legitimately outside the decoder's scope.
    private static readonly string[] Unsupported = [];

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed record Entry(string Name, string File, string Sha256, int Width, int Height, int BitDepth, int ChromaFormat, string[]? Planes);
}
