using System.Drawing;
using System.Text;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class PortraitInfrastructureTests
{
    [Test]
    public async Task Catalog_ParsesAperturesRefocusAndIgnoresMovieAndDescription()
    {
        string root = Directory.CreateTempSubdirectory("heif-portrait-catalog-").FullName;
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, "scene_2x")).FullName;
            foreach (string file in new[] { "img_1234.HeIc", "IMG_1234_a1.4.HEIC", "IMG_1234_refocus_siatla_a16.heic", "IMG_1234.MOV", "opis.txt" })
            {
                File.WriteAllText(Path.Combine(folder, file), "");
            }

            PortraitCatalog result = PortraitSampleCatalog.Scan(root);
            await Assert.That(result.Scenes.Length).IsEqualTo(1);
            await Assert.That(result.Scenes[0].Exports.Select(e => e.Aperture)).IsEquivalentTo(new[] { 1.4f, 16f }, CollectionOrdering.Matching);
            await Assert.That(result.Scenes[0].Exports[1].Variant).IsEqualTo("refocus_siatla");
            await Assert.That(result.Ignored.Length).IsEqualTo(2);
            File.WriteAllText(Path.Combine(folder, "IMG_1234_refocus_siatla_a16.0.HEIC"), "");
            await Assert.That(() => PortraitSampleCatalog.Scan(root)).Throws<InvalidDataException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Catalog_RejectsAmbiguousOriginalAndUnclassifiedHeic()
    {
        string root = Directory.CreateTempSubdirectory("heif-portrait-catalog-").FullName;
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, "scene")).FullName;
            File.WriteAllText(Path.Combine(folder, "IMG_1234.HEIC"), "");
            File.WriteAllText(Path.Combine(folder, "IMG_5678.HEIC"), "");
            await Assert.That(() => PortraitSampleCatalog.Scan(root)).Throws<InvalidDataException>();
            File.Delete(Path.Combine(folder, "IMG_5678.HEIC"));
            File.WriteAllText(Path.Combine(folder, "unexpected.heic"), "");
            await Assert.That(() => PortraitSampleCatalog.Scan(root)).Throws<InvalidDataException>();
        }
        finally { Directory.Delete(root, recursive: true); }
        await Assert.That(PortraitSampleCatalog.Scan(Path.Combine(root, "absent")).Scenes.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Metrics_ExactShiftAndEmptyRoiHaveDefinedResults()
    {
        using var a = new Image(16, 16);
        using var b = new Image(16, 16);
        a.GetPixelSpan().Fill(new Rgba32(80, 80, 80));
        b.GetPixelSpan().Fill(new Rgba32(90, 90, 90));
        byte[] regions = Enumerable.Repeat((byte)PortraitRegion.Background, 256).ToArray();
        PortraitScore identical = PortraitMetrics.Measure(a, a, regions)[0], shifted = PortraitMetrics.Measure(a, b, regions)[0];
        await Assert.That(identical.Mse).IsEqualTo(0d);
        await Assert.That(identical.Psnr).IsEqualTo(double.PositiveInfinity);
        await Assert.That(identical.Ssim!.Value).IsEqualTo(1d).Within(1e-12);
        await Assert.That(shifted.Mse).IsEqualTo(100d);
        await Assert.That(shifted.Psnr!.Value).IsEqualTo(10 * Math.Log10(650.25)).Within(1e-12);
        await Assert.That(PortraitMetrics.Measure(a, b, regions)[1].Mse).IsNull();
        await Assert.That(PortraitMetrics.Measure(a, b, regions)[1].Ssim).IsNull();
        await Assert.That(PortraitMetrics.HighPass(a, new byte[256]).Deviation).IsNull();
        await Assert.That(PortraitMetrics.HighPass(a, regions).Deviation!.Value).IsEqualTo(0d).Within(1e-10);
        regions[0] = 0;
        await Assert.That(PortraitMetrics.Measure(a, a, regions)[0].Windows).IsEqualTo(3L);
        using var wrong = new Image(2, 2);
        await Assert.That(() => PortraitMetrics.Measure(a, wrong, regions)).Throws<ArgumentException>();
        await Assert.That(() => PortraitMetrics.Measure(a, b, new byte[1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Tone_UsesOriginalReferenceOnlyAndRejectsEmptyOrFlatSupport()
    {
        using var source = new Image(32, 32);
        using var target = new Image(32, 32);
        for (int i = 0; i < 1024; i++)
        {
            byte v = (byte)(20 + i % 180);
            source.GetPixelSpan()[i] = new(v, v, v);
            target.GetPixelSpan()[i] = new((byte)(v + 10), (byte)(v + 5), v);
        }

        PortraitTone tone = PortraitTone.Fit(source, target, new(0, 0, 32, 32));
        await Assert.That(tone.Status).IsEqualTo("ok");
        await Assert.That(tone.Residual).IsEqualTo(0d);
        await Assert.That(PortraitTone.Map(new(80, 80, 80), tone.Lut)).IsEqualTo(new Rgba32(90, 85, 80));
        await Assert.That(PortraitTone.Fit(source, target, Rectangle.Empty).Lut).IsNull();
        source.GetPixelSpan().Fill(new(70, 70, 70));
        await Assert.That(PortraitTone.Fit(source, target, new(0, 0, 32, 32)).Lut).IsNull();
    }

    [Test]
    public async Task Aggregation_WeightsScenesEquallyAndEmptyValuesStayAbsent()
    {
        double? mse = PortraitMetrics.PooledMse(new[] { ("a", (double?)100), ("a", (double?)100), ("a", (double?)100), ("b", (double?)900), ("missing", (double?)null) });
        await Assert.That(mse).IsEqualTo(500d);
        await Assert.That(PortraitMetrics.PooledMse([])).IsNull();
    }

    [Test]
    public async Task Regions_UseExplicitObjectRoiAndNeverTreatSkyAsSubject()
    {
        using var photo = new Image(100, 100);
        var focus = new PortraitFocus(null, [0.1, 0.1, 0.1, 0.1], "synthetic");
        var roi = new PortraitRoi("object", [[0.5, 0, 0.5, 1]], [[0, 0, 0.25, 1]], new() { ["default"] = focus });
        byte[] regions = PortraitMeasurementInputs.Regions(roi, focus, photo, null, null);
        await Assert.That(regions.Count(v => (v & (byte)PortraitRegion.Background) != 0)).IsEqualTo(5000);
        await Assert.That(regions.Count(v => (v & (byte)PortraitRegion.Subject) != 0)).IsEqualTo(2500);
        await Assert.That(regions.Count(v => (v & (byte)PortraitRegion.Focus) != 0)).IsEqualTo(100);
        await Assert.That(regions.Count(v => (v & (byte)PortraitRegion.Hair) != 0)).IsEqualTo(0);
        await Assert.That(() => PortraitMeasurementInputs.Rectangle([0.9, 0, 0.2, 1], 100, 100)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Exif_ReadsOnlyEquivalentFocalTagAndHandlesBadOffsets()
    {
        foreach (bool little in new[] { true, false })
        {
            byte[] tiff = new byte[44];
            tiff[0] = tiff[1] = (byte)(little ? 'I' : 'M');
            void W16(int offset, ushort value)
            {
                if (little) { System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset), value); }
                else { System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(offset), value); }
            }
            void W32(int offset, uint value)
            {
                if (little) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset), value); }
                else { System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(offset), value); }
            }
            W16(2, 42); W32(4, 8); W16(8, 1); W16(10, 0x8769); W16(12, 4); W32(14, 1); W32(18, 26);
            W16(26, 1); W16(28, 0xa405); W16(30, 3); W32(32, 1); W16(36, 48);
            await Assert.That(PortraitExif.EquivalentFocalLength(tiff)).IsEqualTo(48);
            W32(18, uint.MaxValue);
            await Assert.That(PortraitExif.EquivalentFocalLength(tiff)).IsNull();
        }
        await Assert.That(PortraitExif.EquivalentFocalLength([1, 2])).IsNull();
    }

    [Test]
    public async Task Freeze_RefusesToOverwriteAnyBaselineArtifact()
    {
        string root = Directory.CreateTempSubdirectory("heif-portrait-freeze-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs", "measurements"));
            File.WriteAllText(PortraitMeasurementRun.BaselinePath(root), "immutable-sentinel");
            var empty = new PortraitMeasurementManifest(1, PortraitMetrics.Version, PortraitMeasurementInputs.MeasurementLongSide,
                "frozen", "source", "roi", "[]", "runtime", "architecture", []);
            await Assert.That(() => PortraitMeasurementRun.WriteReports(root, empty, freeze: true)).Throws<IOException>();
            await Assert.That(File.ReadAllText(PortraitMeasurementRun.BaselinePath(root))).IsEqualTo("immutable-sentinel");
            await Assert.That(File.Exists(PortraitMeasurementRun.ManifestPath(root))).IsFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Experiments_CannotChangeTheFrozenBaselineOrEscapeTheirOutputFolder()
    {
        var empty = new PortraitMeasurementManifest(1, PortraitMetrics.Version, PortraitMeasurementInputs.MeasurementLongSide,
            "frozen", "source", "roi", "[]", "runtime", "architecture", []);
        await Assert.That(() => PortraitMeasurementRun.Measure("unused", new PortraitCatalog([], [], []), freeze: true,
            captureFocus: new Dictionary<string, System.Drawing.Point>())).Throws<ArgumentException>();
        foreach (string name in new[] { "../baseline", "/baseline", "capture-focus" })
        {
            await Assert.That(() => PortraitMeasurementRun.WriteReports("unused", empty, freeze: true, experiment: name)).Throws<ArgumentException>();
        }
        await Assert.That(() => PortraitMeasurementRun.WriteReports("unused", empty, freeze: false, experiment: "../baseline")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Read_RejectsAnIncompatibleMeasurementProtocol()
    {
        string root = Directory.CreateTempSubdirectory("heif-portrait-manifest-").FullName;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PortraitMeasurementRun.ManifestPath(root))!);
            var invalid = new PortraitMeasurementManifest(1, "different-metrics", PortraitMeasurementInputs.MeasurementLongSide,
                "frozen", "source", PortraitMeasurementRun.Hash(Encoding.UTF8.GetBytes("[]")), "[]", "runtime", "architecture", []);
            File.WriteAllText(PortraitMeasurementRun.ManifestPath(root), JsonSerializer.Serialize(invalid));
            await Assert.That(() => PortraitMeasurementRun.Read(root)).Throws<InvalidDataException>();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task FrozenSource_HasPinnedChecksums()
    {
        string folder = Path.Combine(HevcLocalTests.FindRoot(), "tests", "PictureSharp.Heif.Tests", "Portrait", "BaselineV066");
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "source.json")));
        foreach (JsonProperty file in source.RootElement.GetProperty("frozenSha256").EnumerateObject())
        {
            await Assert.That(PortraitMeasurementRun.FileHash(Path.Combine(folder, file.Name))).IsEqualTo(file.Value.GetString()).Because(file.Name);
        }
    }
}
