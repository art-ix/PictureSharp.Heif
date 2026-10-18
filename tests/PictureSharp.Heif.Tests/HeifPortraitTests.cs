using System.Diagnostics;
using System.Drawing;
using System.Text;
using static PictureSharp.Formats.Heif.Tests.HeifDocumentFixture;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;

namespace PictureSharp.Formats.Heif.Tests;

// Apple depth description and depth-of-field rendering. Synthetic photographs are built from items the plugin's own
// encoder writes: a textured photograph, a disparity map whose left half is near (the subject) and right half far,
// its Apple XMP, and a portrait matte covering the left half.
public sealed class HeifPortraitTests
{
    private const string DepthType = "urn:mpeg:hevc:2015:auxid:2";
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static readonly DecoderOptions Options = new() { Configuration = Configuration };

    internal static string AppleXmp(string kind = "disparity", string intMin = "0", string intMax = "255", string floatMin = "0.000000",
        string floatMax = "2.000000", string aperture = "4.500000", string extra = "") => $"""
        <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="XMP Core 6.0.0">
           <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about=""
                    xmlns:apdi="http://ns.apple.com/pixeldatainfo/1.0/"
                    xmlns:depthData="http://ns.apple.com/depthData/1.0/"
                    xmlns:depthBlurEffect="http://ns.apple.com/depthBlurEffect/1.0/">
                 <apdi:IntMaxValue>{intMax}</apdi:IntMaxValue>
                 <apdi:StoredFormat>1278226488</apdi:StoredFormat>
                 <apdi:NativeFormat>1751411059</apdi:NativeFormat>
                 <apdi:IntMinValue>{intMin}</apdi:IntMinValue>
                 <apdi:FloatMaxValue>{floatMax}</apdi:FloatMaxValue>
                 <apdi:FloatMinValue>{floatMin}</apdi:FloatMinValue>
                 <apdi:AuxiliaryImageType>{kind}</apdi:AuxiliaryImageType>
                 <depthData:IntrinsicMatrixReferenceWidth>4032</depthData:IntrinsicMatrixReferenceWidth>
                 <depthData:IntrinsicMatrixReferenceHeight>3024</depthData:IntrinsicMatrixReferenceHeight>
                 <depthData:DepthDataVersion>65541</depthData:DepthDataVersion>
                 <depthData:Quality>high</depthData:Quality>
                 <depthData:IntrinsicMatrix>
                    <rdf:Seq>
                       <rdf:li>5597.6337890625</rdf:li><rdf:li>0.0</rdf:li><rdf:li>0.0</rdf:li>
                       <rdf:li>0.0</rdf:li><rdf:li>5597.6337890625</rdf:li><rdf:li>0.0</rdf:li>
                       <rdf:li>2051.3427734375</rdf:li><rdf:li>1518.791015625</rdf:li><rdf:li>1</rdf:li>
                    </rdf:Seq>
                 </depthData:IntrinsicMatrix>
                 <depthData:LensDistortionCoefficients>
                    <rdf:Seq><rdf:li>0.0</rdf:li><rdf:li>-0.5574</rdf:li></rdf:Seq>
                 </depthData:LensDistortionCoefficients>
                 <depthData:LensDistortionCenterOffsetX>2048.5</depthData:LensDistortionCenterOffsetX>
                 <depthData:LensDistortionCenterOffsetY>1523.25</depthData:LensDistortionCenterOffsetY>
                 <depthData:Accuracy>relative</depthData:Accuracy>
                 <depthData:PixelSize>0.001220</depthData:PixelSize>
                 <depthData:Filtered>True</depthData:Filtered>
                 <depthBlurEffect:RenderingParameters>UkVORAcAAAA=</depthBlurEffect:RenderingParameters>
                 <depthBlurEffect:SimulatedAperture>{aperture}</depthBlurEffect:SimulatedAperture>
                 {extra}
              </rdf:Description>
           </rdf:RDF>
        </x:xmpmeta>
        """;

