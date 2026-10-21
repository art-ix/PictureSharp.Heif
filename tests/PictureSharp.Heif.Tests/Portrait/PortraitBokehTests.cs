using System.Drawing;

namespace PictureSharp.Formats.Heif.Tests;

// E5: grain in blurred areas and hard discs for point lights, on synthetic scenes whose right half lies far behind the
// focused left half.
public sealed class PortraitBokehTests
{
    private const int Width = 1280, Height = 960;
    private static readonly Point Focus = new(Width / 4, Height / 2);

    [Test]
    public async Task Grain_FillsBlurredAreasOnly_ScalesWithIso_AndIsDeterministic()
    {
        (HeifDepthImage depth, Image matte) = Load();
        using Image photo = Flat(128);
        using Image low = Render(photo, depth, matte, threads: 1);
        using Image parallel = Render(photo, depth, matte, threads: 4);
        photo.Metadata.ExifProfile.SetValue(ExifTag.IsoSpeed, (ushort)1250);
        using Image high = Render(photo, depth, matte, threads: 1);

        await Assert.That(low.GetPixelSpan().SequenceEqual(parallel.GetPixelSpan())).IsTrue();
        var focused = new Rectangle(0, 0, Width / 2 - 64, Height);
        var blurred = new Rectangle(Width / 2 + 128, 0, Width / 2 - 128, Height);
        await Assert.That(Deviation(low, focused)).IsLessThan(1e-9);
        double lowGrain = Deviation(low, blurred), highGrain = Deviation(high, blurred);
        await Assert.That(lowGrain).IsBetween(0.3, 1.2).Because("about 0.6 codes at ISO 64");
        await Assert.That(highGrain / lowGrain).IsBetween(1.8, 3.0).Because("(1250/64)^0.28 ≈ 2.3");
    }

    [Test]
    public async Task PointLight_BecomesAHardDisc_KeepingItsLight()
    {
        (HeifDepthImage depth, Image matte) = Load();
        const int background = 10, cx = 960, cy = 480;
        // Below clipping, so the light is only moved, not scaled up (a clipped point gets PointGain, checked below).
        using Image photo = Point(background, 230, cx, cy);
        using Image result = Render(photo, depth, matte, threads: 1);
        Span<Rgba32> centre = result.GetRowSpan(cy);
        double[] profile = new double[100];
        for (int dx = 0; dx < profile.Length; dx++)
        {
            profile[dx] = HeifPortraitTests.Luma(centre[cx + dx]);
        }

        double plateau = profile[..4].Average(), floor = profile[^20..].Average(), span = plateau - floor;
        await Assert.That(span).IsGreaterThan(8).Because("the disc stands out of the background");
        int radius = Array.FindIndex(profile, v => v < floor + span / 2);
        int edge = profile.Count(v => v > floor + 0.1 * span && v < floor + 0.9 * span);
        await Assert.That(radius).IsGreaterThan(10);
        // The 5 px point, the 2 px texels and grain set the edge; the gathered layers alone fall off over about 34 px.
        await Assert.That(edge).IsLessThanOrEqualTo(10).Because($"hard edge; disc radius {radius} px");

        // The scattered light equals the point's light above the background, within rounding and grain.
        double light = Light(result, cx, cy, radius, background);

        double expected = 25 * (Linear(230) - Linear(background));
        await Assert.That(light / expected).IsBetween(0.85, 1.15);

        using Image clipped = Point(background, 255, cx, cy);
        using Image clippedResult = Render(clipped, depth, matte, threads: 1);
        await Assert.That(Light(clippedResult, cx, cy, radius, background) / (25 * (1 - Linear(background)))).IsGreaterThan(1.2)
            .Because("clipped points stand for more light than code 255");
    }

    private static Image Point(int background, byte value, int cx, int cy)
    {
        Image photo = Flat((byte)background);
        for (int y = cy - 2; y <= cy + 2; y++)
        {
            Span<Rgba32> row = photo.GetRowSpan(y);
            for (int x = cx - 2; x <= cx + 2; x++)
            {
                row[x] = new Rgba32(value, value, value);
            }
        }

        return photo;
    }

    private static double Light(Image result, int cx, int cy, int radius, int background)
    {
        double light = 0;
        for (int y = cy - 2 * radius; y <= cy + 2 * radius; y++)
        {
            Span<Rgba32> row = result.GetRowSpan(y);
            for (int x = cx - 2 * radius; x <= cx + 2 * radius; x++)
            {
                light += Linear(row[x].G) - Linear(background);
            }
        }

        return light;
    }

    private static (HeifDepthImage Depth, Image Matte) Load()
    {
        HeifDocument doc = HeifDocument.Open(HeifPortraitTests.Scene.Build(Width, Height).Bytes);
        HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
        Image matte = doc.LoadImage(doc.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)!, HeifPortraitAuditTests.Decode);
        return (depth, matte);
    }

    private static Image Render(Image photo, HeifDepthImage depth, Image matte, int threads) =>
        TestRender.Portrait(photo, depth, matte, new HeifPortraitOptions { Aperture = 1.4f, FocusPoint = Focus }, threads);

    private static Image Flat(byte value)
    {
        var image = new Image(Width, Height);
        image.GetPixelSpan().Fill(new Rgba32(value, value, value));
        return image;
    }

    private static double Deviation(Image image, Rectangle area)
    {
        var values = new List<double>();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            Span<Rgba32> row = image.GetRowSpan(y);
            for (int x = area.Left; x < area.Right; x++)
            {
                values.Add(HeifPortraitTests.Luma(row[x]));
            }
        }

        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    private static double Linear(int code)
    {
        double v = code / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
