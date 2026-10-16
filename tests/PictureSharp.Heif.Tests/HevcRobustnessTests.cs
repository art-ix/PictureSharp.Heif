using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HevcRobustnessTests
{
    private static List<ReadOnlyMemory<byte>>[] Seeds() =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "hevc"), "*.hevc")
            .Where(path => new FileInfo(path).Length < 12000)
            .Order(StringComparer.Ordinal)
            .Select(path => HevcConformanceTests.SplitAnnexB(File.ReadAllBytes(path))),
    ];

    private static void Reserve(int width, int height)
    {
        if ((long)width * height > 1 << 20)
        {
            throw new ImageFormatException("Test pixel budget exceeded.");
        }
    }

    [Test]
    public async Task TruncatedSlicesAndMissingParameterSetsFail()
    {
        List<ReadOnlyMemory<byte>> nals = HevcConformanceTests.SplitAnnexB(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "hevc", "hm-main-qp32.hevc")));
        List<ReadOnlyMemory<byte>> slicesOnly = [.. nals.Where(nal => ((nal.Span[0] >> 1) & 0x3f) < 32)];
        await Assert.That(() => HevcDecoder.Decode(slicesOnly, null, CancellationToken.None)).Throws<ImageFormatException>();
        List<ReadOnlyMemory<byte>> truncated = [.. nals.Select(nal => ((nal.Span[0] >> 1) & 0x3f) < 32 ? nal[..(nal.Length / 2)] : nal)];
        await Assert.That(() => HevcDecoder.Decode(truncated, null, CancellationToken.None)).Throws<ImageFormatException>();
        await Assert.That(() => HevcDecoder.Decode([], null, CancellationToken.None)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task CancellationIsObserved()
    {
        List<ReadOnlyMemory<byte>> nals = HevcConformanceTests.SplitAnnexB(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "hevc", "hm-main-qp32.hevc")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => HevcDecoder.Decode(nals, null, cancelled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task MutatedStreamsNeverLeakUncheckedExceptions()
    {
        var random = new Random(0x48455643);
        List<ReadOnlyMemory<byte>>[] seeds = Seeds();
        int decoded = 0;
        for (int iteration = 0; iteration < 6000; iteration++)
        {
            List<ReadOnlyMemory<byte>> seed = seeds[iteration % seeds.Length];
            var nals = new List<ReadOnlyMemory<byte>>(seed.Count);
            int target = random.Next(seed.Count);
            for (int n = 0; n < seed.Count; n++)
            {
                byte[] nal = seed[n].ToArray();
                if (n == target || random.Next(8) == 0)
                {
                    if (iteration % 5 == 0 && nal.Length > 3)
                    {
                        Array.Resize(ref nal, 2 + random.Next(nal.Length - 2));
                    }
                    else
                    {
                        for (int j = 0; j < 1 + (iteration % 6); j++)
                        {
                            // Keep the two-byte NAL header mostly intact so the mutation reaches the payload parsers.
                            int position = nal.Length > 2 && random.Next(16) != 0 ? 2 + random.Next(nal.Length - 2) : random.Next(nal.Length);
                            nal[position] ^= (byte)(1 << random.Next(8));
                        }
                    }
                }

                nals.Add(nal);
            }

            try
            {
                HevcDecoder.Decode(nals, Reserve, CancellationToken.None);
                decoded++;
            }
            catch (Exception error) when (error is ImageFormatException or NotSupportedException)
            {
            }
        }

        // Some mutations land in bits that do not change validity; most must be rejected or decoded without leaks.
        await Assert.That(seeds.Length).IsGreaterThan(20);
        Console.WriteLine($"HEVC fuzzing: {decoded} of 6000 mutated streams still decoded.");
    }
}