    [Test]
    public async Task FromXmp_ReadsAppleDisparityDescription()
    {
        HeifAppleDepthInfo info = HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(AppleXmp(floatMax: "0.508301")))!;
        await Assert.That(info).IsNotNull();
        await Assert.That(info.Kind).IsEqualTo(HeifAuxiliaryKind.Disparity);
        await Assert.That((info.StoredFormat, info.NativeFormat)).IsEqualTo(("L008", "hdis"));
        await Assert.That((info.IntMinValue, info.IntMaxValue)).IsEqualTo((0, 255));
        await Assert.That((info.FloatMinValue, info.FloatMaxValue)).IsEqualTo((0f, 0.508301f));
        await Assert.That((info.Accuracy, info.Quality, info.IsFiltered)).IsEqualTo(("relative", "high", (bool?)true));
        await Assert.That(info.DepthDataVersion).IsEqualTo(65541);
        await Assert.That(info.PixelSize).IsEqualTo(0.00122f);
        await Assert.That(info.IntrinsicMatrix.ToArray()).IsEquivalentTo(
            new[] { 5597.6337890625f, 0, 0, 0, 5597.6337890625f, 0, 2051.3427734375f, 1518.791015625f, 1 }, CollectionOrdering.Matching);
        await Assert.That(info.IntrinsicMatrixReferenceSize).IsEqualTo(new Size(4032, 3024));
        await Assert.That(info.ExtrinsicMatrix).IsEmpty();
        await Assert.That(info.LensDistortionCoefficients.ToArray()).IsEquivalentTo(new[] { 0f, -0.5574f }, CollectionOrdering.Matching);
        await Assert.That(info.LensDistortionCenter).IsEqualTo(new PointF(2048.5f, 1523.25f));
        await Assert.That(info.SimulatedAperture).IsEqualTo(4.5f);
        await Assert.That(info.RenderingParameters.ToArray()).IsEquivalentTo("REND\u0007\0\0\0"u8.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task FromXmp_ReadsAttributeFormAndDepthKind()
    {
        byte[] xmp = """
            <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:apdi="http://ns.apple.com/pixeldatainfo/1.0/"
              apdi:AuxiliaryImageType="depth" apdi:IntMinValue="16" apdi:IntMaxValue="1008"
              apdi:FloatMinValue="0.25" apdi:FloatMaxValue="12.5"/>
            </rdf:RDF></x:xmpmeta>
            <?xpacket end="w"?>
            """u8.ToArray();
        HeifAppleDepthInfo info = HeifAppleDepthInfo.FromXmp([.. xmp, 0, 0, 0])!;
        await Assert.That(info.Kind).IsEqualTo(HeifAuxiliaryKind.Depth);
        await Assert.That((info.IntMinValue, info.IntMaxValue, info.FloatMinValue, info.FloatMaxValue)).IsEqualTo((16, 1008, 0.25f, 12.5f));
        await Assert.That(info.StoredFormat).IsNull();
        await Assert.That(info.SimulatedAperture).IsNull();
        await Assert.That(info.IntrinsicMatrix).IsEmpty();
        await Assert.That(info.IntrinsicMatrixReferenceSize).IsNull();
        await Assert.That(info.RenderingParameters.IsEmpty).IsTrue();
    }

    public static IEnumerable<string> InvalidXmp() =>
    [
        "",
        "not xml",
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF",
        "<!DOCTYPE x [<!ENTITY e \"e\">]><x/>",
        AppleXmp(kind: "matte"),
        AppleXmp(kind: ""),
        AppleXmp(intMin: "5", intMax: "5"),
        AppleXmp(intMin: "-1"),
        AppleXmp(intMax: "x"),
        AppleXmp(floatMin: "1", floatMax: "0.5"),
        AppleXmp(floatMax: "NaN"),
        AppleXmp(floatMax: "Infinity"),
        AppleXmp(floatMin: "1e40"),
        AppleXmp().Replace("<apdi:FloatMinValue>0.000000</apdi:FloatMinValue>", "", StringComparison.Ordinal),
    ];

    [Test]
    [MethodDataSource(nameof(InvalidXmp))]
    public async Task FromXmp_MissingOrMalformed_GivesNoDescription(string xmp)
    {
        await Assert.That(HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(xmp))).IsNull();
    }

