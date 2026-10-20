using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

// Private portrait inputs remain local. The public TSV contains only numeric measurements and scene identifiers.
[NotInParallel]
public sealed class HeifPortraitLocalTests
{
    [Test]
    [Property("Category", "Measurement")]
    public Task Freeze_Portrait066Baseline() => Freeze(PortraitSampleSet.Main, "PICTURESHARP_HEIF_FREEZE_PORTRAIT");

    [Test]
    [Property("Category", "Measurement")]
    public Task Freeze_PortraitValidation066Baseline() => Freeze(PortraitSampleSet.Validation, "PICTURESHARP_HEIF_FREEZE_PORTRAIT_VALIDATION");

    private static async Task Freeze(PortraitSampleSet set, string variable)
    {
        Skip.Unless(Environment.GetEnvironmentVariable(variable) == "1",
            $"Set {variable}=1 to create the baseline once. Existing baseline artifacts are never overwritten.");
        string root = HevcLocalTests.FindRoot();
        foreach (string file in new[] { PortraitMeasurementRun.ManifestPath(root, set), PortraitMeasurementRun.BaselinePath(root, set),
            Path.Combine(root, "docs", "measurements", set.BaselineName + ".json"),
            Path.Combine(root, "docs", "measurements", set.BaselineName + "-summary.tsv") })
        {
            await Assert.That(File.Exists(file)).IsFalse().Because("Frozen baseline already exists: " + file);
        }

        PortraitCatalog catalog = PortraitSampleCatalog.Scan(set.Samples(root));
        Skip.Unless(catalog.Scenes.Length > 0, $"No private Portrait photos in samples/{set.Folder}/.");
        PortraitMeasurementManifest run = PortraitMeasurementRun.Measure(root, catalog, freeze: true, set: set);
        await Assert.That(run.Pairs.Length).IsEqualTo(catalog.Scenes.Sum(s => s.Exports.Length));
        PortraitMeasurementRun.WriteReports(root, run, freeze: true, set: set);
        Console.WriteLine("Frozen baseline: " + PortraitMeasurementRun.BaselinePath(root, set));
    }

    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_AgreeWithFrozenPortraitBaseline()
    {
        (PortraitMeasurementManifest baseline, PortraitMeasurementManifest current) = await MeasureAgainstBaseline(PortraitSampleSet.Main);
        await AssertPairGate(baseline, current);
        if (Scenes() is not null)
        {
            return;
        }

        // DoD: the pooled clean background beats the frozen 0.6.5/0.6.6 model (E1) under the same cohort definition.
        // The subject-vs-unblurred condition is not met yet and is reported, not asserted.
        double? frozenBackground = PooledBackground(baseline), currentBackground = PooledBackground(current);
        await Assert.That(currentBackground!.Value).IsLessThan(frozenBackground!.Value)
            .Because($"clean background MSE {currentBackground} vs E1 {frozenBackground}");

        // E1 established the reference.
        // At E1 every native render must also agree byte for byte with the frozen implementation.
        if (Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_VERIFY_PORTRAIT_FREEZE") == "1")
        {
            foreach (PortraitPairMeasurement pair in current.Pairs)
            {
                PortraitPairMeasurement old = baseline.Pairs.Single(p => p.Scene == pair.Scene && p.Variant == pair.Variant && p.Aperture == pair.Aperture);
                await Assert.That(pair.RenderHash).IsEqualTo(old.RenderHash).Because($"E1 exact native render: {pair.Scene}/{pair.Variant}/f{pair.Aperture}");
            }
        }
    }

    // Held-out scenes, never used for tuning: the same per-pair gate against their own frozen 0.6.6 renders. The pooled
    // background over a few scenes is reported in the summary, not asserted.
    [Test]
    [Property("Category", "Local")]
    public async Task PrivateValidationSamples_AgreeWithFrozenPortraitBaseline()
    {
        (PortraitMeasurementManifest baseline, PortraitMeasurementManifest current) = await MeasureAgainstBaseline(PortraitSampleSet.Validation);
        await AssertPairGate(baseline, current);
    }

    private static async Task<(PortraitMeasurementManifest Baseline, PortraitMeasurementManifest Current)> MeasureAgainstBaseline(PortraitSampleSet set)
    {
        string root = HevcLocalTests.FindRoot();
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(set.Samples(root));
        string[]? scenes = Scenes();
        if (scenes is not null)
        {
            catalog = catalog with { Scenes = [.. catalog.Scenes.Where(s => scenes.Contains(s.Name))] };
        }

        Skip.Unless(catalog.Scenes.Length > 0 && File.Exists(PortraitMeasurementRun.ManifestPath(root, set)),
            $"Needs private samples/{set.Folder}/ and the frozen baseline inputs manifest; a missing baseline is not a pass.");
        PortraitMeasurementManifest baseline = PortraitMeasurementRun.Read(root, set);
        using JsonDocument context = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(PortraitMeasurementRun.BaselinePath(root, set), ".json")));
        await Assert.That(PortraitMeasurementRun.FileHash(PortraitMeasurementRun.BaselinePath(root, set)))
            .IsEqualTo(context.RootElement.GetProperty("BaselineTsvSha256").GetString());
        await Assert.That(PortraitMeasurementRun.FileHash(PortraitMeasurementRun.ManifestPath(root, set)))
            .IsEqualTo(context.RootElement.GetProperty("LocalManifestSha256").GetString());
        PortraitMeasurementManifest current = PortraitMeasurementRun.Measure(root, catalog, freeze: false, baseline, set: set, subset: scenes is not null);
        PortraitMeasurementRun.WriteReports(root, current, freeze: false, scenes is null ? null : "subset", set);
        return (baseline, current);
    }

    // Screening of risky scenes during experiments: PICTURESHARP_HEIF_PORTRAIT_SCENES=a,b checks only those scenes'
    // pairs (reports go to *-subset*, the pooled background is not asserted). Acceptance always runs every pair.
    private static string[]? Scenes() =>
        Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_PORTRAIT_SCENES") is { Length: > 0 } list ? list.Split(',') : null;

    private static async Task AssertPairGate(PortraitMeasurementManifest baseline, PortraitMeasurementManifest current)
    {
        foreach (PortraitPairMeasurement pair in current.Pairs)
        {
            PortraitPairMeasurement old = baseline.Pairs.Single(p => p.Scene == pair.Scene && p.Variant == pair.Variant && p.Aperture == pair.Aperture);
            IEnumerable<int> regions = pair.Edited ? [0, 4] : Enumerable.Range(0, PortraitMetrics.Regions.Length);
            foreach (int r in regions)
            {
                PortraitScore score = pair.BaselineNormalized?[r] ?? pair.BaselineRaw[r];
                PortraitScore before = old.BaselineNormalized?[r] ?? old.BaselineRaw[r];
                await Assert.That(score.Pixels).IsEqualTo(before.Pixels);
                if (before.Mse is { } mse && !EdgeBandException(pair, r))
                {
                    await Assert.That(score.Mse!.Value).IsLessThanOrEqualTo(mse * Math.Pow(10, 0.5 / 10) + 1e-10)
                        .Because($"{pair.Scene}/{pair.Variant}/f{pair.Aperture}: {PortraitMetrics.Regions[r]} regression <=0.5 dB");
                }
            }
        }
    }

    // User decisions 2026-10-19 (E4, docs/measurements/portrait-0.6.7-edges.md): at f/8–f/16 the edge and hair bands
    // are reported but not held to the per-pair limit. Fully protecting partly matted pixels there loses up to 2 dB
    // against E1, which kept them blurred by accident; side-by-side comparisons were judged visually. kapiel_2x f/4.5
    // edges are excepted too: wet strands behind the ear lie farther than the face but share its coarse depth, and no
    // depth source tried (matte weighting, colour-guided upsampling) separated them from strands in front of a far
    // background without regressing other scenes.
    private static bool EdgeBandException(PortraitPairMeasurement pair, int region) =>
        (pair.Aperture >= 8 && PortraitMetrics.Regions[region] is PortraitRegion.Edge or PortraitRegion.Hair)
        || (pair.Scene == "kapiel_2x" && pair.Variant == "default" && pair.Aperture == 4.5f
            && PortraitMetrics.Regions[region] == PortraitRegion.Edge);

    private static double? PooledBackground(PortraitMeasurementManifest run) => PortraitMetrics.PooledMse(run.Pairs
        .Where(p => PortraitMeasurementRun.InCohort(p, "clean-all") && p.BaselineNormalized?[0].Mse is not null)
        .Select(p => (p.Scene, p.BaselineNormalized![0].Mse)));
}
