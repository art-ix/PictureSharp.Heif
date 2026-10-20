using System.Diagnostics;
using System.Globalization;
using PictureSharp;
using PictureSharp.Formats;
using PictureSharp.Formats.Heif;
using PictureSharp.Processing;

// Wall-clock speed probe for decoding and encoding (best of N runs, after a warm-up run).
//   decode <file.heic>... [--runs 5] [--threads 1,all]
//   encode <file.heic>... [--runs 2] [--threads 1,all] [--quality 50]
//   quality <file.png>... [--runs 2] [--threads 1]  size, RGB PSNR, luma SSIM dB and time at Q30/45/60/75
//   hash <file.heic>... [--threads 1,all]    SHA-256 of the decoded Rgba32 pixels (bit-identity checks)
//   encodehash <file.heic>... [--quality 50]  SHA-256 of the encoded file (byte-identity checks)
//   make48 <file.heic> <out-grid.heic>   writes the 2x upscaled photo as a 512x512 grid (the 0.5.0 48 MP input)
//   portrait <file.heic>... [--runs 3] [--threads 1,all] [--scale 1] [--aperture N]  HeifPortrait.Render time, allocation
//     and working-set peak above the loaded inputs; --scale resizes the photograph (√2 ≈ 24 MP, 2 ≈ 48 MP from 12 MP)
//   portraithash <file.heic>... [--threads 1,all] [--scale 1]  SHA-256 of portrait renders (bit-identity checks)
// Prints one TSV row per measurement: operation, file, threads, best ms, allocated MB per run, output bytes.
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
var configuration = ImageConfiguration.Default;
string mode = args.Length > 0 ? args[0] : "";
var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
int runs = Option("--runs", mode is "encode" or "quality" ? 2 : mode is "portrait" ? 3 : 5);
int quality = Option("--quality", 50);
string[] threads = (OptionText("--threads") ?? "1,all").Split(',');

switch (mode)
{
    case "decode":
        Console.WriteLine("operation\tfile\tthreads\tms\talloc_mb\tbytes");
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            foreach (string t in threads)
            {
                var options = new DecoderOptions { Configuration = configuration, MaxDegreeOfParallelism = Threads(t) };
                (double ms, double mb) = Measure(() => Image.Load(bytes.AsSpan(), options).Dispose());
                Row("decode", file, t, ms, mb, 0);
            }
        }

        break;
    case "encode":
        Console.WriteLine("operation\tfile\tthreads\tms\talloc_mb\tbytes");
        foreach (string file in files)
        {
            using Image image = Image.Load(File.ReadAllBytes(file).AsSpan(), new DecoderOptions { Configuration = configuration });
            image.Metadata.Clear();
            foreach (string t in threads)
            {
                var options = new HeifEncoderOptions { Quality = quality, MaxDegreeOfParallelism = Threads(t) };
                int length = 0;
                (double ms, double mb) = Measure(() => length = image.ToHeic(options).Length);
                Row("encode", file, t, ms, mb, length);
            }
        }

        break;
    case "quality":
        Console.WriteLine("image\tquality\tbytes\tpsnr_rgb\tssim_db\tms");
        foreach (string file in files)
        {
            using Image image = Image.Load(file, new DecoderOptions { Configuration = configuration });
            image.Metadata.Clear();
            foreach (int q in new[] { 30, 45, 60, 75 })
            {
                byte[] encoded = [];
                var options = new HeifEncoderOptions { Quality = q, MaxDegreeOfParallelism = Threads(threads[0]) };
                (double ms, _) = Measure(() => encoded = image.ToHeic(options));
                using Image decoded = Image.Load(encoded.AsSpan(), new DecoderOptions { Configuration = configuration });
                Console.WriteLine(FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(file)}\t{q}\t{encoded.Length}\t{QualityMetrics.Psnr(image, decoded):F6}\t{QualityMetrics.SsimDb(image, decoded):F6}\t{ms:F3}"));
            }
        }

        break;
    case "hash":
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            foreach (string t in threads)
            {
                using Image image = Image.Load(bytes.AsSpan(), new DecoderOptions { Configuration = configuration, MaxDegreeOfParallelism = Threads(t) });
                var pixels = new Rgba32[image.Width * image.Height];
                image.CopyPixelDataTo(pixels);
                byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
                Console.WriteLine($"{Path.GetFileName(file)}\t{t}\t{image.Width}x{image.Height}\t{Convert.ToHexStringLower(hash)}");
            }
        }

        break;
    case "encodehash":
        foreach (string file in files)
        {
            using Image image = Image.Load(File.ReadAllBytes(file).AsSpan(), new DecoderOptions { Configuration = configuration });
            image.Metadata.Clear();
            byte[] encoded = image.ToHeic(new HeifEncoderOptions { Quality = quality });
            Console.WriteLine($"{Path.GetFileName(file)}\tq{quality}\t{encoded.Length}\t{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(encoded))}");
        }

        break;
    case "portrait":
        Console.WriteLine("operation\tfile\tthreads\tmegapixels\tms\talloc_mb\tpeak_mb");
        foreach (string file in files)
        {
            (Image photo, HeifDepthImage depth, Image? matte) = LoadPortrait(file, Scale());
            using (photo)
            using (matte)
            {
                foreach (string t in threads)
                {
                    var portrait = new HeifPortraitOptions { Aperture = Aperture(), MaxDegreeOfParallelism = Threads(t) };
                    // The working set high-water mark of the first render above the loaded inputs (Linux: reset by
                    // clear_refs); later renders reuse pooled buffers, so measure one thread setting per process.
                    long before = ResetPeak();
                    HeifPortrait.Render(photo, depth, matte, portrait).Dispose();
                    double peak = (PeakWorkingSet() - before) / 1048576.0;
                    (double ms, double mb) = Measure(() => HeifPortrait.Render(photo, depth, matte, portrait).Dispose());
                    Console.WriteLine(string.Join('\t', "portrait", Path.GetFileName(file), t,
                        (photo.Width * (double)photo.Height / 1e6).ToString("F1", CultureInfo.InvariantCulture),
                        ms.ToString("F0", CultureInfo.InvariantCulture), mb.ToString("F1", CultureInfo.InvariantCulture),
                        peak.ToString("F0", CultureInfo.InvariantCulture)));
                }
            }
        }

        break;
    case "portraithash":
        // Default focus at f/1.4, f/4.5 and f/16, and the picture centre at f/2.8.
        foreach (string file in files)
        {
            (Image photo, HeifDepthImage depth, Image? matte) = LoadPortrait(file, Scale());
            using (photo)
            using (matte)
            {
                foreach (HeifPortraitOptions variant in (HeifPortraitOptions[])[new() { Aperture = 1.4f }, new() { Aperture = 4.5f },
                    new() { Aperture = 16f }, new() { Aperture = 2.8f, FocusPoint = new System.Drawing.Point(photo.Width / 2, photo.Height / 2) }])
                {
                    foreach (string t in threads)
                    {
                        using Image rendered = HeifPortrait.Render(photo, depth, matte, variant with { MaxDegreeOfParallelism = Threads(t) });
                        var pixels = new Rgba32[rendered.Width * rendered.Height];
                        rendered.CopyPixelDataTo(pixels);
                        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
                        string focus = variant.FocusPoint is null ? "default" : "centre";
                        Console.WriteLine(FormattableString.Invariant($"{Path.GetFileName(file)}\tf{variant.Aperture}\t{focus}\t{t}\t{Convert.ToHexStringLower(hash)}"));
                    }
                }
            }
        }

        break;
    case "make48":
    {
        using Image image = Image.Load(File.ReadAllBytes(files[0]).AsSpan(), new DecoderOptions { Configuration = configuration });
        image.Metadata.Clear();
        image.Mutate(x => x.Resize(image.Width * 2, image.Height * 2));
        File.WriteAllBytes(files[1], image.ToHeic(new HeifEncoderOptions { Quality = quality }));
        image.Save(Path.ChangeExtension(files[1], ".png"));
        break;
    }

    default:
        Console.Error.WriteLine("usage: decode|encode|quality <files> [--runs N] [--threads 1,all] [--quality Q] | make48 <in> <out>");
        return 1;
}