    [Test]
    public async Task FromXmp_MalformedOptionalFields_AreAbsent()
    {
        string xmp = AppleXmp(aperture: "wide").Replace("UkVORAcAAAA=", "not base64!", StringComparison.Ordinal)
            .Replace("<rdf:li>1</rdf:li>", "<rdf:li>one</rdf:li>", StringComparison.Ordinal)
            .Replace("<depthData:Filtered>True", "<depthData:Filtered>maybe", StringComparison.Ordinal)
            .Replace("1278226488", "1", StringComparison.Ordinal);
        HeifAppleDepthInfo info = HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(xmp))!;
        await Assert.That(info).IsNotNull();
        await Assert.That(info.SimulatedAperture).IsNull();
        await Assert.That(info.RenderingParameters.IsEmpty).IsTrue();
        await Assert.That(info.IntrinsicMatrix).IsEmpty();
        await Assert.That(info.IsFiltered).IsNull();
        await Assert.That(info.StoredFormat).IsNull();
        await Assert.That(HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(AppleXmp(aperture: "-2")))!.SimulatedAperture).IsNull();
    }

    [Test]
    public async Task ToFloat_MapsTheIntegerRangeLinearly_IncludingExtremeValues()
    {
        HeifAppleDepthInfo info = HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(AppleXmp(intMin: "10", intMax: "210", floatMin: "-1", floatMax: "3")))!;
        await Assert.That(info.ToFloat(10)).IsEqualTo(-1f);
        await Assert.That(info.ToFloat(210)).IsEqualTo(3f);
        await Assert.That(info.ToFloat(110)).IsEqualTo(1f);
        await Assert.That(info.ToFloat(0)).IsEqualTo(-1.2f).Within(1e-6f);
        var values = new float[3];
        info.ToFloat([10, 60, 210], values);
        await Assert.That(values).IsEquivalentTo(new[] { -1f, 0f, 3f }, CollectionOrdering.Matching);
        await Assert.That(() => info.ToFloat([1, 2], new float[1])).Throws<ArgumentException>();

        HeifAppleDepthInfo wide = HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(AppleXmp(intMax: "65535", floatMin: "-3e38", floatMax: "3e38")))!;
        await Assert.That(wide.ToFloat(0)).IsEqualTo(-3e38f);
        await Assert.That(wide.ToFloat(65535)).IsEqualTo(3e38f);
        await Assert.That(float.IsFinite(wide.ToFloat(32768))).IsTrue();
        HeifAppleDepthInfo flat = HeifAppleDepthInfo.FromXmp(Encoding.UTF8.GetBytes(AppleXmp(floatMin: "0.5", floatMax: "0.5")))!;
        await Assert.That(flat.ToFloat(200)).IsEqualTo(0.5f);
    }

    [Test]
    public async Task LoadDepth_ParsesAppleDescription_AndToImageStretchesSamples()
    {
        Scene scene = Scene.Build(160, 120);
        HeifDocument doc = HeifDocument.Open(scene.Bytes);
        await Assert.That(doc.PrimaryImage.FindDepthImage()!.Id).IsEqualTo(2u);
        await Assert.That(doc.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)!.Id).IsEqualTo(4u);
        await Assert.That(doc.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.SkyMatte)).IsNull();
        HeifDepthImage depth = doc.LoadDepth(doc.GetItem(2));
        await Assert.That(depth.AppleDepth!.Kind).IsEqualTo(HeifAuxiliaryKind.Disparity);
        await Assert.That(depth.AppleDepth.FloatMaxValue).IsEqualTo(2f);
        await Assert.That(doc.LoadDepth(doc.GetItem(2), new DecoderOptions { IgnoreMetadata = true }).AppleDepth).IsNull();

        using Image image = depth.ToImage();
        await Assert.That((image.Width, image.Height)).IsEqualTo((depth.Width, depth.Height));
        ReadOnlySpan<ushort> samples = depth.Samples.Span;
        bool matches = true;
        for (int i = 0; i < samples.Length; i++)
        {
            Rgba32 p = image.GetPixelSpan()[i];
            matches &= p.R == samples[i] && p.G == samples[i] && p.B == samples[i] && p.A == 255;
        }

        await Assert.That(matches).IsTrue().Because("0–255 samples stretch to themselves");
    }

    [Test]
    public async Task Render_KeepsProtectedFocusedPixels_AndBlursTheBackground()
    {
        Scene scene = Scene.Build(320, 240);
        HeifDocument doc = HeifDocument.Open(scene.Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        using Image result = doc.RenderPortrait(doc.PrimaryImage, null, Options);
        await Assert.That((result.Width, result.Height)).IsEqualTo((photo.Width, photo.Height));
        // Inside the subject (matte 255, disparity at the focus) every pixel is copied bit for bit.
        await Assert.That(Equal(photo, result, new Rectangle(8, 8, 140, 224))).IsTrue();
        double sharp = Energy(photo, Background), blurred = Energy(result, Background);
        await Assert.That(blurred).IsLessThan(sharp * 0.5);

        // Without the matte the subject still stays: its disparity equals the focus.
        HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage()!);
        using Image noMatte = HeifPortrait.Render(photo, depth);
        await Assert.That(Equal(photo, noMatte, new Rectangle(8, 8, 140, 224))).IsTrue();
        using Image copy = doc.LoadImage(doc.PrimaryImage, Options);
        await Assert.That(Equal(photo, copy, new Rectangle(0, 0, 320, 240))).IsTrue().Because("the photograph is not changed");
    }

    [Test]
    public async Task Render_BackgroundDetailDecreasesAsTheApertureOpens()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(320, 240).Bytes);
        double previous = double.MaxValue;
        foreach (float aperture in new[] { 16f, 8f, 4f, 2.8f, 1.4f })
        {
            using Image result = doc.RenderPortrait(doc.PrimaryImage, new HeifPortraitOptions { Aperture = aperture }, Options);
            double energy = Energy(result, Background);
            await Assert.That(energy).IsLessThan(previous).Because($"f/{aperture}");
            previous = energy;
        }
    }

    [Test]
    public async Task Render_FocusPointOnTheBackground_KeepsItSharp()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(320, 240).Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        using Image result = doc.RenderPortrait(doc.PrimaryImage, new HeifPortraitOptions { FocusPoint = new Point(260, 120) }, Options);
        await Assert.That(Equal(photo, result, new Rectangle(172, 8, 140, 224))).IsTrue();
        // The subject is now far from the focus, so the matte no longer protects it.
        await Assert.That(Energy(result, new Rectangle(8, 8, 140, 224))).IsLessThan(Energy(photo, new Rectangle(8, 8, 140, 224)) * 0.5);
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task Render_DoesNotDependOnTheThreadCount()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(704, 512).Bytes);
        using Image single = doc.RenderPortrait(doc.PrimaryImage, new HeifPortraitOptions { MaxDegreeOfParallelism = 1, Aperture = 2 }, Options);
        foreach (int threads in new[] { 2, 3, -1 })
        {
            using Image parallel = doc.RenderPortrait(doc.PrimaryImage, new HeifPortraitOptions { MaxDegreeOfParallelism = threads, Aperture = 2 }, Options);
            await Assert.That(Equal(single, parallel, new Rectangle(0, 0, 704, 512))).IsTrue().Because($"{threads} threads");
        }
    }

    [Test]
    public async Task Render_ChecksLimitsAndCancellation()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(320, 240).Bytes);
        await Assert.That(() => doc.RenderPortrait(doc.PrimaryImage, new HeifPortraitOptions { MaxPixels = 320 * 240 }, Options))
            .Throws<ImageFormatException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => doc.RenderPortrait(doc.PrimaryImage, null, Options, cancellation.Token)).Throws<OperationCanceledException>();
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        HeifDepthImage depth = doc.LoadDepth(doc.GetItem(2));
        await Assert.That(() => HeifPortrait.Render(photo, depth, null, null, cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Render_RejectsInvalidArguments()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(160, 120).Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, Options);
        HeifDepthImage depth = doc.LoadDepth(doc.GetItem(2));
        foreach (float aperture in new[] { 1f, 17f, float.NaN })
        {
            await Assert.That(() => HeifPortrait.Render(photo, depth, null, new HeifPortraitOptions { Aperture = aperture }))
                .Throws<ArgumentOutOfRangeException>();
        }

        await Assert.That(() => HeifPortrait.Render(photo, depth, null, new HeifPortraitOptions { FocusPoint = new Point(160, 0) }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => HeifPortrait.Render(photo, depth, null, new HeifPortraitOptions { MaxDegreeOfParallelism = 0 }))
            .Throws<ArgumentOutOfRangeException>();
        using var square = new Image(60, 60);
        await Assert.That(() => HeifPortrait.Render(photo, depth, square)).Throws<ArgumentException>();
        await Assert.That(() => HeifPortrait.Render(square, depth)).Throws<ArgumentException>();
        await Assert.That(() => HeifPortrait.Render(photo, doc.LoadDepth(doc.GetItem(2), new DecoderOptions { IgnoreMetadata = true })))
            .Throws<ArgumentException>();
        await Assert.That(() => HeifPortrait.Render(null!, depth)).Throws<ArgumentNullException>();
        await Assert.That(() => doc.RenderPortrait(doc.GetItem(4), null, Options)).Throws<ArgumentException>()
            .Because("the matte has no depth image");
    }

    [Test]
    public async Task RenderPortrait_ReportsMissingDescription_AndInconsistentItems()
    {
        HeifDocument noXmp = HeifDocument.Open(Scene.Build(160, 120, xmp: null).Bytes);
        await Assert.That(() => noXmp.RenderPortrait(noXmp.PrimaryImage, null, Options)).Throws<NotSupportedException>();
        HeifDocument narrowRange = HeifDocument.Open(Scene.Build(160, 120, xmp: AppleXmp(intMax: "100")).Bytes);
        await Assert.That(() => narrowRange.RenderPortrait(narrowRange.PrimaryImage, null, Options)).Throws<ImageFormatException>();
        HeifDocument squareMatte = HeifDocument.Open(Scene.Build(160, 120, matteSize: new Size(64, 64)).Bytes);
        await Assert.That(() => squareMatte.RenderPortrait(squareMatte.PrimaryImage, null, Options)).Throws<ImageFormatException>();
        HeifDocument depthOnly = HeifDocument.Open(Scene.Build(160, 120, matteSize: Size.Empty).Bytes);
        using Image rendered = depthOnly.RenderPortrait(depthOnly.PrimaryImage, null, Options);
        await Assert.That(rendered.Width).IsEqualTo(160);
    }

    [Test]
    public async Task RenderPortrait_WithTargetSize_ResamplesDepthAndMatte()
    {
        HeifDocument doc = HeifDocument.Open(Scene.Build(320, 240).Bytes);
        var small = new DecoderOptions { Configuration = Configuration, TargetSize = new Size(160, 120) };
        using Image photo = doc.LoadImage(doc.PrimaryImage, small);
        using Image result = doc.RenderPortrait(doc.PrimaryImage, null, small);
        await Assert.That((result.Width, result.Height)).IsEqualTo((photo.Width, photo.Height));
        await Assert.That(Equal(photo, result, new Rectangle(4, 4, 70, 112))).IsTrue();
    }

    private static readonly Rectangle Background = new(176, 8, 136, 224);

    private static bool Equal(Image a, Image b, Rectangle area)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            if (!a.GetRowSpan(y).Slice(area.Left, area.Width).SequenceEqual(b.GetRowSpan(y).Slice(area.Left, area.Width)))
            {
                return false;
            }
        }

        return true;
    }

    // Mean squared horizontal and vertical luma differences: high-frequency energy.
    internal static double Energy(Image image, Rectangle area)
    {
        double sum = 0;
        long count = 0;
        for (int y = area.Top; y < area.Bottom - 1; y++)
        {
            for (int x = area.Left; x < area.Right - 1; x++)
            {
                double l = Luma(image[x, y]), right = Luma(image[x + 1, y]), down = Luma(image[x, y + 1]);
                sum += (l - right) * (l - right) + (l - down) * (l - down);
                count++;
            }
        }

        return sum / count;
    }

    internal static double Luma(Rgba32 p) => 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;

    // A photograph whose left half is the subject: near disparity (sample 200), matte 255; the right half is far
    // (sample 40). The depth is a quarter and the matte half of the photograph's size, as in iPhone files.
    private sealed record Scene(byte[] Bytes)
    {
        public static Scene Build(int width, int height, string? xmp = "", Size? matteSize = null)
        {
            using Image photo = Texture(width, height);
            using Image depth = Halves(width / 4, height / 4, 200, 40);
            Size matteDimensions = matteSize ?? new Size(width / 2, height / 2);
            var items = new List<Item>
            {
                Coded(1, photo, [], null, hidden: false),
                Coded(2, depth, [Auxiliary(DepthType, 0x5a, 0, 7)], [("auxl", [1])], hidden: true),
            };
            if (xmp is not null)
            {
                items.Add(new Item(3, "mime", [], Encoding.UTF8.GetBytes(xmp.Length == 0 ? AppleXmp() : xmp), References: [("cdsc", [2])]));
            }

            if (!matteDimensions.IsEmpty)
            {
                using Image matte = Halves(matteDimensions.Width, matteDimensions.Height, 255, 0);
                items.Add(Coded(4, matte, [Auxiliary(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)], [("auxl", [1])], hidden: true));
            }

            return new Scene(HeifDocumentFixture.Build([.. items]));
        }

        private static Item Coded(int id, Image image, byte[][] extra, (string, int[])[]? references, bool hidden)
        {
            byte[] file = image.ToHeic(new HeifEncoderOptions { Configuration = Configuration, TileSize = 0, Quality = 95 });
            byte[][] properties = [.. HeifTestItems.ItemProperties(file, 1), .. extra];
            byte[] payload = HeifTestItems.Payloads(file).Single(item => item.Id == 1).Data;
            return new Item(id, Properties: properties, Payload: payload, References: references, Hidden: hidden);
        }

        private static Image Texture(int width, int height)
        {
            var image = new Image(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = image.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    // Fine stripes and a checkerboard carry high frequencies everywhere.
                    int stripe = ((x / 2) + (y / 3)) % 2 == 0 ? 60 : 0, check = ((x / 5) + (y / 5)) % 2 == 0 ? 80 : 0;
                    row[x] = new Rgba32((byte)(60 + stripe + check), (byte)(90 + check), (byte)(150 - stripe));
                }
            }

            return image;
        }

        private static Image Halves(int width, int height, byte left, byte right)
        {
            var image = new Image(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = image.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte v = x < width / 2 ? left : right;
                    row[x] = new Rgba32(v, v, v);
                }
            }

            return image;
        }
    }
}
