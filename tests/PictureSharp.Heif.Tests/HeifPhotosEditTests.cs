namespace PictureSharp.Formats.Heif.Tests;

// Photos "Adjust" sliders on synthetic photographs: identity, ranges, the sign of each slider and determinism.
public sealed class HeifPhotosEditTests
{
    [Test]
    public async Task Render_WithoutAdjustments_ReturnsUnchangedCopy()
    {
        using Image photo = Texture(64, 48);
        using Image result = Adjust(photo, new HeifPhotosAdjustments());
        await Assert.That(new HeifPhotosAdjustments().IsEmpty).IsTrue();
        await Assert.That(result.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(photo.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Adjustments_RejectValuesOutsideTheSliders()
    {
        await Assert.That(() => new HeifPhotosAdjustments { Exposure = 101 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifPhotosAdjustments { Vignette = -100.5f }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifPhotosAdjustments { Sharpness = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifPhotosAdjustments { NoiseReduction = float.NaN }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new HeifPhotosAdjustments { Definition = 100, Brightness = -100 }.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Warmth_And_Tint_ShiftGreyTheDocumentedWay()
    {
        using Image grey = Solid(128, 128, 128);
        Rgba32 warm = Centre(grey, new HeifPhotosAdjustments { Warmth = 50 });
        Rgba32 cool = Centre(grey, new HeifPhotosAdjustments { Warmth = -50 });
        Rgba32 magenta = Centre(grey, new HeifPhotosAdjustments { Tint = 50 });
        Rgba32 green = Centre(grey, new HeifPhotosAdjustments { Tint = -50 });
        await Assert.That(warm.R > warm.B + 10).IsTrue();
        await Assert.That(cool.B > cool.R + 10).IsTrue();
        await Assert.That(magenta.G < magenta.R && magenta.G < magenta.B).IsTrue();
        await Assert.That(green.G > green.R && green.G > green.B).IsTrue();
    }

    [Test]
    public async Task ToneSliders_HaveThePhotosSigns()
    {
        using Image dark = Solid(40, 40, 40);
        using Image bright = Solid(230, 230, 230);
        using Image mid = Solid(128, 128, 128);
        await Assert.That(Centre(dark, new HeifPhotosAdjustments { Contrast = -100 }).G > 50).IsTrue();
        await Assert.That(Centre(bright, new HeifPhotosAdjustments { Contrast = -100 }).G < 220).IsTrue();
        await Assert.That(Centre(dark, new HeifPhotosAdjustments { BlackPoint = -50 }).G > 55).IsTrue();
        await Assert.That(Centre(dark, new HeifPhotosAdjustments { BlackPoint = 50 }).G < 20).IsTrue();
        await Assert.That(Centre(mid, new HeifPhotosAdjustments { Brightness = 50 }).G > 140).IsTrue();
        await Assert.That(Centre(mid, new HeifPhotosAdjustments { Exposure = -100 }).G < 100).IsTrue();
        await Assert.That(Centre(dark, new HeifPhotosAdjustments { Shadows = 100 }).G > 55).IsTrue();
    }

    [Test]
    public async Task SaturationMinus100_GivesGrey()
    {
        using Image photo = Texture(40, 30);
        using Image result = Adjust(photo, new HeifPhotosAdjustments { Saturation = -100 });
        int spread = 0;
        foreach (Rgba32 p in result.GetPixelSpan<Rgba32>())
        {
            spread = Math.Max(spread, Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)));
        }

        await Assert.That(spread).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Vignette_ChangesCornersAndKeepsTheCentre()
    {
        using Image grey = Solid(150, 150, 150, 120, 90);
        using Image darker = Adjust(grey, new HeifPhotosAdjustments { Vignette = 50 });
        using Image lighter = Adjust(grey, new HeifPhotosAdjustments { Vignette = -50 });
        await Assert.That(darker.GetRowSpan<Rgba32>(45)[60].G).IsEqualTo((byte)150);
        await Assert.That(darker.GetRowSpan<Rgba32>(0)[0].G < 100).IsTrue();
        await Assert.That(lighter.GetRowSpan<Rgba32>(89)[119].G > 170).IsTrue();
    }

    [Test]
    public async Task Render_KeepsAlpha_And_DoesNotDependOnThreads()
    {
        using Image photo = Texture(640, 480);
        photo.GetRowSpan<Rgba32>(7)[9] = new Rgba32(10, 20, 30, 77);
        var adjustments = new HeifPhotosAdjustments
        {
            Exposure = 30, Brilliance = 40, Shadows = 60, Saturation = 20, Vibrance = -30, Warmth = 10, Contrast = 15,
            Sharpness = 50, Definition = 70, NoiseReduction = 20, Vignette = 40,
        };
        using Image single = Adjust(photo, adjustments, threads: 1);
        using Image parallel = Adjust(photo, adjustments);
        await Assert.That(parallel.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(single.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(single.GetRowSpan<Rgba32>(7)[9].A).IsEqualTo((byte)77);
        await Assert.That(single.GetPixelSpan<Rgba32>().ToArray()).IsNotEquivalentTo(photo.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Sharpness_RaisesLocalContrast_And_NoiseReductionLowersIt()
    {
        using Image photo = Texture(96, 96);
        using Image sharp = Adjust(photo, new HeifPhotosAdjustments { Sharpness = 100 });
        using Image smooth = Adjust(photo, new HeifPhotosAdjustments { NoiseReduction = 100 });
        await Assert.That(Detail(sharp) > Detail(photo)).IsTrue();
        await Assert.That(Detail(smooth) < Detail(photo)).IsTrue();
    }

    [Test]
    public async Task Render_ChecksCancellation()
    {
        using Image photo = Texture(32, 32);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => Adjust(photo, new HeifPhotosAdjustments { Exposure = 10 }, cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    private static Image Adjust(Image photo, HeifPhotosAdjustments adjustments, CancellationToken cancellationToken = default, int threads = -1) =>
        photo.ApplyHeifEdits(new HeifEdits { Adjustments = adjustments, MaxDegreeOfParallelism = threads }, null, cancellationToken);

    private static Rgba32 Centre(Image photo, HeifPhotosAdjustments adjustments)
    {
        using Image result = Adjust(photo, adjustments);
        return result.GetRowSpan<Rgba32>(result.Height / 2)[result.Width / 2];
    }

    // Mean absolute difference between horizontal neighbours.
    private static double Detail(Image image)
    {
        double sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<Rgba32> row = image.GetRowSpan<Rgba32>(y);
            for (int x = 1; x < row.Length; x++)
            {
                sum += Math.Abs(row[x].G - row[x - 1].G);
            }
        }

        return sum / (image.Height * (image.Width - 1));
    }

    private static Image Solid(byte r, byte g, byte b, int width = 16, int height = 16)
    {
        var image = new Image(width, height);
        image.GetPixelSpan<Rgba32>().Fill(new Rgba32(r, g, b));
        return image;
    }

    private static Image Texture(int width, int height)
    {
        var image = new Image(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRowSpan<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                int stripe = ((x / 2) + (y / 3)) % 2 == 0 ? 60 : 0, check = ((x / 5) + (y / 5)) % 2 == 0 ? 80 : 0;
                row[x] = new Rgba32((byte)(60 + stripe + check), (byte)(90 + check), (byte)(150 - stripe));
            }
        }

        return image;
    }
}
