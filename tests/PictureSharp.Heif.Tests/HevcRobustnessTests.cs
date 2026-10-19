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

    // Wavefront rows decoded in parallel, and the row-band loop filters, give the planes of the sequential decoder for
    // every corpus stream and thread count.
    [Test]
    [MethodDataSource(typeof(HevcConformanceTests), nameof(HevcConformanceTests.Streams))]
    public async Task ParallelDecode_MatchesSequential(string file)
    {
        List<ReadOnlyMemory<byte>> nals = HevcConformanceTests.SplitAnnexB(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "hevc", file)));
        string[] sequential;
        try
        {
            sequential = HevcConformanceTests.PlaneHashes(HevcDecoder.Decode(nals, null, CancellationToken.None));
        }
        catch (NotSupportedException)
        {
            await Assert.That(() => HevcDecoder.Decode(nals, null, CancellationToken.None, new ParallelOptions { MaxDegreeOfParallelism = 4 }))
                .Throws<NotSupportedException>();
            return;
        }

        foreach (int threads in new[] { 2, 3, 16 })
        {
            HevcFrame frame = HevcDecoder.Decode(nals, null, CancellationToken.None, new ParallelOptions { MaxDegreeOfParallelism = threads });
            await Assert.That(HevcConformanceTests.PlaneHashes(frame)).IsEquivalentTo(sequential, CollectionOrdering.Matching);
        }
    }

    // Wrong entry points make the parallel decoder fall back to sequential parsing: the same pixels or the same error
    // as without parallelism, never another exception or a hang.
    [Test]
    public async Task CorruptEntryPointsFallBackOrFail()
    {
        List<ReadOnlyMemory<byte>> nals = HevcConformanceTests.SplitAnnexB(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "hevc", "hm-wpp.hevc")));
        string[] expected = HevcConformanceTests.PlaneHashes(HevcDecoder.Decode(nals, null, CancellationToken.None));
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        int slice = nals.FindIndex(nal => ((nal.Span[0] >> 1) & 0x3f) is <= 21 and (<= 9 or >= 16));
        var random = new Random(0x57505021);
        int failures = 0;
        for (int iteration = 0; iteration < 400; iteration++)
        {
            byte[] nal = nals[slice].ToArray();
            // The entry points follow the first few header bytes; flip bits there.
            int position = 3 + random.Next(Math.Min(24, nal.Length - 3));
            nal[position] ^= (byte)(1 << random.Next(8));
            List<ReadOnlyMemory<byte>> mutated = [.. nals];
            mutated[slice] = nal;
            string sequential = Outcome(() => HevcDecoder.Decode(mutated, Reserve, CancellationToken.None));
            string parallelOutcome = Outcome(() => HevcDecoder.Decode(mutated, Reserve, CancellationToken.None, parallel));
            await Assert.That(parallelOutcome).IsEqualTo(sequential);
            failures += sequential.StartsWith("error", StringComparison.Ordinal) ? 1 : 0;
        }

        await Assert.That(expected.Length).IsGreaterThan(0);
        Console.WriteLine($"Entry point mutations: {failures} of 400 rejected.");
    }

    // Plane hashes, or the type and message of the expected error.
    private static string Outcome(Func<HevcFrame> decode)
    {
        try
        {
            return string.Join(',', HevcConformanceTests.PlaneHashes(decode()));
        }
        catch (Exception error) when (error is ImageFormatException or NotSupportedException)
        {
            return $"error {error.GetType().Name}: {error.Message}";
        }
    }

    [Test]
    public async Task ParallelCancellationIsObserved()
    {
        List<ReadOnlyMemory<byte>> nals = HevcConformanceTests.SplitAnnexB(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "hevc", "hm-wpp.hevc")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => HevcDecoder.Decode(nals, null, cancelled.Token, new ParallelOptions { MaxDegreeOfParallelism = 4 }))
            .Throws<OperationCanceledException>();
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
                // Every other stream takes the parallel path (wavefront rows, row-band loop filters).
                HevcDecoder.Decode(nals, Reserve, CancellationToken.None,
                    iteration % 2 == 0 ? null : new ParallelOptions { MaxDegreeOfParallelism = 4 });
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
