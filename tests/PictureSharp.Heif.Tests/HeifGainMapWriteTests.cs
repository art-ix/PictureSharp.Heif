using System.Diagnostics;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;

namespace PictureSharp.Formats.Heif.Tests;

// Writing HDR gain maps (0.6.10): the map attached to the image, read through the core's GetGainMap, becomes a tmap item
// and a hidden map. Reloaded files carry the same ISO metadata; this plugin's unedited maps are copied byte for byte,
// other maps come back within the budget of a re-encoded map.
public sealed class HeifGainMapWriteTests
{
    // Largest difference of a re-encoded map at the default GainMapQuality (90, QP 5) on the corpus and edited maps:
    // gray maps measured 1. RGB maps are coded 4:2:0, so the synthetic map, a different colour in every sample,
    // measured 14 (mean 5.6) at every quality from 70 to 100.
    private const int GrayBudget = 3;
    private const int RgbBudget = 16;

    private static int Budget(string file) => file.Contains("-rgb", StringComparison.Ordinal) ? RgbBudget : GrayBudget;

    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

    private static readonly DecoderOptions Hdr = new() { Configuration = Configuration, LoadGainMap = true };

    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");

    private static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Corpus, file));

    private static HeifEncoderOptions Options(HeifGainMapMode mode) => new() { GainMap = mode, Configuration = Configuration };

    public static IEnumerable<string> IsoFiles() => ["gainmap-iso.heic", "gainmap-iso-rgb.heic", "gainmap-both-rotated.heic"];

    private static (uint Id, string Type, byte[] Data)[] Items(byte[] file, string type) =>
        [.. HeifTestItems.Payloads(file).Where(item => item.Type == type)];

    private static byte[] MapNals(byte[] file)
    {
        // The map is the hvc1 item (or the grid's tiles) the tmap item derives from second; in our files the only
        // hidden image apart from grid tiles, written after the primary image and its alpha.
        uint tmap = Items(file, "tmap").Single().Id;
        return [.. HeifTestItems.Extract(file).Where(item => item.Id == tmap - 1).SelectMany(item => item.Nals.SelectMany(nal => nal.ToArray()))];
    }

    private static Image DecodeMap(Image image) => image.GetGainMap()!.Map.Decode(-1, default);

    private static int Difference(Image a, Image b)
    {
        Span<Rgba32> x = a.GetPixelSpan();
        Span<Rgba32> y = b.GetPixelSpan();
        int worst = 0;
        for (int i = 0; i < x.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(x[i].R - y[i].R), Math.Max(Math.Abs(x[i].G - y[i].G), Math.Abs(x[i].B - y[i].B))));
        }

        return worst;
    }

    private static async Task AssertReloads(Image source, byte[] written, int budget, string name)
    {
        using Image reloaded = Image.Load(written, Hdr);
        await Assert.That(reloaded.SourceFormat).IsEqualTo(new HeifFormat().Format);
        await Assert.That(reloaded.GainMap!.CanDecode).IsTrue().Because(reloaded.GainMap.UnsupportedReason ?? name);
        await Assert.That(reloaded.GainMap.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        EncodableGainMap expected = source.GetGainMap()!, actual = reloaded.GetGainMap()!;
        await Assert.That(actual.Metadata.ToIso()).IsEquivalentTo(expected.Metadata.ToIso(), CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.Metadata.MultiplicationSpace).IsEqualTo(expected.Metadata.MultiplicationSpace).Because(name);

        using Image before = DecodeMap(source);
        using Image after = DecodeMap(reloaded);
        await Assert.That((after.Width, after.Height)).IsEqualTo((before.Width, before.Height)).Because(name);
        await Assert.That(Difference(after, before)).IsLessThanOrEqualTo(budget).Because(name);
        await Assert.That((reloaded.Width, reloaded.Height)).IsEqualTo((source.Width, source.Height)).Because(name);
        using HdrImage hdr = reloaded.ToHdr();
        await Assert.That((hdr.Width, hdr.Height)).IsEqualTo((source.Width, source.Height));
    }

    [Test]
    [MethodDataSource(nameof(IsoFiles))]
    public async Task OwnMap_IsCopied(string file)
    {
        using Image image = Image.Load(Read(file), Hdr);
        byte[] written = image.Encode(Options(HeifGainMapMode.Require));
        await AssertReloads(image, written, budget: 0, file);
        await Assert.That(MapNals(written)).IsEquivalentTo(MapNals(Read(file)), CollectionOrdering.Matching).Because(file);
    }

    [Test]
    [MethodDataSource(nameof(IsoFiles))]
    public async Task EditedMap_IsEncodedInTheEditedGeometry(string file)
    {
        using Image image = Image.Load(Read(file), Hdr);
        image.Mutate(x => x.Crop(3, 2, image.Width - 7, image.Height - 5).Rotate(RotateMode.Rotate90).Resize(37, 53));
        await AssertReloads(image, image.Encode(Options(HeifGainMapMode.Preserve)), Budget(file), file);
    }

    [Test]
    [Arguments(ExifOrientation.RightTop)]
    [Arguments(ExifOrientation.BottomLeft)]
    [Arguments(ExifOrientation.RightBottom)]
    public async Task Orientation_IsWrittenToTheMapToo(ExifOrientation orientation)
    {
        using Image image = Image.Load(Read("gainmap-both-rotated.heic"), Hdr);
        image.Metadata.TrySetOrientation(orientation);
        byte[] written = image.Encode(Options(HeifGainMapMode.Require));

        // The reloaded file shows the oriented base and map; applying the orientation to the source gives the same.
        using Image oriented = Image.Load(Read("gainmap-both-rotated.heic"), Hdr);
        oriented.Metadata.TrySetOrientation(orientation);
        oriented.Mutate(x => x.AutoOrient());
        await AssertReloads(oriented, written, GrayBudget, orientation.ToString());
    }

    [Test]
    [Arguments("gainmap-iso.heic", 1)]
    [Arguments("gainmap-iso-rgb.heic", 3)]
    public async Task MapChannels_FollowTheMap(string file, int channels)
    {
        using Image image = Image.Load(Read(file), Hdr);
        image.Mutate(x => x.Resize(30, 20));
        byte[] written = image.Encode(Options(HeifGainMapMode.Require));
        // HEVCDecoderConfigurationRecord byte 16: chromaFormat, 0 for a monochrome map.
        byte[] map = MapNals(written);
        await Assert.That(map.Length).IsGreaterThan(0);
        using Image reloaded = Image.Load(written, Hdr);
        await Assert.That(reloaded.GainMap!.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await Assert.That(reloaded.GetGainMap()!.Metadata.ChannelCount).IsEqualTo(channels);
    }

    [Test]
    [Arguments("gainmap-iso.heic")]
    [Arguments("gainmap-iso-rgb.heic")]
    public async Task UltraHdrJpeg_BecomesHeic(string file)
    {
        using Image heic = Image.Load(Read(file), Hdr);
        byte[] jpeg = heic.Encode(new JpegEncoderOptions { Quality = 95, GainMap = JpegGainMapMode.Require });
        using Image image = Image.Load(jpeg, Hdr);
        await Assert.That(image.SourceFormat).IsEqualTo(ImageFormat.Jpeg);
        await AssertReloads(image, image.Encode(Options(HeifGainMapMode.Require)), Budget(file), file);
    }

    [Test]
    public async Task OtherPrimaries_AreStoredInTheToneMapItem()
    {
        // The corpus metadata with use_base_colour_space cleared, Display P3 declared by an ICC profile.
        using Image source = Image.Load(Read("gainmap-iso.heic"), Hdr);
        byte[] iso = source.GetGainMap()!.Metadata.ToIso();
        iso[4] &= 0xbf;
        byte[] p3 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "p3_v4_para0.icc"));
        GainMapMetadata metadata = GainMapMetadata.ParseIso(iso, 1, p3);
        await Assert.That(metadata.MultiplicationSpace).IsEqualTo(HdrColorSpace.LinearDisplayP3);
        using Image plain = Image.Load(Read("gainmap-iso.heic"), Hdr with { LoadGainMap = false });
        using Image map = DecodeMap(source);
        var reference = new ReferenceGainMapFormat(plain, metadata, map);
        using Image image = Image.Load(ReferenceGainMapFormat.Signature,
            new DecoderOptions { LoadGainMap = true, Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(reference).Build() });

        byte[] written = image.Encode(Options(HeifGainMapMode.Require));
        await AssertReloads(image, written, GrayBudget, "p3");
        using Image reloaded = Image.Load(written, Hdr);
        await Assert.That(reloaded.GetGainMap()!.Metadata.ToAlternateIcc()).IsEquivalentTo(metadata.ToAlternateIcc()!, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Modes_FollowTheJpegEncoder()
    {
        using Image plain = Image.Load(Read("gainmap-iso.heic"), Hdr with { LoadGainMap = false });
        await Assert.That(Items(plain.Encode(Options(HeifGainMapMode.Preserve)), "tmap")).IsEmpty();
        await Assert.That(() => plain.Encode(Options(HeifGainMapMode.Require))).Throws<InvalidOperationException>();

        // Since PictureSharp 0.8.1 GetGainMap converts an Apple-only map to ISO 21496-1, so both modes write it.
        using Image apple = Image.Load(Read("gainmap-apple.heic"), Hdr);
        await Assert.That(apple.GainMap!.Detected).IsEqualTo(HdrGainMapKinds.Apple);
        byte[] preserved = apple.Encode(Options(HeifGainMapMode.Preserve));
        await Assert.That(Items(preserved, "tmap").Length).IsEqualTo(1);
        await Assert.That(apple.Encode(Options(HeifGainMapMode.Require))).IsEquivalentTo(preserved, CollectionOrdering.Matching);
        await AssertReloads(apple, preserved, GrayBudget, "apple");
        using (Image reloaded = Image.Load(preserved, Hdr))
        {
            await Assert.That(reloaded.GainMap!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);
        }

        // The default writes an SDR file, byte for byte as without a map.
        using Image hdr = Image.Load(Read("gainmap-iso.heic"), Hdr);
        byte[] none = hdr.Encode(new HeifEncoderOptions { Configuration = Configuration });
        await Assert.That(Items(none, "tmap")).IsEmpty();
        await Assert.That(none).IsEquivalentTo(plain.Encode(new HeifEncoderOptions { Configuration = Configuration }), CollectionOrdering.Matching);
        await Assert.That(() => new HeifEncoderOptions { GainMap = (HeifGainMapMode)3 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifEncoderOptions { GainMapQuality = 101 }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Layout_IsTheOneIPhonesWrite()
    {
        using Image image = Image.Load(Read("gainmap-both-rotated.heic"), Hdr);
        byte[] written = image.Encode(Options(HeifGainMapMode.Require) with { ThumbnailMaxDimension = 16 });
        (uint id, _, byte[] data) = Items(written, "tmap").Single();
        await Assert.That(data[0]).IsEqualTo((byte)0);
        await Assert.That(data[1..]).IsEquivalentTo(image.GetGainMap()!.Metadata.ToIso(), CollectionOrdering.Matching);
        await Assert.That(System.Text.Encoding.ASCII.GetString(written, 8, 24)).Contains("tmap");

        // tmap: ispe of the displayed base, no transforms; one map only (the thumbnail has none).
        string[] properties = [.. HeifTestItems.ItemProperties(written, id).Select(box => System.Text.Encoding.ASCII.GetString(box, 4, 4))];
        await Assert.That(properties).IsEquivalentTo(["ispe"], CollectionOrdering.Matching);
        byte[] ispe = HeifTestItems.ItemProperties(written, id)[0];
        await Assert.That((ispe[15], ispe[19])).IsEqualTo(((byte)image.Width, (byte)image.Height));
        await Assert.That(written.AsSpan().IndexOf("altr"u8)).IsGreaterThan(0);
    }

    [Test]
    public async Task Thumbnail_AndAlpha_CoexistWithTheMap()
    {
        using Image image = Image.Load(Read("gainmap-iso.heic"), Hdr);
        image.Mutate(x => x.Resize(64, 48));
        image.GetPixelSpan()[0] = new Rgba32(10, 20, 30, 128);
        byte[] written = image.Encode(Options(HeifGainMapMode.Require) with { ThumbnailMaxDimension = 32 });
        using Image reloaded = Image.Load(written, Hdr);
        await Assert.That(reloaded.GetPixelSpan()[0].A).IsLessThan((byte)255);
        await AssertReloads(image, written, GrayBudget, "alpha");
        await Assert.That(Items(written, "tmap").Length).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_IsObserved()
    {
        using Image image = Image.Load(Read("gainmap-iso.heic"), Hdr);
        image.Mutate(x => x.Resize(64, 48));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => new HeifFormat().Encoder.Encode(image, new MemoryStream(), Options(HeifGainMapMode.Require), cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task HeifDecAndHm_ReadTheWrittenFiles()
    {
        string tools = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools");
        string heifDec = Path.Combine(tools, "bin", "heif-dec");
        string hm = Path.Combine(tools, "bin", "TAppDecoder");
        Skip.Unless(File.Exists(heifDec) && File.Exists(hm), "Build heif-dec and TAppDecoder with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-gainmap-").FullName;
        try
        {
            foreach (string file in IsoFiles())
            {
                foreach (bool edited in new[] { false, true })
                {
                    using Image image = Image.Load(Read(file), Hdr);
                    if (edited)
                    {
                        image.Mutate(x => x.Resize(41, 29));
                    }

                    string name = $"{Path.GetFileNameWithoutExtension(file)}-{(edited ? "edited" : "copied")}";
                    byte[] written = image.Encode(Options(HeifGainMapMode.Require));
                    string path = Path.Combine(work, name + ".heic");
                    File.WriteAllBytes(path, written);

                    // libheif 1.20 does not interpret tmap; it must still decode the base like our decoder.
                    string png = Path.Combine(work, name + ".png");
                    await Run(heifDec, ["--quiet", path, png], name);
                    using Image reference = Image.Load(png);
                    using Image ours = Image.Load(written, Hdr with { LoadGainMap = false });
                    await Assert.That(HeifDecodeTests.RgbaHash(ours)).IsEqualTo(HeifDecodeTests.RgbaHash(reference)).Because(name);

                    // HM decodes the map stream (a single picture in these sizes).
                    uint tmap = Items(written, "tmap").Single().Id;
                    List<ReadOnlyMemory<byte>> nals = HeifTestItems.Extract(written).Single(item => item.Id == tmap - 1).Nals;
                    string stream = Path.Combine(work, name + ".hevc");
                    File.WriteAllBytes(stream, HeifTestItems.ToAnnexB(nals));
                    await Run(hm, ["-b", stream, "-o", Path.Combine(work, name + ".yuv"), "-d", "0"], name);
                }
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task Run(string tool, string[] arguments, string name)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Assert.That(process.ExitCode).IsEqualTo(0).Because($"{name}: {error}{await output}");
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
