using System.Drawing;

namespace PictureSharp.Formats.Heif.Tests;

[Flags]
internal enum PortraitRegion : byte { Background = 1, Subject = 2, Edge = 4, Hair = 8, Focus = 16 }

internal sealed record PortraitScore(long Pixels, long Windows, double? Mse, double? Ssim)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double? Psnr => Mse is null ? null : Mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(65025 / Mse.Value);
}

internal sealed record PortraitTone(byte[][]? Lut, int Samples, double? Residual, string Status)
{
    // The fit uses only a fixed sharp ROI of the original and reference, never a renderer's output.
    public static PortraitTone Fit(Image original, Image reference, Rectangle sharp)
    {
        PortraitMetrics.CheckSize(original, reference);
        if (sharp.Width <= 0 || sharp.Height <= 0 || !new Rectangle(0, 0, original.Width, original.Height).Contains(sharp))
        {
            return new(null, 0, null, "no-sharp-roi");
        }

        var lut = new byte[3][];
        int samples = int.MaxValue;
        double squared = 0;
        for (int c = 0; c < 3; c++)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int count = 0;
            for (int y = sharp.Top; y < sharp.Bottom; y++)
            {
                for (int x = sharp.Left; x < sharp.Right; x++)
                {
                    double a = Channel(original[x, y], c), b = Channel(reference[x, y], c);
                    if (a is < 4 or > 251 || b is < 4 or > 251)
                    {
                        continue;
                    }

                    sx += a; sy += b; sxx += a * a; sxy += a * b; count++;
                }
            }

            samples = Math.Min(samples, count);
            double variance = count == 0 ? 0 : sxx / count - sx * sx / count / count;
            if (count < 256 || variance < 16)
            {
                return new(null, samples, null, "insufficient-tone-support");
            }

            double slope = (sxy - sx * sy / count) / (sxx - sx * sx / count), intercept = (sy - slope * sx) / count;
            if (slope is < 0.5 or > 2 || Math.Abs(intercept) > 64)
            {
                return new(null, samples, null, "unreliable-tone-fit");
            }

            lut[c] = Enumerable.Range(0, 256).Select(v => (byte)Math.Clamp((int)Math.Round(slope * v + intercept), 0, 255)).ToArray();
        }

        long n = 0;
        for (int y = sharp.Top; y < sharp.Bottom; y++)
        {
            for (int x = sharp.Left; x < sharp.Right; x++)
            {
                Rgba32 a = Map(original[x, y], lut), b = reference[x, y];
                squared += Squared(a.R - b.R) + Squared(a.G - b.G) + Squared(a.B - b.B);
                n += 3;
            }
        }

        double residual = Math.Sqrt(squared / n);
        return residual > 5 ? new(null, samples, residual, "sharp-roi-mismatch") : new(lut, samples, residual, "ok");
    }

    public static Rgba32 Map(Rgba32 value, byte[][]? lut) => lut is null ? value : new(lut[0][value.R], lut[1][value.G], lut[2][value.B], value.A);
    private static int Channel(Rgba32 p, int c) => c == 0 ? p.R : c == 1 ? p.G : p.B;
    private static double Squared(double value) => value * value;
}

internal static class PortraitMetrics
{
    public const string Version = "portrait-e1-v1";
    public static readonly PortraitRegion[] Regions = [PortraitRegion.Background, PortraitRegion.Subject, PortraitRegion.Edge, PortraitRegion.Hair, PortraitRegion.Focus];

