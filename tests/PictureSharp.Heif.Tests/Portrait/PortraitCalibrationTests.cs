using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

[NotInParallel]
public sealed class PortraitCalibrationTests
{
    [Test]
    [Property("Category", "Measurement")]
    public async Task Measure_CaptureFocusCandidate()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_CAPTURE_FOCUS") == "1", "Set PICTURESHARP_HEIF_CAPTURE_FOCUS=1 for the capture-focus experiment.");
        string root = HevcLocalTests.FindRoot();
        string path = Path.Combine(root, "artifacts", "portrait-focus-metadata", "mapped-points.json");
        Skip.Unless(File.Exists(PortraitMeasurementRun.ManifestPath(root)), "Needs private samples and the E1 manifest.");
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        PortraitCaptureFocus.Mapped[] points = PortraitCaptureFocus.ReadPoints(catalog);
        if (File.Exists(path)) { PortraitCaptureFocus.VerifySavedPoints(path, points); }
        var capture = points.ToDictionary(p => p.Scene, p => new System.Drawing.Point(p.X, p.Y));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(Path.Combine(root, "artifacts", "portrait-focus-metadata", "verified-points.json"), JsonSerializer.Serialize(points, PortraitMeasurementInputs.Json));
        PortraitMeasurementManifest run = PortraitMeasurementRun.Measure(root, catalog, freeze: false, PortraitMeasurementRun.Read(root), capture);
        await Assert.That(run.Pairs.Length).IsEqualTo(catalog.Scenes.Sum(s => s.Exports.Length));
        PortraitMeasurementRun.WriteReports(root, run, freeze: false, experiment: "capture-focus-candidate");
        string[] sources = ["HeifPortrait.cs", "HeifPortraitOptions.cs", "Codecs/HeifPortraitRenderer.cs"];
        File.WriteAllText(Path.Combine(root, "artifacts", "portrait-0.6.7-capture-focus-experiment.json"), JsonSerializer.Serialize(new
        {
            Policy = "Only null FocusPoint is replaced by the transformed EXIF SubjectArea centre; existing explicit refocus is unchanged.",
            VerifiedPointsSha256 = PortraitMeasurementRun.FileHash(Path.Combine(root, "artifacts", "portrait-focus-metadata", "verified-points.json")),
            WorkingSources = sources.ToDictionary(p => p, p => PortraitMeasurementRun.FileHash(Path.Combine(root, "src", "PictureSharp.Heif", p))),
            MeasurementSources = new[] { "PortraitMeasurementRun.cs", "PortraitCaptureFocus.cs", "PortraitCalibrationTests.cs" }
                .ToDictionary(p => p, p => PortraitMeasurementRun.FileHash(Path.Combine(root, "tests", "PictureSharp.Heif.Tests", "Portrait", p))),
        }, PortraitMeasurementInputs.Json));
    }

    [Test]
    [Property("Category", "Local")]
    public async Task CaptureFocus_MatchesIndependentMappingOnEveryPrivateScene()
    {
        string root = HevcLocalTests.FindRoot();
        string folder = Path.Combine(root, "artifacts", "portrait-focus-metadata");
        string path = Path.Combine(folder, "mapped-points.json");
        Skip.Unless(File.Exists(path), "Needs the independent private capture mapping.");
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        PortraitCaptureFocus.Mapped[] points = PortraitCaptureFocus.ReadPoints(catalog);
        PortraitCaptureFocus.VerifySavedPoints(path, points);
        await Assert.That(points.Length).IsEqualTo(catalog.Scenes.Length);
        File.WriteAllText(Path.Combine(folder, "verified-points.json"), JsonSerializer.Serialize(points, PortraitMeasurementInputs.Json));
    }

    [Test]
    public async Task DiscProbe_NormalizesConstantAtEdgesAndPreservesZeroRadius()
    {
        using var image = new Image(17, 13);
        image.GetPixelSpan().Fill(new Rgba32(17, 127, 241));
        var probe = new PortraitDiscProbe(image);
        foreach (float radius in new[] { 0f, 0.75f, 2f, 8f, 24f })
        {
            PortraitDiscProbe.Kernel kernel = PortraitDiscProbe.Disc(radius);
            await Assert.That(probe.At(0, 0, kernel)).IsEqualTo(new Rgba32(17, 127, 241));
            await Assert.That(probe.At(8, 6, kernel)).IsEqualTo(new Rgba32(17, 127, 241));
        }
        await Assert.That(() => PortraitDiscProbe.Disc(float.NaN)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DiscProbe_MixesInLinearLightWithAnAntialiasedRim()
    {
        using var image = new Image(3, 3);
        image.GetPixelSpan().Fill(new Rgba32(0, 0, 0));
        image[1, 1] = new Rgba32(255, 255, 255);
        var probe = new PortraitDiscProbe(image);
        await Assert.That(probe.At(1, 1, PortraitDiscProbe.Disc(0))).IsEqualTo(new Rgba32(255, 255, 255));
        // At r=.75 the centre has weight 1, four cardinal neighbours .25; linear white/2 encodes to 188.
        await Assert.That(probe.At(1, 1, PortraitDiscProbe.Disc(0.75f))).IsEqualTo(new Rgba32(188, 188, 188));
        // Prefix interior and rim must agree with a direct antialiased convolution, including clipped borders.
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                double mass = 0, centre = 0;
                for (int sy = 0; sy < 3; sy++)
                {
                    for (int sx = 0; sx < 3; sx++)
                    {
                        double weight = Math.Clamp(2 - Math.Sqrt((sx - x) * (sx - x) + (sy - y) * (sy - y)) + 0.5, 0, 1);
                        mass += weight;
                        if (sx == 1 && sy == 1) { centre = weight; }
                    }
                }
                double linear = centre / mass;
                int expected = (int)Math.Round(255 * (linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055));
                await Assert.That((int)probe.At(x, y, PortraitDiscProbe.Disc(2)).R).IsEqualTo(expected).Within(1);
            }
        }
    }

    // Half-width in pixels of the synthetic outline band E4 may change: the 7×7-sample depth split window (depth is
    // four times coarser) plus bilinear sampling and the direct discs.
    private const int OutlineBand = 20;

    // E5 grain (0.6 codes at the default ISO 64) changes blurred pixels by a few codes; anything larger is a change
    // of the blur itself.
    private static bool Close(ReadOnlySpan<Rgba32> a, ReadOnlySpan<Rgba32> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i].R - b[i].R) > 4 || Math.Abs(a[i].G - b[i].G) > 4 || Math.Abs(a[i].B - b[i].B) > 4 || a[i].A != b[i].A)
            {
                return false;
            }
        }

        return a.Length == b.Length;
    }

    [Test]
    public async Task ForegroundPerson_BlursLessWhileFocusAndUnmaskedRenderingRemainStable()
    {
        HeifDocument doc = HeifDocument.Open(HeifPortraitTests.Scene.Build(320, 240).Bytes);
        using Image photo = doc.LoadImage(doc.PrimaryImage, HeifPortraitAuditTests.Decode);
        HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
        using Image matte = doc.LoadImage(doc.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)!, HeifPortraitAuditTests.Decode);
        var focus = new System.Drawing.Point(240, 120);
        var options = new HeifPortraitOptions { Aperture = 1.4f, FocusPoint = focus, MaxDegreeOfParallelism = 1 };
        var oldOptions = new BaselineV066.HeifPortraitOptions { Aperture = 1.4f, FocusPoint = focus, MaxDegreeOfParallelism = 1 };
        using Image old = BaselineV066.HeifPortrait.Render(photo, depth, matte, oldOptions);
        using Image current = HeifPortrait.Render(photo, depth, matte, options);
        var foreground = new System.Drawing.Rectangle(16, 16, 128, 208);
        await Assert.That(HeifPortraitTests.Energy(current, foreground)).IsGreaterThan(HeifPortraitTests.Energy(old, foreground));
        bool focused = true;
        for (int y = 0; y < 240; y++)
        {
            focused &= current.GetRowSpan(y)[176..].SequenceEqual(old.GetRowSpan(y)[176..]);
        }
        await Assert.That(focused).IsTrue();
        using Image unmaskedOld = BaselineV066.HeifPortrait.Render(photo, depth, null, oldOptions);
        using Image unmaskedNew = HeifPortrait.Render(photo, depth, null, options);
        // E5: without a matte the near half is scenery in front of the focus, which Photos blurs less; behind the
        // focus the render stays as before (within grain).
        await Assert.That(HeifPortraitTests.Energy(unmaskedNew, foreground)).IsGreaterThan(HeifPortraitTests.Energy(unmaskedOld, foreground));
        bool behind = true;
        for (int y = 0; y < 240; y++)
        {
            behind &= Close(unmaskedNew.GetRowSpan(y)[(160 + OutlineBand)..], unmaskedOld.GetRowSpan(y)[(160 + OutlineBand)..]);
        }

        await Assert.That(behind).IsTrue();
        // The foreground correction is gone by f/8. E4 changes the matte's outline (x = 160) at every aperture, so the
        // comparison leaves out the band its depth split and outline treatment reach.
        foreach (float aperture in new[] { 8f, 16f })
        {
            using Image narrowOld = BaselineV066.HeifPortrait.Render(photo, depth, matte, oldOptions with { Aperture = aperture });
            using Image narrowNew = HeifPortrait.Render(photo, depth, matte, options with { Aperture = aperture });
            bool away = true;
            for (int y = 0; y < 240; y++)
            {
                away &= Close(narrowNew.GetRowSpan(y)[..(160 - OutlineBand)], narrowOld.GetRowSpan(y)[..(160 - OutlineBand)])
                    && Close(narrowNew.GetRowSpan(y)[(160 + OutlineBand)..], narrowOld.GetRowSpan(y)[(160 + OutlineBand)..]);
            }

            await Assert.That(away).IsTrue().Because($"f/{aperture} away from the outline");
        }
        using Image parallel = HeifPortrait.Render(photo, depth, matte, options with { MaxDegreeOfParallelism = 4 });
        await Assert.That(current.GetPixelSpan().SequenceEqual(parallel.GetPixelSpan())).IsTrue();
    }

    [Test]
    [Property("Category", "Measurement")]
    public async Task Measure_PortraitRadiusAcrossAllExports()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_RADIUS") == "1", "Set PICTURESHARP_HEIF_RADIUS=1 for E2 radius calibration.");
        string root = HevcLocalTests.FindRoot();
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        Skip.Unless(catalog.Scenes.Length > 0 && File.Exists(PortraitMeasurementRun.ManifestPath(root)), "Needs private samples and the E1 manifest.");
        await Assert.That(File.Exists(Path.Combine(root, "docs", "measurements", "portrait-0.6.7-radius.tsv"))).IsFalse().Because("Do not overwrite calibration measurements; name a new revision.");
        PortraitRadiusResult[] results = PortraitRadiusMeasurements.Measure(root, catalog, PortraitMeasurementRun.Read(root));
        await Assert.That(results.Length).IsEqualTo(catalog.Scenes.Sum(s => s.Exports.Length));
        await Assert.That(results.All(r => r.Tiles.Length > 0)).IsTrue();
        PortraitRadiusMeasurements.Write(root, results);
    }

    [Test]
    [Property("Category", "Measurement")]
    public async Task Measure_FocusCandidatesFromSharpRegions()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_FOCUS") == "1", "Set PICTURESHARP_HEIF_FOCUS=1 for focus candidates.");
        string root = HevcLocalTests.FindRoot();
        Skip.Unless(File.Exists(PortraitMeasurementRun.ManifestPath(root)), "Needs the E1 private manifest.");
        string[] rows = PortraitFocusMeasurements.Measure(root);
        await Assert.That(rows.Length).IsGreaterThan(1);
        string path = Path.Combine(root, "docs", "measurements", "portrait-0.6.7-focus.tsv");
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew));
        foreach (string row in rows) { writer.WriteLine(row); }
    }

    [Test]
    [Property("Category", "Measurement")]
    public async Task Measure_RenderingParametersInventory()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_REND") == "1", "Set PICTURESHARP_HEIF_REND=1 for the E2 REND inventory.");
        string root = HevcLocalTests.FindRoot();
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        Skip.Unless(catalog.Scenes.Length > 0, "Needs private portrait samples.");
        var rows = new List<string> { "scene\tcamera\tfocal_ratio\tstored_aperture\tversion\tdeclared_size\tmarker\tblob_bytes\tentries\tkey\ttype\tvalue\traw_hex\tduplicate_key\tblob_sha256\texports_identical" };
        PortraitMeasurementManifest baseline = PortraitMeasurementRun.Read(root);
        foreach (PortraitScene scene in catalog.Scenes)
        {
            HeifDocument doc = HeifDocument.Open(scene.Original);
            HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
            HeifAppleDepthInfo info = depth.AppleDepth!;
            byte[] blob = info.RenderingParameters.ToArray();
            await Assert.That(blob.AsSpan(0, 4).SequenceEqual("REND"u8)).IsTrue().Because(scene.Name);
            await Assert.That((blob.Length - 16) % 8).IsEqualTo(0);
            int version = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4)), declared = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8)), marker = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(12));
            double ratio = info.IntrinsicMatrix[0] / (double)Math.Max(info.IntrinsicMatrixReferenceSize!.Value.Width, info.IntrinsicMatrixReferenceSize.Value.Height);
            bool identical = true;
            foreach (PortraitExport export in scene.Exports)
            {
                HeifDocument exported = HeifDocument.Open(export.Path);
                HeifItemInfo? item = exported.PrimaryImage.FindDepthImage();
                if (item is null) { identical = false; continue; }
                HeifDepthImage other = exported.LoadDepth(item, HeifPortraitAuditTests.Decode);
                identical &= other.AppleDepth is { } otherInfo && otherInfo.RenderingParameters.Span.SequenceEqual(blob);
            }
            string camera = baseline.Pairs.First(p => p.Scene == scene.Name).Camera;
            var seen = new HashSet<ushort>();
            for (int p = 16; p < blob.Length; p += 8)
            {
                ushort key = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p)), type = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + 2));
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(p + 4));
                string value = type == 1 ? BitConverter.Int32BitsToSingle(unchecked((int)raw)).ToString("G9", CultureInfo.InvariantCulture)
                    : type == 2 ? unchecked((int)raw).ToString(CultureInfo.InvariantCulture) : "unknown";
                rows.Add(FormattableString.Invariant($"{scene.Name}\t{camera}\t{ratio:G9}\t{info.SimulatedAperture:G9}\t{version}\t{declared}\t{marker}\t{blob.Length}\t{(blob.Length - 16) / 8}\t{key}\t{type}\t{value}\t{raw:x8}\t{!seen.Add(key)}\t{PortraitMeasurementRun.Hash(blob)}\t{identical}"));
            }
        }
        string path = Path.Combine(root, "docs", "measurements", "portrait-0.6.7-rendering-parameters.tsv");
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew));
        foreach (string line in rows) { writer.WriteLine(line); }
        Console.WriteLine($"REND inventory: {catalog.Scenes.Length} scenes, {rows.Count - 1} entries.");
    }
}
