using System.Globalization;

namespace PictureSharp.Formats.Heif.Tests;

// Renders every hand-annotated Photos export in samples/edit/<scene>/edits.tsv from its original (reduced to at most
// 2 MP) and compares it with the export (RMS over 8×8 block means of the 8-bit RGB difference, which suppresses HEVC
// noise). The closest sliders must beat the unedited photograph on every pair; the rest are reported until refined.
public sealed class HeifPhotosEditLocalTests
{
    private static readonly string[] Determined = ["warmth", "tint", "vignette", "brightness", "contrast"];
    // Decoded within 1632 × 1632 (at most 2 MP for 4:3 photos) to keep the run short; the export and the original
    // are reduced the same way.
    private static readonly DecoderOptions Options = new()
    {
        Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build(),
        TargetSize = new System.Drawing.Size(1632, 1632),
    };

    [Test]
    [Property("Category", "Local")]
    public async Task PrivateEdits_RenderCloserToPhotosThanTheOriginal()
    {
        string folder = Path.Combine(HevcLocalTests.FindRoot(), "samples", "edit");
        string[] tables = Directory.Exists(folder) ? Directory.GetFiles(folder, "edits.tsv", SearchOption.AllDirectories) : [];
        Skip.Unless(tables.Length > 0, "No annotated Photos edits in samples/edit/.");
        var failures = new List<string>();
        Console.WriteLine("scene\texport\tadjustment\tvalue\tbaseline_rms8\trender_rms8");
        foreach (string table in tables.Order(StringComparer.Ordinal))
        {
            string scene = Path.GetDirectoryName(table)!;
            var rows = File.ReadAllLines(table).Where(line => line.Length > 0 && line[0] != '#').Skip(1)
                .Select(line => line.Split('\t')).ToArray();
            foreach (var group in rows.GroupBy(row => row[1]))
            {
                using Image original = Image.Load(Path.Combine(scene, group.Key), Options);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                TestRender.Adjust(original, All).Dispose();
                Console.WriteLine($"# {group.Key}: {original.Width}x{original.Height}, all sliders {clock.Elapsed.TotalSeconds:F2} s; "
                    + $"tones only {Time(original, All with { Sharpness = 0, Definition = 0, NoiseReduction = 0, Brilliance = 0, Shadows = 0 }):F2} s");
                foreach (string[] row in group)
                {
                    using Image export = Image.Load(Path.Combine(scene, row[0]), Options);
                    float value = float.Parse(row[3], CultureInfo.InvariantCulture);
                    using Image rendered = TestRender.Adjust(original, Adjust(row[2], value));
                    double baseline = Rms8(original, export), error = Rms8(rendered, export);
                    Console.WriteLine($"{Path.GetFileName(scene)}\t{row[0]}\t{row[2]}\t{row[3]}\t{baseline:F3}\t{error:F3}");
                    if (Determined.Contains(row[2]) && error >= baseline)
                    {
                        failures.Add($"{row[0]}: {error:F2} >= {baseline:F2}");
                    }
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    private static readonly HeifPhotosAdjustments All = new()
    {
        Exposure = 20, Brilliance = 30, Highlights = -20, Shadows = 25, Contrast = 10, Brightness = 15, BlackPoint = 5,
        Saturation = 10, Vibrance = 20, Warmth = 10, Tint = -5, Sharpness = 30, Definition = 20, NoiseReduction = 10,
        Vignette = 25,
    };

    private static double Time(Image photo, HeifPhotosAdjustments adjustments)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TestRender.Adjust(photo, adjustments).Dispose();
        return clock.Elapsed.TotalSeconds;
    }

    private static HeifPhotosAdjustments Adjust(string adjustment, float value) => adjustment switch
    {
        "exposure" => new() { Exposure = value },
        "brilliance" => new() { Brilliance = value },
        "highlights" => new() { Highlights = value },
        "shadows" => new() { Shadows = value },
        "contrast" => new() { Contrast = value },
        "brightness" => new() { Brightness = value },
        "black_point" => new() { BlackPoint = value },
        "saturation" => new() { Saturation = value },
        "vibrance" => new() { Vibrance = value },
        "warmth" => new() { Warmth = value },
        "tint" => new() { Tint = value },
        "sharpness" => new() { Sharpness = value },
        "definition" => new() { Definition = value },
        "noise_reduction" => new() { NoiseReduction = value },
        "vignette" => new() { Vignette = value },
        _ => throw new InvalidDataException("Unknown adjustment in edits.tsv: " + adjustment),
    };

    private static double Rms8(Image a, Image b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new InvalidDataException($"Size mismatch {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
        }

        int blocksX = a.Width / 8, blocksY = a.Height / 8;
        double sum = 0;
        var block = new double[blocksX * 3];
        for (int by = 0; by < blocksY; by++)
        {
            Array.Clear(block);
            for (int y = by * 8; y < by * 8 + 8; y++)
            {
                ReadOnlySpan<Rgba32> ra = a.GetRowSpan(y), rb = b.GetRowSpan(y);
                for (int x = 0; x < blocksX * 8; x++)
                {
                    int i = x / 8 * 3;
                    block[i] += ra[x].R - rb[x].R;
                    block[i + 1] += ra[x].G - rb[x].G;
                    block[i + 2] += ra[x].B - rb[x].B;
                }
            }

            foreach (double d in block)
            {
                sum += d / 64 * (d / 64);
            }
        }

        return Math.Sqrt(sum / (blocksX * blocksY * 3));
    }
}
