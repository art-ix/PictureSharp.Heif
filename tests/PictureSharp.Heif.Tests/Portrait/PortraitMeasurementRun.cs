using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

internal sealed record PortraitPairMeasurement(
    string Scene, string Camera, string Model, double FocalMm, int? EquivalentMm, bool Edited, string Variant, float Aperture,
    string Original, string Export, string OriginalHash, string ExportHash,
    int NativeWidth, int NativeHeight, int Width, int Height, string DepthSize, string MatteSize, string HairSize,
    string RoiHash, string RenderHash, string OriginalPixelsHash, string ReferencePixelsHash,
    string DepthHash, string DepthXmpHash, string MatteHash, string HairHash,
    PortraitFocus Focus, PortraitTone Tone,
    PortraitScore[] OriginalRaw, PortraitScore[] BaselineRaw, PortraitScore[]? OriginalNormalized, PortraitScore[]? BaselineNormalized,
    long GrainPixels, double? OriginalGrain, double? BaselineGrain, double? ReferenceGrain,
    double? OriginalNormalizedGrain, double? BaselineNormalizedGrain,
    long Milliseconds, long AllocatedBytes);

internal sealed record PortraitMeasurementManifest(int Schema, string Metrics, int MeasurementLongSide, string SourceCommit,
    string SourceHash, string RoiDefinitionHash, string RoiDefinitions, string Runtime, string Architecture,
    PortraitPairMeasurement[] Pairs);

// A set of private portrait scenes with its own frozen baseline. Validation scenes (added 2026-10-19, user decision) are
// never used for tuning; they live in a separate folder so calibration and E2 tools that scan samples/portrait skip them.
internal sealed record PortraitSampleSet(string Folder, string BaselineName, string RoiFile, string ReportPrefix)
{
    public static readonly PortraitSampleSet Main = new("portrait", "portrait-0.6.7-baseline", "portrait-0.6.7-roi.json", "portrait-0.6.7-");
    public static readonly PortraitSampleSet Validation = new("portrait-validation", "portrait-0.6.7-validation-baseline",
        "portrait-0.6.7-validation-roi.json", "portrait-0.6.7-validation-");

    public string Samples(string root) => Path.Combine(root, "samples", Folder);
}

