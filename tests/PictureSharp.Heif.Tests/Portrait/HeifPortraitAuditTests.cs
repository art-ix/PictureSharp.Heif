using System.Drawing;
using System.Globalization;

namespace PictureSharp.Formats.Heif.Tests;

[NotInParallel]
public sealed class HeifPortraitAuditTests
{
    internal static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    internal static readonly DecoderOptions Decode = new() { Configuration = Configuration, ApplyExifOrientation = true, LoadGainMap = false };

    [Test]
    [Property("Category", "Local")]
    public async Task Audit_PrivateCatalogAndPreviews()
    {
        string root = HevcLocalTests.FindRoot();
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        Skip.Unless(catalog.Scenes.Length > 0, "No private portrait catalogue.");
        string output = Path.Combine(root, "artifacts", "portrait-0.6.7-audit");
        Directory.CreateDirectory(output);
        var lines = new List<string> { "scene\texports\twidth\theight\tdepth\tportrait_matte\thair_matte\tsky_matte\tmodel\tfocal_mm\torientation\tedited" };
        foreach (PortraitScene scene in catalog.Scenes)
        {
            HeifDocument doc = HeifDocument.Open(scene.Original);
            using Image photo = doc.LoadImage(doc.PrimaryImage, Decode);
            HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage() ?? throw new InvalidDataException($"{scene.Name}: no depth."), Decode);
            string Map(string type)
            {
                if (doc.PrimaryImage.FindAuxiliaryImage(type) is not { } item)
                {
                    return "absent";
                }

                using Image image = doc.LoadImage(item, Decode);
                if (!Matching(photo.Width, photo.Height, image.Width, image.Height))
                {
                    throw new InvalidDataException($"{scene.Name}: auxiliary aspect ratio mismatch.");
                }

                string size = $"{image.Width}x{image.Height}";
                string role = type == HeifAppleAuxiliaryTypes.PortraitEffectsMatte ? "portrait" : type == HeifAppleAuxiliaryTypes.HairMatte ? "hair" : "sky";
                SavePreview(image, Path.Combine(output, scene.Name + "." + role + ".png"));
                return size;
            }

            await Assert.That(Matching(photo.Width, photo.Height, depth.Width, depth.Height)).IsTrue().Because(scene.Name);
            await Assert.That(photo.Metadata.Orientation is ExifOrientation.TopLeft or ExifOrientation.Unknown).IsTrue().Because(scene.Name);
            await Assert.That(depth.AppleDepth).IsNotNull().Because(scene.Name);
            photo.Metadata.ExifProfile.TryGetValue(ExifTag.Model, out string? model);
            photo.Metadata.ExifProfile.TryGetValue(ExifTag.FocalLength, out ExifRational focal);
            string portrait = Map(HeifAppleAuxiliaryTypes.PortraitEffectsMatte), hair = Map(HeifAppleAuxiliaryTypes.HairMatte), sky = Map(HeifAppleAuxiliaryTypes.SkyMatte);
            lines.Add(string.Join('\t', scene.Name, scene.Exports.Length, photo.Width, photo.Height, $"{depth.Width}x{depth.Height}", portrait, hair, sky,
                model ?? "unknown", focal.Denominator == 0 ? "N/A" : focal.ToDouble().ToString("G8", CultureInfo.InvariantCulture), photo.Metadata.Orientation, scene.Edited));
            Console.WriteLine(lines[^1]);
            SavePreview(photo, Path.Combine(output, scene.Name + ".png"));
            using Image depthPreview = depth.ToImage();
            SavePreview(depthPreview, Path.Combine(output, scene.Name + ".depth.png"));
            foreach (PortraitExport export in scene.Exports)
            {
                HeifDocument reference = HeifDocument.Open(export.Path);
                await Assert.That(reference.PrimaryImage.DisplaySize).IsEqualTo(doc.PrimaryImage.DisplaySize).Because(export.Path);
            }
        }

        File.WriteAllLines(Path.Combine(output, "catalog.tsv"), lines);
        File.WriteAllLines(Path.Combine(output, "notices.txt"), catalog.Notices.Concat(catalog.Ignored.Select(p => "ignored: " + p)));
        Console.WriteLine($"Portrait audit: {catalog.Scenes.Length} originals, {catalog.Scenes.Sum(s => s.Exports.Length)} exports; {output}");
    }

    [Test]
    [Property("Category", "Measurement")]
    public async Task Audit_SharpRoiToneSupport()
    {
        string root = HevcLocalTests.FindRoot();
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_PORTRAIT_TONE_AUDIT") == "1", "Set PICTURESHARP_HEIF_PORTRAIT_TONE_AUDIT=1 for the tone preflight.");
        string inputs = Path.Combine(root, "artifacts", "portrait-0.6.7-roi.json");
        Skip.Unless(File.Exists(inputs), "No local ROI definitions.");
        Directory.CreateDirectory(Path.Combine(root, "artifacts", "portrait-0.6.7-audit"));
        var definitions = PortraitMeasurementInputs.Read(inputs);
        var lines = new List<string> { "scene\tvariant\taperture\tstatus\tsamples\trmse" };
        foreach (PortraitScene scene in PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait")).Scenes)
        {
            HeifDocument doc = HeifDocument.Open(scene.Original);
            using Image full = doc.LoadImage(doc.PrimaryImage, Decode);
            using Image photo = PortraitMeasurementInputs.Reduce(full);
            foreach (PortraitExport export in scene.Exports)
            {
                HeifDocument reference = HeifDocument.Open(export.Path);
                using Image native = reference.LoadImage(reference.PrimaryImage, Decode);
                using Image apple = PortraitMeasurementInputs.Reduce(native);
                if (scene.Name.StartsWith("liscie", StringComparison.Ordinal))
                {
                    using Image preview = native.Clone(x => x.Resize(480, 640));
                    preview.Metadata.Clear();
                    preview.Save(Path.Combine(root, "artifacts", "portrait-0.6.7-audit", scene.Name + ".export.png"));
                }
                PortraitFocus focus = definitions[scene.Name].Focus[export.Variant == "e1" ? "default" : export.Variant];
                PortraitTone tone = PortraitTone.Fit(photo, apple, PortraitMeasurementInputs.Rectangle(focus.Sharp, photo.Width, photo.Height));
                lines.Add(string.Join('\t', scene.Name, export.Variant, export.Aperture.ToString(CultureInfo.InvariantCulture), tone.Status, tone.Samples, tone.Residual?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A"));
            }
        }

        File.WriteAllLines(Path.Combine(root, "artifacts", "portrait-0.6.7-audit", "tone.tsv"), lines);
        await Assert.That(lines.Count).IsGreaterThan(1);
    }

    private static void SavePreview(Image image, string path)
    {
        using Image preview = image.Clone(x => x.Resize(image.Width * 640 / Math.Max(image.Width, image.Height), image.Height * 640 / Math.Max(image.Width, image.Height)));
        preview.Metadata.Clear();
        preview.Save(path);
    }

    internal static bool Matching(int width, int height, int mapWidth, int mapHeight)
    {
        long longer = Math.Max(width, height), mapLonger = Math.Max(mapWidth, mapHeight);
        return Math.Abs(mapWidth * longer - width * mapLonger) <= longer && Math.Abs(mapHeight * longer - height * mapLonger) <= longer;
    }
}