    public static void CheckSize(Image a, Image b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException("Portrait measurements require matching dimensions.");
        }
    }

    public static PortraitScore[] Measure(Image a, Image b, byte[] regions, byte[][]? tone = null)
    {
        CheckSize(a, b);
        if (regions.Length != a.Width * a.Height)
        {
            throw new ArgumentException("ROI map size differs from the photograph.");
        }

        double[] errors = new double[Regions.Length], ssim = new double[Regions.Length];
        long[] counts = new long[Regions.Length], windows = new long[Regions.Length];
        ReadOnlySpan<Rgba32> pa = a.GetPixelSpan<Rgba32>(), pb = b.GetPixelSpan<Rgba32>();
        for (int i = 0; i < regions.Length; i++)
        {
            Rgba32 x = PortraitTone.Map(pa[i], tone), y = pb[i];
            double error = (x.R - y.R) * (x.R - y.R) + (x.G - y.G) * (x.G - y.G) + (x.B - y.B) * (x.B - y.B);
            for (int r = 0; r < Regions.Length; r++)
            {
                if ((regions[i] & (byte)Regions[r]) != 0)
                {
                    errors[r] += error; counts[r]++;
                }
            }
        }

        for (int by = 0; by + 8 <= a.Height; by += 8)
        {
            for (int bx = 0; bx + 8 <= a.Width; bx += 8)
            {
                byte included = 31;
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int index = (by + j) * a.Width + bx + i;
                        included &= regions[index];
                        double x = HeifPortraitTests.Luma(PortraitTone.Map(pa[index], tone)), y = HeifPortraitTests.Luma(pb[index]);
                        sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y;
                    }
                }

                double ma = sa / 64, mb = sb / 64, va = Math.Max(0, saa / 64 - ma * ma), vb = Math.Max(0, sbb / 64 - mb * mb), cov = sab / 64 - ma * mb;
                double value = ((2 * ma * mb + 6.5025) * (2 * cov + 58.5225)) / ((ma * ma + mb * mb + 6.5025) * (va + vb + 58.5225));
                for (int r = 0; r < Regions.Length; r++)
                {
                    if ((included & (byte)Regions[r]) != 0)
                    {
                        ssim[r] += value; windows[r]++;
                    }
                }
            }
        }

        return Enumerable.Range(0, Regions.Length).Select(r => new PortraitScore(counts[r], windows[r],
            counts[r] == 0 ? null : errors[r] / (3 * counts[r]), windows[r] == 0 ? null : ssim[r] / windows[r])).ToArray();
    }

    // 7x7 box residual, population standard deviation; only wholly background neighbourhoods contribute.
    public static (long Count, double? Deviation) HighPass(Image image, byte[] regions, byte[][]? tone = null)
    {
        int w = image.Width, h = image.Height, stride = w + 1;
        if (regions.Length != w * h)
        {
            throw new ArgumentException("ROI map size differs from the photograph.");
        }

        double[] integral = new double[(w + 1) * (h + 1)];
        int[] mask = new int[integral.Length];
        ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
        for (int y = 0; y < h; y++)
        {
            double sum = 0;
            int count = 0;
            for (int x = 0; x < w; x++)
            {
                sum += HeifPortraitTests.Luma(PortraitTone.Map(pixels[y * w + x], tone));
                count += (regions[y * w + x] & (byte)PortraitRegion.Background) != 0 ? 1 : 0;
                integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1] + sum;
                mask[(y + 1) * stride + x + 1] = mask[y * stride + x + 1] + count;
            }
        }

        long n = 0;
        double total = 0, squares = 0;
        for (int y = 3; y < h - 3; y++)
        {
            for (int x = 3; x < w - 3; x++)
            {
                int tl = (y - 3) * stride + x - 3, tr = tl + 7, bl = tl + 7 * stride, br = bl + 7;
                if (mask[br] - mask[bl] - mask[tr] + mask[tl] != 49)
                {
                    continue;
                }

                double residual = HeifPortraitTests.Luma(PortraitTone.Map(pixels[y * w + x], tone))
                    - (integral[br] - integral[bl] - integral[tr] + integral[tl]) / 49;
                total += residual; squares += residual * residual; n++;
            }
        }

        return (n, n == 0 ? null : Math.Sqrt(Math.Max(0, squares / n - total * total / n / n)));
    }

    // Scene-balanced MSE: equal pairs within a scene, then equal scenes. PSNR is computed after averaging.
    public static double? PooledMse(IEnumerable<(string Scene, double? Mse)> values)
    {
        double[] means = values.Where(v => v.Mse.HasValue).GroupBy(v => v.Scene, StringComparer.Ordinal)
            .Select(g => g.Average(v => v.Mse!.Value)).ToArray();
        return means.Length == 0 ? null : means.Average();
    }
}
