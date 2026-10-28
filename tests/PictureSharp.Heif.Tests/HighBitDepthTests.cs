using System.Diagnostics;

namespace PictureSharp.Formats.Heif.Tests;

// PictureSharp 0.9.0: images coded with more than 8 bits decode to Rgba64 with PreserveBitDepth.
public sealed class HighBitDepthTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static IEnumerable<string> TenBitFiles() =>
        ["x265-rgb10.heic", "x265-rgba10.heic", "x265-bt709-full10.heic", "x265-bt2020-limited10.heic"];

    private static Image Load(string file, DecoderOptions? options = null) =>
        Image.Load(File.ReadAllBytes(Path.Combine(Corpus, file)).AsSpan(), (options ?? new DecoderOptions()) with { Configuration = Configuration });

    private static Image LoadWide(string file) => Load(file, new DecoderOptions { PreserveBitDepth = true });

    // The 10-bit code of a sample scaled to 16 bits with round(v * 65535 / 1023); also inverts bit replication.
    private static int To10(ushort value) => ((value * 1023) + 32767) / 65535;

    [Test]
    [MethodDataSource(nameof(TenBitFiles))]
    public async Task TenBitFiles_DecodeToRgba64_RoundingToTheEightBitDecode(string file)
    {
        using Image narrow = Load(file);
        using Image wide = LoadWide(file);
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
        await Assert.That(wide.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(wide.HasAlpha).IsEqualTo(narrow.HasAlpha);
        await Assert.That(HeifDecodeTests.RgbaHash(narrow)).IsEqualTo(HeifDecodeTests.RgbaHash(Load(file)));

        // Both paths round at 10 bits; the 8-bit decode reduces that code, the 16-bit one scales it.
        Rgba32[] expected = narrow.GetPixelSpan<Rgba32>().ToArray();
        Rgba64[] actual = wide.GetPixelSpan<Rgba64>().ToArray();
        int worst = 0, levels = 0;
        var seen = new HashSet<ushort>();
        for (int i = 0; i < expected.Length; i++)
        {
            Rgba32 rounded = actual[i].ToRgba32();
            if (expected[i].A != 0)
            {
                worst = Math.Max(worst, Math.Max(Math.Abs(rounded.R - expected[i].R), Math.Max(Math.Abs(rounded.G - expected[i].G), Math.Abs(rounded.B - expected[i].B))));
            }

            worst = Math.Max(worst, Math.Abs(rounded.A - expected[i].A));
            if (seen.Add(actual[i].G))
            {
                levels++;
            }
        }

        await Assert.That(worst).IsLessThanOrEqualTo(1);
        // Every sample is a 10-bit code scaled to 16 bits.
        foreach (Rgba64 pixel in actual)
        {
            foreach (ushort sample in (ReadOnlySpan<ushort>)[pixel.R, pixel.G, pixel.B, pixel.A])
            {
                if ((((To10(sample) * 65535) + 511) / 1023) != sample)
                {
                    Assert.Fail($"{file}: sample {sample} is not a scaled 10-bit code.");
                }
            }
        }
    }

    [Test]
    public async Task EightBitFiles_StayRgba32_WithPreserveBitDepth()
    {
        using Image image = LoadWide("grid-2x1.heic");
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba32);
        await Assert.That(HeifDecodeTests.RgbaHash(image)).IsEqualTo(HeifDecodeTests.RgbaHash(Load("grid-2x1.heic")));
    }

    [Test]
    public async Task Budget_ChargesSixteenBitPixelsTwice()
    {
        // 96 × 64 colour pixels decoded to Rgba64 count twice; the alpha item of x265-rgba10 is charged by the container.
        long pixels = 96 * 64;
        using (Image image = Load("x265-rgb10.heic", new DecoderOptions { PreserveBitDepth = true, MaxPixels = pixels * 2 }))
        {
            await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        }

        await Assert.That(() => Load("x265-rgb10.heic", new DecoderOptions { PreserveBitDepth = true, MaxPixels = (pixels * 2) - 1 }))
            .Throws<ImageFormatException>();
        using Image narrow = Load("x265-rgb10.heic", new DecoderOptions { MaxPixels = pixels });
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    [Test]
    public async Task Edits_RejectSixteenBitImages()
    {
        using Image wide = LoadWide("x265-rgb10.heic");
        await Assert.That(() => wide.ApplyHeifEdits(new HeifEdits())).Throws<NotSupportedException>();
        using Image narrow = wide.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        using Image edited = narrow.ApplyHeifEdits(new HeifEdits());
        await Assert.That(edited.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    // A smooth 16-bit gradient with more levels than 8 bits hold, optionally with an alpha ramp.
    private static Image WideSample(int width, int height, bool alpha = false)
    {
        var image = new Image(width, height, PixelType.Rgba64, new Rgba64(0, 0, 0, ushort.MaxValue));
        Span<Rgba64> pixels = image.GetPixelSpan<Rgba64>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / (width - 1), v = (double)y / (height - 1);
                pixels[(y * width) + x] = new Rgba64((ushort)(65535 * u), (ushort)(65535 * (0.25 + (0.5 * v))), (ushort)(65535 * (1 - u) * v),
                    alpha ? (ushort)(65535 * (0.2 + (0.8 * u))) : ushort.MaxValue);
            }
        }

        return image;
    }

    private static byte[] Encode(Image image, HeifEncoderOptions? options = null) =>
        image.ToHeic((options ?? new HeifEncoderOptions()) with { Configuration = Configuration });

    private static Image Decode(byte[] heic, bool wide = true) =>
        Image.Load(heic.AsSpan(), new DecoderOptions { Configuration = Configuration, PreserveBitDepth = wide });

    // Largest difference in 10-bit codes over R, G, B (and A).
    private static int MaxError10(Image a, Image b)
    {
        Rgba64[] pa = a.GetPixelSpan<Rgba64>().ToArray(), pb = b.GetPixelSpan<Rgba64>().ToArray();
        int worst = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(To10(pa[i].R) - To10(pb[i].R)), Math.Max(Math.Abs(To10(pa[i].G) - To10(pb[i].G)),
                Math.Max(Math.Abs(To10(pa[i].B) - To10(pb[i].B)), Math.Abs(To10(pa[i].A) - To10(pb[i].A))))));
        }

        return worst;
    }

    [Test]
    public async Task Encoder_SupportsSixteenBitImages()
    {
        var format = new HeifFormat();
        await Assert.That(((IImageFormatModule)format).Encoder!.SupportsPixelType(PixelType.Rgba64)).IsTrue();
        await Assert.That(() => new HeifEncoderOptions { BitDepth = (HeifBitDepth)9 }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TenBitEncoding_RoundTripsAboveEightBitPrecision(bool alpha)
    {
        using Image source = WideSample(256, 96, alpha);
        byte[] heic = Encode(source, new HeifEncoderOptions { Quality = 95 });
        ImageInfo info = Image.Identify(heic.AsSpan(), new DecoderOptions { Configuration = Configuration });
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(10);
        using Image decoded = Decode(heic);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(decoded.HasAlpha).IsEqualTo(alpha);
        await Assert.That(MaxError10(source, decoded)).IsLessThanOrEqualTo(12);
        // More distinct red levels than 8 bits can hold.
        await Assert.That(decoded.GetPixelSpan<Rgba64>().ToArray().Select(p => To10(p.R)).Distinct().Count()).IsGreaterThan(256);
    }

    [Test]
    public async Task BitDepthOption_SelectsTheCodedDepth()
    {
        using Image wide = WideSample(64, 32);
        using Image widened = wide.Clone(x => x.ConvertPixelType(PixelType.Rgba32).ConvertPixelType(PixelType.Rgba64));
        using Image narrow = wide.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        int Bits(Image image, HeifBitDepth depth) =>
            Image.Identify(Encode(image, new HeifEncoderOptions { BitDepth = depth }).AsSpan(), new DecoderOptions { Configuration = Configuration }).Color.BitsPerSample;
        await Assert.That(Bits(wide, HeifBitDepth.Auto)).IsEqualTo(10);
        await Assert.That(Bits(widened, HeifBitDepth.Auto)).IsEqualTo(8);
        await Assert.That(Bits(narrow, HeifBitDepth.Auto)).IsEqualTo(8);
        await Assert.That(Bits(wide, HeifBitDepth.UpTo8)).IsEqualTo(8);
        await Assert.That(Bits(narrow, HeifBitDepth.Ten)).IsEqualTo(10);
        // An exact 8-bit image coded with Auto is the 8-bit image's file.
        await Assert.That(Encode(widened)).IsEquivalentTo(Encode(narrow), CollectionOrdering.Matching);
        await Assert.That(Encode(wide, new HeifEncoderOptions { BitDepth = HeifBitDepth.UpTo8 })).IsEquivalentTo(Encode(narrow), CollectionOrdering.Matching);
    }

    [Test]
    [Property("Category", "Oracle")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HeifDec_ReadsTenBitFilesLikeTheDecoder(bool alpha)
    {
        string tools = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools");
        string heifDec = Path.Combine(tools, "bin", "heif-dec");
        Skip.Unless(File.Exists(heifDec), "Build heif-dec with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-10-").FullName;
        try
        {
            using Image source = WideSample(600, 200, alpha);
            // A grid of 256 tiles exercises tiles, cropping and the 10-bit alpha plane.
            byte[] heic = Encode(source, new HeifEncoderOptions { TileSize = 256 });
            string path = Path.Combine(work, "ten.heic"), png = Path.Combine(work, "ten.png");
            File.WriteAllBytes(path, heic);
            var start = new ProcessStartInfo(heifDec) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(png);
            using (Process process = Process.Start(start)!)
            {
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await Assert.That(process.ExitCode).IsEqualTo(0).Because(error);
            }

            using Image reference = Image.Load(png, new DecoderOptions { PreserveBitDepth = true });
            using Image decoded = Decode(heic);
            Rgba64[] expected = reference.GetPixelSpan<Rgba64>().ToArray(), actual = decoded.GetPixelSpan<Rgba64>().ToArray();
            int mismatches = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                Rgba64 e = expected[i], a = actual[i];
                if ((e.A >> 6) != To10(a.A) || (e.A >> 6) == 1023 && ((e.R >> 6) != To10(a.R) || (e.G >> 6) != To10(a.G) || (e.B >> 6) != To10(a.B)))
                {
                    mismatches++;
                }
            }

            await Assert.That(mismatches).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // heif-dec writes 10-bit images as 16-bit PNG with the codes shifted left (v << 6); our samples are the same codes
    // scaled with round(v * 65535 / 1023). Both reduced to their 10-bit code must be equal.
    [Test]
    [Property("Category", "Oracle")]
    [MethodDataSource(nameof(TenBitFiles))]
    public async Task HeifDec_SixteenBitOutputMatchesAtTenBits(string file)
    {
        string tools = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools");
        string heifDec = Path.Combine(tools, "bin", "heif-dec");
        Skip.Unless(File.Exists(heifDec), "Build heif-dec with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-16-").FullName;
        try
        {
            string png = Path.Combine(work, "out.png");
            var start = new ProcessStartInfo(heifDec) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add(Path.Combine(Corpus, file));
            start.ArgumentList.Add(png);
            using (Process process = Process.Start(start)!)
            {
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await Assert.That(process.ExitCode).IsEqualTo(0).Because(error);
            }

            using Image reference = Image.Load(png, new DecoderOptions { PreserveBitDepth = true });
            using Image wide = LoadWide(file);
            await Assert.That(reference.PixelType).IsEqualTo(PixelType.Rgba64);
            Rgba64[] expected = reference.GetPixelSpan<Rgba64>().ToArray();
            Rgba64[] actual = wide.GetPixelSpan<Rgba64>().ToArray();
            int mismatches = 0;
            string first = "";
            for (int i = 0; i < expected.Length; i++)
            {
                Rgba64 e = expected[i], a = actual[i];
                // heif-dec keeps premultiplied colour as coded; compare colour where it is unaffected.
                bool colour = (e.A >> 6) == 1023;
                if ((e.A >> 6) != To10(a.A) || colour && ((e.R >> 6) != To10(a.R) || (e.G >> 6) != To10(a.G) || (e.B >> 6) != To10(a.B)))
                {
                    if (mismatches++ == 0)
                    {
                        first = $"pixel {i}: heif-dec {e.R},{e.G},{e.B},{e.A}, ours {a.R},{a.G},{a.B},{a.A}";
                    }
                }
            }

            await Assert.That(mismatches).IsEqualTo(0).Because($"{mismatches} of {expected.Length} pixels differ; first {first}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SixteenBitGainMap_IsEncodedAtItsOwnDepth(bool gray)
    {
        using Image original = Load("gainmap-iso.heic", new DecoderOptions { LoadGainMap = true });
        using Image map = WideSample(512, 48);
        if (gray)
        {
            Span<Rgba64> pixels = map.GetPixelSpan<Rgba64>();
            for (int i = 0; i < pixels.Length; i++)
            {
                ushort sample = pixels[i].R;
                pixels[i] = new Rgba64(sample, sample, sample, ushort.MaxValue);
            }
        }

        var format = new ReferenceGainMapFormat(original, original.GetGainMap()!.Metadata, map);
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(format).Build();
        using Image source = Image.Load(ReferenceGainMapFormat.Signature, new DecoderOptions { Configuration = configuration, LoadGainMap = true });
        byte[] heic = Encode(source, new HeifEncoderOptions { GainMap = HeifGainMapMode.Require, GainMapQuality = 100 });
        using Image reloaded = Image.Load(heic.AsSpan(), new DecoderOptions { Configuration = Configuration, LoadGainMap = true, PreserveBitDepth = true });
        // The base remains 8-bit while the attached map is Main 10 / Monochrome 10.
        await Assert.That(reloaded.PixelType).IsEqualTo(PixelType.Rgba32);
        EncodableGainMap gainMap = reloaded.GetGainMap()!;
        await Assert.That(gainMap.Map.PixelType).IsEqualTo(PixelType.Rgba64);
        using Image decoded = gainMap.Map.Decode(-1, default);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(MaxError10(map, decoded)).IsLessThanOrEqualTo(gray ? 4 : 20);
        await Assert.That(decoded.GetPixelSpan<Rgba64>().ToArray().Select(p => p.R).Distinct().Count()).IsGreaterThan(256);
        using HdrImage hdr = reloaded.ToHdr();
        await Assert.That(hdr.Width).IsEqualTo(original.Width);
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
