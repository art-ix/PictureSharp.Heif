using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PictureSharp.Formats.Heif.Tests;

// The 0.3.0 criterion: at equal RGB PSNR, PictureSharp.Heif files are at most 1.5x the size of x265 'medium' files
// (heif-enc), measured as the Bjøntegaard delta rate over four quality points on crops of the private photos.
public sealed class HeifEncoderMeasurementTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

    [Test]
    [Property("Category", "Local")]
    public async Task BdRate_AgainstX265Medium()
    {
        string root = Root();
        string[] samples = Directory.Exists(Path.Combine(root, "samples"))
            ? [.. Directory.GetFiles(Path.Combine(root, "samples")).Where(p => Path.GetExtension(p).Equals(".heic", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)]
            : [];
        Skip.Unless(samples.Length > 0 && File.Exists(HeifEnc()), "Needs private samples and heif-enc.");
        var images = new List<(string Name, Image Image)>();
        foreach (string sample in samples)
        {
            using Image photo = Image.Load(File.ReadAllBytes(sample).AsSpan(), new DecoderOptions { Configuration = Configuration });
            // A 768x512 crop from the centre keeps the run short and avoids the private photo's metadata.
            Image crop = photo.Clone();
            crop.Mutate(x => x.Crop(new System.Drawing.Rectangle((photo.Width - 768) / 2, (photo.Height - 512) / 2, 768, 512)));
            crop.Metadata.Clear();
            images.Add((Path.GetFileNameWithoutExtension(sample), crop));
        }

        (double psnr, double ssim) = await Measure(images, Path.Combine(root, "docs", "measurements", "encoder-bdrate-x265-medium.tsv"));
        // Size ratio 1.5 at equal quality is a BD-rate of +50%.
        await Assert.That(psnr).IsLessThanOrEqualTo(50.0);
        await Assert.That(ssim).IsLessThanOrEqualTo(50.0);
    }

    // The public Kodak suite (scripts/fetch-kodak.py --approved), whose results can be published.
    [Test]
    [Property("Category", "Measurement")]
    public async Task BdRate_Kodak_AgainstX265Medium()
    {
        string root = Root();
        string directory = Path.Combine(root, "tests", "assets", "external", "kodak");
        string[] files = Directory.Exists(directory) ? [.. Directory.GetFiles(directory, "kodim*.png").Order(StringComparer.Ordinal)] : [];
        Skip.Unless(files.Length == 24 && File.Exists(HeifEnc()), "Needs the Kodak suite and heif-enc.");
        var images = files.Select(f => (Path.GetFileNameWithoutExtension(f), Image.Load(f))).ToList();
        (double psnr, double ssim) = await Measure(images, Path.Combine(root, "docs", "measurements", "encoder-bdrate-kodak-x265-medium.tsv"));
        await Assert.That(psnr).IsLessThanOrEqualTo(50.0);
        await Assert.That(ssim).IsLessThanOrEqualTo(50.0);
    }

    // SSIM of BT.601 luma over 8x8 windows (stride 4, C1/C2 from Wang et al.), reported as -10 log10(1 - SSIM).
    internal static double SsimDb(Image a, Image b)
    {
        var pa = new Rgba32[a.Width * a.Height];
        var pb = new Rgba32[b.Width * b.Height];
        a.CopyPixelDataTo(pa);
        b.CopyPixelDataTo(pb);
        static double Luma(Rgba32 p) => (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
        const double c1 = 6.5025;
        const double c2 = 58.5225;
        double total = 0;
        int windows = 0;
        for (int y = 0; y + 8 <= a.Height; y += 4)
        {
            for (int x = 0; x + 8 <= a.Width; x += 4)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double va = Luma(pa[((y + j) * a.Width) + x + i]);
                        double vb = Luma(pb[((y + j) * a.Width) + x + i]);
                        sa += va;
                        sb += vb;
                        saa += va * va;
                        sbb += vb * vb;
                        sab += va * vb;
                    }
                }

                double ma = sa / 64, mb = sb / 64;
                double va2 = (saa / 64) - (ma * ma), vb2 = (sbb / 64) - (mb * mb), cov = (sab / 64) - (ma * mb);
                total += ((2 * ma * mb) + c1) * ((2 * cov) + c2) / (((ma * ma) + (mb * mb) + c1) * (va2 + vb2 + c2));
                windows++;
            }
        }

        return -10 * Math.Log10(1 - (total / windows));
    }

    // Sweeps the adaptive quantization strength on Kodak; prints BD-rates per strength (x265 is encoded once).
    [Test]
    [Property("Category", "Measurement")]
    public async Task Sweep_AdaptiveQuantization_Kodak()
    {
        string root = Root();
        string directory = Path.Combine(root, "tests", "assets", "external", "kodak");
        string[] files = Directory.Exists(directory) ? [.. Directory.GetFiles(directory, "kodim*.png").Order(StringComparer.Ordinal)] : [];
        Skip.Unless(files.Length == 24 && File.Exists(HeifEnc()) && Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_SWEEP") == "1",
            "Set PICTURESHARP_HEIF_SWEEP=1 with the Kodak suite and heif-enc.");
        float[] strengths = [0f, 0.5f, 1f, 1.5f, 2f];
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-sweep-").FullName;
        var psnr = new double[strengths.Length];
        var ssim = new double[strengths.Length];
        try
        {
            foreach (string file in files)
            {
                using Image image = Image.Load(file);
                var x265 = new List<(double, double)>();
                var x265Ssim = new List<(double, double)>();
                foreach (int quality in new[] { 30, 45, 60, 75 })
                {
                    string output = Path.Combine(work, "x265.heic");
                    await Run(HeifEnc(), ["-e", "x265", "-q", quality.ToString(CultureInfo.InvariantCulture), "-p", "preset=medium", "-p", "x265:pools=none", "-o", output, file]);
                    byte[] reference = File.ReadAllBytes(output);
                    using Image decoded = Image.Load(reference.AsSpan(), new DecoderOptions { Configuration = Configuration });
                    x265.Add((reference.Length, HeifEncodeTests.Psnr(image, decoded)));
                    x265Ssim.Add((reference.Length, SsimDb(image, decoded)));
                }

                for (int s = 0; s < strengths.Length; s++)
                {
                    var ours = new List<(double, double)>();
                    var oursSsim = new List<(double, double)>();
                    foreach (int quality in new[] { 30, 45, 60, 75 })
                    {
                        using var stream = new MemoryStream();
                        new HeifFormat().Encoder.Encode(image, stream, new HeifEncoderOptions { Quality = quality, AdaptiveQuantizationStrength = strengths[s] }, CancellationToken.None);
                        using Image decoded = Image.Load(stream.ToArray().AsSpan(), new DecoderOptions { Configuration = Configuration });
                        ours.Add((stream.Length, HeifEncodeTests.Psnr(image, decoded)));
                        oursSsim.Add((stream.Length, SsimDb(image, decoded)));
                    }

                    psnr[s] += BjontegaardRate(x265, ours) / files.Length;
                    ssim[s] += BjontegaardRate(x265Ssim, oursSsim) / files.Length;
                }
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        for (int s = 0; s < strengths.Length; s++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"AQ {strengths[s]:F1}: BD-rate PSNR {psnr[s]:F2}%, SSIM {ssim[s]:F2}%"));
        }

        await Assert.That(strengths.Length).IsEqualTo(5);
    }

    private static string HeifEnc() =>
        Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools"), "bin", "heif-enc");

    // Encodes every image with PictureSharp.Heif and with heif-enc (x265 medium) at qualities 30/45/60/75, writes a TSV
    // report and returns the average BD-rates on RGB PSNR and luma SSIM. The images are disposed.
    private static async Task<(double Psnr, double Ssim)> Measure(List<(string Name, Image Image)> images, string reportPath)
    {
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-bd-").FullName;
        var report = new StringBuilder("image\tencoder\tquality\tbytes\tpsnr_rgb\tssim_db\n");
        var deltas = new List<double>();
        var ssimDeltas = new List<double>();
        try
        {
            foreach ((string name, Image crop) in images)
            {
                using Image image = crop;
                string png = Path.Combine(work, "input.png");
                image.Save(png);
                var ours = new List<(double Rate, double Psnr)>();
                var x265 = new List<(double Rate, double Psnr)>();
                var oursSsim = new List<(double Rate, double Psnr)>();
                var x265Ssim = new List<(double Rate, double Psnr)>();
                foreach (int quality in new[] { 30, 45, 60, 75 })
                {
                    using var stream = new MemoryStream();
                    new HeifFormat().Encoder.Encode(image, stream, new HeifEncoderOptions { Quality = quality }, CancellationToken.None);
                    byte[] encoded = stream.ToArray();
                    using (Image decoded = Image.Load(encoded.AsSpan(), new DecoderOptions { Configuration = Configuration }))
                    {
                        ours.Add((encoded.Length, HeifEncodeTests.Psnr(image, decoded)));
                        oursSsim.Add((encoded.Length, SsimDb(image, decoded)));
                    }

                    string output = Path.Combine(work, "x265.heic");
                    await Run(HeifEnc(), ["-e", "x265", "-q", quality.ToString(CultureInfo.InvariantCulture), "-p", "preset=medium",
                        "-p", "x265:pools=none", "--no-thumb-alpha", "-o", output, png]);
                    byte[] reference = File.ReadAllBytes(output);
                    using (Image decoded = Image.Load(reference.AsSpan(), new DecoderOptions { Configuration = Configuration }))
                    {
                        x265.Add((reference.Length, HeifEncodeTests.Psnr(image, decoded)));
                        x265Ssim.Add((reference.Length, SsimDb(image, decoded)));
                    }

                    report.Append(CultureInfo.InvariantCulture, $"{name}\tpicturesharp\t{quality}\t{ours[^1].Rate}\t{ours[^1].Psnr:F3}\t{oursSsim[^1].Psnr:F3}\n");
                    report.Append(CultureInfo.InvariantCulture, $"{name}\tx265-medium\t{quality}\t{x265[^1].Rate}\t{x265[^1].Psnr:F3}\t{x265Ssim[^1].Psnr:F3}\n");
                }

                double delta = BjontegaardRate(x265, ours);
                double ssimDelta = BjontegaardRate(x265Ssim, oursSsim);
                deltas.Add(delta);
                ssimDeltas.Add(ssimDelta);
                report.Append(CultureInfo.InvariantCulture, $"{name}\tbd-rate\t\t\t{delta:F2}%\t{ssimDelta:F2}%\n");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        double average = deltas.Average();
        double ssimAverage = ssimDeltas.Average();
        report.Append(CultureInfo.InvariantCulture, $"average\tbd-rate\t\t\t{average:F2}%\t{ssimAverage:F2}%\n");
        Console.WriteLine(report.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, report.ToString());
        return (average, ssimAverage);
    }

    [Test]
    [Property("Category", "Local")]
    public async Task FullPhoto_EncodesAndHeifDecReadsIt()
    {
        string root = Root();
        string heifDec = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools"), "bin", "heif-dec");
        string? sample = Directory.Exists(Path.Combine(root, "samples"))
            ? Directory.GetFiles(Path.Combine(root, "samples")).Where(p => Path.GetExtension(p).Equals(".heic", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        Skip.Unless(sample is not null && File.Exists(heifDec), "Needs a private sample and heif-dec.");
        using Image photo = Image.Load(File.ReadAllBytes(sample!).AsSpan(), new DecoderOptions { Configuration = Configuration });
        var stopwatch = Stopwatch.StartNew();
        using var stream = new MemoryStream();
        new HeifFormat().Encoder.Encode(photo, stream, new HeifEncoderOptions(), CancellationToken.None);
        stopwatch.Stop();
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-full-").FullName;
        try
        {
            string path = Path.Combine(work, "photo.heic");
            File.WriteAllBytes(path, stream.ToArray());
            string png = Path.Combine(work, "photo.png");
            await Run(heifDec, ["--quiet", path, png]);
            using Image reference = Image.Load(png);
            using Image ours = Image.Load(stream.ToArray().AsSpan(), new DecoderOptions { Configuration = Configuration });
            await Assert.That(HeifDecodeTests.RgbaHash(ours)).IsEqualTo(HeifDecodeTests.RgbaHash(reference));
            Console.WriteLine($"Full photo {photo.Width}x{photo.Height}: {stream.Length} bytes in {stopwatch.ElapsedMilliseconds} ms, PSNR {HeifEncodeTests.Psnr(photo, ours):F2} dB.");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Bjøntegaard delta rate (VCEG-M33): cubic fit of log rate over PSNR, averaged over the common PSNR interval.
    internal static double BjontegaardRate(List<(double Rate, double Psnr)> anchor, List<(double Rate, double Psnr)> test)
    {
        double[] fitA = Cubic([.. anchor.Select(p => p.Psnr)], [.. anchor.Select(p => Math.Log(p.Rate))]);
        double[] fitB = Cubic([.. test.Select(p => p.Psnr)], [.. test.Select(p => Math.Log(p.Rate))]);
        double low = Math.Max(anchor.Min(p => p.Psnr), test.Min(p => p.Psnr));
        double high = Math.Min(anchor.Max(p => p.Psnr), test.Max(p => p.Psnr));
        double integralA = Integral(fitA, high) - Integral(fitA, low);
        double integralB = Integral(fitB, high) - Integral(fitB, low);
        return (Math.Exp((integralB - integralA) / (high - low)) - 1) * 100;
    }

    private static double Integral(double[] c, double x) => (c[0] * x) + (c[1] * x * x / 2) + (c[2] * x * x * x / 3) + (c[3] * x * x * x * x / 4);

    // Least-squares polynomial of degree 3 (exact for four points).
    private static double[] Cubic(double[] x, double[] y)
    {
        var a = new double[4, 5];
        for (int i = 0; i < x.Length; i++)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    a[r, c] += Math.Pow(x[i], r + c);
                }

                a[r, 4] += y[i] * Math.Pow(x[i], r);
            }
        }

        for (int p = 0; p < 4; p++)
        {
            int pivot = p;
            for (int r = p + 1; r < 4; r++)
            {
                if (Math.Abs(a[r, p]) > Math.Abs(a[pivot, p]))
                {
                    pivot = r;
                }
            }

            for (int c = 0; c < 5; c++)
            {
                (a[p, c], a[pivot, c]) = (a[pivot, c], a[p, c]);
            }

            for (int r = 0; r < 4; r++)
            {
                if (r != p)
                {
                    double factor = a[r, p] / a[p, p];
                    for (int c = p; c < 5; c++)
                    {
                        a[r, c] -= factor * a[p, c];
                    }
                }
            }
        }

        return [a[0, 4] / a[0, 0], a[1, 4] / a[1, 1], a[2, 4] / a[2, 2], a[3, 4] / a[3, 3]];
    }

    private static async Task Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(await output + await error);
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
