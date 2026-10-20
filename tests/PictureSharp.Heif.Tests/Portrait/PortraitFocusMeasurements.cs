using System.Drawing;

namespace PictureSharp.Formats.Heif.Tests;

internal static class PortraitFocusMeasurements
{
    public static string[] Measure(string root)
    {
        PortraitMeasurementManifest baseline = PortraitMeasurementRun.Read(root);
        PortraitCatalog catalog = PortraitSampleCatalog.Scan(Path.Combine(root, "samples", "portrait"));
        var rows = new List<string> { "scene\tcamera\tvariant\tlegacy_focus\tsharp_point_focus\tsharp_d10\tsharp_d50\tsharp_d90\tsharp_samples\tdisparity_min\tdisparity_max\tstored_aperture\tfocal_ratio" };
        foreach (PortraitScene scene in catalog.Scenes)
        {
            HeifDocument doc = HeifDocument.Open(scene.Original);
            HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
            using Image? matte = doc.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte) is { } item
                ? doc.LoadImage(item, HeifPortraitAuditTests.Decode) : null;
            HeifAppleDepthInfo info = depth.AppleDepth!;
            foreach (PortraitPairMeasurement pair in baseline.Pairs.Where(p => p.Scene == scene.Name).DistinctBy(p => p.Variant))
            {
                double[] sharp = pair.Focus.Sharp;
                var centre = new Point((int)((sharp[0] + sharp[2] / 2) * pair.NativeWidth), (int)((sharp[1] + sharp[3] / 2) * pair.NativeHeight));
                double actual = PortraitRadiusMeasurements.LegacyFocus(depth, matte, PortraitMeasurementInputs.Point(pair.Focus.Point, pair.NativeWidth, pair.NativeHeight), pair.NativeWidth, pair.NativeHeight);
                double candidate = PortraitRadiusMeasurements.LegacyFocus(depth, null, centre, pair.NativeWidth, pair.NativeHeight);
                var values = new List<float>();
                Rectangle area = PortraitMeasurementInputs.Rectangle(sharp, depth.Width, depth.Height);
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    for (int x = area.Left; x < area.Right; x++)
                    {
                        float value = info.ToFloat(depth.Samples.Span[y * depth.Width + x]);
                        values.Add(info.Kind == HeifAuxiliaryKind.Depth ? (value > 0 ? 1 / value : 0) : value);
                    }
                }
                values.Sort();
                float Q(double q) => values[(int)Math.Round(q * (values.Count - 1))];
                double ratio = info.IntrinsicMatrix[0] / (double)Math.Max(info.IntrinsicMatrixReferenceSize!.Value.Width, info.IntrinsicMatrixReferenceSize.Value.Height);
                rows.Add(FormattableString.Invariant($"{scene.Name}\t{pair.Camera}\t{pair.Variant}\t{actual:G9}\t{candidate:G9}\t{Q(.1):G9}\t{Q(.5):G9}\t{Q(.9):G9}\t{values.Count}\t{info.FloatMinValue:G9}\t{info.FloatMaxValue:G9}\t{info.SimulatedAperture:R}\t{ratio:G9}"));
            }
        }
        return [.. rows];
    }
}
