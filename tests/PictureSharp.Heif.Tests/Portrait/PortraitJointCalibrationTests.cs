namespace PictureSharp.Formats.Heif.Tests;

[NotInParallel]
public sealed class PortraitJointCalibrationTests
{
    [Test]
    [Property("Category", "Measurement")]
    public async Task Measure_CaptureDisparity()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_JOINT") == "1", "Set PICTURESHARP_HEIF_JOINT=1 for joint focus/radius measurements.");
        string root = HevcLocalTests.FindRoot();
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        Skip.Unless(catalog.Scenes.Length > 0, "Needs the private portrait corpus.");
        var points = PortraitCaptureFocus.ReadPoints(catalog).ToDictionary(p => p.Scene);
        var rows = new List<string> { "scene\tcapture_focus\toriginal_sha256\tdepth_sha256" };
        foreach (PortraitScene scene in catalog.Scenes)
        {
            HeifDocument document = HeifDocument.Open(scene.Original);
            HeifDepthImage depth = document.LoadDepth(document.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
            PortraitCaptureFocus.Mapped point = points[scene.Name];
            double focus = PortraitRadiusMeasurements.LegacyFocus(depth, null, new System.Drawing.Point(point.X, point.Y), point.Width, point.Height);
            rows.Add(FormattableString.Invariant($"{scene.Name}\t{focus:G9}\t{point.OriginalSha256}\t{PortraitMeasurementRun.Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(depth.Samples.Span))}"));
        }
        string path = Path.Combine(root, "docs", "measurements", "portrait-0.6.7-capture-disparity.tsv");
        using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew));
        foreach (string row in rows) { output.WriteLine(row); }
        await Assert.That(rows.Count).IsEqualTo(1 + catalog.Scenes.Length);
    }

    [Test]
    [Property("Category", "Measurement")]
    public async Task Measure_DefaultFocusDistribution()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_JOINT") == "1", "Set PICTURESHARP_HEIF_JOINT=1 for joint focus/radius measurements.");
        string root = HevcLocalTests.FindRoot();
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        Skip.Unless(catalog.Scenes.Length > 0, "Needs the private portrait corpus.");
        double[] extensions = [0, .25, .5, .75, .9, .95, 1];
        var rows = new List<string> { "scene\tuses_matte\tselected_samples\tdepth_samples\textension\tquantile\tdisparity\toriginal_sha256\tdepth_sha256" };
        foreach (PortraitScene scene in catalog.Scenes)
        {
            HeifDocument document = HeifDocument.Open(scene.Original);
            HeifDepthImage depth = document.LoadDepth(document.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
            using Image? matte = document.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte) is { } item
                ? document.LoadImage(item, HeifPortraitAuditTests.Decode) : null;
            var all = new List<float>(depth.Width * depth.Height);
            var subject = new List<float>();
            for (int y = 0; y < depth.Height; y++)
            {
                for (int x = 0; x < depth.Width; x++)
                {
                    float value = depth.AppleDepth!.ToFloat(depth.Samples.Span[y * depth.Width + x]);
                    if (depth.AppleDepth.Kind == HeifAuxiliaryKind.Depth) { value = value > 0 ? 1 / value : 0; }
                    all.Add(value);
                    if (matte is not null && matte[Math.Min(matte.Width - 1, (int)((x + .5) * matte.Width / depth.Width)),
                        Math.Min(matte.Height - 1, (int)((y + .5) * matte.Height / depth.Height))].R >= 128)
                    { subject.Add(value); }
                }
            }
            bool usesMatte = subject.Count >= Math.Max(16, all.Count / 1000);
            List<float> selected = usesMatte ? subject : all;
            selected.Sort();
            double start = usesMatte ? .5 : .9;
            foreach (double extension in extensions)
            {
                double q = start + extension * (1 - start);
                float disparity = selected[(int)Math.Round(q * (selected.Count - 1))];
                rows.Add(FormattableString.Invariant($"{scene.Name}\t{usesMatte}\t{selected.Count}\t{all.Count}\t{extension:G9}\t{q:G9}\t{disparity:G9}\t{PortraitMeasurementRun.FileHash(scene.Original)}\t{PortraitMeasurementRun.Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(depth.Samples.Span))}"));
            }
        }
        string path = Path.Combine(root, "docs", "measurements", "portrait-0.6.7-focus-distribution.tsv");
        using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew));
        foreach (string row in rows) { output.WriteLine(row); }
        await Assert.That(rows.Count).IsEqualTo(1 + catalog.Scenes.Length * extensions.Length);
    }
}
