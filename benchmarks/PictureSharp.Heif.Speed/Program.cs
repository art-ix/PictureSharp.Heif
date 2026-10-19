using System.Diagnostics;
using System.Globalization;
using PictureSharp;
using PictureSharp.Formats;
using PictureSharp.Formats.Heif;
using PictureSharp.Processing;

// Wall-clock speed probe for decoding and encoding (best of N runs, after a warm-up run).
//   decode <file.heic>... [--runs 5] [--threads 1,all]
//   encode <file.heic>... [--runs 2] [--threads 1,all] [--quality 50]
//   hash <file.heic>... [--threads 1,all]    SHA-256 of the decoded Rgba32 pixels (bit-identity checks)
//   make48 <file.heic> <out-grid.heic>   writes the 2x upscaled photo as a 512x512 grid (the 0.5.0 48 MP input)
// Prints one TSV row per measurement: operation, file, threads, best ms, allocated MB per run, output bytes.
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
var configuration = ImageConfiguration.Default;
string mode = args.Length > 0 ? args[0] : "";
var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
int runs = Option("--runs", mode == "encode" ? 2 : 5);
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
        Console.Error.WriteLine("usage: decode|encode <files> [--runs N] [--threads 1,all] [--quality Q] | make48 <in> <out>");
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
        long start = Stopwatch.GetTimestamp();
        action();
        best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    return (best, allocated / 1048576.0);
}

void Row(string operation, string file, string t, double ms, double mb, int bytes) =>
    Console.WriteLine(string.Join('\t', operation, Path.GetFileName(file), t, ms.ToString("F0", CultureInfo.InvariantCulture),
        mb.ToString("F1", CultureInfo.InvariantCulture), bytes.ToString(CultureInfo.InvariantCulture)));

static int Threads(string text) => text == "all" ? -1 : int.Parse(text, CultureInfo.InvariantCulture);

int Option(string name, int fallback) => OptionText(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

string? OptionText(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