internal static class PortraitMeasurementRun
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public const string BaselineName = "portrait-0.6.7-baseline";
    public static string ManifestPath(string root, PortraitSampleSet? set = null)
    {
        set ??= PortraitSampleSet.Main;
        return Path.Combine(set.Samples(root), set.BaselineName + "-inputs.json");
    }

    public static string BaselinePath(string root, PortraitSampleSet? set = null) =>
        Path.Combine(root, "docs", "measurements", (set ?? PortraitSampleSet.Main).BaselineName + ".tsv");
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string FileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string PixelsHash(Image image) => Hash(MemoryMarshal.AsBytes(image.GetPixelSpan()));

    public static PortraitMeasurementManifest Read(string root, PortraitSampleSet? set = null)
    {
        PortraitMeasurementManifest saved = JsonSerializer.Deserialize<PortraitMeasurementManifest>(File.ReadAllText(ManifestPath(root, set)))
            ?? throw new InvalidDataException("No frozen manifest.");
        if (saved.Schema != 1 || saved.Metrics != PortraitMetrics.Version || saved.MeasurementLongSide != PortraitMeasurementInputs.MeasurementLongSide
            || Hash(Encoding.UTF8.GetBytes(saved.RoiDefinitions)) != saved.RoiDefinitionHash)
        {
            throw new InvalidDataException("Measurement protocol differs from the frozen baseline; create a separate revision.");
        }

        return saved;
    }

    public static PortraitMeasurementManifest Measure(string root, PortraitCatalog catalog, bool freeze, PortraitMeasurementManifest? saved = null,
        IReadOnlyDictionary<string, System.Drawing.Point>? captureFocus = null, PortraitSampleSet? set = null, bool subset = false)
    {
        set ??= PortraitSampleSet.Main;
        if (freeze && captureFocus is not null) { throw new ArgumentException("Capture focus is a separate experiment, never a baseline input.", nameof(captureFocus)); }
        string roiText = freeze ? File.ReadAllText(Path.Combine(root, "artifacts", set.RoiFile)) : saved!.RoiDefinitions;
        var definitions = (JsonSerializer.Deserialize<PortraitRoi[]>(roiText) ?? throw new InvalidDataException("No ROI definitions."))
            .ToDictionary(r => r.Scene, StringComparer.Ordinal);
        string[] unconfigured = catalog.Scenes.Select(s => s.Name).Except(definitions.Keys, StringComparer.Ordinal).ToArray();
        if (unconfigured.Length != 0 || (!subset && definitions.Count != catalog.Scenes.Length))
        {
            throw new InvalidDataException("ROI coverage differs from the catalogue: " + string.Join(", ", unconfigured));
        }

        // Experiments may measure scenes in parallel (PICTURESHARP_HEIF_PORTRAIT_JOBS); renders do not depend on it, but
        // render_ms and allocated_bytes are then only indicative. Pairs keep the catalogue order.
        int jobs = int.TryParse(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_PORTRAIT_JOBS"), out int parsed) && parsed > 1 ? parsed : 1;
        var perScene = new List<PortraitPairMeasurement>[catalog.Scenes.Length];
        int total = catalog.Scenes.Sum(s => s.Exports.Length), finished = 0;
        Parallel.For(0, catalog.Scenes.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, index =>
        {
            PortraitScene scene = catalog.Scenes[index];
            var results = perScene[index] = [];
            PortraitRoi roi = definitions[scene.Name];
            HeifDocument doc = HeifDocument.Open(scene.Original);
            using Image native = doc.LoadImage(doc.PrimaryImage, HeifPortraitAuditTests.Decode);
            HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage() ?? throw new InvalidDataException($"{scene.Name}: missing depth."), HeifPortraitAuditTests.Decode);
            using Image? matte = LoadMap(HeifAppleAuxiliaryTypes.PortraitEffectsMatte);
            using Image? hair = LoadMap(HeifAppleAuxiliaryTypes.HairMatte);
            using Image photo = PortraitMeasurementInputs.Reduce(native);
            if (native.Metadata.Orientation is not (ExifOrientation.TopLeft or ExifOrientation.Unknown)
                || !HeifPortraitAuditTests.Matching(native.Width, native.Height, depth.Width, depth.Height))
            {
                throw new InvalidDataException($"{scene.Name}: orientation/aspect mismatch.");
            }

            native.Metadata.ExifProfile.TryGetValue(ExifTag.Model, out string? model);
            native.Metadata.ExifProfile.TryGetValue(ExifTag.FocalLength, out ExifRational focal);
            int? equivalent = PortraitExif.EquivalentFocalLength(native.Metadata.Exif);
            string camera = equivalent switch { 24 => "1x", 48 => "2x", 77 => "3x", 100 => "4x", 30 => "front", _ => "unknown" };
            string originalHash = FileHash(scene.Original), originalPixelsHash = PixelsHash(photo);
            string depthHash = Hash(MemoryMarshal.AsBytes(depth.Samples.Span)), depthXmpHash = Hash(depth.Xmp.Span);
            string matteHash = matte is null ? "absent" : PixelsHash(matte), hairHash = hair is null ? "absent" : PixelsHash(hair);
            var rows = new PortraitPairMeasurement[scene.Exports.Length];
            Parallel.For(0, scene.Exports.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, e =>
            {
                PortraitExport export = scene.Exports[e];
                string variant = export.Variant == "e1" ? "default" : export.Variant;
                if (!roi.Focus.TryGetValue(variant, out PortraitFocus? focus))
                {
                    throw new InvalidDataException($"{scene.Name}/{variant}: missing focus/ROI definition.");
                }

                string originalRelative = Path.GetRelativePath(root, scene.Original), exportRelative = Path.GetRelativePath(root, export.Path);
                string exportHash = FileHash(export.Path);
                PortraitPairMeasurement? previous = saved?.Pairs.SingleOrDefault(p => p.Scene == scene.Name && p.Variant == export.Variant && p.Aperture == export.Aperture);
                // Paths compare ignoring case (a renamed extension, .HEIC -> .heic); the file hashes pin the content.
                if (!freeze && (previous is null || !previous.Original.Equals(originalRelative, StringComparison.OrdinalIgnoreCase)
                    || !previous.Export.Equals(exportRelative, StringComparison.OrdinalIgnoreCase)
                    || previous.OriginalHash != originalHash || previous.ExportHash != exportHash || previous.Edited != scene.Edited))
                {
                    throw new InvalidDataException($"{scene.Name}/{export.Variant}/f{export.Aperture}: input differs from the frozen manifest.");
                }

                HeifDocument reference = HeifDocument.Open(export.Path);
                using Image appleNative = reference.LoadImage(reference.PrimaryImage, HeifPortraitAuditTests.Decode);
                PortraitMetrics.CheckSize(native, appleNative);
                using Image apple = PortraitMeasurementInputs.Reduce(appleNative);
                byte[] regions = PortraitMeasurementInputs.Regions(roi, focus, photo, matte, hair);
                string roiHash = Hash(regions), referenceHash = PixelsHash(apple);
                if (!freeze && (previous!.RoiHash != roiHash || previous.OriginalPixelsHash != originalPixelsHash || previous.ReferencePixelsHash != referenceHash
                    || previous.DepthHash != depthHash || previous.DepthXmpHash != depthXmpHash || previous.MatteHash != matteHash || previous.HairHash != hairHash))
                {
                    throw new InvalidDataException($"{scene.Name}: decoder pixels or ROI differ from the baseline.");
                }

                PortraitTone tone = freeze ? PortraitTone.Fit(photo, apple, PortraitMeasurementInputs.Rectangle(focus.Sharp, photo.Width, photo.Height)) : previous!.Tone;
                long allocated = GC.GetTotalAllocatedBytes(true);
                var clock = Stopwatch.StartNew();
                System.Drawing.Point? focusPoint = PortraitMeasurementInputs.Point(focus.Point, native.Width, native.Height);
                if (focusPoint is null && captureFocus is not null) { focusPoint = captureFocus[scene.Name]; }
                using Image rendered = freeze
                    ? BaselineV066.HeifPortrait.Render(native, depth, matte, new BaselineV066.HeifPortraitOptions
                    {
                        Aperture = export.Aperture, FocusPoint = focusPoint, MaxDegreeOfParallelism = 1,
                    })
                    : HeifPortrait.Render(native, depth, matte, new HeifPortraitOptions
                    {
                        Aperture = export.Aperture, FocusPoint = focusPoint, MaxDegreeOfParallelism = 1,
                    });
                long elapsed = clock.ElapsedMilliseconds, bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                using Image reduced = PortraitMeasurementInputs.Reduce(rendered);
                Dump(scene.Name, export, reduced, apple, regions, tone);
                PortraitScore[] originalRaw = PortraitMetrics.Measure(photo, apple, regions), baselineRaw = PortraitMetrics.Measure(reduced, apple, regions);
                if (originalRaw[0].Pixels == 0 || originalRaw[4].Pixels == 0)
                {
                    throw new InvalidDataException($"{scene.Name}: empty required background or focused ROI.");
                }

                PortraitScore[]? originalNormalized = tone.Lut is null ? null : PortraitMetrics.Measure(photo, apple, regions, tone.Lut);
                PortraitScore[]? baselineNormalized = tone.Lut is null ? null : PortraitMetrics.Measure(reduced, apple, regions, tone.Lut);
                var originalGrain = PortraitMetrics.HighPass(photo, regions);
                var baselineGrain = PortraitMetrics.HighPass(reduced, regions);
                var appleGrain = PortraitMetrics.HighPass(apple, regions);
                double? originalNormGrain = tone.Lut is null ? null : PortraitMetrics.HighPass(photo, regions, tone.Lut).Deviation;
                double? baselineNormGrain = tone.Lut is null ? null : PortraitMetrics.HighPass(reduced, regions, tone.Lut).Deviation;
                var row = new PortraitPairMeasurement(scene.Name, camera, model ?? "unknown", focal.Denominator == 0 ? 0 : focal.ToDouble(), equivalent,
                    scene.Edited, export.Variant, export.Aperture, originalRelative, exportRelative, originalHash, exportHash,
                    native.Width, native.Height, photo.Width, photo.Height, $"{depth.Width}x{depth.Height}", Size(matte), Size(hair),
                    roiHash, PixelsHash(rendered), originalPixelsHash, referenceHash, depthHash, depthXmpHash, matteHash, hairHash, focus, tone,
                    originalRaw, baselineRaw, originalNormalized, baselineNormalized,
                    originalGrain.Count, originalGrain.Deviation, baselineGrain.Deviation, appleGrain.Deviation, originalNormGrain, baselineNormGrain,
                    elapsed, bytes);
                rows[e] = row;
                Console.WriteLine($"Portrait {Interlocked.Increment(ref finished)}/{total}: {scene.Name}/{export.Variant}/f{export.Aperture} "
                    + $"{elapsed} ms, background {Number(baselineRaw[0].Psnr)} dB raw, {Number(baselineNormalized?[0].Psnr)} dB normalized; tone={tone.Status}");
            });
            results.AddRange(rows);

            Image? LoadMap(string type)
            {
                if (doc.PrimaryImage.FindAuxiliaryImage(type) is not { } item) { return null; }
                Image map = doc.LoadImage(item, HeifPortraitAuditTests.Decode);
                if (!HeifPortraitAuditTests.Matching(native.Width, native.Height, map.Width, map.Height))
                {
                    map.Dispose();
                    throw new InvalidDataException($"{scene.Name}: auxiliary aspect mismatch.");
                }

                return map;
            }
        });
        List<PortraitPairMeasurement> results = [.. perScene.SelectMany(pairs => pairs)];

        if (!freeze && !subset && results.Count != saved!.Pairs.Length)
        {
            throw new InvalidDataException("Frozen pairs disappeared from the catalogue.");
        }

        string source = Path.Combine(root, "tests", "PictureSharp.Heif.Tests", "Portrait", "BaselineV066", "source.json");
        return new(1, PortraitMetrics.Version, PortraitMeasurementInputs.MeasurementLongSide,
            "f22ee6ea1672059df7bedb2160b67515c12cf419", FileHash(source), Hash(Encoding.UTF8.GetBytes(roiText)), roiText,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(), [.. results]);
    }

    public static void WriteReports(string root, PortraitMeasurementManifest run, bool freeze, string? experiment = null, PortraitSampleSet? set = null)
    {
        set ??= PortraitSampleSet.Main;
        if (experiment is not null && (freeze || !System.Text.RegularExpressions.Regex.IsMatch(experiment, "^[a-z0-9-]+$")))
        {
            throw new ArgumentException("An experiment needs a safe, separate report name and cannot replace the baseline.", nameof(experiment));
        }
        string prefix = freeze ? set.BaselineName : set.ReportPrefix + (experiment ?? "current");
        string output = freeze ? Path.Combine(root, "docs", "measurements") : Path.Combine(root, "artifacts");
        Directory.CreateDirectory(output);
        string tsv = Path.Combine(output, prefix + ".tsv"), summary = Path.Combine(output, prefix + "-summary.tsv");
        string context = Path.Combine(output, prefix + ".json");
        string manifest = freeze ? ManifestPath(root, set) : Path.Combine(output, prefix + "-inputs.json");
        string[] targets = [tsv, summary, context, manifest];
        if (freeze && targets.Any(File.Exists))
        {
            throw new IOException("Baseline artifacts already exist. Never overwrite the frozen baseline; choose a new protocol revision.");
        }

        var header = new List<string> { "scene", "camera", "model", "focal_mm", "equivalent_mm", "edited", "variant", "aperture", "native_width", "native_height", "width", "height", "tone_status", "tone_samples", "tone_rmse", "focus_confidence" };
        foreach (PortraitRegion region in PortraitMetrics.Regions)
        {
            string key = region.ToString().ToLowerInvariant();
            header.AddRange(new[] { "pixels", "ssim_windows", "original_raw_mse", "render_raw_mse", "original_raw_psnr", "render_raw_psnr", "original_raw_ssim", "render_raw_ssim", "original_norm_mse", "render_norm_mse", "original_norm_psnr", "render_norm_psnr", "original_norm_ssim", "render_norm_ssim" }.Select(c => key + "_" + c));
        }

        header.AddRange(["grain_pixels", "original_raw_grain", "render_raw_grain", "reference_grain", "original_norm_grain", "render_norm_grain", "render_ms", "allocated_bytes"]);
        var lines = new List<string> { string.Join('\t', header) };
        foreach (PortraitPairMeasurement p in run.Pairs)
        {
            var fields = new List<string> { p.Scene, p.Camera, p.Model, Number(p.FocalMm), Number(p.EquivalentMm), p.Edited ? "yes" : "no", p.Variant,
                Number(p.Aperture), p.NativeWidth.ToString(Invariant), p.NativeHeight.ToString(Invariant), p.Width.ToString(Invariant), p.Height.ToString(Invariant),
                p.Tone.Status, p.Tone.Samples.ToString(Invariant), Number(p.Tone.Residual), p.Focus.Confidence };
            for (int r = 0; r < PortraitMetrics.Regions.Length; r++)
            {
                PortraitScore a = p.OriginalRaw[r], b = p.BaselineRaw[r];
                PortraitScore? na = p.OriginalNormalized?[r], nb = p.BaselineNormalized?[r];
                fields.AddRange([a.Pixels.ToString(Invariant), a.Windows.ToString(Invariant), Number(a.Mse), Number(b.Mse), Number(a.Psnr), Number(b.Psnr), Number(a.Ssim), Number(b.Ssim),
                    Number(na?.Mse), Number(nb?.Mse), Number(na?.Psnr), Number(nb?.Psnr), Number(na?.Ssim), Number(nb?.Ssim)]);
            }

            fields.AddRange([p.GrainPixels.ToString(Invariant), Number(p.OriginalGrain), Number(p.BaselineGrain), Number(p.ReferenceGrain), Number(p.OriginalNormalizedGrain), Number(p.BaselineNormalizedGrain),
                p.Milliseconds.ToString(Invariant), p.AllocatedBytes.ToString(Invariant)]);
            lines.Add(string.Join('\t', fields));
        }

        Write(tsv, string.Join('\n', lines) + "\n", freeze);
        Write(summary, Summary(run), freeze);
        Write(manifest, JsonSerializer.Serialize(run, PortraitMeasurementInputs.Json) + "\n", freeze);
        var provenance = new
        {
            run.Schema, run.Metrics, run.SourceCommit, run.SourceHash, run.RoiDefinitionHash, run.Runtime, run.Architecture,
            Experiment = experiment,
            PictureSharp = typeof(Image).Assembly.GetName().Version?.ToString(), Decoder = "HeifFormat; ApplyExifOrientation=true; LoadGainMap=false; native size; default colour conversion",
            Render = experiment is null ? "native size; MaxDegreeOfParallelism=1; explicit aperture; default or manual provisional FocusPoint"
                : "native size; MaxDegreeOfParallelism=1; explicit aperture; focus policy recorded in the separate experiment provenance",
            Measurement = $"long side <= {run.MeasurementLongSide}; PictureSharp default Resize; RGB8 MSE; BT.601 luma SSIM 8x8 stride 8, wholly inside ROI; edge L1 distance 2..24 at long side 4032; high-pass 7x7 wholly background",
            Tone = "per-channel affine least squares in fixed manual sharp ROI; source/reference values 4..251; >=256 samples/channel; variance>=16; slope[0.5,2]; |offset|<=64; RMSE<=5; otherwise normalized metrics N/A; LUT applied equally to original and render",
            Aggregate = "equal pair MSE within scene, equal scenes, then PSNR; edited and low-light test scenes (leaves, user decision 2026-10-19) separately; provisional refocus separately",
            Originals = run.Pairs.Select(p => p.Scene).Distinct().Count(), Exports = run.Pairs.Length,
            BaselineTsvSha256 = FileHash(tsv), SummarySha256 = FileHash(summary), LocalManifestSha256 = FileHash(manifest),
        };
        Write(context, JsonSerializer.Serialize(provenance, PortraitMeasurementInputs.Json) + "\n", freeze);
    }

    private static string Summary(PortraitMeasurementManifest run)
    {
        var lines = new List<string> { "group\tvalue\tcohort\tregion\tmode\tpairs\tscenes\toriginal_mse\trender_mse\toriginal_psnr\trender_psnr" };
        var groups = new List<(string Group, string Value, IEnumerable<PortraitPairMeasurement> Pairs)> { ("all", "all", run.Pairs) };
        groups.AddRange(run.Pairs.GroupBy(p => p.Scene).Select(g => ("scene", g.Key, g.AsEnumerable())));
        groups.AddRange(run.Pairs.GroupBy(p => p.Camera).Select(g => ("camera", g.Key, g.AsEnumerable())));
        groups.AddRange(run.Pairs.GroupBy(p => p.Aperture).Select(g => ("aperture", Number(g.Key), g.AsEnumerable())));
        groups.AddRange(run.Pairs.GroupBy(p => p.Variant).Select(g => ("variant", g.Key, g.AsEnumerable())));
        foreach (var group in groups)
        {
            foreach (string cohort in new[] { "clean-all", "clean-default", "clean-refocus-provisional", "edited", "low-light-test" })
            {
                PortraitPairMeasurement[] pairs = group.Pairs.Where(p => InCohort(p, cohort)).ToArray();
                for (int r = 0; r < PortraitMetrics.Regions.Length; r++)
                {
                    foreach (bool normalized in new[] { false, true })
                    {
                        var valid = pairs.Where(p => (normalized ? p.BaselineNormalized?[r] : p.BaselineRaw[r])?.Mse is not null).ToArray();
                        if (valid.Length == 0) { continue; }
                        double? a = PortraitMetrics.PooledMse(valid.Select(p => (p.Scene, (normalized ? p.OriginalNormalized![r] : p.OriginalRaw[r]).Mse)));
                        double? b = PortraitMetrics.PooledMse(valid.Select(p => (p.Scene, (normalized ? p.BaselineNormalized![r] : p.BaselineRaw[r]).Mse)));
                        lines.Add(string.Join('\t', group.Group, group.Value, cohort, PortraitMetrics.Regions[r], normalized ? "normalized" : "raw", valid.Length,
                            valid.Select(p => p.Scene).Distinct().Count(), Number(a), Number(b), Number(new PortraitScore(1, 0, a, null).Psnr), Number(new PortraitScore(1, 0, b, null).Psnr)));
                    }
                }
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    public static bool InCohort(PortraitPairMeasurement pair, string cohort) => cohort switch
    {
        "edited" => pair.Edited,
        "low-light-test" => !pair.Edited && PortraitSampleCatalog.IsLowLightTest(pair.Scene),
        _ when pair.Edited || PortraitSampleCatalog.IsLowLightTest(pair.Scene) => false,
        "clean-all" => true,
        "clean-refocus-provisional" => pair.Variant.StartsWith("refocus", StringComparison.Ordinal),
        "clean-default" => !pair.Variant.StartsWith("refocus", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(cohort), cohort, null),
    };

    // Private diagnostics (E5 metrics computed outside the test): with PICTURESHARP_HEIF_PORTRAIT_DUMP set to a folder,
    // each pair's reduced render is written there; the reference, ROI map and tone LUT once per pair.
    private static void Dump(string scene, PortraitExport export, Image reduced, Image apple, byte[] regions, PortraitTone tone)
    {
        if (Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_PORTRAIT_DUMP") is not { Length: > 0 } folder) { return; }
        Directory.CreateDirectory(folder);
        string stem = Path.Combine(folder, $"{scene}_{export.Variant}_f{export.Aperture.ToString(Invariant)}");
        reduced.Save(stem + "_render.png");
        if (!File.Exists(stem + "_regions.bin"))
        {
            apple.Save(stem + "_reference.png");
            File.WriteAllBytes(stem + "_regions.bin", regions);
            if (tone.Lut is not null) { File.WriteAllBytes(stem + "_lut.bin", [.. tone.Lut.SelectMany(c => c)]); }
        }
    }

    private static string Size(Image? image) => image is null ? "absent" : $"{image.Width}x{image.Height}";
    private static string Number(double? value) => value is null ? "N/A" : double.IsPositiveInfinity(value.Value) ? "Infinity" : value.Value.ToString("G17", Invariant);
    private static void Write(string path, string value, bool frozen)
    {
        using var stream = new FileStream(path, frozen ? FileMode.CreateNew : FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value);
    }
}
