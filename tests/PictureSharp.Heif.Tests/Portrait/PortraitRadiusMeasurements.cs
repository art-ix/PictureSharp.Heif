using System.Drawing;
using System.Globalization;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

internal sealed record PortraitRadiusTile(int Id, int X, int Y, double D10, double D50, double D90, double Texture, double Matte);
internal sealed record PortraitRadiusFit(PortraitRadiusTile Tile, float Radius, float Low, float High, double Mse, double UnblurredMse, string Status);
internal sealed record PortraitRadiusResult(string Scene, string Camera, bool Edited, string Variant, float Aperture,
    int NativeLongSide, int ProbeLongSide, double FocalRatio, double LegacyFocus, string ToneStatus, PortraitRadiusFit[] Tiles);

internal static class PortraitRadiusMeasurements
{
    public const int LongSide = 768, TileSize = 32, SampleStep = 4;
    public static ReadOnlySpan<float> Radii => [0, 0.75f, 1, 1.25f, 1.5f, 2, 2.5f, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24];

    public static PortraitRadiusResult[] Measure(string root, PortraitCatalog catalog, PortraitMeasurementManifest baseline)
    {
        var results = new List<PortraitRadiusResult>();
        string live = Path.Combine(root, "artifacts", "portrait-0.6.7-radius-progress.txt");
        File.WriteAllText(live, "");
        foreach (PortraitScene scene in catalog.Scenes)
        {
            HeifDocument document = HeifDocument.Open(scene.Original);
            using Image native = document.LoadImage(document.PrimaryImage, HeifPortraitAuditTests.Decode);
            using Image photo = Reduce(native);
            HeifDepthImage depth = document.LoadDepth(document.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
            using Image? matte = document.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte) is { } item
                ? document.LoadImage(item, HeifPortraitAuditTests.Decode) : null;
            HeifAppleDepthInfo info = depth.AppleDepth!;
            double ratio = info.IntrinsicMatrix[0] / (double)Math.Max(info.IntrinsicMatrixReferenceSize!.Value.Width, info.IntrinsicMatrixReferenceSize.Value.Height);
            PortraitRadiusTile[] tiles = Tiles(photo, depth, matte);
            var references = new List<(PortraitPairMeasurement Baseline, Rgba32[] Pixels, double[] Errors)>();
            foreach (PortraitExport export in scene.Exports)
            {
                PortraitPairMeasurement pair = baseline.Pairs.Single(p => p.Scene == scene.Name && p.Variant == export.Variant && p.Aperture == export.Aperture);
                if (PortraitMeasurementRun.FileHash(scene.Original) != pair.OriginalHash || PortraitMeasurementRun.FileHash(export.Path) != pair.ExportHash)
                {
                    throw new InvalidDataException("Radius inputs differ from the E1 baseline.");
                }

                HeifDocument doc = HeifDocument.Open(export.Path);
                using Image full = doc.LoadImage(doc.PrimaryImage, HeifPortraitAuditTests.Decode);
                using Image reduced = Reduce(full);
                PortraitMetrics.CheckSize(photo, reduced);
                references.Add((pair, reduced.GetPixelSpan().ToArray(), new double[tiles.Length * Radii.Length]));
            }

            var probe = new PortraitDiscProbe(photo);
            for (int radiusIndex = 0; radiusIndex < Radii.Length; radiusIndex++)
            {
                PortraitDiscProbe.Kernel kernel = PortraitDiscProbe.Disc(Radii[radiusIndex]);
                for (int t = 0; t < tiles.Length; t++)
                {
                    PortraitRadiusTile tile = tiles[t];
                    for (int y = tile.Y + SampleStep / 2; y < tile.Y + TileSize; y += SampleStep)
                    {
                        for (int x = tile.X + SampleStep / 2; x < tile.X + TileSize; x += SampleStep)
                        {
                            Rgba32 pixel = probe.At(x, y, kernel);
                            foreach (var reference in references)
                            {
                                Rgba32 a = PortraitTone.Map(pixel, reference.Baseline.Tone.Lut), b = reference.Pixels[y * photo.Width + x];
                                double error = (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
                                reference.Errors[t * Radii.Length + radiusIndex] += error / (3d * (TileSize / SampleStep) * (TileSize / SampleStep));
                            }
                        }
                    }
                }
            }

            foreach (var reference in references)
            {
                var fits = new PortraitRadiusFit[tiles.Length];
                for (int t = 0; t < tiles.Length; t++)
                {
                    ReadOnlySpan<double> errors = reference.Errors.AsSpan(t * Radii.Length, Radii.Length);
                    int best = 0;
                    for (int i = 1; i < errors.Length; i++) { if (errors[i] < errors[best]) { best = i; } }
                    double tolerance = errors[best] + Math.Max(0.25, errors[best] * 0.05);
                    int low = best, high = best;
                    while (low > 0 && errors[low - 1] <= tolerance) { low--; }
                    while (high + 1 < errors.Length && errors[high + 1] <= tolerance) { high++; }
                    string status = tiles[t].Texture < 4 ? "low-texture" : tiles[t].D90 - tiles[t].D10 > Math.Max(0.01, (info.FloatMaxValue - info.FloatMinValue) * 0.06)
                        ? "mixed-depth" : best == Radii.Length - 1 ? "radius-limit" : high - low > 4 ? "ambiguous-radius" : "ok";
                    fits[t] = new(tiles[t], Radii[best], Radii[low], Radii[high], errors[best], errors[0], status);
                }

                PortraitPairMeasurement pair = reference.Baseline;
                var focus = PortraitMeasurementInputs.Point(pair.Focus.Point, native.Width, native.Height);
                double legacyFocus = LegacyFocus(depth, matte, focus, native.Width, native.Height);
                results.Add(new(scene.Name, pair.Camera, scene.Edited, pair.Variant, pair.Aperture, Math.Max(native.Width, native.Height),
                    Math.Max(photo.Width, photo.Height), ratio, legacyFocus, pair.Tone.Status, fits));
            }

            string message = $"{scene.Name}: {references.Count} exports, {tiles.Length} tiles, {tiles.Length * Radii.Length} disc probes; {results.Count}/66 pairs complete.";
            File.AppendAllText(live, message + "\n");
            Console.WriteLine(message);
        }

        return [.. results];
    }

    public static void Write(string root, PortraitRadiusResult[] results)
    {
        string path = Path.Combine(root, "docs", "measurements", "portrait-0.6.7-radius.tsv");
        var lines = new List<string> { "scene\tcamera\tedited\tvariant\taperture\tnative_long_side\tprobe_long_side\tfocal_ratio\tlegacy_focus\ttone_status\ttile_id\td10\td50\td90\ttexture_std\tmatte_mean\tradius_px\tradius_low_px\tradius_high_px\tnative_radius_px\tmse\tunblurred_mse\tstatus" };
        foreach (PortraitRadiusResult r in results)
        {
            foreach (PortraitRadiusFit f in r.Tiles)
            {
                lines.Add(FormattableString.Invariant($"{r.Scene}\t{r.Camera}\t{r.Edited}\t{r.Variant}\t{r.Aperture:R}\t{r.NativeLongSide}\t{r.ProbeLongSide}\t{r.FocalRatio:G9}\t{r.LegacyFocus:G9}\t{r.ToneStatus}\t{f.Tile.Id}\t{f.Tile.D10:G9}\t{f.Tile.D50:G9}\t{f.Tile.D90:G9}\t{f.Tile.Texture:G9}\t{f.Tile.Matte:G9}\t{f.Radius:G9}\t{f.Low:G9}\t{f.High:G9}\t{f.Radius * r.NativeLongSide / r.ProbeLongSide:G9}\t{f.Mse:G9}\t{f.UnblurredMse:G9}\t{f.Status}"));
            }
        }

        using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew)))
        {
            foreach (string line in lines) { writer.WriteLine(line); }
        }
        File.WriteAllText(Path.Combine(root, "artifacts", "portrait-0.6.7-radius-inputs.json"), JsonSerializer.Serialize(results, PortraitMeasurementInputs.Json));
        Console.WriteLine($"Radius table: {lines.Count - 1} tiles, {path}");
    }

    public static double LegacyFocus(HeifDepthImage depth, Image? matte, Point? point, int width, int height)
    {
        var all = new List<double>();
        var selected = new List<double>();
        int cx = point is null ? -1 : Math.Min(depth.Width - 1, (int)((point.Value.X + 0.5) * depth.Width / width));
        int cy = point is null ? -1 : Math.Min(depth.Height - 1, (int)((point.Value.Y + 0.5) * depth.Height / height));
        for (int y = 0; y < depth.Height; y++)
        {
            for (int x = 0; x < depth.Width; x++)
            {
                double d = Value(depth, x, y); all.Add(d);
                if (point is not null ? Math.Abs(x - cx) <= 2 && Math.Abs(y - cy) <= 2
                    : matte is not null && Sample(matte, x, y, depth.Width, depth.Height) >= 128)
                {
                    selected.Add(d);
                }
            }
        }

        if (point is not null || selected.Count >= Math.Max(16, depth.Width * depth.Height / 1000)) { return Quantile(selected, 0.5); }
        return Quantile(all, 0.9);
    }

    private static PortraitRadiusTile[] Tiles(Image photo, HeifDepthImage depth, Image? matte)
    {
        var tiles = new List<PortraitRadiusTile>();
        int id = 0;
        for (int y = 32; y + TileSize <= photo.Height - 32; y += TileSize)
        {
            for (int x = 32; x + TileSize <= photo.Width - 32; x += TileSize)
            {
                var disparities = new List<double>();
                double sum = 0, squares = 0, mask = 0;
                for (int j = y + SampleStep / 2; j < y + TileSize; j += SampleStep)
                {
                    for (int i = x + SampleStep / 2; i < x + TileSize; i += SampleStep)
                    {
                        int dx = Math.Min(depth.Width - 1, (int)((i + 0.5) * depth.Width / photo.Width));
                        int dy = Math.Min(depth.Height - 1, (int)((j + 0.5) * depth.Height / photo.Height));
                        disparities.Add(Value(depth, dx, dy));
                        double luma = HeifPortraitTests.Luma(photo[i, j]); sum += luma; squares += luma * luma;
                        mask += Sample(matte, i, j, photo.Width, photo.Height);
                    }
                }

                double n = disparities.Count;
                disparities.Sort();
                tiles.Add(new(id++, x, y, Q(disparities, 0.1), Q(disparities, 0.5), Q(disparities, 0.9), Math.Sqrt(Math.Max(0, squares / n - sum * sum / n / n)), mask / n));
            }
        }

        return [.. tiles];
    }

    private static Image Reduce(Image image) => image.Clone(x => x.Resize((int)Math.Round((double)image.Width * LongSide / Math.Max(image.Width, image.Height)),
        (int)Math.Round((double)image.Height * LongSide / Math.Max(image.Width, image.Height))));
    private static double Value(HeifDepthImage depth, int x, int y)
    {
        double value = depth.AppleDepth!.ToFloat(depth.Samples.Span[y * depth.Width + x]);
        return depth.AppleDepth.Kind == HeifAuxiliaryKind.Depth ? (value > 0 ? 1 / value : 0) : value;
    }
    private static byte Sample(Image? image, int x, int y, int width, int height) => image is null ? (byte)0 : image[Math.Min(image.Width - 1, (int)((x + 0.5) * image.Width / width)), Math.Min(image.Height - 1, (int)((y + 0.5) * image.Height / height))].R;
    private static double Quantile(List<double> values, double q) { values.Sort(); return Q(values, q); }
    private static double Q(List<double> values, double q) => values[(int)Math.Min(values.Count - 1, Math.Round(q * (values.Count - 1)))];
}
