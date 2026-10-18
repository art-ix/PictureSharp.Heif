using System.Drawing;
using System.Diagnostics;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HeifThumbnailTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static DecoderOptions DecodeOptions => new() { Configuration = Configuration, ApplyExifOrientation = true };

    [Test]
    [Arguments(ExifOrientation.TopLeft)]
    [Arguments(ExifOrientation.TopRight)]
    [Arguments(ExifOrientation.BottomRight)]
    [Arguments(ExifOrientation.BottomLeft)]
    [Arguments(ExifOrientation.LeftTop)]
    [Arguments(ExifOrientation.RightTop)]
    [Arguments(ExifOrientation.RightBottom)]
    [Arguments(ExifOrientation.LeftBottom)]
    public async Task GeneratedThumbnail_KeepsOwnAlphaColourAndOrientation_WithoutExifOrXmp(ExifOrientation orientation)
    {
        using Image original = HeifEncodeTests.Sample(101, 57, alpha: true);
        original.Metadata.Orientation = orientation;
        byte[] icc = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "p3_v4_para0.icc"));
        original.Metadata.SetBlock(MetadataKinds.Icc, icc);
        original.Metadata.SetBlock(MetadataKinds.Xmp, "<photo/>"u8);
        byte[] bytes = original.ToHeic(new HeifEncoderOptions { Configuration = Configuration, ThumbnailMaxDimension = 41, AlphaQuality = 90 });
        HeifDocument doc = HeifDocument.Open(bytes);
        HeifItemInfo photo = doc.PrimaryImage;
        HeifItemInfo thumbnail = photo.Thumbnails.Single();
        bool swaps = (int)orientation >= 5;
        await Assert.That(thumbnail.DisplaySize).IsEqualTo(swaps ? new Size(23, 41) : new Size(41, 23));
        await Assert.That(thumbnail.MetadataItems.Count).IsEqualTo(0);
        await Assert.That(thumbnail.AuxiliaryImages.Count).IsEqualTo(1);
        await Assert.That(doc.TopLevelImages.Select(item => item.Id)).IsEquivalentTo(new[] { doc.PrimaryImage.Id }, CollectionOrdering.Matching);
        using Image preview = doc.LoadPreview(photo, new Size(41, 41), DecodeOptions);
        await Assert.That(preview.Size).IsEqualTo(thumbnail.DisplaySize!.Value);
        await Assert.That(preview.HasAlpha).IsTrue();
        await Assert.That(preview.Metadata.IccProfile!).IsEquivalentTo(icc, CollectionOrdering.Matching);
        await Assert.That(preview.Metadata.Xmp).IsNull();
        await Assert.That(preview.Metadata.Exif).IsNull();
        using Image reduced = original.Clone(x => x.AutoOrient().Thumbnail(41, 41));
        byte[] referenceBytes = reduced.ToHeic(new HeifEncoderOptions { Configuration = Configuration, AlphaQuality = 90, Metadata = MetadataKinds.Icc });
        using Image expected = Image.Load(referenceBytes, DecodeOptions);
        await Assert.That(HeifDecodeTests.RgbaHash(preview)).IsEqualTo(HeifDecodeTests.RgbaHash(expected));
        await Assert.That(original.Size).IsEqualTo(new Size(101, 57));
        await Assert.That(original.Metadata.Orientation).IsEqualTo(orientation);
    }

    [Test]
    [Arguments(0)]
    [Arguments(64)]
    public async Task GeneratedThumbnail_HandlesGridsAndSmallImagesWithoutUpscaling(int tileSize)
    {
        using Image original = HeifEncodeTests.Sample(131, 79, alpha: true);
        var options = new HeifEncoderOptions { Configuration = Configuration, ThumbnailMaxDimension = 95, TileSize = tileSize };
        byte[] bytes = original.ToHeic(options);
        HeifDocument doc = HeifDocument.Open(bytes);
        HeifItemInfo thumbnail = doc.PrimaryImage.Thumbnails.Single();
        await Assert.That(thumbnail.DisplaySize).IsEqualTo(new Size(95, 57));
        using Image preview = doc.LoadPreview(doc.PrimaryImage, new Size(80, 80), DecodeOptions);
        await Assert.That(preview.Size).IsEqualTo(new Size(80, 48));
        using Image small = HeifEncodeTests.Sample(7, 3);
        HeifDocument smallDoc = HeifDocument.Open(small.ToHeic(options));
        await Assert.That(smallDoc.PrimaryImage.Thumbnails.Single().DisplaySize).IsEqualTo(new Size(7, 3));
        using Image smallPreview = smallDoc.LoadPreview(smallDoc.PrimaryImage, new Size(80, 80), DecodeOptions);
        await Assert.That(smallPreview.Size).IsEqualTo(new Size(7, 3));
    }

    [Test]
    public async Task GeneratedThumbnail_RespectsMetadataPolicyAndParallelism()
    {
        using Image original = HeifEncodeTests.Sample(131, 79, alpha: true);
        original.Metadata.SetBlock(MetadataKinds.Icc, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "p3_v4_para0.icc")));
        var options = new HeifEncoderOptions { Configuration = Configuration, ThumbnailMaxDimension = 70, TileSize = 64, Metadata = MetadataKinds.None };
        byte[] serial = original.ToHeic(options with { MaxDegreeOfParallelism = 1 });
        byte[] parallel = original.ToHeic(options with { MaxDegreeOfParallelism = -1 });
        await Assert.That(parallel).IsEquivalentTo(serial, CollectionOrdering.Matching);
        HeifDocument serialDoc = HeifDocument.Open(serial);
        using Image preview = serialDoc.LoadPreview(serialDoc.GetItem(1), new Size(60, 60), DecodeOptions);
        await Assert.That(preview.Metadata.IccProfile).IsNull();
        await Assert.That(preview.Metadata.Xmp).IsNull();
        await Assert.That(new HeifEncoderOptions().ThumbnailMaxDimension).IsEqualTo(0);
        await Assert.That(() => new HeifEncoderOptions { ThumbnailMaxDimension = -1 }).Throws<ArgumentOutOfRangeException>();
        HeifDocument disabled = HeifDocument.Open(original.ToHeic(options with { ThumbnailMaxDimension = 0 }));
        await Assert.That(disabled.PrimaryImage.Thumbnails.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(1, 80)]
    [Arguments(80, 1)]
    public async Task GeneratedThumbnail_HandlesExtremeAspectRatios(int width, int height)
    {
        using Image original = HeifEncodeTests.Sample(width, height);
        byte[] bytes = original.ToHeic(new HeifEncoderOptions { Configuration = Configuration, ThumbnailMaxDimension = 31 });
        HeifDocument doc = HeifDocument.Open(bytes);
        using Image preview = doc.LoadPreview(doc.PrimaryImage, new Size(3, 3), DecodeOptions);
        await Assert.That(preview.Size).IsEqualTo(width == 1 ? new Size(1, 3) : new Size(3, 1));
    }

    [Test]
    public async Task ThumbnailEncoding_ObservesCancellationBeforeWriting()
    {
        using Image image = HeifEncodeTests.Sample(80, 60);
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.That(() => new HeifFormat().Encoder.Encode(image, output,
            new HeifEncoderOptions { ThumbnailMaxDimension = 31 }, cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    [Property("Category", "Oracle")]
    [Arguments(0)]
    [Arguments(64)]
    public async Task GeneratedThumbnail_IsRecognizedByLibheif_AndEveryCodedItemMatchesHm(int tileSize)
    {
        string tools = Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools");
        string info = Path.Combine(tools, "bin", "heif-info"), hm = Path.Combine(tools, "bin", "TAppDecoder");
        Skip.Unless(File.Exists(info) && File.Exists(hm), "Build heif-info and TAppDecoder with scripts/build-heif-reference.sh.");
        Skip.Unless(File.Exists("/usr/bin/cc") && File.Exists(Path.Combine(tools, "include", "libheif", "heif.h")), "The libheif pixel oracle requires cc and the reference headers.");
        string work = Directory.CreateTempSubdirectory("heif-thumbnail-oracle-").FullName;
        try
        {
            using Image image = HeifEncodeTests.Sample(101, 57, alpha: true);
            image.Metadata.Orientation = ExifOrientation.RightTop;
            int maximum = tileSize == 64 ? 95 : 41;
            byte[] bytes = image.ToHeic(new HeifEncoderOptions { Configuration = Configuration, ThumbnailMaxDimension = maximum, TileSize = tileSize });
            string file = Path.Combine(work, "photo.heic");
            File.WriteAllBytes(file, bytes);
            string log = await Run(info, file);
            await Assert.That(log).Contains("thumbnail");
            await Assert.That(log).Contains(tileSize == 64 ? "54x95" : "23x41");
            string driver = Path.Combine(work, "thumbnail-oracle"), rgba = Path.Combine(work, "thumbnail.rgba");
            await Run("/usr/bin/cc", "-std=c11", "-Wall", "-Wextra", "-Werror", "-I" + Path.Combine(tools, "include"),
                Path.Combine(AppContext.BaseDirectory, "heif-thumbnail-oracle.c"), "-L" + Path.Combine(tools, "lib"),
                "-Wl,-rpath," + Path.Combine(tools, "lib"), "-lheif", "-o", driver);
            string[] dimensions = (await Run(driver, file, rgba)).Trim().Split(' ');
            HeifDocument doc = HeifDocument.Open(bytes);
            await Assert.That(uint.Parse(dimensions[0])).IsEqualTo(doc.PrimaryImage.Thumbnails.Single().Id);
            using Image preview = doc.LoadPreview(doc.PrimaryImage, new Size(maximum, maximum), DecodeOptions);
            await Assert.That(preview.Size).IsEqualTo(new Size(int.Parse(dimensions[1]), int.Parse(dimensions[2])));
            byte[] actual = System.Runtime.InteropServices.MemoryMarshal.AsBytes(preview.GetPixelSpan()).ToArray();
            byte[] referencePixels = File.ReadAllBytes(rgba);

            await Assert.That(actual.AsSpan().SequenceEqual(referencePixels)).IsTrue().Because("RGBA must match libheif exactly.");
            foreach ((uint id, List<ReadOnlyMemory<byte>> nals) in HeifTestItems.Extract(bytes))
            {
                string bitstream = Path.Combine(work, id + ".hevc"), output = Path.Combine(work, id + ".yuv");
                File.WriteAllBytes(bitstream, HeifTestItems.ToAnnexB(nals));
                await Run(hm, "-b", bitstream, "-o", output, "-d", "0");
                HevcFrame frame = HevcDecoder.Decode(nals, null, CancellationToken.None);
                byte[] ours = [.. frame.Planes.SelectMany((plane, channel) => Samples(frame, plane, channel))];
                await Assert.That(ours).IsEquivalentTo(File.ReadAllBytes(output), CollectionOrdering.Matching).Because($"item {id}");
            }
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private static IEnumerable<byte> Samples(HevcFrame frame, ushort[] plane, int channel)
    {
        int shift = channel == 0 ? 0 : 1;
        for (int y = 0; y < frame.CroppedHeight >> shift; y++)
        {
            for (int x = 0; x < frame.CroppedWidth >> shift; x++)
            {
                yield return (byte)plane[y * frame.Strides[channel] + x];
            }
        }
    }

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout, error = await stderr;
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(output + error);
        return output + error;
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
