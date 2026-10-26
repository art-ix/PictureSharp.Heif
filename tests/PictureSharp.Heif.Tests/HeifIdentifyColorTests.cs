namespace PictureSharp.Formats.Heif.Tests;

/// <summary><c>ImageInfo.Color</c> and <c>Compression</c> (PictureSharp 0.8.5) from the committed corpus.</summary>
public sealed class HeifIdentifyColorTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus");

    private static ImageInfo Identify(string file, DecoderOptions? options = null) =>
        new HeifFormat().Decoder.Identify(File.ReadAllBytes(Path.Combine(Corpus, file)), options ?? new DecoderOptions());

    [Test]
    [Arguments("x265-rgb8.heic", ImageColorModel.Rgb, 3, 8, ImageAlphaMode.None)]
    [Arguments("x265-rgb10.heic", ImageColorModel.Rgb, 3, 10, ImageAlphaMode.None)]
    [Arguments("x265-gray8.heic", ImageColorModel.Gray, 1, 8, ImageAlphaMode.None)]
    [Arguments("x265-rgba8.heic", ImageColorModel.Rgb, 4, 8, ImageAlphaMode.Straight)]
    [Arguments("x265-rgba10.heic", ImageColorModel.Rgb, 4, 10, ImageAlphaMode.Straight)]
    [Arguments("x265-rgba8-premultiplied.heic", ImageColorModel.Rgb, 4, 8, ImageAlphaMode.Premultiplied)]
    [Arguments("grid-2x1.heic", ImageColorModel.Rgb, 3, 8, ImageAlphaMode.None)]
    public async Task Identify_ReportsModelChannelsDepthAndAlpha(string file, ImageColorModel model, int channels, int bits,
        ImageAlphaMode alpha)
    {
        ImageInfo info = Identify(file);
        await Assert.That(info.Color.Model).IsEqualTo(model);
        await Assert.That(info.Color.ChannelCount).IsEqualTo(channels);
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(bits);
        await Assert.That(info.Color.SampleFormat).IsEqualTo(ImageSampleFormat.UnsignedInteger);
        await Assert.That(info.Color.AlphaMode).IsEqualTo(alpha);
        await Assert.That(info.Compression).IsEqualTo(ImageCompression.Unknown);
        await Assert.That(info.Resolution).IsNull();
    }

    [Test]
    public async Task Identify_ReportsNclxAsCicp()
    {
        await Assert.That(Identify("x265-bt2020-limited10.heic").Color.Cicp).IsEqualTo(new CicpColorDescription(9, 14, 9, false));
        CicpColorDescription bt709 = Identify("x265-bt709-full10.heic").Color.Cicp!;
        await Assert.That((bt709.ColorPrimaries, bt709.MatrixCoefficients, bt709.FullRange)).IsEqualTo((1, 1, true));
    }

    [Test]
    public async Task Identify_DetectsIccProfile_WithoutReservingMetadata()
    {
        ImageInfo info = Identify("wrap-icc-exif-xmp.heic", new DecoderOptions { MaxMetadataBytes = 0, IgnoreMetadata = true });
        await Assert.That(info.Color.HasIccProfile).IsTrue();
        await Assert.That(Identify("x265-rgb8.heic").Color.HasIccProfile).IsFalse();
    }

    [Test]
    public async Task Identify_DoesNotCountDepthOrGainMapItems()
    {
        ImageInfo info = Identify("gainmap-iso.heic");
        await Assert.That(info.ContainerItemCount!.Value).IsGreaterThan(1);
        await Assert.That(info.Color.ChannelCount).IsEqualTo(3);
    }
}
