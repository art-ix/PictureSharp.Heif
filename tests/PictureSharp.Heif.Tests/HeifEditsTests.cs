using System.Drawing;
using static PictureSharp.Formats.Heif.Tests.HeifPortraitTests;

namespace PictureSharp.Formats.Heif.Tests;

// The HeifEdits pipeline: the order of the stages, reusable portrait maps, documents without depth and the shared
// working-pixel budget.
public sealed class HeifEditsTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static readonly DecoderOptions Options = new() { Configuration = Configuration };
    private static readonly HeifPhotosAdjustments Sliders = new() { Exposure = 20, Saturation = 30, Warmth = 15, Sharpness = 40, Vignette = 30 };

    [Test]
    public async Task Render_AppliesTheSlidersBeforeTheBlur()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(320, 240).Bytes);
        var edits = new HeifEdits { Adjustments = Sliders, Portrait = new HeifPortraitOptions { Aperture = 2 } };
        using Image rendered = doc.Render(doc.PrimaryImage, edits, Options);

        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        using HeifPortraitMaps maps = doc.LoadPortraitMaps(doc.PrimaryImage, Options);
        using Image adjusted = photo.ApplyHeifEdits(new HeifEdits { Adjustments = Sliders });
        using Image slidersThenBlur = adjusted.ApplyHeifEdits(new HeifEdits { Portrait = edits.Portrait }, maps);
        using Image blurred = photo.ApplyHeifEdits(new HeifEdits { Portrait = edits.Portrait }, maps);
        using Image blurThenSliders = blurred.ApplyHeifEdits(new HeifEdits { Adjustments = Sliders });
        using Image together = photo.ApplyHeifEdits(edits, maps);

        await Assert.That(Pixels(rendered)).IsEquivalentTo(Pixels(slidersThenBlur), CollectionOrdering.Matching);
        await Assert.That(Pixels(together)).IsEquivalentTo(Pixels(slidersThenBlur), CollectionOrdering.Matching);
        await Assert.That(Pixels(together)).IsNotEquivalentTo(Pixels(blurThenSliders), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Maps_RenderSeveralVariants_AndLeaveThePhotographUnchanged()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(320, 240).Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        Rgba32[] before = Pixels(photo);
        using HeifPortraitMaps maps = doc.LoadPortraitMaps(doc.PrimaryImage, Options);
        await Assert.That(maps.Matte).IsNotNull();
        foreach (float aperture in new[] { 1.4f, 8f })
        {
            var edits = new HeifEdits { Adjustments = Sliders, Portrait = new HeifPortraitOptions { Aperture = aperture } };
            using Image fromMaps = photo.ApplyHeifEdits(edits, maps);
            using Image fromDocument = doc.Render(doc.PrimaryImage, edits, Options);
            await Assert.That(Pixels(fromMaps)).IsEquivalentTo(Pixels(fromDocument), CollectionOrdering.Matching).Because($"f/{aperture}");
        }

        await Assert.That(Pixels(photo)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmptyEdits_ReturnAnUnchangedCopy_AndIgnoreMaps()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(160, 120).Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        await Assert.That(new HeifEdits().IsEmpty).IsTrue();
        await Assert.That(new HeifEdits { Adjustments = new() }.IsEmpty).IsTrue();
        await Assert.That(new HeifEdits { Portrait = new() }.IsEmpty).IsFalse();
        using Image copy = photo.ApplyHeifEdits(new HeifEdits());
        using Image rendered = doc.Render(doc.PrimaryImage, new HeifEdits { Adjustments = new() }, Options);
        using HeifPortraitMaps maps = doc.LoadPortraitMaps(doc.PrimaryImage, Options);
        using Image ignored = photo.ApplyHeifEdits(new HeifEdits(), maps);
        await Assert.That(ReferenceEquals(copy, photo)).IsFalse();
        await Assert.That(Pixels(copy)).IsEquivalentTo(Pixels(photo), CollectionOrdering.Matching);
        await Assert.That(Pixels(rendered)).IsEquivalentTo(Pixels(photo), CollectionOrdering.Matching);
        await Assert.That(Pixels(ignored)).IsEquivalentTo(Pixels(photo), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Render_WithoutDepth_AppliesSlidersOnly()
    {
        using var source = new Image(96, 64);
        source.GetPixelSpan().Fill(new Rgba32(120, 110, 100));
        HeifDocument doc = HeifDocument.Open(source.ToHeic(new HeifEncoderOptions { Configuration = Configuration, Quality = 95 }));
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        using Image adjusted = doc.Render(doc.PrimaryImage, new HeifEdits { Adjustments = Sliders }, Options);
        using Image expected = photo.ApplyHeifEdits(new HeifEdits { Adjustments = Sliders });
        await Assert.That(Pixels(adjusted)).IsEquivalentTo(Pixels(expected), CollectionOrdering.Matching);
        await Assert.That(() => doc.Render(doc.PrimaryImage, new HeifEdits { Adjustments = Sliders, Portrait = new() }, Options))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task MaxPixels_CountsTheIntermediateImageOfBothStages()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(160, 120).Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        using HeifPortraitMaps maps = doc.LoadPortraitMaps(doc.PrimaryImage, Options);
        var blur = new HeifEdits { Portrait = new HeifPortraitOptions { Aperture = 2 } };

        // The smallest budget the blur alone fits in.
        long low = 1, high = 16L * photo.Width * photo.Height;
        while (low < high)
        {
            long middle = (low + high) / 2;
            if (Fits(blur with { MaxPixels = middle }))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        var both = blur with { Adjustments = Sliders };
        await Assert.That(Fits(both with { MaxPixels = low })).IsFalse();
        await Assert.That(Fits(both with { MaxPixels = low + (long)photo.Width * photo.Height })).IsTrue();
        await Assert.That(Fits(new HeifEdits { Adjustments = Sliders, MaxPixels = (long)photo.Width * photo.Height })).IsTrue();
        await Assert.That(Fits(new HeifEdits { Adjustments = Sliders, MaxPixels = (long)photo.Width * photo.Height - 1 })).IsFalse();

        bool Fits(HeifEdits edits)
        {
            try
            {
                photo.ApplyHeifEdits(edits, maps).Dispose();
                return true;
            }
            catch (ImageFormatException)
            {
                return false;
            }
        }
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task BothStages_DoNotDependOnTheThreadCount()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(704, 512).Bytes);
        var edits = new HeifEdits { Adjustments = Sliders, Portrait = new HeifPortraitOptions { Aperture = 2, FocusPoint = new Point(100, 300) } };
        using Image single = doc.Render(doc.PrimaryImage, edits with { MaxDegreeOfParallelism = 1 }, Options);
        foreach (int threads in new[] { 2, 3, -1 })
        {
            using Image parallel = doc.Render(doc.PrimaryImage, edits with { MaxDegreeOfParallelism = threads }, Options);
            await Assert.That(Pixels(parallel)).IsEquivalentTo(Pixels(single), CollectionOrdering.Matching).Because($"{threads} threads");
        }
    }

    [Test]
    public async Task Render_ChecksCancellation()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(160, 120).Bytes);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => doc.Render(doc.PrimaryImage, new HeifEdits { Adjustments = Sliders }, Options, cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(() => doc.LoadPortraitMaps(doc.PrimaryImage, Options, cancelled.Token)).Throws<OperationCanceledException>();
    }

    private static Rgba32[] Pixels(Image image) => image.GetPixelSpan().ToArray();
}