return 0;

(double Ms, double Mb) Measure(Action action)
{
    action();
    double best = double.MaxValue;
    long allocated = 0;
    for (int i = 0; i < runs; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan pausedBefore = GC.GetTotalPauseDuration();
        int collectionsBefore = GC.CollectionCount(0);
        long start = Stopwatch.GetTimestamp();
        action();
        best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        if (Environment.GetEnvironmentVariable("SPEED_GC") is not null)
        {
            Console.Error.WriteLine($"gc: {GC.CollectionCount(0) - collectionsBefore} gen0 collections, {(GC.GetTotalPauseDuration() - pausedBefore).TotalMilliseconds:F0} ms paused");
        }
    }

    return (best, allocated / 1048576.0);
}

void Row(string operation, string file, string t, double ms, double mb, int bytes) =>
    Console.WriteLine(string.Join('\t', operation, Path.GetFileName(file), t, ms.ToString("F0", CultureInfo.InvariantCulture),
        mb.ToString("F1", CultureInfo.InvariantCulture), bytes.ToString(CultureInfo.InvariantCulture)));

// The photograph (resized by scale, depth and matte kept: the renderer resamples them), depth and matte of a portrait.
(Image Photo, HeifDepthImage Depth, Image? Matte) LoadPortrait(string file, double scale)
{
    HeifDocument document = HeifDocument.Open(file);
    HeifItemInfo item = document.PrimaryImage;
    HeifDepthImage depth = document.LoadDepth(item.FindDepthImage() ?? throw new ArgumentException($"{file} has no depth image."));
    Image photo = document.LoadImage(item);
    Image? matte = item.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte) is { IsSupported: true } matteItem
        ? document.LoadImage(matteItem)
        : null;
    if (scale != 1)
    {
        photo.Mutate(x => x.Resize((int)Math.Round(photo.Width * scale), (int)Math.Round(photo.Height * scale)));
    }

    return (photo, depth, matte);
}

double Scale() => OptionText("--scale") is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : 1;

float? Aperture() => OptionText("--aperture") is { } text ? float.Parse(text, CultureInfo.InvariantCulture) : null;

static long ResetPeak()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    if (OperatingSystem.IsLinux())
    {
        File.WriteAllText("/proc/self/clear_refs", "5");
    }

    return Environment.WorkingSet;
}

static long PeakWorkingSet()
{
    using var process = Process.GetCurrentProcess();
    return process.PeakWorkingSet64;
}

static int Threads(string text) => text == "all" ? -1 : int.Parse(text, CultureInfo.InvariantCulture);

int Option(string name, int fallback) => OptionText(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

string? OptionText(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
