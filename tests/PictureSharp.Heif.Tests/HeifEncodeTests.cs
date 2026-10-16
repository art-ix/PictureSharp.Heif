using System.Diagnostics;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HeifEncodeTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

    // Smooth gradients with edges and mild texture, roughly photo-like.
    internal static Image Sample(int width, int height, bool alpha = false)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / width;
                double v = (double)y / height;
                int r = (int)(255 * u);
                int g = (int)(128 + (100 * Math.Sin((u * 9) + (v * 4))));
                int b = ((x / 24) + (y / 24)) % 2 == 0 ? 200 : 60;
                int a = alpha ? (int)(255 * Math.Clamp(1.2 - Math.Sqrt(((u - 0.5) * (u - 0.5)) + ((v - 0.5) * (v - 0.5))) * 2, 0, 1)) : 255;
                pixels[(y * width) + x] = new Rgba32((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }

        return image;
    }

    internal static double Psnr(Image a, Image b, bool alphaChannel = false)
    {
        var pa = new Rgba32[a.Width * a.Height];
        var pb = new Rgba32[b.Width * b.Height];
        a.CopyPixelDataTo(pa);
        b.CopyPixelDataTo(pb);
        double error = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            if (alphaChannel)
            {
                error += Math.Pow(pa[i].A - pb[i].A, 2);
            }
            else
            {
                error += Math.Pow(pa[i].R - pb[i].R, 2) + Math.Pow(pa[i].G - pb[i].G, 2) + Math.Pow(pa[i].B - pb[i].B, 2);
            }
        }

        double mse = error / (pa.Length * (alphaChannel ? 1 : 3));
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
    }

    private static Image Decode(byte[] heic, DecoderOptions? options = null) =>
        Image.Load(heic.AsSpan(), (options ?? new DecoderOptions()) with { Configuration = Configuration });

    private static byte[] Encode(Image image, HeifEncoderOptions? options = null)
    {
        using var stream = new MemoryStream();
        new HeifFormat().Encoder.Encode(image, stream, options ?? new HeifEncoderOptions(), CancellationToken.None);
        return stream.ToArray();
    }

    [Test]
    [Arguments(50, 34.0)]
    [Arguments(90, 44.0)]
    [Arguments(10, 24.0)]
    public async Task Encode_RoundTripsWithExpectedQuality(int quality, double minimumPsnr)
    {
        using Image original = Sample(160, 96);
        byte[] heic = Encode(original, new HeifEncoderOptions { Quality = quality });
        using Image decoded = Decode(heic);
        await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((160, 96));
        await Assert.That(decoded.HasAlpha).IsFalse();
        await Assert.That(Psnr(original, decoded)).IsGreaterThan(minimumPsnr);
    }

    [Test]
    public async Task Encode_LargeImagesBecomeGridsAndOddSizesAreCropped()
    {
        using Image original = Sample(1100, 601);
        byte[] heic = Encode(original, new HeifEncoderOptions { TileSize = 512 });
        ImageInfo info = Image.Identify(heic.AsMemory(), new DecoderOptions { Configuration = Configuration });
        await Assert.That((info.Width, info.Height, info.ContainerItemCount)).IsEqualTo((1100, 601, 1 + 6));
        using Image decoded = Decode(heic);
        await Assert.That(Psnr(original, decoded)).IsGreaterThan(34.0);

        using Image odd = Sample(101, 57);
        using Image oddDecoded = Decode(Encode(odd));
        await Assert.That((oddDecoded.Width, oddDecoded.Height)).IsEqualTo((101, 57));
        await Assert.That(Psnr(odd, oddDecoded)).IsGreaterThan(32.0);
    }

    [Test]
    public async Task Encode_KeepsAlpha()
    {
        using Image original = Sample(128, 80, alpha: true);
        using Image decoded = Decode(Encode(original, new HeifEncoderOptions { AlphaQuality = 90 }));
        await Assert.That(decoded.HasAlpha).IsTrue();
        await Assert.That(Psnr(original, decoded, alphaChannel: true)).IsGreaterThan(40.0);
    }

    [Test]
    public async Task Encode_WritesOrientationAsTransformsAndCopiesMetadata()
    {
        using Image original = Sample(96, 64);
        original.Metadata.Orientation = ExifOrientation.RightTop;
        original.Metadata.SetBlock(MetadataKinds.Xmp, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8);
        byte[] icc = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "p3_v4_para0.icc"));
        original.Metadata.SetBlock(MetadataKinds.Icc, icc);
        using Image decoded = Decode(Encode(original));
        // RightTop: displayed rotated by 90 degrees clockwise, and the EXIF tag no longer asks for rotation.
        await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((64, 96));
        await Assert.That(decoded.Metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
        await Assert.That(decoded.Metadata.IccProfile!).IsEquivalentTo(icc, CollectionOrdering.Matching);
        await Assert.That(decoded.Metadata.Xmp).IsNotNull();
        using Image oriented = original.Clone();
        oriented.Mutate(x => x.AutoOrient());
        await Assert.That(Psnr(oriented, decoded)).IsGreaterThan(34.0);

        using Image stripped = Decode(Encode(original, new HeifEncoderOptions { Metadata = MetadataKinds.None }));
        await Assert.That(stripped.Metadata.IccProfile).IsNull();
        await Assert.That((stripped.Width, stripped.Height)).IsEqualTo((64, 96));
    }

    [Test]
    public async Task Encode_OutputDoesNotDependOnParallelism()
    {
        using Image original = Sample(700, 300);
        byte[] serial = Encode(original, new HeifEncoderOptions { TileSize = 256, MaxDegreeOfParallelism = 1 });
        byte[] parallel = Encode(original, new HeifEncoderOptions { TileSize = 256, MaxDegreeOfParallelism = -1 });
        await Assert.That(parallel).IsEquivalentTo(serial, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(3, 5)]
    [Arguments(2, 64)]
    [Arguments(513, 1)]
    public async Task Encode_HandlesTinyAndThinImages(int width, int height)
    {
        using var image = new Image(width, height, new Rgba32(10, 200, 90, 255));
        using Image decoded = Decode(Encode(image, new HeifEncoderOptions { Quality = 90 }));
        await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((width, height));
        await Assert.That(Psnr(image, decoded)).IsGreaterThan(40.0);
    }

    [Test]
    public async Task Encode_ObservesCancellationAndUsesTheFirstFrame()
    {
        using Image original = Sample(256, 256);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => new HeifFormat().Encoder.Encode(original, new MemoryStream(), new HeifEncoderOptions(), cancelled.Token))
            .Throws<OperationCanceledException>();

        using Image second = new(256, 256, new Rgba32(0, 0, 255, 255));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), [original, second]);
        using Image decoded = Decode(Encode(animation));
        await Assert.That(Psnr(original, decoded)).IsGreaterThan(34.0);
    }

    [Test]
    public async Task Options_ValidateRanges()
    {
        await Assert.That(() => new HeifEncoderOptions { Quality = 101 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifEncoderOptions { TileSize = 100 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifEncoderOptions { MaxDegreeOfParallelism = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new HeifEncoderOptions { TileSize = 0 }.TileSize).IsEqualTo(0);
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task HeifDec_ReadsEncodedFilesLikeTheDecoder()
    {
        string tools = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools");
        string heifDec = Path.Combine(tools, "bin", "heif-dec");
        Skip.Unless(File.Exists(heifDec), "Build heif-dec with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-encode-").FullName;
        try
        {
            var cases = new (string Name, Image Image, HeifEncoderOptions Options)[]
            {
                ("single", Sample(160, 96), new HeifEncoderOptions()),
                ("odd", Sample(101, 57), new HeifEncoderOptions { Quality = 70 }),
                ("grid", Sample(1100, 601), new HeifEncoderOptions { TileSize = 512, Quality = 40 }),
                ("alpha", Sample(128, 80, alpha: true), new HeifEncoderOptions()),
                ("alpha-grid", Sample(600, 200, alpha: true), new HeifEncoderOptions { TileSize = 256 }),
            };
            Image rotated = Sample(96, 64);
            rotated.Metadata.Orientation = ExifOrientation.LeftBottom;
            Image mirrored = Sample(96, 64);
            mirrored.Metadata.Orientation = ExifOrientation.RightBottom;
            Image alphaRotated = Sample(96, 64, alpha: true);
            alphaRotated.Metadata.Orientation = ExifOrientation.RightTop;
            cases = [.. cases, ("rotated", rotated, new HeifEncoderOptions()), ("transverse", mirrored, new HeifEncoderOptions()),
                ("alpha-rotated", alphaRotated, new HeifEncoderOptions())];
            foreach ((string name, Image image, HeifEncoderOptions options) in cases)
            {
                using (image)
                {
                    string path = Path.Combine(work, name + ".heic");
                    File.WriteAllBytes(path, Encode(image, options));
                    string png = Path.Combine(work, name + ".png");
                    var start = new ProcessStartInfo(heifDec) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    start.ArgumentList.Add("--quiet");
                    start.ArgumentList.Add(path);
                    start.ArgumentList.Add(png);
                    using (Process process = Process.Start(start)!)
                    {
                        string error = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        await Assert.That(process.ExitCode).IsEqualTo(0).Because(name + ": " + error);
                    }

                    using Image reference = Image.Load(png);
                    using Image ours = Decode(File.ReadAllBytes(path));
                    await Assert.That((ours.Width, ours.Height)).IsEqualTo((reference.Width, reference.Height)).Because(name);
                    await Assert.That(HeifDecodeTests.RgbaHash(ours)).IsEqualTo(HeifDecodeTests.RgbaHash(reference)).Because(name);
                }
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
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
